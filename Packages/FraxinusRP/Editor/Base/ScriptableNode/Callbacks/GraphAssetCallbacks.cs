using UnityEngine;
using FraxinusEngine.Base;
using UnityEditor;
using UnityEditor.Callbacks;

namespace FraxinusEditor.Base
{
    public class GraphAssetCallbacks
    {
        [MenuItem("Assets/Create/FraxinusEngine/ScriptableNode/GraphProcessor", priority = 101)]
        public static void CreateGraphPorcessor()
        {
            var graph = ScriptableObject.CreateInstance<BaseGraph>();
            ProjectWindowUtil.CreateAsset(graph, "GraphProcessor.asset");
        }

        [OnOpenAsset(1)]
        public static bool OnBaseGraphOpened(int instanceID, int line)
        {
            var asset = EditorUtility.InstanceIDToObject(instanceID) as BaseGraph;
            if (asset != null)
            {
                EditorWindow.GetWindow<DefaultGraphWindow>().InitializeGraph(asset as BaseGraph);
                return true;
            }

            return false;
        }
    }
}
