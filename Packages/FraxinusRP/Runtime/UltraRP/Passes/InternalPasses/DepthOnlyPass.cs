using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    public class DepthOnlyPass : BuiltinPass
    {
        private class PassData
        {
            internal RendererListHandle rendererList;
        }

        internal ShaderTagId shaderTagId { get; set; } = k_shaderTagId;

        private FilteringSettings _filteringSettings;
        private LayerMask _layerMask;

        public LayerMask layerMask
        {
            get => _layerMask;
            set
            {
                if (_layerMask != value)
                {
                    _layerMask = value;
                    UpdateFilteringSettings();
                }
            }
        }

        // Statics
        private static readonly ShaderTagId k_shaderTagId = new ShaderTagId("DepthOnly");
        private static readonly int s_cameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");

        public DepthOnlyPass() : base()
        {
            UpdateFilteringSettings();
        }

        protected override void OnCameraCleanup(CommandBuffer cmd) { }

        protected override void OnDispose() { }


        internal void Render(RenderGraph renderGraph, ContextContainer frameData, ref TextureHandle cameraDepthTexture, uint batchLayerMask, bool setGlobalDepth)
        {
            RenderingData renderingData = frameData.Get<RenderingData>();
            CameraData cameraData       = frameData.Get<CameraData>();
            LightData lightData         = frameData.Get<LightData>();


            using (var builder = renderGraph.AddRasterRenderPass<PassData>(_passName, out var passData, _profilingSampler))
            {
                var param = InitRendererListParams(renderingData, cameraData, lightData);
#if UNITY_6000_0_OR_NEWER
                param.filteringSettings.batchLayerMask = batchLayerMask;
#endif
                passData.rendererList = renderGraph.CreateRendererList(param);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderAttachmentDepth(cameraDepthTexture, AccessFlags.Write);
                if (setGlobalDepth)
                    builder.SetGlobalTextureAfterPass(cameraDepthTexture, s_cameraDepthTextureID);


                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    using (new ProfilingScope(context.cmd, ProfilingSampler.Get(UltraRPProfileId.DepthPrepass)))
                    {
                        context.cmd.DrawRendererList(data.rendererList);
                    }
                });
            }
        }

        private RendererListParams InitRendererListParams(RenderingData renderingData, CameraData cameraData, LightData lightData)
        {
            var sortFlags = cameraData.defaultOpaqueSortFlags;
            var drawSettings = RenderingUtils.CreateDrawingSettings(this.shaderTagId, renderingData, cameraData, lightData, sortFlags);
            drawSettings.perObjectData = PerObjectData.None;
            return new RendererListParams(renderingData.cullResults, drawSettings, _filteringSettings);
        }

        private void UpdateFilteringSettings()
        {
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, _layerMask);
        }
    }

}
