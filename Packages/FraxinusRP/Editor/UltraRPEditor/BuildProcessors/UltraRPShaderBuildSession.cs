using System;
using System.Collections.Generic;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    // As in URP, callback construction prepares data before native keyword
    // enumeration. SBP's optional PostScripts hook also covers initialization
    // that reuses callback instances with the same callback flags.
    internal sealed class UltraRPShaderBuildCallbacks : IPreprocessShaders, IPreprocessComputeShaders
    {
        public int callbackOrder => int.MinValue;
        public UltraRPShaderBuildCallbacks() => UltraRPShaderBuildSession.InitializeShaderCallbacks();
        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
            => UltraRPShaderBuildSession.EnsureForShaderCallbacks();
        public void OnProcessComputeShader(ComputeShader shader, string kernel, IList<ShaderCompilerData> data)
            => UltraRPShaderBuildSession.EnsureForShaderCallbacks();
    }

    internal static class UltraRPShaderBuildSession
    {
        internal enum Kind
        {
            Player,
            ImplicitAssetBundles,
            ScriptableAssetBundles,
            ExplicitAssetBundles
        }

        static UltraRPBuildData s_data;
        static Kind s_kind;
        static long s_generation;
        static Hash128 s_implicitConfiguration;
        internal static bool isActive => s_data != null;
        internal static bool isBuildingForUltraRP => s_data != null && s_data.buildingPlayerForUltraRenderPipeline;
        internal static UltraRPBuildData data => s_data;

        [InitializeOnLoadMethod]
        static void Initialize()
        {
            EditorApplication.update -= RestoreAfterBuild;
            EditorApplication.update += RestoreAfterBuild;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorUserBuildSettings.activeBuildTargetChanged -= OnBuildTargetChanged;
            EditorUserBuildSettings.activeBuildTargetChanged += OnBuildTargetChanged;
        }

        static void OnBuildTargetChanged()
        {
            // SBP can reuse the same callback flags after switching target, in
            // which case Unity skips callback construction. Refresh before its
            // native shader enumeration rather than waiting for a shader callback.
            if (s_data == null || s_data.buildTarget == EditorUserBuildSettings.activeBuildTarget)
                return;
            if (s_kind == Kind.ExplicitAssetBundles)
            {
                End("target changed during explicit bundle scope");
                throw new BuildFailedException("Switch the active build target before entering UltraRPAssetBundleBuildScope.");
            }
            if (!BuildPipeline.isBuildingPlayer)
                InitializeShaderCallbacks();
        }

        static void BeforeReload()
        {
            // Player domain reloads continue through the persisted recovery journal.
            // Bundle target switches recreate callbacks and recompute capabilities.
            if (!BuildPipeline.isBuildingPlayer && s_data != null)
                End("interrupted by domain reload");
        }

        static void RestoreAfterBuild()
        {
            if (s_data == null || BuildPipeline.isBuildingPlayer || s_kind == Kind.ExplicitAssetBundles)
                return;
            End(s_kind == Kind.Player ? "player ended without postprocess callback" :
                "bundle callbacks completed; build result unavailable");
        }

        internal static void InitializeShaderCallbacks()
        {
            if (BuildPipeline.isBuildingPlayer)
            {
                // After a Player domain reload the report callback may not be
                // replayed. Recreate capabilities/prefilters before enumeration.
                if (s_data == null)
                    BeginFromShaderCallbacks(Kind.Player);
                return;
            }
            if (s_data != null && (s_kind == Kind.ExplicitAssetBundles || s_kind == Kind.ScriptableAssetBundles))
                return;
            BeginFromShaderCallbacks(Kind.ImplicitAssetBundles);
        }

        internal static void EnsureForShaderCallbacks()
        {
            // Recheck target after an SBP platform switch. Cached stripper objects
            // see the new UltraRPBuildData identity on their next shader scope.
            if (s_data != null && s_data.buildTarget == EditorUserBuildSettings.activeBuildTarget)
            {
                // Unity 2022 reuses native Bundle callback objects even across
                // separate synchronous BuildAssetBundles calls. Never retain a
                // prior settings snapshot solely because the target is unchanged.
                if (s_kind != Kind.ImplicitAssetBundles ||
                    s_implicitConfiguration == UltraRPShaderBuildConfiguration.GetProjectHash())
                    return;
                BeginFromShaderCallbacks(Kind.ImplicitAssetBundles);
                return;
            }
            if (s_data != null && s_kind == Kind.ExplicitAssetBundles)
                throw new BuildFailedException("Switch the active build target before entering UltraRPAssetBundleBuildScope.");
            BeginFromShaderCallbacks(BuildPipeline.isBuildingPlayer ? Kind.Player : Kind.ImplicitAssetBundles);
        }

        static void BeginFromShaderCallbacks(Kind kind)
        {
            Begin(EditorUserBuildSettings.activeBuildTarget, EditorUserBuildSettings.development, kind,
                string.Empty, null, preparePlayerPrefiltering: kind == Kind.Player);
        }

        internal static void BeginPlayer(BuildReport report)
        {
            Begin(report.summary.platform, (report.summary.options & BuildOptions.Development) != 0,
                Kind.Player, report.summary.outputPath, null);
            UltraRPShaderBuildReport.TrackPlayerBuild(report);
        }

        internal static void BeginScriptableBuild(BuildTarget target, string outputPath)
        {
            if (s_data != null && s_kind == Kind.ExplicitAssetBundles)
                return;
            if (target != EditorUserBuildSettings.activeBuildTarget)
                throw new BuildFailedException("SBP must switch to its target before preparing Fraxinus shader settings.");
            Begin(target, EditorUserBuildSettings.development, Kind.ScriptableAssetBundles, outputPath, null);
        }

        internal static void EndScriptableBuild(string status)
        {
            if (s_data != null && s_kind == Kind.ScriptableAssetBundles)
                End(status);
        }

        internal static long BeginAssetBundles(BuildTarget target, string outputPath,
            IEnumerable<RenderPipelineAsset> additionalAssets)
        {
            if (BuildPipeline.isBuildingPlayer || (s_data != null && s_kind == Kind.ExplicitAssetBundles))
                throw new InvalidOperationException("UltraRP AssetBundle build scopes cannot be nested or entered during a Player build.");
            if (target != EditorUserBuildSettings.activeBuildTarget)
                throw new InvalidOperationException("Switch the active build target before entering UltraRPAssetBundleBuildScope.");
            var configured = new List<RenderPipelineAsset>();
            if (!UnityEditor.Rendering.BuildTargetExtensions.TryGetRenderPipelineAssets(target, configured) ||
                configured.Count == 0 || configured.Exists(asset => asset == null || asset.GetType() != typeof(UltraRenderPipelineAsset)))
                throw new InvalidOperationException("Configure Fraxinus in the target's Graphics/Quality Settings before building its bundles. Additional pipeline assets cannot replace the consuming player's pipeline tag.");
            Begin(target, EditorUserBuildSettings.development, Kind.ExplicitAssetBundles, outputPath, additionalAssets);
            return s_generation;
        }

        static void Begin(BuildTarget target, bool development, Kind kind, string outputPath,
            IEnumerable<RenderPipelineAsset> additionalAssets, bool preparePlayerPrefiltering = false)
        {
            End("superseded by a new build");
            UltraRPBuildData.DisposeCurrent();
            // Core's static data can otherwise outlive standalone bundle builds.
            CoreBuildData.instance.Dispose();
            ++s_generation;
            s_kind = kind;
            s_data = new UltraRPBuildData(target, development, additionalAssets);
            if (!s_data.buildingPlayerForUltraRenderPipeline)
                return;
            try
            {
                UltraRPShaderBuildReport.Begin(target.ToString(), outputPath, kind.ToString(), development);
                if (kind != Kind.Player || preparePlayerPrefiltering)
                {
                    UltraRPShaderPrefiltering.PrepareForBuild(development, target, s_data.allRenderPipelineAssets,
                        enableNativeFiltering: kind != Kind.ImplicitAssetBundles);
                    if (kind != Kind.Player)
                        UltraRPPreprocessBuild.LogIncludedAssets(s_data.renderPipelineAssets);
                }
                if (kind == Kind.ImplicitAssetBundles)
                    s_implicitConfiguration = UltraRPShaderBuildConfiguration.GetProjectHash();
            }
            catch
            {
                End("build preparation failed");
                throw;
            }
        }

        internal static void End(string status)
        {
            // Reset first so failed recovery cannot leave old capabilities active.
            var previous = s_data;
            s_data = null;
            previous?.Dispose();
            try
            {
                UltraRPShaderBuildReport.End(status);
            }
            finally
            {
                UltraRPShaderPrefiltering.RestorePending();
            }
        }

        internal static void EndAssetBundles(long generation, string status)
        {
            if (s_data != null && s_kind == Kind.ExplicitAssetBundles && generation == s_generation)
                End(status);
        }
    }

    /// <summary>
    /// Deterministic recovery for custom AssetBundle/SBP/Addressables build scripts.
    /// Switch the active target first, then wrap the synchronous build in using.
    /// Include every pipeline asset used by the consuming players or bundle content.
    /// Native/SBP builds also initialize automatically; this scope adds finally cleanup
    /// and explicit pipeline requirements that project Quality Settings cannot describe.
    /// </summary>
    public sealed class UltraRPAssetBundleBuildScope : IDisposable
    {
        readonly long _generation;
        string _status = "bundle scope disposed; result not reported";
        bool _disposed;

        public UltraRPAssetBundleBuildScope(BuildTarget target, string outputPath = "",
            IEnumerable<RenderPipelineAsset> additionalPipelineAssets = null)
            => _generation = UltraRPShaderBuildSession.BeginAssetBundles(target, outputPath, additionalPipelineAssets);

        public void SetResult(bool succeeded) => _status = succeeded ? "bundle succeeded" : "bundle failed or cancelled";

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            UltraRPShaderBuildSession.EndAssetBundles(_generation, _status);
        }
    }
}
