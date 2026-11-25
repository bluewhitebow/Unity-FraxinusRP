using System;
using System.Collections.Generic;
using UnityEngine;
using FraxinusEngine.Base;

namespace FraxinusEditor.Base
{
    [Serializable]
    public class ExposedParameterWorkaround : ScriptableObject
    {
        [SerializeReference]
        public List<ExposedParameter>   parameters = new List<ExposedParameter>();
        public BaseGraph                graph;
    }
}