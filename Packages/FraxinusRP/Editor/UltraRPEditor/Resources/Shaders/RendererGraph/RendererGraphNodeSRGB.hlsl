#ifndef RENDERER_GRAPH_SRGB
#define RENDERER_GRAPH_SRGB

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"

half GetLuminance(half3 colorLinear)
{
#if _TONEMAP_ACES
    return AcesLuminance(colorLinear);
#else
    return Luminance(colorLinear);
#endif
}

real3 GetSRGBToLinear(real3 c)
{
#if _USE_FAST_SRGB_LINEAR_CONVERSION
    return FastSRGBToLinear(c);
#else
    return SRGBToLinear(c);
#endif
}

real4 GetSRGBToLinear(real4 c)
{
#if _USE_FAST_SRGB_LINEAR_CONVERSION
    return FastSRGBToLinear(c);
#else
    return SRGBToLinear(c);
#endif
}

real3 GetLinearToSRGB(real3 c)
{
#if _USE_FAST_SRGB_LINEAR_CONVERSION
    return FastLinearToSRGB(c);
#else
    return LinearToSRGB(c);
#endif
}

real4 GetLinearToSRGB(real4 c)
{
#if _USE_FAST_SRGB_LINEAR_CONVERSION
    return FastLinearToSRGB(c);
#else
    return LinearToSRGB(c);
#endif
}

int _IsSRGB;

float3 ConvertToSRGBIfNeeded(float3 color)
{
    if (_IsSRGB)
        return color;
    else
        return LinearToSRGB(color);
}

float3 ConvertToLinearIfNeeded(float3 color)
{
    if (_IsSRGB)
        return SRGBToLinear(color);
    else
        return color;
}

#endif //RENDERER_GRAPH_SRGB