using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;
using FraxinusEngine.Base;

using Lightmapping = UnityEngine.Experimental.GlobalIllumination.Lightmapping;

#if ENABLE_VIRTUALTEXTURES
using UnityEngine.Rendering.VirtualTexturing;
using VirtualTexturing = UnityEngine.Rendering.VirtualTexturing;
#endif

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FraxinusEngine.Rendering.Ultra
{
    public sealed partial class UltraRenderPipeline : RenderPipeline
    {
        public const string shaderTagName = "UltraPipeline";

        internal static class CameraMetadataCache
        {
            public class CameraMetadataCacheEntry
            {
                public string name;
                public ProfilingSampler sampler;
            }

            static Dictionary<int, CameraMetadataCacheEntry> s_metadataCache = new();

            static readonly CameraMetadataCacheEntry k_noAllocEntry = new() { name = "Unknown", sampler = new ProfilingSampler("Unknown") };

            public static CameraMetadataCacheEntry GetCached(Camera camera)
            {
#if UNIVERSAL_PROFILING_NO_ALLOC
                return k_noAllocEntry;
#else
                int cameraId = camera.GetHashCode();
                if (!s_metadataCache.TryGetValue(cameraId, out CameraMetadataCacheEntry result))
                {
                    string cameraName = camera.name; // Warning: camera.name allocates
                    result = new CameraMetadataCacheEntry
                    {
                        name = cameraName,
                        sampler = new ProfilingSampler(
                            $"{nameof(UltraRenderPipeline)}.{nameof(RenderSingleCameraInternal)}: {cameraName}")
                    };
                    s_metadataCache.Add(cameraId, result);
                }

                return result;
#endif
            }
        }

        internal static class Profiling
        {
            public static class Pipeline
            {
                const string k_name = nameof(UltraRenderPipeline);
                public static readonly ProfilingSampler initializeCameraData = new ProfilingSampler($"{k_name}.{nameof(CreateCameraData)}");
                public static readonly ProfilingSampler initializeStackedCameraData = new ProfilingSampler($"{k_name}.{nameof(InitializeStackedCameraData)}");
                public static readonly ProfilingSampler initializeAdditionalCameraData = new ProfilingSampler($"{k_name}.{nameof(InitializeAdditionalCameraData)}");
                public static readonly ProfilingSampler initializeRenderingData = new ProfilingSampler($"{k_name}.{nameof(CreateRenderingData)}");
                public static readonly ProfilingSampler initializeShadowData = new ProfilingSampler($"{k_name}.{nameof(CreateShadowData)}");
                public static readonly ProfilingSampler initializeLightData = new ProfilingSampler($"{k_name}.{nameof(CreateLightData)}");
                public static readonly ProfilingSampler buildAdditionalLightsShadowAtlasLayout = new ProfilingSampler($"{k_name}.{nameof(BuildAdditionalLightsShadowAtlasLayout)}");
                public static readonly ProfilingSampler getPerObjectLightFlags = new ProfilingSampler($"{k_name}.{nameof(GetPerObjectLightFlags)}");
                public static readonly ProfilingSampler getMainLightIndex = new ProfilingSampler($"{k_name}.{nameof(GetMainLightIndex)}");
                public static readonly ProfilingSampler setupPerFrameShaderConstants = new ProfilingSampler($"{k_name}.{nameof(SetupPerFrameShaderConstants)}");
                public static readonly ProfilingSampler setupPerCameraShaderConstants = new ProfilingSampler($"{k_name}.{nameof(SetupPerCameraShaderConstants)}");

                public static class Renderer
                {
                    const string k_name = nameof(ScriptableRenderer);
                    public static readonly ProfilingSampler setupCullingParameters = new ProfilingSampler($"{k_name}.{nameof(ScriptableRenderer.SetupCullingParameters)}");
                }

                public static class Context
                {
                    const string k_name = nameof(ScriptableRenderContext);
                    public static readonly ProfilingSampler submit = new ProfilingSampler($"{k_name}.{nameof(ScriptableRenderContext.Submit)}");
                };
            }
        }

        public class SingleCameraRequest
        {
            /// <summary>
            /// Target texture
            /// </summary>
            public RenderTexture destination = null;

            /// <summary>
            /// Target texture mip level
            /// </summary>
            public int mipLevel = 0;

            /// <summary>
            /// Target texture cubemap face
            /// </summary>
            public CubemapFace face = CubemapFace.Unknown;

            /// <summary>
            /// Target texture slice
            /// </summary>
            public int slice = 0;
        }


        public static float maxShadowBias
        {
            get => 10.0f;
        }

        public static float minRenderScale
        {
            get => 0.1f;
        }

        public static float maxRenderScale
        {
            get => 2.0f;
        }

        public static int maxNumIterationsEnclosingSphere
        {
            get => 1000;
        }

        public static int maxPerObjectLights
        {
            get => 8;
        }

        public static int maxVisibleAdditionalLights
        {
            get
            {
                // Must match: Input.hlsl, MAX_VISIBLE_LIGHTS
                bool isMobileOrMobileBuildTarget = PlatformAutoDetect.isShaderAPIMobileDefined;
                if (isMobileOrMobileBuildTarget && (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 && Graphics.minOpenGLESVersion <= OpenGLESVersion.OpenGLES30))
                    return ShaderOptions.maxVisibleLightCountLowEndMobile;

                // GLES can be selected as platform on Windows (not a mobile platform) but uniform buffer size so we must use a low light count.
                // WebGPU's minimal limits are based on mobile rather than desktop, so it will need to assume mobile.
                bool constrainedGraphicsApi = isMobileOrMobileBuildTarget || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3;
#if UNITY_6000_0_OR_NEWER
                constrainedGraphicsApi |= SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU;
#endif
                return constrainedGraphicsApi
                    ? ShaderOptions.maxVisibleLightCountMobile : ShaderOptions.maxVisibleLightCountDesktop;
            }
        }

        // Match with values in Input.hlsl
        internal static int lightsPerTile => ((maxVisibleAdditionalLights + 31) / 32) * 32;
        internal static int maxZBinWords => 1024 * 4;
        internal static int maxTileWords => (maxVisibleAdditionalLights <= 32 ? 1024 : 4096) * 4;
        internal static int maxVisibleReflectionProbes => Math.Min(maxVisibleAdditionalLights, 64);

        internal const int defaultRenderingLayerMask = 0x00000001;
        private readonly DebugDisplaySettingsUI _debugDisplaySettingsUI = new DebugDisplaySettingsUI();


        private UltraRenderPipelineGlobalSettings _globalSettings;
        internal UltraRenderPipelineRuntimeTextures runtimeTextures { get; private set; }

        public override RenderPipelineGlobalSettings defaultSettings => _globalSettings;

        internal static RenderGraph renderGraph;
        internal static RTHandleResourcePool rtHandlePool;

        // Track provider ownership so cleanup matches initialization for either backend.
        private readonly bool _probeVolumesInitialized;

        internal static bool canOptimizeScreenMSAASamples { get; private set; }
        internal static int startFrameScreenMSAASamples { get; private set; }

        private readonly UltraRenderPipelineAsset _pipelineAsset;

        public override string ToString() => _pipelineAsset?.ToString();

        public UltraRenderPipeline(UltraRenderPipelineAsset asset)
        {
            _pipelineAsset = asset;

            _globalSettings = UltraRenderPipelineGlobalSettings.instance;
#if !UNITY_6000_0_OR_NEWER
            RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(_globalSettings, typeof(UltraRenderPipeline));
            Shader.globalRenderPipeline = shaderTagName;
#endif

            runtimeTextures = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<UltraRenderPipelineRuntimeTextures>();

            var shaders = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>();
#if UNITY_EDITOR
            shaders.EnsureShadersCompiled();
#endif

            Blitter.Cleanup();
            Blitter.Initialize(shaders.coreBlitPS, shaders.coreBlitColorAndDepthPS);

            SetSupportedRenderingFeatures(_pipelineAsset);

            RTHandles.Initialize(Screen.width, Screen.height);
            LensFlareCommonSRP.Initialize();

            ShaderGlobalKeywords.InitializeShaderGlobalKeywords();

            GraphicsSettings.useScriptableRenderPipelineBatching = asset.useSRPBatcher;

            int qualitySettingsMsaaSampleCount = QualitySettings.antiAliasing > 0 ? QualitySettings.antiAliasing : 1;
            bool msaaSampleCountNeedsUpdate = qualitySettingsMsaaSampleCount != asset.msaaSampleCount;
            if (msaaSampleCountNeedsUpdate)
                QualitySettings.antiAliasing = asset.msaaSampleCount;

            var defaultVolumeProfileSettings = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<UltraRenderPipelineDefaultVolumeProfileSettings>();
            if (!VolumeManager.instance.isInitialized)
                VolumeManager.instance.Initialize(defaultVolumeProfileSettings?.volumeProfile, asset.volumeProfile);

            Lightmapping.SetDelegate(lightsDelegate);
            CameraCaptureBridge.enabled = true;

            RenderingUtils.ClearSystemInfoCache();

            DecalProjector.defaultMaterial = asset.decalMaterial;

            renderGraph = new RenderGraph("UltraPRenderGraph");

            rtHandlePool = new RTHandleResourcePool();

            DebugManager.instance.RefreshEditor();

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _debugDisplaySettingsUI.RegisterDebug(UltraRenderPipelineDebugDisplaySettings.Instance);
#endif

#if ENABLE_VIRTUALTEXTURES
            VirtualTexturingSettingsSRP settings = asset.virtualTexturingSettings;

            if (settings == null)
                settings = new VirtualTexturingSettingsSRP();

            VirtualTexturing.Streaming.SetCPUCacheSize(settings.streamingCpuCacheSizeInMegaBytes);
            VirtualTexturing.Streaming.EnableMipPreloading(settings.streamingMipPreloadTexturesPerFrame, settings.streamingPreloadMipCount);

            GPUCacheSetting[] gpuCacheSettings = new GPUCacheSetting[settings.streamingGpuCacheSettings.Count];
            for (int i = 0; i < settings.streamingGpuCacheSettings.Count; ++i)
            {
                GPUCacheSettingSRP srpSetting = settings.streamingGpuCacheSettings[i];
                gpuCacheSettings[i] = new GPUCacheSetting() { format = srpSetting.format, sizeInMegaBytes = srpSetting.sizeInMegaBytes };
            }

            VirtualTexturing.Streaming.SetGPUCacheSettings(gpuCacheSettings);

#if UNITY_EDITOR_WIN
            // Ideally, we would simply check if "System.Runtime.InteropServices.RuntimeInformation.OSArchitecture" is Arm64. However: under emulation,
            // OSArchitecture may return X64 on an ARM64 device, so we rely on the "PROCESSOR_ARCHITECTURE" environment variable instead.
            string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE", EnvironmentVariableTarget.Machine).ToLowerInvariant();
            if (architecture == "aarch64" || architecture == "arm64")
            {
                Debug.LogWarning("Unity has detected that Virtual Texturing is being used on Windows ARM64: please note that this is not supported and that issues may arise. It is recommended to disable the feature.");
            }
#endif
#endif

            QualitySettings.enableLODCrossFade = asset.enableLODCrossFade;
            bool apvIsEnabled = asset != null && asset.supportProbeVolume;
            // APRTV retains native light probes as the ambient fallback.
            SupportedRenderingFeatures.active.overridesLightProbeSystem = apvIsEnabled;
#if UNITY_6000_0_OR_NEWER
            SupportedRenderingFeatures.active.skyOcclusion = apvIsEnabled;
#endif
            if (apvIsEnabled || (asset != null && asset.supportAPRTV))
            {
                _probeVolumesInitialized = ProbeReferenceVolumeProvider.Initialize(
                    asset.GetProbeVolumeSystemParameters(), asset.probeVolumeBackendId) != null;
            }


            #region Extension

            PreIntegratedFGD.instance.Build(PreIntegratedFGD.FGDIndex.FGD_GGXAndDisneyDiffuse);

            IBLFilterGGX.instance.Initialize(shaders);

            Hammersley.Initialize();

            AreaLightSystem.instance.Build();
            #endregion
        }

        protected override void Dispose(bool disposing)
        {
            APRTRelightPass.Cleanup();
            if (_probeVolumesInitialized)
                ProbeReferenceVolumeProvider.Cleanup();
            Shader.DisableKeyword(FraxinusEngine.Rendering.Ultra.ShaderKeywordStrings.ProbeVolumeAPRT);

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            _debugDisplaySettingsUI.UnregisterDebug();
#endif

            Blitter.Cleanup();

            base.Dispose(disposing);

            _pipelineAsset.DestroyRenderers();
            LensFlareCommonSRP.Dispose();
#if !UNITY_6000_0_OR_NEWER
            Shader.globalRenderPipeline = string.Empty;
#endif

            SupportedRenderingFeatures.active = new SupportedRenderingFeatures();
            ShaderData.instance.Dispose();

            renderGraph?.Cleanup();
            renderGraph = null;

            rtHandlePool?.Cleanup();
            rtHandlePool = null;

            #region Extension

            AreaLightSystem.instance.Cleanup();

            PreIntegratedFGD.instance.Cleanup(PreIntegratedFGD.FGDIndex.FGD_GGXAndDisneyDiffuse);

            IBLFilterGGX.instance.Cleanup();

            #endregion

#if UNITY_EDITOR
            SceneViewDrawMode.ResetDrawMode();
#endif
            Lightmapping.ResetDelegate();
            CameraCaptureBridge.enabled = false;

            ConstantBuffer.ReleaseAll();
            ConstantBufferCompute.ReleaseAll();
            VolumeManager.instance.Deinitialize();

            DisposeAdditionalCameraData();
            AdditionalLightsShadowAtlasLayout.ClearStaticCaches();
        }

        private void DisposeAdditionalCameraData()
        {
            foreach (var c in Camera.allCameras)
            {
                if (c.TryGetComponent<UltraAdditionalCameraData>(out var additionalCameraData))
                    additionalCameraData.historyManager.Dispose();
            }
        }

        readonly struct CameraRenderingScope : IDisposable
        {
            static readonly ProfilingSampler beginCameraRenderingSampler = new ProfilingSampler($"{nameof(RenderPipeline)}.{nameof(BeginCameraRendering)}");
            static readonly ProfilingSampler endCameraRenderingSampler = new ProfilingSampler($"{nameof(RenderPipeline)}.{nameof(EndCameraRendering)}");

            private readonly ScriptableRenderContext _context;
            private readonly Camera _camera;

            public CameraRenderingScope(ScriptableRenderContext context, Camera camera)
            {
                using (new ProfilingScope(beginCameraRenderingSampler))
                {
                    _context = context;
                    _camera = camera;

                    BeginCameraRendering(context, camera);
                }
            }

            public void Dispose()
            {
                using (new ProfilingScope(endCameraRenderingSampler))
                {
                    EndCameraRendering(_context, _camera);
                }
            }
        }

        readonly struct ContextRenderingScope : IDisposable
        {
            static readonly ProfilingSampler beginContextRenderingSampler = new ProfilingSampler($"{nameof(RenderPipeline)}.{nameof(BeginContextRendering)}");
            static readonly ProfilingSampler endContextRenderingSampler = new ProfilingSampler($"{nameof(RenderPipeline)}.{nameof(EndContextRendering)}");

            private readonly ScriptableRenderContext _context;
            private readonly List<Camera> _cameras;

            public ContextRenderingScope(ScriptableRenderContext context, List<Camera> cameras)
            {
                _context = context;
                _cameras = cameras;

                using (new ProfilingScope(beginContextRenderingSampler))
                {
                    BeginContextRendering(_context, _cameras);
                }
            }

            public void Dispose()
            {
                using (new ProfilingScope(endContextRenderingSampler))
                {
                    EndContextRendering(_context, _cameras);
                }
            }
        }

#if UNITY_2021_1_OR_NEWER
        protected override void Render(ScriptableRenderContext renderContext, Camera[] cameras)
        {
            Render(renderContext, new List<Camera>(cameras));
        }
#endif


#if UNITY_2021_1_OR_NEWER
        protected override void Render(ScriptableRenderContext renderContext, List<Camera> cameras)
#else
        protected override void Render(ScriptableRenderContext renderContext, Camera[] cameras)
#endif
        {
            SetHDRState(cameras);

#if UNITY_2021_1_OR_NEWER
            int cameraCount = cameras.Count;
#else
            int cameraCount = cameras.Length;
#endif
            AdjustUIOverlayOwnership(cameraCount);
            SetupScreenMSAASamplesState(cameraCount);

            GPUResidentDrawer.ReinitializeIfNeeded();

            using var profScope = new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.UltraRenderPipelineTotal));

            using (new ContextRenderingScope(renderContext, cameras))
            {
                GraphicsSettings.lightsUseLinearIntensity = (QualitySettings.activeColorSpace == ColorSpace.Linear);
                GraphicsSettings.lightsUseColorTemperature = true;
                SetupPerFrameShaderConstants();

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                if (DebugManager.instance.isAnyDebugUIActive)
                    UltraRenderPipelineDebugDisplaySettings.Instance.UpdateDisplayStats();

                // This is for texture streaming
                UltraRenderPipelineDebugDisplaySettings.Instance.UpdateMaterials();
#endif

                // UltraRP uses the camera's allowDynamicResolution flag to decide if useDynamicScale should be enabled for camera render targets.
                // However, the RTHandle system has an additional setting that controls if useDynamicScale will be set for render targets allocated via RTHandles.
                // In order to avoid issues at runtime, we must make the RTHandle system setting consistent with UltraRP's logic. UltraRP already synchronizes the setting
                // during initialization, but unfortunately it's possible for external code to overwrite the setting due to RTHandle state being global.
                // The best we can do to avoid errors in this situation is to ensure the state is set to the correct value every time we perform rendering.
                RTHandles.SetHardwareDynamicResolutionState(true);

                SortCameras(cameras);
                int lastBaseCameraIndex = GetLastBaseCameraIndex(cameras);
#if UNITY_2021_1_OR_NEWER
                for (int i = 0; i < cameras.Count; ++i)
#else
                for (int i = 0; i < cameras.Length; ++i)
#endif
                {
                    var camera = cameras[i];
                    bool isLastBaseCamera = i == lastBaseCameraIndex;
                    if (IsGameCamera(camera))
                        RenderCameraStack(renderContext, camera, isLastBaseCamera);
                    else
                    {
                        using (new CameraRenderingScope(renderContext, camera))
                        {
#if VISUAL_EFFECT_GRAPH_0_0_1_OR_NEWER
                            // VFX.VFXManager.PrepareCamera(camera);
#endif
                            UpdateVolumeFramework(camera, null);
                            RenderSingleCameraInternal(renderContext, camera, isLastBaseCamera);
                        }

                    }
                }

                renderGraph.EndFrame();
                rtHandlePool.PurgeUnusedResources(Time.frameCount);
            }

#if ENABLE_SHADER_DEBUG_PRINT
            ShaderDebugPrintManager.instance.EndFrame();
#endif
        }

        protected override bool IsRenderRequestSupported<RequestData>(Camera camera, RequestData data)
        {
            if (data is StandardRequest)
                return true;
            else if (data is SingleCameraRequest)
                return true;

            return false;
        }

        protected override void ProcessRenderRequests<RequestData>(ScriptableRenderContext context, Camera camera, RequestData renderRequest)
        {
            StandardRequest standardRequest = renderRequest as StandardRequest;
            SingleCameraRequest singleRequest = renderRequest as SingleCameraRequest;

            if (standardRequest != null || singleRequest != null)
            {
                RenderTexture destination = standardRequest != null ? standardRequest.destination : singleRequest.destination;

                // don't go further if no destination texture
                if (destination == null)
                {
                    Debug.LogError("RenderRequest has no destination texture, set one before sending request");
                    return;
                }

                int mipLevel = standardRequest != null ? standardRequest.mipLevel : singleRequest.mipLevel;
                int slice = standardRequest != null ? standardRequest.slice : singleRequest.slice;
                int face = standardRequest != null ? (int)standardRequest.face : (int)singleRequest.face;

                //store data that will be changed
                var originalTarget = camera.targetTexture;
 
                //set data
                RenderTexture temporaryRT = null;
                RenderTextureDescriptor rtDesc = destination.descriptor;
                //need to set use default constructor of RenderTextureDescriptor which doesn't enable allowVerticalFlip which matters for cubemaps.
                if (destination.dimension == TextureDimension.Cube)
                    rtDesc = new RenderTextureDescriptor();

                rtDesc.colorFormat = destination.format;
                rtDesc.volumeDepth = 1;
                rtDesc.msaaSamples = destination.descriptor.msaaSamples;
                rtDesc.dimension = TextureDimension.Tex2D;
                rtDesc.width = destination.width / (int)Math.Pow(2, mipLevel);
                rtDesc.height = destination.height / (int)Math.Pow(2, mipLevel);
                rtDesc.width = Mathf.Max(1, rtDesc.width);
                rtDesc.height = Mathf.Max(1, rtDesc.height);

                //if mip is 0 and target is Texture2D we can immediately render to the requested destination
                if (destination.dimension != TextureDimension.Tex2D || mipLevel != 0)
                    temporaryRT = RenderTexture.GetTemporary(rtDesc);

                camera.targetTexture = temporaryRT ? temporaryRT : destination;

                if (standardRequest != null)
                    Render(context, new Camera[] { camera });
                else
                {
                    using (ListPool<Camera>.Get(out var tmp))
                    {
                        tmp.Add(camera);

                        using (new ContextRenderingScope(context, tmp))
                        using (new CameraRenderingScope(context, camera))
                        {
                            camera.gameObject.TryGetComponent<UltraAdditionalCameraData>(out var additionalCameraData);
                            RenderSingleCameraInternal(context, camera, ref additionalCameraData);
                        }
                    }
                }

                if (temporaryRT)
                {
                    bool isCopySupported = false;
                    switch (destination.dimension)
                    {
                        case TextureDimension.Tex2D:
                            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0)
                            {
                                isCopySupported = true;
                                Graphics.CopyTexture(temporaryRT, 0, 0, destination, 0, mipLevel);
                            }
                            break;
                        case TextureDimension.Tex2DArray:
                            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) != 0)
                            {
                                isCopySupported = true;
                                Graphics.CopyTexture(temporaryRT, 0, 0, destination, slice, mipLevel);
                            }
                            break;
                        case TextureDimension.Tex3D:
                            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) != 0)
                            {
                                isCopySupported = true;
                                Graphics.CopyTexture(temporaryRT, 0, 0, destination, slice, mipLevel);
                            }
                            break;
                        case TextureDimension.Cube:
                            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) != 0)
                            {
                                isCopySupported = true;
                                Graphics.CopyTexture(temporaryRT, 0, 0, destination, face, mipLevel);
                            }
                            break;
                        case TextureDimension.CubeArray:
                            if ((SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes) != 0)
                            {
                                isCopySupported = true;
                                Graphics.CopyTexture(temporaryRT, 0, 0, destination, face + slice * 6, mipLevel);
                            }
                            break;
                        default:
                            break;
                    }

                    if (!isCopySupported)
                        Debug.LogError($"RenderRequest cannot have destination texture of this format: {Enum.GetName(typeof(TextureDimension), destination.dimension)}");
                }

                //restore data
                camera.targetTexture = originalTarget;
                Graphics.SetRenderTarget(originalTarget);
                RenderTexture.ReleaseTemporary(temporaryRT);
            }
            else
                Debug.LogWarning($"RenderRequest type: {typeof(RequestData).FullName} is either invalid or unsupported by the current pipeline");
        }

        internal static void RenderSingleCameraInternal(ScriptableRenderContext context, Camera camera, bool isLastBaseCamera = true)
        {
            UltraAdditionalCameraData additionalCameraData = null;
            if (IsGameCamera(camera))
                camera.gameObject.TryGetComponent(out additionalCameraData);

            RenderSingleCameraInternal(context, camera, ref additionalCameraData, isLastBaseCamera);
        }

        internal static void RenderSingleCameraInternal(ScriptableRenderContext context, Camera camera, ref UltraAdditionalCameraData additionalCameraData, bool isLastBaseCamera = true)
        {
            if (additionalCameraData != null && additionalCameraData.renderType != CameraRenderType.Base)
            {
                Debug.LogWarning("Only Base cameras can be rendered with standalone RenderSingleCamera. Camera will be skipped.");
                return;
            }

            if (camera.targetTexture.width == 0 || camera.targetTexture.height == 0 || camera.pixelWidth == 0 || camera.pixelHeight == 0)
            {
                Debug.LogWarning($"Camera '{camera.name}' has an invalid render target size (width: {camera.targetTexture.width}, height: {camera.targetTexture.height}) or pixel dimensions (width: {camera.pixelWidth}, height: {camera.pixelHeight}). Camera will be skipped.");
                return;
            }

            var renderer = GetRenderer(camera, additionalCameraData);
            APRTRelightPass.PrepareSkyEnvironment(camera, renderer);
            var frameData = renderer.frameData;
            var cameraData = CreateCameraData(frameData, camera, additionalCameraData, true);
            InitializeAdditionalCameraData(camera, additionalCameraData, true, isLastBaseCamera, cameraData);

            RenderSingleCamera(context, cameraData);
        }

        static bool TryGetCullingParameters(CameraData cameraData, out ScriptableCullingParameters cullingParams)
        {
            return cameraData.camera.TryGetCullingParameters(false, out cullingParams);
        }

        static void RenderSingleCamera(ScriptableRenderContext context, CameraData cameraData)
        {
            Camera camera = cameraData.camera;
            ScriptableRenderer renderer = cameraData.renderer;

            if (renderer == null)
            {
                Debug.LogWarning($"Trying to render {camera.name} with an invalid renderer. Camera rendering will be skipped.");
                return;
            }

            using ContextContainer frameData = renderer.frameData;
            if (!TryGetCullingParameters(cameraData, out var cullingParameters))
                return;

            ScriptableRenderer.current = renderer;

            renderGraph.nativeRenderPassesEnabled = renderer.useNativeRenderPassRenderGraphCompiler;
            bool isSceneViewCamera = cameraData.isSceneViewCamera;

            CommandBuffer cmd = CommandBufferPool.Get();
            var cameraMetadata = CameraMetadataCache.GetCached(camera);
            using (new ProfilingScope(cmd, cameraMetadata.sampler))
            {
                renderer.RefreshInjectPassSettings();
                renderer.StartRenderGraphRendering(in cameraData);

                // Request previous object transforms before culling, including the first temporal frame.
                if (cameraData.IsTemporalJitterEnabled() || frameData.Get<InternalRendererData>().renderPassInputs.requiresMotionVectors)
                    camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;

                using (new ProfilingScope(Profiling.Pipeline.Renderer.setupCullingParameters))
                {
                    renderer.OnPreCullRenderPasses(in cameraData);
                    renderer.SetupCullingParameters(ref cullingParameters, ref cameraData);
                }

                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                PreIntegratedFGD.instance.RenderInit(PreIntegratedFGD.FGDIndex.FGD_GGXAndDisneyDiffuse, cmd);

                SetupPerCameraShaderConstants(cmd);

                ProbeVolumesOptions apvOptions = null;
                camera.TryGetComponent<UltraAdditionalCameraData>(out var additionalCameraData);
                bool supportProbeVolume = asset != null && (asset.supportProbeVolume || asset.supportAPRTV);
                if (supportProbeVolume)
                {
                    if (asset.supportProbeVolume && additionalCameraData != null)
                        apvOptions = additionalCameraData.volumeStack?.GetComponent<ProbeVolumesOptions>();

                    var probeVolume = ProbeReferenceVolumeProvider.instance;
                    probeVolume.SetEnableStateFromSRP(true);
                    probeVolume.SetVertexSamplingEnabled(asset.shEvalMode == ShEvalMode.PerVertex || asset.shEvalMode == ShEvalMode.Mixed);
                    if (probeVolume.isInitialized)
                    {
                        probeVolume.PerformPendingOperations();
                        if (asset.supportProbeVolume && camera.cameraType != CameraType.Reflection && camera.cameraType != CameraType.Preview)
                            probeVolume.UpdateCellStreaming(cmd, camera, apvOptions);
                    }
                }
                else
                {
                    // Leave the provider cold when neither backend is selected.
                    ProbeReferenceVolumeProvider.context?.volume?.SetEnableStateFromSRP(false);
                }

                // Emit scene/game view UI. The main game camera UI is always rendered, so this needs to be handled only for different camera types
                if (camera.cameraType == CameraType.Reflection || camera.cameraType == CameraType.Preview)
                    ScriptableRenderContext.EmitGeometryForCamera(camera);
#if UNITY_EDITOR
                else if (isSceneViewCamera)
                    ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif

                if (supportProbeVolume)
                {
                    var probeVolume = ProbeReferenceVolumeProvider.instance;
                    // APRTV sampling is enabled by its graph pass only after this
                    // camera's relight or inherited Base result is available.
                    probeVolume.BindPRVRuntimeResources(cmd, asset.supportProbeVolume);
                    if (asset.supportAPRTV)
                        cmd.SetGlobalInt("_APRTDebugMode", 0);
                    // Debug geometry must be emitted before culling.
                    if (asset.supportProbeVolume || (APRTRelightPass.CanUpdate(cameraData) &&
                        probeVolume.isInitialized && probeVolume.DataHasBeenLoaded()))
                        probeVolume.RenderDebug(camera, apvOptions, Texture2D.whiteTexture);
                }

                // Update camera motion tracking (prev matrices) from cameraData.
                // Called and updated only once, as the same camera can be rendered multiple times.
                // NOTE: Tracks only the current (this) camera, not shadow views or any other offscreen views.
                // NOTE: Shared between both Execute and Render (RG) paths.
                if (additionalCameraData != null)
                    additionalCameraData.motionVectorsPersistentData.Update(cameraData);

                // TODO: Move into the renderer. Problem: It modifies the AdditionalCameraData which is copied into RenderingData which causes value divergence for value types.
                // Update TAA persistent data based on cameraData. Most importantly resize the history render targets.
                // NOTE: Persistent data is kept over multiple frames. Its life-time differs from typical resources.
                // NOTE: Shared between both Execute and Render (RG) paths.
                if (cameraData.taaHistory != null)
                    UpdateTemporalAATargets(cameraData);

                RTHandles.SetReferenceSize(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);

                // Do NOT use cameraData after 'InitializeRenderingData'. CameraData state may diverge otherwise.
                // RenderingData takes a copy of the CameraData.
                // RenderingData needs to be created here to avoid copying cullResults.
                var data = frameData.Create<RenderingData>();
                data.cullResults = context.Cull(ref cullingParameters);

                GPUResidentDrawer.PostCullBeginCameraRendering(new RenderRequestBatcherContext { commandBuffer = cmd });

                bool isForwardPlus = cameraData.renderer.usesForwardPlusLightLists;

                // Initialize all the data types required for rendering.
                LightData lightData;
                ShadowData shadowData;
                using (new ProfilingScope(Profiling.Pipeline.initializeRenderingData))
                {
                    CreateResourceData(frameData);
                    lightData = CreateLightData(frameData, asset, data.cullResults.visibleLights);
                    shadowData = CreateShadowData(frameData, asset, isForwardPlus);
                    CreatePostProcessingData(frameData, asset);
                    CreateRenderingData(frameData, asset, cmd, isForwardPlus, cameraData.renderer);
                }

                CheckAndApplyDebugSettings(frameData);

                CreateShadowAtlasAndCullShadowCasters(lightData, shadowData, cameraData, ref data.cullResults, ref context);

                // Execute with renderGraph
                RecordAndExecuteRenderGraph(renderGraph, context, renderer, cmd, cameraData.camera, cameraMetadata.name);
                renderer.FinishRenderGraphRendering(cmd);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            using (new ProfilingScope(Profiling.Pipeline.Context.submit))
            {
                context.Submit(); // Actually execute the commands that we previously sent to the ScriptableRenderContext context
            }

            ScriptableRenderer.current = null;
        }


        private static void CreateShadowAtlasAndCullShadowCasters(LightData lightData, ShadowData shadowData, CameraData cameraData, ref CullingResults cullResults, ref ScriptableRenderContext context)
        {
            if (!shadowData.supportsMainLightShadows && !shadowData.supportsAdditionalLightShadows)
                return;

            if (shadowData.supportsMainLightShadows)
                InitializeMainLightShadowResolution(shadowData);

            if (shadowData.supportsAdditionalLightShadows)
                shadowData.shadowAtlasLayout = BuildAdditionalLightsShadowAtlasLayout(lightData, shadowData, cameraData);

            shadowData.visibleLightsShadowCullingInfos = ShadowCulling.CullShadowCasters(ref context, shadowData, ref shadowData.shadowAtlasLayout, ref cullResults, lightData.mainLightIndex);
        }

        static void RenderCameraStack(ScriptableRenderContext context, Camera baseCamera, bool isLastBaseCamera)
        {
            using var profScope = new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RenderCameraStack));

            baseCamera.TryGetComponent<UltraAdditionalCameraData>(out var baseCameraAdditionalData);
            if (baseCameraAdditionalData != null && baseCameraAdditionalData.renderType == CameraRenderType.Overlay)
                return;

            var renderer = GetRenderer(baseCamera, baseCameraAdditionalData);
            List<Camera> cameraStack = baseCameraAdditionalData?.cameraStack;

            bool mainHdrDisplayOutputActive = HDROutputForMainDisplayIsActive();
            int lastActiveOverlayCameraIndex = -1;
            if (cameraStack != null)
            {
                var baseCameraRendererType = renderer.GetType();
                bool shouldUpdateCameraStack = false;

                for (int i = 0; i < cameraStack.Count; ++i)
                {
                    Camera overlayCamera = cameraStack[i];
                    if (overlayCamera == null)
                    {
                        shouldUpdateCameraStack = true;
                        continue;
                    }

                    if (overlayCamera.isActiveAndEnabled)
                    {
                        overlayCamera.TryGetComponent<UltraAdditionalCameraData>(out var data);
                        var overlayRenderer = GetRenderer(overlayCamera, data);

                        lastActiveOverlayCameraIndex = i;
                    }
                }

                if (shouldUpdateCameraStack)
                    baseCameraAdditionalData.UpdateCameraStack();
            }


            bool isStackedRendering = lastActiveOverlayCameraIndex != -1;

            using (new CameraRenderingScope(context, baseCamera))
            {
                UpdateVolumeFramework(baseCamera, baseCameraAdditionalData);
                APRTRelightPass.PrepareSkyEnvironment(baseCamera, renderer);
                ContextContainer frameData = renderer.frameData;
                CameraData baseCameraData = CreateCameraData(frameData, baseCamera, baseCameraAdditionalData, !isStackedRendering);

                InitializeAdditionalCameraData(baseCamera, baseCameraAdditionalData, !isStackedRendering, isLastBaseCamera, baseCameraData);

#if VISUAL_EFFECT_GRAPH_0_0_1_OR_NEWER
                // VFX.VFXManager.PrepareCamera(baseCamera);
#endif

                bool hdrDisplayOutputActive = mainHdrDisplayOutputActive;

                bool finalOutputHDR = asset.supportsHDR && hdrDisplayOutputActive // Check whether any HDR display is active and the render pipeline asset allows HDR rendering
                    && baseCamera.targetTexture == null && (baseCamera.cameraType == CameraType.Game || baseCamera.cameraType == CameraType.VR) // Check whether the stack outputs to a screen
                    && baseCameraData.allowHDROutput; // Check whether the base camera allows HDR output


                // Update stack-related parameters
                baseCameraData.stackLastCameraOutputToHDR = finalOutputHDR;

                RenderSingleCamera(context, baseCameraData);

                if (isStackedRendering)
                {
                    for (int i = 0; i < cameraStack.Count; ++i)
                    {
                        var overlayCamera = cameraStack[i];
                        if (!overlayCamera.isActiveAndEnabled)
                            continue;

                        overlayCamera.TryGetComponent<UltraAdditionalCameraData>(out var overlayAdditionalCameraData);
                        // Camera is overlay and enabled
                        if (overlayAdditionalCameraData != null)
                        {
                            ContextContainer overlayFrameData = GetRenderer(overlayCamera, overlayAdditionalCameraData).frameData;
                            CameraData overlayCameraData = CreateCameraData(overlayFrameData, baseCamera, baseCameraAdditionalData, false);

                            InitializeAdditionalCameraData(overlayCamera, overlayAdditionalCameraData, false, isLastBaseCamera, overlayCameraData);
                            overlayCameraData.camera = overlayCamera;
                            overlayCameraData.baseCamera = baseCamera;

                            using (new CameraRenderingScope(context, overlayCamera))
                            {
#if VISUAL_EFFECT_GRAPH_0_0_1_OR_NEWER
                                // VFX.VFXManager.PrepareCamera(overlayCamera);
#endif
                                UpdateVolumeFramework(overlayCamera, overlayAdditionalCameraData);

                                bool isLastOverlayCamera = i == lastActiveOverlayCameraIndex;
                                InitializeAdditionalCameraData(overlayCamera, overlayAdditionalCameraData, isLastOverlayCamera, isLastBaseCamera, overlayCameraData);
                                
                                overlayCameraData.stackLastCameraOutputToHDR = finalOutputHDR;

                                RenderSingleCamera(context, overlayCameraData);
                            }
                        }
                    }
                }
            }
        }

        // since the game itself might want to control this
        internal bool enableHDROnce = true;
#if UNITY_2021_1_OR_NEWER
        void SetHDRState(List<Camera> cameras)
#else
        void SetHDRState(Camera[] cameras)
#endif
        {
            bool hdrOutputActive = HDROutputSettings.main.available && HDROutputSettings.main.active;

            // If the pipeline doesn't support HDR rendering, output to SDR.
            bool supportsSwitchingHDR = SystemInfo.hdrDisplaySupportFlags.HasFlag(HDRDisplaySupportFlags.RuntimeSwitchable);
            bool switchHDRToSDR = supportsSwitchingHDR && !asset.supportsHDR && hdrOutputActive;
            if (switchHDRToSDR)
                HDROutputSettings.main.RequestHDRModeChange(false);

#if UNITY_EDITOR
            bool requestedHDRModeChange = false;

            // Automatically switch to HDR in the editor if it's available
            if (supportsSwitchingHDR && asset.supportsHDR && PlayerSettings.useHDRDisplay && HDROutputSettings.main.available)
            {
#if UNITY_2021_1_OR_NEWER
                int cameraCount = cameras.Count;
#else
                int cameraCount = cameras.Length;
#endif
                if (cameraCount > 0 && cameras[0].cameraType != CameraType.Game)
                {
                    requestedHDRModeChange = hdrOutputActive;
                    HDROutputSettings.main.RequestHDRModeChange(false);
                }
                else if (enableHDROnce)
                {
                    requestedHDRModeChange = !hdrOutputActive;
                    HDROutputSettings.main.RequestHDRModeChange(true);
                    enableHDROnce = false;
                }
            }

            if (requestedHDRModeChange || switchHDRToSDR)
            {
                // Repaint scene views and game views so the HDR mode request is applied
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
#endif

            // Make sure HDR auto tonemap is off if the UltraRP is handling it
            if (hdrOutputActive)
                HDROutputSettings.main.automaticHDRTonemapping = false;
        }

        internal static void GetHDROutputLuminanceParameters(HDROutputUtils.HDRDisplayInformation hdrDisplayInformation, ColorGamut hdrDisplayColorGamut, Tonemapping tonemapping, out Vector4 hdrOutputParameters)
        {
            float minNits = hdrDisplayInformation.minToneMapLuminance;
            float maxNits = hdrDisplayInformation.maxToneMapLuminance;
            float paperWhite = hdrDisplayInformation.paperWhiteNits;

            if (!tonemapping.detectPaperWhite.value)
            {
                paperWhite = tonemapping.paperWhite.value;
            }
            if (!tonemapping.detectBrightnessLimits.value)
            {
                minNits = tonemapping.minNits.value;
                maxNits = tonemapping.maxNits.value;
            }

            hdrOutputParameters = new Vector4(minNits, maxNits, paperWhite, 1f / paperWhite);
        }

        internal static void GetHDROutputGradingParameters(Tonemapping tonemapping, out Vector4 hdrOutputParameters)
        {
            int eetfMode = 0;
            float hueShift = 0.0f;

            switch (tonemapping.mode.value)
            {
                case TonemappingMode.Neutral:
                    eetfMode = (int)tonemapping.neutralHDRRangeReductionMode.value;
                    hueShift = tonemapping.hueShiftAmount.value;
                    break;

                case TonemappingMode.ACES:
                    eetfMode = (int)tonemapping.acesPreset.value;
                    break;
            }

            hdrOutputParameters = new Vector4(eetfMode, hueShift, 0.0f, 0.0f);
        }

        static void SetSupportedRenderingFeatures(UltraRenderPipelineAsset pipelineAsset)
        {
#if UNITY_EDITOR
            SupportedRenderingFeatures.active = new SupportedRenderingFeatures()
            {
                reflectionProbeModes = SupportedRenderingFeatures.ReflectionProbeModes.None,
                defaultMixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeModes.Subtractive,
                mixedLightingModes = SupportedRenderingFeatures.LightmapMixedBakeModes.Subtractive | SupportedRenderingFeatures.LightmapMixedBakeModes.IndirectOnly | SupportedRenderingFeatures.LightmapMixedBakeModes.Shadowmask,
                lightmapBakeTypes = LightmapBakeType.Baked | LightmapBakeType.Mixed | LightmapBakeType.Realtime,
                lightmapsModes = LightmapsMode.CombinedDirectional | LightmapsMode.NonDirectional,
                lightProbeProxyVolumes = false,
                motionVectors = true,
                receiveShadows = false,
                reflectionProbes = false,
                reflectionProbesBlendDistance = true,
                particleSystemInstancing = true,
                overridesEnableLODCrossFade = true
            };
            SceneViewDrawMode.SetupDrawMode();
#endif

            SupportedRenderingFeatures.active.supportsHDR = pipelineAsset.supportsHDR;
            SupportedRenderingFeatures.active.rendersUIOverlay = true;
        }

        void SetupPerFrameShaderConstants()
        {
            using var profScope = new ProfilingScope(Profiling.Pipeline.setupPerFrameShaderConstants);

            // Required for 2D Unlit Shadergraph master node as it doesn't currently support hidden properties.
            Shader.SetGlobalColor(ShaderPropertyId.rendererColor, Color.white);
            Shader.SetGlobalInteger(ShaderPropertyId.frameCount, Time.frameCount);

            Texture2D ditheringTexture = null;
            switch (asset.lodCrossFadeDitheringType)
            {
                case LODCrossFadeDitheringType.BayerMatrix:
                    ditheringTexture = runtimeTextures.bayerMatrixTex;
                    break;
                case LODCrossFadeDitheringType.BlueNoise:
                    ditheringTexture = runtimeTextures.blueNoise64LTex;
                    break;
                default:
                    Debug.LogWarning($"This Lod Cross Fade Dithering Type is not supported: {asset.lodCrossFadeDitheringType}");
                    break;
            }

            if (ditheringTexture != null)
            {
                Shader.SetGlobalFloat(ShaderPropertyId.ditheringTextureInvSize, 1.0f / ditheringTexture.width);
                Shader.SetGlobalTexture(ShaderPropertyId.ditheringTexture, ditheringTexture);
            }
        }

        static ScriptableRenderer GetRenderer(Camera camera, UltraAdditionalCameraData additionalCameraData)
        {
            var renderer = additionalCameraData != null ? additionalCameraData.scriptableRenderer : null;
            if (renderer == null || camera.cameraType == CameraType.SceneView)
                renderer = asset.scriptableRenderer;

            return renderer;
        }

        static CameraData CreateCameraData(ContextContainer frameData, Camera camera, UltraAdditionalCameraData additionalCameraData, bool resolveFinalTarget)
        {
            using var profScope = new ProfilingScope(Profiling.Pipeline.initializeCameraData);
            var renderer = GetRenderer(camera, additionalCameraData);
            CameraData cameraData = frameData.Create<CameraData>();
            InitializeStackedCameraData(camera, additionalCameraData, cameraData);

            cameraData.camera = camera;
            cameraData.historyManager = additionalCameraData?.historyManager;

            ///////////////////////////////////////////////////////////////////
            //                      Descriptor settings                       /
            ///////////////////////////////////////////////////////////////////
            bool rendererSupportsMSAA = renderer != null && renderer.supportedRenderingFeatures.msaa;

            int msaaSamples = 1;
            if (camera.allowMSAA && asset.msaaSampleCount > 1 && rendererSupportsMSAA)
                msaaSamples = (camera.targetTexture != null) ? camera.targetTexture.antiAliasing : asset.msaaSampleCount;

            bool needsAlphaChannel = Graphics.preserveFramebufferAlpha;

            cameraData.hdrColorBufferPrecision = asset ? asset.hdrColorBufferPrecision : HDRColorBufferPrecision._32Bits;
            cameraData.cameraTargetDescriptor = CreateRenderTextureDescriptor(camera, cameraData,
                cameraData.isHdrEnabled, cameraData.hdrColorBufferPrecision, msaaSamples, needsAlphaChannel);

            uint count = GraphicsFormatUtility.GetAlphaComponentCount(cameraData.cameraTargetDescriptor.graphicsFormat);
            cameraData.isAlphaOutputEnabled = GraphicsFormatUtility.HasAlphaChannel(cameraData.cameraTargetDescriptor.graphicsFormat);
            if (cameraData.camera.cameraType == CameraType.SceneView && CoreUtils.IsSceneFilteringEnabled())
                cameraData.isAlphaOutputEnabled = true;

            return cameraData;
        }

        static void InitializeStackedCameraData(Camera baseCamera, UltraAdditionalCameraData baseAdditionalCameraData, CameraData cameraData)
        {
            using var profScope = new ProfilingScope(Profiling.Pipeline.initializeStackedCameraData);

            var settings = asset;
            cameraData.targetTexture = baseCamera.targetTexture;
            cameraData.cameraType = baseCamera.cameraType;
            bool isSceneViewCamera = cameraData.isSceneViewCamera;

            if (isSceneViewCamera)
            {
                cameraData.volumeLayerMask = 1; // "Default"
                cameraData.volumeTrigger = null;
                cameraData.isStopNaNEnabled = false;
                cameraData.isDitheringEnabled = false;
                cameraData.antialiasing = AntialiasingMode.None;
                cameraData.antialiasingQuality = AntialiasingQuality.High;
                cameraData.allowHDROutput = false;
            }
            else if (baseAdditionalCameraData != null)
            {
                cameraData.volumeLayerMask = baseAdditionalCameraData.volumeLayerMask;
                cameraData.volumeTrigger = baseAdditionalCameraData.volumeTrigger == null ? baseCamera.transform : baseAdditionalCameraData.volumeTrigger;
                cameraData.isStopNaNEnabled = baseAdditionalCameraData.stopNaN && SystemInfo.graphicsShaderLevel >= 35;
                cameraData.isDitheringEnabled = baseAdditionalCameraData.dithering;
                cameraData.antialiasing = baseAdditionalCameraData.antialiasing;
                cameraData.antialiasingQuality = baseAdditionalCameraData.antialiasingQuality;
                cameraData.allowHDROutput = baseAdditionalCameraData.allowHDROutput;
            }
            else
            {
                cameraData.volumeLayerMask = 1; // "Default"
                cameraData.volumeTrigger = null;
                cameraData.isStopNaNEnabled = false;
                cameraData.isDitheringEnabled = false;
                cameraData.antialiasing = AntialiasingMode.None;
                cameraData.antialiasingQuality = AntialiasingQuality.High;
                cameraData.allowHDROutput = true;
            }

            ///////////////////////////////////////////////////////////////////
            //          Settings that control output of the camera            /
            ///////////////////////////////////////////////////////////////////
            cameraData.isHdrEnabled = baseCamera.allowHDR && settings.supportsHDR;
            cameraData.allowHDROutput &= settings.supportsHDR;

            Rect cameraRect = baseCamera.rect;
            cameraData.pixelRect = baseCamera.pixelRect;
            cameraData.pixelWidth = baseCamera.pixelWidth;
            cameraData.pixelHeight = baseCamera.pixelHeight;
            cameraData.aspectRatio = (float)cameraData.pixelWidth / (float)cameraData.pixelHeight;
            cameraData.isDefaultViewport = (!(Math.Abs(cameraRect.x) > 0.0f || Math.Abs(cameraRect.y) > 0.0f ||
                Math.Abs(cameraRect.width) < 1.0f || Math.Abs(cameraRect.height) < 1.0f));

            bool isScenePreviewOrReflectionCamera = cameraData.cameraType == CameraType.SceneView 
                || cameraData.cameraType == CameraType.Preview 
                || cameraData.cameraType == CameraType.Reflection;

            const float renderScaleThreshold = 0.05f;
            bool disableRenderScale = ((Mathf.Abs(1.0f - settings.renderScale) < renderScaleThreshold) || isScenePreviewOrReflectionCamera);
            cameraData.renderScale = disableRenderScale ? 1.0f : settings.renderScale;

            cameraData.upscalingFilter = ResolveUpscalingFilterSelection(new Vector2(cameraData.pixelWidth, cameraData.pixelHeight), cameraData.renderScale, settings.upscalingFilter);

            // SGSR2 supplies its own anti-aliasing. Do not run FXAA/SMAA/TAA on top,
            // including when unsupported resources require a non-temporal fallback.
            if (settings.upscalingFilter == UpscalingFilterSelection.SGSR2 &&
                cameraData.renderScale <= 1.0f && !isScenePreviewOrReflectionCamera)
                cameraData.antialiasing = AntialiasingMode.None;

            // When FSR is enabled, we still consider 100% render scale an upscaling operation. (This behavior is only intended for game view cameras) 
            // This allows us to run the FSR shader passes all the time since they improve visual quality even at 100% scale.
            if (cameraData.renderScale > 1.0f)
                cameraData.imageScalingMode = ImageScalingMode.Downscaling;
            else if ((cameraData.renderScale < 1.0f) || (!isScenePreviewOrReflectionCamera &&
                     (cameraData.upscalingFilter == ImageUpscalingFilter.FSR || cameraData.upscalingFilter == ImageUpscalingFilter.STP ||
                      cameraData.upscalingFilter == ImageUpscalingFilter.SGSR1 || cameraData.upscalingFilter == ImageUpscalingFilter.SGSR2)))
            {
                // When certain upscalers are requested, we still consider 100% render scale an upscaling operation. (This behavior is only intended for game view cameras)
                // This allows us to run the upscaling shader passes all the time since they improve visual quality even at 100% scale.

                cameraData.imageScalingMode = ImageScalingMode.Upscaling;

                // When STP is requested, we force temporal anti-aliasing on since it's a prerequisite.
                if (cameraData.upscalingFilter == ImageUpscalingFilter.STP)
                    cameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
            }
            else
                cameraData.imageScalingMode = ImageScalingMode.None;

            cameraData.fsrOverrideSharpness = settings.fsrOverrideSharpness;
            cameraData.fsrSharpness = settings.fsrSharpness;

            var commonOpaqueFlags = SortingCriteria.CommonOpaque;
            var noFrontToBackOpaqueFlags = SortingCriteria.SortingLayer 
                | SortingCriteria.RenderQueue 
                | SortingCriteria.OptimizeStateChanges 
                | SortingCriteria.CanvasOrder;
            bool hasHSRGPU = SystemInfo.hasHiddenSurfaceRemovalOnGPU;
            bool canSkipFrontToBackSorting = (baseCamera.opaqueSortMode == OpaqueSortMode.Default && hasHSRGPU) || baseCamera.opaqueSortMode == OpaqueSortMode.NoDistanceSort;

            cameraData.defaultOpaqueSortFlags = canSkipFrontToBackSorting ? noFrontToBackOpaqueFlags : commonOpaqueFlags;
            cameraData.captureActions = CameraCaptureBridge.GetCaptureActions(baseCamera);
        }

        static void InitializeAdditionalCameraData(Camera camera, UltraAdditionalCameraData additionalCameraData, bool resolveFinalTarget, bool isLastBaseCamera, CameraData cameraData)
        {
            using var profScope = new ProfilingScope(Profiling.Pipeline.initializeAdditionalCameraData);

            var renderer = GetRenderer(camera, additionalCameraData);
            var settings = asset;

            bool anyShadowsEnabled = settings.supportsMainLightShadows || settings.supportsAdditionalLightShadows;
                cameraData.maxShadowDistance = Mathf.Min(settings.shadowDistance, camera.farClipPlane);
                cameraData.maxShadowDistance = (anyShadowsEnabled && cameraData.maxShadowDistance >= camera.nearClipPlane) ? cameraData.maxShadowDistance : 0.0f;

            bool isSceneViewCamera = cameraData.isSceneViewCamera;
            if (isSceneViewCamera)
            {
                cameraData.renderType = CameraRenderType.Base;
                cameraData.clearDepth = true;
                cameraData.postProcessEnabled = CoreUtils.ArePostProcessesEnabled(camera);
                cameraData.useScreenCoordOverride = false;
                cameraData.screenSizeOverride = cameraData.pixelRect.size;
                cameraData.screenCoordScaleBias = Vector2.one;
            }
            else if (additionalCameraData != null)
            {
                cameraData.renderType = additionalCameraData.renderType;
                cameraData.clearDepth = (additionalCameraData.renderType != CameraRenderType.Base) ? additionalCameraData.clearDepth : true;
                cameraData.postProcessEnabled = additionalCameraData.renderPostProcessing;
                cameraData.maxShadowDistance = (additionalCameraData.renderShadows) ? cameraData.maxShadowDistance : 0.0f;
                cameraData.useScreenCoordOverride = additionalCameraData.useScreenCoordOverride;
                cameraData.screenSizeOverride = additionalCameraData.screenSizeOverride;
                cameraData.screenCoordScaleBias = additionalCameraData.screenCoordScaleBias;
            }
            else
            {
                cameraData.renderType = CameraRenderType.Base;
                cameraData.clearDepth = true;
                cameraData.postProcessEnabled = false;
                cameraData.useScreenCoordOverride = false;
                cameraData.screenSizeOverride = cameraData.pixelRect.size;
                cameraData.screenCoordScaleBias = Vector2.one;
            }

            cameraData.renderer = renderer;
            cameraData.requiresDepthTexture |= isSceneViewCamera;
            cameraData.resolveFinalTarget = resolveFinalTarget;
            cameraData.isLastBaseCamera = isLastBaseCamera;

            // enable GPU occlusion culling in game and scene views only
            cameraData.useGPUOcclusionCulling = GPUResidentDrawer.IsInstanceOcclusionCullingEnabled()
                && renderer.supportsGPUOcclusion
                && camera.cameraType is CameraType.SceneView or CameraType.Game or CameraType.Preview;

            cameraData.requiresDepthTexture |= cameraData.useGPUOcclusionCulling;

            bool isOverlayCamera = (cameraData.renderType == CameraRenderType.Overlay);
            if (isOverlayCamera)
                cameraData.requiresOpaqueTexture = false;

            // NOTE: TAA depends on XR modifications of cameraTargetDescriptor.
            if (additionalCameraData != null)
                UpdateTemporalAAData(cameraData, additionalCameraData);

            // Resolve eligibility before building the projection matrix: a bilinear
            // fallback must never inherit jitter from an unavailable temporal resolve.
            cameraData.sgsr2Enabled = cameraData.CanEnableSGSR2();
            bool sgsr1Unsupported = cameraData.upscalingFilter == ImageUpscalingFilter.SGSR1 &&
                (camera.allowDynamicResolution || camera.stereoEnabled ||
                 cameraData.cameraTargetDescriptor.dimension != TextureDimension.Tex2D ||
                 !renderer.graph.materialLib.HasMaterial("sgsr1PS"));
            if (sgsr1Unsupported || (cameraData.IsSGSR2Requested() && !cameraData.IsSGSR2Enabled()))
            {
                cameraData.sgsr2History?.Reset();
                cameraData.sgsr2History = null;
                cameraData.upscalingFilter = ImageUpscalingFilter.Linear;
                if (cameraData.renderScale == 1.0f)
                    cameraData.imageScalingMode = ImageScalingMode.None;
            }

            Matrix4x4 projectionMatrix = camera.projectionMatrix;
            if (isOverlayCamera && !camera.orthographic && cameraData.pixelRect != camera.pixelRect)
            {
                // m00 = (cotangent / aspect), therefore m00 * aspect gives us cotangent.
                float cotangent = camera.projectionMatrix.m00 * camera.aspect;

                // Get new m00 by dividing by base camera aspectRatio.
                float newCotangent = cotangent / cameraData.aspectRatio;
                projectionMatrix.m00 = newCotangent;
            }

            ApplyTaaRenderingDebugOverrides(ref cameraData.taaSettings);

            TemporalAA.JitterFunc jitterFunc = cameraData.IsSTPEnabled() ? StpUtils.s_jitterFunc : TemporalAA.jitterFunc;
            Matrix4x4 jitterMat = TemporalAA.CalculateJitterMatrix(cameraData, jitterFunc);
            cameraData.SetViewProjectionAndJitterMatrix(camera.worldToCameraMatrix, projectionMatrix, jitterMat);

            // SetPixelCoordToViewDirWSMatrix for Sky and compute shader
            var screenSize = new Vector4(cameraData.cameraTargetDescriptor.width, 
                cameraData.cameraTargetDescriptor.height,
                1.0f / cameraData.cameraTargetDescriptor.width, 
                1.0f / cameraData.cameraTargetDescriptor.height);
            var gpuProj = cameraData.GetGPUProjectionMatrix(true);
            var gpuProjAspect = RenderingUtils.ProjectionMatrixAspect(gpuProj);
            cameraData.SetPixelCoordToViewDirWSMatrix(RenderingUtils.ComputePixelCoordToWorldSpaceViewDirectionMatrix(camera, camera.worldToCameraMatrix, gpuProj, screenSize, gpuProjAspect));

            cameraData.worldSpaceCameraPos = camera.transform.position;

            var backgroundColorSRGB = camera.backgroundColor;
#if UNITY_EDITOR
            if (camera.cameraType == CameraType.Preview && camera.clearFlags != CameraClearFlags.SolidColor)
                backgroundColorSRGB = CoreRenderPipelinePreferences.previewBackgroundColor;
#endif

            cameraData.backgroundColor = CoreUtils.ConvertSRGBToActiveColorSpace(backgroundColorSRGB);

            cameraData.stackAnyPostProcessingEnabled = cameraData.postProcessEnabled;
            cameraData.stackLastCameraOutputToHDR = cameraData.isHDROutputActive;

            bool allowAlphaOutput = !cameraData.postProcessEnabled || (cameraData.postProcessEnabled && settings.allowPostProcessAlphaOutput);
            cameraData.isAlphaOutputEnabled = cameraData.isAlphaOutputEnabled && allowAlphaOutput;
        }

        static RenderingData CreateRenderingData(ContextContainer frameData, UltraRenderPipelineAsset settings, CommandBuffer cmd, bool isForwardPlus, ScriptableRenderer renderer)
        {
            var lightData = frameData.Get<LightData>();

            var data = frameData.Get<RenderingData>();
            data.supportsDynamicBatching = settings.supportsDynamicBatching;
            data.perObjectData = GetPerObjectLightFlags(lightData.additionalLightsCount, isForwardPlus);
            data.commandBuffer = null;

            return data;
        }

        static ShadowData CreateShadowData(ContextContainer frameData, UltraRenderPipelineAsset settings, bool isForwardPlus)
        {
            using var profScope = new ProfilingScope(Profiling.Pipeline.initializeShadowData);

            // Initial setup
            ShadowData shadowData = frameData.Create<ShadowData>();
            CameraData cameraData = frameData.Get<CameraData>();
            LightData lightData = frameData.Get<LightData>();

            s_shadowBiasData.Clear();
            s_shadowResolutionData.Clear();

            shadowData.shadowmapDepthBufferBits = 16;
            shadowData.mainLightShadowCascadeBorder = settings.cascadeBorder;
            shadowData.mainLightShadowCascadesCount = settings.shadowCascadeCount;
            shadowData.mainLightShadowCascadesSplit = GetMainLightCascadeSplit(shadowData.mainLightShadowCascadesCount, settings);
            shadowData.mainLightShadowmapWidth = settings.mainLightShadowmapResolution;
            shadowData.mainLightShadowmapHeight = settings.mainLightShadowmapResolution;
            shadowData.additionalLightsShadowmapWidth = shadowData.additionalLightsShadowmapHeight = settings.additionalLightsShadowmapResolution;

            // This will be setup in AdditionalLightsShadowCasterPass.
            shadowData.isKeywordAdditionalLightShadowsEnabled = false;
            shadowData.isKeywordSoftShadowsEnabled = false;

            // Those fields must be setup after ApplyAdaptivePerformance is called on RenderingData.
            // This is because this function can currently modify mainLightShadowmapWidth, mainLightShadowmapHeight and mainLightShadowCascadesCount.
            // All three parameters are needed to compute those fields, so their initialization is deferred to InitializeMainLightShadowResolution.
            shadowData.mainLightShadowResolution = 0;
            shadowData.mainLightRenderTargetWidth = 0;
            shadowData.mainLightRenderTargetHeight = 0;

            // Those two fields must be initialized using ShadowData, which can be modified right after this function (InitializeRenderingData) by ApplyAdaptivePerformance.
            // Their initializations is thus deferred to a later point when ShadowData is fully initialized.
            shadowData.shadowAtlasLayout = default;
            shadowData.visibleLightsShadowCullingInfos = default;

            // Setup data that requires iterating over lights
            // ------------------------------------------------------
            var mainLightIndex = lightData.mainLightIndex;
            var visibleLights = lightData.visibleLights;

            // maxShadowDistance is set to 0.0f when the Render Shadows toggle is disabled on the camera
            bool cameraRenderShadows = cameraData.maxShadowDistance > 0.0f;

            shadowData.mainLightShadowsEnabled = settings.supportsMainLightShadows && settings.mainLightRenderingMode == LightRenderingMode.PerPixel;
            shadowData.supportsMainLightShadows = SystemInfo.supportsShadows && shadowData.mainLightShadowsEnabled && cameraRenderShadows;

            shadowData.additionalLightShadowsEnabled = settings.supportsAdditionalLightShadows && (settings.additionalLightsRenderingMode == LightRenderingMode.PerPixel || isForwardPlus);
            shadowData.supportsAdditionalLightShadows = SystemInfo.supportsShadows && shadowData.additionalLightShadowsEnabled && !lightData.shadeAdditionalLightsPerVertex && cameraRenderShadows;

            // Early out if shadows are not rendered...
            if (!shadowData.supportsMainLightShadows && !shadowData.supportsAdditionalLightShadows)
                return shadowData;

            shadowData.supportsMainLightShadows &= mainLightIndex != -1
                                                   && visibleLights[mainLightIndex].light != null
                                                   && visibleLights[mainLightIndex].light.shadows != LightShadows.None;


            if (shadowData.supportsAdditionalLightShadows)
            {
                // Check if there is at least one additional light casting shadows...
                bool additionalLightsCastShadows = false;
                for (int i = 0; i < visibleLights.Length; ++i)
                {
                    if (i == mainLightIndex)
                        continue;

                    ref VisibleLight vl = ref visibleLights.UnsafeElementAtMutable(i);

                    // UltraRP doesn't support additional directional light shadows yet
                    if (vl.lightType == LightType.Spot || vl.lightType == LightType.Point)
                    {
                        Light light = vl.light;
                        if (light == null || light.shadows == LightShadows.None)
                            continue;

                        additionalLightsCastShadows = true;
                        break;
                    }
                }
                shadowData.supportsAdditionalLightShadows &= additionalLightsCastShadows;
            }

            // Check again if it's possible to early out...
            if (!shadowData.supportsMainLightShadows && !shadowData.supportsAdditionalLightShadows)
                return shadowData;

            for (int i = 0; i < visibleLights.Length; ++i)
            {
                if (!shadowData.supportsMainLightShadows && i == mainLightIndex)
                {
                    s_shadowBiasData.Add(Vector4.zero);
                    s_shadowResolutionData.Add(0);
                    continue;
                }

                if (!shadowData.supportsAdditionalLightShadows && i != mainLightIndex)
                {
                    s_shadowBiasData.Add(Vector4.zero);
                    s_shadowResolutionData.Add(0);
                    continue;
                }

                ref VisibleLight vl = ref visibleLights.UnsafeElementAtMutable(i);
                Light light = vl.light;
                UltraAdditionalLightData data = null;
                if (light != null)
                    data = light.GetOrMigrateAdditionalLightData();

                if (data && !data.usePipelineSettings)
                    s_shadowBiasData.Add(new Vector4(light.shadowBias, light.shadowNormalBias, 0.0f, 0.0f));
                else
                    s_shadowBiasData.Add(new Vector4(settings.shadowDepthBias, settings.shadowNormalBias, 0.0f, 0.0f));

                if (data && (data.additionalLightsShadowResolutionTier == UltraAdditionalLightData.additionalLightsShadowResolutionTierCustom))
                    s_shadowResolutionData.Add((int)light.shadowResolution); // native code does not clamp light.shadowResolution between -1 and 3
                else if (data && (data.additionalLightsShadowResolutionTier != UltraAdditionalLightData.additionalLightsShadowResolutionTierCustom))
                {
                    int resolutionTier = Mathf.Clamp(data.additionalLightsShadowResolutionTier, UltraAdditionalLightData.additionalLightsShadowResolutionTierLow, UltraAdditionalLightData.additionalLightsShadowResolutionTierHigh);
                    s_shadowResolutionData.Add(settings.GetAdditionalLightsShadowResolution(resolutionTier));
                }
                else
                    s_shadowResolutionData.Add(settings.GetAdditionalLightsShadowResolution(UltraAdditionalLightData.additionalLightsShadowDefaultResolutionTier));
            }

            shadowData.bias = s_shadowBiasData;
            shadowData.resolution = s_shadowResolutionData;
            shadowData.supportsSoftShadows = settings.supportsSoftShadows && (shadowData.supportsMainLightShadows || shadowData.supportsAdditionalLightShadows);

            return shadowData;
        }


        private static Vector3 GetMainLightCascadeSplit(int mainLightShadowCascadesCount, UltraRenderPipelineAsset settings)
        {
            switch (mainLightShadowCascadesCount)
            {
                case 1: return new Vector3(1.0f, 0.0f, 0.0f);
                case 2: return new Vector3(settings.cascade2Split, 1.0f, 0.0f);
                case 3: return settings.cascade3Split;
                default: return settings.cascade4Split;
            }
        }

        static void InitializeMainLightShadowResolution(ShadowData shadowData)
        {
            shadowData.mainLightShadowResolution = ShadowUtils.GetMaxTileResolutionInAtlas(shadowData.mainLightShadowmapWidth, shadowData.mainLightShadowmapHeight, shadowData.mainLightShadowCascadesCount);
            shadowData.mainLightRenderTargetWidth = shadowData.mainLightShadowmapWidth;
            shadowData.mainLightRenderTargetHeight = (shadowData.mainLightShadowCascadesCount == 2) ? shadowData.mainLightShadowmapHeight >> 1 : shadowData.mainLightShadowmapHeight;
        }

        static PostProcessingData CreatePostProcessingData(ContextContainer frameData, UltraRenderPipelineAsset settings)
        {
            PostProcessingData postProcessingData = frameData.Create<PostProcessingData>();
            CameraData cameraData = frameData.Get<CameraData>();

            postProcessingData.isEnabled = cameraData.stackAnyPostProcessingEnabled;

            postProcessingData.gradingMode = settings.supportsHDR
                ? settings.colorGradingMode
                : ColorGradingMode.LowDynamicRange;

            if (cameraData.stackLastCameraOutputToHDR)
                postProcessingData.gradingMode = ColorGradingMode.HighDynamicRange;

            postProcessingData.lutSize = settings.colorGradingLutSize;
            postProcessingData.useFastSRGBLinearConversion = settings.useFastSRGBLinearConversion;
            postProcessingData.supportScreenSpaceLensFlare = settings.supportScreenSpaceLensFlare;
            postProcessingData.supportDataDrivenLensFlare = settings.supportDataDrivenLensFlare;

            return postProcessingData;
        }

        static ResourceData CreateResourceData(ContextContainer frameData)
        {
            return frameData.Create<ResourceData>();
        }

        static LightData CreateLightData(ContextContainer frameData, UltraRenderPipelineAsset settings, NativeArray<VisibleLight> visibleLights)
        {
            using var profScope = new ProfilingScope(Profiling.Pipeline.initializeLightData);

            LightData lightData = frameData.Create<LightData>();

            lightData.mainLightIndex = GetMainLightIndex(settings, visibleLights);


            var lightCount = visibleLights.Length;
            var dirlightOffset = 0;
            while (dirlightOffset < lightCount && visibleLights[dirlightOffset].lightType == LightType.Directional)
            {
                dirlightOffset++;
            }
            lightData.directionalLightsCount = dirlightOffset;

            if (settings.additionalLightsRenderingMode != LightRenderingMode.Disabled)
            {
                lightData.additionalLightsCount = Math.Min((lightData.mainLightIndex != -1) ? visibleLights.Length - 1 : visibleLights.Length, maxVisibleAdditionalLights);
                lightData.maxPerObjectAdditionalLightsCount = Math.Min(settings.maxAdditionalLightsCount, maxPerObjectLights);
            }
            else
            {
                lightData.additionalLightsCount = 0;
                lightData.maxPerObjectAdditionalLightsCount = 0;
            }

            lightData.supportsAdditionalLights = settings.additionalLightsRenderingMode != LightRenderingMode.Disabled;
            lightData.shadeAdditionalLightsPerVertex = settings.additionalLightsRenderingMode == LightRenderingMode.PerVertex;
            lightData.visibleLights = visibleLights;
            lightData.supportsMixedLighting = settings.supportsMixedLighting;
            lightData.reflectionProbeBlending = settings.reflectionProbeBlending;
            lightData.reflectionProbeBoxProjection = settings.reflectionProbeBoxProjection;
            lightData.supportsLightLayers = RenderingUtils.SupportsLightLayers(SystemInfo.graphicsDeviceType) && settings.useRenderingLayers;

            return lightData;
        }

        private static void ApplyTaaRenderingDebugOverrides(ref TAASettings taaSettings)
        {
            var debugDisplaySettings = UltraRenderPipelineDebugDisplaySettings.Instance;
            DebugDisplaySettingsRendering renderingSettings = debugDisplaySettings.renderingSettings;
            switch (renderingSettings.taaDebugMode)
            {
                case DebugDisplaySettingsRendering.TaaDebugMode.ShowClampedHistory:
                    taaSettings.frameInfluence = 0;
                    break;

                case DebugDisplaySettingsRendering.TaaDebugMode.ShowRawFrame:
                    taaSettings.frameInfluence = 1;
                    break;

                case DebugDisplaySettingsRendering.TaaDebugMode.ShowRawFrameNoJitter:
                    taaSettings.frameInfluence = 1;
                    taaSettings.jitterScale = 0;
                    break;
            }
        }

        static void UpdateVolumeFramework(Camera camera, UltraAdditionalCameraData additionalCameraData)
        {
            using var profScope = new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.UpdateVolumeFramework));

            // We update the volume framework for:
            // * All cameras in the editor when not in playmode
            // * scene cameras
            // * cameras with update mode set to EveryFrame
            // * cameras with update mode set to UsePipelineSettings and the UltraRP Asset set to EveryFrame
            bool shouldUpdate = camera.cameraType == CameraType.SceneView;
            shouldUpdate |= additionalCameraData != null && additionalCameraData.requiresVolumeFrameworkUpdate;

#if UNITY_EDITOR
            shouldUpdate |= Application.isPlaying == false;
#endif

            // When we have volume updates per-frame disabled...
            if (!shouldUpdate && additionalCameraData)
            {
                // If an invalid volume stack is present, destroy it
                if (additionalCameraData.volumeStack != null && !additionalCameraData.volumeStack.isValid)
                    camera.DestroyVolumeStack(additionalCameraData);

                // Create a local volume stack and cache the state if it's null
                if (additionalCameraData.volumeStack == null)
                    camera.UpdateVolumeStack(additionalCameraData);

                VolumeManager.instance.stack = additionalCameraData.volumeStack;
                return;
            }

            // When we want to update the volumes every frame...

            // We destroy the volumeStack in the additional camera data, if present, to make sure
            // it gets recreated and initialized if the update mode gets later changed to ViaScripting...
            if (additionalCameraData && additionalCameraData.volumeStack != null)
                camera.DestroyVolumeStack(additionalCameraData);

            // Get the mask + trigger and update the stack
            camera.GetVolumeLayerMaskAndTrigger(additionalCameraData, out LayerMask layerMask, out Transform trigger);
            VolumeManager.instance.ResetMainStack();
            VolumeManager.instance.Update(trigger, layerMask);
        }

        static void AdjustUIOverlayOwnership(int cameraCount)
        {
            if (XRSystem.displayActive || cameraCount == 0)
                SupportedRenderingFeatures.active.rendersUIOverlay = false;
            else
                SupportedRenderingFeatures.active.rendersUIOverlay = true;
        }

        private static void SetupScreenMSAASamplesState(int cameraCount)
        {
#if UNITY_6000_0_OR_NEWER
            // Enable potential bandwidth optimization only when rendering a single base camera
            canOptimizeScreenMSAASamples = (cameraCount == 1);
            // Save ScreenMSAASamples value at beginning of the frame, useful for iOS/macOS
            startFrameScreenMSAASamples = Screen.msaaSamples;
#else
            canOptimizeScreenMSAASamples = false;
            startFrameScreenMSAASamples = Mathf.Max(QualitySettings.antiAliasing, 1);
#endif
        }

        internal static bool HDROutputForAnyDisplayIsActive()
        {
            bool hdrDisplayOutputActive = HDROutputForMainDisplayIsActive();
            return hdrDisplayOutputActive;
        }

        internal static bool HDROutputForMainDisplayIsActive()
        {
            bool hdrOutputSupported = SystemInfo.hdrDisplaySupportFlags.HasFlag(HDRDisplaySupportFlags.Supported) && asset.supportsHDR;
            bool hdrOutputActive = HDROutputSettings.main.available && HDROutputSettings.main.active;
            return hdrOutputSupported && hdrOutputActive;
        }

        static ImageUpscalingFilter ResolveUpscalingFilterSelection(Vector2 imageSize, float renderScale, UpscalingFilterSelection selection)
        {
            ImageUpscalingFilter filter = ImageUpscalingFilter.Linear;
            if ((selection == UpscalingFilterSelection.FSR) && (!FSRUtils.IsSupported()))
                selection = UpscalingFilterSelection.Auto;
            if (selection == UpscalingFilterSelection.STP && !StpUtils.IsSupported())
                selection = UpscalingFilterSelection.Linear;
            if (selection == UpscalingFilterSelection.SGSR1 && !TAAU_SGSR2.IsSGSR1Supported())
                selection = UpscalingFilterSelection.Linear;
            if (selection == UpscalingFilterSelection.SGSR2 && !TAAU_SGSR2.IsSupported())
                selection = UpscalingFilterSelection.Linear;

            switch (selection)
            {
                case UpscalingFilterSelection.Auto:
                {
                    // The user selected "auto" for their upscaling filter so we should attempt to choose the best filter
                    // for the current situation. When the current resolution and render scale are compatible with integer
                    // scaling we use the point sampling filter. Otherwise we just use the default filter (linear).
                    float pixelScale = (1.0f / renderScale);
                    bool isIntegerScale = Mathf.Approximately((pixelScale - Mathf.Floor(pixelScale)), 0.0f);
                
                    if (isIntegerScale)
                    {
                        float widthScale = (imageSize.x / pixelScale);
                        float heightScale = (imageSize.y / pixelScale);
                
                        bool isImageCompatible = (Mathf.Approximately((widthScale - Mathf.Floor(widthScale)), 0.0f) &&
                                                  Mathf.Approximately((heightScale - Mathf.Floor(heightScale)), 0.0f));
                
                        if (isImageCompatible)
                            filter = ImageUpscalingFilter.Point;
                    }
                
                    break;
                }

                case UpscalingFilterSelection.Linear:
                {
                    // Do nothing since linear is already the default
                
                    break;
                }
                case UpscalingFilterSelection.Point:
                {
                    filter = ImageUpscalingFilter.Point;
                
                    break;
                }
                case UpscalingFilterSelection.FSR:
                {
                    filter = ImageUpscalingFilter.FSR;

                    break;
                }
                case UpscalingFilterSelection.STP:
                {
                    filter = ImageUpscalingFilter.STP;
                    break;
                }
                case UpscalingFilterSelection.SGSR1:
                {
                    filter = ImageUpscalingFilter.SGSR1;
                    break;
                }
                case UpscalingFilterSelection.SGSR2:
                {
                    filter = ImageUpscalingFilter.SGSR2;
                    break;
                }
            }

            return filter;
        }

        private static void UpdateTemporalAAData(CameraData cameraData, UltraAdditionalCameraData additionalCameraData)
        {
            if (UltraRenderer.IsOffscreenDepthTexture(cameraData))
            {
                additionalCameraData.historyManager.GetHistoryForRead<TaaHistory>()?.Reset();
                additionalCameraData.historyManager.GetHistoryForRead<StpHistory>()?.Reset();
                additionalCameraData.historyManager.GetHistoryForRead<Sgsr2History>()?.Reset();
                cameraData.taaHistory = null;
                cameraData.stpHistory = null;
                cameraData.sgsr2History = null;
                return;
            }

            // Always request the TAA history data here in order to fit the existing UltraRP structure.
            additionalCameraData.historyManager.RequestAccess<TaaHistory>();
            cameraData.taaHistory = additionalCameraData.historyManager.GetHistoryForWrite<TaaHistory>();

            if (cameraData.IsSGSR2Requested())
            {
                additionalCameraData.historyManager.RequestAccess<Sgsr2History>();
                cameraData.sgsr2History = additionalCameraData.historyManager.GetHistoryForWrite<Sgsr2History>();
            }
            else
            {
                // Switching filters or disabling post-processing must discard old feedback.
                additionalCameraData.historyManager.GetHistoryForRead<Sgsr2History>()?.Reset();
                cameraData.sgsr2History = null;
            }

            if (cameraData.IsSTPEnabled())
            {
                additionalCameraData.historyManager.RequestAccess<StpHistory>();
                cameraData.stpHistory = additionalCameraData.historyManager.GetHistoryForWrite<StpHistory>();
            }
            else
            {
                // A short disable/enable cycle must not reuse feedback from an older frame.
                additionalCameraData.historyManager.GetHistoryForRead<StpHistory>()?.Reset();
                cameraData.stpHistory = null;
            }

            // Update TAA settings
            ref var taaSettings = ref additionalCameraData.taaSettings;
            cameraData.taaSettings = taaSettings;

            // Decrease history clear counter. Typically clear is only 1 frame, but can be many for XR multipass eyes!
            taaSettings.resetHistoryFrames -= taaSettings.resetHistoryFrames > 0 ? 1 : 0;
        }

        private static void UpdateTemporalAATargets(CameraData cameraData)
        {
            if (cameraData.IsSGSR2Enabled())
            {
                cameraData.taaHistory?.Reset();
                cameraData.stpHistory?.Reset();
                bool allocation = cameraData.sgsr2History.Update(ref cameraData.cameraTargetDescriptor,
                    cameraData.pixelWidth, cameraData.pixelHeight, false);
                if (allocation && cameraData.taaSettings.resetHistoryFrames >= 0)
                    ++cameraData.taaSettings.resetHistoryFrames;
                return;
            }

            cameraData.sgsr2History?.Reset();

            if (cameraData.IsTemporalAAEnabled())
            {
                bool xrMultipassEnabled = false;
#if FRAXINUSRP_ENABLE_XR && ENABLE_VR && ENABLE_XR_MODULE
                xrMultipassEnabled = cameraData.xr.enabled && !cameraData.xr.singlePassEnabled;
#endif
                bool allocation;
                if (cameraData.IsSTPRequested())
                {
                    Debug.Assert(cameraData.stpHistory != null);

                    // When STP is active, we don't require the full set of resources needed by TAA.
                    cameraData.taaHistory.Reset();

                    allocation = cameraData.stpHistory.Update(cameraData);
                }
                else
                    allocation = cameraData.taaHistory.Update(ref cameraData.cameraTargetDescriptor, xrMultipassEnabled);

                // Fill new history with current frame
                // XR Multipass renders a "frame" per eye
                if (allocation)
                    cameraData.taaSettings.resetHistoryFrames += xrMultipassEnabled ? 2 : 1;
            }
            else
            {
                cameraData.taaHistory.Reset();   // TAA GPUResources is explicitly released if the feature is turned off. We could refactor this to rely on the type request and the "gc" only.

                // In the case where STP is requested, but TAA gets disabled for various reasons so STP is disabled, we should release the STP history resources
                if (cameraData.IsSTPRequested())
                    cameraData.stpHistory?.Reset();
            }
        }

        static PerObjectData GetPerObjectLightFlags(int additionalLightsCount, bool isForwardPlus)
        {
            using var profScope = new ProfilingScope(Profiling.Pipeline.getPerObjectLightFlags);

            var configuration = PerObjectData.Lightmaps 
                | PerObjectData.LightProbe 
                | PerObjectData.OcclusionProbe 
                | PerObjectData.ShadowMask;

            if (!isForwardPlus)
                configuration |= PerObjectData.ReflectionProbes | PerObjectData.LightData;

            if (additionalLightsCount > 0 && !isForwardPlus)
            {
                // In this case we also need per-object indices (unity_LightIndices)
                if (!RenderingUtils.useStructuredBuffer)
                    configuration |= PerObjectData.LightIndices;
            }

            return configuration;
        }

        static int GetMainLightIndex(UltraRenderPipelineAsset settings, NativeArray<VisibleLight> visibleLights)
        {
            using var profScope = new ProfilingScope(Profiling.Pipeline.getMainLightIndex);

            int totalVisibleLights = visibleLights.Length;

            if (totalVisibleLights == 0 || settings.mainLightRenderingMode != LightRenderingMode.PerPixel)
                return -1;

            Light sunLight = RenderSettings.sun;
            int brightestDirectionalLightIndex = -1;
            float brightestLightIntensity = 0.0f;
            for (int i = 0; i < totalVisibleLights; ++i)
            {
                ref VisibleLight currVisibleLight = ref visibleLights.UnsafeElementAtMutable(i);
                Light currLight = currVisibleLight.light;

                // Particle system lights have the light property as null. We sort lights so all particles lights
                // come last. Therefore, if first light is particle light then all lights are particle lights.
                // In this case we either have no main light or already found it.
                if (currLight == null)
                    break;

                if (currVisibleLight.lightType == LightType.Directional)
                {
                    // Sun source needs be a directional light
                    if (currLight == sunLight)
                        return i;

                    // In case no sun light is present we will return the brightest directional light
                    if (currLight.intensity > brightestLightIntensity)
                    {
                        brightestLightIntensity = currLight.intensity;
                        brightestDirectionalLightIndex = i;
                    }
                }
            }

            return brightestDirectionalLightIndex;
        }

        static void SetupPerCameraShaderConstants(CommandBuffer cmd)
        {
            using var profScope = new ProfilingScope(Profiling.Pipeline.setupPerCameraShaderConstants);
            // When glossy reflections are OFF in the shader we set a constant color to use as indirect specular
            SphericalHarmonicsL2 ambientSH = RenderSettings.ambientProbe;
            Color linearGlossyEnvColor = new Color(ambientSH[0, 0], ambientSH[1, 0], ambientSH[2, 0]) * RenderSettings.reflectionIntensity;
            Color glossyEnvColor = CoreUtils.ConvertLinearToActiveColorSpace(linearGlossyEnvColor);
            cmd.SetGlobalVector(ShaderPropertyId.glossyEnvironmentColor, glossyEnvColor);

            // Used as fallback cubemap for reflections
            cmd.SetGlobalTexture(ShaderPropertyId.glossyEnvironmentCubeMap, ReflectionProbe.defaultTexture);
            cmd.SetGlobalVector(ShaderPropertyId.glossyEnvironmentCubeMapHDR, ReflectionProbe.defaultTextureHDRDecodeValues);

            // Ambient
            cmd.SetGlobalVector(ShaderPropertyId.ambientSkyColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.ambientSkyColor));
            cmd.SetGlobalVector(ShaderPropertyId.ambientEquatorColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.ambientEquatorColor));
            cmd.SetGlobalVector(ShaderPropertyId.ambientGroundColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.ambientGroundColor));

            // Used when subtractive mode is selected
            cmd.SetGlobalVector(ShaderPropertyId.subtractiveShadowColor, CoreUtils.ConvertSRGBToActiveColorSpace(RenderSettings.subtractiveShadowColor));
        }

        static void CheckAndApplyDebugSettings(ContextContainer frameData)
        {
            var debugDisplaySettings = UltraRenderPipelineDebugDisplaySettings.Instance;
            CameraData cameraData = frameData.Get<CameraData>();

            if (debugDisplaySettings.AreAnySettingsActive && !cameraData.isPreviewCamera)
            {
                DebugDisplaySettingsRendering renderingSettings = debugDisplaySettings.renderingSettings;
                int msaaSamples = cameraData.cameraTargetDescriptor.msaaSamples;

                if (!renderingSettings.enableMsaa)
                    msaaSamples = 1;

                if (!renderingSettings.enableHDR)
                    cameraData.isHdrEnabled = false;

                if (!debugDisplaySettings.IsPostProcessingAllowed)
                    cameraData.postProcessEnabled = false;

                cameraData.hdrColorBufferPrecision = asset ? asset.hdrColorBufferPrecision : HDRColorBufferPrecision._32Bits;
                cameraData.cameraTargetDescriptor.graphicsFormat = MakeRenderTextureGraphicsFormat(cameraData.isHdrEnabled, cameraData.hdrColorBufferPrecision, true);
                cameraData.cameraTargetDescriptor.msaaSamples = msaaSamples;

            }
        }

        static AdditionalLightsShadowAtlasLayout BuildAdditionalLightsShadowAtlasLayout(LightData lightData, ShadowData shadowData,CameraData cameraData)
        {
            using var profScope = new ProfilingScope(Profiling.Pipeline.buildAdditionalLightsShadowAtlasLayout);
            return new AdditionalLightsShadowAtlasLayout(lightData, shadowData, cameraData);
        }
    }
}
