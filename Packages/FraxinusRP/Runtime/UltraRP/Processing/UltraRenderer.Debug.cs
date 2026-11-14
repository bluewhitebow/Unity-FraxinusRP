using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public sealed partial class UltraRenderer
    {
        void CreateDebugTexture(RenderTextureDescriptor descriptor)
        {
            var debugTexDescriptor = descriptor;
            debugTexDescriptor.useMipMap = false;
            debugTexDescriptor.autoGenerateMips = false;
            debugTexDescriptor.bindMS = false;
            debugTexDescriptor.depthStencilFormat = GraphicsFormat.None;

            RenderingUtils.ReAllocateHandleIfNeeded(ref s_renderGraphDebugTextureHandle, debugTexDescriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_RenderingDebuggerTexture");
        }

        private Rect CalculateUVRect(CameraData cameraData, float width, float height)
        {
            float normalizedSizeX = width / cameraData.pixelWidth;
            float normalizedSizeY = height / cameraData.pixelHeight;

            return new Rect(1 - normalizedSizeX, 1 - normalizedSizeY, normalizedSizeX, normalizedSizeY);
        }

        private Rect CalculateUVRect(CameraData cameraData, int textureHeightPercent)
        {
            var relativeSize = Mathf.Clamp01(textureHeightPercent / 100f);
            var width = relativeSize * cameraData.pixelWidth;
            var height = relativeSize * cameraData.pixelHeight;

            return CalculateUVRect(cameraData, width, height);
        }

        private void CorrectForTextureAspectRatio(ref float width, ref float height, float sourceWidth, float sourceHeight)
        {
            if (sourceWidth != 0 && sourceHeight != 0)
            {
                // Ensure that atlas is not stretched, but doesn't take up more than the percentage in any dimension.
                var targetWidth = height * sourceWidth / sourceHeight;
                if (targetWidth > width)
                {
                    height = width * sourceHeight / sourceWidth;
                }
                else
                {
                    width = targetWidth;
                }
            }
        }


        public override void SetupRenderGraphFinalPassDebug(RenderGraph renderGraph, ContextContainer frameData)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();

            if ((debugHandler != null) && debugHandler.IsActiveForCamera(cameraData.isPreviewCamera))
            {
                if (debugHandler.TryGetFullscreenDebugMode(out DebugFullScreenMode fullScreenDebugMode, out int textureHeightPercent) &&
                    (fullScreenDebugMode != DebugFullScreenMode.ReflectionProbeAtlas || usesForwardPlusLightLists) &&
                    (fullScreenDebugMode != DebugFullScreenMode.STP))
                {
                    float screenWidth = cameraData.pixelWidth;
                    float screenHeight = cameraData.pixelHeight;

                    var relativeSize = Mathf.Clamp01(textureHeightPercent / 100f);
                    var height = relativeSize * screenHeight;
                    var width = relativeSize * screenWidth;

                    bool supportsStereo = false;
                    Vector4 dataRangeRemap = Vector4.zero; // zero = off, .x = old min, .y = old max, .z = new min, .w = new max

                    var mainShadowPass = GetPass<MainLightShadowCasterPass>();
                    var additionalShadowPass = GetPass<AdditionalLightsShadowCasterPass>();

                    // visualize RG internal resources
                    {
                        // if we want to visualize RG internal resources, we need to create an RTHandle external to RG and copy to it the textures to visualize
                        // this is required because the lifetime of these resources is limited to the RenderGraph execution, and we cannot access the actual resources here

                        // we also copy external resources to make them "read only". CreateDebugTexture() can lead to (external) texture reallocation.

                        var debugDescriptor = cameraData.cameraTargetDescriptor;
                        // Ensure target can hold all source values. Source can be signed for example.
                        if (GraphicsFormatCompatibility.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormatUsage.Linear | GraphicsFormatUsage.Render))
                            debugDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

                        CreateDebugTexture(debugDescriptor);

                        ImportResourceParams importParams = new ImportResourceParams();
                        importParams.clearOnFirstUse = false;
                        importParams.discardOnLastUse = false;
                        TextureHandle debugTexture = renderGraph.ImportTexture(s_renderGraphDebugTextureHandle, importParams);

                        switch (fullScreenDebugMode)
                        {
                            case DebugFullScreenMode.Depth:
                                {
                                    BlitToDebugTexture(renderGraph, resourceData.cameraDepthTexture, debugTexture);
                                    supportsStereo = true;
                                    break;
                                }
                            case DebugFullScreenMode.MotionVector:
                                {
                                    BlitToDebugTexture(renderGraph, resourceData.motionVectorColor, debugTexture, isSourceTextureColor: true);
                                    supportsStereo = true;
                                    // Motion vectors are in signed UV space, zoom in and normalize for visualization. (note: maybe add an option to use (angle, mag) visualization)
                                    const float zoom = 0.01f;
                                    dataRangeRemap.x = -zoom;
                                    dataRangeRemap.y = zoom;
                                    dataRangeRemap.z = 0;
                                    dataRangeRemap.w = 1.0f;
                                    break;
                                }
                            case DebugFullScreenMode.AdditionalLightsShadowMap:
                                {
                                    if (additionalShadowPass != null)
                                        BlitToDebugTexture(renderGraph, additionalShadowPass.additionalLightShadow.value, debugTexture);
                                    break;
                                }
                            case DebugFullScreenMode.MainLightShadowMap:
                                {
                                    if (mainShadowPass != null)
                                        BlitToDebugTexture(renderGraph, mainShadowPass.mainLightShadow.value, debugTexture);
                                    break;
                                }
                            case DebugFullScreenMode.AdditionalLightsCookieAtlas:
                                {
                                    // Copy atlas texture to make it "readonly". Direct reference (debug=atlas) can lead to handle->texture reallocation.
                                    var textureHandle =
                                        _lightCookieManager is { additionalLightsCookieAtlasTexture: not null }
                                            ? renderGraph.ImportTexture(_lightCookieManager
                                                .additionalLightsCookieAtlasTexture)
                                            : TextureHandle.nullHandle;

                                    BlitToDebugTexture(renderGraph, textureHandle, debugTexture);
                                    break;
                                }

                            case DebugFullScreenMode.ReflectionProbeAtlas:
                                {
                                    // Copy atlas texture to make it "readonly". Direct reference (debug=atlas) can lead to handle->texture reallocation.
                                    var textureHandle =
                                        _forwardLights.reflectionProbeManager.atlasRT != null
                                            ? renderGraph.ImportTexture(_forwardLights.reflectionProbeManager.atlasRTHandle)
                                            : TextureHandle.nullHandle;

                                    BlitToDebugTexture(renderGraph, textureHandle, debugTexture);
                                    break;
                                }
                            default:
                                    break;
          
                        }
                    }

                    // Textures that are not in screen aspect ratio need to be corrected
                    {
                        RenderTexture source = null;
                        switch (fullScreenDebugMode)
                        {
                            case DebugFullScreenMode.AdditionalLightsShadowMap: source = additionalShadowPass?.additionalLightsShadowmapHandle?.rt; break;
                            case DebugFullScreenMode.MainLightShadowMap: source = mainShadowPass?.mainLightShadowmapTexture?.rt; break;
                            case DebugFullScreenMode.AdditionalLightsCookieAtlas: source = _lightCookieManager?.additionalLightsCookieAtlasTexture?.rt; break;
                            case DebugFullScreenMode.ReflectionProbeAtlas: source = _forwardLights?.reflectionProbeManager.atlasRT; break;
                            default:
                                break;
                        }

                        // Ensure that atlas is not stretched, but doesn't take up more than the percentage in any dimension.
                        if (source != null)
                            CorrectForTextureAspectRatio(ref width, ref height, source.width, source.height);
                    }

                    Rect uvRect = CalculateUVRect(cameraData, width, height);
                    debugHandler.SetDebugRenderTarget(s_renderGraphDebugTextureHandle, uvRect, supportsStereo, dataRangeRemap);
                }
                else
                {
                    debugHandler.ResetDebugRenderTarget();
                }
            }

            if (debugHandler != null)
            {
                if (!debugHandler.TryGetFullscreenDebugMode(out DebugFullScreenMode fullScreenDebugMode, out int textureHeightPercent))
                {
                    var debugSettings = debugHandler.DebugDisplaySettings.gpuResidentDrawerSettings;

                    GPUResidentDrawer.RenderDebugOcclusionTestOverlay(renderGraph, debugSettings, cameraData.camera.GetInstanceID(), resourceData.activeColorTexture);

                    float screenWidth = (int)(cameraData.pixelHeight * cameraData.renderScale);
                    float screenHeight = (int)(cameraData.pixelHeight * cameraData.renderScale);
                    float maxHeight = screenHeight * textureHeightPercent / 100.0f;
                    GPUResidentDrawer.RenderDebugOccluderOverlay(renderGraph, debugSettings, new Vector2(0.25f * screenWidth, screenHeight - 1.5f * maxHeight), maxHeight, resourceData.activeColorTexture);
                }
            }
        }

        public override void SetupAfterPostRenderGraphFinalPassDebug(RenderGraph renderGraph, ContextContainer frameData)
        {
           ResourceData resourceData = frameData.Get<ResourceData>();
           CameraData cameraData = frameData.Get<CameraData>();

            if ((debugHandler != null) && debugHandler.IsActiveForCamera(cameraData.isPreviewCamera))
            {
                if (debugHandler.TryGetFullscreenDebugMode(out var debugFullscreenMode, out int textureHeightPercent) &&
                    (debugFullscreenMode == DebugFullScreenMode.STP))
                {
                    CreateDebugTexture(cameraData.cameraTargetDescriptor);

                    ImportResourceParams importParams = new ImportResourceParams();
                    importParams.clearOnFirstUse = false;
                    importParams.discardOnLastUse = false;
                    TextureHandle debugTexture = renderGraph.ImportTexture(s_renderGraphDebugTextureHandle, importParams);
                    BlitToDebugTexture(renderGraph, resourceData.stpDebugView, debugTexture);

                    Rect uvRect = CalculateUVRect(cameraData, textureHeightPercent);
                    Vector4 rangeRemap = Vector4.zero; // Off
                    debugHandler.SetDebugRenderTarget(s_renderGraphDebugTextureHandle, uvRect, true, rangeRemap);
                }
            }
        }
    }
}
