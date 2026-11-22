#ifndef GPUBatcher_InstanceColor_INCLUDED
#define GPUBatcher_InstanceColor_INCLUDED

// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

float _ColorRegionByteOffset;

void GPUBatcher_UnpackColor_float(out float4 Color)
{
    Color = float4(1, 1, 1, 1);

#ifdef UNITY_DOTS_INSTANCING_ENABLED
#ifdef DOTS_INSTANCING_ON
    if (_ColorRegionByteOffset <= 0)    //this means that instance color is not enabled in config.
        return;

    uint idx = GetDOTSInstanceIndex();
    uint byteOffset = (uint)_ColorRegionByteOffset + idx * 4;

    uint packedValue = DOTSInstanceData_Load(byteOffset);
    if (packedValue == 0u) return;

    //color is stored as packed uint, 255 values per channel.
    float4 inv = float4(
        (packedValue      ) & 0xFF,
        (packedValue >>  8) & 0xFF,
        (packedValue >> 16) & 0xFF,
        (packedValue >> 24) & 0xFF
    ) / 255.0;
    Color = 1.0 - inv;

#endif
#endif
}

void GPUBatcher_UnpackColor_half(out float4 Color)
{
    GPUBatcher_UnpackColor_float(Color);
}

void GPUBatcher_GetInstanceID_float(out float id)
{
    id = 0;
#ifdef UNITY_DOTS_INSTANCING_ENABLED
#ifdef DOTS_INSTANCING_ON
    id = GetDOTSInstanceIndex();
#endif
#endif
}

void GPUBatcher_GetInstanceID_half(out float id)
{
    GPUBatcher_GetInstanceID_float(id);
}

#endif // GPUBatcher_InstanceColor_INCLUDED
