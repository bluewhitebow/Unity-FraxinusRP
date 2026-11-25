using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    internal class DecalDrawDBufferSystem : DecalDrawSystem
    {
        public DecalDrawDBufferSystem(DecalEntityManager entityManager) : base("DecalDrawIntoDBufferSystem.Execute", entityManager) { }
        protected override int GetPassIndex(DecalCachedChunk decalCachedChunk) => decalCachedChunk.passIndexDBuffer;
    }

    internal class DBufferRenderPass : IDisposable
    {
        internal static string[] s_DBufferNames = { "_DBufferTexture0", "_DBufferTexture1", "_DBufferTexture2", "_DBufferTexture3" };
        internal static string s_DBufferDepthName = "DBufferDepth";

        private DecalDrawDBufferSystem _drawSystem;
        private DBufferSettings _settings;
        private Material _dBufferClear;
        private FilteringSettings _filteringSettings;
        private List<ShaderTagId> _shaderTagIdList;
        private ProfilingSampler _profilingSampler;
        private ProfilingSampler _dBufferClearSampler;
        private bool _decalLayers;

        private TextureHandle[] _dbufferHandles;

        public DBufferRenderPass(Material dBufferClear, DBufferSettings settings, DecalDrawDBufferSystem drawSystem, bool decalLayers)
        {
            _drawSystem = drawSystem;
            _settings = settings;
            _dBufferClear = dBufferClear;
            _profilingSampler = new ProfilingSampler("DBuffer");
            _dBufferClearSampler = new ProfilingSampler("DBufferClear");
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, -1);
            _decalLayers = decalLayers;

            _shaderTagIdList = new List<ShaderTagId>();
            _shaderTagIdList.Add(new ShaderTagId(DecalShaderPassNames.dBufferMesh));
            _shaderTagIdList.Add(new ShaderTagId(DecalShaderPassNames.dBufferProjectorVFX));
        }

        public void Dispose()
        {
            // RenderGraph handles texture cleanup automatically
        }

        private class PassData
        {
            internal DecalDrawDBufferSystem drawSystem;
            internal DBufferSettings settings;
            internal Material dBufferClear;
            internal ProfilingSampler dBufferClearSampler;
            internal bool decalLayers;
            internal RendererListHandle rendererList;
        }

        public void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle depthTarget)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("DBuffer", out var passData, _profilingSampler))
            {
                ResourceData resourceData = frameData.Get<ResourceData>();
                RenderingData renderingData = frameData.Get<RenderingData>();
                CameraData cameraData = frameData.Get<CameraData>();
                LightData lightData = frameData.Get<LightData>();

                passData.drawSystem = _drawSystem;
                passData.settings = _settings;
                passData.dBufferClear = _dBufferClear;
                passData.dBufferClearSampler = _dBufferClearSampler;
                passData.decalLayers = _decalLayers;

                if (_dbufferHandles == null)
                    _dbufferHandles = new TextureHandle[4];

                Array.Clear(_dbufferHandles, 0, _dbufferHandles.Length);
                resourceData.decalLayers = _decalLayers;

                // Create DBuffer textures
                var desc = cameraData.cameraTargetDescriptor;
                desc.msaaSamples = 1;
                desc.depthStencilFormat = GraphicsFormat.None;

                // DBuffer 0 - Albedo + MaterialFlags
                desc.graphicsFormat = QualitySettings.activeColorSpace == ColorSpace.Linear 
                    ? GraphicsFormat.R8G8B8A8_SRGB 
                    : GraphicsFormat.R8G8B8A8_UNorm;
                _dbufferHandles[0] = UltraRenderer.CreateRenderGraphTexture(
                    renderGraph, desc, s_DBufferNames[0], true, new Color(0, 0, 0, 1));
                builder.SetRenderAttachment(_dbufferHandles[0], 0, AccessFlags.Write);

                if (_settings.surfaceData == DecalSurfaceData.AlbedoNormal || 
                    _settings.surfaceData == DecalSurfaceData.AlbedoNormalMAOS)
                {
                    // DBuffer 1 - Normal + Smoothness + Metallic
                    desc.graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm;
                    _dbufferHandles[1] = UltraRenderer.CreateRenderGraphTexture(
                        renderGraph, desc, s_DBufferNames[1], true, new Color(0.5f, 0.5f, 0.5f, 1));
                    builder.SetRenderAttachment(_dbufferHandles[1], 1, AccessFlags.Write);
                }

                if (_settings.surfaceData == DecalSurfaceData.AlbedoNormalMAOS)
                {
                    // DBuffer 2 - MAOS (Metallic, AO, Smoothness)
                    desc.graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm;
                    _dbufferHandles[2] = UltraRenderer.CreateRenderGraphTexture(
                        renderGraph, desc, s_DBufferNames[2], true, new Color(0, 0, 0, 1));
                    builder.SetRenderAttachment(_dbufferHandles[2], 2, AccessFlags.Write);
                }

                builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Read);

                // Use camera depth and normals textures
                if (resourceData.cameraDepthTexture.IsValid())
                    builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                if (resourceData.cameraNormalsTexture.IsValid())
                    builder.UseTexture(resourceData.cameraNormalsTexture, AccessFlags.Read);
                if (_decalLayers && resourceData.renderingLayersTexture.IsValid())
                    builder.UseTexture(resourceData.renderingLayersTexture, AccessFlags.Read);

                // Create renderer list
                SortingCriteria sortingCriteria = cameraData.defaultOpaqueSortFlags;
                DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                    _shaderTagIdList, renderingData, cameraData, lightData, sortingCriteria);
                var rendererListParams = new RendererListParams(renderingData.cullResults, drawingSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                builder.UseRendererList(passData.rendererList);

                // Set global textures after pass
                for (int i = 0; i < 3; ++i)
                {
                    if (_dbufferHandles[i].IsValid())
                        builder.SetGlobalTextureAfterPass(_dbufferHandles[i], Shader.PropertyToID(s_DBufferNames[i]));
                }

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    ExecutePass(context.cmd, data);
                });

                // Store DBuffer handles in resource data
                resourceData.dBuffer = _dbufferHandles;
            }
        }

        private static void ExecutePass(RasterCommandBuffer cmd, PassData passData)
        {
            // Set keywords
            cmd.SetKeyword(ShaderGlobalKeywords.DBufferMRT1, passData.settings.surfaceData == DecalSurfaceData.Albedo);
            cmd.SetKeyword(ShaderGlobalKeywords.DBufferMRT2, passData.settings.surfaceData == DecalSurfaceData.AlbedoNormal);
            cmd.SetKeyword(ShaderGlobalKeywords.DBufferMRT3, passData.settings.surfaceData == DecalSurfaceData.AlbedoNormalMAOS);
            cmd.SetKeyword(ShaderGlobalKeywords.DecalLayers, passData.decalLayers);

            // Execute draw system
            passData.drawSystem.Execute(cmd);
            cmd.DrawRendererList(passData.rendererList);
        }
    }
}

