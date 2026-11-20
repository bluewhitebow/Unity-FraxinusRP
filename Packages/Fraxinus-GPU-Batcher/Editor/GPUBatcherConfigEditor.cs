// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEngine;
using UnityEditor;

namespace Fraxinus.GPUBatcher.Editor
{
    [InitializeOnLoad]
    static class GPUBatcherConfigValidator
    {
        static GPUBatcherConfigValidator()
        {
            int count = GPUBatcherConfig.CountInPreloadedAssets();
            if (count > 1)
                Debug.LogError($"GPUBatcherConfig: {count} configs found in Preloaded Assets! Only one should exist. Open any config asset in the Inspector to fix this.");
        }
    }

    [CustomEditor(typeof(GPUBatcherConfig))]
    internal class GPUBatcherConfigEditor : UnityEditor.Editor
    {
        static float s_gatheredPadding = 1f;

        public override void OnInspectorGUI()
        {

            var config = (GPUBatcherConfig)target;
            var preloaded = PlayerSettings.GetPreloadedAssets();
            int preloadedCount = CountPreloadedConfigs(preloaded);
            bool isActive = IsActiveConfig(config, preloaded);

            if (preloadedCount > 1)
            {
                EditorGUILayout.HelpBox(
                    $"Multiple GPUBatcherConfig assets ({preloadedCount}) found in Preloaded Assets! " +
                    "Only one should exist. Click below to fix this.",
                    MessageType.Error);
                if (GUILayout.Button("Set This As Active (removes duplicates)"))
                {
                    SetAsActiveConfig(config);
                }
            }
            else if (isActive)
            {
                EditorGUILayout.HelpBox("This is the active config (registered in Preloaded Assets).", MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("This is NOT the active config.", MessageType.Warning);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Select Active Config"))
                {
                    var active = GetPreloadedConfig(preloaded);
                    if (active != null)
                    {
                        Selection.activeObject = active;
                        EditorGUIUtility.PingObject(active);
                    }
                }
                if (GUILayout.Button("Set This As Active"))
                {
                    SetAsActiveConfig(config);
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space();

            // button to open the system window
            if (GUILayout.Button("Open System Window", GUILayout.Height(30)))
            {
                GPUBatcherRendererWindow.ShowWindow();
            }

            EditorGUILayout.Space();

            serializedObject.Update();
            DrawConfigInspector(serializedObject, config);
            serializedObject.ApplyModifiedProperties();
        }

        private static int CountPreloadedConfigs(Object[] preloaded)
        {
            int count = 0;
            foreach (var asset in preloaded)
            {
                if (asset is GPUBatcherConfig)
                    count++;
            }
            return count;
        }

        private static GPUBatcherConfig GetPreloadedConfig(Object[] preloaded)
        {
            foreach (var asset in preloaded)
            {
                if (asset is GPUBatcherConfig config)
                    return config;
            }
            return null;
        }

        private static bool IsActiveConfig(GPUBatcherConfig config, Object[] preloaded)
        {
            foreach (var asset in preloaded)
            {
                if (asset is GPUBatcherConfig preloadedConfig)
                    return preloadedConfig == config;
            }
            return false;
        }

        private static void SetAsActiveConfig(GPUBatcherConfig config)
        {
            var preloaded = PlayerSettings.GetPreloadedAssets();
            var list = new System.Collections.Generic.List<Object>();

            // remove any existing GPUBatcherConfig entries
            foreach (var asset in preloaded)
            {
                if (asset != null && asset is not GPUBatcherConfig)
                    list.Add(asset);
            }

            // add the new one
            list.Add(config);
            PlayerSettings.SetPreloadedAssets(list.ToArray());

            // reinitialize the system with the new config
            var system = GPUBatcherRenderer.Instance;
            if (system != null)
                system.Reinitialize();
        }

        // caller must handle SerializedObject.Update() / ApplyModifiedProperties()
        public static void DrawConfigInspector(SerializedObject configSO, GPUBatcherConfig config)
        {
            float savedLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 240f;

            var prop = configSO.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (prop.name == "m_Script") continue;

                if (prop.name is "gatheredMaxChunks" or "gatheredPoolCapacity" or "gatheredMaxInstances")
                {
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.PropertyField(prop, true);
                    EditorGUI.EndDisabledGroup();
                    continue;
                }

                // gray out retest pass when occlusion is off or render pipeline doesn't support it
                if (prop.name == "useHZBRetestPass")
                {
                    bool compatible = config.UseGPUOcclusionCulling && GPUBatcherConfig.HZBRetestPassCompatible;
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(!compatible);
                    EditorGUILayout.PropertyField(prop, true);
                    if (config.UseGPUOcclusionCulling && !GPUBatcherConfig.HZBRetestPassCompatible)
                        EditorGUILayout.LabelField("  Requires URP or HDRP depth prepass patch", EditorStyles.miniLabel);
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                    continue;
                }

                // gray out pixel expansion when occlusion is off or retest pass handles it
                if (prop.name == "hzbPixelExpansion")
                {
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(!config.UseGPUOcclusionCulling || config.useHZBRetestPass);
                    EditorGUILayout.PropertyField(prop, true);
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                    continue;
                }

                // gray out shadow caster occlusion when occlusion culling is off
                if (prop.name == "shadowCasterOcclusion")
                {
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(!config.UseGPUOcclusionCulling);
                    EditorGUILayout.PropertyField(prop, true);
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                    continue;
                }

                // hide HDRP-only option when not using HDRP
                if (prop.name == "hdrpForwardDepthFix")
                {
                    var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                    if (rp == null || !rp.GetType().Name.Contains("HD"))
                        continue;
                }

                if (prop.name == "terrainDetailUnloadHysteresis")
                {
                    EditorGUILayout.PropertyField(prop, new GUIContent("Detail Unload Hysteresis", prop.tooltip), true);
                    continue;
                }

                // gray out fade width when not using stateless crossfade
                if (prop.name == "defaultSmallMeshFadeWidth")
                {
                    EditorGUI.BeginDisabledGroup(config.defaultSmallMeshFadeMode != MinScreenSizeFadeMode.Crossfade);
                    EditorGUILayout.PropertyField(prop, true);
                    EditorGUI.EndDisabledGroup();
                    continue;
                }

                EditorGUILayout.PropertyField(prop, true);

                if (prop.name == "maxPoolRefsPerChunk")
                {
                    int maxInstances = config.maxPoolRefsPerChunk * GPUBatcherRenderer.kPoolSize;
                    EditorGUILayout.LabelField($"  Max {maxInstances:N0} instances per chunk", EditorStyles.miniLabel);
                }

                // session-only toggles drawn inline after the last debug field
                if (prop.name == "debugDisableRendering")
                {
                    config.debugVisualizeCullingInSceneView = EditorGUILayout.Toggle(
                        new GUIContent("Debug Visualize Culling In Scene View",
                            "Freeze culling to the game camera's frustum. Scene view will show what the game camera sees. Resets to off on editor restart."),
                        config.debugVisualizeCullingInSceneView);
                    config.debugDrawBoundingSpheres = EditorGUILayout.Toggle(
                        new GUIContent("Debug Draw Bounding Spheres",
                            "Draw a per-instance bounding sphere for the visible set, colored by LOD. Resets to off on editor restart."),
                        config.debugDrawBoundingSpheres);
                    config.debugValidateUploads = EditorGUILayout.Toggle(
                        new GUIContent("Debug Validate Uploads",
                            "Validate GPU uploads against a CPU mirror and log mismatches with last-writer info. Slow (GPU readback). Resets to off on editor restart."),
                        config.debugValidateUploads);
                }
            }

            EditorGUIUtility.labelWidth = savedLabelWidth;

            // gathered allocations actions
            bool hasData = config.gatheredMaxChunks > 0 ||
                           config.gatheredPoolCapacity > 0 ||
                           config.gatheredMaxInstances > 0;
            if (hasData)
            {
                EditorGUILayout.Space(4f);
                s_gatheredPadding = EditorGUILayout.Slider("Pool Padding", s_gatheredPadding, 1f, 1.25f);

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15f);
                if (GUILayout.Button("Copy to Initial Sizes"))
                {
                    Undo.RecordObject(config, "Copy Gathered to Initial Sizes");
                    config.CopyGatheredToInitial(s_gatheredPadding);
                    EditorUtility.SetDirty(config);
                    configSO.Update();
                }
                if (GUILayout.Button("Clear Gathered Data"))
                {
                    Undo.RecordObject(config, "Clear Gathered Allocations");
                    config.ClearGatheredData();
                    EditorUtility.SetDirty(config);
                    configSO.Update();
                }
                EditorGUILayout.EndHorizontal();
            }
        }
    }
}
