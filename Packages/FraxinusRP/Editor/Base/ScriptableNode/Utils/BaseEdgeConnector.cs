using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor.Experimental.GraphView;

namespace FraxinusEditor.Base
{
    public class BaseEdgeConnector : EdgeConnector
    {
        protected BaseEdgeDragHelper _dragHelper;
        Edge _edgeCandidate;
        protected bool _active;
        Vector2 _mouseDownPosition;
        protected BaseGraphView _graphView;

        internal const float k_connectionDistanceTreshold = 10f;

        public BaseEdgeConnector(IEdgeConnectorListener listener) : base()
        {
            _graphView = (listener as BaseEdgeConnectorListener)?.graphView;
            _active = false;
            InitEdgeConnector(listener);
        }

        protected virtual void InitEdgeConnector(IEdgeConnectorListener listener)
        {
            _dragHelper = new BaseEdgeDragHelper(listener);
            activators.Add(new ManipulatorActivationFilter { button = MouseButton.LeftMouse });
        }

        public override EdgeDragHelper edgeDragHelper => _dragHelper;

        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<MouseDownEvent>(OnMouseDown);
            target.RegisterCallback<MouseMoveEvent>(OnMouseMove);
            target.RegisterCallback<MouseUpEvent>(OnMouseUp);
            target.RegisterCallback<KeyDownEvent>(OnKeyDown);
            target.RegisterCallback<MouseCaptureOutEvent>(OnCaptureOut);
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<MouseDownEvent>(OnMouseDown);
            target.UnregisterCallback<MouseMoveEvent>(OnMouseMove);
            target.UnregisterCallback<MouseUpEvent>(OnMouseUp);
            target.UnregisterCallback<KeyDownEvent>(OnKeyDown);
            target.UnregisterCallback<MouseCaptureOutEvent>(OnCaptureOut);
        }

        protected virtual void OnMouseDown(MouseDownEvent e)
        {
            if (_active)
            {
                e.StopImmediatePropagation();
                return;
            }

            if (!CanStartManipulation(e))
                return;

            var graphElement = target as Port;
            if (graphElement == null)
                return;

            _mouseDownPosition = e.localMousePosition;

            _edgeCandidate = _graphView != null ? _graphView.CreateEdgeView() : new EdgeView();
            edgeDragHelper.draggedPort = graphElement;
            edgeDragHelper.edgeCandidate = _edgeCandidate;

            if (edgeDragHelper.HandleMouseDown(e))
            {
                _active = true;
                target.CaptureMouse();

                e.StopPropagation();
            }
            else
            {
                edgeDragHelper.Reset();
                _edgeCandidate = null;
            }
        }

        void OnCaptureOut(MouseCaptureOutEvent e)
        {
            _active = false;
            if (_edgeCandidate != null)
                Abort();
        }

        protected virtual void OnMouseMove(MouseMoveEvent e)
        {
            if (!_active) return;

            edgeDragHelper.HandleMouseMove(e);
            _edgeCandidate.candidatePosition = e.mousePosition;
            _edgeCandidate.UpdateEdgeControl();
            e.StopPropagation();
        }

        protected virtual void OnMouseUp(MouseUpEvent e)
        {
            if (!_active || !CanStopManipulation(e))
                return;

            if (CanPerformConnection(e.localMousePosition))
                edgeDragHelper.HandleMouseUp(e);
            else
                Abort();

            _active = false;
            _edgeCandidate = null;
            target.ReleaseMouse();
            e.StopPropagation();
        }

        private void OnKeyDown(KeyDownEvent e)
        {
            if (e.keyCode != KeyCode.Escape || !_active)
                return;

            Abort();

            _active = false;
            target.ReleaseMouse();
            e.StopPropagation();
        }

        void Abort()
        {
            var graphView = target?.GetFirstAncestorOfType<GraphView>();
            graphView?.RemoveElement(_edgeCandidate);

            _edgeCandidate.input = null;
            _edgeCandidate.output = null;
            _edgeCandidate = null;

            edgeDragHelper.Reset();
        }

        bool CanPerformConnection(Vector2 mousePosition)
        {
            return Vector2.Distance(_mouseDownPosition, mousePosition) > k_connectionDistanceTreshold;
        }
    }
}