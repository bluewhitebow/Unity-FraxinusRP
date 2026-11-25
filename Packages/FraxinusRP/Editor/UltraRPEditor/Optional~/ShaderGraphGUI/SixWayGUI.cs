using UnityEngine;
using UnityEngine.Rendering;

using UnityEditor;

namespace FraxinusEditor.Rendering.Ultra
{
    // Used for ShaderGraph Lit shaders
    class SixWayGUI : ShaderGraphLitGUI
    {
        private MaterialProperty _useColorAbsorption;

        /// <summary>
        /// Container for the text and tooltips used to display the shader.
        /// </summary>
        protected class SixWayStyles
        {
            // Categories
            /// <summary>
            /// The text and tooltip for the surface options GUI.
            /// </summary>
            public static readonly GUIContent UseColorAbsorptionText =
                EditorGUIUtility.TrTextContent("Use Color Absorption", "When enabled, the lightmaps are used to simulate color absorption whose strength can be tuned with the Absorption Strength parameter.");
        }

        // collect properties from the material properties
        public override void FindProperties(MaterialProperty[] properties)
        {
            base.FindProperties(properties);
            _useColorAbsorption = FindProperty(UltraRPSixWaySubTarget.SixWayProperties.UseColorAbsorption, properties, false);
        }

        public override void DrawSurfaceOptions(Material material)
        {
            base.DrawSurfaceOptions(material);
            if (_useColorAbsorption != null)
                materialEditor.ShaderProperty(_useColorAbsorption, SixWayStyles.UseColorAbsorptionText);
        }

        public static void UpdateSixWayKeywords(Material material)
        {
            bool useColorAbsorptionValue = false;
            if (material.HasProperty(UltraRPSixWaySubTarget.SixWayProperties.UseColorAbsorption))
                useColorAbsorptionValue = material.GetFloat(UltraRPSixWaySubTarget.SixWayProperties.UseColorAbsorption) > 0.0f;
            CoreUtils.SetKeyword(material, "_SIX_WAY_COLOR_ABSORPTION", useColorAbsorptionValue);
        }

        public override void ValidateMaterial(Material material)
        {
            base.ValidateMaterial(material);
            UpdateSixWayKeywords(material);
        }
    }
} // namespace FraxinusEditor.Rendering.Ultra
