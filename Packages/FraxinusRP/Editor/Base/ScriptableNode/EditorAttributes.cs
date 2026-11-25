using System;

namespace FraxinusEditor.Base
{
    [AttributeUsage(AttributeTargets.Class)]
    public class NodeCustomEditor : Attribute
    {
        public Type nodeType;
    
        public NodeCustomEditor(Type nodeType)
        {
            this.nodeType = nodeType;
        }
    }
}