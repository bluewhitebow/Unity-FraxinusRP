using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;
using FraxinusEngine.Base;


#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.ProjectWindowCallback;
using ShaderKeywordFilter = UnityEditor.ShaderKeywordFilter;
#endif

namespace FraxinusEngine.Rendering.Ultra
{
    public enum ShadowQuality
    {
        Disabled,
        HardShadows,
        SoftShadows,
    }

    public enum SoftShadowQuality
    {
        [InspectorName("Use settings from Render Pipeline Asset")]
        UsePipelineSettings,
        Low,
        Medium,
        High,
    }

    public enum ShadowResolution
    {
        _256 = 256,
        _512 = 512,
        _1024 = 1024,
        _2048 = 2048,
        _4096 = 4096,
        _8192 = 8192
    }

    public enum LightCookieResolution
    {
        _256 = 256,
        _512 = 512,
        _1024 = 1024,
        _2048 = 2048,
        _4096 = 4096
    }

    public enum LightCookieFormat
    {
        GrayscaleLow,
        GrayscaleHigh,
        ColorLow,
        ColorHigh,
        ColorHDR,
    }

    public enum HDRColorBufferPrecision
    {
        [Tooltip("Use 32-bits per pixel for HDR rendering.")]
        _32Bits,
        [Tooltip("Use 64-bits per pixel for HDR rendering.")]
        _64Bits,
    }

    public enum MsaaQuality
    {
        Disabled = 1,
        _2x = 2,
        _4x = 4,
        _8x = 8
    }

    public enum Downsampling
    {
        None,
        _2xBilinear,
        _4xBox,
        _4xBilinear
    }

    public enum LightRenderingMode
    {
        Disabled = 0,
        PerVertex = 2,
        PerPixel = 1,
    }

    public enum ColorGradingMode
    {
        LowDynamicRange,
        HighDynamicRange
    }

    public enum StoreActionsOptimization
    {
        Auto,
        Discard,
        Store
    }
    public enum VolumeFrameworkUpdateMode
    {
        [InspectorName("Every Frame")]
        EveryFrame = 0,

        [InspectorName("Via Scripting")]
        ViaScripting = 1,

        [InspectorName("Use Pipeline Settings")]
        UsePipelineSettings = 2,
    }

    public enum UpscalingFilterSelection
    {
        [InspectorName("Automatic"), Tooltip("Unity selects a filtering option automatically based on the Render Scale value and the current screen resolution.")]
        Auto,

        [InspectorName("Bilinear")]
        Linear,

        [InspectorName("Nearest-Neighbor")]
        Point,

        [InspectorName("FidelityFX Super Resolution 1.0"), Tooltip("If the target device does not support Unity shader model 4.5, Unity falls back to the Automatic option.")]
        FSR,

        [InspectorName("Spatial-Temporal Post-Processing"), Tooltip("If the target device does not support compute shaders or is running GLES, Unity falls back to the Automatic option.")]
        STP,

        [InspectorName("Snapdragon Game Super Resolution 1"), Tooltip("Spatial upscaling. Falls back to Bilinear when the shader is not supported.")]
        SGSR1,

        [InspectorName("Snapdragon Game Super Resolution 2"), Tooltip("Temporal upscaling with built-in anti-aliasing. Requires post-processing, motion vectors and a single-sampled camera without stacking or dynamic resolution. Unsupported cameras fall back to Bilinear without temporal jitter.")]
        SGSR2
    }

    public enum LODCrossFadeDitheringType
    {
        BayerMatrix,
        BlueNoise
    }

    public enum LightProbeSystem
    {
        [InspectorName("Light Probe Groups")]
        LegacyLightProbes = 0,
        ProbeVolumes = 1,
        [InspectorName("Adaptive PRT Volumes (APRTV)")]
        PrecomputeRadianceTransfer = 2,
    }

    public enum ShEvalMode
    {
        Auto = 0,
        PerVertex = 1,
        Mixed = 2,
        PerPixel = 3,
    }

    [ExcludeFromPreset]
#if UNITY_EDITOR
    [ShaderKeywordFilter.ApplyRulesIfTagsEqual("RenderPipeline", "UltraPipeline")]
#endif
    public partial class UltraRenderPipelineAsset : RenderPipelineAsset<UltraRenderPipeline>, ISerializationCallbackReceiver, 
        IProbeVolumeEnabledRenderPipeline, IGPUResidentRenderPipeline, 
        IRenderGraphEnabledRenderPipeline, ISTPEnabledRenderPipeline, IVirtualTexturingEnabledRenderPipeline
    {
        internal bool IsAtLastVersion() => true;

        ScriptableRenderer[] _renderers = new ScriptableRenderer[1];

        // Renderer settings
        [SerializeField] internal ScriptableRendererGraph[] rendererGraphList = new ScriptableRendererGraph[1];
        [SerializeField] internal int defaultRendererIndex = 0;

        // General settings
        [SerializeField] bool _supportsTerrainHoles = true;

        // Quality settings
        [SerializeField] bool _supportsHDR = false;
        [SerializeField] HDRColorBufferPrecision _hdrColorBufferPrecision = HDRColorBufferPrecision._32Bits;
        [SerializeField] MsaaQuality _msaa = MsaaQuality.Disabled;
        [SerializeField] float _renderScale = 1.0f;
        [SerializeField] UpscalingFilterSelection _upscalingFilter = UpscalingFilterSelection.Auto;
        [SerializeField] bool _fsrOverrideSharpness = false;
        [SerializeField] float _fsrSharpness = FSRUtils.kDefaultSharpnessLinear;

#if UNITY_EDITOR // multi_compile _ LOD_FADE_CROSSFADE
#endif
        [SerializeField] bool _enableLODCrossFade = true;
        [SerializeField] LODCrossFadeDitheringType _lodCrossFadeDitheringType = LODCrossFadeDitheringType.BlueNoise;

#if UNITY_EDITOR // multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
#endif
        // ShEvalMode.Auto is handled in shader preprocessor.
        [SerializeField] ShEvalMode _shEvalMode = ShEvalMode.Auto;


        // Probe volume settings
        // Native filtering is derived from all build consumers in the prefilter
        // fields. Direct filters here would bypass stripUnused and that union.
        [SerializeField] LightProbeSystem _lightProbeSystem = LightProbeSystem.LegacyLightProbes;
        [SerializeField] ProbeVolumeTextureMemoryBudget _probeVolumeMemoryBudget = ProbeVolumeTextureMemoryBudget.MemoryBudgetMedium;
        [SerializeField] ProbeVolumeBlendingTextureMemoryBudget _probeVolumeBlendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetMedium;
        [SerializeField] bool _supportProbeVolumeGPUStreaming = false;
        [SerializeField] bool _supportProbeVolumeDiskStreaming = false;
        [SerializeField] bool _supportProbeVolumeScenarios = false;
        [SerializeField] bool _supportProbeVolumeScenarioBlending = false;

#if UNITY_EDITOR
#endif
        [SerializeField] ProbeVolumeSHBands _probeVolumeSHBands = ProbeVolumeSHBands.SphericalHarmonicsL1;

        // Main directional light Settings
        [SerializeField] LightRenderingMode _mainLightRenderingMode = LightRenderingMode.PerPixel;
        [SerializeField] bool _mainLightShadowsSupported = true;
        [SerializeField] ShadowResolution _mainLightShadowmapResolution = ShadowResolution._2048;

        // Additional lights settings
        [SerializeField] LightRenderingMode _additionalLightsRenderingMode = LightRenderingMode.PerPixel;
        [SerializeField] int _additionalLightsPerObjectLimit = 4;
        [SerializeField] bool _additionalLightShadowsSupported = false;
        [SerializeField] ShadowResolution _additionalLightsShadowmapResolution = ShadowResolution._512;

        [SerializeField] int _additionalLightsShadowResolutionTierLow = additionalLightsDefaultShadowResolutionTierLow;
        [SerializeField] int _additionalLightsShadowResolutionTierMedium = additionalLightsDefaultShadowResolutionTierMedium;
        [SerializeField] int _additionalLightsShadowResolutionTierHigh = additionalLightsDefaultShadowResolutionTierHigh;

        [SerializeField] bool _reflectionProbeBlending = false;
        [SerializeField] bool _reflectionProbeBoxProjection = false;
        [SerializeField] bool _reflectionProbeAtlas = true;

        // Shadows Settings
        [SerializeField] float _shadowDistance = 50.0f;
        [SerializeField] int _shadowCascadeCount = 1;
        [SerializeField] float _cascade2Split = 0.25f;
        [SerializeField] Vector2 _cascade3Split = new Vector2(0.1f, 0.3f);
        [SerializeField] Vector3 _cascade4Split = new Vector3(0.067f, 0.2f, 0.467f);
        [SerializeField] float _cascadeBorder = 0.2f;
        [SerializeField] float _shadowDepthBias = 1.0f;
        [SerializeField] float _shadowNormalBias = 1.0f;

#if UNITY_EDITOR // multi_compile_fragment _ _SHADOWS_SOFT
#endif
        [SerializeField] bool _softShadowsSupported = false;
        [SerializeField] bool _conservativeEnclosingSphere = false;
        [SerializeField] int _numIterationsEnclosingSphere = 64;
        [SerializeField] SoftShadowQuality _softShadowQuality = SoftShadowQuality.Medium;

        // Light Cookie Settings
        [SerializeField] LightCookieResolution _additionalLightsCookieResolution = LightCookieResolution._2048;
        [SerializeField] LightCookieFormat _additionalLightsCookieFormat = LightCookieFormat.ColorHigh;

        // Advanced settings
        [SerializeField] bool _useSRPBatcher = true;
        [SerializeField] bool _supportsDynamicBatching = false;

#if UNITY_EDITOR
        // multi_compile _ LIGHTMAP_SHADOW_MIXING
        // multi_compile _ SHADOWS_SHADOWMASK
#endif
        [SerializeField] bool _mixedLightingSupported = true;

#if UNITY_EDITOR
        // multi_compile_fragment _ _LIGHT_COOKIES
#endif
        [SerializeField] bool _supportsLightCookies = true;
        [SerializeField] bool _supportsAreaLights = true;

#if UNITY_EDITOR
        // multi_compile_fragment _ _LIGHT_LAYERS
#endif
        [SerializeField] bool _supportsLightLayers = false;
        [SerializeField] StoreActionsOptimization _storeActionsOptimization = StoreActionsOptimization.Auto;

        // Adaptive performance settings
        [SerializeField] bool _useAdaptivePerformance = true;

        // Post-processing settings
        [SerializeField] ColorGradingMode _colorGradingMode = ColorGradingMode.LowDynamicRange;
        [SerializeField] int _colorGradingLutSize = 32;

#if UNITY_EDITOR // multi_compile_fragment _ _ENABLE_ALPHA_OUTPUT
#endif
        [SerializeField] bool _allowPostProcessAlphaOutput = false;

#if UNITY_EDITOR // multi_compile_local_fragment _ _USE_FAST_SRGB_LINEAR_CONVERSION
#endif
        [SerializeField] bool _useFastSRGBLinearConversion = false;
        [SerializeField] bool _supportDataDrivenLensFlare = true;
        [SerializeField] bool _supportScreenSpaceLensFlare = true;

        // virtual Texturing
        [SerializeField]
        internal VirtualTexturingSettingsSRP virtualTexturingSettings = new VirtualTexturingSettingsSRP();

        // GPU Resident Drawer
        [SerializeField] GPUResidentDrawerMode _gpuResidentDrawerMode = GPUResidentDrawerMode.Disabled;
        [SerializeField] float _smallMeshScreenPercentage = 0.0f;
        [SerializeField] bool _gpuResidentDrawerEnableOcclusionCullingInCameras;

        GPUResidentDrawerSettings IGPUResidentRenderPipeline.gpuResidentDrawerSettings => new()
        {
            mode = gpuResidentDrawerMode,
            enableOcclusionCulling = gpuResidentDrawerEnableOcclusionCullingInCameras,
            supportDitheringCrossFade = _enableLODCrossFade,
            allowInEditMode = true,
            smallMeshScreenPercentage = _smallMeshScreenPercentage,
#if UNITY_EDITOR
            pickingShader = Shader.Find("Hidden/Ultra Render Pipeline/BRGPicking"),
#endif
            errorShader = Shader.Find("Hidden/Ultra Render Pipeline/FallbackError"),
            loadingShader = Shader.Find("Hidden/Ultra Render Pipeline/FallbackLoading"),
        };

        [SerializeField] VolumeFrameworkUpdateMode _volumeFrameworkUpdateMode = VolumeFrameworkUpdateMode.EveryFrame;
        [SerializeField] VolumeProfile _volumeProfile;

        // 16^3大小的lut在hdr模式下几乎没用过。32是lut在log中被编码的最小量。越低尺寸在额外的1D shaper中表现更佳
        // 但目前姑且先用16
        public const int minLutSize = 16;
        public const int maxLutSize = 65;

        internal const int shadowCascadeMinCount = 1;
        internal const int shadowCascadeMaxCount = 4;

        public static readonly int additionalLightsDefaultShadowResolutionTierLow = 256;
        public static readonly int additionalLightsDefaultShadowResolutionTierMedium = 512;
        public static readonly int additionalLightsDefaultShadowResolutionTierHigh = 1024;

        public ReadOnlySpan<ScriptableRenderer> renderers => _renderers;

        public bool isImmediateModeSupported => false;

#if UNITY_EDITOR
        public static readonly string packagePath = "Packages/com.bluewhitebow.fraxinus-rp";

#endif

        protected override void EnsureGlobalSettings()
        {
            base.EnsureGlobalSettings();

#if UNITY_EDITOR
            UltraRenderPipelineGlobalSettings.Ensure();
#endif
        }

        protected override RenderPipeline CreatePipeline()
        {
            EnsureGlobalSettings();
            if (rendererGraphList == null)
                rendererGraphList = new ScriptableRendererGraph[1];

            if (defaultRendererIndex < 0 || defaultRendererIndex >= rendererGraphList.Length || rendererGraphList[defaultRendererIndex] == null)
            {
                Debug.LogError(
                    $"Default Renderer is missing, make sure there is a Renderer assigned as the default on the current Ultra RP asset:{name}",
                    this);

                return null;
            }

            DestroyRenderers();
            var pipeline = new UltraRenderPipeline(this);
            CreateRenderers();

            IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();

            return pipeline;
        }


        protected override void OnValidate()
        {
            DestroyRenderers();

            base.OnValidate();
        }

        protected override void OnDisable()
        {
            DestroyRenderers();

            base.OnDisable();
        }

        internal void DestroyRenderers()
        {
            if (_renderers == null)
                return;

            for (int i = 0; i < _renderers.Length; i++)
                DestroyRenderer(ref _renderers[i]);
        }

        void DestroyRenderer(ref ScriptableRenderer renderer)
        {
            if (renderer != null)
            {
                renderer.Dispose();
                renderer = null;
            }
        }

        void CreateRenderers()
        {
            if (_renderers != null)
            {
                for (int i = 0; i < _renderers.Length; ++i)
                {
                    if (_renderers[i] != null)
                        Debug.LogError($"Creating renderers but previous instance wasn't properly destroyed: _renderers[{i}]");
                }
            }

            if (_renderers == null || _renderers.Length != rendererGraphList.Length)
                _renderers = new ScriptableRenderer[rendererGraphList.Length];

            for (int i = 0; i < rendererGraphList.Length; ++i)
            {
                if (rendererGraphList[i] != null)
                    _renderers[i] = rendererGraphList[i].InternalCreateRenderer();
            }
        }

        public ScriptableRenderer scriptableRenderer
        {
            get
            {
                if (rendererGraphList?.Length > defaultRendererIndex && rendererGraphList[defaultRendererIndex] == null)
                {
                    Debug.LogError("Default renderer is missing from the current Pipeline Asset.", this);
                    return null;
                }

                if (scriptableRendererGraph.isInvalidated || _renderers[defaultRendererIndex] == null)
                {
                    DestroyRenderer(ref _renderers[defaultRendererIndex]);
                    _renderers[defaultRendererIndex] = scriptableRendererGraph.InternalCreateRenderer();

                    if (gpuResidentDrawerMode != GPUResidentDrawerMode.Disabled)
                        IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();
                }

                return _renderers[defaultRendererIndex];
            }
        }

        public ScriptableRenderer GetRenderer(int index)
        {
            if (index == -1)
                index = defaultRendererIndex;

            if (index >= rendererGraphList.Length || index < 0 || rendererGraphList[index] == null)
            {
                Debug.LogWarning($"Renderer at index {index.ToString()} is missing, falling back to Default Renderer {rendererGraphList[defaultRendererIndex].name}", this as UnityEngine.Object);
                index = defaultRendererIndex;
            }

            if (_renderers == null || _renderers.Length < rendererGraphList.Length)
            {
                DestroyRenderers();
                CreateRenderers();
            }

            if (rendererGraphList[index].isInvalidated || _renderers[index] == null)
            {
                DestroyRenderer(ref _renderers[index]);
                _renderers[index] = rendererGraphList[index].InternalCreateRenderer();

                if (gpuResidentDrawerMode != GPUResidentDrawerMode.Disabled)
                    IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();
            }

            return _renderers[index];
        }

        internal ScriptableRendererGraph scriptableRendererGraph
        {
            get
            {
                if (rendererGraphList[defaultRendererIndex] == null)
                    CreatePipeline();

                return rendererGraphList[defaultRendererIndex];
            }
        }

#if UNITY_EDITOR
        internal GUIContent[] rendererDisplayList
        {
            get
            {
                GUIContent[] list = new GUIContent[rendererGraphList.Length + 1];
                list[0] = new GUIContent($"Default Renderer ({RendererDataDisplayName(rendererGraphList[defaultRendererIndex])})");

                for (var i = 1; i < list.Length; i++)
                    list[i] = new GUIContent($"{(i - 1).ToString()}: {RendererDataDisplayName(rendererGraphList[i - 1])}");

                return list;
            }
        }

        string RendererDataDisplayName(ScriptableRendererGraph data)
        {
            if (data != null)
                return data.name;

            return "NULL (Missing RendererData)";
        }
#endif


        private static GraphicsFormat[][] s_lightCookieFormatList = new GraphicsFormat[][]
        {
            /* Grayscale Low */ new GraphicsFormat[] {GraphicsFormat.R8_UNorm},
            /* Grayscale High*/ new GraphicsFormat[] {GraphicsFormat.R16_UNorm},
            /* Color Low     */ new GraphicsFormat[] {GraphicsFormat.R5G6B5_UNormPack16, GraphicsFormat.B5G6R5_UNormPack16, GraphicsFormat.R5G5B5A1_UNormPack16, GraphicsFormat.B5G5R5A1_UNormPack16},
            /* Color High    */ new GraphicsFormat[] {GraphicsFormat.A2B10G10R10_UNormPack32, GraphicsFormat.R8G8B8A8_SRGB, GraphicsFormat.B8G8R8A8_SRGB},
            /* Color HDR     */ new GraphicsFormat[] {GraphicsFormat.B10G11R11_UFloatPack32},
        };

        internal GraphicsFormat additionalLightsCookieFormat
        {
            get
            {
                GraphicsFormat result = GraphicsFormat.None;
                foreach (var format in s_lightCookieFormatList[(int)_additionalLightsCookieFormat])
                {
                    if (GraphicsFormatCompatibility.IsFormatSupported(format, GraphicsFormatUsage.Render))
                    {
                        result = format;
                        break;
                    }
                }

                if (QualitySettings.activeColorSpace == ColorSpace.Gamma)
                    result = GraphicsFormatUtility.GetLinearFormat(result);

                // Fallback
                if (result == GraphicsFormat.None)
                {
                    result = GraphicsFormat.R8G8B8A8_UNorm;
                    Debug.LogWarning($"Additional Lights Cookie Format ({_additionalLightsCookieFormat.ToString()}) is not supported by the platform. Falling back to {GraphicsFormatUtility.GetBlockSize(result) * 8}-bit format ({GraphicsFormatUtility.GetFormatString(result)})");
                }

                return result;
            }
        }

        internal Vector2Int additionalLightsCookieResolution => new Vector2Int((int)_additionalLightsCookieResolution, (int)_additionalLightsCookieResolution);

        public int[] rendererIndexList
        {
            get
            {
                int[] list = new int[rendererGraphList.Length + 1];
                for (int i = 0; i < list.Length; i++)
                    list[i] = i - 1;

                return list;
            }
        }


        public bool supportsTerrainHoles
        {
            get { return _supportsTerrainHoles; }
        }

        public StoreActionsOptimization storeActionsOptimization
        {
            get => _storeActionsOptimization;
            set => _storeActionsOptimization = value;
        }

        public bool supportsHDR
        {
            get { return _supportsHDR; }
            set { _supportsHDR = value; }
        }

        public HDRColorBufferPrecision hdrColorBufferPrecision
        {
            get => _hdrColorBufferPrecision;
            set => _hdrColorBufferPrecision = value;
        }

        public int msaaSampleCount
        {
            get { return (int)_msaa; }
            set { _msaa = (MsaaQuality)value; }
        }

        public float renderScale
        {
            get { return _renderScale; }
            set { _renderScale = ValidateRenderScale(value); }
        }

        public bool enableLODCrossFade => _enableLODCrossFade;

        public LODCrossFadeDitheringType lodCrossFadeDitheringType => _lodCrossFadeDitheringType;

        public UpscalingFilterSelection upscalingFilter
        {
            get => _upscalingFilter;
            set => _upscalingFilter = value;
        }

        public bool fsrOverrideSharpness
        {
            get => _fsrOverrideSharpness;
            set => _fsrOverrideSharpness = value;
        }

        public float fsrSharpness
        {
            get => _fsrSharpness;
            set => _fsrSharpness = value;
        }

        public ShEvalMode shEvalMode
        {
            get => _shEvalMode;
            internal set => _shEvalMode = value;
        }

        public LightProbeSystem lightProbeSystem
        {
            get => _lightProbeSystem;
            internal set => _lightProbeSystem = value;
        }

        public ProbeVolumeTextureMemoryBudget probeVolumeMemoryBudget
        {
            get => _probeVolumeMemoryBudget;
            internal set => _probeVolumeMemoryBudget = value;
        }

        public ProbeVolumeBlendingTextureMemoryBudget probeVolumeBlendingMemoryBudget
        {
            get => _probeVolumeBlendingMemoryBudget;
            internal set => _probeVolumeBlendingMemoryBudget = value;
        }


        public bool supportProbeVolumeGPUStreaming
        {
            get => _supportProbeVolumeGPUStreaming;
            internal set => _supportProbeVolumeGPUStreaming = value;
        }


        public bool supportProbeVolumeDiskStreaming
        {
            get => _supportProbeVolumeDiskStreaming;
            internal set => _supportProbeVolumeDiskStreaming = value;
        }


        public bool supportProbeVolumeScenarios
        {
            get { return _supportProbeVolumeScenarios; }
            internal set { _supportProbeVolumeScenarios = value; }
        }

        public bool supportProbeVolumeScenarioBlending
        {
            get { return _supportProbeVolumeScenarioBlending; }
            internal set { _supportProbeVolumeScenarioBlending = value; }
        }

        public ProbeVolumeSHBands probeVolumeSHBands
        {
            get => _probeVolumeSHBands;
            internal set => _probeVolumeSHBands = value;
        }

        public LightRenderingMode mainLightRenderingMode
        {
            get => _mainLightRenderingMode;
            internal set => _mainLightRenderingMode = value;
        }

        public bool supportsMainLightShadows
        {
            get => _mainLightShadowsSupported;
            internal set
            {
                _mainLightShadowsSupported = value;
            }
        }

        public int mainLightShadowmapResolution
        {
            get => (int)_mainLightShadowmapResolution;
            set => _mainLightShadowmapResolution = (ShadowResolution)value;
        }

        public LightRenderingMode additionalLightsRenderingMode
        {
            get => _additionalLightsRenderingMode;
            internal set => _additionalLightsRenderingMode = value;
        }

        public int maxAdditionalLightsCount
        {
            get { return _additionalLightsPerObjectLimit; }
            set { _additionalLightsPerObjectLimit = ValidatePerObjectLights(value); }
        }

        public bool supportsAdditionalLightShadows
        {
            get => _additionalLightShadowsSupported;
            internal set
            {
                _additionalLightShadowsSupported = value;
            }
        }

        public int additionalLightsShadowmapResolution
        {
            get => (int)_additionalLightsShadowmapResolution;
            set => _additionalLightsShadowmapResolution = (ShadowResolution)value;
        }

        public int additionalLightsShadowResolutionTierLow
        {
            get => _additionalLightsShadowResolutionTierLow;
            internal set => _additionalLightsShadowResolutionTierLow = value;
        }

        public int additionalLightsShadowResolutionTierMedium
        {
            get => _additionalLightsShadowResolutionTierMedium;
            internal set => _additionalLightsShadowResolutionTierMedium = value;
        }

        public int additionalLightsShadowResolutionTierHigh
        {
            get => _additionalLightsShadowResolutionTierHigh;
            internal set => _additionalLightsShadowResolutionTierHigh = value;
        }

        internal int GetAdditionalLightsShadowResolution(int additionalLightsShadowResolutionTier)
        {
            if (additionalLightsShadowResolutionTier <= UltraAdditionalLightData.additionalLightsShadowResolutionTierLow /* 0 */)
                return additionalLightsShadowResolutionTierLow;

            if (additionalLightsShadowResolutionTier == UltraAdditionalLightData.additionalLightsShadowResolutionTierMedium /* 1 */)
                return additionalLightsShadowResolutionTierMedium;

            if (additionalLightsShadowResolutionTier >= UltraAdditionalLightData.additionalLightsShadowResolutionTierHigh /* 2 */)
                return additionalLightsShadowResolutionTierHigh;

            return additionalLightsShadowResolutionTierMedium;
        }

        public bool reflectionProbeBlending
        {
            get => _reflectionProbeBlending;
            internal set => _reflectionProbeBlending = value;
        }

        public bool reflectionProbeBoxProjection
        {
            get => _reflectionProbeBoxProjection;
            internal set => _reflectionProbeBoxProjection = value;
        }

        public bool reflectionProbeAtlas
        {
            get => _reflectionProbeAtlas;
            internal set => _reflectionProbeAtlas = value;
        }

        public float shadowDistance
        {
            get => _shadowDistance;
            set => _shadowDistance = Mathf.Max(0.0f, value);
        }

        public int shadowCascadeCount
        {
            get => _shadowCascadeCount;
            set
            {
                if (value < shadowCascadeMinCount || value > shadowCascadeMaxCount)
                    throw new ArgumentException($"Value ({value}) needs to be between {shadowCascadeMinCount} and {shadowCascadeMaxCount}.");

                _shadowCascadeCount = value;
            }
        }

        public float cascade2Split
        {
            get => _cascade2Split;
            set => _cascade2Split = value;
        }

        public Vector2 cascade3Split
        {
            get => _cascade3Split;
            set => _cascade3Split = value;
        }

        public Vector3 cascade4Split
        {
            get => _cascade4Split;
            set => _cascade4Split = value;
        }

        public float cascadeBorder
        {
            get => _cascadeBorder;
            set => _cascadeBorder = value;
        }

        public float shadowDepthBias
        {
            get => _shadowDepthBias;
            set => _shadowDepthBias = ValidateShadowBias(value);
        }

        public float shadowNormalBias
        {
            get => _shadowNormalBias;
            set => _shadowNormalBias = ValidateShadowBias(value);
        }

        public bool supportsSoftShadows
        {
            get => _softShadowsSupported;
            // Recovered runtime quality options switch this alongside shadow maps.
            set => _softShadowsSupported = value;
        }

        internal SoftShadowQuality softShadowQuality
        {
            get => _softShadowQuality;
            set => _softShadowQuality = value;
        }

        public bool supportsDynamicBatching
        {
            get { return _supportsDynamicBatching; }
            set { _supportsDynamicBatching = value; }
        }

        public bool useSRPBatcher
        {
            get { return _useSRPBatcher; }
            set { _useSRPBatcher = value; }
        }

        public bool supportsMixedLighting
        {
            get { return _mixedLightingSupported; }
        }

        public bool supportsLightCookies => _supportsLightCookies;

        /// <summary>Build capability for realtime rectangle lights. Keep this fixed in a Player.</summary>
        public bool supportsAreaLights => _supportsAreaLights;

        public bool useRenderingLayers => _supportsLightLayers;

        public VolumeFrameworkUpdateMode volumeFrameworkUpdateMode => _volumeFrameworkUpdateMode;

        public VolumeProfile volumeProfile
        {
            get => _volumeProfile;
            set => _volumeProfile = value;
        }

        public ColorGradingMode colorGradingMode
        {
            get { return _colorGradingMode; }
            set { _colorGradingMode = value; }
        }

        public int colorGradingLutSize
        {
            get { return _colorGradingLutSize; }
            set { _colorGradingLutSize = Mathf.Clamp(value, minLutSize, maxLutSize); }
        }

        public bool allowPostProcessAlphaOutput => _allowPostProcessAlphaOutput;
        public bool useFastSRGBLinearConversion => _useFastSRGBLinearConversion;
        public bool supportScreenSpaceLensFlare => _supportScreenSpaceLensFlare;
        public bool supportDataDrivenLensFlare => _supportDataDrivenLensFlare;

        public bool useAdaptivePerformance
        {
            get => _useAdaptivePerformance;
            set => _useAdaptivePerformance = value;
        }

        public bool conservativeEnclosingSphere
        {
            get => _conservativeEnclosingSphere;
            set => _conservativeEnclosingSphere = value;
        }

        public int numIterationsEnclosingSphere
        {
            get => _numIterationsEnclosingSphere;
            set => _numIterationsEnclosingSphere = value;
        }

        public override string renderPipelineShaderTag => UltraRenderPipeline.shaderTagName;

        public GPUResidentDrawerMode gpuResidentDrawerMode
        {
#if UNITY_6000_0_OR_NEWER
            get => _gpuResidentDrawerMode;
#else
            get => GPUResidentDrawerMode.Disabled;
#endif
            set
            {
                if (value == _gpuResidentDrawerMode)
                    return;

                _gpuResidentDrawerMode = value;
                OnValidate();
            }
        }

        public bool gpuResidentDrawerEnableOcclusionCullingInCameras
        {
#if UNITY_6000_0_OR_NEWER
            get => _gpuResidentDrawerEnableOcclusionCullingInCameras;
#else
            get => false;
#endif
            set
            {
                if (value == _gpuResidentDrawerEnableOcclusionCullingInCameras)
                    return;

                _gpuResidentDrawerEnableOcclusionCullingInCameras = value;
                OnValidate();
            }
        }

        static class Strings
        {
            public static readonly string notRenderer = $"{nameof(GPUResidentDrawer)} Disabled due to some configured Ultra RenderGraph not being {nameof(UltraRendererGraph)}.";
        }

        public bool IsGPUResidentDrawerSupportedBySRP(out string message, out LogType severty)
        {
#if !UNITY_6000_0_OR_NEWER
            message = "GPU Resident Drawer requires Unity 6. UltraRP uses the standard renderer-list path on Unity 2022.";
            severty = LogType.Warning;
            return false;
#else
            message = string.Empty;
            severty = LogType.Warning;

            // if any of the renderers are not set to Forward+ return false
            foreach (var rendererGraph in rendererGraphList)
            {
                if (rendererGraph is not UltraRendererGraph ultraRendererGraph)
                {
                    message = Strings.notRenderer;
                    return false;
                }
            }

            return true;
#endif
        }

        /// <summary>
        /// Default minimum screen percentage (0-20%) gpu-driven Renderers can cover before getting culled.
        /// </summary>
        public float smallMeshScreenPercentage
        {
            get => _smallMeshScreenPercentage;
            set
            {
                if (Math.Abs(value - _smallMeshScreenPercentage) < float.Epsilon)
                    return;

                _smallMeshScreenPercentage = Mathf.Clamp(value, 0.0f, 20.0f);
                OnValidate();
            }
        }

        public void OnBeforeSerialize()
        {

        }

        public void OnAfterDeserialize()
        {

        }

        float ValidateShadowBias(float value)
        {
            return Mathf.Max(0.0f, Mathf.Min(value, UltraRenderPipeline.maxShadowBias));
        }

        int ValidatePerObjectLights(int value)
        {
            return Math.Max(0, Math.Min(value, UltraRenderPipeline.maxPerObjectLights));
        }

        float ValidateRenderScale(float value)
        {
            return Mathf.Max(UltraRenderPipeline.minRenderScale, Mathf.Min(value, UltraRenderPipeline.maxRenderScale));
        }

        public bool ValidateRendererGraphList(bool partial = false)
        {
            var emptyEntries = 0;
            for (int i = 0; i < rendererGraphList.Length; i++)
                emptyEntries += ValidateRendererGraph(i) ? 0 : 1;

            if (partial)
                return emptyEntries == 0;

            return emptyEntries != rendererGraphList.Length;
        }

        public bool ValidateRendererGraph(int index)
        {
            // Check to see if you are asking for the default renderer
            if (index == -1)
                index = defaultRendererIndex;

            return index < rendererGraphList.Length ? rendererGraphList[index] != null : false;
        }

        public bool supportAPRTV
        {
            get => lightProbeSystem == LightProbeSystem.PrecomputeRadianceTransfer;
        }

        public bool supportProbeVolume
        {
            get => lightProbeSystem == LightProbeSystem.ProbeVolumes;
        }

        internal string probeVolumeBackendId => supportProbeVolume ? APVProbeReferenceVolumeBackend.backendId :
            supportAPRTV ? APRTProbeReferenceVolumeBackend.backendId : null;

        internal ProbeVolumeSystemParameters GetProbeVolumeSystemParameters() => new ProbeVolumeSystemParameters
        {
            enableAPV = supportProbeVolume,
            enableAPRT = supportAPRTV,
            memoryBudget = probeVolumeMemoryBudget,
            blendingMemoryBudget = probeVolumeBlendingMemoryBudget,
            shBands = probeVolumeSHBands,
            supportGPUStreaming = supportProbeVolume && supportProbeVolumeGPUStreaming,
            supportDiskStreaming = supportProbeVolume && supportProbeVolumeDiskStreaming,
            supportScenarios = supportProbeVolumeScenarios,
            supportScenarioBlending = supportProbeVolumeScenarioBlending,
        };

        public ProbeVolumeSHBands maxSHBands
        {
            get
            {
                if (lightProbeSystem == LightProbeSystem.ProbeVolumes)
                    return probeVolumeSHBands;
                else
                    return ProbeVolumeSHBands.SphericalHarmonicsL1;
            }
        }

        public bool isStpUsed
        {
            get { return _upscalingFilter == UpscalingFilterSelection.STP; }
        }

        public bool virtualTexturingEnabled { get { return true; } }
    }
}
