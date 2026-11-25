using FraxinusEditor.Base;
using FraxinusEngine.Base;
using FraxinusEngine.Rendering.Ultra;
using System.Linq;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace FraxinusEditor.Rendering.Ultra
{
    [NodeCustomEditor(typeof(ContextBeginNode))]
    public class ContextBeginNodeView : ScriptableRenderPassNodeView
    {
        protected override float _minNodeWidth => 320f;
        protected override bool _showActiveToggle => false;

        private ContextScopeHighlight _highlight;

        public override void Enable(bool fromInspector)
        {
            base.Enable(fromInspector);

            if (!fromInspector)
            {
                schedule.Execute(() => CreateHighlight()).ExecuteLater(100);
                RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);

                schedule.Execute(() => UpdateHighlight()).Every(100);

                var graphView = owner as ScriptableRendererGraphView;
                if (graphView != null)
                    graphView.graphViewChanged += OnGraphViewChanged;
            }
        }

        public override void Disable()
        {
            var graphView = owner as ScriptableRendererGraphView;
            if (graphView != null)
            {
                graphView.graphViewChanged -= OnGraphViewChanged;
            }

            if (_highlight != null)
            {
                _highlight.RemoveFromHierarchy();
                _highlight = null;
            }

            base.Disable();
        }

        private GraphViewChange OnGraphViewChanged(GraphViewChange changes)
        {
            if (changes.edgesToCreate != null || changes.elementsToRemove != null)
                schedule.Execute(() => UpdateHighlight()).ExecuteLater(50);
            return changes;
        }

        private void CreateHighlight()
        {
            if (_highlight != null)
                return;

            var graphView = owner as ScriptableRendererGraphView;
            if (graphView == null)
                return;

            _highlight = new ContextScopeHighlight();

            var contentViewContainer = graphView.contentViewContainer;
            contentViewContainer.Insert(0, _highlight);

            UpdateHighlight();
        }

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            schedule.Execute(() => UpdateHighlight()).ExecuteLater(50);
        }

        private void UpdateHighlight()
        {
            if (_highlight == null)
                return;

            var endNode = FindConnectedContextEndNode();
            if (endNode == null)
            {
                _highlight.style.display = DisplayStyle.None;
                return;
            }

            var graphView = owner as ScriptableRendererGraphView;
            var endNodeView = graphView.nodeViewsPerNode.Values
                .FirstOrDefault(n => n.nodeTarget == endNode) as ContextEndNodeView;

            if (endNodeView == null)
            {
                _highlight.style.display = DisplayStyle.None;
                return;
            }

            var nodesInScope = CollectNodesInScope(endNode);
            var bounds = CalculateScopeBounds(nodesInScope, graphView);
            _highlight.UpdateBounds(bounds);
            _highlight.style.display = DisplayStyle.Flex;
        }

        private System.Collections.Generic.List<BaseNode> CollectNodesInScope(ContextEndNode endNode)
        {
            var nodes = new System.Collections.Generic.List<BaseNode>();
            var visited = new System.Collections.Generic.HashSet<string>();

            CollectNodesRecursive(nodeTarget, endNode, visited, nodes);

            return nodes;
        }

        private bool CollectNodesRecursive(BaseNode currentNode, ContextEndNode targetEndNode,
            System.Collections.Generic.HashSet<string> visited, System.Collections.Generic.List<BaseNode> nodes)
        {
            if (currentNode == null || visited.Contains(currentNode.guid))
                return false;

            visited.Add(currentNode.guid);

            if (currentNode == targetEndNode)
            {
                nodes.Add(currentNode);
                return true;
            }

            foreach (var port in currentNode.outputPorts)
            {
                foreach (var edge in port.GetEdges())
                {
                    var targetNode = edge.inputNode;
                    if (CollectNodesRecursive(targetNode, targetEndNode, visited, nodes))
                    {
                        nodes.Add(currentNode);
                        return true;
                    }
                }
            }

            return false;
        }

        private Rect CalculateScopeBounds(System.Collections.Generic.List<BaseNode> nodes, ScriptableRendererGraphView graphView)
        {
            if (nodes.Count == 0)
                return Rect.zero;

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            foreach (var node in nodes)
            {
                if (graphView.nodeViewsPerNode.TryGetValue(node, out var nodeView))
                {
                    var bounds = nodeView.GetPosition();
                    minX = Mathf.Min(minX, bounds.x);
                    minY = Mathf.Min(minY, bounds.y);
                    maxX = Mathf.Max(maxX, bounds.xMax);
                    maxY = Mathf.Max(maxY, bounds.yMax);
                }
            }

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private ContextEndNode FindConnectedContextEndNode()
        {
            var visited = new System.Collections.Generic.HashSet<string>();
            return FindEndNodeRecursive(nodeTarget, visited);
        }

        private ContextEndNode FindEndNodeRecursive(BaseNode currentNode, System.Collections.Generic.HashSet<string> visited)
        {
            if (currentNode == null || visited.Contains(currentNode.guid))
                return null;

            visited.Add(currentNode.guid);

            if (currentNode is ContextEndNode endNode)
                return endNode;

            foreach (var port in currentNode.outputPorts)
            {
                foreach (var edge in port.GetEdges())
                {
                    var targetNode = edge.inputNode;
                    var result = FindEndNodeRecursive(targetNode, visited);
                    if (result != null)
                        return result;
                }
            }

            return null;
        }
    }
}
