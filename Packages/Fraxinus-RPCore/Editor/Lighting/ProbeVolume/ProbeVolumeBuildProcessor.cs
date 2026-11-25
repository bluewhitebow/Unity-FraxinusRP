using System.IO;
using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering
{
    class ProbeVolumeBuildProcessor : BuildPlayerProcessor, IPostprocessBuildWithReport
    {
        const string kTempStreamingAssetsPath = "TempProbeVolumeStreamingAssets";
        static readonly HashSet<ProbeVolumeStreamableAsset> s_ProcessedAssets = new();

        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            RestoreAssetReferences();

            var renderPipelineAssets = new List<RenderPipelineAsset>();
            buildPlayerContext.BuildPlayerOptions.target.TryGetRenderPipelineAssets(renderPipelineAssets);
            ProbeVolumeBakingSet.SyncBakingSets();

            var tempPath = Path.Combine(Path.GetFullPath("Library"), kTempStreamingAssetsPath);
            if (Directory.Exists(tempPath))
                Directory.Delete(tempPath, recursive: true);

            var processedSets = new HashSet<ProbeVolumeBakingSet>();
            var streamingPaths = new HashSet<string>();
            var settings = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ProbeVolumeGlobalSettings>();
            bool useStreamingAssets = settings == null || !settings.probeVolumeDisableStreamingAssets;

            foreach (var scene in buildPlayerContext.BuildPlayerOptions.scenes)
            {
                var bakingSet = ProbeVolumeBakingSet.GetBakingSetForScene(AssetDatabase.AssetPathToGUID(scene));
                if (bakingSet == null || !processedSets.Add(bakingSet))
                    continue;

                var bakingSetGUID = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(bakingSet));
                foreach (var data in bakingSet.backendBakedData)
                {
                    if (data == null)
                        continue;

                    var basePath = Path.Combine(tempPath, data.streamingAssetsPath, bakingSetGUID);
                    if (PrepareBakedData(data, renderPipelineAssets, basePath, useStreamingAssets))
                        streamingPaths.Add(data.streamingAssetsPath);
                }
            }

            foreach (var path in streamingPaths)
                buildPlayerContext.AddAdditionalPathToStreamingAssets(Path.Combine(tempPath, path), path);
        }

        internal static bool PrepareBakedData(ProbeVolumeBakedData data, IReadOnlyList<RenderPipelineAsset> renderPipelineAssets, string basePath, bool useStreamingAssets)
        {
            bool copiedAssets = false;
            bool prepared = false;
            try
            {
                foreach (var resource in data.GetBuildResources(renderPipelineAssets))
                {
                    var asset = resource.asset;
                    if (asset == null || !asset.IsValid())
                    {
                        if (resource.includeInBuild)
                            throw new BuildFailedException($"Missing Probe Volume data reference for backend '{data.backendId}'. Bake its lighting data before building.");
                        continue;
                    }

                    if (s_ProcessedAssets.Count == 0)
                        EditorApplication.update += RestoreReferencesAfterBuild;
                    s_ProcessedAssets.Add(asset);
                    if (!resource.includeInBuild)
                    {
                        asset.ClearAssetReferenceForBuild();
                        continue;
                    }

                    asset.RefreshAssetPath();
                    var assetPath = asset.GetAssetPath();
                    if (!File.Exists(assetPath))
                        throw new BuildFailedException($"Missing Probe Volume data asset {assetPath} for backend '{data.backendId}'. Bake its lighting data before building.");

                    if (!useStreamingAssets)
                    {
                        asset.EnsureAssetLoaded();
                        if (!asset.HasValidAssetReference())
                            throw new BuildFailedException($"Unable to load Probe Volume data asset {assetPath} for backend '{data.backendId}'. Reimport its lighting data before building.");
                        continue;
                    }

                    asset.ClearAssetReferenceForBuild();
                    Directory.CreateDirectory(basePath);
                    File.Copy(assetPath, Path.Combine(basePath, asset.assetGUID + ".bytes"), overwrite: true);
                    copiedAssets = true;
                }

                prepared = true;
                return copiedAssets;
            }
            finally
            {
                if (!prepared)
                    RestoreAssetReferences();
            }
        }

        public void OnPostprocessBuild(BuildReport report) => RestoreAssetReferences();

        static void RestoreReferencesAfterBuild()
        {
            if (!BuildPipeline.isBuildingPlayer)
                RestoreAssetReferences();
        }

        internal static void RestoreAssetReferences()
        {
            EditorApplication.update -= RestoreReferencesAfterBuild;
            foreach (var asset in s_ProcessedAssets)
                asset.EnsureAssetLoaded();

            s_ProcessedAssets.Clear();
        }
    }
}
