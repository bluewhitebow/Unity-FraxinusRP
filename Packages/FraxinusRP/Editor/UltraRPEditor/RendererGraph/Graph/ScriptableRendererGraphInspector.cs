using UnityEngine;
using UnityEngine.UIElements;
using FraxinusEngine.Base;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(ScriptableRendererGraph), true)]
    public class ScriptableRendererGraphEditor : RendererGraphEditor
    {
    }

    public abstract class RendererGraphEditor : Editor
    {
        protected ScriptableRendererGraph _graph;
        protected VisualElement _root;
        protected VisualElement _parameters;
        protected ExposedParameterFieldFactory _exposedParameterFactory;

        protected virtual void OnEnable()
        {
            _graph = RendererGraphEditorUtils.GetGraphAtPath(AssetDatabase.GetAssetPath(target));

            if (_graph != null)
            {
                _exposedParameterFactory = new ExposedParameterFieldFactory(_graph);
                _graph.onExposedParameterListChanged += UpdateExposedParameters;
                _graph.onExposedParameterModified += UpdateExposedParameters;
                Undo.undoRedoPerformed += UpdateExposedParameters;
            }
        }

        protected virtual void OnDisable()
        {
            if (_graph != null)
            {
                _graph.onExposedParameterListChanged -= UpdateExposedParameters;
                _graph.onExposedParameterModified -= UpdateExposedParameters;
                Undo.undoRedoPerformed -= UpdateExposedParameters;
                _exposedParameterFactory.Dispose();
                _exposedParameterFactory = null;
            }
        }

        public override VisualElement CreateInspectorGUI()
        {
            if (_graph == null)
                return base.CreateInspectorGUI();

            CreateRootElement();

            UpdateExposedParameters(null);

            return _root;
        }

        protected void CreateRootElement()
        {
            _root = new VisualElement();

            var styleSheet = Resources.Load<StyleSheet>("UIElementRes/ScriptableRendererGraphInspector");
            if (styleSheet != null)
                _root.styleSheets.Add(styleSheet);
        }

        protected void UpdateExposedParameters(ExposedParameter param) => UpdateExposedParameters();
        protected void UpdateExposedParameters()
        {
            if (_root == null)
                return;

            if (_parameters == null || !_root.Contains(_parameters))
            {
                _parameters = new VisualElement() { name = "ExposedParameters" };
                _root.Add(_parameters);
            }

            _parameters.Clear();

            bool header = true;
            foreach (var param in _graph.exposedParameters)
            {
                if (param.settings.isHidden)
                    continue;

                if (header)
                {
                    var headerLabel = new Label("Exposed Parameters");
                    headerLabel.AddToClassList("Header");
                    _parameters.Add(headerLabel);
                    header = false;
                }
                VisualElement prop = new VisualElement();
                prop.AddToClassList("Indent");
                prop.style.display = DisplayStyle.Flex;
                var p = _exposedParameterFactory.GetParameterValueField(param, (newValue) => 
                {
                    param.value = newValue;
                    _graph.NotifyExposedParameterValueChanged(param);
                });
                prop.Add(p);
                _parameters.Add(prop);
            }
        }
    }
}
