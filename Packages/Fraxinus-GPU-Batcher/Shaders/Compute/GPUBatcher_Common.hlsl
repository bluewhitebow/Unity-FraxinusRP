#ifndef GPUBatcher_COMMON_INCLUDED
#define GPUBatcher_COMMON_INCLUDED

// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

// The same width is used for the linear index, strided loops and CPU dispatch counts.
#if defined(SHADER_API_VULKAN) && (defined(UNITY_PLATFORM_ANDROID) || defined(SHADER_API_MOBILE))
#define THREADS_INSTANCES 128
#else
#define THREADS_INSTANCES 256
#endif
#define MAX_LOD_LEVELS 8
#define DISPATCH_MAX_X 65535u    // max work groups in X dimension (D3D11/D3D12/Vulkan/GLES3.1)

// pool configuration - must match kPoolSize in Fraxinus.GPUBatcher.cs
#define _PoolSizeShift 4
#define _PoolSize (1u << _PoolSizeShift)
#define _PoolSizeMask (_PoolSize - 1u)

// recover linear thread index from 2D dispatch wrapping.
// when groupCount > 65535, C# splits into (65535, ceil(groupCount/65535), 1).
// when groupCount <= 65535, groupId.y == 0 and this is free.
uint GetLinearThreadId(uint3 groupId, uint groupIndex, uint threadsPerGroup)
{
    uint linearGroup = groupId.x + groupId.y * DISPATCH_MAX_X;
    return linearGroup * threadsPerGroup + groupIndex;
}

// recover linear group index for batch kernels (one group = one command).
uint GetLinearGroupId(uint3 groupId)
{
    return groupId.x + groupId.y * DISPATCH_MAX_X;
}


// data structures

struct ChunkInfo
{
    float2 boundsMinXZ;
    float2 boundsMaxXZ;
    float boundsMinY;
    float boundsMaxY;
    uint poolRefStart;    // index into _PoolRefs buffer
    uint poolCount;       // number of pools this chunk uses
    float cullDistanceSq;   // per-chunk camera cull distance squared
    float shadowDistanceSq; // per-chunk shadow cull distance squared
    uint maxShadowCascade; // max cascades for shadow rendering, for early chunk culling
    uint layerMask;        // bit N = layer N has instances in this chunk
    float instanceCullDistanceSq;
};

// pre-matrix: position+sigDensityPacked; post-matrix: boundsCenterWS+maxScaleSigned
struct InstanceInput
{
    float3 positionOrBoundsCenter;
    float  padOrMaxScale;          // 0 pre-matrix; maxScale with sign=isFlipped post-matrix
    uint   sigDensityPacked;       // [0:15] signatureIndex (0xFFFF = invalid), [16:31] densityHash as uint16
};

// rotation + scale, only needed during upload/matrix compute
struct InstanceRotationScale
{
    float4 rotation;
    float3 scale;
};

// staging struct matches CPU GPUStagedInstanceData layout (44B)
struct StagedInstance
{
    float3 position;
    float4 rotation;
    float3 scale;
    uint sigDensityPacked;
};

int   InstSignatureIndex(InstanceInput inst) { uint idx = inst.sigDensityPacked & 0xFFFF; return (idx == 0xFFFFu) ? -1 : (int)idx; }
float InstDensityHash(InstanceInput inst)    { return (float)(inst.sigDensityPacked >> 16) / 65535.0; }
float InstMaxScale(InstanceInput inst)       { return abs(inst.padOrMaxScale); }
bool  InstIsFlipped(InstanceInput inst)      { return inst.padOrMaxScale < 0; }
uint  PackSigDensity(int sigIdx, float densityHash)
{
    uint sigBits = (sigIdx < 0) ? 0xFFFFu : ((uint)sigIdx & 0xFFFFu);
    uint denBits = (uint)(densityHash * 65535.0 + 0.5) & 0xFFFFu;
    return sigBits | (denBits << 16);
}

struct SignatureInfo
{
    // full float (16B)
    float3 boundsCenter;
    float shadowDistanceSq;       // 0 = no limit

    // half-float pairs for LOD thresholds/fade, UNORM16 pairs for offsets (36B)
    uint lodScreenSizes01;        // lod0 | lod1 (half)
    uint lodScreenSizes23;        // lod2 | lod3 (half)
    uint lodScreenSizes45;        // lod4 | lod5 (half)
    uint lodScreenSizes67;        // lod6 | lod7 (half)
    uint lodFadeWidths01;         // fw0 | fw1 (half)
    uint lodFadeWidths23;         // fw2 | fw3 (half)
    uint lodFadeWidths45;         // fw4 | fw5 (half)
    uint lodFadeWidths67;         // fw6 | fw7 (half)
    uint densMinFrac_fadeDur;     // densityMinFraction | fadeDuration/10

    // half pairs (12B)
    uint boundsRadiusLodSize;     // boundsRadius | lodReferenceSize
    uint densParam1_densParam2;   // densityParam1 | densityParam2
    uint fadeWidth_pad;           // densityFadeWidth | groupDensity

    // per-proto bucket start offsets (16B)
    uint cameraBucketOffset;
    uint shadowBucketOffset;
    uint flipBucketOffset;
    uint shadowFlipBucketOffset;

    // bit-packed flags (8B)
    // flags0 bit layout (32 bits):
    //   [0:2]   lodCount - 1              (3 bits, 1-8 -> 0-7)
    //   [3]     crossfadeEnabled          (1 bit)
    //   [4]     useAnimatedCrossfade      (1 bit)
    //   [5:6]   densityMode              (2 bits, 0-2)
    //   [7]     unused
    //   [8:15]  lodShadowMask            (8 bits)
    //   [16:23] percentageFadeMask       (8 bits)
    //   [24:26] minShadowLOD             (3 bits, 0-7)
    //   [27:28] maxShadowCascade - 1     (2 bits, 1-4 -> 0-3)
    //   [29:31] maxShadowCrossfadeCascade (3 bits, 0-4)
    uint flags0;

    // flags1 bit layout (32 bits):
    //   [0:2]   shadowCrossfadeCascades   (3 bits, 0-6)
    //   [3:6]   shadowLodStride - 1       (4 bits, 1-16 -> 0-15)
    //   [7:31]  reserved
    uint flags1;
};

// SignatureInfo field accessors - unpack from compressed storage

// UNORM16 unpack
float UnpackUNORM16Lo(uint packedValue) { return (float)(packedValue & 0xFFFF) / 65535.0; }
float UnpackUNORM16Hi(uint packedValue) { return (float)(packedValue >> 16) / 65535.0; }

// half-float unpack
float UnpackHalfLo(uint packedValue) { return f16tof32(packedValue); }
float UnpackHalfHi(uint packedValue) { return f16tof32(packedValue >> 16); }

float SigBoundsRadius(SignatureInfo s) { return UnpackHalfLo(s.boundsRadiusLodSize); }
float SigLodReferenceSize(SignatureInfo s) { return UnpackHalfHi(s.boundsRadiusLodSize); }
float SigDistanceDensityMinFraction(SignatureInfo s) { return UnpackUNORM16Lo(s.densMinFrac_fadeDur); }
float SigFadeDuration(SignatureInfo s) { return UnpackUNORM16Hi(s.densMinFrac_fadeDur) * 10.0; }
float SigDistanceDensityParam1(SignatureInfo s) { return UnpackHalfLo(s.densParam1_densParam2); }
float SigDistanceDensityParam2(SignatureInfo s) { return UnpackHalfHi(s.densParam1_densParam2); }
float SigDistanceDensityFadeWidth(SignatureInfo s) { return UnpackHalfLo(s.fadeWidth_pad); }
float SigGroupDensity(SignatureInfo s)     { return UnpackHalfHi(s.fadeWidth_pad); }

float4 SigLodScreenSizes0(SignatureInfo s)
{
    return float4(UnpackHalfLo(s.lodScreenSizes01), UnpackHalfHi(s.lodScreenSizes01),
                  UnpackHalfLo(s.lodScreenSizes23), UnpackHalfHi(s.lodScreenSizes23));
}
float4 SigLodScreenSizes1(SignatureInfo s)
{
    return float4(UnpackHalfLo(s.lodScreenSizes45), UnpackHalfHi(s.lodScreenSizes45),
                  UnpackHalfLo(s.lodScreenSizes67), UnpackHalfHi(s.lodScreenSizes67));
}
float4 SigLodFadeWidths0(SignatureInfo s)
{
    return float4(UnpackHalfLo(s.lodFadeWidths01), UnpackHalfHi(s.lodFadeWidths01),
                  UnpackHalfLo(s.lodFadeWidths23), UnpackHalfHi(s.lodFadeWidths23));
}
float4 SigLodFadeWidths1(SignatureInfo s)
{
    return float4(UnpackHalfLo(s.lodFadeWidths45), UnpackHalfHi(s.lodFadeWidths45),
                  UnpackHalfLo(s.lodFadeWidths67), UnpackHalfHi(s.lodFadeWidths67));
}

// packed flag accessors
uint  SigCameraBucketOffset(SignatureInfo s)      { return s.cameraBucketOffset; }
uint  SigShadowBucketOffset(SignatureInfo s)      { return s.shadowBucketOffset; }
uint  SigFlipBucketOffset(SignatureInfo s)        { return s.flipBucketOffset; }
uint  SigShadowFlipBucketOffset(SignatureInfo s)  { return s.shadowFlipBucketOffset; }
int   SigLodCount(SignatureInfo s)                { return (int)(s.flags0 & 0x7) + 1; }
int   SigCrossfadeEnabled(SignatureInfo s)        { return (int)((s.flags0 >> 3) & 0x1); }
int   SigUseAnimatedCrossfade(SignatureInfo s)    { return (int)((s.flags0 >> 4) & 0x1); }
int   SigDistanceDensityMode(SignatureInfo s)     { return (int)((s.flags0 >> 5) & 0x3); }
uint  SigLodShadowMask(SignatureInfo s)           { return (s.flags0 >> 8) & 0xFF; }
uint  SigPercentageFadeMask(SignatureInfo s)      { return (s.flags0 >> 16) & 0xFF; }
int   SigMinShadowLOD(SignatureInfo s)            { return (int)((s.flags0 >> 24) & 0x7); }
int   SigMaxShadowCascade(SignatureInfo s)        { return (int)((s.flags0 >> 27) & 0x3) + 1; }
int   SigMaxShadowCrossfadeCascade(SignatureInfo s) { return (int)((s.flags0 >> 29) & 0x7); }
int   SigShadowCrossfadeCascades(SignatureInfo s) { return (int)(s.flags1 & 0x7); }
uint  SigShadowLodStride(SignatureInfo s)         { return ((s.flags1 >> 3) & 0xF) + 1; }

struct DrawBucket
{
    uint bufferOffset;
    uint instanceCount;
    uint maxSlots;      // overflow guard: max instances this bucket can hold
};

// fade state for animated crossfade (8 bytes packed)
// lodPacked: low 16 bits = currentLOD + 2 (unsigned), high 16 bits = targetLOD + 2 (unsigned)
// offset by +2 so that -2..-1 map to 0..1, avoiding sign issues in bit packing
struct FadeState
{
    int lodPacked;       // packed currentLOD | targetLOD
    float fadeProgress;  // 0.0 -> 1.0
};

FadeState PackFadeState(int currentLOD, int targetLOD, float fadeProgress)
{
    FadeState s;
    s.lodPacked = ((currentLOD + 2) & 0xFFFF) | (((targetLOD + 2) & 0xFFFF) << 16);
    s.fadeProgress = fadeProgress;
    return s;
}

int GetCurrentLOD(FadeState s) { return (s.lodPacked & 0xFFFF) - 2; }
int GetTargetLOD(FadeState s)  { return (s.lodPacked >> 16) - 2; }



// shared camera uniforms
float3 _CameraPosition;
float _CameraHalfFOV;  // tan(verticalFOV * 0.5)
float _LODBias;
int _IsOrthographic;
float _OrthoSize;
float _ScreenRatioScale; // precomputed: perspective = _LODBias / (2 * _CameraHalfFOV), ortho = _LODBias / (2 * _OrthoSize)


// helper functions - core math

float3 QuatMulVec(float4 q, float3 v)
{
    float3 t = 2.0 * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}

// helper functions - instance data

// pre-computed world-space data for an instance
struct InstanceWorldData
{
    float3 worldCenter;
    float worldRadius;
    float worldSize;
    float distSq;
};

// calculate world-space bounds and distance from precomputed cull data (post-matrix)
InstanceWorldData CalcInstanceWorldData(InstanceInput inst, SignatureInfo sig)
{
    InstanceWorldData data;

    float maxScale = InstMaxScale(inst);
    data.worldRadius = SigBoundsRadius(sig) * maxScale;
    data.worldSize = SigLodReferenceSize(sig) * maxScale;
    data.worldCenter = inst.positionOrBoundsCenter; // already world-space bounds center

    float3 toCamera = data.worldCenter - _CameraPosition;
    data.distSq = dot(toCamera, toCamera);

    return data;
}

// calculate screen ratio with LOD bias applied
float CalcScreenRatio(float worldSize, float distSq)
{
    if (_IsOrthographic)
        return (_ScreenRatioScale > 0.0) ? worldSize * _ScreenRatioScale : 1e6;
    if (distSq <= 0.0) return 1e6;  // at zero distance, guarantee LOD 0
    return worldSize * rsqrt(distSq) * _ScreenRatioScale;
}

// dynamic density helpers

// for TRSPatchInstances - recomputes densityHash after position change
// must stay in sync with GPUBatcherBurstHelpers.StableDensityHash()
float StableDensityHash(float3 position)
{
    int3 ip = (int3)(position * 1024.0);
    uint h = (uint)ip.x * 0x45d9f3bu ^ (uint)ip.y * 0x8e3779b9u ^ (uint)ip.z * 0x9e3769b1u;
    h ^= h >> 16;
    h *= 0x45d9f3bu;
    h ^= h >> 16;
    return (float)(h & 0xFFFFu) / 65535.0;
}

// compute density threshold for a given screen ratio
// mode 1 (screen size): full density above param1, ramps to minFraction at param2 (cull threshold)
//   param2 = last LOD screen size (where the prototype gets culled by LOD/minScreenSize)
// mode 2 (distance):   full density below param1 (startDist), ramps to minFraction over range
//   param2 = 1/(endDist - startDist), pre-computed on CPU
float ComputeDistanceDensity(int mode, float screenRatio, float distSq,
                               float param1, float param2, float minFraction)
{
    float t;
    if (mode == 1) // screen size
    {
        if (screenRatio >= param1) return 1.0;
        // param2 is the LOD/screen cull threshold; ramp ends there so density transition fits the visible range
        float range = max(param1 - param2, 1e-5);
        t = saturate((param1 - screenRatio) / range);
    }
    else // distance
    {
        // early-out with squared comparison
        if (distSq <= param1 * param1) return 1.0;
        float dist = sqrt(distSq);
        // param2 = 1/(endDist - startDist)
        t = saturate((dist - param1) * param2);
    }
    return lerp(1.0, minFraction, t);
}

// helper functions - LOD selection

// simple LOD selection - used by fade state tracking and shadow culling
int SelectLOD(float screenRatio, float4 lodScreenSizes0, float4 lodScreenSizes1, int lodCount)
{
    lodCount = clamp(lodCount, 1, MAX_LOD_LEVELS);

    float thresholds[8];
    thresholds[0] = lodScreenSizes0.x;
    thresholds[1] = lodScreenSizes0.y;
    thresholds[2] = lodScreenSizes0.z;
    thresholds[3] = lodScreenSizes0.w;
    thresholds[4] = lodScreenSizes1.x;
    thresholds[5] = lodScreenSizes1.y;
    thresholds[6] = lodScreenSizes1.z;
    thresholds[7] = lodScreenSizes1.w;

    int lastLod = max(0, lodCount - 1);
    float cullThreshold = (lastLod < 8) ? thresholds[lastLod] : 0.0;

    if (screenRatio < cullThreshold)
        return -1;

    for (int lod = 0; lod < lodCount - 1 && lod < 8; lod++)
    {
        if (screenRatio >= thresholds[lod])
            return lod;
    }

    return lastLod;
}

#endif // GPUBatcher_COMMON_INCLUDED
