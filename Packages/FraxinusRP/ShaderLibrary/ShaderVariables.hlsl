#ifndef ULTRA_VT_FEEDBACK_UTILITIES_INCLUDED
#define ULTRA_VT_FEEDBACK_UTILITIES_INCLUDED

// Virtual-texturing feedback packing utilities. Texture declarations belong to
// the calling shader; this helper must not change Core's texture or XR macros.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

#if defined(_WRITE_RENDERING_LAYERS)
    #define ULTRA_VT_FEEDBACK_TARGET SV_Target2
#else
    #define ULTRA_VT_FEEDBACK_TARGET SV_Target1
#endif

float2 ConvertRGBA8UnormToRG16Unorm(float4 feedback)
{
    uint4 values = (uint4)round(saturate(feedback) * 255.0);
    return float2(values.x | (values.y << 8), values.z | (values.w << 8)) / 65535.0;
}

// Converts RG16_UNorm format to RGBA8_UNorm format
// This is used to unpack Virtual Texturing feedback data stored in a compact format
float4 ConvertRG16UnormToRGBA8Unorm(float2 compressedFeedback)
{
    float scale = (float)(0xFFFF);
    uint2 packedUintValues = (uint2)round(saturate(compressedFeedback) * scale);
    uint4 uintValues = uint4(packedUintValues.x, packedUintValues.x >> 8, packedUintValues.y, packedUintValues.y >> 8) & 0xFF;
    return (float4)uintValues / 255.0f;
}

// Feedback contains page IDs, so transparency must select a complete ID rather
// than interpolate IDs. Callers premultiply RGB and use One/OneMinusSrcAlpha.
float4 PackVTFeedbackWithAlpha(float4 feedback, float2 pixelCoord, float alpha)
{
    float2 packedFeedback = ConvertRGBA8UnormToRG16Unorm(feedback);
    alpha = saturate(alpha);
    if (alpha == 0.0 || alpha == 1.0)
        return float4(packedFeedback, 0.0, alpha);

    uint2 pixel = (uint2)floor(pixelCoord) & 3u;
    uint pixelIndex = pixel.y * 4u + pixel.x;
    const float thresholds[16] = {
         1.0 / 17.0,  9.0 / 17.0,  3.0 / 17.0, 11.0 / 17.0,
        13.0 / 17.0,  5.0 / 17.0, 15.0 / 17.0,  7.0 / 17.0,
         4.0 / 17.0, 12.0 / 17.0,  2.0 / 17.0, 10.0 / 17.0,
        16.0 / 17.0,  8.0 / 17.0, 14.0 / 17.0,  6.0 / 17.0
    };
    return float4(packedFeedback, 0.0, alpha < thresholds[pixelIndex] ? 0.0 : 1.0);
}

// Unpacks Virtual Texturing feedback data with alpha channel
// Used in VT feedback downsample passes
float4 UnpackVTFeedbackWithAlpha(float4 feedbackWithAlpha)
{
    return ConvertRG16UnormToRGBA8Unorm(feedbackWithAlpha.xy);
}

#endif // ULTRA_VT_FEEDBACK_UTILITIES_INCLUDED
