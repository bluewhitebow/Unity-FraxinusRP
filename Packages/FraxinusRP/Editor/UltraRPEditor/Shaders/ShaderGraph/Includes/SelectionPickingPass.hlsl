#ifndef ULTRARPTARGET_SELECTIONPICKINGPASS_INCLUDED
#define ULTRARPTARGET_SELECTIONPICKINGPASS_INCLUDED

PackedVaryings vert(Attributes input)
{
    Varyings output = BuildVaryings(input);
    return PackVaryings(output);
}

half4 frag(PackedVaryings packedInput) : SV_Target
{
    Varyings unpacked = UnpackVaryings(packedInput);
    UNITY_SETUP_INSTANCE_ID(unpacked);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(unpacked);

    SurfaceDescriptionInputs surfaceDescriptionInputs = BuildSurfaceDescriptionInputs(unpacked);
    SurfaceDescription surfaceDescription = SurfaceDescriptionFunction(surfaceDescriptionInputs);

    #if defined(_ALPHATEST_ON) && defined(ALPHA_CLIP_THRESHOLD)
        clip(surfaceDescription.Alpha - surfaceDescription.AlphaClipThreshold);
    #endif

    #ifdef SCENESELECTIONPASS
        return half4(_ObjectId, _PassValue, 1, 1);
    #endif

    #ifdef SCENEPICKINGPASS
        return _SelectionID;
    #endif

    return half4(0, 0, 0, 0);
}

#endif // ULTRARPTARGET_SELECTIONPICKINGPASS_INCLUDED
