using FraxinusEngine.Rendering.Ultra;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [NodeCustomEditor(typeof(MainLightShadowCasterPass))]
    public class MainLightShadowCasterPassNodeView : ScriptableRenderPassNodeView
    {
        protected override bool _showRenderOrder => true;

    }
}
