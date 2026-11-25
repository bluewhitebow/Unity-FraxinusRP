Shader "Ultra Render Pipeline/2D/Sprite-Mask"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [HideInInspector] _Cutoff ("Mask alpha cutoff", Range(0.0, 1.0)) = 0.0
    }

    HLSLINCLUDE
    #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
    ENDHLSL

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
            "RenderPipeline" = "UltraPipeline"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend Off
        ColorMask 0

        Pass
        {
            Tags{ "LightMode" = "Ultra2D" }
            HLSLPROGRAM
            #pragma vertex MaskRenderingVertex
            #pragma fragment MaskRenderingFragment

            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/2D/Include/SpriteMaskShared.hlsl"
            ENDHLSL
        }
        Pass
        {
            Tags{ "LightMode" = "NormalsRendering" }
            HLSLPROGRAM
            #pragma vertex MaskRenderingVertex
            #pragma fragment MaskRenderingFragment

            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/2D/Include/SpriteMaskShared.hlsl"
            ENDHLSL
        }
        Pass
        {
            Tags{ "LightMode" = "UltraForward" }
            HLSLPROGRAM
            #pragma vertex MaskRenderingVertex
            #pragma fragment MaskRenderingFragment

            #include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/2D/Include/SpriteMaskShared.hlsl"
            ENDHLSL
        }

    }
}
