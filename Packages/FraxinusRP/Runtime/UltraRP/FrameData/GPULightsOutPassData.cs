using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class GPULightsOutPassData : ContextItem
    {
        internal ShaderVariablesLightList lightListCB;
        // LightsDataBuffer
        internal BufferHandle gpuLightsData;
        internal BufferHandle directionalLightsData;
        //internal BufferHandle envLightsData;

        // CoarseBuffer
        internal BufferHandle coarseLightList;
        // ClusterBuffer
        internal BufferHandle perVoxelOffset;
        internal BufferHandle perVoxelLightLists;
        internal BufferHandle perTileLogBaseTweak;

        public override void Reset()
        {
            // We should always reset texture handles since they are only vaild for the current frame.
            lightListCB = new ShaderVariablesLightList();
            gpuLightsData = BufferHandle.nullHandle;
            coarseLightList = BufferHandle.nullHandle;
            directionalLightsData = BufferHandle.nullHandle;
            perVoxelOffset = BufferHandle.nullHandle;
            perVoxelLightLists = BufferHandle.nullHandle;
            perTileLogBaseTweak = BufferHandle.nullHandle;
        }
    }
}

