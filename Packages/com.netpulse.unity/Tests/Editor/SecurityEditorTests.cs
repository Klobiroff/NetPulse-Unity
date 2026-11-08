using System;
using System.Collections.Generic;
using System.Net;
using NetPulse.Common.Security;
using NUnit.Framework;
using UnityEngine;

namespace NetPulse.Tests.Editor
{
    public sealed class ConnectionSecurityTests
    {
        private static readonly string ValidKey = SecurityKeyText.ToText(X25519.GetPublicKey(X25519.GeneratePrivateKey()));

        [Test]
        public void Off_NeedsNoKeys()
        {
            var options = new ConnectionOptions();

            Assert.IsNull(options.ValidateSecurity());
            Assert.IsTrue(options.TryBuildSecurity(out var settings, out var error));
            Assert.IsNull(settings);
            Assert.IsNull(error);
        }

        [Test]
        public void Required_WithoutKeys_IsRejected()
        {
            var options = new ConnectionOptions { EncryptionMode = ClientEncryptionMode.Required };

            Assert.IsNotNull(options.ValidateSecurity());
            Assert.IsFalse(options.TryBuildSecurity(out var settings, out _));
            Assert.IsNull(settings);
        }

        [TestCase("")]
        [TestCase("not base64!")]
        [TestCase("AAAA")]
        public void Required_WithMalformedKey_IsRejected(string key)
        {
            var options = new ConnectionOptions { EncryptionMode = ClientEncryptionMode.Required, ServerPublicKeys = new[] { key } };

            Assert.IsNotNull(options.ValidateSecurity());
        }

        [Test]
        public void Required_WithValidKeys_BuildsSettings()
        {
            var second = SecurityKeyText.ToText(X25519.GetPublicKey(X25519.GeneratePrivateKey()));
            var psk = SecurityKeyText.ToText(X25519.GeneratePrivateKey());
            var options = new ConnectionOptions
            {
                EncryptionMode = ClientEncryptionMode.Required,
                ServerPublicKeys = new[] { ValidKey, second },
                PreSharedKey = psk,
                PaddingBlock = 64,
                ReplayWindow = 1024,
                RekeyAfterPackets = 500,
                RekeyAfterMs = 60000,
                HandshakeTimeout = 3f
            };

            Assert.IsTrue(options.TryBuildSecurity(out var settings, out var error), error);
            Assert.AreEqual(2, settings.ServerPublicKeys.Count);
            Assert.AreEqual(32, settings.PreSharedKey.Length);
            Assert.AreEqual(64, settings.Session.PaddingBlock);
            Assert.AreEqual(1024, settings.Session.ReplayWindow);
            Assert.AreEqual(500, settings.Session.RekeyAfterPackets);
            Assert.AreEqual(3000, settings.HandshakeTimeoutMs);
        }

        [Test]
        public void Required_WithBadNumbers_IsRejected()
        {
            var good = new ConnectionOptions { EncryptionMode = ClientEncryptionMode.Required, ServerPublicKeys = new[] { ValidKey } };

            Assert.IsNull(good.ValidateSecurity());

            good.ReplayWindow = 100;
            Assert.IsNotNull(good.ValidateSecurity());

            good.ReplayWindow = 4096;
            good.PaddingBlock = 8;
            Assert.IsNotNull(good.ValidateSecurity());

            good.PaddingBlock = 0;
            good.PreSharedKey = "short";
            Assert.IsNotNull(good.ValidateSecurity());
        }
    }

    public sealed class SecuritySettingsTests
    {
        [Test]
        public void Defaults_KeepEncryptionOffAndSane()
        {
            var settings = ScriptableObject.CreateInstance<NetPulseSettings>();

            try
            {
                Assert.AreEqual(ClientEncryptionMode.Off, settings.EncryptionMode);
                Assert.IsEmpty(settings.ServerPublicKeys);
                Assert.AreEqual(4096, settings.ReplayWindow);
                Assert.AreEqual(0, settings.PaddingBlock);
                Assert.AreEqual(1000000, settings.RekeyAfterPackets);
                Assert.AreEqual(600000, settings.RekeyAfterMs);
                Assert.AreEqual(5f, settings.HandshakeTimeout);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Fields_SurviveSerialization()
        {
            var key = SecurityKeyText.ToText(X25519.GetPublicKey(X25519.GeneratePrivateKey()));
            var original = ScriptableObject.CreateInstance<NetPulseSettings>();
            var copy = ScriptableObject.CreateInstance<NetPulseSettings>();

            try
            {
                original.EncryptionMode = ClientEncryptionMode.Required;
                original.ServerPublicKeys = new[] { key };
                original.PreSharedKey = "psk-text";
                original.PaddingBlock = 128;
                original.ReplayWindow = 2048;
                original.RekeyAfterPackets = 77;
                original.RekeyAfterMs = 88;
                original.HandshakeTimeout = 7f;

                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(original), copy);

                Assert.AreEqual(ClientEncryptionMode.Required, copy.EncryptionMode);
                Assert.AreEqual(new[] { key }, copy.ServerPublicKeys);
                Assert.AreEqual("psk-text", copy.PreSharedKey);
                Assert.AreEqual(128, copy.PaddingBlock);
                Assert.AreEqual(2048, copy.ReplayWindow);
                Assert.AreEqual(77, copy.RekeyAfterPackets);
                Assert.AreEqual(88, copy.RekeyAfterMs);
                Assert.AreEqual(7f, copy.HandshakeTimeout);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(original);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        [Test]
        public void ConnectionOptions_CopyEncryptionSettings()
        {
            var key = SecurityKeyText.ToText(X25519.GetPublicKey(X25519.GeneratePrivateKey()));
            var settings = ScriptableObject.CreateInstance<NetPulseSettings>();

            try
            {
                settings.EncryptionMode = ClientEncryptionMode.Required;
                settings.ServerPublicKeys = new[] { key };
                settings.PaddingBlock = 32;

                var options = ConnectionOptions.From(settings, "user", "nick");

                Assert.AreEqual(ClientEncryptionMode.Required, options.EncryptionMode);
                Assert.AreEqual(new[] { key }, options.ServerPublicKeys);
                Assert.AreEqual(32, options.PaddingBlock);
                Assert.IsNull(options.ValidateSecurity());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }

    public sealed class SecureClientLayerTests
    {
        private const byte ConnectRequestProperty = 5;
        private const byte PingProperty = 3;

        private readonly IPEndPoint _server = new(IPAddress.Parse("198.51.100.20"), 5055);

        private byte[] _serverPrivate;
        private SecureServerCore _serverCore;
        private SecureClientCore _clientCore;
        private SecureClientLayer _layer;
        private SecureEndpoint _clientEndpoint;
        private long _now;
        private byte[] _reply;

        [SetUp]
        public void SetUp()
        {
            _now = 1000;
            _reply = new byte[1024];
            _serverPrivate = X25519.GeneratePrivateKey();
            _serverCore = new SecureServerCore(
                new SecureServerSettings { PrivateKeys = new List<byte[]> { _serverPrivate } },
                new SecurityCounters(),
                () => _now);
            _clientCore = new SecureClientCore(
                new SecureClientSettings { ServerPublicKeys = new List<byte[]> { X25519.GetPublicKey(_serverPrivate) } },
                new SecurityCounters(),
                () => _now);
            _layer = new SecureClientLayer(_clientCore);
            _clientEndpoint = SecureEndpoint.From(IPAddress.Parse("203.0.113.5"), 41000);
        }

        [TearDown]
        public void TearDown() => _clientCore.Dispose();

        private static byte[] ConnectRequest()
        {
            var packet = new byte[60];
            packet[0] = ConnectRequestProperty;
            for (var i = 1; i < packet.Length; i++)
            {
                packet[i] = (byte)(i + 10);
            }

            return packet;
        }

        private byte[] Outbound(byte[] packet, int extra = 128)
        {
            var data = new byte[packet.Length + extra];
            packet.CopyTo(data, 0);
            var endpoint = _server;
            var offset = 0;
            var length = packet.Length;
            _layer.ProcessOutBoundPacket(ref endpoint, ref data, ref offset, ref length);
            return length == 0 ? Array.Empty<byte>() : data.AsSpan(offset, length).ToArray();
        }

        private byte[] Inbound(byte[] record, IPEndPoint from = null)
        {
            var data = new byte[2048];
            record.CopyTo(data, 0);
            var endpoint = from ?? _server;
            var length = record.Length;
            _layer.ProcessInboundPacket(ref endpoint, ref data, ref length);
            return data.AsSpan(0, length).ToArray();
        }

        private byte[] ServerHandle(byte[] record)
        {
            var data = new byte[2048];
            record.CopyTo(data, 0);
            var result = _serverCore.ProcessInbound(_clientEndpoint, data, record.Length, _reply);
            return result.Kind switch
            {
                SecureInboundKind.Reply => _reply.AsSpan(0, result.Length).ToArray(),
                SecureInboundKind.Decrypted => data.AsSpan(0, result.Length).ToArray(),
                _ => Array.Empty<byte>()
            };
        }

        private void Handshake()
        {
            var connect = ConnectRequest();
            var init = Outbound(connect);
            var retry = ServerHandle(init);
            Assert.AreEqual(SecureRecord.Retry, retry[0]);
            Assert.AreEqual(0, Inbound(retry).Length);
            var cookied = Outbound(connect);
            var response = ServerHandle(cookied);
            Assert.AreEqual(SecureRecord.Response, response[0]);
            Assert.AreEqual(0, Inbound(response).Length);
            Assert.AreEqual(SecureClientState.Established, _clientCore.State);
        }

        [Test]
        public void ConnectRequest_IsReplacedByAnInitRecordWithoutThePlaintext()
        {
            var connect = ConnectRequest();
            var record = Outbound(connect);

            Assert.GreaterOrEqual(record.Length, SecureRecord.InitMinimumSize);
            Assert.AreEqual(SecureRecord.Init, record[0]);
            Assert.IsFalse(record.AsSpan().IndexOf(connect.AsSpan(1, 20)) >= 0);
        }

        [Test]
        public void OtherPlainPacketsAreDroppedBeforeTheSessionExists()
        {
            Assert.AreEqual(0, Outbound(new byte[] { PingProperty, 1, 2, 3 }).Length);
            Assert.AreEqual(SecureClientState.Idle, _clientCore.State);
        }

        [Test]
        public void ReplyToTheServerIsNeverLongerThanTheInit()
        {
            var init = Outbound(ConnectRequest());
            var retry = ServerHandle(init);

            Assert.Less(retry.Length, init.Length);
        }

        [Test]
        public void PlaintextAndForeignSourcesAreDroppedOnInbound()
        {
            Outbound(ConnectRequest());

            Assert.AreEqual(0, Inbound(new byte[] { 3, 0, 0, 0 }).Length);
            var impostor = new IPEndPoint(IPAddress.Parse("203.0.113.99"), 5055);
            var retry = ServerHandle(Outbound(ConnectRequest()));
            Assert.AreEqual(0, Inbound(retry, impostor).Length);
        }

        [Test]
        public void AfterTheHandshakeDataFlowsBothWaysEncrypted()
        {
            Handshake();
            var ping = new byte[] { PingProperty, 9, 8, 7, 6, 5 };
            var record = Outbound(ping);

            Assert.AreEqual(ping.Length + SecureRecord.Overhead, record.Length);
            Assert.AreEqual(SecureRecord.Data, record[0]);

            var first = Outbound(ConnectRequest());
            Assert.AreEqual(SecureRecord.Data, first[0]);
            Assert.AreEqual(ConnectRequest(), ServerHandle(first));
            Assert.AreEqual(ping, ServerHandle(record));

            var outbound = new byte[100];
            new byte[] { 1, 2, 3 }.CopyTo(outbound, 0);
            var written = _serverCore.ProtectOutbound(_clientEndpoint, outbound, 0, 3);
            Assert.Greater(written, 0);
            Assert.AreEqual(new byte[] { 1, 2, 3 }, Inbound(outbound.AsSpan(0, written).ToArray()));
        }

        [Test]
        public void TamperedOrReplayedRecordsAreDropped()
        {
            Handshake();
            var first = Outbound(ConnectRequest());
            ServerHandle(first);
            var outbound = new byte[100];
            outbound[0] = 7;
            var written = _serverCore.ProtectOutbound(_clientEndpoint, outbound, 0, 1);
            var record = outbound.AsSpan(0, written).ToArray();

            var tampered = (byte[])record.Clone();
            tampered[tampered.Length - 1] ^= 1;
            Assert.AreEqual(0, Inbound(tampered).Length);
            Assert.AreEqual(1, Inbound(record).Length);
            Assert.AreEqual(0, Inbound(record).Length);
        }

        [Test]
        public void ExtraPacketSizeCoversRecordOverhead()
        {
            Assert.AreEqual(SecureRecord.Overhead, _layer.ExtraPacketSizeForLayer);
            var padded = new SecureClientCore(
                new SecureClientSettings
                {
                    ServerPublicKeys = new List<byte[]> { X25519.GetPublicKey(_serverPrivate) },
                    Session = new SecureSessionSettings { PaddingBlock = 64 }
                },
                new SecurityCounters(),
                () => _now);

            try
            {
                Assert.AreEqual(SecureRecord.PaddedOverhead + 63, new SecureClientLayer(padded).ExtraPacketSizeForLayer);
            }
            finally
            {
                padded.Dispose();
            }
        }
    }
}
