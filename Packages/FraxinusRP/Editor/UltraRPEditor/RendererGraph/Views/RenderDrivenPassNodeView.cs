using FraxinusEngine.Rendering.Ultra;

using UnityEditor.Experimental.GraphView;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [NodeCustomEditor(typeof(RenderDrivenPass))]
    public class RenderDrivenPassNodeView : ScriptableRenderPassNodeView
    {
        protected RenderDrivenPass _passNode;
        protected ScriptableRendererGraph _graph;

        protected override bool _showActiveToggle => false;

        public override void Enable(bool fromInspector)
        {
            capabilities &= ~Capabilities.Deletable;
            _passNode = nodeTarget as RenderDrivenPass;
            _graph = owner.graph as ScriptableRendererGraph;

            base.Enable(fromInspector);
        }
    }
}
