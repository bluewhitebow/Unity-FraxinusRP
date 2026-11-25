using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;

namespace FraxinusEditor.Rendering.Ultra
{
    static class UltraRPStructs
    {
        public static StructDescriptor Varyings = new StructDescriptor()
        {
            name = "Varyings",
            packFields = true,
            populateWithCustomInterpolators = true,
            fields = new FieldDescriptor[]
            {
                StructFields.Varyings.positionCS,
                StructFields.Varyings.positionWS,
                StructFields.Varyings.normalWS,
                StructFields.Varyings.tangentWS,
                StructFields.Varyings.texCoord0,
                StructFields.Varyings.texCoord1,
                StructFields.Varyings.texCoord2,
                StructFields.Varyings.texCoord3,
                StructFields.Varyings.color,
                StructFields.Varyings.screenPosition,
                UltraRPStructFields.Varyings.staticLightmapUV,
                UltraRPStructFields.Varyings.dynamicLightmapUV,
                UltraRPStructFields.Varyings.sh,
                UltraRPStructFields.Varyings.probeOcclusion,
                UltraRPStructFields.Varyings.fogFactorAndVertexLight,
                UltraRPStructFields.Varyings.shadowCoord,
                StructFields.Varyings.instanceID,
                UltraRPStructFields.Varyings.stereoTargetEyeIndexAsBlendIdx0,
                UltraRPStructFields.Varyings.stereoTargetEyeIndexAsRTArrayIdx,
                StructFields.Varyings.cullFace,
            }
        };
    }

    static class UltraRPStructFields
    {
        public struct Varyings
        {
            public static string name = "Varyings";
            public static FieldDescriptor staticLightmapUV = new FieldDescriptor(Varyings.name, "staticLightmapUV", "", ShaderValueType.Float2,
                preprocessor: "defined(LIGHTMAP_ON)", subscriptOptions: StructFieldOptions.Optional);
            public static FieldDescriptor dynamicLightmapUV = new FieldDescriptor(Varyings.name, "dynamicLightmapUV", "", ShaderValueType.Float2,
                preprocessor: "defined(DYNAMICLIGHTMAP_ON)", subscriptOptions: StructFieldOptions.Optional);
            public static FieldDescriptor sh = new FieldDescriptor(Varyings.name, "sh", "", ShaderValueType.Float3,
                preprocessor: "!defined(LIGHTMAP_ON)", subscriptOptions: StructFieldOptions.Optional);
            public static FieldDescriptor probeOcclusion = new FieldDescriptor(Varyings.name, "probeOcclusion", "", ShaderValueType.Float4,
                preprocessor: "defined(USE_APV_PROBE_OCCLUSION)", subscriptOptions: StructFieldOptions.Optional);
            public static FieldDescriptor fogFactorAndVertexLight = new FieldDescriptor(Varyings.name, "fogFactorAndVertexLight", "VARYINGS_NEED_FOG_AND_VERTEX_LIGHT", ShaderValueType.Float4,
                subscriptOptions: StructFieldOptions.Optional);
            public static FieldDescriptor shadowCoord = new FieldDescriptor(Varyings.name, "shadowCoord", "VARYINGS_NEED_SHADOWCOORD", ShaderValueType.Float4,
                subscriptOptions: StructFieldOptions.Optional, preprocessor: "defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)");
            public static FieldDescriptor stereoTargetEyeIndexAsRTArrayIdx = new FieldDescriptor(Varyings.name, "stereoTargetEyeIndexAsRTArrayIdx", "", ShaderValueType.Uint,
                "SV_RenderTargetArrayIndex", "(defined(UNITY_STEREO_INSTANCING_ENABLED))", StructFieldOptions.Generated);
            public static FieldDescriptor stereoTargetEyeIndexAsBlendIdx0 = new FieldDescriptor(Varyings.name, "stereoTargetEyeIndexAsBlendIdx0", "", ShaderValueType.Uint,
                "BLENDINDICES0", "(defined(UNITY_STEREO_MULTIVIEW_ENABLED)) || (defined(UNITY_STEREO_INSTANCING_ENABLED) && (defined(SHADER_API_GLES3) || defined(SHADER_API_GLCORE)))");
        }
    }
}
