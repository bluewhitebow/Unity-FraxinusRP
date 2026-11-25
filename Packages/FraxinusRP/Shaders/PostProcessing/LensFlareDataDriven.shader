Shader "Hidden/Ultra Render Pipeline/LensFlareDataDriven"
{
    SubShader
    {
        Tags{ "RenderPipeline" = "UltraPipeline" }
        
        // Additive
        Pass
        {
            Name "LensFlareAdditive"
            Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UltraPipeline" }
            LOD 100

            Blend One One
            ColorMask RGB
            ZWrite Off
            Cull Off
            ZTest Always

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_fragment _ FLARE_INVERSE_SDF

            #pragma multi_compile_vertex _ FLARE_OPENGL3_OR_OPENGLCORE

            #pragma multi_compile _ FLARE_HAS_OCCLUSION

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/UnityInput.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/PostProcessing/Common.hlsl"

            #define FLARE_ADDITIVE_BLEND
            #include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/LensFlareCommon.hlsl"

            ENDHLSL
        }
        // Screen
        Pass
        {
            Name "LensFlareScreen"
            Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UltraPipeline" }
            LOD 100

            Blend One OneMinusSrcColor
            ColorMask RGB
            BlendOp Max
            ZWrite Off
            Cull Off
            ZTest Always

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_fragment _ FLARE_INVERSE_SDF

            #pragma multi_compile_vertex _ FLARE_OPENGL3_OR_OPENGLCORE

            #pragma multi_compile _ FLARE_HAS_OCCLUSION

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/UnityInput.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/PostProcessing/Common.hlsl"

            #define FLARE_SCREEN_BLEND
            #include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/LensFlareCommon.hlsl"

            ENDHLSL
        }
        // Premultiply
        Pass
        {
            Name "LensFlarePremultiply"
            Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UltraPipeline" }
            LOD 100

            Blend One OneMinusSrcAlpha
            ColorMask RGB
            ZWrite Off
            Cull Off
            ZTest Always

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_fragment _ FLARE_INVERSE_SDF

            #pragma multi_compile_vertex _ FLARE_OPENGL3_OR_OPENGLCORE

            #pragma multi_compile _ FLARE_HAS_OCCLUSION

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/UnityInput.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/PostProcessing/Common.hlsl"

            #define FLARE_PREMULTIPLIED_BLEND
            #include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/LensFlareCommon.hlsl"

            ENDHLSL
        }
        // Lerp
        Pass
        {
            Name "LensFlareLerp"
            Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UltraPipeline" }
            LOD 100

            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGB
            ZWrite Off
            Cull Off
            ZTest Always

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_fragment _ FLARE_INVERSE_SDF

            #pragma multi_compile_vertex _ FLARE_OPENGL3_OR_OPENGLCORE

            #pragma multi_compile _ FLARE_HAS_OCCLUSION

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/UnityInput.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/PostProcessing/Common.hlsl"

            #define FLARE_LERP_BLEND
            #include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/LensFlareCommon.hlsl"

            ENDHLSL
        }
        // OcclusionOnly
        Pass
        {
            Name "LensFlareOcclusion"

            Blend Off
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex vertOcclusion
            #pragma fragment fragOcclusion
            #pragma exclude_renderers gles

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DeclareDepthTexture.hlsl"

            #define FLARE_COMPUTE_OCCLUSION
            #include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/LensFlareCommon.hlsl"

            ENDHLSL
        }
    }
}
