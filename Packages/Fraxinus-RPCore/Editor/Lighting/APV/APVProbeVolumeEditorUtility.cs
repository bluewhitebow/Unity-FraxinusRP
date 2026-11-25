using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering
{
    internal static class APVProbeVolumeEditorUtility
    {
        internal static void APVDisabledHelpBox()
        {
            var pipelineAsset = GraphicsSettings.currentRenderPipeline;
            var renderPipelineAssetType = pipelineAsset?.GetType();
            switch (renderPipelineAssetType)
            {
                case { Name: "HDRenderPipelineAsset" }:
                {
                    var lightingGroup = GetHDRPLightingGroup();
                    var probeVolume = GetHDRPProbeVolumeEnum();
                    var k_QualitySettingsHelpBox = GetHDRPQualitySettingsHelpBox();

                    k_QualitySettingsHelpBox.Invoke(null, new []
                    {
                        "The current HDRP Asset does not support Adaptive Probe Volumes.", MessageType.Warning, lightingGroup, probeVolume, "m_RenderPipelineSettings.lightProbeSystem"
                    });
                    break;
                }
                case { Name: "UniversalRenderPipelineAsset" }:
                {
                    var k_QualitySettingsHelpBox = GetURPQualitySettingsHelpBox();
                    var lightingValue = GetURPLightingGroup();

                    k_QualitySettingsHelpBox.Invoke(null, new[]
                    {
                        "The current URP Asset does not support Adaptive Probe Volumes.", MessageType.Warning, lightingValue, "m_LightProbeSystem"
                    });
                    break;
                }
                default:
                {
                    if (pipelineAsset is IProbeVolumeEnabledRenderPipeline probePipeline)
                    {
                        var message = probePipeline.supportProbeVolume
                            ? $"Adaptive Probe Volumes are enabled in '{pipelineAsset.name}', but the runtime system is not initialized. Check the Console for render pipeline initialization errors."
                            : $"Adaptive Probe Volumes are not enabled in the active render pipeline asset '{pipelineAsset.name}'. Check its Light Probe System setting. A Quality setting can override the Graphics pipeline asset.";
                        EditorGUILayout.HelpBox(message, MessageType.Warning);
                        if (GUILayout.Button("Open Active Render Pipeline Asset"))
                            EditorUtility.OpenPropertyEditor(pipelineAsset);
                    }
                    else
                        EditorGUILayout.HelpBox("The current SRP does not support Adaptive Probe Volumes.", MessageType.Warning);
                    break;
                }
            }
        }

        internal static bool SupportsSkyOcclusion()
        {
#if UNITY_6000_0_OR_NEWER
            return SupportedRenderingFeatures.active.skyOcclusion;
#else
            // The Unity 2022 engine feature descriptor predates this flag.
            // Sky occlusion is supplied by the backported APV implementation
            // whenever the active SRP has enabled probe volumes.
            return APVProbeReferenceVolume.instance != null &&
                APVProbeReferenceVolume.instance.enabledBySRP;
#endif
        }

        internal static object GetHDRPLightingGroup()
        {
            var k_ExpandableGroup = Type.GetType("UnityEditor.Rendering.HighDefinition.HDRenderPipelineUI+ExpandableGroup,Unity.RenderPipelines.HighDefinition.Editor");
            return k_ExpandableGroup.GetEnumValues().GetValue(IndexOf(k_ExpandableGroup.GetEnumNames(), "Lighting"));
        }

        internal static object GetHDRPProbeVolumeEnum()
        {
            var k_LightingSection = Type.GetType("UnityEditor.Rendering.HighDefinition.HDRenderPipelineUI+ExpandableLighting,Unity.RenderPipelines.HighDefinition.Editor");
            return k_LightingSection.GetEnumValues().GetValue(IndexOf(k_LightingSection.GetEnumNames(), "ProbeVolume"));
        }

        internal static MethodInfo GetHDRPQualitySettingsHelpBox()
        {
            return Type.GetType("UnityEditor.Rendering.HighDefinition.HDEditorUtils,Unity.RenderPipelines.HighDefinition.Editor")
                .GetMethod("QualitySettingsHelpBoxForReflection", BindingFlags.Static | BindingFlags.NonPublic);
        }

        internal static MethodInfo GetURPQualitySettingsHelpBox()
        {
            return Type.GetType("UnityEditor.Rendering.Universal.EditorUtils,Unity.RenderPipelines.Universal.Editor")
                .GetMethod("QualitySettingsHelpBox", BindingFlags.Static | BindingFlags.NonPublic);
        }

        internal static void EnableURPLightLayers(RenderPipelineAsset pipeline)
        {
            if (pipeline == null)
                return;

            var serialized = new SerializedObject(pipeline);
            var property = serialized.FindProperty("m_SupportsLightLayers");
            if (property == null || property.propertyType != SerializedPropertyType.Boolean || property.boolValue)
                return;

            Undo.RecordObject(pipeline, "Enable URP Rendering Layers");
            property.boolValue = true;
            serialized.ApplyModifiedProperties();
            EditorUtility.OpenPropertyEditor(pipeline);
            EditorApplication.delayCall += () => EditorApplication.delayCall += () =>
                CoreEditorUtils.Highlight(pipeline.name, "m_SupportsLightLayers", HighlightSearchMode.Identifier);
        }

        internal static object GetURPLightingGroup()
        {
            var k_LightingSection = Type.GetType("UnityEditor.Rendering.Universal.UniversalRenderPipelineAssetUI+Expandable,Unity.RenderPipelines.Universal.Editor");
            return k_LightingSection.GetEnumValues().GetValue(IndexOf(k_LightingSection.GetEnumNames(), "Lighting"));
        }

        internal static int IndexOf(string[] names, string name)
        {
            for (int i = 0; i < names.Length; i++)
                if (name == names[i])
                    return i;
            return -1;
        }

        internal static void FrameSettingDisabledHelpBox()
        {
            var renderPipelineAssetType = GraphicsSettings.currentRenderPipeline?.GetType();

            // HDRP only
            if (renderPipelineAssetType != null && renderPipelineAssetType.Name == "HDRenderPipelineAsset")
            {
                static int IndexOf(string[] names, string name) { for (int i = 0; i < names.Length; i++) { if (name == names[i]) return i; } return -1; }

                var k_FrameSettingsField = Type.GetType("UnityEngine.Rendering.HighDefinition.FrameSettingsField,Unity.RenderPipelines.HighDefinition.Runtime");
                var k_APVFrameSetting = k_FrameSettingsField.GetEnumValues().GetValue(IndexOf(k_FrameSettingsField.GetEnumNames(), "AdaptiveProbeVolume"));

                var k_EnsureFrameSetting = Type.GetType("UnityEditor.Rendering.HighDefinition.HDEditorUtils,Unity.RenderPipelines.HighDefinition.Editor")
                    .GetMethod("EnsureFrameSetting", BindingFlags.Static | BindingFlags.NonPublic);

                k_EnsureFrameSetting.Invoke(null, new object[] { k_APVFrameSetting});
            }
        }

    }
}
