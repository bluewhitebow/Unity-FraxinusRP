#if FRAXINUS_RP && (UNITY_6000_0_OR_NEWER || TUANJIE_1_8_OR_NEWER)
// Adapted for Fraxinus GPU Batcher from BRG Instanced Renderer.
// Original implementation copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using FraxinusEngine.Rendering.Ultra;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace Fraxinus.GPUBatcher.Integration
{
    /// <summary>Connects GPU Batcher's compute stages to Fraxinus renderer graph injection points.</summary>
    internal static class GPUBatcherCullingInjector
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Initialize()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            if (!GPUBatcherRenderer.canSubmitDraws) return;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            GPUBatcherDebugUI.Initialize();
#endif
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void InitializeEditor()
        {
            Initialize();
            GPUBatcherConfig.hzbRetestPassCompatible = GPUBatcherRenderer.canSubmitDraws;
        }
#endif

        internal static bool HasMainInjection(ScriptableRenderer renderer, Type injectionType)
        {
            if (renderer == null) return false;
            int nesting = 0;
            foreach (var node in renderer.sortedNodes)
            {
                if (node is ContextBeginNode) { ++nesting; continue; }
                if (node is ContextEndNode) { nesting = Mathf.Max(0, nesting - 1); continue; }
                if (nesting != 0 || !(node is ScriptableRenderPassNode pass) || !pass.active ||
                    pass.computeOrder < 0 || !pass.canProcess || pass.graph == null || !pass.graph.IsInjectPassEnabled(injectionType)) continue;
                var before = pass.beforeRenderingPasses;
                for (int i = 0; i < before.Count; i++)
                    if (before[i].GetType() == injectionType) return true;
                var after = pass.afterRenderingPasses;
                for (int i = 0; i < after.Count; i++)
                    if (after[i].GetType() == injectionType) return true;
            }
            return false;
        }

        internal static bool SupportsOcclusion(Camera camera) => camera != null && camera.cameraType == CameraType.Game &&
            // Fraxinus CameraData currently exposes one rendered view/projection.
            // A stereo atlas cannot be tested against that mono matrix safely.
            !camera.stereoEnabled;

        internal static Type HZBInjectionType(bool retest) => retest ? typeof(GPUBatcherHZBPass) : typeof(GPUBatcherHZBHistoryPass);

        internal static Vector2Int DepthDimensions(RenderTextureDescriptor descriptor)
        {
            var size = new Vector2Int(descriptor.width, descriptor.height);
            if (descriptor.useDynamicScale)
            {
                size.x = Mathf.Max(1, Mathf.CeilToInt(size.x * ScalableBufferManager.widthScaleFactor));
                size.y = Mathf.Max(1, Mathf.CeilToInt(size.y * ScalableBufferManager.heightScaleFactor));
            }
            return size;
        }

        internal static bool IsOpaqueProducer(ScriptableRenderPassNode pass) =>
            pass is DrawObjectsPass draw && draw.isOpaque || pass is DeferredObjectPass || pass is GBufferPassNode;

        internal static ScriptableRenderPassNode LastMainOpaquePass(ScriptableRenderer renderer, ContextContainer frame)
        {
            ScriptableRenderPassNode last = null;
            int nesting = 0;
            foreach (var node in renderer.sortedNodes)
            {
                if (node is ContextBeginNode) { ++nesting; continue; }
                if (node is ContextEndNode) { nesting = Mathf.Max(0, nesting - 1); continue; }
                if (nesting != 0 || !(node is ScriptableRenderPassNode pass) || !pass.active ||
                    pass.computeOrder < 0 || !pass.canProcess) continue;
                if (IsOpaqueProducer(pass) && pass.SupportedInCurrentFrame(frame))
                    last = pass;
            }
            return last;
        }

        static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!GPUBatcherRenderer.hasInstance || !(GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset asset)) return;
            var batcher = GPUBatcherRenderer.instance;
            if (batcher == null || !batcher.isInitialized) return;
            int cameraId = GPUBatcherHelpers.GetViewID(camera);
            var renderer = camera.TryGetComponent(out UltraAdditionalCameraData data) ? data.scriptableRenderer : asset.scriptableRenderer;
            if (!HasMainInjection(renderer, typeof(GPUBatcherCullingPass)))
            {
                batcher.DisableCameraHZB(cameraId);
                return;
            }
            bool useHZB = batcher.isGPUOcclusionEnabled && SupportsOcclusion(camera) &&
                HasMainInjection(renderer, HZBInjectionType(GPUBatcherConfig.instance.useHZBRetestPass));
            if (!useHZB) batcher.DisableCameraHZB(cameraId);
            batcher.EnqueueDeferredCulling(cameraId, useHZB);
        }
    }

    // A single module can bind before several nodes. Fraxinus executes it once in the
    // main context, before the first bound shadow/depth producer in graph order.
    [InjectPass(typeof(MainLightShadowCasterPass), InjectionPoint.Before, scopeFilter = ContextScopeFilter.Main)]
    [InjectPass(typeof(AdditionalLightsShadowCasterPass), InjectionPoint.Before, scopeFilter = ContextScopeFilter.Main)]
    [InjectPass(typeof(RenderDrivenPass), InjectionPoint.Before, scopeFilter = ContextScopeFilter.Main)]
    public sealed class GPUBatcherCullingPass : InjectPass
    {
        public override void Execute(RenderGraph graph, ScriptableRenderContext context, ContextContainer frame)
        {
            var camera = frame.Get<CameraData>();
            if (frame.GetOrCreate<ContextStateManager>().IsInSubContext() || !GPUBatcherRenderer.hasInstance) return;
            var batcher = GPUBatcherRenderer.instance;
            int cameraId = GPUBatcherHelpers.GetViewID(camera.camera);
            if (batcher == null || !batcher.HasPendingCullingForCamera(cameraId)) return;
            // Allocate before importing the history atlas. A render-scale change must
            // not retire an RTHandle already captured by the phase-zero graph pass.
            if (batcher.isGPUOcclusionEnabled && batcher.HasPendingHZBForCamera(cameraId))
            {
                // Fraxinus can adjust overlay aspect and add temporal jitter after
                // beginCameraRendering; use the matrices that produced this depth.
                batcher.UpdatePendingHZBCameraData(cameraId,
                    GPUBatcherHZBBridge.ComputeCameraData(camera.GetViewMatrix(), camera.GetGPUProjectionMatrix()));
                var size = GPUBatcherCullingInjector.DepthDimensions(camera.cameraTargetDescriptor);
                batcher.EnsureHZBAtlas(camera.camera, size.x, size.y);
            }
            GPUBatcherRenderGraphPasses.RecordDeferredCullingPass(graph, batcher,
                "FraxinusGPUBatcher.Culling", cameraId, batcher.isGPUOcclusionEnabled);
        }
        protected override void OnCameraCleanup(CommandBuffer cmd) { }
        protected override void OnDispose() { }
    }

    internal static class GPUBatcherHZBInjection
    {
        sealed class HZBFrameData : ContextItem
        {
            public bool active;
            public bool recorded;
            public bool retest;
            public override void Reset() { active = false; recorded = false; retest = false; }
        }

        internal static void Reset(ContextContainer frame) => frame.GetOrCreate<HZBFrameData>().Reset();

        internal static RenderPassInputSummary Requirements(bool retest) => new RenderPassInputSummary
        {
            requiresDepthTexture = true,
            requiresDepthPrepass = retest,
            requiresFullDepthPrepass = retest,
            // History samples opaque depth, including when an unrelated prepass
            // was requested. Keep that attachment sampleable off the backbuffer.
            requiresIntermediateTexture = !retest,
            disableFramebufferFetch = true
        };

        internal static RenderPassInputSummary Prepare(ContextContainer frame, bool retest)
        {
            var camera = frame.Get<CameraData>();
            if (frame.GetOrCreate<ContextStateManager>().IsInSubContext() || !GPUBatcherRenderer.hasInstance)
                return default;
            var batcher = GPUBatcherRenderer.instance;
            if (batcher == null || GPUBatcherConfig.instance.useHZBRetestPass != retest) return default;
            int cameraId = GPUBatcherHelpers.GetViewID(camera.camera);
            if (!GPUBatcherCullingInjector.SupportsOcclusion(camera.camera) ||
                (camera.cameraTargetDescriptor.dimension != TextureDimension.Tex2D &&
                 camera.cameraTargetDescriptor.dimension != TextureDimension.Tex2DArray) ||
                !batcher.isGPUOcclusionEnabled || !batcher.HasPendingCullingForCamera(cameraId) ||
                !GPUBatcherCullingInjector.HasMainInjection(camera.renderer, typeof(GPUBatcherCullingPass)))
            {
                batcher.DisableCameraHZB(cameraId);
                return default;
            }
            var prepared = frame.GetOrCreate<HZBFrameData>();
            prepared.active = true;
            prepared.retest = retest;
            return Requirements(retest);
        }

        internal static void Record(RenderGraph graph, ContextContainer frame, bool retest)
        {
            var camera = frame.Get<CameraData>();
            if (frame.GetOrCreate<ContextStateManager>().IsInSubContext() || !frame.Contains<HZBFrameData>() || !GPUBatcherRenderer.hasInstance) return;
            var prepared = frame.Get<HZBFrameData>();
            if (!prepared.active || prepared.recorded || prepared.retest != retest) return;
            var batcher = GPUBatcherRenderer.instance;
            if (batcher == null || !batcher.isGPUOcclusionEnabled) return;
            int cameraId = GPUBatcherHelpers.GetViewID(camera.camera);
            var resources = frame.Get<ResourceData>();
            // Retest consumes the complete prepass. History consumes final opaque
            // depth even if cameraDepthTexture was copied earlier in the frame.
            var depth = retest ? resources.cameraDepthTexture : resources.activeDepthTexture;
            if (!depth.IsValid())
            {
                batcher.DisableCameraHZB(cameraId);
                return;
            }
            prepared.recorded = true;
            var size = GPUBatcherCullingInjector.DepthDimensions(camera.cameraTargetDescriptor);
            int width = size.x;
            int height = size.y;
            var sourceInfo = graph.GetRenderTargetInfo(depth);
            bool sourceArray = graph.GetTextureDesc(depth).dimension == TextureDimension.Tex2DArray;
            if (sourceInfo.msaaSamples > 1 && sourceInfo.bindMS)
            {
                depth = GPUBatcherRenderGraphPasses.RecordHZBMSAAResolve(graph, batcher, depth,
                    sourceInfo.width, sourceInfo.height, sourceArray);
                sourceArray = false; // the selected source slice resolves to a 2D texture
            }
            GPUBatcherRenderGraphPasses.RecordHZBBuildPass(graph, batcher, "FraxinusGPUBatcher.HZBBuild", cameraId, depth, width, height,
                sourceTextureArray: sourceArray);
            if (retest)
                GPUBatcherRenderGraphPasses.RecordHZBCullPass(graph, batcher, "FraxinusGPUBatcher.HZBRetest", cameraId);
        }
    }

    [InjectPass(typeof(RenderDrivenPass), InjectionPoint.After, scopeFilter = ContextScopeFilter.Main)]
    public sealed class GPUBatcherHZBPass : InjectPass
    {
        public override void ResetFrame(RenderGraph graph, ContextContainer frame) => GPUBatcherHZBInjection.Reset(frame);
        public override RenderPassInputSummary PrepareFrame(RenderGraph graph, ContextContainer frame) => GPUBatcherHZBInjection.Prepare(frame, true);
        public override void Execute(RenderGraph graph, ScriptableRenderContext context, ContextContainer frame) => GPUBatcherHZBInjection.Record(graph, frame, true);
        protected override void OnCameraCleanup(CommandBuffer cmd) { }
        protected override void OnDispose() { }
    }

    [InjectPass(typeof(DrawObjectsPass), InjectionPoint.After, scopeFilter = ContextScopeFilter.Main)]
    [InjectPass(typeof(DeferredObjectPass), InjectionPoint.After, scopeFilter = ContextScopeFilter.Main)]
    [InjectPass(typeof(GBufferPassNode), InjectionPoint.After, scopeFilter = ContextScopeFilter.Main)]
    public sealed class GPUBatcherHZBHistoryPass : InjectPass
    {
        public override bool IsEligibleToInject(ScriptableRenderer renderer, ScriptableRenderPassNode node) =>
            GPUBatcherCullingInjector.IsOpaqueProducer(node);
        public override bool ShouldExecute(ScriptableRenderPassNode node, ContextContainer frame) =>
            node == GPUBatcherCullingInjector.LastMainOpaquePass(frame.Get<CameraData>().renderer, frame);
        public override void ResetFrame(RenderGraph graph, ContextContainer frame) => GPUBatcherHZBInjection.Reset(frame);
        public override RenderPassInputSummary PrepareFrame(RenderGraph graph, ContextContainer frame) => GPUBatcherHZBInjection.Prepare(frame, false);
        public override void Execute(RenderGraph graph, ScriptableRenderContext context, ContextContainer frame) => GPUBatcherHZBInjection.Record(graph, frame, false);
        protected override void OnCameraCleanup(CommandBuffer cmd) { }
        protected override void OnDispose() { }
    }
}
#endif
