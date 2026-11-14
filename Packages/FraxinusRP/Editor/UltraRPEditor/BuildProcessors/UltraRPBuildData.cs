using System;
using System.Collections.Generic;
using System.Linq;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    internal class UltraRPBuildData : IDisposable
    {
        // Shader callback sessions own the lifetime for Player and AssetBundle builds.
        static UltraRPBuildData s_instance;

        public static UltraRPBuildData instance => s_instance ??= new(EditorUserBuildSettings.activeBuildTarget, Debug.isDebugBuild);

        public bool buildingPlayerForUltraRenderPipeline { get; private set; }

        public BuildTarget buildTarget { get; }
        public bool isDevelopmentBuild { get; }
        public IReadOnlyList<RenderPipelineAsset> allRenderPipelineAssets { get; }

        public List<UltraRenderPipelineAsset> renderPipelineAssets { get; } = new List<UltraRenderPipelineAsset>();

        public UltraRPBuildData(BuildTarget buildTarget, bool isDevelopmentBuild,
            IEnumerable<RenderPipelineAsset> additionalAssets = null)
        {
            s_instance = this;
            this.buildTarget = buildTarget;
            this.isDevelopmentBuild = isDevelopmentBuild;
            var assets = new List<RenderPipelineAsset>();
            UnityEditor.Rendering.BuildTargetExtensions.TryGetRenderPipelineAssets(buildTarget, assets);
            if (additionalAssets != null)
                assets.AddRange(additionalAssets);
            allRenderPipelineAssets = assets.Distinct().ToArray();
            // Never prove capabilities from only the first asset in a mixed or
            // partially unresolved configuration.
            buildingPlayerForUltraRenderPipeline = allRenderPipelineAssets.Count > 0 &&
                allRenderPipelineAssets.All(asset => asset != null && asset.GetType() == typeof(UltraRenderPipelineAsset));
            if (buildingPlayerForUltraRenderPipeline)
                renderPipelineAssets.AddRange(allRenderPipelineAssets.Cast<UltraRenderPipelineAsset>());
        }

        public void Dispose()
        {
            renderPipelineAssets?.Clear();

            buildingPlayerForUltraRenderPipeline = false;

            // A late Dispose from an earlier build must not erase its successor.
            if (ReferenceEquals(s_instance, this))
                s_instance = null;
        }

        internal static void DisposeCurrent() => s_instance?.Dispose();
    }
}
