using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Rendering.Ultra;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [NodeCustomEditor(typeof(ContextEndNode))]
    public class ContextEndNodeView : ScriptableRenderPassNodeView
    {
        protected override float _minNodeWidth => 320f;
        protected override bool _showActiveToggle => false;
        
        public override void Enable(bool fromInspector)
        {
            base.Enable(fromInspector);
        }
    }
}
