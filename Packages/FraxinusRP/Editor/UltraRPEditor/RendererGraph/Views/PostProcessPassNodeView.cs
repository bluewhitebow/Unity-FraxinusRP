using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using UnityEditor.Experimental.GraphView;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [NodeCustomEditor(typeof(PostProcessPass))]
    public class PostProcessPassNodeView : ScriptableRenderPassNodeView
    {
        #region Fields
        
        protected PostProcessPass _passNode;
        protected ScriptableRendererGraph _graph;
        
        private VisualElement _postPassesContainer;
        private VisualElement _postPassListContainer;
        
        private VisualElement _uberEffectsContainer;
        
        private int _draggedIndex = -1;
        private int _dragStartIndex = -1;
        private VisualElement _draggedElement;
        private int _targetIndex = -1;
        
        #endregion

        #region Color Constants
        
        private static readonly Color s_containerBackgroundColor = new Color32(34, 42, 53, 255);
        private static readonly Color s_containerBorderColor = new Color32(65, 77, 94, 255);
        private static readonly Color c_itemBackgroundColor = new Color32(46, 56, 69, 255);
        private static readonly Color c_itemDragColor = new Color32(70, 75, 77, 255);
        private static readonly Color c_textColor = new Color32(218, 229, 240, 255);
        private static readonly Color s_dragHandleColor = new Color32(147, 164, 184, 255);
        
        #endregion

        protected override bool _showActiveToggle => false;

        #region Lifecycle

        public override void Enable(bool fromInspector)
        {
            capabilities &= ~Capabilities.Deletable;
            _passNode = nodeTarget as PostProcessPass;
            _graph = owner.graph as ScriptableRendererGraph;

            base.Enable(fromInspector);

            InitPostPassesDisplay();
            InitUberEffectsDisplay();
        }

        #endregion

        #region UI Initialization

        private void InitPostPassesDisplay()
        {
            if (_passNode == null || _postPassesContainer != null)
                return;

            _postPassesContainer = new VisualElement
            {
                name = "PostPassesContainer"
            };
            
            SetupContainerStyle(_postPassesContainer);
            Add(_postPassesContainer);
            
            UpdatePostPassesDisplay();
        }

        private void SetupContainerStyle(VisualElement container)
        {
            container.AddToClassList("fraxinus-post-list");
            var style = container.style;
            
            style.marginTop = 4;
            style.marginBottom = 4;
            style.paddingTop = 6;
            style.paddingBottom = 6;
            style.paddingLeft = 6;
            style.paddingRight = 6;
            
            style.backgroundColor = s_containerBackgroundColor;
            
            style.borderTopLeftRadius = 8;
            style.borderTopRightRadius = 8;
            style.borderBottomLeftRadius = 8;
            style.borderBottomRightRadius = 8;
            
            style.borderTopWidth = 1;
            style.borderBottomWidth = 1;
            style.borderLeftWidth = 1;
            style.borderRightWidth = 1;
            
            style.borderTopColor = s_containerBorderColor;
            style.borderBottomColor = s_containerBorderColor;
            style.borderLeftColor = s_containerBorderColor;
            style.borderRightColor = s_containerBorderColor;
        }

        #endregion

        #region UI Update

        private void UpdatePostPassesDisplay()
        {
            if (_postPassesContainer == null || _passNode == null)
                return;

            _postPassesContainer.Clear();

            var postPassEntries = _passNode.postPassEntries;
            if (postPassEntries == null || postPassEntries.Count == 0)
            {
                _postPassesContainer.style.display = DisplayStyle.None;
                return;
            }

            _postPassesContainer.style.display = DisplayStyle.Flex;

            var foldout = new Foldout
            {
                text = "Post Passes",
                value = true
            };
            foldout.style.fontSize = 11;
            foldout.style.marginLeft = 4;
            foldout.style.marginRight = 4;

            _postPassListContainer = new VisualElement();
            _postPassListContainer.style.flexDirection = FlexDirection.Column;

            for (int i = 0; i < postPassEntries.Count; i++)
            {
                var entry = postPassEntries[i];
                var itemElement = CreatePostPassItem(entry, i);
                _postPassListContainer.Add(itemElement);
            }

            foldout.Add(_postPassListContainer);
            _postPassesContainer.Add(foldout);
        }

        private void InitUberEffectsDisplay()
        {
            if (_passNode == null || _uberEffectsContainer != null)
                return;

            _uberEffectsContainer = new VisualElement
            {
                name = "UberEffectsContainer"
            };
            
            SetupContainerStyle(_uberEffectsContainer);
            Add(_uberEffectsContainer);
            
            UpdateUberEffectsDisplay();
        }

        private void UpdateUberEffectsDisplay()
        {
            if (_uberEffectsContainer == null || _passNode == null)
                return;

            _uberEffectsContainer.Clear();

            var uberEffects = _passNode.uberEffectEntries;
            if (uberEffects == null || uberEffects.Count == 0)
            {
                _uberEffectsContainer.style.display = DisplayStyle.None;
                return;
            }

            _uberEffectsContainer.style.display = DisplayStyle.Flex;

            var foldout = new Foldout
            {
                text = "Uber Post Effects",
                value = true
            };
            foldout.style.fontSize = 11;
            foldout.style.marginLeft = 4;
            foldout.style.marginRight = 4;

            var listContainer = new VisualElement();
            listContainer.style.flexDirection = FlexDirection.Column;

            for (int i = 0; i < uberEffects.Count; i++)
            {
                var entry = uberEffects[i];
                var itemElement = CreateUberEffectItem(entry);
                listContainer.Add(itemElement);
            }

            foldout.Add(listContainer);
            _uberEffectsContainer.Add(foldout);
        }

        private VisualElement CreateUberEffectItem(PostProcessPass.UberEffectEntry entry)
        {
            var itemContainer = new VisualElement();
            itemContainer.AddToClassList("fraxinus-post-item");
            
            var style = itemContainer.style;
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.paddingTop = 3;
            style.paddingBottom = 3;
            style.paddingLeft = 6;
            style.paddingRight = 6;
            style.marginBottom = 2;
            
            style.backgroundColor = c_itemBackgroundColor;
            style.borderTopLeftRadius = 4;
            style.borderTopRightRadius = 4;
            style.borderBottomLeftRadius = 4;
            style.borderBottomRightRadius = 4;

            // Toggle
            var toggle = new Toggle
            {
                value = entry.enabled
            };
            toggle.style.flexGrow = 0;
            toggle.style.marginRight = 6;
            toggle.style.marginLeft = 2;
            
            toggle.RegisterValueChangedCallback(evt =>
            {
                owner.RegisterCompleteObjectUndo("Toggle Uber Effect");
                entry.enabled = evt.newValue;
                itemContainer.style.opacity = evt.newValue ? 1.0f : 0.5f;
                owner.graph.NotifyNodeChanged(_passNode);
            });

            // Label
            var label = new Label(entry.displayName);
            label.style.flexGrow = 1;
            label.style.fontSize = 11;
            label.style.color = c_textColor;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.paddingLeft = 2;

            itemContainer.Add(toggle);
            itemContainer.Add(label);

            // Set initial opacity based on enabled state
            if (!entry.enabled)
                itemContainer.style.opacity = 0.5f;

            return itemContainer;
        }

        #endregion

        #region Item Creation

        private VisualElement CreatePostPassItem(PostProcessPass.PostPassEntry entry, int index)
        {
            var itemContainer = new VisualElement
            {
                name = $"PostPassItem_{index}"
            };
            
            SetupItemStyle(itemContainer);
            
            itemContainer.Add(CreateDragHandle(itemContainer, index));
            itemContainer.Add(CreateToggle(entry, itemContainer));
            itemContainer.Add(CreateNameLabel(entry));

            if (!entry.enabled)
                itemContainer.style.opacity = 0.5f;

            return itemContainer;
        }

        private void SetupItemStyle(VisualElement item)
        {
            item.AddToClassList("fraxinus-post-item");
            var style = item.style;
            
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.marginBottom = 3;
            style.paddingLeft = 6;
            style.paddingRight = 6;
            style.paddingTop = 4;
            style.paddingBottom = 4;
            
            style.backgroundColor = c_itemBackgroundColor;
            
            style.borderTopLeftRadius = 6;
            style.borderTopRightRadius = 6;
            style.borderBottomLeftRadius = 6;
            style.borderBottomRightRadius = 6;
        }

        private Label CreateDragHandle(VisualElement itemContainer, int index)
        {
            var dragHandle = new Label("≡");
            dragHandle.style.fontSize = 14;
            dragHandle.style.color = s_dragHandleColor;
            dragHandle.style.marginRight = 4;
            dragHandle.style.unityTextAlign = TextAnchor.MiddleCenter;
            dragHandle.style.width = 16;
            dragHandle.AddToClassList("drag-handle");
            
            dragHandle.RegisterCallback<MouseDownEvent>(evt => OnDragStart(itemContainer, index, evt));
            
            return dragHandle;
        }

        private Toggle CreateToggle(PostProcessPass.PostPassEntry entry, VisualElement itemContainer)
        {
            var toggle = new Toggle
            {
                value = entry.enabled
            };
            toggle.style.marginRight = 4;
            toggle.RegisterValueChangedCallback(evt =>
            {
                owner.RegisterCompleteObjectUndo("Toggle Post Pass");
                entry.enabled = evt.newValue;
                itemContainer.style.opacity = evt.newValue ? 1.0f : 0.5f;
            });
            
            return toggle;
        }

        private Label CreateNameLabel(PostProcessPass.PostPassEntry entry)
        {
            var nameLabel = new Label(ObjectNames.NicifyVariableName(entry.passType.Name));
            nameLabel.style.flexGrow = 1;
            nameLabel.style.color = c_textColor;
            nameLabel.style.fontSize = 11;
            nameLabel.tooltip = $"Type: {entry.passType.FullName}\nDrag handle to reorder";
            
            return nameLabel;
        }

        #endregion

        #region Drag and Drop

        private void OnDragStart(VisualElement element, int index, MouseDownEvent evt)
        {
            if (evt.button != 0) return;

            _draggedIndex = index;
            _dragStartIndex = index;
            _draggedElement = element;
            _targetIndex = index;
            
            ApplyDragStyle(element);
            
            element.RegisterCallback<MouseMoveEvent>(OnDragMove);
            element.RegisterCallback<MouseUpEvent>(OnDragEnd);
            element.CaptureMouse();
            
            evt.StopPropagation();
        }

        private void OnDragMove(MouseMoveEvent evt)
        {
            if (_draggedIndex < 0 || _draggedElement == null) return;

            int newIndex = CalculateTargetIndex(evt.mousePosition);
            
            if (newIndex != _draggedIndex && newIndex >= 0)
            {
                ReorderEntry(_draggedIndex, newIndex);
                _draggedIndex = newIndex;
                _targetIndex = newIndex;
                
                UpdatePostPassesDisplay();
                RestoreDragState();
            }
        }

        private void OnDragEnd(MouseUpEvent evt)
        {
            if (_draggedElement == null) return;

            RemoveDragStyle(_draggedElement);
            
            _draggedElement.UnregisterCallback<MouseMoveEvent>(OnDragMove);
            _draggedElement.UnregisterCallback<MouseUpEvent>(OnDragEnd);
            _draggedElement.ReleaseMouse();
            
            if (_dragStartIndex != _draggedIndex)
                owner.RegisterCompleteObjectUndo("Reorder Post Pass");
            
            ResetDragState();
            
            evt.StopPropagation();
        }

        private int CalculateTargetIndex(Vector2 mousePosition)
        {
            var localPos = _postPassListContainer.WorldToLocal(mousePosition);
            
            for (int i = 0; i < _postPassListContainer.childCount; i++)
            {
                var child = _postPassListContainer[i];
                var childRect = child.layout;
                
                if (localPos.y < childRect.y + childRect.height / 2)
                    return i;
            }
            
            return _postPassListContainer.childCount - 1;
        }

        private void ReorderEntry(int fromIndex, int toIndex)
        {
            var entry = _passNode.postPassEntries[fromIndex];
            _passNode.postPassEntries.RemoveAt(fromIndex);
            _passNode.postPassEntries.Insert(toIndex, entry);
        }

        private void RestoreDragState()
        {
            _draggedElement = _postPassListContainer[_draggedIndex];
            ApplyDragStyle(_draggedElement);
            _draggedElement.RegisterCallback<MouseMoveEvent>(OnDragMove);
            _draggedElement.RegisterCallback<MouseUpEvent>(OnDragEnd);
            _draggedElement.CaptureMouse();
        }

        private void ApplyDragStyle(VisualElement element)
        {
            element.style.backgroundColor = c_itemDragColor;
            element.style.scale = new Scale(new Vector3(1.05f, 1.05f, 1.0f));
        }

        private void RemoveDragStyle(VisualElement element)
        {
            element.style.backgroundColor = c_itemBackgroundColor;
            element.style.scale = new Scale(Vector3.one);
        }

        private void ResetDragState()
        {
            _draggedIndex = -1;
            _dragStartIndex = -1;
            _draggedElement = null;
            _targetIndex = -1;
        }

        #endregion
    }
}
