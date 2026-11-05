using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NetPulse.Editor
{
    [InitializeOnLoad]
    public static class SceneIdUtility
    {
        static SceneIdUtility()
        {
            EditorSceneManager.sceneSaving += OnSceneSaving;
        }

        public static List<NetworkObject> GetSceneObjects(Scene scene)
        {
            var result = new List<NetworkObject>();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return result;
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                result.AddRange(root.GetComponentsInChildren<NetworkObject>(true));
            }

            return result;
        }

        public static int NextFreeId(Scene scene)
        {
            var used = new HashSet<int>();
            foreach (var networkObject in GetSceneObjects(scene))
            {
                if (networkObject.SceneId != 0)
                {
                    used.Add(networkObject.SceneId);
                }
            }

            var id = 1;
            while (used.Contains(id))
            {
                id++;
            }

            return id;
        }

        public static void SetSceneId(NetworkObject networkObject, int id)
        {
            var serialized = new SerializedObject(networkObject);
            serialized.FindProperty("_sceneId").intValue = id;
            serialized.ApplyModifiedProperties();
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            var seen = new HashSet<int>();
            foreach (var networkObject in GetSceneObjects(scene))
            {
                if (networkObject.SceneId == 0 || PrefabUtility.IsPartOfPrefabAsset(networkObject))
                {
                    continue;
                }

                if (seen.Add(networkObject.SceneId))
                {
                    continue;
                }

                var id = NextFreeId(scene);
                SetSceneId(networkObject, id);
                seen.Add(id);
                Debug.LogWarning($"[NetPulse] Duplicate scene id on '{networkObject.name}' was reassigned to {id}.", networkObject);
            }
        }
    }
}
