using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public class InternalRendererData : ContextItem
    {
        public RenderPassInputSummary renderPassInputs;
        public LayerMask opaqueLayerMask;
        public bool hasForwardPlus;
        public bool hasDeferred;
        public DeferredLightingMode deferredLightingMode;
        public bool hasDeferredCluster => hasDeferred && deferredLightingMode == DeferredLightingMode.GPUCluster;
        public bool hasDeferredPlus => hasDeferred && deferredLightingMode == DeferredLightingMode.DeferredPlus;
        // Forward+ and Deferred+ share CPU-built lists; GPU Cluster has its own light lists.
        public bool usesCpuLightLists => hasForwardPlus || hasDeferredPlus;
        public ClusterLightingMode clusterLightingMode;

        public override void Reset()
        {
            renderPassInputs = default;
            opaqueLayerMask = 0;
            hasForwardPlus = false;
            hasDeferred = false;
            deferredLightingMode = DeferredLightingMode.Stencil;
            clusterLightingMode = ClusterLightingMode.Auto;
        }
    }
}
