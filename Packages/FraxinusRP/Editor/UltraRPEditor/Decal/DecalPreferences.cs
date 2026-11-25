using UnityEngine;

namespace FraxinusEditor.Rendering.Ultra
{
    /// <summary>
    /// Decal preferences for UltraRP
    /// </summary>
    internal static class DecalPreferences
    {
        private static Color s_DecalGizmoColor = new Color(0.5f, 0.8f, 1.0f, 0.5f);
        
        /// <summary>
        /// Color used for decal gizmos in the scene view
        /// </summary>
        public static Color decalGizmoColor
        {
            get => s_DecalGizmoColor;
            set => s_DecalGizmoColor = value;
        }
    }
}
