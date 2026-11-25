using System.Collections.Generic;
using Unity.Collections;

namespace UnityEngine.Rendering
{
    internal sealed class APVProbeVolumeBakingDataFormat : IProbeVolumeBakingDataFormat
    {
        public string backendId => APVProbeReferenceVolumeBackend.backendId;

        public ProbeVolumeBakedData CreateBakedData()
        {
            return new APVProbeVolumeBakedData();
        }

        readonly List<int> m_PrunedScenarioIndexList = new();

        public ICellData CreateCellData(ProbeVolumeBakingSet bakingSet, int cellIndex, bool streamingOnly)
        {
            var cellData = new APVCellData();
            if (streamingOnly)
            {
                foreach (var scenario in bakingSet.apvBakedData.scenarios)
                    cellData.scenarios[scenario.Key] = default;
            }
            return cellData;
        }

        public bool ResolveSharedCellData(in ProbeVolumeBakingDataResolveContext context)
        {
            var bakingSet = context.bakingSet;
            var bakedData = bakingSet.apvBakedData;
            if (bakedData.cellSharedDataAsset == null || !bakedData.cellSharedDataAsset.IsValid())
                return false;
            if (!bakingSet.HasValidSharedData(backendId))
            {
                Debug.LogError($"One or more data files are missing for baking set {bakingSet.name}. Cannot load shared data.");
                return false;
            }

            var sharedData = bakingSet.LoadStreamableAssetData<byte>(bakedData.cellSharedDataAsset, context.cellIndices);
            var bricksData = bakingSet.LoadStreamableAssetData<ProbeBrickIndex.Brick>(bakedData.cellBricksDataAsset, context.cellIndices);
            bool hasSupportData = bakingSet.HasSupportData(backendId);
            var supportData = hasSupportData ? bakingSet.LoadStreamableAssetData<byte>(bakedData.cellSupportDataAsset, context.cellIndices) : default;
            bool resolved = sharedData.IsCreated && bricksData.IsCreated && (!hasSupportData || supportData.IsCreated) && DecodeSharedCellData(context, bricksData, sharedData, supportData);
            bakingSet.ReleaseStreamableAssetData(sharedData);
            bakingSet.ReleaseStreamableAssetData(bricksData);
            bakingSet.ReleaseStreamableAssetData(supportData);
            return resolved;
        }

        public bool ResolveScenarioCellData(in ProbeVolumeBakingDataResolveContext context)
        {
            var bakingSet = context.bakingSet;
            var bakedData = bakingSet.apvBakedData;
            bool loadOptionalData = context.referenceVolume.shBands == ProbeVolumeSHBands.SphericalHarmonicsL2;
            bool loadOcclusionData = bakedData.bakedProbeOcclusion;

            foreach (var scenario in bakedData.scenarios)
            {
                string name = scenario.Key;
                var data = scenario.Value;
                if (!bakedData.HasValidScenarioData(name, context.referenceVolume))
                {
                    Debug.LogError($"One or more data files are missing for baking set {bakingSet.name}, scenario {name}. Cannot load scenario data.");
                    return false;
                }

                m_PrunedScenarioIndexList.Clear();
                foreach (int cellIndex in context.cellIndices)
                {
                    if (data.cellDataAsset.streamableCellDescs.ContainsKey(cellIndex))
                        m_PrunedScenarioIndexList.Add(cellIndex);
                }
                if (m_PrunedScenarioIndexList.Count == 0)
                    continue;

                var scenarioData = bakingSet.LoadStreamableAssetData<byte>(data.cellDataAsset, m_PrunedScenarioIndexList);
                var optionalData = loadOptionalData ? bakingSet.LoadStreamableAssetData<byte>(data.cellOptionalDataAsset, m_PrunedScenarioIndexList) : default;
                var occlusionData = loadOcclusionData ? bakingSet.LoadStreamableAssetData<byte>(data.cellProbeOcclusionDataAsset, m_PrunedScenarioIndexList) : default;
                var scenarioContext = new ProbeVolumeBakingDataResolveContext(bakingSet, context.referenceVolume, m_PrunedScenarioIndexList, context.useStreamingAsset, backendId);
                bool resolved = scenarioData.IsCreated && (!loadOptionalData || optionalData.IsCreated) && (!loadOcclusionData || occlusionData.IsCreated) && DecodeScenarioCellData(scenarioContext, scenarioData, optionalData, occlusionData, name);
                bakingSet.ReleaseStreamableAssetData(scenarioData);
                bakingSet.ReleaseStreamableAssetData(optionalData);
                bakingSet.ReleaseStreamableAssetData(occlusionData);
                if (!resolved)
                {
                    Debug.LogError($"Baked data for scenario '{name}' cannot be loaded.");
                    return false;
                }
            }
            return true;
        }

        static bool DecodeSharedCellData(in ProbeVolumeBakingDataResolveContext context, NativeArray<ProbeBrickIndex.Brick> bricksData, NativeArray<byte> sharedData, NativeArray<byte> supportData)
        {
            var bakingSet = context.bakingSet;
            var referenceVolume = context.referenceVolume;
            bool hasSupportData = supportData.Length != 0;

            int sharedDataChunkOffset = 0;
            int supportDataChunkOffset = 0;
            int totalBricksCount = 0;

            for (int i = 0; i < context.cellIndices.Count; ++i)
            {
                int cellIndex = context.cellIndices[i];
                var cellData = new APVCellData();
                var cellDesc = bakingSet.cellDescs[cellIndex];
                int bricksCount = cellDesc.bricksCount;
                int shChunkCount = cellDesc.shChunkCount;

                Debug.Assert(!bakingSet.cellDataMap.ContainsKey(cellIndex));

                var sourceBricks = bricksData.GetSubArray(totalBricksCount, bricksCount);
                var sourceValidityNeighMaskData = sharedData.GetSubArray(sharedDataChunkOffset, bakingSet.sharedValidityMaskChunkSize * shChunkCount);
                sharedDataChunkOffset += bakingSet.sharedValidityMaskChunkSize * shChunkCount;

                cellData.bricks = CopyIfStreaming(context.useStreamingAsset, sourceBricks);
                cellData.validityNeighMaskData = CopyIfStreaming(context.useStreamingAsset, sourceValidityNeighMaskData);

                if (bakingSet.bakedSkyOcclusion)
                {
                    if (referenceVolume.skyOcclusion)
                    {
                        var sourceSkyOcclusionDataL0L1 = sharedData.GetSubArray(sharedDataChunkOffset, bakingSet.sharedSkyOcclusionL0L1ChunkSize * shChunkCount).Reinterpret<ushort>(1);
                        cellData.skyOcclusionDataL0L1 = CopyIfStreaming(context.useStreamingAsset, sourceSkyOcclusionDataL0L1);
                    }

                    sharedDataChunkOffset += bakingSet.sharedSkyOcclusionL0L1ChunkSize * shChunkCount;

                    if (bakingSet.bakedSkyShadingDirection)
                    {
                        if (referenceVolume.skyOcclusion && referenceVolume.skyOcclusionShadingDirection)
                        {
                            var sourceSkyShadingDirectionIndices = sharedData.GetSubArray(sharedDataChunkOffset, bakingSet.sharedSkyShadingDirectionIndicesChunkSize * shChunkCount);
                            cellData.skyShadingDirectionIndices = CopyIfStreaming(context.useStreamingAsset, sourceSkyShadingDirectionIndices);
                        }

                        sharedDataChunkOffset += bakingSet.sharedSkyShadingDirectionIndicesChunkSize * shChunkCount;
                    }
                }

                if (hasSupportData)
                {
                    var sourcePositions = supportData.GetSubArray(supportDataChunkOffset, shChunkCount * bakingSet.supportPositionChunkSize).Reinterpret<Vector3>(1);
                    supportDataChunkOffset += shChunkCount * bakingSet.supportPositionChunkSize;
                    cellData.probePositions = CopyIfStreaming(context.useStreamingAsset, sourcePositions);

                    var sourceValidity = supportData.GetSubArray(supportDataChunkOffset, shChunkCount * bakingSet.supportValidityChunkSize).Reinterpret<float>(1);
                    supportDataChunkOffset += shChunkCount * bakingSet.supportValidityChunkSize;
                    cellData.validity = CopyIfStreaming(context.useStreamingAsset, sourceValidity);

                    var sourceTouchup = supportData.GetSubArray(supportDataChunkOffset, shChunkCount * bakingSet.supportTouchupChunkSize).Reinterpret<float>(1);
                    supportDataChunkOffset += shChunkCount * bakingSet.supportTouchupChunkSize;
                    cellData.touchupVolumeInteraction = CopyIfStreaming(context.useStreamingAsset, sourceTouchup);

                    if (bakingSet.supportLayerMaskChunkSize != 0)
                    {
                        var sourceLayer = supportData.GetSubArray(supportDataChunkOffset, shChunkCount * bakingSet.supportLayerMaskChunkSize);
                        supportDataChunkOffset += shChunkCount * bakingSet.supportLayerMaskChunkSize;
                        cellData.layer = CopyIfStreaming(context.useStreamingAsset, sourceLayer);
                    }

                    if (bakingSet.supportOffsetsChunkSize != 0)
                    {
                        var sourceOffsetVectors = supportData.GetSubArray(supportDataChunkOffset, shChunkCount * bakingSet.supportOffsetsChunkSize).Reinterpret<Vector3>(1);
                        supportDataChunkOffset += shChunkCount * bakingSet.supportOffsetsChunkSize;
                        cellData.offsetVectors = CopyIfStreaming(context.useStreamingAsset, sourceOffsetVectors);
                    }
                }

                bakingSet.cellDataMap.Add(cellIndex, cellData);
                totalBricksCount += bricksCount;
            }

            return true;
        }

        static bool DecodeScenarioCellData(in ProbeVolumeBakingDataResolveContext context, NativeArray<byte> scenarioData, NativeArray<byte> optionalScenarioData, NativeArray<byte> additionalScenarioData, string scenarioName)
        {
            if (!scenarioData.IsCreated)
                return false;

            var bakingSet = context.bakingSet;
            bool hasOptionalData = optionalScenarioData.IsCreated;
            bool hasAdditionalData = additionalScenarioData.IsCreated && additionalScenarioData.Length > 0;

            int chunkOffsetL0L1 = 0;
            int chunkOffsetL2 = 0;
            int chunkOffsetProbeOcclusion = 0;

            for (int i = 0; i < context.cellIndices.Count; ++i)
            {
                int cellIndex = context.cellIndices[i];
                var cell = bakingSet.cellDataMap[cellIndex];
                var cellDesc = bakingSet.cellDescs[cellIndex];
                var cellState = new APVCellData.PerScenarioData();
                int shChunkCount = cellDesc.shChunkCount;

                var sourceShL0L1RxData = scenarioData.GetSubArray(chunkOffsetL0L1, bakingSet.L0ChunkSize * shChunkCount).Reinterpret<ushort>(1);
                var sourceShL1GL1RyData = scenarioData.GetSubArray(chunkOffsetL0L1 + bakingSet.L0ChunkSize * shChunkCount, bakingSet.L1ChunkSize * shChunkCount);
                var sourceShL1BL1RzData = scenarioData.GetSubArray(chunkOffsetL0L1 + (bakingSet.L0ChunkSize + bakingSet.L1ChunkSize) * shChunkCount, bakingSet.L1ChunkSize * shChunkCount);

                cellState.shL0L1RxData = CopyIfStreaming(context.useStreamingAsset, sourceShL0L1RxData);
                cellState.shL1GL1RyData = CopyIfStreaming(context.useStreamingAsset, sourceShL1GL1RyData);
                cellState.shL1BL1RzData = CopyIfStreaming(context.useStreamingAsset, sourceShL1BL1RzData);

                if (hasOptionalData)
                {
                    int l2DataSize = shChunkCount * bakingSet.L2TextureChunkSize;

                    var sourceShL2Data0 = optionalScenarioData.GetSubArray(chunkOffsetL2 + l2DataSize * 0, l2DataSize);
                    var sourceShL2Data1 = optionalScenarioData.GetSubArray(chunkOffsetL2 + l2DataSize * 1, l2DataSize);
                    var sourceShL2Data2 = optionalScenarioData.GetSubArray(chunkOffsetL2 + l2DataSize * 2, l2DataSize);
                    var sourceShL2Data3 = optionalScenarioData.GetSubArray(chunkOffsetL2 + l2DataSize * 3, l2DataSize);

                    cellState.shL2Data_0 = CopyIfStreaming(context.useStreamingAsset, sourceShL2Data0);
                    cellState.shL2Data_1 = CopyIfStreaming(context.useStreamingAsset, sourceShL2Data1);
                    cellState.shL2Data_2 = CopyIfStreaming(context.useStreamingAsset, sourceShL2Data2);
                    cellState.shL2Data_3 = CopyIfStreaming(context.useStreamingAsset, sourceShL2Data3);
                }

                if (hasAdditionalData)
                {
                    var sourceProbeOcclusionData = additionalScenarioData.GetSubArray(chunkOffsetProbeOcclusion, bakingSet.ProbeOcclusionChunkSize * shChunkCount);
                    cellState.probeOcclusion = CopyIfStreaming(context.useStreamingAsset, sourceProbeOcclusionData);
                }

                chunkOffsetL0L1 += (bakingSet.L0ChunkSize + 2 * bakingSet.L1ChunkSize) * shChunkCount;
                chunkOffsetL2 += bakingSet.L2TextureChunkSize * 4 * shChunkCount;
                chunkOffsetProbeOcclusion += bakingSet.ProbeOcclusionChunkSize * shChunkCount;

                if (cell is APVCellData apvCellData)
                    apvCellData.scenarios[scenarioName] = cellState;
            }

            return true;
        }

        public int GetChunkGPUMemory(ProbeVolumeBakingSet bakingSet, TProbeReferenceVolumeBase referenceVolume)
        {
            return GetChunkGPUMemory(bakingSet, referenceVolume.shBands);
        }

        internal static int GetChunkGPUMemory(ProbeVolumeBakingSet bakingSet, ProbeVolumeSHBands shBands)
        {
            int size = bakingSet.L0ChunkSize + 2 * bakingSet.L1ChunkSize + bakingSet.sharedDataChunkSize;

            if (shBands == ProbeVolumeSHBands.SphericalHarmonicsL2)
                size += 4 * bakingSet.L2TextureChunkSize;

            if (bakingSet.bakedProbeOcclusion)
                size += bakingSet.ProbeOcclusionChunkSize;

            return size;
        }

        static NativeArray<T> CopyIfStreaming<T>(bool useStreamingAsset, NativeArray<T> source)
            where T : struct
        {
            return useStreamingAsset ? new NativeArray<T>(source, Allocator.Persistent) : source;
        }
    }
}
