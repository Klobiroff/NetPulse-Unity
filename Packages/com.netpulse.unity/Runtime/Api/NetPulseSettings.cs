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
