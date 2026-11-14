using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{

    public class RenderDrivenPass : ScriptableRenderPassNode
    {
        private enum DepthCopySchedule
        {
            // In some cases, we can render depth directly to the depth texture during the depth prepass
            DuringPrepass,

            AfterPrepass,
            AfterGBuffer,
            AfterOpaques,
            None
        }

        private static class DrivenConstantBuffer
        {
            public static readonly List<ShaderTagId> depthNormalsOnly = new List<ShaderTagId> { new ShaderTagId("DepthNormalsOnly") };
            public static readonly List<ShaderTagId> fullDepthNormals = new List<ShaderTagId> { new ShaderTagId("DepthNormals"), new ShaderTagId("DepthNormalsOnly") };
        }

        private class PassData
        {
            internal TextureHandle color;
            internal TextureHandle depth;
        }

        [Output("Camera Target", allowMultiple = true)]
        internal CameraTargetBufferNodeParameter outCameraTarget;

        protected override void Enable()
        {
            base.Enable();
        }

        protected override void Disable()
        {
            base.Disable();
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            ResourceData resourceData       = frameData.Get<ResourceData>();
            RenderingData renderingData     = frameData.Get<RenderingData>();
            CameraData cameraData           = frameData.Get<CameraData>();
            LightData lightData             = frameData.Get<LightData>();

            var renderer = cameraData.renderer;
            var deferredLights = renderer.GetModule<DeferredLights>();
            var gBufferPass = renderer.GetModule<GBufferPass>();

            if (!renderGraph.nativeRenderPassesEnabled)
            {
                RTClearFlags clearFlags = (RTClearFlags)GetCameraClearFlag(cameraData);
                if (clearFlags != RTClearFlags.None)
                    ClearTargetsPass.Render(renderGraph, resourceData.activeColorTexture, resourceData.activeDepthTexture, clearFlags, cameraData.backgroundColor);
            }

            bool requiresDepthPrepass = renderingData.requiresDepthPrepass;
            bool isDepthOnlyPrepass = requiresDepthPrepass && !renderingData.renderPassInputs.requiresNormalsTexture;
            bool isDepthNormalPrepass = requiresDepthPrepass && renderingData.renderPassInputs.requiresNormalsTexture;
            bool allowPartialDepthNormals = AllowPartialDepthNormalsPrepass(renderingData.hasDeferred) &&
                !renderingData.renderPassInputs.requiresFullDepthPrepass;

            bool hasFullPrepass = isDepthOnlyPrepass || (isDepthNormalPrepass && !allowPartialDepthNormals);

            deferredLights.ConfigureLightingPath(renderGraph.nativeRenderPassesEnabled,
                renderingData.deferredLightingMode, renderingData.clusterLightingMode,
                renderingData.renderPassInputs.disableFramebufferFetch);
            TextureCopySchedules copySchedules = CalculateTextureCopySchedules(renderer, cameraData, renderingData.renderPassInputs,
                renderingData.hasDeferred, requiresDepthPrepass, hasFullPrepass, deferredLights.useFramebufferFetch);

            bool needsOccluderUpdate = cameraData.useGPUOcclusionCulling && (renderingData.opaqueLayerMask != 0);
            if (requiresDepthPrepass)
            {
                // If we're in deferred mode, prepasses always render directly to the depth attachment rather than the camera depth texture.
                // In non-deferred mode, we only render to the depth attachment directly when depth priming is enabled and we're starting with an empty depth buffer.
                bool isDepthPrimingTarget = (renderer.useDepthPriming && (cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth));
                bool renderToAttachment = (renderingData.hasDeferred || isDepthPrimingTarget);
                TextureHandle depthTarget = renderToAttachment ? resourceData.activeDepthTexture : resourceData.cameraDepthTexture;

                var passCount = needsOccluderUpdate ? 2 : 1;
                for (int passIndex = 0; passIndex < passCount; ++passIndex)
                {
                    uint batchLayerMask = uint.MaxValue;
                    if (needsOccluderUpdate)
                    {
                        OcclusionTest occlusionTest = (passIndex == 0) ? OcclusionTest.TestAll : OcclusionTest.TestCulled;
                        UltraRenderer.InstanceOcclusionTest(renderGraph, cameraData, occlusionTest);
#if UNITY_6000_0_OR_NEWER
                        batchLayerMask = occlusionTest.GetBatchLayerMask();
#endif
                    }

                    // The prepasses are executed multiple times when GRD occlusion is active.
                    // We only want to set global textures after all executions are complete.
                    bool isLastPass = (passIndex == (passCount - 1));

                    // When we render to the depth attachment, a copy must happen later to populate the camera depth texture and the copy will handle setting globals.
                    // If we're rendering to the camera depth texture, we can set the globals immediately.
                    bool setGlobalDepth = isLastPass && !renderToAttachment;

                    // There's no special copy logic for the camera normals texture, so we can set the global as long as we're not performing a partial prepass.
                    // In the case of a partial prepass, the global will be set later by the gbuffer pass once it completes the data in the texture.
                    bool setGlobalTextures = isLastPass && (!renderingData.hasDeferred || hasFullPrepass);

                    if (isDepthNormalPrepass)
                    {
                        DepthNormalOnlyPass depthNormalPrepass = renderer.GetModule<DepthNormalOnlyPass>();
                        if (allowPartialDepthNormals)
                        {
                            // Configure GBufferPass for partial prepass
                            // GBufferPassNode will also check this condition when used independently
                            gBufferPass.gbufferDepthPriming = false;
                            depthNormalPrepass.shaderTagIds = DrivenConstantBuffer.depthNormalsOnly;
                        }
                        else
                            depthNormalPrepass.shaderTagIds = DrivenConstantBuffer.fullDepthNormals;

                        TextureHandle renderingLayersTexture = resourceData.renderingLayersTexture;
                        TextureHandle normalsTexture = resourceData.cameraNormalsTexture;
                        depthNormalPrepass.layerMask = renderingData.opaqueLayerMask;
                        depthNormalPrepass.Render(renderGraph, frameData, normalsTexture, depthTarget, renderingLayersTexture, batchLayerMask, setGlobalDepth, setGlobalTextures);
                    }
                    else
                    {
                        DepthOnlyPass depthOnlyPass = renderer.GetModule<DepthOnlyPass>();

                        depthOnlyPass.layerMask = renderingData.opaqueLayerMask;
                        depthOnlyPass.Render(renderGraph, frameData, ref depthTarget, batchLayerMask, setGlobalDepth);

                        if (renderer.renderinglayerSetting.requiresRenderingLayer)
                            renderer.SetRenderingLayersGlobalTextures(renderGraph);
                    }

                    if (needsOccluderUpdate)
                    {
                        // first pass: make current frame intermediate depth pyramid
                        // second pass: make current frame final depth pyramid, set occlusion test results for later passes
                        UltraRenderer.UpdateInstanceOccluders(renderGraph, cameraData, depthTarget);
                        if (passIndex != 0)
                            UltraRenderer.InstanceOcclusionTest(renderGraph, cameraData, OcclusionTest.TestAll);
                    }
                }
                needsOccluderUpdate = false;
            }
            renderingData.needsOccluderUpdate = needsOccluderUpdate;

            // After the prepass completes, we should copy depth if necessary and also render motion vectors. (they're expected to be available whenever depth is)
            // In the case where depth is rendered as part of the prepass and no copy is necessary, we still need to render motion vectors here to ensure they're available
            // with depth before any user passes are executed.
            if (copySchedules.depth == DepthCopySchedule.AfterPrepass)
                ExecuteScheduledDepthCopyWithMotion(renderGraph, frameData, renderer, renderingData.renderPassInputs.requiresMotionVectors);
            else if ((copySchedules.depth == DepthCopySchedule.DuringPrepass) && renderingData.renderPassInputs.requiresMotionVectors)
                RenderMotionVectors(renderGraph, frameData, renderer);

            if (renderingData.hasDeferred)
            {
                deferredLights.hasNormalPrepass = isDepthNormalPrepass;
                deferredLights.hasDepthPrepass = requiresDepthPrepass;
                deferredLights.ResolveMixedLightingMode(lightData);
                deferredLights.isOverlay = cameraData.renderType == CameraRenderType.Overlay;

                bool setGlobalTextures = isDepthNormalPrepass && !hasFullPrepass;

                gBufferPass.setGlobalTextures = setGlobalTextures;
                gBufferPass.layerMask = renderingData.opaqueLayerMask;

                // Texture-based deferred lighting needs a depth copy. Only raster
                // framebuffer fetch can read depth from the extra GBuffer slice.
                bool copyAfterGBuffer = copySchedules.depth == DepthCopySchedule.AfterGBuffer;
                var gbufferDepthCopyPass = renderer.GetModule<GBufferDepthCopyAndMotionPass>();
                gbufferDepthCopyPass.ConfigExecuteValid(copyAfterGBuffer,
                    copyAfterGBuffer && renderingData.renderPassInputs.requiresMotionVectors);
            }
 
            var passWithDepthCopyAndMotion = renderer.GetModule<CustomPassWithDepthCopyAndMotion>();
            // Configure for AfterOpaques scenario
            // AfterGBuffer is now handled by GBufferDepthCopyAndMotionPass
            passWithDepthCopyAndMotion.ConfigExecuteValid(
                copySchedules.depth == DepthCopySchedule.AfterOpaques,
                copySchedules.depth == DepthCopySchedule.AfterOpaques && renderingData.renderPassInputs.requiresMotionVectors);

            if (renderingData.hasDeferredCluster)
                deferredLights.RenderClusterLights(renderGraph, frameData);

            ConfigurePortValue(outCameraTarget, new CameraTargetBuffer(resourceData.activeColorTexture, resourceData.activeDepthTexture));
        }

        private bool AllowPartialDepthNormalsPrepass(bool isDeferred)
        {
            return isDeferred;
        }

        static ClearFlag GetCameraClearFlag(CameraData cameraData)
        {
            var cameraClearFlags = cameraData.camera.clearFlags;

            // UltraRP doesn't support CameraClearFlags.DepthOnly and CameraClearFlags.Nothing.
            // CameraClearFlags.DepthOnly has the same effect of CameraClearFlags.SolidColor
            // CameraClearFlags.Nothing clears Depth on PC/Desktop and in mobile it clears both
            // depth and color.
            // CameraClearFlags.Skybox clears depth only.

            // Implementation details:
            // Camera clear flags are used to initialize the attachments on the first render pass.
            // ClearFlag is used together with Tile Load action to figure out how to clear the camera render target.
            // In Tile Based GPUs ClearFlag.Depth + RenderBufferLoadAction.DontCare becomes DontCare load action.

            // RenderBufferLoadAction.DontCare in PC/Desktop behaves as not clearing screen
            // RenderBufferLoadAction.DontCare in Vulkan/Metal behaves as DontCare load action
            // RenderBufferLoadAction.DontCare in GLES behaves as glInvalidateBuffer

            // Overlay cameras composite on top of previous ones. They don't clear color.
            // For overlay cameras we check if depth should be cleared on not.
            if (cameraData.renderType == CameraRenderType.Overlay)
                return (cameraData.clearDepth) ? ClearFlag.DepthStencil : ClearFlag.None;

            // Certain debug modes (e.g. wireframe/overdraw modes) require that we override clear flags and clear everything.
            var debugHandler = cameraData.renderer.debugHandler;
            if (debugHandler != null && debugHandler.IsActiveForCamera(cameraData.isPreviewCamera) && debugHandler.IsScreenClearNeeded)
                return ClearFlag.All;

            // XRTODO: remove once we have visible area of occlusion mesh available
            if (cameraClearFlags == CameraClearFlags.Skybox && RenderSettings.skybox != null && cameraData.postProcessEnabled)
                return ClearFlag.All;

            if ((cameraClearFlags == CameraClearFlags.Skybox && RenderSettings.skybox != null) ||
                cameraClearFlags == CameraClearFlags.Nothing)
                return ClearFlag.DepthStencil;

            return ClearFlag.All;
        }

        private struct TextureCopySchedules
        {
            internal DepthCopySchedule depth;
        }

        private TextureCopySchedules CalculateTextureCopySchedules(ScriptableRenderer renderer, CameraData cameraData, RenderPassInputSummary renderPassInputs,
            bool isDeferred, bool requiresDepthPrepass, bool hasFullPrepass, bool useFramebufferFetch)
        {
            // Determine if we read the contents of the depth texture at some point in the frame
            bool depthTextureUsed = (cameraData.requiresDepthTexture || renderPassInputs.requiresDepthTexture) ||
                                 renderer.DebugHandlerRequireDepthPass(cameraData) ||
                                 (isDeferred && !useFramebufferFetch);
            // Texture-based deferred lighting reconstructs positions from camera
            // depth even when the camera and post effects do not request it.


            // Assume the depth texture is unused and no copy is needed until we determine otherwise
            DepthCopySchedule depth = DepthCopySchedule.None;

            // If the depth texture is read during the frame, determine when the copy should occur
            if (depthTextureUsed)
            {
                // In forward, the depth prepass typically writes directly to the depth texture so no copy is needed. However, when depth priming is enabled,
                // the prepass targets the depth attachment instead, so we still have to perform a depth copy to populate the depth texture.
                // In deferred, the depth prepass writes to the depth attachment so a copy must happen later in the frame.
                bool depthTextureRequiresCopy = (isDeferred || (!requiresDepthPrepass || renderer.useDepthPriming));

                depth = depthTextureRequiresCopy ? CalculateDepthCopySchedule(isDeferred, hasFullPrepass)
                                 : DepthCopySchedule.DuringPrepass;
            }

            TextureCopySchedules schedules;

            schedules.depth = depth;

            return schedules;
        }

        private DepthCopySchedule CalculateDepthCopySchedule(bool isDeferred, bool hasFullPrepass)
        {
            DepthCopySchedule schedule;
            if (hasFullPrepass)
            {
                // If we have a full prepass, we can copy depth immediately after since a full prepass guarantees complete depth data.
                schedule = DepthCopySchedule.AfterPrepass;
            }
            else
            {
                // Without a full prepass, complete depth is available after GBuffer in deferred or after opaque draws in forward.
                schedule = isDeferred ? DepthCopySchedule.AfterGBuffer : DepthCopySchedule.AfterOpaques;
            }

            return schedule;
        }

        private void CopyDepthToDepthTexture(RenderGraph renderGraph, ContextContainer frameData, ScriptableRenderer renderer)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();

            var copyDepthPass = renderer.GetModule<DepthCopyPass>();
            copyDepthPass.Render(renderGraph, frameData, resourceData.cameraDepthTexture, resourceData.activeDepthTexture, true);
        }

        private void RenderMotionVectors(RenderGraph renderGraph, ContextContainer frameData, ScriptableRenderer renderer)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();

            var motionVectorPass = renderer.GetModule<MotionVectorRenderPass>();
            motionVectorPass.Render(renderGraph, frameData, resourceData.cameraDepthTexture, resourceData.motionVectorColor, resourceData.motionVectorDepth);
        }

        private void ExecuteScheduledDepthCopyWithMotion(RenderGraph renderGraph, ContextContainer frameData, ScriptableRenderer renderer, bool renderMotionVectors)
        {
            CopyDepthToDepthTexture(renderGraph, frameData, renderer);

            if (renderMotionVectors)
                RenderMotionVectors(renderGraph, frameData, renderer);
        }

        class ClearTargetsPass
        {
            private static ProfilingSampler s_clearProfilingSampler = new ProfilingSampler("Clear Targets");

            private class PassData
            {
                internal TextureHandle color;
                internal TextureHandle depth;

                internal RTClearFlags clearFlags;
                internal Color clearColor;
            }

            internal static void Render(RenderGraph graph, TextureHandle colorHandle, TextureHandle depthHandle, CameraData cameraData)
            {
                RTClearFlags clearFlags = RTClearFlags.None;

                if (cameraData.renderType == CameraRenderType.Base)
                    clearFlags = RTClearFlags.All;
                else if (cameraData.clearDepth)
                    clearFlags = RTClearFlags.Depth;

                if (clearFlags != RTClearFlags.None)
                    Render(graph, colorHandle, depthHandle, clearFlags, cameraData.backgroundColor);
            }

            internal static void Render(RenderGraph graph, TextureHandle colorHandle, TextureHandle depthHandle, RTClearFlags clearFlags, Color clearColor)
            {
                using (var builder = graph.AddRasterRenderPass<PassData>("Clear Targets Pass", out var passData, s_clearProfilingSampler))
                {
                    if (colorHandle.IsValid())
                    {
                        passData.color = colorHandle;
                        builder.SetRenderAttachment(colorHandle, 0, AccessFlags.Write);
                    }

                    if (depthHandle.IsValid())
                    {
                        passData.depth = depthHandle;
                        builder.SetRenderAttachmentDepth(depthHandle, AccessFlags.Write);
                    }

                    passData.clearFlags = clearFlags;
                    passData.clearColor = clearColor;

                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                    {
                        context.cmd.ClearRenderTarget(data.clearFlags, data.clearColor, 1, 0);
                    });
                }
            }
        }
    }
}
