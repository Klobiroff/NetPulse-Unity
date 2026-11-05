using System;
using LiteNetLib;
using NetPulse.Common;
using UnityEngine.Scripting;

namespace NetPulse
{
    [Preserve]
    public sealed class LiteNetLibTransport : INetTransport
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _manager;
        private NetPeer _peer;
        private bool _connectedRaised;

        public LiteNetLibTransport()
        {
            _manager = new NetManager(_listener)
            {
                DisconnectTimeout = 10000,
                AutoRecycle = true,
                UnsyncedEvents = false
            };
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public event Action Connected;

        public event Action<string> Disconnected;

        public event Action<ArraySegment<byte>, NetPulse.Common.DeliveryMode> DataReceived;

        public bool IsConnected => _peer != null && _peer.ConnectionState == ConnectionState.Connected;

        public int RoundTripTimeMs => _peer?.RoundTripTime ?? 0;

        public void Connect(string host, int port, string connectionKey)
        {
            Disconnect();
            _connectedRaised = false;
            if (!_manager.IsRunning && !_manager.Start())
            {
                Disconnected?.Invoke("Failed to open a UDP socket");
                return;
            }

            _peer = _manager.Connect(host, port, connectionKey ?? string.Empty);
        }

        public void Send(ArraySegment<byte> data, NetPulse.Common.DeliveryMode mode)
        {
            if (!IsConnected)
            {
                return;
            }

            _peer.Send(data.Array, data.Offset, data.Count, ToLiteNetLib(mode));
        }

        public void Poll()
        {
            if (_manager.IsRunning)
            {
                _manager.PollEvents();
            }
        }

        public void Disconnect()
        {
            if (_peer == null && !_manager.IsRunning)
            {
                return;
            }

            _peer = null;
            _connectedRaised = false;
            _manager.Stop();
        }

        private static LiteNetLib.DeliveryMethod ToLiteNetLib(NetPulse.Common.DeliveryMode mode) => mode switch
        {
            NetPulse.Common.DeliveryMode.ReliableOrdered => LiteNetLib.DeliveryMethod.ReliableOrdered,
            NetPulse.Common.DeliveryMode.ReliableUnordered => LiteNetLib.DeliveryMethod.ReliableUnordered,
            NetPulse.Common.DeliveryMode.UnreliableSequenced => LiteNetLib.DeliveryMethod.Sequenced,
            _ => LiteNetLib.DeliveryMethod.Unreliable
        };

        private static NetPulse.Common.DeliveryMode FromLiteNetLib(LiteNetLib.DeliveryMethod method) => method switch
        {
            LiteNetLib.DeliveryMethod.ReliableOrdered => NetPulse.Common.DeliveryMode.ReliableOrdered,
            LiteNetLib.DeliveryMethod.ReliableUnordered => NetPulse.Common.DeliveryMode.ReliableUnordered,
            LiteNetLib.DeliveryMethod.Sequenced => NetPulse.Common.DeliveryMode.UnreliableSequenced,
            _ => NetPulse.Common.DeliveryMode.Unreliable
        };

        private void OnPeerConnected(NetPeer peer)
        {
            _peer = peer;
            _connectedRaised = true;
            Connected?.Invoke();
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (_peer != null && !ReferenceEquals(peer, _peer))
            {
                return;
            }

            _peer = null;
            _connectedRaised = false;
            Disconnected?.Invoke(info.Reason.ToString());
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, LiteNetLib.DeliveryMethod method)
        {
            if (!_connectedRaised)
            {
                return;
            }

            DataReceived?.Invoke(reader.GetRemainingBytesSegment(), FromLiteNetLib(method));
        }
    }
}
