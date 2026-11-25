using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FraxinusEngine.Base
{
    public delegate IEnumerable<PortData> CustomPortBehaviorDelegate(List<SerializableEdge> edges);
    public delegate IEnumerable<PortData> CustomPortTypeBehaviorDelegate(string fieldName, string displayName, object value);

    [Serializable]
    public abstract class BaseNode
    {
        internal class NodeFieldInformation
        {
            public string       name;
            public string       fieldName;
            public string       tooltip;
            public FieldInfo    info;
            public bool         input;
            public bool         isMultiple;
            public bool         vertical;

            public CustomPortBehaviorDelegate behavior;

            public NodeFieldInformation(FieldInfo info, string name, bool input, bool isMultiple, string tooltip, bool vertical, CustomPortBehaviorDelegate behavior)
            {
                this.input = input;
                this.isMultiple = isMultiple;
                this.tooltip = tooltip;
                this.info = info;
                this.name = name;
                this.fieldName = info.Name;
                this.vertical = vertical;
                this.behavior = behavior;
            }
        }

        struct PortUpdate
        {
            public List<string>     fieldNames;
            public BaseNode         node;

            public void Deconstruct(out List<string> fieldNames, out BaseNode node)
            {
                fieldNames = this.fieldNames;
                node = this.node;
            }
        }

        public delegate void ProcessDelegate();

        [SerializeField]
        internal string nodeCustomName = null;

        public virtual string   name => GetType().Name;
        public virtual Color    color => Color.clear;
        public virtual string   layoutStyle => string.Empty;
        public virtual bool     unlockable => true;             // If the node can be locked or not
        public virtual bool     isLocked => nodeLock;           // Is the node is locked (if locked it can't be moved)

        public string           guid;
        public int              computeOrder = -1;
        public int              excuteOrder = -1;
        public virtual bool     canProcess => true;
        public virtual bool     showControlsOnHover => false;   // Show the node controlContainer only when the mouse is over the node
        public virtual bool     deletable => true;

        [NonSerialized]
        public readonly NodeInputPortContainer inputPorts;

        [NonSerialized]
        public readonly NodeOutputPortContainer outputPorts;

        public Rect             position;
        public bool             expanded;
        public bool             debug;
        public bool             nodeLock;

        /// <summary>
        /// Triggered when the node is processes
        /// </summary>
        public event ProcessDelegate onProcessed;
        public event Action<string, NodeMessageType> onMessageAdded;
        public event Action<string> onMessageRemoved;

        public event Action<SerializableEdge> onAfterEdgeConnected;     // Triggered after an edge was connected on the node
        public event Action<SerializableEdge> onAfterEdgeDisconnected;  // Triggered after an edge was disconnected on the node
        public event Action<string> onPortsUpdated;                     // Triggered after a single/list of port(s) is updated, the parameter is the field name

        [NonSerialized]
        bool _needsInspector = false;

        public virtual bool     needsInspector => _needsInspector;
        public virtual bool     isRenamable => false;

        public bool             createdFromDuplication { get; internal set; } = false;
        public bool             createdWithinGroup { get; internal set; } = false;

        [NonSerialized]
        internal Dictionary<string, NodeFieldInformation> nodeFields = new Dictionary<string, NodeFieldInformation>();

        [NonSerialized]
        internal Dictionary<Type, CustomPortTypeBehaviorDelegate> customPortTypeBehaviorMap = new Dictionary<Type, CustomPortTypeBehaviorDelegate>();

        [NonSerialized]
        List<string> _messages = new List<string>();

        [NonSerialized]
        protected BaseGraph graph;

        Stack<PortUpdate> _fieldsToUpdate = new Stack<PortUpdate>();
        HashSet<PortUpdate> _updatedFields = new HashSet<PortUpdate>();

        public static T CreateFromType<T>(Vector2 position) where T : BaseNode
        {
            return CreateFromType(typeof(T), position) as T;
        }

        public static BaseNode CreateFromType(Type nodeType, Vector2 position)
        {
            if (!nodeType.IsSubclassOf(typeof(BaseNode)))
                return null;

            var node = Activator.CreateInstance(nodeType) as BaseNode;
            node.position = new Rect(position, new Vector2(100, 100));
            node.OnNodeCreated();

            return node;
        }

        #region Initialization

        public void Initialize(BaseGraph graph)
        {
            this.graph = graph;

            Enable();

            InitializePorts();
        }

        public virtual void InitializePorts()
        {
            InitializeCustomPortTypeMethods();

            foreach (FieldInfo key in OverrideFieldOrder(nodeFields.Values.Select(k => k.info)))
            {
                var nodeField = nodeFields[key.Name];
                if (HasCustomBehavior(nodeField))
                    UpdatePortsForField(nodeField.fieldName, sendPortUpdatedEvent: false);
                else
                {
                    // If we don't have a custom behavior on the node, we just have to create a simple port
                    AddPort(nodeField.input, nodeField.fieldName, 
                        new PortData 
                        { 
                            acceptMultipleEdges = nodeField.isMultiple, 
                            displayName = nodeField.name, 
                            tooltip = nodeField.tooltip, 
                            vertical = nodeField.vertical
                        }
                    );
                }
            }
        }

        /// <summary>
        /// Override the field order inside the node.
        /// </summary>
        public virtual IEnumerable<FieldInfo> OverrideFieldOrder(IEnumerable<FieldInfo> fields)
        {
            long GetFieldInheritanceLevel(FieldInfo f)
            {
                int level = 0;
                var t = f.DeclaringType;
                while (t != null)
                {
                    t = t.BaseType;
                    level++;
                }

                return level;
            }

            // 按照metadata 和 继承优先级进行排序，以便与端口顺序同步(确保 filed drawers 位于正确端口的旁边)
            return fields.OrderByDescending(f => (long)(((GetFieldInheritanceLevel(f) << 32)) | (long)f.MetadataToken));
        }

        void InitializeCustomPortTypeMethods()
        {
            MethodInfo[] methods = new MethodInfo[0];
            Type baseType = GetType();

            while (true)
            {
                methods = baseType.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var method in methods)
                {
                    var typeBehaviors = method.GetCustomAttributes<CustomPortTypeBehavior>().ToArray();
                    if (typeBehaviors.Length == 0)
                        continue;

                    var deleg = method.ContainsGenericParameters ? null : Delegate.CreateDelegate(
                        typeof(CustomPortTypeBehaviorDelegate), this, method, throwOnBindFailure: false) as CustomPortTypeBehaviorDelegate;
                    if (deleg == null)
                        Debug.LogError($"Cannot convert method {method} to a delegate of type {typeof(CustomPortTypeBehaviorDelegate)}");

                    foreach (var typeBehavior in typeBehaviors)
                        customPortTypeBehaviorMap[typeBehavior.type] = deleg;
                }

                // Try to also find private methods in the base class
                baseType = baseType.BaseType;
                if (baseType == null)
                    break;
            }
        }

        /// <summary>
        /// Update the ports related to one C# property field and all connected nodes in the graph
        /// </summary>
        public bool UpdatePortsForField(string fieldName, bool sendPortUpdatedEvent = true)
        {
            bool changed = false;

            _fieldsToUpdate.Clear();
            _updatedFields.Clear();

            _fieldsToUpdate.Push(new PortUpdate { fieldNames = new List<string>() { fieldName }, node = this });

            while (_fieldsToUpdate.Count != 0)
            {
                var (fields, node) = _fieldsToUpdate.Pop();

                // Avoid updating twice a port
                if (_updatedFields.Any((t) => t.node == node && fields.SequenceEqual(t.fieldNames)))
                    continue;

                _updatedFields.Add(new PortUpdate { fieldNames = fields, node = node });

                foreach (var field in fields)
                {
                    if (node.UpdatePortsForFieldLocal(field, sendPortUpdatedEvent))
                    {
                        foreach (var port in node.IsFieldInput(field) ? (NodePortContainer)node.inputPorts : node.outputPorts)
                        {
                            if (port.fieldName != field)
                                continue;

                            foreach (var edge in port.GetEdges())
                            {
                                var edgeNode = (node.IsFieldInput(field)) ? edge.outputNode : edge.inputNode;
                                var fieldsWithBehavior = edgeNode.nodeFields.Values.Where(f => HasCustomBehavior(f)).Select(f => f.fieldName).ToList();
                                _fieldsToUpdate.Push(new PortUpdate { fieldNames = fieldsWithBehavior, node = edgeNode });
                            }
                        }
                        changed = true;
                    }
                }
            }

            return changed;
        }

        /// <summary>
        /// Update the ports related to one C# property field (only for this node)
        /// </summary>
        public bool UpdatePortsForFieldLocal(string fieldName, bool sendPortUpdatedEvent = true)
        {
            bool changed = false;
            if (!nodeFields.ContainsKey(fieldName))
                return false;

            var fieldInfo = nodeFields[fieldName];
            if (!HasCustomBehavior(fieldInfo))
                return false;

            List<string> finalPorts = new List<string>();

            var portCollection = fieldInfo.input ? (NodePortContainer)inputPorts : outputPorts;

            // Gather all fields for this port
            var nodePorts = portCollection.Where(p => p.fieldName == fieldName);
            // Gather all edges connected to these fields:
            var edges = nodePorts.SelectMany(n => n.GetEdges()).ToList();

            if (fieldInfo.behavior != null)
            {
                foreach (var portData in fieldInfo.behavior(edges))
                    AddPortData(portData);
            }
            else
            {
                var customPortTypeBehavior = customPortTypeBehaviorMap[fieldInfo.info.FieldType];
                foreach (var portData in customPortTypeBehavior(fieldName, fieldInfo.name, fieldInfo.info.GetValue(this)))
                    AddPortData(portData);
            }

            void AddPortData(PortData portData)
            {
                // Guard using the port identifier so we don't duplicate identifiers
                var port = nodePorts.FirstOrDefault(n => n.portData.identifier == portData.identifier);
                if (port == null)
                {
                    AddPort(fieldInfo.input, fieldName, portData);
                    changed = true;
                }
                else
                {
                    // in case the port type have changed for an incompatible type, we disconnect all the edges attached to this port
                    if (!BaseGraph.TypesAreConnectable(port.portData.displayType, portData.displayType))
                    {
                        foreach (var edge in port.GetEdges().ToList())
                            graph.Disconnect(edge.guid);
                    }

                    // patch the port data
                    if (port.portData != portData)
                    {
                        port.portData.CopyFrom(portData);
                        changed = true;
                    }
                }

                finalPorts.Add(portData.identifier);
            }

            // TODO
            // Remove only the ports that are no more in the list
            if (nodePorts != null)
            {
                var currentPortsCopy = nodePorts.ToList();
                foreach (var currentPort in currentPortsCopy)
                {
                    // If the current port does not appear in the list of final ports, we remove it
                    if (!finalPorts.Any(id => id == currentPort.portData.identifier))
                    {
                        RemovePort(fieldInfo.input, currentPort);
                        changed = true;
                    }
                }
            }

            // Make sure the port order is correct:
            portCollection.Sort((p1, p2) => {
                int p1Index = finalPorts.FindIndex(id => p1.portData.identifier == id);
                int p2Index = finalPorts.FindIndex(id => p2.portData.identifier == id);

                if (p1Index == -1 || p2Index == -1)
                    return 0;

                return p1Index.CompareTo(p2Index);
            });

            if (sendPortUpdatedEvent)
                onPortsUpdated?.Invoke(fieldName);

            return changed;
        }

        internal void DisableInternal()
        {
            // port containers are initialized in the OnEnable
            inputPorts.Clear();
            outputPorts.Clear();

            Disable();
        }

        internal void DestroyInternal() => Destroy();

        public virtual void OnNodeCreated() => guid = Guid.NewGuid().ToString();

        public virtual FieldInfo[] GetNodeFields() 
            => GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(x => x.FieldType.IsSubclassOf(typeof(NodeParameter))).ToArray();

        public bool UpdateAllPorts()
        {
            bool changed = false;
            foreach (var key in OverrideFieldOrder(nodeFields.Values.Select(k => k.info)))
            {
                var field = nodeFields[key.Name];
                changed |= UpdatePortsForField(field.fieldName);
            }

            return changed;
        }

        protected BaseNode()
        {
            inputPorts = new NodeInputPortContainer(this);
            outputPorts = new NodeOutputPortContainer(this);

            InitializeInOutDatas();
        }

        void InitializeInOutDatas()
        {
            var fields = GetNodeFields();
            var methods = GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                var inputAttribute      = field.GetCustomAttribute<InputAttribute>();
                var outputAttribute     = field.GetCustomAttribute<OutputAttribute>();
                var tooltipAttribute    = field.GetCustomAttribute<TooltipAttribute>();
                var showInInspector     = field.GetCustomAttribute<ShowInInspector>();
                var vertical            = field.GetCustomAttribute<VerticalAttribute>();

                if (showInInspector != null)
                    _needsInspector = true;

                if (inputAttribute == null && outputAttribute == null)
                    continue;

                var nodeFiled = field.GetValue(this) as NodeParameter;
                if (nodeFiled == null && field.FieldType != typeof(NodeParameter))
                {
                    Type fieldType = field.FieldType;
                    object newValue = Activator.CreateInstance(fieldType);
                    field.SetValue(this, newValue);
                }

                // Check if field is a collection type
                bool isMultiple = (inputAttribute != null) ? inputAttribute.allowMultiple : (outputAttribute.allowMultiple);
                bool input = inputAttribute != null;
                string tooltip = tooltipAttribute?.tooltip;

                string name = field.Name;
                if (!String.IsNullOrEmpty(inputAttribute?.name))
                    name = inputAttribute.name;

                if (!String.IsNullOrEmpty(outputAttribute?.name))
                    name = outputAttribute.name;

                // By default we set the behavior to null, if the field have a custom behavior, it will be set in the loop just below
                nodeFields[field.Name] = new NodeFieldInformation(field, name, input, isMultiple, tooltip, vertical != null, null);
            }

            foreach (var method in methods)
            {
                var customPortBehaviorAttribute = method.GetCustomAttribute<CustomPortBehaviorAttribute>();
                CustomPortBehaviorDelegate behavior = null;

                if (customPortBehaviorAttribute == null)
                    continue;

                behavior = method.ContainsGenericParameters ? null : (CustomPortBehaviorDelegate)Delegate.CreateDelegate(
                    typeof(CustomPortBehaviorDelegate), this, method, throwOnBindFailure: false);
                if (behavior == null)
                    Debug.LogError($"The function {method} cannot be converted to the required delegate format: {typeof(CustomPortBehaviorDelegate)}");

                if (nodeFields.ContainsKey(customPortBehaviorAttribute.fieldName))
                    nodeFields[customPortBehaviorAttribute.fieldName].behavior = behavior;
                else
                    Debug.LogError($"Invalid field name for custom port behavior: {method}, {customPortBehaviorAttribute.fieldName}");
            }
        }

        bool HasCustomBehavior(NodeFieldInformation info)
        {
            if (info.behavior != null)
                return true;

            if (customPortTypeBehaviorMap.ContainsKey(info.info.FieldType))
                return true;

            return false;
        }

        #endregion

        #region Events and Processing
        public void OnEdgeConnected(SerializableEdge edge)
        {
            bool input = edge.inputNode == this;
            NodePortContainer portCollection = (input) ? (NodePortContainer)inputPorts : outputPorts;

            portCollection.Add(edge);
            UpdateAllPorts();

            onAfterEdgeConnected?.Invoke(edge);
        }

        public void OnEdgeDisconnected(SerializableEdge edge)
        {
            if (edge == null)
                return;

            bool input = edge.inputNode == this;
            NodePortContainer portCollection = (input) ? (NodePortContainer)inputPorts : outputPorts;

            portCollection.Remove(edge);

            // Reset default values of input port:
            bool haveConnectedEdges = edge.inputNode.inputPorts.Where(p => p.fieldName == edge.inputFieldName).Any(p => p.GetEdges().Count != 0);
            if (edge.inputNode == this && !haveConnectedEdges && CanResetPort(edge.inputPort))
                edge.inputPort?.ResetToDefault();

            UpdateAllPorts();

            onAfterEdgeDisconnected?.Invoke(edge);
        }

        public void OnProcess()
        {
            inputPorts.PullDatas();

            Process();

            InvokeOnProcessed();

            outputPorts.PushDatas();
        }

        public void InvokeOnProcessed() => onProcessed?.Invoke();

        protected virtual void Enable() { }
        protected virtual void Disable() { }
        protected virtual void Destroy() { }
        protected virtual void Process() { }
        protected virtual bool CanResetPort(NodePort port) => true;

        #endregion

        #region API and utils
        public void AddPort(bool input, string fieldName, PortData portData)
        {
            if (portData.displayType == null)
                portData.displayType = nodeFields[fieldName].info.FieldType;

            if (input)
                inputPorts.Add(new NodePort(this, fieldName, portData));
            else
                outputPorts.Add(new NodePort(this, fieldName, portData));
        }

        public void RemovePort(bool input, NodePort port)
        {
            if (input)
                inputPorts.Remove(port);
            else
                outputPorts.Remove(port);
        }

        public void RemovePort(bool input, string fieldName)
        {
            if (input)
                inputPorts.RemoveAll(p => p.fieldName == fieldName);
            else
                outputPorts.RemoveAll(p => p.fieldName == fieldName);
        }

        public IEnumerable<BaseNode> GetInputNodes()
        {
            foreach (var port in inputPorts)
            {
                foreach (var edge in port.GetEdges())
                    yield return edge.outputNode;
            }
        }

        public IEnumerable<BaseNode> GetOutputNodes()
        {
            foreach (var port in outputPorts)
            {
                foreach (var edge in port.GetEdges())
                    yield return edge.inputNode;
            }
        }

        /// <summary>
        /// Return a node matching the condition in the dependencies of the node
        /// </summary>
        public BaseNode FindInDependencies(Func<BaseNode, bool> condition)
        {
            Stack<BaseNode> dependencies = new Stack<BaseNode>();

            dependencies.Push(this);

            int depth = 0;
            while (dependencies.Count > 0)
            {
                var node = dependencies.Pop();

                // Guard for infinite loop (faster than a HashSet based solution)
                depth++;
                if (depth > 2000)
                    break;

                if (condition(node))
                    return node;

                foreach (var dep in node.GetInputNodes())
                    dependencies.Push(dep);
            }
            return null;
        }

        /// <summary>
        /// Get the port from field name and identifier
        /// </summary>
        public NodePort GetPort(string fieldName, string identifier)
        {
            return inputPorts.Concat(outputPorts).FirstOrDefault(p => 
            {
                var bothNull = String.IsNullOrEmpty(identifier) && String.IsNullOrEmpty(p.portData.identifier);
                return p.fieldName == fieldName && (bothNull || identifier == p.portData.identifier);
            });
        }

        public IEnumerable<NodePort> GetAllPorts()
        {
            foreach (var port in inputPorts)
                yield return port;

            foreach (var port in outputPorts)
                yield return port;
        }

        public IEnumerable<SerializableEdge> GetAllEdges()
        {
            foreach (var port in GetAllPorts())
            {
                foreach (var edge in port.GetEdges())
                    yield return edge;
            }
        }

        public bool IsFieldInput(string fieldName) => nodeFields[fieldName].input;

        public void AddMessage(string message, NodeMessageType messageType)
        {
            if (_messages.Contains(message))
                return;

            onMessageAdded?.Invoke(message, messageType);
            _messages.Add(message);
        }

        public void RemoveMessage(string message)
        {
            onMessageRemoved?.Invoke(message);
            _messages.Remove(message);
        }

        public void RemoveMessageContains(string subMessage)
        {
            string toRemove = _messages.Find(m => m.Contains(subMessage));
            _messages.Remove(toRemove);
            onMessageRemoved?.Invoke(toRemove);
        }

        public void ClearMessages()
        {
            foreach (var message in _messages)
                onMessageRemoved?.Invoke(message);

            _messages.Clear();
        }

        public void SetCustomName(string customName) => nodeCustomName = customName;

        public string GetCustomName() => String.IsNullOrEmpty(nodeCustomName) ? name : nodeCustomName;

#if UNITY_EDITOR
        internal virtual void OnValidate() { }
#endif

        #endregion
    }

}