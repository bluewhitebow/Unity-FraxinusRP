using UnityEditor;
using UnityEngine;

namespace FraxinusEditor.Rendering.Ultra
{
    // Used for ShaderGraph Sprite shaders
    class ShaderGraphSpriteGUI : BaseShaderGUI
    {
        protected override uint materialFilter => uint.MaxValue & ~(uint)Expandable.SurfaceOptions;

        private MaterialProperty[] _properties;

        // collect properties from the material properties
        public override void FindProperties(MaterialProperty[] properties)
        {
            // save off the list of all properties for shadergraph
            _properties = properties;

            var material = materialEditor?.target as Material;
            if (material == null)
                return;

            base.FindProperties(properties);
        }

        public override void DrawSurfaceInputs(Material material)
        {
            DrawShaderGraphProperties(material, _properties);
        }
    }
} // namespace FraxinusEditor.Rendering.Ultra
