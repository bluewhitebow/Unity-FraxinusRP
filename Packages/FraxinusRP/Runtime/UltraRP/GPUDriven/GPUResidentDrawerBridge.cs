using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    internal static class GPUResidentDrawerBridge
    {
#if UNITY_6000_0_OR_NEWER
        internal static bool IsAvailable => true;
        internal static bool IsOcclusionCullingEnabled => GPUResidentDrawer.IsInstanceOcclusionCullingEnabled();

        internal static void Reinitialize() => IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();
        internal static void ReinitializeIfNeeded() => GPUResidentDrawer.ReinitializeIfNeeded();

        internal static void PostCull(CommandBuffer commandBuffer)
        {
            GPUResidentDrawer.PostCullBeginCameraRendering(new RenderRequestBatcherContext { commandBuffer = commandBuffer });
        }

        internal static void UpdateOccluders(RenderGraph renderGraph, CameraData cameraData, TextureHandle depthTexture)
        {
            int scaledWidth = (int)(cameraData.pixelWidth * cameraData.renderScale);
            int scaledHeight = (int)(cameraData.pixelHeight * cameraData.renderScale);
            bool isSinglePassXR = false;
            var occluderParams = new OccluderParameters(cameraData.camera.GetInstanceID())
            {
                subviewCount = isSinglePassXR ? 2 : 1,
                depthTexture = depthTexture,
                depthSize = new Vector2Int(scaledWidth, scaledHeight),
                depthIsArray = isSinglePassXR,
            };
            Span<OccluderSubviewUpdate> occluderSubviewUpdates = stackalloc OccluderSubviewUpdate[occluderParams.subviewCount];
            for (int subviewIndex = 0; subviewIndex < occluderParams.subviewCount; ++subviewIndex)
            {
                var viewMatrix = cameraData.GetViewMatrix(subviewIndex);
                var projMatrix = cameraData.GetProjectionMatrix(subviewIndex);
                occluderSubviewUpdates[subviewIndex] = new OccluderSubviewUpdate(subviewIndex)
                {
                    depthSliceIndex = subviewIndex,
                    viewMatrix = viewMatrix,
                    invViewMatrix = viewMatrix.inverse,
                    gpuProjMatrix = GL.GetGPUProjectionMatrix(projMatrix, true),
                    viewOffsetWorldSpace = Vector3.zero,
                };
            }
            GPUResidentDrawer.UpdateInstanceOccluders(renderGraph, occluderParams, occluderSubviewUpdates);
        }

        internal static IDebugDisplaySettingsData CreateDebugSettings() => new DebugDisplayGPUResidentDrawer();

        internal static void DrawDebugOverlays(RenderGraph renderGraph, CameraData cameraData, TextureHandle colorTexture,
            IDebugDisplaySettingsData settings, int textureHeightPercent)
        {
            var debugSettings = (DebugDisplayGPUResidentDrawer)settings;
            GPUResidentDrawer.RenderDebugOcclusionTestOverlay(renderGraph, debugSettings, cameraData.camera.GetInstanceID(), colorTexture);

            float screenWidth = (int)(cameraData.pixelHeight * cameraData.renderScale);
            float screenHeight = (int)(cameraData.pixelHeight * cameraData.renderScale);
            float maxHeight = screenHeight * textureHeightPercent / 100.0f;
            GPUResidentDrawer.RenderDebugOccluderOverlay(renderGraph, debugSettings,
                new Vector2(0.25f * screenWidth, screenHeight - 1.5f * maxHeight), maxHeight, colorTexture);
        }
#else
        internal static bool IsAvailable => false;
        internal static bool IsOcclusionCullingEnabled => false;

        internal static void Reinitialize() { }
        internal static void ReinitializeIfNeeded() { }
        internal static void PostCull(CommandBuffer commandBuffer) { }
        internal static void UpdateOccluders(RenderGraph renderGraph, CameraData cameraData, TextureHandle depthTexture) { }
        internal static IDebugDisplaySettingsData CreateDebugSettings() => null;
        internal static void DrawDebugOverlays(RenderGraph renderGraph, CameraData cameraData, TextureHandle colorTexture,
            IDebugDisplaySettingsData settings, int textureHeightPercent) { }
#endif
    }
}
