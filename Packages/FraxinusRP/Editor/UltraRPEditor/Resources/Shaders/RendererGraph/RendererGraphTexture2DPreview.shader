Shader "Hidden/Editor/RendererGraphTexture2DPreview"
{
    Properties
    {
        _MainTex ("MainTex", 2D) = "" {}
        _Size ("Size", Vector) = (512.0, 512.0, 1.0, 1.0)
        _Channels ("Channels", Vector) = (1.0, 1.0, 1.0, 1.0)
    }

    SubShader
    {
        Tags { "RenderType" = "Overlay" }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Resources/Shaders/RendererGraph/RendererGraphInspectorPreview.hlsl"
            ENDHLSL
        }
    }
}
