#ifndef ULTRA_DECAL_INPUT_INCLUDED
#define ULTRA_DECAL_INPUT_INCLUDED

struct DecalSurfaceData
{
    half4 baseColor;
    half4 normalWS;
    half3 emissive;
    half metallic;
    half occlusion;
    half smoothness;
    half MAOSAlpha;
};
#endif // ULTRA_DECAL_INPUT_INCLUDED
