using System.Linq;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    [InjectPass(typeof(DrawObjectsPass), InjectionPoint.After, true)]
    [InjectPass(typeof(DeferredObjectPass), InjectionPoint.After, true)]
    public class InvokeOnRenderObjectCallbackPass : InjectPass
    {
        private class PassData
        {
            internal TextureHandle colorTarget;
            internal TextureHandle depthTarget;
        }

        protected override void OnCameraCleanup(CommandBuffer cmd) { }

        protected override void OnDispose() { }

        public override bool IsEligibleToInject(ScriptableRenderer renderer, ScriptableRenderPassNode node)
        {
            var nodes = renderer.sortedNodes;
            var lastOpaqueDrawPass = nodes.OfType<DrawObjectsPass>().LastOrDefault(n => n.isOpaque && n.active && renderer.IsSameContext(n, node));
            var lastDeferredPass = nodes.OfType<DeferredObjectPass>().LastOrDefault(p => p.active && renderer.IsSameContext(p, node));
            int drawPassIndex = lastOpaqueDrawPass != null ? nodes.IndexOf(lastOpaqueDrawPass) : -1;
            int deferredPassIndex = lastDeferredPass != null ? nodes.IndexOf(lastDeferredPass) : -1;

            ScriptableRenderPassNode targetNode = null;
            if (drawPassIndex > deferredPassIndex)
                targetNode = lastOpaqueDrawPass;
            else if (deferredPassIndex >= 0)
                targetNode = lastDeferredPass;

            return node == targetNode;
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();

            using (var builder = renderGraph.AddUnsafePass<PassData>(_passName, out var passData, _profilingSampler))
            {
                var colorTarget = resourceData.activeColorTexture;
                var depthTarget = resourceData.activeDepthTexture;

                passData.colorTarget = colorTarget;
                builder.UseTexture(colorTarget, AccessFlags.Write);
                passData.depthTarget = depthTarget;
                builder.UseTexture(depthTarget, AccessFlags.Write);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                {
                    context.cmd.SetRenderTarget(data.colorTarget, data.depthTarget);
                    context.cmd.InvokeOnRenderObjectCallbacks();
                });
            }
        }
    }
}
