using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    public class SMAAPass : BasePostPass
    {
        static class ShaderConstants
        {
            public static readonly int _StencilRef = Shader.PropertyToID("_StencilRef");
            public static readonly int _StencilMask = Shader.PropertyToID("_StencilMask");

            public static readonly int _Metrics = Shader.PropertyToID("_Metrics");
            public static readonly int _AreaTexture = Shader.PropertyToID("_AreaTexture");
            public static readonly int _SearchTexture = Shader.PropertyToID("_SearchTexture");
            public static readonly int _EdgeTexture = Shader.PropertyToID("_EdgeTexture");
            public static readonly int _BlendTexture = Shader.PropertyToID("_BlendTexture");
        }

        private class SMAASetupPassData
        {
            internal Vector4 metrics;
            internal Texture2D areaTexture;
            internal Texture2D searchTexture;
            internal float stencilRef;
            internal float stencilMask;
            internal AntialiasingQuality antialiasingQuality;
            internal Material material;
        }

        private class SMAAPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal TextureHandle depthStencilTexture;
            internal TextureHandle blendTexture;
            internal Material material;
        }

        private GraphicsFormat _smssEdgeFormat;

        public SMAAPass() : base()
        {
            if (RenderingUtils.SupportsGraphicsFormat(GraphicsFormat.R8G8_UNorm, GraphicsFormatUsage.Render) && SystemInfo.graphicsDeviceVendor.ToLowerInvariant().Contains("arm"))
                _smssEdgeFormat = GraphicsFormat.R8G8_UNorm;
            else
                _smssEdgeFormat = GraphicsFormat.R8G8B8A8_UNorm;
        }

        public override bool Setup(bool enable, VolumeStack stack)
        { 
            return enable;
        }

        public override TextureHandle Render(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle activeCameraColor)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();

            bool useSubPixelMorpAA = cameraData.antialiasing == AntialiasingMode.SubpixelMorphologicalAntiAliasing;

            TextureHandle currentSource = activeCameraColor;
            if (useSubPixelMorpAA)
            {
                RenderSMAA(renderGraph, resourceData, cameraData.antialiasingQuality, in currentSource, out var SMAATarget);
                currentSource = SMAATarget;
            }

            return currentSource;
        }

        protected override void OnDispose()
        {

        }

        private void RenderSMAA(RenderGraph renderGraph, ResourceData resourceData, AntialiasingQuality antialiasingQuality, in TextureHandle source, out TextureHandle SMAATarget)
        {
            var descriptor = _postProcessPass.descriptor;
            var graph = _postProcessPass.graph;
            var data = _postProcessPass.data;

            var desc = PostProcessPass.GetCompatibleDescriptor(descriptor,
                descriptor.width,
                descriptor.height,
                descriptor.graphicsFormat,
                GraphicsFormat.None);
            SMAATarget = UltraRenderer.CreateRenderGraphTexture(renderGraph, desc, "_SMAATarget", true, FilterMode.Bilinear);

            var edgeTextureDesc = PostProcessPass.GetCompatibleDescriptor(descriptor,
                descriptor.width,
                descriptor.height,
                _smssEdgeFormat,
                GraphicsFormat.None);
            var edgeTexture = UltraRenderer.CreateRenderGraphTexture(renderGraph, edgeTextureDesc, "_EdgeStencilTexture", true, FilterMode.Bilinear);

            var edgeTextureStencilDesc = PostProcessPass.GetCompatibleDescriptor(descriptor,
                descriptor.width,
                descriptor.height,
                GraphicsFormat.None,
                GraphicsFormatUtility.GetDepthStencilFormat(24, 8));
            var edgeTextureStencil = UltraRenderer.CreateRenderGraphTexture(renderGraph, edgeTextureStencilDesc, "_EdgeTexture", true, FilterMode.Bilinear);

            var blendTextureDesc = PostProcessPass.GetCompatibleDescriptor(descriptor,
                descriptor.width,
                descriptor.height,
                GraphicsFormat.R8G8B8A8_UNorm,
                GraphicsFormat.None);
            var blendTexture = UltraRenderer.CreateRenderGraphTexture(renderGraph, blendTextureDesc, "_BlendTexture", true, FilterMode.Point);

            // Anti-aliasing
            var material = graph.materialLib.GetMaterial("subpixelMorphologicalAntialiasingPS");

            using (var builder = renderGraph.AddRasterRenderPass<SMAASetupPassData>("SMAA Material Setup", out var passData, ProfilingSampler.Get(UltraRPProfileId.RG_SMAAMaterialSetup)))
            {
                const int kStencilBit = 64;
                // TODO RENDERGRAPH: handle dynamic scaling
                passData.metrics = new Vector4(1f / descriptor.width, 1f / descriptor.height, descriptor.width, descriptor.height);
                passData.areaTexture = data.textures.smaaAreaTex;
                passData.searchTexture = data.textures.smaaSearchTex;
                passData.stencilRef = (float)kStencilBit;
                passData.stencilMask = (float)kStencilBit;
                passData.antialiasingQuality = antialiasingQuality;
                passData.material = material;

                builder.SetRenderFunc(static (SMAASetupPassData data, RasterGraphContext context) =>
                {
                    // Globals
                    data.material.SetVector(ShaderConstants._Metrics, data.metrics);
                    data.material.SetTexture(ShaderConstants._AreaTexture, data.areaTexture);
                    data.material.SetTexture(ShaderConstants._SearchTexture, data.searchTexture);
                    data.material.SetFloat(ShaderConstants._StencilRef, data.stencilRef);
                    data.material.SetFloat(ShaderConstants._StencilMask, data.stencilMask);

                    // Quality presets
                    data.material.shaderKeywords = null;

                    switch (data.antialiasingQuality)
                    {
                        case AntialiasingQuality.Low:
                            data.material.EnableKeyword(ShaderKeywordStrings.SmaaLow);
                            break;
                        case AntialiasingQuality.Medium:
                            data.material.EnableKeyword(ShaderKeywordStrings.SmaaMedium);
                            break;
                        case AntialiasingQuality.High:
                            data.material.EnableKeyword(ShaderKeywordStrings.SmaaHigh);
                            break;
                    }
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<SMAAPassData>("SMAA Edge Detection", out var passData, ProfilingSampler.Get(UltraRPProfileId.RG_SMAAEdgeDetection)))
            {
                passData.destinationTexture = edgeTexture;
                builder.SetRenderAttachment(edgeTexture, 0, AccessFlags.Write);
                passData.depthStencilTexture = edgeTextureStencil;
                builder.SetRenderAttachmentDepth(edgeTextureStencil, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(resourceData.cameraDepth, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc(static (SMAAPassData data, RasterGraphContext context) =>
                {
                    var SMAAMaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Pass 1: Edge detection
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, SMAAMaterial, 0);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<SMAAPassData>("SMAA Blend weights", out var passData, ProfilingSampler.Get(UltraRPProfileId.RG_SMAABlendWeight)))
            {
                passData.destinationTexture = blendTexture;
                builder.SetRenderAttachment(blendTexture, 0, AccessFlags.Write);
                passData.depthStencilTexture = edgeTextureStencil;
                builder.SetRenderAttachmentDepth(edgeTextureStencil, AccessFlags.Read);
                passData.sourceTexture = edgeTexture;
                builder.UseTexture(edgeTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc(static (SMAAPassData data, RasterGraphContext context) =>
                {
                    var SMAAMaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Pass 2: Blend weights
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, SMAAMaterial, 1);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<SMAAPassData>("SMAA Neighborhood blending", out var passData, ProfilingSampler.Get(UltraRPProfileId.RG_SMAANeighborhoodBlend)))
            {
                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = SMAATarget;
                builder.SetRenderAttachment(SMAATarget, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.blendTexture = blendTexture;
                builder.UseTexture(blendTexture, AccessFlags.Read);
                passData.material = material;

                builder.SetRenderFunc(static (SMAAPassData data, RasterGraphContext context) =>
                {
                    var SMAAMaterial = data.material;
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    // Pass 3: Neighborhood blending
                    SMAAMaterial.SetTexture(ShaderConstants._BlendTexture, data.blendTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, SMAAMaterial, 2);
                });
            }
        }
    }
}
