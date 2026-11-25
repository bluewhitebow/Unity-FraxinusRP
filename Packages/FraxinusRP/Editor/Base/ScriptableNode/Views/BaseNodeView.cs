using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Base;

using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;

using Status = UnityEngine.UIElements.DropdownMenuAction.Status;
using NodeView = UnityEditor.Experimental.GraphView.Node;

namespace FraxinusEditor.Base
{
    [NodeCustomEditor(typeof(BaseNode))]
    public class BaseNodeView : NodeView
    {
        public BaseNode                 nodeTarget;
        public List<PortView>           inputPortViews = new List<PortView>();
        public List<PortView>           outputPortViews = new List<PortView>();

        public BaseGraphView            owner { private set; get; }

        protected Dictionary<string, List<PortView>> _portsPerFieldName = new Dictionary<string, List<PortView>>();

        public VisualElement            controlsContainer;
        protected VisualElement         _debugContainer;
        protected VisualElement         _rightTitleContainer;
        protected VisualElement         _topPortContainer;
        protected VisualElement         _bottomPortContainer;
        private VisualElement           _inputContainerElement;

        VisualElement                   _settings;
        NodeSettingsView                _settingsContainer;
        Button                          _settingButton;
        TextField                       _titleTextField;

        Label                           _computeOrderLabel = new Label();

        public event Action<PortView>   _onPortConnected;
        public event Action<PortView>   _onPortDisconnected;

        protected virtual bool          _hasSettings { get; set; }

        public bool                     initializing = false; //Used for applying SetPosition on locked node at init.

        readonly string                 _baseNodeStyle = "GraphProcessorStyles/BaseNodeView";
        bool                            _settingsExpanded = false;

        [NonSerialized]
        List<IconBadge>                 _badges = new List<IconBadge>();

        private List<Node> _selectedNodes = new List<Node>();
        private float      _selectedNodesFarLeft;
        private float      _selectedNodesNearLeft;
        private float      _selectedNodesFarRight;
        private float      _selectedNodesNearRight;
        private float      _selectedNodesFarTop;
        private float      _selectedNodesNearTop;
        private float      _selectedNodesFarBottom;
        private float      _selectedNodesNearBottom;
        private float      _selectedNodesAvgHorizontal;
        private float      _selectedNodesAvgVertical;

        private IVisualElementScheduledItem _selectionBorderSyncSchedule;
        private bool _disposed;
        private bool _disabled;
        
        #region  Initialization
        
        public void Initialize(BaseGraphView owner, BaseNode node)
        {
            nodeTarget = node;
            this.owner = owner;

            if (!node.deletable)
                capabilities &= ~Capabilities.Deletable;
            // Note that the Renamable capability is useless right now as it haven't been implemented in Graphview
            if (node.isRenamable)
                capabilities |= Capabilities.Renamable;

            owner.graph.afterComputedOrder += ComputeOrderUpdatedCallback;
            node.onMessageAdded += AddMessageView;
            node.onMessageRemoved += RemoveMessageView;
            node.onPortsUpdated += OnPortsUpdated;

            styleSheets.Add(Resources.Load<StyleSheet>(_baseNodeStyle));
            if (!string.IsNullOrEmpty(node.layoutStyle))
                styleSheets.Add(Resources.Load<StyleSheet>(node.layoutStyle));

            InitializeView();
            InitializePorts();
            InitializeDebug();

            // If the standard Enable method is still overwritten, we call it
            if (GetType().GetMethod(nameof(Enable), new Type[]{}).DeclaringType != typeof(BaseNodeView))
                ExceptionToLog.Call(() => Enable());
            else
                ExceptionToLog.Call(() => Enable(false));

            InitializeSettings();

            RefreshExpandedState();

            this.RefreshPorts();

            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RegisterCallback<DetachFromPanelEvent>(e => DisableView());
            OnGeometryChanged(null);
        }

        void OnPortsUpdated(string fieldName)
        {
            schedule.Execute(() => { if (!_disposed) UpdatePortsForField(fieldName); }).ExecuteLater(0);
        }

        void DisableView()
        {
            if (_disabled)
                return;
            _disabled = true;
            ExceptionToLog.Call(Disable);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Undo.undoRedoPerformed -= UpdateFieldValues;
            if (owner?.graph != null)
                owner.graph.afterComputedOrder -= ComputeOrderUpdatedCallback;
            nodeTarget.onMessageAdded -= AddMessageView;
            nodeTarget.onMessageRemoved -= RemoveMessageView;
            nodeTarget.onPortsUpdated -= OnPortsUpdated;
            _selectionBorderSyncSchedule?.Pause();
            DisableView();
        }

        void InitializePorts()
        {
            var listener = owner.connectorListener;
            foreach (var inputPort in nodeTarget.inputPorts)
                AddPort(inputPort.fieldInfo, Direction.Input, listener, inputPort.portData);

            foreach (var outputPort in nodeTarget.outputPorts)
                AddPort(outputPort.fieldInfo, Direction.Output, listener, outputPort.portData);
        }

        void InitializeView()
        {
            controlsContainer = new VisualElement{ name = "controls" };
            controlsContainer.AddToClassList("NodeControls");
            mainContainer.Add(controlsContainer);

            _rightTitleContainer = new VisualElement{ name = "RightTitleContainer" };
            titleContainer.Add(_rightTitleContainer);

            _topPortContainer = new VisualElement { name = "TopPortContainer" };
            this.Insert(0, _topPortContainer);

            _bottomPortContainer = new VisualElement { name = "BottomPortContainer" };
            this.Add(_bottomPortContainer);

            if (nodeTarget.showControlsOnHover)
            {
                bool mouseOverControls = false;
                controlsContainer.style.display = DisplayStyle.None;
                RegisterCallback<MouseOverEvent>(e => 
                {
                    controlsContainer.style.display = DisplayStyle.Flex;
                    mouseOverControls = true;
                });

                RegisterCallback<MouseOutEvent>(e => 
                {
                    var rect = GetPosition();
                    var graphMousePosition = owner.contentViewContainer.WorldToLocal(e.mousePosition);
                    if (rect.Contains(graphMousePosition) || !nodeTarget.showControlsOnHover)
                        return;

                    mouseOverControls = false;
                    schedule.Execute(_ => 
                    {
                        if (!mouseOverControls)
                            controlsContainer.style.display = DisplayStyle.None;
                    }).ExecuteLater(500);
                });
            }

            Undo.undoRedoPerformed += UpdateFieldValues;

            _debugContainer = new VisualElement{ name = "debug" };
            if (nodeTarget.debug)
                mainContainer.Add(_debugContainer);

            initializing = true;

            UpdateTitle();
            SetPosition(nodeTarget.position);
            SetNodeColor(nodeTarget.color);
            
            AddInputContainer();

            // Add renaming capability
            if ((capabilities & Capabilities.Renamable) != 0)
                SetupRenamableTitle();
        }

        void SetupRenamableTitle()
        {
            var titleLabel = this.Q("title-label") as Label;

            _titleTextField = new TextField{ isDelayed = true };
            _titleTextField.style.display = DisplayStyle.None;
            titleLabel.parent.Insert(0, _titleTextField);

            titleLabel.RegisterCallback<MouseDownEvent>(e => {
                if (e.clickCount == 2 && e.button == (int)MouseButton.LeftMouse)
                    OpenTitleEditor();
            });

            _titleTextField.RegisterValueChangedCallback(e => CloseAndSaveTitleEditor(e.newValue));

            _titleTextField.RegisterCallback<MouseDownEvent>(e => {
                if (e.clickCount == 2 && e.button == (int)MouseButton.LeftMouse)
                    CloseAndSaveTitleEditor(_titleTextField.value);
            });

            _titleTextField.RegisterCallback<FocusOutEvent>(e => CloseAndSaveTitleEditor(_titleTextField.value));

            void OpenTitleEditor()
            {
                // show title textbox
                _titleTextField.style.display = DisplayStyle.Flex;
                titleLabel.style.display = DisplayStyle.None;
                _titleTextField.focusable = true;

                _titleTextField.SetValueWithoutNotify(title);
                _titleTextField.Focus();
                _titleTextField.SelectAll();
            }

            void CloseAndSaveTitleEditor(string newTitle)
            {
                owner.RegisterCompleteObjectUndo("Renamed node " + newTitle);
                nodeTarget.SetCustomName(newTitle);

                // hide title TextBox
                _titleTextField.style.display = DisplayStyle.None;
                titleLabel.style.display = DisplayStyle.Flex;
                _titleTextField.focusable = false;

                UpdateTitle();
            }
        }

        void UpdateTitle()
        {
            title = (nodeTarget.GetCustomName() == null) ? nodeTarget.GetType().Name : nodeTarget.GetCustomName();
        }

        void InitializeSettings()
        {
            // Initialize settings button:
            if (_hasSettings)
            {
                CreateSettingButton();
                _settingsContainer = new NodeSettingsView();
                _settingsContainer.visible = false;
                _settings = new VisualElement();
                // Add Node type specific settings
                _settings.Add(CreateSettingsView());
                _settingsContainer.Add(_settings);
                Add(_settingsContainer);
                
                var fields = nodeTarget.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                foreach(var field in fields)
                    if(field.GetCustomAttribute(typeof(SettingAttribute)) != null) 
                        AddSettingField(field);
            }
        }

        void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (_settingButton != null)
            {
                var settingsButtonLayout = _settingButton.ChangeCoordinatesTo(_settingsContainer.parent, _settingButton.layout);
                _settingsContainer.style.top = settingsButtonLayout.yMax - 18f;
                _settingsContainer.style.left = settingsButtonLayout.xMin - layout.width + 20f;
            }
        }

        // Workaround for bug in GraphView that makes the node selection border way too big
        VisualElement selectionBorder, nodeBorder;
        internal void EnableSyncSelectionBorderHeight()
        {
            if (selectionBorder == null || nodeBorder == null)
            {
                selectionBorder = this.Q("selection-border");
                nodeBorder = this.Q("node-border");

                _selectionBorderSyncSchedule = schedule.Execute(() => {
                    selectionBorder.style.height = nodeBorder.localBound.height;
                }).Every(17);
            }
        }
        
        void CreateSettingButton()
        {
            _settingButton = new Button(ToggleSettings){name = "settings-button"};
            _settingButton.Add(new Image { name = "icon", scaleMode = ScaleMode.ScaleToFit });

            titleContainer.Add(_settingButton);
        }

        void ToggleSettings()
        {
            _settingsExpanded = !_settingsExpanded;
            if (_settingsExpanded)
                OpenSettings();
            else
                CloseSettings();
        }

        public void OpenSettings()
        {
            if (_settingsContainer != null)
            {
                owner.ClearSelection();
                owner.AddToSelection(this);

                _settingButton.AddToClassList("clicked");
                _settingsContainer.visible = true;
                _settingsExpanded = true;
            }
        }

        public void CloseSettings()
        {
            if (_settingsContainer != null)
            {
                _settingButton.RemoveFromClassList("clicked");
                _settingsContainer.visible = false;
                _settingsExpanded = false;
            }
        }

        void InitializeDebug()
        {
            ComputeOrderUpdatedCallback(null);
            _debugContainer.Add(_computeOrderLabel);
        }

        #endregion

        #region API

        public List<PortView> GetPortViewsFromFieldName(string fieldName)
        {
            List<PortView>    ret;

            _portsPerFieldName.TryGetValue(fieldName, out ret);

            return ret;
        }

        public PortView GetFirstPortViewFromFieldName(string fieldName)
        {
            return GetPortViewsFromFieldName(fieldName)?.First();
        }

        public PortView GetPortViewFromFieldName(string fieldName, string identifier)
        {
            return GetPortViewsFromFieldName(fieldName)?.FirstOrDefault(pv => {
                return (pv.portData.identifier == identifier) || (String.IsNullOrEmpty(pv.portData.identifier) && String.IsNullOrEmpty(identifier));
            });
        }

        public PortView AddPort(FieldInfo fieldInfo, Direction direction, BaseEdgeConnectorListener listener, PortData portData)
        {
            PortView p = CreatePortView(direction, fieldInfo, portData, listener);

            if (p.direction == Direction.Input)
            {
                inputPortViews.Add(p);

                if (portData.vertical)
                    _topPortContainer.Add(p);
                else
                    inputContainer.Add(p);
            }
            else
            {
                outputPortViews.Add(p);

                if (portData.vertical)
                    _bottomPortContainer.Add(p);
                else
                    outputContainer.Add(p);
            }

            p.Initialize(this, portData?.displayName);

            List<PortView> ports;
            _portsPerFieldName.TryGetValue(p.fieldName, out ports);
            if (ports == null)
            {
                ports = new List<PortView>();
                _portsPerFieldName[p.fieldName] = ports;
            }
            ports.Add(p);

            return p;
        }

        protected virtual PortView CreatePortView(Direction direction, FieldInfo fieldInfo, PortData portData, BaseEdgeConnectorListener listener)
            => PortView.CreatePortView(direction, fieldInfo, portData, listener);

        public void InsertPort(PortView portView, int index)
        {
            if (portView.direction == Direction.Input)
            {
                if (portView.portData.vertical)
                    _topPortContainer.Insert(index, portView);
                else
                    inputContainer.Insert(index, portView);
            }
            else
            {
                if (portView.portData.vertical)
                    _bottomPortContainer.Insert(index, portView);
                else
                    outputContainer.Insert(index, portView);
            }
        }

        public void RemovePort(PortView p)
        {
            // Remove all connected edges:
            var edgesCopy = p.GetEdges().ToList();
            foreach (var e in edgesCopy)
                owner.Disconnect(e, refreshPorts: false);

            if (p.direction == Direction.Input)
            {
                if (inputPortViews.Remove(p))
                    p.RemoveFromHierarchy();
            }
            else
            {
                if (outputPortViews.Remove(p))
                    p.RemoveFromHierarchy();
            }

            List<PortView> ports;
            _portsPerFieldName.TryGetValue(p.fieldName, out ports);
            ports.Remove(p);
        }
        
        private void SetValuesForSelectedNodes()
        {
            _selectedNodes = new List<Node>();
            owner.nodes.ForEach(node =>
            {
                if(node.selected) _selectedNodes.Add(node);
            });

            if(_selectedNodes.Count < 2) return; //    No need for any of the calculations below

            _selectedNodesFarLeft   = int.MinValue;
            _selectedNodesFarRight  = int.MinValue;
            _selectedNodesFarTop    = int.MinValue;
            _selectedNodesFarBottom = int.MinValue;

            _selectedNodesNearLeft   = int.MaxValue;
            _selectedNodesNearRight  = int.MaxValue;
            _selectedNodesNearTop    = int.MaxValue;
            _selectedNodesNearBottom = int.MaxValue;

            foreach(var selectedNode in _selectedNodes)
            {
                var nodeStyle  = selectedNode.style;
                var nodeWidth  = selectedNode.localBound.size.x;
                var nodeHeight = selectedNode.localBound.size.y;

                if(nodeStyle.left.value.value > _selectedNodesFarLeft) _selectedNodesFarLeft                 = nodeStyle.left.value.value;
                if(nodeStyle.left.value.value + nodeWidth > _selectedNodesFarRight) _selectedNodesFarRight   = nodeStyle.left.value.value + nodeWidth;
                if(nodeStyle.top.value.value > _selectedNodesFarTop) _selectedNodesFarTop                    = nodeStyle.top.value.value;
                if(nodeStyle.top.value.value + nodeHeight > _selectedNodesFarBottom) _selectedNodesFarBottom = nodeStyle.top.value.value + nodeHeight;

                if(nodeStyle.left.value.value < _selectedNodesNearLeft) _selectedNodesNearLeft                 = nodeStyle.left.value.value;
                if(nodeStyle.left.value.value + nodeWidth < _selectedNodesNearRight) _selectedNodesNearRight   = nodeStyle.left.value.value + nodeWidth;
                if(nodeStyle.top.value.value < _selectedNodesNearTop) _selectedNodesNearTop                    = nodeStyle.top.value.value;
                if(nodeStyle.top.value.value + nodeHeight < _selectedNodesNearBottom) _selectedNodesNearBottom = nodeStyle.top.value.value + nodeHeight;
            }

            _selectedNodesAvgHorizontal = (_selectedNodesNearLeft + _selectedNodesFarRight) / 2f;
            _selectedNodesAvgVertical   = (_selectedNodesNearTop + _selectedNodesFarBottom) / 2f;
        }

        public static Rect GetNodeRect(Node node, float left = int.MaxValue, float top = int.MaxValue)
        {
            return new Rect(
                new Vector2(left != int.MaxValue ? left : node.style.left.value.value, top != int.MaxValue ? top : node.style.top.value.value),
                new Vector2(node.style.width.value.value, node.style.height.value.value)
            );
        }

        public void AlignToLeft()
        {
            SetValuesForSelectedNodes();
            if(_selectedNodes.Count < 2) return;

            foreach(var selectedNode in _selectedNodes)
            {
                selectedNode.SetPosition(GetNodeRect(selectedNode, _selectedNodesNearLeft));
            }
        }

        public void AlignToCenter()
        {
            SetValuesForSelectedNodes();
            if(_selectedNodes.Count < 2) return;

            foreach(var selectedNode in _selectedNodes)
            {
                selectedNode.SetPosition(GetNodeRect(selectedNode, _selectedNodesAvgHorizontal - selectedNode.localBound.size.x / 2f));
            }
        }

        public void AlignToRight()
        {
            SetValuesForSelectedNodes();
            if(_selectedNodes.Count < 2) return;

            foreach(var selectedNode in _selectedNodes)
            {
                selectedNode.SetPosition(GetNodeRect(selectedNode, _selectedNodesFarRight - selectedNode.localBound.size.x));
            }
        }

        public void AlignToTop()
        {
            SetValuesForSelectedNodes();
            if(_selectedNodes.Count < 2) return;

            foreach(var selectedNode in _selectedNodes)
            {
                selectedNode.SetPosition(GetNodeRect(selectedNode, top: _selectedNodesNearTop));
            }
        }

        public void AlignToMiddle()
        {
            SetValuesForSelectedNodes();
            if(_selectedNodes.Count < 2) return;

            foreach(var selectedNode in _selectedNodes)
            {
                selectedNode.SetPosition(GetNodeRect(selectedNode, top: _selectedNodesAvgVertical - selectedNode.localBound.size.y / 2f));
            }
        }

        public void AlignToBottom()
        {
            SetValuesForSelectedNodes();
            if(_selectedNodes.Count < 2) return;

            foreach(var selectedNode in _selectedNodes)
            {
                selectedNode.SetPosition(GetNodeRect(selectedNode, top: _selectedNodesFarBottom - selectedNode.localBound.size.y));
            }
        }

        public void OpenNodeViewScript()
        {
            var script = NodeProvider.GetNodeViewScript(GetType());

            if (script != null)
                AssetDatabase.OpenAsset(script.GetInstanceID(), 0, 0);
        }

        public void OpenNodeScript()
        {
            var script = NodeProvider.GetNodeScript(nodeTarget.GetType());

            if (script != null)
                AssetDatabase.OpenAsset(script.GetInstanceID(), 0, 0);
        }

        public void ToggleDebug()
        {
            nodeTarget.debug = !nodeTarget.debug;
            UpdateDebugView();
        }

        public void UpdateDebugView()
        {
            if (nodeTarget.debug)
                mainContainer.Add(_debugContainer);
            else
                mainContainer.Remove(_debugContainer);
        }

        public void AddMessageView(string message, Texture icon, Color color)
            => AddBadge(new NodeBadgeView(message, icon, color));

        public void AddMessageView(string message, NodeMessageType messageType)
        {
            IconBadge    badge = null;
            switch (messageType)
            {
                case NodeMessageType.Warning:
                    badge = new NodeBadgeView(message, EditorGUIUtility.IconContent("Collab.Warning").image, Color.yellow);
                    break ;
                case NodeMessageType.Error:    
                    badge = IconBadge.CreateError(message);
                    break ;
                case NodeMessageType.Info:
                    badge = IconBadge.CreateComment(message);
                    break ;
                default:
                case NodeMessageType.None:
                    badge = new NodeBadgeView(message, null, Color.grey);
                    break ;
            }
            
            AddBadge(badge);
        }

        void AddBadge(IconBadge badge)
        {
            Add(badge);
            _badges.Add(badge);
            badge.AttachTo(topContainer, SpriteAlignment.TopRight);
        }

        void RemoveBadge(Func<IconBadge, bool> callback)
        {
            _badges.RemoveAll(b => {
                if (callback(b))
                {
                    b.Detach();
                    b.RemoveFromHierarchy();
                    return true;
                }
                return false;
            });
        }

        public void RemoveMessageViewContains(string message) => RemoveBadge(b => b.badgeText.Contains(message));
        
        public void RemoveMessageView(string message) => RemoveBadge(b => b.badgeText == message);

        public void Highlight()
        {
            AddToClassList("Highlight");
        }

        public void UnHighlight()
        {
            RemoveFromClassList("Highlight");
        }

        #endregion

        #region Callbacks & Overrides

        protected virtual void ComputeOrderUpdatedCallback(IEnumerable<BaseNode> computedNodes)
        {
            //Update debug compute order
            _computeOrderLabel.text = "Compute order: " + nodeTarget.computeOrder;
        }

        public virtual void Enable(bool fromInspector = false) => DrawDefaultInspector(fromInspector);
        public virtual void Enable() => DrawDefaultInspector(false);

        public virtual void Disable() 
        {
            if (_selectionBorderSyncSchedule != null)
            {
                _selectionBorderSyncSchedule.Pause();
                _selectionBorderSyncSchedule = null;
            }
        }

        Dictionary<string, List<(object value, VisualElement target)>> _visibleConditions = new Dictionary<string, List<(object value, VisualElement target)>>();
        Dictionary<VisualElement, List<(string fieldName, object value)>> _elementConditions = new Dictionary<VisualElement, List<(string fieldName, object value)>>();
        Dictionary<string, VisualElement>  _hideElementIfConnected = new Dictionary<string, VisualElement>();
        Dictionary<FieldInfo, List<VisualElement>> _fieldControlsMap = new Dictionary<FieldInfo, List<VisualElement>>();

        protected void AddInputContainer()
        {
            _inputContainerElement = new VisualElement {name = "input-container"};
            mainContainer.parent.Add(_inputContainerElement);
            _inputContainerElement.SendToBack();
            _inputContainerElement.pickingMode = PickingMode.Ignore;
        }

        protected virtual void DrawDefaultInspector(bool fromInspector = false)
        {
            var fields = nodeTarget.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                // Filter fields from the BaseNode type since we are only interested in user-defined fields
                // (better than BindingFlags.DeclaredOnly because we keep any inherited user-defined fields) 
                .Where(f => f.DeclaringType != typeof(BaseNode));

            fields = nodeTarget.OverrideFieldOrder(fields).Reverse();
            foreach (var field in fields)
            {
                //skip if the field is a node setting
                if(field.GetCustomAttribute(typeof(SettingAttribute)) != null)
                {
                    _hasSettings = true;
                    continue;
                }

                //skip if the field is not serializable
                bool serializeField = field.GetCustomAttribute(typeof(SerializeField)) != null;
                if((!field.IsPublic && !serializeField) || field.IsNotSerialized)
                {
                    AddEmptyField(field, fromInspector);
                    continue;
                }

                //skip if the field is an input/output and not marked as SerializedField
                bool hasInputAttribute          = field.GetCustomAttribute(typeof(InputAttribute)) != null;
                bool hasInputOrOutputAttribute  = hasInputAttribute || field.GetCustomAttribute(typeof(OutputAttribute)) != null;
                bool showAsDrawer               = !fromInspector && field.GetCustomAttribute(typeof(ShowAsDrawer)) != null;
                if (!serializeField && hasInputOrOutputAttribute && !showAsDrawer)
                {
                    AddEmptyField(field, fromInspector);
                    continue;
                }

                //skip if marked with NonSerialized or HideInInspector
                if (field.GetCustomAttribute(typeof(System.NonSerializedAttribute)) != null || field.GetCustomAttribute(typeof(HideInInspector)) != null)
                {
                    AddEmptyField(field, fromInspector);
                    continue;
                }

                // Hide the field if we want to display in in the inspector
                var showInInspector = field.GetCustomAttribute<ShowInInspector>();
                if (!serializeField && showInInspector != null && !showInInspector.showInNode && !fromInspector)
                {
                    AddEmptyField(field, fromInspector);
                    continue;
                }

                var showInputDrawer = field.GetCustomAttribute(typeof(InputAttribute)) != null && field.GetCustomAttribute(typeof(SerializeField)) != null;
                showInputDrawer |= field.GetCustomAttribute(typeof(InputAttribute)) != null && field.GetCustomAttribute(typeof(ShowAsDrawer)) != null;
                showInputDrawer &= !fromInspector; // We can't show a drawer in the inspector
                showInputDrawer &= !typeof(IList).IsAssignableFrom(field.FieldType);

                string displayName = ObjectNames.NicifyVariableName(field.Name);

                var inspectorNameAttribute = field.GetCustomAttribute<InspectorNameAttribute>();
                if (inspectorNameAttribute != null)
                    displayName = inspectorNameAttribute.displayName;

                var showObjectDrawer = field.GetCustomAttribute(typeof(ShowAsObjectDrawer)) != null;
                var elem = AddControlField(field, displayName, showInputDrawer, showObjectDrawer);
                if (hasInputAttribute)
                {
                    _hideElementIfConnected[field.Name] = elem;

                    // Hide the field right away if there is already a connection:
                    if (_portsPerFieldName.TryGetValue(field.Name, out var pvs))
                        if (pvs.Any(pv => pv.GetEdges().Count > 0))
                            elem.style.display = DisplayStyle.None;
                }
            }
        }

        protected virtual void SetNodeColor(Color color)
        {
            titleContainer.style.borderBottomColor = new StyleColor(color);
            titleContainer.style.borderBottomWidth = new StyleFloat(color.a > 0 ? 5f : 0f);
        }
        
        private void AddEmptyField(FieldInfo field, bool fromInspector)
        {
            if (field.GetCustomAttribute(typeof(InputAttribute)) == null || fromInspector)
                return;

            if (field.GetCustomAttribute<VerticalAttribute>() != null)
                return;
            
            var box = new VisualElement {name = field.Name};
            box.AddToClassList("port-input-element");
            box.AddToClassList("empty");
            _inputContainerElement.Add(box);
        }

        protected void UpdateFieldVisibility(string fieldName, object newValue)
        {
            if (newValue == null)
                return;

            if (_visibleConditions.TryGetValue(fieldName, out var list))
            {
                foreach (var elem in list)
                {
                    // 对于每个受影响的元素，重新评估所有条件
                    if (_elementConditions.TryGetValue(elem.target, out var conditions))
                    {
                        UpdateFieldVisibilityForElement(elem.target, conditions);
                    }
                }
            }
        }

        private void UpdateFieldVisibilityForElement(VisualElement target, List<(string fieldName, object value)> conditions)
        {
            bool shouldShow = true;
            
            foreach (var condition in conditions)
            {
                var conditionField = nodeTarget.GetType().GetField(condition.fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (conditionField == null)
                    continue;
                    
                var fieldValue = conditionField.GetValue(nodeTarget);
                if (fieldValue == null || !fieldValue.Equals(condition.value))
                {
                    shouldShow = false;
                    break;
                }
            }
            
            target.style.display = shouldShow ? DisplayStyle.Flex : DisplayStyle.None;
        }

        void UpdateOtherFieldValueSpecific<T>(FieldInfo field, object newValue)
        {
            foreach (var inputField in _fieldControlsMap[field])
            {
                var notify = inputField as INotifyValueChanged<T>;
                if (notify != null)
                    notify.SetValueWithoutNotify((T)newValue);
            }
        }

        static MethodInfo specificUpdateOtherFieldValue = typeof(BaseNodeView).GetMethod(nameof(UpdateOtherFieldValueSpecific), BindingFlags.NonPublic | BindingFlags.Instance);
        void UpdateOtherFieldValue(FieldInfo info, object newValue)
        {
            // Warning: Keep in sync with FieldFactory CreateField
            var fieldType = info.FieldType.IsSubclassOf(typeof(UnityEngine.Object)) ? typeof(UnityEngine.Object) : info.FieldType;
            var genericUpdate = specificUpdateOtherFieldValue.MakeGenericMethod(fieldType);

            genericUpdate.Invoke(this, new object[]{info, newValue});
        }

        object GetInputFieldValueSpecific<T>(FieldInfo field)
        {
            if (_fieldControlsMap.TryGetValue(field, out var list))
            {
                foreach (var inputField in list)
                {
                    if (inputField is INotifyValueChanged<T> notify)
                        return notify.value;
                }
            }
            return null;
        }

        static MethodInfo specificGetValue = typeof(BaseNodeView).GetMethod(nameof(GetInputFieldValueSpecific), BindingFlags.NonPublic | BindingFlags.Instance);
        object GetInputFieldValue(FieldInfo info)
        {
            // Warning: Keep in sync with FieldFactory CreateField
            var fieldType = info.FieldType.IsSubclassOf(typeof(UnityEngine.Object)) ? typeof(UnityEngine.Object) : info.FieldType;
            var genericUpdate = specificGetValue.MakeGenericMethod(fieldType);

            return genericUpdate.Invoke(this, new object[]{info});
        }


        static Regex s_replaceNodeIndexPropertyPath = new Regex(@"(^nodes.Array.data\[)(\d+)(\])");
        internal void SyncSerializedPropertyPathes()
        {
            // Safety checks
            if (owner?.graph == null || owner.serializedGraph == null || nodeTarget == null)
                return;
                
            int nodeIndex = owner.graph.nodes.FindIndex(n => n == nodeTarget);

            // If the node is not found, then it means that it has been deleted from serialized data.
            if (nodeIndex == -1)
                return;

            var nodeIndexString = nodeIndex.ToString();
            foreach (var propertyField in this.Query<PropertyField>().ToList())
            {
                // Skip if bindingPath is null or empty
                if (string.IsNullOrEmpty(propertyField.bindingPath))
                    continue;
                    
                propertyField.Unbind();
                // The property path look like this: nodes.Array.data[x].fieldName
                // And we want to update the value of x with the new node index:
                propertyField.bindingPath = s_replaceNodeIndexPropertyPath.Replace(propertyField.bindingPath, m => m.Groups[1].Value + nodeIndexString + m.Groups[3].Value);
                propertyField.Bind(owner.serializedGraph);
            }
        }

        protected VisualElement AddControlField(string fieldName, string label = null, bool showInputDrawer = false, bool showAsObjectDrawer = false, Action valueChangedCallback = null)
            => AddControlField(nodeTarget.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), label, showInputDrawer, showAsObjectDrawer, valueChangedCallback);

        protected VisualElement AddControlField(FieldInfo field, string label = null, bool showInputDrawer = false, bool showAsObjectDrawer = false, Action valueChangedCallback = null)
        {
            if (field == null)
                return null;

            VisualElement visualElement = null;

            showAsObjectDrawer &= typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType);
            showAsObjectDrawer &= !typeof(IList).IsAssignableFrom(field.FieldType);
            if (!showAsObjectDrawer)
            {
                var fieldElement = new PropertyField(FindSerializedProperty(field.Name), showInputDrawer ? "" : label);
                fieldElement.Bind(owner.serializedGraph);

                if (typeof(IList).IsAssignableFrom(field.FieldType))
                    EnableSyncSelectionBorderHeight();

                fieldElement.RegisterValueChangeCallback(e =>
                {
                    UpdateFieldVisibility(field.Name, field.GetValue(nodeTarget));
                    valueChangedCallback?.Invoke();
                    nodeTarget.OnValidate();
                    NotifyNodeChanged();
                });

                // Disallow picking scene objects when the graph is not linked to a scene
                if (fieldElement != null && !owner.graph.IsLinkedToScene())
                {
                    var objectField = fieldElement.Q<ObjectField>();
                    if (objectField != null)
                        objectField.allowSceneObjects = false;
                }

                visualElement = fieldElement;
            }
            else
            {
                var fieldElement = new ObjectField
                {
                    objectType = field.FieldType,
                    allowSceneObjects = false,
                    value = (UnityEngine.Object)field.GetValue(nodeTarget),
                    label = label
                };

                fieldElement.RegisterValueChangedCallback(v =>
                {
                    field.SetValue(nodeTarget, fieldElement.value);
                    UpdateFieldVisibility(field.Name, field.GetValue(nodeTarget));
                    nodeTarget.OnValidate();
                    NotifyNodeChanged();
                });

                visualElement = fieldElement;
            }

            if (!_fieldControlsMap.TryGetValue(field, out var inputFieldList))
                inputFieldList = _fieldControlsMap[field] = new List<VisualElement>();

            inputFieldList.Add(visualElement);

            if(visualElement != null)
            {
                if (showInputDrawer)
                {
                    var box = new VisualElement {name = field.Name};
                    box.AddToClassList("port-input-element");
                    box.Add(visualElement);
                    _inputContainerElement.Add(box);
                }
                else
                    controlsContainer.Add(visualElement);

                visualElement.name = field.Name;
            }
            else
            {
                // Make sure we create an empty placeholder if FieldFactory can not provide a drawer
                if (showInputDrawer) 
                    AddEmptyField(field, false);
            }

            var visibleConditions = field.GetCustomAttributes(typeof(VisibleIf), false) as VisibleIf[];
            if (visibleConditions != null && visibleConditions.Length > 0)
            {
                List<(string fieldName, object value)> conditions = new List<(string fieldName, object value)>();
                
                foreach (var visibleCondition in visibleConditions)
                {
                    // Check if target field exists:
                    var conditionField = nodeTarget.GetType().GetField(visibleCondition.fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (conditionField == null)
                    {
                        Debug.LogError($"[VisibleIf] Field {visibleCondition.fieldName} does not exists in node {nodeTarget.GetType()}");
                        continue;
                    }
                    
                    conditions.Add((visibleCondition.fieldName, visibleCondition.value));
                    
                    _visibleConditions.TryGetValue(visibleCondition.fieldName, out var list);
                    if (list == null)
                        list = _visibleConditions[visibleCondition.fieldName] = new List<(object value, VisualElement target)>();
                    list.Add((visibleCondition.value, visualElement));
                }
                
                if (conditions.Count > 0)
                {
                    _elementConditions[visualElement] = conditions;
                    UpdateFieldVisibilityForElement(visualElement, conditions);
                }
            }

            return visualElement;
        }

        protected SerializedProperty FindSerializedProperty(string fieldName)
        {
            int i = owner.graph.nodes.FindIndex(n => n == nodeTarget);
            return owner.serializedGraph.FindProperty("nodes").GetArrayElementAtIndex(i).FindPropertyRelative(fieldName);
        }

        void UpdateFieldValues()
        {
            foreach (var kp in _fieldControlsMap)
                UpdateOtherFieldValue(kp.Key, kp.Key.GetValue(nodeTarget));
        }
        
        void AddSettingField(FieldInfo field)
        {
            if (field == null)
                return;

            var label = field.GetCustomAttribute<SettingAttribute>().name;

            var element = new PropertyField(FindSerializedProperty(field.Name));
            element.Bind(owner.serializedGraph);

            if (element != null)
            {
                _settingsContainer.Add(element);
                element.name = field.Name;
            }
        }

        internal void OnPortConnected(PortView port)
        {
            if(port.direction == Direction.Input && _inputContainerElement?.Q(port.fieldName) != null)
                _inputContainerElement.Q(port.fieldName).AddToClassList("empty");
            
            if (_hideElementIfConnected.TryGetValue(port.fieldName, out var elem))
                elem.style.display = DisplayStyle.None;

            _onPortConnected?.Invoke(port);
        }

        internal void OnPortDisconnected(PortView port)
        {
            if (port.direction == Direction.Input && _inputContainerElement?.Q(port.fieldName) != null)
            {
                _inputContainerElement.Q(port.fieldName).RemoveFromClassList("empty");

                if (nodeTarget.nodeFields.TryGetValue(port.fieldName, out var fieldInfo))
                {
                    var valueBeforeConnection = GetInputFieldValue(fieldInfo.info);

                    if (valueBeforeConnection != null)
                    {
                        fieldInfo.info.SetValue(nodeTarget, valueBeforeConnection);
                    }
                }
            }
            
            if (_hideElementIfConnected.TryGetValue(port.fieldName, out var elem))
                elem.style.display = DisplayStyle.Flex;

            _onPortDisconnected?.Invoke(port);
        }

        // TODO: a function to force to reload the custom behavior ports (if we want to do a button to add ports for example)

        public virtual void OnRemoved() {}
        public virtual void OnCreated() {}

        public override void SetPosition(Rect newPos)
        {
            if (initializing || !nodeTarget.isLocked)
            {
                base.SetPosition(newPos);

                if (!initializing)
                    owner.RegisterCompleteObjectUndo("Moved graph node");

                nodeTarget.position = newPos;
                initializing = false;
            }
        }

        public override bool expanded
        {
            get { return base.expanded; }
            set
            {
                base.expanded = value;
                nodeTarget.expanded = value;
            }
        }

        public void ChangeLockStatus()
        {
            nodeTarget.nodeLock ^= true;
        }

        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            BuildAlignMenu(evt);
            evt.menu.AppendAction("Open Node Script", (e) => OpenNodeScript(), OpenNodeScriptStatus);
            evt.menu.AppendAction("Open Node View Script", (e) => OpenNodeViewScript(), OpenNodeViewScriptStatus);
            evt.menu.AppendAction("Debug", (e) => ToggleDebug(), DebugStatus);
            if (nodeTarget.unlockable)
                evt.menu.AppendAction((nodeTarget.isLocked ? "Unlock" : "Lock"), (e) => ChangeLockStatus(), LockStatus);
        }

        protected void BuildAlignMenu(ContextualMenuPopulateEvent evt)
        {
            evt.menu.AppendAction("Align/To Left", (e) => AlignToLeft());
            evt.menu.AppendAction("Align/To Center", (e) => AlignToCenter());
            evt.menu.AppendAction("Align/To Right", (e) => AlignToRight());
            evt.menu.AppendSeparator("Align/");
            evt.menu.AppendAction("Align/To Top", (e) => AlignToTop());
            evt.menu.AppendAction("Align/To Middle", (e) => AlignToMiddle());
            evt.menu.AppendAction("Align/To Bottom", (e) => AlignToBottom());
            evt.menu.AppendSeparator();
        }

        Status LockStatus(DropdownMenuAction action)
        {
            return Status.Normal;
        }

        Status DebugStatus(DropdownMenuAction action)
        {
            if (nodeTarget.debug)
                return Status.Checked;
            return Status.Normal;
        }

        Status OpenNodeScriptStatus(DropdownMenuAction action)
        {
            if (NodeProvider.GetNodeScript(nodeTarget.GetType()) != null)
                return Status.Normal;
            return Status.Disabled;
        }

        Status OpenNodeViewScriptStatus(DropdownMenuAction action)
        {
            if (NodeProvider.GetNodeViewScript(GetType()) != null)
                return Status.Normal;
            return Status.Disabled;
        }

        IEnumerable<PortView> SyncPortCounts(IEnumerable<NodePort > ports, IEnumerable<PortView> portViews)
        {
            var listener = owner.connectorListener;
            var portViewList = portViews.ToList();

            // Maybe not good to remove ports as edges are still connected :/
            foreach (var pv in portViews.ToList())
            {
                // If the port have disappeared from the node data, we remove the view:
                // We can use the identifier here because this function will only be called when there is a custom port behavior
                if (!ports.Any(p => p.portData.identifier == pv.portData.identifier))
                {
                    RemovePort(pv);
                    portViewList.Remove(pv);
                }
            }

            foreach (var p in ports)
            {
                // Add missing port views
                if (!portViews.Any(pv => p.portData.identifier == pv.portData.identifier))
                {
                    Direction portDirection = nodeTarget.IsFieldInput(p.fieldName) ? Direction.Input : Direction.Output;
                    var pv = AddPort(p.fieldInfo, portDirection, listener, p.portData);
                    portViewList.Add(pv);
                }
            }

            return portViewList;
        }

        void SyncPortOrder(IEnumerable<NodePort> ports, IEnumerable<PortView> portViews)
        {
            var portViewList = portViews.ToList();
            var portsList = ports.ToList();

            // Re-order the port views to match the ports order in case a custom behavior re-ordered the ports
            for (int i = 0; i < portsList.Count; i++)
            {
                var id = portsList[i].portData.identifier;

                var pv = portViewList.FirstOrDefault(p => p.portData.identifier == id);
                if (pv != null)
                    InsertPort(pv, i);
            }
        }

        public virtual new bool RefreshPorts()
        {
            // If a port behavior was attached to one port, then
            // the port count might have been updated by the node
            // so we have to refresh the list of port views.
            UpdatePortViewWithPorts(nodeTarget.inputPorts, inputPortViews);
            UpdatePortViewWithPorts(nodeTarget.outputPorts, outputPortViews);

            void UpdatePortViewWithPorts(NodePortContainer ports, List<PortView> portViews)
            {
                if (ports.Count == 0 && portViews.Count == 0) // Nothing to update
                    return;

                // When there is no current portviews, we can't zip the list so we just add all
                if (portViews.Count == 0)
                    SyncPortCounts(ports, new PortView[]{});
                else if (ports.Count == 0) // Same when there is no ports
                    SyncPortCounts(new NodePort[]{}, portViews);
                else if (portViews.Count != ports.Count)
                    SyncPortCounts(ports, portViews);
                else
                {
                    var p = ports.GroupBy(n => n.fieldName);
                    var pv = portViews.GroupBy(v => v.fieldName);
                    p.Zip(pv, (portPerFieldName, portViewPerFieldName) => {
                        IEnumerable<PortView> portViewsList = portViewPerFieldName;
                        if (portPerFieldName.Count() != portViewPerFieldName.Count())
                            portViewsList = SyncPortCounts(portPerFieldName, portViewPerFieldName);
                        SyncPortOrder(portPerFieldName, portViewsList);
                        // We don't care about the result, we just iterate over port and portView
                        return "";
                    }).ToList();
                }

                // Here we're sure that we have the same amount of port and portView
                // so we can update the view with the new port data (if the name of a port have been changed for example)

                for (int i = 0; i < portViews.Count; i++)
                    portViews[i].UpdatePortView(ports[i].portData);
            }

            return base.RefreshPorts();
        }

        public void ForceUpdatePorts()
        {
            nodeTarget.UpdateAllPorts();

            RefreshPorts();
        }

        void UpdatePortsForField(string fieldName)
        {
            // TODO: actual code
            RefreshPorts();
        }

        protected virtual VisualElement CreateSettingsView() => new Label("Settings") {name = "header"};

        /// <summary>
        /// Send an event to the graph telling that the content of this node have changed
        /// </summary>
        public void NotifyNodeChanged() => owner.graph.NotifyNodeChanged(nodeTarget);

        #endregion
    }
}