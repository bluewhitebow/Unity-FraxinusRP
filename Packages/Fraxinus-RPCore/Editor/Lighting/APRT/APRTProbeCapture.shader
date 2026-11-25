Shader "Hidden/Fraxinus/APRTProbeCapture"
{
    Properties
    {
        _BaseMap("Albedo", 2D) = "white" {}
        _BaseColor("Color", Color) = (1,1,1,1)
        _AlphaClip("Alpha Clip", Float) = 0
        _Cutoff("Cutoff", Float) = 0.5
        _TwoSided("Two Sided", Float) = 0
        _Terrain("Terrain", Float) = 0
        _Control0("Terrain Control 0", 2D) = "white" {}
        _Control1("Terrain Control 1", 2D) = "black" {}
        _Holes("Terrain Holes", 2D) = "white" {}
        _Layer0("Terrain Layer 0", 2D) = "white" {}
        _Layer1("Terrain Layer 1", 2D) = "white" {}
        _Layer2("Terrain Layer 2", 2D) = "white" {}
        _Layer3("Terrain Layer 3", 2D) = "white" {}
        _Layer4("Terrain Layer 4", 2D) = "white" {}
        _Layer5("Terrain Layer 5", 2D) = "white" {}
        _Layer6("Terrain Layer 6", 2D) = "white" {}
        _Layer7("Terrain Layer 7", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            // Retain backface hits to detect invalid probes inside closed meshes.
            Cull Off ZWrite On ZTest LEqual
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            sampler2D _BaseMap, _Control0, _Control1, _Holes;
            sampler2D _Layer0, _Layer1, _Layer2, _Layer3, _Layer4, _Layer5, _Layer6, _Layer7;
            float4 _BaseMap_ST, _BaseColor;
            float4 _Layer0_ST, _Layer1_ST, _Layer2_ST, _Layer3_ST, _Layer4_ST, _Layer5_ST, _Layer6_ST, _Layer7_ST;
            float _AlphaClip, _Cutoff, _TwoSided, _Terrain;
            float4 _APRTCapturePosition;
            float3 _APRTCaptureForward;
            float _APRTCaptureDistance;
            float _APRTReferenceCapture;
            struct Attributes { float4 position : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 position : SV_POSITION; float3 worldPosition : TEXCOORD0; float3 normal : TEXCOORD1; float2 uv : TEXCOORD2; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.worldPosition = mul(unity_ObjectToWorld, input.position).xyz;
                output.position = mul(UNITY_MATRIX_VP, float4(output.worldPosition, 1));
                output.normal = UnityObjectToWorldNormal(input.normal);
                output.uv = input.uv;
                return output;
            }
            float4 Frag(Varyings input) : SV_Target
            {
                float4 albedo;
                if (_Terrain > 0.5)
                {
                    clip(tex2D(_Holes, input.uv).r - 0.5);
                    float4 c0 = tex2D(_Control0, input.uv), c1 = tex2D(_Control1, input.uv);
                    float sum = dot(c0 + c1, 1.0);
                    c0 /= max(sum, 0.0001); c1 /= max(sum, 0.0001);
                    albedo = tex2D(_Layer0, input.uv * _Layer0_ST.xy + _Layer0_ST.zw) * c0.x;
                    albedo += tex2D(_Layer1, input.uv * _Layer1_ST.xy + _Layer1_ST.zw) * c0.y;
                    albedo += tex2D(_Layer2, input.uv * _Layer2_ST.xy + _Layer2_ST.zw) * c0.z;
                    albedo += tex2D(_Layer3, input.uv * _Layer3_ST.xy + _Layer3_ST.zw) * c0.w;
                    albedo += tex2D(_Layer4, input.uv * _Layer4_ST.xy + _Layer4_ST.zw) * c1.x;
                    albedo += tex2D(_Layer5, input.uv * _Layer5_ST.xy + _Layer5_ST.zw) * c1.y;
                    albedo += tex2D(_Layer6, input.uv * _Layer6_ST.xy + _Layer6_ST.zw) * c1.z;
                    albedo += tex2D(_Layer7, input.uv * _Layer7_ST.xy + _Layer7_ST.zw) * c1.w;
                }
                else
                {
                    albedo = tex2D(_BaseMap, input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw) * _BaseColor;
                    if (_AlphaClip > 0.5) clip(albedo.a - _Cutoff);
                }
                float3 ray = input.worldPosition - _APRTCapturePosition.xyz;
                float distance = max(length(ray), 0.00001);
                // The cube projection clips axial depth; transport distance is radial.
                if (_APRTReferenceCapture < 0.5) clip(_APRTCaptureDistance - distance);
                bool backface = (_APRTReferenceCapture > 0.5 || _TwoSided < 0.5) && dot(input.normal, ray) > 0;
                float storedDistance = _APRTReferenceCapture > 0.5 ? max(dot(ray, _APRTCaptureForward), 0.00001) : distance;
                return float4(_APRTReferenceCapture > 0.5 ? albedo.rgb : saturate(albedo.rgb), backface ? -storedDistance : storedDistance);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
