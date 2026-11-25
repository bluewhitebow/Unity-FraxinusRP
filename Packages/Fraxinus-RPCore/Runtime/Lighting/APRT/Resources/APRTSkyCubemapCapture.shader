Shader "Hidden/APRT/SkyCubemapCapture"
{
    Properties { _APRTEnvironmentCube("Environment Radiance", Cube) = "" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            samplerCUBE _APRTEnvironmentCube;
            // Unity binds the texture's native HDR decode instructions alongside
            // its material texture property, as for Skybox/Cubemap and LookDev.
            float4 _APRTEnvironmentCube_HDR;
            struct Varyings { float4 position : SV_POSITION; float3 direction : TEXCOORD0; };
            Varyings Vert(float4 vertex : POSITION)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(vertex);
                output.direction = vertex.xyz;
                return output;
            }
            float4 Frag(Varyings input) : SV_Target
            {
                // Hardware sampling supports unreadable native HDR cubes; the
                // texture metadata also decodes imported RGBM environments.
                float4 sample = texCUBElod(_APRTEnvironmentCube, float4(normalize(input.direction), 0));
                return float4(DecodeHDR(sample, _APRTEnvironmentCube_HDR), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
