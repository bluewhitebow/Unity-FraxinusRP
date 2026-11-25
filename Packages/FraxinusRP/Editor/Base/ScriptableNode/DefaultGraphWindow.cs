using UnityEngine;
using FraxinusEngine.Base;

namespace FraxinusEditor.Base
{
    public class DefaultGraphWindow : BaseGraphWindow
    {
        protected override void OnDestroy()
        {
            base.OnDestroy();
        }

        protected override void InitializeWindow(BaseGraph graph)
        {
            titleContent = new GUIContent("Default Graph");

            if (_graphView == null)
            {
                _graphView = new BaseGraphView(this);
                _graphView.Add(new ToolbarView(_graphView));
            }

            _rootView.Add(_graphView);
        }
    }
}
