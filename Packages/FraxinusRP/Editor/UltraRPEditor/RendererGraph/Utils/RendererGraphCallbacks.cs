using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace FraxinusEditor.Rendering.Ultra
{
    public class RendererGraphCallbacks
    {
        [OnOpenAsset(0)]
        public static bool OnBaseGraphOpened(int instanceID, int line)
        {
            var asset = EditorUtility.InstanceIDToObject(instanceID);

            if (asset is ScriptableRendererGraph)
            {
                var path = AssetDatabase.GetAssetPath(EditorUtility.InstanceIDToObject(instanceID));
                var graph = RendererGraphEditorUtils.GetGraphAtPath(path);

                if (graph == null)
                    return false;

                ScriptableRendererGraphWindow.Open(graph);
                return true;
            }

            return false;
        }
    }
}
