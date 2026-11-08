using FraxinusEngine.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Windows;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable]
    public partial class PostProcessPass : ScriptableRenderPassNode
    {
        // Recovery compatibility: plugins can sample the processed scene before
        // final AA/upscaling, presentation and overlay UI. While subscribed, keep
        // this image in a sampleable intermediate target and resolve it afterward.
        public static event Action<RenderGraph, ScriptableRenderContext, ContextContainer> beforeFinalResolve;

        static class ShaderConstants
        {
            public static readonly int _TempTarget = Shader.PropertyToID("_TempTarget");
            public static readonly int _TempTarget2 = Shader.PropertyToID("_TempTarget2");

            public static readonly int _StencilRef = Shader.PropertyToID("_StencilRef");
            public static readonly int _StencilMask = Shader.PropertyToID("_StencilMask");

            public static readonly int _Lut_Params = Shader.PropertyToID("_Lut_Params");
            public static readonly int _UserLut_Params = Shader.PropertyToID("_UserLut_Params");
            public static readonly int _InternalLut = Shader.PropertyToID("_InternalLut");
            public static readonly int _UserLut = Shader.PropertyToID("_UserLut");

            public static readonly int _FullscreenProjMat = Shader.PropertyToID("_FullscreenProjMat");
        }

        const string _CameraColorUpscaled = "_CameraColorUpscaled";
        const string _CameraColorAfterPostProcessingName = "_CameraColorAfterPostProcessing";

        [Serializable]
        public class PostPassEntry
        {
            public string typeName;
            [NonSerialized] private Type _cachedType;
            
            public Type passType
            {
                get
                {
                    if (_cachedType == null && !string.IsNullOrEmpty(typeName))
                        _cachedType = Type.GetType(typeName);
     
                    return _cachedType;
                }
                set
                {
                    _cachedType = value;
                    typeName = value?.AssemblyQualifiedName;
                }
            }
            
            [NonSerialized] public BasePostPass passInstance;
            [SerializeField] public bool enabled = true;
        }

        [Serializable]
        public class UberEffectEntry
        {
            public string typeName; // Store type name as string for serialization
            public string displayName;
            [SerializeField] public bool enabled = true;
            
            [NonSerialized] private Type _cachedType;
            
            public Type componentType
            {
                get
                {
                    if (_cachedType == null && !string.IsNullOrEmpty(typeName))
                        _cachedType = Type.GetType(typeName);
 
                    return _cachedType;
                }
            }
            
            public UberEffectEntry(Type type, string name, bool defaultEnabled = true)
            {
                typeName = type?.AssemblyQualifiedName;
                _cachedType = type;
                displayName = name;
                enabled = defaultEnabled;
            }
        }

        [HideInInspector, SerializeField]
        private List<PostPassEntry> _postPassEntries = new List<PostPassEntry>();

        public List<PostPassEntry> postPassEntries => _postPassEntries;

        // Read the renderer setting directly: camera jitter is initialized before
        // input builds the active post-pass list for the current frame.
        internal bool temporalAAEnabled
        {
            get
            {
                foreach (var entry in _postPassEntries)
                    if (entry.enabled && entry.passInstance is TAAPass)
                        return true;
                return false;
            }
        }

        [HideInInspector, SerializeField]
        private List<UberEffectEntry> _uberEffectEntries = new List<UberEffectEntry>();

        public List<UberEffectEntry> uberEffectEntries => _uberEffectEntries;

        [Input(name = "ColorLut")]
        public TextureHandleNodeParameter colorLut;

        [Input(name = "Camera Target")]
        public CameraTargetBufferNodeParameter inCameraTarget;

        private RenderTextureDescriptor _descriptor;

        public RenderTextureDescriptor descriptor => _descriptor;

        public PostProcessData data { get => graph.postProcessData; }

        // Misc
        private GraphicsFormat _defaultColorFormat;   // The default format for post-processing, follows back-buffer format in UltraRP.
        public GraphicsFormat defaultColorFormat => _defaultColorFormat;
        private bool _defaultColorFormatIsAlpha;
        public bool defaultColorFormatIsAlpha => _defaultColorFormatIsAlpha;

        // If there's a final post process pass after this pass.
        // If yes, Film Grain and Dithering are setup in the final pass, otherwise they are setup in this pass.
        bool _hasFinalPass;

        // Some Android devices do not support sRGB backbuffer
        // We need to do the conversion manually on those
        // Also if HDR output is active
        bool _enableColorEncodingIfNeeded;

        // Use Fast conversions between SRGB and Linear
        bool _useFastSRGBLinearConversion;

        public bool useFastSRGBLinearConversion => _useFastSRGBLinearConversion;

        // Blit to screen or color frontbuffer at the end
        bool _resolveToScreen;

        // Builtin effects settings (kept for non-Uber effects)
        ColorAdjustments _colorAdjustments;
        Tonemapping _tonemapping;
        FilmGrain _filmGrain;  // Also used in FinalPass

        FinalBlitPass _finalBlitPass;
        TAAPass _temporalAAPass;

        internal BloomPass.UberSetupBloomPassData bloomData;

        // Imported lookup textures remain owned by their assets/callers. Keep
        // wrappers alive until this graph node is disabled, including when two
        // cameras record different LUTs before execution.
        readonly Dictionary<Texture, RTHandle> _userLutHandles = new Dictionary<Texture, RTHandle>();

        void ReleaseUserLutHandles()
        {
            foreach (var handle in _userLutHandles.Values)
                handle.Release();
            _userLutHandles.Clear();
        }

        // 添加一个缓存列表，用于存储当前帧/当前相机激活的 Pass
        private List<PostPassEntry> _activePostPassEntries = new List<PostPassEntry>();

        // 在 input getter 中完成遍历和 Setup，既解决了时序问题，又缓存了结果
        public override ScriptableRenderPassInput input
        {
            get
            {
                var stack = VolumeManager.instance.stack;
                if (stack == null) return ScriptableRenderPassInput.None;

                _activePostPassEntries.Clear();
                ScriptableRenderPassInput requiredInput = ScriptableRenderPassInput.None;

                foreach (var entry in _postPassEntries)
                {
                    // 在这里统一调用 Setup，并筛选出激活的 Entry
                    if (entry.passInstance is not null && entry.passInstance.Setup(entry.enabled, stack))
                    {
                        _activePostPassEntries.Add(entry);
                        requiredInput |= entry.passInstance.renderInput;
                    }
                }

                return requiredInput;
            }
        }

        public PostProcessPass() : base()
        {
            base.profilingSampler = new ProfilingSampler(nameof(PostProcessPass));
        }

        protected override void Destroy()
        {
            base.Destroy();

            _finalBlitPass?.Dispose();
            _finalBlitPass = null;
            ReleaseUserLutHandles();
        }

        protected override void Enable()
        {
            base.Enable();

            GraphicsFormat requestColorFormat = GraphicsFormat.B10G11R11_UFloatPack32;
            var asset = UltraRenderPipeline.asset;
            if (asset)
                requestColorFormat = UltraRenderPipeline.MakeRenderTextureGraphicsFormat(asset.supportsHDR, asset.hdrColorBufferPrecision, false);

            bool requestHDR = IsHDRFormat(requestColorFormat);
            bool requestAlpha = IsAlphaFormat(requestColorFormat);

            // Texture format pre-lookup
            // UUM-41070: We require `Linear | Render` but with the deprecated FormatUsage this was checking `Blend`
            // For now, we keep checking for `Blend` until the performance hit of doing the correct checks is evaluated
            if (requestHDR)
            {
                _defaultColorFormatIsAlpha = requestAlpha;

                const GraphicsFormatUsage usage = GraphicsFormatUsage.Blend;
                if (GraphicsFormatCompatibility.IsFormatSupported(requestColorFormat, usage))    // Typically, RGBA16Float.
                {
                    _defaultColorFormat = requestColorFormat;
                }
                else if (GraphicsFormatCompatibility.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, usage)) // HDR fallback
                {
                    // NOTE: Technically request format can be with alpha, however if it's not supported and we fall back here
                    // , we assume no alpha. Post-process default format follows the back buffer format.
                    // If support failed, it must have failed for back buffer too.
                    _defaultColorFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                    _defaultColorFormatIsAlpha = false;
                }
                else
                {
                    _defaultColorFormat = QualitySettings.activeColorSpace == ColorSpace.Linear
                        ? GraphicsFormat.R8G8B8A8_SRGB
                        : GraphicsFormat.R8G8B8A8_UNorm;
                }
            }
            else // SDR
            {
                _defaultColorFormat = QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? GraphicsFormat.R8G8B8A8_SRGB
                    : GraphicsFormat.R8G8B8A8_UNorm;

                _defaultColorFormatIsAlpha = true;
            }

            if (_finalBlitPass is null)
                _finalBlitPass = new FinalBlitPass();

            SetupPostPasses();
            SetupUberEffects();
        }

        protected override void Disable()
        {
            base.Disable();

            CleanupPostPasses();
            ReleaseUserLutHandles();
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();
            PostProcessingData postProcessingData = frameData.Get<PostProcessingData>();

            var renderer = cameraData.renderer;
            CameraTargetBuffer buffer = inCameraTarget.value;
            TextureHandle srcHandle = buffer.color;
            TextureHandle cameraColor = resourceData.cameraColor;

            DrawScreenSpaceUIPass drawOverlayUIPass = renderer.GetModule<DrawScreenSpaceUIPass>();

            bool shouldRenderUI = cameraData.rendersOverlayUI;
            bool outputToHDR = cameraData.isHDROutputActive;
            if (shouldRenderUI && outputToHDR)
            {
                TextureHandle overlayUI;
                drawOverlayUIPass.RenderOffscreen(renderGraph, frameData, renderer.cameraDepthAttachmentFormat, out overlayUI);
                resourceData.overlayUITexture = overlayUI;
            }

            DebugHandler debugHandler = ScriptableRenderPassNode.GetActiveDebugHandler(cameraData);

            // if it's the last camera in the stack, setup the rendering debugger
            if (cameraData.resolveFinalTarget)
                renderer.SetupRenderGraphFinalPassDebug(renderGraph, frameData);

            // Disable Gizmos when using scene overrides. Gizmos break some effects like Overdraw debug.
            bool drawGizmos = UltraRenderPipelineDebugDisplaySettings.Instance.renderingSettings.sceneOverrideMode == DebugSceneOverrideMode.None;
            if (drawGizmos)
                renderer.DrawRenderGraphGizmos(renderGraph, frameData, resourceData.activeColorTexture, resourceData.activeDepthTexture, GizmoSubset.PreImageEffects);

            bool srcValid = srcHandle.IsValid();
            bool applyPostProcessing = ShouldApplyPostProcessing(cameraData.postProcessEnabled && srcValid);
            foreach (var entry in _postPassEntries)
                if (entry.passInstance is AutoExposurePass exposure &&
                    (!applyPostProcessing || !_activePostPassEntries.Contains(entry)))
                    exposure.InvalidateHistory(cameraData.camera);
            // There's at least a camera in the camera stack that applies post-processing
            bool anyPostProcessing = postProcessingData.isEnabled;

            // When FXAA or scaling is active, we must perform an additional pass at the end of the frame for the following reasons:
            // 1. FXAA expects to be the last shader running on the image before it's presented to the screen. Since users are allowed
            //    to add additional render passes after post processing occurs, we can't run FXAA until all of those passes complete as well.
            //    The FinalPost pass is guaranteed to execute after user authored passes so FXAA is always run inside of it.
            // 2. UberPost can only handle upscaling with linear filtering. All other filtering methods require the FinalPost pass.
            // 3. TAA sharpening using standalone RCAS pass is required. (When upscaling is not enabled).
            bool applyFinalPostProcessing = anyPostProcessing && cameraData.resolveFinalTarget &&
                                ((cameraData.antialiasing == AntialiasingMode.FastApproximateAntialiasing) ||
                                 ((cameraData.imageScalingMode == ImageScalingMode.Upscaling) && (cameraData.upscalingFilter != ImageUpscalingFilter.Linear)) ||
                                 (cameraData.IsTemporalAAEnabled() && cameraData.taaSettings.contrastAdaptiveSharpening > 0.0f));

            bool hasCaptureActions = cameraData.captureActions != null && cameraData.resolveFinalTarget;
            bool hasBeforeFinalResolve = beforeFinalResolve != null;
            bool resolvePostProcessingToCameraTarget = !hasCaptureActions && !applyFinalPostProcessing && !hasBeforeFinalResolve;
            bool needsColorEncoding = debugHandler == null || !debugHandler.HDRDebugViewIsActive(cameraData.resolveFinalTarget);

            bool resolveToDebugScreen = debugHandler != null && debugHandler.WriteToDebugScreenTexture(cameraData.resolveFinalTarget);
            if (resolveToDebugScreen)
            {
                RenderTextureDescriptor colorDesc = cameraData.cameraTargetDescriptor;
                DebugHandler.ConfigureColorDescriptorForDebugScreen(ref colorDesc, cameraData.pixelWidth, cameraData.pixelHeight);
                resourceData.debugScreenColor = UltraRenderer.CreateRenderGraphTexture(renderGraph, colorDesc, "_DebugScreenColor", false);

                RenderTextureDescriptor depthDesc = cameraData.cameraTargetDescriptor;
                DebugHandler.ConfigureDepthDescriptorForDebugScreen(ref depthDesc, renderer.cameraDepthAttachmentFormat, cameraData.pixelWidth, cameraData.pixelHeight);
                resourceData.debugScreenDepth = UltraRenderer.CreateRenderGraphTexture(renderGraph, depthDesc, "_DebugScreenDepth", false);
            }

            // If the debugHandler displays HDR debug views, it needs to redirect (final) post-process output to an intermediate color target (debugScreenTexture)
            // and it will write into the post-process intended output.
            TextureHandle debugHandlerColorTarget = resourceData.afterPostProcessColor;

            if (applyPostProcessing)
            {
                TextureHandle backbuffer = resourceData.backBufferColor;
                TextureHandle overlayUITexture = resourceData.overlayUITexture;

                bool isTargetBackbuffer = (cameraData.resolveFinalTarget && !applyFinalPostProcessing && !hasBeforeFinalResolve);

                TextureHandle target;
                if (isTargetBackbuffer)
                    target = backbuffer;
                else
                {
                    ImportResourceParams importColorParams = new ImportResourceParams();
                    importColorParams.clearOnFirstUse = true;
                    importColorParams.clearColor = Color.black;
                    importColorParams.discardOnLastUse = cameraData.resolveFinalTarget;  // check if last camera in the stack

                    if (cameraData.IsSTPEnabled())
                    {
                        // STP is disabled when using camera stacking.
                        // In any case, we don't use persistent textures here so we need to make sure there is no next camera in the stack (should always be true).
                        Debug.Assert(cameraData.resolveFinalTarget);

                        var desc = resourceData.cameraColor.GetDescriptor(renderGraph);
                        static void MakeCompatible(ref TextureDesc desc)
                        {
                            desc.msaaSamples = MSAASamples.None;
                            desc.useMipMap = false;
                            desc.autoGenerateMips = false;
                            desc.anisoLevel = 0;
                            desc.discardBuffer = false;
                        }
                        MakeCompatible(ref desc);

                        desc.width = cameraData.pixelWidth;
                        desc.height = cameraData.pixelHeight;
                        desc.name = _CameraColorUpscaled;

                        resourceData.cameraColor = renderGraph.CreateTexture(desc);
                    }
                    else
                    {
                        var isSingleCamera = cameraData.resolveFinalTarget && cameraData.renderType == CameraRenderType.Base;

                        resourceData.cameraColor = (isSingleCamera)
                            ? renderGraph.CreateTexture(resourceData.activeColorTexture, _CameraColorAfterPostProcessingName)
                            : renderGraph.ImportTexture(renderer.nextRenderGraphCameraColorHandle, importColorParams);
                    }

                    target = resourceData.cameraColor;
                }

                // but we may actually render to an intermediate texture if debug views are enabled.
                // In that case, DebugHandler will eventually blit DebugScreenTexture into AfterPostProcessColor.
                if (resolveToDebugScreen && isTargetBackbuffer)
                {
                    debugHandlerColorTarget = target;
                    target = resourceData.debugScreenColor;
                }

                bool doSRGBEncoding = resolvePostProcessingToCameraTarget && needsColorEncoding;

                var source = resourceData.cameraColor;
                RenderPostProcessingRenderGraph(renderGraph, frameData, in srcHandle, in overlayUITexture, in target, applyFinalPostProcessing, resolveToDebugScreen, doSRGBEncoding);

                // Handle any after-post rendering debugger overlays
                if (cameraData.resolveFinalTarget)
                    renderer.SetupAfterPostRenderGraphFinalPassDebug(renderGraph, frameData);

                if (isTargetBackbuffer)
                {
                    resourceData.activeColorID = ResourceData.ActiveID.BackBuffer;
                    resourceData.activeDepthID = ResourceData.ActiveID.BackBuffer;
                }
            }

            beforeFinalResolve?.Invoke(renderGraph, context, frameData);

            if (applyFinalPostProcessing)
            {
                TextureHandle overlayUITexture = resourceData.overlayUITexture;
                TextureHandle backbuffer = resourceData.backBufferColor;
                // Desired target for post-processing pass.
                TextureHandle target = backbuffer;

                if (resolveToDebugScreen)
                {
                    debugHandlerColorTarget = target;
                    target = resourceData.debugScreenColor;
                }

                // make sure we are accessing the proper camera color in case it was replaced by injected passes
                var source = resourceData.cameraColor;
                RenderFinalPassRenderGraph(renderGraph, frameData, in source, in overlayUITexture, in target, needsColorEncoding);
            }

            if (cameraData.captureActions != null)
            {
                var capturePass = renderer.GetModule<CapturePass>();
                capturePass.RecordRenderGraph(renderGraph, frameData);
            }

            bool cameraTargetResolved =
                // final PP always blit to camera target
                applyFinalPostProcessing ||
                // no final PP but we have PP stack. In that case it blit unless there are render pass after PP
                (applyPostProcessing && !hasCaptureActions && !hasBeforeFinalResolve);

            if (!resourceData.isActiveTargetBackBuffer && cameraData.resolveFinalTarget && !cameraTargetResolved)
            {
                TextureHandle backbuffer = resourceData.backBufferColor;
                TextureHandle overlayUITexture = resourceData.overlayUITexture;
                TextureHandle target = backbuffer;

                if (resolveToDebugScreen)
                {
                    debugHandlerColorTarget = target;
                    target = resourceData.debugScreenColor;
                }

                // make sure we are accessing the proper camera color in case it was replaced by injected passes
                var source = resourceData.cameraColor;

                debugHandler?.UpdateShaderGlobalPropertiesForFinalValidationPass(renderGraph, cameraData, !resolveToDebugScreen);

                _finalBlitPass.Render(renderGraph, frameData, cameraData, source, target, overlayUITexture);

                resourceData.activeColorID = ResourceData.ActiveID.BackBuffer;
                resourceData.activeDepthID = ResourceData.ActiveID.BackBuffer;
            }

            // We can explicitely render the overlay UI from UltraRP when HDR output is not enabled.
            // SupportedRenderingFeatures.active.rendersUIOverlay should also be set to true.
            shouldRenderUI = cameraData.rendersOverlayUI && cameraData.isLastBaseCamera;
            if (shouldRenderUI && !outputToHDR)
            {
                TextureHandle depthBuffer = resourceData.backBufferDepth;
                TextureHandle target = resourceData.backBufferColor;

                if (resolveToDebugScreen)
                {
                    debugHandlerColorTarget = target;
                    target = resourceData.debugScreenColor;
                    depthBuffer = resourceData.debugScreenDepth;
                }

                drawOverlayUIPass.RenderOverlay(renderGraph, frameData, in target, in depthBuffer);
            }

            if (debugHandler != null)
            {
                TextureHandle overlayUITexture = resourceData.overlayUITexture;
                TextureHandle debugScreenTexture = resourceData.debugScreenColor;

                debugHandler.Render(renderGraph, frameData, debugScreenTexture, overlayUITexture, debugHandlerColorTarget);
            }

            if (cameraData.resolveFinalTarget)
            {
#if UNITY_EDITOR
                // If we render to an intermediate depth attachment instead of the backbuffer, we need to copy the result to the backbuffer in cases where backbuffer
                // depth data is required later in the frame.
                bool backbufferDepthRequired = (cameraData.isSceneViewCamera || cameraData.isPreviewCamera || UnityEditor.Handles.ShouldRenderGizmos());
                if (ScriptableRenderer.requiresIntermediateAttachments && backbufferDepthRequired)
                {
                    renderer.GetModule<DepthCopyPass>().RenderToCameraTarget(renderGraph, frameData, resourceData.cameraDepth);
                }
#endif
                if (cameraData.isSceneViewCamera)
                    renderer.DrawRenderGraphWireOverlay(renderGraph, frameData, resourceData.backBufferColor);

                if (drawGizmos)
                    renderer.DrawRenderGraphGizmos(renderGraph, frameData, resourceData.backBufferColor, resourceData.backBufferDepth, GizmoSubset.PostImageEffects);
            }
        }


        class PostFXSetupPassData { }

        internal void RenderPostProcessingRenderGraph(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle activeCameraColorTexture, in TextureHandle overlayUITexture, in TextureHandle postProcessingTarget, bool hasFinalPass, bool resolveToDebugScreen, bool enableColorEndingIfNeeded)
        {
            ResourceData resourceData               = frameData.Get<ResourceData>();
            RenderingData renderingData             = frameData.Get<RenderingData>();
            CameraData cameraData                   = frameData.Get<CameraData>();
            PostProcessingData postProcessingData   = frameData.Get<PostProcessingData>();

            var stack = VolumeManager.instance.stack;
            
            // Update cached components once per frame (single dictionary access per component type)
            UpdateCachedComponents(stack);
            
            // Still need these for non-Uber effects
            _colorAdjustments = stack.GetComponent<ColorAdjustments>();
            _tonemapping = stack.GetComponent<Tonemapping>();

            _descriptor = cameraData.cameraTargetDescriptor;
            _descriptor.useMipMap = false;
            _descriptor.autoGenerateMips = false;
            _useFastSRGBLinearConversion = postProcessingData.useFastSRGBLinearConversion;
            _hasFinalPass = hasFinalPass;
            _enableColorEncodingIfNeeded = enableColorEndingIfNeeded;

            ref ScriptableRenderer renderer = ref cameraData.renderer;
            bool isSceneViewCamera = cameraData.isSceneViewCamera;


            bool isFsrEnabled = ((cameraData.imageScalingMode == ImageScalingMode.Upscaling) && (cameraData.upscalingFilter == ImageUpscalingFilter.FSR));

            using (var builder = renderGraph.AddRasterRenderPass<PostFXSetupPassData>("Setup PostFX passes", out var passData,
                    ProfilingSampler.Get(UltraRPProfileId.RG_SetupPostFX)))
            {
                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PostFXSetupPassData data, RasterGraphContext context) =>
                {
                    // Setup projection matrix for cmd.DrawMesh()
                    context.cmd.SetGlobalMatrix(ShaderConstants._FullscreenProjMat, GL.GetGPUProjectionMatrix(Matrix4x4.identity, true));
                });
            }

            var uberMat = graph.materialLib.GetMaterial("uberPostPS");
            uberMat.shaderKeywords = null;
            bloomData = default; // Each camera/frame records its own optional Bloom dependency.

            TextureHandle currentSource = activeCameraColorTexture;

            // The renderer must support the camera's TAA/STP request. Resolve before
            // Bloom and other effects so they see the upscaled dimensions.
            if (temporalAAEnabled && cameraData.IsTemporalAARequested() && !cameraData.IsSGSR2Requested())
                currentSource = _temporalAAPass.Render(renderGraph, frameData, in currentSource);

            // Meter scene-referred color after temporal resolve, and expose it before
            // Bloom, lens effects and tonemapping regardless of the editable list order.
            foreach (var entry in _activePostPassEntries)
                if (entry.passInstance is AutoExposurePass)
                    currentSource = entry.passInstance.Render(renderGraph, frameData, in currentSource);

            foreach (var entry in _activePostPassEntries)
                if (entry.passInstance is not TAAPass && entry.passInstance is not AutoExposurePass)
                    currentSource = entry.passInstance.Render(renderGraph, frameData, in currentSource);

            {
                // Apply all Uber effects
                ApplyUberEffects(uberMat, cameraData);

                if (RequireSRGBConversionBlitToBackBuffer(cameraData.requireSrgbConversion))
                    uberMat.EnableKeyword(ShaderKeywordStrings.LinearToSRGBConversion);

                bool requireHDROutput = RequireHDROutput(cameraData);
                if (requireHDROutput)
                {
                    // Color space conversion is already applied through color grading, do encoding if uber post is the last pass
                    // Otherwise encoding will happen in the final post process pass or the final blit pass
                    HDROutputUtils.Operation hdrOperation = !_hasFinalPass && _enableColorEncodingIfNeeded ? HDROutputUtils.Operation.ColorEncoding : HDROutputUtils.Operation.None;
                    SetupHDROutput(cameraData.hdrDisplayInformation, cameraData.hdrDisplayColorGamut, uberMat, hdrOperation, cameraData.rendersOverlayUI);
                }

                if (_useFastSRGBLinearConversion)
                    uberMat.EnableKeyword(ShaderKeywordStrings.UseFastSRGBLinearConversion);

                bool enableAlphaOutput = cameraData.isAlphaOutputEnabled;
                DebugHandler debugHandler = GetActiveDebugHandler(cameraData);
                debugHandler?.UpdateShaderGlobalPropertiesForFinalValidationPass(renderGraph, cameraData, !_hasFinalPass && !resolveToDebugScreen);

                RenderUberPost(renderGraph, frameData, cameraData, postProcessingData, in currentSource, in postProcessingTarget, in overlayUITexture, requireHDROutput, enableAlphaOutput, resolveToDebugScreen, hasFinalPass);
            }
        }

        RenderTextureDescriptor GetCompatibleDescriptor()
            => GetCompatibleDescriptor(_descriptor.width, _descriptor.height, _descriptor.graphicsFormat);

        RenderTextureDescriptor GetCompatibleDescriptor(int width, int height, GraphicsFormat format, GraphicsFormat depthStencilFormat = GraphicsFormat.None)
            => GetCompatibleDescriptor(_descriptor, width, height, format, depthStencilFormat);

        public static RenderTextureDescriptor GetCompatibleDescriptor(RenderTextureDescriptor desc, int width, int height, GraphicsFormat format, GraphicsFormat depthStencilFormat = GraphicsFormat.None)
        {
            desc.depthStencilFormat = depthStencilFormat;
            desc.msaaSamples = 1;
            desc.width = width;
            desc.height = height;
            desc.graphicsFormat = format;
            return desc;
        }

        bool IsHDRFormat(GraphicsFormat format)
        {
            return format == GraphicsFormat.B10G11R11_UFloatPack32 ||
                   GraphicsFormatUtility.IsHalfFormat(format) ||
                   GraphicsFormatUtility.IsFloatFormat(format);
        }

        bool IsAlphaFormat(GraphicsFormat format)
        {
            return GraphicsFormatUtility.HasAlphaChannel(format);
        }

        bool RequireSRGBConversionBlitToBackBuffer(bool requireSrgbConversion)
        {
            return requireSrgbConversion && _enableColorEncodingIfNeeded;
        }

        bool RequireHDROutput(CameraData cameraData)
        {
            // If capturing, don't convert to HDR.
            // If not last in the stack, don't convert to HDR.
            return cameraData.isHDROutputActive && cameraData.captureActions == null;
        }

        void SetupPostPasses()
        {
            CleanupPostPasses();
            
            var passTypes = GetTypesFromAssemblies<BasePostPass>();
            var typeNames = new HashSet<string>();

            foreach (var type in passTypes)
            {
                try
                {
                    var typeName = type.AssemblyQualifiedName;
                    typeNames.Add(typeName);
                    
                    var existingEntry = _postPassEntries.Find(e => e.typeName == typeName);
                    
                    if (existingEntry != null)
                    {
                        // Update instance, preserve enabled state
                        var instance = Activator.CreateInstance(type) as BasePostPass;
                        instance?.Init(this);
                        existingEntry.passType = type;
                        existingEntry.passInstance = instance;
                    }
                    else
                    {
                        var instance = Activator.CreateInstance(type) as BasePostPass;
                        if (instance != null)
                        {
                            instance.Init(this);
                            _postPassEntries.Add(new PostPassEntry
                            {
                                passType = type,
                                passInstance = instance,
                                // Exposure is controlled by its default-off Volume component.
                                enabled = type == typeof(AutoExposurePass) || type == typeof(PaniniProjectionPass)
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    Debug.LogWarning($"Failed to auto-register pass {type.Name}: {ex.Message}");
#endif
                }
            }
            
            CleanupRemovedEntries(_postPassEntries, typeNames, e => e.typeName, 
                e => (e.passInstance as IDisposable)?.Dispose());
            _temporalAAPass = _postPassEntries.Find(entry => entry.passInstance is TAAPass)?.passInstance as TAAPass;
            if (_temporalAAPass == null)
            {
                _temporalAAPass = new TAAPass();
                _temporalAAPass.Init(this);
                _postPassEntries.Add(new PostPassEntry { passType = typeof(TAAPass), passInstance = _temporalAAPass, enabled = false });
            }
        }

        void CleanupPostPasses()
        {
            if (_postPassEntries == null)
                return;

            foreach (var entry in _postPassEntries)
            {
                if (entry.passInstance is IDisposable disposable)
                    disposable.Dispose();
            }
        }

        private struct CachedEffectProcessor
        {
            public IUberEffectProcessor processor;
            public UberEffectEntry entry;
            public VolumeComponent component;
        }

        private CachedEffectProcessor[] _cachedProcessors;

        void SetupUberEffects()
        {
            var processorTypes = GetTypesFromAssemblies<IUberEffectProcessor>(includeInterfaces: false);
            var discoveredProcessors = new Dictionary<Type, IUberEffectProcessor>();
            
            foreach (var type in processorTypes)
            {
                try
                {
                    var processor = Activator.CreateInstance(type) as IUberEffectProcessor;
                    if (processor != null)
                    {
                        discoveredProcessors[processor.componentType] = processor;
                    }
                }
                catch (Exception ex)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    Debug.LogWarning($"Failed to instantiate effect processor {type.Name}: {ex.Message}");
#endif
                }
            }

            if (_uberEffectEntries == null || _uberEffectEntries.Count == 0)
            {
                _uberEffectEntries = new List<UberEffectEntry>();
                foreach (var kvp in discoveredProcessors)
                {
                    _uberEffectEntries.Add(new UberEffectEntry(
                        kvp.Key,
                        kvp.Value.displayName,
                        true
                    ));
                }
            }
            else
            {
                var typeNames = discoveredProcessors.Keys.Select(t => t.AssemblyQualifiedName).ToHashSet();
                CleanupRemovedEntries(_uberEffectEntries, typeNames, e => e.typeName, null);
            }

            var tempList = new List<CachedEffectProcessor>(_uberEffectEntries.Count);
            foreach (var entry in _uberEffectEntries)
            {
                if (entry.componentType != null && discoveredProcessors.TryGetValue(entry.componentType, out var processor))
                {
                    tempList.Add(new CachedEffectProcessor
                    {
                        processor = processor,
                        entry = entry,
                        component = null
                    });
                }
            }
            
            _cachedProcessors = tempList.ToArray();
        }

        IEnumerable<Type> GetTypesFromAssemblies<T>(bool includeInterfaces = true)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => {
                    try { return a.GetTypes(); }
                    catch { return Type.EmptyTypes; }
                })
                .Where(t => typeof(T).IsAssignableFrom(t) 
                            && !t.IsAbstract
                            && (includeInterfaces || !t.IsInterface)
                            && t != typeof(T));
        }
        

        void CleanupRemovedEntries<T>(List<T> entries, HashSet<string> validTypeNames, 
            Func<T, string> getTypeName, Action<T> disposeAction)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                var typeName = getTypeName(entry);
                
                if (string.IsNullOrEmpty(typeName) || !validTypeNames.Contains(typeName))
                {
                    disposeAction?.Invoke(entry);
                    entries.RemoveAt(i);
                }
            }
        }

        void UpdateCachedComponents(VolumeStack stack)
        {
            for (int i = 0; i < _cachedProcessors.Length; i++)
                _cachedProcessors[i].component = stack.GetComponent(_cachedProcessors[i].entry.componentType);
        }

        void ApplyUberEffects(Material material, CameraData cameraData)
        {
            for (int i = 0; i < _cachedProcessors.Length; i++)
            {
                ref var cached = ref _cachedProcessors[i];
    
                var postProcessComponent = cached.component as IPostProcessComponent;
                if (postProcessComponent == null)
                    continue;

                if (cached.entry.componentType == typeof(FilmGrain) && _hasFinalPass)
                    continue;

                bool enable = !(!cached.entry.enabled || !postProcessComponent.IsActive());
                if (cached.processor.Valid(enable))
                    cached.processor.Setup(material, cached.component, this, cameraData);
            }
        }

        private class UpdateCameraResolutionPassData
        {
            internal Vector2Int newCameraTargetSize;
        }

        public void UpdateCameraResolution(RenderGraph renderGraph, CameraData cameraData, Vector2Int newCameraTargetSize)
        {
            // Update the local descriptor and the camera data descriptor to reflect post-upscaled sizes
            _descriptor.width = newCameraTargetSize.x;
            _descriptor.height = newCameraTargetSize.y;
            cameraData.cameraTargetDescriptor.width = newCameraTargetSize.x;
            cameraData.cameraTargetDescriptor.height = newCameraTargetSize.y;

            // Update the shader constants to reflect the new camera resolution
            using (var builder = renderGraph.AddUnsafePass<UpdateCameraResolutionPassData>("Update Camera Resolution", out var passData))
            {
                passData.newCameraTargetSize = newCameraTargetSize;

                // This pass only modifies shader constants so we need to set some special flags to ensure it isn't culled or optimized away
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (UpdateCameraResolutionPassData data, UnsafeGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalVector(
                        ShaderPropertyId.screenSize,
                        new Vector4(
                            data.newCameraTargetSize.x,
                            data.newCameraTargetSize.y,
                            1.0f / data.newCameraTargetSize.x,
                            1.0f / data.newCameraTargetSize.y
                        )
                    );
                });
            }
        }

        bool ShouldApplyPostProcessing(bool postProcessEnabled)
        {
            return postProcessEnabled && (graph.postProcessData is not null);
        }

        #region HDR Output
        void SetupHDROutput(HDROutputUtils.HDRDisplayInformation hdrDisplayInformation, ColorGamut hdrDisplayColorGamut, Material material, HDROutputUtils.Operation hdrOperations, bool rendersOverlayUI)
        {
            Vector4 hdrOutputLuminanceParams;
            UltraRenderPipeline.GetHDROutputLuminanceParameters(hdrDisplayInformation, hdrDisplayColorGamut, _tonemapping, out hdrOutputLuminanceParams);
            material.SetVector(ShaderPropertyId.hdrOutputLuminanceParams, hdrOutputLuminanceParams);

            HDROutputUtils.ConfigureHDROutput(material, hdrDisplayColorGamut, hdrOperations);
            CoreUtils.SetKeyword(material, ShaderKeywordStrings.HDROverlay, rendersOverlayUI);
        }
        #endregion

        #region FinalPass
        private class PostProcessingFinalSetupPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
            internal CameraData cameraData;
        }

        public void RenderFinalSetup(RenderGraph renderGraph, CameraData cameraData, in TextureHandle source, in TextureHandle destination, ref FinalBlitSettings settings)
        {
            // Scaled FXAA
            using (var builder = renderGraph.AddRasterRenderPass<PostProcessingFinalSetupPassData>("Postprocessing Final Setup Pass", out var passData, ProfilingSampler.Get(UltraRPProfileId.RG_FinalSetup)))
            {
                Material material = graph.materialLib.GetMaterial("scalingSetupPS");
                material.shaderKeywords = null;

                if (settings.isFxaaEnabled)
                    material.EnableKeyword(ShaderKeywordStrings.Fxaa);

                if (settings.isFsrEnabled)
                    material.EnableKeyword(settings.hdrOperations.HasFlag(HDROutputUtils.Operation.ColorEncoding) ? ShaderKeywordStrings.Gamma20AndHDRInput : ShaderKeywordStrings.Gamma20);

                if (settings.hdrOperations.HasFlag(HDROutputUtils.Operation.ColorEncoding))
                    SetupHDROutput(cameraData.hdrDisplayInformation, cameraData.hdrDisplayColorGamut, material, settings.hdrOperations, cameraData.rendersOverlayUI);

                if (settings.isAlphaOutputEnabled)
                    CoreUtils.SetKeyword(material, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, settings.isAlphaOutputEnabled);

                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = destination;
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.cameraData = cameraData;
                passData.material = material;

                builder.SetRenderFunc(static (PostProcessingFinalSetupPassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    PostProcessUtils.SetSourceSize(cmd, sourceTextureHdl);

                    bool hasFinalPass = true; // This is a pass just before final pass. Viewport must match intermediate target.
                    ScaleViewportAndBlit(context.cmd, sourceTextureHdl, data.destinationTexture, data.cameraData, data.material, hasFinalPass);
                });
                return;
            }
        }


        private class PostProcessingFinalFSRScalePassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
            internal bool enableAlphaOutput;
        }

        public void RenderFinalFSRScale(RenderGraph renderGraph, in TextureHandle source, in TextureHandle destination, bool enableAlphaOutput)
        {
            // FSR upscale
            var easuMat = graph.materialLib.GetMaterial("easuPS");
            easuMat.shaderKeywords = null;

            using (var builder = renderGraph.AddRasterRenderPass<PostProcessingFinalFSRScalePassData>("Postprocessing Final FSR Scale Pass", out var passData, ProfilingSampler.Get(UltraRPProfileId.RG_FinalFSRScale)))
            {
                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = destination;
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.material = easuMat;
                passData.enableAlphaOutput = enableAlphaOutput;

                builder.SetRenderFunc(static (PostProcessingFinalFSRScalePassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var sourceTex = data.sourceTexture;
                    var destTex = data.destinationTexture;
                    var material = data.material;
                    var enableAlphaOutput = data.enableAlphaOutput;
                    RTHandle sourceHdl = (RTHandle)sourceTex;
                    RTHandle destHdl = (RTHandle)destTex;

                    var fsrInputSize = new Vector2(sourceHdl.referenceSize.x, sourceHdl.referenceSize.y);
                    var fsrOutputSize = new Vector2(destHdl.referenceSize.x, destHdl.referenceSize.y);
                    FSRUtils.SetEasuConstants(cmd, fsrInputSize, fsrInputSize, fsrOutputSize);

                    CoreUtils.SetKeyword(material, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, enableAlphaOutput);

                    Vector2 viewportScale = sourceHdl.useScaling ? new Vector2(sourceHdl.rtHandleProperties.rtHandleScale.x, sourceHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceHdl, viewportScale, material, 0);
                });
                return;
            }
        }

        private class PostProcessingFinalBlitPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal Material material;
            internal CameraData cameraData;
            internal FinalBlitSettings settings;
        }

        public struct FinalBlitSettings
        {
            /// <summary>Is FXAA enabled</summary>
            public bool isFxaaEnabled;
            /// <summary>Is FSR Enabled.</summary>
            public bool isFsrEnabled;
            /// <summary>Is TAA sharpening enabled.</summary>
            public bool isTaaSharpeningEnabled;
            /// <summary>True if final blit requires HDR output.</summary>
            public bool requireHDROutput;
            /// <summary>True if final blit needs to resolve to debug screen.</summary>
            public bool resolveToDebugScreen;
            /// <summary>True if final blit needs to output alpha channel.</summary>
            public bool isAlphaOutputEnabled;

            /// <summary>HDR Operations</summary>
            public HDROutputUtils.Operation hdrOperations;

            /// <summary>
            /// Create FinalBlitSettings
            /// </summary>
            /// <returns>New FinalBlitSettings</returns>
            public static FinalBlitSettings Create()
            {
                FinalBlitSettings s = new FinalBlitSettings();
                s.isFxaaEnabled = false;
                s.isFsrEnabled = false;
                s.isTaaSharpeningEnabled = false;
                s.requireHDROutput = false;
                s.resolveToDebugScreen = false;
                s.isAlphaOutputEnabled = false;

                s.hdrOperations = HDROutputUtils.Operation.None;

                return s;
            }
        };

        public void RenderFinalBlit(RenderGraph renderGraph, CameraData cameraData,
            in TextureHandle source, in TextureHandle overlayUITexture, in TextureHandle postProcessingTarget, ref FinalBlitSettings settings)
        {
            using (var builder = renderGraph.AddRasterRenderPass<PostProcessingFinalBlitPassData>("Postprocessing Final Blit Pass", out var passData, ProfilingSampler.Get(UltraRPProfileId.RG_FinalBlit)))
            {
                var finalPostMat = graph.materialLib.GetMaterial("finalPostPassPS");

                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = postProcessingTarget;
                builder.SetRenderAttachment(postProcessingTarget, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);
                passData.cameraData = cameraData;
                passData.material = finalPostMat;
                passData.settings = settings;

                if (settings.requireHDROutput && _enableColorEncodingIfNeeded && cameraData.rendersOverlayUI)
                    builder.UseTexture(overlayUITexture, AccessFlags.Read);

                builder.SetRenderFunc(static (PostProcessingFinalBlitPassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var material = data.material;
                    var isFxaaEnabled = data.settings.isFxaaEnabled;
                    var isFsrEnabled = data.settings.isFsrEnabled;
                    var isRcasEnabled = data.settings.isTaaSharpeningEnabled;
                    var requireHDROutput = data.settings.requireHDROutput;
                    var resolveToDebugScreen = data.settings.resolveToDebugScreen;
                    var isAlphaOutputEnabled = data.settings.isAlphaOutputEnabled;
                    RTHandle sourceTextureHdl = data.sourceTexture;
                    RTHandle destinationTextureHdl = data.destinationTexture;

                    PostProcessUtils.SetSourceSize(cmd, data.sourceTexture);

                    if (isFxaaEnabled)
                        material.EnableKeyword(ShaderKeywordStrings.Fxaa);

                    if (isFsrEnabled)
                    {
                        // RCAS
                        // Use the override value if it's available, otherwise use the default.
                        float sharpness = data.cameraData.fsrOverrideSharpness ? data.cameraData.fsrSharpness : FSRUtils.kDefaultSharpnessLinear;

                        // Set up the parameters for the RCAS pass unless the sharpness value indicates that it wont have any effect.
                        if (data.cameraData.fsrSharpness > 0.0f)
                        {
                            // RCAS is performed during the final post blit, but we set up the parameters here for better logical grouping.
                            material.EnableKeyword(requireHDROutput ? ShaderKeywordStrings.EasuRcasAndHDRInput : ShaderKeywordStrings.Rcas);
                            FSRUtils.SetRcasConstantsLinear(cmd, sharpness);
                        }
                    }
                    else if (isRcasEnabled)   // RCAS only
                    {
                        // Reuse RCAS as a standalone sharpening filter for TAA.
                        // If FSR is enabled then it overrides the sharpening/TAA setting and we skip it.
                        material.EnableKeyword(ShaderKeywordStrings.Rcas);
                        FSRUtils.SetRcasConstantsLinear(cmd, data.cameraData.taaSettings.contrastAdaptiveSharpening);
                    }

                    if (isAlphaOutputEnabled)
                        CoreUtils.SetKeyword(material, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, isAlphaOutputEnabled);

                    bool isRenderToBackBufferTarget = !data.cameraData.isSceneViewCamera;
#if FRAXINUSRP_ENABLE_XR && ENABLE_VR && ENABLE_XR_MODULE
                    if (data.cameraData.xr.enabled)
                        isRenderToBackBufferTarget = destinationTextureHdl == data.cameraData.xr.renderTarget;
#endif
                    // HDR debug views force-renders to DebugScreenTexture.
                    isRenderToBackBufferTarget &= !resolveToDebugScreen;

                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;

                    // We y-flip if
                    // 1) we are blitting from render texture to back buffer(UV starts at bottom) and
                    // 2) renderTexture starts UV at top
                    bool yflip = isRenderToBackBufferTarget && data.cameraData.targetTexture == null && SystemInfo.graphicsUVStartsAtTop;
                    Vector4 scaleBias = yflip ? new Vector4(viewportScale.x, -viewportScale.y, 0, viewportScale.y) : new Vector4(viewportScale.x, viewportScale.y, 0, 0);

                    cmd.SetViewport(data.cameraData.pixelRect);
                    Blitter.BlitTexture(cmd, sourceTextureHdl, scaleBias, material, 0);
                });

                return;
            }
        }

        public void RenderFinalPassRenderGraph(RenderGraph renderGraph, ContextContainer frameData,
            in TextureHandle source, in TextureHandle overlayUITexture, in TextureHandle postProcessingTarget, bool enableColorEncodingIfNeeded)
        {
            CameraData cameraData = frameData.Get<CameraData>();

            var stack = VolumeManager.instance.stack;
            _tonemapping = stack.GetComponent<Tonemapping>();

            var material = graph.materialLib.GetMaterial("finalPostPassPS");
            material.shaderKeywords = null;

            FinalBlitSettings settings = FinalBlitSettings.Create();

            // TODO RENDERGRAPH: when we remove the old path we should review the naming of these variables...
            // m_HasFinalPass is used to let FX passes know when they are not being called by the actual final pass, so they can skip any "final work"
            _hasFinalPass = false;
            // m_IsFinalPass is used by effects called by RenderFinalPassRenderGraph, so we let them know that we are in a final PP pass
            _enableColorEncodingIfNeeded = enableColorEncodingIfNeeded;

            // Upscalers run before grain, so the noise stays at display resolution
            // and SGSR2's history never accumulates it. Honor the graph's effect toggle.
            _filmGrain = stack.GetComponent<FilmGrain>();
            if (_filmGrain != null && _filmGrain.IsActive())
            {
                foreach (var effect in _cachedProcessors)
                {
                    if (effect.entry.componentType == typeof(FilmGrain) && effect.entry.enabled)
                    {
                        effect.processor.Setup(material, _filmGrain, this, cameraData);
                        break;
                    }
                }
            }

            if (RequireSRGBConversionBlitToBackBuffer(cameraData.requireSrgbConversion))
                material.EnableKeyword(ShaderKeywordStrings.LinearToSRGBConversion);

            settings.hdrOperations = HDROutputUtils.Operation.None;
            settings.requireHDROutput = RequireHDROutput(cameraData);

            if (settings.requireHDROutput)
            {
                // If there is a final post process pass, it's always the final pass so do color encoding
                settings.hdrOperations = _enableColorEncodingIfNeeded ? HDROutputUtils.Operation.ColorEncoding : HDROutputUtils.Operation.None;
                // If the color space conversion wasn't applied by the uber pass, do it here
                if (!cameraData.postProcessEnabled)
                    settings.hdrOperations |= HDROutputUtils.Operation.ColorConversion;

                SetupHDROutput(cameraData.hdrDisplayInformation, cameraData.hdrDisplayColorGamut, material, settings.hdrOperations, cameraData.rendersOverlayUI);
            }
            DebugHandler debugHandler = GetActiveDebugHandler(cameraData);
            bool resolveToDebugScreen = debugHandler != null && debugHandler.WriteToDebugScreenTexture(cameraData.resolveFinalTarget);
            debugHandler?.UpdateShaderGlobalPropertiesForFinalValidationPass(renderGraph, cameraData, !_hasFinalPass && !resolveToDebugScreen);

            settings.resolveToDebugScreen = resolveToDebugScreen;
            settings.isAlphaOutputEnabled = cameraData.isAlphaOutputEnabled;
            settings.isFxaaEnabled = (cameraData.antialiasing == AntialiasingMode.FastApproximateAntialiasing);
            settings.isFsrEnabled = ((cameraData.imageScalingMode == ImageScalingMode.Upscaling) && (cameraData.upscalingFilter == ImageUpscalingFilter.FSR));

            // Reuse RCAS pass as an optional standalone post sharpening pass for TAA.
            // This avoids the cost of EASU and is available for other upscaling options.
            // If FSR is enabled then FSR settings override the TAA settings and we perform RCAS only once.
            // If STP is enabled, then TAA sharpening has already been performed inside STP.
            settings.isTaaSharpeningEnabled = (cameraData.IsTemporalAAEnabled() && cameraData.taaSettings.contrastAdaptiveSharpening > 0.0f) && !settings.isFsrEnabled && !cameraData.IsSTPEnabled();

            var tempRtDesc = cameraData.cameraTargetDescriptor;
            tempRtDesc.msaaSamples = 1;
            tempRtDesc.depthStencilFormat = GraphicsFormat.None;

            // Select a UNORM format since we've already performed tonemapping. (Values are in 0-1 range)
            // This improves precision and is required if we want to avoid excessive banding when FSR is in use.
            if (!settings.requireHDROutput)
                tempRtDesc.graphicsFormat = UltraRenderPipeline.MakeUnormRenderTextureGraphicsFormat();

            var scalingSetupTarget = UltraRenderer.CreateRenderGraphTexture(renderGraph, tempRtDesc, "scalingSetupTarget", true, FilterMode.Point);

            var upscaleRtDesc = cameraData.cameraTargetDescriptor;
            upscaleRtDesc.msaaSamples = 1;
            upscaleRtDesc.depthStencilFormat = GraphicsFormat.None;
            upscaleRtDesc.width = cameraData.pixelWidth;
            upscaleRtDesc.height = cameraData.pixelHeight;

            var upScaleTarget = UltraRenderer.CreateRenderGraphTexture(renderGraph, upscaleRtDesc, "_UpscaledTexture", true, FilterMode.Point);

            var currentSource = source;
            if (cameraData.imageScalingMode != ImageScalingMode.None)
            {
                // When FXAA is enabled in scaled renders, we execute it in a separate blit since it's not designed to be used in
                // situations where the input and output resolutions do not match.
                // When FSR is active, we always need an additional pass since it has a very particular color encoding requirement.

                // NOTE: An ideal implementation could inline this color conversion logic into the UberPost pass, but the current code structure would make
                //       this process very complex. Specifically, we'd need to guarantee that the uber post output is always written to a UNORM format render
                //       target in order to preserve the precision of specially encoded color data.
                bool isSetupRequired = (settings.isFxaaEnabled || settings.isFsrEnabled);

                // When FXAA is needed while scaling is active, we must perform it before the scaling takes place.
                if (isSetupRequired)
                {
                    RenderFinalSetup(renderGraph, cameraData, in currentSource, in scalingSetupTarget, ref settings);
                    currentSource = scalingSetupTarget;

                    // Indicate that we no longer need to perform FXAA in the final pass since it was already perfomed here.
                    settings.isFxaaEnabled = false;
                }

                switch (cameraData.imageScalingMode)
                {
                    case ImageScalingMode.Upscaling:
                        {
                            switch (cameraData.upscalingFilter)
                            {
                                case ImageUpscalingFilter.Point:
                                    {
                                        // TAA post sharpening is an RCAS pass, avoid overriding it with point sampling.
                                        if (!settings.isTaaSharpeningEnabled)
                                            material.EnableKeyword(ShaderKeywordStrings.PointSampling);
                                        break;
                                    }
                                case ImageUpscalingFilter.Linear:
                                    {
                                        break;
                                    }
                                case ImageUpscalingFilter.FSR:
                                    {
                                        RenderFinalFSRScale(renderGraph, in currentSource, in upScaleTarget, settings.isAlphaOutputEnabled);
                                        currentSource = upScaleTarget;
                                        break;
                                    }
                                case ImageUpscalingFilter.SGSR1:
                                    {
                                        if (graph.materialLib.HasMaterial("sgsr1PS"))
                                        {
                                            SgsrUpscaling.RenderSpatial(renderGraph, graph.materialLib.GetMaterial("sgsr1PS"), currentSource, upScaleTarget);
                                            currentSource = upScaleTarget;
                                        }
                                        break;
                                    }
                                case ImageUpscalingFilter.SGSR2:
                                    {
                                        if (graph.materialLib.HasMaterial("sgsr2PS") &&
                                            SgsrUpscaling.RenderTemporal(renderGraph, graph.materialLib.GetMaterial("sgsr2PS"),
                                                cameraData, frameData.Get<ResourceData>(), currentSource, upScaleTarget))
                                            currentSource = upScaleTarget;
                                        break;
                                    }
                            }
                            break;
                        }
                    case ImageScalingMode.Downscaling:
                        {
                            // In the downscaling case, we don't perform any sort of filter override logic since we always want linear filtering
                            // and it's already the default option in the shader.

                            // Also disable TAA post sharpening pass when downscaling.
                            settings.isTaaSharpeningEnabled = false;
                            break;
                        }
                }
            }
            else if (settings.isFxaaEnabled)
            {
                // In unscaled renders, FXAA can be safely performed in the FinalPost shader
                material.EnableKeyword(ShaderKeywordStrings.Fxaa);
            }

            RenderFinalBlit(renderGraph, cameraData, in currentSource, in overlayUITexture, in postProcessingTarget, ref settings);
        }

        #endregion

        #region UberPost
        class UberPostPassData
        {
            internal BloomPass.UberSetupBloomPassData bloomData;
            internal TextureHandle sourceTexture;
            internal TextureHandle destinationTexture;
            internal TextureHandle lutTexture;
            internal Vector4 lutParams;
            internal TextureHandle userLut;
            internal Vector4 userLutParams;
            internal Material material;
            internal CameraData cameraData;
            internal TonemappingMode toneMappingMode;
            internal bool isHdrGrading;
            internal bool isBackbuffer;
            internal bool enableAlphaOutput;
            internal bool hasFinalPass;
        }

        private void RenderUberPost(RenderGraph renderGraph, ContextContainer frameData, CameraData cameraData, PostProcessingData postProcessingData,
            in TextureHandle sourceTexture, in TextureHandle destTexture, in TextureHandle overlayUITexture,
            bool requireHDROutput, bool enableAlphaOutput, bool resolveToDebugScreen, bool hasFinalPass)
        {
            var uberMat = graph.materialLib.GetMaterial("uberPostPS");

            bool hdrGrading = postProcessingData.gradingMode == ColorGradingMode.HighDynamicRange;
            int lutHeight = postProcessingData.lutSize;
            int lutWidth = lutHeight * lutHeight;

            float postExposureLinear = Mathf.Pow(2f, _colorAdjustments.postExposure.value);
            Vector4 lutParams = new Vector4(1f / lutWidth, 1f / lutHeight, lutHeight - 1f, postExposureLinear);

            bool colorLutValid = colorLut?.value != null;
            if (colorLutValid) colorLutValid &= colorLut.value.IsValid();

            // Snapshot the Volume values before deferred graph execution. A LUT
            // may itself be a render target, so declare its sampled dependency.
            var lookup = VolumeManager.instance.stack.GetComponent<ColorLookup>();
            TextureHandle userLut = renderGraph.defaultResources.blackTexture;
            Vector4 userLutParams = Vector4.zero;
            if (lookup != null && lookup.active && lookup.IsActive())
            {
                Texture texture = lookup.texture.value;
                if (!_userLutHandles.TryGetValue(texture, out var wrapper))
                {
                    wrapper = texture is RenderTexture target
                        ? RTHandles.Alloc(target, transferOwnership: false)
                        : RTHandles.Alloc(texture);
                    _userLutHandles.Add(texture, wrapper);
                }
                userLut = renderGraph.ImportTexture(wrapper);
                userLutParams = new Vector4(1f / texture.width, 1f / texture.height,
                    texture.height - 1f, lookup.contribution.value);
            }

            using (var builder = renderGraph.AddRasterRenderPass<UberPostPassData>("Blit Post Processing", out var passData, ProfilingSampler.Get(UltraRPProfileId.RG_UberPost)))
            {
                passData.bloomData = bloomData;
                if (bloomData.bloomTexture.IsValid())
                    builder.UseTexture(bloomData.bloomTexture, AccessFlags.Read);

                // LUT
                passData.lutParams = lutParams;
                passData.userLut = userLut;
                passData.userLutParams = userLutParams;
                builder.UseTexture(userLut, AccessFlags.Read);
                if (colorLutValid)
                {
                    builder.UseTexture(colorLut.value, AccessFlags.Read);
                    passData.lutTexture = colorLut.value;
                }

                if (requireHDROutput && _enableColorEncodingIfNeeded && overlayUITexture.IsValid())
                    builder.UseTexture(overlayUITexture, AccessFlags.Read);

                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = destTexture;
                builder.SetRenderAttachment(destTexture, 0, AccessFlags.Write);
                passData.sourceTexture = sourceTexture;
                builder.UseTexture(sourceTexture, AccessFlags.Read);
                passData.cameraData = cameraData;
                passData.material = uberMat;
                passData.toneMappingMode = _tonemapping.mode.value;
                passData.isHdrGrading = hdrGrading;
                passData.enableAlphaOutput = enableAlphaOutput;
                passData.hasFinalPass = hasFinalPass;

                builder.SetRenderFunc((UberPostPassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    var camera = data.cameraData.camera;
                    var material = data.material;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    material.SetTexture(ShaderConstants._InternalLut, data.lutTexture);
                    material.SetVector(ShaderConstants._Lut_Params, data.lutParams);
                    material.SetTexture(ShaderConstants._UserLut, data.userLut);
                    material.SetVector(ShaderConstants._UserLut_Params, data.userLutParams);

                    if (data.isHdrGrading)
                        material.EnableKeyword(ShaderKeywordStrings.HDRGrading);
                    else
                    {
                        switch (data.toneMappingMode)
                        {
                            case TonemappingMode.Neutral: material.EnableKeyword(ShaderKeywordStrings.TonemapNeutral); break;
                            case TonemappingMode.ACES: material.EnableKeyword(ShaderKeywordStrings.TonemapACES); break;
                            default: break; // None
                        }
                    }

                    CoreUtils.SetKeyword(material, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, data.enableAlphaOutput);

                    BloomPass.SetupUberMaterial(data.bloomData, material);

                    // Done with Uber, blit it
                    ScaleViewportAndBlit(cmd, sourceTextureHdl, data.destinationTexture, data.cameraData, material, data.hasFinalPass);
                });
            }
        }
        #endregion

        static private void ScaleViewportAndBlit(RasterCommandBuffer cmd, RTHandle sourceTextureHdl, RTHandle dest, CameraData cameraData, Material material, bool hasFinalPass)
        {
            Vector4 scaleBias = RenderingUtils.GetFinalBlitScaleBias(sourceTextureHdl, dest, cameraData);
            RenderTargetIdentifier cameraTarget = BuiltinRenderTextureType.CameraTarget;
            if (dest.nameID == cameraTarget || cameraData.targetTexture != null)
            {
                if (hasFinalPass || !cameraData.resolveFinalTarget)
                {
                    // Inside the camera stack the target is the shared intermediate target, which can be scaled with render scale.
                    // camera.pixelRect is the viewport of the final target in pixels, so it cannot be used for the intermediate target.
                    // On intermediate target allocation the viewport size is baked into the target size.
                    // Which means the intermediate target does not have a viewport rect. Its offset is always 0 and its size matches viewport size.
                    // The offsetting is finally done by the final output viewport-rect to the final target.
                    // Note: effectively this is setting a fullscreen viewport for the intermediate target.
                    var targetWidth = cameraData.cameraTargetDescriptor.width;
                    var targetHeight = cameraData.cameraTargetDescriptor.height;
                    var targetViewportInPixels = new Rect(
                        0,
                        0,
                        targetWidth,
                        targetHeight);
                    cmd.SetViewport(targetViewportInPixels);
                }
                else
                    cmd.SetViewport(cameraData.pixelRect);
            }


            Blitter.BlitTexture(cmd, sourceTextureHdl, scaleBias, material, 0);
        }

    }
}
