using UnityEngine.UIElements;
using FraxinusEngine.Base;
using UnityEditor;

namespace FraxinusEditor.Base
{
    public class GraphInspector : Editor
    {
        protected VisualElement _root;
        protected BaseGraph     _graph;
        protected ExposedParameterFieldFactory _exposedParameterFactory;

        VisualElement _parameterContainer;

        protected virtual void OnEnable()
        {
            _graph = target as BaseGraph;
            _graph.onExposedParameterListChanged += UpdateExposedParameters;
            _graph.onExposedParameterModified += UpdateExposedParameters;
            if (_exposedParameterFactory == null)
                _exposedParameterFactory = new ExposedParameterFieldFactory(_graph);
        }

        protected virtual void OnDisable()
        {
            _graph.onExposedParameterListChanged -= UpdateExposedParameters;
            _graph.onExposedParameterModified -= UpdateExposedParameters;
            _exposedParameterFactory?.Dispose(); //  Graphs that created in GraphBehaviour sometimes gives null ref.
            _exposedParameterFactory = null;
        }

        public sealed override VisualElement CreateInspectorGUI()
        {
            _root = new VisualElement();
            CreateInspector();
            return _root;
        }

        protected virtual void CreateInspector()
        {
            _parameterContainer = new VisualElement{ name = "ExposedParameters" };
            FillExposedParameters(_parameterContainer);

            _root.Add(_parameterContainer);
        }

        protected void FillExposedParameters(VisualElement parameterContainer)
        {
            if (_graph.exposedParameters.Count != 0)
                parameterContainer.Add(new Label("Exposed Parameters:"));

            foreach (var param in _graph.exposedParameters)
            {
                if (param.settings.isHidden)
                    continue;

                var field = _exposedParameterFactory.GetParameterValueField(param, (newValue) => {
                    param.value = newValue;
                    serializedObject.ApplyModifiedProperties();
                    _graph.NotifyExposedParameterValueChanged(param);
                });
                parameterContainer.Add(field);
            }
        }

        void UpdateExposedParameters(ExposedParameter param) => UpdateExposedParameters();

        void UpdateExposedParameters()
        {
            _parameterContainer.Clear();
            FillExposedParameters(_parameterContainer);
        }

        // Don't use ImGUI
        public sealed override void OnInspectorGUI() {}

    }
}