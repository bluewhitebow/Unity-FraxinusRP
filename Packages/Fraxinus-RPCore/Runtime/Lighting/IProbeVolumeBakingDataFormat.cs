using System.Collections.Generic;

namespace UnityEngine.Rendering
{
    internal readonly struct ProbeVolumeBakingDataResolveContext
    {
        internal readonly ProbeVolumeBakingSet bakingSet;
        internal readonly TProbeReferenceVolumeBase referenceVolume;
        internal readonly List<int> cellIndices;
        internal readonly bool useStreamingAsset;
        // Identifies the data layout that owns the cell indices.  Null keeps compatibility with
        // pre-backend callers and resolves to the active backend in ProbeVolumeBakingSet.
        internal readonly string backendId;

        internal ProbeVolumeBakingDataResolveContext(ProbeVolumeBakingSet bakingSet, TProbeReferenceVolumeBase referenceVolume, List<int> cellIndices, bool useStreamingAsset)
            : this(bakingSet, referenceVolume, cellIndices, useStreamingAsset, null)
        {
        }

        internal ProbeVolumeBakingDataResolveContext(ProbeVolumeBakingSet bakingSet, TProbeReferenceVolumeBase referenceVolume, List<int> cellIndices, bool useStreamingAsset, string backendId)
        {
            this.bakingSet = bakingSet;
            this.referenceVolume = referenceVolume;
            this.cellIndices = cellIndices;
            this.useStreamingAsset = useStreamingAsset;
            this.backendId = backendId;
        }
    }

    /// <summary>Owns the resources, decoding and streaming metadata of one probe lighting algorithm.</summary>
    internal interface IProbeVolumeBakingDataFormat
    {
        string backendId { get; }

        ProbeVolumeBakedData CreateBakedData();
        ICellData CreateCellData(ProbeVolumeBakingSet bakingSet, int cellIndex, bool streamingOnly);
        bool ResolveSharedCellData(in ProbeVolumeBakingDataResolveContext context);
        bool ResolveScenarioCellData(in ProbeVolumeBakingDataResolveContext context);
        int GetChunkGPUMemory(ProbeVolumeBakingSet bakingSet, TProbeReferenceVolumeBase referenceVolume);
    }
}
