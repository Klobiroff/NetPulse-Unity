using System.ComponentModel;
using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NetPulseWriterExtensions
    {
        public static void WriteVector2(this NetWriter writer, Vector2 value) => UnityTypeSerializers.WriteVector2(writer, value);

        public static void WriteVector3(this NetWriter writer, Vector3 value) => UnityTypeSerializers.WriteVector3(writer, value);

        public static void WriteVector4(this NetWriter writer, Vector4 value)
        {
            writer.WriteFloat(value.x);
            writer.WriteFloat(value.y);
            writer.WriteFloat(value.z);
            writer.WriteFloat(value.w);
        }

        public static void WriteQuaternion(this NetWriter writer, Quaternion value) => UnityTypeSerializers.WriteQuaternion(writer, value);

        public static void WriteColor(this NetWriter writer, Color value) => UnityTypeSerializers.WriteColor(writer, value);

        public static void WriteColor32(this NetWriter writer, Color32 value)
        {
            writer.WriteByte(value.r);
            writer.WriteByte(value.g);
            writer.WriteByte(value.b);
            writer.WriteByte(value.a);
        }

        public static void WriteVector2Int(this NetWriter writer, Vector2Int value) => UnityTypeSerializers.WriteVector2Int(writer, value);

        public static void WriteVector3Int(this NetWriter writer, Vector3Int value) => UnityTypeSerializers.WriteVector3Int(writer, value);

        public static void WriteRect(this NetWriter writer, Rect value)
        {
            writer.WriteFloat(value.x);
            writer.WriteFloat(value.y);
            writer.WriteFloat(value.width);
            writer.WriteFloat(value.height);
        }

        public static void WritePlayer(this NetWriter writer, Player value) => writer.WriteInt(value is null ? 0 : value.ActorId);

        public static void WriteNetworkObject(this NetWriter writer, NetworkObject value) => writer.WriteInt(value == null ? 0 : value.ViewId);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NetPulseReaderExtensions
    {
        public static Vector2 ReadVector2(this NetReader reader) => UnityTypeSerializers.ReadVector2(reader);

        public static Vector3 ReadVector3(this NetReader reader) => UnityTypeSerializers.ReadVector3(reader);

        public static Vector4 ReadVector4(this NetReader reader) => new(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());

        public static Quaternion ReadQuaternion(this NetReader reader) => UnityTypeSerializers.ReadQuaternion(reader);

        public static Color ReadColor(this NetReader reader) => UnityTypeSerializers.ReadColor(reader);

        public static Color32 ReadColor32(this NetReader reader) => new(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());

        public static Vector2Int ReadVector2Int(this NetReader reader) => UnityTypeSerializers.ReadVector2Int(reader);

        public static Vector3Int ReadVector3Int(this NetReader reader) => UnityTypeSerializers.ReadVector3Int(reader);

        public static Rect ReadRect(this NetReader reader) => new(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());

        public static Player ReadPlayer(this NetReader reader)
        {
            var actorId = reader.ReadInt();
            return actorId == 0 ? null : Pulse.CurrentRoom?.GetPlayer(actorId);
        }

        public static NetworkObject ReadNetworkObject(this NetReader reader)
        {
            var viewId = reader.ReadInt();
            return viewId == 0 ? null : Pulse.FindObject(viewId);
        }
    }
}
