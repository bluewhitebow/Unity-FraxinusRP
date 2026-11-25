using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;
using FraxinusEditor.Base;

using PopupWindow = UnityEditor.PopupWindow;

namespace FraxinusEditor.Rendering.Ultra
{
    public class ScriptableRendererGraphToolbar : ToolbarView
    {
        static readonly List<string> s_resolutions = new List<string>
        {
            "32", "64", "128", "256", "512", "1024", "Custom"
        };

        PopupField<string> _resolution;
        Toggle _globalPreviewToggle;
        VisualElement _customResolution;
        IntegerField _width;
        IntegerField _height;
        Button _parameterButton;
        Button _settingsButton;
        Button _analysisButton;

        class SettingsRendererGraphPopupWindow : PopupWindowContent
        {
            readonly ScriptableRendererGraphView _graphView;

            public SettingsRendererGraphPopupWindow(ScriptableRendererGraphView graphView)
            {
                _graphView = graphView;
            }

            public override Vector2 GetWindowSize() => new Vector2(300, 310);
            public override void OnGUI(Rect rect) { }

            public override void OnOpen()
            {
                var settingsView = new PassNodeSettingsView(_graphView.graph.settings, _graphView, "Graph Settings", false);
                settingsView.AddToClassList("NodeSettingsView");
                settingsView.style.height = new StyleLength(new Length(100, LengthUnit.Percent));

                var otherHeader = new Label("Advanced Settings");
                otherHeader.AddToClassList(PassNodeSettingsView.headerStyleClass);
                settingsView.Add(otherHeader);

                var globalPreviewToggle = new Toggle("Global Preview Enabled")
                {
                    value = _graphView.graph.globalPreviewEnabled,
                };
                globalPreviewToggle.RegisterValueChangedCallback(e =>
                {
                    SetGlobalPreview(_graphView, e.newValue);
                });
                globalPreviewToggle.schedule.Execute(() =>
                {
                    if (_graphView.graph != null)
                        globalPreviewToggle.SetValueWithoutNotify(_graphView.graph.globalPreviewEnabled);
                }).Every(250);
                settingsView.Add(globalPreviewToggle);
                editorWindow.rootVisualElement.Add(settingsView);
            }
        }

        class AnalysisPopupWindow : PopupWindowContent
        {
            public override Vector2 GetWindowSize() => new Vector2(212, 112);
            public override void OnGUI(Rect rect) { }

            public override void OnOpen()
            {
                var root = editorWindow.rootVisualElement;
                AddToolbarStyles(root);
                root.AddToClassList("fraxinus-analysis-popup");
                var title = new Label("RENDERING TOOLS");
                title.AddToClassList("fraxinus-popup-caption");
                root.Add(title);

                var renderGraphButton = new Button(() =>
                {
                    EditorWindow.GetWindow<RenderGraphViewer>().Focus();
                    editorWindow.Close();
                })
                {
                    text = "Render Graph Viewer",
                    tooltip = "Inspect the render graph and its resource usage."
                };
                root.Add(renderGraphButton);

                var debuggerType = Type.GetType("UnityEditor.Rendering.DebugWindow, Unity.RenderPipelines.Core.Editor");
                var debuggerButton = new Button(() =>
                {
                    var window = EditorWindow.GetWindow(debuggerType);
                    if (window != null)
                        window.Focus();
                    editorWindow.Close();
                })
                {
                    text = "Rendering Debugger",
                    tooltip = "Open Unity's rendering diagnostics."
                };
                debuggerButton.SetEnabled(debuggerType != null);
                root.Add(debuggerButton);
            }
        }

        public ScriptableRendererGraphToolbar(BaseGraphView graphView) : base(graphView)
        {
            // The base class owns initialization; replace only its IMGUI presentation.
            Clear();
            AddToClassList("fraxinus-graph-toolbar");
            AddToolbarStyles(this);
            RegisterCallback<GeometryChangedEvent>(e => UpdateResponsiveLayout(e.newRect.width));
            RegisterCallback<MouseDownEvent>(e => e.StopPropagation());
            RegisterCallback<WheelEvent>(e => e.StopPropagation());
            schedule.Execute(SyncControls).Every(250);
        }

        public ScriptableRendererGraphView graphView => base._graphView as ScriptableRendererGraphView;
        ScriptableRendererGraph _graph => graphView.graph;

        static void AddToolbarStyles(VisualElement element)
        {
            var styleSheet = Resources.Load<StyleSheet>("UIElementRes/RendererGraphToolbar");
            if (styleSheet != null)
                element.styleSheets.Add(styleSheet);
        }

        protected override void AddButtons()
        {
            Clear();

            var brand = new VisualElement();
            brand.AddToClassList("fraxinus-toolbar-brand");
            var mark = new Label("F");
            mark.AddToClassList("fraxinus-toolbar-mark");
            brand.Add(mark);
            var brandText = new VisualElement();
            brandText.AddToClassList("fraxinus-toolbar-brand-text");
            var title = new Label("FRAXINUS");
            title.AddToClassList("fraxinus-toolbar-title");
            var subtitle = new Label("RENDER GRAPH");
            subtitle.AddToClassList("fraxinus-toolbar-subtitle");
            brandText.Add(title);
            brandText.Add(subtitle);
            brand.Add(brandText);
            Add(brand);

            var actions = CreateGroup();
            actions.AddToClassList("fraxinus-toolbar-actions");
            actions.Add(CreateButton(null, EditorGUIUtility.IconContent("Refresh").image, "Refresh graph and previews", Refresh));
            actions.Add(CreateButton(null, EditorGUIUtility.IconContent("SaveAs").image, "Save renderer graph", SaveAll));
            actions.Add(CreateButton("Project", EditorGUIUtility.IconContent("Folder Icon").image, "Show renderer graph in the Project window", ShowInProject));
            var fitButton = CreateButton("Fit View", RendererGraphEditorUtils.fitIcon, "Fit all render passes in the canvas", graphView.FrameGraph);
            fitButton.AddToClassList("fraxinus-toolbar-fit");
            actions.Add(fitButton);
            Add(actions);

            var spacer = new VisualElement();
            spacer.AddToClassList("fraxinus-toolbar-spacer");
            Add(spacer);

            var previewGroup = CreateGroup();
            previewGroup.AddToClassList("fraxinus-toolbar-preview");
            _globalPreviewToggle = new Toggle
            {
                name = "global-preview-toggle",
                text = "Global Preview",
                tooltip = "Enable or disable texture previews for all render passes"
            };
            _globalPreviewToggle.AddToClassList("fraxinus-toolbar-preview-toggle");
            _globalPreviewToggle.RegisterValueChangedCallback(e =>
            {
                SetGlobalPreview(graphView, e.newValue);
                SyncControls();
            });
            previewGroup.Add(_globalPreviewToggle);

            _resolution = new PopupField<string>(s_resolutions, GetResolutionLabel())
            {
                tooltip = "Preview texture resolution"
            };
            _resolution.AddToClassList("fraxinus-toolbar-resolution");
            _resolution.RegisterValueChangedCallback(e => ChangeResolution(e.newValue));
            previewGroup.Add(_resolution);

            _customResolution = CreateGroup();
            _customResolution.AddToClassList("fraxinus-toolbar-custom-resolution");
            _width = CreateDimensionField("Preview width in pixels", true);
            _height = CreateDimensionField("Preview height in pixels", false);
            _customResolution.Add(_width);
            _customResolution.Add(new Label("×"));
            _customResolution.Add(_height);
            previewGroup.Add(_customResolution);
            Add(previewGroup);

            var utilities = CreateGroup();
            utilities.AddToClassList("fraxinus-toolbar-utilities");
            _parameterButton = CreateButton("Parameters", null, "Show or hide graph parameters", ToggleParameterView);
            _parameterButton.Insert(0, CreateUtilityIcon("fraxinus-toolbar-parameters-icon"));
            utilities.Add(_parameterButton);
            _settingsButton = CreateButton(null, RendererGraphEditorUtils.settingsIcon24, "Graph settings", ShowSettingsWindow);
            utilities.Add(_settingsButton);
            _analysisButton = CreateButton("Analysis", null, "Open rendering analysis tools", ShowAnalysisWindow);
            _analysisButton.Insert(0, CreateUtilityIcon("fraxinus-toolbar-analysis-icon"));
            utilities.Add(_analysisButton);
            Add(utilities);

            SyncControls();
            UpdateResponsiveLayout(resolvedStyle.width);
        }

        static VisualElement CreateGroup()
        {
            var group = new VisualElement();
            group.AddToClassList("fraxinus-toolbar-group");
            return group;
        }

        static Button CreateButton(string label, Texture icon, string tooltip, Action callback)
        {
            var button = new Button(callback) { tooltip = tooltip };
            button.AddToClassList("fraxinus-toolbar-button");
            if (icon != null)
            {
                var image = new Image { image = icon, pickingMode = PickingMode.Ignore };
                image.AddToClassList("fraxinus-toolbar-icon");
                button.Add(image);
            }
            if (!string.IsNullOrEmpty(label))
            {
                var text = new Label(label) { pickingMode = PickingMode.Ignore };
                text.AddToClassList("fraxinus-toolbar-button-label");
                button.Add(text);
            }
            return button;
        }

        static VisualElement CreateUtilityIcon(string className)
        {
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("fraxinus-toolbar-utility-icon");
            icon.AddToClassList(className);
            for (int i = 0; i < 3; i++)
            {
                var bar = new VisualElement { pickingMode = PickingMode.Ignore };
                bar.AddToClassList("fraxinus-toolbar-icon-bar");
                bar.AddToClassList("fraxinus-toolbar-icon-bar-" + i);
                icon.Add(bar);
            }
            return icon;
        }

        IntegerField CreateDimensionField(string tooltip, bool isWidth)
        {
            var field = new IntegerField { isDelayed = true, tooltip = tooltip };
            field.AddToClassList("fraxinus-toolbar-dimension");
            field.RegisterValueChangedCallback(e =>
            {
                graphView.RegisterCompleteObjectUndo("Change preview resolution");
                int value = Mathf.Max(1, e.newValue);
                if (isWidth)
                    _graph.settings.width = value;
                else
                    _graph.settings.height = value;
                field.SetValueWithoutNotify(value);
                _graph.SetDirty();
                SyncControls();
            });
            return field;
        }

        void ChangeResolution(string value)
        {
            graphView.RegisterCompleteObjectUndo("Change preview resolution");
            if (value == "Custom")
                _graph.settings.potSize = POTSize.Custom;
            else if (int.TryParse(value, out int size))
                _graph.settings.SetPOTSize(size);
            _graph.SetDirty();
            SyncControls();
        }

        static void SetGlobalPreview(ScriptableRendererGraphView view, bool enabled)
        {
            var graph = view.graph;
            if (graph == null || graph.globalPreviewEnabled == enabled)
                return;

            view.RegisterCompleteObjectUndo("Toggle global preview");
            graph.globalPreviewEnabled = enabled;
            EditorUtility.SetDirty(graph);
            graph.SetDirty();
            view.ReloadView();
        }

        string GetResolutionLabel()
        {
            return _graph.settings.potSize == POTSize.Custom ? "Custom" : ((int)_graph.settings.potSize).ToString();
        }

        static bool IsEditing(VisualElement field)
        {
            var focused = field.panel?.focusController.focusedElement as VisualElement;
            return focused != null && (focused == field || field.Contains(focused));
        }

        void SyncControls()
        {
            if (_graph == null || _resolution == null)
                return;

            _globalPreviewToggle.SetValueWithoutNotify(_graph.globalPreviewEnabled);
            _globalPreviewToggle.EnableInClassList("is-active", _graph.globalPreviewEnabled);
            _resolution.SetValueWithoutNotify(GetResolutionLabel());
            _customResolution.style.display = _graph.settings.potSize == POTSize.Custom ? DisplayStyle.Flex : DisplayStyle.None;
            // Delayed fields commit on Enter or blur; keep in-progress edits intact.
            if (!IsEditing(_width))
                _width.SetValueWithoutNotify(_graph.settings.width);
            if (!IsEditing(_height))
                _height.SetValueWithoutNotify(_graph.settings.height);
            UpdateButtonStatus();
        }

        public override void UpdateButtonStatus()
        {
            base.UpdateButtonStatus();
            if (_parameterButton != null && _graph != null)
                _parameterButton.EnableInClassList("is-active", graphView.GetPinnedElementStatus<RendererGraphParameterView>() != DropdownMenuAction.Status.Hidden);
        }

        void UpdateResponsiveLayout(float width)
        {
            EnableInClassList("is-compact", width < 920);
            EnableInClassList("is-narrow", width < 660);
            if (_globalPreviewToggle != null)
                _globalPreviewToggle.text = width < 660 ? "Preview" : "Global Preview";
        }

        void Refresh()
        {
            graphView.ReloadView();
            _graph.SetDirty();
        }

        void SaveAll()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Scriptable Renderer Graph", "Saving All...", 0.0f);
                graphView.SaveGraphToDisk();
                AssetDatabase.SaveAssetIfDirty(_graph);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void ShowInProject()
        {
            EditorGUIUtility.PingObject(_graph);
            ProjectWindowUtil.ShowCreatedAsset(_graph);
        }

        void ToggleParameterView()
        {
            graphView.ToggleView<RendererGraphParameterView>();
            _graph.isParameterViewOpen = graphView.GetPinnedElementStatus<RendererGraphParameterView>() != DropdownMenuAction.Status.Hidden;
            UpdateButtonStatus();
        }

        void ShowSettingsWindow()
        {
            PopupWindow.Show(_settingsButton.worldBound, new SettingsRendererGraphPopupWindow(graphView));
        }

        void ShowAnalysisWindow()
        {
            PopupWindow.Show(_analysisButton.worldBound, new AnalysisPopupWindow());
        }
    }
}
