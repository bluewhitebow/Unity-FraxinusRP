using System;
using UnityEngine.UIElements;
using UnityEngine;
using FraxinusEngine.Base;
using UnityEditor.Experimental.GraphView;

namespace FraxinusEditor.Base
{
    public abstract class PinnedElementView : GraphElement, IDisposable
    {
        protected PinnedElement _pinnedElement;
        protected VisualElement _root;
        protected VisualElement _content;
        protected VisualElement _header;

        protected event Action _onResized;

        VisualElement _main;
        Label _titleLabel;
        bool  _scrollable;
        ScrollView _scrollView;

        static readonly string s_pinnedElementStyle = "GraphProcessorStyles/PinnedElementView";
        static readonly string s_pinnedElementTree = "GraphProcessorElements/PinnedElement";

        public override string title
        {
            get { return _titleLabel.text; }
            set { _titleLabel.text = value; }
        }

        protected bool scrollable
        {
            get
            {
                return _scrollable;
            }
            set
            {
                if (_scrollable == value)
                    return;

                _scrollable = value;

                style.position = Position.Absolute;
                if (_scrollable)
                {
                    _content.RemoveFromHierarchy();
                    _root.Add(_scrollView);
                    _scrollView.Add(_content);
                    AddToClassList("scrollable");
                }
                else
                {
                    _scrollView.RemoveFromHierarchy();
                    _content.RemoveFromHierarchy();
                    _root.Add(_content);
                    RemoveFromClassList("scrollable");
                }
            }
        }

        public PinnedElementView()
        {
            var tpl = Resources.Load<VisualTreeAsset>(s_pinnedElementTree);
            styleSheets.Add(Resources.Load<StyleSheet>(s_pinnedElementStyle));

            _main = tpl.CloneTree();
            _main.AddToClassList("mainContainer");
            _scrollView = new ScrollView(ScrollViewMode.VerticalAndHorizontal);

            _root = _main.Q("content");

            _header = _main.Q("header");

            _titleLabel = _main.Q<Label>(name: "titleLabel");
            _content = _main.Q<VisualElement>(name: "contentContainer");

            hierarchy.Add(_main);

            capabilities |= Capabilities.Movable | Capabilities.Resizable;
            style.overflow = Overflow.Hidden;

            ClearClassList();
            AddToClassList("pinnedElement");

            this.AddManipulator(new Dragger { clampToParentEdges = true });

            scrollable = false;

            hierarchy.Add(new Resizer(() => _onResized?.Invoke()));

            RegisterCallback<DragUpdatedEvent>(e =>
            {
                e.StopPropagation();
            });

            title = "PinnedElementView";
        }

        public void InitializeGraphView(PinnedElement pinnedElement, BaseGraphView graphView)
        {
            this._pinnedElement = pinnedElement;
            SetPosition(pinnedElement.position);

            _onResized += () => {
                pinnedElement.position.size = layout.size;
            };

            RegisterCallback<MouseUpEvent>(e => {
                pinnedElement.position.position = layout.position;
            });

            Initialize(graphView);
        }

        public void ResetPosition()
        {
            _pinnedElement.position = new Rect(Vector2.zero, PinnedElement.defaultSize);
            SetPosition(_pinnedElement.position);
        }

        protected abstract void Initialize(BaseGraphView graphView);

        bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Destroy();
        }

        protected virtual void Destroy() {}
    }
}
