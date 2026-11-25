using System;
using System.Collections.Generic;
using UnityEngine;

namespace FraxinusEngine.Base
{
    public class ParameterNode : BaseNode
    {
        [Output]
        public ExposedNodeParameter output;

        public override string name => "Parameter";

        [SerializeField, HideInInspector]
        public string parameterGUID;

        public ExposedParameter parameter { get; private set; }

        public event Action onParameterChanged;

        protected override void Enable()
        {
            // load the parameter
            LoadExposedParameter();

            graph.onExposedParameterModified += OnParamChanged;
            if (onParameterChanged != null)
                onParameterChanged?.Invoke();
        }

        void LoadExposedParameter()
        {
            parameter = graph.GetExposedParameterFromGUID(parameterGUID);

            if (parameter == null)
            {
                Debug.Log($"Property \"" + parameterGUID + "\" Can't be found !");

                // Delete this node as the property can't be found
                graph.RemoveNode(this);
                return;
            }

            output.value = parameter;
        }

        void OnParamChanged(ExposedParameter modifiedParam)
        {
            if (parameter == modifiedParam)
                onParameterChanged?.Invoke();
        }

        [CustomPortBehavior(nameof(output))]
        IEnumerable<PortData> GetOutputPort(List<SerializableEdge> edges)
        {
            yield return new PortData
            {
                identifier = "output",
                displayName = "Value",
                displayType = parameter.GetValueType(),
                acceptMultipleEdges = true
            };
        }

        protected override void Process()
        {

#if UNITY_EDITOR
            // parameter = graph.GetExposedParameterFromGUID(parameterGUID); // 有不少 GC
#endif

            ClearMessages();
            if (parameter == null)
            {
                AddMessage($"Parameter not found: {parameterGUID}", NodeMessageType.Error);
                return;
            }

            output.value = parameter;
        }

    }
}