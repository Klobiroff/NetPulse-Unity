using System;
using System.Collections.Generic;
using System.Reflection;
using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    internal sealed class RpcMethodInfo
    {
        public RpcMethodInfo(int id, MethodInfo method)
        {
            Id = id;
            Method = method;
            var attribute = (NetRpcAttribute)Attribute.GetCustomAttribute(method, typeof(NetRpcAttribute), true);
            DefaultTarget = attribute?.Target ?? RpcTarget.All;
            Delivery = attribute?.Delivery ?? DeliveryMode.ReliableOrdered;
            OwnerOnly = attribute != null && attribute.OwnerOnly;
            GeneratedId = NetworkTypeInfo.ComputeRpcId(method.Name);
            StatName = $"{method.DeclaringType?.Name}.{method.Name}";
            var parameters = method.GetParameters();
            var count = parameters.Length;
            AcceptsInfo = count > 0 && parameters[count - 1].ParameterType == typeof(NetRpcInfo);
            ArgumentCount = AcceptsInfo ? count - 1 : count;
            ParameterTypes = new Type[count];
            for (var i = 0; i < count; i++)
            {
                ParameterTypes[i] = parameters[i].ParameterType;
            }
        }

        public int Id { get; }

        public ushort GeneratedId { get; }

        public RpcTarget DefaultTarget { get; }

        public DeliveryMode Delivery { get; }

        public bool OwnerOnly { get; }

        public string StatName { get; }

        public MethodInfo Method { get; }

        public bool AcceptsInfo { get; }

        public int ArgumentCount { get; }

        public Type[] ParameterTypes { get; }
    }

    internal sealed class NetworkTypeInfo
    {
        private static readonly Dictionary<Type, NetworkTypeInfo> Cache = new();

        private readonly Dictionary<int, RpcMethodInfo> _rpcById = new();
        private readonly Dictionary<string, RpcMethodInfo> _rpcByName = new();
        private readonly Dictionary<ushort, RpcMethodInfo> _rpcByGeneratedId = new();

        private NetworkTypeInfo(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            for (var current = type; current != null && current != typeof(NetworkBehaviour); current = current.BaseType)
            {
                foreach (var method in current.GetMethods(flags | BindingFlags.DeclaredOnly))
                {
                    if (!method.IsDefined(typeof(NetRpcAttribute), true) || _rpcByName.ContainsKey(method.Name))
                    {
                        continue;
                    }

                    var rpc = new RpcMethodInfo(StableHash(method.Name), method);
                    _rpcByName[method.Name] = rpc;
                    _rpcById[rpc.Id] = rpc;
                    _rpcByGeneratedId[rpc.GeneratedId] = rpc;
                }
            }

            var synced = new List<FieldInfo>();
            for (var current = type; current != null && current != typeof(NetworkBehaviour); current = current.BaseType)
            {
                foreach (var field in current.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (field.IsDefined(typeof(SyncedAttribute), true) && SyncedValueCodec.IsSupported(field.FieldType))
                    {
                        synced.Add(field);
                    }
                }
            }

            synced.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            if (synced.Count > 32)
            {
                Debug.LogError($"[NetPulse] {type.Name} has more than 32 [Synced] fields; extra fields are ignored.");
                synced.RemoveRange(32, synced.Count - 32);
            }

            TypeName = type.Name;
            SyncedFields = synced.ToArray();
            var serialize = type.GetMethod(nameof(NetworkBehaviour.OnSerialize), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            OverridesSerialize = serialize != null && serialize.DeclaringType != typeof(NetworkBehaviour);
        }

        public string TypeName { get; }

        public FieldInfo[] SyncedFields { get; }

        public bool OverridesSerialize { get; }

        public bool HasState => SyncedFields.Length > 0 || OverridesSerialize;

        public static NetworkTypeInfo Get(Type type)
        {
            if (!Cache.TryGetValue(type, out var info))
            {
                info = new NetworkTypeInfo(type);
                Cache[type] = info;
            }

            return info;
        }

        public bool TryGetRpc(int id, out RpcMethodInfo rpc) => _rpcById.TryGetValue(id, out rpc);

        public bool TryGetRpc(string name, out RpcMethodInfo rpc) => _rpcByName.TryGetValue(name, out rpc);

        public bool TryGetGeneratedRpc(ushort id, out RpcMethodInfo rpc) => _rpcByGeneratedId.TryGetValue(id, out rpc);

        public static ushort ComputeRpcId(string name)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var b in System.Text.Encoding.UTF8.GetBytes(name))
                {
                    hash = (hash ^ b) * 16777619u;
                }

                return (ushort)(hash ^ (hash >> 16));
            }
        }

        public static int StableHash(string text)
        {
            unchecked
            {
                var hash = (int)2166136261;
                foreach (var c in text)
                {
                    hash = (hash ^ c) * 16777619;
                }

                return hash;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Cache.Clear();
    }

    internal static class SyncedValueCodec
    {
        public static bool IsSupported(Type type) =>
            type == typeof(bool) || type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long)
            || type == typeof(float) || type == typeof(double) || type == typeof(string) || type == typeof(Vector2)
            || type == typeof(Vector3) || type == typeof(Quaternion) || type == typeof(Color) || type == typeof(Vector2Int)
            || type == typeof(Vector3Int) || type.IsEnum;

        public static void Write(NetWriter writer, Type type, object value)
        {
            if (type.IsEnum)
            {
                writer.WriteInt(Convert.ToInt32(value));
            }
            else if (type == typeof(bool))
            {
                writer.WriteBool((bool)value);
            }
            else if (type == typeof(byte))
            {
                writer.WriteByte((byte)value);
            }
            else if (type == typeof(short))
            {
                writer.WriteShort((short)value);
            }
            else if (type == typeof(int))
            {
                writer.WriteInt((int)value);
            }
            else if (type == typeof(long))
            {
                writer.WriteLong((long)value);
            }
            else if (type == typeof(float))
            {
                writer.WriteFloat((float)value);
            }
            else if (type == typeof(double))
            {
                writer.WriteDouble((double)value);
            }
            else if (type == typeof(string))
            {
                writer.WriteString((string)value);
            }
            else if (type == typeof(Vector2))
            {
                UnityTypeSerializers.WriteVector2(writer, (Vector2)value);
            }
            else if (type == typeof(Vector3))
            {
                UnityTypeSerializers.WriteVector3(writer, (Vector3)value);
            }
            else if (type == typeof(Quaternion))
            {
                UnityTypeSerializers.WriteQuaternion(writer, (Quaternion)value);
            }
            else if (type == typeof(Color))
            {
                UnityTypeSerializers.WriteColor(writer, (Color)value);
            }
            else if (type == typeof(Vector2Int))
            {
                UnityTypeSerializers.WriteVector2Int(writer, (Vector2Int)value);
            }
            else if (type == typeof(Vector3Int))
            {
                UnityTypeSerializers.WriteVector3Int(writer, (Vector3Int)value);
            }
        }

        public static object Read(NetReader reader, Type type)
        {
            if (type.IsEnum)
            {
                return Enum.ToObject(type, reader.ReadInt());
            }

            if (type == typeof(bool))
            {
                return reader.ReadBool();
            }

            if (type == typeof(byte))
            {
                return reader.ReadByte();
            }

            if (type == typeof(short))
            {
                return reader.ReadShort();
            }

            if (type == typeof(int))
            {
                return reader.ReadInt();
            }

            if (type == typeof(long))
            {
                return reader.ReadLong();
            }

            if (type == typeof(float))
            {
                return reader.ReadFloat();
            }

            if (type == typeof(double))
            {
                return reader.ReadDouble();
            }

            if (type == typeof(string))
            {
                return reader.ReadString();
            }

            if (type == typeof(Vector2))
            {
                return UnityTypeSerializers.ReadVector2(reader);
            }

            if (type == typeof(Vector3))
            {
                return UnityTypeSerializers.ReadVector3(reader);
            }

            if (type == typeof(Quaternion))
            {
                return UnityTypeSerializers.ReadQuaternion(reader);
            }

            if (type == typeof(Color))
            {
                return UnityTypeSerializers.ReadColor(reader);
            }

            if (type == typeof(Vector2Int))
            {
                return UnityTypeSerializers.ReadVector2Int(reader);
            }

            return UnityTypeSerializers.ReadVector3Int(reader);
        }
    }
}
