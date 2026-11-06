using System;
using System.Collections.Generic;
using System.ComponentModel;
using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        private NetworkObject _networkObject;
        private NetworkTypeInfo _typeInfo;
        private object[] _lastSynced;
        private byte[] _lastCustom;
        private NetWriter _customWriter;
        private ISyncVar[] _ownerVars = Array.Empty<ISyncVar>();
        private ISyncVar[] _masterVars = Array.Empty<ISyncVar>();
        private bool _syncVarsCollected;
        private bool _fullSyncVars = true;

        public NetworkObject NetworkObject
        {
            get
            {
                if (_networkObject == null)
                {
                    _networkObject = GetComponentInParent<NetworkObject>();
                }

                return _networkObject;
            }
        }

        public bool IsMine => NetworkObject != null && NetworkObject.IsMine;

        public Player Owner => NetworkObject != null ? NetworkObject.Owner : null;

        public int ViewId => NetworkObject != null ? NetworkObject.ViewId : 0;

        public bool IsSpawned => NetworkObject != null && NetworkObject.IsSpawned;

        internal int BehaviourIndex { get; set; }

        internal float LastSendTime { get; set; }

        internal NetworkTypeInfo TypeInfo => _typeInfo ??= NetworkTypeInfo.Get(GetType());

        internal DeliveryMode Delivery => SerializeDelivery;

        internal bool HasState => TypeInfo.HasState || _ownerVars.Length > 0;

        internal bool HasInitialState => HasState || _masterVars.Length > 0;

        internal bool HasMasterVars => _masterVars.Length > 0;

        protected virtual DeliveryMode SerializeDelivery => DeliveryMode.ReliableOrdered;

        public virtual void OnNetworkSpawned()
        {
        }

        public virtual void OnNetworkDespawned()
        {
        }

        public virtual void OnOwnerChanged(Player previous, Player current)
        {
        }

        public virtual void OnOwnershipGained()
        {
        }

        public virtual void OnOwnershipLost()
        {
        }

        public virtual void OnMasterChanged(Player previous, Player current)
        {
        }

        public virtual void OnNetworkTick(long tick)
        {
        }

        public virtual void OnOwnershipRequest(Player requester)
        {
        }

        public virtual void OnSerialize(NetStream stream)
        {
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        protected virtual void RegisterSyncVars(SyncVarCollector collector)
        {
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        protected virtual bool InvokeGeneratedRpc(ushort rpcId, NetReader reader, in NetRpcInfo info) => false;

        [EditorBrowsable(EditorBrowsableState.Never)]
        protected NetWriter BeginRpc() => NetWriterPool.Rent();

        [EditorBrowsable(EditorBrowsableState.Never)]
        protected void SendRpc(ushort rpcId, RpcTarget? target, Player player, NetWriter args, DeliveryMode delivery, bool ownerOnly)
        {
            try
            {
                if (NetworkObject == null)
                {
                    Debug.LogError($"[NetPulse] {name}: an RPC requires a NetworkObject on this object or a parent.", this);
                    return;
                }

                Pulse.Objects.SendGeneratedRpc(NetworkObject, this, rpcId, target, player, args, delivery, ownerOnly);
            }
            finally
            {
                NetWriterPool.Return(args);
            }
        }

        public void CallRpc(string methodName, RpcTarget target, params object[] arguments)
        {
            if (NetworkObject == null)
            {
                Debug.LogError($"[NetPulse] {name}: CallRpc requires a NetworkObject.", this);
                return;
            }

            if (!TypeInfo.TryGetRpc(methodName, out var rpc))
            {
                Debug.LogError($"[NetPulse] {GetType().FullName} has no [NetRpc] method named '{methodName}'. Check the name and the [NetRpc] attribute; in an IL2CPP build the method was probably stripped - make sure no custom Player build step bypasses NetPulse link.xml generation (NetPulse > Tools > Preview IL2CPP link.xml shows what is preserved).", this);
                return;
            }

            Pulse.Objects.SendRpc(NetworkObject, this, rpc, target, arguments);
        }

        internal bool DispatchGeneratedRpc(ushort rpcId, NetReader reader, in NetRpcInfo info) => InvokeGeneratedRpc(rpcId, reader, in info);

        internal void CollectSyncVars()
        {
            if (_syncVarsCollected)
            {
                return;
            }

            _syncVarsCollected = true;
            var collector = new SyncVarCollector(this);
            RegisterSyncVars(collector);
            var owner = new List<ISyncVar>();
            var master = new List<ISyncVar>();
            foreach (var variable in collector.Variables)
            {
                if (variable.Permission == SyncPermission.Master)
                {
                    master.Add(variable);
                }
                else
                {
                    owner.Add(variable);
                }
            }

            if (owner.Count > 32)
            {
                Debug.LogError($"[NetPulse] {GetType().Name} has more than 32 Owner SyncVars; the extra ones are not synchronised.", this);
                owner.RemoveRange(32, owner.Count - 32);
            }

            _ownerVars = owner.ToArray();
            _masterVars = master.ToArray();
        }

        internal bool CanWriteSyncVar(SyncPermission permission)
        {
            var networkObject = NetworkObject;
            if (networkObject == null || !networkObject.IsSpawned || !Pulse.IsInRoom)
            {
                return true;
            }

            return permission == SyncPermission.Owner ? networkObject.IsMine : Pulse.IsMasterClient;
        }

        internal void NotifySyncVarWritten(ISyncVar variable)
        {
            if (variable.Permission != SyncPermission.Master || !IsSpawned)
            {
                return;
            }

            Pulse.ObjectsOrNull?.MarkMasterVarsDirty(this);
        }

        internal bool WriteMasterVars(NetWriter output, bool full)
        {
            var written = false;
            foreach (var variable in _masterVars)
            {
                if (!full && !variable.IsDirty)
                {
                    continue;
                }

                output.WriteInt(ViewId);
                output.WriteUShort((ushort)((BehaviourIndex << 8) | variable.Index));
                variable.Write(output);
                variable.ClearDirty();
                written = true;
            }

            return written;
        }

        internal void ReadMasterVar(int variableIndex, NetReader input)
        {
            foreach (var variable in _masterVars)
            {
                if (variable.Index == variableIndex)
                {
                    variable.ReadAndApply(input);
                    return;
                }
            }

            throw new InvalidOperationException($"{GetType().Name} has no Master SyncVar with index {variableIndex}.");
        }

        internal void ForceFullSync()
        {
            _lastSynced = null;
            _lastCustom = null;
            _fullSyncVars = true;
        }

        internal bool WriteState(NetWriter output, bool force)
        {
            var info = TypeInfo;
            var fields = info.SyncedFields;
            var changedMask = 0u;
            if (fields.Length > 0)
            {
                var first = _lastSynced == null;
                _lastSynced ??= new object[fields.Length];
                for (var i = 0; i < fields.Length; i++)
                {
                    var current = fields[i].GetValue(this);
                    if (force || first || !Equals(current, _lastSynced[i]))
                    {
                        changedMask |= 1u << i;
                    }
                }
            }

            var customChanged = false;
            byte[] custom = null;
            if (info.OverridesSerialize)
            {
                _customWriter ??= new NetWriter(64);
                _customWriter.Reset();
                OnSerialize(new NetStream(_customWriter, Pulse.ServerTime));
                custom = _customWriter.ToArray();
                customChanged = force || _lastCustom == null || !custom.AsSpan().SequenceEqual(_lastCustom);
            }

            var varMask = 0u;
            if (_ownerVars.Length > 0)
            {
                var full = force || _fullSyncVars;
                for (var i = 0; i < _ownerVars.Length; i++)
                {
                    if (full || _ownerVars[i].IsDirty)
                    {
                        varMask |= 1u << i;
                    }
                }
            }

            if (changedMask == 0u && !customChanged && varMask == 0u)
            {
                return false;
            }

            if (fields.Length > 0)
            {
                output.WriteUInt(changedMask);
                for (var i = 0; i < fields.Length; i++)
                {
                    if ((changedMask & (1u << i)) == 0u)
                    {
                        continue;
                    }

                    var value = fields[i].GetValue(this);
                    _lastSynced[i] = value;
                    SyncedValueCodec.Write(output, fields[i].FieldType, value);
                }
            }

            if (info.OverridesSerialize)
            {
                output.WriteBytes(customChanged ? custom : Array.Empty<byte>());
                if (customChanged)
                {
                    _lastCustom = custom;
                }
            }

            if (_ownerVars.Length > 0)
            {
                output.WriteUInt(varMask);
                for (var i = 0; i < _ownerVars.Length; i++)
                {
                    if ((varMask & (1u << i)) == 0u)
                    {
                        continue;
                    }

                    _ownerVars[i].Write(output);
                    _ownerVars[i].ClearDirty();
                }

                _fullSyncVars = false;
            }

            return true;
        }

        internal void ReadState(NetReader input, double timestamp)
        {
            var info = TypeInfo;
            var fields = info.SyncedFields;
            if (fields.Length > 0)
            {
                var mask = input.ReadUInt();
                for (var i = 0; i < fields.Length; i++)
                {
                    if ((mask & (1u << i)) != 0u)
                    {
                        fields[i].SetValue(this, SyncedValueCodec.Read(input, fields[i].FieldType));
                    }
                }
            }

            if (info.OverridesSerialize)
            {
                var custom = input.ReadBytes();
                if (custom.Length > 0)
                {
                    OnSerialize(new NetStream(new NetReader(custom), timestamp));
                }
            }

            if (_ownerVars.Length == 0)
            {
                return;
            }

            var varMask = input.ReadUInt();
            for (var i = 0; i < _ownerVars.Length; i++)
            {
                if ((varMask & (1u << i)) != 0u)
                {
                    _ownerVars[i].ReadAndApply(input);
                }
            }
        }
    }
}
