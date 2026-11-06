using System;
using System.Collections.Generic;
using NetPulse.Common;
using UnityEngine;

namespace NetPulse
{
    public static class Pulse
    {
        private const string UserIdPrefsKey = "NetPulse.UserId";

        private static NetPulseClient _client;
        private static NetworkObjectManager _objects;
        private static NetworkSceneManager _scenes;
        private static CallbackRegistry _callbacks;
        private static INetTransport _customTransport;
        private static Func<double> _customClock;
        private static GameObject _runner;
        private static string _nickName;
        private static string _authToken;

        public static NetPulseEvents Events => Client.Events;

        public static NetState State => _client?.State ?? NetState.Disconnected;

        public static bool IsConnected => _client != null && _client.IsConnected;

        public static bool IsInRoom => _client != null && _client.IsInRoom;

        public static bool IsMasterClient => LocalPlayer != null && LocalPlayer.IsMasterClient;

        public static Room CurrentRoom => _client?.CurrentRoom;

        public static Player LocalPlayer => _client?.CurrentRoom?.LocalPlayer;

        public static int LocalActorId => _client?.LocalActorId ?? 0;

        public static IReadOnlyList<RoomInfo> LobbyRooms => _client?.LobbyRooms ?? Array.Empty<RoomInfo>();

        public static string UserId => _client != null && !string.IsNullOrEmpty(_client.UserId) ? _client.UserId : ResolveUserId(NetPulseSettings.Instance);

        public static double ServerTime => _client?.ServerTime ?? 0d;

        public static NetStats Stats => Client.Stats;

        public static int TickRate => _client?.TickRate ?? 0;

        public static long Tick => _client?.CurrentTick ?? 0L;

        public static double TickDelta => _client?.TickDelta ?? 0d;

        public static int RoundTripTimeMs => _client?.RoundTripTimeMs ?? 0;

        public static NetPulseClient Client
        {
            get
            {
                EnsureInitialized();
                return _client;
            }
        }

        public static string NickName
        {
            get => string.IsNullOrEmpty(_nickName) ? NetPulseSettings.Instance.DefaultNickName : _nickName;
            set => _nickName = value;
        }

        public static string AuthToken
        {
            get => _authToken;
            set => _authToken = value;
        }

        public static bool OfflineMode
        {
            get => _client != null && _client.OfflineMode;
            set => Client.OfflineMode = value;
        }

        internal static NetPulseClient ClientOrNull => _client;

        internal static NetworkObjectManager Objects
        {
            get
            {
                EnsureInitialized();
                return _objects;
            }
        }

        internal static NetworkObjectManager ObjectsOrNull => _objects;

        internal static NetworkSceneManager Scenes
        {
            get
            {
                EnsureInitialized();
                return _scenes;
            }
        }

        public static event Action<NetState> StateChanged
        {
            add => Client.Events.OnStateChanged += value;
            remove
            {
                if (_client != null)
                {
                    _client.Events.OnStateChanged -= value;
                }
            }
        }

        public static void UseTransport(INetTransport transport)
        {
            if (_client != null && _client.State != NetState.Disconnected)
            {
                throw new InvalidOperationException("The transport can only be replaced while disconnected.");
            }

            _customTransport = transport;
            ResetClient();
        }

        internal static void UseClock(Func<double> clock)
        {
            _customClock = clock;
            ResetClient();
        }

        public static NetOperation Connect() => Connect(NetPulseSettings.Instance);

        public static NetOperation Connect(NetPulseSettings settings)
        {
            settings ??= NetPulseSettings.Instance;
            NetPulseSettings.SetInstance(settings);
            Application.runInBackground = settings.RunInBackground || Application.runInBackground;
            var nick = string.IsNullOrEmpty(NickName) ? "Player" + UnityEngine.Random.Range(1000, 9999) : NickName;
            var options = ConnectionOptions.From(settings, ResolveUserId(settings), nick);
            options.AuthToken = _authToken;
            ApplySettings(settings);
            return Client.Connect(options);
        }

        public static NetOperation Connect(ConnectionOptions options) => Client.Connect(options);

        public static void Disconnect() => _client?.Disconnect();

        public static NetOperation JoinLobby(string lobbyName = null) => Client.JoinLobby(lobbyName);

        public static NetOperation LeaveLobby() => Client.LeaveLobby();

        public static NetOperation<IReadOnlyList<RoomInfo>> GetRoomList() => Client.GetRoomList();

        public static NetOperation<Room> CreateRoom(string roomName, RoomOptions options = null, PropertyTable playerProperties = null) =>
            Client.CreateRoom(roomName, options, playerProperties);

        public static NetOperation<Room> JoinRoom(string roomName, PropertyTable playerProperties = null) => Client.JoinRoom(roomName, playerProperties);

        public static NetOperation<Room> JoinOrCreateRoom(string roomName, RoomOptions options = null, PropertyTable playerProperties = null) =>
            Client.JoinOrCreateRoom(roomName, options, playerProperties);

        public static NetOperation<Room> JoinRandomRoom(PropertyTable expectedProperties = null, byte expectedMaxPlayers = 0, RoomOptions createOptions = null, PropertyTable playerProperties = null) =>
            Client.JoinRandomRoom(expectedProperties, expectedMaxPlayers, createOptions, playerProperties);

        public static NetOperation LeaveRoom(bool becomeInactive = false) => Client.LeaveRoom(becomeInactive);

        public static void RaiseEvent(byte eventCode, object data, RaiseEventOptions options = null) => Client.RaiseEvent(eventCode, data, options);

        public static void ChangeInterestGroups(byte[] add, byte[] remove) => Client.ChangeInterestGroups(add, remove);

        public static void Send<T>(in T message, RpcTarget target = RpcTarget.Others) where T : INetMessage => Client.Messages.Send(in message, target, null, null);

        public static void Send<T>(in T message, RpcTarget target, MessageOptions options) where T : INetMessage => Client.Messages.Send(in message, target, null, options);

        public static void Send<T>(in T message, Player player, MessageOptions options = null) where T : INetMessage =>
            Client.Messages.Send(in message, RpcTarget.Others, player ?? throw new ArgumentNullException(nameof(player)), options);

        public static IDisposable On<T>(Action<T, Player> handler) where T : INetMessage => Client.Messages.On(handler);

        public static NetOperation LoadScene(string sceneName) => Scenes.LoadScene(sceneName);

        public static NetOperation LoadScene(int buildIndex) => Scenes.LoadScene(buildIndex);

        public static NetOperation WaitForPlayersAsync(int count) => Client.WaitForPlayers(count);

        public static NetOperation WaitUntilAsync(Func<bool> condition) => Client.WaitUntil(condition);

        public static void AddCallbackTarget(object target)
        {
            EnsureInitialized();
            _callbacks.Add(target);
        }

        public static void RemoveCallbackTarget(object target) => _callbacks?.Remove(target);

        public static GameObject Instantiate(GameObject prefab, Vector3 position, Quaternion rotation, object data = null, byte interestGroup = 0) =>
            Objects.Instantiate(prefab, position, rotation, data, interestGroup);

        public static GameObject Instantiate(string prefabName, Vector3 position, Quaternion rotation, object data = null, byte interestGroup = 0)
        {
            var registry = NetPulseSettings.Instance.PrefabRegistry;
            if (registry != null && registry.TryGetPrefab(prefabName, out var prefab))
            {
                return Instantiate(prefab, position, rotation, data, interestGroup);
            }

            Debug.LogError($"[NetPulse] No prefab named '{prefabName}' in the NetworkPrefabRegistry.");
            return null;
        }

        public static void Destroy(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            if (gameObject.TryGetComponent<NetworkObject>(out var networkObject))
            {
                Destroy(networkObject);
                return;
            }

            UnityEngine.Object.Destroy(gameObject);
        }

        public static void Destroy(NetworkObject networkObject) => Objects.Destroy(networkObject);

        public static NetworkObject FindObject(int viewId) => _objects?.Find(viewId);

        private static void ApplySettings(NetPulseSettings settings)
        {
            if (_client == null || settings == null)
            {
                return;
            }

            _client.Stats.Enabled = settings.CollectStats;
            _client.AutoSyncScene = settings.AutoSyncScene;
        }

        private static string ResolveUserId(NetPulseSettings settings)
        {
            if (!settings.PersistUserId)
            {
                return Guid.NewGuid().ToString("N");
            }

            var stored = PlayerPrefs.GetString(UserIdPrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(stored))
            {
                return stored;
            }

            stored = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(UserIdPrefsKey, stored);
            return stored;
        }

        private static void EnsureInitialized()
        {
            if (_client != null)
            {
                return;
            }

            UnityTypeSerializers.Register();
            _client = new NetPulseClient(_customTransport ?? new LiteNetLibTransport(), _customClock);
            _objects = new NetworkObjectManager(_client);
            _scenes = new NetworkSceneManager(_client, _objects, new UnitySceneLoader());
            ApplySettings(NetPulseSettings.Instance);
            _callbacks = new CallbackRegistry(_client.Events);
            if (Application.isPlaying && _runner == null)
            {
                _runner = new GameObject("[NetPulse]") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(_runner);
                _runner.AddComponent<NetPulseRunner>();
            }
        }

        private static void ResetClient()
        {
            _client?.Disconnect();
            _client = null;
            _objects = null;
            _scenes = null;
            _callbacks = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ResetClient();
            _customTransport = null;
            _customClock = null;
            _nickName = null;
            _authToken = null;
            if (_runner != null)
            {
                UnityEngine.Object.Destroy(_runner);
            }

            _runner = null;
        }
    }
}
