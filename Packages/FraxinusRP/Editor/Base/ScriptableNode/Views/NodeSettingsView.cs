using UnityEngine.UIElements;
using UnityEditor.UIElements;
using UnityEngine;

namespace FraxinusEditor.Base
{
    class NodeSettingsView : VisualElement
    {
        VisualElement _contentContainer;

        public NodeSettingsView()
        {
            pickingMode = PickingMode.Ignore;
            styleSheets.Add(Resources.Load<StyleSheet>("GraphProcessorStyles/NodeSettings"));
            var uxml = Resources.Load<VisualTreeAsset>("UXML/NodeSettings");
            uxml.CloneTree(this);

            // Get the element we want to use as content container
            _contentContainer = this.Q("contentContainer");
            RegisterCallback<MouseDownEvent>(OnMouseDown);
            RegisterCallback<MouseUpEvent>(OnMouseUp);
        }

        void OnMouseUp(MouseUpEvent evt)
        {
            evt.StopPropagation();
        }

        void OnMouseDown(MouseDownEvent evt)
        {
            evt.StopPropagation();
        }

        public override VisualElement contentContainer
        {
            get { return _contentContainer; }
        }
    }
}