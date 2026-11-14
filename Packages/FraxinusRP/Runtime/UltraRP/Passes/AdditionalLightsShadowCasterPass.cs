using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable]
    [NodeMenuItem("UltraRP/AdditionalLightsShadowCasterPass")]
    public class AdditionalLightsShadowCasterPass : ScriptableRenderPassNode
    {
        private static class AdditionalShadowsConstantBuffer
        {
            public static int _AdditionalLightsWorldToShadow = Shader.PropertyToID("_AdditionalLightsWorldToShadow");
            public static int _AdditionalShadowParams = Shader.PropertyToID("_AdditionalShadowParams");
            public static int _AdditionalShadowOffset0 = Shader.PropertyToID("_AdditionalShadowOffset0");
            public static int _AdditionalShadowOffset1 = Shader.PropertyToID("_AdditionalShadowOffset1");
            public static int _AdditionalShadowFadeParams = Shader.PropertyToID("_AdditionalShadowFadeParams");
            public static int _AdditionalShadowmapSize = Shader.PropertyToID("_AdditionalShadowmapSize");
            public static readonly int _AdditionalLightsShadowmapID = Shader.PropertyToID(k_additionalLightShadowMapTextureName);
            public static readonly int _AdditionalLightsWorldToShadow_SSBO = Shader.PropertyToID("_AdditionalLightsWorldToShadow_SSBO");
            public static readonly int _AdditionalShadowParams_SSBO = Shader.PropertyToID("_AdditionalShadowParams_SSBO");
        }

        private class PassData
        {
            internal int shadowmapID;
            internal bool emptyShadowmap;
            internal bool setKeywordForEmptyShadowmap;
            internal bool useStructuredBuffer;
            internal bool stripShadowsOffVariants;
            internal Matrix4x4 viewMatrix;
            internal Vector2Int allocatedShadowAtlasSize;
            internal TextureHandle shadowmapTexture;
            internal LightData lightData;
            internal ShadowData shadowData;
            internal AdditionalLightsShadowCasterPass pass;
            internal readonly RendererList[] shadowRendererLists = new RendererList[ShaderOptions.maxVisibleLightCountDesktop];
            internal readonly RendererListHandle[] shadowRendererListsHdl = new RendererListHandle[ShaderOptions.maxVisibleLightCountDesktop];
        }

        [Output("AdditionalLightShadow", allowMultiple = true)]
        public TextureHandleNodeParameter additionalLightShadow;

        // Internal
        internal RTHandle additionalLightsShadowmapHandle;

        // Private
        private int _renderTargetWidth;
        private int _renderTargetHeight;
        private bool _createEmptyShadowmap;
        private bool _setKeywordForEmptyShadowmap;
        private bool _issuedMessageAboutShadowSlicesTooMany;
        private bool _issuedMessageAboutShadowMapsRescale;
        private bool _issuedMessageAboutShadowMapsTooBig;
        private bool _issuedMessageAboutRemovedShadowSlices;
        private float _maxShadowDistanceSq;
        private float _cascadeBorder;
        private RTHandle _emptyAdditionalLightShadowmapTexture;
        private bool[] _visibleLightIndexToIsCastingShadows;                          // maps a "global" visible light index (index to lightData.visibleLights) to a shadow casting state (Is the light casting shadows or not?)
        private short[] _visibleLightIndexToAdditionalLightIndex;                     // maps a "global" visible light index (index to lightData.visibleLights) to an "additional light index" (index to arrays _AdditionalLightsPosition, _AdditionalShadowParams, ...), or -1 if it is not an additional light (i.e if it is the main light)
        private short[] _additionalLightIndexToVisibleLightIndex;                     // maps additional light index (index to arrays _AdditionalLightsPosition, _AdditionalShadowParams, ...) to its "global" visible light index (index to lightData.visibleLights)
        private Vector4[] _additionalLightIndexToShadowParams;                        // per-additional-light shadow info passed to the lighting shader (x: shadowStrength, y: softShadows, z: light type, w: perLightFirstShadowSliceIndex)
        private Matrix4x4[] _additionalLightShadowSliceIndexTo_WorldShadowMatrix;     // per-shadow-slice info passed to the lighting shader
        private ShadowSliceData[] _additionalLightsShadowSlices;
        private readonly List<byte> _globalShadowSliceIndexToPerLightShadowSliceIndex = new(); // For each shadow slice, store its "per-light shadow slice index" in the punctual light that casts it (can be up to 5 for point lights)
        private readonly List<short> _shadowSliceToAdditionalLightIndex = new();               // For each shadow slice, store the "additional light indices" of the punctual light that casts it
        private readonly Dictionary<int, ulong> _shadowRequestsHashes = new();                 // used to keep track of changes in the shadow requests and shadow atlas configuration (per camera)
        private readonly ProfilingSampler _profilingSetupSampler = new("Setup Additional Shadows");
        private RenderTextureDescriptor _additionalLightShadowDescriptor;
        private DeferredLights.GetShadowLightIndexDelegate _getShadowLightIndexDelegate;

        // Constants and Statics
        private static bool s_issuedMessageAboutPointLightHardShadowResolutionTooSmall;
        private static bool s_issuedMessageAboutPointLightSoftShadowResolutionTooSmall;
        private static bool s_useStructuredBuffer;

        private const int k_shadowmapBufferBits = 16;
        // Magic numbers used to identify light type when rendering shadow receiver.
        private const float k_lightTypeIdentifierInShadowParams_Spot = 0;
        private const float k_lightTypeIdentifierInShadowParams_Point = 1;
        private const string k_additionalLightShadowMapTextureName = "_AdditionalLightsShadowmapTexture";
        // x is used in RenderAdditionalShadowMapAtlas to skip shadow map rendering for non-shadow-casting lights.
        // w is perLightFirstShadowSliceIndex, used in Lighting shader to find if Additional light casts shadows.
        private static readonly Vector4 s_defaultShadowParams = new(0, 0, 0, -1);
        private static Vector4 s_emptyAdditionalShadowFadeParams;
        private static Vector4[] s_emptyAdditionalLightIndexToShadowParams;
        private static bool s_isAdditionalShadowParamsDirty;

        public AdditionalLightsShadowCasterPass()
        {
            base.profilingSampler = new ProfilingSampler(nameof(AdditionalLightsShadowCasterPass));
        }

        protected override void Enable()
        {
            base.Enable();

            _getShadowLightIndexDelegate = GetShadowLightIndexFromLightIndex;

            s_useStructuredBuffer = RenderingUtils.useStructuredBuffer;

            int maxVisibleAdditionalLights = UltraRenderPipeline.maxVisibleAdditionalLights;
            const int maxMainLights = 1;
            int maxVisibleLights = maxVisibleAdditionalLights + maxMainLights;
            int maxAdditionalLightShadowParams = s_useStructuredBuffer ? maxVisibleLights : Math.Min(maxVisibleLights, maxVisibleAdditionalLights);

            // These array sizes should be as big as ScriptableCullingParameters.maximumVisibleLights (that is defined during ScriptableRenderer.SetupCullingParameters).
            // We initialize these array sizes with the number of visible lights allowed by the UltraRenderer.
            // The number of visible lights can become much higher when using the Deferred rendering path, we resize the arrays during Setup() if required.
            _additionalLightIndexToVisibleLightIndex = new short[maxAdditionalLightShadowParams];
            _visibleLightIndexToAdditionalLightIndex = new short[maxVisibleLights];
            _visibleLightIndexToIsCastingShadows = new bool[maxVisibleLights];
            _additionalLightIndexToShadowParams = new Vector4[maxAdditionalLightShadowParams];

            s_emptyAdditionalLightIndexToShadowParams = new Vector4[maxAdditionalLightShadowParams];
            for (int i = 0; i < s_emptyAdditionalLightIndexToShadowParams.Length; i++)
                s_emptyAdditionalLightIndexToShadowParams[i] = s_defaultShadowParams;

            // Uniform buffers are faster on some platforms, but they have stricter size limitations
            if (!s_useStructuredBuffer)
                _additionalLightShadowSliceIndexTo_WorldShadowMatrix = new Matrix4x4[maxVisibleAdditionalLights];
        }

        protected override void Disable()
        {
            base.Disable();

            additionalLightsShadowmapHandle?.Release();
            _emptyAdditionalLightShadowmapTexture?.Release();
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            ConfigurePortValue(additionalLightShadow, Render(renderGraph, frameData));
        }

        // Keep the same shadow keyword and empty-map policy for depth-target cameras.
        internal TextureHandle Render(RenderGraph renderGraph, ContextContainer frameData)
        {
            ShadowData shadowData = frameData.Get<ShadowData>();
            CameraData cameraData = frameData.Get<CameraData>();
            LightData lightData = frameData.Get<LightData>();
            RenderingData renderingData = frameData.Get<RenderingData>();

            var renderer = cameraData.renderer;
            var deferredLights = renderer.GetModule<DeferredLights>();
            deferredLights.RegisterGetShadowLightIndexCallback(_getShadowLightIndexDelegate);

            if (Setup(renderingData, cameraData, lightData, shadowData))
            {
                TextureHandle shadowTexture;
                using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
                {
                    InitPassData(ref passData, cameraData, lightData, shadowData);
                    InitRendererLists(ref renderingData.cullResults, ref passData, default(ScriptableRenderContext), renderGraph);

                    if (!_createEmptyShadowmap)
                    {
                        for (int globalShadowSliceIndex = 0; globalShadowSliceIndex < _shadowSliceToAdditionalLightIndex.Count; ++globalShadowSliceIndex)
                            builder.UseRendererList(passData.shadowRendererListsHdl[globalShadowSliceIndex]);

                        shadowTexture = UltraRenderer.CreateRenderGraphTexture(renderGraph, _additionalLightShadowDescriptor, k_additionalLightShadowMapTextureName, true, ShadowUtils.forceShadowPointSampling ? FilterMode.Point : FilterMode.Bilinear);
                        builder.SetRenderAttachmentDepth(shadowTexture, AccessFlags.Write);
                    }
                    else
                        shadowTexture = renderGraph.defaultResources.defaultShadowTexture;

                    TextureDesc descriptor = shadowTexture.GetDescriptor(renderGraph);
                    passData.allocatedShadowAtlasSize = new Vector2Int(descriptor.width, descriptor.height);

                    builder.AllowGlobalStateModification(true);

                    if (shadowTexture.IsValid())
                        builder.SetGlobalTextureAfterPass(shadowTexture, AdditionalShadowsConstantBuffer._AdditionalLightsShadowmapID);

                    builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                    {
                        RasterCommandBuffer rasterCommandBuffer = context.cmd;
                        if (!data.emptyShadowmap)
                            data.pass.RenderAdditionalShadowmapAtlas(rasterCommandBuffer, ref data);
                        else
                        {
                            if (data.setKeywordForEmptyShadowmap)
                                rasterCommandBuffer.EnableKeyword(ShaderGlobalKeywords.AdditionalLightShadows);

                            SetShadowParamsForEmptyShadowmap(rasterCommandBuffer);
                        }
                    });

                }
                return shadowTexture;
            }
            return TextureHandle.nullHandle;
        }

        // Returns the guard angle that must be added to a frustum angle covering a projection map of resolution sliceResolutionInTexels,
        // in order to also cover a guard band of size guardBandSizeInTexels around the projection map.
        // Formula illustrated in https://i.ibb.co/wpW5Mnf/Calc-Guard-Angle.png
        internal static float CalcGuardAngle(float frustumAngleInDegrees, float guardBandSizeInTexels, float sliceResolutionInTexels)
        {
            float frustumAngle = frustumAngleInDegrees * Mathf.Deg2Rad;
            float halfFrustumAngle = frustumAngle / 2;
            float tanHalfFrustumAngle = Mathf.Tan(halfFrustumAngle);

            float halfSliceResolution = sliceResolutionInTexels / 2;
            float halfGuardBand = guardBandSizeInTexels / 2;
            float factorBetweenAngleTangents = 1 + halfGuardBand / halfSliceResolution;

            float tanHalfGuardAnglePlusHalfFrustumAngle = tanHalfFrustumAngle * factorBetweenAngleTangents;

            float halfGuardAnglePlusHalfFrustumAngle = Mathf.Atan(tanHalfGuardAnglePlusHalfFrustumAngle);
            float halfGuardAngleInRadian = halfGuardAnglePlusHalfFrustumAngle - halfFrustumAngle;

            float guardAngleInRadian = 2 * halfGuardAngleInRadian;
            float guardAngleInDegree = guardAngleInRadian * Mathf.Rad2Deg;

            return guardAngleInDegree;
        }

        // Returns the guard angle that must be added to a point light shadow face frustum angle
        // in order to avoid shadows missing at the boundaries between cube faces.
        internal static float GetPointLightShadowFrustumFovBiasInDegrees(int shadowSliceResolution, bool shadowFiltering)
        {
            // Commented-out code below uses the theoretical formula to compute the required guard angle based on the number of additional
            // texels that the projection should cover. It is close to HDRP's HDShadowUtils.CalcGuardAnglePerspective method.
            // However, due to precision issues or other filtering performed at lighting for example, this formula also still requires a fudge factor.
            // Since we only handle a fixed number of resolutions, we use empirical values instead.
#if false
            float fudgeFactor = 1.5f;
            return fudgeFactor * CalcGuardAngle(90, shadowFiltering ? 5 : 1, shadowSliceResolution);
#endif

            float fovBias = 4.00f;

            // Empirical value found to remove gaps between point light shadow faces in test scenes.
            // We can see that the guard angle is roughly proportional to the inverse of resolution https://docs.google.com/spreadsheets/d/1QrIZJn18LxVKq2-K1XS4EFRZcZdZOJTTKKhDN8Z1b_s
            if (shadowSliceResolution <= ShadowUtils.minimumPunctualLightHardShadowResolution)
            {
                if (!s_issuedMessageAboutPointLightHardShadowResolutionTooSmall)
                {
                    Debug.LogWarning("Too many additional punctual lights shadows, increase shadow atlas size or remove some shadowed lights");
                    s_issuedMessageAboutPointLightHardShadowResolutionTooSmall = true; // Only output this once per shadow requests configuration
                }
            }
            else if (shadowSliceResolution <= 16)
                fovBias = 43.0f;
            else if (shadowSliceResolution <= 32)
                fovBias = 18.55f;
            else if (shadowSliceResolution <= 64)
                fovBias = 8.63f;
            else if (shadowSliceResolution <= 128)
                fovBias = 4.13f;
            else if (shadowSliceResolution <= 256)
                fovBias = 2.03f;
            else if (shadowSliceResolution <= 512)
                fovBias = 1.00f;
            else if (shadowSliceResolution <= 1024)
                fovBias = 0.50f;
            else if (shadowSliceResolution <= 2048)
                fovBias = 0.25f;

            if (shadowFiltering)
            {
                if (shadowSliceResolution <= ShadowUtils.minimumPunctualLightHardShadowResolution)
                {
                    if (!s_issuedMessageAboutPointLightSoftShadowResolutionTooSmall)
                    {
                        Debug.LogWarning("Too many additional punctual lights shadows to use Soft Shadows. Increase shadow atlas size, remove some shadowed lights or use Hard Shadows.");
                        // With such small resolutions no fovBias can give good visual results
                        s_issuedMessageAboutPointLightSoftShadowResolutionTooSmall = true; // Only output this once per shadow requests configuration
                    }
                }
                else if (shadowSliceResolution <= 32)
                    fovBias += 9.35f;
                else if (shadowSliceResolution <= 64)
                    fovBias += 4.07f;
                else if (shadowSliceResolution <= 128)
                    fovBias += 1.77f;
                else if (shadowSliceResolution <= 256)
                    fovBias += 0.85f;
                else if (shadowSliceResolution <= 512)
                    fovBias += 0.39f;
                else if (shadowSliceResolution <= 1024)
                    fovBias += 0.17f;
                else if (shadowSliceResolution <= 2048)
                    fovBias += 0.074f;

                // These values were verified to work on untethered devices for which m_SupportsBoxFilterForShadows is true.
                // TODO: Investigate finer-tuned values for those platforms. Soft shadows are implemented differently for them.
            }

            return fovBias;
        }

        private ulong ResolutionLog2ForHash(int resolution)
        {
            switch (resolution)
            {
                case 4096: return 12;
                case 2048: return 11;
                case 1024: return 10;
                case 0512: return 09;
            }
            return 08;
        }

        private ulong ComputeShadowRequestHash(LightData lightData, ShadowData shadowData)
        {
            ulong numberOfShadowedPointLights = 0;
            ulong numberOfSoftShadowedLights = 0;
            ulong numberOfShadowsWithResolution0128 = 0;
            ulong numberOfShadowsWithResolution0256 = 0;
            ulong numberOfShadowsWithResolution0512 = 0;
            ulong numberOfShadowsWithResolution1024 = 0;
            ulong numberOfShadowsWithResolution2048 = 0;
            ulong numberOfShadowsWithResolution4096 = 0;

            var visibleLights = lightData.visibleLights;
            for (int visibleLightIndex = 0; visibleLightIndex < visibleLights.Length; ++visibleLightIndex)
            {
                ref VisibleLight vl = ref visibleLights.UnsafeElementAt(visibleLightIndex);
                Light light = vl.light;

                if (light == null || !ShadowUtils.IsValidShadowCastingLight(lightData, visibleLightIndex, vl.lightType, light.shadows, light.shadowStrength))
                    continue;

                switch (vl.lightType)
                {
                    case LightType.Spot: ++numberOfSoftShadowedLights; break;
                    case LightType.Point: ++numberOfShadowedPointLights; break;
                }

                int resolution = shadowData.resolution[visibleLightIndex];
                switch (resolution)
                {
                    case 0128: ++numberOfShadowsWithResolution0128; break;
                    case 0256: ++numberOfShadowsWithResolution0256; break;
                    case 0512: ++numberOfShadowsWithResolution0512; break;
                    case 1024: ++numberOfShadowsWithResolution1024; break;
                    case 2048: ++numberOfShadowsWithResolution2048; break;
                    case 4096: ++numberOfShadowsWithResolution4096; break;
                }
            }
            ulong shadowRequestsHash = ResolutionLog2ForHash(shadowData.additionalLightsShadowmapWidth) - 8; // bits [00~02]
            shadowRequestsHash |= numberOfShadowedPointLights << 03;        // bits [03~10]
            shadowRequestsHash |= numberOfSoftShadowedLights << 11;         // bits [11~18]
            shadowRequestsHash |= numberOfShadowsWithResolution0128 << 19;  // bits [19~26]
            shadowRequestsHash |= numberOfShadowsWithResolution0256 << 27;  // bits [27~34]
            shadowRequestsHash |= numberOfShadowsWithResolution0512 << 35;  // bits [35~42]
            shadowRequestsHash |= numberOfShadowsWithResolution1024 << 43;  // bits [43~49]
            shadowRequestsHash |= numberOfShadowsWithResolution2048 << 50;  // bits [50~56]
            shadowRequestsHash |= numberOfShadowsWithResolution4096 << 57;  // bits [57~63]
            return shadowRequestsHash;
        }

        private float GetLightTypeIdentifierForShadowParams(LightType lightType)
        {
            switch (lightType)
            {
                case LightType.Spot:
                    return k_lightTypeIdentifierInShadowParams_Spot;
                case LightType.Point:
                    return k_lightTypeIdentifierInShadowParams_Point;
                default:
                    return -1;
            }
        }

        private bool UsesBakedShadows(Light light)
        {
            return light.bakingOutput.lightmapBakeType != LightmapBakeType.Realtime;
        }


        public bool Setup(RenderingData renderingData, CameraData cameraData, LightData lightData,ShadowData shadowData)
        {
            using var profScope = new ProfilingScope(_profilingSetupSampler);

            bool shadowsEnabled = shadowData.additionalLightShadowsEnabled;
            if (!shadowsEnabled)
            {
                // If (realtime) shadows are disabled, but any additional light casts baked shadows, we need to do empty rendering to setup the _MainLightShadowParams uniform,
                // which is also used when sampling baked shadows. This allows for using baked shadows even when realtime shadows are completely disabled.
                if (AnyAdditionalLightHasMixedShadows(lightData))
                    return SetupForEmptyRendering(cameraData.renderer.stripShadowsOffVariants, shadowsEnabled, lightData, shadowData);

                return false;
            }

            if (!shadowData.supportsAdditionalLightShadows)
                return SetupForEmptyRendering(cameraData.renderer.stripShadowsOffVariants, shadowsEnabled, lightData, shadowData);

            Clear();

            _renderTargetWidth = shadowData.additionalLightsShadowmapWidth;
            _renderTargetHeight = shadowData.additionalLightsShadowmapHeight;

            var visibleLights = lightData.visibleLights;
            ref AdditionalLightsShadowAtlasLayout atlasLayout = ref shadowData.shadowAtlasLayout;

            // Check changes in the shadow requests and shadow atlas configuration - compute shadow request/configuration hash
            // Should only be done in the editor or development builds as computing the hash has a cost that's clearly visible when profiling.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!cameraData.isPreviewCamera)
            {
                ulong newShadowRequestHash = ComputeShadowRequestHash(lightData, shadowData);
                _shadowRequestsHashes.TryGetValue(cameraData.camera.GetHashCode(), out ulong oldShadowRequestHash);
                if (oldShadowRequestHash != newShadowRequestHash)
                {
                    _shadowRequestsHashes[cameraData.camera.GetHashCode()] = newShadowRequestHash;

                    // config changed ; reset error message flags as we might need to issue those messages again
                    s_issuedMessageAboutPointLightHardShadowResolutionTooSmall = false;
                    s_issuedMessageAboutPointLightSoftShadowResolutionTooSmall = false;
                    _issuedMessageAboutShadowMapsRescale = false;
                    _issuedMessageAboutShadowMapsTooBig = false;
                    _issuedMessageAboutShadowSlicesTooMany = false;
                    _issuedMessageAboutRemovedShadowSlices = false;
                }
            }
#endif

            if (_visibleLightIndexToAdditionalLightIndex.Length < visibleLights.Length)
            {
                // Array "visibleLights" is returned by ScriptableRenderContext.Cull()
                // The maximum number of "visibleLights" that ScriptableRenderContext.Cull() should return, is defined by parameter ScriptableCullingParameters.maximumVisibleLights
                // Ultra RP sets this "ScriptableCullingParameters.maximumVisibleLights" value during ScriptableRenderer.SetupCullingParameters.
                // When using Deferred rendering, it is possible to specify a very high number of visible lights.
                _visibleLightIndexToAdditionalLightIndex = new short[visibleLights.Length];
                _visibleLightIndexToIsCastingShadows = new bool[visibleLights.Length];
            }

            int maxAdditionalLightShadowParams = s_useStructuredBuffer ? visibleLights.Length : Math.Min(visibleLights.Length, UltraRenderPipeline.maxVisibleAdditionalLights);
            if (_additionalLightIndexToVisibleLightIndex.Length < maxAdditionalLightShadowParams)
            {
                _additionalLightIndexToVisibleLightIndex = new short[maxAdditionalLightShadowParams];
                _additionalLightIndexToShadowParams = new Vector4[maxAdditionalLightShadowParams];
            }

            int totalShadowSlicesCount = atlasLayout.GetTotalShadowSlicesCount();
            int totalShadowResolutionRequestCount = atlasLayout.GetTotalShadowResolutionRequestCount();
            int shadowSlicesScaleFactor = atlasLayout.GetShadowSlicesScaleFactor();
            bool hasTooManyShadowMaps = atlasLayout.HasTooManyShadowMaps();
            int atlasSize = atlasLayout.GetAtlasSize();

            if (totalShadowSlicesCount < totalShadowResolutionRequestCount)
            {
                if (!_issuedMessageAboutRemovedShadowSlices)
                {
                    string limit = !s_useStructuredBuffer && totalShadowResolutionRequestCount > UltraRenderPipeline.maxVisibleAdditionalLights
                        ? $"The current platform supports at most {UltraRenderPipeline.maxVisibleAdditionalLights} shadow slices; a point light requires 6 and a spot light requires 1. Reduce shadow-casting lights or use spot lights where appropriate. Increasing atlas resolution does not raise this slice limit."
                        : "Increase the shadow atlas resolution, lower per-light shadow resolutions, or reduce shadow-casting lights.";
                    Debug.LogWarning($"UltraRP allocated {totalShadowSlicesCount} of {totalShadowResolutionRequestCount} requested additional-light shadow slices in the {atlasSize}x{atlasSize} atlas. {limit}");
                    _issuedMessageAboutRemovedShadowSlices = true;  // Only output this once per shadow requests configuration
                }
            }

            if (!_issuedMessageAboutShadowMapsTooBig && hasTooManyShadowMaps)
            {
                Debug.LogWarning($"Too many additional punctual lights shadows. UltraRP tried reducing shadow resolutions by {shadowSlicesScaleFactor} but it was still too much. Increase shadow atlas size, decrease big shadow resolutions, or reduce the number of shadow maps active in the same frame (currently was {totalShadowSlicesCount}).");
                _issuedMessageAboutShadowMapsTooBig = true; // Only output this once per shadow requests configuration
            }

            if (!_issuedMessageAboutShadowMapsRescale && shadowSlicesScaleFactor > 1)
            {
                Debug.Log($"Reduced additional punctual light shadows resolution by {shadowSlicesScaleFactor} to make {totalShadowSlicesCount} shadow maps fit in the {atlasSize}x{atlasSize} shadow atlas. To avoid this, increase shadow atlas size, decrease big shadow resolutions, or reduce the number of shadow maps active in the same frame");
                _issuedMessageAboutShadowMapsRescale = true; // Only output this once per shadow requests configuration
            }

            if (_additionalLightsShadowSlices == null || _additionalLightsShadowSlices.Length < totalShadowSlicesCount)
                _additionalLightsShadowSlices = new ShadowSliceData[totalShadowSlicesCount];

            if (_additionalLightShadowSliceIndexTo_WorldShadowMatrix == null ||
                (s_useStructuredBuffer && (_additionalLightShadowSliceIndexTo_WorldShadowMatrix.Length < totalShadowSlicesCount)))   // m_AdditionalLightShadowSliceIndexTo_WorldShadowMatrix can be resized when using SSBO to pass shadow data (no size limitation)
                _additionalLightShadowSliceIndexTo_WorldShadowMatrix = new Matrix4x4[totalShadowSlicesCount];

            // initialize _AdditionalShadowParams
            for (int i = 0; i < maxAdditionalLightShadowParams; ++i)
                _additionalLightIndexToShadowParams[i] = s_defaultShadowParams;

            for (int i = 0; i < _visibleLightIndexToAdditionalLightIndex.Length; ++i)
            {
                _visibleLightIndexToAdditionalLightIndex[i] = -1;
                _visibleLightIndexToIsCastingShadows[i] = false;
            }

            short additionalLightCount = 0;
            short validShadowCastingLightsCount = 0;
            bool supportsSoftShadows = shadowData.supportsSoftShadows;
            UltraRenderer ultraRenderer = (UltraRenderer)cameraData.renderer;
            // todo: ���ж�
            bool isDeferred = false;
            bool shadowTransparentReceive = false;
            bool hasForwardShadowPass = !isDeferred || shadowTransparentReceive;
            for (int visibleLightIndex = 0; visibleLightIndex < visibleLights.Length; ++visibleLightIndex)
            {
                // Skip main directional light as it is not packed into the shadow atlas
                if (visibleLightIndex == lightData.mainLightIndex)
                    continue;

                short lightIndexToUse = !hasForwardShadowPass ? validShadowCastingLightsCount : additionalLightCount++;

                // We need to always set these indices, even if the light is not shadow casting or doesn't fit in the shadow slices (UUM-46577)
                _visibleLightIndexToAdditionalLightIndex[visibleLightIndex] = lightIndexToUse;

                // If we've reached the maximum amount of lights to support, we move on to the next light.
                if (lightIndexToUse >= _additionalLightIndexToVisibleLightIndex.Length)
                    continue;

                _additionalLightIndexToVisibleLightIndex[lightIndexToUse] = (short)visibleLightIndex;
                if (_shadowSliceToAdditionalLightIndex.Count >= totalShadowSlicesCount)
                    continue;

                ref VisibleLight visibleLight = ref visibleLights.UnsafeElementAt(visibleLightIndex);
                Light light = visibleLight.light;
                if (light == null)
                    continue;

                LightType lightType = visibleLight.lightType;
                bool usesBakedShadows = UsesBakedShadows(light);
                float lightTypeIdentifierForShadowParams = GetLightTypeIdentifierForShadowParams(lightType);
                int perLightShadowSlicesCount = ShadowUtils.GetPunctualLightShadowSlicesCount(lightType);
                bool isValidShadowCastingLight = ShadowUtils.IsValidShadowCastingLight(lightData, visibleLightIndex, visibleLight.lightType, light.shadows, light.shadowStrength);
                bool hasSpaceForLight = atlasLayout.HasSpaceForLight(visibleLightIndex);
                if (isValidShadowCastingLight && hasSpaceForLight &&
                    (_shadowSliceToAdditionalLightIndex.Count + perLightShadowSlicesCount) > totalShadowSlicesCount)
                {
                    if (!_issuedMessageAboutShadowSlicesTooMany)
                    {
                        // This case can especially happen in Deferred, where there can be a high number of visibleLights
                        Debug.Log($"There are too many shadowed additional punctual lights active at the same time, UltraRP will not render all the shadows. To ensure all shadows are rendered, reduce the number of shadowed additional lights in the scene ; make sure they are not active at the same time ; or replace point lights by spot lights (spot lights use less shadow maps than point lights).");
                        _issuedMessageAboutShadowSlicesTooMany = true; // Only output this once
                    }
                    continue;
                }

                float softShadows = ShadowUtils.SoftShadowQualityToShaderProperty(light, (supportsSoftShadows && light.shadows == LightShadows.Soft));
                int perLightFirstShadowSliceIndex = _shadowSliceToAdditionalLightIndex.Count; // shadowSliceIndex within the global array of all additional light shadow slices
                bool shouldAddLight = false;
                for (byte perLightShadowSlice = 0; perLightShadowSlice < perLightShadowSlicesCount; ++perLightShadowSlice)
                {
                    int globalShadowSliceIndex = _shadowSliceToAdditionalLightIndex.Count; // shadowSliceIndex within the global array of all additional light shadow slices

                    // We need to iterate the lights even though additional lights are disabled because
                    // cullResults.GetShadowCasterBounds() does the fence sync for the shadow culling jobs.
                    bool lightRangeContainsShadowCasters = renderingData.cullResults.GetShadowCasterBounds(visibleLightIndex, out Bounds _);
                    if (!shadowData.supportsAdditionalLightShadows || !isValidShadowCastingLight || !lightRangeContainsShadowCasters)
                    {
                        // Even though there are not real-time shadows, the lights might be using shadowmasks,
                        // which is why we need to update the shadow parameters, for example so shadow strength can be used.
                        // This check is to make sure we only update this for spot and point lights.
                        if (usesBakedShadows && lightTypeIdentifierForShadowParams > -1)
                        {
                            _additionalLightIndexToShadowParams[lightIndexToUse] = new Vector4(light.shadowStrength, softShadows, lightTypeIdentifierForShadowParams, lightIndexToUse);
                            _visibleLightIndexToIsCastingShadows[visibleLightIndex] = usesBakedShadows;
                        }
                        continue;
                    }

                    if (!hasSpaceForLight)
                    {
                        // We could not find place in the shadow atlas for shadow maps of this light.
                        // Skip it.
                    }
                    else if (lightType == LightType.Spot)
                    {
                        ref readonly UltraRPLightShadowCullingInfos shadowCullingInfos = ref shadowData.visibleLightsShadowCullingInfos.UnsafeElementAt(visibleLightIndex);
                        ref readonly ShadowSliceData sliceData = ref shadowCullingInfos.slices.UnsafeElementAt(0);

                        _additionalLightsShadowSlices[globalShadowSliceIndex].viewMatrix = sliceData.viewMatrix;
                        _additionalLightsShadowSlices[globalShadowSliceIndex].projectionMatrix = sliceData.projectionMatrix;
                        _additionalLightsShadowSlices[globalShadowSliceIndex].splitData = sliceData.splitData;

                        if (shadowCullingInfos.IsSliceValid(0))
                        {
                            _shadowSliceToAdditionalLightIndex.Add(lightIndexToUse);
                            _globalShadowSliceIndexToPerLightShadowSliceIndex.Add(perLightShadowSlice);
                            _additionalLightShadowSliceIndexTo_WorldShadowMatrix[globalShadowSliceIndex] = sliceData.shadowTransform;
                            _additionalLightIndexToShadowParams[lightIndexToUse] = new Vector4(light.shadowStrength, softShadows, lightTypeIdentifierForShadowParams, perLightFirstShadowSliceIndex);
                            shouldAddLight = true;
                        }
                    }
                    else if (lightType == LightType.Point)
                    {
                        ref readonly UltraRPLightShadowCullingInfos shadowCullingInfos = ref shadowData.visibleLightsShadowCullingInfos.UnsafeElementAt(visibleLightIndex);
                        ref readonly ShadowSliceData sliceData = ref shadowCullingInfos.slices.UnsafeElementAt(perLightShadowSlice);

                        _additionalLightsShadowSlices[globalShadowSliceIndex].viewMatrix = sliceData.viewMatrix;
                        _additionalLightsShadowSlices[globalShadowSliceIndex].projectionMatrix = sliceData.projectionMatrix;
                        _additionalLightsShadowSlices[globalShadowSliceIndex].splitData = sliceData.splitData;

                        if (shadowCullingInfos.IsSliceValid(perLightShadowSlice))
                        {
                            _shadowSliceToAdditionalLightIndex.Add(lightIndexToUse);
                            _globalShadowSliceIndexToPerLightShadowSliceIndex.Add(perLightShadowSlice);
                            _additionalLightShadowSliceIndexTo_WorldShadowMatrix[globalShadowSliceIndex] = sliceData.shadowTransform;
                            _additionalLightIndexToShadowParams[lightIndexToUse] = new Vector4(light.shadowStrength, softShadows, lightTypeIdentifierForShadowParams, perLightFirstShadowSliceIndex);
                            shouldAddLight = true;
                        }
                    }
                }

                if (shouldAddLight)
                {
                    _visibleLightIndexToIsCastingShadows[visibleLightIndex] = true;
                    _visibleLightIndexToAdditionalLightIndex[visibleLightIndex] = lightIndexToUse;
                    _additionalLightIndexToVisibleLightIndex[lightIndexToUse] = (short)visibleLightIndex;
                    validShadowCastingLightsCount++;
                }
                else
                {
                    _visibleLightIndexToIsCastingShadows[visibleLightIndex] = usesBakedShadows;
                    _additionalLightIndexToShadowParams[lightIndexToUse] = new Vector4(light.shadowStrength, softShadows, lightTypeIdentifierForShadowParams, s_defaultShadowParams.w);
                }
            }

            // Lights that need to be rendered in the shadow map atlas
            if (validShadowCastingLightsCount == 0)
                return SetupForEmptyRendering(cameraData.renderer.stripShadowsOffVariants, shadowsEnabled, lightData, shadowData);

            int shadowCastingLightsBufferCount = _shadowSliceToAdditionalLightIndex.Count;

            // Trim shadow atlas dimensions if possible (to avoid allocating texture space that will not be used)
            int atlasMaxX = 0;
            int atlasMaxY = 0;
            for (int sortedShadowResolutionRequestIndex = 0; sortedShadowResolutionRequestIndex < totalShadowSlicesCount; ++sortedShadowResolutionRequestIndex)
            {
                var shadowResolutionRequest = atlasLayout.GetSortedShadowResolutionRequest(sortedShadowResolutionRequestIndex);
                atlasMaxX = Mathf.Max(atlasMaxX, shadowResolutionRequest.offsetX + shadowResolutionRequest.allocatedResolution);
                atlasMaxY = Mathf.Max(atlasMaxY, shadowResolutionRequest.offsetY + shadowResolutionRequest.allocatedResolution);
            }
            // ...but make sure we still use power-of-two dimensions (might perform better on some hardware)

            _renderTargetWidth = Mathf.NextPowerOfTwo(atlasMaxX);
            _renderTargetHeight = Mathf.NextPowerOfTwo(atlasMaxY);

            float oneOverAtlasWidth = 1.0f / _renderTargetWidth;
            float oneOverAtlasHeight = 1.0f / _renderTargetHeight;

            for (int globalShadowSliceIndex = 0; globalShadowSliceIndex < shadowCastingLightsBufferCount; ++globalShadowSliceIndex)
            {
                int additionalLightIndex = _shadowSliceToAdditionalLightIndex[globalShadowSliceIndex];

                // We can skip the slice if strength is zero.
                if (Mathf.Approximately(_additionalLightIndexToShadowParams[additionalLightIndex].x, 0.0f) || Mathf.Approximately(_additionalLightIndexToShadowParams[additionalLightIndex].w, -1.0f))
                    continue;

                int visibleLightIndex = _additionalLightIndexToVisibleLightIndex[additionalLightIndex];
                int perLightSliceIndex = _globalShadowSliceIndexToPerLightShadowSliceIndex[globalShadowSliceIndex];
                var shadowResolutionRequest = atlasLayout.GetSliceShadowResolutionRequest(visibleLightIndex, perLightSliceIndex);
                int sliceResolution = shadowResolutionRequest.allocatedResolution;

                Matrix4x4 sliceTransform = Matrix4x4.identity;
                sliceTransform.m00 = sliceResolution * oneOverAtlasWidth;
                sliceTransform.m11 = sliceResolution * oneOverAtlasHeight;

                _additionalLightsShadowSlices[globalShadowSliceIndex].offsetX = shadowResolutionRequest.offsetX;
                _additionalLightsShadowSlices[globalShadowSliceIndex].offsetY = shadowResolutionRequest.offsetY;
                _additionalLightsShadowSlices[globalShadowSliceIndex].resolution = sliceResolution;

                sliceTransform.m03 = _additionalLightsShadowSlices[globalShadowSliceIndex].offsetX * oneOverAtlasWidth;
                sliceTransform.m13 = _additionalLightsShadowSlices[globalShadowSliceIndex].offsetY * oneOverAtlasHeight;

                // We bake scale and bias to each shadow map in the atlas in the matrix.
                // saves some instructions in shader.
                _additionalLightShadowSliceIndexTo_WorldShadowMatrix[globalShadowSliceIndex] = sliceTransform * _additionalLightShadowSliceIndexTo_WorldShadowMatrix[globalShadowSliceIndex];
            }

            UpdateTextureDescriptorIfNeeded();

            _maxShadowDistanceSq = cameraData.maxShadowDistance * cameraData.maxShadowDistance;
            _cascadeBorder = shadowData.mainLightShadowCascadeBorder;
            _createEmptyShadowmap = false;

            return true;
        }

        private void UpdateTextureDescriptorIfNeeded()
        {
            if (_additionalLightShadowDescriptor.width != _renderTargetWidth
                || _additionalLightShadowDescriptor.height != _renderTargetHeight
                || _additionalLightShadowDescriptor.depthBufferBits != k_shadowmapBufferBits
                || _additionalLightShadowDescriptor.colorFormat != RenderTextureFormat.Shadowmap)
            {
                _additionalLightShadowDescriptor = new RenderTextureDescriptor(_renderTargetWidth, _renderTargetHeight, RenderTextureFormat.Shadowmap, k_shadowmapBufferBits);
            }
        }

        bool AnyAdditionalLightHasMixedShadows(LightData lightData)
        {
            for (int visibleLightIndex = 0; visibleLightIndex < lightData.visibleLights.Length; ++visibleLightIndex)
            {
                if (visibleLightIndex == lightData.mainLightIndex)
                {
                    continue;
                }

                Light light = lightData.visibleLights[visibleLightIndex].light;
                if (light.shadows != LightShadows.None &&
                    light.bakingOutput.isBaked &&
                    light.bakingOutput.mixedLightingMode != MixedLightingMode.IndirectOnly &&
                    light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed)
                {
                    return true;
                }
            }

            return false;
        }

        bool SetupForEmptyRendering(bool stripShadowsOffVariants, bool shadowsEnabled, LightData lightData, ShadowData shadowData)
        {
            if (!stripShadowsOffVariants)
                return false;

            shadowData.isKeywordAdditionalLightShadowsEnabled = true;
            _createEmptyShadowmap = true;

            _setKeywordForEmptyShadowmap = shadowsEnabled;

            // Even though there are not real-time shadows, the lights might be using shadowmasks,
            // which is why we need to update the shadow parameters, for example so shadow strength can be used.

            // _AdditionalShadowFadeParams
            ShadowUtils.GetScaleAndBiasForLinearDistanceFade(_maxShadowDistanceSq, _cascadeBorder, out float shadowFadeScale, out float shadowFadeBias);
            s_emptyAdditionalShadowFadeParams = new Vector4(shadowFadeScale, shadowFadeBias, 0, 0);

            var visibleLights = lightData.visibleLights;
            if (s_emptyAdditionalLightIndexToShadowParams.Length < visibleLights.Length)
            {
                _visibleLightIndexToAdditionalLightIndex = new short[visibleLights.Length];
                _visibleLightIndexToIsCastingShadows = new bool[visibleLights.Length];
                s_emptyAdditionalLightIndexToShadowParams = new Vector4[visibleLights.Length];
                s_isAdditionalShadowParamsDirty = true;
            }

            // Temporarily we are avoiding SetGlobalVectorArray array for _AdditionalShadowParams if we exceeds maximum additional lights (UUM-102023).
            if (s_isAdditionalShadowParamsDirty)
            {
                s_isAdditionalShadowParamsDirty = false;
                Debug.LogWarning($"The number of visible additional lights {visibleLights.Length} exceeds the maximum supported lights {UltraRenderPipeline.maxVisibleAdditionalLights}." +
                    $" Please refer UltraRP documentation to change maximum number of visible lights or" +
                    $" reduce the number of lights to maximum allowed additional lights.");
            }

            // Initialize _AdditionalShadowParams
            short additionalLightCount = 0;
            for (int visibleLightIndex = 0; visibleLightIndex < visibleLights.Length; ++visibleLightIndex)
            {
                // Skip main directional light as it is not packed into the shadow atlas
                if (visibleLightIndex == lightData.mainLightIndex)
                    continue;

                // Match ForwardLights and the normal atlas setup: every non-main
                // visible light owns a slot, including unsupported rectangle lights.
                short lightIndexToUse = additionalLightCount++;
                s_emptyAdditionalLightIndexToShadowParams[lightIndexToUse] = s_defaultShadowParams;
                _visibleLightIndexToAdditionalLightIndex[visibleLightIndex] = -1;
                _visibleLightIndexToIsCastingShadows[visibleLightIndex] = false;
                ref VisibleLight visibleLight = ref visibleLights.UnsafeElementAt(visibleLightIndex);
                Light light = visibleLight.light;
                if (light == null)
                    continue;

                // Only want to update this for spot and point lights
                float lightType = GetLightTypeIdentifierForShadowParams(light.type);
                if (lightType < 0f)
                    continue;

                LightShadows shadows = light.shadows;
                bool lightHasShadows = shadows != LightShadows.None;
                if (lightHasShadows)
                {
                    bool lightHasSoftShadows = shadows != LightShadows.Soft;
                    bool supportsSoftShadows = shadowData.supportsSoftShadows;
                    float softShadows = ShadowUtils.SoftShadowQualityToShaderProperty(light, (supportsSoftShadows && lightHasSoftShadows));
                    s_emptyAdditionalLightIndexToShadowParams[lightIndexToUse] = new Vector4(light.shadowStrength, softShadows, lightType, lightIndexToUse);
                }
                else
                {
                    s_emptyAdditionalLightIndexToShadowParams[lightIndexToUse] = s_defaultShadowParams;
                }

                _visibleLightIndexToAdditionalLightIndex[visibleLightIndex] = lightIndexToUse;
                _visibleLightIndexToIsCastingShadows[visibleLightIndex] = UsesBakedShadows(light);
            }

            return true;
        }

        void Clear()
        {
            _shadowSliceToAdditionalLightIndex.Clear();
            _globalShadowSliceIndexToPerLightShadowSliceIndex.Clear();
        }

        private void InitPassData(ref PassData passData, CameraData cameraData, LightData lightData, ShadowData shadowData)
        {
            passData.pass = this;

            passData.lightData = lightData;
            passData.shadowData = shadowData;
            passData.viewMatrix = cameraData.GetViewMatrix();
            passData.stripShadowsOffVariants = cameraData.renderer.stripShadowsOffVariants;

            passData.emptyShadowmap = _createEmptyShadowmap;
            passData.setKeywordForEmptyShadowmap = _setKeywordForEmptyShadowmap;
            passData.useStructuredBuffer = s_useStructuredBuffer;
        }

        private void InitRendererLists(ref CullingResults cullResults, ref PassData passData, ScriptableRenderContext context, RenderGraph renderGraph)
        {
            if (_createEmptyShadowmap)
                return;

            for (int globalShadowSliceIndex = 0; globalShadowSliceIndex < _shadowSliceToAdditionalLightIndex.Count; ++globalShadowSliceIndex)
            {
                int additionalLightIndex = _shadowSliceToAdditionalLightIndex[globalShadowSliceIndex];
                int visibleLightIndex = _additionalLightIndexToVisibleLightIndex[additionalLightIndex];

                ShadowDrawingSettings settings = new(cullResults, visibleLightIndex, BatchCullingProjectionType.Perspective)
                {
                    useRenderingLayerMaskTest = UltraRenderPipeline.asset.useRenderingLayers,
                    splitData = _additionalLightsShadowSlices[globalShadowSliceIndex].splitData
                };

                passData.shadowRendererListsHdl[globalShadowSliceIndex] = renderGraph.CreateShadowRendererList(ref settings);
            }
        }

        internal static void SetShadowParamsForEmptyShadowmap(RasterCommandBuffer rasterCommandBuffer)
        {
            rasterCommandBuffer.SetGlobalVector(AdditionalShadowsConstantBuffer._AdditionalShadowFadeParams, s_emptyAdditionalShadowFadeParams);

            if (RenderingUtils.useStructuredBuffer)
            {
                ComputeBuffer shadowParamsBuffer = ShaderData.instance.GetAdditionalLightShadowParamsStructuredBuffer(s_emptyAdditionalLightIndexToShadowParams.Length);
                shadowParamsBuffer.SetData(s_emptyAdditionalLightIndexToShadowParams);
                rasterCommandBuffer.SetGlobalBuffer(AdditionalShadowsConstantBuffer._AdditionalShadowParams_SSBO, shadowParamsBuffer);
            }
            else if (s_emptyAdditionalLightIndexToShadowParams.Length <= UltraRenderPipeline.maxVisibleAdditionalLights)
                rasterCommandBuffer.SetGlobalVectorArray(AdditionalShadowsConstantBuffer._AdditionalShadowParams, s_emptyAdditionalLightIndexToShadowParams);
        }

        private void RenderAdditionalShadowmapAtlas(RasterCommandBuffer cmd, ref PassData data)
        {
            NativeArray<VisibleLight> visibleLights = data.lightData.visibleLights;

            bool additionalLightHasSoftShadows = false;

            using (new ProfilingScope(cmd, ProfilingSampler.Get(UltraRPProfileId.AdditionalLightsShadow)))
            {
                bool anyShadowSliceRenderer = false;
                int shadowSlicesCount = _shadowSliceToAdditionalLightIndex.Count;
                if (shadowSlicesCount > 0)
                    cmd.SetKeyword(ShaderGlobalKeywords.CastingPunctualLightShadow, true);

                Vector4 lastShadowBias = new Vector4(-10f, -10f, -10f, -10f);
                for (int globalShadowSliceIndex = 0; globalShadowSliceIndex < shadowSlicesCount; ++globalShadowSliceIndex)
                {
                    int additionalLightIndex = _shadowSliceToAdditionalLightIndex[globalShadowSliceIndex];

                    // we do the shadow strength check here again here because we might have zero strength for non-shadow-casting lights.
                    // In that case we need the shadow data buffer but we can skip rendering them to shadowmap.
                    if (ShadowUtils.FastApproximately(_additionalLightIndexToShadowParams[additionalLightIndex].x, 0.0f)
                        || ShadowUtils.FastApproximately(_additionalLightIndexToShadowParams[additionalLightIndex].w, -1.0f))
                        continue;

                    int visibleLightIndex = _additionalLightIndexToVisibleLightIndex[additionalLightIndex];
                    ref VisibleLight shadowLight = ref visibleLights.UnsafeElementAt(visibleLightIndex);
                    ShadowSliceData shadowSliceData = _additionalLightsShadowSlices[globalShadowSliceIndex];
                    Vector4 shadowBias = ShadowUtils.GetShadowBias(ref shadowLight, visibleLightIndex, data.shadowData, shadowSliceData.projectionMatrix, shadowSliceData.resolution);

                    // Update the bias when rendering the first slice or when the bias has changed
                    if (globalShadowSliceIndex == 0 || !ShadowUtils.FastApproximately(shadowBias, lastShadowBias))
                    {
                        ShadowUtils.SetShadowBias(cmd, shadowBias);
                        lastShadowBias = shadowBias;
                    }

                    // Update light position
                    Vector3 lightPosition = shadowLight.localToWorldMatrix.GetColumn(3);
                    ShadowUtils.SetLightPosition(cmd, lightPosition);

                    // Note: _LightDirection is not updated for additional lights.
                    // For Directional lights, _LightDirection is used when applying shadow Normal Bias.
                    // For Spot lights and Point lights _LightPosition is used to compute the actual light direction because it is different at each shadow caster geometry vertex.

                    RendererList shadowRendererList = data.shadowRendererListsHdl[globalShadowSliceIndex];
                    ShadowUtils.RenderShadowSlice(cmd, ref shadowSliceData, ref shadowRendererList, shadowSliceData.projectionMatrix, shadowSliceData.viewMatrix);
                    additionalLightHasSoftShadows |= shadowLight.light.shadows == LightShadows.Soft;
                    anyShadowSliceRenderer = true;

                    // We share soft shadow settings for main light and additional lights to save keywords.
                    // So we check here if pipeline supports soft shadows and either main light or any additional light has soft shadows
                    // to enable the keyword.
                    // TODO: In PC and Consoles we can upload shadow data per light and branch on shader. That will be more likely way faster.
                    bool mainLightHasSoftShadows = data.shadowData.supportsMainLightShadows &&
                                                   data.lightData.mainLightIndex != -1 &&
                                                   visibleLights[data.lightData.mainLightIndex].light.shadows == LightShadows.Soft;

                    // If the OFF variant has been stripped, the additional light shadows keyword must always be enabled
                    bool hasOffVariant = !data.stripShadowsOffVariants;
                    data.shadowData.isKeywordAdditionalLightShadowsEnabled = !hasOffVariant || anyShadowSliceRenderer;
                    cmd.SetKeyword(ShaderGlobalKeywords.AdditionalLightShadows, data.shadowData.isKeywordAdditionalLightShadowsEnabled);

                    bool softShadows = data.shadowData.supportsSoftShadows && (mainLightHasSoftShadows || additionalLightHasSoftShadows);
                    data.shadowData.isKeywordSoftShadowsEnabled = softShadows;
                    ShadowUtils.SetSoftShadowQualityShaderKeywords(cmd, data.shadowData);

                    if (anyShadowSliceRenderer)
                        SetupAdditionalLightsShadowReceiverConstants(cmd, data.allocatedShadowAtlasSize, data.useStructuredBuffer, softShadows);
                }
            }
        }

        // Set constant buffer data that will be used during the lighting/shadowing pass
        private void SetupAdditionalLightsShadowReceiverConstants(RasterCommandBuffer cmd, Vector2Int allocatedShadowAtlasSize, bool useStructuredBuffer, bool softShadows)
        {
            if (useStructuredBuffer)
            {
                // per-light data
                var shadowParamsBuffer = ShaderData.instance.GetAdditionalLightShadowParamsStructuredBuffer(_additionalLightIndexToShadowParams.Length);
                shadowParamsBuffer.SetData(_additionalLightIndexToShadowParams);
                cmd.SetGlobalBuffer(AdditionalShadowsConstantBuffer._AdditionalShadowParams_SSBO, shadowParamsBuffer);

                // per-shadow-slice data
                var shadowSliceMatricesBuffer = ShaderData.instance.GetAdditionalLightShadowSliceMatricesStructuredBuffer(_additionalLightShadowSliceIndexTo_WorldShadowMatrix.Length);
                shadowSliceMatricesBuffer.SetData(_additionalLightShadowSliceIndexTo_WorldShadowMatrix);
                cmd.SetGlobalBuffer(AdditionalShadowsConstantBuffer._AdditionalLightsWorldToShadow_SSBO, shadowSliceMatricesBuffer);
            }
            else
            {
                cmd.SetGlobalVectorArray(AdditionalShadowsConstantBuffer._AdditionalShadowParams, _additionalLightIndexToShadowParams);                         // per-light data
                cmd.SetGlobalMatrixArray(AdditionalShadowsConstantBuffer._AdditionalLightsWorldToShadow, _additionalLightShadowSliceIndexTo_WorldShadowMatrix); // per-shadow-slice data
            }

            ShadowUtils.GetScaleAndBiasForLinearDistanceFade(_maxShadowDistanceSq, _cascadeBorder, out float shadowFadeScale, out float shadowFadeBias);
            cmd.SetGlobalVector(AdditionalShadowsConstantBuffer._AdditionalShadowFadeParams, new Vector4(shadowFadeScale, shadowFadeBias, 0, 0));

            if (softShadows)
            {
                Vector2 invShadowAtlasSize = Vector2.one / allocatedShadowAtlasSize;
                Vector2 invHalfShadowAtlasSize = invShadowAtlasSize * 0.5f;

                cmd.SetGlobalVector(AdditionalShadowsConstantBuffer._AdditionalShadowOffset0,
                        new Vector4(-invHalfShadowAtlasSize.x, -invHalfShadowAtlasSize.y,
                            invHalfShadowAtlasSize.x, -invHalfShadowAtlasSize.y));
                cmd.SetGlobalVector(AdditionalShadowsConstantBuffer._AdditionalShadowOffset1,
                        new Vector4(-invHalfShadowAtlasSize.x, invHalfShadowAtlasSize.y,
                            invHalfShadowAtlasSize.x, invHalfShadowAtlasSize.y));

                cmd.SetGlobalVector(AdditionalShadowsConstantBuffer._AdditionalShadowmapSize, new Vector4(invShadowAtlasSize.x, invShadowAtlasSize.y,
                    allocatedShadowAtlasSize.x, allocatedShadowAtlasSize.y));
            }
        }

        public int GetShadowLightIndexFromLightIndex(int visibleLightIndex)
        {
            if (visibleLightIndex < 0 || visibleLightIndex >= _visibleLightIndexToAdditionalLightIndex.Length || !_visibleLightIndexToIsCastingShadows[visibleLightIndex])
                return -1;

            return _visibleLightIndexToAdditionalLightIndex[visibleLightIndex];
        }
    }
}
