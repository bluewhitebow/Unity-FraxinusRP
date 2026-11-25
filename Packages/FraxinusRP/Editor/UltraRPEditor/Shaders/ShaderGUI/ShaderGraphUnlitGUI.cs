using UnityEditor;
using UnityEngine;
using static FraxinusEditor.Rendering.Ultra.ShaderEditorUtils;

namespace FraxinusEditor.Rendering.Ultra
{
    // Used for ShaderGraph Unlit shaders
    class ShaderGraphUnlitGUI : BaseShaderGUI
    {
        private MaterialProperty[] _properties;

        // collect properties from the material properties
        public override void FindProperties(MaterialProperty[] properties)
        {
            // save off the list of all properties for shadergraph
            _properties = properties;

            base.FindProperties(properties);
        }

        public static void UpdateMaterial(Material material, MaterialUpdateType updateType)
        {
            bool automaticRenderQueue = GetAutomaticQueueControlSetting(material);
            BaseShaderGUI.UpdateMaterialSurfaceOptions(material, automaticRenderQueue);
            BaseShaderGUI.UpdateMotionVectorKeywordsAndPass(material);
        }

        public override void ValidateMaterial(Material material)
        {
            UpdateMaterial(material, MaterialUpdateType.ModifiedMaterial);
        }

        // material main surface inputs
        public override void DrawSurfaceInputs(Material material)
        {
            DrawShaderGraphProperties(material, _properties);
        }

        public override void DrawAdvancedOptions(Material material)
        {
            // Always show the queue control field.  Only show the render queue field if queue control is set to user override
            DoPopup(Styles.queueControl, queueControlProp, Styles.queueControlNames);
            if (material.HasProperty(UltraRPProperty.QueueControl) && material.GetFloat(UltraRPProperty.QueueControl) == (float)QueueControl.UserOverride)
                materialEditor.RenderQueueField();
            base.DrawAdvancedOptions(material);
            materialEditor.DoubleSidedGIField();
        }
    }
} // namespace FraxinusEditor.Rendering.Ultra
