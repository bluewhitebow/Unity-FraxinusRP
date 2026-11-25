using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

#if UNITY_EDITOR
using UnityEditor;
#endif

using Debug = UnityEngine.Debug;

namespace FraxinusEngine.Rendering.Ultra
{
    class DebugHandler : IDebugDisplaySettingsQuery
    {
        #region Property Id Constants

        static readonly int k_debugColorInvalidModePropertyId = Shader.PropertyToID("_DebugColorInvalidMode");
        static readonly int k_debugCurrentRealTimeId = Shader.PropertyToID("_DebugCurrentRealTime");

        static readonly int k_debugColorPropertyId = Shader.PropertyToID("_DebugColor");
        static readonly int k_debugTexturePropertyId = Shader.PropertyToID("_DebugTexture");
        static readonly int k_debugFontId = Shader.PropertyToID("_DebugFont");
        static readonly int k_debugTextureNoStereoPropertyId = Shader.PropertyToID("_DebugTextureNoStereo");
        static readonly int k_debugTextureDisplayRect = Shader.PropertyToID("_DebugTextureDisplayRect");
        static readonly int k_debugRenderTargetSupportsStereo = Shader.PropertyToID("_DebugRenderTargetSupportsStereo");
        static readonly int k_debugRenderTargetRangeRemap = Shader.PropertyToID("_DebugRenderTargetRangeRemap");

        // Material settings...
        static readonly int k_debugMaterialModeId = Shader.PropertyToID("_DebugMaterialMode");
        static readonly int k_debugVertexAttributeModeId = Shader.PropertyToID("_DebugVertexAttributeMode");
        static readonly int k_debugMaterialValidationModeId = Shader.PropertyToID("_DebugMaterialValidationMode");

        // Rendering settings...
        static readonly int k_debugMipInfoModeId = Shader.PropertyToID("_DebugMipInfoMode");
        static readonly int k_debugMipMapStatusModeId = Shader.PropertyToID("_DebugMipMapStatusMode");
        static readonly int k_debugMipMapShowStatusCodeId = Shader.PropertyToID("_DebugMipMapShowStatusCode");
        static readonly int k_debugMipMapOpacityId = Shader.PropertyToID("_DebugMipMapOpacity");
        static readonly int k_debugMipMapRecentlyUpdatedCooldownId = Shader.PropertyToID("_DebugMipMapRecentlyUpdatedCooldown");
        static readonly int k_debugMipMapTerrainTextureModeId = Shader.PropertyToID("_DebugMipMapTerrainTextureMode");
        static readonly int k_debugSceneOverrideModeId = Shader.PropertyToID("_DebugSceneOverrideMode");
        static readonly int k_debugFullScreenModeId = Shader.PropertyToID("_DebugFullScreenMode");
        static readonly int k_debugValidationModeId = Shader.PropertyToID("_DebugValidationMode");
        static readonly int k_debugValidateBelowMinThresholdColorPropertyId = Shader.PropertyToID("_DebugValidateBelowMinThresholdColor");
        static readonly int k_debugValidateAboveMaxThresholdColorPropertyId = Shader.PropertyToID("_DebugValidateAboveMaxThresholdColor");
        static readonly int k_debugMaxPixelCost = Shader.PropertyToID("_DebugMaxPixelCost");


        // Lighting settings...
        static readonly int k_debugLightingModeId = Shader.PropertyToID("_DebugLightingMode");
        static readonly int k_debugLightingFeatureFlagsId = Shader.PropertyToID("_DebugLightingFeatureFlags");
        static readonly int k_debugTileClusterModeId = Shader.PropertyToID("_DebugTileClusterMode");

        static readonly int k_debugValidateAlbedoMinLuminanceId = Shader.PropertyToID("_DebugValidateAlbedoMinLuminance");
        static readonly int k_debugValidateAlbedoMaxLuminanceId = Shader.PropertyToID("_DebugValidateAlbedoMaxLuminance");
        static readonly int k_debugValidateAlbedoSaturationToleranceId = Shader.PropertyToID("_DebugValidateAlbedoSaturationTolerance");
        static readonly int k_debugValidateAlbedoHueToleranceId = Shader.PropertyToID("_DebugValidateAlbedoHueTolerance");
        static readonly int k_debugValidateAlbedoCompareColorId = Shader.PropertyToID("_DebugValidateAlbedoCompareColor");

        static readonly int k_debugValidateMetallicMinValueId = Shader.PropertyToID("_DebugValidateMetallicMinValue");
        static readonly int k_debugValidateMetallicMaxValueId = Shader.PropertyToID("_DebugValidateMetallicMaxValue");

        static readonly int k_validationChannelsId = Shader.PropertyToID("_ValidationChannels");
        static readonly int k_rangeMinimumId = Shader.PropertyToID("_RangeMinimum");
        static readonly int k_rangeMaximumId = Shader.PropertyToID("_RangeMaximum");

        #endregion

        #region Pass Data

        private static readonly ProfilingSampler s_debugSetupSampler = new ProfilingSampler("Setup Debug Properties");
        private static readonly ProfilingSampler s_debugFinalValidationSampler = new ProfilingSampler(nameof(UpdateShaderGlobalPropertiesForFinalValidationPass));

        DebugSetupPassData s_debugSetupPassData = new DebugSetupPassData();
        DebugFinalValidationPassData s_debugFinalValidationPassData = new DebugFinalValidationPassData();

        #endregion

        readonly Material _replacementMaterial;
        readonly Material _hdrDebugViewMaterial;
        readonly Material _tileClusterDebugMaterial;

        HDRDebugViewPass _hdrDebugViewPass;
        TileClusterDebugPass _tileClusterDebugPass;
        RTHandle _debugScreenColorHandle;
        RTHandle _debugScreenDepthHandle;

        readonly UltraRenderPipelineRuntimeTextures _runtimeTextures;

        bool _hasDebugRenderTarget;
        bool _debugRenderTargetSupportsStereo;
        Vector4 _debugRenderTargetPixelRect;
        Vector4 _debugRenderTargetRangeRemap;
        RTHandle _debugRenderTarget;

        RTHandle _debugFontTexture;

        private GraphicsBuffer _debugDisplayConstant;

        readonly UltraRenderPipelineDebugDisplaySettings _debugDisplaySettings;

        DebugDisplaySettingsLighting LightingSettings => _debugDisplaySettings.lightingSettings;
        DebugDisplaySettingsMaterial MaterialSettings => _debugDisplaySettings.materialSettings;
        DebugDisplaySettingsRendering RenderingSettings => _debugDisplaySettings.renderingSettings;

        #region IDebugDisplaySettingsQuery

        /// <inheritdoc/>
        public bool AreAnySettingsActive => _debugDisplaySettings.AreAnySettingsActive;

        /// <inheritdoc/>
        public bool IsPostProcessingAllowed => _debugDisplaySettings.IsPostProcessingAllowed;

        /// <inheritdoc/>
        public bool IsLightingActive => _debugDisplaySettings.IsLightingActive;

        // These modes would require putting custom data into gbuffer, so instead we just disable deferred mode.
        internal bool IsActiveModeUnsupportedForDeferred =>
            _debugDisplaySettings.lightingSettings.lightingDebugMode != DebugLightingMode.None ||
            _debugDisplaySettings.lightingSettings.lightingFeatureFlags != DebugLightingFeatureFlags.None ||
            _debugDisplaySettings.renderingSettings.sceneOverrideMode != DebugSceneOverrideMode.None ||
            _debugDisplaySettings.materialSettings.materialDebugMode != DebugMaterialMode.None ||
            _debugDisplaySettings.materialSettings.vertexAttributeDebugMode != DebugVertexAttributeMode.None ||
            _debugDisplaySettings.materialSettings.materialValidationMode != DebugMaterialValidationMode.None ||
            _debugDisplaySettings.renderingSettings.mipInfoMode != DebugMipInfoMode.None;

        /// <inheritdoc/>
        public bool TryGetScreenClearColor(ref Color color)
        {
            return _debugDisplaySettings.TryGetScreenClearColor(ref color);
        }

        #endregion

        internal Material ReplacementMaterial => _replacementMaterial;
        internal UltraRenderPipelineDebugDisplaySettings DebugDisplaySettings => _debugDisplaySettings;
        internal ref RTHandle DebugScreenColorHandle => ref _debugScreenColorHandle;
        internal ref RTHandle DebugScreenDepthHandle => ref _debugScreenDepthHandle;
        internal HDRDebugViewPass hdrDebugViewPass => _hdrDebugViewPass;
        internal TileClusterDebugPass tileClusterDebugPass => _tileClusterDebugPass;

        internal bool HDRDebugViewIsActive(bool resolveFinalTarget)
        {
            // HDR debug views should only apply to the last camera in the stack
            return DebugDisplaySettings.lightingSettings.hdrDebugMode != HDRDebugMode.None && resolveFinalTarget;
        }


        internal bool TileClusterDebugIsActive(bool resolveFinalTarget)
        {
            // TileC luster debug views should only apply to the last camera in the stack
            return DebugDisplaySettings.lightingSettings.tileClusterDebugMode != DebugTileClusterMode.None && resolveFinalTarget;
        }

        internal bool WriteToDebugScreenTexture(bool resolveFinalTarget)
        {
            return HDRDebugViewIsActive(resolveFinalTarget);
        }

        internal bool IsScreenClearNeeded
        {
            get
            {
                Color color = Color.black;

                return TryGetScreenClearColor(ref color);
            }
        }

        internal bool IsRenderPassSupported
        {
            get
            {
                return RenderingSettings.sceneOverrideMode == DebugSceneOverrideMode.None || RenderingSettings.sceneOverrideMode == DebugSceneOverrideMode.Overdraw;
            }
        }

        internal int stpDebugViewIndex { get { return RenderingSettings.stpDebugViewIndex; } }

        internal DebugHandler()
        {
            _debugDisplaySettings = UltraRenderPipelineDebugDisplaySettings.Instance;

            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraPipelineDebugShaders>(out var shaders))
            {
                _replacementMaterial = (shaders.debugReplacementPS != null) ? CoreUtils.CreateEngineMaterial(shaders.debugReplacementPS) : null;
                _hdrDebugViewMaterial = (shaders.hdrDebugViewPS != null) ? CoreUtils.CreateEngineMaterial(shaders.hdrDebugViewPS) : null;
                _tileClusterDebugMaterial = (shaders.tileClusterDebugPS != null) ? CoreUtils.CreateEngineMaterial(shaders.tileClusterDebugPS) : null;
            }

            _hdrDebugViewPass = new HDRDebugViewPass(_hdrDebugViewMaterial);
            _tileClusterDebugPass = new TileClusterDebugPass(_tileClusterDebugMaterial);

            _runtimeTextures = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<UltraRenderPipelineRuntimeTextures>();
            if (_runtimeTextures != null)
                _debugFontTexture = RTHandles.Alloc(_runtimeTextures.debugFontTexture);

            _debugDisplayConstant = new GraphicsBuffer(GraphicsBuffer.Target.Constant, 32, Marshal.SizeOf(typeof(Vector4)));
        }

        public void Dispose()
        {
            _hdrDebugViewPass.Dispose();
            _tileClusterDebugPass.Dispose();
            _debugScreenColorHandle?.Release();
            _debugScreenDepthHandle?.Release();
            _debugFontTexture?.Release();
            _debugDisplayConstant.Dispose();
            CoreUtils.Destroy(_tileClusterDebugMaterial);
            CoreUtils.Destroy(_hdrDebugViewMaterial);
            CoreUtils.Destroy(_replacementMaterial);
        }

        internal bool IsActiveForCamera(bool isPreviewCamera)
        {
            return !isPreviewCamera && AreAnySettingsActive;
        }

        internal bool TryGetFullscreenDebugMode(out DebugFullScreenMode debugFullScreenMode)
        {
            return TryGetFullscreenDebugMode(out debugFullScreenMode, out _);
        }

        internal bool TryGetFullscreenDebugMode(out DebugFullScreenMode debugFullScreenMode, out int textureHeightPercent)
        {
            debugFullScreenMode = RenderingSettings.fullScreenDebugMode;
            textureHeightPercent = RenderingSettings.fullScreenDebugModeOutputSizeScreenPercent;
            return debugFullScreenMode != DebugFullScreenMode.None;
        }

        internal static void ConfigureColorDescriptorForDebugScreen(ref RenderTextureDescriptor descriptor, int cameraWidth, int cameraHeight)
        {
            descriptor.width = cameraWidth;
            descriptor.height = cameraHeight;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.useDynamicScale = true;
            descriptor.depthStencilFormat = GraphicsFormat.None;
        }

        internal static void ConfigureDepthDescriptorForDebugScreen(ref RenderTextureDescriptor descriptor, GraphicsFormat depthStencilFormat, int cameraWidth, int cameraHeight)
        {
            descriptor.width = cameraWidth;
            descriptor.height = cameraHeight;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.useDynamicScale = true;
            descriptor.depthStencilFormat = depthStencilFormat;
            descriptor.graphicsFormat = GraphicsFormat.None;
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal void SetupShaderProperties(RasterCommandBuffer cmd, int passIndex = 0)
        {
            if (LightingSettings.lightingDebugMode == DebugLightingMode.ShadowCascades)
            {
                // we disable cubemap reflections, too distracting (in TemplateLWRP for ex.)
                cmd.EnableShaderKeyword("_DEBUG_ENVIRONMENTREFLECTIONS_OFF");
            }
            else
            {
                cmd.DisableShaderKeyword("_DEBUG_ENVIRONMENTREFLECTIONS_OFF");
            }

            _debugDisplayConstant.SetData(MaterialSettings.debugRenderingLayersColors, 0, 0, 32);

            cmd.SetGlobalConstantBuffer(_debugDisplayConstant, "_DebugDisplayConstant", 0, _debugDisplayConstant.count * _debugDisplayConstant.stride);

            if (MaterialSettings.renderingLayersSelectedLight)
                cmd.SetGlobalInt("_DebugRenderingLayerMask", (int)MaterialSettings.GetDebugLightLayersMask());
            else
                cmd.SetGlobalInt("_DebugRenderingLayerMask", (int)MaterialSettings.renderingLayerMask);

            switch (RenderingSettings.sceneOverrideMode)
            {
                case DebugSceneOverrideMode.Overdraw:
                {
                    // Target texture can contains only a value between 0 and 1
                    // So we encode the number of overdraw in the range (0, max displayed overdaw count)
                    // The value will be clamped by the GPU driver
                    var value = 1 / (float)RenderingSettings.maxOverdrawCount;
                    cmd.SetGlobalColor(k_debugColorPropertyId, new Color(value, value, value, 1));
                    break;
                }

                case DebugSceneOverrideMode.Wireframe:
                {
                    cmd.SetGlobalColor(k_debugColorPropertyId, Color.black);
                    break;
                }

                case DebugSceneOverrideMode.SolidWireframe:
                {
                    cmd.SetGlobalColor(k_debugColorPropertyId, (passIndex == 0) ? Color.white : Color.black);
                    break;
                }

                case DebugSceneOverrideMode.ShadedWireframe:
                {
                    if (passIndex == 0)
                    {
                        cmd.SetKeyword(ShaderGlobalKeywords.DEBUG_DISPLAY, false);
                    }
                    else if (passIndex == 1)
                    {
                        cmd.SetGlobalColor(k_debugColorPropertyId, Color.black);
                        cmd.SetKeyword(ShaderGlobalKeywords.DEBUG_DISPLAY, true);
                    }

                    break;
                }
            }

            switch (MaterialSettings.materialValidationMode)
            {
                case DebugMaterialValidationMode.Albedo:
                    cmd.SetGlobalFloat(k_debugValidateAlbedoMinLuminanceId, MaterialSettings.albedoMinLuminance);
                    cmd.SetGlobalFloat(k_debugValidateAlbedoMaxLuminanceId, MaterialSettings.albedoMaxLuminance);
                    cmd.SetGlobalFloat(k_debugValidateAlbedoSaturationToleranceId, MaterialSettings.albedoSaturationTolerance);
                    cmd.SetGlobalFloat(k_debugValidateAlbedoHueToleranceId, MaterialSettings.albedoHueTolerance);
                    cmd.SetGlobalColor(k_debugValidateAlbedoCompareColorId, MaterialSettings.albedoCompareColor.linear);
                    break;

                case DebugMaterialValidationMode.Metallic:
                    cmd.SetGlobalFloat(k_debugValidateMetallicMinValueId, MaterialSettings.metallicMinValue);
                    cmd.SetGlobalFloat(k_debugValidateMetallicMaxValueId, MaterialSettings.metallicMaxValue);
                    break;
            }
        }

        internal void SetDebugRenderTarget(RTHandle renderTarget, Rect displayRect, bool supportsStereo, Vector4 dataRangeRemap)
        {
            _hasDebugRenderTarget = true;
            _debugRenderTargetSupportsStereo = supportsStereo;
            _debugRenderTarget = renderTarget;
            _debugRenderTargetPixelRect = new Vector4(displayRect.x, displayRect.y, displayRect.width, displayRect.height);
            _debugRenderTargetRangeRemap = dataRangeRemap;
        }

        internal void ResetDebugRenderTarget()
        {
            _hasDebugRenderTarget = false;
        }

        class DebugFinalValidationPassData
        {
            public bool isFinalPass;
            public bool resolveFinalTarget;
            public bool isActiveForCamera;
            public bool hasDebugRenderTarget;

            public TextureHandle debugRenderTargetHandle;
            public int debugTexturePropertyId;

            public Vector4 debugRenderTargetPixelRect;
            public int debugRenderTargetSupportsStereo;
            public Vector4 debugRenderTargetRangeRemap;

            public TextureHandle debugFontTextureHandle;

            // NOTE: The settings are references.
            // It's assumed they're the same for the whole frame. Build timeline != execution timeline!
            // Ideally these would be copied without any allocs.
            public DebugDisplaySettingsRendering renderingSettings;
        }

        DebugFinalValidationPassData InitDebugFinalValidationPassData(DebugFinalValidationPassData passData, CameraData cameraData, bool isFinalPass)
        {
            passData.isFinalPass = isFinalPass;
            passData.resolveFinalTarget = cameraData.resolveFinalTarget;
            passData.isActiveForCamera = IsActiveForCamera(cameraData.isPreviewCamera);
            passData.hasDebugRenderTarget = _hasDebugRenderTarget;

            passData.debugRenderTargetHandle = TextureHandle.nullHandle;
            passData.debugTexturePropertyId = _debugRenderTargetSupportsStereo ? k_debugTexturePropertyId : k_debugTextureNoStereoPropertyId;

            passData.debugRenderTargetPixelRect = _debugRenderTargetPixelRect;
            passData.debugRenderTargetSupportsStereo = _debugRenderTargetSupportsStereo ? 1 : 0;
            passData.debugRenderTargetRangeRemap = _debugRenderTargetRangeRemap;

            passData.debugFontTextureHandle = TextureHandle.nullHandle;

            passData.renderingSettings = RenderingSettings;

            return passData;
        }

        static void UpdateShaderGlobalPropertiesForFinalValidationPass(RasterCommandBuffer cmd, DebugFinalValidationPassData data)
        {
            // Ensure final validation & fullscreen debug modes are only done once in the very final pass, for the last camera on the stack.
            bool isFinal = data.isFinalPass && data.resolveFinalTarget;
            if (!isFinal)
            {
                cmd.SetKeyword(ShaderGlobalKeywords.DEBUG_DISPLAY, false);
                return;
            }

            if (data.isActiveForCamera)
            {
                cmd.SetKeyword(ShaderGlobalKeywords.DEBUG_DISPLAY, true);
            }
            else
            {
                cmd.SetKeyword(ShaderGlobalKeywords.DEBUG_DISPLAY, false);
            }

            if (data.hasDebugRenderTarget)
            {
                if(data.debugRenderTargetHandle.IsValid())
                    cmd.SetGlobalTexture(data.debugTexturePropertyId,  data.debugRenderTargetHandle);

                cmd.SetGlobalVector(k_debugTextureDisplayRect, data.debugRenderTargetPixelRect);
                cmd.SetGlobalInteger(k_debugRenderTargetSupportsStereo, data.debugRenderTargetSupportsStereo);
                cmd.SetGlobalVector(k_debugRenderTargetRangeRemap, data.debugRenderTargetRangeRemap);
            }

            var renderingSettings = data.renderingSettings;
            if (renderingSettings.validationMode == DebugValidationMode.HighlightOutsideOfRange)
            {
                cmd.SetGlobalInteger(k_validationChannelsId, (int)renderingSettings.validationChannels);
                cmd.SetGlobalFloat(k_rangeMinimumId, renderingSettings.validationRangeMin);
                cmd.SetGlobalFloat(k_rangeMaximumId, renderingSettings.validationRangeMax);
            }

            if (renderingSettings.mipInfoMode != DebugMipInfoMode.None)
            {
                // some (not all) of these need text rendering
                cmd.SetGlobalTexture(k_debugFontId, data.debugFontTextureHandle);
            }
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal void UpdateShaderGlobalPropertiesForFinalValidationPass(CommandBuffer cmd, CameraData cameraData, bool isFinalPass)
        {
            UpdateShaderGlobalPropertiesForFinalValidationPass(CommandBufferHelpers.GetRasterCommandBuffer(cmd), InitDebugFinalValidationPassData(s_debugFinalValidationPassData, cameraData, isFinalPass));
            cmd.SetGlobalTexture(s_debugFinalValidationPassData.debugTexturePropertyId,  _debugRenderTarget);
            if (RenderingSettings.mipInfoMode != DebugMipInfoMode.None)
            {
                // some (not all) of these need text rendering
                cmd.SetGlobalTexture(k_debugFontId, _runtimeTextures.debugFontTexture);
            }
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal void UpdateShaderGlobalPropertiesForFinalValidationPass(RenderGraph renderGraph, CameraData cameraData, bool isFinalPass)
        {
            using (var builder = renderGraph.AddRasterRenderPass<DebugFinalValidationPassData>(nameof(UpdateShaderGlobalPropertiesForFinalValidationPass), out var passData, s_debugFinalValidationSampler))
            {
                InitDebugFinalValidationPassData(passData, cameraData, isFinalPass);

                if (_debugRenderTarget != null)
                    passData.debugRenderTargetHandle = renderGraph.ImportTexture(_debugRenderTarget);

                if (_debugFontTexture != null)
                    passData.debugFontTextureHandle = renderGraph.ImportTexture(_debugFontTexture);

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                if (passData.debugRenderTargetHandle.IsValid())
                {
                    builder.UseTexture(passData.debugRenderTargetHandle);
                    builder.SetGlobalTextureAfterPass(passData.debugRenderTargetHandle, passData.debugTexturePropertyId);
                }

                if (passData.debugFontTextureHandle.IsValid())
                {
                    builder.UseTexture(passData.debugFontTextureHandle);
                    builder.SetGlobalTextureAfterPass(passData.debugFontTextureHandle, k_debugFontId);
                }

                builder.SetRenderFunc(static (DebugFinalValidationPassData data, RasterGraphContext context) =>
                {
                    UpdateShaderGlobalPropertiesForFinalValidationPass(context.cmd, data);
                });
            }
        }

        class DebugSetupPassData
        {
            public bool isActiveForCamera;

            // NOTE: The settings are references.
            // It's assumed they're the same for the whole frame. Build timeline != execution timeline!
            // Ideally these would be copied without any allocs.
            public DebugDisplaySettingsMaterial  materialSettings;
            public DebugDisplaySettingsRendering renderingSettings;
            public DebugDisplaySettingsLighting  lightingSettings;
        }

        DebugSetupPassData InitDebugSetupPassData(DebugSetupPassData passData, bool isPreviewCamera)
        {
            passData.isActiveForCamera = IsActiveForCamera(isPreviewCamera);
            passData.materialSettings  = MaterialSettings;
            passData.renderingSettings = RenderingSettings;
            passData.lightingSettings  = LightingSettings;
            return passData;
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        static void Setup(RasterCommandBuffer cmd, DebugSetupPassData passData)
        {
            if (passData.isActiveForCamera)
            {
                cmd.SetKeyword(ShaderGlobalKeywords.DEBUG_DISPLAY, true);

                // Material settings...
                cmd.SetGlobalFloat(k_debugMaterialModeId, (int)passData.materialSettings.materialDebugMode);
                cmd.SetGlobalFloat(k_debugVertexAttributeModeId, (int)passData.materialSettings.vertexAttributeDebugMode);

                cmd.SetGlobalInteger(k_debugMaterialValidationModeId, (int)passData.materialSettings.materialValidationMode);

                // Rendering settings...
                cmd.SetGlobalInteger(k_debugMipInfoModeId, (int)passData.renderingSettings.mipInfoMode);
                cmd.SetGlobalInteger(k_debugMipMapStatusModeId, (int)passData.renderingSettings.mipDebugStatusMode);
                cmd.SetGlobalInteger(k_debugMipMapShowStatusCodeId, passData.renderingSettings.mipDebugStatusShowCode ? 1 : 0);
                cmd.SetGlobalFloat(k_debugMipMapOpacityId, passData.renderingSettings.mipDebugOpacity);
                cmd.SetGlobalFloat(k_debugMipMapRecentlyUpdatedCooldownId, passData.renderingSettings.mipDebugRecentUpdateCooldown);
                cmd.SetGlobalFloat(k_debugMipMapTerrainTextureModeId, (int)passData.renderingSettings.mipDebugTerrainTexture);
                cmd.SetGlobalInteger(k_debugSceneOverrideModeId, (int)passData.renderingSettings.sceneOverrideMode);
                cmd.SetGlobalInteger(k_debugFullScreenModeId, (int)passData.renderingSettings.fullScreenDebugMode);
                cmd.SetGlobalInteger(k_debugMaxPixelCost, (int)passData.renderingSettings.maxOverdrawCount);
                cmd.SetGlobalInteger(k_debugValidationModeId, (int)passData.renderingSettings.validationMode);
                cmd.SetGlobalColor(k_debugValidateBelowMinThresholdColorPropertyId, Color.red);
                cmd.SetGlobalColor(k_debugValidateAboveMaxThresholdColorPropertyId, Color.blue);

                // Lighting settings...
                cmd.SetGlobalFloat(k_debugLightingModeId, (int)passData.lightingSettings.lightingDebugMode);
                cmd.SetGlobalInteger(k_debugLightingFeatureFlagsId, (int)passData.lightingSettings.lightingFeatureFlags);

                // Set-up any other persistent properties...
                cmd.SetGlobalColor(k_debugColorInvalidModePropertyId, Color.red);
#if UNITY_EDITOR
                cmd.SetGlobalFloat(k_debugCurrentRealTimeId, (float)EditorApplication.timeSinceStartup);
#else
                cmd.SetGlobalFloat(k_debugCurrentRealTimeId, Time.realtimeSinceStartup);
#endif
            }
            else
            {
                cmd.SetKeyword(ShaderGlobalKeywords.DEBUG_DISPLAY, false);
            }
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal void Setup(CommandBuffer cmd, bool isPreviewCamera)
        {
            Setup(CommandBufferHelpers.GetRasterCommandBuffer(cmd), InitDebugSetupPassData(s_debugSetupPassData, isPreviewCamera));
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal void Setup(RenderGraph renderGraph, bool isPreviewCamera)
        {
            using (var builder = renderGraph.AddRasterRenderPass<DebugSetupPassData>(s_debugSetupSampler.name, out var passData, s_debugSetupSampler))
            {
                InitDebugSetupPassData(passData, isPreviewCamera);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (DebugSetupPassData data, RasterGraphContext context) =>
                {
                    Setup(context.cmd, data);
                });
            }
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle srcColor, TextureHandle overlayTexture, TextureHandle dstColor)
        {
            var cameraData = frameData.Get<CameraData>();
            if (IsActiveForCamera(cameraData.isPreviewCamera) && HDRDebugViewIsActive(cameraData.resolveFinalTarget))
                _hdrDebugViewPass.RenderHDRDebug(renderGraph, cameraData, srcColor, overlayTexture, dstColor, LightingSettings.hdrDebugMode);

            if (IsActiveForCamera(cameraData.isPreviewCamera) && TileClusterDebugIsActive(cameraData.resolveFinalTarget))
                _tileClusterDebugPass.RenderTileClusterDebug(renderGraph, frameData, cameraData, LightingSettings.tileClusterDebugMode, LightingSettings.clusterDebugID, LightingSettings.clusterCategoryDebugMode);
        }

        #region DebugRendererLists

        internal DebugRendererLists CreateRendererListsWithDebugRenderState(
             ScriptableRenderContext context,
             ref CullingResults cullResults,
             ref DrawingSettings drawingSettings,
             ref FilteringSettings filteringSettings,
             ref RenderStateBlock renderStateBlock)
        {
            DebugRendererLists debug = new DebugRendererLists(this, filteringSettings);
            debug.CreateRendererListsWithDebugRenderState(context, ref cullResults, ref drawingSettings, ref filteringSettings, ref renderStateBlock);
            return debug;
        }

        internal DebugRendererLists CreateRendererListsWithDebugRenderState(
            RenderGraph renderGraph,
            ref CullingResults cullResults,
            ref DrawingSettings drawingSettings,
            ref FilteringSettings filteringSettings,
            ref RenderStateBlock renderStateBlock)
        {
            DebugRendererLists debug = new DebugRendererLists(this, filteringSettings);
            debug.CreateRendererListsWithDebugRenderState(renderGraph, ref cullResults, ref drawingSettings, ref filteringSettings, ref renderStateBlock);
            return debug;
        }
        #endregion
    }

    internal class DebugRendererLists
    {
        private readonly DebugHandler _debugHandler;
        readonly FilteringSettings _filteringSettings;
        List<DebugRenderSetup> _debugRenderSetups = new List<DebugRenderSetup>(2);
        List<RendererList> _activeDebugRendererList = new List<RendererList>(2);
        List<RendererListHandle> _activeDebugRendererListHdl = new List<RendererListHandle>(2);

        public DebugRendererLists(DebugHandler debugHandler,
            FilteringSettings filteringSettings)
        {
            _debugHandler = debugHandler;
            _filteringSettings = filteringSettings;
        }

        private void CreateDebugRenderSetups(FilteringSettings filteringSettings)
        {
            var sceneOverrideMode = _debugHandler.DebugDisplaySettings.renderingSettings.sceneOverrideMode;
            var numIterations = ((sceneOverrideMode == DebugSceneOverrideMode.SolidWireframe) || (sceneOverrideMode == DebugSceneOverrideMode.ShadedWireframe)) ? 2 : 1;
            for (var i = 0; i < numIterations; i++)
                _debugRenderSetups.Add(new DebugRenderSetup(_debugHandler, i, filteringSettings));
        }

        void DisposeDebugRenderLists()
        {
            foreach (var debugRenderSetup in _debugRenderSetups)
            {
                debugRenderSetup.Dispose();
            }
            _debugRenderSetups.Clear();
            _activeDebugRendererList.Clear();
            _activeDebugRendererListHdl.Clear();
        }

        internal void CreateRendererListsWithDebugRenderState(
             ScriptableRenderContext context,
             ref CullingResults cullResults,
             ref DrawingSettings drawingSettings,
             ref FilteringSettings filteringSettings,
             ref RenderStateBlock renderStateBlock)
        {
            CreateDebugRenderSetups(filteringSettings);
            foreach (DebugRenderSetup debugRenderSetup in _debugRenderSetups)
            {
                DrawingSettings debugDrawingSettings = debugRenderSetup.CreateDrawingSettings(drawingSettings);
                RenderStateBlock debugRenderStateBlock = debugRenderSetup.GetRenderStateBlock(renderStateBlock);
                RendererList rendererList = new RendererList();
                RenderingUtils.CreateRendererListWithRenderStateBlock(context, ref cullResults, debugDrawingSettings, filteringSettings, debugRenderStateBlock, ref rendererList);
                _activeDebugRendererList.Add((rendererList));
            }
        }

        internal void CreateRendererListsWithDebugRenderState(
            RenderGraph renderGraph,
            ref CullingResults cullResults,
            ref DrawingSettings drawingSettings,
            ref FilteringSettings filteringSettings,
            ref RenderStateBlock renderStateBlock)
        {
            CreateDebugRenderSetups(filteringSettings);
            foreach (DebugRenderSetup debugRenderSetup in _debugRenderSetups)
            {
                DrawingSettings debugDrawingSettings = debugRenderSetup.CreateDrawingSettings(drawingSettings);
                RenderStateBlock debugRenderStateBlock = debugRenderSetup.GetRenderStateBlock(renderStateBlock);
                RendererListHandle rendererListHdl = new RendererListHandle();
                RenderingUtils.CreateRendererListWithRenderStateBlock(renderGraph, ref cullResults, debugDrawingSettings, filteringSettings, debugRenderStateBlock, ref rendererListHdl);
                _activeDebugRendererListHdl.Add((rendererListHdl));
            }
        }

        internal void PrepareRendererListForRasterPass(IRasterRenderGraphBuilder builder)
        {
            foreach (RendererListHandle rendererListHdl in _activeDebugRendererListHdl)
            {
                builder.UseRendererList(rendererListHdl);
            }
        }

        internal void DrawWithRendererList(RasterCommandBuffer cmd)
        {
            foreach (DebugRenderSetup debugRenderSetup in _debugRenderSetups)
            {
                debugRenderSetup.Begin(cmd);
                RendererList rendererList = new RendererList();
                if (_activeDebugRendererList.Count > 0)
                {
                    rendererList = _activeDebugRendererList[debugRenderSetup.GetIndex()];
                }
                else if(_activeDebugRendererListHdl.Count > 0)
                {
                    rendererList = _activeDebugRendererListHdl[debugRenderSetup.GetIndex()];
                }

                debugRenderSetup.DrawWithRendererList(cmd, ref rendererList);
                debugRenderSetup.End(cmd);
            }

            DisposeDebugRenderLists();
        }
    }
}
