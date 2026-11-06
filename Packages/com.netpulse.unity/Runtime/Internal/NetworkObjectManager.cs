using System;
using System.Collections.Generic;
using System.Reflection;
using NetPulse.Common;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NetPulse
{
    internal sealed class NetworkObjectManager : ISceneObjectHost
    {
        private const int ViewIdsPerActor = 1000;
        private const float KeepAliveSeconds = 1f;
        private const double InitialStateTimeoutSeconds = 1d;
        private const int MaxTicksPerFrame = 5;
        private const byte RpcKindBehaviour = 0;
        private const byte RpcKindGenerated = 1;

        private static readonly DeliveryMode[] DeliveryModes =
        {
            DeliveryMode.ReliableOrdered,
            DeliveryMode.ReliableUnordered,
            DeliveryMode.UnreliableSequenced,
            DeliveryMode.Unreliable
        };

        private readonly NetPulseClient _client;
        private readonly Dictionary<int, NetworkObject> _objects = new();
        private readonly List<NetworkObject> _tickList = new();
        private readonly List<NetworkObject> _processList = new();
        private readonly List<NetworkObject> _awaiting = new();
        private readonly List<PendingOwnership> _pendingOwnership = new();
        private readonly HashSet<NetworkBehaviour> _dirtyMasterBehaviours = new();
        private readonly List<NetworkBehaviour> _masterScratch = new();
        private readonly NetWriter _writer = new(256);
        private readonly NetWriter _scratch = new(128);
        private int _counter;
        private long _lastTick;
        private bool _tickStarted;
        private bool _forceSend;
        private bool _sceneRegistrationPending;

        public NetworkObjectManager(NetPulseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            var events = _client.Events;
            events.OnRoomEvent += OnRoomEvent;
            events.OnOwnershipChangedRaw += OnOwnershipChanged;
            events.OnOwnershipRequestedRaw += OnOwnershipRequested;
            events.OnOwnershipFailedRaw += OnOwnershipFailed;
            events.OnJoinedRoom += OnJoinedRoom;
            events.OnLeftRoom += OnLeftRoom;
            events.OnDisconnected += _ => OnLeftRoom();
            events.OnPlayerEnteredRoom += OnPlayerEntered;
            events.OnPlayerLeftRoom += OnPlayerLeft;
            events.OnMasterClientSwitched += OnMasterSwitched;
            events.OnMasterChangedRaw += OnMasterChanged;
        }

        public int Count => _objects.Count;

        public NetworkObject Find(int viewId) => _objects.TryGetValue(viewId, out var no) ? no : null;

        public void RegisterSceneObject(NetworkObject no)
        {
            if (no.ViewId == 0)
            {
                return;
            }

            if (_objects.TryGetValue(no.ViewId, out var existing) && existing != null && existing != no)
            {
                Debug.LogError($"[NetPulse] Duplicate scene id {no.ViewId} on '{no.name}' and '{existing.name}'.", no);
                return;
            }

            _objects[no.ViewId] = no;
            _sceneRegistrationPending = _client.IsInRoom;
            SpawnObject(no);
        }

        public void Unregister(NetworkObject no)
        {
            if (no.ViewId != 0 && _objects.TryGetValue(no.ViewId, out var existing) && existing == no)
            {
                _objects.Remove(no.ViewId);
            }

            _awaiting.Remove(no);
        }

        public void MarkMasterVarsDirty(NetworkBehaviour behaviour) => _dirtyMasterBehaviours.Add(behaviour);

        public void FlushSceneRegistrationIfMaster()
        {
            if (_sceneRegistrationPending && _client.IsInRoom && !_client.OfflineMode && _client.CurrentRoom.MasterClientId == _client.LocalActorId)
            {
                FlushSceneRegistration();
            }
        }

        public GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation, object data, byte group)
        {
            if (prefab == null)
            {
                throw new ArgumentNullException(nameof(prefab));
            }

            if (!_client.IsInRoom)
            {
                Debug.LogError("[NetPulse] Instantiate requires a joined room (or offline mode).");
                return null;
            }

            var registry = NetPulseSettings.Instance.PrefabRegistry;
            var prefabId = 0;
            if (!_client.OfflineMode && (registry == null || !registry.TryGetId(prefab, out prefabId)))
            {
                Debug.LogError($"[NetPulse] Prefab '{prefab.name}' is not in the NetworkPrefabRegistry. Add it via the NetPulse Hub or Project Settings.", prefab);
                return null;
            }

            var viewId = AllocateViewId();
            var go = Spawn(prefab, viewId, _client.LocalActorId, prefabId, position, rotation, data);
            if (go == null || _client.OfflineMode)
            {
                return go;
            }

            _writer.Reset();
            _writer.WriteInt(viewId);
            _writer.WriteInt(prefabId);
            _writer.WriteInt(_client.LocalActorId);
            UnityTypeSerializers.WriteVector3(_writer, position);
            UnityTypeSerializers.WriteQuaternion(_writer, rotation);
            _writer.WriteByte(group);
            _writer.WriteObject(data);
            var options = new RaiseEventOptions { Receivers = ReceiverGroup.Others, Caching = CachingOption.AddToRoomCache, InterestGroup = group };
            if (go.TryGetComponent<NetworkObject>(out var spawned))
            {
                options.OwnershipMode = (ObjectOwnershipMode)(byte)spawned.OwnershipMode;
                options.ObjectFlags = spawned.PersistAcrossScenes ? ObjectFlag.PersistAcrossScenes : (byte)0;
                TagOut(spawned, StatCategory.NetworkObject, null);
            }

            _client.RaiseRoomEvent(EventCode.ObjectInstantiate, _writer.ToArray(), options, viewId);
            return go;
        }

        public void Destroy(NetworkObject no)
        {
            if (no == null)
            {
                return;
            }

            if (!no.IsSpawned || no.IsSceneObject || !_client.IsInRoom)
            {
                Object.Destroy(no.gameObject);
                return;
            }

            var room = _client.CurrentRoom;
            if (!no.IsMine && room.MasterClientId != _client.LocalActorId)
            {
                Debug.LogWarning($"[NetPulse] Only the owner or the master client can destroy '{no.name}'.", no);
                return;
            }

            if (!_client.OfflineMode)
            {
                _writer.Reset();
                _writer.WriteInt(no.ViewId);
                var options = new RaiseEventOptions { Receivers = ReceiverGroup.Others, Caching = CachingOption.RemoveFromRoomCache, InterestGroup = no.InterestGroup };
                TagOut(no, StatCategory.NetworkObject, null);
                _client.RaiseRoomEvent(EventCode.ObjectDestroy, _writer.ToArray(), options, no.ViewId);
            }

            Object.Destroy(no.gameObject);
        }

        public void SendRpc(NetworkObject no, NetworkBehaviour behaviour, RpcMethodInfo rpc, RpcTarget target, object[] arguments)
        {
            arguments ??= Array.Empty<object>();
            if (arguments.Length != rpc.ArgumentCount)
            {
                Debug.LogError($"[NetPulse] RPC '{rpc.Method.Name}' expects {rpc.ArgumentCount} argument(s) but got {arguments.Length}.", behaviour);
                return;
            }

            if (!no.IsSpawned || !_client.IsInRoom)
            {
                Debug.LogError($"[NetPulse] RPC '{rpc.Method.Name}' requires a spawned object in a joined room.", behaviour);
                return;
            }

            if (rpc.OwnerOnly && !no.IsMine)
            {
                Debug.LogWarning($"[NetPulse] RPC '{rpc.Method.Name}' is OwnerOnly and was not sent because this client does not own '{no.name}'.", behaviour);
                return;
            }

            var localActor = _client.LocalActorId;
            var room = _client.CurrentRoom;
            var plan = ResolveTarget(no, target, null);
            plan.Options.Delivery = rpc.Delivery;
            if (plan.Send && !_client.OfflineMode)
            {
                _writer.Reset();
                _writer.WriteByte(RpcKindBehaviour);
                _writer.WriteInt(no.ViewId);
                _writer.WriteByte((byte)behaviour.BehaviourIndex);
                _writer.WriteInt(rpc.Id);
                _writer.WriteByte((byte)arguments.Length);
                foreach (var argument in arguments)
                {
                    _writer.WriteObject(argument);
                }

                TagOut(no, StatCategory.Rpc, rpc.StatName);
                _client.RaiseRoomEvent(EventCode.Rpc, _writer.ToArray(), plan.Options, plan.Options.Caching == CachingOption.DoNotCache ? 0 : no.ViewId);
            }

            if (plan.InvokeLocal)
            {
                Invoke(behaviour, rpc, arguments, new NetRpcInfo(room.LocalPlayer, localActor, _client.ServerTime));
            }
        }

        public void SendGeneratedRpc(NetworkObject no, NetworkBehaviour behaviour, ushort rpcId, RpcTarget? target, Player player, NetWriter args, DeliveryMode delivery, bool ownerOnly)
        {
            if (!no.IsSpawned || !_client.IsInRoom)
            {
                Debug.LogError($"[NetPulse] {behaviour.GetType().Name}: an RPC requires a spawned object in a joined room.", behaviour);
                return;
            }

            var needsInfo = !target.HasValue || _client.Stats.Enabled;
            RpcMethodInfo info = null;
            if (needsInfo)
            {
                behaviour.TypeInfo.TryGetGeneratedRpc(rpcId, out info);
            }

            if (ownerOnly && !no.IsMine)
            {
                Debug.LogWarning($"[NetPulse] {behaviour.GetType().Name}: an OwnerOnly RPC was not sent because this client does not own '{no.name}'.", behaviour);
                return;
            }

            var resolved = target ?? (info != null ? info.DefaultTarget : RpcTarget.All);
            var localActor = _client.LocalActorId;
            var room = _client.CurrentRoom;
            var plan = ResolveTarget(no, resolved, player);
            plan.Options.Delivery = delivery;
            if (plan.Send && !_client.OfflineMode)
            {
                _writer.Reset();
                _writer.WriteByte(RpcKindGenerated);
                _writer.WriteInt(no.ViewId);
                _writer.WriteByte((byte)behaviour.BehaviourIndex);
                _writer.WriteUShort(rpcId);
                _writer.WriteRaw(args.AsSpan());
                TagOut(no, StatCategory.Rpc, info != null ? info.StatName : null);
                _client.RaiseRoomEvent(EventCode.Rpc, _writer.ToArray(), plan.Options, plan.Options.Caching == CachingOption.DoNotCache ? 0 : no.ViewId);
            }

            if (!plan.InvokeLocal)
            {
                return;
            }

            var reader = new NetReader(args.AsSegment());
            var rpcInfo = new NetRpcInfo(room.LocalPlayer, localActor, _client.ServerTime);
            try
            {
                if (!behaviour.DispatchGeneratedRpc(rpcId, reader, in rpcInfo))
                {
                    Debug.LogError($"[NetPulse] {behaviour.GetType().Name} has no generated RPC with id {rpcId}. Is the class 'partial' and the NetPulse source generator active?", behaviour);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e, behaviour);
            }
        }

        public void RequestOwnership(NetworkObject no)
        {
            if (!_client.IsInRoom || no.IsMine)
            {
                return;
            }

            if (no.OwnershipMode == OwnershipMode.Fixed)
            {
                Debug.LogWarning($"[NetPulse] '{no.name}' has Fixed ownership; it cannot be requested.", no);
                return;
            }

            _client.RequestOwnership(no.ViewId);
        }

        public NetOperation<bool> RequestOwnershipAsync(NetworkObject no, float timeoutSeconds)
        {
            var operation = new NetOperation<bool>();
            if (!_client.IsInRoom)
            {
                operation.Complete(false);
                return operation;
            }

            if (no.IsMine)
            {
                operation.Complete(true);
                return operation;
            }

            if (no.OwnershipMode == OwnershipMode.Fixed)
            {
                Debug.LogWarning($"[NetPulse] '{no.name}' has Fixed ownership; it cannot be requested.", no);
                operation.Complete(false);
                return operation;
            }

            TrackOwnership(no, _client.LocalActorId, timeoutSeconds, operation);
            _client.RequestOwnership(no.ViewId);
            return operation;
        }

        public void TransferOwnership(NetworkObject no, Player newOwner)
        {
            if (!_client.IsInRoom)
            {
                return;
            }

            if (no.OwnershipMode == OwnershipMode.Fixed)
            {
                Debug.LogWarning($"[NetPulse] '{no.name}' has Fixed ownership; it cannot change owner.", no);
                return;
            }

            _client.TransferOwnership(no.ViewId, newOwner.ActorId);
        }

        public NetOperation<bool> TransferOwnershipAsync(NetworkObject no, Player newOwner, float timeoutSeconds)
        {
            var operation = new NetOperation<bool>();
            if (!_client.IsInRoom)
            {
                operation.Complete(false);
                return operation;
            }

            if (no.EffectiveOwnerActorId == newOwner.ActorId)
            {
                operation.Complete(true);
                return operation;
            }

            if (no.OwnershipMode == OwnershipMode.Fixed)
            {
                Debug.LogWarning($"[NetPulse] '{no.name}' has Fixed ownership; it cannot change owner.", no);
                operation.Complete(false);
                return operation;
            }

            TrackOwnership(no, newOwner.ActorId, timeoutSeconds, operation);
            _client.TransferOwnership(no.ViewId, newOwner.ActorId);
            return operation;
        }

        public void Tick(float deltaTime)
        {
            if (!_client.IsInRoom)
            {
                _tickStarted = false;
                return;
            }

            CheckInitialStateTimeouts();
            if (!_client.OfflineMode)
            {
                if (_sceneRegistrationPending && !_client.IsDispatchSuspended && _client.CurrentRoom.MasterClientId == _client.LocalActorId)
                {
                    FlushSceneRegistration();
                }

                FlushMasterVars();
            }

            RunTicks();
        }

        public void Reset()
        {
            DestroyNetworkObjects(null);
            _counter = 0;
            _tickStarted = false;
            _forceSend = false;
            _awaiting.Clear();
            _dirtyMasterBehaviours.Clear();
            CancelPendingOwnership();
        }

        public void BeginSceneChange()
        {
            _tickList.Clear();
            foreach (var pair in _objects)
            {
                var no = pair.Value;
                if (no != null && !no.IsSceneObject && !no.PersistAcrossScenes)
                {
                    _tickList.Add(no);
                }
            }

            foreach (var no in _tickList)
            {
                _objects.Remove(no.ViewId);
                _awaiting.Remove(no);
                Object.Destroy(no.gameObject);
            }

            _tickList.Clear();
            _dirtyMasterBehaviours.Clear();
        }

        public void ApproveOwnership(NetworkObject no, Player requester)
        {
            if (!_client.IsInRoom || !no.IsMine)
            {
                Debug.LogWarning($"[NetPulse] Only the current owner can approve ownership of '{no.name}'.", no);
                return;
            }

            _client.TransferOwnership(no.ViewId, requester.ActorId);
        }

        private TargetPlan ResolveTarget(NetworkObject no, RpcTarget target, Player player)
        {
            var localActor = _client.LocalActorId;
            var room = _client.CurrentRoom;
            var options = new RaiseEventOptions { InterestGroup = no.InterestGroup };
            if (player != null)
            {
                options.Receivers = ReceiverGroup.Targets;
                options.TargetActors = new[] { player.ActorId };
                return new TargetPlan(player.ActorId == localActor, player.ActorId != localActor, options);
            }

            switch (target)
            {
                case RpcTarget.MasterClient:
                    options.Receivers = ReceiverGroup.MasterClient;
                    return new TargetPlan(room.MasterClientId == localActor, room.MasterClientId != localActor, options);
                case RpcTarget.Owner:
                    options.Receivers = ReceiverGroup.Targets;
                    options.TargetActors = new[] { no.EffectiveOwnerActorId };
                    return new TargetPlan(no.EffectiveOwnerActorId == localActor, no.EffectiveOwnerActorId != localActor, options);
                case RpcTarget.AllBuffered:
                    options.Receivers = ReceiverGroup.Others;
                    options.Caching = CachingOption.AddToRoomCache;
                    return new TargetPlan(true, true, options);
                case RpcTarget.OthersBuffered:
                    options.Receivers = ReceiverGroup.Others;
                    options.Caching = CachingOption.AddToRoomCache;
                    return new TargetPlan(false, true, options);
                case RpcTarget.All:
                    options.Receivers = ReceiverGroup.Others;
                    return new TargetPlan(true, true, options);
                default:
                    options.Receivers = ReceiverGroup.Others;
                    return new TargetPlan(false, true, options);
            }
        }

        private void TagOut(NetworkObject no, StatCategory category, string name)
        {
            var stats = _client.Stats;
            if (!stats.Enabled)
            {
                return;
            }

            if (name != null)
            {
                stats.Tag(category, name);
            }

            stats.Tag(StatCategory.NetworkObject, no.StatName);
        }

        private void RecordIn(NetworkObject no, StatCategory category, string name)
        {
            var stats = _client.Stats;
            if (!stats.Enabled)
            {
                return;
            }

            if (name != null)
            {
                stats.RecordIn(category, name);
            }

            if (no != null)
            {
                stats.RecordIn(StatCategory.NetworkObject, no.StatName);
            }
        }

        private void SpawnObject(NetworkObject no)
        {
            no.Spawn(_client.Now + InitialStateTimeoutSeconds);
            if (no.IsAwaitingInitialState)
            {
                _awaiting.Add(no);
            }
        }

        private void CheckInitialStateTimeouts()
        {
            if (_awaiting.Count == 0)
            {
                return;
            }

            var now = _client.Now;
            for (var i = _awaiting.Count - 1; i >= 0; i--)
            {
                var no = _awaiting[i];
                if (no == null || !no.IsAwaitingInitialState)
                {
                    _awaiting.RemoveAt(i);
                    continue;
                }

                if (now < no.InitialStateDeadline)
                {
                    continue;
                }

                _awaiting.RemoveAt(i);
                no.CompleteInitialState();
            }
        }

        private void CompleteInitialState(NetworkObject no)
        {
            if (!no.IsAwaitingInitialState)
            {
                return;
            }

            _awaiting.Remove(no);
            no.CompleteInitialState();
        }

        private void TrackOwnership(NetworkObject no, int targetActor, float timeoutSeconds, NetOperation<bool> operation)
        {
            var pending = new PendingOwnership(no.ViewId, targetActor, operation);
            _pendingOwnership.Add(pending);
            var deadline = _client.Now + Math.Max(0.01d, timeoutSeconds);
            operation.SetCancelHandler(() => _pendingOwnership.Remove(pending));
            _client.Schedule(() =>
            {
                if (operation.IsDone)
                {
                    return true;
                }

                if (_client.Now < deadline)
                {
                    return false;
                }

                _pendingOwnership.Remove(pending);
                operation.Complete(false);
                return true;
            });
        }

        private void ResolveOwnership(int viewId, int newOwner)
        {
            for (var i = _pendingOwnership.Count - 1; i >= 0; i--)
            {
                var pending = _pendingOwnership[i];
                if (pending.ViewId != viewId || pending.TargetActor != newOwner)
                {
                    continue;
                }

                _pendingOwnership.RemoveAt(i);
                pending.Operation.Complete(true);
            }
        }

        private void FailOwnership(int viewId)
        {
            for (var i = _pendingOwnership.Count - 1; i >= 0; i--)
            {
                var pending = _pendingOwnership[i];
                if (viewId != 0 && pending.ViewId != viewId)
                {
                    continue;
                }

                _pendingOwnership.RemoveAt(i);
                pending.Operation.Complete(false);
            }
        }

        private void CancelPendingOwnership()
        {
            FailOwnership(0);
        }

        private void RunTicks()
        {
            var current = _client.CurrentTick;
            if (!_tickStarted)
            {
                _tickStarted = true;
                _lastTick = current;
                return;
            }

            var pending = current - _lastTick;
            if (pending <= 0)
            {
                if (pending < -_client.TickRate)
                {
                    _lastTick = current;
                }

                return;
            }

            if (pending > _client.TickRate)
            {
                _lastTick = current - MaxTicksPerFrame;
                pending = MaxTicksPerFrame;
            }

            var count = (int)Math.Min(pending, MaxTicksPerFrame);
            for (var i = 0; i < count; i++)
            {
                _lastTick++;
                ProcessTick(_lastTick);
            }
        }

        private void ProcessTick(long tick)
        {
            _processList.Clear();
            _processList.AddRange(_objects.Values);
            foreach (var no in _processList)
            {
                if (no == null || !no.IsSpawnNotified)
                {
                    continue;
                }

                foreach (var behaviour in no.Behaviours)
                {
                    if (behaviour == null)
                    {
                        continue;
                    }

                    try
                    {
                        behaviour.OnNetworkTick(tick);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e, behaviour);
                    }
                }
            }

            if (_client.OfflineMode || !_client.IsInRoom)
            {
                return;
            }

            if (!_forceSend && tick % TicksPerSend() != 0)
            {
                return;
            }

            _forceSend = false;
            _processList.Clear();
            _processList.AddRange(_objects.Values);
            foreach (var no in _processList)
            {
                if (no != null && no.IsSpawned && no.IsMine)
                {
                    SendState(no);
                }
            }
        }

        private long TicksPerSend()
        {
            var settings = NetPulseSettings.Instance;
            var serialization = settings.SerializationRate > 0 ? settings.SerializationRate : _client.TickRate;
            var rate = Mathf.Max(1, Mathf.Min(settings.SendRate, serialization));
            return Mathf.Max(1, Mathf.RoundToInt(_client.TickRate / (float)rate));
        }

        private GameObject Spawn(GameObject prefab, int viewId, int ownerActorId, int prefabId, Vector3 position, Quaternion rotation, object data)
        {
            var go = InstantiateWithContext(prefab, new SpawnContext(viewId, ownerActorId, prefabId, data), position, rotation);

            if (!go.TryGetComponent<NetworkObject>(out var no))
            {
                Debug.LogError($"[NetPulse] Prefab '{prefab.name}' has no NetworkObject component on its root.", prefab);
                Object.Destroy(go);
                return null;
            }

            _objects[viewId] = no;
            SpawnObject(no);
            return go;
        }

        private static GameObject InstantiateWithContext(GameObject prefab, SpawnContext context, Vector3 position, Quaternion rotation)
        {
            NetworkObject.SetPendingSpawn(context);
            try
            {
                return Object.Instantiate(prefab, position, rotation);
            }
            finally
            {
                NetworkObject.SetPendingSpawn(null);
            }
        }

        private int AllocateViewId()
        {
            var actor = _client.LocalActorId;
            for (var i = 0; i < ViewIdsPerActor - 1; i++)
            {
                _counter = (_counter % (ViewIdsPerActor - 1)) + 1;
                var viewId = (actor * ViewIdsPerActor) + _counter;
                if (!_objects.ContainsKey(viewId))
                {
                    return viewId;
                }
            }

            throw new InvalidOperationException("No free view ids left for this actor.");
        }

        private void SendState(NetworkObject no)
        {
            var behaviours = no.Behaviours;
            foreach (var mode in DeliveryModes)
            {
                _writer.Reset();
                _writer.WriteInt(no.ViewId);
                _writer.WriteDouble(_client.ServerTime);
                var written = 0;
                var reliable = mode == DeliveryMode.ReliableOrdered || mode == DeliveryMode.ReliableUnordered;
                for (var i = 0; i < behaviours.Count; i++)
                {
                    var behaviour = behaviours[i];
                    if (behaviour == null || behaviour.Delivery != mode || !behaviour.HasState)
                    {
                        continue;
                    }

                    var keepAlive = !reliable && Time.unscaledTime - behaviour.LastSendTime > KeepAliveSeconds;
                    _scratch.Reset();
                    if (!behaviour.WriteState(_scratch, keepAlive))
                    {
                        continue;
                    }

                    behaviour.LastSendTime = Time.unscaledTime;
                    _writer.WriteByte((byte)i);
                    _writer.WriteBytes(_scratch.AsSpan());
                    written++;
                }

                if (written == 0)
                {
                    continue;
                }

                var options = new RaiseEventOptions { Receivers = ReceiverGroup.Others, InterestGroup = no.InterestGroup, Delivery = mode };
                TagOut(no, StatCategory.StateSync, "StateSync");
                _client.RaiseRoomEvent(EventCode.StateSync, _writer.ToArray(), options, no.ViewId);
            }
        }

        private void FlushMasterVars()
        {
            if (_dirtyMasterBehaviours.Count == 0)
            {
                return;
            }

            _masterScratch.Clear();
            _masterScratch.AddRange(_dirtyMasterBehaviours);
            _dirtyMasterBehaviours.Clear();
            if (_client.CurrentRoom.MasterClientId != _client.LocalActorId)
            {
                return;
            }

            foreach (var behaviour in _masterScratch)
            {
                if (behaviour != null && behaviour.IsSpawned)
                {
                    SendMasterVars(behaviour, null, false);
                }
            }
        }

        private void SendMasterVars(NetworkBehaviour behaviour, int[] targets, bool full)
        {
            _writer.Reset();
            if (!behaviour.WriteMasterVars(_writer, full))
            {
                return;
            }

            var options = new RaiseEventOptions { InterestGroup = behaviour.NetworkObject.InterestGroup };
            if (targets == null)
            {
                options.Receivers = ReceiverGroup.Others;
            }
            else
            {
                options.Receivers = ReceiverGroup.Targets;
                options.TargetActors = targets;
            }

            TagOut(behaviour.NetworkObject, StatCategory.SyncVar, behaviour.TypeInfo.TypeName);
            _client.RaiseRoomEvent(EventCode.SyncVarSet, _writer.ToArray(), options, behaviour.ViewId);
        }

        private void OnRoomEvent(byte code, int sender, object data, int viewId)
        {
            if (data is not byte[] bytes)
            {
                return;
            }

            try
            {
                var reader = new NetReader(bytes);
                switch (code)
                {
                    case EventCode.ObjectInstantiate:
                        ReceiveInstantiate(reader);
                        break;
                    case EventCode.ObjectDestroy:
                        ReceiveDestroy(reader.ReadInt());
                        break;
                    case EventCode.Rpc:
                        ReceiveRpc(reader, sender);
                        break;
                    case EventCode.StateSync:
                        ReceiveState(reader, sender);
                        break;
                    case EventCode.SyncVarSet:
                        ReceiveSyncVars(reader, sender);
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void ReceiveInstantiate(NetReader reader)
        {
            var viewId = reader.ReadInt();
            var prefabId = reader.ReadInt();
            var owner = reader.ReadInt();
            var position = UnityTypeSerializers.ReadVector3(reader);
            var rotation = UnityTypeSerializers.ReadQuaternion(reader);
            var group = reader.ReadByte();
            var data = reader.ReadObject();
            if (_objects.ContainsKey(viewId))
            {
                return;
            }

            var registry = NetPulseSettings.Instance.PrefabRegistry;
            if (registry == null || !registry.TryGetPrefab(prefabId, out var prefab))
            {
                Debug.LogError($"[NetPulse] Received spawn for unknown prefab id {prefabId}. Rebuild the NetworkPrefabRegistry.");
                return;
            }

            var spawned = Spawn(prefab, viewId, owner, prefabId, position, rotation, data);
            if (spawned != null && spawned.TryGetComponent<NetworkObject>(out var no))
            {
                RecordIn(no, StatCategory.NetworkObject, null);
            }
        }

        private void ReceiveDestroy(int viewId)
        {
            if (_objects.TryGetValue(viewId, out var no) && no != null && !no.IsSceneObject)
            {
                RecordIn(no, StatCategory.NetworkObject, null);
                Object.Destroy(no.gameObject);
            }
        }

        private void ReceiveRpc(NetReader reader, int sender)
        {
            var kind = reader.ReadByte();
            var viewId = reader.ReadInt();
            if (!_objects.TryGetValue(viewId, out var no) || no == null)
            {
                return;
            }

            var index = reader.ReadByte();
            if (index >= no.Behaviours.Count || no.Behaviours[index] == null)
            {
                return;
            }

            var behaviour = no.Behaviours[index];
            if (kind == RpcKindGenerated)
            {
                ReceiveGeneratedRpc(reader, sender, no, behaviour);
                return;
            }

            var methodId = reader.ReadInt();
            var argumentCount = reader.ReadByte();
            if (!behaviour.TypeInfo.TryGetRpc(methodId, out var rpc))
            {
                Debug.LogError($"[NetPulse] Received an RPC (id {methodId}) that {behaviour.GetType().FullName} does not define. The method is missing on this client: version mismatch, or it was stripped by IL2CPP - fix: rebuild with NetPulse link.xml generation active (NetPulse > Tools > Preview IL2CPP link.xml) or mark the method [NetRpc].", behaviour);
                return;
            }

            RecordIn(no, StatCategory.Rpc, rpc.StatName);
            var arguments = new object[argumentCount];
            for (var i = 0; i < argumentCount; i++)
            {
                arguments[i] = reader.ReadObject();
            }

            var room = _client.CurrentRoom;
            Invoke(behaviour, rpc, arguments, new NetRpcInfo(room?.GetPlayer(sender), sender, _client.ServerTime));
        }

        private void ReceiveGeneratedRpc(NetReader reader, int sender, NetworkObject no, NetworkBehaviour behaviour)
        {
            var rpcId = reader.ReadUShort();
            if (_client.Stats.Enabled)
            {
                behaviour.TypeInfo.TryGetGeneratedRpc(rpcId, out var rpc);
                RecordIn(no, StatCategory.Rpc, rpc != null ? rpc.StatName : null);
            }

            var room = _client.CurrentRoom;
            var info = new NetRpcInfo(room?.GetPlayer(sender), sender, _client.ServerTime);
            if (!behaviour.DispatchGeneratedRpc(rpcId, reader, in info))
            {
                Debug.LogError($"[NetPulse] Received an RPC (id {rpcId}) that {behaviour.GetType().FullName} does not define. The sender runs a different version, or the class is not 'partial' and the NetPulse source generator is inactive on this client.", behaviour);
            }
        }

        private void ReceiveState(NetReader reader, int sender)
        {
            var viewId = reader.ReadInt();
            var timestamp = reader.ReadDouble();
            if (!_objects.TryGetValue(viewId, out var no) || no == null || no.IsMine)
            {
                return;
            }

            RecordIn(no, StatCategory.StateSync, "StateSync");
            while (reader.Remaining > 0)
            {
                var index = reader.ReadByte();
                var payload = reader.ReadBytes();
                if (index >= no.Behaviours.Count || no.Behaviours[index] == null)
                {
                    continue;
                }

                try
                {
                    no.Behaviours[index].ReadState(new NetReader(payload), timestamp);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, no.Behaviours[index]);
                }
            }

            CompleteInitialState(no);
        }

        private void ReceiveSyncVars(NetReader reader, int sender)
        {
            var room = _client.CurrentRoom;
            if (room == null || sender != room.MasterClientId)
            {
                return;
            }

            NetworkObject touched = null;
            while (reader.Remaining > 0)
            {
                var viewId = reader.ReadInt();
                var packed = reader.ReadUShort();
                if (!_objects.TryGetValue(viewId, out var no) || no == null)
                {
                    return;
                }

                var behaviourIndex = packed >> 8;
                if (behaviourIndex >= no.Behaviours.Count || no.Behaviours[behaviourIndex] == null)
                {
                    return;
                }

                if (touched == null)
                {
                    touched = no;
                    RecordIn(no, StatCategory.SyncVar, no.Behaviours[behaviourIndex].TypeInfo.TypeName);
                }

                try
                {
                    no.Behaviours[behaviourIndex].ReadMasterVar(packed & 0xFF, reader);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, no.Behaviours[behaviourIndex]);
                    return;
                }
            }

            if (touched != null)
            {
                CompleteInitialState(touched);
            }
        }

        private void OnOwnershipRequested(int viewId, int requesterActorId)
        {
            var room = _client.CurrentRoom;
            if (room == null || !_objects.TryGetValue(viewId, out var no) || no == null || !no.IsMine)
            {
                return;
            }

            var requester = room.GetPlayer(requesterActorId);
            if (requester == null)
            {
                return;
            }

            _client.Events.PublishOwnershipRequest(no, requester);
            foreach (var behaviour in no.Behaviours)
            {
                if (behaviour != null)
                {
                    behaviour.OnOwnershipRequest(requester);
                }
            }
        }

        private void OnOwnershipFailed(int viewId, short code, string message)
        {
            FailOwnership(viewId);
            _client.Events.PublishOwnershipFailed(Find(viewId), code, message);
        }

        private void FlushSceneRegistration()
        {
            _sceneRegistrationPending = false;
            var ids = new List<int>();
            var modes = new List<byte>();
            foreach (var no in _objects.Values)
            {
                if (no != null && no.IsSceneObject)
                {
                    ids.Add(no.ViewId);
                    modes.Add((byte)no.OwnershipMode);
                }
            }

            if (ids.Count > 0)
            {
                _client.RegisterSceneObjects(ids.ToArray(), modes.ToArray());
            }
        }

        private void OnJoinedRoom()
        {
            _sceneRegistrationPending = true;
            _tickStarted = false;
        }

        private void OnOwnershipChanged(int viewId, int newOwner, int sender)
        {
            if (_objects.TryGetValue(viewId, out var no) && no != null && no.OwnerActorId != newOwner)
            {
                no.ChangeOwner(newOwner);
            }

            ResolveOwnership(viewId, newOwner);
        }

        private void OnLeftRoom()
        {
            DestroyNetworkObjects(null);
            _awaiting.Clear();
            _dirtyMasterBehaviours.Clear();
            _tickStarted = false;
            CancelPendingOwnership();
        }

        private void OnPlayerEntered(Player player)
        {
            foreach (var no in _objects.Values)
            {
                if (no != null && no.IsMine)
                {
                    no.ForceFullSync();
                }
            }

            _forceSend = true;
            if (_client.OfflineMode || _client.CurrentRoom.MasterClientId != _client.LocalActorId)
            {
                return;
            }

            var targets = new[] { player.ActorId };
            _tickList.Clear();
            _tickList.AddRange(_objects.Values);
            foreach (var no in _tickList)
            {
                if (no == null || !no.IsSpawned)
                {
                    continue;
                }

                foreach (var behaviour in no.Behaviours)
                {
                    if (behaviour != null && behaviour.HasMasterVars)
                    {
                        SendMasterVars(behaviour, targets, true);
                    }
                }
            }
        }

        private void OnPlayerLeft(Player player)
        {
            if (player.IsInactive)
            {
                return;
            }

            ReleaseSceneObjects(player.ActorId);
            DestroyNetworkObjects(player.ActorId);
        }

        private void ReleaseSceneObjects(int ownerActorId)
        {
            _tickList.Clear();
            _tickList.AddRange(_objects.Values);
            foreach (var no in _tickList)
            {
                if (no != null && no.IsSceneObject && no.OwnerActorId == ownerActorId)
                {
                    no.ChangeOwner(0);
                }
            }
        }

        private void OnMasterChanged(Player previous, Player current)
        {
            _tickList.Clear();
            _tickList.AddRange(_objects.Values);
            foreach (var no in _tickList)
            {
                if (no != null && no.IsSpawnNotified)
                {
                    no.NotifyMasterChanged(previous, current);
                }
            }
        }

        private void OnMasterSwitched(Player master)
        {
            _sceneRegistrationPending = master != null && master.IsLocal;
            foreach (var no in _objects.Values)
            {
                if (no != null && no.IsSceneObject && no.OwnerActorId == 0 && master != null && master.IsLocal)
                {
                    no.ForceFullSync();
                }
            }
        }

        private void DestroyNetworkObjects(int? ownerActorId)
        {
            _tickList.Clear();
            _tickList.AddRange(_objects.Values);
            foreach (var no in _tickList)
            {
                if (no == null || no.IsSceneObject)
                {
                    continue;
                }

                if (ownerActorId.HasValue && no.OwnerActorId != ownerActorId.Value)
                {
                    continue;
                }

                Object.Destroy(no.gameObject);
            }
        }

        private static void Invoke(NetworkBehaviour behaviour, RpcMethodInfo rpc, object[] arguments, NetRpcInfo info)
        {
            var count = rpc.ParameterTypes.Length;
            var values = new object[count];
            try
            {
                for (var i = 0; i < rpc.ArgumentCount; i++)
                {
                    values[i] = ConvertArgument(arguments[i], rpc.ParameterTypes[i]);
                }

                if (rpc.AcceptsInfo)
                {
                    values[count - 1] = info;
                }

                rpc.Method.Invoke(behaviour, values);
            }
            catch (TargetInvocationException e)
            {
                Debug.LogException(e.InnerException ?? e, behaviour);
            }
            catch (Exception e)
            {
                Debug.LogException(e, behaviour);
            }
        }

        private static object ConvertArgument(object value, Type target)
        {
            if (value == null || target.IsInstanceOfType(value))
            {
                return value;
            }

            if (target.IsEnum)
            {
                return Enum.ToObject(target, value);
            }

            return Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
        }

        private readonly struct TargetPlan
        {
            public TargetPlan(bool invokeLocal, bool send, RaiseEventOptions options)
            {
                InvokeLocal = invokeLocal;
                Send = send;
                Options = options;
            }

            public bool InvokeLocal { get; }

            public bool Send { get; }

            public RaiseEventOptions Options { get; }
        }

        private sealed class PendingOwnership
        {
            public PendingOwnership(int viewId, int targetActor, NetOperation<bool> operation)
            {
                ViewId = viewId;
                TargetActor = targetActor;
                Operation = operation;
            }

            public int ViewId { get; }

            public int TargetActor { get; }

            public NetOperation<bool> Operation { get; }
        }
    }
}
