using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public partial class DeferredLights
    {
        readonly UltraRenderPipelineRuntimeShaders _runtimeShaders;
        Material _clusterDeferredMaterial;

        internal bool useClusterRaster { get; private set; }
        internal Material clusterDeferredMaterial => _clusterDeferredMaterial;

        // Both setup callers must agree before allocating GBuffer slots, deciding
        // depth copies, and deciding whether camera color needs UAV support.
        internal void ConfigureLightingPath(bool nativeRenderPasses, bool clustered,
            ClusterLightingMode mode, bool disableFramebufferFetch)
        {
            cluster = clustered;
            // Seven MRTs covers depth, rendering layers and mixed lighting as well
            // as the four base buffers. Keep Auto on compute on smaller devices.
            useFramebufferFetch = nativeRenderPasses && !disableFramebufferFetch &&
                (!clustered || (mode != ClusterLightingMode.Compute && SystemInfo.supportedRenderTargetCount >= 7));
            // Resolve a material only when it can actually be used. In particular,
            // Auto on the guarded DX11 backend keeps the original compute costs.
            bool rasterAvailable = clustered && (mode == ClusterLightingMode.Raster || useFramebufferFetch) &&
                EnsureClusterRasterMaterial();
            if (clustered && mode == ClusterLightingMode.Raster && !rasterAvailable)
                throw new System.InvalidOperationException("Raster clustered deferred lighting requires a supported ClusterDeferred shader in UltraRenderPipelineRuntimeShaders.clusterDeferredPS.");
            if (clustered && !rasterAvailable)
                useFramebufferFetch = false;
            useClusterRaster = rasterAvailable && (mode == ClusterLightingMode.Raster || useFramebufferFetch);
        }

        bool EnsureClusterRasterMaterial()
        {
            var shader = _runtimeShaders?.clusterDeferredPS;
            if (shader == null || !shader.isSupported)
                return false;
            if (_clusterDeferredMaterial == null || _clusterDeferredMaterial.shader != shader)
            {
                CoreUtils.Destroy(_clusterDeferredMaterial);
                _clusterDeferredMaterial = CoreUtils.CreateEngineMaterial(shader);
            }
            return _clusterDeferredMaterial.passCount >= (int)ShadingModels.CurModelsNum;
        }
    }
}
