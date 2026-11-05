using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    public static class UnityTypeSerializers
    {
        public const byte Vector2Code = 100;
        public const byte Vector3Code = 101;
        public const byte QuaternionCode = 102;
        public const byte ColorCode = 103;
        public const byte Vector2IntCode = 104;
        public const byte Vector3IntCode = 105;

        public static void Register()
        {
            if (NetTypeRegistry.IsRegistered(Vector2Code))
            {
                return;
            }

            NetTypeRegistry.Register<Vector2>(Vector2Code, static (w, v) => WriteVector2(w, v), static r => ReadVector2(r));
            NetTypeRegistry.Register<Vector3>(Vector3Code, static (w, v) => WriteVector3(w, v), static r => ReadVector3(r));
            NetTypeRegistry.Register<Quaternion>(QuaternionCode, static (w, v) => WriteQuaternion(w, v), static r => ReadQuaternion(r));
            NetTypeRegistry.Register<Color>(ColorCode, static (w, v) => WriteColor(w, v), static r => ReadColor(r));
            NetTypeRegistry.Register<Vector2Int>(Vector2IntCode, static (w, v) => WriteVector2Int(w, v), static r => ReadVector2Int(r));
            NetTypeRegistry.Register<Vector3Int>(Vector3IntCode, static (w, v) => WriteVector3Int(w, v), static r => ReadVector3Int(r));
        }

        public static void WriteVector2(NetWriter writer, Vector2 value)
        {
            writer.WriteFloat(value.x);
            writer.WriteFloat(value.y);
        }

        public static Vector2 ReadVector2(NetReader reader) => new(reader.ReadFloat(), reader.ReadFloat());

        public static void WriteVector3(NetWriter writer, Vector3 value)
        {
            writer.WriteFloat(value.x);
            writer.WriteFloat(value.y);
            writer.WriteFloat(value.z);
        }

        public static Vector3 ReadVector3(NetReader reader) => new(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());

        public static void WriteQuaternion(NetWriter writer, Quaternion value)
        {
            writer.WriteFloat(value.x);
            writer.WriteFloat(value.y);
            writer.WriteFloat(value.z);
            writer.WriteFloat(value.w);
        }

        public static Quaternion ReadQuaternion(NetReader reader) =>
            new(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());

        public static void WriteColor(NetWriter writer, Color value)
        {
            writer.WriteFloat(value.r);
            writer.WriteFloat(value.g);
            writer.WriteFloat(value.b);
            writer.WriteFloat(value.a);
        }

        public static Color ReadColor(NetReader reader) =>
            new(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat());

        public static void WriteVector2Int(NetWriter writer, Vector2Int value)
        {
            writer.WriteInt(value.x);
            writer.WriteInt(value.y);
        }

        public static Vector2Int ReadVector2Int(NetReader reader) => new(reader.ReadInt(), reader.ReadInt());

        public static void WriteVector3Int(NetWriter writer, Vector3Int value)
        {
            writer.WriteInt(value.x);
            writer.WriteInt(value.y);
            writer.WriteInt(value.z);
        }

        public static Vector3Int ReadVector3Int(NetReader reader) => new(reader.ReadInt(), reader.ReadInt(), reader.ReadInt());

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoRegister() => Register();
    }
}
