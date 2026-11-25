using FraxinusEngine.Rendering.Ultra;

using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [NodeCustomEditor(typeof(DrawObjectsPass))]
    public class DrawObjectsPassNodeView : ScriptableRenderPassNodeView
    {
        protected DrawObjectsPass _passNode;
        protected ScriptableRendererGraph _graph;

        public override void Enable(bool fromInspector)
        {
            base.Enable(fromInspector);

            _passNode = nodeTarget as DrawObjectsPass;
            _graph = owner.graph as ScriptableRendererGraph;
        }
    }
}
