// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEngine;
using UnityEditor;

namespace Fraxinus.GPUBatcher.Editor
{
    [CustomEditor(typeof(GPUBatcherPrototypeExtraData))]
    [CanEditMultipleObjects]
    internal class GPUBatcherPrototypeExtraDataEditor : UnityEditor.Editor
    {
        static readonly GUIContent s_FadeMode = new GUIContent("Fade Mode");
        static readonly GUIContent s_FadeWidth = new GUIContent("Fade Width");
        static readonly GUIContent s_EnableDistanceDensity = new GUIContent("Enable Distance Density", "Randomly thin out instances based on distance or screen size.");
        static readonly GUIContent s_DensityMode = new GUIContent("Density Mode", "ScreenSize: based on screen % (matches LOD). Raw Distance: based on world-space distance.");
        static readonly GUIContent s_StartScreenSize = new GUIContent("Start Screen Size", "Screen size % where density starts reducing. Above = full density.");
        static readonly GUIContent s_EndScreenSize = new GUIContent("End Screen Size", "Screen size % where density reaches minimum. Auto-derived from the prototype's LOD/screen cull threshold.");
        static readonly GUIContent s_StartDistance = new GUIContent("Start Distance", "Distance (meters) where density is at full (100%). Closer = full density.");
        static readonly GUIContent s_EndDistance = new GUIContent("End Distance", "Distance (meters) where density reaches minimum fraction. Farther = min density.");
        static readonly GUIContent s_MinFraction = new GUIContent("Min Fraction", "Minimum density fraction (0 = cull all, 1 = keep all).");
        static readonly GUIContent s_DensityFadeWidthStateless = new GUIContent("Fade Width", "Fade band width (0-1 for screen-size, 0-1 density fraction for distance). Wider = smoother fade.");
        static readonly GUIContent s_DensityFadeWidthDisabled = new GUIContent("Fade Width", "Only available when using non-Animated Crossfade.");
        static readonly GUIContent s_MaxShadowDistance = new GUIContent("Max Shadow Distance");
        static readonly GUIContent s_MaxCrossfadeCascade = new GUIContent("Max Crossfade Cascade");
        static readonly GUIContent s_CrossfadeDuration = new GUIContent("Animated Crossfade Duration");

        // cached serialized properties
        private SerializedProperty _propMaxShadowCascade;
        private SerializedProperty _propMinShadowLOD;
        private SerializedProperty _propOverrideMaxShadowDistance;
        private SerializedProperty _propMaxShadowDistance;
        private SerializedProperty _propOverrideMaxShadowCrossfadeCascade;
        private SerializedProperty _propMaxShadowCrossfadeCascade;
        private SerializedProperty _propMinScreenSize;
        private SerializedProperty _propMinScreenSizeFadeMode;
        private SerializedProperty _propMinScreenSizeFadeWidth;
        private SerializedProperty _propOverrideLodFadeDuration;
        private SerializedProperty _propLodFadeDuration;
        private SerializedProperty _propPrototypeGroup;
        private SerializedProperty _propEnableDistanceDensity;
        private SerializedProperty _propDistanceDensityMode;
        private SerializedProperty _propDensityStartScreenSize;
        private SerializedProperty _propDensityStartDistance;
        private SerializedProperty _propDensityEndDistance;
        private SerializedProperty _propDensityMinFraction;
        private SerializedProperty _propDensityFadeWidth;

        private void OnEnable()
        {
            _propMaxShadowCascade = serializedObject.FindProperty("maxShadowCascade");
            _propMinShadowLOD = serializedObject.FindProperty("minShadowLOD");
            _propOverrideMaxShadowDistance = serializedObject.FindProperty("overrideMaxShadowDistance");
            _propMaxShadowDistance = serializedObject.FindProperty("maxShadowDistance");
            _propOverrideMaxShadowCrossfadeCascade = serializedObject.FindProperty("overrideMaxShadowCrossfadeCascade");
            _propMaxShadowCrossfadeCascade = serializedObject.FindProperty("maxShadowCrossfadeCascade");
            _propMinScreenSize = serializedObject.FindProperty("minScreenSize");
            _propMinScreenSizeFadeMode = serializedObject.FindProperty("minScreenSizeFadeMode");
            _propMinScreenSizeFadeWidth = serializedObject.FindProperty("minScreenSizeFadeWidth");
            _propOverrideLodFadeDuration = serializedObject.FindProperty("overrideLodFadeDuration");
            _propLodFadeDuration = serializedObject.FindProperty("lodFadeDuration");
            _propPrototypeGroup = serializedObject.FindProperty("prototypeGroup");
            _propEnableDistanceDensity = serializedObject.FindProperty("enableDistanceDensity");
            _propDistanceDensityMode = serializedObject.FindProperty("distanceDensityMode");
            _propDensityStartScreenSize = serializedObject.FindProperty("densityStartScreenSize");
            _propDensityStartDistance = serializedObject.FindProperty("densityStartDistance");
            _propDensityEndDistance = serializedObject.FindProperty("densityEndDistance");
            _propDensityMinFraction = serializedObject.FindProperty("densityMinFraction");
            _propDensityFadeWidth = serializedObject.FindProperty("densityFadeWidth");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            GPUBatcherInspectorGUI.DrawScriptField(serializedObject);
            var extraData = (GPUBatcherPrototypeExtraData)target;
            var lodGroup = extraData.GetComponent<LODGroup>();
            bool hasLODGroup = lodGroup != null;
            var meshLodData = extraData.GetComponent<GPUBatcherMeshLodData>();
            bool meshLodActive = meshLodData != null && !hasLODGroup;

            // grouping
            EditorGUILayout.PropertyField(_propPrototypeGroup, new GUIContent("Prototype Group",
                "Optional group for runtime LOD bias and density control via GPUBatcherPrototypeGroup.SetLodBias / SetDensity."));
            EditorGUILayout.Space(4);

            // shadow optimizations
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GPUBatcherInspectorGUI.DrawSectionHeader("Shadow Optimizations");
            EditorGUILayout.PropertyField(_propMaxShadowCascade);
            EditorGUILayout.PropertyField(_propMinShadowLOD);
            DrawOverrideField(
                _propOverrideMaxShadowDistance,
                _propMaxShadowDistance,
                s_MaxShadowDistance);
            DrawOverrideSliderField(
                _propOverrideMaxShadowCrossfadeCascade,
                _propMaxShadowCrossfadeCascade,
                s_MaxCrossfadeCascade, 0, 4);
            EditorGUILayout.EndVertical();

            // screen size / LOD overrides
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GPUBatcherInspectorGUI.DrawSectionHeader("Screen Size / LOD Overrides");
            {
                // LODGroup animated crossfade duration override (Mesh LOD and minScreenSize have their own)
                if (hasLODGroup)
                {
                    bool lodGroupAnimated = lodGroup.fadeMode != LODFadeMode.None && lodGroup.animateCrossFading;
                    EditorGUI.BeginDisabledGroup(!lodGroupAnimated);
                    DrawOverrideField(_propOverrideLodFadeDuration, _propLodFadeDuration, s_CrossfadeDuration);
                    EditorGUI.EndDisabledGroup();
                }

                #region  Min Screen Size sub-box

                EditorGUILayout.Space(2);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUI.BeginDisabledGroup(meshLodActive || hasLODGroup);

                EditorGUILayout.LabelField("Screen Size Culling", EditorStyles.boldLabel);

                if (hasLODGroup)
                    EditorGUILayout.LabelField("Disabled when LOD Group is active. (Use Culled level instead)", EditorStyles.miniLabel);
                if (meshLodActive)
                    EditorGUILayout.LabelField("Disabled when Mesh LOD is active.", EditorStyles.miniLabel);

                if (!hasLODGroup && !meshLodActive)
                {
                    EditorGUILayout.PropertyField(_propMinScreenSize);
                    bool minScreenActive = extraData.minScreenSize > 0f && !meshLodActive && !hasLODGroup;

                    EditorGUI.BeginDisabledGroup(!minScreenActive);
                    EditorGUILayout.PropertyField(_propMinScreenSizeFadeMode, s_FadeMode);
                    EditorGUI.EndDisabledGroup();

                    bool fadeWidthEditable = minScreenActive &&
                                                extraData.minScreenSizeFadeMode == MinScreenSizeFadeMode.Crossfade;
                    EditorGUI.BeginDisabledGroup(!fadeWidthEditable);
                    EditorGUILayout.PropertyField(_propMinScreenSizeFadeWidth, s_FadeWidth);
                    EditorGUI.EndDisabledGroup();

                    bool minScreenAnimated = minScreenActive &&
                                                extraData.minScreenSizeFadeMode == MinScreenSizeFadeMode.AnimatedCrossfade;
                    EditorGUI.BeginDisabledGroup(!minScreenAnimated);
                    DrawOverrideField(_propOverrideLodFadeDuration, _propLodFadeDuration, s_CrossfadeDuration);
                    EditorGUI.EndDisabledGroup();
                }

                EditorGUI.EndDisabledGroup();
                EditorGUILayout.EndVertical();

                #endregion

            }
            EditorGUILayout.EndVertical();

            // density
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GPUBatcherInspectorGUI.DrawSectionHeader("Density");
            {
                EditorGUILayout.PropertyField(_propEnableDistanceDensity, s_EnableDistanceDensity);

                bool densityEnabled = extraData.enableDistanceDensity;
                EditorGUI.BeginDisabledGroup(!densityEnabled);

                // determine if this prototype uses any crossfade mode
                bool hasCrossfade;
                bool usesAnimatedCrossfade;
                if (hasLODGroup)
                {
                    hasCrossfade = lodGroup.fadeMode != LODFadeMode.None;
                    usesAnimatedCrossfade = hasCrossfade && lodGroup.animateCrossFading;
                }
                else if (meshLodActive)
                {
                    hasCrossfade = meshLodData.meshLodFadeMode != MinScreenSizeFadeMode.None;
                    usesAnimatedCrossfade = meshLodData.meshLodFadeMode == MinScreenSizeFadeMode.AnimatedCrossfade;
                }
                else
                {
                    hasCrossfade = extraData.minScreenSizeFadeMode != MinScreenSizeFadeMode.None;
                    usesAnimatedCrossfade = extraData.minScreenSizeFadeMode == MinScreenSizeFadeMode.AnimatedCrossfade;
                }

                // shared parameters
                EditorGUILayout.PropertyField(_propDensityMinFraction, s_MinFraction);

                bool fadeWidthActive = hasCrossfade && !usesAnimatedCrossfade;
                EditorGUI.BeginDisabledGroup(!fadeWidthActive);
                EditorGUILayout.PropertyField(_propDensityFadeWidth,
                    fadeWidthActive ? s_DensityFadeWidthStateless : s_DensityFadeWidthDisabled);
                EditorGUI.EndDisabledGroup();

                if (densityEnabled && !hasCrossfade)
                {
                    string hint = "No crossfade mode - density-culled instances will snap (no fade)."
                        + "\nSet a crossfade mode via LOD Group, Min Screen Size culling"
#if UNITY_6000_2_OR_NEWER
                        + ", or Mesh LOD."
#else
                        + "."
#endif
                        ;
                    EditorGUILayout.HelpBox(hint, MessageType.Info);
                }

                // mode selection
                EditorGUILayout.PropertyField(_propDistanceDensityMode, s_DensityMode);

                // mode-specific parameters
                bool useScreenSize = extraData.distanceDensityMode == DistanceDensityMode.ScreenSize;
                if (useScreenSize)
                {
                    EditorGUILayout.PropertyField(_propDensityStartScreenSize, s_StartScreenSize);

                    // show the auto-derived end screen size (cull threshold) read-only
                    float cullRatio = ComputeCullThresholdRatio(extraData, lodGroup, meshLodData);
                    float cullPercent = cullRatio * 100f;
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.FloatField(s_EndScreenSize, cullPercent);
                    EditorGUI.EndDisabledGroup();

                    // ensure start > end so the density ramp has positive range
                    float minAllowedStart = cullPercent + 0.01f;
                    if (_propDensityStartScreenSize.floatValue < minAllowedStart)
                        _propDensityStartScreenSize.floatValue = minAllowedStart;
                }
                else // Distance mode
                {
                    EditorGUILayout.PropertyField(_propDensityStartDistance, s_StartDistance);
                    EditorGUILayout.PropertyField(_propDensityEndDistance, s_EndDistance);
                }

                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.EndVertical();

            // other settings
            serializedObject.ApplyModifiedProperties();
        }

        // mirrors the runtime logic in ApplyMinScreenSize/ExtractLODGroupSignature: last LOD's screen size,
        // raised by minScreenSize override if larger
        private static float ComputeCullThresholdRatio(GPUBatcherPrototypeExtraData extraData, LODGroup lodGroup, GPUBatcherMeshLodData meshLodData)
        {
            float threshold = 0f;

            if (lodGroup != null)
            {
                var lods = lodGroup.GetLODs();
                if (lods != null && lods.Length > 0)
                    threshold = lods[lods.Length - 1].screenRelativeTransitionHeight;
            }
            else if (meshLodData != null && meshLodData.meshLodScreenSizes != null && meshLodData.meshLodScreenSizes.Length > 0)
            {
                threshold = meshLodData.meshLodScreenSizes[meshLodData.meshLodScreenSizes.Length - 1];
            }

            float minScreenRatio = extraData.minScreenSize * 0.01f;
            if (minScreenRatio > threshold)
                threshold = minScreenRatio;

            return threshold;
        }

        private static void DrawOverrideSliderField(SerializedProperty toggleProp, SerializedProperty valueProp, GUIContent label, int min, int max)
        {
            var rect = EditorGUILayout.GetControlRect();
            float toggleWidth = EditorStyles.toggle.CalcSize(GUIContent.none).x + 2;

            var toggleRect = new Rect(rect.x, rect.y, toggleWidth, rect.height);
            toggleProp.boolValue = EditorGUI.Toggle(toggleRect, toggleProp.boolValue);

            var fieldRect = new Rect(rect.x + toggleWidth, rect.y,
                rect.width - toggleWidth, rect.height);
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = prevLabelWidth - toggleWidth;
            EditorGUI.BeginDisabledGroup(!toggleProp.boolValue);
            valueProp.intValue = EditorGUI.IntSlider(fieldRect, label, valueProp.intValue, min, max);
            EditorGUI.EndDisabledGroup();
            EditorGUIUtility.labelWidth = prevLabelWidth;
        }

        private static void DrawOverrideField(SerializedProperty toggleProp, SerializedProperty valueProp, GUIContent label)
        {
            var rect = EditorGUILayout.GetControlRect();
            float toggleWidth = EditorStyles.toggle.CalcSize(GUIContent.none).x + 2;

            // checkbox on the far left
            var toggleRect = new Rect(rect.x, rect.y, toggleWidth, rect.height);
            toggleProp.boolValue = EditorGUI.Toggle(toggleRect, toggleProp.boolValue);

            // shrink label width so the value field lines up with standard fields
            var fieldRect = new Rect(rect.x + toggleWidth, rect.y,
                rect.width - toggleWidth, rect.height);
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = prevLabelWidth - toggleWidth;
            EditorGUI.BeginDisabledGroup(!toggleProp.boolValue);
            EditorGUI.PropertyField(fieldRect, valueProp, label);
            EditorGUI.EndDisabledGroup();
            EditorGUIUtility.labelWidth = prevLabelWidth;
        }
    }
}
