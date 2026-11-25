using UnityEngine.UIElements;
using FraxinusEngine.Base;
using UnityEditor;

namespace FraxinusEditor.Base
{
    [CustomEditor(typeof(BaseGraph), true)]
    public class GraphAssetInspector : GraphInspector
    {
        protected override void CreateInspector()
        {
            base.CreateInspector();

            _root.Add(new Button(() =>
            {
                var graph = target as BaseGraph;
                if (graph != null)
                {
                    var window = EditorWindow.GetWindow<DefaultGraphWindow>();
                    if (window != null)
                        window.InitializeGraph(graph);
                }
            })
            {
                text = "Open base graph window"
            });
        }
    }
}
