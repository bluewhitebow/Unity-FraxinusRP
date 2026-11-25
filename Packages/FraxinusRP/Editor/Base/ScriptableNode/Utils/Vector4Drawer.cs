using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace FraxinusEditor.Base
{
    [CustomPropertyDrawer(typeof(Vector4))]
    public class IngredientDrawerUIE : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var vectorField = new Vector4Field() { value = property.vector4Value };
            vectorField.RegisterValueChangedCallback(e =>
            {
                property.vector4Value = e.newValue;
                property.serializedObject.ApplyModifiedProperties();
            });

            return vectorField;
        }
    }
}