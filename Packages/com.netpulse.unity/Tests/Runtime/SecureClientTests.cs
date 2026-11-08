using System;
using System.Collections.Generic;
using NetPulse.Common;
using NetPulse.Common.Security;
using NUnit.Framework;

namespace NetPulse.Tests
{
    public sealed class SecureProbeTransport : ISecureNetTransport
    {
        public event Action Connected;

        public event Action<string> Disconnected;

        public event Action<ArraySegment<byte>, DeliveryMode> DataReceived;

        public bool IsConnected { get; private set; }

        public bool IsEncrypted { get; set; }

        public int RoundTripTimeMs => 1;

        public SecureClientSettings Configured { get; private set; }

        public int ConfigureCalls { get; private set; }

        public int ConnectCalls { get; private set; }

        public void ConfigureSecurity(SecureClientSettings settings)
        {
            Configured = settings;
            ConfigureCalls++;
        }

        public void Connect(string host, int port, string connectionKey) => ConnectCalls++;

        public void Send(ArraySegment<byte> data, DeliveryMode mode)
        {
        }

        public void Poll()
        {
        }

        public void Disconnect() => IsConnected = false;

        public void SimulateDisconnected(string reason) => Disconnected?.Invoke(reason);

        public void SimulateConnected()
        {
            IsConnected = true;
            Connected?.Invoke();
        }

        public void Receive(ArraySegment<byte> data) => DataReceived?.Invoke(data, DeliveryMode.ReliableOrdered);
    }

    public sealed class SecureClientTests
    {
        private static readonly string ValidKey = SecurityKeyText.ToText(X25519.GetPublicKey(X25519.GeneratePrivateKey()));

        [Test]
        public void Required_ConfiguresTheTransportBeforeConnecting()
        {
            var transport = new SecureProbeTransport();
            var client = new NetPulseClient(transport, () => 1d);

            client.Connect(new ConnectionOptions { EncryptionMode = ClientEncryptionMode.Required, ServerPublicKeys = new[] { ValidKey } });

            Assert.AreEqual(1, transport.ConnectCalls);
            Assert.IsNotNull(transport.Configured);
            Assert.AreEqual(1, transport.Configured.ServerPublicKeys.Count);
            Assert.AreEqual(NetState.Connecting, client.State);
        }

        [Test]
        public void Off_ClearsAnyPreviousSecurityConfiguration()
        {
            var transport = new SecureProbeTransport();
            var client = new NetPulseClient(transport, () => 1d);

            client.Connect(new ConnectionOptions());

            Assert.AreEqual(1, transport.ConfigureCalls);
            Assert.IsNull(transport.Configured);
        }

        [Test]
        public void Required_WithoutAValidKey_FailsWithoutTouchingTheNetwork()
        {
            var transport = new SecureProbeTransport();
            var client = new NetPulseClient(transport, () => 1d);

            var operation = client.Connect(new ConnectionOptions { EncryptionMode = ClientEncryptionMode.Required, ServerPublicKeys = new[] { "garbage" } });
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("NetPulse"));

            Assert.IsTrue(operation.HasError);
            Assert.AreEqual(NetPulseReturnCode.InvalidConfiguration, operation.Error.ReturnCode);
            Assert.AreEqual(0, transport.ConnectCalls);
            Assert.AreEqual(NetState.Disconnected, client.State);
        }

        [Test]
        public void Required_OnATransportWithoutEncryption_IsRefused()
        {
            var client = new NetPulseClient(new FakeTransport(), () => 1d);

            var operation = client.Connect(new ConnectionOptions { EncryptionMode = ClientEncryptionMode.Required, ServerPublicKeys = new[] { ValidKey } });
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("NetPulse"));

            Assert.IsTrue(operation.HasError);
            Assert.AreEqual(NetState.Disconnected, client.State);
        }

        [TestCase(SecureDisconnectReason.SecureHandshakeFailed)]
        [TestCase(SecureDisconnectReason.SecureHandshakeTimeout)]
        public void HandshakeFailureReasonsReachTheCaller(string reason)
        {
            var transport = new SecureProbeTransport();
            var client = new NetPulseClient(transport, () => 1d);
            var reasons = new List<string>();
            client.Events.OnDisconnected += reasons.Add;
            var operation = client.Connect(new ConnectionOptions { EncryptionMode = ClientEncryptionMode.Required, ServerPublicKeys = new[] { ValidKey } });

            transport.SimulateDisconnected(reason);

            Assert.IsTrue(operation.HasError);
            Assert.AreEqual(new[] { reason }, reasons.ToArray());
            Assert.AreEqual(NetState.Disconnected, client.State);
        }

        [Test]
        public void IsEncrypted_FollowsTheTransport()
        {
            var transport = new SecureProbeTransport();
            var client = new NetPulseClient(transport, () => 1d);

            Assert.IsFalse(client.IsEncrypted);
            transport.IsEncrypted = true;
            Assert.IsTrue(client.IsEncrypted);
            Assert.IsFalse(new NetPulseClient(new FakeTransport(), () => 1d).IsEncrypted);
        }
    }

    public sealed class RejoinTokenTests
    {
        private FakeTransport _transport;
        private NetPulseClient _client;

        [SetUp]
        public void SetUp()
        {
            UnityTypeSerializers.Register();
            _transport = new FakeTransport();
            _client = new NetPulseClient(_transport, () => 100d);
            _client.Connect(new ConnectionOptions { UserId = "u", NickName = "Me" });
            _transport.SimulateConnected();
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok, new AuthenticateResponse { UserId = "u", NickName = "Me" }.ToParameters());
        }

        private void JoinWith(string token)
        {
            var join = _client.JoinOrCreateRoom("room");
            var request = _transport.NextRequest();
            var response = new JoinRoomResponse { RoomName = "room", LocalActorId = 1, MasterClientId = 1, RejoinToken = token };
            _transport.Respond(request, ReturnCode.Ok, response.ToParameters());
            Assert.IsTrue(join.IsDone);
        }

        [Test]
        public void TokenFromTheServerIsPresentedWhenJoiningTheSameRoomAgain()
        {
            JoinWith("secret-token");
            _client.LeaveRoom(true);
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok);

            _client.JoinOrCreateRoom("room");
            var request = JoinOrCreateRoomRequest.FromParameters(_transport.NextRequest().Parameters);

            Assert.AreEqual("secret-token", request.RejoinToken);
        }

        [Test]
        public void LeavingForGoodForgetsTheToken()
        {
            JoinWith("secret-token");
            _client.LeaveRoom();
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok);

            _client.JoinOrCreateRoom("room");
            var request = JoinOrCreateRoomRequest.FromParameters(_transport.NextRequest().Parameters);

            Assert.IsNull(request.RejoinToken);
        }

        [Test]
        public void NoTokenIsSentForAnotherRoom()
        {
            JoinWith("secret-token");
            _client.LeaveRoom(true);
            _transport.Respond(_transport.NextRequest(), ReturnCode.Ok);

            _client.JoinOrCreateRoom("other");
            var request = JoinOrCreateRoomRequest.FromParameters(_transport.NextRequest().Parameters);

            Assert.IsNull(request.RejoinToken);
        }
    }
}
