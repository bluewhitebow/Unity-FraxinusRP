Shader "Hidden/Ultra Render Pipeline/ClusterDeferred"
{
    HLSLINCLUDE
    #pragma editor_sync_compilation
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal
    #pragma vertex ClusterDeferredVertex
    #pragma fragment ClusterDeferredFragment
    #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
    #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
    #pragma multi_compile_fragment _ _SHADOWS_SOFT
    #pragma multi_compile_fragment _ _LIGHT_COOKIES
    #pragma multi_compile_fragment _ _LIGHT_LAYERS
    #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
    #pragma multi_compile_fragment _ _RENDER_PASS_ENABLED
    #define _GPU_LIGHTS_CLUSTER
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UltraPipeline" }
        ZTest NotEqual
        ZWrite Off
        Cull Off
        Blend One SrcAlpha, Zero One
        BlendOp Add, Add
        ColorMask RGB

        Pass
        {
            Name "Cluster Deferred Lit"
            // ShadingModels.ModelsMask excludes the four user stencil bits.
            Stencil
            {
                Ref 32
                ReadMask 240
                WriteMask 0
                Comp Equal
                Pass Keep
                Fail Keep
                ZFail Keep
            }
            HLSLPROGRAM
            #define FRAXINUS_CLUSTER_SHADING_MODEL SHADINGMODELS_LIT
            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/Lighting/ClusterDeferred.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Cluster Deferred SimpleLit"
            // ShadingModels.ModelsMask excludes the four user stencil bits.
            Stencil
            {
                Ref 64
                ReadMask 240
                WriteMask 0
                Comp Equal
                Pass Keep
                Fail Keep
                ZFail Keep
            }
            HLSLPROGRAM
            #define FRAXINUS_CLUSTER_SHADING_MODEL SHADINGMODELS_SIMPLE_LIT
            #define CLUSTER_SIMPLE_LIT
            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/Lighting/ClusterDeferred.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Cluster Deferred Character"
            // ShadingModels.ModelsMask excludes the four user stencil bits.
            Stencil
            {
                Ref 96
                ReadMask 240
                WriteMask 0
                Comp Equal
                Pass Keep
                Fail Keep
                ZFail Keep
            }
            HLSLPROGRAM
            #define FRAXINUS_CLUSTER_SHADING_MODEL SHADINGMODELS_CHARACTER
            // Character retains the current Lit BRDF fallback.
            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/Lighting/ClusterDeferred.hlsl"
            ENDHLSL
        }
    }
    Fallback Off
}
