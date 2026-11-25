using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using Chunk = UnityEngine.Rendering.ProbeBrickPoolBase.BrickChunkAlloc;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// Number of Spherical Harmonics bands that are used with Probe Volumes
    /// </summary>
    [Serializable]
    public enum ProbeVolumeSHBands
    {
        /// <summary>Up to the L1 band of Spherical Harmonics</summary>
        SphericalHarmonicsL1 = 1,
        /// <summary>Up to the L2 band of Spherical Harmonics</summary>
        SphericalHarmonicsL2 = 2,
    }


    internal partial class APVProbeReferenceVolume : TProbeReferenceVolume<
        APVProbeReferenceVolume,
        APVCellData, 
        APVCellData.PerScenarioData, 
        APVCell, 
        APVProbeBrickPool,
        APVProbeBrickBlendingPool,
        APVDataLocation>
    {
        ProbeVolumeSHBands m_SHBands;

        internal override string backendId => APVProbeReferenceVolumeBackend.backendId;

        public override ProbeVolumeSHBands shBands => m_SHBands;
        public override bool probeOcclusion => m_CurrentBakingSet ? m_CurrentBakingSet.bakedProbeOcclusion : false;
        internal override bool skyOcclusion => m_CurrentBakingSet ? m_CurrentBakingSet.bakedSkyOcclusion : false;
        internal override bool skyOcclusionShadingDirection => m_CurrentBakingSet ? m_CurrentBakingSet.bakedSkyShadingDirection : false;
        bool useRenderingLayers => m_CurrentBakingSet.bakedMaskCount != 1;

        internal static readonly int shaderVariablesID = Shader.PropertyToID(nameof(APVShaderVariablesProbeVolumes));

        protected override void InitializeSpecializedParameters(in ProbeVolumeSystemParameters parameters)
        {
            m_SHBands = parameters.shBands;
        }

        // Baking and dilation temporarily require L2 even when the runtime is
        // configured for L1. Keep Unity 6's reinitialization semantics on the
        // generic APV backend.
        internal void ForceSHBand(ProbeVolumeSHBands shBands)
        {
            m_SHBands = shBands;
            DeinitProbeReferenceVolume();

            foreach (var data in perSceneDataList)
                data.Initialize(backendId);

            PerformPendingOperations();
        }

        protected override IProbeVolumeConstantRuntimeResources CreateConstantRuntimeResources()
        {
            return new APVProbeVolumeConstantRuntimeResources();
        }

        /// <summary>
        /// Initialize the reference volume.
        /// </summary>
        protected override bool InitProbeReferenceVolume(bool probeReferenceVolumeInit)
        {
            // If a set without sky occlusion was loaded, and a set with sky occlusion is now loaded,
            // the pools will not have allocated all necessary buffers
            // To support that case, we can force reinit here because we know no scenes are loaded (as we are changing baking set)
            if (probeReferenceVolumeInit && !m_Pool.EnsureTextureValidity(useRenderingLayers, skyOcclusion, skyOcclusionShadingDirection, probeOcclusion))
            {
                m_TemporaryDataLocation.Cleanup();
                m_TemporaryDataLocation = APVProbeBrickPool.CreateDataLocation(APVProbeBrickPool.GetChunkSizeInProbeCount(), compressed: false, m_SHBands, "APV_Intermediate",
                    false, true, useRenderingLayers, skyOcclusion, skyOcclusionShadingDirection, probeOcclusion, out m_TemporaryDataLocationMemCost);
            }

            if (!probeReferenceVolumeInit)
            {
                Profiler.BeginSample("Initialize Reference Volume");
                m_Pool = new APVProbeBrickPool(m_MemoryBudget, m_SHBands, allocateValidityData: true, useRenderingLayers, skyOcclusion, skyOcclusionShadingDirection, probeOcclusion);
                m_BlendingPool = new APVProbeBrickBlendingPool(m_BlendingMemoryBudget, m_SupportScenarioBlending, m_SHBands, probeOcclusion);

                m_Pool.Initialize();
                m_BlendingPool.Initialize();

                m_Index = new ProbeBrickIndex(m_MemoryBudget);

                if (m_SupportGPUStreaming)
                {
                    m_DefragIndex = new ProbeBrickIndex(m_MemoryBudget);
                }

                InitializeGlobalIndirection();

                m_TemporaryDataLocation = APVProbeBrickPool.CreateDataLocation(APVProbeBrickPool.GetChunkSizeInProbeCount(), compressed: false, m_SHBands, "APV_Intermediate",
                    false, true, useRenderingLayers, skyOcclusion, skyOcclusionShadingDirection, probeOcclusion, out m_TemporaryDataLocationMemCost);

                // initialize offsets
                m_PositionOffsets[0] = 0.0f;
                float probeDelta = 1.0f / ProbeBrickPoolBase.brickCellCount;
                for (int i = 1; i < ProbeBrickPoolBase.brickProbeCountPerDim - 1; i++)
                    m_PositionOffsets[i] = i * probeDelta;

                m_PositionOffsets[m_PositionOffsets.Length - 1] = 1.0f;

                Profiler.EndSample();

                probeReferenceVolumeInit = true;

                ClearDebugData();
            }

            return probeReferenceVolumeInit;
        }

        protected override void UpdatePool(CellStreamingRequest request, CommandBuffer cmd, List<Chunk> chunkList, CellStreamingScratchBuffer dataBuffer, int poolIndex)
        {
            // Update pool textures with incoming SH data and ignore any potential frame latency related issues for now.
            if (poolIndex == -1)
                m_Pool.Update(cmd, dataBuffer, request.GetScratchBufferLayout<CellStreamingScratchBufferLayout>(), chunkList, updateSharedData: true, m_Pool.GetValidityTexture(), m_SHBands, skyOcclusion, m_Pool.GetSkyOcclusionTexture(), skyOcclusionShadingDirection, m_Pool.GetSkyShadingDirectionIndicesTexture(), probeOcclusion);
            else
                m_BlendingPool.Update(cmd, dataBuffer, request.GetScratchBufferLayout<CellStreamingScratchBufferLayout>(), chunkList, m_SHBands, poolIndex, m_Pool.GetValidityTexture(), skyOcclusion, m_Pool.GetSkyOcclusionTexture(), skyOcclusionShadingDirection, m_Pool.GetSkyShadingDirectionIndicesTexture(), probeOcclusion);
        }

        protected override bool AddBlendingBricks(APVCell cell)
        {
            Debug.Assert(cell.loaded);

            using var pm = new ProfilerMarker("AddBlendingBricks").Auto();

            Debug.Assert(cell.blendingInfo.chunkList.Count == 0);

            // If no blending is needed, bypass the blending pool and directly update uploaded cells
            bool bypassBlending = m_CurrentBakingSet.otherScenario == null || !cell.hasTwoScenarios;

            // Try to allocate texture space
            if (!bypassBlending && !m_BlendingPool.Allocate(cell.poolInfo.shChunkCount, cell.blendingInfo.chunkList))
                return false;

            if (diskStreamingEnabled)
            {
                if (bypassBlending)
                {
                    if (cell.blendingInfo.blendingFactor != scenarioBlendingFactor)
                        PushDiskStreamingRequest(cell, lightingScenario, -1, m_OnStreamingComplete);

                    // As we bypass blending, we don't load the blending data so we want to avoid trying to blend them later on.
                    cell.blendingInfo.MarkUpToDate();
                }
                else
                {
                    PushDiskStreamingRequest(cell, lightingScenario, 0, m_OnBlendingStreamingComplete);
                    PushDiskStreamingRequest(cell, otherScenario, 1, m_OnBlendingStreamingComplete);
                }
            }
            else
            {
                // Now that we are sure probe data will be uploaded, we can register the cell in the pool
                if (!cell.indexInfo.indexUpdated)
                {
                    // Update the cell index
                    UpdateCellIndex(cell);
                    // Upload validity data directly to main pool - constant per scenario, will not need blending, therefore we use the cellInfo chunk list.
                    var chunkList = cell.poolInfo.chunkList;
                    for (int chunkIndex = 0; chunkIndex < chunkList.Count; ++chunkIndex)
                        UpdateSharedData(chunkList, cell.data.validityNeighMaskData, cell.data.skyOcclusionDataL0L1, cell.data.skyShadingDirectionIndices, chunkIndex);
                }

                if (bypassBlending)
                {
                    if (cell.blendingInfo.blendingFactor != scenarioBlendingFactor)
                    {
                        var chunkList = cell.poolInfo.chunkList;
                        for (int chunkIndex = 0; chunkIndex < chunkList.Count; ++chunkIndex)
                        {
                            // No blending so do the same operation as AddBricks would do. But because cell is already loaded,
                            // no index or chunk data must change, so only probe values need to be updated
                            UpdatePool(chunkList, cell.scenario0, cell.data.validityNeighMaskData, cell.data.skyOcclusionDataL0L1, cell.data.skyShadingDirectionIndices, chunkIndex, -1);
                        }
                    }

                    // As we bypass blending, we don't load the blending data so we want to avoid trying to blend them later on.
                    cell.blendingInfo.MarkUpToDate();
                }
                else
                {
                    var chunkList = cell.blendingInfo.chunkList;
                    for (int chunkIndex = 0; chunkIndex < chunkList.Count; ++chunkIndex)
                    {
                        UpdatePool(chunkList, cell.scenario0, cell.data.validityNeighMaskData, cell.data.skyOcclusionDataL0L1, cell.data.skyShadingDirectionIndices, chunkIndex, 0);
                        UpdatePool(chunkList, cell.scenario1, cell.data.validityNeighMaskData, cell.data.skyOcclusionDataL0L1, cell.data.skyShadingDirectionIndices, chunkIndex, 1);
                    }
                }
            }

            cell.blendingInfo.blending = true;

            return true;
        }

        protected override void UpdatePoolAndIndex(APVCell cell, CellStreamingRequest request, CellStreamingScratchBuffer dataBuffer, int poolIndex, CommandBuffer cmd)
        {
            if (diskStreamingEnabled)
            {
                Debug.Assert(request != null);
                Debug.Assert(dataBuffer != null);
                CellStreamingScratchBufferLayout layout = request.GetScratchBufferLayout<CellStreamingScratchBufferLayout>();

                if (m_DiskStreamingUseCompute)
                {
                    Debug.Assert(dataBuffer.buffer != null);
                    UpdatePool(request, cmd, cell.poolInfo.chunkList, dataBuffer, poolIndex);
                }
                else
                {
                    int chunkCount = cell.poolInfo.chunkList.Count;
                    // The staging buffer includes both destination-coordinate arrays,
                    // regardless of whether upload uses compute or texture copies.

                    APVCellData.PerScenarioData data = default;
                    data.shL0L1RxData = dataBuffer.stagingBuffer.GetSubArray(layout._L0L1rxOffset, chunkCount * layout._L0Size).Reinterpret<ushort>(sizeof(byte));
                    data.shL1GL1RyData = dataBuffer.stagingBuffer.GetSubArray(layout._L1GryOffset, chunkCount * layout._L1Size);
                    data.shL1BL1RzData = dataBuffer.stagingBuffer.GetSubArray(layout._L1BrzOffset, chunkCount * layout._L1Size);

                    NativeArray<byte> validityNeighMaskData = dataBuffer.stagingBuffer.GetSubArray(layout._ValidityOffset, chunkCount * layout._ValiditySize);

                    if (m_SHBands == ProbeVolumeSHBands.SphericalHarmonicsL2)
                    {
                        data.shL2Data_0 = dataBuffer.stagingBuffer.GetSubArray(layout._L2_0Offset, chunkCount * layout._L2Size);
                        data.shL2Data_1 = dataBuffer.stagingBuffer.GetSubArray(layout._L2_1Offset, chunkCount * layout._L2Size);
                        data.shL2Data_2 = dataBuffer.stagingBuffer.GetSubArray(layout._L2_2Offset, chunkCount * layout._L2Size);
                        data.shL2Data_3 = dataBuffer.stagingBuffer.GetSubArray(layout._L2_3Offset, chunkCount * layout._L2Size);
                    }

                    if (probeOcclusion && layout._ProbeOcclusionSize > 0)
                    {
                        data.probeOcclusion = dataBuffer.stagingBuffer.GetSubArray(layout._ProbeOcclusionOffset, chunkCount * layout._ProbeOcclusionSize);
                    }

                    NativeArray<ushort> skyOcclusionData = default;
                    if (skyOcclusion && layout._SkyOcclusionSize > 0)
                    {
                        skyOcclusionData = dataBuffer.stagingBuffer.GetSubArray(layout._SkyOcclusionOffset, chunkCount * layout._SkyOcclusionSize).Reinterpret<ushort>(sizeof(byte));
                    }

                    NativeArray<byte> skyOcclusionDirectionData = default;
                    if (skyOcclusion && skyOcclusionShadingDirection && layout._SkyShadingDirectionSize > 0)
                    {
                        skyOcclusionDirectionData = dataBuffer.stagingBuffer.GetSubArray(layout._SkyShadingDirectionOffset, chunkCount * layout._SkyShadingDirectionSize);
                    }

                    for (int chunkIndex = 0; chunkIndex < chunkCount; ++chunkIndex)
                    {
                        UpdatePool(cell.poolInfo.chunkList, data, validityNeighMaskData, skyOcclusionData, skyOcclusionDirectionData, chunkIndex, poolIndex);
                    }
                }
            }
            else
            {
                // In order not to pre-allocate for the worse case, we update the texture by smaller chunks with a preallocated DataLoc
                for (int chunkIndex = 0; chunkIndex < cell.poolInfo.chunkList.Count; ++chunkIndex)
                    UpdatePool(cell.poolInfo.chunkList, cell.scenario0, cell.data.validityNeighMaskData, cell.data.skyOcclusionDataL0L1, cell.data.skyShadingDirectionIndices, chunkIndex, poolIndex);
            }

            // Index may already be updated when simply switching scenarios.
            if (!cell.indexInfo.indexUpdated)
                UpdateCellIndex(cell);
        }


        private void UpdatePool(List<Chunk> chunkList, APVCellData.PerScenarioData data, NativeArray<byte> validityNeighMaskData,
            NativeArray<ushort> skyOcclusionL0L1Data, NativeArray<byte> skyShadingDirectionIndices, int chunkIndex, int poolIndex)
        {
            var chunkSizeInProbes = APVProbeBrickPool.GetChunkSizeInProbeCount();

            UpdateDataLocationTexture(m_TemporaryDataLocation.TexL0_L1rx, data.shL0L1RxData.GetSubArray(chunkIndex * chunkSizeInProbes * 4, chunkSizeInProbes * 4));
            UpdateDataLocationTexture(m_TemporaryDataLocation.TexL1_G_ry, data.shL1GL1RyData.GetSubArray(chunkIndex * chunkSizeInProbes * 4, chunkSizeInProbes * 4));
            UpdateDataLocationTexture(m_TemporaryDataLocation.TexL1_B_rz, data.shL1BL1RzData.GetSubArray(chunkIndex * chunkSizeInProbes * 4, chunkSizeInProbes * 4));

            if (m_SHBands == ProbeVolumeSHBands.SphericalHarmonicsL2 && data.shL2Data_0.Length > 0)
            {
                UpdateDataLocationTexture(m_TemporaryDataLocation.TexL2_0, data.shL2Data_0.GetSubArray(chunkIndex * chunkSizeInProbes * 4, chunkSizeInProbes * 4));
                UpdateDataLocationTexture(m_TemporaryDataLocation.TexL2_1, data.shL2Data_1.GetSubArray(chunkIndex * chunkSizeInProbes * 4, chunkSizeInProbes * 4));
                UpdateDataLocationTexture(m_TemporaryDataLocation.TexL2_2, data.shL2Data_2.GetSubArray(chunkIndex * chunkSizeInProbes * 4, chunkSizeInProbes * 4));
                UpdateDataLocationTexture(m_TemporaryDataLocation.TexL2_3, data.shL2Data_3.GetSubArray(chunkIndex * chunkSizeInProbes * 4, chunkSizeInProbes * 4));
            }

            if (probeOcclusion && data.probeOcclusion.Length > 0)
            {
                UpdateDataLocationTexture(m_TemporaryDataLocation.TexProbeOcclusion, data.probeOcclusion.GetSubArray(chunkIndex * chunkSizeInProbes * 4, chunkSizeInProbes * 4));
            }

            if (poolIndex == -1) // shared data that don't need to be updated per scenario
            {
                if (validityNeighMaskData.Length > 0)
                {
                    if (m_CurrentBakingSet.bakedMaskCount == 1)
                        UpdateValidityTextureWithoutMask(m_TemporaryDataLocation.TexValidity, validityNeighMaskData.GetSubArray(chunkIndex * chunkSizeInProbes, chunkSizeInProbes));
                    else
                        UpdateDataLocationTexture(m_TemporaryDataLocation.TexValidity, validityNeighMaskData.Reinterpret<uint>(1).GetSubArray(chunkIndex * chunkSizeInProbes, chunkSizeInProbes));
                }

                if (skyOcclusion && skyOcclusionL0L1Data.Length > 0)
                    UpdateDataLocationTexture(m_TemporaryDataLocation.TexSkyOcclusion, skyOcclusionL0L1Data.GetSubArray(chunkIndex * chunkSizeInProbes * 4, chunkSizeInProbes * 4));

                if (skyOcclusionShadingDirection && skyShadingDirectionIndices.Length > 0)
                    UpdateDataLocationTexture(m_TemporaryDataLocation.TexSkyShadingDirectionIndices, skyShadingDirectionIndices.GetSubArray(chunkIndex * chunkSizeInProbes, chunkSizeInProbes));
            }

            // New data format only uploads one chunk at a time (we need predictable chunk size)
            var srcChunks = GetSourceLocations(1, ProbeBrickPoolBase.GetChunkSizeInBrickCount(), m_TemporaryDataLocation);

            // Update pool textures with incoming SH data and ignore any potential frame latency related issues for now.
            if (poolIndex == -1)
                m_Pool.Update(m_TemporaryDataLocation, srcChunks, chunkList, chunkIndex);
            else
                m_BlendingPool.Update(m_TemporaryDataLocation, srcChunks, chunkList, chunkIndex, poolIndex);
        }

        // Updates data shared by all scenarios (validity, sky occlusion, sky direction)
        void UpdateSharedData(List<Chunk> chunkList, NativeArray<byte> validityNeighMaskData, NativeArray<ushort> skyOcclusionData, NativeArray<byte> skyShadingDirectionIndices, int chunkIndex)
        {
            var chunkSizeInProbes = ProbeBrickPoolBase.GetChunkSizeInBrickCount() * ProbeBrickPoolBase.brickProbeCountTotal;

            if (m_CurrentBakingSet.bakedMaskCount == 1)
                UpdateValidityTextureWithoutMask(m_TemporaryDataLocation.TexValidity, validityNeighMaskData.GetSubArray(chunkIndex * chunkSizeInProbes, chunkSizeInProbes));
            else
                UpdateDataLocationTexture(m_TemporaryDataLocation.TexValidity, validityNeighMaskData.Reinterpret<uint>(1).GetSubArray(chunkIndex * chunkSizeInProbes, chunkSizeInProbes));

            if (skyOcclusion && skyOcclusionData.Length > 0)
            {
                UpdateDataLocationTexture(m_TemporaryDataLocation.TexSkyOcclusion, skyOcclusionData.GetSubArray(chunkIndex * chunkSizeInProbes * 4, chunkSizeInProbes * 4));
            }

            if (skyOcclusion && skyOcclusionShadingDirection && skyShadingDirectionIndices.Length > 0)
            {
                UpdateDataLocationTexture(m_TemporaryDataLocation.TexSkyShadingDirectionIndices, skyShadingDirectionIndices.GetSubArray(chunkIndex * chunkSizeInProbes, chunkSizeInProbes));
            }

            var srcChunks = GetSourceLocations(1, ProbeBrickPoolBase.GetChunkSizeInBrickCount(), m_TemporaryDataLocation);

            m_Pool.UpdateValidity(m_TemporaryDataLocation, srcChunks, chunkList, chunkIndex);
        }
    }
}
