using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    internal class DecalPreviewPass
    {
        private FilteringSettings _filteringSettings;
        private List<ShaderTagId> _shaderTagIdList;
        private ProfilingSampler _profilingSampler;

        public DecalPreviewPass()
        {
            _profilingSampler = new ProfilingSampler("DecalPreview");
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, -1);

            _shaderTagIdList = new List<ShaderTagId>();
            _shaderTagIdList.Add(new ShaderTagId(DecalShaderPassNames.decalScreenSpaceMesh));
            _shaderTagIdList.Add(new ShaderTagId(DecalShaderPassNames.decalPreview));
        }

        private class PassData
        {
            internal RendererListHandle rendererList;
        }

        public void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle colorTarget, TextureHandle depthTarget)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Decal Preview", out var passData, _profilingSampler))
            {
                RenderingData renderingData = frameData.Get<RenderingData>();
                CameraData cameraData = frameData.Get<CameraData>();
                LightData lightData = frameData.Get<LightData>();

                builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Read);

                // Create renderer list
                SortingCriteria sortingCriteria = cameraData.defaultOpaqueSortFlags;
                DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                    _shaderTagIdList, renderingData, cameraData, lightData, sortingCriteria);
                var rendererListParams = new RendererListParams(renderingData.cullResults, drawingSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(data.rendererList);
                });
            }
        }
    }
}

