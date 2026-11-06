using System;
using System.Collections.Generic;
using System.ComponentModel;
using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    public enum SyncPermission
    {
        Owner,
        Master
    }

    internal interface ISyncVar
    {
        SyncPermission Permission { get; }

        bool IsDirty { get; }

        int Index { get; }

        void Write(NetWriter writer);

        void ReadAndApply(NetReader reader);

        void ClearDirty();
    }

    public sealed class SyncVar<T> : ISyncVar
    {
        private readonly SyncPermission _permission;
        private T _value;
        private NetworkBehaviour _owner;
        private Action<NetWriter, T> _write;
        private Func<NetReader, T> _read;
        private bool _dirty;
        private bool _warned;
        private int _index = -1;

        public SyncVar(T value = default, SyncPermission permission = SyncPermission.Owner)
        {
            _value = value;
            _permission = permission;
        }

        public event Action<T, T> Changed;

        public SyncPermission Permission => _permission;

        public T Value
        {
            get => _value;
            set => Assign(value);
        }

        bool ISyncVar.IsDirty => _dirty;

        int ISyncVar.Index => _index;

        public static implicit operator T(SyncVar<T> variable) => variable._value;

        internal void Attach(NetworkBehaviour owner, int index, Action<NetWriter, T> write, Func<NetReader, T> read)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            _owner = owner;
            _index = index;
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _read = read ?? throw new ArgumentNullException(nameof(read));
        }

        public override string ToString() => _value?.ToString() ?? string.Empty;

        void ISyncVar.Write(NetWriter writer) => _write(writer, _value);

        void ISyncVar.ReadAndApply(NetReader reader) => ApplyRemote(_read(reader));

        void ISyncVar.ClearDirty() => _dirty = false;

        private void Assign(T value)
        {
            if (EqualityComparer<T>.Default.Equals(_value, value))
            {
                return;
            }

            if (_owner != null && !_owner.CanWriteSyncVar(_permission))
            {
                WarnOnce();
                return;
            }

            var previous = _value;
            _value = value;
            _dirty = true;
            if (_owner != null)
            {
                _owner.NotifySyncVarWritten(this);
            }

            RaiseChanged(previous, value);
        }

        private void ApplyRemote(T value)
        {
            if (EqualityComparer<T>.Default.Equals(_value, value))
            {
                return;
            }

            var previous = _value;
            _value = value;
            RaiseChanged(previous, value);
        }

        private void RaiseChanged(T previous, T current)
        {
            var handler = Changed;
            if (handler == null)
            {
                return;
            }

            foreach (var item in handler.GetInvocationList())
            {
                try
                {
                    ((Action<T, T>)item)(previous, current);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, _owner);
                }
            }
        }

        private void WarnOnce()
        {
            if (_warned)
            {
                return;
            }

            _warned = true;
            var who = _permission == SyncPermission.Master ? "the master client" : "the owner";
            Debug.LogWarning($"[NetPulse] {_owner.GetType().Name}: ignored a write to SyncVar #{_index} ({typeof(T).Name}, {_permission}) - only {who} may change it. Further ignored writes to this field are not logged.", _owner);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class SyncVarCollector
    {
        private readonly NetworkBehaviour _owner;
        private readonly List<ISyncVar> _variables = new();

        internal SyncVarCollector(NetworkBehaviour owner)
        {
            _owner = owner;
        }

        internal IReadOnlyList<ISyncVar> Variables => _variables;

        public void Add<T>(SyncVar<T> variable, Action<NetWriter, T> write, Func<NetReader, T> read)
        {
            if (variable == null)
            {
                throw new ArgumentNullException(nameof(variable));
            }

            if (_variables.Count >= 256)
            {
                Debug.LogError($"[NetPulse] {_owner.GetType().Name} has more than 256 SyncVar fields; the extra fields are not synchronised.", _owner);
                return;
            }

            variable.Attach(_owner, _variables.Count, write, read);
            _variables.Add(variable);
        }
    }
}
