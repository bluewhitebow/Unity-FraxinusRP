using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.Experimental.GraphView;

namespace FraxinusEditor.Base
{
    public class BaseEdgeDragHelper : EdgeDragHelper
    {
        internal const int panAreaWidth = 30;
        internal const int panSpeed = 4;
        internal const int pnInterval = 10;
        internal const float minSpeedFactor = 0.5f;
        internal const float maxSpeedFactor = 7f;
        internal const float maxPanSpeed = maxSpeedFactor * panSpeed;
        internal const float portDetectionWidth = 30;

        protected Dictionary<BaseNodeView, List<PortView>> _compatiblePorts = new Dictionary<BaseNodeView, List<PortView>>();
        private Edge _ghostEdge;
        protected GraphView _graphView;
        protected static NodeAdapter s_nodeAdapter = new NodeAdapter();
        protected readonly IEdgeConnectorListener _listener;

        private IVisualElementScheduledItem _panSchedule;
        private Vector3 _panDiff = Vector3.zero;
        private bool _wasPanned;

        public bool resetPositionOnPan { get; set; }

        public BaseEdgeDragHelper(IEdgeConnectorListener listener)
        {
            this._listener = listener;
            resetPositionOnPan = true;
            Reset();
        }

        public override Edge edgeCandidate { get; set; }

        public override Port draggedPort { get; set; }

        public override void Reset(bool didConnect = false)
        {
            if (_compatiblePorts != null && _graphView != null)
            {
                // Reset the highlights.
                _graphView.ports.ForEach((p) => {
                    p.OnStopEdgeDragging();
                });
                _compatiblePorts.Clear();
            }

            // Clean up ghost edge.
            if ((_ghostEdge != null) && (_graphView != null))
            {
                var pv = _ghostEdge.input as PortView;
                _graphView.schedule.Execute(() => {
                    if (pv != null) pv.portCapLit = false;
                    // pv.UpdatePortView(pv.portData);
                }).ExecuteLater(10);
                _graphView.RemoveElement(_ghostEdge);
            }

            if (_wasPanned)
            {
                if (!resetPositionOnPan || didConnect)
                {
                    Vector3 p = _graphView.contentViewContainer.transform.position;
                    Vector3 s = _graphView.contentViewContainer.transform.scale;
                    _graphView.UpdateViewTransform(p, s);
                }
            }

            if (_panSchedule != null)
                _panSchedule.Pause();

            if (_ghostEdge != null)
            {
                _ghostEdge.input = null;
                _ghostEdge.output = null;
            }

            if (draggedPort != null && !didConnect)
            {
                draggedPort.portCapLit = false;
                draggedPort = null;
            }

            if (edgeCandidate != null)
            {
                edgeCandidate.SetEnabled(true);
            }

            _ghostEdge = null;
            edgeCandidate = null;

            _graphView = null;
        }

        public override bool HandleMouseDown(MouseDownEvent evt)
        {
            Vector2 mousePosition = evt.mousePosition;

            if ((draggedPort == null) || (edgeCandidate == null))
            {
                return false;
            }

            _graphView = draggedPort.GetFirstAncestorOfType<GraphView>();

            if (_graphView == null)
            {
                return false;
            }

            if (edgeCandidate.parent == null)
            {
                _graphView.AddElement(edgeCandidate);
            }

            bool startFromOutput = (draggedPort.direction == Direction.Output);

            edgeCandidate.candidatePosition = mousePosition;
            edgeCandidate.SetEnabled(false);

            if (startFromOutput)
            {
                edgeCandidate.output = draggedPort;
                edgeCandidate.input = null;
            }
            else
            {
                edgeCandidate.output = null;
                edgeCandidate.input = draggedPort;
            }

            draggedPort.portCapLit = true;

            _compatiblePorts.Clear();

            foreach (PortView port in _graphView.GetCompatiblePorts(draggedPort, s_nodeAdapter))
            {
                _compatiblePorts.TryGetValue(port.owner, out var portList);
                if (portList == null)
                    portList = _compatiblePorts[port.owner] = new List<PortView>();
                portList.Add(port);
            }

            // Sort ports by position in the node
            foreach (var kp in _compatiblePorts)
                kp.Value.Sort((e1, e2) => e1.worldBound.y.CompareTo(e2.worldBound.y));

            // Only light compatible anchors when dragging an edge.
            _graphView.ports.ForEach((p) => {
                p.OnStartEdgeDragging();
            });

            foreach (var kp in _compatiblePorts)
                foreach (var port in kp.Value)
                    port.highlight = true;

            edgeCandidate.UpdateEdgeControl();

            if (_panSchedule == null)
            {
                _panSchedule = _graphView.schedule.Execute(Pan).Every(pnInterval).StartingIn(pnInterval);
                _panSchedule.Pause();
            }
            _wasPanned = false;

            edgeCandidate.layer = Int32.MaxValue;

            return true;
        }

        internal Vector2 GetEffectivePanSpeed(Vector2 mousePos)
        {
            Vector2 effectiveSpeed = Vector2.zero;

            if (mousePos.x <= panAreaWidth)
                effectiveSpeed.x = -(((panAreaWidth - mousePos.x) / panAreaWidth) + 0.5f) * panSpeed;
            else if (mousePos.x >= _graphView.contentContainer.layout.width - panAreaWidth)
                effectiveSpeed.x = (((mousePos.x - (_graphView.contentContainer.layout.width - panAreaWidth)) / panAreaWidth) + 0.5f) * panSpeed;

            if (mousePos.y <= panAreaWidth)
                effectiveSpeed.y = -(((panAreaWidth - mousePos.y) / panAreaWidth) + 0.5f) * panSpeed;
            else if (mousePos.y >= _graphView.contentContainer.layout.height - panAreaWidth)
                effectiveSpeed.y = (((mousePos.y - (_graphView.contentContainer.layout.height - panAreaWidth)) / panAreaWidth) + 0.5f) * panSpeed;

            effectiveSpeed = Vector2.ClampMagnitude(effectiveSpeed, maxPanSpeed);

            return effectiveSpeed;
        }

        Vector2 _lastMousePos;
        public override void HandleMouseMove(MouseMoveEvent evt)
        {
            var ve = (VisualElement)evt.target;
            Vector2 gvMousePos = ve.ChangeCoordinatesTo(_graphView.contentContainer, evt.localMousePosition);
            _panDiff = GetEffectivePanSpeed(gvMousePos);

            if (_panDiff != Vector3.zero)
                _panSchedule.Resume();
            else
                _panSchedule.Pause();

            Vector2 mousePosition = evt.mousePosition;
            _lastMousePos =  evt.mousePosition;

            edgeCandidate.candidatePosition = mousePosition;

            // Draw ghost edge if possible port exists.
            Port endPort = GetEndPort(mousePosition);

            if (endPort != null)
            {
                if (_ghostEdge == null)
                {
                    _ghostEdge = CreateEdgeView();
                    _ghostEdge.isGhostEdge = true;
                    _ghostEdge.pickingMode = PickingMode.Ignore;
                    _graphView.AddElement(_ghostEdge);
                }

                if (edgeCandidate.output == null)
                {
                    _ghostEdge.input = edgeCandidate.input;
                    if (_ghostEdge.output != null)
                        _ghostEdge.output.portCapLit = false;
                    _ghostEdge.output = endPort;
                    _ghostEdge.output.portCapLit = true;
                }
                else
                {
                    if (_ghostEdge.input != null)
                        _ghostEdge.input.portCapLit = false;
                    _ghostEdge.input = endPort;
                    _ghostEdge.input.portCapLit = true;
                    _ghostEdge.output = edgeCandidate.output;
                }
            }
            else if (_ghostEdge != null)
            {
                if (edgeCandidate.input == null)
                {
                    if (_ghostEdge.input != null)
                        _ghostEdge.input.portCapLit = false;
                }
                else
                {
                    if (_ghostEdge.output != null)
                        _ghostEdge.output.portCapLit = false;
                }
                _graphView.RemoveElement(_ghostEdge);
                _ghostEdge.input = null;
                _ghostEdge.output = null;
                _ghostEdge = null;
            }
        }

        protected virtual EdgeView CreateEdgeView()
        {
            return new EdgeView();
        }

        private void Pan(TimerState ts)
        {
            _graphView.viewTransform.position -= _panDiff;

            // Workaround to force edge to update when we pan the graph
            edgeCandidate.output = edgeCandidate.output;
            edgeCandidate.input = edgeCandidate.input;

            edgeCandidate.UpdateEdgeControl();
            _wasPanned = true;
        }

        public override void HandleMouseUp(MouseUpEvent evt)
        {
            bool didConnect = false;

            Vector2 mousePosition = evt.mousePosition;

            // Reset the highlights.
            _graphView.ports.ForEach((p) => {
                p.OnStopEdgeDragging();
            });

            // Clean up ghost edges.
            if (_ghostEdge != null)
            {
                if (_ghostEdge.input != null)
                    _ghostEdge.input.portCapLit = false;
                if (_ghostEdge.output != null)
                    _ghostEdge.output.portCapLit = false;

                _graphView.RemoveElement(_ghostEdge);
                _ghostEdge.input = null;
                _ghostEdge.output = null;
                _ghostEdge = null;
            }

            Port endPort = GetEndPort(mousePosition);

            if (endPort == null && _listener != null)
            {
                _listener.OnDropOutsidePort(edgeCandidate, mousePosition);
            }

            edgeCandidate.SetEnabled(true);

            if (edgeCandidate.input != null)
                edgeCandidate.input.portCapLit = false;

            if (edgeCandidate.output != null)
                edgeCandidate.output.portCapLit = false;

            // The listener owns reconnect/disconnect and its undo snapshot.
            // Deleting a connected edge here loses the original endpoint before OnDrop.
            if (!(edgeCandidate is EdgeView connected) || !connected.isConnected)
                _graphView.RemoveElement(edgeCandidate);

            if (endPort != null)
            {
                if (endPort.direction == Direction.Output)
                    edgeCandidate.output = endPort;
                else
                    edgeCandidate.input = endPort;

                _listener.OnDrop(_graphView, edgeCandidate);
                didConnect = edgeCandidate is EdgeView result && result.isConnected;
            }
            else
            {
                edgeCandidate.output = null;
                edgeCandidate.input = null;
            }

            edgeCandidate.ResetLayer();

            edgeCandidate = null;
            _compatiblePorts.Clear();
            Reset(didConnect);
        }

        Rect GetPortBounds(BaseNodeView nodeView, int index, List<PortView> portList)
        {
            var port = portList[index];
            var bounds = port.worldBound;

            if (port.orientation == Orientation.Horizontal)
            {
                // Increase horizontal port bounds
                bounds.xMin = nodeView.worldBound.xMin;
                bounds.xMax = nodeView.worldBound.xMax;

                if (index == 0)
                    bounds.yMin = nodeView.worldBound.yMin;
                if (index == portList.Count - 1)
                    bounds.yMax = nodeView.worldBound.yMax;
                
                if (index > 0)
                {
                    Rect above = portList[index - 1].worldBound;
                    bounds.yMin = (above.yMax + bounds.yMin) / 2.0f;
                }
                if (index < portList.Count - 1)
                {
                    Rect below = portList[index + 1].worldBound;
                    bounds.yMax = (below.yMin + bounds.yMax) / 2.0f;
                }

                if (port.direction == Direction.Input)
                    bounds.xMin -= portDetectionWidth;
                else
                    bounds.xMax += portDetectionWidth;
            }
            else
            {
                // Increase vertical port bounds
                if (port.direction == Direction.Input)
                    bounds.yMin -= portDetectionWidth;
                else
                    bounds.yMax += portDetectionWidth;
            }

            return bounds;
        }

        private Port GetEndPort(Vector2 mousePosition)
        {
            if (_graphView == null)
                return null;

            Port bestPort = null;
            float bestDistance = 1e20f;

            foreach (var kp in _compatiblePorts)
            {
                var nodeView = kp.Key;
                var portList = kp.Value;

                // We know that the port in the list is top to bottom in term of layout
                for (int i = 0; i < portList.Count; i++)
                {
                    var port = portList[i];
                    Rect bounds = GetPortBounds(nodeView, i, portList);

                    float distance = Vector2.Distance(port.worldBound.position, mousePosition);

                    // Check if mouse is over port.
                    if (bounds.Contains(mousePosition) && distance < bestDistance)
                    {
                        bestPort = port;
                        bestDistance = distance;
                    }
                }
            }

            return bestPort;
        }

#if UNITY_6000_0_OR_NEWER
        public override bool HandlePointerDown(PointerDownEvent evt)
        {
            return true;
        }

        public override void HandlePointerMove(PointerMoveEvent evt)
        {

        }

        public override void HandlePointerUp(PointerUpEvent evt)
        {

        }
#endif
    }
}
