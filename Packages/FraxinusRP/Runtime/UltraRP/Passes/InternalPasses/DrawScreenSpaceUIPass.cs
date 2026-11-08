using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class DrawScreenSpaceUIPass : BuiltinPass
    {

        public static void ConfigureColorDescriptor(ref RenderTextureDescriptor descriptor, int cameraWidth, int cameraHeight)
        {
            descriptor.graphicsFormat = GraphicsFormat.R8G8B8A8_SRGB;
            descriptor.depthStencilFormat = GraphicsFormat.None;
            descriptor.width = cameraWidth;
            descriptor.height = cameraHeight;
        }

        public static void ConfigureDepthDescriptor(ref RenderTextureDescriptor descriptor, GraphicsFormat depthStencilFormat, int cameraWidth, int cameraHeight)
        {
            descriptor.graphicsFormat = GraphicsFormat.None;
            descriptor.depthStencilFormat = depthStencilFormat;
            descriptor.width = cameraWidth;
            descriptor.height = cameraHeight;
        }

        private static void ExecutePass(RasterCommandBuffer commandBuffer, PassData passData, RendererList rendererList)
        {
            commandBuffer.DrawRendererList(rendererList);
        }

        private static void ExecutePass(UnsafeCommandBuffer commandBuffer, UnsafePassData passData, RendererList rendererList)
        {
            commandBuffer.DrawRendererList(rendererList);
        }

        protected override void OnDispose()
        {
            // No resources to dispose
        }

        protected override void OnCameraCleanup(CommandBuffer cmd)
        {
            // No resources to cleanup
        }

#if !UNITY_2023_1_OR_NEWER && !UNITY_6000_0_OR_NEWER
        private class LegacyPassData
        {
            internal Camera camera;
            internal TextureHandle colorTarget;
            internal TextureHandle depthTarget;
        }
#endif

        // RenderGraph path
        private class PassData
        {
            internal RendererListHandle rendererList;
        }

        // Specific to RG cases which have to go through Unsafe commands
        private class UnsafePassData
        {
            internal RendererListHandle rendererList;
            internal TextureHandle colorTarget;
        }

        public void RenderOffscreen(RenderGraph renderGraph, ContextContainer frameData, GraphicsFormat depthStencilFormat, out TextureHandle output)
        {
            CameraData cameraData = frameData.Get<CameraData>();
            
            // Create color texture descriptor
            RenderTextureDescriptor colorDescriptor = cameraData.cameraTargetDescriptor;
            ConfigureColorDescriptor(ref colorDescriptor, cameraData.pixelWidth, cameraData.pixelHeight);
            output = RenderingUtils.CreateRenderGraphTexture(renderGraph, colorDescriptor, "_OverlayUITexture", true);
            
            // Create depth texture descriptor
            RenderTextureDescriptor depthDescriptor = cameraData.cameraTargetDescriptor;
            ConfigureDepthDescriptor(ref depthDescriptor, depthStencilFormat, cameraData.pixelWidth, cameraData.pixelHeight);
            TextureHandle depthBuffer = RenderingUtils.CreateRenderGraphTexture(renderGraph, depthDescriptor, "_OverlayUITexture_Depth", false);

#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
            // Render UIToolkit and uGUI overlays
            // Note: We only render UIToolkit/uGUI here (UISubset.UIToolkit_UGUI), not IMGUI
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Draw Screen Space UIToolkit/uGUI - Offscreen", out var passData, _profilingSampler))
            {
                // UIToolkit/uGUI pass accept custom shaders, we need to make sure we use all global textures
                builder.UseAllGlobalTextures(true);

                builder.SetRenderAttachment(output, 0);

                // Use UISubset.UIToolkit_UGUI to only render UIToolkit and uGUI, not IMGUI
                passData.rendererList = renderGraph.CreateUIOverlayRendererList(cameraData.camera, UISubset.UIToolkit_UGUI);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderAttachmentDepth(depthBuffer, AccessFlags.ReadWrite);

                if (output.IsValid())
                    builder.SetGlobalTextureAfterPass(output, ShaderPropertyId.overlayUITexture);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    ExecutePass(context.cmd, data, data.rendererList);
                });
            }

            // Render IMGUI overlay and software cursor in a UnsafePass
            // Doing so allow us to safely cover cases when graphics commands called through onGUI() in user scripts are not supported by RenderPass API
            // Besides, Vulkan backend doesn't support SetSRGWrite() in RenderPass API and we have some of them at IMGUI levels
            // Note, these specific UI calls doesn't need depth buffer unlike UIToolkit/uGUI
            using (var builder = renderGraph.AddUnsafePass<UnsafePassData>("Draw Screen Space IMGUI/SoftwareCursor - Offscreen", out var passData, _profilingSampler))
            {
                passData.colorTarget = output;
                builder.UseTexture(output, AccessFlags.Write);

                // Use UISubset.LowLevel to only render IMGUI and software cursor
                passData.rendererList = renderGraph.CreateUIOverlayRendererList(cameraData.camera, UISubset.LowLevel);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((UnsafePassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.colorTarget);
                    ExecutePass(context.cmd, data, data.rendererList);
                });
            }
#else
            // Unity 2022 has DrawUIOverlay, but cannot create the split UI
            // renderer lists. Submit the complete overlay after binding its
            // graph targets through the Core command-buffer bridge.
            using (var builder = renderGraph.AddUnsafePass<LegacyPassData>(
                       "Draw Screen Space UI - Offscreen (Unity 2022)", out var passData, _profilingSampler))
            {
                passData.camera = cameraData.camera;
                passData.colorTarget = output;
                passData.depthTarget = depthBuffer;
                builder.UseTexture(output, AccessFlags.Write);
                builder.UseTexture(depthBuffer, AccessFlags.ReadWrite);
                builder.UseAllGlobalTextures(true);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((LegacyPassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.colorTarget, data.depthTarget);
                    context.cmd.ClearRenderTarget(false, true, Color.clear);
                    context.cmd.DrawUIOverlay(data.camera);
                    context.cmd.SetGlobalTexture(ShaderPropertyId.overlayUITexture, data.colorTarget);
                });
            }
#endif
        }

        public void RenderOverlay(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle colorBuffer, in TextureHandle depthBuffer)
        {
            CameraData cameraData = frameData.Get<CameraData>();

#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
            // Render UIToolkit and uGUI overlays
            // Note: We only render UIToolkit/uGUI here (UISubset.UIToolkit_UGUI), not IMGUI
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Draw UIToolkit/uGUI Overlay", out var passData, _profilingSampler))
            {
                // UIToolkit/uGUI pass accept custom shaders, we need to make sure we use all global textures
                builder.UseAllGlobalTextures(true);

                builder.SetRenderAttachment(colorBuffer, 0);
                builder.SetRenderAttachmentDepth(depthBuffer, AccessFlags.ReadWrite);

                // Use UISubset.UIToolkit_UGUI to only render UIToolkit and uGUI, not IMGUI
                passData.rendererList = renderGraph.CreateUIOverlayRendererList(cameraData.camera, UISubset.UIToolkit_UGUI);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    ExecutePass(context.cmd, data, data.rendererList);
                });
            }

            // Render IMGUI overlay and software cursor in a UnsafePass
            // Doing so allow us to safely cover cases when graphics commands called through onGUI() in user scripts are not supported by RenderPass API
            // Besides, Vulkan backend doesn't support SetSRGWrite() in RenderPass API and we have some of them at IMGUI levels
            // Note, these specific UI calls doesn't need depth buffer unlike UIToolkit/uGUI
            using (var builder = renderGraph.AddUnsafePass<UnsafePassData>("Draw IMGUI/SoftwareCursor Overlay", out var passData, _profilingSampler))
            {
                passData.colorTarget = colorBuffer;
                builder.UseTexture(colorBuffer, AccessFlags.Write);

                // Use UISubset.LowLevel to only render IMGUI and software cursor
                passData.rendererList = renderGraph.CreateUIOverlayRendererList(cameraData.camera, UISubset.LowLevel);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((UnsafePassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.colorTarget);
                    ExecutePass(context.cmd, data, data.rendererList);
                });
            }
#else
            using (var builder = renderGraph.AddUnsafePass<LegacyPassData>(
                       "Draw Screen Space UI Overlay (Unity 2022)", out var passData, _profilingSampler))
            {
                passData.camera = cameraData.camera;
                passData.colorTarget = colorBuffer;
                passData.depthTarget = depthBuffer;
                builder.UseTexture(colorBuffer, AccessFlags.Write);
                builder.UseTexture(depthBuffer, AccessFlags.ReadWrite);
                builder.UseAllGlobalTextures(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((LegacyPassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.colorTarget, data.depthTarget);
                    context.cmd.DrawUIOverlay(data.camera);
                });
            }
#endif
        }
    }
}
