using UnityEngine.UIElements;
using FraxinusEngine.Base;

namespace FraxinusEditor.Base
{
    [NodeCustomEditor(typeof(ParameterNode))]
    public class ParameterNodeView : BaseNodeView
    {
        ParameterNode _parameterNode;

        public override void Enable(bool fromInspector = false)
        {
            _parameterNode = nodeTarget as ParameterNode;

            UpdatePort();

            // Find and remove expand/collapse button
            titleContainer.Remove(titleContainer.Q("title-button-container"));
            // Remove Port from the #content
            topContainer.parent.Remove(topContainer);
            // Add Port to the #title
            titleContainer.Add(topContainer);

            _parameterNode.onParameterChanged += UpdateView;
            UpdateView();
        }

        void UpdateView()
        {
            title = _parameterNode.parameter?.name;
        }

        void UpdatePort()
        {
            titleContainer.RemoveFromClassList("input");
        }
    }

}