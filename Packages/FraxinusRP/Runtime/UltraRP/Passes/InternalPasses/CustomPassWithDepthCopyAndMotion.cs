using System.Linq;

namespace FraxinusEngine.Rendering.Ultra
{
    [InjectPass(typeof(DrawObjectsPass), InjectionPoint.After, true)]
    [InjectPass(typeof(DeferredObjectPass), InjectionPoint.After, true)]
    public class CustomPassWithDepthCopyAndMotion : DepthCopyAndMotionPassBase
    {
        public CustomPassWithDepthCopyAndMotion() : base() { }

        public override bool IsEligibleToInject(ScriptableRenderer renderer, ScriptableRenderPassNode node)
        {
            var nodes = renderer.sortedNodes;
            var lastOpaqueDrawPass = nodes.OfType<DrawObjectsPass>().LastOrDefault(n => n.isOpaque && n.active && renderer.IsSameContext(n, node));
            var lastDeferredPass = nodes.OfType<DeferredObjectPass>().LastOrDefault(p => p.active && renderer.IsSameContext(p, node));
            
            int drawPassIndex = lastOpaqueDrawPass != null ? nodes.IndexOf(lastOpaqueDrawPass) : -1;
            int deferredPassIndex = lastDeferredPass != null ? nodes.IndexOf(lastDeferredPass) : -1;

            ScriptableRenderPassNode targetNode = null;

            // Always inject after the last opaque pass (DeferredObjectPass or DrawObjectsPass)
            // This ensures AfterOpaques depth copy works correctly
            if (drawPassIndex > deferredPassIndex)
                targetNode = lastOpaqueDrawPass;
            else if (deferredPassIndex >= 0)
                targetNode = lastDeferredPass;

            return node == targetNode;
        }
    }

}