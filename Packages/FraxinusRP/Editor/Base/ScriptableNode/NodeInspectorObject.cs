using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

using Object = UnityEngine.Object;

namespace FraxinusEditor.Base
{
    public class NodeInspectorObject : ScriptableObject
    {
        public Object previouslySelectedObject;
        public HashSet<BaseNodeView> selectedNodes { get; private set; } = new HashSet<BaseNodeView>();

        public event Action nodeSelectionUpdated;

        public virtual void UpdateSelectedNodes(HashSet<BaseNodeView> views)
        {
            selectedNodes = views;
            nodeSelectionUpdated?.Invoke();
        }

        public virtual void RefreshNodes() => nodeSelectionUpdated?.Invoke();

        public virtual void NodeViewRemoved(BaseNodeView view)
        {
            selectedNodes.Remove(view);
            nodeSelectionUpdated?.Invoke();
        }
    }

    [CustomEditor(typeof(NodeInspectorObject))]
    public class NodeInspectorObjectEditor : Editor
    {
        NodeInspectorObject _inspector;
        protected VisualElement _root;
        protected VisualElement _selectedNodeList;
        protected VisualElement _placeholder;

        protected virtual void OnEnable()
        {
            _inspector = target as NodeInspectorObject;
            _inspector.nodeSelectionUpdated += UpdateNodeInspectorList;
            _root = new VisualElement();
            _selectedNodeList = new VisualElement();
            _selectedNodeList.styleSheets.Add(Resources.Load<StyleSheet>("GraphProcessorStyles/InspectorView"));

            _root.Add(_selectedNodeList);
            _placeholder = new Label("Select a node to show it's settings in the inspector");
            _placeholder.AddToClassList("PlaceHolder");
            UpdateNodeInspectorList();
        }

        protected virtual void OnDisable()
        {
            _inspector.nodeSelectionUpdated -= UpdateNodeInspectorList;
        }

        public override VisualElement CreateInspectorGUI() => _root;

        protected virtual void UpdateNodeInspectorList()
        {
            _selectedNodeList.Clear();

            if (_inspector.selectedNodes.Count == 0)
                _selectedNodeList.Add(_placeholder);

            foreach (var nodeView in _inspector.selectedNodes)
                _selectedNodeList.Add(CreateNodeBlock(nodeView));
        }

        protected VisualElement CreateNodeBlock(BaseNodeView nodeView)
        {
            var view = new VisualElement();

            view.Add(new Label(nodeView.nodeTarget.name));

            var tmp = nodeView.controlsContainer;
            nodeView.controlsContainer = view;
            nodeView.Enable(true);
            nodeView.controlsContainer.AddToClassList("NodeControls");
            var block = nodeView.controlsContainer;
            nodeView.controlsContainer = tmp;

            return block;
        }
    }
}
