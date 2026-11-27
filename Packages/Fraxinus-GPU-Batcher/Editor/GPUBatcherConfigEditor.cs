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
            if (!Rendering.BRGIndirectSubmission.hasNativeApi) return;
            int count = GPUBatcherConfig.CountInPreloadedAssets();
            if (count > 1)
                Debug.LogError($"GPUBatcherConfig: {count} configs found in Preloaded Assets! Only one should exist. Open any config asset in the Inspector to fix this.");
        }
    }

    [CustomEditor(typeof(GPUBatcherConfig))]
    internal class GPUBatcherConfigEditor : UnityEditor.Editor
    {
        static float s_gatheredPadding = 1f;

        [SettingsProvider]
        private static SettingsProvider CreateProjectSettings()
        {
            if (!Rendering.BRGIndirectSubmission.hasNativeApi) return null;
            UnityEditor.Editor configEditor = null;
            return new SettingsProvider("Project/Graphics/Fraxinus GPU Batcher", SettingsScope.Project)
            {
                keywords = new[] { "GPU Batcher", "BRG", "HZB", "Occlusion", "Culling", "LOD" },
                activateHandler = (_, _) => GPUBatcherConfig.GetOrCreateProjectConfig(),
                guiHandler = _ =>
                {
                    CreateCachedEditor(GPUBatcherConfig.instance, typeof(GPUBatcherConfigEditor), ref configEditor);
                    configEditor.OnInspectorGUI();
                },
                deactivateHandler = () => DestroyImmediate(configEditor)
            };
        }

        public override void OnInspectorGUI()
        {
            if (!Rendering.BRGIndirectSubmission.hasNativeApi)
            {
                EditorGUILayout.HelpBox("GPU Batcher rendering requires Unity 6.", MessageType.Info);
                return;
            }

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
            GPUBatcherInspectorGUI.DrawRenderingDebuggerButton();

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
            GPUBatcherConfig.GetOrCreateProjectConfig();

            if (GPUBatcherRenderer.hasInstance)
                GPUBatcherRenderer.instance?.Reinitialize();
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
                    DrawProperty(prop, disabled: true);
                    continue;
                }

                // gray out retest pass when occlusion is off or render pipeline doesn't support it
                if (prop.name == "useHZBRetestPass")
                {
                    bool compatible = config.useGPUOcclusionCulling && GPUBatcherConfig.hzbRetestPassCompatible;
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(!compatible);
                    EditorGUILayout.PropertyField(prop, true);
                    if (config.useGPUOcclusionCulling && !GPUBatcherConfig.hzbRetestPassCompatible)
                        EditorGUILayout.LabelField("  Requires URP or HDRP depth prepass patch", EditorStyles.miniLabel);
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                    continue;
                }

                // gray out pixel expansion when occlusion is off or retest pass handles it
                if (prop.name == "hzbPixelExpansion")
                {
                    DrawProperty(prop, !config.useGPUOcclusionCulling || config.useHZBRetestPass, indented: true);
                    continue;
                }

                // gray out shadow caster occlusion when occlusion culling is off
                if (prop.name == "shadowCasterOcclusion")
                {
                    DrawProperty(prop, !config.useGPUOcclusionCulling, indented: true);
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
                    DrawProperty(prop, config.defaultSmallMeshFadeMode != MinScreenSizeFadeMode.Crossfade);
                    continue;
                }

                EditorGUILayout.PropertyField(prop, true);

                if (prop.name == "maxPoolRefsPerChunk")
                {
                    int maxInstances = config.maxPoolRefsPerChunk * GPUBatcherRenderer.instancesPerPool;
                    EditorGUILayout.LabelField($"  Max {maxInstances:N0} instances per chunk", EditorStyles.miniLabel);
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

        private static void DrawProperty(SerializedProperty property, bool disabled, bool indented = false)
        {
            using (new EditorGUI.IndentLevelScope(indented ? 1 : 0))
            using (new EditorGUI.DisabledScope(disabled))
                EditorGUILayout.PropertyField(property, true);
        }
    }
}
