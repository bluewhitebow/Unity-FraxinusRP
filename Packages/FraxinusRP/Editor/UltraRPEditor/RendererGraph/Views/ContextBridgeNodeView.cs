using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Rendering.Ultra;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [NodeCustomEditor(typeof(ContextBridgeNode))]
    public class ContextBridgeNodeView : ScriptableRenderPassNodeView
    {
        protected override float _minNodeWidth => 320f;
        protected override bool _showDefaultInspector => false;
        protected override bool _showActiveToggle => false;
        
        public override void Enable(bool fromInspector)
        {
            base.Enable(fromInspector);
            
            if (!fromInspector)
            {
                style.minHeight = 50;
                titleContainer.style.backgroundColor = new Color(0.25f, 0.28f, 0.45f, 0.9f);
            }
        }
    }
}
