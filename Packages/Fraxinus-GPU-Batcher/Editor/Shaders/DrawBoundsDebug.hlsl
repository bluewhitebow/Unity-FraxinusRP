#ifndef GPUBatcher_DEBUG_SPHERE_INCLUDED
#define GPUBatcher_DEBUG_SPHERE_INCLUDED

struct GPUBatcherInstanceInput { float3 positionOrBoundsCenter; float padOrMaxScale; uint sigDensityPacked; };
struct GPUBatcherSignatureInfo {
    float3 boundsCenter; float shadowDistanceSq;
    uint lodScreenSizes01, lodScreenSizes23, lodScreenSizes45, lodScreenSizes67;
    uint lodFadeWidths01, lodFadeWidths23, lodFadeWidths45, lodFadeWidths67;
    uint densMinFrac_fadeDur;
    uint boundsRadiusLodSize, densParam1_densParam2, fadeWidth_pad;
    uint cameraBucketOffset, shadowBucketOffset, flipBucketOffset, shadowFlipBucketOffset;
    uint flags0, flags1;
};

StructuredBuffer<uint> _DebugSphereList;
StructuredBuffer<GPUBatcherInstanceInput> _Instances;
StructuredBuffer<GPUBatcherSignatureInfo> _Signatures;

void GPUBatcherDebugSphere_float(float3 PositionOS, float instID, out float3 PositionWS, out float LOD)
{

    uint entry      = _DebugSphereList[instID];
    LOD             = (float)(entry >> 24);
    uint globalIdx  = entry & 0x00FFFFFF;

    GPUBatcherInstanceInput inst = _Instances[globalIdx];
    uint  sigIdx   = inst.sigDensityPacked & 0xFFFF;
    float radius   = f16tof32(_Signatures[sigIdx].boundsRadiusLodSize & 0xFFFF) * abs(inst.padOrMaxScale);

    // RenderMeshIndirect draws with identity object->world, so world coords can go
    // straight into the object-space Position port
    PositionWS = inst.positionOrBoundsCenter + normalize(PositionOS) * radius;
}

void GetLODColor_float(float LOD, out float3 Color)
{
    const float3 colors[8] =
    {
        float3(0.0, 1.0, 0.0),  // 0 green
        float3(0.0, 1.0, 1.0),  // 1 cyan
        float3(0.0, 0.0, 1.0),  // 2 blue
        float3(0.5, 0.0, 1.0),  // 3 purple
        float3(1.0, 1.0, 0.0),  // 4 yellow
        float3(1.0, 0.5, 0.0),  // 5 orange
        float3(1.0, 0.0, 0.0),  // 6 red
        float3(0.0, 0.0, 0.0),  // 7 black
    };
    Color = colors[(uint)clamp(LOD, 0.0, 7.0)];
}
#endif
