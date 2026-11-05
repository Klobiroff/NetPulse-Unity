using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetPulse
{
    [Serializable]
    public sealed class NetworkPrefabEntry
    {
        [SerializeField] private int _id;
        [SerializeField] private string _guid;
        [SerializeField] private GameObject _prefab;

        public NetworkPrefabEntry(int id, string guid, GameObject prefab)
        {
            _id = id;
            _guid = guid;
            _prefab = prefab;
        }

        public int Id => _id;

        public string Guid => _guid;

        public GameObject Prefab => _prefab;
    }

    [CreateAssetMenu(fileName = "NetworkPrefabRegistry", menuName = "NetPulse/Network Prefab Registry")]
    public sealed class NetworkPrefabRegistry : ScriptableObject
    {
        [SerializeField] private List<NetworkPrefabEntry> _entries = new();
        [SerializeField] private int _nextId = 1;

        private Dictionary<int, GameObject> _byId;
        private Dictionary<GameObject, int> _byPrefab;
        private Dictionary<string, GameObject> _byName;

        public IReadOnlyList<NetworkPrefabEntry> Entries => _entries;

        public int NextId => _nextId;

        public bool TryGetPrefab(int id, out GameObject prefab)
        {
            EnsureIndex();
            return _byId.TryGetValue(id, out prefab);
        }

        public bool TryGetId(GameObject prefab, out int id)
        {
            EnsureIndex();
            if (prefab != null)
            {
                return _byPrefab.TryGetValue(prefab, out id);
            }

            id = 0;
            return false;
        }

        public bool TryGetPrefab(string prefabName, out GameObject prefab)
        {
            EnsureIndex();
            return _byName.TryGetValue(prefabName ?? string.Empty, out prefab);
        }

        public void SetEntries(List<NetworkPrefabEntry> entries, int nextId)
        {
            _entries = entries ?? throw new ArgumentNullException(nameof(entries));
            _nextId = nextId;
            _byId = null;
        }

        private void OnEnable() => _byId = null;

        private void OnValidate() => _byId = null;

        private void EnsureIndex()
        {
            if (_byId != null)
            {
                return;
            }

            _byId = new();
            _byPrefab = new();
            _byName = new();
            foreach (var entry in _entries)
            {
                if (entry == null || entry.Prefab == null)
                {
                    continue;
                }

                _byId[entry.Id] = entry.Prefab;
                _byPrefab[entry.Prefab] = entry.Id;
                _byName[entry.Prefab.name] = entry.Prefab;
            }
        }
    }
}
