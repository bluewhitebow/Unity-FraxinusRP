using UnityEngine.UIElements;
using FraxinusEngine.Base;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    public class RendererGraphParameterPropertyView : VisualElement
    {
        protected ScriptableRendererGraphView _rendererGraphView;

        public ExposedParameter parameter { get; private set; }

        public Toggle hideInInspector { get; private set; }

        public RendererGraphParameterPropertyView(BaseGraphView graphView, ExposedParameter param)
        {
            _rendererGraphView = graphView as ScriptableRendererGraphView;
            parameter = param;

            var valueField = graphView.exposedParameterFactory.GetParameterValueField(param, (newValue) => 
            {
                graphView.RegisterCompleteObjectUndo("Updated Parameter Value");
                param.value = newValue;
                graphView.graph.NotifyExposedParameterValueChanged(param);
                // mixtureGraphView.ProcessGraph();
            });

            var field = graphView.exposedParameterFactory.GetParameterSettingsField(param, (newValue) => 
            {
                param.settings = newValue as ExposedParameter.Settings;
            });

            Add(valueField);
            Add(field);
        }
    }
}
