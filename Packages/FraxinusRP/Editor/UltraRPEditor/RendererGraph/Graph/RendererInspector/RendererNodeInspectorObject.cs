using FraxinusEditor.Base;
using FraxinusEngine.Base;
using FraxinusEngine.Rendering.Ultra;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace FraxinusEditor.Rendering.Ultra
{
    [Serializable]
    public class RenderPassNodeInspectorObject : NodeInspectorObject
    {
        internal enum Texture3DPreviewMode
        {
            Volumetric,
            DistanceFieldNormal,
            DistanceFieldColor,
        }

        internal enum SDFChannel
        {
            R,
            G,
            B,
            A
        }

        // Preview params:
        internal Vector2 middleClickMousePosition;
        internal Vector2 middleClickCameraPosition;
        internal Vector2 positionOffset;
        internal Vector2 positionOffsetTarget;
        internal Vector2 lastMousePosition;
        internal float zoomTarget = 1;
        internal float zoom = 1;
        internal float zoomSpeed = 20f;
        internal double timeSinceStartup, latestTime, deltaTime;

        internal float compareSlider = 0.5f;
        internal float compareSlider3D = 0.5f;
        internal Vector2 mouseUV;
        internal Matrix4x4 volumeCameraMatrix = Matrix4x4.identity;
        internal float cameraZoom;
        internal float cameraXAxis;
        internal float cameraYAxis;
        internal bool lockPreview = false;
        internal ScriptableRenderPassNode lockedPreviewTarget;
        internal Vector2 shaderPos;
        internal bool needsRepaint;

        // Preview settings
        internal PreviewChannels channels = PreviewChannels.RGBA;
        internal bool alwaysRefresh = true;
        internal float mipLevel;
        internal bool preserveAspect = true;
        internal Texture3DPreviewMode texture3DPreviewMode;
        internal float texture3DDensity = 1;
        internal float texture3DDistanceFieldOffset = 0;
        internal SDFChannel sdfChannel = SDFChannel.R;
        internal float comparisonOffset;
        internal bool showCubeBackface = true;
        internal bool invertSurface = false;
        internal int volumetricDensityChannel = 3;

        // TODO
        // internal enum PreviewMode
        // {
        //     Color,
        //     Normal,
        //     Height,
        // }

        public event Action pinnedNodeUpdate;

        public List<BaseNodeView> pinnedNodes = new List<BaseNodeView>();

        public void AddPinnedView(BaseNodeView view)
        {
            Selection.activeObject = this;
            if (!pinnedNodes.Any(b => b.nodeTarget.guid == view.nodeTarget.guid))
            {
                pinnedNodes.Add(view);
                pinnedNodeUpdate?.Invoke();
            }
        }

        public void RemovePinnedView(BaseNodeView view)
        {
            Selection.activeObject = this;
            if (pinnedNodes.Remove(view))
                pinnedNodeUpdate?.Invoke();
        }

        public override void NodeViewRemoved(BaseNodeView view)
        {
            pinnedNodes.Remove(view);
            base.NodeViewRemoved(view);
        }
    }

    [CustomEditor(typeof(RenderPassNodeInspectorObject))]
    public class RenderPassNodeInspectorObjectEditor : NodeInspectorObjectEditor
    {
        const float k_maxZoom = 512;
        const float k_minZoom = 0.05f;
        const int k_buttonWidth = 25;

        Event _e => Event.current;

        internal RenderPassNodeInspectorObject nodeInspectorObject;
        internal List<ScriptableRenderPassNode> nodeWithPreviews = new List<ScriptableRenderPassNode>();

        RenderPassNodeInspectorSettingsPopupWindow _comparisonWindow;

        Material _previewMaterial;

        VisualTreeAsset _nodeInspectorFoldout;
        
        IVisualElementScheduledItem _repaintSchedule;

        protected override void OnHeaderGUI()
        {
            base.OnHeaderGUI();
            // EditorGUILayout.LabelField(new GUIContent("Node Inspector", RendererGraphEditorUtils.pinIcon));
        }

        protected override void OnEnable()
        {
            nodeInspectorObject = target as RenderPassNodeInspectorObject;

            _nodeInspectorFoldout = Resources.Load("UIElementRes/UI Blocks/InspectorNodeFoldout") as VisualTreeAsset;

            // There is a really weird issue where the resources.Load returns null when building a player
            if (_nodeInspectorFoldout == null)
                return;

            _nodeInspectorFoldout.hideFlags = HideFlags.HideAndDontSave;

            base.OnEnable();

            nodeInspectorObject.pinnedNodeUpdate += UpdateNodeInspectorList;
            _previewMaterial = new Material(Shader.Find("Hidden/Editor/RendererGraphInspectorPreview")) 
            { 
                hideFlags = HideFlags.HideAndDontSave
            };

            // Workaround because UIElements is not able to correctly detect mouse enter / leave events :(
            _repaintSchedule = _root.schedule.Execute(() =>{ Repaint(); }).Every(16);

            _root.Insert(0, new IMGUIContainer(() => 
            {
                if (_selectedNodeList.localBound.Contains(Event.current.mousePosition))
                    _repaintSchedule.Resume();
                else
                {
                    // Unselect all nodes:
                    foreach (var view in nodeInspectorObject.selectedNodes)
                        view.RemoveFromClassList("highlight");

                    foreach (var view in nodeInspectorObject.pinnedNodes)
                        view.RemoveFromClassList("highlight");

                    _repaintSchedule.Pause();
                }
            }));
            // End workaround

            // Handle the always refresh option
            _root.Add(new IMGUIContainer(() => 
            {
                if (!Application.runInBackground && !UnityEditorInternal.InternalEditorUtility.isApplicationActive && nodeInspectorObject.alwaysRefresh)
                    return;

                if (nodeInspectorObject.alwaysRefresh)
                {
                    if (Event.current.type == EventType.Layout)
                        Repaint();
                }
            }));

            Fit();
        }

        protected override void OnDisable()
        {
            nodeInspectorObject.pinnedNodeUpdate -= UpdateNodeInspectorList;
            
            if (_repaintSchedule != null)
            {
                _repaintSchedule.Pause();
                _repaintSchedule = null;
            }
        }

        protected override void UpdateNodeInspectorList()
        {
            _selectedNodeList.Clear();
            nodeWithPreviews.Clear();

            if (nodeInspectorObject.selectedNodes.Count == 0 && nodeInspectorObject.pinnedNodes.Count == 0)
                _selectedNodeList.Add(_placeholder);

            // Selection first
            foreach (var nodeView in nodeInspectorObject.selectedNodes)
            {
                var view = CreateRenderPassNodeBlock(nodeView, true);
                view.AddToClassList("SelectedNode");
                _selectedNodeList.Add(view);

                if (nodeView.nodeTarget is ScriptableRenderPassNode n && n.nodeSettings.showPreview && n.previewTexture != null)
                    nodeWithPreviews.Add(n);
            }

            // Then pinned nodes
            foreach (var nodeView in nodeInspectorObject.pinnedNodes)
            {
                var view = CreateRenderPassNodeBlock(nodeView, false);
                view.AddToClassList("PinnedView");
                _selectedNodeList.Add(view);
                
                if (nodeView.nodeTarget is ScriptableRenderPassNode n && n.nodeSettings.showPreview && n.previewTexture != null)
                    nodeWithPreviews.Add(n);
            }
        }

        VisualElement CreateRenderPassNodeBlock(BaseNodeView nodeView, bool selection)
        {
            var nodeFoldout = _nodeInspectorFoldout.CloneTree();
            var foldout = nodeFoldout.Q("Foldout") as Foldout;
            var nodeName = nodeFoldout.Q("NodeName") as Label;
            var nodePreview = nodeFoldout.Q("NodePreview") as VisualElement;
            var foldoutContainer = nodeFoldout.Q("FoldoutContainer");
            var unpinButton = nodeFoldout.Q("UnpinButton") as VisualElement;

            unpinButton.RegisterCallback<MouseDownEvent>((e) =>
            {
                nodeView.RemoveFromClassList("highlight");
                if (selection)
                {
                    nodeInspectorObject.selectedNodes.Remove(nodeView);
                    nodeView.owner.RemoveFromSelection(nodeView);
                    UpdateNodeInspectorList();
                }
                else
                    (nodeView as ScriptableRenderPassNodeView)?.UnpinView();
            });

            unpinButton.style.unityBackgroundImageTintColor = selection ? (Color)new Color32(15, 134, 255, 255) : (Color)new Color32(245, 127, 23, 255);

            foldout.text = nodeView.nodeTarget.name;

            var tmp = nodeView.controlsContainer;
            nodeView.controlsContainer = foldoutContainer;
            nodeView.Enable(true);
            nodeView.controlsContainer.AddToClassList("NodeControls");
            var block = nodeView.controlsContainer;
            nodeView.controlsContainer = tmp;

            nodeFoldout.RegisterCallback<MouseEnterEvent>(e => 
            {
                nodeView.AddToClassList("highlight");
            });

            nodeFoldout.RegisterCallback<MouseLeaveEvent>(e => 
            {
                nodeView.RemoveFromClassList("highlight");
            });

            return nodeFoldout;
        }

        public override bool HasPreviewGUI() => nodeWithPreviews.Count > 0;

        static GUILayoutOption s_buttonLayout = GUILayout.Width(k_buttonWidth);

        // TODO: move interactive preview to another class
        public override void OnPreviewSettings()
        {
            var options = nodeWithPreviews.Select(n => n.name).ToArray();
            if (options.Length == 0)
            {
                EditorGUILayout.PrefixLabel("Nothing Selected");
                return;
            }

            if (GUILayout.Button(RendererGraphEditorUtils.fitIcon, EditorStyles.toolbarButton, s_buttonLayout))
                Fit();

            GUILayout.Space(2);
            
            if (!nodeInspectorObject.lockPreview)
                nodeInspectorObject.lockedPreviewTarget = nodeWithPreviews.FirstOrDefault();

            GUILayout.Label(nodeInspectorObject.lockedPreviewTarget.name, EditorStyles.toolbarButton);
            nodeInspectorObject.lockPreview = GUILayout.Toggle(nodeInspectorObject.lockPreview, GetLockIcon(nodeInspectorObject.lockPreview), EditorStyles.toolbarButton, s_buttonLayout);

            if (GUILayout.Button(RendererGraphEditorUtils.settingsIcon, EditorStyles.toolbarButton, s_buttonLayout))
            {
                _comparisonWindow = new RenderPassNodeInspectorSettingsPopupWindow(this);
                UnityEditor.PopupWindow.Show(new Rect(EditorGUIUtility.currentViewWidth - RenderPassNodeInspectorSettingsPopupWindow.width, -RenderPassNodeInspectorSettingsPopupWindow.height, 0, 0), _comparisonWindow);
            }
        }

        public override void OnInteractivePreviewGUI(Rect previewRect, GUIStyle background)
        {
            HandleZoomAndPan(previewRect);

            if (nodeInspectorObject.lockedPreviewTarget?.previewTexture != null && _e.type == EventType.Repaint)
            {
                nodeInspectorObject.volumeCameraMatrix = Matrix4x4.Rotate(Quaternion.Euler(nodeInspectorObject.cameraYAxis, nodeInspectorObject.cameraXAxis, 0));

                // Set texture property based on the dimension
                RendererGraphEditorUtils.SetTextureWithDimension(_previewMaterial, "_MainTex0", nodeInspectorObject.lockedPreviewTarget.previewTexture);

                _previewMaterial.SetVector("_Channels", RendererGraphEditorUtils.GetChannelMask(nodeInspectorObject.channels));

                EditorGUI.DrawPreviewTexture(previewRect, Texture2D.whiteTexture, _previewMaterial);
            }
            else
                EditorGUI.DrawRect(previewRect, new Color(1, 0, 1, 1));
        }

        Texture2D GetLockIcon(bool locked) => locked ? RendererGraphEditorUtils.lockClose : RendererGraphEditorUtils.lockOpen;

        void Fit()
        {
            // position offset is wrong?
            nodeInspectorObject.zoomTarget = 1;
            nodeInspectorObject.positionOffsetTarget = Vector2.zero;
            nodeInspectorObject.cameraXAxis = 30;
            nodeInspectorObject.cameraYAxis = 15;
            nodeInspectorObject.cameraZoom = 6;
        }

        Vector2 LocalToWorld(Vector2 pos)
        {
            pos *= nodeInspectorObject.zoom;
            pos += nodeInspectorObject.positionOffset;
            return pos;
        }

        bool IsMoveMouse(int button, EventModifiers mods) => _e.button == 2 || _e.button == 0;

        public void HandleZoomAndPan(Rect previewRect)
        {
            nodeInspectorObject.timeSinceStartup = EditorApplication.timeSinceStartup;
            nodeInspectorObject.deltaTime = nodeInspectorObject.timeSinceStartup - nodeInspectorObject.latestTime;
            nodeInspectorObject.latestTime = nodeInspectorObject.timeSinceStartup;

            nodeInspectorObject.lastMousePosition = _e.mousePosition;
            switch (_e.type)
            {
                case EventType.MouseDown:
                    if (IsMoveMouse(_e.button, _e.modifiers))
                    {
                        nodeInspectorObject.middleClickMousePosition = nodeInspectorObject.lastMousePosition;
                        nodeInspectorObject.middleClickCameraPosition = nodeInspectorObject.positionOffset;
                        nodeInspectorObject.needsRepaint = true;
                    }
                    Vector2 t = (_e.mousePosition - nodeInspectorObject.positionOffset) / nodeInspectorObject.zoom;
                    nodeInspectorObject.comparisonOffset = Mathf.Floor(t.x / (float)previewRect.width);
                    break;
                case EventType.MouseDrag:
                    if (IsMoveMouse(_e.button, _e.modifiers))
                    {
                        nodeInspectorObject.positionOffset = nodeInspectorObject.middleClickCameraPosition + (nodeInspectorObject.lastMousePosition - nodeInspectorObject.middleClickMousePosition);
                        nodeInspectorObject.positionOffsetTarget = nodeInspectorObject.positionOffset;
                        nodeInspectorObject.needsRepaint = true;

                        nodeInspectorObject.cameraXAxis += _e.delta.x / 4.0f;
                        nodeInspectorObject.cameraYAxis += _e.delta.y / 4.0f;
                        nodeInspectorObject.cameraXAxis = Mathf.Repeat(nodeInspectorObject.cameraXAxis, 360);
                        nodeInspectorObject.cameraYAxis = Mathf.Repeat(nodeInspectorObject.cameraYAxis, 360);
                    }
                    if (_e.button == 1)
                    {
                        float pos = (_e.mousePosition.x - nodeInspectorObject.positionOffsetTarget.x) / nodeInspectorObject.zoom / (float)previewRect.width;
                        nodeInspectorObject.compareSlider = Mathf.Clamp01(pos - nodeInspectorObject.comparisonOffset);
                        nodeInspectorObject.compareSlider3D = Mathf.Clamp01(_e.mousePosition.x / (float)previewRect.width);
                        nodeInspectorObject.needsRepaint = true;
                        nodeInspectorObject.mouseUV = new Vector2(_e.mousePosition.x / previewRect.width, _e.mousePosition.y / previewRect.height);
                    }
                    break;
                case EventType.ScrollWheel:
                    float delta = Mathf.Clamp(1f + Mathf.Abs((float)_e.delta.y * 0.1f), 0.1f, 2f);
                    delta = _e.delta.y > 0 ? 1f / delta : delta;
                    if (nodeInspectorObject.zoomTarget * delta > k_maxZoom || nodeInspectorObject.zoomTarget * delta < k_minZoom)
                        delta = 1;
                    nodeInspectorObject.zoomTarget *= delta;
                    nodeInspectorObject.positionOffsetTarget = nodeInspectorObject.lastMousePosition + (nodeInspectorObject.positionOffsetTarget - nodeInspectorObject.lastMousePosition) * delta;

                    nodeInspectorObject.cameraZoom *= 1.0f / delta;
                    break;
            }

            float zoomDiff = nodeInspectorObject.zoomTarget - nodeInspectorObject.zoom;
            Vector2 offsetDiff = nodeInspectorObject.positionOffsetTarget - nodeInspectorObject.positionOffset;

            if (Mathf.Abs(zoomDiff) > 0.01f || offsetDiff.magnitude > 0.01f)
            {
                nodeInspectorObject.zoom += zoomDiff * nodeInspectorObject.zoomSpeed * (float)nodeInspectorObject.deltaTime;
                nodeInspectorObject.positionOffset += offsetDiff * nodeInspectorObject.zoomSpeed * (float)nodeInspectorObject.deltaTime;
                nodeInspectorObject.needsRepaint = true;
            }
            else
            {
               nodeInspectorObject.zoom = nodeInspectorObject.zoomTarget;
               nodeInspectorObject.positionOffset = nodeInspectorObject.positionOffsetTarget;
            }
            nodeInspectorObject.shaderPos = LocalToWorld(Vector2.zero);

            if (nodeInspectorObject.needsRepaint)
            {
                Repaint();
                nodeInspectorObject.needsRepaint = false;
            }
        }
    }
}