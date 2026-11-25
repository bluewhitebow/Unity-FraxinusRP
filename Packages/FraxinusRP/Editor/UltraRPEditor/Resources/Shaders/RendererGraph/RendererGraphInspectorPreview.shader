Shader "Hidden/Editor/RendererGraphInspectorPreview"
{
    Properties
    {
        _MainTex0_2D("_MainTex2D", 2D) = "" {}
        _MainTex0_3D("_MainTex3D", 3D) = "" {}
        _Size("_Size", Vector) = (512.0,512.0,1.0,1.0)
        _Channels("_Channels", Vector) = (1.0,1.0,1.0,1.0)
        _PreviewMip("_PreviewMip", Float) = 0.0
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
