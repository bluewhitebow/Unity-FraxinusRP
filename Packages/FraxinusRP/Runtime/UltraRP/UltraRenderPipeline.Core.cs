using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.GlobalIllumination;

using Lightmapping = UnityEngine.Experimental.GlobalIllumination.Lightmapping;
using LightType = UnityEngine.LightType;

namespace FraxinusEngine.Rendering.Ultra
{
    static class NativeArrayExtensions
    {
        /// <summary>
        /// IMPORTANT: Make sure you do not write to the value! There are no checks for this!
        /// </summary>
        public static unsafe ref T UnsafeElementAt<T>(this NativeArray<T> array, int index) where T : struct
        {
            return ref UnsafeUtility.ArrayElementAsRef<T>(array.GetUnsafeReadOnlyPtr(), index);
        }

        public static unsafe ref T UnsafeElementAtMutable<T>(this NativeArray<T> array, int index) where T : struct
        {
            return ref UnsafeUtility.ArrayElementAsRef<T>(array.GetUnsafePtr(), index);
        }
    }

    public enum MixedLightingSetup
    {
        /// <summary>
        /// Use this to disable mixed lighting.
        /// </summary>
        None,

        /// <summary>
        /// Use this to select shadow mask.
        /// </summary>
        ShadowMask,

        /// <summary>
        /// Use this to select subtractive.
        /// </summary>
        Subtractive,
    };

    internal enum ImageScalingMode
    {
        /// No scaling
        None,

        /// Upscaling to a larger image
        Upscaling,

        /// Downscaling to a smaller image
        Downscaling
    }

    internal enum ImageUpscalingFilter
    {
        /// Bilinear filtering
        Linear,

        /// Nearest-Neighbor filtering
        Point,

        /// FidelityFX Super Resolution
        FSR,

        /// Spatial-Temporal Post-Processing
        STP,

        /// Snapdragon Game Super Resolution 1 (spatial)
        SGSR1,

        /// Snapdragon Game Super Resolution 2 (temporal)
        SGSR2
    }

    internal static class ShaderPropertyId
    {
        public static readonly int glossyEnvironmentColor = Shader.PropertyToID("_GlossyEnvironmentColor");
        public static readonly int subtractiveShadowColor = Shader.PropertyToID("_SubtractiveShadowColor");

        public static readonly int glossyEnvironmentCubeMap = Shader.PropertyToID("_GlossyEnvironmentCubeMap");
        public static readonly int glossyEnvironmentCubeMapHDR = Shader.PropertyToID("_GlossyEnvironmentCubeMap_HDR");

        public static readonly int ambientSkyColor = Shader.PropertyToID("unity_AmbientSky");
        public static readonly int ambientEquatorColor = Shader.PropertyToID("unity_AmbientEquator");
        public static readonly int ambientGroundColor = Shader.PropertyToID("unity_AmbientGround");

        public static readonly int time = Shader.PropertyToID("_Time");
        public static readonly int sinTime = Shader.PropertyToID("_SinTime");
        public static readonly int cosTime = Shader.PropertyToID("_CosTime");
        public static readonly int deltaTime = Shader.PropertyToID("unity_DeltaTime");
        public static readonly int timeParameters = Shader.PropertyToID("_TimeParameters");
        public static readonly int lastTimeParameters = Shader.PropertyToID("_LastTimeParameters");
        public static readonly int frameCount = Shader.PropertyToID("_FrameCount");

        public static readonly int scaledScreenParams = Shader.PropertyToID("_ScaledScreenParams");
        public static readonly int worldSpaceCameraPos = Shader.PropertyToID("_WorldSpaceCameraPos");
        public static readonly int screenParams = Shader.PropertyToID("_ScreenParams");
        public static readonly int alphaToMaskAvailable = Shader.PropertyToID("_AlphaToMaskAvailable");
        public static readonly int projectionParams = Shader.PropertyToID("_ProjectionParams");
        public static readonly int zBufferParams = Shader.PropertyToID("_ZBufferParams");
        public static readonly int orthoParams = Shader.PropertyToID("unity_OrthoParams");
        public static readonly int globalMipBias = Shader.PropertyToID("_GlobalMipBias");

        public static readonly int screenSize = Shader.PropertyToID("_ScreenSize");
        public static readonly int screenCoordScaleBias = Shader.PropertyToID("_ScreenCoordScaleBias");
        public static readonly int screenSizeOverride = Shader.PropertyToID("_ScreenSizeOverride");

        public static readonly int viewMatrix = Shader.PropertyToID("unity_MatrixV");
        public static readonly int projectionMatrix = Shader.PropertyToID("glstate_matrix_projection");
        public static readonly int viewAndProjectionMatrix = Shader.PropertyToID("unity_MatrixVP");

        public static readonly int inverseViewMatrix = Shader.PropertyToID("unity_MatrixInvV");
        public static readonly int inverseProjectionMatrix = Shader.PropertyToID("unity_MatrixInvP");
        public static readonly int inverseViewAndProjectionMatrix = Shader.PropertyToID("unity_MatrixInvVP");

        public static readonly int cameraProjectionMatrix = Shader.PropertyToID("unity_CameraProjection");
        public static readonly int inverseCameraProjectionMatrix = Shader.PropertyToID("unity_CameraInvProjection");
        public static readonly int worldToCameraMatrix = Shader.PropertyToID("unity_WorldToCamera");
        public static readonly int cameraToWorldMatrix = Shader.PropertyToID("unity_CameraToWorld");

        public static readonly int cameraWorldClipPlanes = Shader.PropertyToID("unity_CameraWorldClipPlanes");

        public static readonly int billboardNormal = Shader.PropertyToID("unity_BillboardNormal");
        public static readonly int billboardTangent = Shader.PropertyToID("unity_BillboardTangent");
        public static readonly int billboardCameraParams = Shader.PropertyToID("unity_BillboardCameraParams");

        public static readonly int previousViewProjectionNoJitter = Shader.PropertyToID("_PrevViewProjMatrix");
        public static readonly int viewProjectionNoJitter = Shader.PropertyToID("_NonJitteredViewProjMatrix");

        public static readonly int blitTexture = Shader.PropertyToID("_BlitTexture");
        public static readonly int blitScaleBias = Shader.PropertyToID("_BlitScaleBias");
        public static readonly int sourceTex = Shader.PropertyToID("_SourceTex");
        public static readonly int scaleBias = Shader.PropertyToID("_ScaleBias");
        public static readonly int scaleBiasRt = Shader.PropertyToID("_ScaleBiasRt");

        // This uniform is specific to the RTHandle system
        public static readonly int rtHandleScale = Shader.PropertyToID("_RTHandleScale");

        public static readonly int rendererColor = Shader.PropertyToID("_RendererColor");

        public static readonly int ditheringTexture = Shader.PropertyToID("_DitheringTexture");
        public static readonly int ditheringTextureInvSize = Shader.PropertyToID("_DitheringTextureInvSize");

        public static readonly int renderingLayerMaxInt = Shader.PropertyToID("_RenderingLayerMaxInt");
        public static readonly int renderingLayerRcpMaxInt = Shader.PropertyToID("_RenderingLayerRcpMaxInt");

        public static readonly int overlayUITexture = Shader.PropertyToID("_OverlayUITexture");
        public static readonly int hdrOutputLuminanceParams = Shader.PropertyToID("_HDROutputLuminanceParams");
        public static readonly int hdrOutputGradingParams = Shader.PropertyToID("_HDROutputGradingParams");
    }

    internal static class ShaderGlobalKeywords
    {
        public static GlobalKeyword MainLightShadows;
        public static GlobalKeyword MainLightShadowCascades;
        public static GlobalKeyword MainLightShadowScreen;
        public static GlobalKeyword CastingPunctualLightShadow;
        public static GlobalKeyword AdditionalLightsVertex;
        public static GlobalKeyword AdditionalLightsPixel;
        public static GlobalKeyword ForwardPlus;
        public static GlobalKeyword AdditionalLightShadows;
        public static GlobalKeyword ReflectionProbeBoxProjection;
        public static GlobalKeyword ReflectionProbeBlending;
        public static GlobalKeyword SoftShadows;
        public static GlobalKeyword SoftShadowsLow;
        public static GlobalKeyword SoftShadowsMedium;
        public static GlobalKeyword SoftShadowsHigh;
        public static GlobalKeyword MixedLightingSubtractive; // Backward compatibility
        public static GlobalKeyword LightmapShadowMixing;
        public static GlobalKeyword ShadowsShadowMask;
        public static GlobalKeyword LightLayers;
        public static GlobalKeyword RenderPassEnabled;
        public static GlobalKeyword BillboardFaceCameraPos;
        public static GlobalKeyword LightCookies;
        public static GlobalKeyword AreaLights;
        public static GlobalKeyword DepthNoMsaa;
        public static GlobalKeyword DepthMsaa2;
        public static GlobalKeyword DepthMsaa4;
        public static GlobalKeyword DepthMsaa8;
        public static GlobalKeyword DBufferMRT1;
        public static GlobalKeyword DBufferMRT2;
        public static GlobalKeyword DBufferMRT3;
        public static GlobalKeyword DecalNormalBlendLow;
        public static GlobalKeyword DecalNormalBlendMedium;
        public static GlobalKeyword DecalNormalBlendHigh;
        public static GlobalKeyword DecalLayers;
        public static GlobalKeyword WriteRenderingLayers;
        public static GlobalKeyword ScreenSpaceOcclusion;
        public static GlobalKeyword _SPOT;
        public static GlobalKeyword _DIRECTIONAL;
        public static GlobalKeyword _POINT;
        public static GlobalKeyword _DEFERRED_STENCIL;
        public static GlobalKeyword _DEFERRED_FIRST_LIGHT;
        public static GlobalKeyword _DEFERRED_MAIN_LIGHT;
        public static GlobalKeyword _GBUFFER_NORMALS_OCT;
        public static GlobalKeyword _DEFERRED_MIXED_LIGHTING;
        public static GlobalKeyword LIGHTMAP_ON;
        public static GlobalKeyword DYNAMICLIGHTMAP_ON;
        public static GlobalKeyword _ALPHATEST_ON;
        public static GlobalKeyword DIRLIGHTMAP_COMBINED;
        public static GlobalKeyword _DETAIL_MULX2;
        public static GlobalKeyword _DETAIL_SCALED;
        public static GlobalKeyword _CLEARCOAT;
        public static GlobalKeyword _CLEARCOATMAP;
        public static GlobalKeyword DEBUG_DISPLAY;
        public static GlobalKeyword LOD_FADE_CROSSFADE;
        public static GlobalKeyword USE_UNITY_CROSSFADE;
        public static GlobalKeyword _EMISSION;
        public static GlobalKeyword _RECEIVE_SHADOWS_OFF;
        public static GlobalKeyword _SURFACE_TYPE_TRANSPARENT;
        public static GlobalKeyword _ALPHAPREMULTIPLY_ON;
        public static GlobalKeyword _ALPHAMODULATE_ON;
        public static GlobalKeyword _NORMALMAP;
        public static GlobalKeyword _ADD_PRECOMPUTED_VELOCITY;
        public static GlobalKeyword EDITOR_VISUALIZATION;
        public static GlobalKeyword FoveatedRenderingNonUniformRaster;
        public static GlobalKeyword DisableTexture2DXArray;
        public static GlobalKeyword BlitSingleSlice;
        public static GlobalKeyword XROcclusionMeshCombined;
        public static GlobalKeyword SCREEN_COORD_OVERRIDE;
        public static GlobalKeyword DOWNSAMPLING_SIZE_2;
        public static GlobalKeyword DOWNSAMPLING_SIZE_4;
        public static GlobalKeyword DOWNSAMPLING_SIZE_8;
        public static GlobalKeyword DOWNSAMPLING_SIZE_16;
        public static GlobalKeyword EVALUATE_SH_MIXED;
        public static GlobalKeyword EVALUATE_SH_VERTEX;
        public static GlobalKeyword ProbeVolumeL1;
        public static GlobalKeyword ProbeVolumeL2;
        public static GlobalKeyword ProbeVolumeAPRT;
        public static GlobalKeyword _OUTPUT_DEPTH;
        public static GlobalKeyword LinearToSRGBConversion;
        public static GlobalKeyword _ENABLE_ALPHA_OUTPUT;

        public static GlobalKeyword GPULightsCluster;

        public static void InitializeShaderGlobalKeywords()
        {
            // Init all keywords upfront
            ShaderGlobalKeywords.MainLightShadows = GlobalKeyword.Create(ShaderKeywordStrings.MainLightShadows);
            ShaderGlobalKeywords.MainLightShadowCascades = GlobalKeyword.Create(ShaderKeywordStrings.MainLightShadowCascades);
            ShaderGlobalKeywords.MainLightShadowScreen = GlobalKeyword.Create(ShaderKeywordStrings.MainLightShadowScreen);
            ShaderGlobalKeywords.CastingPunctualLightShadow = GlobalKeyword.Create(ShaderKeywordStrings.CastingPunctualLightShadow);
            ShaderGlobalKeywords.AdditionalLightsVertex = GlobalKeyword.Create(ShaderKeywordStrings.AdditionalLightsVertex);
            ShaderGlobalKeywords.AdditionalLightsPixel = GlobalKeyword.Create(ShaderKeywordStrings.AdditionalLightsPixel);
            ShaderGlobalKeywords.ForwardPlus = GlobalKeyword.Create(ShaderKeywordStrings.ForwardPlus);
            ShaderGlobalKeywords.AdditionalLightShadows = GlobalKeyword.Create(ShaderKeywordStrings.AdditionalLightShadows);
            ShaderGlobalKeywords.ReflectionProbeBoxProjection = GlobalKeyword.Create(ShaderKeywordStrings.ReflectionProbeBoxProjection);
            ShaderGlobalKeywords.ReflectionProbeBlending = GlobalKeyword.Create(ShaderKeywordStrings.ReflectionProbeBlending);
            ShaderGlobalKeywords.SoftShadows = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadows);
            ShaderGlobalKeywords.SoftShadowsLow = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadowsLow);
            ShaderGlobalKeywords.SoftShadowsMedium = GlobalKeyword.Create(ShaderKeywordStrings.SoftShadowsMedium);
            ShaderGlobalKeywords.MixedLightingSubtractive = GlobalKeyword.Create(ShaderKeywordStrings.MixedLightingSubtractive);
            ShaderGlobalKeywords.LightmapShadowMixing = GlobalKeyword.Create(ShaderKeywordStrings.LightmapShadowMixing);
            ShaderGlobalKeywords.ShadowsShadowMask = GlobalKeyword.Create(ShaderKeywordStrings.ShadowsShadowMask);
            ShaderGlobalKeywords.LightLayers = GlobalKeyword.Create(ShaderKeywordStrings.LightLayers);
            ShaderGlobalKeywords.RenderPassEnabled = GlobalKeyword.Create(ShaderKeywordStrings.RenderPassEnabled);
            ShaderGlobalKeywords.BillboardFaceCameraPos = GlobalKeyword.Create(ShaderKeywordStrings.BillboardFaceCameraPos);
            ShaderGlobalKeywords.LightCookies = GlobalKeyword.Create(ShaderKeywordStrings.LightCookies);
            ShaderGlobalKeywords.AreaLights = GlobalKeyword.Create(ShaderKeywordStrings.AreaLights);
            ShaderGlobalKeywords.DepthNoMsaa = GlobalKeyword.Create(ShaderKeywordStrings.DepthNoMsaa);
            ShaderGlobalKeywords.DepthMsaa2 = GlobalKeyword.Create(ShaderKeywordStrings.DepthMsaa2);
            ShaderGlobalKeywords.DepthMsaa4 = GlobalKeyword.Create(ShaderKeywordStrings.DepthMsaa4);
            ShaderGlobalKeywords.DepthMsaa8 = GlobalKeyword.Create(ShaderKeywordStrings.DepthMsaa8);
            ShaderGlobalKeywords.DBufferMRT1 = GlobalKeyword.Create(ShaderKeywordStrings.DBufferMRT1);
            ShaderGlobalKeywords.DBufferMRT2 = GlobalKeyword.Create(ShaderKeywordStrings.DBufferMRT2);
            ShaderGlobalKeywords.DBufferMRT3 = GlobalKeyword.Create(ShaderKeywordStrings.DBufferMRT3);
            ShaderGlobalKeywords.DecalNormalBlendLow = GlobalKeyword.Create(ShaderKeywordStrings.DecalNormalBlendLow);
            ShaderGlobalKeywords.DecalNormalBlendMedium = GlobalKeyword.Create(ShaderKeywordStrings.DecalNormalBlendMedium);
            ShaderGlobalKeywords.DecalNormalBlendHigh = GlobalKeyword.Create(ShaderKeywordStrings.DecalNormalBlendHigh);
            ShaderGlobalKeywords.DecalLayers = GlobalKeyword.Create(ShaderKeywordStrings.DecalLayers);
            ShaderGlobalKeywords.WriteRenderingLayers = GlobalKeyword.Create(ShaderKeywordStrings.WriteRenderingLayers);
            ShaderGlobalKeywords.ScreenSpaceOcclusion = GlobalKeyword.Create(ShaderKeywordStrings.ScreenSpaceOcclusion);
            ShaderGlobalKeywords._SPOT = GlobalKeyword.Create(ShaderKeywordStrings._SPOT);
            ShaderGlobalKeywords._DIRECTIONAL = GlobalKeyword.Create(ShaderKeywordStrings._DIRECTIONAL);
            ShaderGlobalKeywords._POINT = GlobalKeyword.Create(ShaderKeywordStrings._POINT);
            ShaderGlobalKeywords._DEFERRED_STENCIL = GlobalKeyword.Create(ShaderKeywordStrings._DEFERRED_STENCIL);
            ShaderGlobalKeywords._DEFERRED_FIRST_LIGHT = GlobalKeyword.Create(ShaderKeywordStrings._DEFERRED_FIRST_LIGHT);
            ShaderGlobalKeywords._DEFERRED_MAIN_LIGHT = GlobalKeyword.Create(ShaderKeywordStrings._DEFERRED_MAIN_LIGHT);
            ShaderGlobalKeywords._GBUFFER_NORMALS_OCT = GlobalKeyword.Create(ShaderKeywordStrings._GBUFFER_NORMALS_OCT);
            ShaderGlobalKeywords._DEFERRED_MIXED_LIGHTING = GlobalKeyword.Create(ShaderKeywordStrings._DEFERRED_MIXED_LIGHTING);
            ShaderGlobalKeywords.LIGHTMAP_ON = GlobalKeyword.Create(ShaderKeywordStrings.LIGHTMAP_ON);
            ShaderGlobalKeywords.DYNAMICLIGHTMAP_ON = GlobalKeyword.Create(ShaderKeywordStrings.DYNAMICLIGHTMAP_ON);
            ShaderGlobalKeywords._ALPHATEST_ON = GlobalKeyword.Create(ShaderKeywordStrings._ALPHATEST_ON);
            ShaderGlobalKeywords.DIRLIGHTMAP_COMBINED = GlobalKeyword.Create(ShaderKeywordStrings.DIRLIGHTMAP_COMBINED);
            ShaderGlobalKeywords._DETAIL_MULX2 = GlobalKeyword.Create(ShaderKeywordStrings._DETAIL_MULX2);
            ShaderGlobalKeywords._DETAIL_SCALED = GlobalKeyword.Create(ShaderKeywordStrings._DETAIL_SCALED);
            ShaderGlobalKeywords._CLEARCOAT = GlobalKeyword.Create(ShaderKeywordStrings._CLEARCOAT);
            ShaderGlobalKeywords._CLEARCOATMAP = GlobalKeyword.Create(ShaderKeywordStrings._CLEARCOATMAP);
            ShaderGlobalKeywords.DEBUG_DISPLAY = GlobalKeyword.Create(ShaderKeywordStrings.DEBUG_DISPLAY);
            ShaderGlobalKeywords.LOD_FADE_CROSSFADE = GlobalKeyword.Create(ShaderKeywordStrings.LOD_FADE_CROSSFADE);
            ShaderGlobalKeywords.USE_UNITY_CROSSFADE = GlobalKeyword.Create(ShaderKeywordStrings.USE_UNITY_CROSSFADE);
            ShaderGlobalKeywords._EMISSION = GlobalKeyword.Create(ShaderKeywordStrings._EMISSION);
            ShaderGlobalKeywords._RECEIVE_SHADOWS_OFF = GlobalKeyword.Create(ShaderKeywordStrings._RECEIVE_SHADOWS_OFF);
            ShaderGlobalKeywords._SURFACE_TYPE_TRANSPARENT = GlobalKeyword.Create(ShaderKeywordStrings._SURFACE_TYPE_TRANSPARENT);
            ShaderGlobalKeywords._ALPHAPREMULTIPLY_ON = GlobalKeyword.Create(ShaderKeywordStrings._ALPHAPREMULTIPLY_ON);
            ShaderGlobalKeywords._ALPHAMODULATE_ON = GlobalKeyword.Create(ShaderKeywordStrings._ALPHAMODULATE_ON);
            ShaderGlobalKeywords._NORMALMAP = GlobalKeyword.Create(ShaderKeywordStrings._NORMALMAP);
            ShaderGlobalKeywords._ADD_PRECOMPUTED_VELOCITY = GlobalKeyword.Create(ShaderKeywordStrings._ADD_PRECOMPUTED_VELOCITY);
            ShaderGlobalKeywords.EDITOR_VISUALIZATION = GlobalKeyword.Create(ShaderKeywordStrings.EDITOR_VISUALIZATION);
            ShaderGlobalKeywords.FoveatedRenderingNonUniformRaster = GlobalKeyword.Create(ShaderKeywordStrings.FoveatedRenderingNonUniformRaster);
            ShaderGlobalKeywords.DisableTexture2DXArray = GlobalKeyword.Create(ShaderKeywordStrings.DisableTexture2DXArray);
            ShaderGlobalKeywords.BlitSingleSlice = GlobalKeyword.Create(ShaderKeywordStrings.BlitSingleSlice);
            ShaderGlobalKeywords.XROcclusionMeshCombined = GlobalKeyword.Create(ShaderKeywordStrings.XROcclusionMeshCombined);
            ShaderGlobalKeywords.SCREEN_COORD_OVERRIDE = GlobalKeyword.Create(ShaderKeywordStrings.SCREEN_COORD_OVERRIDE);
            ShaderGlobalKeywords.DOWNSAMPLING_SIZE_2 = GlobalKeyword.Create(ShaderKeywordStrings.DOWNSAMPLING_SIZE_2);
            ShaderGlobalKeywords.DOWNSAMPLING_SIZE_4 = GlobalKeyword.Create(ShaderKeywordStrings.DOWNSAMPLING_SIZE_4);
            ShaderGlobalKeywords.DOWNSAMPLING_SIZE_8 = GlobalKeyword.Create(ShaderKeywordStrings.DOWNSAMPLING_SIZE_8);
            ShaderGlobalKeywords.DOWNSAMPLING_SIZE_16 = GlobalKeyword.Create(ShaderKeywordStrings.DOWNSAMPLING_SIZE_16);
            ShaderGlobalKeywords.EVALUATE_SH_MIXED = GlobalKeyword.Create(ShaderKeywordStrings.EVALUATE_SH_MIXED);
            ShaderGlobalKeywords.EVALUATE_SH_VERTEX = GlobalKeyword.Create(ShaderKeywordStrings.EVALUATE_SH_VERTEX);
            ShaderGlobalKeywords.ProbeVolumeL1 = GlobalKeyword.Create(ShaderKeywordStrings.ProbeVolumeL1);
            ShaderGlobalKeywords.ProbeVolumeL2 = GlobalKeyword.Create(ShaderKeywordStrings.ProbeVolumeL2);
            ShaderGlobalKeywords.ProbeVolumeAPRT = GlobalKeyword.Create(ShaderKeywordStrings.ProbeVolumeAPRT);
            ShaderGlobalKeywords._OUTPUT_DEPTH = GlobalKeyword.Create(ShaderKeywordStrings._OUTPUT_DEPTH);
            ShaderGlobalKeywords.LinearToSRGBConversion = GlobalKeyword.Create(ShaderKeywordStrings.LinearToSRGBConversion);
            ShaderGlobalKeywords._ENABLE_ALPHA_OUTPUT = GlobalKeyword.Create(ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT);
            ShaderGlobalKeywords.GPULightsCluster = GlobalKeyword.Create(ShaderKeywordStrings._GPULightsCluster);
        }
    }

    public static class ShaderKeywordStrings
    {
        /// <summary> Keyword used for shadows without cascades. </summary>
        public const string MainLightShadows = "_MAIN_LIGHT_SHADOWS";

        /// <summary> Keyword used for shadows with cascades. </summary>
        public const string MainLightShadowCascades = "_MAIN_LIGHT_SHADOWS_CASCADE";

        /// <summary> Keyword used for screen space shadows. </summary>
        public const string MainLightShadowScreen = "_MAIN_LIGHT_SHADOWS_SCREEN";

        /// <summary> Keyword used during shadow map generation to differentiate between directional and punctual light shadows, as they use different formulas to apply Normal Bias. </summary>
        public const string CastingPunctualLightShadow = "_CASTING_PUNCTUAL_LIGHT_SHADOW";

        /// <summary> Keyword used for per vertex additional lights. </summary>
        public const string AdditionalLightsVertex = "_ADDITIONAL_LIGHTS_VERTEX";

        /// <summary> Keyword used for per pixel additional lights. </summary>
        public const string AdditionalLightsPixel = "_ADDITIONAL_LIGHTS";

        /// <summary> Keyword used for Forward+. </summary>
        internal const string ForwardPlus = "_FORWARD_PLUS";

        /// <summary> Keyword used for shadows on additional lights. </summary>
        public const string AdditionalLightShadows = "_ADDITIONAL_LIGHT_SHADOWS";

        /// <summary> Keyword used for Box Projection with Reflection Probes. </summary>
        public const string ReflectionProbeBoxProjection = "_REFLECTION_PROBE_BOX_PROJECTION";

        /// <summary> Keyword used for Reflection probe blending. </summary>
        public const string ReflectionProbeBlending = "_REFLECTION_PROBE_BLENDING";

        /// <summary> Keyword used for soft shadows. </summary>
        public const string SoftShadows = "_SHADOWS_SOFT";

        /// <summary> Keyword used for low quality soft shadows. </summary>
        public const string SoftShadowsLow = "_SHADOWS_SOFT_LOW";

        /// <summary> Keyword used for medium quality soft shadows. </summary>
        public const string SoftShadowsMedium = "_SHADOWS_SOFT_MEDIUM";

        /// <summary> Keyword used for high quality soft shadows. </summary>
        public const string SoftShadowsHigh = "_SHADOWS_SOFT_HIGH";

        /// <summary> Keyword used for Mixed Lights in Subtractive lighting mode. </summary>
        public const string MixedLightingSubtractive = "_MIXED_LIGHTING_SUBTRACTIVE"; // Backward compatibility

        /// <summary> Keyword used for mixing lightmap shadows. </summary>
        public const string LightmapShadowMixing = "LIGHTMAP_SHADOW_MIXING";

        /// <summary> Keyword used for Shadowmask. </summary>
        public const string ShadowsShadowMask = "SHADOWS_SHADOWMASK";

        /// <summary> Keyword used for Light Layers. </summary>
        public const string LightLayers = "_LIGHT_LAYERS";

        /// <summary> Keyword used for RenderPass. </summary>
        public const string RenderPassEnabled = "_RENDER_PASS_ENABLED";

        /// <summary> Keyword used for Billboard cameras. </summary>
        public const string BillboardFaceCameraPos = "BILLBOARD_FACE_CAMERA_POS";

        /// <summary> Keyword used for Light Cookies. </summary>
        public const string LightCookies = "_LIGHT_COOKIES";
        public const string AreaLights = "_FRAXINUS_AREA_LIGHTS";

        /// <summary> Keyword used for no Multi Sampling Anti-Aliasing (MSAA). </summary>
        public const string DepthNoMsaa = "_DEPTH_NO_MSAA";

        /// <summary> Keyword used for Multi Sampling Anti-Aliasing (MSAA) with 2 per pixel sample count. </summary>
        public const string DepthMsaa2 = "_DEPTH_MSAA_2";

        /// <summary> Keyword used for Multi Sampling Anti-Aliasing (MSAA) with 4 per pixel sample count. </summary>
        public const string DepthMsaa4 = "_DEPTH_MSAA_4";

        /// <summary> Keyword used for Multi Sampling Anti-Aliasing (MSAA) with 8 per pixel sample count. </summary>
        public const string DepthMsaa8 = "_DEPTH_MSAA_8";

        /// <summary> Keyword used for Linear to SRGB conversions. </summary>
        public const string LinearToSRGBConversion = "_LINEAR_TO_SRGB_CONVERSION";

        /// <summary> Keyword used for less expensive Linear to SRGB conversions. </summary>
        internal const string UseFastSRGBLinearConversion = "_USE_FAST_SRGB_LINEAR_CONVERSION";

        /// <summary> Keyword used for first target in the DBuffer. </summary>
        public const string DBufferMRT1 = "_DBUFFER_MRT1";

        /// <summary> Keyword used for second target in the DBuffer. </summary>
        public const string DBufferMRT2 = "_DBUFFER_MRT2";

        /// <summary> Keyword used for third target in the DBuffer. </summary>
        public const string DBufferMRT3 = "_DBUFFER_MRT3";

        /// <summary> Keyword used for low quality normal reconstruction in Decals. </summary>
        public const string DecalNormalBlendLow = "_DECAL_NORMAL_BLEND_LOW";

        /// <summary> Keyword used for medium quality normal reconstruction in Decals. </summary>
        public const string DecalNormalBlendMedium = "_DECAL_NORMAL_BLEND_MEDIUM";

        /// <summary> Keyword used for high quality normal reconstruction in Decals. </summary>
        public const string DecalNormalBlendHigh = "_DECAL_NORMAL_BLEND_HIGH";

        /// <summary> Keyword used for Decal Layers. </summary>
        public const string DecalLayers = "_DECAL_LAYERS";

        /// <summary> Keyword used for writing Rendering Layers. </summary>
        public const string WriteRenderingLayers = "_WRITE_RENDERING_LAYERS";

        /// <summary> Keyword used for low quality Subpixel Morphological Anti-aliasing (SMAA). </summary>
        public const string SmaaLow = "_SMAA_PRESET_LOW";

        /// <summary> Keyword used for medium quality Subpixel Morphological Anti-aliasing (SMAA). </summary>
        public const string SmaaMedium = "_SMAA_PRESET_MEDIUM";

        /// <summary> Keyword used for high quality Subpixel Morphological Anti-aliasing (SMAA). </summary>
        public const string SmaaHigh = "_SMAA_PRESET_HIGH";

        /// <summary> Keyword used for generic Panini Projection. </summary>
        public const string PaniniGeneric = "_GENERIC";

        /// <summary> Keyword used for unit distance Panini Projection. </summary>
        public const string PaniniUnitDistance = "_UNIT_DISTANCE";

        /// <summary> Keyword used for low quality Bloom. </summary>
        public const string BloomLQ = "_BLOOM_LQ";

        /// <summary> Keyword used for high quality Bloom. </summary>
        public const string BloomHQ = "_BLOOM_HQ";

        /// <summary> Keyword used for low quality Bloom dirt. </summary>
        public const string BloomLQDirt = "_BLOOM_LQ_DIRT";

        /// <summary> Keyword used for high quality Bloom dirt. </summary>
        public const string BloomHQDirt = "_BLOOM_HQ_DIRT";

        /// <summary> Keyword used for RGBM format for Bloom. </summary>
        public const string UseRGBM = "_USE_RGBM";

        /// <summary> Keyword used for Distortion. </summary>
        public const string Distortion = "_DISTORTION";

        /// <summary> Keyword used for Chromatic Aberration. </summary>
        public const string ChromaticAberration = "_CHROMATIC_ABERRATION";

        /// <summary> Keyword used for HDR Color Grading. </summary>
        public const string HDRGrading = "_HDR_GRADING";

        /// <summary> Keyword used for ACES Tonemapping. </summary>
        public const string TonemapACES = "_TONEMAP_ACES";

        /// <summary> Keyword used for HDR UI Overlay compositing. </summary>
        public const string HDROverlay = "_HDR_OVERLAY";

        /// <summary> Keyword used for Neutral Tonemapping. </summary>
        public const string TonemapNeutral = "_TONEMAP_NEUTRAL";

        /// <summary> Keyword used for Film Grain. </summary>
        public const string FilmGrain = "_FILM_GRAIN";

        /// <summary> Keyword used for Fast Approximate Anti-aliasing (FXAA). </summary>
        public const string Fxaa = "_FXAA";

        /// <summary> Keyword used for Dithering. </summary>
        public const string Dithering = "_DITHERING";

        /// <summary> Keyword used for Screen Space Occlusion, such as Screen Space Ambient Occlusion (SSAO). </summary>
        public const string ScreenSpaceOcclusion = "_SCREEN_SPACE_OCCLUSION";

        /// <summary> Keyword used for Point sampling when doing upsampling. </summary>
        public const string PointSampling = "_POINT_SAMPLING";

        /// <summary> Keyword used for Robust Contrast-Adaptive Sharpening (RCAS) when doing upsampling. </summary>
        public const string Rcas = "_RCAS";

        /// <summary> Keyword used for Robust Contrast-Adaptive Sharpening (RCAS) when doing upsampling, after EASU has ran and with HDR Dsiplay output. </summary>
        public const string EasuRcasAndHDRInput = "_EASU_RCAS_AND_HDR_INPUT";

        /// <summary> Keyword used for Gamma 2.0. </summary>
        public const string Gamma20 = "_GAMMA_20";

        /// <summary> Keyword used for Gamma 2.0 with HDR_INPUT. </summary>
        public const string Gamma20AndHDRInput = "_GAMMA_20_AND_HDR_INPUT";

        /// <summary> Keyword used for high quality sampling for Depth Of Field. </summary>
        public const string HighQualitySampling = "_HIGH_QUALITY_SAMPLING";

        /// <summary> Keyword used for Spot lights. </summary>
        public const string _SPOT = "_SPOT";

        /// <summary> Keyword used for Directional lights. </summary>
        public const string _DIRECTIONAL = "_DIRECTIONAL";

        /// <summary> Keyword used for Point lights. </summary>
        public const string _POINT = "_POINT";

        /// <summary> Keyword used for stencils when rendering with the Deferred rendering path. </summary>
        public const string _DEFERRED_STENCIL = "_DEFERRED_STENCIL";

        /// <summary> Keyword used for the first light when rendering with the Deferred rendering path. </summary>
        public const string _DEFERRED_FIRST_LIGHT = "_DEFERRED_FIRST_LIGHT";

        /// <summary> Keyword used for the main light when rendering with the Deferred rendering path. </summary>
        public const string _DEFERRED_MAIN_LIGHT = "_DEFERRED_MAIN_LIGHT";

        /// <summary> Keyword used for Accurate G-buffer normals when rendering with the Deferred rendering path. </summary>
        public const string _GBUFFER_NORMALS_OCT = "_GBUFFER_NORMALS_OCT";

        /// <summary> Keyword used for Mixed Lighting when rendering with the Deferred rendering path. </summary>
        public const string _DEFERRED_MIXED_LIGHTING = "_DEFERRED_MIXED_LIGHTING";

        /// <summary> Keyword used for Lightmaps. </summary>
        public const string LIGHTMAP_ON = "LIGHTMAP_ON";

        /// <summary> Keyword used for dynamic Lightmaps. </summary>
        public const string DYNAMICLIGHTMAP_ON = "DYNAMICLIGHTMAP_ON";

        /// <summary> Keyword used for Alpha testing. </summary>
        public const string _ALPHATEST_ON = "_ALPHATEST_ON";

        /// <summary> Keyword used for combined directional Lightmaps. </summary>
        public const string DIRLIGHTMAP_COMBINED = "DIRLIGHTMAP_COMBINED";

        /// <summary> Keyword used for 2x detail mapping. </summary>
        public const string _DETAIL_MULX2 = "_DETAIL_MULX2";

        /// <summary> Keyword used for scaled detail mapping. </summary>
        public const string _DETAIL_SCALED = "_DETAIL_SCALED";

        /// <summary> Keyword used for Clear Coat. </summary>
        public const string _CLEARCOAT = "_CLEARCOAT";

        /// <summary> Keyword used for Clear Coat maps. </summary>
        public const string _CLEARCOATMAP = "_CLEARCOATMAP";

        /// <summary> Keyword used for Debug Display. </summary>
        public const string DEBUG_DISPLAY = "DEBUG_DISPLAY";

        /// <summary> Keyword used for LOD Crossfade. </summary>
        public const string LOD_FADE_CROSSFADE = "LOD_FADE_CROSSFADE";

        /// <summary> Keyword used for LOD Crossfade with ShaderGraph shaders. </summary>
        public const string USE_UNITY_CROSSFADE = "USE_UNITY_CROSSFADE";

        /// <summary> Keyword used for Emission. </summary>
        public const string _EMISSION = "_EMISSION";

        /// <summary> Keyword used for receiving shadows. </summary>
        public const string _RECEIVE_SHADOWS_OFF = "_RECEIVE_SHADOWS_OFF";

        /// <summary> Keyword used for opaque or transparent surface types. </summary>
        public const string _SURFACE_TYPE_TRANSPARENT = "_SURFACE_TYPE_TRANSPARENT";

        /// <summary> Keyword used for Alpha premultiply. </summary>
        public const string _ALPHAPREMULTIPLY_ON = "_ALPHAPREMULTIPLY_ON";

        /// <summary> Keyword used for Alpha modulate. </summary>
        public const string _ALPHAMODULATE_ON = "_ALPHAMODULATE_ON";

        /// <summary> Keyword used for Normal maps. </summary>
        public const string _NORMALMAP = "_NORMALMAP";

        /// <summary> Keyword used for Alembic precomputed velocity. </summary>
        public const string _ADD_PRECOMPUTED_VELOCITY = "_ADD_PRECOMPUTED_VELOCITY";

        /// <summary> Keyword used for editor visualization. </summary>
        public const string EDITOR_VISUALIZATION = "EDITOR_VISUALIZATION";

        /// <summary> Keyword used for foveated rendering. </summary>
        public const string FoveatedRenderingNonUniformRaster = "_FOVEATED_RENDERING_NON_UNIFORM_RASTER";

        /// <summary> Keyword used for disabling Texture 2D Arrays. </summary>
        public const string DisableTexture2DXArray = "DISABLE_TEXTURE2D_X_ARRAY";

        /// <summary> Keyword used for Single Slice Blits. </summary>
        public const string BlitSingleSlice = "BLIT_SINGLE_SLICE";

        /// <summary> Keyword used for rendering a combined mesh for XR. </summary>
        public const string XROcclusionMeshCombined = "XR_OCCLUSION_MESH_COMBINED";

        /// <summary> Keyword used for applying scale and bias. </summary>
        public const string SCREEN_COORD_OVERRIDE = "SCREEN_COORD_OVERRIDE";

        /// <summary> Keyword used for half size downsampling. </summary>
        public const string DOWNSAMPLING_SIZE_2 = "DOWNSAMPLING_SIZE_2";

        /// <summary> Keyword used for quarter size downsampling. </summary>
        public const string DOWNSAMPLING_SIZE_4 = "DOWNSAMPLING_SIZE_4";

        /// <summary> Keyword used for eighth size downsampling. </summary>
        public const string DOWNSAMPLING_SIZE_8 = "DOWNSAMPLING_SIZE_8";

        /// <summary> Keyword used for sixteenth size downsampling. </summary>
        public const string DOWNSAMPLING_SIZE_16 = "DOWNSAMPLING_SIZE_16";

        /// <summary> Keyword used for mixed Spherical Harmonic (SH) evaluation in UltraRP Lit shaders.</summary>
        public const string EVALUATE_SH_MIXED = "EVALUATE_SH_MIXED";

        /// <summary> Keyword used for vertex Spherical Harmonic (SH) evaluation in UltraRP Lit shaders.</summary>
        public const string EVALUATE_SH_VERTEX = "EVALUATE_SH_VERTEX";

        /// <summary> Keyword used for APV with SH L1 </summary>
        public const string ProbeVolumeL1 = "PROBE_VOLUMES_L1";

        /// <summary> Keyword used for APV with SH L2 </summary>
        public const string ProbeVolumeL2 = "PROBE_VOLUMES_L2";

        /// <summary> Keyword used for Adaptive PRT Volumes. </summary>
        public const string ProbeVolumeAPRT = "PROBE_VOLUMES_APRT";

        /// <summary> Keyword used for opting out of lightmap texture arrays, when using BatchRendererGroup. </summary>
        public const string USE_LEGACY_LIGHTMAPS = "USE_LEGACY_LIGHTMAPS";

        /// <summary> Keyword used for CopyDepth pass. </summary>
        public const string _OUTPUT_DEPTH = "_OUTPUT_DEPTH";

        /// <summary> Keyword used for enable alpha output. Used in post processing. </summary>
        public const string _ENABLE_ALPHA_OUTPUT = "_ENABLE_ALPHA_OUTPUT";

        /// <summary> Keyword used for GPULights.</summary>
        public const string _GPULightsCluster = "_GPU_LIGHTS_CLUSTER";
    }

    public sealed partial class UltraRenderPipeline
    {
        static List<Vector4> s_shadowBiasData = new List<Vector4>();
        static List<int> s_shadowResolutionData = new List<int>();

        public static class ShaderKeywordStrings
        {
            public const string MainLightShadows = "_MAIN_LIGHT_SHADOWS";

            public const string BillboardFaceCameraPos = "BILLBOARD_FACE_CAMERA_POS";

            public const string ScreenCoordOverride = "SCREEN_COORD_OVERRIDE";
        }
        
        public static bool IsGameCamera(Camera camera)
        {
            if (camera == null)
                throw new ArgumentNullException("camera");

            return camera.cameraType == CameraType.Game || camera.cameraType == CameraType.VR;
        }

        public static UltraRenderPipelineAsset asset
        {
            get => GraphicsSettings.currentRenderPipeline as UltraRenderPipelineAsset;
        }

        Comparison<Camera> cameraComparison = (camera1, camera2) => { return (int)camera1.depth - (int)camera2.depth; };
#if UNITY_2021_1_OR_NEWER
        void SortCameras(List<Camera> cameras)
        {
            if (cameras.Count > 1)
                cameras.Sort(cameraComparison);
        }

#else
        void SortCameras(Camera[] cameras)
        {
            if (cameras.Length > 1)
                Array.Sort(cameras, cameraComparison);
        }

#endif

        private int GetLastBaseCameraIndex(List<Camera> cameras)
        {
            int lastBaseCameraIndex = 0;
            for (int i = 0; i < cameras.Count; i++)
            {
                cameras[i].TryGetComponent<UltraAdditionalCameraData>(out var baseCameraAdditionalData);
                if (baseCameraAdditionalData?.renderType == CameraRenderType.Base)
                    lastBaseCameraIndex = i;
            }
            return lastBaseCameraIndex;
        }

        internal static RenderTextureDescriptor CreateRenderTextureDescriptor(Camera camera, CameraData cameraData,
             bool isHdrEnabled, HDRColorBufferPrecision requestHDRColorBufferPrecision, int msaaSamples, bool needsAlpha)
        {
            RenderTextureDescriptor desc;

            if (camera.targetTexture == null)
            {
                desc = new RenderTextureDescriptor(cameraData.scaledWidth, cameraData.scaledHeight);
                desc.graphicsFormat = MakeRenderTextureGraphicsFormat(isHdrEnabled, requestHDRColorBufferPrecision, needsAlpha);
                desc.depthStencilFormat = SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil);
                desc.msaaSamples = msaaSamples;
                desc.sRGB = (QualitySettings.activeColorSpace == ColorSpace.Linear);
            }
            else
            {
                desc = camera.targetTexture.descriptor;
                desc.msaaSamples = msaaSamples;
                desc.width = cameraData.scaledWidth;
                desc.height = cameraData.scaledHeight;

                if (camera.cameraType == CameraType.SceneView && !isHdrEnabled)
                {
                    desc.graphicsFormat = SystemInfo.GetGraphicsFormat(DefaultFormat.LDR);
                }
                // SystemInfo.SupportsRenderTextureFormat(camera.targetTexture.descriptor.colorFormat)
                // will assert on R8_SINT since it isn't a valid value of RenderTextureFormat.
                // If this is fixed then we can implement debug statement to the user explaining why some
                // RenderTextureFormats available resolves in a black render texture when no warning or error
                // is given.
            }

            desc.enableRandomWrite = false;
            desc.bindMS = false;
            desc.useDynamicScale = camera.allowDynamicResolution;

            // check that the requested MSAA samples count is supported by the current platform. If it's not supported,
            // replace the requested desc.msaaSamples value with the actual value the engine falls back to
            desc.msaaSamples = SystemInfo.GetRenderTextureSupportedMSAASampleCount(desc);

            // if the target platform doesn't support storing multisampled RTs and we are doing any offscreen passes, using a Load load action on the subsequent passes
            // will result in loading Resolved data, which on some platforms is discarded, resulting in losing the results of the previous passes.
            // As a workaround we disable MSAA to make sure that the results of previous passes are stored. (fix for Case 1247423).
            if (!SystemInfo.supportsStoreAndResolveAction)
                desc.msaaSamples = 1;

            return desc;
        }

        private static Lightmapping.RequestLightsDelegate lightsDelegate = (Light[] requests, NativeArray<LightDataGI> lightsOutput) =>
        {
            LightDataGI lightData = new LightDataGI();
#if UNITY_EDITOR
            // Always extract lights in the Editor.
            for (int i = 0; i < requests.Length; i++)
            {
                Light light = requests[i];
                var additionalLightData = light.GetUltraAdditionalLightData();

                LightmapperUtils.Extract(light, out Cookie cookie);

                switch (light.type)
                {
                    case LightType.Directional:
                        DirectionalLight directionalLight = new DirectionalLight();
                        LightmapperUtils.Extract(light, ref directionalLight);

                        if (light.cookie != null)
                        {
                            // Size == 1 / Scale
                            cookie.sizes = additionalLightData.lightCookieSize;
                            // Offset, Map cookie UV offset to light position on along local axes.
                            if (additionalLightData.lightCookieOffset != Vector2.zero)
                            {
                                var r = light.transform.right * additionalLightData.lightCookieOffset.x;
                                var u = light.transform.up * additionalLightData.lightCookieOffset.y;
                                var offset = r + u;

                                directionalLight.position += offset;
                            }
                        }

                        lightData.Init(ref directionalLight, ref cookie);
                        break;
                    case LightType.Point:
                        PointLight pointLight = new PointLight();
                        LightmapperUtils.Extract(light, ref pointLight);
                        lightData.Init(ref pointLight, ref cookie);
                        break;
                    case LightType.Spot:
                        SpotLight spotLight = new SpotLight();
                        LightmapperUtils.Extract(light, ref spotLight);
                        spotLight.innerConeAngle = light.innerSpotAngle * Mathf.Deg2Rad;
                        spotLight.angularFalloff = AngularFalloffType.AnalyticAndInnerAngle;
                        lightData.Init(ref spotLight, ref cookie);
                        break;
                    case LightType.Rectangle:
                        RectangleLight rectangleLight = new RectangleLight();
                        LightmapperUtils.Extract(light, ref rectangleLight);
                        rectangleLight.mode = LightMode.Baked;
                        lightData.Init(ref rectangleLight);
                        break;
                    case LightType.Disc:
                        DiscLight discLight = new DiscLight();
                        LightmapperUtils.Extract(light, ref discLight);
                        discLight.mode = LightMode.Baked;
                        lightData.Init(ref discLight);
                        break;
                    default:
                        lightData.InitNoBake(light.GetInstanceID());
                        break;
                }

                lightData.falloff = FalloffType.InverseSquared;
                lightsOutput[i] = lightData;
            }
#else
            // If Enlighten realtime GI isn't active, we don't extract lights.
            if (SupportedRenderingFeatures.active.enlighten == false || ((int)SupportedRenderingFeatures.active.lightmapBakeTypes | (int)LightmapBakeType.Realtime) == 0)
            {
                for (int i = 0; i < requests.Length; i++)
                {
                    Light light = requests[i];
                    lightData.InitNoBake(light.GetInstanceID());
                    lightsOutput[i] = lightData;
                }
            }
            else
            {
                for (int i = 0; i < requests.Length; i++)
                {
                    Light light = requests[i];
                    switch (light.type)
                    {
                        case LightType.Directional:
                            DirectionalLight directionalLight = new DirectionalLight();
                            LightmapperUtils.Extract(light, ref directionalLight);
                            lightData.Init(ref directionalLight);
                            break;
                        case LightType.Point:
                            PointLight pointLight = new PointLight();
                            LightmapperUtils.Extract(light, ref pointLight);
                            lightData.Init(ref pointLight);
                            break;
                        case LightType.Spot:
                            SpotLight spotLight = new SpotLight();
                            LightmapperUtils.Extract(light, ref spotLight);
                            spotLight.innerConeAngle = light.innerSpotAngle * Mathf.Deg2Rad;
                            spotLight.angularFalloff = AngularFalloffType.AnalyticAndInnerAngle;
                            lightData.Init(ref spotLight);
                            break;
                        case LightType.Rectangle:
                            // Rect area light is baked only in UltraRP.
                            lightData.InitNoBake(light.GetInstanceID());
                            break;
                        case LightType.Disc:
                            // Disc light is baked only.
                            lightData.InitNoBake(light.GetInstanceID());
                            break;
                        default:
                            lightData.InitNoBake(light.GetInstanceID());
                            break;
                    }
                    lightData.falloff = FalloffType.InverseSquared;
                    lightsOutput[i] = lightData;
                }
            }
#endif
        };

        internal static GraphicsFormat MakeRenderTextureGraphicsFormat(bool isHdrEnabled, HDRColorBufferPrecision requestHDRColorBufferPrecision, bool needsAlpha)
        {
            if (isHdrEnabled)
            {
                // Match URP's HDR color-target requirement. Unity 2022 can report Linear
                // unsupported for packed HDR even when rendering, blending and filtering work.
                if (!needsAlpha && requestHDRColorBufferPrecision != HDRColorBufferPrecision._64Bits && RenderingUtils.SupportsGraphicsFormat(GraphicsFormat.B10G11R11_UFloatPack32, GraphicsFormatUsage.Blend))
                    return GraphicsFormat.B10G11R11_UFloatPack32;

                if (RenderingUtils.SupportsGraphicsFormat(GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormatUsage.Blend))
                    return GraphicsFormat.R16G16B16A16_SFloat;

                return SystemInfo.GetGraphicsFormat(DefaultFormat.HDR); // This might actually be a LDR format on old devices.
            }

            return SystemInfo.GetGraphicsFormat(DefaultFormat.LDR);
        }

        internal static GraphicsFormat MakeUnormRenderTextureGraphicsFormat()
        {
            if (RenderingUtils.SupportsGraphicsFormat(GraphicsFormat.A2B10G10R10_UNormPack32, GraphicsFormatUsage.Linear | GraphicsFormatUsage.Render))
                return GraphicsFormat.A2B10G10R10_UNormPack32;
            else
                return GraphicsFormat.R8G8B8A8_UNorm;
        }

        public enum UltraRPProfileId
        {
            // CPU
            UltraRenderPipelineTotal,
            UpdateVolumeFramework,
            RenderCameraStack,

            // GPU
            AdditionalLightsShadow,
            ColorGradingLUT,
            CopyColor,
            CopyDepth,
            DepthNormalPrepass,
            DepthPrepass,
            UpdateReflectionProbeAtlas,
            GenerateMipmaps,
            UpdateSkyAmbientProbe,
            UpdataSkyEnvConvolution,
            RenderSkyToCubemap,

            // DrawObjectsPass
            DrawOpaqueObjects,
            DrawTransparentObjects,
            DrawScreenSpaceUI,

            //Full Record Render Graph
            RecordRenderGraph,

            LightCookies,

            MainLightShadow,
            ResolveShadows,
            SSAO,

            // PostProcessPass
            LensFlareDataDrivenComputeOcclusion,
            LensFlareDataDriven,
            LensFlareScreenSpace,
            Bloom,

            // PostProcessPass RenderGraph
            [HideInDebugUI] RG_SetupPostFX,
            [HideInDebugUI] RG_StopNaNs,
            [HideInDebugUI] RG_SMAAMaterialSetup,
            [HideInDebugUI] RG_SMAAEdgeDetection,
            [HideInDebugUI] RG_SMAABlendWeight,
            [HideInDebugUI] RG_SMAANeighborhoodBlend,
            [HideInDebugUI] RG_SetupDoF,
            [HideInDebugUI] RG_DOFComputeCOC,
            [HideInDebugUI] RG_DOFDownscalePrefilter,
            [HideInDebugUI] RG_DOFBlurH,
            [HideInDebugUI] RG_DOFBlurV,
            [HideInDebugUI] RG_DOFBlurBokeh,
            [HideInDebugUI] RG_DOFPostFilter,
            [HideInDebugUI] RG_DOFComposite,
            [HideInDebugUI] RG_TAA,
            [HideInDebugUI] RG_TAACopyHistory,
            [HideInDebugUI] RG_MotionBlur,
            [HideInDebugUI] RG_BloomSetup,
            [HideInDebugUI] RG_BloomPrefilter,
            [HideInDebugUI] RG_BloomDownsample,
            [HideInDebugUI] RG_BloomUpsample,
            [HideInDebugUI] RG_UberPostSetupBloomPass,
            [HideInDebugUI] RG_UberPost,
            [HideInDebugUI] RG_FinalSetup,
            [HideInDebugUI] RG_FinalFSRScale,
            [HideInDebugUI] RG_FinalBlit,

            FinalBlit,
            
            // IBL Filter
            FilterCubemapGGX,

#if ENABLE_VIRTUALTEXTURES
            VTFeedbackDownsample,
#endif

            // GPU Lights
            GPULights
        }
    }

}
