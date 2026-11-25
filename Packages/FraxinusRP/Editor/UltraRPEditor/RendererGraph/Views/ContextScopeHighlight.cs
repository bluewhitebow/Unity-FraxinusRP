using UnityEngine;
using UnityEngine.UIElements;

namespace FraxinusEditor.Rendering.Ultra
{
    public class ContextScopeHighlight : VisualElement
    {
        private static readonly Color s_highlightColor = new Color(0.25f, 0.88f, 0.82f, 0.15f);
        private static readonly Color s_borderColor = new Color(0.25f, 0.88f, 0.82f, 0.6f);

        public ContextScopeHighlight()
        {
            pickingMode = PickingMode.Ignore;

            style.position = Position.Absolute;
            style.backgroundColor = s_highlightColor;
            style.borderLeftWidth = 2f;
            style.borderRightWidth = 2f;
            style.borderTopWidth = 2f;
            style.borderBottomWidth = 2f;
            style.borderLeftColor = s_borderColor;
            style.borderRightColor = s_borderColor;
            style.borderTopColor = s_borderColor;
            style.borderBottomColor = s_borderColor;
            style.borderTopLeftRadius = 8f;
            style.borderTopRightRadius = 8f;
            style.borderBottomLeftRadius = 8f;
            style.borderBottomRightRadius = 8f;
        }

        public void UpdateBounds(Rect bounds)
        {
            style.left = bounds.x - 20;
            style.top = bounds.y - 20;
            style.width = bounds.width + 40;
            style.height = bounds.height + 40;
        }
    }
}
