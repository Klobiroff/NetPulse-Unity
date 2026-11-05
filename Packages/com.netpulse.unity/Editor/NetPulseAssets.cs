using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NetPulse.Editor
{
    public static class NetPulseAssets
    {
        public const string ResourcesFolder = "Assets/Resources";
        public const string SettingsPath = ResourcesFolder + "/" + NetPulseSettings.ResourceName + ".asset";
        public const string RegistryPath = ResourcesFolder + "/NetworkPrefabRegistry.asset";

        public static NetPulseSettings FindSettings()
        {
            var settings = Resources.Load<NetPulseSettings>(NetPulseSettings.ResourceName);
            if (settings != null)
            {
                return settings;
            }

            foreach (var guid in AssetDatabase.FindAssets("t:NetPulseSettings"))
            {
                settings = AssetDatabase.LoadAssetAtPath<NetPulseSettings>(AssetDatabase.GUIDToAssetPath(guid));
                if (settings != null)
                {
                    return settings;
                }
            }

            return null;
        }

        public static NetPulseSettings GetOrCreateSettings()
        {
            var settings = FindSettings();
            if (settings != null)
            {
                return settings;
            }

            EnsureFolder(ResourcesFolder);
            settings = ScriptableObject.CreateInstance<NetPulseSettings>();
            AssetDatabase.CreateAsset(settings, SettingsPath);
            AssetDatabase.SaveAssets();
            return settings;
        }

        public static NetworkPrefabRegistry GetOrCreateRegistry(NetPulseSettings settings)
        {
            if (settings.PrefabRegistry != null)
            {
                return settings.PrefabRegistry;
            }

            var registry = AssetDatabase.LoadAssetAtPath<NetworkPrefabRegistry>(RegistryPath);
            if (registry == null)
            {
                EnsureFolder(ResourcesFolder);
                registry = ScriptableObject.CreateInstance<NetworkPrefabRegistry>();
                AssetDatabase.CreateAsset(registry, RegistryPath);
            }

            settings.PrefabRegistry = registry;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return registry;
        }

        public static bool RebuildRegistry()
        {
            var settings = GetOrCreateSettings();
            var registry = GetOrCreateRegistry(settings);
            var previous = new Dictionary<string, int>();
            foreach (var entry in registry.Entries)
            {
                if (entry != null && !string.IsNullOrEmpty(entry.Guid))
                {
                    previous[entry.Guid] = entry.Id;
                }
            }

            var nextId = Mathf.Max(1, registry.NextId);
            var entries = new List<NetworkPrefabEntry>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (prefab == null || !prefab.TryGetComponent<NetworkObject>(out _))
                {
                    continue;
                }

                if (!previous.TryGetValue(guid, out var id))
                {
                    id = nextId++;
                }

                entries.Add(new NetworkPrefabEntry(id, guid, prefab));
            }

            entries.Sort(static (a, b) => a.Id.CompareTo(b.Id));
            if (!HasChanged(registry, entries, nextId))
            {
                return false;
            }

            registry.SetEntries(entries, nextId);
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssetIfDirty(registry);
            return true;
        }

        public static void EnsureFolder(string path)
        {
            if (Directory.Exists(path))
            {
                return;
            }

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            var name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, name);
        }

        private static bool HasChanged(NetworkPrefabRegistry registry, List<NetworkPrefabEntry> entries, int nextId)
        {
            if (registry.NextId != nextId || registry.Entries.Count != entries.Count)
            {
                return true;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var current = registry.Entries[i];
                if (current == null || current.Id != entries[i].Id || current.Prefab != entries[i].Prefab || current.Guid != entries[i].Guid)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
