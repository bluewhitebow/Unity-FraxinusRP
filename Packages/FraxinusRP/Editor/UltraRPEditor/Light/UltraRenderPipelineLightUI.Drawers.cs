using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    using CED = CoreEditorDrawer<UltraRenderPipelineSerializedLight>;

    internal partial class UltraRenderPipelineLightUI
    {
        enum Expandable
        {
            General = 1 << 0,
            Shape = 1 << 1,
            Emission = 1 << 2,
            Rendering = 1 << 3,
            Shadows = 1 << 4,
            LightCookie = 1 << 5
        }

        static readonly ExpandedState<Expandable, Light> k_ExpandedState = new(~-1, "UltraRP");

        public static readonly CED.IDrawer inspector = CED.Group(
            CED.Conditional(
                (_, __) =>
                {
                    if (SceneView.lastActiveSceneView == null)
                        return false;

#if UNITY_2019_1_OR_NEWER
                    var sceneLighting = SceneView.lastActiveSceneView.sceneLighting;
#else
                    var sceneLighting = SceneView.lastActiveSceneView.m_SceneLighting;
#endif
                    return !sceneLighting;
                },
                (_, __) => EditorGUILayout.HelpBox(Styles.disabledLightWarning.text, MessageType.Warning)),
            CED.FoldoutGroup(LightUI.Styles.generalHeader,
                Expandable.General,
                k_ExpandedState,
                DrawGeneralContent),
            CED.Conditional(
                (serializedLight, editor) => !serializedLight.settings.lightType.hasMultipleDifferentValues && serializedLight.settings.light.type == LightType.Spot,
                CED.FoldoutGroup(LightUI.Styles.shapeHeader, Expandable.Shape, k_ExpandedState, DrawSpotShapeContent)),
            CED.Conditional(
                (serializedLight, editor) => !serializedLight.settings.lightType.hasMultipleDifferentValues && serializedLight.settings.light.type == LightType.Point,
                CED.FoldoutGroup(LightUI.Styles.shapeHeader, Expandable.Shape, k_ExpandedState, DrawPointShapeContent)),
            CED.Conditional(
                (serializedLight, editor) =>
                {
                    if (serializedLight.settings.lightType.hasMultipleDifferentValues)
                        return false;
                    var lightType = serializedLight.settings.light.type;
                    return lightType == LightType.Rectangle || lightType == LightType.Disc;
                },
                CED.FoldoutGroup(LightUI.Styles.shapeHeader, Expandable.Shape, k_ExpandedState, DrawAreaShapeContent)),
            CED.FoldoutGroup(LightUI.Styles.emissionHeader,
                Expandable.Emission,
                k_ExpandedState,
                CED.Group(
                    LightUI.DrawColor,
                    DrawEmissionContent)),
            CED.FoldoutGroup(LightUI.Styles.renderingHeader,
                Expandable.Rendering,
                k_ExpandedState,
                DrawRenderingContent),
            CED.FoldoutGroup(LightUI.Styles.shadowHeader,
                Expandable.Shadows,
                k_ExpandedState,
                DrawShadowsContent)
        );

        static Func<int> s_SetGizmosDirty = SetGizmosDirty();
        static Func<int> SetGizmosDirty()
        {
            var type = Type.GetType("UnityEditor.AnnotationUtility,UnityEditor");
            var method = type.GetMethod("SetGizmosDirty", BindingFlags.Static | BindingFlags.NonPublic);
            var lambda = Expression.Lambda<Func<int>>(Expression.Call(method));
            return lambda.Compile();
        }

        static Action<GUIContent, SerializedProperty, LightEditor.Settings> k_SliderWithTexture = GetSliderWithTexture();
        static Action<GUIContent, SerializedProperty, LightEditor.Settings> GetSliderWithTexture()
        {
            //quicker than standard reflection as it is compiled
            var paramLabel = Expression.Parameter(typeof(GUIContent), "label");
            var paramProperty = Expression.Parameter(typeof(SerializedProperty), "property");
            var paramSettings = Expression.Parameter(typeof(LightEditor.Settings), "settings");
            System.Reflection.MethodInfo sliderWithTextureInfo = typeof(EditorGUILayout)
                .GetMethod(
                "SliderWithTexture",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                System.Reflection.CallingConventions.Any,
                new[] { typeof(GUIContent), typeof(SerializedProperty), typeof(float), typeof(float), typeof(float), typeof(Texture2D), typeof(GUILayoutOption[]) },
                null);
            var sliderWithTextureCall = Expression.Call(
                sliderWithTextureInfo,
                paramLabel,
                paramProperty,
                Expression.Constant((float)typeof(LightEditor.Settings).GetField("kMinKelvin", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetRawConstantValue()),
                Expression.Constant((float)typeof(LightEditor.Settings).GetField("kMaxKelvin", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetRawConstantValue()),
                Expression.Constant((float)typeof(LightEditor.Settings).GetField("kSliderPower", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetRawConstantValue()),
                Expression.Field(paramSettings, typeof(LightEditor.Settings).GetField("m_KelvinGradientTexture", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)),
                Expression.Constant(null, typeof(GUILayoutOption[])));
            var lambda = Expression.Lambda<System.Action<GUIContent, SerializedProperty, LightEditor.Settings>>(sliderWithTextureCall, paramLabel, paramProperty, paramSettings);
            return lambda.Compile();
        }

        static void DrawGeneralContent(UltraRenderPipelineSerializedLight serializedLight, Editor owner)
        {
            DrawGeneralContentInternal(serializedLight, owner, isInPreset: false);
        }

        static void DrawGeneralContentPreset(UltraRenderPipelineSerializedLight serializedLight, Editor owner)
        {
            DrawGeneralContentInternal(serializedLight, owner, isInPreset: true);
        }

        static void DrawGeneralContentInternal(UltraRenderPipelineSerializedLight serializedLight, Editor owner, bool isInPreset)
        {
            // To the user, we will only display it as a area light, but under the hood, we have Rectangle and Disc. This is not to confuse people
            // who still use our legacy light inspector.

            int selectedLightType = serializedLight.settings.lightType.intValue;

            // Handle all lights that are not in the default set
            if (!Styles.lightTypeValues.Contains(serializedLight.settings.lightType.intValue))
            {
                if (serializedLight.settings.lightType.intValue == (int)LightType.Disc)
                {
                    selectedLightType = (int)LightType.Rectangle;
                }
            }

            var rect = EditorGUILayout.GetControlRect();
            EditorGUI.BeginProperty(rect, Styles.type, serializedLight.settings.lightType);
            EditorGUI.BeginChangeCheck();
            int type;
            if (Styles.lightTypeValues.Contains(selectedLightType))
            {
                // ^ The currently selected light type is supported in the
                // current pipeline.
                type = EditorGUI.IntPopup(rect, Styles.type, selectedLightType, Styles.lightTypeTitles, Styles.lightTypeValues);
            }
            else
            {
                // ^ The currently selected light type is not supported in
                // the current pipeline. Add it to the dropdown, since it
                // would show up as a blank entry.
                string currentTitle = ((LightType)selectedLightType).ToString();
                GUIContent[] titles = Styles.lightTypeTitles.Append(EditorGUIUtility.TrTextContent(currentTitle)).ToArray();
                int[] values = Styles.lightTypeValues.Append(selectedLightType).ToArray();
                type = EditorGUI.IntPopup(rect, Styles.type, selectedLightType, titles, values);
            }

            if (EditorGUI.EndChangeCheck())
            {
                s_SetGizmosDirty();
                serializedLight.settings.lightType.intValue = type;
            }
            EditorGUI.EndProperty();

            if (!Styles.lightTypeValues.Contains(type))
            {
                EditorGUILayout.HelpBox(
                    "This light type is not supported in the current active render pipeline. Change the light type or the active Render Pipeline to use this light.",
                    MessageType.Info
                );
            }

            Light light = serializedLight.settings.light;
            var lightType = light.type;
            if (LightType.Directional != lightType && light == RenderSettings.sun)
            {
                EditorGUILayout.HelpBox(Styles.sunSourceWarning.text, MessageType.Warning);
            }

            if (!serializedLight.settings.lightType.hasMultipleDifferentValues)
            {
                bool isRectangle = serializedLight.settings.lightType.intValue == (int)LightType.Rectangle;
                bool isDisc = serializedLight.settings.lightType.intValue == (int)LightType.Disc;
                if (isRectangle)
                {
                    var mode = serializedLight.settings.lightmapping;
                    bool hasMixedMode = mode.intValue == (int)LightmapBakeType.Mixed;
                    var modeRect = EditorGUILayout.GetControlRect();
                    EditorGUI.BeginProperty(modeRect, Styles.areaLightMode, mode);
                    EditorGUI.BeginChangeCheck();
                    int selectedMode = EditorGUI.IntPopup(modeRect, Styles.areaLightMode, mode.intValue,
                        hasMixedMode ? Styles.areaLightMixedModeTitles : Styles.areaLightModeTitles,
                        hasMixedMode ? Styles.areaLightMixedModeValues : Styles.areaLightModeValues);
                    if (EditorGUI.EndChangeCheck())
                        mode.intValue = selectedMode;
                    EditorGUI.EndProperty();
                    if (mode.intValue == (int)LightmapBakeType.Mixed && !mode.hasMultipleDifferentValues)
                        EditorGUILayout.HelpBox("Mixed rectangle lighting is not supported. Choose Realtime for LTC lighting or Baked for lightmapped lighting.", MessageType.Info);
                }
                else
                {
                    using (new EditorGUI.DisabledScope(isDisc))
                        serializedLight.settings.DrawLightmapping();
                }

                if (isDisc && serializedLight.settings.lightmapping.intValue != (int)LightmapBakeType.Baked)
                {
                    serializedLight.settings.lightmapping.intValue = (int)LightmapBakeType.Baked;
                    serializedLight.Apply();
                }
            }
        }

        internal static void SyncLightAndShadowLayers(UltraRenderPipelineSerializedLight serializedLight, SerializedProperty serialized)
        {
            // If we're not in decoupled mode for light layers, we sync light with shadow layers.
            // In mixed state, it makes sense to do it only on Light that links the mode.
            foreach (var lightTarget in serializedLight.serializedObject.targetObjects)
            {
                var additionData = (lightTarget as Component).gameObject.GetComponent<UltraAdditionalLightData>();
                if (additionData.customShadowLayers)
                    continue;

                Light target = lightTarget as Light;
                if (target.renderingLayerMask != serialized.intValue)
                    target.renderingLayerMask = serialized.intValue;
            }
        }

        static void DrawSpotShapeContent(UltraRenderPipelineSerializedLight serializedLight, Editor owner)
        {
            serializedLight.settings.DrawInnerAndOuterSpotAngle();
        }

        static void DrawPointShapeContent(UltraRenderPipelineSerializedLight serializedLight, Editor owner)
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(serializedLight.shapeRadius, Styles.lightRadius);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(serializedLight.settings.light, "Adjust Point Light Shape");
                serializedLight.shapeRadius.floatValue = Mathf.Clamp(serializedLight.shapeRadius.floatValue, 0, 30);
            }
        }

        static void DrawAreaShapeContent(UltraRenderPipelineSerializedLight serializedLight, Editor owner)
        {
            int selectedShape = serializedLight.settings.isAreaLightType ? serializedLight.settings.lightType.intValue : 0;

            // Handle all lights that are not in the default set
            if (!Styles.lightTypeValues.Contains(serializedLight.settings.lightType.intValue))
            {
                if (serializedLight.settings.lightType.intValue == (int)LightType.Disc)
                {
                    selectedShape = (int)LightType.Disc;
                }
            }

            var rect = EditorGUILayout.GetControlRect();
            EditorGUI.BeginProperty(rect, Styles.areaLightShapeContent, serializedLight.settings.lightType);
            EditorGUI.BeginChangeCheck();
            int shape = EditorGUI.IntPopup(rect, Styles.areaLightShapeContent, selectedShape, Styles.areaLightShapeTitles, Styles.areaLightShapeValues);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(serializedLight.settings.light, "Adjust Light Shape");
                serializedLight.settings.lightType.intValue = shape;
            }
            EditorGUI.EndProperty();

            using (new EditorGUI.IndentLevelScope())
            {
                if (shape == (int)LightType.Rectangle)
                {
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(serializedLight.areaSize, Styles.areaSize);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObjects(serializedLight.serializedObject.targetObjects, "Adjust Rectangle Light Size");
                        Vector2 size = serializedLight.areaSize.vector2Value;
                        size.x = Mathf.Max(UltraAdditionalLightData.minimumAreaLightSize, size.x);
                        size.y = Mathf.Max(UltraAdditionalLightData.minimumAreaLightSize, size.y);
                        serializedLight.areaSize.vector2Value = size;
                    }
                }
                else
                    serializedLight.settings.DrawArea();
            }
        }

        static void DrawEmissionContent(UltraRenderPipelineSerializedLight serializedLight, Editor owner)
        {
            serializedLight.settings.DrawIntensity();
            serializedLight.settings.DrawBounceIntensity();

            if (!serializedLight.settings.lightType.hasMultipleDifferentValues)
            {
                var lightType = serializedLight.settings.light.type;
                if (lightType != LightType.Directional)
                {
#if UNITY_2020_1_OR_NEWER
                    serializedLight.settings.DrawRange();
#else
                    serializedLight.settings.DrawRange(false);
#endif
                }
            }

            DrawLightCookieContent(serializedLight, owner);

            if (!serializedLight.settings.lightType.hasMultipleDifferentValues &&
                serializedLight.settings.lightType.intValue == (int)LightType.Rectangle &&
                !serializedLight.settings.lightmapping.hasMultipleDifferentValues &&
                serializedLight.settings.lightmapping.intValue == (int)LightmapBakeType.Realtime)
            {
                EditorGUILayout.Slider(serializedLight.areaDiffuseDimmer, 0f, 1f, Styles.areaDiffuseDimmer);
                EditorGUILayout.Slider(serializedLight.areaSpecularDimmer, 0f, 1f, Styles.areaSpecularDimmer);
            }
        }

        static void DrawRenderingContent(UltraRenderPipelineSerializedLight serializedLight, Editor owner)
        {
            if (!serializedLight.settings.isCompletelyBaked)
            {
                EditorGUI.BeginChangeCheck();
                GUI.enabled = UltraRenderPipeline.asset.useRenderingLayers;
                EditorUtils.DrawRenderingLayerMask(
                    serializedLight.renderingLayers,
                    UltraRenderPipeline.asset.useRenderingLayers ? Styles.renderingLayers : Styles.renderingLayersDisabled
                );
                GUI.enabled = true;
                if (EditorGUI.EndChangeCheck())
                {
                    if (!serializedLight.customShadowLayers.boolValue)
                        SyncLightAndShadowLayers(serializedLight, serializedLight.renderingLayers);
                }
            }

            if (!serializedLight.settings.lightType.hasMultipleDifferentValues &&
                serializedLight.settings.lightType.intValue == (int)LightType.Rectangle &&
                !serializedLight.settings.lightmapping.hasMultipleDifferentValues &&
                serializedLight.settings.lightmapping.intValue == (int)LightmapBakeType.Realtime)
            {
                EditorGUILayout.HelpBox("Use Rendering Layers to control which objects receive rectangle lighting.", MessageType.Info);
                return;
            }

            EditorGUILayout.PropertyField(serializedLight.settings.cullingMask, Styles.cullingMask);
            if (serializedLight.settings.cullingMask.intValue != -1)
            {
                EditorGUILayout.HelpBox(Styles.cullingMaskWarning.text, MessageType.Info);
            }
        }

        static void DrawShadowsContent(UltraRenderPipelineSerializedLight serializedLight, Editor owner)
        {
            if (serializedLight.settings.lightType.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox("Cannot multi edit shadows from different light types.", MessageType.Info);
                return;
            }

            if (serializedLight.settings.lightType.intValue == (int)LightType.Rectangle &&
                !serializedLight.settings.isCompletelyBaked)
            {
                EditorGUILayout.HelpBox("Realtime rectangle lights currently render without shadows.", MessageType.Info);
                return;
            }

            serializedLight.settings.DrawShadowsType();

            if (serializedLight.settings.shadowsType.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox("Cannot multi edit different shadow types", MessageType.Info);
                return;
            }

            if (serializedLight.settings.light.shadows == LightShadows.None)
                return;

            var lightType = serializedLight.settings.light.type;

            using (new EditorGUI.IndentLevelScope())
            {
                if (serializedLight.settings.isBakedOrMixed)
                {
                    switch (lightType)
                    {
                        // Baked Shadow radius
                        case LightType.Point:
                        case LightType.Spot:
                            serializedLight.settings.DrawBakedShadowRadius();
                            break;
                        case LightType.Directional:
                            serializedLight.settings.DrawBakedShadowAngle();
                            break;
                    }
                }

                if (lightType != LightType.Rectangle && !serializedLight.settings.isCompletelyBaked)
                {
                    EditorGUILayout.LabelField(Styles.shadowRealtimeSettings, EditorStyles.boldLabel);
                    using (new EditorGUI.IndentLevelScope())
                    {
                        // Resolution
                        if (lightType == LightType.Point || lightType == LightType.Spot)
                            DrawShadowsResolutionGUI(serializedLight);

                        EditorGUILayout.Slider(serializedLight.settings.shadowsStrength, 0f, 1f, Styles.shadowStrength);

                        // Bias
                        DrawAdditionalShadowData(serializedLight, owner);

                        // this min bound should match the calculation in SharedLightData::GetNearPlaneMinBound()
                        float nearPlaneMinBound = Mathf.Min(0.01f * serializedLight.settings.range.floatValue, 0.1f);
                        EditorGUILayout.Slider(serializedLight.settings.shadowsNearPlane, nearPlaneMinBound, 10.0f, Styles.shadowNearPlane);
                        var isHololens = false;
                        var isQuest = false;
#if XR_MANAGEMENT_4_0_1_OR_NEWER
                        var buildTargetGroup = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
                        var buildTargetSettings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(buildTargetGroup);
                        if (buildTargetSettings != null && buildTargetSettings.AssignedSettings != null && buildTargetSettings.AssignedSettings.activeLoaders.Count > 0)
                        {
                            isHololens = buildTargetGroup == BuildTargetGroup.WSA;
                            isQuest = buildTargetGroup == BuildTargetGroup.Android;
                        }

#endif
                        // Soft Shadow Quality
                        if (serializedLight.settings.light.shadows == LightShadows.Soft)
                            EditorGUILayout.PropertyField(serializedLight.softShadowQualityProp, Styles.softShadowQuality);

                        if (isHololens || isQuest)
                        {
                            EditorGUILayout.HelpBox(
                                "Per-light soft shadow quality level is not supported on HoloLens and Oculus platforms. Use the Soft Shadow Quality setting in the UltraRP Asset instead",
                                MessageType.Warning
                            );
                        }

                    }

                    if (UltraRenderPipeline.asset.useRenderingLayers)
                    {
                        EditorGUI.BeginChangeCheck();
                        EditorGUILayout.PropertyField(serializedLight.customShadowLayers, Styles.customShadowLayers);
                        // Undo the changes in the light component because the SyncLightAndShadowLayers will change the value automatically when link is ticked
                        if (EditorGUI.EndChangeCheck())
                        {
                            if (serializedLight.customShadowLayers.boolValue)
                            {
                                serializedLight.settings.light.renderingLayerMask = serializedLight.shadowRenderingLayers.intValue;
                            }
                            else
                            {
                                serializedLight.serializedAdditionalDataObject.ApplyModifiedProperties(); // we need to push above modification the modification on object as it is used to sync
                                SyncLightAndShadowLayers(serializedLight, serializedLight.renderingLayers);
                            }
                        }

                        if (serializedLight.customShadowLayers.boolValue)
                        {
                            using (new EditorGUI.IndentLevelScope())
                            {
                                EditorGUI.BeginChangeCheck();
                                EditorUtils.DrawRenderingLayerMask(serializedLight.shadowRenderingLayers, Styles.shadowLayer);
                                if (EditorGUI.EndChangeCheck())
                                {
                                    serializedLight.settings.light.renderingLayerMask = serializedLight.shadowRenderingLayers.intValue;
                                    serializedLight.Apply();
                                }
                            }
                        }
                    }
                }
            }

            if (!UnityEditor.Lightmapping.bakedGI && !serializedLight.settings.lightmapping.hasMultipleDifferentValues && serializedLight.settings.isBakedOrMixed)
                EditorGUILayout.HelpBox(Styles.bakingWarning.text, MessageType.Warning);
        }

        static void DrawAdditionalShadowData(UltraRenderPipelineSerializedLight serializedLight, Editor editor)
        {
            // 0: Custom bias - 1: Bias values defined in Pipeline settings
            int selectedUseAdditionalData = serializedLight.additionalLightData.usePipelineSettings ? 1 : 0;
            Rect r = EditorGUILayout.GetControlRect(true);
            EditorGUI.BeginProperty(r, Styles.shadowBias, serializedLight.useAdditionalDataProp);
            {
                using (var checkScope = new EditorGUI.ChangeCheckScope())
                {
                    selectedUseAdditionalData = EditorGUI.IntPopup(r, Styles.shadowBias, selectedUseAdditionalData, Styles.displayedDefaultOptions, Styles.optionDefaultValues);
                    if (checkScope.changed)
                    {
                        Undo.RecordObjects(serializedLight.lightsAdditionalData, "Modified light additional data");
                        foreach (var additionData in serializedLight.lightsAdditionalData)
                            additionData.usePipelineSettings = selectedUseAdditionalData != 0;

                        serializedLight.Apply();
                        (editor as UltraRenderPipelineLightEditor)?.ReconstructReferenceToAdditionalDataSO();
                    }
                }
            }
            EditorGUI.EndProperty();

            if (!serializedLight.useAdditionalDataProp.hasMultipleDifferentValues)
            {
                if (selectedUseAdditionalData != 1) // Custom Bias
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        using (var checkScope = new EditorGUI.ChangeCheckScope())
                        {
                            EditorGUILayout.Slider(serializedLight.settings.shadowsBias, 0f, 10f, Styles.shadowDepthBias);
                            EditorGUILayout.Slider(serializedLight.settings.shadowsNormalBias, 0f, 10f, Styles.shadowNormalBias);
                            if (checkScope.changed)
                                serializedLight.Apply();
                        }
                    }
                }
            }
        }

        static void DrawShadowsResolutionGUI(UltraRenderPipelineSerializedLight serializedLight)
        {
            int shadowResolutionTier = serializedLight.additionalLightData.additionalLightsShadowResolutionTier;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (var checkScope = new EditorGUI.ChangeCheckScope())
                {
                    Rect r = EditorGUILayout.GetControlRect(true);
                    r.width += 30;

                    shadowResolutionTier = EditorGUI.IntPopup(r, Styles.shadowResolution, shadowResolutionTier, Styles.shadowResolutionDefaultOptions, Styles.shadowResolutionDefaultValues);
                    if (shadowResolutionTier == UltraAdditionalLightData.additionalLightsShadowResolutionTierCustom)
                    {
                        // show the custom value field GUI.
                        var newResolution = EditorGUILayout.IntField(serializedLight.settings.shadowsResolution.intValue, GUILayout.ExpandWidth(false));
                        serializedLight.settings.shadowsResolution.intValue = Mathf.Max(UltraAdditionalLightData.additionalLightsShadowMinimumResolution, Mathf.NextPowerOfTwo(newResolution));
                    }
                    else
                    {
                        if (GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset urpAsset)
                            EditorGUILayout.LabelField($"{urpAsset.GetAdditionalLightsShadowResolution(shadowResolutionTier)} ({urpAsset.name})", GUILayout.ExpandWidth(false));
                    }
                    if (checkScope.changed)
                    {
                        serializedLight.additionalLightsShadowResolutionTierProp.intValue = shadowResolutionTier;
                        serializedLight.Apply();
                    }
                }
            }

            EditorGUILayout.HelpBox(Styles.shadowInfo.text, MessageType.Info);
        }

        static void DrawLightCookieContent(UltraRenderPipelineSerializedLight serializedLight, Editor owner)
        {
            var settings = serializedLight.settings;
            if (settings.lightType.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox("Cannot multi edit light cookies from different light types.", MessageType.Info);
                return;
            }

            if (settings.lightType.intValue == (int)LightType.Rectangle && !settings.isCompletelyBaked)
                return;

            settings.DrawCookie();

            // Draw 2D cookie size for directional lights
            bool isDirectionalLight = settings.light.type == LightType.Directional;
            if (isDirectionalLight)
            {
                if (settings.cookie != null)
                {
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(serializedLight.lightCookieSizeProp, Styles.lightCookieSize);
                    EditorGUILayout.PropertyField(serializedLight.lightCookieOffsetProp, Styles.lightCookieOffset);
                    if (EditorGUI.EndChangeCheck())
                        UnityEditor.Experimental.Lightmapping.SetLightDirty((UnityEngine.Light)serializedLight.serializedObject.targetObject);
                }
            }
        }
    }
}
