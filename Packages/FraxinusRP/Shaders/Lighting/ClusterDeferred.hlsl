#ifndef FRAXINUS_CLUSTER_DEFERRED_RASTER_INCLUDED
#define FRAXINUS_CLUSTER_DEFERRED_RASTER_INCLUDED

#ifndef _GPU_LIGHTS_CLUSTER
#define _GPU_LIGHTS_CLUSTER
#endif
#define FRAXINUS_CLUSTER_RASTER_LIGHTING
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/Lighting/ClusterDeferredLighting.hlsl"

// These are input-attachment indices, not GBuffer slice indices. C# supplies
// GBuffer0/1/2, the explicit GBuffer depth slice, and optional rendering layers.
// The lighting/color attachment is updated by blending and is never sampled.
#if defined(_RENDER_PASS_ENABLED)
    #define CLUSTER_INPUT_ALBEDO 0
    #define CLUSTER_INPUT_SPECULAR 1
    #define CLUSTER_INPUT_NORMAL 2
    #define CLUSTER_INPUT_DEPTH 3
    // Match ordinary texture loads: converting UNorm smoothness/normals to
    // half before BRDF decoding measurably shifts sharp HDR specular highlights.
    FRAMEBUFFER_INPUT_X_FLOAT(CLUSTER_INPUT_ALBEDO);
    FRAMEBUFFER_INPUT_X_FLOAT(CLUSTER_INPUT_SPECULAR);
    FRAMEBUFFER_INPUT_X_FLOAT(CLUSTER_INPUT_NORMAL);
    FRAMEBUFFER_INPUT_X_FLOAT(CLUSTER_INPUT_DEPTH);
    #if defined(_LIGHT_LAYERS)
        #define CLUSTER_INPUT_RENDERING_LAYERS 4
        FRAMEBUFFER_INPUT_X_FLOAT(CLUSTER_INPUT_RENDERING_LAYERS);
    #endif
#else
    #include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DeclareDepthTexture.hlsl"
    TEXTURE2D_X(_GBuffer0);
    TEXTURE2D_X(_GBuffer1);
    TEXTURE2D_X(_GBuffer2);
    #if defined(_LIGHT_LAYERS)
        // Explicit binding follows CPU gBufferRenderingLayers even when optional
        // mask/decal attachments change the physical GBuffer slot layout.
        TEXTURE2D_X(_ClusterRenderingLayersTexture);
    #endif
#endif

struct ClusterDeferredAttributes
{
    uint vertexID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct ClusterDeferredVaryings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

ClusterDeferredVaryings ClusterDeferredVertex(ClusterDeferredAttributes input)
{
    ClusterDeferredVaryings output = (ClusterDeferredVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID, UNITY_RAW_FAR_CLIP_VALUE);
    return output;
}

float4 ClusterDeferredFragment(ClusterDeferredVaryings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    uint2 coordSS = (uint2)input.positionCS.xy;
    float renderingLayers = 0;
#if defined(_RENDER_PASS_ENABLED)
    float depth = LOAD_FRAMEBUFFER_X_INPUT(CLUSTER_INPUT_DEPTH, coordSS).r;
    float4 gbuffer0 = LOAD_FRAMEBUFFER_X_INPUT(CLUSTER_INPUT_ALBEDO, coordSS);
    float4 gbuffer1 = LOAD_FRAMEBUFFER_X_INPUT(CLUSTER_INPUT_SPECULAR, coordSS);
    float4 gbuffer2 = LOAD_FRAMEBUFFER_X_INPUT(CLUSTER_INPUT_NORMAL, coordSS);
    #if defined(_LIGHT_LAYERS)
        renderingLayers = LOAD_FRAMEBUFFER_X_INPUT(CLUSTER_INPUT_RENDERING_LAYERS, coordSS).r;
    #endif
#else
    float depth = LoadSceneDepth(coordSS);
    float4 gbuffer0 = LOAD_TEXTURE2D_X(_GBuffer0, coordSS);
    float4 gbuffer1 = LOAD_TEXTURE2D_X(_GBuffer1, coordSS);
    float4 gbuffer2 = LOAD_TEXTURE2D_X(_GBuffer2, coordSS);
    #if defined(_LIGHT_LAYERS)
        renderingLayers = LOAD_TEXTURE2D_X(_ClusterRenderingLayersTexture, coordSS).r;
    #endif
#endif

    if (depth == UNITY_RAW_FAR_CLIP_VALUE)
        return float4(0, 0, 0, 1);

    // Raster SV_Position already carries the half-pixel center; unlike compute,
    // GetPositionInput does not add it for fragment shaders.
    PositionInputs receiver = GetPositionInput(input.positionCS.xy, _ScreenSize.zw, depth,
        UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
    ShadingData data = DecodeShadingDataFromGBuffer(receiver, gbuffer0, gbuffer1, gbuffer2, renderingLayers);
    if ((data.materialFlags & kMaterialFlagAPRTDebug) != 0)
        return float4(0, 0, 0, 1);

    float3 directLighting = EvaluateClusterLighting(receiver, data, FRAXINUS_CLUSTER_SHADING_MODEL);
    // Blend One SrcAlpha applies the same GI/emission correction as compute's
    // read-modify-write while leaving the existing destination alpha untouched.
    return float4(directLighting, GetLegacyIndirectOcclusion(data, coordSS));
}

#endif
