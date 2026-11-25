using System;
using UnityEngine;

namespace FraxinusEngine.Base
{
    [Serializable]
    public class SerializableEdge : ISerializationCallbackReceiver
    {
        public string guid;

        [SerializeField]
        BaseGraph owner;

        [SerializeField]
        string inputNodeGUID;
        [SerializeField]
        string outputNodeGUID;

        public string inputNodeGuid => inputNode?.guid ?? inputNodeGUID;
        public string outputNodeGuid => outputNode?.guid ?? outputNodeGUID;

        [NonSerialized]
        public BaseNode inputNode;
        [NonSerialized]
        public BaseNode outputNode;

        [NonSerialized]
        public NodePort inputPort;
        [NonSerialized]
        public NodePort outputPort;

        [NonSerialized]
        public NodeParameter passThroughBuffer;

        public string inputFieldName;
        public string outputFieldName;

        public string inputPortIdentifier;
        public string outputPortIdentifier;

        public SerializableEdge() { }

        public static SerializableEdge CreateNewEdge(BaseGraph graph, NodePort inputPort, NodePort outputPort)
        {
            SerializableEdge edge = new SerializableEdge();

            edge.owner = graph;
            edge.guid = Guid.NewGuid().ToString();
            edge.inputNode = inputPort.owner;
            edge.inputFieldName = inputPort.fieldName;
            edge.outputNode = outputPort.owner;
            edge.outputFieldName = outputPort.fieldName;
            edge.inputPort = inputPort;
            edge.outputPort = outputPort;
            edge.inputPortIdentifier = inputPort.portData.identifier;
            edge.outputPortIdentifier = outputPort.portData.identifier;

            return edge;
        }

        public void OnBeforeSerialize()
        {
            if (outputNode == null || inputNode == null)
                return;

            outputNodeGUID = outputNode.guid;
            inputNodeGUID = inputNode.guid;
        }

        public void OnAfterDeserialize() { }

        internal void Deserialize(BaseGraph graph)
        {
            // JsonUtility cloning retains Unity object references. Edges belong
            // to the graph being initialized, including after a JSON overwrite.
            owner = graph;
            Deserialize();
        }

        // Here our owner have been deserialized
        public void Deserialize()
        {
            inputNode = outputNode = null;
            inputPort = outputPort = null;
            if (owner == null || string.IsNullOrEmpty(outputNodeGUID) || string.IsNullOrEmpty(inputNodeGUID)
                || !owner.nodesPerGUID.ContainsKey(outputNodeGUID) || !owner.nodesPerGUID.ContainsKey(inputNodeGUID))
                return;

            outputNode = owner.nodesPerGUID[outputNodeGUID];
            inputNode = owner.nodesPerGUID[inputNodeGUID];
            inputPort = inputNode.GetPort(inputFieldName, inputPortIdentifier);
            outputPort = outputNode.GetPort(outputFieldName, outputPortIdentifier);
        }

        public override string ToString() => $"{outputNode.name}:{outputPort.fieldName} -> {inputNode.name}:{inputPort.fieldName}";
    }

}
