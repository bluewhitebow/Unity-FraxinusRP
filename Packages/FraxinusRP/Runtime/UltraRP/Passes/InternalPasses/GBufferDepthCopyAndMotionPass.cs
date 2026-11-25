using System.Linq;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    [InjectPass(typeof(GBufferPassNode), InjectionPoint.After, true)]
    public class GBufferDepthCopyAndMotionPass : DepthCopyAndMotionPassBase
    {
        public GBufferDepthCopyAndMotionPass() : base() { }

        public override void Execute(RenderGraph graph, ScriptableRenderContext context, ContextContainer frame)
        {
            // Child GBuffer nodes publish their own required depth explicitly.
            // They also have no camera temporal history to update here.
            if (!frame.Get<CameraData>().isSubContext)
                base.Execute(graph, context, frame);
        }

        public override bool IsEligibleToInject(ScriptableRenderer renderer, ScriptableRenderPassNode node)
        {
            var nodes = renderer.sortedNodes;
            var lastGBufferPass = nodes.OfType<GBufferPassNode>().LastOrDefault(n => n.active && renderer.IsSameContext(n, node));
            
            return node == lastGBufferPass;
        }
    }
}

