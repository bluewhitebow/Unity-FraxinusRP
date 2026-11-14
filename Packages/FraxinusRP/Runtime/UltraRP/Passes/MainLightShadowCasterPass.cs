using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable]
    [NodeMenuItem("UltraRP/MainLightShadowCasterPass")]
    public class MainLightShadowCasterPass : ScriptableRenderPassNode
    {
        private static class MainLightShadowConstantBuffer
        {
            public static int _WorldToShadow = Shader.PropertyToID("_MainLightWorldToShadow");
            public static int _ShadowParams = Shader.PropertyToID("_MainLightShadowParams");
            public static int _CascadeShadowSplitSpheres0 = Shader.PropertyToID("_CascadeShadowSplitSpheres0");
            public static int _CascadeShadowSplitSpheres1 = Shader.PropertyToID("_CascadeShadowSplitSpheres1");
            public static int _CascadeShadowSplitSpheres2 = Shader.PropertyToID("_CascadeShadowSplitSpheres2");
            public static int _CascadeShadowSplitSpheres3 = Shader.PropertyToID("_CascadeShadowSplitSpheres3");
            public static int _CascadeShadowSplitSphereRadii = Shader.PropertyToID("_CascadeShadowSplitSphereRadii");
            public static int _ShadowOffset0 = Shader.PropertyToID("_MainLightShadowOffset0");
            public static int _ShadowOffset1 = Shader.PropertyToID("_MainLightShadowOffset1");
            public static int _ShadowmapSize = Shader.PropertyToID("_MainLightShadowmapSize");
            public static readonly int _MainLightShadowmapID = Shader.PropertyToID(k_mainLightShadowMapTextureName);
        }

        private class PassData
        {
            internal bool emptyShadowmap;
            internal bool setKeywordForEmptyShadowmap;
            internal RenderingData renderingData;
            internal CameraData cameraData;
            internal LightData lightData;
            internal ShadowData shadowData;
            internal MainLightShadowCasterPass pass;
            internal TextureHandle shadowmapTexture;
            internal readonly RendererList[] shadowRendererLists = new RendererList[k_maxCascades];
            internal readonly RendererListHandle[] shadowRendererListsHandle = new RendererListHandle[k_maxCascades];
        }

        [Output("MainLightShadow", allowMultiple = true)]
        public TextureHandleNodeParameter mainLightShadow;

        // Internal
        internal RTHandle mainLightShadowmapTexture;

        // Private
        private int _renderTargetWidth;
        private int _renderTargetHeight;
        private int _shadowCasterCascadesCount;
        private bool _createEmptyShadowmap;
        private bool _setKeywordForEmptyShadowmap;
        private float _cascadeBorder;
        private float _maxShadowDistanceSq;
        private RTHandle _emptyMainLightShadowmapTexture;
        private RenderTextureDescriptor _mainLightShadowDescriptor;
        private readonly Vector4[] _cascadeSplitDistances;
        private readonly Matrix4x4[] _mainLightShadowMatrices;
        private readonly ProfilingSampler _profilingSetupSampler = new("Setup Main Shadowmap");
        private readonly ShadowSliceData[] _cascadeSlices;

        // Constants and Statics
        private const int k_emptyShadowMapDimensions = 1;
        private const int k_maxCascades = 4;
        private const int k_shadowmapBufferBits = 16;
        private const string k_mainLightShadowMapTextureName = "_MainLightShadowmapTexture";
        private const string k_emptyMainLightShadowMapTextureName = "_EmptyMainLightShadowmapTexture";
        private static Vector4 s_emptyShadowParams = new(0f, 0f, 1f, 0f);
        private static readonly Vector4 s_emptyShadowmapSize = new(k_emptyShadowMapDimensions, 1f / k_emptyShadowMapDimensions, k_emptyShadowMapDimensions, k_emptyShadowMapDimensions);


        public MainLightShadowCasterPass() : base()
        {
            base.profilingSampler = new ProfilingSampler(nameof(MainLightShadowCasterPass));

            _mainLightShadowMatrices = new Matrix4x4[k_maxCascades + 1];
            _cascadeSlices = new ShadowSliceData[k_maxCascades];
            _cascadeSplitDistances = new Vector4[k_maxCascades];
        }

        protected override void Disable()
        {
            base.Disable();

            mainLightShadowmapTexture?.Release();
            _emptyMainLightShadowmapTexture?.Release();
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            ConfigurePortValue(mainLightShadow, Render(renderGraph, frameData));
        }

        // Also used by depth-target cameras, which draw material Forward passes
        // without executing the renderer graph's nodes.
        internal TextureHandle Render(RenderGraph renderGraph, ContextContainer frameData)
        {
            ShadowData shadowData = frameData.Get<ShadowData>();
            CameraData cameraData = frameData.Get<CameraData>();
            LightData lightData = frameData.Get<LightData>();
            RenderingData renderingData = frameData.Get<RenderingData>();

            if (Setup(shadowData, cameraData, lightData, renderingData))
            {
                TextureHandle shadowTexture;

                using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
                {
                    InitPassData(ref passData, renderingData, cameraData, lightData, shadowData);
                    InitRendererLists(ref passData, default(ScriptableRenderContext), renderGraph, true);

                    if (!_createEmptyShadowmap)
                    {
                        for (int cascadeIndex = 0; cascadeIndex < _shadowCasterCascadesCount; ++cascadeIndex)
                            builder.UseRendererList(passData.shadowRendererListsHandle[cascadeIndex]);

                        shadowTexture = UltraRenderer.CreateRenderGraphTexture(renderGraph, _mainLightShadowDescriptor, k_mainLightShadowMapTextureName, true, ShadowUtils.forceShadowPointSampling ? FilterMode.Point : FilterMode.Bilinear);
                        builder.SetRenderAttachmentDepth(shadowTexture, AccessFlags.Write);
                    }
                    else
                        shadowTexture = renderGraph.defaultResources.defaultShadowTexture;

                    builder.AllowGlobalStateModification(true);
                    if (shadowTexture.IsValid())
                        builder.SetGlobalTextureAfterPass(shadowTexture, MainLightShadowConstantBuffer._MainLightShadowmapID);

                    builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                    {
                        RasterCommandBuffer rasterCommandBuffer = context.cmd;
                        if (!data.emptyShadowmap)
                            data.pass.RenderMainLightCascadeShadowmap(rasterCommandBuffer, ref data, true);
                        else
                        {
                            if (data.setKeywordForEmptyShadowmap)
                                rasterCommandBuffer.EnableKeyword(ShaderGlobalKeywords.MainLightShadows);

                            SetShadowParamsForEmptyShadowmap(rasterCommandBuffer);
                        }
                    });
                }
                return shadowTexture;
            }
            return TextureHandle.nullHandle;
        }

        bool Setup(ShadowData shadowData, CameraData cameraData, LightData lightData, RenderingData renderingData)
        {
            bool shadowsEnabled = shadowData.mainLightShadowsEnabled;
            bool shadowsSupported = shadowData.supportsMainLightShadows;

#if UNITY_EDITOR
            if (CoreUtils.IsSceneLightingDisabled(cameraData.camera))
                return false;
#endif

            using var profScope = new ProfilingScope(_profilingSetupSampler);
            bool stripShadowsOffVariants = cameraData.renderer.stripShadowsOffVariants;
            Clear();

            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex == -1)
            {
                if (shadowsEnabled)
                    return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, null, cameraData, shadowData);
                else
                    return false;
            }

            VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
            Light light = shadowLight.light;
            if (shadowsSupported && light.shadows == LightShadows.None)
                return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);

            if (!shadowsEnabled)
            {
                // If (realtime) shadows are disabled, but the light casts baked shadows, we need to do empty rendering to setup the _MainLightShadowParams uniform,
                // which is also used when sampling baked shadows. This allows for using baked shadows even when realtime shadows are completely disabled.
                if (light.shadows != LightShadows.None &&
                    light.bakingOutput.isBaked &&
                    light.bakingOutput.mixedLightingMode != MixedLightingMode.IndirectOnly &&
                    light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed)
                {
                    return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);
                }

                return false;
            }

            if (!shadowsSupported)
                return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, null, cameraData, shadowData);

            if (shadowLight.lightType != LightType.Directional)
                Debug.LogWarning("Only directional lights are supported as main light.");

            if (!renderingData.cullResults.GetShadowCasterBounds(shadowLightIndex, out Bounds _))
                return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);

            _shadowCasterCascadesCount = shadowData.mainLightShadowCascadesCount;
            _renderTargetWidth = shadowData.mainLightRenderTargetWidth;
            _renderTargetHeight = shadowData.mainLightRenderTargetHeight;

            ref readonly UltraRPLightShadowCullingInfos shadowCullingInfos = ref shadowData.visibleLightsShadowCullingInfos.UnsafeElementAt(shadowLightIndex);
            for (int cascadeIndex = 0; cascadeIndex < _shadowCasterCascadesCount; ++cascadeIndex)
            {
                ref readonly ShadowSliceData sliceData = ref shadowCullingInfos.slices.UnsafeElementAt(cascadeIndex);
                _cascadeSplitDistances[cascadeIndex] = sliceData.splitData.cullingSphere;
                _cascadeSlices[cascadeIndex] = sliceData;

                if (!shadowCullingInfos.IsSliceValid(cascadeIndex))
                    return SetupForEmptyRendering(stripShadowsOffVariants, shadowsEnabled, light, cameraData, shadowData);
            }

            UpdateTextureDescriptorIfNeeded();

            _maxShadowDistanceSq = cameraData.maxShadowDistance * cameraData.maxShadowDistance;
            _cascadeBorder = shadowData.mainLightShadowCascadeBorder;
            _createEmptyShadowmap = false;

            return true;
        }

        private void UpdateTextureDescriptorIfNeeded()
        {
            if (_mainLightShadowDescriptor.width != _renderTargetWidth
                || _mainLightShadowDescriptor.height != _renderTargetHeight
                || _mainLightShadowDescriptor.depthBufferBits != k_shadowmapBufferBits
                || _mainLightShadowDescriptor.colorFormat != RenderTextureFormat.Shadowmap)
            {
                _mainLightShadowDescriptor = new RenderTextureDescriptor(_renderTargetWidth, _renderTargetHeight, RenderTextureFormat.Shadowmap, k_shadowmapBufferBits);
            }
        }

        private bool SetupForEmptyRendering(bool stripShadowsOffVariants, bool shadowsEnabled, Light light, CameraData cameraData,ShadowData shadowData)
        {
            if (!stripShadowsOffVariants)
                return false;

            _createEmptyShadowmap = true;
            _setKeywordForEmptyShadowmap = shadowsEnabled;

            // Even though there are not real-time shadows, the light might be using shadowmasks,
            // which is why we need to update the shadow parameters, for example so shadow strength can be used.

            if (light == null)
                s_emptyShadowParams = new Vector4(0, 0, 1, 0);
            else
            {
                bool supportsSoftShadows = shadowData.supportsSoftShadows;
                float maxShadowDistanceSq = cameraData.maxShadowDistance;
                float mainLightShadowCascadeBorder = shadowData.mainLightShadowCascadeBorder;

                bool softShadows = light.shadows == LightShadows.Soft && supportsSoftShadows;
                float softShadowsProp = ShadowUtils.SoftShadowQualityToShaderProperty(light, softShadows);
                ShadowUtils.GetScaleAndBiasForLinearDistanceFade(maxShadowDistanceSq, mainLightShadowCascadeBorder, out float shadowFadeScale, out float shadowFadeBias);
                s_emptyShadowParams = new Vector4(light.shadowStrength, softShadowsProp, shadowFadeScale, shadowFadeBias);
            }

            return true;
        }

        private void InitPassData(
            ref PassData passData,
            RenderingData renderingData,
            CameraData cameraData,
            LightData lightData,
            ShadowData shadowData)
        {
            passData.pass = this;
            passData.emptyShadowmap = _createEmptyShadowmap;
            passData.setKeywordForEmptyShadowmap = _setKeywordForEmptyShadowmap;
            passData.renderingData = renderingData;
            passData.cameraData = cameraData;
            passData.lightData = lightData;
            passData.shadowData = shadowData;
        }

        private void InitRendererLists(ref PassData passData, ScriptableRenderContext context, RenderGraph renderGraph, bool useRenderGraph)
        {
            int shadowLightIndex = passData.lightData.mainLightIndex;
            if (!_createEmptyShadowmap && shadowLightIndex != -1)
            {
                ShadowDrawingSettings settings = new(passData.renderingData.cullResults, shadowLightIndex, BatchCullingProjectionType.Orthographic)
                {
                    useRenderingLayerMaskTest = UltraRenderPipeline.asset.useRenderingLayers
                };

                for (int cascadeIndex = 0; cascadeIndex < _shadowCasterCascadesCount; ++cascadeIndex)
                {
                    settings.splitData = _cascadeSlices[cascadeIndex].splitData;
                    if (useRenderGraph)
                        passData.shadowRendererListsHandle[cascadeIndex] = renderGraph.CreateShadowRendererList(ref settings);
                    else
                        passData.shadowRendererLists[cascadeIndex] = context.CreateShadowRendererList(ref settings);
                }
            }
        }

        void RenderMainLightCascadeShadowmap(RasterCommandBuffer cmd, ref PassData data, bool isRenderGraph)
        {
            var lightData = data.lightData;

            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex == -1)
                return;

            VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
            using (new ProfilingScope(cmd, ProfilingSampler.Get(UltraRPProfileId.MainLightShadow)))
            {
                // Need to start by setting the Camera position and worldToCamera Matrix as that is not set for passes executed before normal rendering
                ShadowUtils.SetCameraPosition(cmd, data.cameraData.worldSpaceCameraPos);

                for (int cascadeIndex = 0; cascadeIndex < _shadowCasterCascadesCount; ++cascadeIndex)
                {
                    Vector4 shadowBias = ShadowUtils.GetShadowBias(ref shadowLight, shadowLightIndex, data.shadowData, _cascadeSlices[cascadeIndex].projectionMatrix, _cascadeSlices[cascadeIndex].resolution);
                    ShadowUtils.SetupShadowCasterConstantBuffer(cmd, ref shadowLight, shadowBias);
                    cmd.SetKeyword(ShaderGlobalKeywords.CastingPunctualLightShadow, false);
                    RendererList shadowRendererList = isRenderGraph ? data.shadowRendererListsHandle[cascadeIndex] : data.shadowRendererLists[cascadeIndex];
                    ShadowUtils.RenderShadowSlice(cmd, ref _cascadeSlices[cascadeIndex], ref shadowRendererList, _cascadeSlices[cascadeIndex].projectionMatrix, _cascadeSlices[cascadeIndex].viewMatrix);
                }

                data.shadowData.isKeywordSoftShadowsEnabled = shadowLight.light.shadows == LightShadows.Soft && data.shadowData.supportsSoftShadows;
                cmd.SetKeyword(ShaderGlobalKeywords.MainLightShadows, data.shadowData.mainLightShadowCascadesCount == 1);
                cmd.SetKeyword(ShaderGlobalKeywords.MainLightShadowCascades, data.shadowData.mainLightShadowCascadesCount > 1);
                ShadowUtils.SetSoftShadowQualityShaderKeywords(cmd, data.shadowData);

                SetupMainLightShadowReceiverConstants(cmd, ref shadowLight, data.shadowData);
            }
        }

        void SetupMainLightShadowReceiverConstants(RasterCommandBuffer cmd, ref VisibleLight shadowLight, ShadowData shadowData)
        {
            Light light = shadowLight.light;
            bool softShadows = shadowLight.light.shadows == LightShadows.Soft && shadowData.supportsSoftShadows;

            int cascadeCount = _shadowCasterCascadesCount;
            for (int i = 0; i < cascadeCount; ++i)
                _mainLightShadowMatrices[i] = _cascadeSlices[i].shadowTransform;

            // We setup and additional a no-op WorldToShadow matrix in the last index
            // because the ComputeCascadeIndex function in Shadows.hlsl can return an index
            // out of bounds. (position not inside any cascade) and we want to avoid branching
            Matrix4x4 noOpShadowMatrix = Matrix4x4.zero;
            noOpShadowMatrix.m22 = (SystemInfo.usesReversedZBuffer) ? 1.0f : 0.0f;
            for (int i = cascadeCount; i <= k_maxCascades; ++i)
                _mainLightShadowMatrices[i] = noOpShadowMatrix;

            float invShadowAtlasWidth = 1.0f / _renderTargetWidth;
            float invShadowAtlasHeight = 1.0f / _renderTargetHeight;
            float invHalfShadowAtlasWidth = 0.5f * invShadowAtlasWidth;
            float invHalfShadowAtlasHeight = 0.5f * invShadowAtlasHeight;
            float softShadowsProp = ShadowUtils.SoftShadowQualityToShaderProperty(light, softShadows);

            ShadowUtils.GetScaleAndBiasForLinearDistanceFade(_maxShadowDistanceSq, _cascadeBorder, out float shadowFadeScale, out float shadowFadeBias);

            cmd.SetGlobalMatrixArray(MainLightShadowConstantBuffer._WorldToShadow, _mainLightShadowMatrices);
            cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowParams,
                new Vector4(light.shadowStrength, softShadowsProp, shadowFadeScale, shadowFadeBias));

            if (_shadowCasterCascadesCount > 1)
            {
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres0,
                    _cascadeSplitDistances[0]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres1,
                    _cascadeSplitDistances[1]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres2,
                    _cascadeSplitDistances[2]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres3,
                    _cascadeSplitDistances[3]);
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSphereRadii, new Vector4(
                    _cascadeSplitDistances[0].w * _cascadeSplitDistances[0].w,
                    _cascadeSplitDistances[1].w * _cascadeSplitDistances[1].w,
                    _cascadeSplitDistances[2].w * _cascadeSplitDistances[2].w,
                    _cascadeSplitDistances[3].w * _cascadeSplitDistances[3].w));
            }

            // Inside shader soft shadows are controlled through global keyword.
            // If any additional light has soft shadows it will force soft shadows on main light too.
            // As it is not trivial finding out which additional light has soft shadows, we will pass main light properties if soft shadows are supported.
            // This workaround will be removed once we will support soft shadows per light.
            if (shadowData.supportsSoftShadows)
            {
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowOffset0,
                    new Vector4(-invHalfShadowAtlasWidth, -invHalfShadowAtlasHeight,
                        invHalfShadowAtlasWidth, -invHalfShadowAtlasHeight));
                cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowOffset1,
                    new Vector4(-invHalfShadowAtlasWidth, invHalfShadowAtlasHeight,
                        invHalfShadowAtlasWidth, invHalfShadowAtlasHeight));

                cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowmapSize, new Vector4(invShadowAtlasWidth,
                    invShadowAtlasHeight,
                    _renderTargetWidth, _renderTargetHeight));
            }
        }

        internal static void SetShadowParamsForEmptyShadowmap(RasterCommandBuffer rasterCommandBuffer)
        {
            rasterCommandBuffer.SetGlobalVector(MainLightShadowConstantBuffer._ShadowmapSize, s_emptyShadowmapSize);
            rasterCommandBuffer.SetGlobalVector(MainLightShadowConstantBuffer._ShadowParams, s_emptyShadowParams);
        }

        void Clear()
        {
            for (int i = 0; i < _mainLightShadowMatrices.Length; ++i)
                _mainLightShadowMatrices[i] = Matrix4x4.identity;

            for (int i = 0; i < _cascadeSplitDistances.Length; ++i)
                _cascadeSplitDistances[i] = new Vector4(0.0f, 0.0f, 0.0f, 0.0f);

            for (int i = 0; i < _cascadeSlices.Length; ++i)
                _cascadeSlices[i].Clear();
        }
    }
}
