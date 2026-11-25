Shader "Hidden/APRT/ProbeDebug"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Probe Spheres"
            Cull Back
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:SetupProbeInstance
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl"

            StructuredBuffer<float3> _APRTProbeDebugPositions;
            StructuredBuffer<float> _APRTProbeDebugValidity;
            StructuredBuffer<float4> _APRTProbeDebugAxis;
            StructuredBuffer<float4> _APRTProbeDebugSH;
            StructuredBuffer<float4> _APRTProbeDebugSkySH;
            float4x4 _APRTProbeDebugViewProjection;
            float3 _APRTProbeDebugCameraPosition;
            float _APRTProbeDebugRadius;
            float _APRTProbeDebugDistanceSquared;
            float _APRTProbeDebugValidityThreshold;
            float _APRTProbeDebugExposure;
            int _APRTProbeDebugCount;
            int _APRTProbeDebugColorMode;
            int _APRTProbeDebugLightingModel;

            void SetupProbeInstance() { }

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                nointerpolation uint probeIndex : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                uint index = 0;
                #if UNITY_ANY_INSTANCING_ENABLED
                    index = unity_InstanceID;
                #endif
                output.probeIndex = index;
                output.normalWS = input.normalOS;
                output.positionCS = float4(2, 2, 2, 1);
                if (index >= (uint)_APRTProbeDebugCount) return output;
                float3 center = _APRTProbeDebugPositions[index];
                float3 toCamera = center - _APRTProbeDebugCameraPosition;
                if (_APRTProbeDebugDistanceSquared <= 0 || dot(toCamera, toCamera) > _APRTProbeDebugDistanceSquared)
                    return output;
                float3 positionWS = center + input.positionOS * _APRTProbeDebugRadius;
                output.positionCS = mul(_APRTProbeDebugViewProjection, float4(positionWS, 1));
                return output;
            }

            float4 SampleTransfer(uint index, float3 normalWS)
            {
                float3 weight = abs(normalWS);
                weight /= max(weight.x + weight.y + weight.z, 1e-6);
                uint offset = index * 6;
                return _APRTProbeDebugAxis[offset + (normalWS.x < 0 ? 1 : 0)] * weight.x
                     + _APRTProbeDebugAxis[offset + (normalWS.y < 0 ? 3 : 2)] * weight.y
                     + _APRTProbeDebugAxis[offset + (normalWS.z < 0 ? 5 : 4)] * weight.z;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                uint index = input.probeIndex;
                float3 normalWS = normalize(input.normalWS);
                float3 color = float3(0, 1, 1);
                if (_APRTProbeDebugColorMode == 1)
                {
                    uint offset = index * 4;
                    float directionalScale = _APRTProbeDebugLightingModel == 1 ? 2 : 1;
                    float3 directional = _APRTProbeDebugSH[offset].rgb + directionalScale * (
                        normalWS.x * _APRTProbeDebugSH[offset + 1].rgb
                        + normalWS.y * _APRTProbeDebugSH[offset + 2].rgb
                        + normalWS.z * _APRTProbeDebugSH[offset + 3].rgb);
                    float3 sky = _APRTProbeDebugSkySH[offset].rgb
                        + normalWS.x * _APRTProbeDebugSkySH[offset + 1].rgb
                        + normalWS.y * _APRTProbeDebugSkySH[offset + 2].rgb
                        + normalWS.z * _APRTProbeDebugSkySH[offset + 3].rgb;
                    color = (max(0, directional) + max(0, sky)) * _APRTProbeDebugExposure;
                }
                else if (_APRTProbeDebugColorMode == 2)
                {
                    float validity = _APRTProbeDebugValidity[index];
                    float brightness = .25 + .75 * saturate(validity);
                    color = validity <= 0 || validity < _APRTProbeDebugValidityThreshold
                        ? float3(brightness, 0, 0) : float3(0, brightness, 0);
                }
                else if (_APRTProbeDebugColorMode == 3)
                    color = max(0, SampleTransfer(index, normalWS).rgb) * _APRTProbeDebugExposure;
                else if (_APRTProbeDebugColorMode == 4)
                    color = saturate(SampleTransfer(index, normalWS).w).xxx;
                return float4(color, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
