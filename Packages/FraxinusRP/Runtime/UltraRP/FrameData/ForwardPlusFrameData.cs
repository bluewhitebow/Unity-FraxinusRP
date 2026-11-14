using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    // Each context owns its light-list upload and constants. Child contexts may
    // have a different viewport and projection, including a different jitter.
    public sealed class ForwardPlusFrameData : ContextItem
    {
        internal BufferHandle zBins;
        internal BufferHandle tileMasks;
        internal TextureHandle reflectionProbeAtlas;
        internal Vector4 parameters0, parameters1, parameters2;
        internal float4[] zBinUpload;
        internal float4[] tileMaskUpload;

        internal bool isValid => zBins.IsValid() && tileMasks.IsValid();

        public override void Reset()
        {
            zBins = BufferHandle.nullHandle;
            tileMasks = BufferHandle.nullHandle;
            reflectionProbeAtlas = TextureHandle.nullHandle;
            parameters0 = parameters1 = parameters2 = Vector4.zero;
            // Reuse CPU upload storage. RenderGraph owns the GPU allocations.
        }
    }
}
