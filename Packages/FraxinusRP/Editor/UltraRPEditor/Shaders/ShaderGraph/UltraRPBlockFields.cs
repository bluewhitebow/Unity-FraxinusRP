using UnityEngine;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;

namespace FraxinusEditor.Rendering.Ultra
{
    static class UltraRPBlockFields
    {
        [GenerateBlocks("Ultra Render Pipeline")]
        public struct VertexDescription
        {
            // SG14 resolves serialized blocks by tag/name and compares descriptors by reference.
            // A distinct tag prevents URP's MotionVector block from replacing this descriptor on import.
            public static string name = "UltraVertexDescription";
            public static BlockFieldDescriptor MotionVector = new BlockFieldDescriptor(VertexDescription.name, "MotionVector", "Motion Vector", "VERTEXDESCRIPTION_MOTIONVECTOR",
                new Vector3Control(new Vector3(0.0f, 0.0f, 0.0f)), ShaderStage.Vertex);
        }

        [GenerateBlocks("Ultra Render Pipeline")]
        public struct SurfaceDescription
        {
            public static string name = "SurfaceDescription";
            public static BlockFieldDescriptor NormalAlpha = new BlockFieldDescriptor(SurfaceDescription.name, "NormalAlpha", "Normal Alpha", "SURFACEDESCRIPTION_NORMALALPHA",
                new FloatControl(1.0f), ShaderStage.Fragment);
            public static BlockFieldDescriptor MAOSAlpha = new BlockFieldDescriptor(SurfaceDescription.name, "MAOSAlpha", "MAOS Alpha", "SURFACEDESCRIPTION_MAOSALPHA",
                new FloatControl(1.0f), ShaderStage.Fragment);
        }
    }
}
