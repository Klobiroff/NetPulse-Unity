using System;
using Random = UnityEngine.Random;
using System.Collections.Generic;
using NetPulse;
using NetPulse.Common;
using UnityEngine;

namespace NetPulse.Samples.Basic
{
    public sealed class BasicSampleUI : NetPulseCallbacks
    {
        private static readonly RoomKey<int> Score = new("score");

        [SerializeField] private GameObject _playerPrefab = null;
        [SerializeField] private string _roomName = "demo";
        [SerializeField, Range(2, 16)] private int _maxPlayers = 4;

        private readonly List<string> _log = new();
        private IReadOnlyList<RoomInfo> _rooms = new List<RoomInfo>();
        private string _nickName = "Player";
        private string _host = "127.0.0.1";
        private bool _offline;
        private bool _busy;
        private GameObject _localPlayer;
        private Vector2 _roomScroll;
        private Vector2 _logScroll;
        private string _chatText = "hello";
        private IDisposable _chatSubscription;
        private IDisposable _scoreSubscription;

        private void Awake() => DontDestroyOnLoad(gameObject);

        protected override void OnEnable()
        {
            base.OnEnable();
            _chatSubscription = Pulse.On<ChatMessage>((message, sender) => Log($"{sender?.NickName}: {message.Text}"));
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            _chatSubscription?.Dispose();
            _scoreSubscription?.Dispose();
        }

        public override void OnNetworkSceneLoaded(string sceneName) => Log($"Network scene loaded: {sceneName}");

        public override void OnConnected() => Log("Connected as " + Pulse.UserId);

        public override void OnDisconnected(string cause) => Log("Disconnected: " + cause);

        public override void OnJoinedLobby() => Log("In lobby");

        public override void OnRoomListUpdate(IReadOnlyList<RoomInfo> rooms) => _rooms = rooms;

        public override void OnJoinedRoom()
        {
            Log($"Joined '{Pulse.CurrentRoom.Name}' as actor {Pulse.LocalActorId}");
            _scoreSubscription?.Dispose();
            _scoreSubscription = Pulse.CurrentRoom.OnChanged(Score, value => Log($"Room score is now {value}"));
            SpawnPlayer();
        }

        public override void OnLeftRoom()
        {
            _localPlayer = null;
            Log("Left room");
        }

        public override void OnPlayerEnteredRoom(Player player) => Log($"{player.NickName} entered");

        public override void OnPlayerLeftRoom(Player player) => Log($"{player.NickName} left");

        public override void OnMasterClientSwitched(Player newMaster) => Log($"New master: {newMaster?.NickName}");

        public override void OnRoomPropertiesChanged(PropertyTable changed) => Log($"Room properties changed ({changed.Count})");

        public override void OnPlayerPropertiesChanged(Player player, PropertyTable changed) => Log($"{player.NickName} properties changed");

        public override void OnJoinRoomFailed(short returnCode, string message) => Log($"Join failed {returnCode}: {message}");

        public override void OnCreateRoomFailed(short returnCode, string message) => Log($"Create failed {returnCode}: {message}");

        public override void OnEvent(byte eventCode, int senderActorId, object data) => Log($"Event {eventCode} from {senderActorId}: {data}");

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10f, 10f, 360f, Screen.height - 20f), GUI.skin.box);
            GUILayout.Label($"NetPulse Basic Sample    State: {Pulse.State}    Ping: {Pulse.RoundTripTimeMs} ms");
            switch (Pulse.State)
            {
                case NetState.Disconnected:
                    DrawConnect();
                    break;
                case NetState.Connected:
                case NetState.InLobby:
                    DrawLobby();
                    break;
                case NetState.InRoom:
                    DrawRoom();
                    break;
                default:
                    GUILayout.Label("Please wait...");
                    break;
            }

            DrawLog();
            GUILayout.EndArea();
        }

        private void DrawConnect()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Nickname", GUILayout.Width(70f));
            _nickName = GUILayout.TextField(_nickName);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host", GUILayout.Width(70f));
            _host = GUILayout.TextField(_host);
            GUILayout.EndHorizontal();
            _offline = GUILayout.Toggle(_offline, "Offline mode (no server)");
            GUI.enabled = !_busy;
            if (GUILayout.Button("Connect"))
            {
                ConnectAsync();
            }

            GUI.enabled = true;
        }

        private void DrawLobby()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Room", GUILayout.Width(70f));
            _roomName = GUILayout.TextField(_roomName);
            GUILayout.EndHorizontal();
            GUI.enabled = !_busy;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Create"))
            {
                RunRoomOperation(Pulse.CreateRoom(_roomName, new RoomOptions { MaxPlayers = (byte)_maxPlayers }));
            }

            if (GUILayout.Button("Join or create"))
            {
                RunRoomOperation(Pulse.JoinOrCreateRoom(_roomName, new RoomOptions { MaxPlayers = (byte)_maxPlayers }));
            }

            if (GUILayout.Button("Random"))
            {
                RunRoomOperation(Pulse.JoinRandomRoom(createOptions: new RoomOptions { MaxPlayers = (byte)_maxPlayers }));
            }

            GUILayout.EndHorizontal();
            if (Pulse.State == NetState.Connected && GUILayout.Button("Join lobby (list rooms)"))
            {
                Pulse.JoinLobby();
            }

            GUI.enabled = true;
            GUILayout.Label($"Rooms in lobby: {_rooms.Count}");
            _roomScroll = GUILayout.BeginScrollView(_roomScroll, GUILayout.Height(110f));
            foreach (var room in _rooms)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{room.Name} ({room.PlayerCount}/{room.MaxPlayers})");
                if (GUILayout.Button("Join", GUILayout.Width(60f)))
                {
                    RunRoomOperation(Pulse.JoinRoom(room.Name));
                }

                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();
            if (GUILayout.Button("Disconnect"))
            {
                Pulse.Disconnect();
            }
        }

        private void DrawRoom()
        {
            var room = Pulse.CurrentRoom;
            GUILayout.Label($"Room '{room.Name}'  players {room.PlayerCount}/{room.MaxPlayers}  tick {Pulse.Tick} @ {Pulse.TickRate} Hz");
            GUILayout.Label("Move: WASD    RPC: Space    Hurt yourself: H (SyncVar health)");
            foreach (var player in room.Players)
            {
                var master = player.IsMasterClient ? " [master]" : string.Empty;
                var mine = player.IsLocal ? " (you)" : string.Empty;
                GUILayout.Label($"#{player.ActorId} {player.NickName}{master}{mine}");
            }

            GUILayout.Label($"Room score (typed RoomKey<int>): {room.Get(Score)}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Score +1"))
            {
                room.Set(Score, room.Get(Score) + 1);
            }

            if (GUILayout.Button("Score +1 (CAS)"))
            {
                room.SetAsync(Score, room.Get(Score) + 1, room.Get(Score));
            }

            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            _chatText = GUILayout.TextField(_chatText);
            if (GUILayout.Button("Send", GUILayout.Width(60f)))
            {
                Pulse.Send(new ChatMessage { Text = _chatText, Number = room.Get(Score) }, RpcTarget.All);
            }

            GUILayout.EndHorizontal();
            if (Pulse.IsMasterClient)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Load Arena (master)"))
                {
                    Pulse.LoadScene("BasicSampleArena");
                }

                if (GUILayout.Button("Load Main (master)"))
                {
                    Pulse.LoadScene("BasicSample");
                }

                GUILayout.EndHorizontal();
            }

            if (GUILayout.Button("Leave room"))
            {
                Pulse.LeaveRoom();
            }
        }

        private void DrawLog()
        {
            GUILayout.Label("Log");
            _logScroll = GUILayout.BeginScrollView(_logScroll);
            foreach (var line in _log)
            {
                GUILayout.Label(line);
            }

            GUILayout.EndScrollView();
        }

        private async void ConnectAsync()
        {
            _busy = true;
            try
            {
                Pulse.OfflineMode = _offline;
                Pulse.NickName = _nickName;
                var options = ConnectionOptions.From(NetPulseSettings.Instance, Pulse.UserId, _nickName);
                options.Host = _host;
                await Pulse.Connect(options);
                if (!_offline)
                {
                    await Pulse.JoinLobby();
                }
            }
            catch (NetPulseException e)
            {
                Log($"Connect failed {e.ReturnCode}: {e.Message}");
            }
            finally
            {
                _busy = false;
            }
        }

        private async void RunRoomOperation(NetOperation<Room> operation)
        {
            _busy = true;
            try
            {
                await operation;
            }
            catch (NetPulseException e)
            {
                Log($"Room operation failed {e.ReturnCode}: {e.Message}");
            }
            finally
            {
                _busy = false;
            }
        }

        private void SpawnPlayer()
        {
            if (_playerPrefab == null)
            {
                Log("Assign a player prefab to spawn.");
                return;
            }

            var position = new Vector3(Random.Range(-4f, 4f), 0.5f, Random.Range(-4f, 4f));
            _localPlayer = Pulse.Instantiate(_playerPrefab, position, Quaternion.identity, Random.ColorHSV(0f, 1f, 0.6f, 1f, 0.8f, 1f));
        }

        private void Log(string message)
        {
            _log.Add(message);
            if (_log.Count > 50)
            {
                _log.RemoveAt(0);
            }

            _logScroll.y = float.MaxValue;
        }
    }
}
