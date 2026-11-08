using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    public class RenderingData : ContextItem
    {
        // Non-rendergraph path only. Do NOT use with rendergraph! (RG execution timeline breaks.)
        // NOTE: internal for a ref return in legacy RenderingData.commandBuffer.
        private CommandBuffer _commandBuffer;

        // Non-rendergraph path only. Do NOT use with rendergraph! (RG execution timeline breaks.)
        internal CommandBuffer commandBuffer
        {
            get
            {
                if (_commandBuffer == null)
                    Debug.LogError("UltraRenderingData.commandBuffer is null. RenderGraph does not support this property. Please use the command buffer provided by the RenderGraphContext.");

                return _commandBuffer;
            }

            set
            {
                _commandBuffer = value;
            }
        }

        public CullingResults cullResults;
        public bool supportsDynamicBatching;
        public PerObjectData perObjectData;
        public RenderPassInputSummary renderPassInputs;
        public LayerMask opaqueLayerMask;
        public bool requiresDepthPrepass;
        public bool hasForwardPlus;
        public bool hasDeferred;
        public bool hasDeferredCluster;
        public ClusterLightingMode clusterLightingMode;
        public bool needsOccluderUpdate;


        public override void Reset()
        {
            _commandBuffer = default;
            cullResults = default;
            supportsDynamicBatching = default;
            perObjectData = default;
            renderPassInputs = default;
            opaqueLayerMask = 0;
            requiresDepthPrepass = false;
            hasForwardPlus = false;
            hasDeferred = false;
            hasDeferredCluster = false;
            clusterLightingMode = ClusterLightingMode.Auto;
            needsOccluderUpdate = false;
        }
    }
}
