using NetPulse.Samples.Basic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NetPulse.Samples.Basic.Editor
{
    public static class BasicSampleSceneBuilder
    {
        private const string Folder = "Assets/NetPulse Basic Sample";

        [MenuItem("NetPulse/Samples/Build Basic Sample Scene")]
        public static void Build()
        {
            EditorApplication.ExecuteMenuItem("NetPulse/Create Settings Asset");
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets", "NetPulse Basic Sample");
            }

            var prefab = CreatePlayerPrefab();
            EditorApplication.ExecuteMenuItem("NetPulse/Rebuild Prefab Registry");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(2f, 1f, 2f);

            var camera = Camera.main;
            if (camera != null)
            {
                camera.transform.position = new Vector3(0f, 12f, -10f);
                camera.transform.rotation = Quaternion.Euler(50f, 0f, 0f);
            }

            CreateTakeoverCube();

            var ui = new GameObject("Basic Sample UI").AddComponent<BasicSampleUI>();
            var serialized = new SerializedObject(ui);
            serialized.FindProperty("_playerPrefab").objectReferenceValue = prefab;
            serialized.ApplyModifiedProperties();

            var mainPath = Folder + "/BasicSample.unity";
            EditorSceneManager.SaveScene(scene, mainPath);
            var arenaPath = BuildArenaScene();
            AddToBuildSettings(mainPath, arenaPath);
            EditorSceneManager.OpenScene(mainPath);
            Debug.Log($"[NetPulse] Basic sample scenes created: {mainPath} and {arenaPath} (both added to Build Settings).");
        }

        private static string BuildArenaScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Arena Ground";
            ground.transform.localScale = new Vector3(2f, 1f, 2f);
            var camera = Camera.main;
            if (camera != null)
            {
                camera.transform.position = new Vector3(0f, 12f, -10f);
                camera.transform.rotation = Quaternion.Euler(50f, 0f, 0f);
            }

            var path = Folder + "/BasicSampleArena.unity";
            EditorSceneManager.SaveScene(scene, path);
            return path;
        }

        private static void AddToBuildSettings(params string[] paths)
        {
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (var path in paths)
            {
                if (!scenes.Exists(existing => existing.path == path))
                {
                    scenes.Add(new EditorBuildSettingsScene(path, true));
                }
            }

            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void CreateTakeoverCube()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Takeover Cube";
            cube.transform.position = new Vector3(0f, 0.75f, 3f);
            cube.transform.localScale = Vector3.one * 1.5f;
            var networkObject = cube.AddComponent<NetworkObject>();
            var serialized = new SerializedObject(networkObject);
            serialized.FindProperty("_ownershipMode").enumValueIndex = (int)OwnershipMode.Takeover;
            serialized.FindProperty("_sceneId").intValue = 1;
            serialized.ApplyModifiedProperties();
            cube.AddComponent<NetworkTransform>();
            cube.AddComponent<TakeoverCube>();
        }

        private static GameObject CreatePlayerPrefab()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "SamplePlayer";
            var networkObject = go.AddComponent<NetworkObject>();
            networkObject.PersistAcrossScenes = true;
            go.AddComponent<NetworkTransform>();
            go.AddComponent<SamplePlayer>();
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, Folder + "/SamplePlayer.prefab");
            Object.DestroyImmediate(go);
            return prefab;
        }
    }
}
