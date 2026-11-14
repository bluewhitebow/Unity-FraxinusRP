using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using FraxinusEngine.Base;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [NodeCustomEditor(typeof(ScriptableRenderPassNode))]
    public class ScriptableRenderPassNodeView : BaseNodeView
    {
        #region Fields

        protected VisualElement _previewContainer;
        protected PassNodeSettingsView _settingsView;

        private Label _debugInfoLabel;
        private Image _pinIcon;
        private VisualElement _buttonRowContainer;
        private VisualElement _injectInfoContainer;
        private VisualElement _executionStatusBorder;
        
        private IVisualElementScheduledItem _debugInfoSchedule;
        private IVisualElementScheduledItem _autoHideSchedule;
        private IVisualElementScheduledItem _injectInfoSchedule;
        private readonly List<(Button button, Type passType, bool? enabled)> _injectPassButtons = new();
        private int _beforeInjectPassCount;
        private int _afterInjectPassCount;
        private int _fallbackInjectPassCount;

        private string[] _previewOptions;
        private int[] _previewIndices;

        #endregion

        #region Constants

        private const string StylesheetName = "UIElementRes/RendererGraphCommon";
        private const string NodeStylesheetName = "UIElementRes/RendererGraphNodes";

        private static readonly Color s_infoBackgroundColor = new Color(0.08f, 0.10f, 0.15f, 0.8f);
        private static readonly Color s_infoBorderColor = new Color(0.3f, 0.4f, 0.6f, 0.3f);
        private static readonly Color s_injectBackgroundColor = new Color(0.08f, 0.10f, 0.15f, 0.7f);
        private static readonly Color s_injectBorderColor = new Color(0.3f, 0.4f, 0.6f, 0.25f);
        
        private static readonly Color s_injectButtonBgColor = new Color(0.08f, 0.22f, 0.18f, 0.9f);
        private static readonly Color s_injectButtonTextColor = new Color(0.6f, 1.0f, 0.85f);
        private static readonly Color s_injectButtonBorderColor = new Color(0.3f, 0.7f, 0.6f, 0.5f);
        private static readonly Color s_injectButtonHoverBgColor = new Color(0.12f, 0.32f, 0.26f, 1f);
        private static readonly Color s_injectButtonHoverBorderColor = new Color(0.4f, 0.9f, 0.75f, 0.8f);

        private static readonly Color32 s_pinActiveColor = new Color32(245, 127, 23, 255);

        // Execution status colors used by the compact indicator.
        private static readonly Color s_executedBorderColor = new Color32(118, 221, 201, 255);
        private static readonly Color s_notExecutedBorderColor = new Color32(232, 157, 147, 255);
        private static readonly Color s_inactiveBorderColor = new Color32(177, 189, 205, 255);
        private static readonly Color s_excludedBorderColor = new Color32(196, 176, 233, 255);

        #endregion

        #region Properties

        public new ScriptableRendererGraphView owner => base.owner as ScriptableRendererGraphView;
        public new ScriptableRenderPassNode nodeTarget => base.nodeTarget as ScriptableRenderPassNode;
        public PassNodeSettings nodeSettings => nodeTarget.nodeSettings;

        protected override bool _hasSettings => nodeTarget.hasSettings;
        protected virtual string _header => string.Empty;
        protected virtual float _nodeWidth => CalculateAdaptiveWidth();
        protected virtual float _minNodeWidth => GraphNodeUtils.defaultNodeWidth;
        protected virtual float _maxNodeWidth => 400f;
        protected virtual bool _showDefaultInspector => true;
        protected virtual bool _showActiveToggle => true;
        protected virtual bool _showRenderOrder => false;

        #endregion


        #region Lifecycle

        public override void Enable(bool fromInspector)
        {
            CollectPreviewTextures();
            LoadStylesheet();

            contentContainer.AddToClassList("ControlsContainer");

            AddHeaderIfNeeded();
            AddRenderOrderIfNeeded();
            DrawDefaultInspectorIfNeeded(fromInspector);
            
            if (!fromInspector)
            {
                InitializeNodePresentation();
                InitializeButtonRow();
                InitializePreviewContainer();
                UpdateActiveStateIfNeeded();
                InitExecutionStatusBorder();
            }

            InitDebugInfoLabel();
            InitInjectInfoContainer();
            _injectInfoSchedule?.Pause();
            _injectInfoSchedule = schedule.Execute(RefreshInjectPassStates).Every(500);
        }

        public override bool RefreshPorts()
        {
            var result = base.RefreshPorts();
            UpdateAdaptiveWidth();

            return result;
        }

        private void UpdateAdaptiveWidth()
        {
            float calculatedWidth = CalculateAdaptiveWidth();
            style.width = calculatedWidth;
        }

        public override void Disable()
        {
            base.Disable();
            
            _debugInfoSchedule?.Pause();
            _debugInfoSchedule = null;
            
            _autoHideSchedule?.Pause();
            _autoHideSchedule = null;

            _injectInfoSchedule?.Pause();
            _injectInfoSchedule = null;
        }

        ~ScriptableRenderPassNodeView()
        {
            owner.renderPassNodeInspector.RemovePinnedView(this);
        }

        protected override void ComputeOrderUpdatedCallback(IEnumerable<BaseNode> computedNodes)
        {
            base.ComputeOrderUpdatedCallback(computedNodes);
            if (_injectInfoContainer != null)
                RefreshInjectPassStates();
        }

        #endregion

        #region Initialization

        private void LoadStylesheet()
        {
            var stylesheet = Resources.Load<StyleSheet>(StylesheetName);
            if (stylesheet != null && !styleSheets.Contains(stylesheet))
                styleSheets.Add(stylesheet);
        }

        private void InitializeNodePresentation()
        {
            AddToClassList("fraxinus-renderer-node");
            AddToClassList("fraxinus-node--" + GetNodeCategory());

            var stylesheet = Resources.Load<StyleSheet>(NodeStylesheetName);
            if (stylesheet != null && !styleSheets.Contains(stylesheet))
                styleSheets.Add(stylesheet);

            // Release the base view's inline title border so the scoped theme can style it.
            titleContainer.style.borderBottomColor = StyleKeyword.Null;
            titleContainer.style.borderBottomWidth = StyleKeyword.Null;

            string typeName = nodeTarget.GetType().Name;
            string customName = nodeTarget.GetCustomName();
            if (string.IsNullOrEmpty(customName) || string.Equals(customName, typeName, StringComparison.Ordinal))
            {
                string displayName = typeName;
                if (displayName.EndsWith("Node", StringComparison.Ordinal))
                    displayName = displayName.Substring(0, displayName.Length - 4);
                if (displayName.EndsWith("Pass", StringComparison.Ordinal))
                    displayName = displayName.Substring(0, displayName.Length - 4);
                title = ObjectNames.NicifyVariableName(displayName);
            }

            titleContainer.tooltip = typeName;
        }

        private string GetNodeCategory()
        {
            if (nodeTarget is MainLightShadowCasterPass || nodeTarget is AdditionalLightsShadowCasterPass)
                return "shadow";
            if (nodeTarget is PostProcessPass || nodeTarget is ColorGradingLutPass)
                return "post";
            if (nodeTarget is TextureBlitNode)
                return "resource";
            if (nodeTarget is ContextBeginNode || nodeTarget is ContextEndNode || nodeTarget is ContextBridgeNode)
                return "context";
            return "draw";
        }

        private string GetNodeCategoryLabel()
        {
            switch (GetNodeCategory())
            {
                case "shadow": return "SHADOW";
                case "post": return "POST PROCESS";
                case "resource": return "TEXTURE";
                case "context": return "CONTEXT";
                default: return "RENDER PASS";
            }
        }

        private void AddHeaderIfNeeded()
        {
            if (!String.IsNullOrEmpty(_header))
            {
                var title = new Label(_header);
                title.AddToClassList("PropertyEditorTitle");
                contentContainer.Add(title);
            }
        }

        private void AddRenderOrderIfNeeded()
        {
            if (_showRenderOrder)
                AddRenderOrderField();
        }

        private void DrawDefaultInspectorIfNeeded(bool fromInspector)
        {
            if (_showDefaultInspector)
                DrawDefaultInspector(fromInspector);
        }

        private void UpdateActiveStateIfNeeded()
        {
            if (_showActiveToggle)
                UpdateActiveState();
        }

        private void InitializeButtonRow()
        {
            _buttonRowContainer = new VisualElement { name = "ButtonRow" };
            _buttonRowContainer.AddToClassList("ButtonRow");
            mainContainer.Insert(mainContainer.IndexOf(titleContainer) + 1, _buttonRowContainer);

            var category = new Label(GetNodeCategoryLabel());
            category.AddToClassList("fraxinus-node-category");
            _buttonRowContainer.Add(category);
            
            AddActiveToggleIfNeeded();
            MoveSettingsButtonToButtonRow();
            AddPinButton();
        }

        private void AddActiveToggleIfNeeded()
        {
            if (!_showActiveToggle) return;

            var activeToggle = new Toggle
            {
                value = nodeTarget.active,
                tooltip = "Enable/Disable this render pass"
            };
            activeToggle.RegisterValueChangedCallback(e =>
            {
                owner.RegisterCompleteObjectUndo("Toggle Pass Active");
                nodeTarget.active = e.newValue;
                UpdateActiveState();
            });
            activeToggle.AddToClassList("ActiveToggle");
            _buttonRowContainer.Add(activeToggle);
        }

        private void MoveSettingsButtonToButtonRow()
        {
            schedule.Execute(() =>
            {
                var settingButton = titleContainer.Q<Button>("settings-button");
                if (settingButton != null)
                {
                    settingButton.RemoveFromHierarchy();
                    settingButton.tooltip = "Pass settings";
                    _buttonRowContainer.Insert(_buttonRowContainer.childCount - 1, settingButton);
                }
            }).ExecuteLater(0);
        }

        private void AddPinButton()
        {
            _pinIcon = new Image { image = RendererGraphEditorUtils.pinIcon, scaleMode = ScaleMode.ScaleToFit };
            
            var pinButton = new Button(() =>
            {
                if (nodeTarget.isPinned)
                    UnpinView();
                else
                    PinView();
            });
            pinButton.tooltip = "Pin / unpin in the pass inspector";

            pinButton.Add(_pinIcon);
            if (nodeTarget.isPinned)
                PinView();

            pinButton.AddToClassList("PinButton");
            _buttonRowContainer.Add(pinButton);
        }

        private void InitializePreviewContainer()
        {
            _previewContainer = new VisualElement();
            _previewContainer.AddToClassList("Preview");
            controlsContainer.Add(_previewContainer);
            UpdateTexturePreview();
        }

        private void AddRenderOrderField()
        {
            var renderOrderField = new EnumField("Render Order", nodeTarget.renderOrder);
            renderOrderField.RegisterValueChangedCallback(e =>
            {
                owner.RegisterCompleteObjectUndo("Updated Render Order " + e.newValue);
                nodeTarget.renderOrder = (RenderOrder)e.newValue;
                owner.graph.UpdateComputeOrder();
            });
            controlsContainer.Add(renderOrderField);
        }

        #endregion

        #region Settings View

        protected override VisualElement CreateSettingsView()
        {
            _settingsView = new PassNodeSettingsView(nodeSettings, owner);
            _settingsView.AddToClassList("NodeSettingsView");
            _settingsView.RegisterChangedCallback(() => UpdateTexturePreview());
            return _settingsView;
        }

        public void RefreshSettingsValues() => _settingsView.RefreshSettingsValues();

        #endregion

        #region Adaptive Width Calculation

        protected virtual float CalculateAdaptiveWidth()
        {
            float maxWidth = _minNodeWidth;

            foreach (var port in inputPortViews)
            {
                if (port.portData != null && !string.IsNullOrEmpty(port.portData.displayName))
                {
                    float estimatedWidth = EstimatePortWidth(port.portData.displayName, true);
                    maxWidth = Mathf.Max(maxWidth, estimatedWidth);
                }
            }

            foreach (var port in outputPortViews)
            {
                if (port.portData != null && !string.IsNullOrEmpty(port.portData.displayName))
                {
                    float estimatedWidth = EstimatePortWidth(port.portData.displayName, false);
                    maxWidth = Mathf.Max(maxWidth, estimatedWidth);
                }
            }

            float result = Mathf.Clamp(maxWidth, _minNodeWidth, _maxNodeWidth);
            return result;
        }

        protected virtual float EstimatePortWidth(string portName, bool isInput)
        {
            const float charWidth = 10f;
            const float portDotWidth = 12f;
            const float padding = 30f;
            const float portNameGap = 8f;

            float textWidth = portName.Length * charWidth;
            float totalWidth = padding + portDotWidth + portNameGap + textWidth + padding;

            return totalWidth;
        }

        #endregion

        #region Preview Textures

        private void CollectPreviewTextures()
        {
            var options = new List<string>();
            var indices = new List<int>();
            
            options.Add("Default");
            indices.Add(-1);
            
            var fields = nodeTarget.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            
            int textureIndex = 0;
            int arrayIndex = 0;

            foreach (var field in fields)
            {
                bool isTexture = field.FieldType == typeof(TextureHandleNodeParameter) || 
                                 field.FieldType.IsSubclassOf(typeof(TextureHandleNodeParameter));
                
                bool isTextureArray = field.FieldType == typeof(TextureHandlesNodeParameter) || 
                                      field.FieldType.IsSubclassOf(typeof(TextureHandlesNodeParameter));
                
                bool isCameraTargetBuffer = field.FieldType == typeof(CameraTargetBufferNodeParameter) ||
                                           field.FieldType.IsSubclassOf(typeof(CameraTargetBufferNodeParameter));

                if (isTexture || isTextureArray || isCameraTargetBuffer)
                {
                    var outputAttr = field.GetCustomAttribute<OutputAttribute>();
                    if (outputAttr != null)
                    {
                        string name = !string.IsNullOrEmpty(outputAttr.name) ? outputAttr.name : field.Name;
                        
                        if (isTexture)
                        {
                            options.Add($"{name}");
                            indices.Add(textureIndex);
                            textureIndex++;
                        }
                        else if (isCameraTargetBuffer)
                        {
                            options.Add($"{name} (Color)");
                            indices.Add(textureIndex);
                            textureIndex++;
                            
                            options.Add($"{name} (Depth)");
                            indices.Add(textureIndex);
                            textureIndex++;
                        }
                        else
                        {
                            for (int i = 0; i < ScriptableRenderPassNode.textureArrayMaxElements; i++)
                            {
                                options.Add($"{name} [{i}]");
                                int encodedIndex = ScriptableRenderPassNode.textureArrayPreviewIndexOffset + (arrayIndex * ScriptableRenderPassNode.textureArrayMaxElements) + i;
                                indices.Add(encodedIndex);
                            }
                            arrayIndex++;
                        }
                    }
                }
            }
            
            _previewOptions = options.ToArray();
            _previewIndices = indices.ToArray();
        }

        internal void UpdateTexturePreview()
        {
            if (_previewContainer == null) return;

            _previewContainer.Clear();
            var graph = owner.graph as ScriptableRendererGraph;
            if (graph != null && graph.globalPreviewEnabled && nodeSettings.showPreview && nodeTarget.previewTextureValid)
                CreateTexturePreview(_previewContainer, nodeTarget);
        }

        protected void CreateTexturePreview(VisualElement previewContainer, ScriptableRenderPassNode node)
        {
            VisualElement texturePreview = new VisualElement();
            previewContainer.Add(texturePreview);

            CreateTexturePreviewImGUI(texturePreview, node);

            previewContainer.name = node.previewTexture.dimension.ToString();

            Button togglePreviewButton = null;
            togglePreviewButton = new Button(() =>
            {
                nodeSettings.isPreviewCollapsed = !nodeSettings.isPreviewCollapsed;
                UpdatePreviewCollapseState();
            });

            togglePreviewButton.ClearClassList();
            togglePreviewButton.AddToClassList("PreviewToggleButton");
            previewContainer.Add(togglePreviewButton);

            UpdatePreviewCollapseState();

            void UpdatePreviewCollapseState()
            {
                if (!nodeSettings.isPreviewCollapsed)
                {
                    texturePreview.style.display = DisplayStyle.Flex;
                    togglePreviewButton.RemoveFromClassList("Collapsed");
                }
                else
                {
                    texturePreview.style.display = DisplayStyle.None;
                    togglePreviewButton.AddToClassList("Collapsed");
                }
            }
        }

        private void CreateTexturePreviewImGUI(VisualElement previewContainer, ScriptableRenderPassNode node)
        {
            var previewImageSlice = new IMGUIContainer(() =>
            {
                if (node.previewTexture.dimension == TextureDimension.Tex3D)
                {
                    EditorGUI.BeginChangeCheck();
                    EditorGUIUtility.labelWidth = 70;
                    nodeSettings.previewSlice = EditorGUILayout.Slider("3D Slice", nodeSettings.previewSlice, 0, RendererGraphTextureUtils.GetSliceCount(node.previewTexture) - 1);
                    EditorGUIUtility.labelWidth = 0;
                    if (EditorGUI.EndChangeCheck())
                        MarkDirtyRepaint();
                }

                DrawPreviewSettings(node.previewTexture);

                Rect previewRect = GetPreviewRect(node.previewTexture);
                DrawImGUIPreview(node, previewRect, nodeSettings.previewSlice);

                DrawTextureInfoHover(previewRect, node.previewTexture);
            }) { name = "ImGUIPreview" };

            _autoHideSchedule = RendererGraphEditorUtils.ScheduleAutoHide(previewContainer, owner);
            previewContainer.Add(previewImageSlice);
        }

        protected virtual void DrawPreviewSettings(Texture texture)
        {
            GUILayout.Space(6);
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Delete)
                owner.DelayedDeleteSelection();

            using (new GUILayout.HorizontalScope(EditorStyles.toolbar, GUILayout.Height(12)))
                DrawPreviewToolbar(texture);
        }

        protected virtual void DrawPreviewToolbar(Texture texture)
        {
            EditorGUI.BeginChangeCheck();

            if (_previewOptions != null && _previewOptions.Length > 1)
            {
                int currentIndex = 0;
                for (int i = 0; i < _previewIndices.Length; i++)
                {
                    if (_previewIndices[i] == nodeTarget.previewTextureIndex)
                    {
                        currentIndex = i;
                        break;
                    }
                }
                
                int newIndex = EditorGUILayout.Popup(currentIndex, _previewOptions, EditorStyles.toolbarPopup, GUILayout.MaxWidth(120));
                
                if (newIndex != currentIndex)
                {
                    owner.RegisterCompleteObjectUndo("Change Preview Texture");
                    nodeTarget.previewTextureIndex = _previewIndices[newIndex];
                }
                
                GUILayout.Space(6);
            }

            bool r = GUILayout.Toggle((nodeSettings.previewChannels & PreviewChannels.R) != 0, "R", EditorStyles.toolbarButton);
            bool g = GUILayout.Toggle((nodeSettings.previewChannels & PreviewChannels.G) != 0, "G", EditorStyles.toolbarButton);
            bool b = GUILayout.Toggle((nodeSettings.previewChannels & PreviewChannels.B) != 0, "B", EditorStyles.toolbarButton);
            bool a = GUILayout.Toggle((nodeSettings.previewChannels & PreviewChannels.A) != 0, "A", EditorStyles.toolbarButton);

            if (EditorGUI.EndChangeCheck())
            {
                owner.RegisterCompleteObjectUndo("Updated Preview Masks");
                nodeSettings.previewChannels =
                    (r ? PreviewChannels.R : 0) |
                    (g ? PreviewChannels.G : 0) |
                    (b ? PreviewChannels.B : 0) |
                    (a ? PreviewChannels.A : 0);
            }

            GUILayout.FlexibleSpace();

            EditorGUI.BeginChangeCheck();
            bool srgb = GUILayout.Toggle(nodeSettings.previewSRGB, "sRGB", EditorStyles.toolbarButton);
            if (EditorGUI.EndChangeCheck())
            {
                owner.RegisterCompleteObjectUndo("Updated Preview Masks");
                nodeSettings.previewSRGB = srgb;
            }
        }

        protected virtual void DrawImGUIPreview(ScriptableRenderPassNode node, Rect previewRect, float currentSlice)
        {
            switch (node.previewTexture.dimension)
            {
                case TextureDimension.Tex2D:
                    DrawTexture2DPreview(node, previewRect);
                    break;
                case TextureDimension.Tex3D:
                    DrawTexture3DPreview(node, previewRect, currentSlice);
                    break;
                case TextureDimension.Cube:
                    DrawTextureCubePreview(node, previewRect);
                    break;
            }
        }

        private void DrawTexture2DPreview(ScriptableRenderPassNode node, Rect previewRect)
        {
            Texture tex2D = node.previewTexture;
            RendererGraphEditorUtils.texture2DPreviewMaterial.SetTexture("_MainTex0_2D", tex2D);
            RendererGraphEditorUtils.texture2DPreviewMaterial.SetVector("_Size", new Vector4(tex2D.width, tex2D.height, 1, 1));
            RendererGraphEditorUtils.texture2DPreviewMaterial.SetVector("_Channels", RendererGraphEditorUtils.GetChannelMask(nodeSettings.previewChannels));
            RendererGraphEditorUtils.texture2DPreviewMaterial.SetFloat("_IsSRGB", nodeSettings.previewSRGB ? 1 : 0);

            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawPreviewTexture(previewRect, node.previewTexture, RendererGraphEditorUtils.texture2DPreviewMaterial, ScaleMode.ScaleToFit, 0, 0);
        }

        private void DrawTexture3DPreview(ScriptableRenderPassNode node, Rect previewRect, float currentSlice)
        {
            Texture tex3D = node.previewTexture;
            RendererGraphEditorUtils.texture2DPreviewMaterial.SetTexture("_MainTex0_3D", tex3D);
            RendererGraphEditorUtils.texture2DPreviewMaterial.SetVector("_Channels", RendererGraphEditorUtils.GetChannelMask(nodeSettings.previewChannels));
            RendererGraphEditorUtils.texture2DPreviewMaterial.SetFloat("_Depth", currentSlice / node.nodeSettings.GetResolvedDepth());
            RendererGraphEditorUtils.texture2DPreviewMaterial.SetFloat("_IsSRGB", nodeSettings.previewSRGB ? 1 : 0);

            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawPreviewTexture(previewRect, Texture2D.whiteTexture, RendererGraphEditorUtils.texture2DPreviewMaterial, ScaleMode.ScaleToFit, 0, 0);
        }

        private void DrawTextureCubePreview(ScriptableRenderPassNode node, Rect previewRect)
        {
            Texture texCube = node.previewTexture;
            RendererGraphEditorUtils.texture2DPreviewMaterial.SetTexture("_Cubemap", texCube);
            RendererGraphEditorUtils.texture2DPreviewMaterial.SetVector("_Channels", RendererGraphEditorUtils.GetChannelMask(nodeSettings.previewChannels));
            RendererGraphEditorUtils.texture2DPreviewMaterial.SetFloat("_IsSRGB", nodeSettings.previewSRGB ? 1 : 0);

            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawPreviewTexture(previewRect, Texture2D.whiteTexture, RendererGraphEditorUtils.texture2DPreviewMaterial, ScaleMode.ScaleToFit, 0, 0);
        }

        private Rect GetPreviewRect(Texture texture)
        {
            float width = _nodeWidth;
            float scaleFactor = width / texture.width;
            float height = Mathf.Min(_nodeWidth, texture.height * scaleFactor);
            return GUILayoutUtility.GetRect(1, width, 1, height);
        }

        private void DrawTextureInfoHover(Rect previewRect, Texture texture)
        {
            Rect infoRect = previewRect;
            infoRect.yMin += previewRect.height - 24;
            infoRect.height = 20;
            previewRect.yMax -= 4;

            if (!(EditorWindow.mouseOverWindow is ScriptableRendererGraphWindow rendererGraphWindow && rendererGraphWindow.GetCurrentGraph() == owner.graph))
                return;

            if (previewRect.Contains(Event.current.mousePosition) && !infoRect.Contains(Event.current.mousePosition))
            {
                EditorGUI.DrawRect(infoRect, new Color(0, 0, 0, 0.65f));
                infoRect.xMin += 8;

                GUI.color = Color.white;
                int slices = (texture.dimension == TextureDimension.Cube) ? 6 : RendererGraphTextureUtils.GetSliceCount(texture);
                GUI.Label(infoRect, $"{texture.width}x{texture.height}{(slices > 1 ? "x" + slices.ToString() : "")} - {nodeTarget.previewTexture.graphicsFormat}", EditorStyles.boldLabel);
            }
        }

        protected Vector2 GetPreviewMousePositionBetween01(Vector2 mousePosition)
        {
            if (nodeTarget.previewTexture == null)
                return Vector2.zero;

            var local = _previewContainer.WorldToLocal(mousePosition);
            local.y -= EditorGUIUtility.singleLineHeight + 13;

            float width = _nodeWidth - 8;
            float scaleFactor = width / nodeTarget.previewTexture.width;
            float height = Mathf.Min(width, nodeTarget.previewTexture.height * scaleFactor);
            local.x /= width;
            local.y /= height;

            local.x = Mathf.Clamp01(local.x);
            local.y = Mathf.Clamp01(local.y);

            return local;
        }

        #endregion

        #region Pin/Unpin

        internal void PinView()
        {
            nodeTarget.isPinned = true;
            _pinIcon.tintColor = s_pinActiveColor;
            _pinIcon.image = RendererGraphEditorUtils.unpinIcon;
            schedule.Execute(() => owner.renderPassNodeInspector.AddPinnedView(this)).ExecuteLater(1);
        }

        internal void UnpinView()
        {
            owner.renderPassNodeInspector.RemovePinnedView(this);
            nodeTarget.isPinned = false;
            _pinIcon.tintColor = Color.white;
            _pinIcon.image = RendererGraphEditorUtils.pinIcon;
            _pinIcon.transform.rotation = Quaternion.identity;
        }

        #endregion

        #region Debug Info

        private void InitDebugInfoLabel()
        {
            if (_debugInfoLabel != null)
                return;

            _debugInfoLabel = new Label();
            _debugInfoLabel.style.unityTextAlign = TextAnchor.UpperLeft;
            _debugInfoLabel.style.whiteSpace = WhiteSpace.Normal;
            _debugInfoLabel.style.fontSize = 10;
            _debugInfoLabel.style.color = new Color(0.8f, 1.0f, 0.9f);
            
            SetupInfoLabelStyle(_debugInfoLabel, new Color(0.05f, 0.08f, 0.12f, 0.85f), new Color(0.2f, 0.5f, 0.4f, 0.4f));
            _debugInfoLabel.style.display = DisplayStyle.None;
            Add(_debugInfoLabel);

            _debugInfoSchedule = schedule.Execute(() =>
            {
                int excuteOrder = nodeTarget.excuteOrder;
                bool hasCustomDebugInfo = !string.IsNullOrEmpty(nodeTarget.debugInfo);
                
                bool isExecuted = excuteOrder >= 0;
                this.style.opacity = (!nodeTarget.active || !isExecuted) ? 0.65f : 1.0f;
                
                bool isExcluded = nodeTarget.computeOrder >= 0 && excuteOrder < 0;
                UpdateExecutionStatusBorder(nodeTarget.active, isExecuted, isExcluded);
                
                // debug 标签显示逻辑
                if (!nodeTarget.debug && !hasCustomDebugInfo)
                {
                    _debugInfoLabel.style.display = DisplayStyle.None;
                    return;
                }

                string debugText = "";
                
                if (nodeTarget.debug)
                {
                    debugText = $"Order: {excuteOrder}";
                }
                
                if (hasCustomDebugInfo)
                {
                    if (!string.IsNullOrEmpty(debugText))
                        debugText += $"\n\n{nodeTarget.debugInfo}";
                    else
                        debugText = nodeTarget.debugInfo;
                }

                _debugInfoLabel.text = debugText;
                _debugInfoLabel.style.display = DisplayStyle.Flex;
                
                if (hasCustomDebugInfo)
                {
                    if (nodeTarget.debugInfo.Contains("❌"))
                        _debugInfoLabel.style.color = new Color(1.0f, 0.3f, 0.3f); // 红色 - 错误
                    else if (nodeTarget.debugInfo.Contains("⚠️"))
                        _debugInfoLabel.style.color = new Color(1.0f, 0.8f, 0.3f); // 黄色 - 警告
                    else
                        _debugInfoLabel.style.color = new Color(0.8f, 1.0f, 0.9f); // 绿色 - 信息
                }
                else
                    _debugInfoLabel.style.color = new Color(0.8f, 1.0f, 0.9f);
            }).Every(66);
        }

        #endregion

        #region Execution Status Border

        private void InitExecutionStatusBorder()
        {
            if (_executionStatusBorder != null) return;
            
            // Keep execution state distinct from the category accent in the title.
            _executionStatusBorder = new VisualElement { name = "ExecutionStatusBorder" };
            _executionStatusBorder.style.backgroundColor = s_inactiveBorderColor;
            _executionStatusBorder.tooltip = "Waiting for execution status";
            _buttonRowContainer.Insert(0, _executionStatusBorder);
        }

        private void UpdateExecutionStatusBorder(bool isActive, bool isExecuted, bool isExcluded)
        {
            if (_executionStatusBorder == null) return;
            
            Color borderColor;
            if (!isActive)
                borderColor = s_inactiveBorderColor;
            else if (isExcluded)
                borderColor = s_excludedBorderColor;
            else if (isExecuted)
                borderColor = s_executedBorderColor;
            else
                borderColor = s_notExecutedBorderColor;
            
            _executionStatusBorder.style.backgroundColor = borderColor;
            _executionStatusBorder.tooltip = !isActive ? "Pass disabled"
                : isExcluded ? "Culled from this camera"
                : isExecuted ? "Executed for this camera"
                : "Not executed for this camera";
        }

        #endregion

        #region Inject Info

        private void InitInjectInfoContainer()
        {
            if (_injectInfoContainer != null) return;

            _injectInfoContainer = new VisualElement
            {
                name = "InjectInfoContainer"
            };
            
            _injectInfoContainer.style.flexDirection = FlexDirection.Row;
            _injectInfoContainer.style.justifyContent = Justify.Center;
            _injectInfoContainer.style.flexWrap = Wrap.Wrap;
            _injectInfoContainer.style.marginTop = 2;
            _injectInfoContainer.style.marginBottom = 2;

            SetupInfoLabelStyle(_injectInfoContainer, s_injectBackgroundColor, s_injectBorderColor);
            Add(_injectInfoContainer);

            UpdateInjectPassesContainer(_injectInfoContainer);
        }

        private void UpdateInjectPassesContainer(VisualElement container)
        {
            container.Clear();
            _injectPassButtons.Clear();

            var beforePasses = nodeTarget.beforeRenderingPasses;
            var afterPasses = nodeTarget.afterRenderingPasses;
            var fallbackPasses = nodeTarget.fallbackRenderingPasses;
            _beforeInjectPassCount = beforePasses.Count;
            _afterInjectPassCount = afterPasses.Count;
            _fallbackInjectPassCount = fallbackPasses.Count;

            if (beforePasses.Count == 0 && afterPasses.Count == 0 && fallbackPasses.Count == 0)
            {
                container.style.display = DisplayStyle.None;
                return;
            }

            container.style.display = DisplayStyle.Flex;
            container.style.flexDirection = FlexDirection.Column;
            container.style.justifyContent = Justify.FlexStart;

            if (beforePasses.Count > 0)
                container.Add(CreateInjectPassFoldout("InjectedPasses(Before)", beforePasses));

            if (afterPasses.Count > 0)
                container.Add(CreateInjectPassFoldout("InjectedPasses(After)", afterPasses));

            if (fallbackPasses.Count > 0)
                container.Add(CreateInjectPassFoldout("InjectedPasses(Fallback)", fallbackPasses));

            UpdateInjectPassButtonStates();
        }

        public void RefreshInjectPassStates()
        {
            if (_injectInfoContainer == null || nodeTarget == null)
                return;

            var beforePasses = nodeTarget.beforeRenderingPasses;
            var afterPasses = nodeTarget.afterRenderingPasses;
            var fallbackPasses = nodeTarget.fallbackRenderingPasses;
            if (beforePasses.Count != _beforeInjectPassCount
                || afterPasses.Count != _afterInjectPassCount
                || fallbackPasses.Count != _fallbackInjectPassCount
                || !InjectPassTypesMatch(beforePasses, 0)
                || !InjectPassTypesMatch(afterPasses, _beforeInjectPassCount)
                || !InjectPassTypesMatch(fallbackPasses, _beforeInjectPassCount + _afterInjectPassCount))
            {
                UpdateInjectPassesContainer(_injectInfoContainer);
                return;
            }

            UpdateInjectPassButtonStates();
        }

        private bool InjectPassTypesMatch(IReadOnlyList<InjectPass> passes, int offset)
        {
            for (int i = 0; i < passes.Count; i++)
            {
                if (passes[i].GetType() != _injectPassButtons[offset + i].passType)
                    return false;
            }
            return true;
        }

        private void UpdateInjectPassButtonStates()
        {
            var graph = owner?.graph;
            if (graph == null)
                return;

            for (int i = 0; i < _injectPassButtons.Count; i++)
            {
                var entry = _injectPassButtons[i];
                bool enabled = graph.IsInjectPassEnabled(entry.passType);
                if (entry.enabled == enabled)
                    continue;

                entry.button.style.opacity = enabled ? 1f : 0.45f;
                entry.button.tooltip = $"{entry.passType.FullName}\n"
                    + (enabled ? "Enabled for this renderer." : "Disabled for this renderer.")
                    + " Click to open script.";
                entry.enabled = enabled;
                _injectPassButtons[i] = entry;
            }
        }

        private Foldout CreateInjectPassFoldout(string title, IReadOnlyList<InjectPass> passes)
        {
            var foldout = new Foldout
            {
                text = title,
                value = true
            };
            foldout.style.fontSize = 10;
            foldout.style.marginLeft = 4;
            foldout.style.marginRight = 4;

            var buttonContainer = new VisualElement();
            buttonContainer.style.flexDirection = FlexDirection.Row;
            buttonContainer.style.flexWrap = Wrap.Wrap;
            buttonContainer.style.justifyContent = Justify.FlexStart;

            foreach (var pass in passes)
            {
                var button = CreateInjectPassButton(pass);
                buttonContainer.Add(button);
            }

            foldout.Add(buttonContainer);
            return foldout;
        }

        private Button CreateInjectPassButton(object pass)
        {
            Type passType = pass.GetType();

            var button = new Button(() => TryOpenScriptByFileSearch(passType))
            {
                text = passType.Name,
                tooltip = $"{passType.FullName}"
            };

            SetupInjectButtonStyle(button);
            RegisterInjectButtonHoverEvents(button);
            _injectPassButtons.Add((button, passType, null));

            return button;
        }

        private void SetupInjectButtonStyle(Button button)
        {
            button.style.backgroundColor = s_injectButtonBgColor;
            button.style.color = s_injectButtonTextColor;
            button.style.borderTopWidth = 1;
            button.style.borderBottomWidth = 1;
            button.style.borderLeftWidth = 1;
            button.style.borderRightWidth = 1;
            button.style.borderTopColor = s_injectButtonBorderColor;
            button.style.borderBottomColor = s_injectButtonBorderColor;
            button.style.borderLeftColor = s_injectButtonBorderColor;
            button.style.borderRightColor = s_injectButtonBorderColor;
        }

        private void RegisterInjectButtonHoverEvents(Button button)
        {
            button.RegisterCallback<MouseEnterEvent>(evt =>
            {
                button.style.backgroundColor = s_injectButtonHoverBgColor;
                button.style.borderTopColor = s_injectButtonHoverBorderColor;
                button.style.borderBottomColor = s_injectButtonHoverBorderColor;
                button.style.borderLeftColor = s_injectButtonHoverBorderColor;
                button.style.borderRightColor = s_injectButtonHoverBorderColor;
                button.style.scale = new Scale(new Vector3(1.05f, 1.05f, 1f));
            });

            button.RegisterCallback<MouseLeaveEvent>(evt =>
            {
                button.style.backgroundColor = s_injectButtonBgColor;
                button.style.borderTopColor = s_injectButtonBorderColor;
                button.style.borderBottomColor = s_injectButtonBorderColor;
                button.style.borderLeftColor = s_injectButtonBorderColor;
                button.style.borderRightColor = s_injectButtonBorderColor;
                button.style.scale = new Scale(Vector3.one);
            });
        }

        private void TryOpenScriptByFileSearch(Type type)
        {
            string[] allGuids = AssetDatabase.FindAssets(type.Name);
            foreach (var guid in allGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".cs") && System.IO.Path.GetFileNameWithoutExtension(path) == type.Name)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (asset != null)
                    {
                        AssetDatabase.OpenAsset(asset);
                        return;
                    }
                }
            }
        }

        #endregion

        #region Helper Methods

        private void SetupInfoLabelStyle(VisualElement element, Color backgroundColor, Color borderColor)
        {
            element.style.backgroundColor = backgroundColor;
            element.style.paddingTop = 4;
            element.style.paddingBottom = 4;
            element.style.paddingLeft = 4;
            element.style.paddingRight = 4;
            
            element.style.borderTopLeftRadius = 4;
            element.style.borderTopRightRadius = 4;
            element.style.borderBottomLeftRadius = 4;
            element.style.borderBottomRightRadius = 4;

            element.style.borderTopWidth = 1;
            element.style.borderBottomWidth = 1;
            element.style.borderLeftWidth = 1;
            element.style.borderRightWidth = 1;
            element.style.borderTopColor = borderColor;
            element.style.borderBottomColor = borderColor;
            element.style.borderLeftColor = borderColor;
            element.style.borderRightColor = borderColor;
        }

        protected void UpdateActiveState()
        {
            if (nodeTarget.active)
            {
                this.style.opacity = 1.0f;
                this.RemoveFromClassList("inactive");
            }
            else
            {
                this.style.opacity = 0.5f;
                this.AddToClassList("inactive");
            }
        }

        #endregion
    }
}
