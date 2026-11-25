#if HAS_VFX_GRAPH
using FraxinusEditor.Rendering.Ultra;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor.ShaderGraph;

namespace UnityEditor.VFX.UltraRP
{
    internal class VFXShaderGraphLitGUI : ShaderGraphLitGUI
    {
        protected override uint materialFilter => uint.MaxValue & ~(uint)Expandable.SurfaceInputs;
    }

    internal class VFXShaderGraphUnlitGUI : ShaderGraphUnlitGUI
    {
        protected override uint materialFilter => uint.MaxValue & ~(uint)Expandable.SurfaceInputs;
    }

    internal class VFXSixWayGUI : SixWayGUI
    {
        protected override uint materialFilter => uint.MaxValue & ~(uint)Expandable.SurfaceInputs;
    }

    internal class VFXGenericShaderGraphMaterialGUI : GenericShaderGraphMaterialGUI
    {
        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] props)
        {
            //When material used in VFX, all properties are converted to input slots.
            //This fallback is used with sprite output.
        }
    }

}
#endif
