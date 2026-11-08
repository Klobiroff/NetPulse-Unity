using System;
using LiteNetLib;
using NetPulse.Common;
using NetPulse.Common.Security;
using UnityEngine.Scripting;

namespace NetPulse
{
    [Preserve]
    public sealed class LiteNetLibTransport : ISecureNetTransport
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly SecurityCounters _counters = new();
        private NetManager _manager;
        private SecureClientCore _core;
        private SecureClientSettings _security;
        private NetPeer _peer;
        private bool _connectedRaised;
        private bool _failureRaised;

        public LiteNetLibTransport()
        {
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;
        }

        public event Action Connected;

        public event Action<string> Disconnected;

        public event Action<ArraySegment<byte>, NetPulse.Common.DeliveryMode> DataReceived;

        public bool IsConnected => _peer != null && _peer.ConnectionState == ConnectionState.Connected;

        public bool IsEncrypted => _core != null && _core.Session != null;

        public SecurityCounters Counters => _counters;

        public int RoundTripTimeMs => _peer?.RoundTripTime ?? 0;

        public void ConfigureSecurity(SecureClientSettings settings) => _security = settings;

        public void Connect(string host, int port, string connectionKey)
        {
            Disconnect();
            _connectedRaised = false;
            _failureRaised = false;
            CreateManager();

            if (!_manager.Start())
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
            if (_manager == null || !_manager.IsRunning)
            {
                return;
            }

            _manager.PollEvents();
            CheckSecureFailure();
        }

        public void Disconnect()
        {
            if (_manager == null)
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

        private void CreateManager()
        {
            _core?.Dispose();
            _core = null;

            if (_security == null)
            {
                _manager = new NetManager(_listener)
                {
                    DisconnectTimeout = 10000,
                    AutoRecycle = true,
                    UnsyncedEvents = false
                };

                return;
            }

            _core = new SecureClientCore(_security, _counters, () => MonotonicClock.NowMs);
            var layer = new SecureClientLayer(_core);
            _manager = new NetManager(_listener, layer)
            {
                DisconnectTimeout = 10000,
                AutoRecycle = true,
                UnsyncedEvents = false
            };

            layer.Attach(_manager);
        }

        private void CheckSecureFailure()
        {
            if (_core == null || _failureRaised)
            {
                return;
            }

            string reason = null;

            if (_core.State == SecureClientState.Failed || (_core.Session != null && _core.Session.IsClosed))
            {
                reason = SecureDisconnectReason.SecureHandshakeFailed;
            }
            else if (_core.CheckTimeout())
            {
                reason = SecureDisconnectReason.SecureHandshakeTimeout;
            }

            if (reason == null)
            {
                return;
            }

            _failureRaised = true;
            _peer = null;
            _connectedRaised = false;
            _manager.Stop();
            Disconnected?.Invoke(reason);
        }

        private void OnPeerConnected(NetPeer peer)
        {
            _peer = peer;
            _connectedRaised = true;
            Connected?.Invoke();
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (_failureRaised)
            {
                return;
            }

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
