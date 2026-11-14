#ifndef FRAXINUS_DEFERRED_PLUS_INCLUDED
#define FRAXINUS_DEFERRED_PLUS_INCLUDED

// Cluster traversal adapted from Unity Graphics 6000.3/staging, commit
// 4064d5bb6a1e3748a7cdddc244b89934ad1c3145, Shaders/Utils/ClusterDeferred.hlsl.
// Copyright Unity Technologies. Licensed under the Unity Companion License.
// GBuffer decoding, material models and lighting extensions belong to Fraxinus.
#ifndef _FORWARD_PLUS
#define _FORWARD_PLUS
#endif
#define FRAXINUS_DEFERRED_PLUS_LIGHTING
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/Lighting/DeferredLightingCommon.hlsl"

#ifndef FRAXINUS_DEFERRED_PLUS_MAIN_LIGHT_SHADOW
#define FRAXINUS_DEFERRED_PLUS_MAIN_LIGHT_SHADOW(shadowCoord, receiver, normalWS, shadowMask, occlusionProbeChannels) \
    MainLightShadow(shadowCoord, receiver.positionWS, shadowMask, occlusionProbeChannels)
#endif

int _DeferredPlusMainLightIndex;
// The same visible-light ordering as ForwardLights, excluding the main light.
// x: base contribution; y: minimum roughness.
float4 _MainLightFraxinusParams;
float4 _AdditionalLightsFraxinusParams[MAX_VISIBLE_LIGHTS];

#if defined(_RENDER_PASS_ENABLED)
    #define DEFERRED_PLUS_ALBEDO 0
    #define DEFERRED_PLUS_SPECULAR 1
    #define DEFERRED_PLUS_NORMAL 2
    #define DEFERRED_PLUS_DEPTH 3
    FRAMEBUFFER_INPUT_X_FLOAT(DEFERRED_PLUS_ALBEDO);
    FRAMEBUFFER_INPUT_X_FLOAT(DEFERRED_PLUS_SPECULAR);
    FRAMEBUFFER_INPUT_X_FLOAT(DEFERRED_PLUS_NORMAL);
    FRAMEBUFFER_INPUT_X_FLOAT(DEFERRED_PLUS_DEPTH);
    #if defined(_LIGHT_LAYERS)
        #define DEFERRED_PLUS_LAYERS 4
        FRAMEBUFFER_INPUT_X_FLOAT(DEFERRED_PLUS_LAYERS);
    #endif
    #if defined(_DEFERRED_MIXED_LIGHTING)
        #if defined(_LIGHT_LAYERS)
            #define DEFERRED_PLUS_SHADOWMASK 5
        #else
            #define DEFERRED_PLUS_SHADOWMASK 4
        #endif
        FRAMEBUFFER_INPUT_X_FLOAT(DEFERRED_PLUS_SHADOWMASK);
    #endif
#else
    #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DeclareDepthTexture.hlsl"
    TEXTURE2D_X(_GBuffer0);
    TEXTURE2D_X(_GBuffer1);
    TEXTURE2D_X(_GBuffer2);
    #if defined(_LIGHT_LAYERS)
        TEXTURE2D_X(_DeferredPlusRenderingLayersTexture);
    #endif
    #if defined(_DEFERRED_MIXED_LIGHTING)
        TEXTURE2D_X(_DeferredPlusShadowMaskTexture);
    #endif
#endif

struct DeferredPlusAttributes
{
    uint vertexID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct DeferredPlusVaryings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

DeferredPlusVaryings DeferredPlusVertex(DeferredPlusAttributes input)
{
    DeferredPlusVaryings output = (DeferredPlusVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID, UNITY_RAW_FAR_CLIP_VALUE);
    return output;
}

float3 EvaluateDeferredPlusAdditionalLight(uint lightIndex, PositionInputs receiver,
    ShadingData data, half4 shadowMask, bool receiveShadows)
{
    Light light = GetAdditionalLight(lightIndex, receiver.positionWS, shadowMask);
    int lightFlags = _AdditionalLightsColor[lightIndex].a > 0
        ? kLightFlagSubtractiveMixedLighting : 0;
    if (!ShouldEvaluateLight(data, lightFlags, light.layerMask))
        return 0;
    if (!receiveShadows)
        light.shadowAttenuation = 1;

    float4 lightParams = _AdditionalLightsFraxinusParams[lightIndex];
#if !defined(FRAXINUS_CLUSTER_STENCIL_LIGHTING)
    light.distanceAttenuation *= lightParams.x;
#endif
    return EvaluateDirectLight(data, light, lightParams.y);
}

float3 EvaluateDeferredPlusLighting(PositionInputs receiver, ShadingData data, half4 shadowMask)
{
    float3 lighting = 0;
    bool receiveShadows = (data.materialFlags & kMaterialFlagReceiveShadowsOff) == 0;

    // Keep the unique main-light filter outside loops: project filters may use
    // fragment derivatives, and cookies may use implicit texture LOD.
    if (_DeferredPlusMainLightIndex >= 0)
    {
        Light light = GetMainLight();
        int lightFlags = _MainLightColor.a == 0 ? kLightFlagSubtractiveMixedLighting : 0;
        if (ShouldEvaluateLight(data, lightFlags, light.layerMask))
        {
            if (receiveShadows)
            {
#if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                float4 shadowCoord = float4(receiver.positionNDC, 0, 1);
#else
                float4 shadowCoord = TransformWorldToShadowCoord(receiver.positionWS);
#endif
                light.shadowAttenuation = FRAXINUS_DEFERRED_PLUS_MAIN_LIGHT_SHADOW(shadowCoord,
                    receiver, data.normalWS, shadowMask, _MainLightOcclusionProbes);
                light.shadowAttenuation = min(light.shadowAttenuation, LoadContactShadow(receiver.positionSS));
            }
#if defined(_LIGHT_COOKIES)
            light.color *= SampleMainLightCookie(receiver.positionWS);
#endif
#if !defined(FRAXINUS_CLUSTER_STENCIL_LIGHTING)
            light.distanceAttenuation *= _MainLightFraxinusParams.x;
#endif
            lighting += EvaluateDirectLight(data, light, _MainLightFraxinusParams.y);
        }
    }

    // Directional lights precede punctual lights in ForwardLights' constants.
    // The cluster masks contain punctual indices relative to that prefix.
    ClusterIterator iterator = ClusterInit(receiver.positionNDC, receiver.positionWS, 0);
    uint lightIndex;
    [loop] while (ClusterNext(iterator, lightIndex))
    {
        lightIndex += ULTRA_FP_DIRECTIONAL_LIGHTS_COUNT;
        lighting += EvaluateDeferredPlusAdditionalLight(lightIndex, receiver, data, shadowMask, receiveShadows);
    }
    [loop] for (uint directionalIndex = 0; directionalIndex < min(ULTRA_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); ++directionalIndex)
        lighting += EvaluateDeferredPlusAdditionalLight(directionalIndex, receiver, data, shadowMask, receiveShadows);

    float directOcclusion = GetDirectAmbientOcclusion(receiver.positionSS);
    lighting *= directOcclusion;
    lighting += EvaluateDeferredAreaLighting(receiver, data, FRAXINUS_CLUSTER_SHADING_MODEL, directOcclusion);
    return lighting;
}

float4 DeferredPlusFragment(DeferredPlusVaryings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    uint2 positionSS = (uint2)input.positionCS.xy;
    float renderingLayers = 0;
    half4 shadowMask = 1;
#if defined(_RENDER_PASS_ENABLED)
    float depth = LOAD_FRAMEBUFFER_X_INPUT(DEFERRED_PLUS_DEPTH, positionSS).r;
    float4 gbuffer0 = LOAD_FRAMEBUFFER_X_INPUT(DEFERRED_PLUS_ALBEDO, positionSS);
    float4 gbuffer1 = LOAD_FRAMEBUFFER_X_INPUT(DEFERRED_PLUS_SPECULAR, positionSS);
    float4 gbuffer2 = LOAD_FRAMEBUFFER_X_INPUT(DEFERRED_PLUS_NORMAL, positionSS);
    #if defined(_LIGHT_LAYERS)
        renderingLayers = LOAD_FRAMEBUFFER_X_INPUT(DEFERRED_PLUS_LAYERS, positionSS).r;
    #endif
    #if defined(_DEFERRED_MIXED_LIGHTING)
        shadowMask = LOAD_FRAMEBUFFER_X_INPUT(DEFERRED_PLUS_SHADOWMASK, positionSS);
    #endif
#else
    float depth = LoadSceneDepth(positionSS);
    float4 gbuffer0 = LOAD_TEXTURE2D_X(_GBuffer0, positionSS);
    float4 gbuffer1 = LOAD_TEXTURE2D_X(_GBuffer1, positionSS);
    float4 gbuffer2 = LOAD_TEXTURE2D_X(_GBuffer2, positionSS);
    #if defined(_LIGHT_LAYERS)
        renderingLayers = LOAD_TEXTURE2D_X(_DeferredPlusRenderingLayersTexture, positionSS).r;
    #endif
    #if defined(_DEFERRED_MIXED_LIGHTING)
        shadowMask = LOAD_TEXTURE2D_X(_DeferredPlusShadowMaskTexture, positionSS);
    #endif
#endif

    if (depth == UNITY_RAW_FAR_CLIP_VALUE)
        return float4(0, 0, 0, 1);

    PositionInputs receiver = GetPositionInput(input.positionCS.xy, _ScreenSize.zw, depth,
        UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
    ShadingData data = DecodeShadingDataFromGBuffer(receiver, gbuffer0, gbuffer1, gbuffer2, renderingLayers);
    if ((data.materialFlags & kMaterialFlagAPRTDebug) != 0)
        return float4(0, 0, 0, 1);

    float3 lighting = EvaluateDeferredPlusLighting(receiver, data, shadowMask);
    float indirectOcclusion = 1;
    if (_FraxinusAmbientOcclusionEnabled != 0 &&
        (data.materialFlags & kMaterialFlagScreenSpaceOcclusionApplied) == 0)
    {
        float screenOcclusion = saturate(LOAD_TEXTURE2D_X(_ScreenSpaceOcclusionTexture, positionSS).r);
        indirectOcclusion = screenOcclusion < data.occlusion ? screenOcclusion * rcp(data.occlusion) : 1;
    }
    return float4(lighting, indirectOcclusion);
}

#endif
