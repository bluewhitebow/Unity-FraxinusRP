using UnityEditor;

namespace FraxinusEditor.Base
{
    public class GraphProcessorMenuItems : NodeGraphProcessorMenuItems
    {
        [MenuItem("Assets/Create/FraxinusEngine/ScriptableNode/Node C# Script", priority = 201)]
        private static void CreateNodeCSharpScritpt() => CreateDefaultNodeCSharpScritpt();

        [MenuItem("Assets/Create/FraxinusEngine/ScriptableNode/Node View C# Script", priority = 202)]
        private static void CreateNodeViewCSharpScritpt() => CreateDefaultNodeViewCSharpScritpt();
    }
}

