using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    // Compiler diagnostics can exist even when Unity reports BuildResult.Succeeded.
    // Observe retained compiler inputs and packed assets (including cached Shader
    // Graph assets), rather than scanning unrelated shaders in the whole project.
    internal sealed class UltraRPShaderBuildDiagnostics : IPreprocessShaders, IPreprocessComputeShaders
    {
        const string SessionKey = "FraxinusRP.ShaderBuildDiagnostics";
        const string ReportPath = "Library/FraxinusRP/ShaderBuildDiagnostics.json";
        static BuildSession s_session;
        public int callbackOrder => int.MaxValue;

        [Serializable] sealed class BuildSession
        {
            public string outputPath;
            public List<string> compiledPaths = new List<string>();
        }

        [Serializable] internal sealed class DiagnosticReport
        {
            public string outputPath;
            public string target;
            public int shaderCount;
            public int computeCount;
            public List<string> checkedPaths = new List<string>();
            public List<string> errors = new List<string>();
        }

        [InitializeOnLoadMethod]
        static void Initialize()
        {
            EditorApplication.update -= ClearAbortedBuild;
            EditorApplication.update += ClearAbortedBuild;
        }

        static void ClearAbortedBuild()
        {
            if (!BuildPipeline.isBuildingPlayer &&
                (s_session != null || !string.IsNullOrEmpty(SessionState.GetString(SessionKey, ""))))
                End();
        }

        internal static void Begin(BuildReport report)
        {
            End();
            s_session = new BuildSession { outputPath = report.summary.outputPath };
            Persist();
        }

        static BuildSession Session
        {
            get
            {
                if (s_session == null)
                {
                    string json = SessionState.GetString(SessionKey, "");
                    if (!string.IsNullOrEmpty(json)) s_session = JsonUtility.FromJson<BuildSession>(json);
                }
                return s_session;
            }
        }

        static void Persist() => SessionState.SetString(SessionKey, JsonUtility.ToJson(s_session));

        internal static void End()
        {
            s_session = null;
            SessionState.EraseString(SessionKey);
        }

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
            => Observe(shader, data.Count);

        public void OnProcessComputeShader(ComputeShader shader, string kernelName, IList<ShaderCompilerData> data)
            => Observe(shader, data.Count);

        static void Observe(UnityEngine.Object shader, int retainedVariants)
        {
            if (!BuildPipeline.isBuildingPlayer || retainedVariants == 0 || Session == null) return;
            string path = AssetDatabase.GetAssetPath(shader);
            if (!string.IsNullOrEmpty(path) && !Session.compiledPaths.Contains(path))
            {
                Session.compiledPaths.Add(path);
                Persist();
            }
        }

        internal static void Complete(BuildReport buildReport)
        {
            var session = Session;
            if (session == null) return; // Builds using other pipelines are unaffected.
            // Collection only needs this local snapshot; no build session must
            // remain active if loading assets, writing the report or validation fails.
            End();
            var paths = new HashSet<string>(session.compiledPaths, StringComparer.Ordinal);
            // The packed report also covers warm-cache builds where the native
            // compiler does not call IPreprocessShaders again.
            foreach (var packed in buildReport.packedAssets)
            foreach (var asset in packed.contents)
                if (asset.type == typeof(Shader) || asset.type == typeof(ComputeShader))
                    paths.Add(asset.sourceAssetPath);
            var report = Collect(paths, PlayerSettings.GetGraphicsAPIs(buildReport.summary.platform));
            report.outputPath = session.outputPath;
            report.target = buildReport.summary.platform.ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            ThrowIfErrors(report);
            Debug.Log($"FRAXINUS_BUILD_SHADER_DIAGNOSTICS_PASSED: {report.shaderCount} shaders, " +
                $"{report.computeCount} compute shaders; packed assets and retained compiler inputs. " + ReportPath);
        }

        internal static DiagnosticReport Collect(IEnumerable<string> paths, GraphicsDeviceType[] graphicsApis = null)
        {
            var report = new DiagnosticReport();
            foreach (string path in paths.Where(path => !string.IsNullOrEmpty(path)).Distinct().OrderBy(path => path, StringComparer.Ordinal))
            {
                // Unity built-in shaders have no project-importer diagnostic source.
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal)) continue;
                UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
                var graphCompilerErrors = new List<ShaderMessage>();
                bool checkedAny = false;
                foreach (UnityEngine.Object asset in assets)
                {
                    if (asset is Shader shader)
                    {
                        checkedAny = true;
                        ++report.shaderCount;
                        // Importer exceptions can leave this compilable fallback without
                        // a persisted import error. It is never a usable graph shader.
                        if (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase) &&
                            shader.name == "Hidden/GraphErrorShader2")
                            report.errors.Add(path + ": Shader Graph generation produced its error fallback shader.");
                        foreach (var message in ShaderUtil.GetShaderMessages(shader))
                            if (message.severity == ShaderCompilerMessageSeverity.Error)
                            {
                                graphCompilerErrors.Add(message);
                                if (IsRelevant(message.platform, graphicsApis))
                                    report.errors.Add($"{path}: {message.file}:{message.line}: {message.message} ({message.platform})");
                            }
                    }
                    else if (asset is ComputeShader compute)
                    {
                        checkedAny = true;
                        ++report.computeCount;
                        foreach (var message in ShaderUtil.GetComputeShaderMessages(compute))
                            if (message.severity == ShaderCompilerMessageSeverity.Error && IsRelevant(message.platform, graphicsApis))
                                report.errors.Add($"{path}: {message.file}:{message.line}: {message.message} {message.messageDetails} ({message.platform})");
                    }
                }
                if (checkedAny)
                {
                    report.checkedPaths.Add(path);
                    // Graph validation may generate a compilable error shader;
                    // the graph's importer log still contains the original error.
                    if (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                    {
                        var importLog = AssetImporter.GetImportLog(path);
                        if (importLog != null)
                            foreach (var entry in importLog.logEntries)
                                if ((entry.flags & ImportLogFlags.Error) != 0 &&
                                    !IsForwardedGraphCompilerMessage(path, entry.message, graphCompilerErrors))
                                    report.errors.Add($"{path}: {entry.file}:{entry.line}: {entry.message} (import)");
                    }
                }
                else if (path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".compute", StringComparison.OrdinalIgnoreCase))
                    report.errors.Add(path + ": included shader asset could not be loaded.");
            }
            report.errors = report.errors.Distinct().ToList();
            return report;
        }

        internal static bool IsForwardedGraphCompilerMessage(string path, string importMessage,
            IEnumerable<ShaderMessage> compilerMessages)
        {
            if (compilerMessages == null) return false;
            // ShaderGraph MessageManager.Log copies compiler errors into the importer log
            // without their platform. Exact copies were already checked above with the
            // configured graphics APIs; graph/import errors without a compiler match stay.
            foreach (var message in compilerMessages)
                if (message.severity == ShaderCompilerMessageSeverity.Error && string.Equals(importMessage,
                    $"{message.severity} in Graph at {path} on line {message.line}: {message.message}", StringComparison.Ordinal))
                    return true;
            return false;
        }

        // Parsing errors with no platform apply to every build. Native compiler
        // messages can also contain values absent from this Editor's managed enum.
        // Unknown compiler platforms or graphics APIs retain their diagnostics.
        internal static bool IsRelevant(ShaderCompilerPlatform platform, GraphicsDeviceType[] apis)
        {
            if (platform == ShaderCompilerPlatform.None || !Enum.IsDefined(typeof(ShaderCompilerPlatform), platform) ||
                apis == null || apis.Length == 0) return true;
            foreach (var api in apis)
            {
                ShaderCompilerPlatform expected;
                switch (api)
                {
                    case GraphicsDeviceType.Direct3D11:
                    case GraphicsDeviceType.Direct3D12: expected = ShaderCompilerPlatform.D3D; break;
                    case GraphicsDeviceType.OpenGLES3: expected = ShaderCompilerPlatform.GLES3x; break;
                    case GraphicsDeviceType.OpenGLCore: expected = ShaderCompilerPlatform.OpenGLCore; break;
                    case GraphicsDeviceType.Metal: expected = ShaderCompilerPlatform.Metal; break;
                    case GraphicsDeviceType.Vulkan: expected = ShaderCompilerPlatform.Vulkan; break;
                    default: return true;
                }
                if (platform == expected) return true;
            }
            return false;
        }

        internal static void ThrowIfErrors(DiagnosticReport report)
        {
            if (report.errors.Count == 0) return;
            throw new BuildFailedException("UltraRP: included Shader/ComputeShader compilation errors were reported:\n" +
                string.Join("\n", report.errors));
        }
    }
}
