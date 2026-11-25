using System;
using Unity.Mathematics;

namespace UnityEngine.Rendering
{
    public sealed partial class ProbeVolumeBakingSet
    {
        [Serializable]
        internal class PerScenarioDataInfo
        {
            public void Initialize(ProbeVolumeSHBands shBands) => m_HasValidData = ComputeHasValidData(shBands);
            public bool IsValid() => cellDataAsset != null && cellDataAsset.IsValid();
            public bool HasValidData(ProbeVolumeSHBands shBands)
            {
#if UNITY_EDITOR
                return ComputeHasValidData(shBands);
#else
                return m_HasValidData;
#endif
            }
            public bool ComputeHasValidData(ProbeVolumeSHBands shBands) => cellDataAsset != null && cellDataAsset.FileExists() &&
                (shBands == ProbeVolumeSHBands.SphericalHarmonicsL1 || (cellOptionalDataAsset != null && cellOptionalDataAsset.FileExists()));

            public int sceneHash;
            public ProbeVolumeStreamableAsset cellDataAsset;
            public ProbeVolumeStreamableAsset cellOptionalDataAsset;
            public ProbeVolumeStreamableAsset cellProbeOcclusionDataAsset;
            bool m_HasValidData;
        }

        internal APVProbeVolumeBakedData apvBakedData
        {
            get
            {
                var data = FindBakedData(APVProbeReferenceVolumeBackend.backendId) as APVProbeVolumeBakedData;
                if (data == null)
                {
                    data = new APVProbeVolumeBakedData();
                    AddOrReplaceBakedData(data);
                }
                data.scenarios ??= new SerializedDictionary<string, PerScenarioDataInfo>();
                return data;
            }
        }

        internal ProbeVolumeStreamableAsset cellSharedDataAsset { get => apvBakedData.cellSharedDataAsset; set => apvBakedData.cellSharedDataAsset = value; }
        internal SerializedDictionary<string, PerScenarioDataInfo> scenarios => apvBakedData.scenarios;
        internal ProbeVolumeStreamableAsset cellBricksDataAsset { get => apvBakedData.cellBricksDataAsset; set => apvBakedData.cellBricksDataAsset = value; }
        internal ProbeVolumeStreamableAsset cellSupportDataAsset { get => apvBakedData.cellSupportDataAsset; set => apvBakedData.cellSupportDataAsset = value; }
        internal int chunkSizeInBricks { get => apvBakedData.chunkSizeInBricks; set => apvBakedData.chunkSizeInBricks = value; }
        internal bool bakedProbeOcclusion { get => apvBakedData.bakedProbeOcclusion; set => apvBakedData.bakedProbeOcclusion = value; }
        internal int bakedSkyOcclusionValue { get => apvBakedData.bakedSkyOcclusionValue; set => apvBakedData.bakedSkyOcclusionValue = value; }
        internal int bakedSkyShadingDirectionValue { get => apvBakedData.bakedSkyShadingDirectionValue; set => apvBakedData.bakedSkyShadingDirectionValue = value; }
        internal int bakedMaskCount { get => apvBakedData.bakedMaskCount; set => apvBakedData.bakedMaskCount = value; }
        internal uint4 bakedLayerMasks { get => apvBakedData.bakedLayerMasks; set => apvBakedData.bakedLayerMasks = value; }
        internal int maxSHChunkCount { get => apvBakedData.maxSHChunkCount; set => apvBakedData.maxSHChunkCount = value; }
        internal int L0ChunkSize { get => apvBakedData.L0ChunkSize; set => apvBakedData.L0ChunkSize = value; }
        internal int L1ChunkSize { get => apvBakedData.L1ChunkSize; set => apvBakedData.L1ChunkSize = value; }
        internal int L2TextureChunkSize { get => apvBakedData.L2TextureChunkSize; set => apvBakedData.L2TextureChunkSize = value; }
        internal int ProbeOcclusionChunkSize { get => apvBakedData.ProbeOcclusionChunkSize; set => apvBakedData.ProbeOcclusionChunkSize = value; }
        internal int sharedValidityMaskChunkSize { get => apvBakedData.sharedValidityMaskChunkSize; set => apvBakedData.sharedValidityMaskChunkSize = value; }
        internal int sharedSkyOcclusionL0L1ChunkSize { get => apvBakedData.sharedSkyOcclusionL0L1ChunkSize; set => apvBakedData.sharedSkyOcclusionL0L1ChunkSize = value; }
        internal int sharedSkyShadingDirectionIndicesChunkSize { get => apvBakedData.sharedSkyShadingDirectionIndicesChunkSize; set => apvBakedData.sharedSkyShadingDirectionIndicesChunkSize = value; }
        internal int sharedDataChunkSize { get => apvBakedData.sharedDataChunkSize; set => apvBakedData.sharedDataChunkSize = value; }
        internal int supportPositionChunkSize { get => apvBakedData.supportPositionChunkSize; set => apvBakedData.supportPositionChunkSize = value; }
        internal int supportValidityChunkSize { get => apvBakedData.supportValidityChunkSize; set => apvBakedData.supportValidityChunkSize = value; }
        internal int supportTouchupChunkSize { get => apvBakedData.supportTouchupChunkSize; set => apvBakedData.supportTouchupChunkSize = value; }
        internal int supportLayerMaskChunkSize { get => apvBakedData.supportLayerMaskChunkSize; set => apvBakedData.supportLayerMaskChunkSize = value; }
        internal int supportOffsetsChunkSize { get => apvBakedData.supportOffsetsChunkSize; set => apvBakedData.supportOffsetsChunkSize = value; }
        internal int supportDataChunkSize { get => apvBakedData.supportDataChunkSize; set => apvBakedData.supportDataChunkSize = value; }
        internal bool bakedSkyOcclusion { get => apvBakedData.bakedSkyOcclusion; set => apvBakedData.bakedSkyOcclusion = value; }
        internal bool bakedSkyShadingDirection { get => apvBakedData.bakedSkyShadingDirection; set => apvBakedData.bakedSkyShadingDirection = value; }
    }
}
