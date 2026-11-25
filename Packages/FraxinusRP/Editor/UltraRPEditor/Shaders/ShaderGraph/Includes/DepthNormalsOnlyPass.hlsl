#ifndef ULTRARPTARGET_DEPTHNORMALSONLYPASS_INCLUDED
#define ULTRARPTARGET_DEPTHNORMALSONLYPASS_INCLUDED

PackedVaryings vert(Attributes input)
{
    Varyings output = BuildVaryings(input);
    return PackVaryings(output);
}

void frag(PackedVaryings packedInput, out half4 outNormalWS : SV_Target0
#if defined(_WRITE_RENDERING_LAYERS)
    , out float4 outRenderingLayers : SV_Target1
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

    float3 normalWS = unpacked.normalWS;

    #if defined(_NORMALMAP)
        #if defined(_NORMAL_DROPOFF_TS)
            float3x3 tangentToWorld = float3x3(unpacked.tangentWS.xyz, cross(unpacked.normalWS, unpacked.tangentWS.xyz) * unpacked.tangentWS.w, unpacked.normalWS);
            normalWS = TransformTangentToWorld(surfaceDescription.NormalTS, tangentToWorld);
        #elif defined(_NORMAL_DROPOFF_OS)
            normalWS = TransformObjectToWorldNormal(surfaceDescription.NormalOS);
        #elif defined(_NORMAL_DROPOFF_WS)
            normalWS = surfaceDescription.NormalWS;
        #endif
    #endif

    normalWS = NormalizeNormalPerPixel(normalWS);
    #if defined(_GBUFFER_NORMALS_OCT)
    // Deferred shares this target with its packed GBuffer normals.
    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
    outNormalWS = half4(PackFloat2To888(remappedOctNormalWS), 0.0);
    #else
    outNormalWS = half4(normalWS, 0.0);
    #endif
#if defined(_WRITE_RENDERING_LAYERS)
    outRenderingLayers = float4(EncodeMeshRenderingLayer(GetMeshRenderingLayer()), 0, 0, 0);
#endif
}

#endif // ULTRARPTARGET_DEPTHNORMALSONLYPASS_INCLUDED
