using UnityEditor;
using UnityEngine;

namespace NetPulse.Editor
{
    [CustomEditor(typeof(NetworkObject))]
    [CanEditMultipleObjects]
    public sealed class NetworkObjectEditor : UnityEditor.Editor
    {
        private SerializedProperty _ownershipMode;
        private SerializedProperty _sceneId;
        private SerializedProperty _interestGroup;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(_ownershipMode);
            EditorGUILayout.PropertyField(_interestGroup);
            DrawSceneObject();
            serializedObject.ApplyModifiedProperties();
            DrawRuntime();
        }

        private void OnEnable()
        {
            _ownershipMode = serializedObject.FindProperty("_ownershipMode");
            _sceneId = serializedObject.FindProperty("_sceneId");
            _interestGroup = serializedObject.FindProperty("_interestGroup");
        }

        private void DrawSceneObject()
        {
            var networkObject = (NetworkObject)target;
            var isPrefabAsset = PrefabUtility.IsPartOfPrefabAsset(networkObject);
            using (new EditorGUI.DisabledScope(isPrefabAsset || targets.Length > 1))
            {
                var isScene = _sceneId.intValue != 0;
                var toggled = EditorGUILayout.Toggle(new GUIContent("Scene Object", "Placed in the scene and shared by all players. Owned by the master client."), isScene);
                if (toggled != isScene)
                {
                    _sceneId.intValue = toggled ? SceneIdUtility.NextFreeId(networkObject.gameObject.scene) : 0;
                }

                if (_sceneId.intValue != 0)
                {
                    EditorGUILayout.PropertyField(_sceneId, new GUIContent("Scene Id"));
                    if (_sceneId.intValue < 1 || _sceneId.intValue > 999)
                    {
                        EditorGUILayout.HelpBox("Scene ids must be in the range 1-999.", MessageType.Error);
                    }
                }
            }

            if (isPrefabAsset)
            {
                EditorGUILayout.HelpBox("Prefabs with a NetworkObject are added to the NetworkPrefabRegistry automatically and can be spawned with Pulse.Instantiate.", MessageType.Info);
            }
        }

        private void DrawRuntime()
        {
            if (!Application.isPlaying || targets.Length > 1)
            {
                return;
            }

            var networkObject = (NetworkObject)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Runtime", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.IntField("View Id", networkObject.ViewId);
                EditorGUILayout.IntField("Owner Actor", networkObject.EffectiveOwnerActorId);
                EditorGUILayout.Toggle("Is Mine", networkObject.IsMine);
                EditorGUILayout.Toggle("Spawned", networkObject.IsSpawned);
            }

            Repaint();
        }
    }
}
