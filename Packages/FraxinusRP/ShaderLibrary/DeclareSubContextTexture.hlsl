#ifndef UNITY_DECLARE_SUBCONTEXT_TEXTURE_INCLUDED
#define UNITY_DECLARE_SUBCONTEXT_TEXTURE_INCLUDED

#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/DynamicScalingClamping.hlsl"

TEXTURE2D(_SubContextColor);
SAMPLER(sampler_SubContextColor);
float4 _SubContextColor_TexelSize;

float4 SampleSubContextColor(float2 uv)
{
    uv = ClampAndScaleUVForBilinear(uv, _SubContextColor_TexelSize.xy);
    return SAMPLE_TEXTURE2D(_SubContextColor, sampler_SubContextColor, uv);
}

float3 SampleSubContextColorRGB(float2 uv)
{
    return SampleSubContextColor(uv).rgb;
}

float4 LoadSubContextColor(uint2 uv)
{
    return LOAD_TEXTURE2D(_SubContextColor, uv);
}

float4 SampleSubContextColorFromScreen(float4 positionCS)
{
    float2 uv = positionCS.xy * _ScreenParams.zw; // zw = 1/width, 1/height
    return SampleSubContextColor(uv);
}

#endif // UNITY_DECLARE_SUBCONTEXT_TEXTURE_INCLUDED
