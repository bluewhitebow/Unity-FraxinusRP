using System;
using System.Collections.Generic;
using System.Linq;
using FraxinusEngine.Base;
using UnityEditor;

namespace FraxinusEditor.Base
{
    public static class StackNodeViewProvider
    {
        static Dictionary<Type, Type> s_stackNodeViewPerType = new Dictionary<Type, Type>();

        static StackNodeViewProvider()
        {
            foreach (var t in TypeCache.GetTypesWithAttribute<CustomStackNodeView>())
            {
                var attr = t.GetCustomAttributes(false).Select(a => a as CustomStackNodeView).FirstOrDefault();
                s_stackNodeViewPerType.Add(attr.stackNodeType, t);
            }
        }

        public static Type GetStackNodeCustomViewType(Type stackNodeType)
        {
            foreach (var t in s_stackNodeViewPerType)
            {
                // Debug.Log(t.Key + " -> " + t.Value);
            }
            s_stackNodeViewPerType.TryGetValue(stackNodeType, out var view);
            return view;
        }
    }
}