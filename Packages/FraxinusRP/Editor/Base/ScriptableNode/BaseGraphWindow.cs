using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;
using FraxinusEngine.Base;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace FraxinusEditor.Base
{
    [Serializable]
    public abstract class BaseGraphWindow : EditorWindow
    {
        protected VisualElement        _rootView;
        protected BaseGraphView        _graphView;

        [SerializeField]
        protected BaseGraph            _graph;

        readonly string                _graphWindowStyle = "GraphProcessorStyles/BaseGraphView";

        public bool                    isGraphLoaded
        {
            get { return _graphView != null && _graphView.graph != null; }
        }

        bool                        _reloadWorkaround = false;
        BaseGraph _pendingGraph;
        Scene _linkedScene;

        public event Action<BaseGraph> graphLoaded;
        public event Action<BaseGraph> graphUnloaded;

        /// <summary>
        /// Called by Unity when the window is enabled / opened
        /// </summary>
        protected virtual void OnEnable()
        {
            InitializeRootView();

            if (_graph != null)
                LoadGraph();
            else
                _reloadWorkaround = true;
        }

        protected virtual void Update()
        {
            // Workaround for the Refresh option of the editor window:
            // When Refresh is clicked, OnEnable is called before the serialized data in the
            // editor window is deserialized, causing the graph view to not be loaded
            if (_reloadWorkaround && _graph != null)
            {
                LoadGraph();
                _reloadWorkaround = false;
            }
        }

        void LoadGraph()
        {
            CancelPendingLoad();
            if (_graph.isEnabled)
                InitializeGraph(_graph);
            else
            {
                _pendingGraph = _graph;
                _pendingGraph.onEnabled += OnGraphEnabled;
            }
        }

        void OnGraphEnabled()
        {
            var graph = _pendingGraph;
            CancelPendingLoad();
            if (graph != null)
                InitializeGraph(graph);
        }

        void CancelPendingLoad()
        {
            if (_pendingGraph != null)
                _pendingGraph.onEnabled -= OnGraphEnabled;
            _pendingGraph = null;
        }

        void ReleaseView()
        {
            CancelPendingLoad();
            if (_graph != null)
                _graph.onSceneLinked -= LinkGraphWindowToScene;
            EditorSceneManager.sceneClosed -= OnLinkedSceneClosed;
            _graphView?.Dispose();
            _graphView = null;
        }

        /// <summary>
        /// Called by Unity when the window is disabled (happens on domain reload)
        /// </summary>
        protected virtual void OnDisable()
        {
            if (_graph != null && _graphView != null)
                _graphView.SaveGraphToDisk();
            ReleaseView();
        }
        
        /// <summary>
        /// Called by Unity when the window is closed
        /// </summary>
        protected virtual void OnDestroy() => ReleaseView();

        void InitializeRootView()
        {
            _rootView = base.rootVisualElement;
            _rootView.name = "graphRootView";
            var style = Resources.Load<StyleSheet>(_graphWindowStyle);
            if (style != null && !_rootView.styleSheets.Contains(style))
                _rootView.styleSheets.Add(style);
        }

        public void InitializeGraph(BaseGraph graph)
        {
            if (graph == null)
                throw new ArgumentNullException(nameof(graph));
            _reloadWorkaround = false;
            if (_rootView == null)
                InitializeRootView();
            _graphView?.SaveGraphToDisk();
            ReleaseView();
            if (this._graph != null && graph != this._graph)
            {
                // Save the graph to the disk
                EditorUtility.SetDirty(this._graph);
                if (AssetDatabase.Contains(this._graph))
                    AssetDatabase.SaveAssetIfDirty(this._graph);
                // Unload the graph
                graphUnloaded?.Invoke(this._graph);
            }

            graphLoaded?.Invoke(graph);
            this._graph = graph;

            //Initialize will provide the BaseGraphView
            InitializeWindow(graph);

            _graphView = _rootView.Children().FirstOrDefault(e => e is BaseGraphView) as BaseGraphView;

            if (_graphView == null)
            {
                Debug.LogError("GraphView has not been added to the BaseGraph root view !");
                return ;
            }

            _graphView.Initialize(graph);

            InitializeGraphView(_graphView);

            // TOOD: onSceneLinked...

            if (graph.IsLinkedToScene())
                LinkGraphWindowToScene(graph.GetLinkedScene());
            else
                graph.onSceneLinked += LinkGraphWindowToScene;
        }

        void LinkGraphWindowToScene(Scene scene)
        {
            _linkedScene = scene;
            EditorSceneManager.sceneClosed -= OnLinkedSceneClosed;
            EditorSceneManager.sceneClosed += OnLinkedSceneClosed;
        }

        void OnLinkedSceneClosed(Scene scene)
        {
            if (scene == _linkedScene)
                Close();
        }

        public virtual void OnGraphDeleted()
        {
            ReleaseView();
            _graph = null;
        }

        protected abstract void InitializeWindow(BaseGraph graph);
        protected virtual void InitializeGraphView(BaseGraphView view) {}
    }
}