using System;
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

        public virtual void OnOwnershipRequest(Player requester)
        {
        }

        public virtual void OnSerialize(NetStream stream)
        {
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

        internal void ForceFullSync()
        {
            _lastSynced = null;
            _lastCustom = null;
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

            if (changedMask == 0u && !customChanged)
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

            if (!info.OverridesSerialize)
            {
                return;
            }

            var custom = input.ReadBytes();
            if (custom.Length > 0)
            {
                OnSerialize(new NetStream(new NetReader(custom), timestamp));
            }
        }
    }
}
