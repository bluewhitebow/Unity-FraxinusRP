using System;
using System.Collections.Generic;
using System.Linq;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEngine.UIElements;

namespace FraxinusEditor.Rendering.Ultra
{
    internal sealed class RendererGraphInjectPassView : VisualElement
    {
        sealed class PassRow
        {
            public Type type;
            public VisualElement element;
            public Toggle toggle;
            public bool? enabled;
        }

        readonly ScriptableRendererGraphView _graphView;
        readonly List<PassRow> _rows = new List<PassRow>();
        readonly Foldout _foldout;
        int _enabledCount = -1;

        public bool expanded => _foldout.value;

        public RendererGraphInjectPassView(ScriptableRendererGraphView graphView)
        {
            _graphView = graphView;
            name = "renderer-inject-pass-panel";
            AddToClassList("fraxinus-inject-panel");

            _foldout = new Foldout { text = "Inject Passes", value = true };
            _foldout.AddToClassList("fraxinus-inject-foldout");
            Add(_foldout);

            var summary = new Label("Enabled passes / current renderer")
            {
                tooltip = "Unchecking removes the pass and its input texture requirements. Shared textures remain available to other enabled passes."
            };
            summary.AddToClassList("fraxinus-inject-summary");
            _foldout.Add(summary);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("fraxinus-inject-list");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _foldout.Add(scroll);

            // Discover independently of active bindings so disabled and fallback passes remain available.
            foreach (var type in ScriptableRenderer.injectPassTypes
                .OrderBy(type => type.Name, StringComparer.Ordinal)
                .ThenBy(type => type.FullName, StringComparer.Ordinal))
            {
                var attributes = type.GetCustomAttributes(typeof(InjectPassAttribute), true)
                    .Cast<InjectPassAttribute>().Where(attribute => attribute.targetPassType != null).ToArray();
                if (attributes.Length == 0)
                    continue;

                var targets = attributes.Select(attribute =>
                    $"{attribute.targetPassType.Name} / {attribute.point} / {attribute.scopeFilter}").Distinct();
                var fallback = attributes.Any(attribute => attribute.finalInjectPostPass);
                var tooltip = $"{type.FullName}\n{string.Join("\n", targets)}"
                    + (fallback ? "\nFallback: Post Process" : "")
                    + "\nUncheck to skip this pass everywhere in this renderer.";
                var row = new PassRow
                {
                    type = type,
                    element = new VisualElement { tooltip = tooltip },
                    toggle = new Toggle { text = type.Name, tooltip = tooltip }
                };
                row.element.AddToClassList("fraxinus-inject-row");
                row.toggle.AddToClassList("fraxinus-inject-toggle");
                row.toggle.SetValueWithoutNotify(_graphView.graph.IsInjectPassEnabled(type));
                row.toggle.RegisterValueChangedCallback(evt => SetPassEnabled(type, evt.newValue));
                row.element.Add(row.toggle);

                string targetLabel = attributes.Length == 1
                    ? $"{ObjectNames.NicifyVariableName(attributes[0].targetPassType.Name)} / {attributes[0].point}"
                    : $"{attributes.Length} injection points";
                var target = new Label(targetLabel + (fallback ? " / fallback" : ""));
                target.AddToClassList("fraxinus-inject-target");
                row.element.Add(target);
                scroll.Add(row.element);
                _rows.Add(row);
            }

            if (_rows.Count == 0)
            {
                var empty = new Label("No injected passes found.");
                empty.AddToClassList("fraxinus-inject-empty");
                scroll.Add(empty);
            }
            RefreshStates();
            schedule.Execute(RefreshStates).Every(250);

            // Interacting with the list must not pan, zoom, select or delete canvas nodes.
            RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
            RegisterCallback<MouseUpEvent>(evt => evt.StopPropagation());
            RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            RegisterCallback<WheelEvent>(evt => evt.StopPropagation());
            RegisterCallback<KeyDownEvent>(evt => evt.StopPropagation());
        }

        void SetPassEnabled(Type type, bool enabled)
        {
            var graph = _graphView.graph;
            if (graph == null || graph.IsInjectPassEnabled(type) == enabled)
                return;

            Undo.RegisterCompleteObjectUndo(graph, "Toggle injected render pass");
            graph.SetInjectPassEnabled(type, enabled);
            EditorUtility.SetDirty(graph);
            RefreshStates();
            foreach (var node in _graphView.nodeViews.OfType<ScriptableRenderPassNodeView>())
                node.RefreshInjectPassStates();
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        public void RefreshStates()
        {
            var graph = _graphView.graph;
            if (graph == null)
                return;

            int enabledCount = 0;
            foreach (var row in _rows)
            {
                bool enabled = graph.IsInjectPassEnabled(row.type);
                if (row.enabled != enabled)
                {
                    row.toggle.SetValueWithoutNotify(enabled);
                    row.element.EnableInClassList("fraxinus-inject-disabled", !enabled);
                    row.enabled = enabled;
                }
                if (enabled)
                    enabledCount++;
            }
            if (_enabledCount != enabledCount)
            {
                _enabledCount = enabledCount;
                _foldout.text = $"Inject Passes  {enabledCount}/{_rows.Count}";
            }
        }

    }
}
