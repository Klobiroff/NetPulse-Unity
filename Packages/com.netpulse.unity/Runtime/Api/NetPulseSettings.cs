using System;
using NetPulse.Common.Security;
using UnityEngine;

namespace NetPulse
{
    public enum StatsCollection
    {
        Auto,
        Always,
        Never
    }

    [CreateAssetMenu(fileName = "NetPulseSettings", menuName = "NetPulse/Settings")]
    public sealed class NetPulseSettings : ScriptableObject
    {
        public const string ResourceName = "NetPulseSettings";

        private static NetPulseSettings _instance;

        [Header("Server")]
        [SerializeField] private string _host = "127.0.0.1";
        [SerializeField] private int _port = 5055;
        [SerializeField] private string _appId = string.Empty;
        [SerializeField] private string _appVersion = "1.0";
        [SerializeField] private string _connectionKey = "NetPulse";

        [Header("Security")]
        [Tooltip("Required = the client never sends or accepts plain packets. Off = plaintext; use it only against a server whose encryption mode is Disabled or Optional.")]
        [SerializeField] private ClientEncryptionMode _encryptionMode = ClientEncryptionMode.Off;
        [Tooltip("Base64 public keys printed by NetPulse.Server keygen. Several keys let you rotate the server key without breaking old builds.")]
        [SerializeField] private string[] _serverPublicKeys = Array.Empty<string>();
        [Tooltip("Optional Base64 pre-shared key (32 bytes). It ends up in the build and can be extracted, so it is only an extra gate.")]
        [SerializeField] private string _preSharedKey = string.Empty;
        [SerializeField, Min(0)] private long _rekeyAfterPackets = 1000000;
        [SerializeField, Min(0)] private int _rekeyAfterMs = 600000;
        [Tooltip("Multiple of 64, 64..65536.")]
        [SerializeField] private int _replayWindow = 4096;
        [Tooltip("0 = off, otherwise 16..512. Pads every packet to a multiple of this size to hide message lengths, at the cost of bandwidth. Use the same value on the server.")]
        [SerializeField] private int _paddingBlock;
        [SerializeField, Min(0.5f)] private float _handshakeTimeout = 5f;

        [Header("Player")]
        [SerializeField] private string _defaultNickName = string.Empty;
        [SerializeField] private bool _persistUserId = true;

        [Header("Rates")]
        [SerializeField, Range(1, 60)] private int _sendRate = 20;
        [Tooltip("0 = use the server tick rate.")]
        [SerializeField, Range(0, 60)] private int _serializationRate = 0;
        [SerializeField, Min(1f)] private float _requestTimeout = 10f;

        [Header("Behaviour")]
        [SerializeField] private bool _runInBackground = true;
        [SerializeField] private bool _autoSyncScene = true;
        [Tooltip("Auto = collect traffic statistics in the editor and development builds only.")]
        [SerializeField] private StatsCollection _collectStats = StatsCollection.Auto;
        [SerializeField] private bool _logVerbose;
        [SerializeField] private NetworkPrefabRegistry _prefabRegistry;

        public static NetPulseSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<NetPulseSettings>(ResourceName);
                }

                if (_instance == null)
                {
                    _instance = CreateInstance<NetPulseSettings>();
                }

                return _instance;
            }
        }

        public string Host
        {
            get => _host;
            set => _host = value;
        }

        public int Port
        {
            get => _port;
            set => _port = value;
        }

        public string AppId
        {
            get => _appId;
            set => _appId = value;
        }

        public string AppVersion
        {
            get => _appVersion;
            set => _appVersion = value;
        }

        public string ConnectionKey
        {
            get => _connectionKey;
            set => _connectionKey = value;
        }

        public ClientEncryptionMode EncryptionMode
        {
            get => _encryptionMode;
            set => _encryptionMode = value;
        }

        public string[] ServerPublicKeys
        {
            get => _serverPublicKeys;
            set => _serverPublicKeys = value ?? Array.Empty<string>();
        }

        public string PreSharedKey
        {
            get => _preSharedKey;
            set => _preSharedKey = value ?? string.Empty;
        }

        public long RekeyAfterPackets
        {
            get => _rekeyAfterPackets;
            set => _rekeyAfterPackets = value;
        }

        public int RekeyAfterMs
        {
            get => _rekeyAfterMs;
            set => _rekeyAfterMs = value;
        }

        public int ReplayWindow
        {
            get => _replayWindow;
            set => _replayWindow = value;
        }

        public int PaddingBlock
        {
            get => _paddingBlock;
            set => _paddingBlock = value;
        }

        public float HandshakeTimeout
        {
            get => _handshakeTimeout;
            set => _handshakeTimeout = value;
        }

        public string DefaultNickName
        {
            get => _defaultNickName;
            set => _defaultNickName = value;
        }

        public bool PersistUserId
        {
            get => _persistUserId;
            set => _persistUserId = value;
        }

        public int SendRate
        {
            get => _sendRate;
            set => _sendRate = value;
        }

        public int SerializationRate
        {
            get => _serializationRate;
            set => _serializationRate = value;
        }

        public float RequestTimeout
        {
            get => _requestTimeout;
            set => _requestTimeout = value;
        }

        public bool RunInBackground
        {
            get => _runInBackground;
            set => _runInBackground = value;
        }

        public bool AutoSyncScene
        {
            get => _autoSyncScene;
            set => _autoSyncScene = value;
        }

        public bool CollectStats
        {
            get => _collectStats == StatsCollection.Always || (_collectStats == StatsCollection.Auto && (Application.isEditor || Debug.isDebugBuild));
            set => _collectStats = value ? StatsCollection.Always : StatsCollection.Never;
        }

        public bool LogVerbose
        {
            get => _logVerbose;
            set => _logVerbose = value;
        }

        public NetworkPrefabRegistry PrefabRegistry
        {
            get => _prefabRegistry;
            set => _prefabRegistry = value;
        }

        public static void SetInstance(NetPulseSettings settings) => _instance = settings;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;
    }
}
