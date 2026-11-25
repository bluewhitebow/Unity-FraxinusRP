using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Base;
using UnityEditor.Experimental.GraphView;

namespace FraxinusEditor.Base
{
    /// <summary>
    /// Stack node view implementation, can be used to stack multiple node inside a context like VFX graph does.
    /// </summary>
    public class BaseStackNodeView : StackNode
    {
        public delegate void ReorderNodeAction(BaseNodeView nodeView, int oldIndex, int newIndex);
    
        /// <summary>
        /// StackNode data from the graph
        /// </summary>
        protected internal BaseStackNode    _stackNode;
        protected BaseGraphView             _owner;
        readonly string                     _styleSheet = "GraphProcessorStyles/BaseStackNodeView";

        /// <summary>Triggered when a node is re-ordered in the stack.</summary>
        public event ReorderNodeAction      _onNodeReordered;

        public BaseStackNodeView(BaseStackNode stackNode)
        {
            this._stackNode = stackNode;
            styleSheets.Add(Resources.Load<StyleSheet>(_styleSheet));
        }

        /// <inheritdoc />
        protected override void OnSeparatorContextualMenuEvent(ContextualMenuPopulateEvent evt, int separatorIndex)
        {
            // TODO: write the context menu for stack node
        }

        /// <summary>
        /// Called after the StackNode have been added to the graph view
        /// </summary>
        public virtual void Initialize(BaseGraphView graphView)
        {
            _owner = graphView;
            headerContainer.Add(new Label(_stackNode.title));

            SetPosition(new Rect(_stackNode.position, Vector2.one));

            InitializeInnerNodes();
        }

        void InitializeInnerNodes()
        {
            int i = 0;
            // Sanitize the GUID list in case some nodes were removed
            _stackNode.nodeGUIDs.RemoveAll(nodeGUID =>
            {
                if (_owner.graph.nodesPerGUID.ContainsKey(nodeGUID))
                {
                    var node = _owner.graph.nodesPerGUID[nodeGUID];
                    var view = _owner.nodeViewsPerNode[node];
                    view.AddToClassList("stack-child__" + i);
                    i++;
                    AddElement(view);
                    return false;
                }
                else
                    return true; // remove the entry as the GUID doesn't exist anymore
            });
        }

        /// <inheritdoc />
        public override void SetPosition(Rect newPos)
        {
            base.SetPosition(newPos);

            _stackNode.position = newPos.position;
        }

        /// <inheritdoc />
        protected override bool AcceptsElement(GraphElement element, ref int proposedIndex, int maxIndex)
        {
            bool accept = base.AcceptsElement(element, ref proposedIndex, maxIndex);

            if (accept && element is BaseNodeView nodeView)
            {
                var index = Mathf.Clamp(proposedIndex, 0, _stackNode.nodeGUIDs.Count - 1);

                int oldIndex = _stackNode.nodeGUIDs.FindIndex(g => g == nodeView.nodeTarget.guid);
                if (oldIndex != -1)
                {
                    _stackNode.nodeGUIDs.Remove(nodeView.nodeTarget.guid);
                    if (oldIndex != index)
                        _onNodeReordered?.Invoke(nodeView, oldIndex, index);
                }

                _stackNode.nodeGUIDs.Insert(index, nodeView.nodeTarget.guid);
            }

            return accept;
        }

        public override bool DragLeave(DragLeaveEvent evt, IEnumerable<ISelectable> selection, IDropTarget leftTarget, ISelection dragSource)
        {
            foreach (var elem in selection)
            {
                if (elem is BaseNodeView nodeView)
                    _stackNode.nodeGUIDs.Remove(nodeView.nodeTarget.guid);
            }
            return base.DragLeave(evt, selection, leftTarget, dragSource);
        }
    }
}