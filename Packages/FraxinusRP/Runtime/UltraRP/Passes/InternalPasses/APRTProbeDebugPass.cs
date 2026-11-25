using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    // Probe spheres read the current camera's relight buffers directly. Keep
    // their dependencies in the graph, rather than emitting temporary renderers.
    [InjectPass(typeof(DrawObjectsPass), InjectionPoint.After, true, scopeFilter = ContextScopeFilter.Main)]
    [InjectPass(typeof(DeferredObjectPass), InjectionPoint.After, true, scopeFilter = ContextScopeFilter.Main)]
    [InjectPass(typeof(GBufferPassNode), InjectionPoint.After, true, scopeFilter = ContextScopeFilter.Main)]
    public class APRTProbeDebugPass : InjectPass
    {
        static readonly ProfilingSampler s_Sampler = new ProfilingSampler("APRT Probe Debug");

        sealed class PassData
        {
            internal APRTProbeReferenceVolume volume;
            internal APRTProbeReferenceVolume.ProbeDebugDraw draw;
        }

        protected override void OnCameraCleanup(CommandBuffer cmd) { }

        protected override void OnDispose() { }

        public override bool IsEligibleToInject(ScriptableRenderer renderer, ScriptableRenderPassNode node)
        {
            // Keep probe spheres after the final opaque pass in this scope.
            // The scope filter prevents duplicate draws in same-camera children.
            ScriptableRenderPassNode lastOpaque = null;
            foreach (var candidate in renderer.sortedNodes)
            {
                if (candidate is ScriptableRenderPassNode pass && pass.active && renderer.IsSameContext(pass, node) &&
                    (pass is DrawObjectsPass draw && draw.isOpaque || pass is DeferredObjectPass || pass is GBufferPassNode))
                    lastOpaque = pass;
            }
            return node == lastOpaque;
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            var resources = frameData.Get<ResourceData>();
            Render(renderGraph, frameData, resources.activeColorTexture, resources.activeDepthTexture);
        }

        internal static void Render(RenderGraph renderGraph, ContextContainer frameData,
            TextureHandle colorTarget, TextureHandle depthTarget)
        {
            var asset = UltraRenderPipeline.asset;
            if (asset == null || !asset.supportAPRTV || !colorTarget.IsValid() || !depthTarget.IsValid())
                return;
            var cameraData = frameData.Get<CameraData>();
            if (!APRTRelightPass.CanUpdate(cameraData) || !APRTRelightPass.HasLightingResources(frameData))
                return;
            var volume = APRTProbeReferenceVolume.instance;
            if (!volume.showProbeDebug) return;

            var runtimes = volume.GetRenderingBakingSetRuntimes(cameraData.camera);
            for (int i = 0; i < runtimes.Count; ++i)
            {
                var runtime = runtimes[i];
                var settings = APRTRelightPass.ResolveSettings(VolumeManager.instance.stack, runtime.currentBakingSet);
                if (!runtime.TryGetProbeDebugDraw(cameraData.camera, settings, out var draw)) continue;
                draw.properties.SetMatrix("_APRTProbeDebugViewProjection",
                    cameraData.GetGPUProjectionMatrix() * cameraData.GetViewMatrix());
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("APRT Probe Debug", out var data, s_Sampler))
                {
                    data.volume = runtime;
                    data.draw = draw;
                    builder.SetRenderAttachment(colorTarget, 0, AccessFlags.ReadWrite);
                    builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Read);
                    // Reuse the handles that declared this camera's compute writes.
                    // Importing the same native buffer again creates another graph
                    // resource and would hide the relight-to-debug dependency.
                    APRTRelightPass.UseLightingResources(builder, frameData);
                    builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
                        pass.volume.DrawProbeDebug(context.cmd, pass.draw));
                }
            }
        }
    }
}
