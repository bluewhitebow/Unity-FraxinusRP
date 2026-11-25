#ifndef ULTRARPTARGET_MOTION_VECTOR_PASS_INCLUDED
#define ULTRARPTARGET_MOTION_VECTOR_PASS_INCLUDED

#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/MotionVectorsCommon.hlsl"

struct MotionVectorAttributes
{
    // Unity 2022 supplies previous skinned mesh positions in TEXCOORD4.
    float3 previousPositionOS : TEXCOORD4;
};

struct MotionVectorVaryings
{
    float3 positionCSNoJitter : CLIP_POSITION_NO_JITTER;
    float3 previousPositionCSNoJitter : PREVIOUS_CLIP_POSITION_NO_JITTER;
};

void vert(Attributes input, MotionVectorAttributes motionInput,
    out MotionVectorVaryings motionOutput, out PackedVaryings packedOutput)
{
    MotionVectorPassOutput current;
    Varyings output = BuildVaryings(input, current);
    packedOutput = PackVaryings(output);

    bool hasDeformation = unity_MotionVectorsParams.x == 1;
    float3 previousPositionOS = hasDeformation ? motionInput.previousPositionOS : input.positionOS;
#if defined(FEATURES_GRAPH_VERTEX)
    bool evaluatePreviousVertex = hasDeformation;
    #if defined(AUTOMATIC_TIME_BASED_MOTION_VECTORS) && defined(GRAPH_VERTEX_USES_TIME_PARAMETERS_INPUT)
        evaluatePreviousVertex = true;
    #endif
    if (evaluatePreviousVertex)
    {
        Attributes previousInput = input;
        previousInput.positionOS = previousPositionOS;
        VertexDescriptionInputs previousGraphInput = BuildVertexDescriptionInputs(previousInput);
        #if defined(AUTOMATIC_TIME_BASED_MOTION_VECTORS) && defined(GRAPH_VERTEX_USES_TIME_PARAMETERS_INPUT)
            previousGraphInput.TimeParameters = _LastTimeParameters.xyz;
        #endif
        previousPositionOS = VertexDescriptionFunction(previousGraphInput).Position;
    }
    else
        previousPositionOS = current.positionOS;
    #if defined(FEATURES_GRAPH_VERTEX_MOTION_VECTOR_OUTPUT)
        previousPositionOS -= current.motionVector;
    #endif
#endif
    float4 currentCS = mul(_NonJitteredViewProjMatrix, float4(current.positionWS, 1));
    float4 previousCS = mul(_PrevViewProjMatrix, mul(UNITY_PREV_MATRIX_M, float4(previousPositionOS, 1)));
    motionOutput.positionCSNoJitter = currentCS.xyw;
    motionOutput.previousPositionCSNoJitter = previousCS.xyw;
}

float4 frag(MotionVectorVaryings motionInput, PackedVaryings packedInput) : SV_Target
{
    Varyings input = UnpackVaryings(packedInput);
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    SurfaceDescription surface = BuildSurfaceDescription(input);
#if defined(_ALPHATEST_ON)
    clip(surface.Alpha - surface.AlphaClipThreshold);
#endif
#if defined(LOD_FADE_CROSSFADE) && USE_UNITY_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif
    float4 currentCS = float4(motionInput.positionCSNoJitter.xy, 0, motionInput.positionCSNoJitter.z);
    float4 previousCS = float4(motionInput.previousPositionCSNoJitter.xy, 0, motionInput.previousPositionCSNoJitter.z);
    return float4(CalcNdcMotionVectorFromCsPositions(currentCS, previousCS), 0, 0);
}
#endif
