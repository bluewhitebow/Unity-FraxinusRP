using System;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    static class CreateUltraRPUnlitShaderGraph
    {
        [UnityEditor.MenuItem("Assets/Create/Shader Graph/UltraRP/Unlit Shader Graph", priority = CoreUtils.Sections.section1 + CoreUtils.Priorities.assetsCreateShaderMenuPriority + 1)]
        public static void CreateUnlitGraph()
        {
            var target = (UltraRPTarget)Activator.CreateInstance(typeof(UltraRPTarget));
            target.TrySetActiveSubTarget(typeof(UltraRPUnlitSubTarget));

            var blockDescriptors = new[]
            {
                BlockFields.VertexDescription.Position,
                BlockFields.VertexDescription.Normal,
                BlockFields.VertexDescription.Tangent,
                BlockFields.SurfaceDescription.BaseColor,
                BlockFields.SurfaceDescription.Alpha,
            };

            GraphUtil.CreateNewGraphWithOutputs(new[] { target }, blockDescriptors);
        }
    }
}
