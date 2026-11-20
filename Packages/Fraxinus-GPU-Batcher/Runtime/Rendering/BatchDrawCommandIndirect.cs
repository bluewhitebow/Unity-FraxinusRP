using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher.Rendering
{
    // Keep the Unity 6 flag semantics independent of Unity 2022's LODCrossFade value.
    [Flags]
    public enum IndirectDrawCommandFlags
    {
        None = 0,
        FlipWinding = 1,
        HasMotion = 2,
        IsLightMapped = 4,
        HasSortingPosition = 8,
        LODCrossFadeKeyword = 16,
        LODCrossFadeValuePacked = 32,
        LODCrossFade = LODCrossFadeKeyword | LODCrossFadeValuePacked,
        UseLegacyLightmapsKeyword = 64
    }

    /// <summary>
    /// Package-owned description of a BRG indirect draw.
    /// A backend must convert it to native draw commands field by field.
    /// Buffer handles are borrowed; the producer owns the buffers and their lifetime.
    /// </summary>
    public struct BatchDrawCommandIndirect
    {
        public IndirectDrawCommandFlags flags;
        public BatchID batchID;
        public BatchMaterialID materialID;
        public ushort splitVisibilityMask;
        public ushort lightmapIndex;
        public int sortingPosition;
        public uint visibleOffset;
        public BatchMeshID meshID;
        public MeshTopology topology;
        public GraphicsBufferHandle visibleInstancesBufferHandle;
        public uint visibleInstancesBufferWindowOffset;
        public uint visibleInstancesBufferWindowSizeBytes;
        public GraphicsBufferHandle indirectArgsBufferHandle;

        /// <summary>Byte offset of the draw arguments in the indirect arguments buffer.</summary>
        public uint indirectArgsBufferOffset;
    }
}
