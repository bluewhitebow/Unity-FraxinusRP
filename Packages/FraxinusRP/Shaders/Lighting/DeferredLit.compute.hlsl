#pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal

// One classifier builds tile lists for the three lighting kernels.
#pragma kernel DeferredClassifyTiles        DEFERRED_CLASSIFY_TILES
#pragma kernel DeferredLighting0            DEFERRED_LIGHTING           DEFERRED_COMPUTE_ENTRY=DeferredLighting0
#pragma kernel DeferredLighting1            DEFERRED_LIGHTING           DEFERRED_COMPUTE_ENTRY=DeferredLighting1        CLUSTER_SIMPLE_LIT
// Character stencil values currently use the Lit lighting model.
#pragma kernel DeferredLighting2            DEFERRED_LIGHTING           DEFERRED_COMPUTE_ENTRY=DeferredLighting2

#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
#pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
#pragma multi_compile _ _SHADOWS_SOFT
#pragma multi_compile _ _LIGHT_COOKIES
#pragma multi_compile _ _LIGHT_LAYERS
#pragma multi_compile _ _GBUFFER_NORMALS_OCT

#define _GPU_LIGHTS_CLUSTER

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/Runtime/UltraRP/LightLoop/Cluster/GPULights.cs.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/Runtime/UltraRP/LightLoop/Cluster/ShadingModels.cs.hlsl"

#if defined(DEFERRED_LIGHTING)
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/Lighting/ClusterDeferredLighting.hlsl"
#endif

#if defined(DEFERRED_CLASSIFY_TILES)
Texture2D<uint2> _StencilTexture;

RWBuffer<uint> g_DispatchIndirectBuffer;
RWStructuredBuffer<uint> g_TileList;
#elif defined(DEFERRED_LIGHTING)

StructuredBuffer<uint> g_TileList;
Texture2D<uint2> _StencilTexture;
TEXTURE2D_X(_GBuffer0);
TEXTURE2D_X(_GBuffer1);
TEXTURE2D_X(_GBuffer2);
// Optional attachments follow the layout declared by UnityGBuffer.hlsl.
#ifdef GBUFFER_OPTIONAL_SLOT_1
TEXTURE2D_X(_GBuffer4);
#endif
#ifdef GBUFFER_OPTIONAL_SLOT_2
TEXTURE2D_X(_GBuffer5);
#endif
#ifdef GBUFFER_OPTIONAL_SLOT_3
TEXTURE2D_X(_GBuffer6);
#endif

RWTexture2D<float4> _LightingTexture;
#endif

#define DEFERRED_LIGHTING_TILE_SIZE (16)
#define DEFERRED_LIGHTING_GROUP_SIZE (DEFERRED_LIGHTING_TILE_SIZE / 2)
#define DEFERRED_LIGHTING_THREADS   (64)
#define HasShadingModel(stencilVal) ((stencilVal >> SHADINGMODELS_USER_MASK_BITS) > 0)
#define StencilToShadingModel(stencilVal) (stencilVal & SHADINGMODELS_MODELS_MASK)

uint2 DecodeTileIndex(uint encoded)
{
    return uint2((encoded >> TILE_INDEX_SHIFT_X) & TILE_INDEX_MASK, (encoded >> TILE_INDEX_SHIFT_Y) & TILE_INDEX_MASK);
}

uint EncodeTileIndex(uint2 tileID)
{
    return (unity_StereoEyeIndex << TILE_INDEX_SHIFT_EYE) | (tileID.y << TILE_INDEX_SHIFT_Y) | (tileID.x << TILE_INDEX_SHIFT_X);
}

uint ShadingModelToIndex(uint shadingModel)
{
    if (shadingModel == SHADINGMODELS_SIMPLE_LIT)
        return 1;
    if (shadingModel == SHADINGMODELS_CHARACTER)
        return 2;
    // Preserve the Lit/default bucket; lighting still checks each pixel's stencil.
    return 0;
}

#if defined(DEFERRED_LIGHTING)

int _ShadingModelIndex;
int _ShadingModelStencil;
int2 _TilesNum;

void GetDispatchCoordinates(uint groupId, uint2 groupThreadId, out uint2 tileCoord, out uint2 positionSS)
{
    uint encodedTileIndex = g_TileList[_ShadingModelIndex * _TilesNum.x * _TilesNum.y + groupId / 4];
    tileCoord = DecodeTileIndex(encodedTileIndex);
    // Four consecutive groups cover the quadrants of one classified tile.
    positionSS = tileCoord * DEFERRED_LIGHTING_TILE_SIZE
        + uint2(groupId & 1, (groupId >> 1) & 1) * DEFERRED_LIGHTING_GROUP_SIZE
        + groupThreadId;
}

#endif

#if defined(DEFERRED_CLASSIFY_TILES)

groupshared uint gs_ShadingModelFlags[SHADINGMODELS_CUR_MODELS_NUM];

// A 16x16 tile uses 64 threads, each classifying four pixels.
[numthreads(DEFERRED_LIGHTING_THREADS, 1, 1)]
void DeferredClassifyTiles(uint groupIndex : SV_GroupIndex, uint3 groupId : SV_GroupID)
{
    uint2 tileCoord = groupId.xy;
    uint2 screenSize = (uint2)_ScreenSize.xy;
    uint2 tileCounts = (screenSize + DEFERRED_LIGHTING_TILE_SIZE - 1) / DEFERRED_LIGHTING_TILE_SIZE;
    uint tileCount = tileCounts.x * tileCounts.y;
    uint2 tileOrigin = DEFERRED_LIGHTING_TILE_SIZE * tileCoord;
    uint shadingModelFlags[SHADINGMODELS_CUR_MODELS_NUM] = (uint[SHADINGMODELS_CUR_MODELS_NUM])0;

    // Each supported shading model has one shared flag per tile.
    if (groupIndex < SHADINGMODELS_CUR_MODELS_NUM)
    {
        gs_ShadingModelFlags[groupIndex] = 0;
    }
    GroupMemoryBarrierWithGroupSync();

    // Clamp the partial tiles at the screen edge to the last valid pixel.
    UNITY_UNROLL
    for (uint sampleIndex = 0; sampleIndex < 4; ++sampleIndex)
    {
        uint pixelIndex = sampleIndex * DEFERRED_LIGHTING_THREADS + groupIndex;
        uint2 pixelOffset = uint2(pixelIndex % DEFERRED_LIGHTING_TILE_SIZE, pixelIndex / DEFERRED_LIGHTING_TILE_SIZE);
        uint2 pixelCoord = min(tileOrigin + pixelOffset, screenSize - 1);
        uint stencil = GetStencilValue(LOAD_TEXTURE2D_X(_StencilTexture, pixelCoord));
        uint modelIndex = ShadingModelToIndex(StencilToShadingModel(stencil));
        shadingModelFlags[modelIndex] |= HasShadingModel(stencil) ? 1u : 0u;
    }

    // Combine the models observed by all 64 threads before writing tile lists.
    UNITY_UNROLL
    for (uint modelIndex = 0; modelIndex < SHADINGMODELS_CUR_MODELS_NUM; ++modelIndex)
    {
        if (shadingModelFlags[modelIndex] != 0)
            InterlockedOr(gs_ShadingModelFlags[modelIndex], shadingModelFlags[modelIndex]);
    }
    GroupMemoryBarrierWithGroupSync();

    // One thread per model appends this tile and reserves its lighting groups.
    if (groupIndex < SHADINGMODELS_CUR_MODELS_NUM)
    {
        if (gs_ShadingModelFlags[groupIndex] != 0)
        {
            uint previousGroupCount;
            // C# initializes Y and Z to one. Each tile adds four 8x8 groups.
            InterlockedAdd(g_DispatchIndirectBuffer[groupIndex * 3], 4, previousGroupCount);
            uint tileOffset = previousGroupCount / 4;
            g_TileList[groupIndex * tileCount + tileOffset] = EncodeTileIndex(tileCoord);
        }
    }
}

#elif defined(DEFERRED_LIGHTING)

[numthreads(DEFERRED_LIGHTING_GROUP_SIZE, DEFERRED_LIGHTING_GROUP_SIZE, 1)]
void DEFERRED_COMPUTE_ENTRY(uint2 groupThreadId : SV_GroupThreadID, uint groupId : SV_GroupID)
{
    uint2 coordSS, tileCoord;
    GetDispatchCoordinates(groupId, groupThreadId, tileCoord, coordSS);
    if (any(coordSS >= (uint2)_ScreenSize.xy))
        return;

    float depth = LoadSceneDepth(coordSS);
    if (depth == UNITY_RAW_FAR_CLIP_VALUE)
        return;

    uint stencil = GetStencilValue(LOAD_TEXTURE2D_X(_StencilTexture, coordSS));
    if (StencilToShadingModel(stencil) != (uint)_ShadingModelStencil)
        return;

    PositionInputs receiver = GetPositionInput(coordSS, _ScreenSize.zw, depth,
        UNITY_MATRIX_I_VP, UNITY_MATRIX_V, tileCoord);
    float4 gbuffer0 = LOAD_TEXTURE2D_X(_GBuffer0, coordSS);
    float4 gbuffer1 = LOAD_TEXTURE2D_X(_GBuffer1, coordSS);
    float4 gbuffer2 = LOAD_TEXTURE2D_X(_GBuffer2, coordSS);
    float renderingLayers = 0;
#ifdef _LIGHT_LAYERS
    renderingLayers = LOAD_TEXTURE2D_X(MERGE_NAME(_, GBUFFER_LIGHT_LAYERS), coordSS).r;
#endif
    ShadingData data = DecodeShadingDataFromGBuffer(receiver, gbuffer0, gbuffer1, gbuffer2, renderingLayers);
    if ((data.materialFlags & kMaterialFlagAPRTDebug) != 0)
        return;

    float3 directLighting = EvaluateClusterLighting(receiver, data, (uint)_ShadingModelStencil);
    // GI and emission already share GBuffer3. Legacy writers receive the same
    // combined-buffer correction as Stencil; updated writers preserve emission.
    float3 indirectAndEmission = _LightingTexture[coordSS].rgb
        * GetLegacyIndirectOcclusion(data, coordSS);
    _LightingTexture[coordSS] = float4(directLighting + indirectAndEmission, 1.0);
}

#endif
