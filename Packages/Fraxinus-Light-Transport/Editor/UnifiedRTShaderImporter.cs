using UnityEditor.AssetImporters;
using UPMPackageInfo = UnityEditor.PackageManager.PackageInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UnityEditor.Rendering.UnifiedRayTracing
{
    // Version 4 also invalidates failed imports whose missing includes were
    // never registered, so creating those files did not rebuild the shader.
    [ScriptedImporter(4, "urtshader")]
    internal class UnifiedRTShaderImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var dependencies = new IncludeDependencies(ctx);
            string source = dependencies.ReadSource(ctx.assetPath);
            string computeSource = computeShaderTemplate.Replace("SHADERCODE", source);
            dependencies.Register(computeSource, ctx.assetPath);

            var com = ShaderUtil.CreateComputeShaderAsset(ctx, computeSource);
            ctx.AddObjectToAsset("ComputeShader", com);
#if UNITY_6000_0_OR_NEWER
            string raytracingSource = raytracingShaderTemplate.Replace("SHADERCODE", source);
            dependencies.Register(raytracingSource, ctx.assetPath);
            var rt = ShaderUtil.CreateRayTracingShaderAsset(ctx, raytracingSource);
            ctx.AddObjectToAsset("RayTracingShader", rt);
#endif
            ctx.SetMainObject(com);
        }

        sealed class IncludeDependencies
        {
            static readonly Regex s_Includes = new Regex(
                @"^\s*#\s*include(?:_with_pragmas)?\s*[""<]([^"">\r\n]+)["">]", RegexOptions.Multiline);
            readonly AssetImportContext m_Context;
            readonly UPMPackageInfo[] m_Packages = UPMPackageInfo.GetAllRegisteredPackages();
            readonly HashSet<string> m_Visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readonly string m_ProjectRoot = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
            readonly string m_BuiltinIncludes = Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes");

            public IncludeDependencies(AssetImportContext context)
            {
                m_Context = context;
                m_Visited.Add(context.assetPath.Replace('\\', '/'));
            }

            public string ReadSource(string assetPath)
            {
                Resolve(assetPath, out _, out string diskPath);
                return File.ReadAllText(diskPath);
            }

            public void Register(string source, string parentPath)
            {
                foreach (Match match in s_Includes.Matches(source))
                {
                    string include = match.Groups[1].Value.Replace('\\', '/');
                    bool projectPath = include.StartsWith("Packages/", StringComparison.Ordinal) ||
                        include.StartsWith("Assets/", StringComparison.Ordinal);
                    string candidate = projectPath ? include : Path.Combine(Path.GetDirectoryName(parentPath), include);
                    Resolve(candidate, out string assetPath, out string diskPath);

                    // Unity supplies these headers outside the AssetDatabase.
                    // A same-directory source include still takes precedence.
                    if (!projectPath && !File.Exists(diskPath) && File.Exists(Path.Combine(m_BuiltinIncludes, include)))
                        continue;
                    if (!m_Visited.Add(assetPath))
                        continue;

                    // Keep the path dependency even if the file is missing: its
                    // later creation must invalidate this failed shader artifact.
                    m_Context.DependsOnSourceAsset(assetPath);
                    if (File.Exists(diskPath))
                        Register(File.ReadAllText(diskPath), assetPath);
                }
            }

            void Resolve(string path, out string assetPath, out string diskPath)
            {
                // Normalizing relative segments before matching package roots
                // lets nested includes use ../ without depending on process cwd.
                diskPath = Path.GetFullPath(Path.Combine(m_ProjectRoot, path)).Replace('\\', '/');
                assetPath = diskPath.StartsWith(m_ProjectRoot + "/", StringComparison.OrdinalIgnoreCase)
                    ? diskPath.Substring(m_ProjectRoot.Length + 1) : diskPath;

                foreach (var package in m_Packages)
                {
                    string packageAssetPath = package.assetPath.Replace('\\', '/');
                    string namedPath = "Packages/" + package.name;
                    string prefix = assetPath.StartsWith(packageAssetPath + "/", StringComparison.OrdinalIgnoreCase)
                        ? packageAssetPath : namedPath;
                    if (!assetPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relative = assetPath.Substring(prefix.Length + 1);
                    // Embedded package folders can differ from package.json's
                    // name; local file packages can live outside the project.
                    assetPath = packageAssetPath + "/" + relative;
                    diskPath = Path.Combine(package.resolvedPath, relative);
                    return;
                }
            }
        }

        const string computeShaderTemplate =
            "#define UNIFIED_RT_BACKEND_COMPUTE\n" +
            "#define UNIFIED_RT_KERNELS_DECLARED\n" +
            "SHADERCODE\n" +
            "#include \"Packages/com.fraxinus.rendering.light-transport/Runtime/UnifiedRayTracing/Compute/ComputeRaygenShader.hlsl\"\n" +
            "#pragma kernel MainRayGenShader\n" +
            "#pragma kernel ComputeIndirectDispatchDims\n";

        const string raytracingShaderTemplate =
            "#define UNIFIED_RT_BACKEND_HARDWARE\n" +
            "SHADERCODE\n" +
            "#include_with_pragmas \"Packages/com.fraxinus.rendering.light-transport/Runtime/UnifiedRayTracing/Hardware/HardwareRaygenShader.hlsl\"\n";
    }
}
