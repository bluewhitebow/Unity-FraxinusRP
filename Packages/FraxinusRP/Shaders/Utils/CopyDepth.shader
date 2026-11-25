Shader "Hidden/Ultra Render Pipeline/CopyDepth"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UltraPipeline"}

        Pass
        {
            Name "CopyDepth"
            ZTest Always
            ZWrite[_ZWrite]
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag

            #pragma multi_compile _ _DEPTH_MSAA_2 _DEPTH_MSAA_4 _DEPTH_MSAA_8
            #pragma multi_compile _ _OUTPUT_DEPTH

            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/Utils/CopyDepthPass.hlsl"

            ENDHLSL
        }
    }
}
