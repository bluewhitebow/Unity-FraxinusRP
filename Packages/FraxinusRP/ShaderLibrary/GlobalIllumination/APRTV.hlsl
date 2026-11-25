#ifndef __APRTV_HLSL__
#define __APRTV_HLSL__

// TODO: Phase 7+ - 当前是 Phase 7 的基础框架，完整实现需要更多采样逻辑

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SphericalHarmonics.hlsl"
#include "ShaderVariablesAPRTV.cs.hlsl"

// ============================================================================
// Macro Definitions - 从 ShaderVariablesAPRTV 中解包数据
// ============================================================================

#define _APRTVWorldOffset _Offset_LayerCount.xyz
#define _APRTVLayerCount (uint)(_Offset_LayerCount.w)
#define _APRTVIndirectionEntryDim _MinLoadedCellInEntries_IndirectionEntryDim.w
#define _APRTVRcpIndirectionEntryDim _MaxLoadedCellInEntries_RcpIndirectionEntryDim.w
#define _APRTVMinBrickSize _PoolDim_MinBrickSize.w
#define _APRTVPoolDim _PoolDim_MinBrickSize.xyz
#define _APRTVRcpPoolDim _RcpPoolDim_XY.xyz
#define _APRTVRcpPoolDimXY _RcpPoolDim_XY.w
#define _APRTVMinEntryPosition _MinEntryPos_Noise.xyz
#define _APRTVSamplingNoise _MinEntryPos_Noise.w
#define _APRTVEntryCount _EntryCount_X_XY_LeakReduction.xy
#define _APRTVLeakReductionMode _EntryCount_X_XY_LeakReduction.z
#define _APRTVNormalBias _Biases_NormalizationClamp.x
#define _APRTVViewBias _Biases_NormalizationClamp.y
#define _APRTVMinReflProbeNormalizationFactor _Biases_NormalizationClamp.z
#define _APRTVMaxReflProbeNormalizationFactor _Biases_NormalizationClamp.w
#define _APRTVMinLoadedCellInEntries _MinLoadedCellInEntries_IndirectionEntryDim.xyz
#define _APRTVMaxLoadedCellInEntries _MaxLoadedCellInEntries_RcpIndirectionEntryDim.xyz
#define _APRTVFrameIndex _FrameIndex_Weights.x
#define _APRTVWeight _FrameIndex_Weights.y
#define _APRTVSkyOcclusionWeight _FrameIndex_Weights.z
#define _APRTVSkyDirectionWeight _FrameIndex_Weights.w

// ============================================================================
// Samplers
// ============================================================================

SAMPLER(s_linear_clamp_sampler);
SAMPLER(s_point_clamp_sampler);

// ============================================================================
// APRTV Resources Structure
// ============================================================================

struct APRTVResources
{
    // Index buffers
    StructuredBuffer<int> index;
    StructuredBuffer<uint3> cellIndices;
    
    // SH texture pools
    TEXTURE3D(L0_L1Rx);
    TEXTURE3D(L1G_L1Ry);
    TEXTURE3D(L1B_L1Rz);
    TEXTURE3D(L2_0);
    TEXTURE3D(L2_1);
    TEXTURE3D(L2_2);
    TEXTURE3D(L2_3);
    
    // Optional textures
    TEXTURE3D(Validity);
    TEXTURE3D(SkyOcclusion);
    TEXTURE3D(ProbeOcclusion);
    
    // Constant resources
    StructuredBuffer<float3> SkyPrecomputedDirections;
    StructuredBuffer<uint> AntiLeakData;
};

// ============================================================================
// APRTV Sample Structure
// ============================================================================

struct APRTVSample
{
    // Spherical Harmonics L0-L1
    half3 L0;
    half3 L1_R;
    half3 L1_G;
    half3 L1_B;
    
#ifdef APRTV_SH_L2
    // Spherical Harmonics L2 (optional)
    half4 L2_R;
    half4 L2_G;
    half4 L2_B;
    half3 L2_C;
#endif
    
    // Sky occlusion
    float4 skyOcclusionL0L1;
    float3 skyShadingDirection;
    
    // Probe occlusion (for mixed lighting)
#ifdef APRTV_USE_PROBE_OCCLUSION
    float4 probeOcclusion;
#endif
    
    // Status tracking
    int status; // -1=invalid, 0=encoded, 1=decoded
};

// ============================================================================
// Global APRTV Resources (Bound by APRTVReferenceVolume.Binding)
// ============================================================================

StructuredBuffer<int> _APRTVIndex;
StructuredBuffer<uint3> _APRTVCellIndices;
StructuredBuffer<float3> _APRTVSkyPrecomputedDirections;
StructuredBuffer<uint> _APRTVAntiLeakData;

TEXTURE3D(_APRTVPool_L0_L1Rx);
TEXTURE3D(_APRTVPool_L1G_L1Ry);
TEXTURE3D(_APRTVPool_L1B_L1Rz);
TEXTURE3D(_APRTVPool_L2_0);
TEXTURE3D(_APRTVPool_L2_1);
TEXTURE3D(_APRTVPool_L2_2);
TEXTURE3D(_APRTVPool_L2_3);
TEXTURE3D(_APRTVPool_Validity);
TEXTURE3D(_APRTVPool_SkyOcclusion);
TEXTURE3D(_APRTVPool_ProbeOcclusion);

// ============================================================================
// Helper Functions
// ============================================================================

/// <summary>
/// Calculates probe distance based on subdivision level
/// </summary>
float APRTVProbeDistance(uint subdiv)
{
    return pow(3.0f, subdiv) * _APRTVMinBrickSize / 3.0f;
}

/// <summary>
/// Gets the snapped probe position for a given world position and subdivision
/// </summary>
float3 APRTVGetSnappedProbePosition(float3 posWS, uint subdiv)
{
    float3 distBetweenProbes = APRTVProbeDistance(subdiv);
    float3 dividedPos = posWS / distBetweenProbes;
    return (dividedPos - frac(dividedPos)) * distBetweenProbes;
}

/// <summary>
/// Gets 3D offset for trilinear interpolation sample (0-7)
/// </summary>
uint3 APRTVGetSampleOffset(uint i)
{
    return uint3(i, i >> 1, i >> 2) & 1;
}

/// <summary>
/// Gets validity weight for a probe in trilinear interpolation
/// </summary>
half APRTVGetValidityWeight(uint offset, uint validityMask)
{
    uint mask = 1U << offset;
    return (validityMask & mask) > 0 ? 1.0 : 0.0;
}

// ============================================================================
// Index & Indirection Functions
// ============================================================================

/// <summary>
/// Loads cell index metadata from indirection buffer
/// Returns false if cell is unloaded
/// </summary>
bool APRTVLoadCellIndexMetaData(uint cellFlatIdx, out uint chunkIndex, out int stepSize, out int3 minRelativeIdx, out uint3 sizeOfValid)
{
    uint3 metaData = _APRTVCellIndices[cellFlatIdx];
    
    // Unpack metadata (see APRTVIndexOfIndices.cs for packing format)
    chunkIndex = metaData.x & 0x1FFFFFFF;         // 29 bits: first chunk index
    stepSize = round(pow(3.0f, (metaData.x >> 29) & 0x7)); // 3 bits: min subdiv
    
    minRelativeIdx.x = metaData.y & 0x3FF;         // 10 bits per component
    minRelativeIdx.y = (metaData.y >> 10) & 0x3FF;
    minRelativeIdx.z = (metaData.y >> 20) & 0x3FF;
    
    sizeOfValid.x = metaData.z & 0x3FF;
    sizeOfValid.y = (metaData.z >> 10) & 0x3FF;
    sizeOfValid.z = (metaData.z >> 20) & 0x3FF;
    
    // 0xFFFFFFFF indicates unloaded cell
    return metaData.x != 0xFFFFFFFF;
}

/// <summary>
/// Gets index data for a world position
/// Returns 0xFFFFFFFF if invalid/unloaded
/// </summary>
uint APRTVGetIndexData(float3 posWS)
{
    // Calculate entry position in indirection table
    float3 entryPos = floor(posWS * _APRTVRcpIndirectionEntryDim);
    
    // Check if cell is loaded
    bool isALoadedCell = all(entryPos >= _APRTVMinLoadedCellInEntries) && 
                         all(entryPos <= _APRTVMaxLoadedCellInEntries);
    
    if (!isALoadedCell)
        return 0xFFFFFFFF;
    
    // Calculate flat index
    uint3 entryPosInt = (uint3)(entryPos - _APRTVMinEntryPosition);
    uint flatIdx = dot(entryPosInt, uint3(1, _APRTVEntryCount.x, _APRTVEntryCount.y));
    
    // Load cell metadata
    int stepSize;
    int3 minRelativeIdx;
    uint3 sizeOfValid;
    uint chunkIdx;
    
    if (!APRTVLoadCellIndexMetaData(flatIdx, chunkIdx, stepSize, minRelativeIdx, sizeOfValid))
        return 0xFFFFFFFF;
    
    // TODO: Phase 7+ - 实现完整的索引查找逻辑
    // 当前返回 chunk index 作为占位符
    return chunkIdx;
}

// ============================================================================
// Sampling Functions (TODO: Phase 7+ - 完整实现)
// ============================================================================

/// <summary>
/// Samples APRTV at a world position with normal
/// TODO: Phase 7+ - 当前是占位符实现
/// </summary>
APRTVSample APRTVSampleProbes(float3 posWS, float3 normalWS)
{
    APRTVSample result = (APRTVSample)0;
    result.status = -1; // Invalid
    
    // TODO: Phase 7+ - 实现以下步骤:
    // 1. 应用 bias (normal + view)
    // 2. 获取 index data
    // 3. 三线性插值采样 8 个探针
    // 4. 处理 validity mask (leak reduction)
    // 5. Decode SH 数据
    // 6. 采样 sky occlusion (optional)
    
    return result;
}

/// <summary>
/// Evaluates diffuse lighting from APRTV sample
/// TODO: Phase 7+ - 完整 SH 评估
/// </summary>
float3 APRTVEvaluateDiffuseLighting(APRTVSample sample, float3 normalWS)
{
    if (sample.status < 0)
        return float3(0, 0, 0);
    
    // TODO: Phase 7+ - 完整 SH 评估
    // 当前返回 L0 (ambient)
    return sample.L0 * _APRTVWeight;
}

/// <summary>
/// Evaluates specular lighting from APRTV sample
/// TODO: Phase 7+ - 完整反射采样
/// </summary>
float3 APRTVEvaluateSpecularLighting(APRTVSample sample, float3 reflDir, float roughness)
{
    if (sample.status < 0)
        return float3(0, 0, 0);
    
    // TODO: Phase 7+ - 基于粗糙度的反射采样
    return float3(0, 0, 0);
}

// ============================================================================
// Public API - 主入口函数
// ============================================================================

/// <summary>
/// Evaluates APRTV indirect lighting for a surface
/// </summary>
/// <param name="posWS">World space position</param>
/// <param name="normalWS">World space normal</param>
/// <param name="viewWS">World space view direction</param>
/// <returns>Indirect diffuse lighting</returns>
float3 EvaluateAPRTVDiffuse(float3 posWS, float3 normalWS, float3 viewWS)
{
    // Apply world offset
    posWS += _APRTVWorldOffset;
    
    // Sample probes
    APRTVSample sample = APRTVSampleProbes(posWS, normalWS);
    
    // Evaluate diffuse lighting
    return APRTVEvaluateDiffuseLighting(sample, normalWS);
}

/// <summary>
/// Evaluates APRTV specular reflection for a surface
/// </summary>
/// <param name="posWS">World space position</param>
/// <param name="normalWS">World space normal</param>
/// <param name="viewWS">World space view direction</param>
/// <param name="roughness">Surface roughness [0,1]</param>
/// <returns>Indirect specular lighting</returns>
float3 EvaluateAPRTVSpecular(float3 posWS, float3 normalWS, float3 viewWS, float roughness)
{
    // Apply world offset
    posWS += _APRTVWorldOffset;
    
    // Calculate reflection direction
    float3 reflDir = reflect(-viewWS, normalWS);
    
    // Sample probes
    APRTVSample sample = APRTVSampleProbes(posWS, normalWS);
    
    // Evaluate specular lighting
    return APRTVEvaluateSpecularLighting(sample, reflDir, roughness);
}

#endif // __APRTV_HLSL__
