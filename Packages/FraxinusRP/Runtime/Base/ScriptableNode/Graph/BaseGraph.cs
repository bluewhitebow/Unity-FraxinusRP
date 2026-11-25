using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.SceneManagement;

using Object = UnityEngine.Object;

namespace FraxinusEngine.Base
{
    public class GraphChanges
    {
        public SerializableEdge removedEdge;
        public SerializableEdge addedEdge;
        public BaseNode         removedNode;
        public BaseNode         addedNode;
        public BaseNode         nodeChanged;
        public Group            addedGroups;
        public Group            removedGroups;
        public BaseStackNode    addedStackNode;
        public BaseStackNode    removedStackNode;
        public StickyNote       addedStickyNotes;
        public StickyNote       removedStickyNotes;
    }

    public enum ComputeOrderType
    {
        DepthFirst,
        BreadthFirst,
    }

    [Serializable]
    public class BaseGraph : ScriptableObject, ISerializationCallbackReceiver
    {
        public delegate void AfterComputedOrderDelegate(IEnumerable<BaseNode> computedNodes);

        static readonly int s_maxComputeOrderDepth = 1000;

        /// <summary>
        /// Invalid compute order number of a node when it's inside a loop
        /// </summary>
        public static readonly int loopComputeOrder = -2;
        /// <summary>
        /// Invalid compute order number of a node can't process
        /// </summary>
        public static readonly int invalidComputeOrder = -1;

        /// <summary>
        /// List of all the nodes in the graph.
        /// </summary>
        [SerializeReference]
        public List<BaseNode> nodes = new List<BaseNode>();

        /// <summary>
        /// Dictionary to access node per GUID, faster than a search in a list
        /// </summary>
        [NonSerialized]
        public Dictionary<string, BaseNode> nodesPerGUID = new Dictionary<string, BaseNode>();

        /// <summary>
        /// Json list of edges
        /// </summary>
        [SerializeField]
        public List<SerializableEdge> edges = new List<SerializableEdge>();

        /// <summary>
        /// Dictionary of edges per GUID, faster than a search in a list
        /// </summary>
        [NonSerialized]
        public Dictionary<string, SerializableEdge> edgesPerGUID = new Dictionary<string, SerializableEdge>();

        /// <summary>
        /// All groups in the graph
        /// </summary>
        [SerializeField, FormerlySerializedAs("commentBlocks")]
        public List<Group> groups = new List<Group>();

        /// <summary>
        /// All Stack Nodes in the graph
        /// </summary>
        [SerializeField, SerializeReference]
        public List<BaseStackNode> stackNodes = new List<BaseStackNode>();

        /// <summary>
        /// All pinned elements in the graph
        /// </summary>
        [SerializeField]
        public List<PinnedElement> pinnedElements = new List<PinnedElement>();

        /// <summary>
        /// All exposed parameters in the graph
        /// </summary>
        [SerializeField, SerializeReference]
        public List<ExposedParameter> exposedParameters = new List<ExposedParameter>();

        [SerializeField]
        public List<StickyNote> stickyNotes = new List<StickyNote>();

        [NonSerialized]
        Dictionary<BaseNode, int> _computeOrderDictionary = new Dictionary<BaseNode, int>();

        [NonSerialized]
        Scene _linkedScene;

        [SerializeField]
        internal Object nodeInspectorReference;

        public Vector3 position = Vector3.zero;
        public Vector3 scale = Vector3.one;

        public event Action onExposedParameterListChanged;
        public event Action<ExposedParameter> onExposedParameterModified;
        public event Action<ExposedParameter> onExposedParameterValueChanged;

        public event Action<Scene> onSceneLinked;
        public event Action onEnabled;
        public event Action<GraphChanges> onGraphChanges;
        public event Action onConnectivityChanged;

        /// <summary>
        /// Triggered just after the compute order of the graph is updated
        /// </summary>
        public event AfterComputedOrderDelegate afterComputedOrder;

        [NonSerialized]
        bool _isEnabled = false;
        public bool isEnabled 
        { 
            get => _isEnabled; 
            private set => _isEnabled = value;
        }

        public HashSet<BaseNode> graphOutputs { get; private set; } = new HashSet<BaseNode>();

        HashSet<BaseNode> _infiniteLoopTracker = new HashSet<BaseNode>();

        protected virtual void OnEnable()
        {
            if (isEnabled)
                OnDisable();

            InitializeGraphElements();
            DestroyBrokenGraphElements();
            UpdateComputeOrder();
            isEnabled = true;
            onEnabled?.Invoke();
            onConnectivityChanged?.Invoke();
        }

        protected virtual void OnDisable()
        {
            isEnabled = false;
            foreach (var node in nodes)
                node.DisableInternal();
        }

        public virtual void OnAssetDeleted() { }

        void InitializeGraphElements()
        {
            nodesPerGUID.Clear();
            edgesPerGUID.Clear();
            nodes.RemoveAll(n => n == null);
            exposedParameters.RemoveAll(e => e == null);

            foreach (var node in nodes.ToList())
            {
                nodesPerGUID[node.guid] = node;
                node.Initialize(this);
            }

            foreach (var edge in edges.ToList())
            {
                edge.Deserialize(this);
                edgesPerGUID[edge.guid] = edge;

                // Sanity check for the edge:
                if (edge.inputPort == null || edge.outputPort == null)
                {
                    Disconnect(edge.guid);
                    continue;
                }

                // Add the edge to the non-serialized port data
                edge.inputPort.owner.OnEdgeConnected(edge);
                edge.outputPort.owner.OnEdgeConnected(edge);
            }
        }

        public BaseNode AddNode(BaseNode node)
        {
            // Check if node type has DisallowMultipleNode attribute
            var disallowMultipleAttr = node.GetType().GetCustomAttribute<DisallowMultipleNodeAttribute>();
            if (disallowMultipleAttr != null)
            {
                // Check if a node of this type already exists
                var existingNode = nodes.FirstOrDefault(n => n.GetType() == node.GetType());
                if (existingNode != null)
                {
                    string title = disallowMultipleAttr.customTitle;
                    if (string.IsNullOrEmpty(title))
                        Debug.LogWarning($"只能在图中添加一个 {node.GetType().Name} 节点");
                   
                    return null;
                }
            }

            nodesPerGUID[node.guid] = node;

            nodes.Add(node);
            node.Initialize(this);

            UpdateComputeOrder();

            onGraphChanges?.Invoke(new GraphChanges { addedNode = node });
            onConnectivityChanged?.Invoke();

            return node;
        }

        public void RemoveNode(BaseNode node)
        {
            foreach (var edge in edges.Where(e => e.inputNode == node || e.outputNode == node).ToList())
                Disconnect(edge);
            foreach (var group in groups)
                group.innerNodeGUIDs.Remove(node.guid);
            node.DisableInternal();
            node.DestroyInternal();

            nodesPerGUID.Remove(node.guid);

            nodes.Remove(node);

            UpdateComputeOrder();

            onGraphChanges?.Invoke(new GraphChanges { removedNode = node });
            onConnectivityChanged?.Invoke();
        }

        public SerializableEdge Connect(NodePort inputPort, NodePort outputPort, bool autoDisconnectInputs = true)
        {
            var edge = SerializableEdge.CreateNewEdge(this, inputPort, outputPort);

            //If the input port does not support multi-connection, we remove them
            if (autoDisconnectInputs && !inputPort.portData.acceptMultipleEdges)
            {
                foreach (var e in inputPort.GetEdges().ToList())
                {
                    // TODO: do not disconnect them if the connected port is the same than the old connected
                    Disconnect(e);
                }
            }
            // same for the output port:
            if (autoDisconnectInputs && !outputPort.portData.acceptMultipleEdges)
            {
                foreach (var e in outputPort.GetEdges().ToList())
                {
                    // TODO: do not disconnect them if the connected port is the same than the old connected
                    Disconnect(e);
                }
            }

            edges.Add(edge);
            edgesPerGUID[edge.guid] = edge;

            // Add the edge to the list of connected edges in the nodes
            inputPort.owner.OnEdgeConnected(edge);
            outputPort.owner.OnEdgeConnected(edge);

            UpdateComputeOrder();

            onGraphChanges?.Invoke(new GraphChanges { addedEdge = edge });
            onConnectivityChanged?.Invoke();

            return edge;
        }

        public void Disconnect(BaseNode inputNode, string inputFieldName, BaseNode outputNode, string outputFieldName)
        {
            edges.RemoveAll(r => 
            {
                bool remove = r.inputNode == inputNode
                    && r.outputNode == outputNode
                    && r.outputFieldName == outputFieldName
                    && r.inputFieldName == inputFieldName;

                if (remove)
                {
                    edgesPerGUID.Remove(r.guid);
                    r.inputNode?.OnEdgeDisconnected(r);
                    r.outputNode?.OnEdgeDisconnected(r);

                    onGraphChanges?.Invoke(new GraphChanges { removedEdge = r });
                }

                return remove;
            });

            UpdateComputeOrder();
            onConnectivityChanged?.Invoke();
        }

        public void Disconnect(SerializableEdge edge) => Disconnect(edge.guid);

        public void Disconnect(string edgeGUID)
        {
            edgesPerGUID.Remove(edgeGUID);
            List<(BaseNode, SerializableEdge)> disconnectEvents = new List<(BaseNode, SerializableEdge)>();

            edges.RemoveAll(r => 
            {
                if (r.guid == edgeGUID)
                {
                    disconnectEvents.Add((r.inputNode, r));
                    disconnectEvents.Add((r.outputNode, r));
                    onGraphChanges?.Invoke(new GraphChanges { removedEdge = r });
                }
                return r.guid == edgeGUID;
            });

            // Delay the edge disconnect event to avoid recursion
            foreach (var (node, edge) in disconnectEvents)
                node?.OnEdgeDisconnected(edge);

            UpdateComputeOrder();
            onConnectivityChanged?.Invoke();
        }

        public void AddGroup(Group block)
        {
            groups.Add(block);
            onGraphChanges?.Invoke(new GraphChanges { addedGroups = block });
        }

        public void RemoveGroup(Group block)
        {
            groups.Remove(block);
            onGraphChanges?.Invoke(new GraphChanges { removedGroups = block });
        }

        public void AddStackNode(BaseStackNode stackNode)
        {
            stackNodes.Add(stackNode);
            onGraphChanges?.Invoke(new GraphChanges { addedStackNode = stackNode });
        }

        public void RemoveStackNode(BaseStackNode stackNode)
        {
            stackNodes.Remove(stackNode);
            onGraphChanges?.Invoke(new GraphChanges { removedStackNode = stackNode });
        }

        public void AddStickyNote(StickyNote note)
        {
            stickyNotes.Add(note);
            onGraphChanges?.Invoke(new GraphChanges { addedStickyNotes = note });
        }

        public void RemoveStickyNote(StickyNote note)
        {
            stickyNotes.Remove(note);
            onGraphChanges?.Invoke(new GraphChanges { removedStickyNotes = note });
        }

        public void NotifyNodeChanged(BaseNode node) => onGraphChanges?.Invoke(new GraphChanges { nodeChanged = node });

        public PinnedElement OpenPinned(Type viewType)
        {
            var pinned = pinnedElements.Find(p => p.editorType.type == viewType);

            if (pinned == null)
            {
                pinned = new PinnedElement(viewType);
                pinnedElements.Add(pinned);
            }
            else
                pinned.opened = true;

            return pinned;
        }

        public void ClosePinned(Type viewType)
        {
            var pinned = pinnedElements.Find(p => p.editorType.type == viewType);
            pinned.opened = false;
        }

        public void OnBeforeSerialize()
        {
            stackNodes.RemoveAll(s => s == null);
            nodes.RemoveAll(n => n == null);
        }

        public void OnAfterDeserialize() { }

        public void Deserialize()
        {
            // Disable nodes correctly before removing them:
            if (nodes != null)
            {
                foreach (var node in nodes)
                    node.DisableInternal();
            }

            InitializeGraphElements();
        }

        public void UpdateComputeOrder(ComputeOrderType type = ComputeOrderType.DepthFirst)
        {
            // Find graph outputs (end nodes) and reset compute order
            graphOutputs.Clear();
            foreach (var node in nodes)
            {
                if (node.GetOutputNodes().Count() == 0)
                    graphOutputs.Add(node);

                node.computeOrder = 0;
            }

            _computeOrderDictionary.Clear();
            _infiniteLoopTracker.Clear();

            switch (type)
            {
                default:
                case ComputeOrderType.DepthFirst:
                    UpdateComputeOrderDepthFirst();
                    break;
                case ComputeOrderType.BreadthFirst:
                    foreach (var node in nodes)
                        UpdateComputeOrderBreadthFirst(0, node);
                    break;
            }

            afterComputedOrder?.Invoke(graphOutputs);
        }

        public static bool TypesAreConnectable(Type t1, Type t2)
        {
            if (t1 == null || t2 == null)
                return false;

            if (TypeAdapter.AreIncompatible(t1, t2))
                return false;

            //  Check if there is custom adapters for this assignation
            if (CustomPortIO.IsAssignable(t1, t2))
                return true;

            //  Check for type assignability
            if (t2.IsReallyAssignableFrom(t1))
                return true;

            // User defined type convertions
            if (TypeAdapter.AreAssignable(t1, t2))
                return true;

            // may diasplay type maps the nodeType
            if (TypeAdapter.AreCompatible(t1, t2))
                return true;

            return false;
        }

        public string AddExposedParameter(ExposedParameter parameter)
        {
            string guid = Guid.NewGuid().ToString();

            parameter.guid = guid;
            exposedParameters.Add(parameter);

            onExposedParameterListChanged?.Invoke();

            return guid;
        }

        public void RemoveExposedParameter(ExposedParameter ep)
        {
            exposedParameters.Remove(ep);

            onExposedParameterListChanged?.Invoke();
        }

        public void RemoveExposedParameter(string guid)
        {
            if (exposedParameters.RemoveAll(e => e.guid == guid) != 0)
                onExposedParameterListChanged?.Invoke();
        }

        internal void NotifyExposedParameterListChanged() => onExposedParameterListChanged?.Invoke();

        public void UpdateExposedParameter<T>(string guid, T value)
        {
            var param = exposedParameters.Find(e => e.guid == guid);
            if (param == null)
                return;

            if (value != null && !param.GetValueType().IsAssignableFrom(value.GetType()))
                throw new Exception("Type mismatch when updating parameter " + param.name + ": from " + param.GetValueType() + " to " + value.GetType().AssemblyQualifiedName);

            param.SetValue(value);
            onExposedParameterModified?.Invoke(param);
        }

        public void UpdateExposedParameterName(ExposedParameter parameter, string name)
        {
            parameter.name = name;
            onExposedParameterModified?.Invoke(parameter);
        }

        public void NotifyExposedParameterChanged(ExposedParameter parameter)
        {
            onExposedParameterModified?.Invoke(parameter);
        }

        public void NotifyExposedParameterValueChanged(ExposedParameter parameter)
        {
            onExposedParameterValueChanged?.Invoke(parameter);
        }

        public ExposedParameter GetExposedParameter(string name)
        {
            return exposedParameters.FirstOrDefault(e => e.name == name);
        }

        public ExposedParameter GetExposedParameterFromGUID(string guid)
        {
            return exposedParameters.FirstOrDefault(e => e?.guid == guid);
        }

        public bool SetParameterValue<T>(string name, T value)
        {
            var e = exposedParameters.FirstOrDefault(p => p.name == name);
            if (e == null)
                return false;

            e.SetValue(value);

            return true;
        }

        public T GetParameterValue<T>(string name)
        {
            var param = exposedParameters.FirstOrDefault(p => p.name == name);
            if (param is not null)
                return param.GetValue<T>();
            else
                return default(T);
        }

        public void LinkToScene(Scene scene)
        {
            _linkedScene = scene;
            onSceneLinked?.Invoke(scene);
        }

        public bool IsLinkedToScene() => _linkedScene.IsValid();

        public Scene GetLinkedScene() => _linkedScene;

        public string AddExposedParameter(string name, Type type, object value = null)
        {
            if (!type.IsSubclassOf(typeof(ExposedParameter)))
                Debug.LogError($"Can't add parameter of type {type}, the type doesn't inherit from ExposedParameter.");

            var param = Activator.CreateInstance(type) as ExposedParameter;
            // patch value with correct type:
            if (param.GetValueType().IsValueType)
                value = Activator.CreateInstance(param.GetValueType());

            param.Initialize(name, value);
            exposedParameters.Add(param);

            onExposedParameterListChanged?.Invoke();

            return param.guid;
        }

        void UpdateComputeOrderDepthFirst()
        {
            Stack<BaseNode> dfs = new Stack<BaseNode>();

            GraphUtils.FindCyclesInGraph(this, (n) => 
            {
                PropagateComputeOrder(n, loopComputeOrder);
            });

            int computeOrder = 0;
            foreach (var node in GraphUtils.DepthFirstSort(this))
            {
                if (node.computeOrder == loopComputeOrder)
                    continue;

                if (!node.canProcess)
                    node.computeOrder = -1;
                else
                    node.computeOrder = computeOrder++;
            }
        }

        int UpdateComputeOrderBreadthFirst(int depth, BaseNode node)
        {
            int computeOrder = 0;
            if (depth > s_maxComputeOrderDepth)
            {
                Debug.LogError("Recursion error while updating compute order");
                return -1;
            }

            if (_computeOrderDictionary.ContainsKey(node))
                return node.computeOrder;

            if (!_infiniteLoopTracker.Add(node))
                return -1;

            if (!node.canProcess)
            {
                node.computeOrder = -1;
                _computeOrderDictionary[node] = -1;
                return -1;
            }

            foreach (var dep in node.GetInputNodes())
            {
                int c = UpdateComputeOrderBreadthFirst(depth + 1, dep);

                if (c == -1)
                {
                    computeOrder = -1;
                    break;
                }

                computeOrder += c;
            }

            if (computeOrder != -1)
                computeOrder++;

            node.computeOrder = computeOrder;
            _computeOrderDictionary[node] = computeOrder;

            return computeOrder;
        }

        void PropagateComputeOrder(BaseNode node, int computeOrder)
        {
            Stack<BaseNode> deps = new Stack<BaseNode>();
            HashSet<BaseNode> loop = new HashSet<BaseNode>();

            deps.Push(node);
            while (deps.Count > 0)
            {
                var n = deps.Pop();
                n.computeOrder = computeOrder;

                if (!loop.Add(n))
                    continue;

                foreach (var dep in n.GetOutputNodes())
                    deps.Push(dep);
            }
        }

        void DestroyBrokenGraphElements()
        {
            edges.RemoveAll(e => e.inputNode == null
                || e.outputNode == null
                || string.IsNullOrEmpty(e.outputFieldName)
                || string.IsNullOrEmpty(e.inputFieldName)
            );
            nodes.RemoveAll(n => n == null);
        }
    }
}
