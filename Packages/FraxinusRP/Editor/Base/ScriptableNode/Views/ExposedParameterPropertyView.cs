using UnityEngine.UIElements;
using FraxinusEngine.Base;

namespace FraxinusEditor.Base
{
    public class ExposedParameterPropertyView : VisualElement
    {
        protected BaseGraphView _baseGraphView;

        public ExposedParameter parameter { get; private set; }

        public Toggle hideInInspector { get; private set; }

        public ExposedParameterPropertyView(BaseGraphView graphView, ExposedParameter param)
        {
            _baseGraphView = graphView;
            parameter = param;

            var field = graphView.exposedParameterFactory.GetParameterSettingsField(param, (newValue) => 
            {
                param.settings = newValue as ExposedParameter.Settings;
            });

            Add(field);
        }
    }
} 