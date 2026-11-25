using System.Collections.Generic;
using UnityEngine;

using UnityEditor;
using UnityEditor.Experimental.GraphView;

namespace FraxinusEditor.Base
{
    /// <summary>
    /// Base class to write your own edge handling connection system
    /// </summary>
    public class BaseEdgeConnectorListener : IEdgeConnectorListener, System.IDisposable
    {
        public readonly BaseGraphView graphView;

        CreateNodeMenuWindow _edgeNodeCreateMenuWindow;

        public BaseEdgeConnectorListener(BaseGraphView graphView)
        {
            this.graphView = graphView;
        }

        public virtual void OnDropOutsidePort(Edge edge, Vector2 position)
        {
            this.graphView.RegisterCompleteObjectUndo("Disconnect edge");

            // While dragging, one visual endpoint is temporarily null. Disconnect
            // both original ports before handing the open edge to the creation menu.
            if (edge is EdgeView view && view.isConnected && view.serializedEdge != null)
            {
                var input = edge.input;
                var output = edge.output;
                var original = view.serializedEdge;
                view.input = graphView.nodeViewsPerNode[original.inputNode]
                    .GetPortViewFromFieldName(original.inputFieldName, original.inputPortIdentifier);
                view.output = graphView.nodeViewsPerNode[original.outputNode]
                    .GetPortViewFromFieldName(original.outputFieldName, original.outputPortIdentifier);
                graphView.Disconnect(view);
                view.input = input;
                view.output = output;
            }
            else
                graphView.DisconnectView(edge as EdgeView);

            // when on of the port is null, then the edge was created and dropped outside of a port
            if (edge.input == null || edge.output == null)
                ShowNodeCreationMenuFromEdge(edge as EdgeView, position);
        }

        public virtual void OnDrop(GraphView graphView, Edge edge)
        {
            if (!(edge is EdgeView view) || view.input == null || view.output == null)
                return;
            var desiredInput = view.input;
            var desiredOutput = view.output;
            var original = view.isConnected ? view.serializedEdge : null;
            Port originalInput = null, originalOutput = null;
            if (original != null)
            {
                originalInput = this.graphView.nodeViewsPerNode[original.inputNode]
                    .GetPortViewFromFieldName(original.inputFieldName, original.inputPortIdentifier);
                originalOutput = this.graphView.nodeViewsPerNode[original.outputNode]
                    .GetPortViewFromFieldName(original.outputFieldName, original.outputPortIdentifier);
                if (desiredInput == originalInput && desiredOutput == originalOutput)
                    return;
            }
            if (!this.graphView.CanConnectEdge(view))
            {
                if (original != null)
                {
                    view.input = originalInput;
                    view.output = originalOutput;
                }
                else
                    this.graphView.DisconnectView(view);
                return;
            }

            // One undo snapshot must include the old connection before it is removed.
            this.graphView.RegisterCompleteObjectUndo("Connect graph ports");
            if (original != null)
            {
                view.input = originalInput;
                view.output = originalOutput;
                this.graphView.Disconnect(view);
                view.input = desiredInput;
                view.output = desiredOutput;
            }
            if (!this.graphView.Connect(view))
                this.graphView.DisconnectView(view);
        }

        public virtual void Dispose()
        {
            if (_edgeNodeCreateMenuWindow != null)
                Object.DestroyImmediate(_edgeNodeCreateMenuWindow);
            _edgeNodeCreateMenuWindow = null;
        }

        void ShowNodeCreationMenuFromEdge(EdgeView edgeView, Vector2 position)
        {
            if (edgeView == null || EditorWindow.focusedWindow == null)
                return;
            if (_edgeNodeCreateMenuWindow == null)
                _edgeNodeCreateMenuWindow = ScriptableObject.CreateInstance<CreateNodeMenuWindow>();

            _edgeNodeCreateMenuWindow.Initialize(graphView, EditorWindow.focusedWindow, edgeView);
            SearchWindow.Open(new SearchWindowContext(position + EditorWindow.focusedWindow.position.position), _edgeNodeCreateMenuWindow);
        }
    }
}