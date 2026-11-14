using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    // Uses the material's forward vertex, clipping and ZWrite state, including transparents.
    // DepthOnly passes force ZWrite On and must not be included in this draw list.
    internal static class OffscreenDepthPass
    {
        static readonly List<ShaderTagId> s_Tags = new()
        {
            new ShaderTagId("UltraForward"), new ShaderTagId("UltraForwardOnly"),
            new ShaderTagId("SRPDefaultUnlit")
        };
        static readonly int s_DrawObjectPassData = Shader.PropertyToID("_DrawObjectPassData");
        static readonly ProfilingSampler s_Sampler = new("Offscreen Depth");

        sealed class PassData
        {
            internal RendererListHandle opaque, transparent;
            internal Rect viewport;
            internal bool clearDepth, alphaToMask;
            internal ForwardPlusFrameData forwardPlus;
        }

        internal static void Render(RenderGraph graph, ContextContainer frameData, TextureHandle depth,
            LayerMask opaqueLayers, LayerMask transparentLayers,
            TextureHandle mainShadows, TextureHandle additionalShadows)
        {
            var camera = frameData.Get<CameraData>();
            var rendering = frameData.Get<RenderingData>();
            var lights = frameData.Get<LightData>();
            using (var builder = graph.AddRasterRenderPass<PassData>("Offscreen Depth", out var data, s_Sampler))
            {
                data.forwardPlus = ForwardLights.UseLightingResources(builder, frameData);
                if (mainShadows.IsValid())
                    builder.UseTexture(mainShadows, AccessFlags.Read);
                if (additionalShadows.IsValid())
                    builder.UseTexture(additionalShadows, AccessFlags.Read);
                var drawing = RenderingUtils.CreateDrawingSettings(s_Tags, rendering, camera, lights, camera.defaultOpaqueSortFlags);
                data.opaque = graph.CreateRendererList(new RendererListParams(rendering.cullResults, drawing,
                    new FilteringSettings(RenderQueueRange.opaque, opaqueLayers)));
                var sorting = drawing.sortingSettings;
                sorting.criteria = SortingCriteria.CommonTransparent;
                drawing.sortingSettings = sorting;
                data.transparent = graph.CreateRendererList(new RendererListParams(rendering.cullResults, drawing,
                    new FilteringSettings(RenderQueueRange.transparent, transparentLayers)));
                data.viewport = camera.pixelRect;
                data.clearDepth = camera.camera.clearFlags != CameraClearFlags.Nothing;
                data.alphaToMask = camera.targetTexture.antiAliasing > 1;
                builder.UseRendererList(data.opaque);
                builder.UseRendererList(data.transparent);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false); // The externally owned target is the camera's output.
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
                {
                    ForwardLights.BindLightLists(context.cmd, pass.forwardPlus);
                    if (pass.clearDepth)
                        context.cmd.ClearRenderTarget(RTClearFlags.DepthStencil, Color.clear, 1, 0);
                    context.cmd.SetViewport(pass.viewport);
                    context.cmd.DisableShaderKeyword("_WRITE_RENDERING_LAYERS");
                    context.cmd.SetGlobalFloat(ShaderPropertyId.alphaToMaskAvailable, pass.alphaToMask ? 1 : 0);
                    context.cmd.SetGlobalVector(s_DrawObjectPassData, new Vector4(0, 0, 0, 1));
                    context.cmd.DrawRendererList(pass.opaque);
                    context.cmd.SetGlobalFloat(ShaderPropertyId.alphaToMaskAvailable, 0);
                    context.cmd.SetGlobalVector(s_DrawObjectPassData, Vector4.zero);
                    context.cmd.DrawRendererList(pass.transparent);
                });
            }
        }
    }
}
