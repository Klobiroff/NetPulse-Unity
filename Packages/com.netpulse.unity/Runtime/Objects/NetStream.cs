using System;
using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    public interface INetSerializable
    {
        void Serialize(NetStream stream);
    }

    public sealed class NetStream
    {
        private readonly NetWriter _writer;
        private readonly NetReader _reader;

        public NetStream(NetWriter writer, double timestamp = 0d)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            Timestamp = timestamp;
        }

        public NetStream(NetReader reader, double timestamp = 0d)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            Timestamp = timestamp;
        }

        public bool IsWriting => _writer != null;

        public bool IsReading => _reader != null;

        public double Timestamp { get; }

        public int Remaining => _reader?.Remaining ?? 0;

        public void SendNext(object value) => _writer.WriteObject(value);

        public T ReceiveNext<T>() => _reader.ReadObject() is T value ? value : default;

        public void Serialize(ref bool value)
        {
            if (IsWriting)
            {
                _writer.WriteBool(value);
                return;
            }

            value = _reader.ReadBool();
        }

        public void Serialize(ref byte value)
        {
            if (IsWriting)
            {
                _writer.WriteByte(value);
                return;
            }

            value = _reader.ReadByte();
        }

        public void Serialize(ref short value)
        {
            if (IsWriting)
            {
                _writer.WriteShort(value);
                return;
            }

            value = _reader.ReadShort();
        }

        public void Serialize(ref int value)
        {
            if (IsWriting)
            {
                _writer.WriteInt(value);
                return;
            }

            value = _reader.ReadInt();
        }

        public void Serialize(ref long value)
        {
            if (IsWriting)
            {
                _writer.WriteLong(value);
                return;
            }

            value = _reader.ReadLong();
        }

        public void Serialize(ref float value)
        {
            if (IsWriting)
            {
                _writer.WriteFloat(value);
                return;
            }

            value = _reader.ReadFloat();
        }

        public void Serialize(ref double value)
        {
            if (IsWriting)
            {
                _writer.WriteDouble(value);
                return;
            }

            value = _reader.ReadDouble();
        }

        public void Serialize(ref string value)
        {
            if (IsWriting)
            {
                _writer.WriteString(value);
                return;
            }

            value = _reader.ReadString();
        }

        public void Serialize(ref byte[] value)
        {
            if (IsWriting)
            {
                _writer.WriteBytes(value);
                return;
            }

            value = _reader.ReadBytes();
        }

        public void Serialize(ref Vector2 value)
        {
            if (IsWriting)
            {
                UnityTypeSerializers.WriteVector2(_writer, value);
                return;
            }

            value = UnityTypeSerializers.ReadVector2(_reader);
        }

        public void Serialize(ref Vector3 value)
        {
            if (IsWriting)
            {
                UnityTypeSerializers.WriteVector3(_writer, value);
                return;
            }

            value = UnityTypeSerializers.ReadVector3(_reader);
        }

        public void Serialize(ref Quaternion value)
        {
            if (IsWriting)
            {
                UnityTypeSerializers.WriteQuaternion(_writer, value);
                return;
            }

            value = UnityTypeSerializers.ReadQuaternion(_reader);
        }

        public void Serialize(ref Color value)
        {
            if (IsWriting)
            {
                UnityTypeSerializers.WriteColor(_writer, value);
                return;
            }

            value = UnityTypeSerializers.ReadColor(_reader);
        }

        public void Serialize(INetSerializable value) => (value ?? throw new ArgumentNullException(nameof(value))).Serialize(this);
    }
}
