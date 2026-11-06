using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NetPulse.Common;
using Debug = UnityEngine.Debug;

namespace NetPulse
{
    public sealed class NetPulseClient
    {
        private const double PingInterval = 2d;
        private const int OfflineActorId = 1;
        private const double ErrorLogInterval = 5d;
        private const int DefaultTickRate = 30;
        private const int MaxQueuedRoomEvents = 4096;

        private readonly INetTransport _transport;
        private readonly Func<double> _clock;
        private readonly RequestTracker _tracker = new();
        private readonly ServerClock _serverClock = new();
        private readonly NetWriter _writer = new(512);
        private readonly Dictionary<string, RoomInfo> _lobbyRooms = new();
        private readonly OperationScheduler _scheduler = new();
        private readonly Queue<QueuedRoomEvent> _queuedRoomEvents = new();

        private ConnectionOptions _options = new();
        private NetOperation _connectOperation;
        private Room _room;
        private double _lastPing;
        private double _lastErrorTime;
        private string _lastErrorKey;
        private bool _offlineMode;
        private int _eventGate;
        private Player _masterPlayer;
        private int _tickRate = DefaultTickRate;

        public NetPulseClient(INetTransport transport, Func<double> clock = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _clock = clock ?? DefaultClock;
            _transport.Connected += OnTransportConnected;
            _transport.Disconnected += OnTransportDisconnected;
            _transport.DataReceived += OnDataReceived;
            Messages = new MessageBus(this);
        }

        public NetPulseEvents Events { get; } = new();

        public NetStats Stats { get; } = new();

        public bool AutoSyncScene { get; set; } = true;

        internal MessageBus Messages { get; }

        internal bool IsDispatchSuspended => _eventGate > 0;

        public int TickRate => _tickRate;

        public double TickDelta => 1d / _tickRate;

        public long CurrentTick => (long)Math.Floor(ServerTime * _tickRate);

        public NetState State { get; private set; } = NetState.Disconnected;

        public string UserId { get; private set; } = string.Empty;

        public string NickName { get; private set; } = string.Empty;

        public Room CurrentRoom => _room;

        public bool IsInRoom => State == NetState.InRoom && _room != null;

        public bool IsConnected => State != NetState.Disconnected && State != NetState.Connecting && State != NetState.Authenticating;

        public int LocalActorId => IsInRoom ? _room.LocalPlayer.ActorId : 0;

        public IReadOnlyList<RoomInfo> LobbyRooms => _lobbyRooms.Values.OrderBy(static r => r.Name, StringComparer.Ordinal).ToList();

        public int RoundTripTimeMs => _offlineMode ? 0 : _transport.RoundTripTimeMs;

        public double ServerTime => _offlineMode ? Now : _serverClock.ServerSeconds(Now);

        public long BytesSent { get; private set; }

        public long BytesReceived { get; private set; }

        public long MessagesSent { get; private set; }

        public long MessagesReceived { get; private set; }

        public bool OfflineMode
        {
            get => _offlineMode;
            set
            {
                if (value == _offlineMode)
                {
                    return;
                }

                if (State != NetState.Disconnected)
                {
                    Debug.LogError("[NetPulse] OfflineMode can only be changed while disconnected.");
                    return;
                }

                _offlineMode = value;
            }
        }

        internal double Now => _clock();

        public NetOperation Connect(ConnectionOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (State != NetState.Disconnected)
            {
                return NetOperation.Failed(NetPulseReturnCode.InvalidState, $"Cannot connect while {State}");
            }

            UserId = string.IsNullOrEmpty(options.UserId) ? Guid.NewGuid().ToString("N") : options.UserId;
            NickName = options.NickName ?? string.Empty;
            if (_offlineMode)
            {
                SetState(NetState.Connected);
                Events.RaiseConnected();
                return NetOperation.Succeeded();
            }

            _connectOperation = new NetOperation();
            SetState(NetState.Connecting);
            var operation = _connectOperation;
            try
            {
                _transport.Connect(options.Host, options.Port, options.ConnectionKey);
            }
            catch (Exception e)
            {
                HandleDisconnected(e.Message);
            }

            return operation;
        }

        public void Disconnect()
        {
            if (State == NetState.Disconnected)
            {
                return;
            }

            if (!_offlineMode)
            {
                _transport.Disconnect();
            }

            HandleDisconnected("Client disconnect");
        }

        public void Tick()
        {
            if (_offlineMode)
            {
                _scheduler.Run();
                return;
            }

            _transport.Poll();
            var now = Now;
            Stats.Update(now);
            _tracker.Tick(now);
            _scheduler.Run();
            if (IsConnected && now - _lastPing >= PingInterval)
            {
                _lastPing = now;
                SendPing(now);
            }
        }

        public NetOperation JoinLobby(string lobbyName = null)
        {
            if (State != NetState.Connected)
            {
                return NetOperation.Failed(NetPulseReturnCode.InvalidState, $"Cannot join lobby while {State}");
            }

            if (_offlineMode)
            {
                SetState(NetState.InLobby);
                Events.RaiseJoinedLobby();
                return NetOperation.Succeeded();
            }

            var operation = new NetOperation();
            Send(OpCode.JoinLobby, new JoinLobbyRequest { LobbyName = lobbyName }.ToParameters(), response =>
            {
                if (response.ReturnCode != ReturnCode.Ok)
                {
                    operation.Fail(response.ReturnCode, response.DebugMessage);
                    return;
                }

                if (State == NetState.Connected)
                {
                    SetState(NetState.InLobby);
                }

                Events.RaiseJoinedLobby();
                operation.Complete();
            });
            return operation;
        }

        public NetOperation LeaveLobby()
        {
            if (State != NetState.InLobby)
            {
                return NetOperation.Failed(NetPulseReturnCode.InvalidState, $"Cannot leave lobby while {State}");
            }

            if (_offlineMode)
            {
                FinishLeaveLobby();
                return NetOperation.Succeeded();
            }

            var operation = new NetOperation();
            Send(OpCode.LeaveLobby, new ParameterDictionary(), response =>
            {
                FinishLeaveLobby();
                if (response.ReturnCode != ReturnCode.Ok)
                {
                    operation.Fail(response.ReturnCode, response.DebugMessage);
                    return;
                }

                operation.Complete();
            });
            return operation;
        }

        public NetOperation<IReadOnlyList<RoomInfo>> GetRoomList()
        {
            if (State != NetState.Connected && State != NetState.InLobby)
            {
                return NetOperation<IReadOnlyList<RoomInfo>>.Failed(NetPulseReturnCode.InvalidState, $"Cannot get room list while {State}");
            }

            if (_offlineMode)
            {
                return NetOperation<IReadOnlyList<RoomInfo>>.Succeeded(Array.Empty<RoomInfo>());
            }

            var operation = new NetOperation<IReadOnlyList<RoomInfo>>();
            Send(OpCode.GetRoomList, new ParameterDictionary(), response =>
            {
                if (response.ReturnCode != ReturnCode.Ok)
                {
                    operation.Fail(response.ReturnCode, response.DebugMessage);
                    return;
                }

                operation.Complete(RoomListData.FromParameters(response.Parameters).Rooms);
            });
            return operation;
        }

        public NetOperation<Room> CreateRoom(string roomName, RoomOptions options = null, PropertyTable playerProperties = null)
        {
            var request = new CreateRoomRequest { RoomName = roomName, Options = options ?? new RoomOptions(), PlayerProperties = playerProperties };
            return JoinRoomInternal(OpCode.CreateRoom, request.ToParameters(), options, roomName, true, Events.RaiseCreateRoomFailed);
        }

        public NetOperation<Room> JoinRoom(string roomName, PropertyTable playerProperties = null)
        {
            var request = new JoinRoomRequest { RoomName = roomName, PlayerProperties = playerProperties };
            return JoinRoomInternal(OpCode.JoinRoom, request.ToParameters(), null, roomName, false, Events.RaiseJoinRoomFailed);
        }

        public NetOperation<Room> JoinOrCreateRoom(string roomName, RoomOptions options = null, PropertyTable playerProperties = null)
        {
            var request = new JoinOrCreateRoomRequest { RoomName = roomName, Options = options ?? new RoomOptions(), PlayerProperties = playerProperties };
            return JoinRoomInternal(OpCode.JoinOrCreateRoom, request.ToParameters(), options, roomName, false, Events.RaiseJoinRoomFailed);
        }

        public NetOperation<Room> JoinRandomRoom(PropertyTable expectedProperties = null, byte expectedMaxPlayers = 0, RoomOptions createOptions = null, PropertyTable playerProperties = null)
        {
            var request = new JoinRandomRequest
            {
                ExpectedProperties = expectedProperties,
                ExpectedMaxPlayers = expectedMaxPlayers,
                CreateOptions = createOptions,
                PlayerProperties = playerProperties
            };
            return JoinRoomInternal(OpCode.JoinRandomRoom, request.ToParameters(), createOptions, null, false, Events.RaiseJoinRandomFailed);
        }

        public NetOperation LeaveRoom(bool becomeInactive = false)
        {
            if (State != NetState.InRoom)
            {
                return NetOperation.Failed(NetPulseReturnCode.InvalidState, $"Cannot leave room while {State}");
            }

            SetState(NetState.Leaving);
            if (_offlineMode)
            {
                FinishLeaveRoom();
                return NetOperation.Succeeded();
            }

            var operation = new NetOperation();
            Send(OpCode.LeaveRoom, new LeaveRoomRequest { BecomeInactive = becomeInactive }.ToParameters(), response =>
            {
                FinishLeaveRoom();
                if (response.ReturnCode != ReturnCode.Ok)
                {
                    operation.Fail(response.ReturnCode, response.DebugMessage);
                    return;
                }

                operation.Complete();
            });
            return operation;
        }

        public void RaiseEvent(byte eventCode, object data, RaiseEventOptions options = null)
        {
            if (eventCode > EventCode.MaxUserEvent)
            {
                throw new ArgumentOutOfRangeException(nameof(eventCode), "Custom event codes must be in the range 0-199.");
            }

            RaiseRoomEvent(eventCode, data, options, 0);
        }

        public NetOperation SetRoomProperties(PropertyTable properties, PropertyTable expected = null)
        {
            if (!IsInRoom)
            {
                return NetOperation.Failed(NetPulseReturnCode.InvalidState, "Not in a room");
            }

            if (properties != null && properties.ContainsKey(RoomPropertyKeys.Scene) && _room.MasterClientId != LocalActorId)
            {
                Debug.LogWarning("[NetPulse] The room property '_scene' is reserved: only the master client may change it (use Pulse.LoadScene).");
                return NetOperation.Failed(ReturnCode.Forbidden, "Only the master client may change the scene");
            }

            var room = _room;
            var request = new SetPropertiesRequest { ActorId = 0, Properties = properties, ExpectedProperties = expected };
            return SetProperties(OpCode.SetRoomProperties, request, expected != null, room.Properties, delta => ApplyRoomProperties(room, delta));
        }

        public NetOperation SetPlayerProperties(Player player, PropertyTable properties, PropertyTable expected = null)
        {
            if (!IsInRoom)
            {
                return NetOperation.Failed(NetPulseReturnCode.InvalidState, "Not in a room");
            }

            var request = new SetPropertiesRequest { ActorId = player.ActorId, Properties = properties, ExpectedProperties = expected };
            return SetProperties(OpCode.SetPlayerProperties, request, expected != null, player.Properties, delta => ApplyPlayerProperties(player, delta));
        }

        public void ChangeInterestGroups(byte[] add, byte[] remove)
        {
            if (!IsInRoom || _offlineMode)
            {
                return;
            }

            Send(OpCode.ChangeInterestGroups, new ChangeInterestGroupsRequest { Add = add, Remove = remove }.ToParameters(), LogFailure);
        }

        public void TransferOwnership(int viewId, int newOwnerActorId)
        {
            if (!IsInRoom)
            {
                return;
            }

            if (_offlineMode)
            {
                Events.RaiseOwnershipChanged(viewId, newOwnerActorId, LocalActorId);
                return;
            }

            Send(OpCode.TransferOwnership, new TransferOwnershipRequest { ViewId = viewId, NewOwnerActorId = newOwnerActorId }.ToParameters(), response => ReportOwnershipFailure(viewId, response));
        }

        public void RequestOwnership(int viewId)
        {
            if (!IsInRoom)
            {
                return;
            }

            if (_offlineMode)
            {
                Events.RaiseOwnershipChanged(viewId, LocalActorId, LocalActorId);
                return;
            }

            Send(OpCode.RequestOwnership, new RequestOwnershipRequest { ViewId = viewId }.ToParameters(), response => ReportOwnershipFailure(viewId, response));
        }

        public void RegisterSceneObjects(int[] viewIds, byte[] modes)
        {
            if (!IsInRoom || _offlineMode)
            {
                return;
            }

            Send(OpCode.RegisterSceneObjects, new RegisterSceneObjectsRequest { ViewIds = viewIds, Modes = modes }.ToParameters(), response => ReportOwnershipFailure(0, response));
        }

        private void ReportOwnershipFailure(int viewId, OperationResponse response)
        {
            if (response.ReturnCode == ReturnCode.Ok)
            {
                return;
            }

            Debug.LogWarning($"[NetPulse] Ownership operation on view {viewId} failed: {DescribeFailure(response)}");
            Events.RaiseOwnershipFailed(viewId, response.ReturnCode, response.DebugMessage);
        }

        private static string DescribeFailure(OperationResponse response) => response.ReturnCode switch
        {
            ReturnCode.Forbidden => $"forbidden ({response.DebugMessage})",
            ReturnCode.ObjectNotFound => $"object not found ({response.DebugMessage})",
            _ => $"{response.ReturnCode} {response.DebugMessage}"
        };

        internal void RaiseRoomEvent(byte eventCode, object data, RaiseEventOptions options, int viewId)
        {
            if (!IsInRoom)
            {
                Stats.DiscardTags();
                return;
            }

            options ??= new RaiseEventOptions();
            if (_offlineMode)
            {
                Stats.DiscardTags();
                if (IsLocalReceiver(options))
                {
                    DispatchRoomEvent(eventCode, LocalActorId, data, viewId);
                }

                return;
            }

            var request = new RaiseEventRequest
            {
                EventCode = eventCode,
                Data = data,
                Receivers = options.Receivers,
                TargetActors = options.TargetActors,
                InterestGroup = options.InterestGroup,
                Caching = options.Caching,
                Delivery = options.Delivery,
                OwnershipMode = options.OwnershipMode,
                ObjectFlags = options.ObjectFlags,
                ViewId = viewId
            };
            Send(OpCode.RaiseEvent, request.ToParameters(), null, options.Delivery);
        }

        private static double DefaultClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        private NetOperation<Room> JoinRoomInternal(byte opCode, ParameterDictionary parameters, RoomOptions options, string roomName, bool isCreate, Action<short, string> raiseFailed)
        {
            if (State != NetState.Connected && State != NetState.InLobby)
            {
                var message = $"Cannot join a room while {State}";
                raiseFailed(NetPulseReturnCode.InvalidState, message);
                return NetOperation<Room>.Failed(NetPulseReturnCode.InvalidState, message);
            }

            var previous = State;
            SetState(NetState.JoiningRoom);
            var operation = new NetOperation<Room>();
            if (_offlineMode)
            {
                var offline = BuildOfflineRoom(roomName, options);
                CompleteJoin(operation, offline, true);
                return operation;
            }

            Send(opCode, parameters, response =>
            {
                if (response.ReturnCode != ReturnCode.Ok)
                {
                    if (State == NetState.JoiningRoom)
                    {
                        SetState(previous);
                    }

                    raiseFailed(response.ReturnCode, response.DebugMessage);
                    operation.Fail(response.ReturnCode, response.DebugMessage);
                    return;
                }

                var joined = JoinRoomResponse.FromParameters(response.Parameters);
                var room = BuildRoom(joined);
                CompleteJoin(operation, room, isCreate || (opCode != OpCode.JoinRoom && joined.Players.Length <= 1 && !joined.IsRejoin));
            });
            return operation;
        }

        private void CompleteJoin(NetOperation<Room> operation, Room room, bool created)
        {
            _room = room;
            SetState(NetState.InRoom);
            if (created)
            {
                Events.RaiseCreatedRoom();
            }

            Events.RaiseJoinedRoom();
            operation.Complete(room);
        }

        private Room BuildRoom(JoinRoomResponse response)
        {
            var room = new Room(this, response.RoomName)
            {
                MaxPlayers = response.MaxPlayers,
                IsOpen = response.IsOpen,
                IsVisible = response.IsVisible,
                MasterClientId = response.MasterClientId
            };
            if (response.RoomProperties != null)
            {
                room.ApplyProperties(response.RoomProperties);
            }

            foreach (var info in response.Players)
            {
                room.AddPlayer(CreatePlayer(info, info.ActorId == response.LocalActorId));
            }

            if (room.GetPlayer(response.LocalActorId) == null)
            {
                room.AddPlayer(new Player(this, response.LocalActorId, NickName, UserId, true));
            }

            room.LocalPlayer = room.GetPlayer(response.LocalActorId);
            _serverClock.SetInitial(response.ServerTimeTicks, Now * 1000d);
            _tickRate = response.TickRate > 0 ? response.TickRate : DefaultTickRate;
            _masterPlayer = room.GetPlayer(response.MasterClientId);
            return room;
        }

        private Room BuildOfflineRoom(string roomName, RoomOptions options)
        {
            var room = new Room(this, string.IsNullOrEmpty(roomName) ? "OfflineRoom" : roomName)
            {
                MaxPlayers = options?.MaxPlayers ?? 0,
                IsOpen = options?.IsOpen ?? true,
                IsVisible = options?.IsVisible ?? true,
                MasterClientId = OfflineActorId
            };
            if (options?.CustomProperties != null)
            {
                room.ApplyProperties(options.CustomProperties);
            }

            var local = new Player(this, OfflineActorId, NickName, UserId, true);
            room.AddPlayer(local);
            room.LocalPlayer = local;
            _tickRate = DefaultTickRate;
            _masterPlayer = local;
            return room;
        }

        private Player CreatePlayer(PlayerInfo info, bool isLocal)
        {
            var player = new Player(this, info.ActorId, info.NickName, info.UserId, isLocal) { IsInactive = info.IsInactive };
            if (info.Properties != null)
            {
                player.ApplyProperties(info.Properties);
            }

            return player;
        }

        private NetOperation SetProperties(byte opCode, SetPropertiesRequest request, bool waitForServer, PropertyTable current, Action<PropertyTable> apply)
        {
            var delta = request.Properties;
            if (_offlineMode)
            {
                apply(delta);
                return NetOperation.Succeeded();
            }

            PropertyTable previous = null;
            if (!waitForServer)
            {
                previous = CapturePrevious(current, delta);
                apply(delta);
            }

            var operation = new NetOperation();
            Send(opCode, request.ToParameters(), response =>
            {
                if (response.ReturnCode != ReturnCode.Ok)
                {
                    if (previous != null)
                    {
                        RollBack(current, delta, previous, apply, response.ReturnCode);
                    }

                    operation.Fail(response.ReturnCode, response.DebugMessage);
                    return;
                }

                if (waitForServer)
                {
                    apply(delta);
                }

                operation.Complete();
            });
            return operation;
        }

        private static PropertyTable CapturePrevious(PropertyTable current, PropertyTable delta)
        {
            var previous = new PropertyTable();
            if (delta == null)
            {
                return previous;
            }

            foreach (var key in delta.Keys)
            {
                previous[key] = current[key];
            }

            return previous;
        }

        private static void RollBack(PropertyTable current, PropertyTable delta, PropertyTable previous, Action<PropertyTable> apply, short returnCode)
        {
            var restore = new PropertyTable();
            foreach (var key in previous.Keys)
            {
                if (Equals(current[key], delta[key]))
                {
                    restore[key] = previous[key];
                }
            }

            if (restore.Count == 0)
            {
                return;
            }

            Debug.LogWarning($"[NetPulse] The server rejected a property change ({returnCode}); restored the previous values of: {string.Join(", ", restore.Keys)}");
            apply(restore);
        }

        private void ApplyRoomProperties(Room room, PropertyTable delta)
        {
            if (room != _room || delta == null)
            {
                return;
            }

            room.ApplyProperties(delta);
            Events.RaiseRoomProperties(delta);
        }

        private void ApplyPlayerProperties(Player player, PropertyTable delta)
        {
            if (delta == null)
            {
                return;
            }

            player.ApplyProperties(delta);
            Events.RaisePlayerProperties(player, delta);
        }

        private bool IsLocalReceiver(RaiseEventOptions options) => options.Receivers switch
        {
            ReceiverGroup.All => true,
            ReceiverGroup.MasterClient => true,
            ReceiverGroup.Targets => options.TargetActors != null && options.TargetActors.Contains(LocalActorId),
            _ => false
        };

        private void LogFailure(OperationResponse response)
        {
            if (response.ReturnCode != ReturnCode.Ok)
            {
                Debug.LogWarning($"[NetPulse] Operation {response.OpCode} failed: {response.ReturnCode} {response.DebugMessage}");
            }
        }

        private void SetState(NetState state)
        {
            if (State == state)
            {
                return;
            }

            State = state;
            Events.RaiseStateChanged(state);
        }

        private void FinishLeaveLobby()
        {
            _lobbyRooms.Clear();
            if (State == NetState.InLobby)
            {
                SetState(NetState.Connected);
            }

            Events.RaiseLeftLobby();
        }

        private void FinishLeaveRoom()
        {
            if (_room == null)
            {
                return;
            }

            _room = null;
            _masterPlayer = null;
            ClearQueuedRoomEvents();
            if (State == NetState.Leaving || State == NetState.InRoom)
            {
                SetState(NetState.Connected);
            }

            Events.RaiseLeftRoom();
        }

        private void HandleDisconnected(string reason)
        {
            if (State == NetState.Disconnected)
            {
                return;
            }

            _tracker.FailAll(NetPulseReturnCode.Disconnected, reason);
            var hadRoom = _room != null;
            _room = null;
            _masterPlayer = null;
            ClearQueuedRoomEvents();
            _lobbyRooms.Clear();
            var connect = _connectOperation;
            _connectOperation = null;
            _serverClock.Reset();
            SetState(NetState.Disconnected);
            connect?.Fail(NetPulseReturnCode.Disconnected, reason);
            if (hadRoom)
            {
                Events.RaiseLeftRoom();
            }

            Events.RaiseDisconnected(reason);
        }

        private void OnTransportConnected()
        {
            if (State != NetState.Connecting)
            {
                return;
            }

            SetState(NetState.Authenticating);
            var request = new AuthenticateRequest
            {
                AppId = _options.AppId,
                AppVersion = _options.AppVersion,
                UserId = UserId,
                NickName = NickName,
                AuthToken = _options.AuthToken
            };
            Send(OpCode.Authenticate, request.ToParameters(), OnAuthenticated);
        }

        private void OnAuthenticated(OperationResponse response)
        {
            if (State != NetState.Authenticating)
            {
                return;
            }

            if (response.ReturnCode != ReturnCode.Ok)
            {
                var connect = _connectOperation;
                _connectOperation = null;
                _transport.Disconnect();
                HandleDisconnected(response.DebugMessage);
                connect?.Fail(response.ReturnCode, response.DebugMessage);
                return;
            }

            var result = AuthenticateResponse.FromParameters(response.Parameters);
            if (!string.IsNullOrEmpty(result.UserId))
            {
                UserId = result.UserId;
            }

            if (!string.IsNullOrEmpty(result.NickName))
            {
                NickName = result.NickName;
            }

            _serverClock.SetInitial(result.ServerTimeTicks, Now * 1000d);
            _lastPing = Now;
            SetState(NetState.Connected);
            var operation = _connectOperation;
            _connectOperation = null;
            Events.RaiseConnected();
            operation?.Complete();
        }

        private void OnTransportDisconnected(string reason) => HandleDisconnected(reason);

        private void OnDataReceived(ArraySegment<byte> data, DeliveryMode mode)
        {
            BytesReceived += data.Count;
            MessagesReceived++;
            Stats.BeginIn(data.Count);
            try
            {
                var reader = new NetReader(data);
                switch (Protocol.ReadKind(reader))
                {
                    case MessageKind.OperationResponse:
                        var response = Protocol.ReadOperationResponse(reader);
                        Stats.LabelIn(NetStats.Label((byte)MessageKind.OperationResponse, response.OpCode));
                        _tracker.TryComplete(response);
                        break;
                    case MessageKind.Event:
                        var eventData = Protocol.ReadEvent(reader);
                        Stats.LabelIn(NetStats.Label((byte)MessageKind.Event, eventData.Code));
                        HandleEvent(eventData);
                        break;
                    case MessageKind.Pong:
                        Stats.LabelIn(NetStats.Label((byte)MessageKind.Pong, 0));
                        Protocol.ReadPingPong(reader, out var clientTicks, out var serverTicks);
                        _serverClock.AddSample(clientTicks, serverTicks, Now * 1000d);
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            Stats.EndIn();
        }

        private void HandleEvent(EventData data)
        {
            var parameters = data.Parameters ?? new ParameterDictionary();
            switch (data.Code)
            {
                case EventCode.RoomList:
                    HandleRoomList(RoomListData.FromParameters(parameters));
                    break;
                case EventCode.PlayerJoined:
                    HandlePlayerJoined(PlayerInfo.FromParameters(parameters));
                    break;
                case EventCode.PlayerLeft:
                    HandlePlayerLeft(PlayerLeftData.FromParameters(parameters));
                    break;
                case EventCode.RoomPropertiesChanged:
                    HandleRoomPropertiesChanged(PropertiesChangedData.FromParameters(parameters));
                    break;
                case EventCode.PlayerPropertiesChanged:
                    HandlePlayerPropertiesChanged(PropertiesChangedData.FromParameters(parameters));
                    break;
                case EventCode.MasterClientChanged:
                    HandleMasterChanged(parameters.Get<int>(ParamKey.MasterClientId));
                    break;
                case EventCode.OwnershipChanged:
                    var ownership = OwnershipChangedData.FromParameters(parameters);
                    Events.RaiseOwnershipChanged(ownership.ViewId, ownership.NewOwnerActorId, ownership.SenderActorId);
                    break;
                case EventCode.ErrorInfo:
                    HandleErrorInfo(ErrorInfoData.FromParameters(parameters));
                    break;
                case EventCode.OwnershipRequested:
                    var requested = OwnershipRequestedData.FromParameters(parameters);
                    Events.RaiseOwnershipRequested(requested.ViewId, requested.RequesterActorId);
                    break;
                default:
                    if (data.Code <= EventCode.MaxUserEvent || (data.Code >= EventCode.ObjectInstantiate && data.Code <= EventCode.SyncVarSet))
                    {
                        var roomEvent = RoomEventData.FromParameters(parameters);
                        DispatchRoomEvent(data.Code, roomEvent.SenderActorId, roomEvent.Data, roomEvent.ViewId);
                    }

                    break;
            }
        }

        private void HandleErrorInfo(ErrorInfoData error)
        {
            var key = $"{error.ErrorCode}:{error.Message}";
            var now = Now;
            if (key == _lastErrorKey && now - _lastErrorTime < ErrorLogInterval)
            {
                return;
            }

            _lastErrorKey = key;
            _lastErrorTime = now;
            Debug.LogWarning($"[NetPulse] Server rejected a message ({error.ErrorCode}): {error.Message}");
        }

        internal void SuspendRoomEvents() => _eventGate++;

        internal void ResumeRoomEvents()
        {
            if (_eventGate > 0)
            {
                _eventGate--;
            }

            while (_eventGate == 0 && _queuedRoomEvents.Count > 0)
            {
                var queued = _queuedRoomEvents.Dequeue();
                try
                {
                    Stats.ReplayIn(queued.Bytes);
                    DispatchRoomEvent(queued.Code, queued.Sender, queued.Data, queued.ViewId);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        internal void ClearQueuedRoomEvents()
        {
            _queuedRoomEvents.Clear();
            _eventGate = 0;
        }

        internal int QueuedRoomEventCount => _queuedRoomEvents.Count;

        public NetOperation WaitUntil(Func<bool> condition)
        {
            if (condition == null)
            {
                throw new ArgumentNullException(nameof(condition));
            }

            var operation = new NetOperation();
            if (!EvaluateWait(operation, condition))
            {
                _scheduler.Add(() => operation.IsDone || EvaluateWait(operation, condition));
            }

            return operation;
        }

        public NetOperation WaitForPlayers(int count)
        {
            if (!IsInRoom)
            {
                return NetOperation.Failed(NetPulseReturnCode.InvalidState, "Not in a room");
            }

            var room = _room;
            var operation = new NetOperation();
            if (room.PlayerCount >= count)
            {
                operation.Complete();
                return operation;
            }

            _scheduler.Add(() =>
            {
                if (operation.IsDone)
                {
                    return true;
                }

                if (_room != room)
                {
                    operation.Fail(NetPulseReturnCode.InvalidState, "Left the room while waiting for players");
                    return true;
                }

                if (room.PlayerCount < count)
                {
                    return false;
                }

                operation.Complete();
                return true;
            });
            return operation;
        }

        internal void Schedule(Func<bool> step) => _scheduler.Add(step);

        private static bool EvaluateWait(NetOperation operation, Func<bool> condition)
        {
            try
            {
                if (!condition())
                {
                    return false;
                }

                operation.Complete();
            }
            catch (Exception e)
            {
                operation.Fail(NetPulseReturnCode.InvalidState, e.Message);
            }

            return true;
        }

        private void DispatchRoomEvent(byte code, int sender, object data, int viewId)
        {
            if (_eventGate > 0)
            {
                if (_queuedRoomEvents.Count < MaxQueuedRoomEvents)
                {
                    Stats.ClaimIn();
                    _queuedRoomEvents.Enqueue(new QueuedRoomEvent(code, sender, data, viewId, Stats.PacketBytes));
                }
                else
                {
                    Debug.LogWarning("[NetPulse] Too many room events queued while a scene was loading; dropping the newest events.");
                }

                return;
            }

            Events.RaiseRoomEvent(code, sender, data, viewId);
            if (code <= EventCode.MaxUserEvent)
            {
                Events.RaiseEvent(code, sender, data);
            }
        }

        private readonly struct QueuedRoomEvent
        {
            public QueuedRoomEvent(byte code, int sender, object data, int viewId, int bytes)
            {
                Code = code;
                Sender = sender;
                Data = data;
                ViewId = viewId;
                Bytes = bytes;
            }

            public byte Code { get; }

            public int Sender { get; }

            public object Data { get; }

            public int ViewId { get; }

            public int Bytes { get; }
        }

        private void HandleRoomList(RoomListData data)
        {
            if (data.IsFullList)
            {
                _lobbyRooms.Clear();
            }

            foreach (var removed in data.RemovedRooms)
            {
                _lobbyRooms.Remove(removed);
            }

            foreach (var info in data.Rooms)
            {
                _lobbyRooms[info.Name] = info;
            }

            Events.RaiseRoomListUpdate(LobbyRooms);
        }

        private void HandlePlayerJoined(PlayerInfo info)
        {
            if (_room == null || _room.GetPlayer(info.ActorId) != null && !_room.GetPlayer(info.ActorId).IsInactive)
            {
                return;
            }

            var player = CreatePlayer(info, false);
            _room.AddPlayer(player);
            Events.RaisePlayerEntered(player);
        }

        private void HandlePlayerLeft(PlayerLeftData data)
        {
            if (_room == null || !_room.TryGetPlayer(data.ActorId, out var player))
            {
                return;
            }

            if (data.IsInactive)
            {
                player.IsInactive = true;
            }
            else
            {
                _room.RemovePlayer(data.ActorId);
            }

            Events.RaisePlayerLeft(player);
        }

        private void HandleRoomPropertiesChanged(PropertiesChangedData data)
        {
            if (_room == null || data.SenderActorId == LocalActorId && data.SenderActorId != 0)
            {
                return;
            }

            _room.ApplyProperties(data.Properties);
            Events.RaiseRoomProperties(data.Properties);
        }

        private void HandlePlayerPropertiesChanged(PropertiesChangedData data)
        {
            if (_room == null || !_room.TryGetPlayer(data.ActorId, out var player))
            {
                return;
            }

            if (data.SenderActorId == LocalActorId && data.SenderActorId != 0)
            {
                return;
            }

            ApplyPlayerProperties(player, data.Properties);
        }

        private void HandleMasterChanged(int masterId)
        {
            if (_room == null || _room.MasterClientId == masterId)
            {
                return;
            }

            var previous = _masterPlayer ?? _room.GetPlayer(_room.MasterClientId);
            _room.MasterClientId = masterId;
            _masterPlayer = _room.MasterClient;
            Events.RaiseMasterChanged(previous, _masterPlayer);
            Events.RaiseMasterSwitched(_masterPlayer);
        }

        private void Send(byte opCode, ParameterDictionary parameters, Action<OperationResponse> onResponse, DeliveryMode delivery = DeliveryMode.ReliableOrdered)
        {
            if (!_transport.IsConnected)
            {
                Stats.DiscardTags();
                onResponse?.Invoke(new OperationResponse
                {
                    OpCode = opCode,
                    ReturnCode = NetPulseReturnCode.Disconnected,
                    DebugMessage = "Not connected",
                    Parameters = new ParameterDictionary()
                });
                return;
            }

            var requestId = (ushort)0;
            if (onResponse != null)
            {
                requestId = _tracker.Add(opCode, Now + _options.RequestTimeout, onResponse);
            }

            var segment = Protocol.Serialize(_writer, new OperationRequest { OpCode = opCode, RequestId = requestId, Parameters = parameters });
            _transport.Send(segment, delivery);
            BytesSent += segment.Count;
            Stats.CommitOut(segment.Count, NetStats.Label((byte)MessageKind.OperationRequest, opCode));
            MessagesSent++;
        }

        private void SendPing(double now)
        {
            var segment = Protocol.SerializePingPong(_writer, MessageKind.Ping, (long)(now * 1000d), 0L);
            _transport.Send(segment, DeliveryMode.Unreliable);
            BytesSent += segment.Count;
            Stats.CommitOut(segment.Count, NetStats.Label((byte)MessageKind.Ping, 0));
            MessagesSent++;
        }
    }
}
