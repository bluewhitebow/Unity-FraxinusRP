using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditorInternal;

namespace UnityEngine.Rendering
{
    internal sealed class APVProbeVolumeBakingSettingsEditor : ProbeVolumeBakingSettingsEditor
    {
        readonly SerializedObject serializedObject;
        ProbeVolumeBakingSet bakingSet => (ProbeVolumeBakingSet)serializedObject.targetObject;
        SerializedProperty m_ProbeVolumeBakingSettings;
        SerializedProperty m_SkyOcclusion;
        SerializedProperty m_SkyOcclusionBakingSamples;
        SerializedProperty m_SkyOcclusionBakingBounces;
        SerializedProperty m_SkyOcclusionAverageAlbedo;
        SerializedProperty m_SkyOcclusionShadingDirection;
        SerializedProperty m_RenderingLayers;
        SerializedProperty m_RenderingLayerMasks;
        static class Styles
        {
            public static readonly GUIContent invaliditySettingsTitle = new GUIContent("Probe Invalidity Settings");
            public static readonly GUIContent skyOcclusionSettingsTitle = new GUIContent("Sky Occlusion Settings");
            public static readonly GUIContent renderingLayersTitle = new GUIContent("Rendering Layers");
            public static readonly GUIContent skyOcclusion = new GUIContent("Sky Occlusion", "Choose whether to generate Sky Occlusion data for probes within this Probe Volume. When enabled, Scenes can be dynamically re-lit when the sky is changed. This feature increases memory usage.");
            public static readonly GUIContent skyOcclusionBakingSamples = new GUIContent("Samples", "The number of samples used to calculate the influence of the sky when baking probes. Increasing this value improves the accuracy of Sky Occlusion data, but increases the time required to generate baked lighting.");
            public static readonly GUIContent skyOcclusionBakingBounces = new GUIContent("Bounces", "The maximum number of bounces allowed for each Sky Occlusion sample. Increasing this value particularly improves the accuracy of occlusion data in areas of the Scene with complicated routes to the sky.");
            public static readonly GUIContent skyOcclusionAverageAlbedo = new GUIContent("Albedo Override", "Sky Occlusion does not consider the albedo of materials in the Scene when calculating bounced light from the sky. Albedo Override determines the value used instead. Lower values darken and higher values will brighten the Scene.");
            public static readonly GUIContent skyOcclusionShadingDirection = new GUIContent("Sky Direction", "For each probe, additionally bake the most suitable direction to use for sampling the Scene’s Ambient Probe. When disabled, surface normals are used instead. Sky Direction improves visual quality at the expense of memory.");
            public static readonly GUIContent cpuLightmapperNotSupportedWarning = new GUIContent("Sky Occlusion is not supported by the current lightmapper. Ensure that Progressive GPU is selected in Lightmapper Settings.");
            public static readonly GUIContent resetDilation = new GUIContent("Reset Dilation Settings");
            public static readonly GUIContent resetVirtualOffset = new GUIContent("Reset Virtual Offset Settings");
            public static readonly GUIContent renderingLayerMasks = new GUIContent("Rendering Layer Masks", "When enabled, geometry in a Rendering Layer will only receive lighting from probes which see Rendering Layers in the same Rendering Layer Mask. This can be used to prevent leaking across boundaries.\nGeometry not belonging to a Rendering Layer Mask will continue to sample all probes. Requires Leak Reduction Mode to be enabled.");
            public static readonly string maskTooltip = "The Rendering Layers for this mask.";
        }

        enum Expandable { Invalidity = 1, SkyOcclusion = 2, RenderingLayers = 4 }
        static readonly ExpandedState<Expandable, APVProbeVolumeBakingSettingsEditor> s_Foldouts = new(Expandable.Invalidity, "APV");

        internal APVProbeVolumeBakingSettingsEditor(SerializedObject serializedObject)
        {
            this.serializedObject = serializedObject;
            m_ProbeVolumeBakingSettings = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.settings));
			m_SkyOcclusion = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.skyOcclusion));
            m_SkyOcclusionBakingSamples = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.skyOcclusionBakingSamples));
            m_SkyOcclusionBakingBounces = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.skyOcclusionBakingBounces));
            m_SkyOcclusionAverageAlbedo = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.skyOcclusionAverageAlbedo));
            m_SkyOcclusionShadingDirection = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.skyOcclusionShadingDirection));
            m_RenderingLayers = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.useRenderingLayers));
            m_RenderingLayerMasks = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.renderingLayerMasks));
        }

        internal override void Draw()
        {
            SkyOcclusionSettingsGUI();
            ProbeInvaliditySettingsGUI();
            RenderingLayersSettingsGUI();
        }

        static bool Foldout(GUIContent label, Expandable area, bool title, Action<Rect> contextMenu = null)
        {
            bool expanded = ProbeVolumeBakingPanel.Foldout(label, s_Foldouts[area], title, contextMenu);
            s_Foldouts.SetExpandedAreas(area, expanded);
            return expanded;
        }

        void ProbeInvaliditySettingsGUI()
        {
            if (!Foldout(Styles.invaliditySettingsTitle, Expandable.Invalidity, true, ResetProbeSettings))
                return;

            using (new EditorGUI.IndentLevelScope())
                EditorGUILayout.PropertyField(m_ProbeVolumeBakingSettings);

            EditorGUILayout.Space();
        }

        void ResetProbeSettings(Rect rect)
        {
            EditorUtility.DisplayCustomMenu(rect, new[] { Styles.resetDilation, Styles.resetVirtualOffset }, -1, (object userData, string[] options, int selected) => {
                if (selected == 0)
                    bakingSet.settings.dilationSettings.SetDefaults();
                else
                    bakingSet.settings.virtualOffsetSettings.SetDefaults();
            }, null);
        }

        #region Rendering Layer Mask
        ReorderableList m_MaskList = null;
        int renamingMask;

        void RenderingLayersSettingsGUI()
        {
            if (!Foldout(Styles.renderingLayersTitle, Expandable.RenderingLayers, true))
                return;

            if (m_MaskList == null)
                m_MaskList = CreateRenderingLayersList();

            DrawRenderingLayersList();

            EditorGUILayout.Space();
        }

        ReorderableList CreateRenderingLayersList()
        {
            return new ReorderableList(serializedObject, m_RenderingLayerMasks, true, false, true, true)
            {
                multiSelect = false,
                elementHeightCallback = _ => EditorGUIUtility.singleLineHeight,
                onCanAddCallback = _ => m_RenderingLayerMasks.arraySize < APVDefinitions.probeMaxRegionCount,
                onAddCallback = _ =>
                {
                    int index = Mathf.Min(m_MaskList.index + 1, m_MaskList.count);
                    m_RenderingLayerMasks.InsertArrayElementAtIndex(index);
                    var mask = m_RenderingLayerMasks.GetArrayElementAtIndex(index).FindPropertyRelative("mask");
                    if (mask.propertyType == SerializedPropertyType.Generic)
                        mask = mask.FindPropertyRelative("m_Bits");
                    mask.uintValue = 0;
                    m_RenderingLayerMasks.GetArrayElementAtIndex(index).FindPropertyRelative("name").stringValue = "Mask " + (index + 1);
                    m_MaskList.index = index;
                },
                drawElementCallback = (rect, index, active, focused) =>
                {
                    var mask = m_RenderingLayerMasks.GetArrayElementAtIndex(index).FindPropertyRelative("mask");
                    var name = m_RenderingLayerMasks.GetArrayElementAtIndex(index).FindPropertyRelative("name");

                    // Fixup alignment
                    rect.yMin++;
                    rect.yMax--;

                    // Name
                    if (RenameEvent(rect, active, focused, index, ref renamingMask))
                    {
                        Rect labelPosition = new Rect(rect.x, rect.y, EditorGUIUtility.labelWidth, EditorGUIUtility.singleLineHeight);
                        Rect fieldPosition = new Rect(rect.x + EditorGUIUtility.labelWidth + 2, rect.y, rect.width - EditorGUIUtility.labelWidth - 2, rect.height);

                        // Renaming
                        EditorGUI.BeginChangeCheck();
                        var newName = EditorGUI.DelayedTextField(labelPosition, name.stringValue, EditorStyles.boldLabel);
                        if (EditorGUI.EndChangeCheck() && !string.IsNullOrWhiteSpace(newName))
                        {
                            renamingMask = -1;
                            name.stringValue = newName;
                        }

                        RenderingLayerMaskEditorGUI.RenderingLayerMaskField(fieldPosition, GUIContent.none, mask);
                    }
                    else
                        RenderingLayerMaskEditorGUI.RenderingLayerMaskField(rect,
                            EditorGUIUtility.TrTextContent(name.stringValue, Styles.maskTooltip), mask);
                }
            };
        }

        void DrawRenderingLayersList()
        {
            var currentPipeline = GraphicsSettings.currentRenderPipeline;
            var renderPipelineAssetType = currentPipeline?.GetType();
            bool isHDRP = IsRenderPipelineAsset(renderPipelineAssetType, "HDRenderPipelineAsset");
            bool isURP = IsRenderPipelineAsset(renderPipelineAssetType, "UniversalRenderPipelineAsset");
            bool supportsLayers = isHDRP ? GetHDRPSupportsLayers(renderPipelineAssetType, currentPipeline) :
                isURP ? GetURPSupportsLayers(renderPipelineAssetType, currentPipeline) : true;

            using var indentLevelScope = new EditorGUI.IndentLevelScope();
            EditorGUILayout.PropertyField(m_RenderingLayers, Styles.renderingLayerMasks);
            if (!m_RenderingLayers.boolValue)
                return;

            using var scope = new EditorGUI.IndentLevelScope();
            switch (supportsLayers)
            {
                case false when isHDRP:
                {
                    var lightingGroup = APVProbeVolumeEditorUtility.GetHDRPLightingGroup();
                    var k_QualitySettingsHelpBox = APVProbeVolumeEditorUtility.GetHDRPQualitySettingsHelpBox();
                    k_QualitySettingsHelpBox.Invoke(null, new[]
                    {
                        "The current HDRP Asset does not support Light Layers.", MessageType.Warning, lightingGroup, -1, "m_RenderPipelineSettings.supportLightLayers"
                    });
                    break;
                }
                case false when isURP:
                {
                    CoreEditorUtils.DrawFixMeBox(
                        "The current URP Asset does not support Light Layers. Enable this setting to use Rendering Layer Masks.",
                        MessageType.Warning, "Enable & Open", () => APVProbeVolumeEditorUtility.EnableURPLightLayers(currentPipeline));
                    break;
                }
                default:
                    ProbeVolumeBakingPanel.DrawListWithIndent(m_MaskList);
                    break;
            }
        }

        internal static bool GetURPSupportsLayers(Type renderPipelineAssetType, RenderPipelineAsset currentPipeline)
        {
            return TryGetBooleanMember(renderPipelineAssetType, currentPipeline, "useRenderingLayers", "m_SupportsLightLayers", out var value) && value;
        }

        internal static bool GetHDRPSupportsLayers(Type renderPipelineAssetType, RenderPipelineAsset currentPipeline)
        {
            if (!TryGetMember(renderPipelineAssetType, currentPipeline, "m_RenderPipelineSettings", out var settings) || settings == null)
                return false;
            return TryGetBooleanMember(settings.GetType(), settings, "supportLightLayers", null, out var value) && value;
        }

        static bool IsRenderPipelineAsset(Type type, string assetName)
        {
            for (; type != null; type = type.BaseType)
                if (type.Name == assetName)
                    return true;
            return false;
        }

        static bool TryGetBooleanMember(Type type, object target, string propertyName, string fieldName, out bool value)
        {
            if (TryGetMember(type, target, propertyName, out var propertyValue) && propertyValue is bool propertyBool)
            {
                value = propertyBool;
                return true;
            }

            if (fieldName != null && TryGetMember(type, target, fieldName, out var fieldValue) && fieldValue is bool fieldBool)
            {
                value = fieldBool;
                return true;
            }

            value = false;
            return false;
        }

        static bool TryGetMember(Type type, object target, string memberName, out object value)
        {
            if (type == null || target == null)
            {
                value = null;
                return false;
            }

            for (var current = type; current != null; current = current.BaseType)
            {
                var property = current.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0 && property.GetGetMethod(true) is { IsStatic: false } getter)
                {
                    value = getter.Invoke(target, null);
                    return true;
                }

                var field = current.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null && !field.IsStatic)
                {
                    value = field.GetValue(target);
                    return true;
                }
            }

            value = null;
            return false;
        }

        #endregion

        #region Sky occlusion
        void SkyOcclusionSettingsGUI()
        {
            if (!APVProbeVolumeEditorUtility.SupportsSkyOcclusion())
                return;
            if (!Foldout(Styles.skyOcclusionSettingsTitle, Expandable.SkyOcclusion, true))
                return;

            using var scope = new EditorGUI.IndentLevelScope();

            var lightmapper = ProbeVolumeBakingWorkflow.GetLightingSettings().lightmapper;
            bool cpuLightmapperSelected = lightmapper == LightingSettings.Lightmapper.ProgressiveCPU;
            if (cpuLightmapperSelected)
            {
                EditorGUILayout.HelpBox(Styles.cpuLightmapperNotSupportedWarning.text, MessageType.Warning);
            }
            using (new EditorGUI.DisabledScope(cpuLightmapperSelected))
            {
                EditorGUILayout.PropertyField(m_SkyOcclusion, Styles.skyOcclusion);

                if (m_SkyOcclusion.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(m_SkyOcclusionBakingSamples, Styles.skyOcclusionBakingSamples);
                    EditorGUILayout.PropertyField(m_SkyOcclusionBakingBounces, Styles.skyOcclusionBakingBounces);
                    EditorGUILayout.PropertyField(m_SkyOcclusionAverageAlbedo, Styles.skyOcclusionAverageAlbedo);
                    EditorGUILayout.PropertyField(m_SkyOcclusionShadingDirection, Styles.skyOcclusionShadingDirection);
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.Space();
        }
        #endregion



        bool RenameEvent(Rect rect, bool active, bool focused, int index, ref int renameIndex)
        {
            if (active)
            {
                if (Event.current.type == EventType.MouseDown && !rect.Contains(Event.current.mousePosition))
                    renameIndex = -1;
                if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2)
                {
                    if (rect.Contains(Event.current.mousePosition))
                        renameIndex = index;
                }
                if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                    renameIndex = -1;
            }
            else if (renameIndex == index && !focused)
                renameIndex = -1;

            return renameIndex == index;
        }
    }
}
