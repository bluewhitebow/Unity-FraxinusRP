#ifndef ULTRARPTARGET_META_PASS_INCLUDED
#define ULTRARPTARGET_META_PASS_INCLUDED

PackedVaryings vert(Attributes input)
{
    return PackVaryings(BuildVaryings(input));
}

half4 frag(PackedVaryings packedInput) : SV_Target
{
    Varyings input = UnpackVaryings(packedInput);
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    SurfaceDescription surface = BuildSurfaceDescription(input);
#if defined(_ALPHATEST_ON)
    clip(surface.Alpha - surface.AlphaClipThreshold);
#endif
    MetaInput meta = (MetaInput)0;
    meta.Albedo = surface.BaseColor;
#if defined(ULTRA_META_LIT)
    meta.Emission = surface.Emission;
#endif
#if defined(EDITOR_VISUALIZATION)
    meta.VizUV = input.texCoord1.xy;
    meta.LightCoord = input.texCoord2;
#endif
    return UnityMetaFragment(meta);
}
#endif
