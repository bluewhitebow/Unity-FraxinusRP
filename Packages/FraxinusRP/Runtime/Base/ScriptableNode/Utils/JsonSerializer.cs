using System;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FraxinusEngine.Base
{
    [Serializable]
    public struct JsonElement
    { 
        public string type;
        public string jsonDatas;

        public override string ToString()
        {
            return $"type:{type} | Json:{jsonDatas}";
        }
    }

    public static class JsonSerializer
    {
        public static JsonElement SerializeNode(BaseNode node)
        {
            return Serialize(node);
        }

        public static BaseNode DeserializeNode(JsonElement e)
        {
            try
            {
                var baseNodeType = SerializableType.ResolveType(e.type);

                if (e.jsonDatas == null)
                    return null;

                var node = Activator.CreateInstance(baseNodeType) as BaseNode;
#if UNITY_EDITOR
                EditorJsonUtility.FromJsonOverwrite(e.jsonDatas, node);
#else
                JsonUtility.FromJsonOverwrite(e.jsonDatas, node);
#endif
                return node;
            }
            catch
            {
                return null;
            }
        }

        public static JsonElement Serialize(object obj)
        {
            JsonElement elem = new JsonElement();

            elem.type = obj.GetType().AssemblyQualifiedName;
#if UNITY_EDITOR
            elem.jsonDatas = EditorJsonUtility.ToJson(obj);
#else
            elem.jsonDatas = JsonUtility.ToJson(obj);
#endif
            return elem;
        }

        public static T Deserialize<T>(JsonElement e)
        {
            if (typeof(T) != SerializableType.ResolveType(e.type))
                throw new ArgumentException("Deserializing type is not the same than Json element type");

            var obj = Activator.CreateInstance<T>();
#if UNITY_EDITOR
            EditorJsonUtility.FromJsonOverwrite(e.jsonDatas, obj);
#else
            JsonUtility.FromJsonOverwrite(e.jsonDatas, obj);
#endif
            return obj;
        }
    }
}
