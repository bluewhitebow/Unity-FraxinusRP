// shader for rendering grass using "texture grass" mode on terrain details
Shader "Hidden/FraxinusGPUBatcher/TerrainTextureGrass"
{
    Properties
    {
        [MainTexture] _MainTex ("Albedo (RGB) Alpha (A)", 2D) = "white" {}
        [MainColor] _BaseColor ("Tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        _Shininess ("Shininess", Range(0.01,1)) = 0.1
        _GrassInvHeight ("Inv Height (wave anchor)", Float) = 1
        _WindSpeed ("Wind Speed", Float) = 0.5
        _WaveSize ("Wave Size", Float) = 1
        _WindStrength ("Wind Strength", Float) = 0.5
        _WavingTint ("Waving Tint", Color) = (0.5,0.5,0.5,1)
        _HealthyColor ("Healthy Color", Color) = (1,1,1,1)
        _DryColor ("Dry Color", Color) = (1,1,1,1)
        _NoiseSpread ("Noise Spread", Float) = 0.1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UltraPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UltraForwardOnly" }
            Cull Off
            ZWrite On
            AlphaToMask On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassForwardVert
            #pragma fragment GrassForwardFrag

            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #if UNITY_VERSION < 600000
            #pragma multi_compile_local _ FRAXINUS_BRG_NATIVE_INDIRECT
            #endif
            #pragma multi_compile_local _ _BILLBOARD

            // grass is dynamic detail, never lightmapped: SH / APV probes only, no lightmap keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #pragma multi_compile_fog
            #include_with_pragmas "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/ProbeVolumeVariants.hlsl"

            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #pragma multi_compile_fragment _ DEBUG_DISPLAY

            #include "FraxinusGPUBatcherTerrainTextureGrass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull Off
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassDepthVert
            #pragma fragment GrassDepthFrag

            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #if UNITY_VERSION < 600000
            #pragma multi_compile_local _ FRAXINUS_BRG_NATIVE_INDIRECT
            #endif
            #pragma multi_compile_local _ _BILLBOARD

            #include "FraxinusGPUBatcherTerrainTextureGrass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull Off
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassShadowVert
            #pragma fragment GrassDepthFrag

            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #if UNITY_VERSION < 600000
            #pragma multi_compile_local _ FRAXINUS_BRG_NATIVE_INDIRECT
            #endif
            #pragma multi_compile_local _ _BILLBOARD
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "FraxinusGPUBatcherTerrainTextureGrass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex GrassDepthNormalsVert
            #pragma fragment GrassDepthNormalsFrag

            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #if UNITY_VERSION < 600000
            #pragma multi_compile_local _ FRAXINUS_BRG_NATIVE_INDIRECT
            #endif
            #pragma multi_compile_local _ _BILLBOARD
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "FraxinusGPUBatcherTerrainTextureGrass.hlsl"
            ENDHLSL
        }
    }

    FallBack Off
}
