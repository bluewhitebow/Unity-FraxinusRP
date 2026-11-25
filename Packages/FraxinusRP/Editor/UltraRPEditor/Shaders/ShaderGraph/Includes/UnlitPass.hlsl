#ifndef ULTRARPTARGET_UNLITPASS_INCLUDED
#define ULTRARPTARGET_UNLITPASS_INCLUDED

PackedVaryings vert(Attributes input)
{
    Varyings output = BuildVaryings(input);
    return PackVaryings(output);
}

void frag(PackedVaryings packedInput, out half4 outColor : SV_Target0
#if defined(_WRITE_RENDERING_LAYERS)
    , out float4 outRenderingLayers : SV_Target1
#endif
#if defined(UNITY_VIRTUAL_TEXTURING)
    , out float4 outVTFeedback : ULTRA_VT_FEEDBACK_TARGET
#endif
)
{
    Varyings unpacked = UnpackVaryings(packedInput);
    UNITY_SETUP_INSTANCE_ID(unpacked);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(unpacked);

    SurfaceDescriptionInputs surfaceDescriptionInputs = BuildSurfaceDescriptionInputs(unpacked);
    SurfaceDescription surfaceDescription = SurfaceDescriptionFunction(surfaceDescriptionInputs);

    #if defined(_ALPHATEST_ON)
        clip(surfaceDescription.Alpha - surfaceDescription.AlphaClipThreshold);
    #endif

    #if defined(LOD_FADE_CROSSFADE) && USE_UNITY_CROSSFADE
        LODFadeCrossFade(unpacked.positionCS);
    #endif

    half3 color = surfaceDescription.BaseColor;
    half alpha = surfaceDescription.Alpha;

    #if defined(_ALPHAPREMULTIPLY_ON)
        color *= alpha;
    #endif
    color = AlphaModulate(color, alpha);

    #if defined(_FOG_FRAGMENT)
        #if defined(VARYINGS_NEED_FOG_AND_VERTEX_LIGHT)
            half fog = InitializeInputDataFog(float4(unpacked.positionWS, 1), unpacked.fogFactorAndVertexLight.x);
            color = MixFog(color, fog);
        #endif
    #endif

    #if !defined(_SURFACE_TYPE_TRANSPARENT)
        alpha = 1;
    #endif
    outColor = half4(color, alpha);
#if defined(_WRITE_RENDERING_LAYERS)
    outRenderingLayers = float4(EncodeMeshRenderingLayer(GetMeshRenderingLayer()), 0, 0, 1);
#endif
#if defined(UNITY_VIRTUAL_TEXTURING)
    outVTFeedback = PackVTFeedbackWithAlpha(surfaceDescription.VTPackedFeedback, unpacked.positionCS.xy, alpha);
    outVTFeedback.rgb *= outVTFeedback.a;
#endif
}

#endif // ULTRARPTARGET_UNLITPASS_INCLUDED
