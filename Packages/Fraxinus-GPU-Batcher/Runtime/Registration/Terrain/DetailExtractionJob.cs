// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    // heightmap sampling context
    internal readonly struct HeightmapContext
    {
        public readonly NativeArray<ushort> heightmap;
        public readonly int resolution, regionX, regionZ, regionW, regionH;
        public readonly float terrainSizeY;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public HeightmapContext(NativeArray<ushort> heightmap, int resolution,
            int regionX, int regionZ, int regionW, int regionH, float terrainSizeY)
        {
            this.heightmap = heightmap;
            this.resolution = resolution;
            this.regionX = regionX;
            this.regionZ = regionZ;
            this.regionW = regionW;
            this.regionH = regionH;
            this.terrainSizeY = terrainSizeY;
        }
    }

    // per-patch extraction constants
    internal readonly struct CellContext
    {
        public readonly HeightmapContext hm;
        public readonly float3 terrainPosition;
        public readonly float invTerrainSizeX;
        public readonly float invTerrainSizeZ;
        public readonly float cellSizeX;
        public readonly float cellSizeZ;
        public readonly float normalScaleX;
        public readonly float normalScaleZ;

        public readonly int detailResolution;
        public readonly int detailResPerPatch;

        // holes
        public readonly NativeBitArray holes;
        public readonly int holeRegionX, holeRegionZ;
        public readonly int holeRegionW, holeRegionH;
        public readonly int holesResolution;
        public readonly bool hasHoles;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CellContext(
            in HeightmapContext hm,
            float3 terrainPosition, float3 terrainSize,
            float cellSizeX, float cellSizeZ,
            int detailResolution, int detailResPerPatch,
            NativeBitArray holes,
            int holeRegionX, int holeRegionZ,
            int holeRegionW, int holeRegionH,
            int holesResolution, bool hasHoles)
        {
            this.hm = hm;
            this.terrainPosition = terrainPosition;
            this.invTerrainSizeX = 1f / terrainSize.x;
            this.invTerrainSizeZ = 1f / terrainSize.z;
            this.cellSizeX = cellSizeX;
            this.cellSizeZ = cellSizeZ;
            this.normalScaleX = terrainSize.y * (hm.resolution - 1) / (2f * terrainSize.x);
            this.normalScaleZ = terrainSize.y * (hm.resolution - 1) / (2f * terrainSize.z);
            this.detailResolution = detailResolution;
            this.detailResPerPatch = detailResPerPatch;
            this.holes = holes;
            this.holeRegionX = holeRegionX;
            this.holeRegionZ = holeRegionZ;
            this.holeRegionW = holeRegionW;
            this.holeRegionH = holeRegionH;
            this.holesResolution = holesResolution;
            this.hasHoles = hasHoles;
        }
    }

    // shared helpers for detail extraction
    [BurstCompile]
    public static class DetailExtractionHelpers
    {
        // returns world-space height relative to terrain origin at normalized coords [0,1]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float SampleHeight(in HeightmapContext ctx, float normX, float normZ)
            => SampleHeightNormalized(in ctx, normX, normZ) * ctx.terrainSizeY;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float3 ComputeNormal(in HeightmapContext ctx, float normX, float normZ,
            float normalScaleX, float normalScaleZ)
        {
            float eps = 1f / (ctx.resolution - 1);

            float hL = SampleHeightNormalized(in ctx, normX - eps, normZ);
            float hR = SampleHeightNormalized(in ctx, normX + eps, normZ);
            float hD = SampleHeightNormalized(in ctx, normX, normZ - eps);
            float hU = SampleHeightNormalized(in ctx, normX, normZ + eps);

            float3 normal = math.normalize(new float3(
                -(hR - hL) * normalScaleX,
                1f,
                -(hU - hD) * normalScaleZ
            ));

            return normal;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float SampleHeightNormalized(in HeightmapContext ctx, float normX, float normZ)
        {
            normX = math.clamp(normX, 0f, 1f);
            normZ = math.clamp(normZ, 0f, 1f);

            float hx = normX * (ctx.resolution - 1) - ctx.regionX;
            float hz = normZ * (ctx.resolution - 1) - ctx.regionZ;

            int ix = (int)hx;
            int iz = (int)hz;
            float fx = hx - ix;
            float fz = hz - iz;

            ix = math.clamp(ix, 0, ctx.regionW - 1);
            iz = math.clamp(iz, 0, ctx.regionH - 1);
            int ix1 = math.min(ix + 1, ctx.regionW - 1);
            int iz1 = math.min(iz + 1, ctx.regionH - 1);

            const float k_inv65535 = 1f / 65535f;
            float h00 = ctx.heightmap[iz  * ctx.regionW + ix]  * k_inv65535;
            float h10 = ctx.heightmap[iz  * ctx.regionW + ix1] * k_inv65535;
            float h01 = ctx.heightmap[iz1 * ctx.regionW + ix]  * k_inv65535;
            float h11 = ctx.heightmap[iz1 * ctx.regionW + ix1] * k_inv65535;

            return (h00 * (1 - fx) + h10 * fx) * (1 - fz)
                 + (h01 * (1 - fx) + h11 * fx) * fz;
        }

        // burst-compatible equivalent of Quaternion.FromToRotation
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static quaternion FromToRotation(float3 from, float3 to)
        {
            float d = math.dot(from, to);
            if (d >= 0.9999f) return quaternion.identity;
            if (d <= -0.9999f)
            {
                float3 axis = math.cross(new float3(1, 0, 0), from);
                if (math.lengthsq(axis) < 0.0001f)
                    axis = math.cross(new float3(0, 1, 0), from);
                return quaternion.AxisAngle(math.normalize(axis), math.PI);
            }

            float3 c = math.cross(from, to);
            float w = 1f + d;
            return math.normalize(new quaternion(c.x, c.y, c.z, w));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            x ^= x >> 16;
            return x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float Hash01(uint seed)
        {
            return (Hash(seed) & 0x00ffffff) / 16777216f;
        }

        // floor(expected) with stochastic round-up by per-cell hash
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int StochasticRoundUp(float expected, int globalX, int globalZ, int noiseSeed)
        {
            int count = (int)expected;
            float fractional = expected - count;
            uint cellHash = (uint)(globalX * 73856093 ^ globalZ * 19349663 ^ noiseSeed);
            if (Hash01(cellHash) < fractional) count++;
            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static quaternion ComputeAlignedRotation(float yaw, float3 terrainNormal, float alignToGround)
        {
            quaternion yawQuat = quaternion.RotateY(yaw);
            if (alignToGround <= 0f)
                return yawQuat;

            // tilt to terrain normal, then yaw around the normal axis
            quaternion tilt = FromToRotation(math.up(), terrainNormal);
            quaternion alignedQuat = math.mul(
                quaternion.AxisAngle(terrainNormal, yaw),
                tilt);
            return math.slerp(yawQuat, alignedQuat, alignToGround);
        }

        // Roberts R2 quasi-random sequence (plastic number)
        private const float k_r2AlphaX = 0.7548777f;   // 1/phi_2
        public static float r2AlphaX => k_r2AlphaX;
        private const float k_r2AlphaZ = 0.5698403f;   // 1/phi_2^2
        public static float r2AlphaZ => k_r2AlphaZ;
        private const float k_r2X0 = 0.00976f;          // universal phase
        public static float r2X0 => k_r2X0;
        private const float k_r2Y0 = 0.63968f;
        public static float r2Y0 => k_r2Y0;

        // base R2 cell-relative position (rx, rz in [0, 1))
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ComputeR2CellPosition(
            int globalX, int globalZ, int iLocal,
            int detailResolution, int detailResPerPatch,
            int effectiveSeed,
            out float rx, out float rz)
        {
            int rpp = detailResPerPatch;
            int patchX = globalX / rpp;
            int patchY = globalZ / rpp;
            int localCellX = globalX - patchX * rpp;
            int localCellZ = globalZ - patchY * rpp;
            int patchCount = detailResolution / rpp;
            int strideZ = rpp + 255;
            int linearPatch = patchX * patchCount + patchY;
            int patchOffset = (linearPatch * 1013) % 4095;
            int seedContribution = (int)((uint)effectiveSeed % 4095u);
            int N = localCellX + strideZ * localCellZ + iLocal + seedContribution + patchOffset;

            // -4095 wrap when patch-local cell position + offsets >= 4095
            int positionInPatch = localCellZ * rpp + localCellX;
            if (positionInPatch + patchOffset + seedContribution >= 4095)
                N -= 4095;

            rx = math.frac(k_r2X0 + N * k_r2AlphaX);
            rz = math.frac(k_r2Y0 + N * k_r2AlphaZ);
        }

        // shared per-instance body; returns false if skipped
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool EmitInstance(
            in CellContext ctx,
            int globalX, int globalZ,
            int iLocal,
            int instancesPerCell,
            in DetailLayerConfig config,
            ref NativeList<InstanceData> instances,
            ref float3 boundsMin, ref float3 boundsMax, ref bool hasBounds)
        {
            ComputeR2CellPosition(globalX, globalZ, iLocal,
                ctx.detailResolution, ctx.detailResPerPatch,
                config.effectiveSeed,
                out float rx, out float rz);

            // distinct from R2 position N, which can collide across (cell, iLocal)
            uint instanceHash = (uint)(globalX * 73856093 ^ globalZ * 19349663 ^ config.noiseSeed)
                              + (uint)(iLocal + 1) * 83492791u;

            if (config.positionJitter > 0f && instancesPerCell > 0)
            {
                float maxOffset = 0.5f / math.sqrt(instancesPerCell);
                float dRX = (Hash01(instanceHash + 11u) - 0.5f) * 2f * maxOffset * config.positionJitter;
                float dRZ = (Hash01(instanceHash + 13u) - 0.5f) * 2f * maxOffset * config.positionJitter;
                // wrap to remain in cell
                rx = math.frac(rx + dRX);
                rz = math.frac(rz + dRZ);
            }

            float worldX = ctx.terrainPosition.x + (globalX + rx) * ctx.cellSizeX;
            float worldZ = ctx.terrainPosition.z + (globalZ + rz) * ctx.cellSizeZ;

            // normalized coordinates for holes, height, and normals
            float normX = (worldX - ctx.terrainPosition.x) * ctx.invTerrainSizeX;
            float normZ = (worldZ - ctx.terrainPosition.z) * ctx.invTerrainSizeZ;

            if (ctx.hasHoles)
            {
                int hx = math.clamp((int)(normX * ctx.holesResolution) - ctx.holeRegionX, 0, ctx.holeRegionW - 1);
                int hz = math.clamp((int)(normZ * ctx.holesResolution) - ctx.holeRegionZ, 0, ctx.holeRegionH - 1);
                int holeIdx = hz * ctx.holeRegionW + hx;
                if (!ctx.holes.IsSet(holeIdx))
                    return false;
            }

            float worldY = SampleHeight(in ctx.hm, normX, normZ) + ctx.terrainPosition.y;

            float yaw = Hash01(instanceHash + 3u) * 6.283185307f;

            // scale from Perlin noise
            float seedOffsetX = (config.noiseSeed & 0xFF) * 0.3876f;
            float seedOffsetZ = ((config.noiseSeed >> 8) & 0xFF) * 0.4127f;
            float2 noiseCoord = new float2(
                worldX * config.noiseSpread + seedOffsetX,
                worldZ * config.noiseSpread + seedOffsetZ);
            float noiseVal = math.saturate(noise.cnoise(noiseCoord) * 0.5f + 0.5f);
            float scaleXZ = math.lerp(config.minWidth, config.maxWidth, noiseVal);
            float scaleY = math.lerp(config.minHeight, config.maxHeight, noiseVal);

            float3 worldPos = new float3(worldX, worldY, worldZ);

            // rotation with optional terrain alignment
            float3 terrainNormal = default;
            if (config.alignToGround > 0f)
                terrainNormal = ComputeNormal(in ctx.hm, normX, normZ, ctx.normalScaleX, ctx.normalScaleZ);
            quaternion rotation = ComputeAlignedRotation(yaw, terrainNormal, config.alignToGround);

            instances.Add(new InstanceData
            {
                position = worldPos,
                rotation = rotation,
                scale = new float3(scaleXZ, scaleY, scaleXZ),
                signatureIndex = config.globalProtoIndex,
            });

            if (!hasBounds)
            {
                boundsMin = worldPos;
                boundsMax = worldPos;
                hasBounds = true;
            }
            else
            {
                boundsMin = math.min(boundsMin, worldPos);
                boundsMax = math.max(boundsMax, worldPos);
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void WriteBounds(NativeArray<float> boundsMinMax, float3 boundsMin, float3 boundsMax, bool hasBounds)
        {
            if (hasBounds)
            {
                boundsMinMax[0] = boundsMin.x;
                boundsMinMax[1] = boundsMin.y;
                boundsMinMax[2] = boundsMin.z;
                boundsMinMax[3] = boundsMax.x;
                boundsMinMax[4] = boundsMax.y;
                boundsMinMax[5] = boundsMax.z;
            }
            else
            {
                for (int j = 0; j < 6; j++)
                    boundsMinMax[j] = 0;
            }
        }

        // builds the target chunk bit array for streaming: sets bit for each chunk within load distance of any camera
        [BurstCompile]
        internal static void BuildTargetChunkBits(
            ref NativeBitArray targetBits,
            in NativeArray<float3> cameraPositions,
            int camCount,
            int chunksPerAxis,
            float detailDistance,
            float terrainPosX, float terrainPosZ,
            float detailChunkSize, float detailChunkSizeZ,
            float patchWorldSize, float patchWorldSizeZ,
            int patchesPerDetailChunk, int patchCount)
        {
            int totalChunks = chunksPerAxis * chunksPerAxis;
            targetBits.SetBits(0, false, totalChunks);

            float loadDistanceSq = detailDistance * detailDistance;

            for (int c = 0; c < camCount; c++)
            {
                float3 camPos = cameraPositions[c];
                int minCX = math.max(0, (int)math.floor((camPos.x - detailDistance - terrainPosX) / detailChunkSize));
                int maxCX = math.min(chunksPerAxis - 1, (int)math.ceil((camPos.x + detailDistance - terrainPosX) / detailChunkSize));
                int minCY = math.max(0, (int)math.floor((camPos.z - detailDistance - terrainPosZ) / detailChunkSizeZ));
                int maxCY = math.min(chunksPerAxis - 1, (int)math.ceil((camPos.z + detailDistance - terrainPosZ) / detailChunkSizeZ));

                for (int cy = minCY; cy <= maxCY; cy++)
                {
                    int rowBase = cy * chunksPerAxis;
                    for (int cx = minCX; cx <= maxCX; cx++)
                    {
                        if (targetBits.IsSet(rowBase + cx)) continue;

                        // inline ChunkEdgeDistanceSq
                        int startX = cx * patchesPerDetailChunk;
                        int startZ = cy * patchesPerDetailChunk;
                        int endX = math.min(startX + patchesPerDetailChunk, patchCount);
                        int endZ = math.min(startZ + patchesPerDetailChunk, patchCount);
                        float minX = terrainPosX + startX * patchWorldSize;
                        float minZ = terrainPosZ + startZ * patchWorldSizeZ;
                        float maxX = terrainPosX + endX * patchWorldSize;
                        float maxZ = terrainPosZ + endZ * patchWorldSizeZ;
                        float dx = math.clamp(camPos.x, minX, maxX) - camPos.x;
                        float dz = math.clamp(camPos.z, minZ, maxZ) - camPos.z;

                        if (dx * dx + dz * dz <= loadDistanceSq)
                            targetBits.Set(rowBase + cx, true);
                    }
                }
            }
        }
    }

    // per-layer config for the extraction job, packed into a NativeArray
    internal struct DetailLayerConfig
    {
        public int globalProtoIndex;
        public int terrainLayerIndex;
        public float effectiveDensity; // density scale factor applied during stochastic thinning
        public float meshArea;         // physical XZ footprint area
        public float minWidth, maxWidth;
        public float minHeight, maxHeight;
        public float alignToGround;
        public float noiseSpread;
        public int noiseSeed;
        public int effectiveSeed; // noiseSeed if non-zero, else terrainLayerIndex*1024
        public float positionJitter;
    }

    // instanceCount mode: rawCount = literal instance count
    [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
    internal struct DetailExtractionJobInstanceCount : IJob
    {
        // terrain heightmap
        [ReadOnly] public NativeArray<ushort> heightmap;
        public int heightmapRegionX, heightmapRegionZ;
        public int heightmapRegionW, heightmapRegionH;
        public int heightmapResolution;

        // detail layers (all terrain layers, full resolution)
        [ReadOnly] public NativeArray<byte> detailLayers;
        public int layerCount;

        // holes
        [ReadOnly] public NativeBitArray holes;
        public int holeRegionX, holeRegionZ;
        public int holeRegionW, holeRegionH;
        public int holesResolution;
        public bool hasHoles;

        // per-layer prototype configuration
        [ReadOnly] public NativeArray<DetailLayerConfig> layerConfigs;

        // terrain parameters
        public float3 terrainPosition;
        public float3 terrainSize;
        public int detailResolution;
        public int detailResPerPatch;
        public int patchStartX, patchStartZ;
        public float cellSizeX, cellSizeZ;

        // output
        public NativeList<InstanceData> instances;

        // bounds output: [0]=minX, [1]=minY, [2]=minZ, [3]=maxX, [4]=maxY, [5]=maxZ
        public NativeArray<float> boundsMinMax;

        public void Execute()
        {
            var hm = new HeightmapContext(heightmap, heightmapResolution,
                heightmapRegionX, heightmapRegionZ, heightmapRegionW, heightmapRegionH, terrainSize.y);
            var ctx = new CellContext(in hm, terrainPosition, terrainSize,
                cellSizeX, cellSizeZ, detailResolution, detailResPerPatch, holes,
                holeRegionX, holeRegionZ, holeRegionW, holeRegionH,
                holesResolution, hasHoles);

            int fullLayerStride = detailResolution * detailResolution;
            float3 boundsMin = new float3(float.MaxValue);
            float3 boundsMax = new float3(float.MinValue);
            bool hasBounds = false;

            for (int layerIdx = 0; layerIdx < layerCount; layerIdx++)
            {
                var config = layerConfigs[layerIdx];
                if (config.globalProtoIndex < 0) continue;
                if (config.effectiveDensity <= 0f) continue;

                int layerBaseOffset = config.terrainLayerIndex * fullLayerStride;

                for (int localZ = 0; localZ < detailResPerPatch; localZ++)
                {
                    int globalZ = patchStartZ + localZ;
                    for (int localX = 0; localX < detailResPerPatch; localX++)
                    {
                        int globalX = patchStartX + localX;
                        if (globalX >= detailResolution || globalZ >= detailResolution)
                            continue;

                        int rawCount = detailLayers[layerBaseOffset + globalZ * detailResolution + globalX];
                        if (rawCount == 0) continue;

                        float expectedInstances = rawCount * config.effectiveDensity;
                        int scaledCount = DetailExtractionHelpers.StochasticRoundUp(
                            expectedInstances, globalX, globalZ, config.noiseSeed);

                        for (int i = 0; i < scaledCount; i++)
                        {
                            DetailExtractionHelpers.EmitInstance(in ctx, globalX, globalZ,
                                i, scaledCount,
                                in config, ref instances, ref boundsMin, ref boundsMax, ref hasBounds);
                        }
                    }
                }
            }

            DetailExtractionHelpers.WriteBounds(boundsMinMax, boundsMin, boundsMax, hasBounds);
        }
    }

    // coverage mode: rawCount represents coverage, converted to instances
    [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
    internal struct DetailExtractionJobCoverage : IJob
    {
        // terrain heightmap
        [ReadOnly] public NativeArray<ushort> heightmap;
        public int heightmapRegionX, heightmapRegionZ;
        public int heightmapRegionW, heightmapRegionH;
        public int heightmapResolution;

        // detail layers (all terrain layers, full resolution)
        [ReadOnly] public NativeArray<byte> detailLayers;
        public int layerCount;

        // holes
        [ReadOnly] public NativeBitArray holes;
        public int holeRegionX, holeRegionZ;
        public int holeRegionW, holeRegionH;
        public int holesResolution;
        public bool hasHoles;

        // per-layer prototype configuration
        [ReadOnly] public NativeArray<DetailLayerConfig> layerConfigs;

        // terrain parameters
        public float3 terrainPosition;
        public float3 terrainSize;
        public int detailResolution;
        public int detailResPerPatch;
        public int patchStartX, patchStartZ;
        public float cellSizeX, cellSizeZ;

        // output
        public NativeList<InstanceData> instances;
        public NativeArray<float> boundsMinMax;

        public void Execute()
        {
            var hm = new HeightmapContext(heightmap, heightmapResolution,
                heightmapRegionX, heightmapRegionZ, heightmapRegionW, heightmapRegionH, terrainSize.y);
            var ctx = new CellContext(in hm, terrainPosition, terrainSize,
                cellSizeX, cellSizeZ, detailResolution, detailResPerPatch, holes,
                holeRegionX, holeRegionZ, holeRegionW, holeRegionH,
                holesResolution, hasHoles);

            int fullLayerStride = detailResolution * detailResolution;
            float3 boundsMin = new float3(float.MaxValue);
            float3 boundsMax = new float3(float.MinValue);
            bool hasBounds = false;

            float cellArea = cellSizeX * cellSizeZ;

            for (int layerIdx = 0; layerIdx < layerCount; layerIdx++)
            {
                var config = layerConfigs[layerIdx];
                if (config.globalProtoIndex < 0) continue;
                if (config.effectiveDensity <= 0f) continue;

                int layerBaseOffset = config.terrainLayerIndex * fullLayerStride;

                // coverage-specific: per-layer density calculation
                float avgScale = (config.minWidth + config.maxWidth) * 0.5f;
                float avgPhysicalArea = config.meshArea * avgScale * avgScale;
                if (avgPhysicalArea <= 0f) continue;
                float densityFactor = cellArea / avgPhysicalArea;

                for (int localZ = 0; localZ < detailResPerPatch; localZ++)
                {
                    int globalZ = patchStartZ + localZ;
                    for (int localX = 0; localX < detailResPerPatch; localX++)
                    {
                        int globalX = patchStartX + localX;
                        if (globalX >= detailResolution || globalZ >= detailResolution)
                            continue;

                        int rawCount = detailLayers[layerBaseOffset + globalZ * detailResolution + globalX];
                        if (rawCount == 0) continue;

                        // painted value is a density modifier, squared for 2D
                        float coverageRatio = rawCount / 255.0f;
                        float totalDensity = config.effectiveDensity * coverageRatio;
                        float expectedInstances = densityFactor * totalDensity * totalDensity;
                        int instancesToPlace = DetailExtractionHelpers.StochasticRoundUp(
                            expectedInstances, globalX, globalZ, config.noiseSeed);

                        for (int i = 0; i < instancesToPlace; i++)
                        {
                            DetailExtractionHelpers.EmitInstance(in ctx, globalX, globalZ,
                                i, instancesToPlace,
                                in config, ref instances, ref boundsMin, ref boundsMax, ref hasBounds);
                        }
                    }
                }
            }

            DetailExtractionHelpers.WriteBounds(boundsMinMax, boundsMin, boundsMax, hasBounds);
        }
    }
}
