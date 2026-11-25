Shader "Hidden/Ultra Render Pipeline/AutoExposure"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UltraPipeline" }
        ZTest Always ZWrite Off Cull Off
        Pass
        {
            Name "Apply Auto Exposure"
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_AutoExposureTexture);

            float4 Frag(Varyings input) : SV_Target
            {
                float4 color = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, input.texcoord, 0);
                float exposure = LOAD_TEXTURE2D(_AutoExposureTexture, uint2(0, 0)).x;
                #if defined(UNITY_COLORSPACE_GAMMA)
                    color.rgb = SRGBToLinear(max(color.rgb, 0.0));
                #endif
                color.rgb = min(max(color.rgb, 0.0) * exposure, 65504.0);
                #if defined(UNITY_COLORSPACE_GAMMA)
                    color.rgb = LinearToSRGB(color.rgb);
                #endif
                return color;
            }
            ENDHLSL
        }
    }
}
