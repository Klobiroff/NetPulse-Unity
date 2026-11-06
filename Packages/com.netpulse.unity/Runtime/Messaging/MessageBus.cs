using System;
using System.Collections.Generic;
using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    internal sealed class MessageBus
    {
        private readonly NetPulseClient _client;
        private readonly Dictionary<ushort, HandlerSet> _handlers = new();
        private readonly HashSet<ushort> _unknownLogged = new();
        private readonly NetWriter _writer = new(128);

        public MessageBus(NetPulseClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _client.Events.OnRoomEvent += OnRoomEvent;
        }

        public void Send<T>(in T message, RpcTarget target, Player player, MessageOptions options)
        {
            if (!_client.IsInRoom)
            {
                Debug.LogWarning($"[NetPulse] Cannot send {typeof(T).Name}: not in a room.");
                return;
            }

            if (!NetMessageRegistry.TryGet<T>(out var codec))
            {
                Debug.LogError($"[NetPulse] {typeof(T).FullName} is not registered as a message. Declare it as 'partial' and implement INetMessage so the NetPulse source generator can register it.");
                return;
            }

            if (player == null && target == RpcTarget.Owner)
            {
                Debug.LogWarning("[NetPulse] Messages cannot be sent to RpcTarget.Owner (no object); use a Player or RpcTarget.MasterClient instead.");
                return;
            }

            _writer.Reset();
            _writer.WriteUShort(codec.Id);
            codec.Write(_writer, message);
            var payload = _writer.ToArray();
            var room = _client.CurrentRoom;
            var raise = new RaiseEventOptions { Delivery = options?.Delivery ?? DeliveryMode.ReliableOrdered, Caching = options?.Caching ?? CachingOption.DoNotCache };
            var invokeLocal = false;
            var send = true;
            if (player != null)
            {
                raise.Receivers = ReceiverGroup.Targets;
                raise.TargetActors = new[] { player.ActorId };
                invokeLocal = player.IsLocal;
                send = !invokeLocal;
            }
            else
            {
                switch (target)
                {
                    case RpcTarget.All:
                        invokeLocal = true;
                        raise.Receivers = ReceiverGroup.Others;
                        break;
                    case RpcTarget.AllBuffered:
                        invokeLocal = true;
                        raise.Receivers = ReceiverGroup.Others;
                        raise.Caching = CachingOption.AddToRoomCache;
                        break;
                    case RpcTarget.OthersBuffered:
                        raise.Receivers = ReceiverGroup.Others;
                        raise.Caching = CachingOption.AddToRoomCache;
                        break;
                    case RpcTarget.MasterClient:
                        raise.Receivers = ReceiverGroup.MasterClient;
                        invokeLocal = room.MasterClientId == _client.LocalActorId;
                        send = !invokeLocal;
                        break;
                    default:
                        raise.Receivers = ReceiverGroup.Others;
                        break;
                }
            }

            if (send)
            {
                if (_client.Stats.Enabled)
                {
                    _client.Stats.Tag(StatCategory.Message, codec.Name);
                }

                _client.RaiseRoomEvent(EventCode.Message, payload, raise, 0);
            }

            if (invokeLocal)
            {
                Dispatch(payload, _client.LocalActorId, false);
            }
        }

        public IDisposable On<T>(Action<T, Player> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (!NetMessageRegistry.TryGet<T>(out var codec))
            {
                Debug.LogError($"[NetPulse] {typeof(T).FullName} is not registered as a message, so the handler will never be called. Declare it as 'partial' and implement INetMessage.");
                return Unsubscriber.None;
            }

            if (!_handlers.TryGetValue(codec.Id, out var set))
            {
                set = codec.CreateHandlerSet();
                _handlers[codec.Id] = set;
            }

            var typed = (HandlerSet<T>)set;
            typed.Add(handler);
            return new Unsubscriber(() => typed.Remove(handler));
        }

        private void OnRoomEvent(byte code, int sender, object data, int viewId)
        {
            if (code != EventCode.Message || data is not byte[] bytes)
            {
                return;
            }

            try
            {
                Dispatch(bytes, sender, true);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void Dispatch(byte[] bytes, int sender, bool countStats)
        {
            var reader = new NetReader(bytes);
            var id = reader.ReadUShort();
            if (!NetMessageRegistry.TryGet(id, out var codec))
            {
                if (_unknownLogged.Add(id))
                {
                    Debug.LogWarning($"[NetPulse] Received a message with unknown id {id}. The sender runs a different version, or the type is not compiled into this build.");
                }

                return;
            }

            if (countStats && _client.Stats.Enabled)
            {
                _client.Stats.RecordIn(StatCategory.Message, codec.Name);
            }

            if (!_handlers.TryGetValue(id, out var set) || set.Count == 0)
            {
                return;
            }

            var room = _client.CurrentRoom;
            var message = codec.ReadBoxed(reader);
            set.Invoke(message, room?.GetPlayer(sender));
        }
    }
}
