using FraxinusEngine.Rendering.Ultra;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [NodeCustomEditor(typeof(AdditionalLightsShadowCasterPass))]
    public class AdditionalLightsShadowCasterPassNodeView : ScriptableRenderPassNodeView
    {
        protected override bool _showRenderOrder => true;

    }
}
