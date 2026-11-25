using UnityEngine.UIElements;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomPropertyDrawer(typeof(RenderGraphSettings))]
    class RenderGraphPropertyDrawer : PropertyDrawer
    {
        VisualElement _root;

        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            _root = new VisualElement();
            return _root;
        }
    }
}
