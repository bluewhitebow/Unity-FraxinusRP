using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    internal class DecalDrawScreenSpaceSystem : DecalDrawSystem
    {
        public DecalDrawScreenSpaceSystem(DecalEntityManager entityManager)
            : base("DecalDrawScreenSpaceSystem.Execute", entityManager)
        {
        }

        protected override int GetPassIndex(DecalCachedChunk decalCachedChunk) => decalCachedChunk.passIndexScreenSpace;
    }

    internal class DecalScreenSpaceRenderPass
    {
        private FilteringSettings _filteringSettings;
        private List<ShaderTagId> _shaderTagIdList;
        private DecalDrawScreenSpaceSystem _drawSystem;
        private DecalScreenSpaceSettings _settings;
        private bool _decalLayers;
        private ProfilingSampler _profilingSampler;

        public DecalScreenSpaceRenderPass(DecalScreenSpaceSettings settings, DecalDrawScreenSpaceSystem drawSystem, bool decalLayers)
        {
            _drawSystem = drawSystem;
            _settings = settings;
            _profilingSampler = new ProfilingSampler("DecalScreenSpace");
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, -1);
            _decalLayers = decalLayers;

            _shaderTagIdList = new List<ShaderTagId>();
            if (_drawSystem == null)
                _shaderTagIdList.Add(new ShaderTagId(DecalShaderPassNames.decalScreenSpaceProjector));
            else
                _shaderTagIdList.Add(new ShaderTagId(DecalShaderPassNames.decalScreenSpaceMesh));
        }

        private class PassData
        {
            internal DecalDrawScreenSpaceSystem drawSystem;
            internal DecalScreenSpaceSettings settings;
            internal bool decalLayers;
            internal bool isGLDevice;
            internal CameraData cameraData;
            internal RendererListHandle rendererList;
            internal ForwardPlusFrameData forwardPlus;
        }

        public void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle colorTarget, TextureHandle depthTarget)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Decal ScreenSpace", out var passData, _profilingSampler))
            {
                passData.forwardPlus = ForwardLights.UseLightingResources(builder, frameData);
                ResourceData resourceData = frameData.Get<ResourceData>();
                RenderingData renderingData = frameData.Get<RenderingData>();
                CameraData cameraData = frameData.Get<CameraData>();
                LightData lightData = frameData.Get<LightData>();

                passData.drawSystem = _drawSystem;
                passData.settings = _settings;
                passData.decalLayers = _decalLayers;
                resourceData.decalLayers = _decalLayers;
                passData.isGLDevice = DecalRenderPassNode.isGLDevice;
                passData.cameraData = cameraData;

                builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Read);

                // Create renderer list
                SortingCriteria sortingCriteria = SortingCriteria.None;
                DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                    _shaderTagIdList, renderingData, cameraData, lightData, sortingCriteria);
                var rendererListParams = new RendererListParams(renderingData.cullResults, drawingSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                builder.UseRendererList(passData.rendererList);

                if (resourceData.cameraDepthTexture.IsValid())
                    builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);

                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    ForwardLights.BindLightLists(context.cmd, data.forwardPlus);
                    ExecutePass(context.cmd, data);
                });
            }
        }

        private static void ExecutePass(RasterCommandBuffer cmd, PassData passData)
        {
            // Setup normal reconstruction
            NormalReconstruction.SetupProperties(cmd, passData.cameraData);

            // Set normal blend quality keywords
            cmd.SetKeyword(ShaderGlobalKeywords.DecalNormalBlendLow, passData.settings.normalBlend == DecalNormalBlend.Low);
            cmd.SetKeyword(ShaderGlobalKeywords.DecalNormalBlendMedium, passData.settings.normalBlend == DecalNormalBlend.Medium);
            cmd.SetKeyword(ShaderGlobalKeywords.DecalNormalBlendHigh, passData.settings.normalBlend == DecalNormalBlend.High);

            if (!passData.isGLDevice)
                cmd.SetKeyword(ShaderGlobalKeywords.DecalLayers, passData.decalLayers);

            passData.drawSystem?.Execute(cmd);
            cmd.DrawRendererList(passData.rendererList);
        }
    }
}

