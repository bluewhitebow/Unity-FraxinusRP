using FraxinusEngine.Base;
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public enum DeferredLightingMode
    {
        Stencil = 0,
        [InspectorName("Deferred+")]
        DeferredPlus = 1,
        [InspectorName("GPU Cluster")]
        GPUCluster = 2
    }

    public enum ClusterLightingMode
    {
        Auto = 0,
        Compute = 1,
        Raster = 2
    }

    [Serializable, NodeMenuItem("UltraRP/DeferredObjectPass")]
    public class DeferredObjectPass : ScriptableRenderPassNode, ISerializationCallbackReceiver
    {
        [Input("Camera Target")]
        public CameraTargetBufferNodeParameter inCameraTarget;

        [Input("In Gbuffer")]
        public TextureHandlesNodeParameter inGbuffer;

        [Input("Main ShadowMap")]
        public TextureHandleNodeParameter mainShadowMap;

        [Input("Additional ShadowMap")]
        public TextureHandleNodeParameter additionalShadowMap;

        [Output("Camera Target")]
        internal CameraTargetBufferNodeParameter outCameraTarget;

        [SerializeField] private LayerMask _layerMask;
        public LayerMask layerMask
        {
            get => _layerMask;
            set
            {
                _layerMask = value;
            }
        }

        [SerializeField] private DeferredLightingMode _lightingMode;
        [SerializeField, HideInInspector] private int _lightingModeVersion;
        [SerializeField, HideInInspector] private bool _cluster;

        public DeferredLightingMode lightingMode
        {
            get => _lightingModeVersion == 0 && _cluster ? DeferredLightingMode.GPUCluster : _lightingMode;
            set
            {
                _lightingMode = value;
                _lightingModeVersion = 1;
                _cluster = value == DeferredLightingMode.GPUCluster;
            }
        }

        // Retain the scripting API used by existing renderer graph setup tools.
        public bool cluster
        {
            get => lightingMode == DeferredLightingMode.GPUCluster;
            set => lightingMode = value ? DeferredLightingMode.GPUCluster : DeferredLightingMode.Stencil;
        }

        [VisibleIf("_lightingMode", DeferredLightingMode.GPUCluster)]
        [SerializeField, Tooltip("Auto uses Raster when framebuffer fetch is available, otherwise Compute. Raster uses fullscreen stencil-filtered lighting with framebuffer fetch or ordinary GBuffer sampling.")]
        private ClusterLightingMode _clusterLightingMode;
        public ClusterLightingMode clusterLightingMode
        {
            get => _clusterLightingMode;
            set => _clusterLightingMode = value;
        }

        public override LayerMask GetOpaqueLayerMask()
        {
            return layerMask;
        }

        public override RenderingMode RequestRenderingMode()
        {
            switch (lightingMode)
            {
                case DeferredLightingMode.DeferredPlus: return RenderingMode.DeferredPlus;
                case DeferredLightingMode.GPUCluster: return RenderingMode.DeferredCluster;
                default: return RenderingMode.Deferred;
            }
        }

        public override ScriptableRenderPassInput input => cluster ? ScriptableRenderPassInput.Depth : base.input;

        public DeferredObjectPass() : base()
        {
            base.profilingSampler = new ProfilingSampler(nameof(DeferredObjectPass));
        }

        public void OnBeforeSerialize()
        {
            lightingMode = lightingMode;
        }

        public void OnAfterDeserialize()
        {
            if (_lightingModeVersion == 0)
                lightingMode = _cluster ? DeferredLightingMode.GPUCluster : DeferredLightingMode.Stencil;
            // Migrate the removed experimental mode. Value 2 already maps to Raster.
            if ((int)_clusterLightingMode == 3)
                _clusterLightingMode = ClusterLightingMode.Raster;
        }

        public override void SetupPass()
        {
            CameraTargetBuffer buffer = inCameraTarget.value;

            ConfigurePortValue(outCameraTarget, buffer);
        }

        public override bool SupportedInCurrentFrame(ContextContainer frameData)
        {
            return frameData.Get<RenderingData>().hasDeferred;
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            CameraTargetBuffer buffer = inCameraTarget.value;
            TextureHandle[] gbuffer = inGbuffer.value;

            if (!buffer.IsValid() || gbuffer is null)
                return;

            TextureHandle colorTarget = buffer.color;
            TextureHandle depthTarget = buffer.depth;

            ResourceData resourceData   = frameData.Get<ResourceData>();
            RenderingData renderingData = frameData.Get<RenderingData>();
            CameraData cameraData       = frameData.Get<CameraData>();
            LightData lightData         = frameData.Get<LightData>();

            var renderer = cameraData.renderer;
            var deferredLights = renderer.GetModule<DeferredLights>();
            var gBufferPass = renderer.GetModule<GBufferPass>();
            var deferredPass = renderer.GetModule<DeferredPass>();

            TextureHandle activeColorTexture = colorTarget;
            TextureHandle activeDepthTexture = depthTarget;

            TextureHandle mainShadowsTexture = mainShadowMap.value;
            TextureHandle additionalShadowsTexture = additionalShadowMap.value;

            deferredPass.Render(renderGraph, frameData, activeColorTexture, activeDepthTexture, gbuffer, mainShadowsTexture, additionalShadowsTexture);

#if UNITY_2022_1_OR_NEWER && !UNITY_2023_1_OR_NEWER
            if ((!renderingData.hasDeferredCluster || deferredLights.useClusterRaster) && renderGraph.nativeRenderPassesEnabled && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11)
            {
                // Separate deferred lighting from the following forward draws
                // on the legacy D3D11 native-pass backend. The read dependencies
                // preserve color and depth across the submission boundary.
                using (var builder = renderGraph.AddUnsafePass<DeferredSubmissionBoundary>(
                    "Deferred D3D11 Submission Boundary", out _))
                {
                    builder.UseTexture(activeColorTexture, AccessFlags.Read);
                    builder.UseTexture(activeDepthTexture, AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((DeferredSubmissionBoundary _, UnsafeGraphContext context) => { });
                }
            }
#endif
        }

#if UNITY_2022_1_OR_NEWER && !UNITY_2023_1_OR_NEWER
        sealed class DeferredSubmissionBoundary { }
#endif
    }
}
