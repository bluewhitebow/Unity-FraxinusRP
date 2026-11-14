using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static FraxinusEngine.Rendering.Ultra.RenderingUtils;

namespace FraxinusEngine.Rendering.Ultra
{
    static class DeferredConfig
    {
        internal static bool isOpenGL { get; set; }

        // DX10 uses SM 4.0. However UltraRP shaders requires SM 4.5 or will use fallback to SM 2.0 shaders otherwise.
        // We will consider deferred renderer is not available when SM 2.0 shaders run.
        internal static bool isDX10 { get; set; }
    }

    internal enum LightFlag
    {
        // Keep in sync with kLightFlagSubtractiveMixedLighting.
        SubtractiveMixedLighting = 4
    }

    public partial class DeferredLights : LightCoreBase
    {
        internal static class ShaderConstants
        {
            public static readonly int _LitStencilRef = Shader.PropertyToID("_LitStencilRef");
            public static readonly int _LitStencilReadMask = Shader.PropertyToID("_LitStencilReadMask");
            public static readonly int _LitStencilWriteMask = Shader.PropertyToID("_LitStencilWriteMask");
            public static readonly int _SimpleLitStencilRef = Shader.PropertyToID("_SimpleLitStencilRef");
            public static readonly int _SimpleLitStencilReadMask = Shader.PropertyToID("_SimpleLitStencilReadMask");
            public static readonly int _SimpleLitStencilWriteMask = Shader.PropertyToID("_SimpleLitStencilWriteMask");
            public static readonly int _StencilRef = Shader.PropertyToID("_StencilRef");
            public static readonly int _StencilReadMask = Shader.PropertyToID("_StencilReadMask");
            public static readonly int _StencilWriteMask = Shader.PropertyToID("_StencilWriteMask");
            public static readonly int _LitPunctualStencilRef = Shader.PropertyToID("_LitPunctualStencilRef");
            public static readonly int _LitPunctualStencilReadMask = Shader.PropertyToID("_LitPunctualStencilReadMask");
            public static readonly int _LitPunctualStencilWriteMask = Shader.PropertyToID("_LitPunctualStencilWriteMask");
            public static readonly int _SimpleLitPunctualStencilRef = Shader.PropertyToID("_SimpleLitPunctualStencilRef");
            public static readonly int _SimpleLitPunctualStencilReadMask = Shader.PropertyToID("_SimpleLitPunctualStencilReadMask");
            public static readonly int _SimpleLitPunctualStencilWriteMask = Shader.PropertyToID("_SimpleLitPunctualStencilWriteMask");
            public static readonly int _LitDirStencilRef = Shader.PropertyToID("_LitDirStencilRef");
            public static readonly int _LitDirStencilReadMask = Shader.PropertyToID("_LitDirStencilReadMask");
            public static readonly int _LitDirStencilWriteMask = Shader.PropertyToID("_LitDirStencilWriteMask");
            public static readonly int _SimpleLitDirStencilRef = Shader.PropertyToID("_SimpleLitDirStencilRef");
            public static readonly int _SimpleLitDirStencilReadMask = Shader.PropertyToID("_SimpleLitDirStencilReadMask");
            public static readonly int _SimpleLitDirStencilWriteMask = Shader.PropertyToID("_SimpleLitDirStencilWriteMask");
            public static readonly int _ClearStencilRef = Shader.PropertyToID("_ClearStencilRef");
            public static readonly int _ClearStencilReadMask = Shader.PropertyToID("_ClearStencilReadMask");
            public static readonly int _ClearStencilWriteMask = Shader.PropertyToID("_ClearStencilWriteMask");

            public static readonly int _ScreenToWorld = Shader.PropertyToID("_ScreenToWorld");

            public static int _MainLightPosition = Shader.PropertyToID("_MainLightPosition");   // ForwardLights.LightConstantBuffer also refers to the same ShaderPropertyID - TODO: move this definition to a common location shared by other UltraRP classes
            public static int _MainLightColor = Shader.PropertyToID("_MainLightColor");         // ForwardLights.LightConstantBuffer also refers to the same ShaderPropertyID - TODO: move this definition to a common location shared by other UltraRP classes
            public static int _MainLightLayerMask = Shader.PropertyToID("_MainLightLayerMask"); // ForwardLights.LightConstantBuffer also refers to the same ShaderPropertyID - TODO: move this definition to a common location shared by other UltraRP classes
            public static int _SpotLightScale = Shader.PropertyToID("_SpotLightScale");
            public static int _SpotLightBias = Shader.PropertyToID("_SpotLightBias");
            public static int _SpotLightGuard = Shader.PropertyToID("_SpotLightGuard");
            public static int _LightPosWS = Shader.PropertyToID("_LightPosWS");
            public static int _LightColor = Shader.PropertyToID("_LightColor");
            public static int _LightAttenuation = Shader.PropertyToID("_LightAttenuation");
            public static int _LightOcclusionProbInfo = Shader.PropertyToID("_LightOcclusionProbInfo");
            public static int _LightDirection = Shader.PropertyToID("_LightDirection");
            public static int _LightFlags = Shader.PropertyToID("_LightFlags");
            public static int _ShadowLightIndex = Shader.PropertyToID("_ShadowLightIndex");
            public static int _LightLayerMask = Shader.PropertyToID("_LightLayerMask");
            public static int _CookieLightIndex = Shader.PropertyToID("_CookieLightIndex");
        }

        internal static readonly string[] gbufferNames = new string[]
        {
            "_GBuffer0",
            "_GBuffer1",
            "_GBuffer2",
            "_GBuffer3",
            "_GBuffer4",
            "_GBuffer5",
            "_GBuffer6"
        };

        internal static readonly int[] gbufferShaderPropertyIDs = new int[]
        {
            Shader.PropertyToID(gbufferNames[0]),
            Shader.PropertyToID(gbufferNames[1]),
            Shader.PropertyToID(gbufferNames[2]),
            Shader.PropertyToID(gbufferNames[3]),
            Shader.PropertyToID(gbufferNames[4]),
            Shader.PropertyToID(gbufferNames[5]),
            Shader.PropertyToID(gbufferNames[6]),
        };

        static readonly string[] s_stencilDeferredPassNames = new string[]
        {
            "Stencil Volume",
            "Deferred Punctual Light (Lit)",
            "Deferred Punctual Light (SimpleLit)",
            "Deferred Directional Light (Lit)",
            "Deferred Directional Light (SimpleLit)",
            "ClearStencilPartial",
            "Fog",
            "SSAOOnly"
        };

        internal enum StencilDeferredPasses
        {
            StencilVolume,
            PunctualLit,
            PunctualSimpleLit,
            DirectionalLit,
            DirectionalSimpleLit,
            ClearStencilPartial,
            Fog,
            SSAOOnly
        };

        static readonly ushort s_invalidLightOffset = 0xFFFF;
        static readonly string s_setupLights = "SetupLights";
        static readonly string s_deferredPass = "Deferred Pass";
        static readonly string s_deferredStencilPass = "Deferred Shading (Stencil)";
        static readonly string s_deferredFogPass = "Deferred Fog";
        static readonly string s_clearStencilPartial = "Clear Stencil Partial";
        static readonly string s_setupLightConstants = "Setup Light Constants";
        static readonly float s_stencilShapeGuard = 1.06067f; // stencil geometric shapes must be inflated to fit the analytic shapes.
        private static readonly ProfilingSampler s_profilingSetupLights = new ProfilingSampler(s_setupLights);
        private static readonly ProfilingSampler s_profilingDeferredPass = new ProfilingSampler(s_deferredPass);
        private static readonly ProfilingSampler s_profilingSetupLightConstants = new ProfilingSampler(s_setupLightConstants);

        internal int gBufferAlbedoIndex { get { return 0; } }
        internal int gBufferSpecularMetallicIndex { get { return 1; } }
        internal int gBufferNormalSmoothnessIndex { get { return 2; } }
        internal int gBufferLightingIndex { get { return 3; } }
        internal int gbufferDepthIndex { get { return useFramebufferFetch ? gBufferLightingIndex + 1 : -1; } }
        internal int gBufferRenderingLayers { get { return useRenderingLayers ? gBufferLightingIndex + (useFramebufferFetch ? 1 : 0) + 1 : -1; } }
        internal int gBufferShadowMask { get { return useShadowMask ? gBufferLightingIndex + (useFramebufferFetch ? 1 : 0) + (useRenderingLayers ? 1 : 0) + 1 : -1; } }
        internal int gBufferSliceCount { get { return 4 + (useFramebufferFetch ? 1 : 0) + (useShadowMask ? 1 : 0) + (useRenderingLayers ? 1 : 0); } }
        internal int gBufferInputAttachmentCount { get { return 4 + (useShadowMask ? 1 : 0); } }


        internal GraphicsFormat GetGBufferFormat(int index)
        {
            if (index == gBufferAlbedoIndex) // sRGB albedo, materialFlags
                return QualitySettings.activeColorSpace == ColorSpace.Linear ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
            else if (index == gBufferSpecularMetallicIndex) // sRGB specular, [unused]
                return GraphicsFormat.R8G8B8A8_UNorm;
            else if (index == gBufferNormalSmoothnessIndex)
                return accurateGbufferNormals ? GraphicsFormat.R8G8B8A8_UNorm : DepthNormalOnlyPass.GetGraphicsFormat(); // normal normal normal packedSmoothness
            else if (index == gBufferLightingIndex) // Emissive+baked: Most likely B10G11R11_UFloatPack32 or R16G16B16A16_SFloat
                return GraphicsFormat.None;
            else if (index == gbufferDepthIndex) // Render-pass on mobiles: reading back real depth-buffer is either inefficient (Arm Vulkan) or impossible (Metal).
                return GraphicsFormat.R32_SFloat;
            else if (index == gBufferShadowMask) // Optional: shadow mask is outputted in mixed lighting subtractive mode for non-static meshes only
                return GraphicsFormat.B8G8R8A8_UNorm;
            else if (index == gBufferRenderingLayers) // Optional: rendering layers is outputted when light layers are enabled (subset of rendering layers)
                return RenderingLayerUtils.GetFormat(renderingLayerMaskSize);
            else
                return GraphicsFormat.None;
        }


        internal bool useShadowMask { get { return this.mixedLightingSetup != MixedLightingSetup.None; } }
        internal bool useRenderingLayers { get { return useLightLayers || useDecalLayers; } }
        internal RenderingLayerUtils.MaskSize renderingLayerMaskSize { get; set; }
        private bool _useFramebufferFetch;
        internal bool useFramebufferFetch
        {
            get => _useFramebufferFetch;
            set
            {
#if UNITY_2022_1_OR_NEWER && !UNITY_2023_1_OR_NEWER
                // Unity 2022/D3D11 loses deferred lighting when the native pass
                // consumes transient GBuffer input attachments. Sampling graph-
                // declared textures preserves their storage and keeps Native
                // RenderGraph enabled. Gate both setup callers here so attachment
                // layout, shader keywords and depth-copy scheduling agree.
                _useFramebufferFetch = value && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11;
#else
                _useFramebufferFetch = value;
#endif
            }
        }
        internal bool hasDepthPrepass { get; set; }
        internal bool hasNormalPrepass { get; set; }
        internal bool hasRenderingLayerPrepass { get; set; }
        internal bool isOverlay { get; set; }
        internal bool useDecalLayers { get; set; }
        internal bool useLightLayers { get { return UltraRenderPipeline.asset.useRenderingLayers; } }
        internal bool useJobSystem { get; set; }

        internal bool accurateGbufferNormals { get; set; }

        internal int renderWidth { get; set; }
        internal int renderHeight { get; set; }

        // Output lighting result.
        internal TextureHandle[] gbufferTextureHandles { get; set; }
        internal GraphicsFormat[] gbufferFormats { get; set; }

        // Visible lights indices rendered using stencil volumes.
        NativeArray<ushort> _stencilVisLights;
        // Offset of each type of lights in m_stencilVisLights.
        NativeArray<ushort> _stencilVisLightOffsets;

        internal MixedLightingSetup mixedLightingSetup { get; set; }

        internal bool cluster;

        // For rendering stencil point lights.
        Mesh _sphereMesh;
        // For rendering stencil spot lights.
        Mesh _hemisphereMesh;
        // For rendering directional lights.
        Mesh _fullscreenMesh;

        // Hold all shaders for stencil-volume deferred shading.
        Material _stencilDeferredMaterial;
        readonly Shader _stencilDeferredShader;

        // Pass indices.
        int[] _stencilDeferredPasses;

        // Avoid memory allocations.
        Matrix4x4[] _screenToWorld = new Matrix4x4[2];

        ProfilingSampler _profilingSamplerDeferredStencilPass = new ProfilingSampler(s_deferredStencilPass);
        ProfilingSampler _profilingSamplerDeferredFogPass = new ProfilingSampler(s_deferredFogPass);
        ProfilingSampler _profilingSamplerClearStencilPartialPass = new ProfilingSampler(s_clearStencilPartial);

        private LightCookieManager _lightCookieManager;

        public delegate int GetShadowLightIndexDelegate(int visibleLightIndex);
        private GetShadowLightIndexDelegate _getShadowLightIndexCallback;

        // cluster lighting
        private GPULights _gpuLights;

        // Constants
        private const int c_deferredLightingTileSize = 16;

        internal const int GBufferAlbedoIndex = 0;
        internal const int GBufferSpecularMetallicIndex = 1;
        internal const int GBufferNormalSmoothnessIndex = 2;
        internal const int GBufferLightingIndex = 3;

        private ComputeShader _deferredLightingCS;
        private int _deferredClassifyTilesKernel = -1;
        private readonly int[] _deferredLightingKernels = { -1, -1, -1 };

        internal struct InitParams
        {
            public LightCookieManager lightCookieManager;
            public UltraRenderPipelineRuntimeShaders runtimeShaders;
            public Shader stencilDeferredShader;
        }

        internal DeferredLights(InitParams initParams)
        {
            DeferredConfig.isOpenGL = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3;
            DeferredConfig.isDX10 = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 && SystemInfo.graphicsShaderLevel <= 40;

            _stencilDeferredShader = initParams.stencilDeferredShader;

            _stencilDeferredPasses = new int[s_stencilDeferredPassNames.Length];

            this.accurateGbufferNormals = true;
            this.useJobSystem = true;
            _lightCookieManager = initParams.lightCookieManager;

            var gpuInitParams = new GPULights.InitParams();
            gpuInitParams.lightCookieManager = initParams.lightCookieManager;
            gpuInitParams.runtimeShaders = initParams.runtimeShaders;
            _gpuLights = new GPULights(gpuInitParams);

            _deferredLightingCS = gpuInitParams.runtimeShaders.deferredLightingCS;
            _runtimeShaders = initParams.runtimeShaders;
        }

        private void EnsureDeferredLightingKernels()
        {
            if (_deferredClassifyTilesKernel >= 0 && _deferredLightingKernels[2] >= 0)
                return;
            // Forward-only builds may strip this compute shader. Resolve kernels only
            // when clustered deferred lighting actually needs to dispatch them.
            if (_deferredLightingCS == null || !_deferredLightingCS.HasKernel("DeferredClassifyTiles") ||
                !_deferredLightingCS.HasKernel("DeferredLighting0"))
                throw new System.InvalidOperationException("UltraRP clustered deferred lighting requires DeferredLit.compute with DeferredClassifyTiles and DeferredLighting0 kernels. Declare the deferred renderer graph in the included pipeline assets, or disable Strip Unused Variants for runtime-created graphs.");
            _deferredClassifyTilesKernel = _deferredLightingCS.FindKernel("DeferredClassifyTiles");
            for (int model = 0; model < _deferredLightingKernels.Length; ++model)
            {
                string kernel = "DeferredLighting" + model;
                if (!_deferredLightingCS.HasKernel(kernel))
                    throw new System.InvalidOperationException("UltraRP clustered deferred lighting is missing kernel " + kernel + ".");
                _deferredLightingKernels[model] = _deferredLightingCS.FindKernel(kernel);
            }
        }

        protected override void OnCameraCleanup(CommandBuffer cmd)
        {
            cluster = false;
            useClusterRaster = false;
            useDeferredPlus = false;

            // Disable any global keywords setup in SetupLights().
            cmd.SetKeyword(ShaderGlobalKeywords._GBUFFER_NORMALS_OCT, false);

            if (_stencilVisLights.IsCreated)
                _stencilVisLights.Dispose();
            if (_stencilVisLightOffsets.IsCreated)
                _stencilVisLightOffsets.Dispose();

            _getShadowLightIndexCallback = null;
            _gpuLights.OnCameraCleanup(cmd);
        }

        protected override void OnDispose()
        {
            CoreUtils.Destroy(_stencilDeferredMaterial);
            _stencilDeferredMaterial = null;
            CoreUtils.Destroy(_clusterDeferredMaterial);
            _clusterDeferredMaterial = null;
            CoreUtils.Destroy(_deferredPlusMaterial);
            _deferredPlusMaterial = null;
            _gpuLights.Dispose();
            _gpuLights = null;
        }

        public void RegisterGetShadowLightIndexCallback(GetShadowLightIndexDelegate callback)
        {
            if (_getShadowLightIndexCallback != callback)
                _getShadowLightIndexCallback = callback;
        }

        public int GetShadowLightIndex(int visibleLightIndex)
        {
            return _getShadowLightIndexCallback?.Invoke(visibleLightIndex) ?? -1;
        }

        void InitStencilDeferredMaterial()
        {
            if (_stencilDeferredMaterial == null)
                return;

            for (int pass = 0; pass < s_stencilDeferredPassNames.Length; ++pass)
                _stencilDeferredPasses[pass] = _stencilDeferredMaterial.FindPass(s_stencilDeferredPassNames[pass]);

            _stencilDeferredMaterial.SetFloat(ShaderConstants._StencilRef, (float)StencilUsage.MaterialUnlit);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._StencilReadMask, (float)StencilUsage.MaterialMask);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._StencilWriteMask, (float)StencilUsage.StencilLight);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._LitPunctualStencilRef, (float)((int)StencilUsage.StencilLight | (int)StencilUsage.MaterialLit));
            _stencilDeferredMaterial.SetFloat(ShaderConstants._LitPunctualStencilReadMask, (float)((int)StencilUsage.StencilLight | (int)StencilUsage.MaterialMask));
            _stencilDeferredMaterial.SetFloat(ShaderConstants._LitPunctualStencilWriteMask, (float)StencilUsage.StencilLight);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._SimpleLitPunctualStencilRef, (float)((int)StencilUsage.StencilLight | (int)StencilUsage.MaterialSimpleLit));
            _stencilDeferredMaterial.SetFloat(ShaderConstants._SimpleLitPunctualStencilReadMask, (float)((int)StencilUsage.StencilLight | (int)StencilUsage.MaterialMask));
            _stencilDeferredMaterial.SetFloat(ShaderConstants._SimpleLitPunctualStencilWriteMask, (float)StencilUsage.StencilLight);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._LitDirStencilRef, (float)StencilUsage.MaterialLit);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._LitDirStencilReadMask, (float)StencilUsage.MaterialMask);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._LitDirStencilWriteMask, 0.0f);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._SimpleLitDirStencilRef, (float)StencilUsage.MaterialSimpleLit);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._SimpleLitDirStencilReadMask, (float)StencilUsage.MaterialMask);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._SimpleLitDirStencilWriteMask, 0.0f);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._ClearStencilRef, 0.0f);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._ClearStencilReadMask, (float)StencilUsage.MaterialMask);
            _stencilDeferredMaterial.SetFloat(ShaderConstants._ClearStencilWriteMask, (float)StencilUsage.MaterialMask);
        }

        void EnsureStencilDeferredMaterial()
        {
            // Forward and Clustered-only builds may remove every Stencil variant.
            // Resolve the material and pass indices only when Stencil is executed.
            if (_stencilDeferredMaterial == null)
            {
                if (_stencilDeferredShader == null)
                    throw new System.InvalidOperationException("UltraRP Stencil Deferred requires its StencilDeferred shader. Declare a Stencil renderer graph for the build, or disable Strip Unused Variants for undeclared runtime modes.");
                _stencilDeferredMaterial = CoreUtils.CreateEngineMaterial(_stencilDeferredShader);
                InitStencilDeferredMaterial();
            }
        }

        int GetStencilDeferredPass(StencilDeferredPasses pass)
        {
            int index = (int)pass;
            // Unused passes (for example Fog in a fog-free build) may legitimately
            // be stripped. Validate only the pass this draw actually needs.
            // Asset changes can temporarily expose the fallback subshader, so
            // retry the lookup before reporting a missing requested pass.
            if (_stencilDeferredPasses[index] < 0)
                InitStencilDeferredMaterial();
            if (_stencilDeferredPasses[index] < 0)
                throw new System.InvalidOperationException("UltraRP Stencil Deferred is missing requested pass " + s_stencilDeferredPassNames[index] + ". Declare the Stencil renderer graph in included pipeline assets, or disable Strip Unused Variants for undeclared runtime modes.");
            return _stencilDeferredPasses[index];
        }

        static ProfilingSampler s_setupDeferredLights = new ProfilingSampler("Setup Deferred lights.");
        static ProfilingSampler s_setupDeferredKeywords = new ProfilingSampler("Setup Deferred keywords.");
        private class SetupLightPassData
        {
            internal CameraData cameraData;
            internal LightData lightData;
            internal DeferredLights deferredLights;
            internal Vector2Int cameraTargetSizeCopy;
        };
        internal void SetupRenderGraphLights(RenderGraph renderGraph, CameraData cameraData, LightData lightData)
        {
            using (var builder = renderGraph.AddUnsafePass<SetupLightPassData>("SetupDeferredLights", out var passData, s_setupDeferredLights))
            {
                passData.cameraData = cameraData;
                passData.cameraTargetSizeCopy = new Vector2Int(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);
                passData.lightData = lightData;
                passData.deferredLights = this;
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((SetupLightPassData data, UnsafeGraphContext rgContext) =>
                {
                    data.deferredLights.SetupLights(CommandBufferHelpers.GetNativeCommandBuffer(rgContext.cmd), data.cameraData, data.cameraTargetSizeCopy, data.lightData, true);
                });
            }
        }

        internal void SetupLights(CommandBuffer cmd, CameraData cameraData, Vector2Int cameraTargetSizeCopy, LightData lightData, bool isRenderGraph = false)
        {
            Profiler.BeginSample(s_setupLights);

            Camera camera = cameraData.camera;
            this.renderWidth = camera.allowDynamicResolution ? Mathf.CeilToInt(ScalableBufferManager.widthScaleFactor * cameraTargetSizeCopy.x) : cameraTargetSizeCopy.x;
            this.renderHeight = camera.allowDynamicResolution ? Mathf.CeilToInt(ScalableBufferManager.heightScaleFactor * cameraTargetSizeCopy.y) : cameraTargetSizeCopy.y;

            if (!cluster && !useDeferredPlus)
            {
                PrecomputeLights(
                    out _stencilVisLights,
                    out _stencilVisLightOffsets,
                    ref lightData.visibleLights,
                    lightData.additionalLightsCount != 0 || lightData.mainLightIndex >= 0
                );
            }

            SetupDeferredKeywords(cmd, cameraData, lightData, isRenderGraph);

            Profiler.EndSample();
        }

        /// <summary>Keywords and RenderingLayerUtils only. Used by cluster path (no stencil).</summary>
        internal void SetupDeferredKeywords(CommandBuffer cmd, CameraData cameraData, LightData lightData, bool isRenderGraph = false)
        {
            using (new ProfilingScope(cmd, s_profilingSetupLightConstants))
            {
#if UNITY_EDITOR
                bool supportsMixedLighting = true;
#else
                bool supportsMixedLighting = lightData.supportsMixedLighting;
#endif
                Camera camera = cameraData.camera;
                cmd.SetKeyword(ShaderGlobalKeywords._GBUFFER_NORMALS_OCT, this.accurateGbufferNormals);
                bool isShadowMask = supportsMixedLighting && this.mixedLightingSetup == MixedLightingSetup.ShadowMask;
                bool isShadowMaskAlways = isShadowMask && QualitySettings.shadowmaskMode == ShadowmaskMode.Shadowmask;
                bool isSubtractive = supportsMixedLighting && this.mixedLightingSetup == MixedLightingSetup.Subtractive;
                cmd.SetKeyword(ShaderGlobalKeywords.LightmapShadowMixing, isSubtractive || isShadowMaskAlways);
                cmd.SetKeyword(ShaderGlobalKeywords.ShadowsShadowMask, isShadowMask);
                cmd.SetKeyword(ShaderGlobalKeywords.MixedLightingSubtractive, isSubtractive);
                cmd.SetKeyword(ShaderGlobalKeywords.RenderPassEnabled, this.useFramebufferFetch && (cameraData.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView || isRenderGraph));
                cmd.SetKeyword(ShaderGlobalKeywords.LightLayers, useLightLayers && !CoreUtils.IsSceneLightingDisabled(camera));

                RenderingLayerUtils.SetupProperties(cmd, renderingLayerMaskSize);
            }
        }

        void PrecomputeLights(
            out NativeArray<ushort> stencilVisLights,
            out NativeArray<ushort> stencilVisLightOffsets,
            ref NativeArray<VisibleLight> visibleLights,
            bool hasAdditionalLights)
        {
#if UNITY_6000_0_OR_NEWER
            const int lightTypeCount = (int)LightType.Tube + 1;
#else
            const int lightTypeCount = (int)LightType.Disc + 1;
#endif

            if (!hasAdditionalLights)
            {
                stencilVisLights = new NativeArray<ushort>(0, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                stencilVisLightOffsets = new NativeArray<ushort>(lightTypeCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < lightTypeCount; ++i)
                    stencilVisLightOffsets[i] = s_invalidLightOffset;
                return;
            }

            NativeArray<int> stencilLightCounts = new NativeArray<int>(lightTypeCount, Allocator.Temp, NativeArrayOptions.ClearMemory);
            stencilVisLightOffsets = new NativeArray<ushort>(lightTypeCount, Allocator.Temp, NativeArrayOptions.ClearMemory);

            // Count the number of lights per type.
            int visibleLightCount = visibleLights.Length;
            for (ushort visLightIndex = 0; visLightIndex < visibleLightCount; ++visLightIndex)
            {
                ref VisibleLight vl = ref visibleLights.UnsafeElementAtMutable(visLightIndex);
                ++stencilVisLightOffsets[(int)vl.lightType];
            }

            int totalStencilLightCount = stencilVisLightOffsets[(int)LightType.Spot] + stencilVisLightOffsets[(int)LightType.Directional] + stencilVisLightOffsets[(int)LightType.Point];
            stencilVisLights = new NativeArray<ushort>(totalStencilLightCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for (int i = 0, soffset = 0; i < stencilVisLightOffsets.Length; ++i)
            {
                if (stencilVisLightOffsets[i] == 0)
                    stencilVisLightOffsets[i] = s_invalidLightOffset;
                else
                {
                    int c = stencilVisLightOffsets[i];
                    stencilVisLightOffsets[i] = (ushort)soffset;
                    soffset += c;
                }
            }

            for (ushort visLightIndex = 0; visLightIndex < visibleLightCount; ++visLightIndex)
            {
                ref VisibleLight vl = ref visibleLights.UnsafeElementAtMutable(visLightIndex);
                if (vl.lightType != LightType.Spot &&
                    vl.lightType != LightType.Directional &&
                    vl.lightType != LightType.Point)
                {
                    // The light type is not supported. Skip the light.
                    continue;
                }

                int i = stencilLightCounts[(int)vl.lightType]++;
                stencilVisLights[stencilVisLightOffsets[(int)vl.lightType] + i] = visLightIndex;
            }
            stencilLightCounts.Dispose();
        }

        internal void ResolveMixedLightingMode(LightData lightData)
        {
            this.mixedLightingSetup = MixedLightingSetup.None;
#if !UNITY_EDITOR
            // This flag is used to strip mixed lighting shader variants when a player is built.
            // All shader variants are available in the editor.
            if (lightData.supportsMixedLighting)
#endif
            {
                NativeArray<VisibleLight> visibleLights = lightData.visibleLights;
                for (int lightIndex = 0; lightIndex < lightData.visibleLights.Length && this.mixedLightingSetup == MixedLightingSetup.None; ++lightIndex)
                {
                    Light light = visibleLights.UnsafeElementAtMutable(lightIndex).light;

                    if (light != null
                        && light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed
                        && light.shadows != LightShadows.None)
                    {
                        switch (light.bakingOutput.mixedLightingMode)
                        {
                            case MixedLightingMode.Subtractive:
                                this.mixedLightingSetup = MixedLightingSetup.Subtractive;
                                break;
                            case MixedLightingMode.Shadowmask:
                                this.mixedLightingSetup = MixedLightingSetup.ShadowMask;
                                break;
                        }
                    }
                }
            }

            // Once the mixed lighting mode has been discovered, we know how many MRTs we need for the gbuffer.
            // Subtractive mixed lighting requires shadowMask output, which is actually used to store unity_ProbesOcclusion values.
            CreateGbufferResources();
        }

        internal void DisableFramebufferFetchInput()
        {
            this.useFramebufferFetch = false;
            CreateGbufferResources();
        }

        internal void CreateGbufferResources()
        {
            int gbufferSliceCount = this.gBufferSliceCount;
            if (this.gbufferTextureHandles == null || this.gbufferTextureHandles.Length != gbufferSliceCount)
            {
                this.gbufferFormats = new GraphicsFormat[gbufferSliceCount];
                this.gbufferTextureHandles = new TextureHandle[gbufferSliceCount];
                // Release the old handles before creating the new one
                for (int i = 0; i < this.gbufferTextureHandles.Length; ++i)
                    this.gbufferFormats[i] = this.GetGBufferFormat(i);
            }
        }


        bool HasStencilLightsOfType(LightType type)
        {
            return _stencilVisLightOffsets[(int)type] != s_invalidLightOffset;
        }

        void SetupShaderLightConstants(CommandBuffer cmd, LightData lightData)
        {
            // Main light has an optimized shader path for main light. This will benefit games that only care about a single light.
            // Forward pipeline only supports a single shadow light, if available it will be the main light.
            SetupMainLightConstants(cmd, lightData);
        }

        void SetupMainLightConstants(CommandBuffer cmd, LightData lightData)
        {
            if (lightData.mainLightIndex < 0)
                return;

            Vector4 lightPos, lightColor, lightAttenuation, lightSpotDir, lightOcclusionChannel;
            InitializeLightConstantsCommon(lightData.visibleLights, lightData.mainLightIndex, out lightPos, out lightColor, out lightAttenuation, out lightSpotDir, out lightOcclusionChannel);

            if (lightData.supportsLightLayers)
            {
                Light light = lightData.visibleLights[lightData.mainLightIndex].light;
                SetRenderingLayersMask(CommandBufferHelpers.GetRasterCommandBuffer(cmd), light, ShaderConstants._MainLightLayerMask);
            }

            cmd.SetGlobalVector(ShaderConstants._MainLightPosition, lightPos);
            cmd.SetGlobalVector(ShaderConstants._MainLightColor, lightColor);
        }

        private void SetRenderingLayersMask(RasterCommandBuffer cmd, Light light, int shaderPropertyID)
        {
            var additionalLightData = light.GetUltraAdditionalLightData();
            uint lightLayerMask = RenderingLayerUtils.ToValidRenderingLayers(additionalLightData.renderingLayers);
            cmd.SetGlobalInt(shaderPropertyID, (int)lightLayerMask);
        }

        void SetupMatrixConstants(RasterCommandBuffer cmd, CameraData cameraData)
        {
            int width = cameraData.cameraTargetDescriptor.width;
            int height = cameraData.cameraTargetDescriptor.height;
            if (cameraData.camera.allowDynamicResolution && !cameraData.isSubContext)
            {
                width = Mathf.CeilToInt(width * ScalableBufferManager.widthScaleFactor);
                height = Mathf.CeilToInt(height * ScalableBufferManager.heightScaleFactor);
            }
            int eyeCount = 1; // todo: vr ar
            Matrix4x4[] screenToWorld = _screenToWorld; // deferred shaders expects 2 elements

            for (int eyeIndex = 0; eyeIndex < eyeCount; eyeIndex++)
            {
                Matrix4x4 proj = cameraData.GetProjectionMatrix(eyeIndex);
                Matrix4x4 view = cameraData.GetViewMatrix(eyeIndex);
                Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(proj, false);

                // xy coordinates in range [-1; 1] go to pixel coordinates.
                Matrix4x4 toScreen = new Matrix4x4(
                    new Vector4(0.5f * width, 0.0f, 0.0f, 0.0f),
                    new Vector4(0.0f, 0.5f * height, 0.0f, 0.0f),
                    new Vector4(0.0f, 0.0f, 1.0f, 0.0f),
                    new Vector4(0.5f * width, 0.5f * height, 0.0f, 1.0f)
                );

                Matrix4x4 zScaleBias = Matrix4x4.identity;
                if (DeferredConfig.isOpenGL)
                {
                    // We need to manunally adjust z in NDC space from [-1; 1] to [0; 1] (storage in depth texture).
                    zScaleBias = new Matrix4x4(
                        new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
                        new Vector4(0.0f, 1.0f, 0.0f, 0.0f),
                        new Vector4(0.0f, 0.0f, 0.5f, 0.0f),
                        new Vector4(0.0f, 0.0f, 0.5f, 1.0f)
                    );
                }

                screenToWorld[eyeIndex] = Matrix4x4.Inverse(toScreen * zScaleBias * gpuProj * view);
            }

            cmd.SetGlobalMatrixArray(ShaderConstants._ScreenToWorld, screenToWorld);
        }

        internal static StencilState OverwriteStencil(StencilState s, int stencilWriteMask)
        {
            if (!s.enabled)
            {
                return new StencilState(
                    true,
                    0, (byte)stencilWriteMask,
                    CompareFunction.Always, StencilOp.Replace, StencilOp.Keep, StencilOp.Keep,
                    CompareFunction.Always, StencilOp.Replace, StencilOp.Keep, StencilOp.Keep
                );
            }

            CompareFunction funcFront = s.compareFunctionFront != CompareFunction.Disabled ? s.compareFunctionFront : CompareFunction.Always;
            CompareFunction funcBack = s.compareFunctionBack != CompareFunction.Disabled ? s.compareFunctionBack : CompareFunction.Always;
            StencilOp passFront = s.passOperationFront;
            StencilOp failFront = s.failOperationFront;
            StencilOp zfailFront = s.zFailOperationFront;
            StencilOp passBack = s.passOperationBack;
            StencilOp failBack = s.failOperationBack;
            StencilOp zfailBack = s.zFailOperationBack;

            return new StencilState(
                true,
                (byte)(s.readMask & 0x0F), (byte)(s.writeMask | stencilWriteMask),
                funcFront, passFront, failFront, zfailFront,
                funcBack, passBack, failBack, zfailBack
            );
        }

        internal static RenderStateBlock OverwriteStencil(RenderStateBlock block, int stencilWriteMask, int stencilRef)
        {
            if (!block.stencilState.enabled)
            {
                block.stencilState = new StencilState(
                    true,
                    0, (byte)stencilWriteMask,
                    CompareFunction.Always, StencilOp.Replace, StencilOp.Keep, StencilOp.Keep,
                    CompareFunction.Always, StencilOp.Replace, StencilOp.Keep, StencilOp.Keep
                );
            }
            else
            {
                StencilState s = block.stencilState;
                CompareFunction funcFront = s.compareFunctionFront != CompareFunction.Disabled ? s.compareFunctionFront : CompareFunction.Always;
                CompareFunction funcBack = s.compareFunctionBack != CompareFunction.Disabled ? s.compareFunctionBack : CompareFunction.Always;
                StencilOp passFront = s.passOperationFront;
                StencilOp failFront = s.failOperationFront;
                StencilOp zfailFront = s.zFailOperationFront;
                StencilOp passBack = s.passOperationBack;
                StencilOp failBack = s.failOperationBack;
                StencilOp zfailBack = s.zFailOperationBack;

                block.stencilState = new StencilState(
                    true,
                    (byte)(s.readMask & 0x0F), (byte)(s.writeMask | stencilWriteMask),
                    funcFront, passFront, failFront, zfailFront,
                    funcBack, passBack, failBack, zfailBack
                );
            }

            block.mask |= RenderStateMask.Stencil;
            block.stencilReference = (block.stencilReference & (int)StencilUsage.UserMask) | stencilRef;

            return block;
        }

        internal void ClearStencilPartial(RasterCommandBuffer cmd)
        {
            EnsureStencilDeferredMaterial();
            if (_fullscreenMesh == null)
                _fullscreenMesh = CreateFullscreenMesh();

            using (new ProfilingScope(cmd, _profilingSamplerClearStencilPartialPass))
            {
                cmd.DrawMesh(_fullscreenMesh, Matrix4x4.identity, _stencilDeferredMaterial, 0, GetStencilDeferredPass(StencilDeferredPasses.ClearStencilPartial));
            }
        }

        internal void ExecuteDeferredPass(RasterCommandBuffer cmd, CameraData cameraData, LightData lightData, ShadowData shadowData, TextureHandle[] gbuffer)
        {
            EnsureStencilDeferredMaterial();

            if (!useFramebufferFetch)
            {
                for (int i = 0; i < gbuffer.Length; i++)
                {
                    if (i != gBufferLightingIndex)
                        _stencilDeferredMaterial.SetTexture(gbufferShaderPropertyIDs[i], gbuffer[i]);
                }
            }

            using (new ProfilingScope(cmd, s_profilingDeferredPass))
            {
                // This does 2 things:
                // - baked geometry are skipped (do not receive dynamic lighting)
                // - non-baked geometry (== non-static geometry) use shadowMask/occlusionProbes to emulate baked shadows influences.
                cmd.SetKeyword(ShaderGlobalKeywords._DEFERRED_MIXED_LIGHTING, this.useShadowMask);

                // This must be set for each eye in XR mode multipass.
                SetupMatrixConstants(cmd, cameraData);

                RenderStencilLights(cmd, lightData, shadowData, cameraData.renderer.stripShadowsOffVariants);

                cmd.SetKeyword(ShaderGlobalKeywords._DEFERRED_MIXED_LIGHTING, false);
            }

            // Restore shader keywords
            cmd.SetKeyword(ShaderGlobalKeywords.AdditionalLightShadows, shadowData.isKeywordAdditionalLightShadowsEnabled);
            ShadowUtils.SetSoftShadowQualityShaderKeywords(cmd, shadowData);
            cmd.SetKeyword(ShaderGlobalKeywords.LightCookies, _lightCookieManager != null && _lightCookieManager.isKeywordLightCookieEnabled);
        }

        void RenderStencilLights(RasterCommandBuffer cmd, LightData lightData, ShadowData shadowData, bool stripShadowsOffVariants)
        {
            if (_stencilVisLights.Length == 0)
                return;

            if (_stencilDeferredMaterial == null)
            {
                Debug.LogError($"Missing {_stencilDeferredMaterial}. {GetType().Name} render pass will not execute. Check for missing reference in the renderer resources.");
                return;
            }

            Profiler.BeginSample(s_deferredStencilPass);

            using (new ProfilingScope(cmd, _profilingSamplerDeferredStencilPass))
            {
                NativeArray<VisibleLight> visibleLights = lightData.visibleLights;
                bool hasLightCookieManager = _lightCookieManager != null;

                if (HasStencilLightsOfType(LightType.Directional))
                    RenderStencilDirectionalLights(cmd, stripShadowsOffVariants, lightData, shadowData, visibleLights, hasLightCookieManager, lightData.mainLightIndex);

                if (lightData.supportsAdditionalLights)
                {
                    if (HasStencilLightsOfType(LightType.Point))
                        RenderStencilPointLights(cmd, stripShadowsOffVariants, lightData, shadowData, visibleLights, hasLightCookieManager);

                    if (HasStencilLightsOfType(LightType.Spot))
                        RenderStencilSpotLights(cmd, stripShadowsOffVariants, lightData, shadowData, visibleLights, hasLightCookieManager);
                }
            }

            Profiler.EndSample();
        }

        void RenderStencilDirectionalLights(RasterCommandBuffer cmd, bool stripShadowsOffVariants, LightData lightData, ShadowData shadowData, 
            NativeArray<VisibleLight> visibleLights, bool hasLightCookieManager, int mainLightIndex)
        {
            if (_fullscreenMesh == null)
                _fullscreenMesh = CreateFullscreenMesh();

            cmd.SetKeyword(ShaderGlobalKeywords._DIRECTIONAL, true);
            // TODO bundle extra directional lights rendering by batches of 8.
            // Also separate shadow caster lights from non-shadow caster.
            int lastLightCookieIndex = -1;
            bool isFirstLight = true;
            bool lastLightCookieKeywordState = false;
            bool lastShadowsKeywordState = false;
            bool lastSoftShadowsKeywordState = false;

            for (int soffset = _stencilVisLightOffsets[(int)LightType.Directional]; soffset < _stencilVisLights.Length; ++soffset)
            {
                ushort visLightIndex = _stencilVisLights[soffset];
                ref VisibleLight vl = ref visibleLights.UnsafeElementAtMutable(visLightIndex);
                if (vl.lightType != LightType.Directional)
                    break;

                // Avoid light find on every access.
                Light light = vl.light;

                Vector4 lightDir, lightColor, lightAttenuation, lightSpotDir, lightOcclusionChannel;
                InitializeLightConstantsCommon(visibleLights, visLightIndex, out lightDir, out lightColor, out lightAttenuation, out lightSpotDir, out lightOcclusionChannel);

                int lightFlags = 0;
                if (light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed)
                    lightFlags |= (int)LightFlag.SubtractiveMixedLighting;

                if (lightData.supportsLightLayers)
                    SetRenderingLayersMask(cmd, light, ShaderConstants._LightLayerMask);


                // Setup shadow parameters:
                // - for the main light, they have already been setup globally, so nothing to do.
                // - for other directional lights, it is actually not supported by UltraRP, but the code would look like this.
                bool hasDeferredShadows = light && light.shadows != LightShadows.None;
                bool isMainLight = visLightIndex == mainLightIndex;
                if (!isMainLight)
                {
                    int shadowLightIndex = GetShadowLightIndex(visLightIndex);
                    hasDeferredShadows = light && light.shadows != LightShadows.None && shadowLightIndex >= 0;

                    cmd.SetGlobalInt(ShaderConstants._ShadowLightIndex, shadowLightIndex);
                    SetLightCookiesKeyword(cmd, visLightIndex, hasLightCookieManager, isFirstLight, ref lastLightCookieKeywordState, ref lastLightCookieIndex);
                }

                // Update keywords states
                SetAdditionalLightsShadowsKeyword(ref cmd, stripShadowsOffVariants, shadowData.additionalLightShadowsEnabled, hasDeferredShadows, isFirstLight, ref lastShadowsKeywordState);
                SetSoftShadowsKeyword(cmd, shadowData, light, hasDeferredShadows, isFirstLight, ref lastSoftShadowsKeywordState);
                cmd.SetKeyword(ShaderGlobalKeywords._DEFERRED_FIRST_LIGHT, isFirstLight); // First directional light applies SSAO
                cmd.SetKeyword(ShaderGlobalKeywords._DEFERRED_MAIN_LIGHT, isMainLight); // main directional light use different uniform constants from additional directional lights

                // Update Global properties
                cmd.SetGlobalVector(ShaderConstants._LightColor, lightColor); // VisibleLight.finalColor already returns color in active color space
                cmd.SetGlobalVector(ShaderConstants._LightDirection, lightDir);
                cmd.SetGlobalInt(ShaderConstants._LightFlags, lightFlags);

                // Lighting pass.
                cmd.DrawMesh(_fullscreenMesh, Matrix4x4.identity, _stencilDeferredMaterial, 0, GetStencilDeferredPass(StencilDeferredPasses.DirectionalLit));
                cmd.DrawMesh(_fullscreenMesh, Matrix4x4.identity, _stencilDeferredMaterial, 0, GetStencilDeferredPass(StencilDeferredPasses.DirectionalSimpleLit));

                isFirstLight = false;
            }

            cmd.SetKeyword(ShaderGlobalKeywords._DIRECTIONAL, false);
        }

        void RenderStencilPointLights(RasterCommandBuffer cmd, bool stripShadowsOffVariants, LightData lightData, ShadowData shadowData, NativeArray<VisibleLight> visibleLights, bool hasLightCookieManager)
        {
            if (_sphereMesh == null)
                _sphereMesh = CreateSphereMesh();

            cmd.SetKeyword(ShaderGlobalKeywords._POINT, true);

            int lastLightCookieIndex = -1;
            bool isFirstLight = true;
            bool lastLightCookieKeywordState = false;
            bool lastShadowsKeywordState = false;
            bool lastSoftShadowsKeywordState = false;
            for (int soffset = _stencilVisLightOffsets[(int)LightType.Point]; soffset < _stencilVisLights.Length; ++soffset)
            {
                ushort visLightIndex = _stencilVisLights[soffset];
                ref VisibleLight vl = ref visibleLights.UnsafeElementAtMutable(visLightIndex);
                if (vl.lightType != LightType.Point)
                    break;

                // Avoid light find on every access.
                Light light = vl.light;

                Vector3 posWS = vl.localToWorldMatrix.GetColumn(3);
                Matrix4x4 transformMatrix = new Matrix4x4(
                    new Vector4(vl.range, 0.0f, 0.0f, 0.0f),
                    new Vector4(0.0f, vl.range, 0.0f, 0.0f),
                    new Vector4(0.0f, 0.0f, vl.range, 0.0f),
                    new Vector4(posWS.x, posWS.y, posWS.z, 1.0f)
                );

                Vector4 lightPos, lightColor, lightAttenuation, lightOcclusionChannel;
                InitializeLightConstantsCommon(visibleLights, visLightIndex, out lightPos, out lightColor, out lightAttenuation, out _, out lightOcclusionChannel);

                if (lightData.supportsLightLayers)
                    SetRenderingLayersMask(cmd, light, ShaderConstants._LightLayerMask);

                int lightFlags = 0;
                if (light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed)
                    lightFlags |= (int)LightFlag.SubtractiveMixedLighting;

                // Determine whether the light is casting shadows and what the index it should use.
                int shadowLightIndex = GetShadowLightIndex(visLightIndex);
                bool hasDeferredShadows = light && light.shadows != LightShadows.None && shadowLightIndex >= 0;

                // Update keywords states
                SetAdditionalLightsShadowsKeyword(ref cmd, stripShadowsOffVariants, shadowData.additionalLightShadowsEnabled, hasDeferredShadows, isFirstLight, ref lastShadowsKeywordState);
                SetSoftShadowsKeyword(cmd, shadowData, light, hasDeferredShadows, isFirstLight, ref lastSoftShadowsKeywordState);
                SetLightCookiesKeyword(cmd, visLightIndex, hasLightCookieManager, isFirstLight, ref lastLightCookieKeywordState, ref lastLightCookieIndex);

                // Update Global properties
                cmd.SetGlobalVector(ShaderConstants._LightPosWS, lightPos);
                cmd.SetGlobalVector(ShaderConstants._LightColor, lightColor);
                cmd.SetGlobalVector(ShaderConstants._LightAttenuation, lightAttenuation);
                cmd.SetGlobalVector(ShaderConstants._LightOcclusionProbInfo, lightOcclusionChannel);
                cmd.SetGlobalInt(ShaderConstants._LightFlags, lightFlags);
                cmd.SetGlobalInt(ShaderConstants._ShadowLightIndex, shadowLightIndex);

                // Stencil pass.
                cmd.DrawMesh(_sphereMesh, transformMatrix, _stencilDeferredMaterial, 0, GetStencilDeferredPass(StencilDeferredPasses.StencilVolume));

                // Lighting pass.
                cmd.DrawMesh(_sphereMesh, transformMatrix, _stencilDeferredMaterial, 0, GetStencilDeferredPass(StencilDeferredPasses.PunctualLit));
                cmd.DrawMesh(_sphereMesh, transformMatrix, _stencilDeferredMaterial, 0, GetStencilDeferredPass(StencilDeferredPasses.PunctualSimpleLit));

                isFirstLight = false;
            }

            cmd.SetKeyword(ShaderGlobalKeywords._POINT, false);
        }

        void RenderStencilSpotLights(RasterCommandBuffer cmd, bool stripShadowsOffVariants, LightData lightData, ShadowData shadowData, NativeArray<VisibleLight> visibleLights, bool hasLightCookieManager)
        {
            if (_hemisphereMesh == null)
                _hemisphereMesh = CreateHemisphereMesh();

            cmd.SetKeyword(ShaderGlobalKeywords._SPOT, true);

            int lastLightCookieIndex = -1;
            bool isFirstLight = true;
            bool lastLightCookieKeywordState = false;
            bool lastShadowsKeywordState = false;
            bool lastSoftShadowsKeywordState = false;
            for (int soffset = _stencilVisLightOffsets[(int)LightType.Spot]; soffset < _stencilVisLights.Length; ++soffset)
            {
                ushort visLightIndex = _stencilVisLights[soffset];

                ref VisibleLight vl = ref visibleLights.UnsafeElementAtMutable(visLightIndex);
                if (vl.lightType != LightType.Spot)
                    break;

                // Cache light to local, avoid light find on every access.
                Light light = vl.light;

                float alpha = Mathf.Deg2Rad * vl.spotAngle * 0.5f;
                float cosAlpha = Mathf.Cos(alpha);
                float sinAlpha = Mathf.Sin(alpha);
                // Artificially inflate the geometric shape to fit the analytic spot shape.
                // The tighter the spot shape, the lesser inflation is needed.
                float guard = Mathf.Lerp(1.0f, s_stencilShapeGuard, sinAlpha);

                InitializeLightConstantsCommon(visibleLights, visLightIndex, out Vector4 lightPos, out Vector4 lightColor, out Vector4 lightAttenuation, out Vector4 lightSpotDir, out Vector4 lightOcclusionChannel);

                if (lightData.supportsLightLayers)
                    SetRenderingLayersMask(cmd, light, ShaderConstants._LightLayerMask);

                int lightFlags = 0;
                if (light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed)
                    lightFlags |= (int)LightFlag.SubtractiveMixedLighting;

                // Determine whether the light is casting shadows and what the index it should use.
                int shadowLightIndex = GetShadowLightIndex(visLightIndex);
                bool hasDeferredShadows = light && light.shadows != LightShadows.None && shadowLightIndex >= 0;

                // Update keywords states
                SetAdditionalLightsShadowsKeyword(ref cmd, stripShadowsOffVariants, shadowData.additionalLightShadowsEnabled, hasDeferredShadows, isFirstLight, ref lastShadowsKeywordState);
                SetSoftShadowsKeyword(cmd, shadowData, light, hasDeferredShadows, isFirstLight, ref lastSoftShadowsKeywordState);
                SetLightCookiesKeyword(cmd, visLightIndex, hasLightCookieManager, isFirstLight, ref lastLightCookieKeywordState, ref lastLightCookieIndex);

                // Update Global properties
                cmd.SetGlobalVector(ShaderConstants._SpotLightScale, new Vector4(sinAlpha, sinAlpha, 1.0f - cosAlpha, vl.range));
                cmd.SetGlobalVector(ShaderConstants._SpotLightBias, new Vector4(0.0f, 0.0f, cosAlpha, 0.0f));
                cmd.SetGlobalVector(ShaderConstants._SpotLightGuard, new Vector4(guard, guard, guard, cosAlpha * vl.range));
                cmd.SetGlobalVector(ShaderConstants._LightPosWS, lightPos);
                cmd.SetGlobalVector(ShaderConstants._LightColor, lightColor);
                cmd.SetGlobalVector(ShaderConstants._LightAttenuation, lightAttenuation);
                cmd.SetGlobalVector(ShaderConstants._LightDirection, new Vector3(lightSpotDir.x, lightSpotDir.y, lightSpotDir.z));
                cmd.SetGlobalVector(ShaderConstants._LightOcclusionProbInfo, lightOcclusionChannel);
                cmd.SetGlobalInt(ShaderConstants._LightFlags, lightFlags);
                cmd.SetGlobalInt(ShaderConstants._ShadowLightIndex, shadowLightIndex);

                // Stencil pass.
                cmd.DrawMesh(_hemisphereMesh, vl.localToWorldMatrix, _stencilDeferredMaterial, 0, GetStencilDeferredPass(StencilDeferredPasses.StencilVolume));

                // Lighting pass.
                cmd.DrawMesh(_hemisphereMesh, vl.localToWorldMatrix, _stencilDeferredMaterial, 0, GetStencilDeferredPass(StencilDeferredPasses.PunctualLit));
                cmd.DrawMesh(_hemisphereMesh, vl.localToWorldMatrix, _stencilDeferredMaterial, 0, GetStencilDeferredPass(StencilDeferredPasses.PunctualSimpleLit));

                isFirstLight = false;
            }
            cmd.SetKeyword(ShaderGlobalKeywords._SPOT, false);
        }

        private void SetAdditionalLightsShadowsKeyword(ref RasterCommandBuffer cmd, bool stripShadowsOffVariants, bool additionalLightShadowsEnabled, bool hasDeferredShadows, bool shouldOverride, ref bool lastShadowsKeyword)
        {
            bool additionalLightShadowsEnabledInAsset = additionalLightShadowsEnabled;
            bool hasOffVariant = !stripShadowsOffVariants;

            // AdditionalLightShadows Keyword is enabled when:
            // Shadows are enabled in Asset and
            // a) the OFF variant has been stripped
            // b) light is casting a shadow
            bool shouldEnable = additionalLightShadowsEnabledInAsset && (!hasOffVariant || hasDeferredShadows);

            // Return if the state hasn't changed...
            if (!shouldOverride && lastShadowsKeyword == shouldEnable)
                return;

            // Update the keyword state
            lastShadowsKeyword = shouldEnable;
            cmd.SetKeyword(ShaderGlobalKeywords.AdditionalLightShadows, shouldEnable);
        }

        // Enable/Disable the _SHADOWS_SOFT keyword if it has changed...
        private void SetSoftShadowsKeyword(RasterCommandBuffer cmd, ShadowData shadowData, Light light, bool hasDeferredShadows, bool shouldOverride, ref bool lastHasSoftShadow)
        {
            bool hasSoftShadow = hasDeferredShadows && shadowData.supportsSoftShadows && light.shadows == LightShadows.Soft;

            // Return if the state hasn't changed...
            if (!shouldOverride && lastHasSoftShadow == hasSoftShadow)
                return;

            // Update the keyword state
            lastHasSoftShadow = hasSoftShadow;
            ShadowUtils.SetPerLightSoftShadowKeyword(cmd, hasSoftShadow);
        }

        // Enable/Disable the _LIGHT_COOKIES keyword if it has changed and the light cookie index
        private void SetLightCookiesKeyword(RasterCommandBuffer cmd, int visLightIndex, bool hasLightCookieManager, bool shouldOverride, ref bool lastLightCookieState, ref int lastCookieLightIndex)
        {
            if (!hasLightCookieManager)
                return;

            int cookieLightIndex = _lightCookieManager.GetLightCookieShaderDataIndex(visLightIndex);
            bool newState = cookieLightIndex >= 0;
            if (shouldOverride || newState != lastLightCookieState)
            {
                lastLightCookieState = newState;
                cmd.SetKeyword(ShaderGlobalKeywords.LightCookies, newState);
            }

            if (shouldOverride || cookieLightIndex != lastCookieLightIndex)
            {
                lastCookieLightIndex = cookieLightIndex;
                cmd.SetGlobalInt(ShaderConstants._CookieLightIndex, cookieLightIndex);
            }
        }

        static Mesh CreateSphereMesh()
        {
            // This icosaedron has been been slightly inflated to fit an unit sphere.
            // This is the same geometry as built-in deferred.

            Vector3[] positions =
            {
                new Vector3(0.000f,  0.000f, -1.070f), new Vector3(0.174f, -0.535f, -0.910f),
                new Vector3(-0.455f, -0.331f, -0.910f), new Vector3(0.562f,  0.000f, -0.910f),
                new Vector3(-0.455f,  0.331f, -0.910f), new Vector3(0.174f,  0.535f, -0.910f),
                new Vector3(-0.281f, -0.865f, -0.562f), new Vector3(0.736f, -0.535f, -0.562f),
                new Vector3(0.296f, -0.910f, -0.468f), new Vector3(-0.910f,  0.000f, -0.562f),
                new Vector3(-0.774f, -0.562f, -0.478f), new Vector3(0.000f, -1.070f,  0.000f),
                new Vector3(-0.629f, -0.865f,  0.000f), new Vector3(0.629f, -0.865f,  0.000f),
                new Vector3(-1.017f, -0.331f,  0.000f), new Vector3(0.957f,  0.000f, -0.478f),
                new Vector3(0.736f,  0.535f, -0.562f), new Vector3(1.017f, -0.331f,  0.000f),
                new Vector3(1.017f,  0.331f,  0.000f), new Vector3(-0.296f, -0.910f,  0.478f),
                new Vector3(0.281f, -0.865f,  0.562f), new Vector3(0.774f, -0.562f,  0.478f),
                new Vector3(-0.736f, -0.535f,  0.562f), new Vector3(0.910f,  0.000f,  0.562f),
                new Vector3(0.455f, -0.331f,  0.910f), new Vector3(-0.174f, -0.535f,  0.910f),
                new Vector3(0.629f,  0.865f,  0.000f), new Vector3(0.774f,  0.562f,  0.478f),
                new Vector3(0.455f,  0.331f,  0.910f), new Vector3(0.000f,  0.000f,  1.070f),
                new Vector3(-0.562f,  0.000f,  0.910f), new Vector3(-0.957f,  0.000f,  0.478f),
                new Vector3(0.281f,  0.865f,  0.562f), new Vector3(-0.174f,  0.535f,  0.910f),
                new Vector3(0.296f,  0.910f, -0.478f), new Vector3(-1.017f,  0.331f,  0.000f),
                new Vector3(-0.736f,  0.535f,  0.562f), new Vector3(-0.296f,  0.910f,  0.478f),
                new Vector3(0.000f,  1.070f,  0.000f), new Vector3(-0.281f,  0.865f, -0.562f),
                new Vector3(-0.774f,  0.562f, -0.478f), new Vector3(-0.629f,  0.865f,  0.000f),
            };

            int[] indices =
            {
                0,  1,  2,  0,  3,  1,  2,  4,  0,  0,  5,  3,  0,  4,  5,  1,  6,  2,
                3,  7,  1,  1,  8,  6,  1,  7,  8,  9,  4,  2,  2,  6, 10, 10,  9,  2,
                8, 11,  6,  6, 12, 10, 11, 12,  6,  7, 13,  8,  8, 13, 11, 10, 14,  9,
                10, 12, 14,  3, 15,  7,  5, 16,  3,  3, 16, 15, 15, 17,  7, 17, 13,  7,
                16, 18, 15, 15, 18, 17, 11, 19, 12, 13, 20, 11, 11, 20, 19, 17, 21, 13,
                13, 21, 20, 12, 19, 22, 12, 22, 14, 17, 23, 21, 18, 23, 17, 21, 24, 20,
                23, 24, 21, 20, 25, 19, 19, 25, 22, 24, 25, 20, 26, 18, 16, 18, 27, 23,
                26, 27, 18, 28, 24, 23, 27, 28, 23, 24, 29, 25, 28, 29, 24, 25, 30, 22,
                25, 29, 30, 14, 22, 31, 22, 30, 31, 32, 28, 27, 26, 32, 27, 33, 29, 28,
                30, 29, 33, 33, 28, 32, 34, 26, 16,  5, 34, 16, 14, 31, 35, 14, 35,  9,
                31, 30, 36, 30, 33, 36, 35, 31, 36, 37, 33, 32, 36, 33, 37, 38, 32, 26,
                34, 38, 26, 38, 37, 32,  5, 39, 34, 39, 38, 34,  4, 39,  5,  9, 40,  4,
                9, 35, 40,  4, 40, 39, 35, 36, 41, 41, 36, 37, 41, 37, 38, 40, 35, 41,
                40, 41, 39, 41, 38, 39,
            };


            Mesh mesh = new Mesh();
            mesh.indexFormat = IndexFormat.UInt16;
            mesh.vertices = positions;
            mesh.triangles = indices;

            return mesh;
        }

        static Mesh CreateHemisphereMesh()
        {
            // TODO reorder for pre&post-transform cache optimisation.
            // This capped hemisphere shape is in unit dimensions. It will be slightly inflated in the vertex shader
            // to fit the cone analytical shape.
            Vector3[] positions =
            {
                new Vector3(0.000000f, 0.000000f, 0.000000f), new Vector3(1.000000f, 0.000000f, 0.000000f),
                new Vector3(0.923880f, 0.382683f, 0.000000f), new Vector3(0.707107f, 0.707107f, 0.000000f),
                new Vector3(0.382683f, 0.923880f, 0.000000f), new Vector3(-0.000000f, 1.000000f, 0.000000f),
                new Vector3(-0.382684f, 0.923880f, 0.000000f), new Vector3(-0.707107f, 0.707107f, 0.000000f),
                new Vector3(-0.923880f, 0.382683f, 0.000000f), new Vector3(-1.000000f, -0.000000f, 0.000000f),
                new Vector3(-0.923880f, -0.382683f, 0.000000f), new Vector3(-0.707107f, -0.707107f, 0.000000f),
                new Vector3(-0.382683f, -0.923880f, 0.000000f), new Vector3(0.000000f, -1.000000f, 0.000000f),
                new Vector3(0.382684f, -0.923879f, 0.000000f), new Vector3(0.707107f, -0.707107f, 0.000000f),
                new Vector3(0.923880f, -0.382683f, 0.000000f), new Vector3(0.000000f, 0.000000f, 1.000000f),
                new Vector3(0.707107f, 0.000000f, 0.707107f), new Vector3(0.000000f, -0.707107f, 0.707107f),
                new Vector3(0.000000f, 0.707107f, 0.707107f), new Vector3(-0.707107f, 0.000000f, 0.707107f),
                new Vector3(0.816497f, -0.408248f, 0.408248f), new Vector3(0.408248f, -0.408248f, 0.816497f),
                new Vector3(0.408248f, -0.816497f, 0.408248f), new Vector3(0.408248f, 0.816497f, 0.408248f),
                new Vector3(0.408248f, 0.408248f, 0.816497f), new Vector3(0.816497f, 0.408248f, 0.408248f),
                new Vector3(-0.816497f, 0.408248f, 0.408248f), new Vector3(-0.408248f, 0.408248f, 0.816497f),
                new Vector3(-0.408248f, 0.816497f, 0.408248f), new Vector3(-0.408248f, -0.816497f, 0.408248f),
                new Vector3(-0.408248f, -0.408248f, 0.816497f), new Vector3(-0.816497f, -0.408248f, 0.408248f),
                new Vector3(0.000000f, -0.923880f, 0.382683f), new Vector3(0.923880f, 0.000000f, 0.382683f),
                new Vector3(0.000000f, -0.382683f, 0.923880f), new Vector3(0.382683f, 0.000000f, 0.923880f),
                new Vector3(0.000000f, 0.923880f, 0.382683f), new Vector3(0.000000f, 0.382683f, 0.923880f),
                new Vector3(-0.923880f, 0.000000f, 0.382683f), new Vector3(-0.382683f, 0.000000f, 0.923880f)
            };

            int[] indices =
            {
                0, 2, 1, 0, 3, 2, 0, 4, 3, 0, 5, 4, 0, 6, 5, 0,
                7, 6, 0, 8, 7, 0, 9, 8, 0, 10, 9, 0, 11, 10, 0, 12,
                11, 0, 13, 12, 0, 14, 13, 0, 15, 14, 0, 16, 15, 0, 1, 16,
                22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 14, 24, 34, 35,
                22, 16, 36, 23, 37, 2, 27, 35, 38, 25, 4, 37, 26, 39, 6, 30,
                38, 40, 28, 8, 39, 29, 41, 10, 33, 40, 34, 31, 12, 41, 32, 36,
                15, 22, 24, 18, 23, 22, 19, 24, 23, 3, 25, 27, 20, 26, 25, 18,
                27, 26, 7, 28, 30, 21, 29, 28, 20, 30, 29, 11, 31, 33, 19, 32,
                31, 21, 33, 32, 13, 14, 34, 15, 24, 14, 19, 34, 24, 1, 35, 16,
                18, 22, 35, 15, 16, 22, 17, 36, 37, 19, 23, 36, 18, 37, 23, 1,
                2, 35, 3, 27, 2, 18, 35, 27, 5, 38, 4, 20, 25, 38, 3, 4,
                25, 17, 37, 39, 18, 26, 37, 20, 39, 26, 5, 6, 38, 7, 30, 6,
                20, 38, 30, 9, 40, 8, 21, 28, 40, 7, 8, 28, 17, 39, 41, 20,
                29, 39, 21, 41, 29, 9, 10, 40, 11, 33, 10, 21, 40, 33, 13, 34,
                12, 19, 31, 34, 11, 12, 31, 17, 41, 36, 21, 32, 41, 19, 36, 32
            };

            Mesh mesh = new Mesh();
            mesh.indexFormat = IndexFormat.UInt16;
            mesh.vertices = positions;
            mesh.triangles = indices;

            return mesh;
        }

        static Mesh CreateFullscreenMesh()
        {
            // TODO reorder for pre&post-transform cache optimisation.
            // Simple full-screen triangle.
            Vector3[] positions =
            {
                new Vector3(-1.0f,  1.0f, 0.0f),
                new Vector3(-1.0f, -3.0f, 0.0f),
                new Vector3(3.0f,  1.0f, 0.0f)
            };

            int[] indices = { 0, 1, 2 };

            Mesh mesh = new Mesh();
            mesh.indexFormat = IndexFormat.UInt16;
            mesh.vertices = positions;
            mesh.triangles = indices;

            return mesh;
        }
    }
}
