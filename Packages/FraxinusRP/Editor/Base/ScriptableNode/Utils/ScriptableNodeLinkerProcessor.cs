using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.PackageManager;
using UnityEditor.UnityLinker;

namespace FraxinusEditor.Base
{
    public sealed class ScriptableNodeLinkerProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            // Unity 2022 does not discover link.xml files inside UPM packages automatically.
            PackageInfo package = PackageInfo.FindForAssembly(typeof(ScriptableNodeLinkerProcessor).Assembly);
            if (package == null || string.IsNullOrEmpty(package.resolvedPath))
                throw new BuildFailedException("Cannot locate the FraxinusRP package for ScriptableNode linker preservation.");

            string path = Path.GetFullPath(Path.Combine(package.resolvedPath, "Runtime", "link.xml"));
            if (!File.Exists(path))
                throw new BuildFailedException("The FraxinusRP linker preservation file is missing: " + path);

            return path;
        }
    }
}
