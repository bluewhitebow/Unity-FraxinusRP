using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline.Profiling.Pipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    public enum RenderingMode
    {
        None = 0,
        Forward = 1 << 0,
        Deferred = 1 << 1,
        ForwardPlus = 1 << 2,
        DeferredPlus = 1 << 3,
        DeferredCluster = 1 << 4, 
    };

    public enum RenderOrder
    {
        Default = -1,
        BeforeDrivenPass = 0
    }

    public enum DepthPrimingMode
    {
        /// <summary>Depth Priming will never be used.</summary>
        Disabled,
        /// <summary>Depth Priming will only be used if there is a depth prepass needed by any of the render passes.</summary>
        Auto,
    }

    public sealed partial class UltraRenderer : ScriptableRenderer
    {
#if ENABLE_VIRTUALTEXTURES
        readonly Dictionary<Camera, VTBufferManager> _virtualTextureCameras = new();
        readonly List<Camera> _retiredVirtualTextureCameras = new();
#endif
        FinalBlitPass _effectFinalBlit;
        // Base and overlay renderers share the camera-stack attachments below.
        private static bool s_effectRequiresFinalBlit;
        private static RTHandle[] s_renderGraphCameraColorHandles = new RTHandle[]
        {
            null, null
        };

        private static RTHandle s_renderGraphCameraDepthHandle;
        private static RTHandle s_renderGraphDebugTextureHandle;

        private static int s_currentColorHandle = 0;

        public override RTHandle currentRenderGraphCameraColorHandle
        {
            get
            {
                Debug.Assert(s_currentColorHandle >= 0,
                            "currentRenderGraphCameraColorHandle should not be accessed in single camera mode.");

                if (s_currentColorHandle < 0)
                    return null;

                return s_renderGraphCameraColorHandles[s_currentColorHandle];
            }
        }

        // get the next _renderGraphCameraColorHandles and make it the new current for future accesses
        public override RTHandle nextRenderGraphCameraColorHandle
        {
            get
            {
                Debug.Assert(s_currentColorHandle >= 0,
                            "nextRenderGraphCameraColorHandle should not be accessed in single camera mode.");

                if (s_currentColorHandle < 0)
                    return null;

                s_currentColorHandle = (s_currentColorHandle + 1) % 2;
                return currentRenderGraphCameraColorHandle;
            }
        }


        RTHandle _targetColorHandle;
        RTHandle _targetDepthHandle;

        // LightSystem
        LightCookieManager _lightCookieManager;
        ForwardLights _forwardLights;
        DeferredLights _deferredLights;

        // Materials used in UltraRP
        Material _cameraMotionVecMaterial = null;

        private bool _depthPrimingRecommended;
        private DepthPrimingMode _depthPrimingMode;
        DepthFormat _cameraDepthAttachmentFormat;
        DepthFormat _cameraDepthTextureFormat;
        public override GraphicsFormat cameraDepthTextureFormat { get => (_cameraDepthTextureFormat != DepthFormat.Default) ? (GraphicsFormat)_cameraDepthTextureFormat : depthStencilFormatDefault; }
        public override GraphicsFormat cameraDepthAttachmentFormat { get => (_cameraDepthAttachmentFormat != DepthFormat.Default) ? (GraphicsFormat)_cameraDepthAttachmentFormat : depthStencilFormatDefault; }

        public override bool accurateGbufferNormals => _deferredLights != null ? _deferredLights.accurateGbufferNormals : false;

        public override int SupportedCameraStackingTypes()
        {
            return 1 << (int)CameraRenderType.Base | 1 << (int)CameraRenderType.Overlay;
        }

        protected internal override bool SupportsMotionVectors()
        {
            // Motion vector pass for TAA and per-object motion blur (etc.) is available.
            return true;
        }

        internal bool SupportsTemporalAA()
        {
            return _cameraMotionVecMaterial != null && graph.postProcessData != null &&
                HasTemporalResolvePass(true);
        }

        internal bool SupportsTemporalUpscaling()
        {
            if (_cameraMotionVecMaterial == null || graph.postProcessData == null ||
                !graph.materialLib.HasMaterial("sgsr2PS"))
                return false;

            var sgsr2Material = graph.materialLib.GetMaterial("sgsr2PS");
            if (sgsr2Material == null || sgsr2Material.passCount <= TAAU_SGSR2.CopyHistoryPass)
                return false;

            return HasTemporalResolvePass(false);
        }

        private bool HasTemporalResolvePass(bool requireTemporalAA)
        {
            // A temporal camera must resolve in its main context. A child-only post pass
            // cannot consume the main camera's jitter or history.
            int contextDepth = 0;
            bool hasDrivenPass = false;
            for (int i = 0; i < _sortedNodes.Count; ++i)
            {
                var node = _sortedNodes[i];
                if (node.computeOrder < 0 || !node.canProcess || !(node is ScriptableRenderPassNode pass) || !pass.active)
                    continue;

                if (pass is ContextBeginNode)
                    ++contextDepth;
                else if (pass is ContextEndNode)
                    contextDepth = Mathf.Max(0, contextDepth - 1);
                else if (contextDepth == 0 && pass is RenderDrivenPass)
                    hasDrivenPass = true;
                else if (contextDepth == 0 && pass is PostProcessPass postProcessPass)
                {
                    if (!hasDrivenPass)
                        return false;
                    if (!requireTemporalAA || postProcessPass.temporalAAEnabled)
                        return true;
                }
            }

            return false;
        }

        public UltraRenderer(UltraRendererGraph graph) : base(graph)
        {
            PlatformAutoDetect.Initialize();

            // GBuffer attachments are single-sampled and clustered lighting writes
            // the color target as a UAV. Neither deferred path can share an MSAA target.
            supportedRenderingFeatures.msaa = !_sortedNodes.Exists(node => node is DeferredObjectPass pass && pass.active);

            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRendererResources>(
                out var ultraRendererShaders))
            {
                _cameraMotionVecMaterial = CoreUtils.CreateEngineMaterial(ultraRendererShaders.cameraMotionVector);
            }

            if (UltraRenderPipeline.asset?.supportsLightCookies ?? false)
            {
                var settings = LightCookieManager.Settings.Create();
                var asset = UltraRenderPipeline.asset;
                if (asset)
                {
                    settings.atlas.format = asset.additionalLightsCookieFormat;
                    settings.atlas.resolution = asset.additionalLightsCookieResolution;
                }

                _lightCookieManager = new LightCookieManager(ref settings);
            }

            graph.InitPostProcessData();

            var forwardInitParams = new ForwardLights.InitParams();
            forwardInitParams.lightCookieManager = _lightCookieManager;
            _forwardLights = SetModule<ForwardLights>(new ForwardLights(forwardInitParams));

            RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>(out var runtimeShaders);
            var deferredInitParams = new DeferredLights.InitParams();
            deferredInitParams.lightCookieManager = _lightCookieManager;
            deferredInitParams.runtimeShaders = runtimeShaders;
            deferredInitParams.stencilDeferredShader = ultraRendererShaders?.stencilDeferredPS;
            _deferredLights = SetModule<DeferredLights>(new DeferredLights(deferredInitParams));

#if UNITY_ANDROID || UNITY_IOS || UNITY_TVOS || UNITY_EMBEDDED_LINUX
            _depthPrimingRecommended = false;
#else
            _depthPrimingRecommended = true;
#endif

            _depthPrimingMode = graph.depthPrimingMode;

            this.stripShadowsOffVariants = true;
            this.stripAdditionalLightOffVariants = true;
        }

        protected override void Dispose(bool dispose)
        {
#if ENABLE_VIRTUALTEXTURES
            foreach (var manager in _virtualTextureCameras.Values)
                manager.Cleanup();
            _virtualTextureCameras.Clear();
#endif
            _effectFinalBlit?.Dispose();
            base.Dispose(dispose);

            _targetColorHandle?.Release();
            _targetDepthHandle?.Release();

            CoreUtils.Destroy(_cameraMotionVecMaterial);

            CleanupRenderGraphResources();
        }

        public override void OnBeginRenderGraphFrame()
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            resourceData.InitFrame();
        }

        public override void OnEndRenderGraphFrame()
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            resourceData.EndFrame();
        }

        protected override void OnFinishRenderGraphRendering(CommandBuffer cmd)
        {

        }

        public override void StartRenderGraphRendering(in CameraData cameraData)
        {
            var interalRendererData = frameData.Create<InternalRendererData>();

            _renderingMode = RenderingMode.None;

            DeferredLightingMode? deferredMode = null;
            LayerMask opaqueLayerMask = 0;
            RenderPassInputSummary inputSummary = new RenderPassInputSummary();
            for (int i = 0; i < _sortedNodes.Count; ++i)
            {
                var node = _sortedNodes[i];
                if (node.computeOrder >= 0 && node.canProcess && node is ScriptableRenderPassNode pass && pass.active)
                {
                    bool needsDepth = (pass.input & ScriptableRenderPassInput.Depth) != ScriptableRenderPassInput.None;
                    bool needsNormals = (pass.input & ScriptableRenderPassInput.Normal) != ScriptableRenderPassInput.None;
                    bool needsColor = (pass.input & ScriptableRenderPassInput.Color) != ScriptableRenderPassInput.None;
                    bool needsMotion = (pass.input & ScriptableRenderPassInput.Motion) != ScriptableRenderPassInput.None;

                    inputSummary.requiresIntermediateTexture |= pass.requiresIntermediateTexture;
                    inputSummary.requiresColorTextureCreated = true;
                    inputSummary.requiresDepthTexture |= needsDepth;

                    inputSummary.requiresDepthPrepass |= needsNormals || needsDepth;

                    inputSummary.requiresNormalsTexture |= needsNormals;
                    inputSummary.requiresColorTexture |= needsColor;
                    inputSummary.requiresMotionVectors |= needsMotion;

                    bool mainContext = graph.drivenNode == null || IsSameContext(pass, graph.drivenNode);
                    if (!(pass is DeferredObjectPass) || mainContext)
                        _renderingMode |= pass.RequestRenderingMode();
                    // GBuffer layout and lighting mode are camera-wide. Child
                    // scopes inherit the main scope instead of overriding it.
                    if (pass is DeferredObjectPass deferred && mainContext)
                    {
                        if (deferredMode.HasValue && deferredMode.Value != deferred.lightingMode)
                            throw new InvalidOperationException("Deferred passes in one camera must use the same lighting mode.");
                        deferredMode = deferred.lightingMode;
                        if (deferred.cluster && deferred.clusterLightingMode != ClusterLightingMode.Auto)
                        {
                            if (interalRendererData.clusterLightingMode != ClusterLightingMode.Auto &&
                                interalRendererData.clusterLightingMode != deferred.clusterLightingMode)
                                throw new InvalidOperationException("Cluster deferred passes in one camera must use the same explicit lighting mode.");
                            interalRendererData.clusterLightingMode = deferred.clusterLightingMode;
                        }
                    }
                    opaqueLayerMask |= pass.GetOpaqueLayerMask();
                }
            }

            if (cameraData.renderType != CameraRenderType.Base)
            {
                if ((_renderingMode & RenderingMode.DeferredPlus) != 0)
                    _renderingMode |= RenderingMode.ForwardPlus;
                _renderingMode &= ~(RenderingMode.Deferred | RenderingMode.DeferredPlus | RenderingMode.DeferredCluster);
            }

            interalRendererData.renderPassInputs = inputSummary;
            interalRendererData.opaqueLayerMask = opaqueLayerMask;
            interalRendererData.hasForwardPlus = isForwardPlus;
            interalRendererData.hasDeferred = (_renderingMode & (RenderingMode.Deferred | RenderingMode.DeferredPlus | RenderingMode.DeferredCluster)) != 0;
            interalRendererData.deferredLightingMode = deferredMode ?? DeferredLightingMode.Stencil;
        }

        public override void SetupCullingParameters(ref ScriptableCullingParameters cullingParameters, ref CameraData cameraData)
        {
            InternalRendererData internalData = frameData.Get<InternalRendererData>();

            // TODO: PerObjectCulling also affect reflection probes. Enabling it for now.
            // if (asset.additionalLightsRenderingMode == LightRenderingMode.Disabled ||
            //     asset.maxAdditionalLightsCount == 0)
            if (internalData.usesForwardPlusLightLists && UltraRenderPipeline.asset.reflectionProbeBlending)
                cullingParameters.cullingOptions |= CullingOptions.DisablePerObjectCulling;

            // We disable shadow casters if both shadow casting modes are turned off
            // or the shadow distance has been turned down to zero
            bool isShadowCastingDisabled = !UltraRenderPipeline.asset.supportsMainLightShadows && !UltraRenderPipeline.asset.supportsAdditionalLightShadows;
            bool isShadowDistanceZero = Mathf.Approximately(cameraData.maxShadowDistance, 0.0f);
            if (isShadowCastingDisabled || isShadowDistanceZero)
                cullingParameters.cullingOptions &= ~CullingOptions.ShadowCasters;

            if (internalData.usesForwardPlusLightLists)
            {
                // We don't add one to the maximum light because mainlight is treated as any other light.
                cullingParameters.maximumVisibleLights = UltraRenderPipeline.maxVisibleAdditionalLights;
                // Do not sort reflection probe from engine it will come in reverse order from what we need.
                cullingParameters.reflectionProbeSortingCriteria = ReflectionProbeSortingCriteria.None;
            }
            else if (internalData.hasDeferred)
                cullingParameters.maximumVisibleLights = 0xFFFF;
            else
            {
                // We set the number of maximum visible lights allowed and we add one for the mainlight...
                //
                // Note: However ScriptableRenderContext.Cull() does not differentiate between light types.
                //       If there is no active main light in the scene, ScriptableRenderContext.Cull() might return  ( cullingParameters.maximumVisibleLights )  visible additional lights.
                //       i.e ScriptableRenderContext.Cull() might return  ( UltraRenderPipeline.maxVisibleAdditionalLights + 1 )  visible additional lights !
                cullingParameters.maximumVisibleLights = UltraRenderPipeline.maxVisibleAdditionalLights + 1;
            }

            cullingParameters.shadowDistance = cameraData.maxShadowDistance;
            cullingParameters.conservativeEnclosingSphere = UltraRenderPipeline.asset.conservativeEnclosingSphere;
            cullingParameters.numIterationsEnclosingSphere = UltraRenderPipeline.asset.numIterationsEnclosingSphere;
        }

        public override void OnPreCullRenderPasses(in CameraData cameraData)
        {
            for (int executionIndex = 0; executionIndex < _sortedNodes.Count; executionIndex++)
            {
                var node = _sortedNodes[executionIndex];
                if (node.computeOrder < 0 || !node.canProcess)
                    continue;

                node.excuteOrder = executionIndex;
                if (node is ScriptableRenderPassNode pass)
                    pass.OnPreCull(this, cameraData);
            }
        }

        public override void OnRecordRenderGraph(RenderGraph renderGraph, ScriptableRenderContext context)
        {
            InternalRendererData internalData       = frameData.Get<InternalRendererData>();
            ResourceData resourceData               = frameData.Get<ResourceData>();
            RenderingData renderingData             = frameData.Get<RenderingData>();
            CameraData cameraData                   = frameData.Get<CameraData>();
            LightData lightData                     = frameData.Get<LightData>();

            useRenderPassEnabled = renderGraph.nativeRenderPassesEnabled;

            CollectRenderingData(cameraData.IsTemporalJitterEnabled(), cameraData.postProcessEnabled, cameraData.isSceneViewCamera, ref internalData, renderingData);
            bool injectedEffectsRequireFinalBlit = PrepareInjectPasses(renderGraph, frameData);
            GetModule<AreaLightsPass>().PrepareFrame(renderGraph, frameData);
            if (cameraData.renderType == CameraRenderType.Base)
                s_effectRequiresFinalBlit = injectedEffectsRequireFinalBlit;
            if (cameraData.isSceneViewCamera)
                renderingData.opaqueLayerMask = -1;

            bool isCameraTargetOffscreenDepth = IsOffscreenDepthTexture(cameraData);
            if (isCameraTargetOffscreenDepth)
            {
                // This path still draws material Forward passes, including on the
                // first camera with no visible lights. RenderDrivenPass is skipped,
                // so establish its lighting policy and Forward+ resources here.
                _forwardLights.useforwardLight = renderingData.usesForwardPlusLightLists;
                _forwardLights.SetupRenderGraphLights(renderGraph, frameData);
                ImportOffscreenDepthTarget(renderGraph, resourceData, cameraData);
                OnOffscreenDepthTextureRendering(renderGraph, context, resourceData, cameraData);
                return;
            }

            MotionVectorRenderPass.SetRenderGraphMotionVectorGlobalMatrices(renderGraph, cameraData);
            SetupRenderGraphLights(renderGraph, renderingData, cameraData, lightData);
            SetupRenderingLayers(cameraData.cameraTargetDescriptor.msaaSamples);

            CreateRenderGraphCameraRenderTargets(renderGraph, isCameraTargetOffscreenDepth, ref renderingData.renderPassInputs);

#if ENABLE_VIRTUALTEXTURES
            VTBufferManager virtualTextureManager = BeginVirtualTexturing(renderGraph, cameraData, resourceData);
#endif
            bool hasMainPostProcessPass = false;

            if (debugHandler != null)
                debugHandler.Setup(renderGraph, cameraData.isPreviewCamera);

            RenderOrder renderOrder = RenderOrder.BeforeDrivenPass;
            for (int executionIndex = 0; executionIndex < _sortedNodes.Count; executionIndex++)
            {
                var node = _sortedNodes[executionIndex];
                if (node.computeOrder < 0 || !node.canProcess)
                    continue;

                node.excuteOrder = executionIndex;
                if (node is ScriptableRenderPassNode pass)
                {
                    if (renderOrder != pass.renderOrder)
                    {
                        // The camera need to be setup again after the shadows since those passes override some settings
                        // TODO RENDERGRAPH: move the setup code into the shadow passes
                        if (renderOrder == RenderOrder.BeforeDrivenPass)
                        {
                            SetupRenderGraphCameraProperties(renderGraph, frameData.Get<ResourceData>().isActiveTargetBackBuffer);
                        }

                        renderOrder = pass.renderOrder;
                    }

                    hasMainPostProcessPass |= pass.active && pass is PostProcessPass &&
                        !frameData.Get<CameraData>().isSubContext;
                    pass.OnProcess(renderGraph, context, frameData);
                }
                else
                    node.OnProcess();
            }

            if (frameData.Get<ContextStateManager>().IsInSubContext())
                throw new InvalidOperationException("A ContextBegin node was not closed before the camera finished recording.");

#if ENABLE_VIRTUALTEXTURES
            virtualTextureManager?.Resolve(renderGraph, resourceData.virtualTextureFeedback, cameraData.cameraTargetDescriptor);
            s_effectRequiresFinalBlit |= virtualTextureManager != null;
#endif

            // Effects can require an intermediate target even when a graph has no
            // post-processing output node. Present that target for these graphs too.
            if (s_effectRequiresFinalBlit && !hasMainPostProcessPass &&
                cameraData.resolveFinalTarget && !resourceData.isActiveTargetBackBuffer)
            {
                _effectFinalBlit ??= new FinalBlitPass();
                _effectFinalBlit.Render(renderGraph, frameData, cameraData,
                    resourceData.cameraColor, resourceData.backBufferColor, resourceData.overlayUITexture);
                resourceData.activeColorID = ResourceData.ActiveID.BackBuffer;
                resourceData.activeDepthID = ResourceData.ActiveID.BackBuffer;
            }
            if (cameraData.resolveFinalTarget)
                s_effectRequiresFinalBlit = false;
        }

#if ENABLE_VIRTUALTEXTURES
        VTBufferManager BeginVirtualTexturing(RenderGraph renderGraph, CameraData cameraData, ResourceData resourceData)
        {
            _retiredVirtualTextureCameras.Clear();
            double now = Time.realtimeSinceStartupAsDouble;
            foreach (var pair in _virtualTextureCameras)
            {
                if (pair.Key == null || now - pair.Value.lastUsedTime > 60.0)
                {
                    pair.Value.Cleanup();
                    _retiredVirtualTextureCameras.Add(pair.Key);
                }
            }
            foreach (var camera in _retiredVirtualTextureCameras)
                _virtualTextureCameras.Remove(camera);

            if (!VTBufferManager.IsSupported(cameraData))
                return null;

            if (!_virtualTextureCameras.TryGetValue(cameraData.camera, out var manager))
            {
                manager = new VTBufferManager(cameraData.camera);
                _virtualTextureCameras.Add(cameraData.camera, manager);
            }

            manager.BeginRender(cameraData.cameraTargetDescriptor);
            resourceData.virtualTextureFeedback = VTBufferManager.CreateVTFeedbackBuffer(renderGraph, cameraData.cameraTargetDescriptor);
            return manager;
        }
#endif

        // Skip native render passes for the first few frames to avoid transient attachment creation failures
        // during editor/runtime initialization (e.g. D3D12 sRGB transient attachment not ready on first frame).
        private static int s_framesSinceInit = 0;
        private const int k_nativeRenderPassWarmupFrames = 4;

        internal override bool useNativeRenderPassRenderGraphCompiler
        {
            get
            {
                if (!graph.useNativePassRenderGraphCompile)
                    return false;

                if (s_framesSinceInit < k_nativeRenderPassWarmupFrames)
                {
                    s_framesSinceInit++;
                    return false;
                }

                return true;
            }
        }

        private bool _issuedGPUOcclusionUnsupportedMsg = false;
        public override bool supportsGPUOcclusion
        {
            get
            {
                // UUM-82677: GRD GPU Occlusion Culling on Vulkan breaks rendering on some mobile GPUs
                //
                // We currently disable gpu occlusion culling when running on Qualcomm GPUs due to suspected driver issues.
                // Once the issue is resolved, this logic should be removed.
                const int kQualcommVendorId = 0x5143;
                bool isGpuSupported = SystemInfo.graphicsDeviceVendorID != kQualcommVendorId;

                if (!isGpuSupported && !_issuedGPUOcclusionUnsupportedMsg)
                {
                    Debug.LogWarning("The GPU Occlusion Culling feature is currently unavailable on this device due to suspected driver issues.");
                    _issuedGPUOcclusionUnsupportedMsg = true;
                }

                return isGpuSupported;
            }
        }

        public override bool isForwardPlus => (_renderingMode & RenderingMode.ForwardPlus) != 0; 
        public override bool usesForwardPlusLightLists => (_renderingMode & (RenderingMode.ForwardPlus | RenderingMode.DeferredPlus)) != 0;

        private void SetupRenderGraphLights(RenderGraph renderGraph, RenderingData renderingData, CameraData cameraData, LightData lightData)
        {
            _forwardLights.useforwardLight = renderingData.usesForwardPlusLightLists;
            _forwardLights.SetupRenderGraphLights(renderGraph, frameData);
            if (renderingData.hasDeferred)
            {
                // Choose the lighting path before attachment layout and camera targets.
                _deferredLights.ConfigureLightingPath(renderGraph.nativeRenderPassesEnabled,
                    renderingData.deferredLightingMode, renderingData.clusterLightingMode,
                    renderingData.renderPassInputs.disableFramebufferFetch);
                _deferredLights.SetupRenderGraphLights(renderGraph, cameraData, lightData);
            }
        }

        private void SetupRenderingLayers(int msaaSamples)
        {
            renderinglayerSetting.requiresRenderingLayer = RenderingLayerUtils.RequireRenderingLayers(this, _sortedNodes, msaaSamples,
                out renderinglayerSetting.renderingLayersEvent, out renderinglayerSetting.renderingLayersMaskSize);

            renderinglayerSetting.renderingLayerProvidesRenderObjectPass = renderinglayerSetting.requiresRenderingLayer && renderinglayerSetting.renderingLayersEvent == RenderingLayerUtils.Event.Opaque;
            renderinglayerSetting.renderingLayerProvidesByDepthNormalPass = renderinglayerSetting.requiresRenderingLayer && renderinglayerSetting.renderingLayersEvent == RenderingLayerUtils.Event.DepthNormalPrePass;

            if (_deferredLights != null)
            {
                _deferredLights.renderingLayerMaskSize = renderinglayerSetting.renderingLayersMaskSize;
                _deferredLights.useDecalLayers = renderinglayerSetting.requiresRenderingLayer;
            }

            var depthNormalPrepass = GetModule<DepthNormalOnlyPass>();
            if (renderinglayerSetting.renderingLayerProvidesByDepthNormalPass)
            {
                depthNormalPrepass.enableRenderingLayers = true;
                depthNormalPrepass.renderingLayersMaskSize = renderinglayerSetting.renderingLayersMaskSize;
            }
            else
                depthNormalPrepass.enableRenderingLayers = false;
        }

        private void OnOffscreenDepthTextureRendering(RenderGraph renderGraph, ScriptableRenderContext context, ResourceData resourceData, CameraData cameraData)
        {
            // Like URP's depth-target path, these are material Forward draws.
            // Run the declared shadow passes (including their empty-map setup),
            // so no-light and zero-shadow-distance cameras use the same keywords
            // as regular cameras. Reuse the graph-owned passes and their lifetime.
            TextureHandle mainShadows = TextureHandle.nullHandle;
            TextureHandle additionalShadows = TextureHandle.nullHandle;
            for (int i = 0; i < _sortedNodes.Count; ++i)
            {
                var node = _sortedNodes[i];
                if (node.computeOrder < 0 || !node.canProcess ||
                    !(node is ScriptableRenderPassNode pass) || !pass.active ||
                    !pass.SupportedInCurrentFrame(frameData))
                    continue;

                // Custom subclasses can change Execute/SetupPass behavior and
                // do not participate in the built-in shadow stripping contract.
                if (node.GetType() == typeof(MainLightShadowCasterPass))
                    mainShadows = ((MainLightShadowCasterPass)node).Render(renderGraph, frameData);
                else if (node.GetType() == typeof(AdditionalLightsShadowCasterPass))
                    additionalShadows = ((AdditionalLightsShadowCasterPass)node).Render(renderGraph, frameData);
            }

            // Shadow rendering changes view/projection state; restore the camera
            // before recording the depth-target draw, just as in the regular path.
            SetupRenderGraphCameraProperties(renderGraph, true);
            RenderingData renderingData = frameData.Get<RenderingData>();
            LayerMask transparentLayers = 0;
            for (int i = 0; i < _sortedNodes.Count; ++i)
            {
                if (_sortedNodes[i] is DrawObjectsPass pass && pass.active && pass.canProcess &&
                    pass.renderQueueType != DrawObjectsPass.RenderQueueType.Opaque)
                    transparentLayers |= pass.layerMask;
            }

            OffscreenDepthPass.Render(renderGraph, frameData, resourceData.backBufferDepth,
                renderingData.opaqueLayerMask, transparentLayers, mainShadows, additionalShadows);
        }

        const string k_cameraTargetAttachmentAName = "_CameraTargetAttachmentA";
        const string k_cameraTargetAttachmentBName = "_CameraTargetAttachmentB";
        const string k_singleCameraTargetAttachmentName = "_CameraTargetAttachment";
        const string k_cameraDepthAttachmentName = "_CameraDepthAttachment";

        private void CreateRenderGraphCameraRenderTargets(RenderGraph renderGraph, bool isCameraTargetOffscreenDepth, ref RenderPassInputSummary inputSummary)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();
            RenderingData renderingData = frameData.Get<RenderingData>();

            if (isCameraTargetOffscreenDepth)
            {
                ImportOffscreenDepthTarget(renderGraph, resourceData, cameraData);
                return;
            }

            bool lastCameraInTheStack = cameraData.resolveFinalTarget;
            bool isBuiltInTexture = (cameraData.targetTexture == null);

            RenderTargetIdentifier targetColorId = cameraData.targetTexture != null ? new RenderTargetIdentifier(cameraData.targetTexture) : BuiltinRenderTextureType.CameraTarget;
#if UNITY_6000_0_OR_NEWER
            RenderTargetIdentifier targetDepthId = cameraData.targetTexture != null ? new RenderTargetIdentifier(cameraData.targetTexture) : BuiltinRenderTextureType.Depth;
#else
            // Keep CameraTarget for regular and unsafe SetRenderTarget calls.
            // The native compiler selects its depth surface when submitting
            // a native attachment, without changing this shared resource handle.
            RenderTargetIdentifier targetDepthId = targetColorId;
#endif

            bool clearColor = cameraData.renderType == CameraRenderType.Base;
            bool clearDepth = cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth;
            Color cameraBackgroundColor = (cameraData.camera.clearFlags == CameraClearFlags.Nothing && cameraData.targetTexture == null) ? Color.yellow : cameraData.backgroundColor;

            // If scene filtering is enabled (prefab edit mode), the filtering is implemented compositing some builtin ImageEffect passes.
            // For the composition to work, we need to clear the color buffer alpha to 0
            // How filtering works:
            // - SRP frame is fully rendered as background
            // - builtin ImageEffect pass grey-out of the full scene previously rendered
            // - SRP frame rendering only the objects belonging to the prefab being edited (with clearColor.a = 0)
            // - builtin ImageEffect pass compositing the two previous passes
            // TODO: We should implement filtering fully in SRP to remove builtin dependencies
            if (IsSceneFilteringEnabled(cameraData.camera))
            {
                cameraBackgroundColor.a = 0;
                clearDepth = false;
            }

            var debugHandler = cameraData.renderer.debugHandler;
            if (debugHandler != null && debugHandler.IsActiveForCamera(cameraData.isPreviewCamera) && debugHandler.IsScreenClearNeeded)
            {
                clearColor = true;
                clearDepth = true;
                if ((debugHandler != null) && debugHandler.IsActiveForCamera(cameraData.isPreviewCamera))
                    debugHandler.TryGetScreenClearColor(ref cameraBackgroundColor);
            }

            if (_targetColorHandle == null)
                _targetColorHandle = RTHandles.Alloc(targetColorId, "Backbuffer color");
            else if (_targetColorHandle.nameID != targetColorId)
                RTHandleStaticHelpers.SetRTHandleUserManagedWrapper(ref _targetColorHandle, targetColorId);

            if (_targetDepthHandle == null)
                _targetDepthHandle = RTHandles.Alloc(targetDepthId, "Backbuffer depth");
            else if (_targetDepthHandle.nameID != targetDepthId)
                RTHandleStaticHelpers.SetRTHandleUserManagedWrapper(ref _targetDepthHandle, targetDepthId);

            // Enable depth normal prepass if it's needed by rendering layers
            if (renderinglayerSetting.renderingLayerProvidesByDepthNormalPass)
                inputSummary.requiresNormalsTexture = true;

            // Gather render pass history requests and update history textures.
            UpdateCameraHistory(cameraData);

            // A stack shares attachments, but each camera needs its own depth
            // prepass and priming state before issuing opaque draws.
            useDepthPriming = IsDepthPrimingEnabled(cameraData);
            renderingData.requiresDepthPrepass = RequireDepthPrepass(cameraData, ref inputSummary);

            // We configure this for the first camera of the stack and overlay camera will reuse create color/depth var
            // to pick the correct target, as if there is an intermediate texture, overlay cam should use them
            if (cameraData.renderType == CameraRenderType.Base)
                requiresIntermediateAttachments = RequiresIntermediateAttachments(cameraData, renderingData);

            // The final output back buffer should be cleared by the graph on first use only if we have no final blit pass.
            // If there is a final blit, that blit will write the buffers so on first sight an extra clear should not be problem,
            // blit will simply blit over the cleared values. BUT! the final blit may not write the whole buffer in case of a camera
            // with a Viewport Rect smaller than the full screen. So the existing backbuffer contents need to be preserved in this case.
            // Finally for non-base cameras the backbuffer should never be cleared. (Note that there might still be two base cameras
            // rendering to the same screen. See e.g. test foundation 014 that renders a minimap)
            bool clearBackbufferOnFirstUse = (cameraData.renderType == CameraRenderType.Base) && !requiresIntermediateAttachments;
            
            // force the clear if we are rendering to an offscreen depth texture
            clearBackbufferOnFirstUse |= isCameraTargetOffscreenDepth;

            // UI Overlay is rendered by native engine if not done within SRP
            // To check if the engine does it natively post-UltraRP, we look at SupportedRenderingFeatures
            // and restrict it to cases where we resolve to screen and render UI overlay, i.e mostly final camera for game view
            // We cannot use directly !cameraData.rendersOverlayUI but this is similar logic
            bool isNativeUIOverlayRenderingAfterUltraRP = !SupportedRenderingFeatures.active.rendersUIOverlay && cameraData.resolveToScreen;
#if UNITY_6000_0_OR_NEWER
            bool isNativeRenderingAfterUltraRP = UnityEngine.Rendering.Watermark.IsVisible() || isNativeUIOverlayRenderingAfterUltraRP;
#else
            bool isNativeRenderingAfterUltraRP = isNativeUIOverlayRenderingAfterUltraRP;
#endif
            // If MSAA > 1, no extra native rendering after SRP and we target the BB directly (!m_CreateColorTexture)
            // then we can discard MSAA buffers and only resolve, otherwise we must store and resolve
            bool noStoreOnlyResolveBBColor = !requiresIntermediateAttachments && !isNativeRenderingAfterUltraRP && (cameraData.cameraTargetDescriptor.msaaSamples > 1);

            ImportResourceParams importBackbufferColorParams = new ImportResourceParams();
            importBackbufferColorParams.clearOnFirstUse = clearBackbufferOnFirstUse;
            importBackbufferColorParams.clearColor = cameraBackgroundColor;
            importBackbufferColorParams.discardOnLastUse = noStoreOnlyResolveBBColor;

            ImportResourceParams importBackbufferDepthParams = new ImportResourceParams();
            importBackbufferDepthParams.clearOnFirstUse = clearBackbufferOnFirstUse;
            importBackbufferDepthParams.clearColor = cameraBackgroundColor;
            importBackbufferDepthParams.discardOnLastUse = !isCameraTargetOffscreenDepth;

#if UNITY_EDITOR
            // on TBDR GPUs like Apple M1/M2, we need to preserve the backbuffer depth for overlay cameras in Editor for Gizmos
            if (cameraData.isSceneViewCamera)
                importBackbufferDepthParams.discardOnLastUse = false;
#endif
            // For BuiltinRenderTextureType wrapping RTHandles RenderGraph can't know what they are so we have to pass it in.
            RenderTargetInfo importInfo = new RenderTargetInfo();
            RenderTargetInfo importInfoDepth = new RenderTargetInfo();

            // So the render target we pass into render graph is
            // RTHandles(RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget))
            // or
            // RTHandles(RenderTargetIdentifier(RenderTexture(cameraData.targetTexture)))
            //
            // Because of the RenderTargetIdentifier in the "wrapper chain" the graph can't know
            // the size of the passed in textures so we need to provide it for the graph.
            // The amount of texture handle wrappers and their subtleties is probably something to be investigated.
            if (isBuiltInTexture)
            {
                // Backbuffer is the final render target, we obtain its number of MSAA samples through Screen API
                // in some cases we disable multisampling for optimization purpose
                int numSamples = AdjustAndGetScreenMSAASamples(renderGraph, requiresIntermediateAttachments);

                // BuiltinRenderTextureType.CameraTarget so this is either system render target or camera.targetTexture if non null
                // NOTE: Careful what you use here as many of the properties bake-in the camera rect so for example
                // cameraData.cameraTargetDescriptor.width is the width of the rectangle but not the actual render target
                // same with cameraData.camera.pixelWidth
                importInfo.width = Screen.width;
                importInfo.height = Screen.height;
                importInfo.volumeDepth = 1;
                importInfo.msaaSamples = numSamples;
                importInfo.format = UltraRenderPipeline.MakeRenderTextureGraphicsFormat(cameraData.isHdrEnabled, cameraData.hdrColorBufferPrecision, Graphics.preserveFramebufferAlpha);

                importInfoDepth = importInfo;
                importInfoDepth.format = SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil);
            }
            else
            {
                importInfo.width = cameraData.targetTexture.width;
                importInfo.height = cameraData.targetTexture.height;
                importInfo.volumeDepth = cameraData.targetTexture.volumeDepth;
                importInfo.msaaSamples = cameraData.targetTexture.antiAliasing;
                importInfo.format = cameraData.targetTexture.graphicsFormat;

                importInfoDepth = importInfo;
                importInfoDepth.format = cameraData.targetTexture.depthStencilFormat;

                // We let users know that a depth format is required for correct usage, but we fallback to the old default depth format behaviour to avoid regressions
                if (importInfoDepth.format == GraphicsFormat.None)
                {
                    importInfoDepth.format = SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil);
                    Debug.LogWarning("Trying to render to a rendertexture without a depth buffer. UltraRP+RG needs a depthbuffer to render.");
                }
            }

            if (!isCameraTargetOffscreenDepth)
                resourceData.backBufferColor = renderGraph.ImportTexture(_targetColorHandle, importInfo, importBackbufferColorParams);

            resourceData.backBufferDepth = renderGraph.ImportTexture(_targetDepthHandle, importInfoDepth, importBackbufferDepthParams);

            #region Intermediate Camera Target

            if (requiresIntermediateAttachments && !isCameraTargetOffscreenDepth)
            {
                var cameraTargetDescriptor = cameraData.cameraTargetDescriptor;
                cameraTargetDescriptor.useMipMap = false;
                cameraTargetDescriptor.autoGenerateMips = false;
                cameraTargetDescriptor.depthBufferBits = (int)DepthBits.None;

                var deferredLights = GetModule<DeferredLights>();
                cameraTargetDescriptor.enableRandomWrite = deferredLights.cluster && !deferredLights.useClusterRaster;

                // When there's a single camera setup, there's no need to do the double buffer technique with attachment A/B, in order to save memory allocation
                // and simplify the workflow by using a RenderGraph texture directly.
                var isSingleCamera = cameraData.resolveFinalTarget && cameraData.renderType == CameraRenderType.Base;
                if (isSingleCamera)
                {
                    resourceData.cameraColor = CreateRenderGraphTexture(renderGraph, cameraTargetDescriptor, k_singleCameraTargetAttachmentName, clearColor, cameraBackgroundColor, FilterMode.Bilinear, discardOnLastUse: cameraData.resolveFinalTarget);

                    s_currentColorHandle = -1;
                }
                else
                {
                    RenderingUtils.ReAllocateHandleIfNeeded(ref s_renderGraphCameraColorHandles[0], cameraTargetDescriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: k_cameraTargetAttachmentAName);
                    RenderingUtils.ReAllocateHandleIfNeeded(ref s_renderGraphCameraColorHandles[1], cameraTargetDescriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: k_cameraTargetAttachmentBName);

                    // Make sure that the base camera always starts rendering to the ColorAttachmentA for deterministic frame results.
                    // Not doing so makes the targets look different every frame, causing the frame debugger to flash, and making debugging harder.
                    if (cameraData.renderType == CameraRenderType.Base)
                        s_currentColorHandle = 0;

                    ImportResourceParams importColorParams = new ImportResourceParams();
                    importColorParams.clearOnFirstUse = clearColor;
                    importColorParams.clearColor = cameraBackgroundColor;
                    importColorParams.discardOnLastUse = cameraData.resolveFinalTarget; // Last camera in stack
                    resourceData.cameraColor = renderGraph.ImportTexture(currentRenderGraphCameraColorHandle, importColorParams);
                }

                resourceData.activeColorID = ResourceData.ActiveID.Camera;
            }
            else
                resourceData.activeColorID = ResourceData.ActiveID.BackBuffer;


            bool depthTextureIsDepthFormat = RequireDepthPrepass(cameraData, ref inputSummary) && (!renderingData.hasDeferred);

            if (requiresIntermediateAttachments)
            {
                var depthDescriptor = cameraData.cameraTargetDescriptor;
                depthDescriptor.useMipMap = false;
                depthDescriptor.autoGenerateMips = false;

                bool hasMSAA = depthDescriptor.msaaSamples > 1;
                bool resolveDepth = RenderingUtils.MultisampleDepthResolveSupported() && renderGraph.nativeRenderPassesEnabled;

                // If we aren't using hardware depth resolves and we have MSAA, we need to resolve depth manually by binding as an MSAA texture.
                depthDescriptor.bindMS = !resolveDepth && hasMSAA;

                if (IsGLESDevice())
                    depthDescriptor.bindMS = false;

                depthDescriptor.graphicsFormat = GraphicsFormat.None;
                depthDescriptor.depthStencilFormat = cameraDepthAttachmentFormat;

                RenderingUtils.ReAllocateHandleIfNeeded(ref s_renderGraphCameraDepthHandle, depthDescriptor, FilterMode.Point, TextureWrapMode.Clamp, name: k_cameraDepthAttachmentName);

                ImportResourceParams importDepthParams = new ImportResourceParams();
                importDepthParams.clearOnFirstUse = clearDepth;
                importDepthParams.clearColor = cameraBackgroundColor;
                importDepthParams.discardOnLastUse = lastCameraInTheStack; // Last camera in stack
#if UNITY_EDITOR
                // scene filtering will reuse "camera" depth  from the normal pass for the "filter highlight" effect
                if (cameraData.isSceneViewCamera && CoreUtils.IsSceneFilteringEnabled())
                    importDepthParams.discardOnLastUse = false;
#endif

                resourceData.cameraDepth = renderGraph.ImportTexture(s_renderGraphCameraDepthHandle, importDepthParams);
                resourceData.activeDepthID = ResourceData.ActiveID.Camera;
            }
            else
                resourceData.activeDepthID = ResourceData.ActiveID.BackBuffer;

            #endregion

            CreateCameraDepthCopyTexture(renderGraph, cameraData.cameraTargetDescriptor, depthTextureIsDepthFormat);
            if (inputSummary.requiresNormalsTexture)
                CreateCameraNormalsTexture(renderGraph, cameraData.cameraTargetDescriptor);
            CreateMotionVectorTextures(renderGraph, cameraData.cameraTargetDescriptor);
            CreateRenderingLayersTexture(renderGraph, cameraData.cameraTargetDescriptor);
        }

        private void CollectRenderingData(bool isTemporalAAEnabled, bool postProcessingEnabled, bool isSceneViewCamera, ref InternalRendererData internalData, RenderingData renderingData)
        {
            RenderPassInputSummary inputSummary = internalData.renderPassInputs;

            if (isTemporalAAEnabled)
                inputSummary.requiresMotionVectors = true;

            //if (postProcessingEnabled)
            //{
            //    var motionBlur = VolumeManager.instance.stack.GetComponent<MotionBlur>();
            //    if (motionBlur != null && motionBlur.IsActive() && motionBlur.mode.value == MotionBlurMode.CameraAndObjects)
            //        inputSummary.requiresMotionVectors = true;
            //}

            if (inputSummary.requiresMotionVectors)
                inputSummary.requiresDepthTexture = true;

            renderingData.opaqueLayerMask = internalData.opaqueLayerMask;
            renderingData.renderPassInputs = inputSummary;

            renderingData.hasForwardPlus = internalData.hasForwardPlus;
            renderingData.hasDeferred = internalData.hasDeferred;
            renderingData.deferredLightingMode = internalData.deferredLightingMode;
            renderingData.clusterLightingMode = internalData.clusterLightingMode;
        }

        private void UpdateCameraHistory(CameraData cameraData)
        {
            if (cameraData != null && cameraData.historyManager != null)
            {
                // XR multipass renders the frame twice, avoid updating camera history twice.
                bool xrMultipassEnabled = false;
                int multipassId = 0;
#if FRAXINUSRP_ENABLE_XR && ENABLE_VR && ENABLE_XR_MODULE
                xrMultipassEnabled = cameraData.xr.enabled && !cameraData.xr.singlePassEnabled;
                multipassId = cameraData.xr.multipassId;
#endif
                bool isNewFrame = !xrMultipassEnabled || (multipassId == 0);

                if (isNewFrame)
                {
                    var history = cameraData.historyManager;

                    // Gather all external user requests by callback.
                    history.GatherHistoryRequests();

                    // Typically we would also gather all the internal requests here before checking for unused textures.
                    // However the requests are versioned in the history manager, so we can defer the clean up for couple frames.

                    // Garbage collect all the unused persistent data instances. Free GPU resources if any.
                    // This will start a new "history frame".
                    history.ReleaseUnusedHistory();

                    // Swap and cycle camera history RTHandles. Update the reference size for the camera history RTHandles.
                    history.SwapAndSetReferenceSize(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);
                }
            }
        }

        bool RequiresIntermediateAttachments(CameraData cameraData, RenderingData renderingData)
        {
            bool isPreviewCamera = cameraData.isPreviewCamera;

            var requireColorTexture = graph.intermediateTextureMode == IntermediateTextureMode.Always;
            requireColorTexture |= renderingData.renderPassInputs.requiresIntermediateTexture;
            requireColorTexture |= Application.isEditor && usesForwardPlusLightLists;
            requireColorTexture |= RequiresIntermediateColorTexture(cameraData, ref renderingData.renderPassInputs);
#if ENABLE_VIRTUALTEXTURES
            // Feedback is an MRT attachment and must match an ordinary offscreen color/depth target.
            requireColorTexture |= VTBufferManager.IsSupported(cameraData);
#endif

            var requireDepthTexture = RequireDepthTexture(cameraData, renderingData.requiresDepthPrepass, ref renderingData.renderPassInputs);

            return requireColorTexture || requireDepthTexture;
        }

        bool RequiresIntermediateColorTexture(CameraData cameraData, ref RenderPassInputSummary renderPassInputs)
        {
            if (cameraData.renderType == CameraRenderType.Base && !cameraData.resolveFinalTarget)
                return true;

            bool isSceneViewCamera = cameraData.isSceneViewCamera;
            var cameraTargetDescriptor = cameraData.cameraTargetDescriptor;
            int msaaSamples = cameraTargetDescriptor.msaaSamples;
            bool isScaledRender = cameraData.imageScalingMode != ImageScalingMode.None;
            bool isCompatibleBackbufferTextureDimension = cameraTargetDescriptor.dimension == TextureDimension.Tex2D;
            bool requiresExplicitMsaaResolve = msaaSamples > 1 && PlatformRequiresExplicitMsaaResolve();
            bool isOffscreenRender = cameraData.targetTexture != null && !isSceneViewCamera;
            bool isCapturing = cameraData.captureActions != null;

            bool postProcessEnabled = cameraData.postProcessEnabled;
            bool requiresBlitForOffscreenCamera = postProcessEnabled || cameraData.requiresOpaqueTexture || requiresExplicitMsaaResolve || !cameraData.isDefaultViewport;
            if (isOffscreenRender)
                return requiresBlitForOffscreenCamera;

            return requiresBlitForOffscreenCamera || isScaledRender || cameraData.isHdrEnabled ||
                !isCompatibleBackbufferTextureDimension || isCapturing || cameraData.requireSrgbConversion ||
                renderPassInputs.requiresColorTexture || renderPassInputs.requiresColorTextureCreated;
        }

        // TODO: Complete the depth texture requirements.
        bool RequireDepthTexture(CameraData cameraData, bool requiresDepthPrepass, ref RenderPassInputSummary renderPassInputs)
        {
            bool depthPrimingEnabled = IsDepthPrimingEnabled(cameraData);
            bool requiresDepthTexture = cameraData.requiresDepthTexture || renderPassInputs.requiresDepthTexture || depthPrimingEnabled;
            bool cameraHasPostProcessingWithDepth = CameraHasPostProcessingWithDepth(cameraData);
            bool useDeferred = (_renderingMode & (RenderingMode.Deferred | RenderingMode.DeferredPlus | RenderingMode.DeferredCluster)) != 0;

            var createDepthTexture = (requiresDepthTexture || cameraHasPostProcessingWithDepth) && !requiresDepthPrepass;
            createDepthTexture |= !cameraData.resolveFinalTarget;
            createDepthTexture |= (useDeferred && !_deferredLights.useFramebufferFetch);
            createDepthTexture |= depthPrimingEnabled;
            createDepthTexture |= DebugHandlerRequireDepthPass(cameraData);

            return createDepthTexture;
        }

        bool RequireDepthPrepass(CameraData cameraData, ref RenderPassInputSummary renderPassInputs)
        {
            bool cameraHasPostProcessingWithDepth = CameraHasPostProcessingWithDepth(cameraData);

            bool depthPrimingEnabled = IsDepthPrimingEnabled(cameraData);

            bool requiresDepthTexture = cameraData.requiresDepthTexture || renderPassInputs.requiresDepthTexture || depthPrimingEnabled;
            bool requiresDepthPrepass = (requiresDepthTexture || cameraHasPostProcessingWithDepth) && (!CanCopyDepth(cameraData));
            requiresDepthPrepass |= renderPassInputs.requiresDepthPrepass;
            requiresDepthPrepass |= renderPassInputs.requiresFullDepthPrepass;
            requiresDepthPrepass |= renderPassInputs.requiresNormalsTexture;
            requiresDepthPrepass |= depthPrimingEnabled;
            return requiresDepthPrepass;
        }

        bool IsDepthPrimingEnabled(CameraData cameraData)
        {
#if UNITY_EDITOR
            // We need to disable depth-priming for DrawCameraMode.Wireframe, since depth-priming forces ZTest to Equal
            // for opaques rendering, which breaks wireframe rendering.
            if (cameraData.isSceneViewCamera)
            {
                foreach (var sceneViewObject in UnityEditor.SceneView.sceneViews)
                {
                    var sceneView = sceneViewObject as UnityEditor.SceneView;
                    if (sceneView != null && sceneView.camera == cameraData.camera && sceneView.cameraMode.drawMode == UnityEditor.DrawCameraMode.Wireframe)
                        return false;
                }
            }
#endif

            // depth priming requires an extra depth copy, disable it on platforms not supporting it (like GLES when MSAA is on)
            if (!CanCopyDepth(cameraData))
                return false;

            // Depth Priming causes rendering errors with WebGL and WebGPU on Apple Arm64 GPUs.
            bool isNotWebGL = !IsWebGL();
            bool depthPrimingRequested = (_depthPrimingRecommended && _depthPrimingMode == DepthPrimingMode.Auto);
            bool isFirstCameraToWriteDepth = cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth;
            // Enabled Depth priming when baking Reflection Probes causes artefacts (UUM-12397)
            bool isNotReflectionCamera = cameraData.cameraType != CameraType.Reflection;
            // Depth is not rendered in a depth-only camera setup with depth priming (UUM-38158)
            bool isNotOffscreenDepthTexture = !IsOffscreenDepthTexture(cameraData);
            bool isNotMSAA = cameraData.cameraTargetDescriptor.msaaSamples == 1;

            return depthPrimingRequested && isFirstCameraToWriteDepth && isNotReflectionCamera && isNotOffscreenDepthTexture && isNotWebGL && isNotMSAA;
        }

        bool CanCopyDepth(CameraData cameraData)
        {
            bool msaaEnabledForCamera = cameraData.cameraTargetDescriptor.msaaSamples > 1;
            bool supportsTextureCopy = SystemInfo.copyTextureSupport != CopyTextureSupport.None;
            bool supportsDepthTarget = RenderingUtils.SupportsRenderTextureFormat(RenderTextureFormat.Depth);
            bool supportsDepthCopy = !msaaEnabledForCamera && (supportsDepthTarget || supportsTextureCopy);

            bool msaaDepthResolve = msaaEnabledForCamera && SystemInfo.supportsMultisampledTextures != 0;

            // copying MSAA depth on GLES3 is giving invalid results. This won't be fixed by the driver team because it would introduce performance issues (more info in the Fogbugz issue 1339401 comments)
            if (IsGLESDevice() && msaaDepthResolve)
                return false;

            return supportsDepthCopy || msaaDepthResolve;
        }

        bool CameraHasPostProcessingWithDepth(CameraData cameraData)
        {
            return cameraData.postProcessEnabled;
        }

        void CreateCameraDepthCopyTexture(RenderGraph renderGraph, RenderTextureDescriptor descriptor, bool isDepthTexture)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();

            var depthDescriptor = descriptor;
            depthDescriptor.msaaSamples = 1;// Depth-Only pass don't use MSAA

            if (isDepthTexture)
            {
                depthDescriptor.graphicsFormat = GraphicsFormat.None;
                depthDescriptor.depthStencilFormat = cameraDepthTextureFormat;
            }
            else
            {
                depthDescriptor.graphicsFormat = GraphicsFormat.R32_SFloat;
                depthDescriptor.depthStencilFormat = GraphicsFormat.None;
            }

            resourceData.cameraDepthTexture = CreateRenderGraphTexture(renderGraph, depthDescriptor, "_CameraDepthTexture", true);
        }

        void CreateMotionVectorTextures(RenderGraph renderGraph, RenderTextureDescriptor descriptor)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();

            var colorDesc = descriptor;
            colorDesc.msaaSamples = 1;  // Disable MSAA, consider a pixel resolve for half left velocity and half right velocity --> no velocity, which is untrue.
            colorDesc.graphicsFormat = MotionVectorRenderPass.targetFormat;
            colorDesc.depthStencilFormat = GraphicsFormat.None;
            resourceData.motionVectorColor = CreateRenderGraphTexture(renderGraph, colorDesc, MotionVectorRenderPass.motionVectorTextureName, true);

            var depthDescriptor = descriptor;
            depthDescriptor.msaaSamples = 1;
            depthDescriptor.graphicsFormat = GraphicsFormat.None;
            depthDescriptor.depthStencilFormat = cameraDepthAttachmentFormat;
            resourceData.motionVectorDepth = CreateRenderGraphTexture(renderGraph, depthDescriptor, MotionVectorRenderPass.motionVectorDepthTextureName, true);
        }

        void CreateCameraNormalsTexture(RenderGraph renderGraph, RenderTextureDescriptor descriptor)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            RenderingData renderingData = frameData.Get<RenderingData>();

            var normalDescriptor = descriptor;
            normalDescriptor.depthStencilFormat = GraphicsFormat.None;
            normalDescriptor.msaaSamples = 1; // Never use MSAA for the normal texture!
            // Find compatible render-target format for storing normals.
            // Shader code outputs normals in signed format to be compatible with deferred gbuffer layout.
            // Deferred gbuffer format is signed so that normals can be blended for terrain geometry.
            var normalsName = !renderingData.hasDeferred ? "_CameraNormalsTexture" : DeferredLights.gbufferNames[_deferredLights.gBufferNormalSmoothnessIndex];
            normalDescriptor.graphicsFormat = !renderingData.hasDeferred ? DepthNormalOnlyPass.GetGraphicsFormat() : _deferredLights.GetGBufferFormat(_deferredLights.gBufferNormalSmoothnessIndex);
            resourceData.cameraNormalsTexture = CreateRenderGraphTexture(renderGraph, normalDescriptor, normalsName, true);
        }


        void CreateRenderingLayersTexture(RenderGraph renderGraph, RenderTextureDescriptor descriptor)
        {
            if (renderinglayerSetting.requiresRenderingLayer)
            {
                ResourceData resourceData = frameData.Get<ResourceData>();
                renderinglayerSetting.renderingLayersTextureName = "_CameraRenderingLayersTexture";

                bool isDeferred = (renderingMode & (RenderingMode.Deferred | RenderingMode.DeferredPlus | RenderingMode.DeferredCluster)) != 0;
                // TODO RENDERGRAPH: deferred optimization
                if (isDeferred && _deferredLights.useRenderingLayers)
                {
                    renderinglayerSetting.renderingLayersTextureName = DeferredLights.gbufferNames[_deferredLights.gBufferRenderingLayers];

                    RenderTextureDescriptor renderingLayersDescriptor = descriptor;
                    renderingLayersDescriptor.depthStencilFormat = GraphicsFormat.None;
                    if (!renderinglayerSetting.renderingLayerProvidesRenderObjectPass)
                        renderingLayersDescriptor.msaaSamples = 1;// Depth-Only pass don't use MSAA

                    // Find compatible render-target format for storing normals.
                    // Shader code outputs normals in signed format to be compatible with deferred gbuffer layout.
                    // Deferred gbuffer format is signed so that normals can be blended for terrain geometry.
                    if (isDeferred)
                        renderingLayersDescriptor.graphicsFormat = _deferredLights.GetGBufferFormat(_deferredLights.gBufferRenderingLayers); // the one used by the gbuffer.
                    else
                        renderingLayersDescriptor.graphicsFormat = RenderingLayerUtils.GetFormat(renderinglayerSetting.renderingLayersMaskSize);

                    resourceData.renderingLayersTexture = CreateRenderGraphTexture(renderGraph, renderingLayersDescriptor, renderinglayerSetting.renderingLayersTextureName, true);
                }
            }
        }


        bool IsWebGL()
        {
            // Both WebGL and WebGPU have issues with depth priming on Apple Arm64
#if PLATFORM_WEBGL
            return true;
#else
            return false;
#endif
        }

        bool IsGLESDevice()
        {
            return SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3;
        }

        private void CleanupRenderGraphResources()
        {
            s_renderGraphCameraColorHandles[0]?.Release();
            s_renderGraphCameraColorHandles[1]?.Release();
            s_renderGraphCameraDepthHandle?.Release();

            s_renderGraphDebugTextureHandle?.Release();
        }

        public static bool IsOffscreenDepthTexture(CameraData cameraData) => cameraData.targetTexture != null &&
            cameraData.targetTexture.graphicsFormat == GraphicsFormat.None &&
            cameraData.targetTexture.depthStencilFormat != GraphicsFormat.None;

        private void ImportOffscreenDepthTarget(RenderGraph renderGraph, ResourceData resources, CameraData cameraData)
        {
            RenderTexture target = cameraData.targetTexture;
            // A depth camera renders directly to the requested viewport, without upscaling.
            cameraData.renderScale = 1;
            cameraData.cameraTargetDescriptor.width = cameraData.pixelWidth;
            cameraData.cameraTargetDescriptor.height = cameraData.pixelHeight;
            var targetId = new RenderTargetIdentifier(target);
            if (_targetDepthHandle == null)
                _targetDepthHandle = RTHandles.Alloc(targetId, "Offscreen camera depth");
            else if (_targetDepthHandle.nameID != targetId)
                RTHandleStaticHelpers.SetRTHandleUserManagedWrapper(ref _targetDepthHandle, targetId);

            var info = new RenderTargetInfo
            {
                width = target.width, height = target.height, volumeDepth = target.volumeDepth,
                msaaSamples = target.antiAliasing, format = target.depthStencilFormat
            };
            var import = new ImportResourceParams
            {
                // OffscreenDepthPass also clears with the compatibility RenderGraph compiler.
                clearOnFirstUse = false,
                discardOnLastUse = false
            };
            resources.backBufferDepth = renderGraph.ImportTexture(_targetDepthHandle, info, import);
            resources.activeDepthID = ResourceData.ActiveID.BackBuffer;
            resources.activeColorID = ResourceData.ActiveID.BackBuffer;
            requiresIntermediateAttachments = false;
        }

        public static TextureHandle CreateRenderGraphTexture(RenderGraph renderGraph, RenderTextureDescriptor desc, string name, bool clear,
            FilterMode filterMode = FilterMode.Point, TextureWrapMode wrapMode = TextureWrapMode.Clamp)
        {
            TextureDesc rgDesc = new TextureDesc(desc.width, desc.height);
            rgDesc.dimension = desc.dimension;
            rgDesc.clearBuffer = clear;
            rgDesc.bindTextureMS = desc.bindMS;
            rgDesc.format = (desc.depthStencilFormat != GraphicsFormat.None) ? desc.depthStencilFormat : desc.graphicsFormat;
            rgDesc.slices = desc.volumeDepth;
            rgDesc.msaaSamples = (MSAASamples)desc.msaaSamples;
            rgDesc.name = name;
            rgDesc.enableRandomWrite = desc.enableRandomWrite;
            rgDesc.filterMode = filterMode;
            rgDesc.wrapMode = wrapMode;
            rgDesc.isShadowMap = desc.shadowSamplingMode != ShadowSamplingMode.None && desc.depthStencilFormat != GraphicsFormat.None;
            rgDesc.vrUsage = desc.vrUsage;
            rgDesc.useDynamicScale = desc.useDynamicScale;
#if UNITY_6000_0_OR_NEWER
            rgDesc.useDynamicScaleExplicit = desc.useDynamicScaleExplicit;
#endif

            return renderGraph.CreateTexture(rgDesc);
        }

        internal static TextureHandle CreateRenderGraphTexture(RenderGraph renderGraph, RenderTextureDescriptor desc, string name, bool clear, Color color,
            FilterMode filterMode = FilterMode.Point, TextureWrapMode wrapMode = TextureWrapMode.Clamp, bool discardOnLastUse = false)
        {
            TextureDesc rgDesc = new TextureDesc(desc.width, desc.height);
            rgDesc.dimension = desc.dimension;
            rgDesc.clearBuffer = clear;
            rgDesc.clearColor = color;
            rgDesc.bindTextureMS = desc.bindMS;
            rgDesc.format = (desc.depthStencilFormat != GraphicsFormat.None) ? desc.depthStencilFormat : desc.graphicsFormat;
            rgDesc.slices = desc.volumeDepth;
            rgDesc.msaaSamples = (MSAASamples)desc.msaaSamples;
            rgDesc.name = name;
            rgDesc.enableRandomWrite = desc.enableRandomWrite;
            rgDesc.filterMode = filterMode;
            rgDesc.wrapMode = wrapMode;
            rgDesc.useDynamicScale = desc.useDynamicScale;
#if UNITY_6000_0_OR_NEWER
            rgDesc.useDynamicScaleExplicit = desc.useDynamicScaleExplicit;
#endif
            rgDesc.discardBuffer = discardOnLastUse;
            rgDesc.vrUsage = desc.vrUsage;

            return renderGraph.CreateTexture(rgDesc);
        }

        internal static void InstanceOcclusionTest(RenderGraph renderGraph, CameraData cameraData, OcclusionTest occlusionTest)
        {
            bool isSinglePassXR = false;
            int subviewCount = isSinglePassXR ? 2 : 1;
            var settings = new OcclusionCullingSettings(cameraData.camera.GetInstanceID(), occlusionTest)
            {
                instanceMultiplier = (isSinglePassXR && !SystemInfo.supportsMultiview) ? 2 : 1,
            };
            Span<SubviewOcclusionTest> subviewOcclusionTests = stackalloc SubviewOcclusionTest[subviewCount];
            for (int subviewIndex = 0; subviewIndex < subviewCount; ++subviewIndex)
            {
                subviewOcclusionTests[subviewIndex] = new SubviewOcclusionTest()
                {
                    cullingSplitIndex = 0,
                    occluderSubviewIndex = subviewIndex,
                };
            }
            GPUResidentDrawer.InstanceOcclusionTest(renderGraph, settings, subviewOcclusionTests);
        }

        internal static void UpdateInstanceOccluders(RenderGraph renderGraph, CameraData cameraData, TextureHandle depthTexture)
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

        internal static bool PlatformRequiresExplicitMsaaResolve()
        {
#if UNITY_EDITOR
            // In the editor play-mode we use a Game View Render Texture, with
            // samples count forced to 1 so we always need to do an explicit MSAA resolve.
            return true;
#else
            // On Metal/iOS the MSAA resolve is done implicitly as part of the renderpass, so we do not need an extra intermediate pass for the explicit autoresolve.
            // Note: On Vulkan Standalone, despite SystemInfo.supportsMultisampleAutoResolve being true, the backbuffer has only 1 sample, so we still require
            // the explicit resolve on non-mobile platforms with supportsMultisampleAutoResolve.
            return !(SystemInfo.supportsMultisampleAutoResolve && Application.isMobilePlatform)
                && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal;
#endif
        }
    }
}
