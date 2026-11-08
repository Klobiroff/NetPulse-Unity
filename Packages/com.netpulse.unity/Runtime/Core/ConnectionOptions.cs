using System;
using System.Collections.Generic;
using NetPulse.Common.Security;

namespace NetPulse
{
    public sealed class ConnectionOptions
    {
        public string Host { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 5055;

        public string ConnectionKey { get; set; } = "NetPulse";

        public string AppId { get; set; } = string.Empty;

        public string AppVersion { get; set; } = "1.0";

        public string UserId { get; set; }

        public string NickName { get; set; }

        public string AuthToken { get; set; }

        public float RequestTimeout { get; set; } = 10f;

        public ClientEncryptionMode EncryptionMode { get; set; } = ClientEncryptionMode.Off;

        public string[] ServerPublicKeys { get; set; } = Array.Empty<string>();

        public string PreSharedKey { get; set; } = string.Empty;

        public long RekeyAfterPackets { get; set; } = 1000000;

        public int RekeyAfterMs { get; set; } = 600000;

        public int ReplayWindow { get; set; } = 4096;

        public int PaddingBlock { get; set; }

        public float HandshakeTimeout { get; set; } = 5f;

        public static ConnectionOptions From(NetPulseSettings settings, string userId, string nickName) => new()
        {
            Host = settings.Host,
            Port = settings.Port,
            ConnectionKey = settings.ConnectionKey,
            AppId = settings.AppId,
            AppVersion = settings.AppVersion,
            RequestTimeout = settings.RequestTimeout,
            UserId = userId,
            NickName = nickName,
            EncryptionMode = settings.EncryptionMode,
            ServerPublicKeys = settings.ServerPublicKeys,
            PreSharedKey = settings.PreSharedKey,
            RekeyAfterPackets = settings.RekeyAfterPackets,
            RekeyAfterMs = settings.RekeyAfterMs,
            ReplayWindow = settings.ReplayWindow,
            PaddingBlock = settings.PaddingBlock,
            HandshakeTimeout = settings.HandshakeTimeout
        };

        public static string ValidateKey(string text, string name)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return $"{name} is empty";
            }

            return SecurityKeyText.TryParseKey(text, out _) ? null : $"{name} must be a Base64-encoded 32-byte key";
        }

        public string ValidateSecurity()
        {
            if (EncryptionMode == ClientEncryptionMode.Off)
            {
                return null;
            }

            if (ServerPublicKeys == null || ServerPublicKeys.Length == 0)
            {
                return "Encryption is required but no server public key is configured";
            }

            for (var i = 0; i < ServerPublicKeys.Length; i++)
            {
                var error = ValidateKey(ServerPublicKeys[i], $"Server public key #{i + 1}");

                if (error != null)
                {
                    return error;
                }
            }

            if (!string.IsNullOrWhiteSpace(PreSharedKey) && ValidateKey(PreSharedKey, "Pre-shared key") is { } pskError)
            {
                return pskError;
            }

            if (ReplayWindow < 64 || ReplayWindow > 65536 || ReplayWindow % 64 != 0)
            {
                return "Replay window must be a multiple of 64 between 64 and 65536";
            }

            if (PaddingBlock != 0 && (PaddingBlock < 16 || PaddingBlock > SecureRecord.MaxPaddingBlock))
            {
                return "Padding block must be 0 or between 16 and 512";
            }

            if (RekeyAfterPackets < 0 || RekeyAfterMs < 0)
            {
                return "Rekey limits must not be negative";
            }

            return HandshakeTimeout < 0.5f ? "Handshake timeout must be at least 0.5 seconds" : null;
        }

        public bool TryBuildSecurity(out SecureClientSettings settings, out string error)
        {
            settings = null;
            error = ValidateSecurity();

            if (error != null || EncryptionMode == ClientEncryptionMode.Off)
            {
                return error == null;
            }

            var keys = new List<byte[]>();

            foreach (var text in ServerPublicKeys)
            {
                SecurityKeyText.TryParseKey(text, out var key);
                keys.Add(key);
            }

            settings = new SecureClientSettings
            {
                ServerPublicKeys = keys,
                HandshakeTimeoutMs = (int)(HandshakeTimeout * 1000f),
                Session = new SecureSessionSettings
                {
                    RekeyAfterPackets = RekeyAfterPackets,
                    RekeyAfterMs = RekeyAfterMs,
                    ReplayWindow = ReplayWindow,
                    PaddingBlock = PaddingBlock
                }
            };

            if (!string.IsNullOrWhiteSpace(PreSharedKey))
            {
                SecurityKeyText.TryParseKey(PreSharedKey, out var psk);
                settings.PreSharedKey = psk;
            }

            return true;
        }
    }
}
