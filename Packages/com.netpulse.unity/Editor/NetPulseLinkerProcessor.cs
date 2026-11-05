using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace NetPulse.Editor
{
    public sealed class NetPulseLinkerProcessor : IUnityLinkerProcessor
    {
        private const string Folder = "Library/NetPulse";

        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            var xml = GenerateForPlayer();
            Directory.CreateDirectory(Folder);
            var path = Path.GetFullPath(Path.Combine(Folder, "link.xml"));
            File.WriteAllText(path, xml);
            return path;
        }

        public static string GenerateForPlayer()
        {
            var playerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies)
                .Select(static a => a.name)
                .ToHashSet();
            return LinkXmlGenerator.Generate(playerAssemblies.Contains, true);
        }

        [MenuItem("NetPulse/Tools/Preview IL2CPP link.xml")]
        public static void Preview()
        {
            var xml = GenerateForPlayer();
            Directory.CreateDirectory(Folder);
            var path = Path.GetFullPath(Path.Combine(Folder, "link.preview.xml"));
            File.WriteAllText(path, xml);
            Debug.Log($"[NetPulse] Generated link.xml written to {path}\n{xml}");
            EditorUtility.RevealInFinder(path);
        }
    }
}
