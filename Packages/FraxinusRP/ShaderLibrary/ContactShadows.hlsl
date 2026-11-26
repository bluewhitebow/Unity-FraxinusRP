#ifndef ULTRA_CONTACT_SHADOWS_INCLUDED
#define ULTRA_CONTACT_SHADOWS_INCLUDED

#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"

// The producer stores main-light visibility, including distance fade and opacity.
TEXTURE2D_X_FLOAT(_ContactShadowTexture);
float _ContactShadowsEnabled;

half SampleContactShadow(float2 normalizedScreenSpaceUV)
{
    half visibility = 1.0h;
#if !defined(_SURFACE_TYPE_TRANSPARENT) && !defined(_RECEIVE_SHADOWS_OFF) && !defined(UNITY_STEREO_INSTANCING_ENABLED) && !defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    [branch] if (_ContactShadowsEnabled > 0.5)
    {
        float2 uv = UnityStereoTransformScreenSpaceTex(normalizedScreenSpaceUV);
        visibility = saturate(half(SAMPLE_TEXTURE2D_X_LOD(_ContactShadowTexture, sampler_PointClamp, uv, 0).r));
    }
#endif
    return visibility;
}

half LoadContactShadow(uint2 positionSS)
{
    half visibility = 1.0h;
#if !defined(_SURFACE_TYPE_TRANSPARENT) && !defined(_RECEIVE_SHADOWS_OFF) && !defined(UNITY_STEREO_INSTANCING_ENABLED) && !defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    [branch] if (_ContactShadowsEnabled > 0.5)
        visibility = saturate(half(LOAD_TEXTURE2D_X(_ContactShadowTexture, positionSS).r));
#endif
    return visibility;
}

#endif
