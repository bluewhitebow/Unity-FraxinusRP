using FraxinusEngine.Rendering.Ultra;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [NodeCustomEditor(typeof(ColorGradingLutPass))]
    public class ColorGradingLutPassNodeView : ScriptableRenderPassNodeView
    {
        protected override bool _showRenderOrder => true;

    }
}
