// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using FraxinusEngine.Rendering.Ultra;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;

namespace Fraxinus.GPUBatcher.Editor
{
    // editor lifecycle: play mode transitions, assembly reload, prefab change detection
    [InitializeOnLoad]
    internal static class GPUBatcherEditorIntegration
    {
        private static readonly HashSet<GameObject> s_dirtyPrototypes = new();
        private static bool s_pendingProcess;

        static GPUBatcherEditorIntegration()
        {
            GPUBatcherRenderer.editorOnPrototypeDirty = OnMarkPrototypeDirty;

            // domain reload just completed - safe to auto-create instances again
            GPUBatcherRenderer.ClearShutdownGuard();

            // editor lifecycle
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            EditorApplication.quitting -= OnEditorQuitting;
            EditorApplication.quitting += OnEditorQuitting;

            Application.quitting -= OnApplicationQuitting;
            Application.quitting += OnApplicationQuitting;

            // scene view overlay for frozen culling visualization
            SceneView.duringSceneGui -= OnSceneViewGUI;
            SceneView.duringSceneGui += OnSceneViewGUI;

            // prefab change detection
            PrefabUtility.prefabInstanceUpdated -= OnPrefabInstanceUpdated;
            PrefabUtility.prefabInstanceUpdated += OnPrefabInstanceUpdated;

            PrefabStage.prefabSaved -= OnPrefabStageSaved;
            PrefabStage.prefabSaved += OnPrefabStageSaved;

        }

        #region Lifecycle and cleanup
        private static void Cleanup()
        {
            GPUBatcherRenderer.Shutdown();
            TerrainGPUBatcherRegisterer.DisposeStaticNativeCollections();
            s_dirtyPrototypes.Clear();
            s_pendingProcess = false;
        }

        private static void OnEditorQuitting() => Cleanup();
        private static void OnApplicationQuitting() => Cleanup();
        private static void OnBeforeAssemblyReload() => Cleanup();

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // clean up when exiting either mode
            if (state == PlayModeStateChange.ExitingPlayMode ||
                state == PlayModeStateChange.ExitingEditMode)
            {
                Cleanup();
                // prevents re-creation until domain reload
            }
            // safety net when fully entered edit mode
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                Cleanup();
                GPUBatcherRenderer.ClearShutdownGuard();  // safe to auto-create again
            }
        }

        #endregion

        #region Scene View Overlay

        private static GUIStyle s_overlayStyle;
        private static int s_overlayStyleFontSize;

        private static void OnSceneViewGUI(SceneView sceneView)
        {
            if (!GPUBatcherRenderer.hasInstance) return;
            var brg = GPUBatcherRenderer.instance;
            if (brg == null || !brg.isInitialized) return;
            if (!GPUBatcherConfig.instance.debugVisualizeCullingInSceneView) return;

            var refCam = UltraRenderPipelineCameraState.referenceCamera;
            string camName = refCam != null ? refCam.name : "(none)";

            int fontSize = Mathf.Clamp((int)(sceneView.position.width / 45f), 10, 18);
            if (s_overlayStyle == null || s_overlayStyleFontSize != fontSize)
            {
                s_overlayStyleFontSize = fontSize;
                s_overlayStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = fontSize,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white },
                    padding = new RectOffset(10, 10, 5, 5)
                };
            }

            Handles.BeginGUI();
            string text = $"Visualizing Culling Results for Camera \"{camName}\"";
            var content = new GUIContent(text);
            var size = s_overlayStyle.CalcSize(content);
            float x = (sceneView.position.width - size.x) * 0.5f;
            float y = 4f;
            // dark background for contrast
            var bgRect = new Rect(x - 2, y - 1, size.x + 4, size.y + 2);
            EditorGUI.DrawRect(bgRect, new Color(0f, 0f, 0f, 0.65f));
            GUI.Label(new Rect(x, y, size.x, size.y), content, s_overlayStyle);
            Handles.EndGUI();
        }

        #endregion

        #region Prototype/Prefab Change Management

        private static void OnPrefabStageSaved(GameObject prefabContentsRoot)
        {
            // prefabContentsRoot is a staging object, not the asset.
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null) return;

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabStage.assetPath);
            if (prefabAsset == null) return;

            if (GPUBatcherRenderer.hasInstance && GPUBatcherRenderer.instance.isInitialized)
                OnMarkPrototypeDirty(prefabAsset);
        }

        private static void OnPrefabInstanceUpdated(GameObject instance)
        {
            var prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(instance);
            if (prefabAsset == null) return;

            if (GPUBatcherRenderer.hasInstance && GPUBatcherRenderer.instance.isInitialized)
                OnMarkPrototypeDirty(prefabAsset);
        }

        private static void OnMarkPrototypeDirty(GameObject prefab)
        {
            if (prefab == null) return;

            var prefabAsset = ResolvePrefabAsset(prefab);
            if (prefabAsset == null) return;

            // check if prototype is registered
            if (GPUBatcherRenderer.hasInstance
                && GPUBatcherRenderer.instance.isInitialized
                && !GPUBatcherRenderer.instance.IsRegisteredPrototype(prefabAsset))
            {
                return;
            }

            s_dirtyPrototypes.Add(prefabAsset);
            ScheduleProcess();
        }

        private static void ScheduleProcess()
        {
            if (!s_pendingProcess)
            {
                s_pendingProcess = true;
                EditorApplication.delayCall += ProcessDirtyPrototypes;
            }
        }

        private static void ProcessDirtyPrototypes()
        {
            s_pendingProcess = false;

            if (s_dirtyPrototypes.Count == 0)
                return;

            if (!GPUBatcherRenderer.hasInstance || !GPUBatcherRenderer.instance.isInitialized)
            {
                s_dirtyPrototypes.Clear();
                return;
            }

            var sys = GPUBatcherRenderer.instance;

            foreach (var prefab in s_dirtyPrototypes)
            {
                if (prefab == null) continue;
                sys.RefreshPrototype(prefab);
            }

            s_dirtyPrototypes.Clear();
        }

        private static GameObject ResolvePrefabAsset(GameObject obj)
        {
            if (EditorUtility.IsPersistent(obj))
                return obj;
            return PrefabUtility.GetCorrespondingObjectFromOriginalSource(obj);
        }

        #endregion
    }
}
