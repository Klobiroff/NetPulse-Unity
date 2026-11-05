using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace NetPulse.Editor
{
    public sealed class PrefabRegistryAutoBuild : AssetPostprocessor, IPreprocessBuildWithReport
    {
        private static bool _scheduled;

        public int callbackOrder => -100;

        public void OnPreprocessBuild(BuildReport report) => NetPulseAssets.RebuildRegistry();

        [MenuItem("NetPulse/Rebuild Prefab Registry")]
        public static void RebuildMenu()
        {
            var changed = NetPulseAssets.RebuildRegistry();
            UnityEngine.Debug.Log(changed ? "[NetPulse] Prefab registry updated." : "[NetPulse] Prefab registry is up to date.");
        }

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (_scheduled || (!ContainsPrefab(imported) && !ContainsPrefab(deleted) && !ContainsPrefab(moved)))
            {
                return;
            }

            _scheduled = true;
            EditorApplication.delayCall += RebuildDeferred;
        }

        private static void RebuildDeferred()
        {
            _scheduled = false;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || NetPulseAssets.FindSettings() == null)
            {
                return;
            }

            NetPulseAssets.RebuildRegistry();
        }

        private static bool ContainsPrefab(string[] paths)
        {
            foreach (var path in paths)
            {
                if (path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase) && path.StartsWith("Assets/", System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
