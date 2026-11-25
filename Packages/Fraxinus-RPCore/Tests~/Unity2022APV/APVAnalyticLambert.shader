Shader "Hidden/APV/AnalyticLambert"
{
    Properties { _Reflectance ("Linear diffuse reflectance", Float) = 0.5 }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            float _Reflectance;
            float4 Vert(float4 position : POSITION) : SV_POSITION { return UnityObjectToClipPos(position); }
            float4 Frag() : SV_Target { return float4(_Reflectance.xxx, 1); }
            ENDCG
        }
        Pass
        {
            Name "Meta"
            Tags { "LightMode" = "Meta" }
            Cull Off
            CGPROGRAM
            #pragma vertex VertMeta
            #pragma fragment FragMeta
            #include "UnityCG.cginc"
            #include "UnityMetaPass.cginc"
            float _Reflectance;
            float4 VertMeta(float4 position : POSITION, float2 uv1 : TEXCOORD1, float2 uv2 : TEXCOORD2) : SV_POSITION
            {
                return UnityMetaVertexPosition(position, uv1, uv2, unity_LightmapST, unity_DynamicLightmapST);
            }
            float4 FragMeta() : SV_Target
            {
                UnityMetaInput input = (UnityMetaInput)0;
                input.Albedo = _Reflectance.xxx;
                return UnityMetaFragment(input);
            }
            ENDCG
        }
    }
}
