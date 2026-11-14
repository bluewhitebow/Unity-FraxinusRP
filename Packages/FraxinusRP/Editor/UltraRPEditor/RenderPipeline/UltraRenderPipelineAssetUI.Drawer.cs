using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;
using System;

namespace FraxinusEditor.Rendering.Ultra
{
    using CED = CoreEditorDrawer<SerializedUltraRenderPipelineAsset>;

    static partial class UltraRenderPipelineAssetUI
    {
        #region Expandable States
        internal enum Expandable
        {
            Rendering = 1 << 1,
            Quality = 1 << 2,
            Lighting = 1 << 3,
            Shadows = 1 << 4,
            VirtualTexturing = 1 << 6,
            Volumes = 1 << 7,
        }

        enum ExpandableAdditional
        {
            Rendering = 1 << 1,
            Quality = 1 << 2,
            Lighting = 1 << 3,
            Shadows = 1 << 4,
        }
        #endregion

        readonly static ExpandedState<Expandable, UltraRenderPipelineAsset> s_expandedState = new (Expandable.Rendering, "UltraRP");
        readonly static AdditionalPropertiesState<ExpandableAdditional, Light> s_additionalPropertiesState = new (0, "UltraRP");

        internal static VirtualTexturingSettingsUI virtualTexturingSettingsUI = new VirtualTexturingSettingsUI();

        public static readonly CED.IDrawer Inspector = CED.Group(
            CED.AdditionalPropertiesFoldoutGroup(Styles.renderingSettingsText, Expandable.Rendering, s_expandedState, ExpandableAdditional.Rendering, s_additionalPropertiesState, DrawRendering, DrawRenderingAdditional),
            CED.FoldoutGroup(Styles.qualitySettingsText, Expandable.Quality, s_expandedState, DrawQuality),
            CED.AdditionalPropertiesFoldoutGroup(Styles.lightingSettingsText, Expandable.Lighting, s_expandedState, ExpandableAdditional.Lighting, s_additionalPropertiesState, DrawLighting, DrawLightingAdditional),
            CED.AdditionalPropertiesFoldoutGroup(Styles.shadowSettingsText, Expandable.Shadows, s_expandedState, ExpandableAdditional.Shadows, s_additionalPropertiesState, DrawShadows, DrawShadowsAdditional),
            CED.FoldoutGroup(Styles.virtualTexturingTitle, Expandable.VirtualTexturing, s_expandedState, DrawVirtualTexturing),
            CED.FoldoutGroup(Styles.volumeSettingsText, Expandable.Volumes, s_expandedState, DrawVolumes)
        );

        static void DrawRendering(SerializedUltraRenderPipelineAsset serialized, Editor ownerEditor)
        {
            if (ownerEditor is UltraRenderPipelineAssetEditor ultraRPAssetEditor)
            {
                EditorGUILayout.Space();
                ultraRPAssetEditor.rendererGraphList.DoLayoutList();

                if (!serialized.asset.ValidateRendererGraph(-1))
                    EditorGUILayout.HelpBox(Styles.rendererMissingDefaultMessage.text, MessageType.Error, true);
                else if (!serialized.asset.ValidateRendererGraphList(true))
                    EditorGUILayout.HelpBox(Styles.rendererMissingMessage.text, MessageType.Warning, true);
                // Inspect graph assets without creating runtime renderers: UltraRP's
                // runtime shader resources may be unavailable while another pipeline is active.
            
#if UNITY_6000_0_OR_NEWER
                EditorGUILayout.PropertyField(serialized.gpuResidentDrawerMode, Styles.gpuResidentDrawerMode);


                var brgStrippingError = EditorGraphicsSettings.batchRendererGroupShaderStrippingMode != BatchRendererGroupStrippingMode.KeepAll;
                var lightingModeError = !HasCorrectLightingModes(serialized.asset);
                var staticBatchingWarning = PlayerSettings.GetStaticBatchingForPlatform(EditorUserBuildSettings.activeBuildTarget);

                if ((GPUResidentDrawerMode)serialized.gpuResidentDrawerMode.intValue != GPUResidentDrawerMode.Disabled)
                {
                    ++EditorGUI.indentLevel;
                    serialized.smallMeshScreenPercentage.floatValue = Mathf.Clamp(EditorGUILayout.FloatField(Styles.smallMeshScreenPercentage, serialized.smallMeshScreenPercentage.floatValue), 0.0f, 20.0f);
                    EditorGUILayout.PropertyField(serialized.gpuResidentDrawerEnableOcclusionCullingInCameras, Styles.gpuResidentDrawerEnableOcclusionCullingInCameras);
                    --EditorGUI.indentLevel;

                    if (brgStrippingError)
                        EditorGUILayout.HelpBox(Styles.brgShaderStrippingErrorMessage.text, MessageType.Warning, true);
                    if (lightingModeError)
                        EditorGUILayout.HelpBox(Styles.lightModeErrorMessage.text, MessageType.Warning, true);
                    if (staticBatchingWarning)
                        EditorGUILayout.HelpBox(Styles.staticBatchingInfoMessage.text, MessageType.Info, true);
                }
#else
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.EnumPopup(Styles.gpuResidentDrawerMode, GPUResidentDrawerMode.Disabled);
                EditorGUILayout.HelpBox("Unity's native GPU Resident Drawer is unavailable in Unity 2022. Standard renderer drawing remains active.", MessageType.Info);
#endif
            }
        }

        static void DrawRenderingAdditional(SerializedUltraRenderPipelineAsset serialized, Editor owner)
        {
            EditorGUILayout.PropertyField(serialized.srpBatcher, Styles.srpBatcher);
            EditorGUILayout.PropertyField(serialized.supportsDynamicBatching, Styles.dynamicBatching);
            EditorGUILayout.PropertyField(serialized.storeActionsOptimizationProperty, Styles.storeActionsOptimizationText);
        }

        static void DrawQuality(SerializedUltraRenderPipelineAsset serialized, Editor ownerEditor)
        {
            DrawHDR(serialized, ownerEditor);

            EditorGUILayout.PropertyField(serialized.msaa, Styles.msaaText);
            serialized.renderScale.floatValue = EditorGUILayout.Slider(Styles.renderScaleText, serialized.renderScale.floatValue, UltraRenderPipeline.minRenderScale, UltraRenderPipeline.maxRenderScale);
            EditorGUILayout.PropertyField(serialized.upscalingFilter, Styles.upscalingFilterText);
            if (serialized.asset.upscalingFilter == UpscalingFilterSelection.FSR)
            {
                ++EditorGUI.indentLevel;

                EditorGUILayout.PropertyField(serialized.fsrOverrideSharpness, Styles.fsrOverrideSharpness);

                // We put the FSR sharpness override value behind an override checkbox so we can tell when the user intends to use a custom value rather than the default.
                if (serialized.fsrOverrideSharpness.boolValue)
                    serialized.fsrSharpness.floatValue = EditorGUILayout.Slider(Styles.fsrSharpnessText, serialized.fsrSharpness.floatValue, 0.0f, 1.0f);

                --EditorGUI.indentLevel;
            }
            else if (serialized.asset.upscalingFilter == UpscalingFilterSelection.STP)
            {
                // Warn users about performance expectations if they attempt to enable STP on a mobile platform
                if (PlatformAutoDetect.isShaderAPIMobileDefined)
                    EditorGUILayout.HelpBox(Styles.stpMobilePlatformWarning, MessageType.Warning, true);
            }
            else if (serialized.asset.upscalingFilter == UpscalingFilterSelection.SGSR1)
            {
                EditorGUILayout.HelpBox("SGSR1 performs spatial upscaling at Render Scale 1 or below. Enable camera Post Processing. Unsupported shaders, stereo targets and dynamic resolution use Bilinear.", MessageType.Info);
            }
            else if (serialized.asset.upscalingFilter == UpscalingFilterSelection.SGSR2)
            {
                EditorGUILayout.HelpBox("SGSR2 includes temporal anti-aliasing at Render Scale 1 or below. Enable camera Post Processing and disable MSAA. Camera stacking, dynamic resolution, stereo and child contexts are unsupported; these cameras use Bilinear without temporal jitter.", MessageType.Info);
            }

            if (serialized.renderScale.floatValue < 1.0f || serialized.asset.upscalingFilter == UpscalingFilterSelection.STP || serialized.asset.upscalingFilter == UpscalingFilterSelection.FSR ||
                serialized.asset.upscalingFilter == UpscalingFilterSelection.SGSR1 || serialized.asset.upscalingFilter == UpscalingFilterSelection.SGSR2)
                EditorGUILayout.HelpBox("Camera depth isn't supported when Upscaling is turned on in the game view. We will automatically fall back to not doing depth-testing for this pass.", MessageType.Warning, true);

            EditorGUILayout.PropertyField(serialized.enableLODCrossFadeProp, Styles.enableLODCrossFadeText);
            EditorGUI.BeginDisabledGroup(!serialized.enableLODCrossFadeProp.boolValue);
            EditorGUILayout.PropertyField(serialized.lodCrossFadeDitheringTypeProp, Styles.lodCrossFadeDitheringTypeText);
            EditorGUI.EndDisabledGroup();
        }

        static void DrawHDR(SerializedUltraRenderPipelineAsset serialized, Editor ownerEditor)
        {
            EditorGUILayout.PropertyField(serialized.hdr, Styles.hdrText);

            // Nested and in-between additional property
            bool additionalProperties = s_expandedState[Expandable.Quality] && s_additionalPropertiesState[ExpandableAdditional.Quality];
            if (serialized.hdr.boolValue && additionalProperties)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serialized.hdrColorBufferPrecisionProp, Styles.hdrColorBufferPrecisionText);
                EditorGUI.indentLevel--;
            }
        }

        static void DrawLighting(SerializedUltraRenderPipelineAsset serialized, Editor ownerEditor)
        {
            // Main Light
            bool disableGroup = false;
            EditorGUI.BeginDisabledGroup(disableGroup);
            CoreEditorUtils.DrawPopup(Styles.mainLightRenderingModeText, serialized.mainLightRenderingModeProp, Styles.mainLightOptions);
            EditorGUI.EndDisabledGroup();

            EditorGUI.indentLevel++;
            disableGroup |= !serialized.mainLightRenderingModeProp.boolValue;

            EditorGUI.BeginDisabledGroup(disableGroup);
            EditorGUILayout.PropertyField(serialized.mainLightShadowsSupportedProp, Styles.supportsMainLightShadowsText);
            EditorGUI.EndDisabledGroup();

            disableGroup |= !serialized.mainLightShadowsSupportedProp.boolValue;
            EditorGUI.BeginDisabledGroup(disableGroup);
            EditorGUILayout.PropertyField(serialized.mainLightShadowmapResolutionProp, Styles.mainLightShadowmapResolutionText);
            EditorGUI.EndDisabledGroup();

            EditorGUI.indentLevel--;
            EditorGUILayout.Space();

            // Additional light
            EditorGUILayout.PropertyField(serialized.additionalLightsRenderingModeProp, Styles.addditionalLightsRenderingModeText);
            EditorGUI.indentLevel++;

            disableGroup = serialized.additionalLightsRenderingModeProp.intValue == (int)LightRenderingMode.Disabled;
            EditorGUI.BeginDisabledGroup(disableGroup);
            serialized.additionalLightsPerObjectLimitProp.intValue = EditorGUILayout.IntSlider(Styles.perObjectLimit, serialized.additionalLightsPerObjectLimitProp.intValue, 0, UltraRenderPipeline.maxPerObjectLights);
            EditorGUI.EndDisabledGroup();

            disableGroup |= (serialized.additionalLightsPerObjectLimitProp.intValue == 0 || serialized.additionalLightsRenderingModeProp.intValue != (int)LightRenderingMode.PerPixel);
            EditorGUI.BeginDisabledGroup(disableGroup);
            EditorGUILayout.PropertyField(serialized.additionalLightShadowsSupportedProp, Styles.supportsAdditionalShadowsText);
            EditorGUI.EndDisabledGroup();

            disableGroup |= !serialized.additionalLightShadowsSupportedProp.boolValue;
            EditorGUI.BeginDisabledGroup(disableGroup);
            EditorGUILayout.PropertyField(serialized.additionalLightShadowmapResolutionProp, Styles.additionalLightsShadowmapResolution);
            DrawShadowResolutionTierSettings(serialized, ownerEditor);
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space();
            disableGroup = serialized.additionalLightsRenderingModeProp.intValue == (int)LightRenderingMode.Disabled || !serialized.supportsLightCookies.boolValue;

            EditorGUI.BeginDisabledGroup(disableGroup);
            EditorGUILayout.PropertyField(serialized.additionalLightCookieResolutionProp, Styles.additionalLightsCookieResolution);
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(disableGroup);
            EditorGUILayout.PropertyField(serialized.additionalLightCookieFormatProp, Styles.additionalLightsCookieFormat);
            EditorGUI.EndDisabledGroup();

            EditorGUI.indentLevel--;
            EditorGUILayout.Space();

            // Sky

            // Reflection Probes
            EditorGUILayout.LabelField(Styles.reflectionProbesSettingsText);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(serialized.reflectionProbeBlendingProp, Styles.reflectionProbeBlendingText);
            EditorGUI.indentLevel++;
            EditorGUI.BeginDisabledGroup(!serialized.reflectionProbeBlendingProp.boolValue);
            EditorGUILayout.PropertyField(serialized.reflectionProbeAtlasProp, Styles.reflectionProbeAtlasText);
            EditorGUI.EndDisabledGroup();

            // Disable probeAtlas when probeBlending is off.
            if (!serialized.reflectionProbeBlendingProp.boolValue)
                serialized.reflectionProbeAtlasProp.boolValue = false;

            if ((GPUResidentDrawerMode)serialized.gpuResidentDrawerMode.intValue != GPUResidentDrawerMode.Disabled)
            {
                if (!serialized.reflectionProbeBlendingProp.boolValue || !serialized.reflectionProbeAtlasProp.boolValue)
                    EditorGUILayout.HelpBox(Styles.reflectionProbeBlendingGpuResidentDrawerWarningText.text, MessageType.Warning, true);
            }

            EditorGUI.indentLevel--;

            EditorGUILayout.PropertyField(serialized.reflectionProbeBoxProjectionProp, Styles.reflectionProbeBoxProjectionText);

            EditorGUI.indentLevel--;

            // Disable probeAtlas when probeBlending is off.
            if (!serialized.reflectionProbeBlendingProp.boolValue)
                serialized.reflectionProbeAtlasProp.boolValue = false;

            if ((GPUResidentDrawerMode)serialized.gpuResidentDrawerMode.intValue != GPUResidentDrawerMode.Disabled)
            {
                if (!serialized.reflectionProbeBlendingProp.boolValue || !serialized.reflectionProbeAtlasProp.boolValue)
                    EditorGUILayout.HelpBox(Styles.reflectionProbeBlendingGpuResidentDrawerWarningText.text, MessageType.Warning, true);
            }

            EditorGUILayout.Space();

            // Probe volumes
            EditorGUILayout.PropertyField(serialized.lightProbeSystem, Styles.lightProbeSystemContent);
            bool aprtvSelected = serialized.lightProbeSystem.intValue == (int)LightProbeSystem.PrecomputeRadianceTransfer;
            if (serialized.lightProbeSystem.intValue == (int)LightProbeSystem.ProbeVolumes || aprtvSelected)
            {
                EditorGUI.indentLevel++;

                if (!aprtvSelected)
                {
                    EditorGUILayout.PropertyField(serialized.probeVolumeTextureSize, Styles.probeVolumeMemoryBudget);
                    EditorGUILayout.PropertyField(serialized.probeVolumeSHBands, Styles.probeVolumeSHBands);

                    EditorGUILayout.PropertyField(serialized.supportProbeVolumeGPUStreaming, Styles.supportProbeVolumeGPUStreaming);
                    EditorGUI.BeginDisabledGroup(!serialized.supportProbeVolumeGPUStreaming.boolValue);
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(serialized.supportProbeVolumeDiskStreaming, Styles.supportProbeVolumeDiskStreaming);
                    EditorGUI.indentLevel--;
                    EditorGUI.EndDisabledGroup();
                }

                EditorGUILayout.PropertyField(serialized.supportProbeVolumeScenarios, aprtvSelected ? Styles.supportAPRTVScenarios : Styles.supportProbeVolumeScenarios);
                if (serialized.supportProbeVolumeScenarios.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(serialized.supportProbeVolumeScenarioBlending, aprtvSelected ? Styles.supportAPRTVScenarioBlending : Styles.supportProbeVolumeScenarioBlending);
                    if (!aprtvSelected && serialized.supportProbeVolumeScenarioBlending.boolValue)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.PropertyField(serialized.probeVolumeBlendingTextureSize, Styles.probeVolumeBlendingMemoryBudget);
                        EditorGUI.indentLevel--;
                    }
                    EditorGUI.indentLevel--;
                }

                int estimatedVMemCost = ProbeReferenceVolumeProvider.instance.GetVideoMemoryCost();
                if (estimatedVMemCost == 0)
                    EditorGUILayout.HelpBox($"Estimated GPU Memory cost: 0.\nProbe reference volume is not used in the scene and resources haven't been allocated yet.", MessageType.Info, wide: true);
                else
                    EditorGUILayout.HelpBox($"Estimated GPU Memory cost: {estimatedVMemCost / (1000 * 1000)} MB.", MessageType.Info, wide: true);

                EditorGUI.indentLevel--;
                EditorGUILayout.Space();
            }
        }

        static void DrawLightingAdditional(SerializedUltraRenderPipelineAsset serialized, Editor ownerEditor)
        {
            EditorGUILayout.PropertyField(serialized.mixedLightingSupportedProp, Styles.mixedLightingSupportLabel);
            EditorGUILayout.PropertyField(serialized.useRenderingLayers, Styles.useRenderingLayers);
            EditorGUILayout.PropertyField(serialized.supportsLightCookies, Styles.supportsLightCookies);
            EditorGUILayout.PropertyField(serialized.supportsAreaLights, Styles.supportsAreaLights);
            EditorGUILayout.PropertyField(serialized.shEvalModeProp, Styles.shEvalModeText);

            if (serialized.useRenderingLayers.boolValue && !ValidateRendererGraphicsAPIsForLightLayers(serialized.asset, out var unsupportedGraphicsApisMessage))
                EditorGUILayout.HelpBox(Styles.lightlayersUnsupportedMessage.text + unsupportedGraphicsApisMessage, MessageType.Warning, true);
        }

        static void DrawShadowResolutionTierSettings(SerializedUltraRenderPipelineAsset serialized, Editor ownerEditor)
        {
            var rect = GUILayoutUtility.GetRect(0, float.Epsilon, EditorGUIUtility.singleLineHeight, EditorGUIUtility.singleLineHeight);
            var contentRect = EditorGUI.PrefixLabel(rect, Styles.additionalLightsShadowResolutionTiers);

            EditorGUI.BeginChangeCheck();

            const int k_ShadowResolutionTiersCount = 3;
            var values = new[] { serialized.additionalLightsShadowResolutionTierLowProp, serialized.additionalLightsShadowResolutionTierMediumProp, serialized.additionalLightsShadowResolutionTierHighProp };

            var num = contentRect.width / (float)k_ShadowResolutionTiersCount;  // space allocated for every field including the label

            var indentLevel = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0; // Reset the indentation

            float pixelShift = 0;  // Variable to keep track of the current pixel shift in the rectangle we were assigned for this whole section.
            for (var index = 0; index < k_ShadowResolutionTiersCount; ++index)
            {
                var labelWidth = Mathf.Clamp(EditorStyles.label.CalcSize(Styles.additionalLightsShadowResolutionTierNames[index]).x, 0, num);
                EditorGUI.LabelField(new Rect(contentRect.x + pixelShift, contentRect.y, labelWidth, contentRect.height), Styles.additionalLightsShadowResolutionTierNames[index]);
                pixelShift += labelWidth;           // We need to remove from the position the label size that we've just drawn and shift by it's length
                float spaceLeft = num - labelWidth; // The amount of space left for the field
                if (spaceLeft > 2) // If at least two pixels are left to draw this field, draw it, otherwise, skip
                {
                    var fieldSlot = new Rect(contentRect.x + pixelShift, contentRect.y, num - labelWidth, contentRect.height); // Define the rectangle for the field
                    int value = EditorGUI.DelayedIntField(fieldSlot, values[index].intValue);
                    values[index].intValue = Mathf.Max(UltraAdditionalLightData.additionalLightsShadowMinimumResolution, Mathf.NextPowerOfTwo(value));
                }
                pixelShift += spaceLeft;  // Shift by the slot that was left for the field
            }

            EditorGUI.indentLevel = indentLevel;

            EditorGUI.EndChangeCheck();
        }

        static void DrawShadows(SerializedUltraRenderPipelineAsset serialized, Editor ownerEditor)
        {
            serialized.shadowDistanceProp.floatValue = Mathf.Max(0.0f, EditorGUILayout.FloatField(Styles.shadowDistanceText, serialized.shadowDistanceProp.floatValue));
            EditorUtils.Unit unit = EditorUtils.Unit.Metric;
            if (serialized.shadowCascadeCountProp.intValue != 0)
            {
                EditorGUI.BeginChangeCheck();
                unit = (EditorUtils.Unit)EditorGUILayout.EnumPopup(Styles.shadowWorkingUnitText, serialized.state.value);
                if (EditorGUI.EndChangeCheck())
                {
                    serialized.state.value = unit;
                }
            }

            EditorGUILayout.IntSlider(serialized.shadowCascadeCountProp, UltraRenderPipelineAsset.shadowCascadeMinCount, UltraRenderPipelineAsset.shadowCascadeMaxCount, Styles.shadowCascadesText);

            int cascadeCount = serialized.shadowCascadeCountProp.intValue;
            EditorGUI.indentLevel++;

            bool useMetric = unit == EditorUtils.Unit.Metric;
            float baseMetric = serialized.shadowDistanceProp.floatValue;
            int cascadeSplitCount = cascadeCount - 1;

            DrawCascadeSliders(serialized, cascadeSplitCount, useMetric, baseMetric);

            EditorGUI.indentLevel--;
            DrawCascades(serialized, cascadeCount, useMetric, baseMetric);
            EditorGUI.indentLevel++;

            serialized.shadowDepthBiasProp.floatValue = EditorGUILayout.Slider(Styles.shadowDepthBias, serialized.shadowDepthBiasProp.floatValue, 0.0f, UltraRenderPipeline.maxShadowBias);
            serialized.shadowNormalBiasProp.floatValue = EditorGUILayout.Slider(Styles.shadowNormalBias, serialized.shadowNormalBiasProp.floatValue, 0.0f, UltraRenderPipeline.maxShadowBias);
            EditorGUILayout.PropertyField(serialized.softShadowsSupportedProp, Styles.supportsSoftShadows);
            if (serialized.softShadowsSupportedProp.boolValue)
            {
                EditorGUI.indentLevel++;
                DrawShadowsSoftShadowQuality(serialized, ownerEditor);
                EditorGUI.indentLevel--;
            }

            EditorGUI.indentLevel--;
        }

        static void DrawShadowsSoftShadowQuality(SerializedUltraRenderPipelineAsset serialized, Editor ownerEditor)
        {
            int selectedAssetSoftShadowQuality = serialized.softShadowQualityProp.intValue;
            Rect r = EditorGUILayout.GetControlRect(true);
            EditorGUI.BeginProperty(r, Styles.softShadowsQuality, serialized.softShadowQualityProp);
            {
                using (var checkScope = new EditorGUI.ChangeCheckScope())
                {
                    selectedAssetSoftShadowQuality = EditorGUI.IntPopup(r, Styles.softShadowsQuality, selectedAssetSoftShadowQuality, Styles.softShadowsQualityAssetOptions, Styles.softShadowsQualityAssetValues);
                    if (checkScope.changed)
                    {
                        serialized.softShadowQualityProp.intValue = Math.Clamp(selectedAssetSoftShadowQuality, (int)SoftShadowQuality.Low, (int)SoftShadowQuality.High);
                    }
                }
            }
            EditorGUI.EndProperty();
        }

        static void DrawShadowsAdditional(SerializedUltraRenderPipelineAsset serialized, Editor ownerEditor)
        {
            EditorGUILayout.PropertyField(serialized.conservativeEnclosingSphereProp, Styles.conservativeEnclosingSphere);
        }

        static void DrawCascadeSliders(SerializedUltraRenderPipelineAsset serialized, int splitCount, bool useMetric, float baseMetric)
        {
            Vector4 shadowCascadeSplit = Vector4.one;
            if (splitCount == 3)
                shadowCascadeSplit = new Vector4(serialized.shadowCascade4SplitProp.vector3Value.x, serialized.shadowCascade4SplitProp.vector3Value.y, serialized.shadowCascade4SplitProp.vector3Value.z, 1);
            else if (splitCount == 2)
                shadowCascadeSplit = new Vector4(serialized.shadowCascade3SplitProp.vector2Value.x, serialized.shadowCascade3SplitProp.vector2Value.y, 1, 0);
            else if (splitCount == 1)
                shadowCascadeSplit = new Vector4(serialized.shadowCascade2SplitProp.floatValue, 1, 0, 0);

            float splitBias = 0.001f;
            float invBaseMetric = baseMetric == 0 ? 0 : 1f / baseMetric;

            // Ensure correct split order
            shadowCascadeSplit[0] = Mathf.Clamp(shadowCascadeSplit[0], 0f, shadowCascadeSplit[1] - splitBias);
            shadowCascadeSplit[1] = Mathf.Clamp(shadowCascadeSplit[1], shadowCascadeSplit[0] + splitBias, shadowCascadeSplit[2] - splitBias);
            shadowCascadeSplit[2] = Mathf.Clamp(shadowCascadeSplit[2], shadowCascadeSplit[1] + splitBias, shadowCascadeSplit[3] - splitBias);


            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < splitCount; ++i)
            {
                float value = shadowCascadeSplit[i];

                float minimum = i == 0 ? 0 : shadowCascadeSplit[i - 1] + splitBias;
                float maximum = i == splitCount - 1 ? 1 : shadowCascadeSplit[i + 1] - splitBias;

                if (useMetric)
                {
                    float valueMetric = value * baseMetric;
                    valueMetric = EditorGUILayout.Slider(EditorGUIUtility.TrTextContent($"Split {i + 1}", "The distance where this cascade ends and the next one starts."), valueMetric, 0f, baseMetric, null);

                    shadowCascadeSplit[i] = Mathf.Clamp(valueMetric * invBaseMetric, minimum, maximum);
                }
                else
                {
                    float valueProcentage = value * 100f;
                    valueProcentage = EditorGUILayout.Slider(EditorGUIUtility.TrTextContent($"Split {i + 1}", "The distance where this cascade ends and the next one starts."), valueProcentage, 0f, 100f, null);

                    shadowCascadeSplit[i] = Mathf.Clamp(valueProcentage * 0.01f, minimum, maximum);
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                switch (splitCount)
                {
                    case 3:
                        serialized.shadowCascade4SplitProp.vector3Value = shadowCascadeSplit;
                        break;
                    case 2:
                        serialized.shadowCascade3SplitProp.vector2Value = shadowCascadeSplit;
                        break;
                    case 1:
                        serialized.shadowCascade2SplitProp.floatValue = shadowCascadeSplit.x;
                        break;
                }
            }

            var borderValue = serialized.shadowCascadeBorderProp.floatValue;

            EditorGUI.BeginChangeCheck();
            if (useMetric)
            {
                var lastCascadeSplitSize = splitCount == 0 ? baseMetric : (1.0f - shadowCascadeSplit[splitCount - 1]) * baseMetric;
                var invLastCascadeSplitSize = lastCascadeSplitSize == 0 ? 0 : 1f / lastCascadeSplitSize;
                float valueMetric = borderValue * lastCascadeSplitSize;
                valueMetric = EditorGUILayout.Slider(EditorGUIUtility.TrTextContent("Last Border", "The distance of the last cascade."), valueMetric, 0f, lastCascadeSplitSize, null);

                borderValue = valueMetric * invLastCascadeSplitSize;
            }
            else
            {
                float valueProcentage = borderValue * 100f;
                valueProcentage = EditorGUILayout.Slider(EditorGUIUtility.TrTextContent("Last Border", "The distance of the last cascade."), valueProcentage, 0f, 100f, null);

                borderValue = valueProcentage * 0.01f;
            }

            if (EditorGUI.EndChangeCheck())
            {
                serialized.shadowCascadeBorderProp.floatValue = borderValue;
            }
        }

        static void DrawCascades(SerializedUltraRenderPipelineAsset serialized, int cascadeCount, bool useMetric, float baseMetric)
        {
            var cascades = new ShadowCascadeGUI.Cascade[cascadeCount];

            Vector3 shadowCascadeSplit = Vector3.zero;
            if (cascadeCount == 4)
                shadowCascadeSplit = serialized.shadowCascade4SplitProp.vector3Value;
            else if (cascadeCount == 3)
                shadowCascadeSplit = serialized.shadowCascade3SplitProp.vector2Value;
            else if (cascadeCount == 2)
                shadowCascadeSplit.x = serialized.shadowCascade2SplitProp.floatValue;
            else
                shadowCascadeSplit.x = serialized.shadowCascade2SplitProp.floatValue;

            float lastCascadePartitionSplit = 0;
            for (int i = 0; i < cascadeCount - 1; ++i)
            {
                cascades[i] = new ShadowCascadeGUI.Cascade()
                {
                    size = i == 0 ? shadowCascadeSplit[i] : shadowCascadeSplit[i] - lastCascadePartitionSplit, // Calculate the size of cascade
                    borderSize = 0,
                    cascadeHandleState = ShadowCascadeGUI.HandleState.Enabled,
                    borderHandleState = ShadowCascadeGUI.HandleState.Hidden,
                };
                lastCascadePartitionSplit = shadowCascadeSplit[i];
            }

            // Last cascade is special
            var lastCascade = cascadeCount - 1;
            cascades[lastCascade] = new ShadowCascadeGUI.Cascade()
            {
                size = lastCascade == 0 ? 1.0f : 1 - shadowCascadeSplit[lastCascade - 1], // Calculate the size of cascade
                borderSize = serialized.shadowCascadeBorderProp.floatValue,
                cascadeHandleState = ShadowCascadeGUI.HandleState.Hidden,
                borderHandleState = ShadowCascadeGUI.HandleState.Enabled,
            };

            EditorGUI.BeginChangeCheck();
            ShadowCascadeGUI.DrawCascades(ref cascades, useMetric, baseMetric);
            if (EditorGUI.EndChangeCheck())
            {
                if (cascadeCount == 4)
                    serialized.shadowCascade4SplitProp.vector3Value = new Vector3(
                        cascades[0].size,
                        cascades[0].size + cascades[1].size,
                        cascades[0].size + cascades[1].size + cascades[2].size
                    );
                else if (cascadeCount == 3)
                    serialized.shadowCascade3SplitProp.vector2Value = new Vector2(
                        cascades[0].size,
                        cascades[0].size + cascades[1].size
                    );
                else if (cascadeCount == 2)
                    serialized.shadowCascade2SplitProp.floatValue = cascades[0].size;

                serialized.shadowCascadeBorderProp.floatValue = cascades[lastCascade].borderSize;
            }
        }

        static void DrawerSectionLightingUnsorted(SerializedUltraRenderPipelineAsset serialized, Editor owner)
        {

        }

        static void DrawVirtualTexturing(SerializedUltraRenderPipelineAsset serialized, Editor owner)
        {
            virtualTexturingSettingsUI.OnGUI(serialized, owner);
        }

        static Editor s_volumeProfileEditor;
        static void DrawVolumes(SerializedUltraRenderPipelineAsset serialized, Editor ownerEditor)
        {
            CoreEditorUtils.DrawPopup(Styles.volumeFrameworkUpdateMode, serialized.volumeFrameworkUpdateModeProp, Styles.volumeFrameworkUpdateOptions);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(serialized.volumeProfileProp, Styles.volumeProfileLabel);
            var profile = serialized.volumeProfileProp.objectReferenceValue as VolumeProfile;
            if (EditorGUI.EndChangeCheck() && UltraRenderPipeline.asset == serialized.serializedObject.targetObject && RenderPipelineManager.currentPipeline is UltraRenderPipeline)
                VolumeManager.instance.SetQualityDefaultProfile(serialized.volumeProfileProp.objectReferenceValue as VolumeProfile);

            var contextMenuButtonRect = GUILayoutUtility.GetRect(CoreEditorStyles.contextMenuIcon,
                Styles.volumeProfileContextMenuStyle.Value);
            if (GUI.Button(contextMenuButtonRect, CoreEditorStyles.contextMenuIcon,
                    Styles.volumeProfileContextMenuStyle.Value))
            {
                var profileEditor = s_volumeProfileEditor as VolumeProfileEditor;
                var componentEditors = profileEditor != null ? profileEditor.componentList.editors : null;
                var srpAsset = serialized.serializedObject.targetObject as UltraRenderPipelineAsset;
                var pos = new Vector2(contextMenuButtonRect.x, contextMenuButtonRect.yMax);
                VolumeProfileUtils.OnVolumeProfileContextClick(pos, srpAsset.volumeProfile, componentEditors,
                    overrideStateOnReset: false,
                    defaultVolumeProfilePath: $"Assets/{srpAsset.name}_VolumeProfile.asset",
                    onNewVolumeProfileCreated: volumeProfile =>
                    {
                        Undo.RecordObject(srpAsset, "Set UltraRenderPipelineAsset Volume Profile");
                        srpAsset.volumeProfile = volumeProfile;
                        if (UltraRenderPipeline.asset == srpAsset)
                            VolumeManager.instance.SetQualityDefaultProfile(volumeProfile);
                        EditorUtility.SetDirty(srpAsset);
                    });
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(2);

            if (profile != null)
            {
                Editor.CreateCachedEditor(profile, typeof(VolumeProfileEditor), ref s_volumeProfileEditor);
                bool oldEnabled = GUI.enabled;
                GUI.enabled = AssetDatabase.IsOpenForEdit(profile);
                s_volumeProfileEditor.OnInspectorGUI();
                GUI.enabled = oldEnabled;
            }
            else
                CoreUtils.Destroy(s_volumeProfileEditor);
        }

        private static bool HasCorrectLightingModes(UltraRenderPipelineAsset asset)
        {
            //foreach (var rendererData in asset.rendererGraphList)
            //{
            //    if (rendererData is not UltraRendererData { renderingMode: RenderingMode.ForwardPlus })
            //        return false;
            //}
            return true;
        }

        static bool ValidateRendererGraphicsAPIsForLightLayers(UltraRenderPipelineAsset pipelineAsset, out string unsupportedGraphicsApisMessage)
        {
            unsupportedGraphicsApisMessage = null;

            BuildTarget platform = EditorUserBuildSettings.activeBuildTarget;
            GraphicsDeviceType[] graphicsAPIs = PlayerSettings.GetGraphicsAPIs(platform);

            for (int apiIndex = 0; apiIndex < graphicsAPIs.Length; apiIndex++)
            {
                if (!RenderingUtils.SupportsLightLayers(graphicsAPIs[apiIndex]))
                {
                    if (unsupportedGraphicsApisMessage != null)
                        unsupportedGraphicsApisMessage += ", ";
                    unsupportedGraphicsApisMessage += System.String.Format("{0}", graphicsAPIs[apiIndex]);
                }
            }

            if (unsupportedGraphicsApisMessage != null)
                unsupportedGraphicsApisMessage += ".";

            return unsupportedGraphicsApisMessage == null;
        }

    }
}
