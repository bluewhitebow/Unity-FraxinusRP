using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class DepthNormalOnlyPass : BuiltinPass
    {
        private class PassData
        {
            internal TextureHandle cameraDepthTexture;
            internal TextureHandle cameraNormalsTexture;
            internal bool accurateGbufferNormals;
            internal bool enableRenderingLayers;
            internal RenderingLayerUtils.MaskSize maskSize;
            internal RendererListHandle rendererList;
        }

        internal List<ShaderTagId> shaderTagIds { get; set; }
        internal bool enableRenderingLayers { get; set; } = false;
        internal RenderingLayerUtils.MaskSize renderingLayersMaskSize { get; set; }

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
        private static readonly List<ShaderTagId> k_depthNormals = new List<ShaderTagId> { new ShaderTagId("DepthNormals"), new ShaderTagId("DepthNormalsOnly") };
        private static readonly int s_cameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int s_cameraNormalsTextureID = Shader.PropertyToID("_CameraNormalsTexture");
        private static readonly int s_cameraRenderingLayersTextureID = Shader.PropertyToID("_CameraRenderingLayersTexture");


        public DepthNormalOnlyPass() : base()
        {
            this.shaderTagIds = k_depthNormals;
        }


        protected override void OnCameraCleanup(CommandBuffer cmd) 
        {
            shaderTagIds = k_depthNormals;
        }

        protected override void OnDispose() { }


        public static GraphicsFormat GetGraphicsFormat()
        {
            if (RenderingUtils.SupportsGraphicsFormat(GraphicsFormat.R8G8B8A8_SNorm, GraphicsFormatUsage.Render))
                return GraphicsFormat.R8G8B8A8_SNorm; // Preferred format
            else if (RenderingUtils.SupportsGraphicsFormat(GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormatUsage.Render))
                return GraphicsFormat.R16G16B16A16_SFloat; // fallback
            else
                return GraphicsFormat.R32G32B32A32_SFloat; // fallback
        }

        internal void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle cameraNormalsTexture, TextureHandle cameraDepthTexture, TextureHandle renderingLayersTexture, uint batchLayerMask, bool setGlobalDepth, bool setGlobalTextures)
        {
            RenderingData renderingData = frameData.Get<RenderingData>();
            CameraData cameraData = frameData.Get<CameraData>();
            LightData lightData = frameData.Get<LightData>();

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(_passName, out var passData, _profilingSampler))
            {
                passData.cameraNormalsTexture = cameraNormalsTexture;
                builder.SetRenderAttachment(cameraNormalsTexture, 0, AccessFlags.Write);
                passData.cameraDepthTexture = cameraDepthTexture;
                builder.SetRenderAttachmentDepth(cameraDepthTexture, AccessFlags.Write);

                passData.accurateGbufferNormals = renderingData.hasDeferred && cameraData.renderer.accurateGbufferNormals;
                passData.enableRenderingLayers = enableRenderingLayers;
                if (passData.enableRenderingLayers)
                {
                    builder.SetRenderAttachment(renderingLayersTexture, 1, AccessFlags.Write);
                    passData.maskSize = renderingLayersMaskSize;
                }

                var param = InitRendererListParams(renderingData, cameraData, lightData);
#if UNITY_6000_0_OR_NEWER
                param.filteringSettings.batchLayerMask = batchLayerMask;
#endif
                passData.rendererList = renderGraph.CreateRendererList(param);
                builder.UseRendererList(passData.rendererList);

                if (setGlobalTextures)
                {
                    builder.SetGlobalTextureAfterPass(cameraNormalsTexture, s_cameraNormalsTextureID);
                    if (passData.enableRenderingLayers)
                        builder.SetGlobalTextureAfterPass(renderingLayersTexture, s_cameraRenderingLayersTextureID);
                }

                if (setGlobalDepth)
                    builder.SetGlobalTextureAfterPass(cameraDepthTexture, s_cameraDepthTextureID);


                // Required here because of RenderingLayerUtils.SetupProperties
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    RenderingLayerUtils.SetupProperties(context.cmd, data.maskSize);
                    ExecutePass(context.cmd, data, data.rendererList);
                });
            }
        }

        private RendererListParams InitRendererListParams(RenderingData renderingData, CameraData cameraData, LightData lightData)
        {
            var sortFlags = cameraData.defaultOpaqueSortFlags;
            var drawSettings = RenderingUtils.CreateDrawingSettings(this.shaderTagIds, renderingData, cameraData, lightData, sortFlags);
            drawSettings.perObjectData = PerObjectData.None;
            return new RendererListParams(renderingData.cullResults, drawSettings, _filteringSettings);
        }

        private void UpdateFilteringSettings()
        {
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, _layerMask);
        }

        private static void ExecutePass(RasterCommandBuffer cmd, PassData passData, RendererList rendererList)
        {
            // Match this camera's target format; Forward uses signed normals.
            cmd.SetKeyword(ShaderGlobalKeywords._GBUFFER_NORMALS_OCT, passData.accurateGbufferNormals);

            // Enable Rendering Layers
            if (passData.enableRenderingLayers)
                cmd.SetKeyword(ShaderGlobalKeywords.WriteRenderingLayers, true);

            // Draw
            cmd.DrawRendererList(rendererList);

            // Clean up
            if (passData.enableRenderingLayers)
                cmd.SetKeyword(ShaderGlobalKeywords.WriteRenderingLayers, false);
        }
    }
}
