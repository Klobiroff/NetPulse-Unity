using System;
using System.Globalization;
using NetPulse.Common;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NetPulse
{
    internal interface ISceneObjectHost
    {
        void BeginSceneChange();

        void FlushSceneRegistrationIfMaster();
    }

    internal interface ISceneLoader
    {
        bool IsActive(string nameOrIndex);

        void Load(string nameOrIndex, Action<string> onLoaded);
    }

    internal sealed class UnitySceneLoader : ISceneLoader
    {
        public bool IsActive(string nameOrIndex)
        {
            var active = SceneManager.GetActiveScene();
            return int.TryParse(nameOrIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                ? active.buildIndex == index
                : string.Equals(active.name, nameOrIndex, StringComparison.Ordinal);
        }

        public void Load(string nameOrIndex, Action<string> onLoaded)
        {
            var operation = int.TryParse(nameOrIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                ? SceneManager.LoadSceneAsync(index)
                : SceneManager.LoadSceneAsync(nameOrIndex);
            if (operation == null)
            {
                onLoaded(null);
                return;
            }

            operation.completed += _ => onLoaded(SceneManager.GetActiveScene().name);
        }
    }

    internal sealed class NetworkSceneManager
    {
        private readonly NetPulseClient _client;
        private readonly ISceneObjectHost _objects;
        private readonly ISceneLoader _loader;
        private int _currentLoadId;
        private int _token;
        private bool _loading;

        public NetworkSceneManager(NetPulseClient client, ISceneObjectHost objects, ISceneLoader loader)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _objects = objects ?? throw new ArgumentNullException(nameof(objects));
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            var events = _client.Events;
            events.OnJoinedRoom += OnJoinedRoom;
            events.OnRoomPropertiesChanged += OnRoomPropertiesChanged;
            events.OnLeftRoom += Reset;
            events.OnDisconnected += _ => Reset();
        }

        public bool IsLoading => _loading;

        public NetOperation LoadScene(int buildIndex) => LoadScene(buildIndex.ToString(CultureInfo.InvariantCulture));

        public NetOperation LoadScene(string nameOrIndex)
        {
            if (string.IsNullOrEmpty(nameOrIndex))
            {
                throw new ArgumentException("Scene name must not be empty.", nameof(nameOrIndex));
            }

            if (!_client.IsInRoom)
            {
                return NetOperation.Failed(NetPulseReturnCode.InvalidState, "Not in a room");
            }

            if (_client.CurrentRoom.MasterClientId != _client.LocalActorId)
            {
                Debug.LogWarning("[NetPulse] Only the master client can load a network scene. Ask the master (for example with an RPC) or wait for the room to switch.");
                return NetOperation.Failed(ReturnCode.Forbidden, "Only the master client can load a network scene");
            }

            var loadId = Math.Max(ParseLoadId(_client.CurrentRoom.GetProperty<string>(RoomPropertyKeys.Scene)), _currentLoadId) + 1;
            _currentLoadId = loadId;
            var operation = new NetOperation();
            var table = new PropertyTable();
            table[RoomPropertyKeys.Scene] = $"{loadId}:{nameOrIndex}";
            _client.SetRoomProperties(table);
            if (_client.AutoSyncScene)
            {
                BeginLoad(nameOrIndex, operation);
            }
            else
            {
                operation.Complete();
            }

            return operation;
        }

        private static int ParseLoadId(string value) => TryParse(value, out var id, out _) ? id : 0;

        private static bool TryParse(string value, out int loadId, out string spec)
        {
            loadId = 0;
            spec = null;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            var separator = value.IndexOf(':');
            if (separator <= 0 || separator == value.Length - 1)
            {
                return false;
            }

            if (!int.TryParse(value.Substring(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out loadId))
            {
                return false;
            }

            spec = value.Substring(separator + 1);
            return true;
        }

        private void OnJoinedRoom()
        {
            _currentLoadId = 0;
            _loading = false;
            _token++;
            if (!_client.AutoSyncScene || !TryParse(_client.CurrentRoom.GetProperty<string>(RoomPropertyKeys.Scene), out var loadId, out var spec))
            {
                return;
            }

            _currentLoadId = loadId;
            if (_loader.IsActive(spec))
            {
                return;
            }

            BeginLoad(spec, null);
        }

        private void OnRoomPropertiesChanged(PropertyTable changed)
        {
            if (!_client.AutoSyncScene || changed == null || !changed.ContainsKey(RoomPropertyKeys.Scene))
            {
                return;
            }

            if (!TryParse(changed.Get<string>(RoomPropertyKeys.Scene), out var loadId, out var spec) || loadId == _currentLoadId)
            {
                return;
            }

            _currentLoadId = loadId;
            BeginLoad(spec, null);
        }

        private void BeginLoad(string spec, NetOperation operation)
        {
            var token = ++_token;
            if (!_loading)
            {
                _loading = true;
                _client.SuspendRoomEvents();
            }

            _objects.BeginSceneChange();
            _loader.Load(spec, sceneName => OnLoaded(token, sceneName, operation));
        }

        private void OnLoaded(int token, string sceneName, NetOperation operation)
        {
            if (token != _token || !_loading)
            {
                return;
            }

            _loading = false;
            if (sceneName == null)
            {
                Debug.LogError("[NetPulse] The network scene could not be loaded. Is it added to Build Settings?");
                _client.ResumeRoomEvents();
                operation?.Fail(NetPulseReturnCode.InvalidState, "Scene could not be loaded");
                return;
            }

            _objects.FlushSceneRegistrationIfMaster();
            _client.ResumeRoomEvents();
            _client.Events.RaiseNetworkSceneLoaded(sceneName);
            operation?.Complete();
        }

        private void Reset()
        {
            _loading = false;
            _currentLoadId = 0;
            _token++;
        }
    }
}
