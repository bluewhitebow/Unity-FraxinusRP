using System;
using System.Collections.Generic;
using UnityEngine;

namespace FraxinusEngine.Base
{
    [Serializable]
    public class SerializableType : ISerializationCallbackReceiver
    {
        static Dictionary<string, Type> s_typeCache = new Dictionary<string, Type>();
        static Dictionary<Type, string> s_typeNameCache = new Dictionary<Type, string>();

        [SerializeField]
        public string serializedType;

        [NonSerialized]
        public Type type;

        public SerializableType(Type t)
        {
            type = t;
        }

        public void OnAfterDeserialize()
        {
            if (!String.IsNullOrEmpty(serializedType))
            {
                if (!s_typeCache.TryGetValue(serializedType, out type))
                {
                    type = ResolveType(serializedType);
                    if (type != null)
                        s_typeCache[serializedType] = type;
                }
            }
        }

        public void OnBeforeSerialize()
        {
            if (type != null)
            {
                if (!s_typeNameCache.TryGetValue(type, out serializedType))
                {
                    serializedType = type.AssemblyQualifiedName;
                    s_typeNameCache[type] = serializedType;
                }
            }
        }

        internal static Type ResolveType(string assemblyQualifiedName)
        {
            if (string.IsNullOrEmpty(assemblyQualifiedName))
                return null;

#if UNITY_EDITOR
            const string oldAssembly = "Unity.RenderPipelines.Universal.Editor";
            const string newAssembly = "Fraxinus.UltraRP.Editor";
            const string ultraNamespace = "FraxinusEditor.Rendering.Ultra.";
            int typeSeparator = assemblyQualifiedName.IndexOf(',');
            if (typeSeparator > 0)
            {
                string typeName = assemblyQualifiedName.Substring(0, typeSeparator).Trim();
                string assemblyName = assemblyQualifiedName.Substring(typeSeparator + 1).Trim();
                int versionSeparator = assemblyName.IndexOf(',');
                if (versionSeparator >= 0)
                    assemblyName = assemblyName.Substring(0, versionSeparator).Trim();
                if (typeName.StartsWith(ultraNamespace, StringComparison.Ordinal)
                    && assemblyName == oldAssembly)
                    return Type.GetType(typeName + ", " + newAssembly, false);
            }
#endif
            return Type.GetType(assemblyQualifiedName, false);
        }
    }
}
