#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using FraxinusEngine.Rendering.Ultra;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher
{
    internal static class GPUBatcherDebugUI
    {
        private const string k_panelName = "GPU Batcher";
        private const int k_panelOrder = 20;
        private static DebugUI.Widget[] s_widgets;
        internal static string panelName => k_panelName;

        internal static void Initialize()
        {
            RenderPipelineManager.activeRenderPipelineCreated -= RegisterPanel;
            RenderPipelineManager.activeRenderPipelineCreated += RegisterPanel;
            RenderPipelineManager.activeRenderPipelineDisposed -= UnregisterPanel;
            RenderPipelineManager.activeRenderPipelineDisposed += UnregisterPanel;
            RegisterPanel();
        }

        private static void RegisterPanel()
        {
            if (s_widgets != null || !(RenderPipelineManager.currentPipeline is UltraRenderPipeline))
                return;

            var panel = GetPanel();
            panel.flags |= DebugUI.Flags.EditorForceUpdate;
            s_widgets = CreateWidgets();
            panel.children.Add(s_widgets);
        }

        private static void UnregisterPanel()
        {
            if (s_widgets == null) return;
            var panel = DebugManager.instance.GetPanel(k_panelName);
            panel?.children.Remove(s_widgets);
            s_widgets = null;
            RemovePanelIfEmpty(panel);
        }

        internal static DebugUI.Panel GetPanel() =>
            DebugManager.instance.GetPanel(k_panelName, createIfNull: true, groupIndex: k_panelOrder);

        internal static void RemovePanelIfEmpty(DebugUI.Panel panel)
        {
            if (panel != null && panel.children.Count == 0)
                DebugManager.instance.RemovePanel(k_panelName);
        }

        private static DebugUI.Widget[] CreateWidgets() => new DebugUI.Widget[]
        {
            new DebugUI.Value
            {
                displayName = "Renderer Status",
                getter = () => GPUBatcherRenderer.initializedRenderer != null ? "Initialized" : "Not initialized",
                refreshRate = 0.25f
            },
            new DebugUI.Value
            {
                displayName = "Config Loaded",
                getter = () => GPUBatcherConfig.loadedConfig != null,
                refreshRate = 0.25f
            },
            new DebugUI.Value
            {
                displayName = "Indirect Submission Available",
                getter = () => GPUBatcherRenderer.canSubmitDraws,
                refreshRate = 0.25f
            },

            ConfigToggle("Disable Crossfade", "Disable LOD crossfading for GPU Batcher instances.",
                config => config.debugDisableCrossfade, (config, value) =>
                {
                    config.debugDisableCrossfade = value;
                    GPUBatcherRenderer.initializedRenderer?.MarkBatchesDirtyForFullRebuild();
                }),
#if DEBUG
            ConfigToggle("Enable Debug Logging", "Write GPU Batcher diagnostic messages to the Console.",
                config => config.enableDebugLogging, (config, value) => config.enableDebugLogging = value),
            ConfigToggle("Disable GPU Culling", "Reuse previous visibility and indirect arguments instead of dispatching GPU culling.",
                config => config.debugDisableCulling, (config, value) => config.debugDisableCulling = value),
            ConfigToggle("Disable Rendering", "Run culling without emitting BRG draw commands.",
                config => config.debugDisableRendering, (config, value) => config.debugDisableRendering = value),
#endif
#if UNITY_EDITOR
            ConfigToggle("Disable CPU Chunk Culling", "Skip CPU chunk frustum/distance tests. GPU culling still runs.",
                config => config.debugDisableCPUChunkCulling, (config, value) => config.debugDisableCPUChunkCulling = value,
                flags: DebugUI.Flags.EditorOnly),
            ConfigToggle("Show Chunk Bounds", "Draw chunk bounds for the selected registerer in Scene view.",
                config => config.showChunkBounds, (config, value) => config.showChunkBounds = value,
                flags: DebugUI.Flags.EditorOnly),
            ConfigToggle("Defer Pool Frees", "Release pools after staged uploads have completed.",
                config => config.debugDeferPoolFrees, (config, value) => config.debugDeferPoolFrees = value,
                flags: DebugUI.Flags.EditorOnly),
            ConfigToggle("Visualize Culling In Scene View", "Show the reference Game camera's culling result in Scene view. Session only.",
                config => config.debugVisualizeCullingInSceneView, (config, value) => config.debugVisualizeCullingInSceneView = value,
                persistent: false, flags: DebugUI.Flags.EditorOnly),
            ConfigToggle("Draw Bounding Spheres", "Draw visible instance bounding spheres by LOD. Session only.",
                config => config.debugDrawBoundingSpheres, (config, value) => config.debugDrawBoundingSpheres = value,
                persistent: false, flags: DebugUI.Flags.EditorOnly),
            ConfigToggle("Validate GPU Uploads", "Compare GPU uploads with the CPU mirror. Adds GPU readbacks. Session only.",
                config => config.debugValidateUploads, (config, value) => config.debugValidateUploads = value,
                persistent: false, flags: DebugUI.Flags.EditorOnly),
#if DEBUG
            new DebugUI.BoolField
            {
                displayName = "Compute Per-View Stats",
                tooltip = "Read back rendered instance, triangle and vertex counts. Details are shown in the GPU Batcher System Window.",
                flags = DebugUI.Flags.EditorOnly,
                getter = () => GPUBatcherRenderer.initializedRenderer?.computeRenderedTriangles ?? false,
                setter = value =>
                {
                    var renderer = GPUBatcherRenderer.initializedRenderer;
                    if (renderer != null) renderer.computeRenderedTriangles = value;
                },
                isHiddenCallback = () => GPUBatcherRenderer.initializedRenderer == null
            },
#endif
#endif
#if DEBUG
            Counter("Instances", renderer => renderer.statInstances),
            Counter("Chunks Used", renderer => renderer.statChunksUsed),
            Counter("Chunks Free", renderer => renderer.statChunksFree),
            Counter("Pools Used", renderer => renderer.statPoolsUsed),
            Counter("Pools Free", renderer => renderer.statPoolsFree),
            Counter("Pool Utilization (%)", renderer => renderer.statUtilization),
            Counter("Batches", renderer => renderer.statBatchCount),
            Counter("Camera Draw Commands", renderer => renderer.statCameraDrawCommands),
            Counter("Shadow Draw Commands", renderer => renderer.statShadowDrawCommands),
            Counter("Camera Buckets", renderer => renderer.statCameraBuckets),
            Counter("Shadow Buckets", renderer => renderer.statShadowBuckets),
            Counter("CPU Visible Chunks", renderer => renderer.statOcclusionPass),
            Counter("CPU Culled Chunks", renderer => renderer.statOcclusionFail),
            Counter("Registered Meshes", renderer => renderer.statRegisteredMeshes),
            Counter("Registered Materials", renderer => renderer.statRegisteredMaterials),
#endif
        };

        private static DebugUI.BoolField ConfigToggle(string label, string tooltip,
            Func<GPUBatcherConfig, bool> getter, Action<GPUBatcherConfig, bool> setter,
            bool persistent = true, DebugUI.Flags flags = DebugUI.Flags.None)
        {
            return new DebugUI.BoolField
            {
                displayName = label,
                tooltip = tooltip,
                flags = flags,
                getter = () =>
                {
                    var config = GPUBatcherConfig.loadedConfig;
                    return config != null && getter(config);
                },
                setter = value =>
                {
                    var config = GPUBatcherConfig.loadedConfig;
                    if (config == null) return;
#if UNITY_EDITOR
                    if (persistent) UnityEditor.Undo.RecordObject(config, label);
#endif
                    setter(config, value);
#if UNITY_EDITOR
                    if (persistent) UnityEditor.EditorUtility.SetDirty(config);
#endif
                },
                isHiddenCallback = () => GPUBatcherConfig.loadedConfig == null
            };
        }

#if DEBUG
        private static DebugUI.Value Counter(string label, Func<GPUBatcherRenderer, object> getter)
        {
            return new DebugUI.Value
            {
                displayName = label,
                getter = () =>
                {
                    var renderer = GPUBatcherRenderer.initializedRenderer;
                    return renderer == null ? 0 : getter(renderer);
                },
                refreshRate = 0.25f
            };
        }
#endif
    }
}
#endif
