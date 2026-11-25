using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace UnityEditor.Rendering
{
    [CustomPropertyDrawer(typeof(RenderPipelineGraphicsSettingsContainer))]
    class RenderPipelineGraphicsSettingsContainerPropertyDrawer : PropertyDrawer
    {
        // Unity 2022's Project Settings inspector is still IMGUI based. The
        // Unity 6 implementation only supplied CreatePropertyGUI, so Unity
        // fell back to its "No GUI Implemented" placeholder whenever this
        // compatibility container was shown from an IMGUI PropertyField.
        // Keep the UI Toolkit path above, and provide the equivalent IMGUI
        // bridge for the backported settings surface.
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var graphicsSettings = property.FindPropertyRelative("m_SettingsList");
            if (graphicsSettings != null)
                EditorGUI.PropertyField(position, graphicsSettings, label, true);
            else
                EditorGUI.LabelField(position, label, EditorGUIUtility.TrTextContent("Settings are unavailable."));

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var graphicsSettings = property.FindPropertyRelative("m_SettingsList");
            return graphicsSettings != null
                ? EditorGUI.GetPropertyHeight(graphicsSettings, label, true)
                : EditorGUIUtility.singleLineHeight;
        }

        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var root = new VisualElement { name = "GlobalSettingsContainer" };
            var graphicsSettings = property.FindPropertyRelative("m_SettingsList");
            Debug.Assert(graphicsSettings != null);

            root.Add(new PropertyField(graphicsSettings));
            return root;
        }
    }
}
