Shader "Hidden/APV/AnalyticGradientSky"
{
    Properties
    {
        _RadianceA ("Linear constant radiance", Vector) = (0.4, 0.4, 0.4, 0)
        _RadianceB ("Linear up-axis gradient", Vector) = (0.3, 0, -0.3, 0)
    }
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            float4 _RadianceA, _RadianceB;
            struct Varyings { float4 position : SV_POSITION; float3 direction : TEXCOORD0; };
            Varyings Vert(float4 position : POSITION)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(position);
                output.direction = mul((float3x3)unity_ObjectToWorld, position.xyz);
                return output;
            }
            float4 Frag(Varyings input) : SV_Target
            {
                return float4(_RadianceA.rgb + _RadianceB.rgb * normalize(input.direction).y, 1);
            }
            ENDCG
        }
    }
}
