using System;
using UnityEngine;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable]
    public class ContextScopeGroup : Group
    {
        public string contextBeginNodeGUID;
        public string contextEndNodeGUID;
        
        public ContextScopeGroup() { }
        
        public ContextScopeGroup(string beginNodeGUID, Vector2 position) : base("Sub Context Scope", position)
        {
            contextBeginNodeGUID = beginNodeGUID;
        }
        
        public override void OnCreated()
        {
            base.OnCreated();
            color = new Color(0.3f, 0.6f, 0.9f, 0.3f);
            size = new Vector2(600, 400);
            position.size = size;
        }
    }
}
