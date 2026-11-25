using UnityEngine;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    public static class EditorUtils
    {
        public enum Unit { Metric, Percent }

        public class Styles
        {
            //Measurements
            public static float defaultLineSpace = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            public static readonly GUIContent alembicMotionVectors = EditorGUIUtility.TrTextContent("Alembic Motion Vectors",
                "When enabled, the material will use motion vectors from the Alembic animation cache. Should not be used on regular meshes or Alembic caches without precomputed motion vectors.");
        }

        internal static void DrawRenderingLayerMask(SerializedProperty property, GUIContent style)
        {
            RenderingLayerMaskEditorGUI.RenderingLayerMaskField(
                EditorGUILayout.GetControlRect(), style, property);
        }

        internal static void FeatureHelpBox(string message, MessageType type)
        {
            CoreEditorUtils.DrawFixMeBox(message, type, "Open", () =>
            {
                EditorUtility.OpenPropertyEditor(UltraRenderPipeline.asset.scriptableRendererGraph);
                GUIUtility.ExitGUI();
            });
        }
    }
}
