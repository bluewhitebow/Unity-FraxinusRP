Shader "Hidden/Ultra Render Pipeline/SGSR/SGSR2"
{
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque" "RenderPipeline" = "UltraPipeline"
        }
        LOD 100
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "SGSR2 Convert"
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/PostProcessing/Common.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DeclareDepthTexture.hlsl"

            #pragma vertex Vert
            #pragma fragment FragConvert
            #pragma target 4.5
            #pragma multi_compile _ _DISABLE_TEXTURE2D_X_ARRAY

            #include "sgsr2_convert.hlsl"

            half4 FragConvert(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return SnapdragonGameSuperResolutionConvertPass(input.texcoord);
            }
            ENDHLSL
        }

        Pass
        {
            Name "SGSR2 Upscale"
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/PostProcessing/Common.hlsl"

            #pragma vertex Vert
            #pragma fragment FragSGSR
            #pragma target 4.5
            #pragma multi_compile _ _DISABLE_TEXTURE2D_X_ARRAY

            #include "sgsr2_upscale.hlsl"

            half4 FragSGSR(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 color = SnapdragonGameSuperResolutionUpscalePass(input.texcoord);
                return color;
            }
            ENDHLSL
        }

        // History copy. Keeping this as a material pass avoids introducing a
        // second shader/material resource and works identically for the
        // compatibility and RenderGraph paths.
        Pass
        {
            Name "SGSR2 Copy History"
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/PostProcessing/Common.hlsl"

            #pragma vertex Vert
            #pragma fragment FragCopy
            #pragma target 3.5
            #pragma multi_compile _ _DISABLE_TEXTURE2D_X_ARRAY

            half4 FragCopy(Varyings input) : SV_TARGET
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
