Shader "Hidden/UltraRP/PRT/ProbeSHDebug"
{
	Properties
	{
		_TintColor("Tint Color", Color) = (1, 1, 1, 1)
	}
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UltraPipeline"
			"RenderType"="Opaque"
			"Queue"="Geometry+0"
			"UltraMaterialType"="Lit"
        }
        LOD 100

        Pass
        {
            Name "Forward"
            Blend 1 One OneMinusSrcAlpha
            Blend 2 One OneMinusSrcAlpha
			Tags
			{
				"LightMode" = "UltraForward"
			}
			
			ZWrite On
			ZTest LEqual
			
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include_with_pragmas "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/RenderingLayers.hlsl"

            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/GlobalIllumination/PRT/SphericalHarmonics.hlsl"
            
            CBUFFER_START(UnityPerMaterial)
            float4 _TintColor;
            CBUFFER_END
            
            StructuredBuffer<float> _coefficientSH9; // array size: 3x9=27

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 normal : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = TransformObjectToHClip(v.vertex.xyz);
                o.normal = TransformObjectToWorldNormal(v.normal);
                o.normal = normalize(o.normal);
                return o;
            }

            float4 frag(
                v2f input
            #ifdef _WRITE_RENDERING_LAYERS
                , out float4 outRenderingLayers : SV_Target1
            #endif
            #if defined(UNITY_VIRTUAL_TEXTURING)
                , out float4 outVTFeedback : ULTRA_VT_FEEDBACK_TARGET
            #endif
            ) : SV_Target0
            {
                #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = float4(EncodeMeshRenderingLayer(GetMeshRenderingLayer()), 0, 0, 1);
                #endif
                #if defined(UNITY_VIRTUAL_TEXTURING)
                outVTFeedback = PackVTFeedbackWithAlpha(float4(1, 1, 1, 1), input.vertex.xy, 1.0);
                outVTFeedback.rgb *= outVTFeedback.a;
                #endif
                float3 dir = input.normal;

                // Decode SH coefficients from structured buffer
                float3 c[9];
                for (int i = 0; i < 9; i++)
                {
                    c[i].x = _coefficientSH9[i * 3 + 0];
                    c[i].y = _coefficientSH9[i * 3 + 1];
                    c[i].z = _coefficientSH9[i * 3 + 2];
                }

                // Evaluate SH irradiance
                float3 Lo = IrradianceSH9(c, dir.xzy); // PI is pre-divided
                
                // Apply tint color (for invalidated probes or debugging)
                Lo *= _TintColor.rgb;
                
                return float4(Lo, 1.0);
            }
            ENDHLSL
        }
		
		Pass
		{
			Name "DepthNormals"
			Tags 
			{ 
				"LightMode" = "DepthNormals" 
		    }
			
			Blend One Zero
			ZTest LEqual
			ZWrite On

			HLSLPROGRAM

			#pragma vertex vert
			#pragma fragment frag

			#define SHADERPASS SHADERPASS_DEPTHNORMALSONLY
			#pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
			
			#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
			#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Input.hlsl"

			struct VertexInput
			{
				float4 vertex : POSITION;
				float3 normal : NORMAL;
				float4 tangent : TANGENT;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct VertexOutput
			{
				float4 clipPos : SV_POSITION;
				float3 worldNormal : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				UNITY_VERTEX_OUTPUT_STEREO
			};
			
			
			VertexOutput vert ( VertexInput v )
			{
				VertexOutput o = (VertexOutput)0;
				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_TRANSFER_INSTANCE_ID(v, o);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
				
				float3 positionWS = TransformObjectToWorld( v.vertex.xyz );
				float3 normalWS = TransformObjectToWorldNormal( v.normal );
				o.worldNormal = normalWS;
				o.clipPos = TransformWorldToHClip( positionWS );
				return o;
			}

			half4 frag( VertexOutput IN ) : SV_TARGET
			{
				UNITY_SETUP_INSTANCE_ID(IN);
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX( IN );
				
				float3 normalWS = NormalizeNormalPerPixel(IN.worldNormal);
				#if defined(_GBUFFER_NORMALS_OCT)
					float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
					float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
					return half4(PackFloat2To888(remappedOctNormalWS), 0.0);
				#else
					return half4(normalWS, 0.0);
				#endif
			}
			ENDHLSL
		}
    }
}
