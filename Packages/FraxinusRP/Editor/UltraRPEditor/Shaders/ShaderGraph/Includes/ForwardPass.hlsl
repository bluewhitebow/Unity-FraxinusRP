#ifndef ULTRARPTARGET_FORWARDPASS_INCLUDED
#define ULTRARPTARGET_FORWARDPASS_INCLUDED

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

    InputData inputData = (InputData)0;
    inputData.positionWS = unpacked.positionWS;
    inputData.positionCS = unpacked.positionCS;

    #if defined(VARYINGS_NEED_NORMAL_WS)
        inputData.normalWS = NormalizeNormalPerPixel(unpacked.normalWS);
    #endif

    #if defined(VARYINGS_NEED_TANGENT_WS)
        inputData.tangentToWorld = half3x3(unpacked.tangentWS.xyz, 
            cross(unpacked.normalWS, unpacked.tangentWS.xyz) * unpacked.tangentWS.w, 
            unpacked.normalWS);
    #endif

    #if defined(_NORMALMAP)
        #if defined(_NORMAL_DROPOFF_TS)
            inputData.normalWS = TransformTangentToWorld(surfaceDescription.NormalTS, inputData.tangentToWorld);
        #elif defined(_NORMAL_DROPOFF_OS)
            inputData.normalWS = TransformObjectToWorldNormal(surfaceDescription.NormalOS);
        #elif defined(_NORMAL_DROPOFF_WS)
            inputData.normalWS = surfaceDescription.NormalWS;
        #endif
        inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
    #endif

    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(unpacked.positionWS);

    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
        inputData.shadowCoord = unpacked.shadowCoord;
    #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
        inputData.shadowCoord = TransformWorldToShadowCoord(unpacked.positionWS);
    #endif

    #if defined(VARYINGS_NEED_FOG_AND_VERTEX_LIGHT)
        inputData.fogCoord = InitializeInputDataFog(float4(unpacked.positionWS, 1.0), unpacked.fogFactorAndVertexLight.x);
        inputData.vertexLighting = unpacked.fogFactorAndVertexLight.yzw;
    #endif

    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo = surfaceDescription.BaseColor;
    surfaceData.alpha = surfaceDescription.Alpha;

    #if defined(_SPECULAR_SETUP)
        surfaceData.specular = surfaceDescription.Specular;
        surfaceData.metallic = 0.0;
    #else
        surfaceData.specular = 0.0;
        surfaceData.metallic = surfaceDescription.Metallic;
    #endif

    surfaceData.smoothness = surfaceDescription.Smoothness;
    surfaceData.normalTS = half3(0, 0, 1);
    #if defined(_NORMALMAP) && defined(_NORMAL_DROPOFF_TS)
        surfaceData.normalTS = surfaceDescription.NormalTS;
    #endif
    surfaceData.occlusion = surfaceDescription.Occlusion;
    surfaceData.emission = surfaceDescription.Emission;

    inputData.shadowMask = SAMPLE_SHADOWMASK(unpacked.staticLightmapUV);
    #if defined(DYNAMICLIGHTMAP_ON)
        inputData.bakedGI = SAMPLE_GI(unpacked.staticLightmapUV, unpacked.dynamicLightmapUV, unpacked.sh, inputData.normalWS);
    #elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
        inputData.bakedGI = SAMPLE_GI(unpacked.sh, GetAbsolutePositionWS(inputData.positionWS),
            inputData.normalWS, inputData.viewDirectionWS, unpacked.positionCS.xy, unpacked.probeOcclusion, inputData.shadowMask);
    #else
        inputData.bakedGI = SAMPLE_GI(unpacked.staticLightmapUV, unpacked.sh, inputData.normalWS);
    #endif
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(unpacked.positionCS);
    surfaceData.albedo = AlphaModulate(surfaceData.albedo, surfaceData.alpha);

    half4 color = UniversalFragmentPBR(inputData, surfaceData, NormalizeNormalPerPixel(unpacked.normalWS));

    #if defined(_FOG_FRAGMENT)
        if (!APRTDebugDisplayEnabled())
            color.rgb = MixFog(color.rgb, inputData.fogCoord);
    #endif

    #if !defined(_SURFACE_TYPE_TRANSPARENT)
        color.a = 1;
    #endif
    outColor = color;
#if defined(_WRITE_RENDERING_LAYERS)
    outRenderingLayers = float4(EncodeMeshRenderingLayer(GetMeshRenderingLayer()), 0, 0, 1);
#endif
#if defined(UNITY_VIRTUAL_TEXTURING)
    float feedbackAlpha = 1.0;
    #if defined(_SURFACE_TYPE_TRANSPARENT)
        feedbackAlpha = surfaceDescription.Alpha;
    #endif
    outVTFeedback = PackVTFeedbackWithAlpha(surfaceDescription.VTPackedFeedback, unpacked.positionCS.xy, feedbackAlpha);
    outVTFeedback.rgb *= outVTFeedback.a;
#endif
}

#endif // ULTRARPTARGET_FORWARDPASS_INCLUDED
