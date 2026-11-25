#ifndef ULTRA_CONTACT_SHADOWS_INCLUDED
#define ULTRA_CONTACT_SHADOWS_INCLUDED

#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"

// The producer stores main-light visibility, including distance fade and opacity.
TEXTURE2D_X_FLOAT(_ContactShadowTexture);
float _ContactShadowsEnabled;

half SampleContactShadow(float2 normalizedScreenSpaceUV)
{
#if !defined(_SURFACE_TYPE_TRANSPARENT) && !defined(_RECEIVE_SHADOWS_OFF) && !defined(UNITY_STEREO_INSTANCING_ENABLED) && !defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    [branch] if (_ContactShadowsEnabled > 0.5)
    {
        float2 uv = UnityStereoTransformScreenSpaceTex(normalizedScreenSpaceUV);
        return saturate(half(SAMPLE_TEXTURE2D_X_LOD(_ContactShadowTexture, sampler_PointClamp, uv, 0).r));
    }
#endif
    return 1.0h;
}

half LoadContactShadow(uint2 positionSS)
{
#if !defined(_SURFACE_TYPE_TRANSPARENT) && !defined(_RECEIVE_SHADOWS_OFF) && !defined(UNITY_STEREO_INSTANCING_ENABLED) && !defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    [branch] if (_ContactShadowsEnabled > 0.5)
        return saturate(half(LOAD_TEXTURE2D_X(_ContactShadowTexture, positionSS).r));
#endif
    return 1.0h;
}

#endif
