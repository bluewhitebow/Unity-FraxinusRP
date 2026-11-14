using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

namespace FraxinusEditor.Rendering.Ultra
{
    // The input is what Unity gives managed callbacks AFTER native prefiltering
    // and built-in stripping. Never label these counts as the full variant space.
    internal static class UltraRPShaderBuildReport
    {
        internal const string ReportPath = "Library/FraxinusRP/ShaderBuildReport.json";
        internal const string PreviousReportPath = "Library/FraxinusRP/ShaderBuildReport.previous.json";
        internal const string ComparisonPath = "Library/FraxinusRP/ShaderBuildComparison.json";
        internal const string BaselineDirectory = "Library/FraxinusRP/ShaderBuildBaselines";

        internal enum Decision
        {
            Retained,
            ForbiddenGlobal,
            InvalidCombination,
            UnusedPass,
            PostEffect,
            OutputSettings,
            DeferredPath,
            RequiredGlobal,
            LightingCombination,
            Count
        }

        [Serializable]
        internal sealed class ReasonCount
        {
            public string reason;
            public long variants;
        }

        [Serializable]
        internal sealed class Batch
        {
            public string shaderPath;
            public string shaderName;
            public string kind;
            public string passName;
            public string passType;
            public string shaderStage;
            public int subshaderIndex;
            public int passIndex;
            public long beforeScriptable;
            public long afterCore = -1;
            public long finalRetained = -1;
            public long removed = -1;
            public double coreStripMilliseconds;
            public List<ReasonCount> decisions = new List<ReasonCount>();
            [NonSerialized]
            internal readonly long[] counts = new long[(int)Decision.Count];

            internal void MaterializeDecisions()
            {
                decisions.Clear();
                for (int i = 0; i < counts.Length; ++i)
                    if (counts[i] != 0)
                        decisions.Add(new ReasonCount { reason = ((Decision)i).ToString(), variants = counts[i] });
            }
        }

        [Serializable]
        internal sealed class Report
        {
            public int schemaVersion = 2;
            public string target;
            public string outputPath;
            public string buildKind;
            public string startedUtc;
            public string finishedUtc;
            public string status;
            public string graphicsAPIs;
            public bool developmentBuild;
            public string playerBuildGuid;
            public string playerBuildStartedUtc;
            public int allowedLightingStates = 0xff;
            public bool requireMainLightShadows;
            public string countStage = "Managed shader callbacks, after native prefiltering and built-in stripping. Counts include compiler platforms and stages as supplied by Unity.";
            public string decisionMeaning = "Fraxinus decisions are candidate votes, not necessarily actual removals: Core combines independent strippers with All(). Retained means this stripper retained the candidate. finalRetained is the observed final managed callback count. -1 means no matching observer ran.";
            public long beforeScriptable;
            public long finalRetained;
            public long removed;
            public int completedBatches;
            public List<string> forbiddenGlobalKeywords = new List<string>();
            public List<string> requiredGlobalKeywords = new List<string>();
            public List<string> prefilterReasons = new List<string>();
            public List<Batch> batches = new List<Batch>();
        }

        // Kept separately so aggregation is testable without a player build or
        // changing the reporting state of an active editor build.
        internal sealed class Collector
        {
            internal readonly Report report;
            internal Batch current;

            internal Collector(string target, string outputPath, string buildKind)
            {
                report = new Report
                {
                    target = target,
                    outputPath = outputPath,
                    buildKind = buildKind,
                    startedUtc = DateTime.UtcNow.ToString("o"),
                    status = "collecting"
                };
            }

            internal void BeginBatch(Batch batch)
            {
                current?.MaterializeDecisions();
                current = batch;
                report.batches.Add(batch);
                report.beforeScriptable += batch.beforeScriptable;
            }

            internal void RecordDecision(Decision decision)
            {
                if (current != null && (uint)decision < (uint)Decision.Count)
                    ++current.counts[(int)decision];
            }

            internal void ObserveCore(long retained, double milliseconds)
            {
                if (current == null)
                    return;
                current.afterCore = retained;
                current.coreStripMilliseconds = milliseconds;
            }

            internal void CompleteBatch(long retained)
            {
                if (current == null || current.finalRetained >= 0)
                    return;
                current.finalRetained = retained;
                current.removed = current.beforeScriptable - retained;
                current.MaterializeDecisions();
                report.finalRetained += retained;
                report.removed += current.removed;
                ++report.completedBatches;
                current = null;
            }

            internal void RecordFeatures(UltraRPShaderBuildFeatures.Snapshot snapshot)
            {
                if (snapshot == null)
                    return;
                report.forbiddenGlobalKeywords.Clear();
                report.forbiddenGlobalKeywords.AddRange(snapshot.ForbiddenGlobalKeywords);
                report.requiredGlobalKeywords.Clear();
                report.requiredGlobalKeywords.AddRange(snapshot.RequiredGlobalKeywords);
                report.prefilterReasons.Clear();
                report.prefilterReasons.AddRange(snapshot.Reasons);
                report.allowedLightingStates = snapshot.AllowedLightingStates;
                report.requireMainLightShadows = snapshot.RequireMainLightShadows;
            }

            internal void Finish(string status)
            {
                current?.MaterializeDecisions();
                report.status = status;
                report.finishedUtc = DateTime.UtcNow.ToString("o");
            }
        }

        static Collector s_collector;
        static UnityEngine.Object s_shader;
        static double s_nextCheckpoint;
        static bool s_writeWarning;
        static UltraRPShaderBuildComparison.Baseline s_baseline;
        static readonly HashSet<string> s_growthWarnings = new HashSet<string>(StringComparer.Ordinal);
        static Report s_pendingPlayerResult;

        internal static void Begin(string target, string outputPath, string buildKind, bool development)
        {
            End("superseded by another build");
            s_collector = new Collector(target, outputPath, buildKind);
            s_writeWarning = false;
            s_growthWarnings.Clear();
            s_collector.report.developmentBuild = development;
            if (Enum.TryParse(target, out BuildTarget buildTarget))
                s_collector.report.graphicsAPIs = string.Join(",", PlayerSettings.GetGraphicsAPIs(buildTarget)
                    .Select(api => api.ToString()).OrderBy(api => api, StringComparer.Ordinal));
            // Snapshot before the first checkpoint replaces the last report.
            try
            {
                if (File.Exists(ReportPath)) File.Copy(ReportPath, PreviousReportPath, true);
                var previous = ReadReport(PreviousReportPath);
                if (UltraRPShaderBuildComparison.CanAccept(previous, false)) TryPromoteBaseline(previous, "Successful completed callbacks");
                s_baseline = ReadBaseline(s_collector.report);
            }
            catch (Exception error) when (IsReportIOError(error))
            {
                s_baseline = null;
                WarnIO(error);
            }
            s_nextCheckpoint = EditorApplication.timeSinceStartup + 5;
            // The package also contains an unrelated watcher with the same name.
            UnityEditor.Rendering.ShaderStrippingWatcher.shaderProcessed += ObserveCoreShader;
            UnityEditor.Rendering.ShaderStrippingWatcher.computeShaderProcessed += ObserveCoreCompute;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            Write();
        }

        internal static void RecordFeatures(UltraRPShaderBuildFeatures.Snapshot snapshot)
            => s_collector?.RecordFeatures(snapshot);

        internal static void Flush() => Write();

        internal static void TrackPlayerBuild(UnityEditor.Build.Reporting.BuildReport buildReport)
        {
            if (s_collector == null || buildReport == null) return;
            var summary = buildReport.summary;
            s_collector.report.playerBuildGuid = summary.guid.ToString();
            s_collector.report.playerBuildStartedUtc = summary.buildStartedAt.ToUniversalTime().ToString("o");
            s_collector.report.developmentBuild = (summary.options & BuildOptions.Development) != 0;
        }

        // One fixed-array increment per candidate: no strings, LINQ or objects
        // are allocated in the per-variant path.
        internal static void RecordDecision(Decision decision) => s_collector?.RecordDecision(decision);

        internal static void End(string status)
        {
            var collector = s_collector;
            if (collector == null)
                return;

            s_collector = null;
            s_shader = null;
            UnityEditor.Rendering.ShaderStrippingWatcher.shaderProcessed -= ObserveCoreShader;
            UnityEditor.Rendering.ShaderStrippingWatcher.computeShaderProcessed -= ObserveCoreCompute;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            collector.Finish(status);
            Write(collector.report);
            if (UltraRPShaderBuildComparison.CanAccept(collector.report, false))
                TryPromoteBaseline(collector.report, "Successful completed callbacks");
            else if (collector.report.buildKind == "Player" && status == "Unknown" &&
                UltraRPShaderBuildComparison.IsComplete(collector.report) &&
                !string.IsNullOrEmpty(collector.report.playerBuildGuid))
            {
                s_pendingPlayerResult = collector.report;
                EditorApplication.update -= VerifyPlayerResult;
                EditorApplication.update += VerifyPlayerResult;
            }
        }

        static void BeforeAssemblyReload() => End("interrupted by assembly reload; build result unavailable");

        internal static void BeforeShader(Shader shader, ShaderSnippetData snippet, int count)
        {
            if (s_collector == null)
                return;
            s_shader = shader;
            s_collector.BeginBatch(new Batch
            {
                shaderPath = AssetDatabase.GetAssetPath(shader),
                shaderName = shader.name,
                kind = "Shader",
                passName = snippet.passName,
                passType = snippet.passType.ToString(),
                shaderStage = snippet.shaderType.ToString(),
                subshaderIndex = (int)snippet.pass.SubshaderIndex,
                passIndex = (int)snippet.pass.PassIndex,
                beforeScriptable = count
            });
        }

        internal static void BeforeCompute(ComputeShader shader, string kernel, int count)
        {
            if (s_collector == null)
                return;
            s_shader = shader;
            s_collector.BeginBatch(new Batch
            {
                shaderPath = AssetDatabase.GetAssetPath(shader),
                shaderName = shader.name,
                kind = "ComputeShader",
                passName = kernel,
                shaderStage = "Compute",
                subshaderIndex = -1,
                passIndex = -1,
                beforeScriptable = count
            });
        }

        static bool MatchesShader(Shader shader, ShaderSnippetData snippet)
        {
            var batch = s_collector?.current;
            return s_shader == shader && batch != null && batch.kind == "Shader" &&
                batch.subshaderIndex == snippet.pass.SubshaderIndex && batch.passIndex == snippet.pass.PassIndex &&
                batch.passName == snippet.passName && batch.shaderStage == snippet.shaderType.ToString();
        }

        static bool MatchesCompute(ComputeShader shader, string kernel)
            => s_shader == shader && s_collector?.current?.kind == "ComputeShader" &&
               s_collector.current.passName == kernel;

        static void ObserveCoreShader(Shader shader, ShaderSnippetData snippet, uint retained, double milliseconds)
        {
            if (MatchesShader(shader, snippet))
                s_collector.ObserveCore(retained, milliseconds);
        }

        static void ObserveCoreCompute(ComputeShader shader, string kernel, uint retained, double milliseconds)
        {
            if (MatchesCompute(shader, kernel))
                s_collector.ObserveCore(retained, milliseconds);
        }

        internal static void AfterShader(Shader shader, ShaderSnippetData snippet, int count)
        {
            if (MatchesShader(shader, snippet))
                CompleteBatch(count);
        }

        internal static void AfterCompute(ComputeShader shader, string kernel, int count)
        {
            if (MatchesCompute(shader, kernel))
                CompleteBatch(count);
        }

        static void CompleteBatch(int count)
        {
            long inputCount = s_collector.current.beforeScriptable;
            s_collector.CompleteBatch(count);
            s_shader = null;
            // Persist a large batch before Unity starts its potentially long
            // native compilation, even if its stripping finished very quickly.
            if (s_collector.report.completedBatches != 1 && inputCount < 1024 &&
                EditorApplication.timeSinceStartup < s_nextCheckpoint)
                return;
            Write();
            s_nextCheckpoint = EditorApplication.timeSinceStartup + 5;
        }

        static void Write()
        {
            if (s_collector != null)
                Write(s_collector.report);
        }

        static void Write(Report report)
        {
            try
            {
                WriteJson(ReportPath, report);
                var comparison = UltraRPShaderBuildComparison.Compare(report, s_baseline);
                WriteJson(ComparisonPath, comparison);
                foreach (var pass in comparison.passes)
                    if (pass.growthWarning && s_growthWarnings.Add(UltraRPShaderBuildComparison.PassKey(pass)))
                        Debug.LogWarning("UltraRP shader variants grew: " + pass.shaderName + " / " + pass.passName +
                            " final managed callbacks " + pass.previousRetained + " -> " + pass.currentRetained +
                            " (+" + pass.increase + "). This is observed callback coverage, not total package variants. " +
                            (comparison.graphicsAPIsVerified ? "" : "Legacy baseline has no verified graphics API metadata. ") +
                            "Comparison: " + ComparisonPath);
            }
            catch (Exception error) when (IsReportIOError(error))
            {
                WarnIO(error);
            }
        }

        static string BaselinePath(Report report, bool legacy = false)
            => BaselineDirectory + "/" + SafeFilePart(report.target) + "-" + SafeFilePart(report.buildKind) + "-" +
                SafeFilePart(legacy || string.IsNullOrEmpty(report.graphicsAPIs) ? "legacy" : report.graphicsAPIs) +
                (report.schemaVersion >= 2 && !legacy ? (report.developmentBuild ? "-development" : "-release") : "") + ".json";

        static string SafeFilePart(string value)
            => string.Concat((value ?? "unknown").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));

        static Report ReadReport(string path)
            => File.Exists(path) ? JsonUtility.FromJson<Report>(File.ReadAllText(path)) : null;

        static UltraRPShaderBuildComparison.Baseline ReadBaseline(Report report)
        {
            string path = BaselinePath(report);
            if (!File.Exists(path)) path = BaselinePath(report, true);
            if (!File.Exists(path)) return null;
            var baseline = JsonUtility.FromJson<UltraRPShaderBuildComparison.Baseline>(File.ReadAllText(path));
            return baseline != null && UltraRPShaderBuildComparison.CanAccept(baseline.report,
                baseline.acceptance == "Explicitly accepted complete report; build result may be unknown") ? baseline : null;
        }

        static void SaveBaseline(Report report, string acceptance)
            => WriteJson(BaselinePath(report), new UltraRPShaderBuildComparison.Baseline
            {
                acceptedUtc = DateTime.UtcNow.ToString("o"), acceptance = acceptance, report = report
            });

        static void TryPromoteBaseline(Report report, string acceptance)
        {
            try
            {
                if (UltraRPShaderBuildComparison.ShouldAutoAccept(report, ReadBaseline(report)))
                    SaveBaseline(report, acceptance);
            }
            catch (Exception error) when (IsReportIOError(error)) { WarnIO(error); }
        }

        [MenuItem("Tools/Fraxinus/Shader Build/Use Last Complete Report as Baseline")]
        internal static void AcceptLastReportAsBaseline()
        {
            if (BuildPipeline.isBuildingPlayer || s_collector != null)
            {
                Debug.LogWarning("Wait for the shader build to finish before selecting a baseline.");
                return;
            }
            try
            {
                var report = ReadReport(ReportPath);
                if (!UltraRPShaderBuildComparison.CanAccept(report, true))
                {
                    Debug.LogWarning("Baseline requires a finished report with every callback completed and status Succeeded, bundle succeeded, or Unknown. Failed/interrupted reports cannot be accepted.");
                    return;
                }
                const string acceptance = "Explicitly accepted complete report; build result may be unknown";
                SaveBaseline(report, acceptance);
                Debug.Log("Shader callback baseline saved: " + BaselinePath(report) +
                    ". Observed callbacks only; cached shaders may be absent. Recorded build status: " + report.status);
            }
            catch (Exception error) when (IsReportIOError(error)) { WarnIO(error); }
        }

        static void VerifyPlayerResult()
        {
            if (BuildPipeline.isBuildingPlayer) return;
            EditorApplication.update -= VerifyPlayerResult;
            var pending = s_pendingPlayerResult;
            s_pendingPlayerResult = null;
            if (pending == null) return;
            var latest = UnityEditor.Build.Reporting.BuildReport.GetLatestReport();
            if (latest == null) return;
            var summary = latest.summary;
            if (summary.guid.ToString() != pending.playerBuildGuid ||
                summary.buildStartedAt.ToUniversalTime().ToString("o") != pending.playerBuildStartedUtc ||
                summary.platform.ToString() != pending.target || summary.outputPath != pending.outputPath ||
                summary.result == UnityEditor.Build.Reporting.BuildResult.Unknown) return;
            // A later postprocess callback can fail after ours has completed.
            // Preserve that final result so a failed build cannot be accepted
            // through the explicit legacy-Unknown baseline path.
            pending.status = summary.result.ToString();
            if (summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
                TryPromoteBaseline(pending, "Player result verified after postprocess callback");
            try
            {
                // Another build may already have replaced the current report.
                var current = ReadReport(ReportPath);
                if (s_collector == null && current?.startedUtc == pending.startedUtc)
                {
                    WriteJson(ReportPath, pending);
                    WriteJson(ComparisonPath, UltraRPShaderBuildComparison.Compare(pending, s_baseline));
                }
            }
            catch (Exception error) when (IsReportIOError(error)) { WarnIO(error); }
        }

        static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(value, true));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        static bool IsReportIOError(Exception error)
            => error is IOException || error is UnauthorizedAccessException || error is ArgumentException;

        static void WarnIO(Exception error)
        {
            if (!s_writeWarning) Debug.LogWarning("UltraRP shader build diagnostics could not be saved/read: " + error.Message);
            s_writeWarning = true;
        }
    }

    internal sealed class UltraRPShaderBuildReportInput : IPreprocessShaders, IPreprocessComputeShaders
    {
        // The common lifecycle starts at int.MinValue, before this observer.
        public int callbackOrder => int.MinValue + 100;
        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
            => UltraRPShaderBuildReport.BeforeShader(shader, snippet, data.Count);
        public void OnProcessComputeShader(ComputeShader shader, string kernelName, IList<ShaderCompilerData> data)
            => UltraRPShaderBuildReport.BeforeCompute(shader, kernelName, data.Count);
    }

    internal sealed class UltraRPShaderBuildReportOutput : IPreprocessShaders, IPreprocessComputeShaders
    {
        public int callbackOrder => int.MaxValue;
        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
            => UltraRPShaderBuildReport.AfterShader(shader, snippet, data.Count);
        public void OnProcessComputeShader(ComputeShader shader, string kernelName, IList<ShaderCompilerData> data)
            => UltraRPShaderBuildReport.AfterCompute(shader, kernelName, data.Count);
    }
}
