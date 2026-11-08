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
        public bool hasDeferredCluster;
        public ClusterLightingMode clusterLightingMode;

        public override void Reset()
        {
            renderPassInputs = default;
            opaqueLayerMask = 0;
            hasForwardPlus = false;
            hasDeferred = false;
            hasDeferredCluster = false;
            clusterLightingMode = ClusterLightingMode.Auto;
        }
    }
}
