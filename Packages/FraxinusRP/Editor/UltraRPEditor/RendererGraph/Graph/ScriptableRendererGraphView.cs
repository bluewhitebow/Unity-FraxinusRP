using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Base;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using UnityEditor.Experimental.GraphView;
using FraxinusEditor.Base;

using Group = FraxinusEngine.Base.Group;

namespace FraxinusEditor.Rendering.Ultra
{
    public class ScriptableRendererGraphView : BaseGraphView
    {
        public new ScriptableRendererGraph graph => base.graph as ScriptableRendererGraph;

        public RenderPassNodeInspectorObject renderPassNodeInspector => nodeInspector as RenderPassNodeInspectorObject;

        private PopupField<string> _cameraSelector;
        private List<string> _cameraOptions = new List<string>();
        private Dictionary<string, int> _cameraNameToInstanceID = new Dictionary<string, int>();
        private Label _graphSummary;
        private Button _zoomButton;
        private VisualElement _previewBar;
        private VisualElement _statusBar;
        private RendererGraphInjectPassView _injectPassView;

        public ScriptableRendererGraphView(EditorWindow window) : base(window)
        {
            AddToClassList("fraxinus-renderer-graph");
            var workspaceStyle = Resources.Load<StyleSheet>("UIElementRes/RendererGraphWorkspace");
            if (workspaceStyle != null)
                styleSheets.Add(workspaceStyle);

            var grid = new GridBackground { name = "renderer-grid", pickingMode = PickingMode.Ignore };
            Insert(0, grid);
            grid.StretchToParentSize();

            initialized += Initialize;
            Undo.undoRedoPerformed += ReloadGraph;
            SetupZoom(0.05f, 32f);
            viewTransformChanged += _ => UpdateGraphSummary();
            RegisterCallback<GeometryChangedEvent>(evt =>
            {
                EnableInClassList("fraxinus-compact", evt.newRect.width < 760);
                UpdateInjectPanelHeight(evt.newRect.height);
            });

            RegisterCallback<DetachFromPanelEvent>(e => Disable());
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            var compatiblePorts = new List<Port>();
            PortView startPortView = startPort as PortView;

            compatiblePorts.AddRange(ports.ToList().Where(p =>
            {
                var portView = p as PortView;

                if (p.direction == startPort.direction)
                    return false;

                if (p.node == startPort.node)
                    return false;

                // Check if there is custom adapters for this assignation
                if (CustomPortIO.IsAssignable(startPort.portType, p.portType))
                    return true;

                // Allow connection between RenderTexture and all texture types:
                Type startType = startPortView.portData.displayType ?? startPortView.portType;
                Type endType = portView.portData.displayType ?? portView.portType;
                if (startType == typeof(RenderTexture))
                {
                    if (endType.IsSubclassOf(typeof(Texture)))
                        return true;
                }

                if (endType == typeof(RenderTexture))
                {
                    if (startType.IsSubclassOf(typeof(Texture)))
                        return true;
                }

                // Check for type assignability
                if (!BaseGraph.TypesAreConnectable(startPort.portType, p.portType))
                    return false;

                // Check if the edge already exists
                if (portView.GetEdges().Any(e => e.input == startPort || e.output == startPort))
                    return false;

                return true;
            }));

            return compatiblePorts;
        }

        protected override NodeInspectorObject CreateNodeInspectorObject()
        {
            var inspector = ScriptableObject.CreateInstance<RenderPassNodeInspectorObject>();
            inspector.name = "";
            inspector.hideFlags = HideFlags.HideAndDontSave ^ HideFlags.NotEditable;

            return inspector;
        }

        void Initialize()
        {
            UnregisterCallback<KeyDownEvent>(KeyCallback);
            RegisterCallback<KeyDownEvent>(KeyCallback);
            CreateCameraPreviewSelector();
            CreateStatusBar();
            _injectPassView?.RemoveFromHierarchy();
            _injectPassView = new RendererGraphInjectPassView(this);
            Add(_injectPassView);
            UpdateInjectPanelHeight(layout.height);
            UpdateGraphSummary();
        }

        void UpdateInjectPanelHeight(float height)
        {
            if (_injectPassView != null && !float.IsNaN(height))
                _injectPassView.style.maxHeight = Mathf.Clamp(height - 140, 64, 400);
        }

        void CreateCameraPreviewSelector()
        {
            _previewBar?.RemoveFromHierarchy();
            _cameraOptions.Clear();
            _cameraNameToInstanceID.Clear();
            _cameraOptions.Add("All Cameras");
            _cameraNameToInstanceID["All Cameras"] = 0;
            UpdateCameraList();
            
            string currentValue = GetCameraDisplayName(graph.previewCameraInstanceID);
            _cameraSelector = new PopupField<string>("Preview Camera", _cameraOptions, currentValue);
            _cameraSelector.RegisterValueChangedCallback(evt => OnCameraSelectionChanged(evt.newValue));
            _cameraSelector.AddToClassList("fraxinus-camera-selector");
            _cameraSelector.tooltip = "Choose which camera supplies render-pass previews and execution status.";

            _previewBar = new VisualElement { name = "renderer-preview-bar" };
            _previewBar.AddToClassList("fraxinus-preview-bar");
            var graphName = new Label(graph.name) { tooltip = AssetDatabase.GetAssetPath(graph) };
            graphName.AddToClassList("fraxinus-graph-name");
            _previewBar.Add(graphName);
            var spacer = new VisualElement();
            spacer.AddToClassList("fraxinus-spacer");
            _previewBar.Add(spacer);
            _previewBar.Add(_cameraSelector);
            StopCanvasEvents(_previewBar);
            Add(_previewBar);

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
        }

        void CreateStatusBar()
        {
            _statusBar?.RemoveFromHierarchy();
            _statusBar = new VisualElement { name = "renderer-status-bar" };
            _statusBar.AddToClassList("fraxinus-status-bar");
            _graphSummary = new Label();
            _graphSummary.AddToClassList("fraxinus-graph-summary");
            _statusBar.Add(_graphSummary);

            var hint = new Label("Scroll to zoom  /  Middle-drag to pan  /  Right-click for actions");
            hint.AddToClassList("fraxinus-canvas-hint");
            _statusBar.Add(hint);
            var spacer = new VisualElement();
            spacer.AddToClassList("fraxinus-spacer");
            _statusBar.Add(spacer);

            AddNavigationButton("\u2212", "Zoom out", () => SetZoom(viewTransform.scale.x / 1.2f));
            _zoomButton = AddNavigationButton("100%", "Reset to actual size", () => SetZoom(1f));
            _zoomButton.AddToClassList("fraxinus-zoom-value");
            AddNavigationButton("+", "Zoom in", () => SetZoom(viewTransform.scale.x * 1.2f));
            AddNavigationButton("Fit", "Frame all nodes", FrameGraph);
            StopCanvasEvents(_statusBar);
            Add(_statusBar);
        }

        Button AddNavigationButton(string text, string tooltip, Action action)
        {
            var button = new Button(action) { text = text, tooltip = tooltip };
            button.AddToClassList("fraxinus-navigation-button");
            _statusBar.Add(button);
            return button;
        }

        static void StopCanvasEvents(VisualElement element)
        {
            element.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
            element.RegisterCallback<WheelEvent>(evt => evt.StopPropagation());
        }

        void SetZoom(float scale)
        {
            scale = Mathf.Clamp(scale, minScale, maxScale);
            var center = new Vector3(layout.width * 0.5f, layout.height * 0.5f, 0);
            var ratio = scale / Mathf.Max(viewTransform.scale.x, 0.001f);
            UpdateViewTransform(center + (viewTransform.position - center) * ratio, new Vector3(scale, scale, 1));
        }

        public void FrameGraph()
        {
            var elements = graphElements.Where(element => element is Node
                || element is UnityEditor.Experimental.GraphView.Group
                || element is UnityEditor.Experimental.GraphView.StickyNote).ToList();
            if (elements.Count == 0)
                return;

            var bounds = elements[0].GetPosition();
            foreach (var element in elements.Skip(1))
            {
                var rect = element.GetPosition();
                bounds = Rect.MinMaxRect(Mathf.Min(bounds.xMin, rect.xMin), Mathf.Min(bounds.yMin, rect.yMin),
                    Mathf.Max(bounds.xMax, rect.xMax), Mathf.Max(bounds.yMax, rect.yMax));
            }

            // Keep nodes clear of the controls, including the expanded injection list.
            float left = _injectPassView != null && _injectPassView.expanded && layout.width > 600 ? 324 : 28;
            var viewport = new Rect(left, 104, Mathf.Max(1, layout.width - left - 28), Mathf.Max(1, layout.height - 164));
            float scale = Mathf.Clamp(Mathf.Min(viewport.width / Mathf.Max(1, bounds.width),
                viewport.height / Mathf.Max(1, bounds.height)), minScale, 1f);
            Vector2 position = viewport.center - bounds.center * scale;
            UpdateViewTransform(new Vector3(position.x, position.y, 0), new Vector3(scale, scale, 1));
        }

        void UpdateGraphSummary()
        {
            if (graph == null)
                return;
            if (_graphSummary != null)
                _graphSummary.text = $"{nodeViews.Count} nodes   /   {edgeViews.Count} connections";
            if (_zoomButton != null)
                _zoomButton.text = $"{Mathf.RoundToInt(viewTransform.scale.x * 100)}%";
        }

        private void UpdateCameraList()
        {
            var cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            
            foreach (var cam in cameras)
            {
                if (cam != null && cam.gameObject.scene.IsValid())
                {
                    int instanceID = cam.GetInstanceID();
                    string displayName = GetUniqueCameraName(cam);
                    
                    if (!_cameraNameToInstanceID.ContainsValue(instanceID))
                    {
                        _cameraNameToInstanceID[displayName] = instanceID;
                        _cameraOptions.Add(displayName);
                    }
                }
            }
        }

        private string GetUniqueCameraName(Camera cam)
        {
            string baseName = cam.name;
            int instanceID = cam.GetInstanceID();
            
            int duplicateCount = 0;
            foreach (var kvp in _cameraNameToInstanceID)
            {
                if (kvp.Key.StartsWith(baseName) && kvp.Value != instanceID)
                    duplicateCount++;
            }
            
            if (duplicateCount > 0)
                return $"{baseName} (ID:{instanceID})";
            
            return baseName;
        }

        private string GetCameraDisplayName(int instanceID)
        {
            if (instanceID == 0)
                return "All Cameras";
            
            foreach (var kvp in _cameraNameToInstanceID)
            {
                if (kvp.Value == instanceID)
                    return kvp.Key;
            }
            
            return "All Cameras";
        }

        private void OnCameraSelectionChanged(string selectedCamera)
        {
            if (_cameraNameToInstanceID.TryGetValue(selectedCamera, out int instanceID))
            {
                graph.previewCameraInstanceID = instanceID;
            }
        }

        private float _lastCameraUpdateTime;
        private void OnEditorUpdate()
        {
            if (graph == null)
                return;
            if (EditorApplication.timeSinceStartup - _lastCameraUpdateTime > 2.0f)
            {
                _lastCameraUpdateTime = (float)EditorApplication.timeSinceStartup;
                RefreshCameraList();
                UpdateGraphSummary();
            }
        }

        private void RefreshCameraList()
        {
            var oldOptions = new List<string>(_cameraOptions);
            var oldMapping = new Dictionary<string, int>(_cameraNameToInstanceID);
            
            _cameraOptions.Clear();
            _cameraNameToInstanceID.Clear();
            
            _cameraOptions.Add("All Cameras");
            _cameraNameToInstanceID["All Cameras"] = 0;
            
            UpdateCameraList();
            
            if (!ListsEqual(oldOptions, _cameraOptions))
            {
                string currentValue = GetCameraDisplayName(graph.previewCameraInstanceID);
                if (!_cameraOptions.Contains(currentValue))
                {
                    currentValue = "All Cameras";
                    graph.previewCameraInstanceID = 0;
                }
                
                _cameraSelector.choices = _cameraOptions;
                _cameraSelector.SetValueWithoutNotify(currentValue);
            }
        }

        private bool ListsEqual(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        void KeyCallback(KeyDownEvent k)
        {
            switch (k.keyCode)
            {
                case KeyCode.P:

                    break;
            }
        }

        public void RefreshNodeSettings()
        {
            foreach (var nodeView in nodeViews)
            {
                if (nodeView is ScriptableRenderPassNodeView v)
                    v.RefreshSettingsValues();
            }
        }

        void Disable()
        {
            Undo.undoRedoPerformed -= ReloadGraph;
            EditorApplication.update -= OnEditorUpdate;
        }

        void ReloadGraph()
        {
            if (graph == null)
                return;
            graph.drivenNode = null;
            graph.postNode = null;
            _injectPassView?.RefreshStates();
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        void OnNodeDuplicated(BaseNode sourceNode, BaseNode newNode)
        {

        }
    }
}
