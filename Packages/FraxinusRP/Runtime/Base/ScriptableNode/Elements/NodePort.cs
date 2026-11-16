using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace FraxinusEngine.Base
{
    public class PortData : IEquatable<PortData>
    {
        public string   identifier;
        public string   tooltip;
        public string   displayName;
        public Type     displayType;
        public int      sizeInPixel;
        public bool     acceptMultipleEdges;
        public bool     vertical;

        public bool Equals(PortData other)
        {
            return identifier == other.identifier
                && tooltip == other.tooltip
                && displayName == other.displayName
                && displayType == other.displayType
                && sizeInPixel == other.sizeInPixel
                && acceptMultipleEdges == other.acceptMultipleEdges
                && vertical == other.vertical;
        }

        public void CopyFrom(PortData other)
        {
            identifier          = other.identifier;
            tooltip             = other.tooltip;
            displayName         = other.displayName;
            displayType         = other.displayType;
            sizeInPixel         = other.sizeInPixel;
            acceptMultipleEdges = other.acceptMultipleEdges;
            vertical            = other.vertical;
        }
    }

    public class NodePort
    {
        public delegate void PushDataDelegate();

        public string           fieldName;
        public BaseNode         owner;
        public PortData         portData;
        public FieldInfo        fieldInfo;

        NodeParameter           _nodeField;

        List<SerializableEdge>  _edges = new List<SerializableEdge>();
        Dictionary<SerializableEdge, PushDataDelegate> _pushDataDelegates = new Dictionary<SerializableEdge, PushDataDelegate>();
        List<SerializableEdge> _edgeWithRemoteCustomIO = new List<SerializableEdge>();

        CustomPortIODelegate _customPortIOMethod;

        public NodePort(BaseNode owner, string fieldName, PortData portData)
        {
            this.fieldName = fieldName;
            this.owner = owner;
            this.portData = portData;

            fieldInfo = owner.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
#if UNITY_EDITOR
            if (!fieldInfo.FieldType.IsSubclassOf(typeof(NodeParameter)))
                throw new Exception($"The field:{GetType().Name}.fieldName is not the type inheritanced from NodeParameter(Class)");
#endif
            _nodeField = fieldInfo.GetValue(owner) as NodeParameter;

            _customPortIOMethod = CustomPortIO.GetCustomPortMethod(owner.GetType(), fieldName);
        }

        /// <summary>
        /// Connect an edge to this port
        /// </summary>
        public void Add(SerializableEdge edge)
        {
            if (!_edges.Contains(edge))
                _edges.Add(edge);

            if (edge.inputNode == owner)
            {
                if (edge.outputPort._customPortIOMethod is not null)
                    _edgeWithRemoteCustomIO.Add(edge);
            }
            else
            {
                if (edge.inputPort._customPortIOMethod is not null)
                    _edgeWithRemoteCustomIO.Add(edge);
            }

            // 有继承就不用默认
            if (edge.inputPort._customPortIOMethod is not null || edge.outputPort._customPortIOMethod is not null)
                return;

            PushDataDelegate edgeDelegate = CreatePushDataDelegateForEdge(edge);
            if (edgeDelegate is not null)
                _pushDataDelegates[edge] = edgeDelegate;
        }

        PushDataDelegate CreatePushDataDelegateForEdge(SerializableEdge edge)
        {
#if ENABLE_IL2CPP
            return CreateAotPushDataDelegateForEdge(edge);
#else
            // Creation of the delegate to move the data from the input node to the output node:
            FieldInfo inputField = edge.inputNode.GetType().GetField(edge.inputFieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo outputField = edge.outputNode.GetType().GetField(edge.outputFieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Type inType, outType;

#if UNITY_EDITOR
            if (!BaseGraph.TypesAreConnectable(outputField.FieldType, inputField.FieldType))
            {
                Debug.LogError($"Can't convert from {inputField.FieldType} to {outputField.FieldType}, you must specify a custom port function (i.e CustomPortInput or CustomPortOutput) for non-implicit convertions");
                return null;
            }
#endif
            Expression inputParamField = Expression.Field(Expression.Constant(edge.inputNode), inputField);
            Expression outputParamField = Expression.Field(Expression.Constant(edge.outputNode), outputField);

            // todo：remove
            //inType = edge.inputPort.portData.displayType ?? inputField.FieldType;
            //outType = edge.outputPort.portData.displayType ?? outputField.FieldType;

            inType = inputField.FieldType;
            outType = outputField.FieldType;

            // 有用户层定义的转换function就调用
            if (TypeAdapter.AreAssignable(outType, inType))
            {
                MethodCallExpression call = Expression.Call(TypeAdapter.GetConvertionMethod(outType, inType), outputParamField, inputParamField);
                return Expression.Lambda<PushDataDelegate>(call).Compile();
            }
            else
            {
                if (inType.IsSubclassOf(typeof(NodeParameter)) || outType.IsSubclassOf(typeof(NodeParameter)))
                {
                    if (inType.IsAssignableFrom(outType))
                    {
                        MethodCallExpression call = Expression.Call(inputParamField, typeof(NodeParameter).GetMethod("SetParamterValue", BindingFlags.Public | BindingFlags.Instance), outputParamField);
                        return Expression.Lambda<PushDataDelegate>(call).Compile();
                    }
                }
            }

            return null;
#endif
        }

        internal static PushDataDelegate CreateAotPushDataDelegateForEdge(SerializableEdge edge)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo inputField = edge.inputNode.GetType().GetField(edge.inputFieldName, flags);
            FieldInfo outputField = edge.outputNode.GetType().GetField(edge.outputFieldName, flags);
            Type inputType = inputField.FieldType;
            Type outputType = outputField.FieldType;

            if (TypeAdapter.AreAssignable(outputType, inputType))
            {
                MethodInfo conversion = TypeAdapter.GetConvertionMethod(outputType, inputType);
                // Reuse arguments while reading the current port fields, matching the compiled delegate.
                object[] arguments = new object[2];
                return () =>
                {
                    arguments[0] = outputField.GetValue(edge.outputNode);
                    arguments[1] = inputField.GetValue(edge.inputNode);
                    conversion.Invoke(null, arguments);
                };
            }

            if (inputType.IsAssignableFrom(outputType) && inputType.IsSubclassOf(typeof(NodeParameter)))
            {
                return () => ((NodeParameter)inputField.GetValue(edge.inputNode))
                    .SetParamterValue((NodeParameter)outputField.GetValue(edge.outputNode));
            }

            return null;
        }

        /// <summary>
        /// Disconnect an Edge from this port
        /// </summary>
        public void Remove(SerializableEdge edge)
        {
            if (!_edges.Contains(edge))
                return;

            _pushDataDelegates.Remove(edge);
            _edgeWithRemoteCustomIO.Remove(edge);
            _edges.Remove(edge);
        }

        public List<SerializableEdge> GetEdges() => _edges;

        /// <summary>
        /// Push the value of the port through the edges
        /// This method can only be called on output ports
        /// </summary>
        public void PushData()
        {
            if (_customPortIOMethod is not null)
            {
                _customPortIOMethod(owner, _edges, this);
                return;
            }

            foreach (var pushDataDelegate in _pushDataDelegates)
                pushDataDelegate.Value();

            if (_edgeWithRemoteCustomIO.Count == 0)
                return;

            foreach (var edge in _edgeWithRemoteCustomIO)
                edge.passThroughBuffer = _nodeField;
        }

        /// <summary>
        /// Pull values from the edge (in case of a custom convertion method)
        /// This method can only be called on input ports
        /// </summary>
        public void PullData()
        {
            if (_customPortIOMethod is not null)
            {
                _customPortIOMethod(owner, _edges, this);
                return;
            }

            if (_edgeWithRemoteCustomIO.Count == 0)
                return;

            if (_edges.Count > 0)
            {
                var passThroughObject = _edges.First().passThroughBuffer;
                if (passThroughObject != null)
                {
                    if (fieldInfo.FieldType == passThroughObject.GetType())
                        _nodeField.SetParamterValue(passThroughObject);
                }
            }
        }

        public void ResetToDefault()
        {
            _nodeField.Default();
        }
    }

    public abstract class NodePortContainer : List<NodePort>
    {
        protected BaseNode _node;

        public NodePortContainer(BaseNode node)
        {
            _node = node;
        }

        public void Remove(SerializableEdge edge)
        {
            ForEach(p => p.Remove(edge));
        }

        public void Add(SerializableEdge edge)
        {
            bool isInput = edge.inputNode == _node;
            string portFieldName = isInput ? edge.inputFieldName : edge.outputFieldName;
            string portIdentifier = isInput ? edge.inputPortIdentifier : edge.outputPortIdentifier;

            if (String.IsNullOrEmpty(portIdentifier))
                portIdentifier = null;

            var port = this.FirstOrDefault(p =>
            {
                return p.fieldName == portFieldName && p.portData.identifier == portIdentifier;
            });

            if (port == null)
            {
                Debug.LogError("The edge can't be properly connected because it's ports can't be found");
                return;
            }

            port.Add(edge);
        }
    }

    public class NodeInputPortContainer : NodePortContainer
    {
        public NodeInputPortContainer(BaseNode node) : base(node) { }

        public void PullDatas()
        {
            ForEach(p => p.PullData());
        }
    }

    public class NodeOutputPortContainer : NodePortContainer
    {
        public NodeOutputPortContainer(BaseNode node) : base(node) { }

        public void PushDatas()
        {
            ForEach(p => p.PushData());
        }
    }

}
