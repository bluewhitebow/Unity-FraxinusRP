#ifndef APRTV_UPLOAD_DATA_COMMON_INCLUDED
#define APRTV_UPLOAD_DATA_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

// Scratch buffer for data upload (set by compute shader)
ByteAddressBuffer _ScratchBuffer;

// Constants from C# (APRTVReferenceVolume.Streaming.cs)
// These are set by the upload compute shader kernel
uint _ProbeCountInChunkLine;
uint _ProbeCountInChunkSlice;

/// <summary>
/// Converts a packed uint (RGBA8) to float4 [0, 1]
/// </summary>
float4 UintToFloat4(uint input)
{
    float4 result;
    result.x = (input & 0x000000FF) / 255.0f;
    result.y = ((input >> 8) & 0x000000FF) / 255.0f;
    result.z = ((input >> 16) & 0x000000FF) / 255.0f;
    result.w = ((input >> 24) & 0x000000FF) / 255.0f;
    return result;
}

/// <summary>
/// Extracts two FP16 RGBA values encoded in a uint4
/// Used for high-precision SH data (L2 bands)
/// </summary>
void ExtractFP16(uint4 input, out float4 value0, out float4 value1)
{
    // Convert FP16 to FP32
    float4 temp0 = f16tof32(input);
    float4 temp1 = f16tof32(input >> 16);

    // Interleave channels
    value0.xz = temp0.xy;
    value0.yw = temp1.xy;
    value1.xz = temp0.zw;
    value1.yw = temp1.zw;
}

/// <summary>
/// Extracts four byte-packed float4 values from uint4
/// Used for validity data or low-precision data
/// </summary>
void ExtractByte(uint4 input, out float4 value0, out float4 value1, out float4 value2, out float4 value3)
{
    value0 = UintToFloat4(input.x);
    value1 = UintToFloat4(input.y);
    value2 = UintToFloat4(input.z);
    value3 = UintToFloat4(input.w);
}

/// <summary>
/// Extracts a single byte-packed float4 value from uint
/// </summary>
void ExtractByte(uint input, out float4 value)
{
    value = UintToFloat4(input);
}

/// <summary>
/// Calculates probe location and offsets for data upload
/// Used by compute shader to write probe data to 3D textures
/// </summary>
/// <param name="chunkIndex">Index of the brick chunk in scratch buffer</param>
/// <param name="chunkProbeIndex">Probe index within the chunk (0-63 for 4x4x4)</param>
/// <param name="baseProbe">Output: Base probe position in chunk (xyz)</param>
/// <param name="loc">Output: Global location in texture pool</param>
/// <param name="probe1Offset">Output: Offset to probe 1 (for packed writes)</param>
/// <param name="probe2Offset">Output: Offset to probe 2 (for packed writes)</param>
/// <param name="probe3Offset">Output: Offset to probe 3 (for packed writes)</param>
void GetProbeLocationAndOffsets(
    uint chunkIndex, 
    uint chunkProbeIndex, 
    out float3 baseProbe, 
    out float3 loc, 
    out float3 probe1Offset, 
    out float3 probe2Offset, 
    out float3 probe3Offset)
{
    // Decompose linear probe index to 3D coordinates
    // Assumes 4x4x4 brick layout (64 probes per brick)
    baseProbe.z = chunkProbeIndex / _ProbeCountInChunkSlice; // Z layer (0-3)
    uint indexInSlice = chunkProbeIndex - baseProbe.z * _ProbeCountInChunkSlice;
    baseProbe.y = indexInSlice / _ProbeCountInChunkLine; // Y row (0-3)
    baseProbe.x = indexInSlice - baseProbe.y * _ProbeCountInChunkLine; // X column (0-3)

    // Load destination chunk position from scratch buffer
    // Each chunk entry is 4 uints (16 bytes): [dstX, dstY, dstZ, reserved]
    uint3 dstChunk = _ScratchBuffer.Load3(chunkIndex * 16);
    
    // Calculate final location in texture pool
    loc = dstChunk + baseProbe;

    // Offsets for packed probe writes (4 probes per write in some data types)
    probe1Offset = uint3(1, 0, 0);
    probe2Offset = uint3(2, 0, 0);
    probe3Offset = uint3(3, 0, 0);
}

/// <summary>
/// Helper to load SH data from scratch buffer for a specific probe
/// </summary>
/// <param name="probeIndex">Global probe index in scratch buffer</param>
/// <param name="dataOffset">Byte offset to the start of SH data</param>
/// <returns>Loaded uint4 data (format depends on data type)</returns>
uint4 LoadProbeData(uint probeIndex, uint dataOffset)
{
    uint byteOffset = dataOffset + probeIndex * 16; // Assume 16 bytes per probe (4 uint)
    return _ScratchBuffer.Load4(byteOffset);
}

#endif // APRTV_UPLOAD_DATA_COMMON_INCLUDED
