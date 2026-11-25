using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;

namespace UnityEngine.Rendering
{
    internal partial class APVProbeReferenceVolume : TProbeReferenceVolume<
        APVProbeReferenceVolume,
        APVCellData,
        APVCellData.PerScenarioData, 
        APVCell,
        APVProbeBrickPool,
        APVProbeBrickBlendingPool,
        APVDataLocation>
    {
        [GenerateHLSL(needAccessors = false, generateCBuffer = true)]
        internal struct CellStreamingScratchBufferLayout : ICellStreamingScratchBufferLayout
        {
            public int _SharedDestChunksOffset;
            public int _L0L1rxOffset;
            public int _L1GryOffset;
            public int _L1BrzOffset;
            public int _ValidityOffset;
            public int _ProbeOcclusionOffset;
            public int _SkyOcclusionOffset;
            public int _SkyShadingDirectionOffset;
            public int _L2_0Offset;
            public int _L2_1Offset;
            public int _L2_2Offset;
            public int _L2_3Offset;

            public int _L0Size;
            public int _L0ProbeSize; // In bytes
            public int _L1Size;
            public int _L1ProbeSize; // In bytes
            public int _ValiditySize;
            public int _ValidityProbeSize; // In bytes
            public int _ProbeOcclusionSize;
            public int _ProbeOcclusionProbeSize; // In bytes
            public int _SkyOcclusionSize;
            public int _SkyOcclusionProbeSize; // In bytes
            public int _SkyShadingDirectionSize;
            public int _SkyShadingDirectionProbeSize; // In bytes
            public int _L2Size;
            public int _L2ProbeSize; // In bytes

            public int _ProbeCountInChunkLine;
            public int _ProbeCountInChunkSlice;
        }

        protected override TProbeVolumeScratchBufferPoolBase CreateScratchBufferPool(ProbeVolumeBakingSet bakingSet)
        {
            return new APVProbeVolumeScratchBufferPool(bakingSet, shBands);
        }

        protected override int GetScratchBufferChunkSize(ProbeVolumeBakingSet bakingSet)
        {
            return bakingSet.GetChunkGPUMemory(this);
        }

        protected override int GetScratchBufferMaxChunkCount(ProbeVolumeBakingSet bakingSet)
        {
            return bakingSet.maxSHChunkCount;
        }

        protected override bool HasValidScenarioDataForStreaming(ProbeVolumeBakingSet.PerScenarioDataInfo scenarioData)
        {
            return scenarioData.HasValidData(m_SHBands);
        }

        protected override int GetOptionalScenarioDataReadSize(ProbeVolumeBakingSet bakingSet, int chunkCount)
        {
            if (m_SHBands != ProbeVolumeSHBands.SphericalHarmonicsL2)
                return 0;

            return bakingSet.L2TextureChunkSize * chunkCount * 4;
        }

        protected override unsafe void AddAdditionalScenarioDataReadCommands(CellStreamingRequest request, int cellIndex, int chunkCount, ref byte* mappedBufferAddr)
        {
            if (!probeOcclusion)
                return;

            var probeOcclusionDataAsset = request.scenarioData.cellProbeOcclusionDataAsset;
            var cellStreamingDesc = probeOcclusionDataAsset.streamableCellDescs[cellIndex];
            var probeOcclusionReadSize = m_CurrentBakingSet.ProbeOcclusionChunkSize * chunkCount;
            request.cellAdditionalScenarioDataStreamingRequest.AddReadCommand(cellStreamingDesc.offset, probeOcclusionReadSize, mappedBufferAddr);
            mappedBufferAddr += probeOcclusionReadSize;
            request.bytesWritten += request.cellAdditionalScenarioDataStreamingRequest.RunCommands(probeOcclusionDataAsset.OpenFile());
        }

        protected override unsafe void ReadSupportData(CellStreamingRequest request, int cellIndex)
        {
            var cellData = request.cell.data;
            if (m_CurrentBakingSet.HasSupportData(backendId))
            {
                var supportDataAsset = m_CurrentBakingSet.cellSupportDataAsset;
                var cellStreamingDesc = supportDataAsset.streamableCellDescs[cellIndex];

                var supportOffset = cellStreamingDesc.offset;
                var positionSize = cellStreamingDesc.elementCount * m_CurrentBakingSet.supportPositionChunkSize;
                var touchupSize = cellStreamingDesc.elementCount * m_CurrentBakingSet.supportTouchupChunkSize;
                var offsetsSize = cellStreamingDesc.elementCount * m_CurrentBakingSet.supportOffsetsChunkSize;
                var layerSize = cellStreamingDesc.elementCount * m_CurrentBakingSet.supportLayerMaskChunkSize;
                var validitySize = cellStreamingDesc.elementCount * m_CurrentBakingSet.supportValidityChunkSize;

                cellData.probePositions = (new NativeArray<byte>(positionSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory)).Reinterpret<Vector3>(1);
                cellData.validity = (new NativeArray<byte>(validitySize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory)).Reinterpret<float>(1);
                cellData.layer = (new NativeArray<byte>(layerSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory)).Reinterpret<byte>(1);
                cellData.touchupVolumeInteraction = (new NativeArray<byte>(touchupSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory)).Reinterpret<float>(1);
                cellData.offsetVectors = (new NativeArray<byte>(offsetsSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory)).Reinterpret<Vector3>(1);

                request.supportStreamingRequest.AddReadCommand(supportOffset, positionSize, (byte*)cellData.probePositions.GetUnsafePtr()); supportOffset += positionSize;
                request.supportStreamingRequest.AddReadCommand(supportOffset, validitySize, (byte*)cellData.validity.GetUnsafePtr()); supportOffset += validitySize;
                request.supportStreamingRequest.AddReadCommand(supportOffset, touchupSize, (byte*)cellData.touchupVolumeInteraction.GetUnsafePtr()); supportOffset += touchupSize;
                request.supportStreamingRequest.AddReadCommand(supportOffset, layerSize, (byte*)cellData.layer.GetUnsafePtr()); supportOffset += layerSize;
                request.supportStreamingRequest.AddReadCommand(supportOffset, offsetsSize, (byte*)cellData.offsetVectors.GetUnsafePtr());
                request.supportStreamingRequest.RunCommands(supportDataAsset.OpenFile());
            }
        }

        protected override void CloseAdditionalScenarioDataFiles(ProbeVolumeBakingSet.PerScenarioDataInfo scenarioData)
        {
            if (scenarioData.cellProbeOcclusionDataAsset != null)
                scenarioData.cellProbeOcclusionDataAsset.CloseFile();
        }
    }
}
