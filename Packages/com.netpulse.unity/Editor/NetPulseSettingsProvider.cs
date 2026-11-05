using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace NetPulse.Editor
{
    public static class NetPulseSettingsProvider
    {
        public const string Path = "Project/NetPulse";

        private static SerializedObject _serialized;

        [SettingsProvider]
        public static SettingsProvider Create() => new(Path, SettingsScope.Project)
        {
            label = "NetPulse",
            keywords = new HashSet<string>(
                new[]
                {
                    "netpulse",
                    "network",
                    "multiplayer",
                    "server",
                    "photon"
                }),
            guiHandler = DrawGui,
            deactivateHandler = () => _serialized = null
        };

        [MenuItem("NetPulse/Create Settings Asset")]
        public static void CreateSettings() => Selection.activeObject = NetPulseAssets.GetOrCreateSettings();

        [MenuItem("NetPulse/Open Settings")]
        public static void OpenSettings() => SettingsService.OpenProjectSettings(Path);

        [MenuItem("NetPulse/Open Hub")]
        public static void OpenHub() => NetPulseHubWindow.Open();

        private static void DrawGui(string searchContext)
        {
            var settings = NetPulseAssets.FindSettings();
            if (settings == null)
            {
                EditorGUILayout.HelpBox("No NetPulseSettings asset found. Create one in Assets/Resources to configure the connection.", MessageType.Info);
                if (GUILayout.Button("Create Settings Asset", GUILayout.Width(200f)))
                {
                    NetPulseAssets.GetOrCreateSettings();
                }

                return;
            }

            if (_serialized == null || _serialized.targetObject != settings)
            {
                _serialized = new SerializedObject(settings);
            }

            _serialized.Update();
            EditorGUI.BeginChangeCheck();
            var property = _serialized.GetIterator();
            property.NextVisible(true);
            while (property.NextVisible(false))
            {
                EditorGUILayout.PropertyField(property, true);
            }

            if (EditorGUI.EndChangeCheck())
            {
                _serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(settings);
            }

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild Prefab Registry"))
                {
                    PrefabRegistryAutoBuild.RebuildMenu();
                }

                if (GUILayout.Button("Open Hub"))
                {
                    NetPulseHubWindow.Open();
                }
            }
        }
    }
}
