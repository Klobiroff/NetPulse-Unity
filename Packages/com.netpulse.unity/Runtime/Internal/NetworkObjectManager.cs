using System;
using System.Collections.Generic;
using System.Reflection;
using NetPulse.Common;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NetPulse
{
    internal sealed class NetworkObjectManager
    {
        private const int ViewIdsPerActor = 1000;
        private const float KeepAliveSeconds = 1f;
        private const byte RpcKindBehaviour = 0;

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
        private readonly NetWriter _writer = new(256);
        private readonly NetWriter _scratch = new(128);
        private int _counter;
        private float _accumulator;
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
            no.Spawn();
        }

        public void Unregister(NetworkObject no)
        {
            if (no.ViewId != 0 && _objects.TryGetValue(no.ViewId, out var existing) && existing == no)
            {
                _objects.Remove(no.ViewId);
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

            var localActor = _client.LocalActorId;
            var room = _client.CurrentRoom;
            var invokeLocal = target is RpcTarget.All or RpcTarget.AllBuffered
                || (target == RpcTarget.MasterClient && room.MasterClientId == localActor)
                || (target == RpcTarget.Owner && no.EffectiveOwnerActorId == localActor);
            var options = new RaiseEventOptions { InterestGroup = no.InterestGroup };
            var send = true;
            switch (target)
            {
                case RpcTarget.MasterClient:
                    options.Receivers = ReceiverGroup.MasterClient;
                    send = room.MasterClientId != localActor;
                    break;
                case RpcTarget.Owner:
                    options.Receivers = ReceiverGroup.Targets;
                    options.TargetActors = new[] { no.EffectiveOwnerActorId };
                    send = no.EffectiveOwnerActorId != localActor;
                    break;
                case RpcTarget.AllBuffered:
                case RpcTarget.OthersBuffered:
                    options.Receivers = ReceiverGroup.Others;
                    options.Caching = CachingOption.AddToRoomCache;
                    break;
                default:
                    options.Receivers = ReceiverGroup.Others;
                    break;
            }

            if (invokeLocal)
            {
                Invoke(behaviour, rpc, arguments, new NetRpcInfo(room.LocalPlayer, localActor, _client.ServerTime));
            }

            if (!send || _client.OfflineMode)
            {
                return;
            }

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

            _client.RaiseRoomEvent(EventCode.Rpc, _writer.ToArray(), options, options.Caching == CachingOption.DoNotCache ? 0 : no.ViewId);
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

        public void Tick(float deltaTime)
        {
            if (!_client.IsInRoom || _client.OfflineMode)
            {
                return;
            }

            if (_sceneRegistrationPending && _client.CurrentRoom.MasterClientId == _client.LocalActorId)
            {
                FlushSceneRegistration();
            }

            if (_objects.Count == 0)
            {
                return;
            }

            var settings = NetPulseSettings.Instance;
            var interval = 1f / Mathf.Max(1, Mathf.Min(settings.SendRate, settings.SerializationRate));
            _accumulator += deltaTime;
            if (_accumulator < interval)
            {
                return;
            }

            _accumulator %= interval;
            _tickList.Clear();
            _tickList.AddRange(_objects.Values);
            foreach (var no in _tickList)
            {
                if (no != null && no.IsSpawned && no.IsMine)
                {
                    SendState(no);
                }
            }
        }

        public void Reset()
        {
            DestroyNetworkObjects(null);
            _counter = 0;
            _accumulator = 0f;
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
            no.Spawn();
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
                    if (behaviour == null || behaviour.Delivery != mode || !behaviour.TypeInfo.HasState)
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
                _client.RaiseRoomEvent(EventCode.StateSync, _writer.ToArray(), options, no.ViewId);
            }
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

            Spawn(prefab, viewId, owner, prefabId, position, rotation, data);
        }

        private void ReceiveDestroy(int viewId)
        {
            if (_objects.TryGetValue(viewId, out var no) && no != null && !no.IsSceneObject)
            {
                Object.Destroy(no.gameObject);
            }
        }

        private void ReceiveRpc(NetReader reader, int sender)
        {
            reader.ReadByte();
            var viewId = reader.ReadInt();
            if (!_objects.TryGetValue(viewId, out var no) || no == null)
            {
                return;
            }


            var index = reader.ReadByte();
            var methodId = reader.ReadInt();
            var argumentCount = reader.ReadByte();
            if (index >= no.Behaviours.Count || no.Behaviours[index] == null)
            {
                return;
            }

            var behaviour = no.Behaviours[index];
            if (!behaviour.TypeInfo.TryGetRpc(methodId, out var rpc))
            {
                Debug.LogError($"[NetPulse] Received an RPC (id {methodId}) that {behaviour.GetType().FullName} does not define. The method is missing on this client: version mismatch, or it was stripped by IL2CPP - fix: rebuild with NetPulse link.xml generation active (NetPulse > Tools > Preview IL2CPP link.xml) or mark the method [NetRpc].", behaviour);
                return;
            }

            var arguments = new object[argumentCount];
            for (var i = 0; i < argumentCount; i++)
            {
                arguments[i] = reader.ReadObject();
            }

            var room = _client.CurrentRoom;
            Invoke(behaviour, rpc, arguments, new NetRpcInfo(room?.GetPlayer(sender), sender, _client.ServerTime));
        }

        private void ReceiveState(NetReader reader, int sender)
        {
            var viewId = reader.ReadInt();
            var timestamp = reader.ReadDouble();
            if (!_objects.TryGetValue(viewId, out var no) || no == null || no.IsMine)
            {
                return;
            }

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

        private void OnOwnershipFailed(int viewId, short code, string message) =>
            _client.Events.PublishOwnershipFailed(Find(viewId), code, message);

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

        private void OnJoinedRoom() => _sceneRegistrationPending = true;

        private void OnOwnershipChanged(int viewId, int newOwner, int sender)
        {
            if (!_objects.TryGetValue(viewId, out var no) || no == null || no.OwnerActorId == newOwner)
            {
                return;
            }

            no.ChangeOwner(newOwner);
        }

        private void OnLeftRoom() => DestroyNetworkObjects(null);

        private void OnPlayerEntered(Player player)
        {
            foreach (var no in _objects.Values)
            {
                if (no != null && no.IsMine)
                {
                    no.ForceFullSync();
                }
            }

            _accumulator = 1f;
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
    }
}
