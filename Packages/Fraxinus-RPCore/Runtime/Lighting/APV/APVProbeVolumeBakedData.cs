using System;
using Unity.Mathematics;
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
#endif

namespace UnityEngine.Rendering
{
    [Serializable]
    internal sealed class APVProbeVolumeBakedData : ProbeVolumeBakedData
    {
        public override string backendId => APVProbeReferenceVolumeBackend.backendId;

        [SerializeField] internal ProbeVolumeStreamableAsset cellSharedDataAsset;
        [SerializeField] internal SerializedDictionary<string, ProbeVolumeBakingSet.PerScenarioDataInfo> scenarios = new SerializedDictionary<string, ProbeVolumeBakingSet.PerScenarioDataInfo>();
        [SerializeField] internal ProbeVolumeStreamableAsset cellBricksDataAsset;
        [SerializeField] internal ProbeVolumeStreamableAsset cellSupportDataAsset;

        [SerializeField] internal int chunkSizeInBricks = ProbeBrickPoolBase.GetChunkSizeInBrickCount();
        [SerializeField] internal bool bakedProbeOcclusion;
        [SerializeField] internal int bakedSkyOcclusionValue = -1;
        [SerializeField] internal int bakedSkyShadingDirectionValue = -1;
        [SerializeField] internal int bakedMaskCount = 1;
        [SerializeField] internal uint4 bakedLayerMasks;
        [SerializeField] internal int maxSHChunkCount = -1;
        [SerializeField] internal int L0ChunkSize;
        [SerializeField] internal int L1ChunkSize;
        [SerializeField] internal int L2TextureChunkSize;
        [SerializeField] internal int ProbeOcclusionChunkSize;
        [SerializeField] internal int sharedValidityMaskChunkSize;
        [SerializeField] internal int sharedSkyOcclusionL0L1ChunkSize;
        [SerializeField] internal int sharedSkyShadingDirectionIndicesChunkSize;
        [SerializeField] internal int sharedDataChunkSize;
        [SerializeField] internal int supportPositionChunkSize;
        [SerializeField] internal int supportValidityChunkSize;
        [SerializeField] internal int supportTouchupChunkSize;
        [SerializeField] internal int supportLayerMaskChunkSize;
        [SerializeField] internal int supportOffsetsChunkSize;
        [SerializeField] internal int supportDataChunkSize;

        internal bool bakedSkyOcclusion
        {
            get => bakedSkyOcclusionValue > 0;
            set => bakedSkyOcclusionValue = value ? 1 : 0;
        }

        internal bool bakedSkyShadingDirection
        {
            get => bakedSkyShadingDirectionValue > 0;
            set => bakedSkyShadingDirectionValue = value ? 1 : 0;
        }

        internal override void OnAfterDeserialize(ProbeVolumeBakingSet bakingSet)
        {
            base.OnAfterDeserialize(bakingSet);
            if (bakedSkyOcclusionValue == -1)
                bakedSkyOcclusion = false;
            if (bakedSkyShadingDirectionValue == -1)
                bakedSkyShadingDirection = false;
        }

        internal override void Initialize(TProbeReferenceVolumeBase referenceVolume)
        {
            if (scenarios == null)
                return;

            foreach (var scenario in scenarios)
                scenario.Value?.Initialize(referenceVolume.shBands);
        }

        internal override bool HasBakedData(string scenario, bool supportLightingScenarios)
        {
            if (scenarios == null)
                return false;

            if (scenario == null)
                return scenarios.ContainsKey(TProbeReferenceVolumeBase.defaultLightingScenario);

            if (!supportLightingScenarios && scenario != TProbeReferenceVolumeBase.defaultLightingScenario)
                return false;

            return scenarios.ContainsKey(scenario);
        }

        internal override bool HasValidScenarioData(string scenario, TProbeReferenceVolumeBase volume)
        {
            return scenario != null && scenarios != null && scenarios.TryGetValue(scenario, out var data) && data != null && data.HasValidData(volume.shBands) &&
                (!bakedProbeOcclusion || (data.cellProbeOcclusionDataAsset != null && data.cellProbeOcclusionDataAsset.FileExists()));
        }

        internal override bool HasValidSharedData()
        {
            return cellSharedDataAsset != null &&
                cellBricksDataAsset != null &&
                cellSharedDataAsset.FileExists() &&
                cellBricksDataAsset.FileExists();
        }

        internal override bool HasSupportData()
        {
            return cellSupportDataAsset != null && cellSupportDataAsset.IsValid() && cellSupportDataAsset.FileExists();
        }

        internal override bool CheckCompatibleCellLayout(ProbeVolumeBakingSet bakingSet)
        {
            return base.CheckCompatibleCellLayout(bakingSet) &&
                bakingSet.skyOcclusion == bakedSkyOcclusion &&
                bakingSet.skyOcclusionShadingDirection == bakedSkyShadingDirection &&
                bakingSet.settings.virtualOffsetSettings.useVirtualOffset == (supportOffsetsChunkSize != 0) &&
                bakingSet.useRenderingLayers == (bakedMaskCount != 1);
        }

        internal override void CleanupStreamableAssets()
        {
            cellSharedDataAsset?.Dispose();
            cellBricksDataAsset?.Dispose();
            cellSupportDataAsset?.Dispose();
            if (scenarios == null)
                return;

            foreach (var scenario in scenarios)
            {
                if (scenario.Value == null)
                    continue;

                scenario.Value.cellDataAsset?.Dispose();
                scenario.Value.cellOptionalDataAsset?.Dispose();
                scenario.Value.cellProbeOcclusionDataAsset?.Dispose();
            }
        }

#if UNITY_EDITOR
        internal const string kStreamingAssetsPath = "APVStreamingAssets";
        internal override string streamingAssetsPath => kStreamingAssetsPath;
        internal override int scenarioCount => scenarios?.Count ?? 0;

        internal override IEnumerable<ProbeVolumeBuildResource> GetBuildResources(IReadOnlyList<RenderPipelineAsset> renderPipelineAssets)
        {
            if (!(cellSharedDataAsset?.IsValid() ?? false) && !(cellBricksDataAsset?.IsValid() ?? false) && scenarioCount == 0)
                yield break;

            bool enabled = false;
            bool useL2 = false;
            foreach (var asset in renderPipelineAssets)
            {
                if (!(asset is IProbeVolumeEnabledRenderPipeline pipeline) || !pipeline.supportProbeVolume)
                    continue;

                enabled = true;
                useL2 |= pipeline.maxSHBands == ProbeVolumeSHBands.SphericalHarmonicsL2;
            }

            yield return new ProbeVolumeBuildResource(cellSharedDataAsset, enabled);
            yield return new ProbeVolumeBuildResource(cellBricksDataAsset, enabled);
            yield return new ProbeVolumeBuildResource(cellSupportDataAsset, false);
            if (scenarios == null)
                yield break;

            foreach (var scenario in scenarios.Values)
            {
                if (scenario == null)
                    continue;

                yield return new ProbeVolumeBuildResource(scenario.cellDataAsset, enabled);
                yield return new ProbeVolumeBuildResource(scenario.cellOptionalDataAsset, enabled && useL2);
                yield return new ProbeVolumeBuildResource(scenario.cellProbeOcclusionDataAsset, enabled && bakedProbeOcclusion);
            }
        }

        internal override bool TryGetScenarioHash(string scenario, out int sceneHash)
        {
            sceneHash = 0;
            if (scenario == null || scenarios == null || !scenarios.TryGetValue(scenario, out var data) || data == null)
                return false;

            sceneHash = data.sceneHash;
            return true;
        }

        internal override long GetDiskSizeOfSharedData()
        {
            return GetFileSize(cellBricksDataAsset) + GetFileSize(cellSharedDataAsset) + GetFileSize(cellSupportDataAsset);
        }

        internal override long GetDiskSizeOfScenarioData(string scenario)
        {
            if (scenario == null || scenarios == null || !scenarios.TryGetValue(scenario, out var data) || data == null)
                return 0;

            return GetFileSize(data.cellDataAsset) + GetFileSize(data.cellOptionalDataAsset) + GetFileSize(data.cellProbeOcclusionDataAsset);
        }

        static long GetFileSize(ProbeVolumeStreamableAsset asset)
        {
            if (asset == null || !asset.IsValid())
                return 0;

            var path = asset.GetAssetPath();
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }

        internal override void ClearAssets()
        {
            CleanupStreamableAssets();
            DeleteAsset(cellBricksDataAsset);
            DeleteAsset(cellSharedDataAsset);
            DeleteAsset(cellSupportDataAsset);
            cellBricksDataAsset = null;
            cellSharedDataAsset = null;
            cellSupportDataAsset = null;
            if (scenarios == null)
                return;

            foreach (var scenario in scenarios.Values)
                DeleteScenarioAssets(scenario);

            scenarios.Clear();
        }

        internal override void RemoveScenario(string scenario)
        {
            if (scenarios == null || !scenarios.TryGetValue(scenario, out var data))
                return;

            DeleteScenarioAssets(data);
            scenarios.Remove(scenario);
        }

        internal override void RenameScenario(string scenario, string newName, string assetName)
        {
            if (scenarios == null || !scenarios.TryGetValue(scenario, out var data))
                return;

            scenarios.Remove(scenario);
            scenarios.Add(newName, data);
            RenameScenarioAssets(data, assetName, newName);
        }

        internal override void EnsureScenarioAssetNames(string assetName)
        {
            if (scenarios == null)
                return;

            foreach (var scenario in scenarios)
                RenameScenarioAssets(scenario.Value, assetName, scenario.Key);
        }

        internal override void UnloadAssetsForWeakReference()
        {
            Resources.UnloadAsset(cellBricksDataAsset?.asset);
            Resources.UnloadAsset(cellSharedDataAsset?.asset);
            Resources.UnloadAsset(cellSupportDataAsset?.asset);
            if (scenarios == null)
                return;

            foreach (var scenario in scenarios.Values)
            {
                if (scenario == null)
                    continue;

                Resources.UnloadAsset(scenario.cellDataAsset?.asset);
                Resources.UnloadAsset(scenario.cellOptionalDataAsset?.asset);
                Resources.UnloadAsset(scenario.cellProbeOcclusionDataAsset?.asset);
            }
        }

        static void DeleteScenarioAssets(ProbeVolumeBakingSet.PerScenarioDataInfo data)
        {
            if (data == null)
                return;

            DeleteAsset(data.cellDataAsset);
            DeleteAsset(data.cellOptionalDataAsset);
            DeleteAsset(data.cellProbeOcclusionDataAsset);
        }

        static void DeleteAsset(ProbeVolumeStreamableAsset asset)
        {
            if (asset == null || !asset.IsValid())
                return;

            asset.Dispose();
            AssetDatabase.DeleteAsset(asset.GetAssetPath());
        }

        static void RenameScenarioAssets(ProbeVolumeBakingSet.PerScenarioDataInfo data, string assetName, string scenario)
        {
            if (data == null)
                return;

            GetCellDataFileNames(assetName, scenario, out var cellDataFileName, out var cellOptionalDataFileName, out var cellProbeOcclusionDataFileName);
            RenameAsset(data.cellDataAsset, cellDataFileName);
            RenameAsset(data.cellOptionalDataAsset, cellOptionalDataFileName);
            RenameAsset(data.cellProbeOcclusionDataAsset, cellProbeOcclusionDataFileName);
        }

        static void RenameAsset(ProbeVolumeStreamableAsset asset, string name)
        {
            if (asset != null && asset.IsValid() && Path.GetFileName(asset.GetAssetPath()) != name)
                asset.RenameAsset(name);
        }

        internal static void GetCellDataFileNames(string basePath, string scenario, out string cellDataFileName, out string cellOptionalDataFileName, out string cellProbeOcclusionDataFileName)
        {
            cellDataFileName = $"{basePath}-{scenario}.CellData.bytes";
            cellOptionalDataFileName = $"{basePath}-{scenario}.CellOptionalData.bytes";
            cellProbeOcclusionDataFileName = $"{basePath}-{scenario}.CellProbeOcclusionData.bytes";
        }
#endif
    }
}
