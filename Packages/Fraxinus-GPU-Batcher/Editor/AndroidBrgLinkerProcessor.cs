#if UNITY_2022_3
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine.Rendering;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Fraxinus.GPUBatcher.Editor
{
    public sealed class AndroidBrgLinkerProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            if (report.summary.platform != BuildTarget.Android ||
                typeof(BatchRendererGroup).Assembly.GetType("UnityEngine.Rendering.IndexedBrgOutputWriter") == null)
                return null;

            // Unity 2022's linker needs an explicit descriptor path for UPM packages.
            var package = PackageInfo.FindForAssembly(typeof(AndroidBrgLinkerProcessor).Assembly);
            if (package == null)
                throw new BuildFailedException("Cannot locate the GPU Batcher package for Android BRG linker preservation.");

            string path = Path.Combine(package.resolvedPath, "Runtime/Plugins/FraxinusNativeContract/Android/link.xml");
            if (!File.Exists(path))
                throw new BuildFailedException("Missing Android BRG linker descriptor at " + path);
            return path;
        }
    }
}
#endif
