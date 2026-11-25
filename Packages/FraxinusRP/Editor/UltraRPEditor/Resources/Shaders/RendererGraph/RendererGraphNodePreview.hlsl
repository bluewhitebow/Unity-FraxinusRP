#ifndef RENDERER_GRAPH_PREVIEW
#define RENDERER_GRAPH_PREVIEW

#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Resources/Shaders/RendererGraph/RendererGraphNodeSRGB.hlsl"

TEXTURE2D(_MainTex);        SAMPLER(sampler_MainTex);

float4 _MainTex_TexelSize;

float4 _Channels;
half _SRGB;

uniform float4x4 unity_GUIClipTextureMatrix;
sampler2D _GUIClipTexture;

struct Attributes
{
    float4 vertex   : POSITION;
    float2 uv       : TEXCOORD0;
};

struct Varings
{
    float2 uv           : TEXCOORD0;
    float2 clipUV       : TEXCOORD1;
    float4 positionCS   : SV_POSITION;
};

Varings Vert(Attributes v)
{
    Varings o = (Varings)0;

    VertexPositionInputs vertextInput = GetVertexPositionInputs(v.vertex.xyz);
    o.uv = v.uv;
    o.clipUV = mul(unity_GUIClipTextureMatrix, float4(vertextInput.positionVS, 1.0)).xy;
    o.positionCS = vertextInput.positionCS;

    return o;
}

half4 MakePreviewColor(Varings input, half4 imageColor, float2 uv, float2 texelSize)
{
    float2 checkboardUVs = ceil(fmod(uv * texelSize / 64.0, 1.0) - 0.5);
    float3 checkerboard = lerp(0.3, 0.4, checkboardUVs.x != checkboardUVs.y ? 1 : 0);

    bool alphaOnly = _Channels.a != 0.0 && all(_Channels.rgb == 0.0);
    if (alphaOnly)
    {
        // Alpha is scalar data, so display it directly without RGB color conversion.
        imageColor.rgb = saturate(imageColor.a);
    }
    else
    {
        imageColor.rgb = ConvertToSRGBIfNeeded(imageColor.rgb);
        half opacity = _Channels.a != 0.0 ? saturate(imageColor.a) : 1.0;
        imageColor.rgb = lerp(checkerboard, imageColor.rgb, opacity);
    }
    imageColor.a = 1;

    return imageColor * tex2D(_GUIClipTexture, input.clipUV).a;
}

half4 FragPreview2D(Varings i) : SV_Target
{
    half4 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * _Channels;
    return MakePreviewColor(i, color, i.uv, _MainTex_TexelSize.zw);
}

#endif //RENDERER_GRAPH_PREVIEW
