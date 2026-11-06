using System;
using System.Collections.Generic;
using System.ComponentModel;
using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    internal interface IMessageCodec
    {
        ushort Id { get; }

        Type MessageType { get; }

        string Name { get; }

        object ReadBoxed(NetReader reader);

        HandlerSet CreateHandlerSet();
    }

    internal abstract class HandlerSet
    {
        public abstract int Count { get; }

        public abstract void Invoke(object message, Player sender);
    }

    internal sealed class HandlerSet<T> : HandlerSet
    {
        private readonly List<Action<T, Player>> _handlers = new();

        public override int Count => _handlers.Count;

        public void Add(Action<T, Player> handler) => _handlers.Add(handler);

        public void Remove(Action<T, Player> handler) => _handlers.Remove(handler);

        public override void Invoke(object message, Player sender)
        {
            var typed = (T)message;
            var snapshot = _handlers.ToArray();
            foreach (var handler in snapshot)
            {
                try
                {
                    handler(typed, sender);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }
    }

    internal sealed class MessageCodec<T> : IMessageCodec
    {
        private readonly Action<NetWriter, T> _write;
        private readonly Func<NetReader, T> _read;

        public MessageCodec(ushort id, Action<NetWriter, T> write, Func<NetReader, T> read)
        {
            Id = id;
            _write = write;
            _read = read;
            Name = typeof(T).Name;
        }

        public ushort Id { get; }

        public Type MessageType => typeof(T);

        public string Name { get; }

        public void Write(NetWriter writer, T message) => _write(writer, message);

        public T Read(NetReader reader) => _read(reader);

        public object ReadBoxed(NetReader reader) => _read(reader);

        public HandlerSet CreateHandlerSet() => new HandlerSet<T>();
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NetMessageRegistry
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<ushort, IMessageCodec> ById = new();
        private static readonly Dictionary<Type, IMessageCodec> ByType = new();

        public static void Register<T>(ushort id, Action<NetWriter, T> write, Func<NetReader, T> read)
        {
            if (write == null)
            {
                throw new ArgumentNullException(nameof(write));
            }

            if (read == null)
            {
                throw new ArgumentNullException(nameof(read));
            }

            lock (Gate)
            {
                if (ById.TryGetValue(id, out var existing) && existing.MessageType != typeof(T))
                {
                    Debug.LogError($"[NetPulse] Message id {id} collision: {typeof(T).FullName} and {existing.MessageType.FullName} hash to the same id. Rename one of the types.");
                    return;
                }

                var codec = new MessageCodec<T>(id, write, read);
                ById[id] = codec;
                ByType[typeof(T)] = codec;
            }
        }

        public static bool IsRegistered(ushort id)
        {
            lock (Gate)
            {
                return ById.ContainsKey(id);
            }
        }

        internal static bool TryGet(ushort id, out IMessageCodec codec)
        {
            lock (Gate)
            {
                return ById.TryGetValue(id, out codec);
            }
        }

        internal static bool TryGet<T>(out MessageCodec<T> codec)
        {
            lock (Gate)
            {
                if (ByType.TryGetValue(typeof(T), out var found) && found is MessageCodec<T> typed)
                {
                    codec = typed;
                    return true;
                }
            }

            codec = null;
            return false;
        }
    }
}
