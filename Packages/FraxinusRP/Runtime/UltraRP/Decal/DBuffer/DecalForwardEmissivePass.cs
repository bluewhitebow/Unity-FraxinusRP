using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    internal class DecalDrawFowardEmissiveSystem : DecalDrawSystem
    {
        public DecalDrawFowardEmissiveSystem(DecalEntityManager entityManager) : base("DecalDrawFowardEmissiveSystem.Execute", entityManager) { }
        protected override int GetPassIndex(DecalCachedChunk decalCachedChunk) => decalCachedChunk.passIndexEmissive;
    }

    internal class DecalForwardEmissivePass
    {
        private FilteringSettings _filteringSettings;
        private List<ShaderTagId> _shaderTagIdList;
        private DecalDrawFowardEmissiveSystem _drawSystem;
        private ProfilingSampler _profilingSampler;

        public DecalForwardEmissivePass(DecalDrawFowardEmissiveSystem drawSystem)
        {
            _drawSystem = drawSystem;
            _profilingSampler = new ProfilingSampler("DecalForwardEmissive");
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, -1);

            _shaderTagIdList = new List<ShaderTagId>();
            _shaderTagIdList.Add(new ShaderTagId(DecalShaderPassNames.decalMeshForwardEmissive));
            _shaderTagIdList.Add(new ShaderTagId(DecalShaderPassNames.decalProjectorForwardEmissive));
        }

        private class PassData
        {
            internal DecalDrawFowardEmissiveSystem drawSystem;
            internal RendererListHandle rendererList;
        }

        public void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle colorTarget, TextureHandle depthTarget)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Decal Forward Emissive", out var passData, _profilingSampler))
            {
                RenderingData renderingData = frameData.Get<RenderingData>();
                CameraData cameraData = frameData.Get<CameraData>();
                LightData lightData = frameData.Get<LightData>();

                passData.drawSystem = _drawSystem;

                // Create renderer list
                SortingCriteria sortingCriteria = cameraData.defaultOpaqueSortFlags;
                DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                    _shaderTagIdList, 
                    renderingData, 
                    cameraData, 
                    lightData, 
                    sortingCriteria);

                var rendererListParams = new RendererListParams(renderingData.cullResults, drawingSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Read);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    data.drawSystem.Execute(context.cmd);
                    context.cmd.DrawRendererList(data.rendererList);
                });
            }
        }
    }
}

