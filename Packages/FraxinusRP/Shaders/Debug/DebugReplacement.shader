Shader "Hidden/Ultra Render Pipeline/Debug/DebugReplacement"
{
    SubShader
    {
        Tags{"RenderType" = "Opaque" "RenderPipeline" = "UltraPipeline" "IgnoreProjector" = "True"}

        Pass
        {
            Name "Vertex Attributes"
            Tags {"LightMode" = "UltraForward"}
            Blend 1 One OneMinusSrcAlpha
            Blend 2 One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex vert
            #pragma fragment frag
            #include_with_pragmas "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/RenderingLayers.hlsl"

            #define DEBUG_DISPLAY

            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Debug/Debugging3D.hlsl"

            //--------------------------------------
            // GPU Instancing
            #include_with_pragmas "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Includes
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Input.hlsl"

            struct Attributes
            {
                float4 positionOS        : POSITION;
                float4 texcoord0         : TEXCOORD0;
                float4 texcoord1         : TEXCOORD1;
                float4 texcoord2         : TEXCOORD2;
                float4 texcoord3         : TEXCOORD3;
                float4 color             : COLOR;
                float4 normal            : NORMAL;
                float4 tangent           : TANGENT;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 texcoord0  : TEXCOORD0;
                float4 texcoord1  : TEXCOORD1;
                float4 texcoord2  : TEXCOORD2;
                float4 texcoord3  : TEXCOORD3;
                float4 color      : COLOR;
                float4 normal     : NORMAL;
                float4 tangent    : TANGENT;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.texcoord0 = input.texcoord0;
                output.texcoord1 = input.texcoord1;
                output.texcoord2 = input.texcoord2;
                output.texcoord3 = input.texcoord3;
                output.color     = input.color;
                output.normal    = input.normal;
                output.tangent   = input.tangent;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 frag(
                Varyings input
            #ifdef _WRITE_RENDERING_LAYERS
                , out float4 outRenderingLayers : SV_Target1
            #endif
            #if defined(UNITY_VIRTUAL_TEXTURING)
                , out float4 outVTFeedback : ULTRA_VT_FEEDBACK_TARGET
            #endif
            ) : SV_Target0
            {
                UNITY_SETUP_INSTANCE_ID(input);
                #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = float4(EncodeMeshRenderingLayer(GetMeshRenderingLayer()), 0, 0, 1);
                #endif
                #if defined(UNITY_VIRTUAL_TEXTURING)
                outVTFeedback = PackVTFeedbackWithAlpha(float4(1, 1, 1, 1), input.positionCS.xy, 1.0);
                outVTFeedback.rgb *= outVTFeedback.a;
                #endif
                switch (_DebugVertexAttributeMode)
                {
                    case DEBUGVERTEXATTRIBUTEMODE_TEXCOORD0:
                        return input.texcoord0;
                    case DEBUGVERTEXATTRIBUTEMODE_TEXCOORD1:
                        return input.texcoord1;
                    case DEBUGVERTEXATTRIBUTEMODE_TEXCOORD2:
                        return input.texcoord2;
                    case DEBUGVERTEXATTRIBUTEMODE_TEXCOORD3:
                        return input.texcoord3;
                    case DEBUGVERTEXATTRIBUTEMODE_COLOR:
                        return input.color;
                    case DEBUGVERTEXATTRIBUTEMODE_TANGENT:
                        return input.tangent;
                    case DEBUGVERTEXATTRIBUTEMODE_NORMAL:
                        return input.normal;
                    default:
                        return half4(0, 0, 0, 1);
                }
            }
            ENDHLSL
        }
    }
}
