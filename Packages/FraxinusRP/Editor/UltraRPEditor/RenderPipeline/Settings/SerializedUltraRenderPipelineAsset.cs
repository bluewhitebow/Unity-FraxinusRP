using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    public class SerializedUltraRenderPipelineAsset
    {
        public SerializedProperty enableLODCrossFadeProp { get; }
        public SerializedProperty lodCrossFadeDitheringTypeProp { get; }
        public SerializedProperty storeActionsOptimizationProperty { get; }

        public SerializedProperty hdr { get; }
        public SerializedProperty hdrColorBufferPrecisionProp { get; }
        public SerializedProperty msaa { get; }
        public SerializedProperty renderScale { get; }
        public SerializedProperty upscalingFilter { get; }
        public SerializedProperty fsrOverrideSharpness { get; }
        public SerializedProperty fsrSharpness { get; }

        public SerializedProperty mainLightRenderingModeProp { get; }
        public SerializedProperty mainLightShadowsSupportedProp { get; }
        public SerializedProperty mainLightShadowmapResolutionProp { get; }

        public SerializedProperty shEvalModeProp { get; }

        internal SerializedProperty lightProbeSystem;
        internal SerializedProperty probeVolumeTextureSize;
        internal SerializedProperty probeVolumeBlendingTextureSize;
        internal SerializedProperty supportProbeVolumeGPUStreaming;
        internal SerializedProperty supportProbeVolumeDiskStreaming;
        internal SerializedProperty supportProbeVolumeScenarios;
        internal SerializedProperty supportProbeVolumeScenarioBlending;
        internal SerializedProperty probeVolumeSHBands;

        public SerializedProperty additionalLightsRenderingModeProp { get; }
        public SerializedProperty additionalLightsPerObjectLimitProp { get; }
        public SerializedProperty additionalLightShadowsSupportedProp { get; }
        public SerializedProperty additionalLightShadowmapResolutionProp { get; }

        public SerializedProperty additionalLightsShadowResolutionTierLowProp { get; }
        public SerializedProperty additionalLightsShadowResolutionTierMediumProp { get; }
        public SerializedProperty additionalLightsShadowResolutionTierHighProp { get; }

        public SerializedProperty additionalLightCookieResolutionProp { get; }
        public SerializedProperty additionalLightCookieFormatProp { get; }

        public SerializedProperty skyReflectionSizeProp { get; }

        public SerializedProperty reflectionProbeBlendingProp { get; }
        public SerializedProperty reflectionProbeBoxProjectionProp { get; }
        public SerializedProperty reflectionProbeAtlasProp { get; }

        public SerializedProperty srpBatcher { get; }
        public SerializedProperty supportsDynamicBatching { get; }
        public SerializedProperty mixedLightingSupportedProp { get; }
        public SerializedProperty useRenderingLayers { get; }
        public SerializedProperty supportsLightCookies { get; }
        public SerializedProperty supportsAreaLights { get; }

        public SerializedProperty shadowDistanceProp { get; }
        public SerializedProperty shadowCascadeCountProp { get; }
        public SerializedProperty shadowCascade2SplitProp { get; }
        public SerializedProperty shadowCascade3SplitProp { get; }
        public SerializedProperty shadowCascade4SplitProp { get; }
        public SerializedProperty shadowCascadeBorderProp { get; }
        public SerializedProperty shadowDepthBiasProp { get; }
        public SerializedProperty shadowNormalBiasProp { get; }
        public SerializedProperty softShadowsSupportedProp { get; }
        public SerializedProperty softShadowQualityProp { get; }
        public SerializedProperty conservativeEnclosingSphereProp { get; }

        public SerializedProperty volumeFrameworkUpdateModeProp { get; }
        public SerializedProperty volumeProfileProp { get; }

        public SerializedProperty gpuResidentDrawerMode { get; }
        public SerializedProperty smallMeshScreenPercentage { get; }
        public SerializedProperty gpuResidentDrawerEnableOcclusionCullingInCameras { get; }

        public SerializedVirtualTexturingSettings virtualTexturingSettings { get; }

        public UltraRenderPipelineAsset asset { get; }
        public SerializedObject serializedObject;


        public EditorPrefBoolFlags<EditorUtils.Unit> state;

        public SerializedUltraRenderPipelineAsset(SerializedObject serializedObject)
        {
            asset = serializedObject.targetObject as UltraRenderPipelineAsset;
            this.serializedObject = serializedObject;

            enableLODCrossFadeProp = serializedObject.FindProperty("_enableLODCrossFade");
            lodCrossFadeDitheringTypeProp = serializedObject.FindProperty("_lodCrossFadeDitheringType");
            storeActionsOptimizationProperty = serializedObject.FindProperty("_storeActionsOptimization");

            hdr = serializedObject.FindProperty("_supportsHDR");
            hdrColorBufferPrecisionProp = serializedObject.FindProperty("_hdrColorBufferPrecision");
            msaa = serializedObject.FindProperty("_msaa");
            renderScale = serializedObject.FindProperty("_renderScale");
            upscalingFilter = serializedObject.FindProperty("_upscalingFilter");
            fsrOverrideSharpness = serializedObject.FindProperty("_fsrOverrideSharpness");
            fsrSharpness = serializedObject.FindProperty("_fsrSharpness");

            mainLightRenderingModeProp = serializedObject.FindProperty("_mainLightRenderingMode");
            mainLightShadowsSupportedProp = serializedObject.FindProperty("_mainLightShadowsSupported");
            mainLightShadowmapResolutionProp = serializedObject.FindProperty("_mainLightShadowmapResolution");

            shEvalModeProp = serializedObject.FindProperty("_shEvalMode");

            lightProbeSystem = serializedObject.FindProperty("_lightProbeSystem");
            probeVolumeTextureSize = serializedObject.FindProperty("_probeVolumeMemoryBudget");
            probeVolumeBlendingTextureSize = serializedObject.FindProperty("_probeVolumeBlendingMemoryBudget");
            supportProbeVolumeGPUStreaming = serializedObject.FindProperty("_supportProbeVolumeGPUStreaming");
            supportProbeVolumeDiskStreaming = serializedObject.FindProperty("_supportProbeVolumeDiskStreaming");
            supportProbeVolumeScenarios = serializedObject.FindProperty("_supportProbeVolumeScenarios");
            supportProbeVolumeScenarioBlending = serializedObject.FindProperty("_supportProbeVolumeScenarioBlending");
            probeVolumeSHBands = serializedObject.FindProperty("_probeVolumeSHBands");

            mainLightRenderingModeProp = serializedObject.FindProperty("_mainLightRenderingMode");
            mainLightShadowsSupportedProp = serializedObject.FindProperty("_mainLightShadowsSupported");
            mainLightShadowmapResolutionProp = serializedObject.FindProperty("_mainLightShadowmapResolution");

            additionalLightsRenderingModeProp = serializedObject.FindProperty("_additionalLightsRenderingMode");
            additionalLightsPerObjectLimitProp = serializedObject.FindProperty("_additionalLightsPerObjectLimit");
            additionalLightShadowsSupportedProp = serializedObject.FindProperty("_additionalLightShadowsSupported");
            additionalLightShadowmapResolutionProp = serializedObject.FindProperty("_additionalLightsShadowmapResolution");

            additionalLightsShadowResolutionTierLowProp = serializedObject.FindProperty("_additionalLightsShadowResolutionTierLow");
            additionalLightsShadowResolutionTierMediumProp = serializedObject.FindProperty("_additionalLightsShadowResolutionTierMedium");
            additionalLightsShadowResolutionTierHighProp = serializedObject.FindProperty("_additionalLightsShadowResolutionTierHigh");

            additionalLightCookieResolutionProp = serializedObject.FindProperty("_additionalLightsCookieResolution");
            additionalLightCookieFormatProp = serializedObject.FindProperty("_additionalLightsCookieFormat");

            skyReflectionSizeProp = serializedObject.FindProperty("_skyReflectionSize");

            reflectionProbeBlendingProp = serializedObject.FindProperty("_reflectionProbeBlending");
            reflectionProbeBoxProjectionProp = serializedObject.FindProperty("_reflectionProbeBoxProjection");
            reflectionProbeAtlasProp = serializedObject.FindProperty("_reflectionProbeAtlas");

            shadowDistanceProp = serializedObject.FindProperty("_shadowDistance");
            shadowCascadeCountProp = serializedObject.FindProperty("_shadowCascadeCount");
            shadowCascade2SplitProp = serializedObject.FindProperty("_cascade2Split");
            shadowCascade3SplitProp = serializedObject.FindProperty("_cascade3Split");
            shadowCascade4SplitProp = serializedObject.FindProperty("_cascade4Split");
            shadowCascadeBorderProp = serializedObject.FindProperty("_cascadeBorder");
            shadowDepthBiasProp = serializedObject.FindProperty("_shadowDepthBias");
            shadowNormalBiasProp = serializedObject.FindProperty("_shadowNormalBias");
            softShadowsSupportedProp = serializedObject.FindProperty("_softShadowsSupported");
            softShadowQualityProp = serializedObject.FindProperty("_softShadowQuality");
            conservativeEnclosingSphereProp = serializedObject.FindProperty("_conservativeEnclosingSphere");

            srpBatcher = serializedObject.FindProperty("_useSRPBatcher");
            supportsDynamicBatching = serializedObject.FindProperty("_supportsDynamicBatching");
            mixedLightingSupportedProp = serializedObject.FindProperty("_mixedLightingSupported");
            useRenderingLayers = serializedObject.FindProperty("_supportsLightLayers");
            supportsLightCookies = serializedObject.FindProperty("_supportsLightCookies");
            supportsAreaLights = serializedObject.FindProperty("_supportsAreaLights");

            volumeFrameworkUpdateModeProp = serializedObject.FindProperty("_volumeFrameworkUpdateMode");
            volumeProfileProp = serializedObject.FindProperty("_volumeProfile");

            gpuResidentDrawerMode = serializedObject.FindProperty("_gpuResidentDrawerMode");
            smallMeshScreenPercentage = serializedObject.FindProperty("_smallMeshScreenPercentage");
            gpuResidentDrawerEnableOcclusionCullingInCameras = serializedObject.FindProperty("_gpuResidentDrawerEnableOcclusionCullingInCameras");

            virtualTexturingSettings = new SerializedVirtualTexturingSettings(serializedObject.FindProperty("virtualTexturingSettings"));

            string Key = "Ultra_Shadow_Setting_Unit:UI_State";
            state = new EditorPrefBoolFlags<EditorUtils.Unit>(Key);
        }

        public void Update()
        {
            serializedObject.Update();
        }

        public void Apply()
        {
            serializedObject.ApplyModifiedProperties();
        }
    }
}
