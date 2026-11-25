Shader "Hidden/PRT/GBuffer"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
    }
    
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            Name "PRT_GBuffer"
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _GBUFFER_WORLDPOS _GBUFFER_NORMAL _GBUFFER_ALBEDO

            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float2 uv : TEXCOORD2;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
            CBUFFER_END

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = TransformObjectToHClip(v.vertex.xyz);
                o.worldPos = TransformObjectToWorld(v.vertex.xyz);
                o.worldNormal = TransformObjectToWorldNormal(v.normal);
                o.uv = v.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                #if defined(_GBUFFER_WORLDPOS)
                    // Output world position
                    return float4(i.worldPos, 1.0);
                    
                #elif defined(_GBUFFER_NORMAL)
                    // Output world normal (encoded to [0,1])
                    float3 normal = normalize(i.worldNormal);
                    return float4(normal * 0.5 + 0.5, 1.0);
                    
                #else // _GBUFFER_ALBEDO or default
                    // Output albedo
                    float4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                    albedo *= _Color;
                    return albedo;
                #endif
            }
            ENDHLSL
        }
    }
    
    Fallback Off
}
