using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;

using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    internal static class CreateDecalProjector
    {
        [MenuItem("GameObject/Rendering/UltraRP Decal Projector", priority = CoreUtils.Priorities.gameObjectMenuPriority)]
        public static void CreateDecal(MenuCommand menuCommand)
        {
            var go = CoreEditorUtils.CreateGameObject("Decal Projector", menuCommand.context);
            go.AddComponent<DecalProjector>();
            go.transform.RotateAround(go.transform.position, go.transform.right, 90);
        }
    }
}
