using System.IO;
using UnityEngine;
using FraxinusEngine.Base;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    public class ScriptableRendererGraphWindow : BaseGraphWindow
    {
        internal ScriptableRendererGraphView view;

        public static ScriptableRendererGraphWindow Open(ScriptableRendererGraph graph)
        {
            var windows = Resources.FindObjectsOfTypeAll<ScriptableRendererGraphWindow>();
            foreach (var window in windows)
            {
                if (window._graph == graph)
                {
                    window.Show();
                    window.Focus();
                    return window;
                }
            }

            var graphWindow = EditorWindow.CreateWindow<ScriptableRendererGraphWindow>();
            graphWindow.Show();
            graphWindow.Focus();
            graphWindow.InitializeGraph(graph);

            return graphWindow;
        }

        protected new void OnEnable()
        {
            base.OnEnable();
            // graphUnloaded += g => MixtureUpdater.RemoveGraphToProcess(g as MixtureGraph);
        }

        protected override void OnDestroy()
        {
            view?.Dispose();
            // MixtureUpdater.RemoveGraphToProcess(view);
        }


        protected override void InitializeWindow(BaseGraph graph)
        {
            if (view != null)
            {
                view.Dispose();
                // MixtureUpdater.RemoveGraphToProcess(view);
            }

            var rendererGraph = (graph as ScriptableRendererGraph);
            var fileName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(rendererGraph));
            titleContent = new GUIContent($"ScriptableRenderer - {fileName}");

            view = new ScriptableRendererGraphView(this);

            _rootView.Add(view);

            view.Add(new ScriptableRendererGraphToolbar(view));
        }

        public ScriptableRendererGraph GetCurrentGraph() => _graph as ScriptableRendererGraph;
    }
}
