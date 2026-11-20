// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    // detail extraction, upload, and unloading
    public partial class TerrainGPUBatcherRegisterer
    {
        #region cached terrain data

        // cached terrain data
        private NativeArray<ushort> _cachedHeightmap;     // full heightmap, row-major [z * hmRes + x], ushort [0,65535] -> [0,1]
        private NativeArray<byte> _cachedDetailLayers;    // all layers packed: layer0[detailRes^2], layer1[...], ...
        private NativeBitArray _cachedHoles;               // bit-packed holes, row-major [z * holesRes + x], bit=1 solid, bit=0 hole
        private int _cachedHeightmapRes;
        private int _cachedHolesRes;
        private int _cachedDetailLayerCount;
        private DetailPrototype[] _cachedDetailPrototypes; // cached to avoid per-access managed array alloc
        private NativeArray<DetailLayerConfig> _cachedLayerConfigs; // shared read-only across all jobs
        private int _cachedRelevantLayerCount;
        private bool _useCoverageMode;                     // true if terrain uses coverage mode

        // reusable buffers to avoid GC allocations
        private NativeList<InstanceData> _detailExtractBuffer;
        private const int kExtractBufferInitialCapacity = 4096;

        private void CacheTerrainData()
        {
            if (_cachedDetailPrototypes == null) return; // cache not initialized yet

            // complete any in-flight jobs that reference the old cached arrays
            FlushPendingJobs();
            DisposeTerrainDataCache();

            // use cached prototypes, property access allocates
            int hmRes = _terrainData.heightmapResolution;
            int detRes = _detailResolution;
            int layerCount = _cachedDetailPrototypes.Length;

            // heightmap
            // needed by both paths: job path for height sampling, Unity path for normal computation
            _cachedHeightmapRes = hmRes;
            float[,] heights = _terrainData.GetHeights(0, 0, hmRes, hmRes);
            _cachedHeightmap = new NativeArray<ushort>(hmRes * hmRes, Allocator.Persistent);
            for (int z = 0; z < hmRes; z++)
                for (int x = 0; x < hmRes; x++)
                    _cachedHeightmap[z * hmRes + x] = (ushort)(heights[z, x] * 65535f + 0.5f);

            // cache DetailPrototype array; detailPrototypes allocates every access
            _cachedDetailPrototypes = _terrainData.detailPrototypes;
#if UNITY_EDITOR
            _rendererHashDirty = true;  // prototype list changed, renderer hashes need recompute
#endif
            _useCoverageMode = _terrainData.detailScatterMode == DetailScatterMode.CoverageMode;

            // detail layers needed by job path for extraction, and by editor for painting change detection
            bool needDetailLayers = !useUnityTransforms;
#if UNITY_EDITOR
            needDetailLayers = true; // editor always needs detail layers for HasPatchChanged()
#endif
            if (needDetailLayers)
            {
                _cachedDetailLayerCount = layerCount;
                int cellsPerLayer = detRes * detRes;
                _cachedDetailLayers = new NativeArray<byte>(layerCount * cellsPerLayer, Allocator.Persistent);
                for (int layer = 0; layer < layerCount; layer++)
                {
                    int[,] layerData = _terrainData.GetDetailLayer(0, 0, detRes, detRes, layer);
                    int baseOffset = layer * cellsPerLayer;
                    for (int z = 0; z < detRes; z++)
                        for (int x = 0; x < detRes; x++)
                            _cachedDetailLayers[baseOffset + z * detRes + x] = (byte)layerData[z, x];
                }
            }

            // holes and layer configs only needed by job path
            if (!useUnityTransforms)
            {
                int holesRes = _terrainData.holesResolution;
                _cachedHolesRes = holesRes;
                if (holesRes > 0)
                {
                    bool[,] holes = _terrainData.GetHoles(0, 0, holesRes, holesRes);
                    int totalHoles = holesRes * holesRes;
                    _cachedHoles = new NativeBitArray(totalHoles, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    for (int z = 0; z < holesRes; z++)
                        for (int x = 0; x < holesRes; x++)
                            if (holes[z, x])
                                _cachedHoles.Set(z * holesRes + x, true);
                }
                else
                {
                    _cachedHoles = new NativeBitArray(1, Allocator.Persistent);
                }
            }

#if UNITY_EDITOR
            _detailTerrainDataCached = true;
#endif

            // layer configs only needed by job path
            _cachedLayerConfigs.DisposeIfCreated();
            if (!useUnityTransforms)
            {
                float densityScale = _terrain.detailObjectDensity;
                // allocate at max possible size, will trim after populating
                var tempConfigs = new NativeArray<DetailLayerConfig>(layerCount, Allocator.Temp);

                // pre-compute cell area for Coverage mode density calculation
                float cellArea = (_terrainSize.x / _detailResolution) * (_terrainSize.z / _detailResolution);

                int cfgIdx = 0;
                for (int layer = 0; layer < layerCount; layer++)
                {
                    int globalIdx = (_detailLayerToGlobalProto != null && layer < _detailLayerToGlobalProto.Length)
                        ? _detailLayerToGlobalProto[layer] : -1;
                    if (globalIdx < 0)
                    {
#if DEBUG
                        if (enableDebugLogging)
                            Debug.LogWarning($"[Detail Config] Layer {layer} has no registered proto - SKIPPING");
#endif
                        continue;
                    }

                    var prototype = _cachedDetailPrototypes[layer];

                    // special case: detailObjectDensity = 0 always produces 0 instances even if useDensityScaling is false
                    float terrainDensity;
                    if (densityScale == 0f)
                        terrainDensity = 0f;
                    else
                        terrainDensity = prototype.useDensityScaling ? densityScale : 1.0f;

                    // per-prototype density only used in Coverage mode; InstanceCount mode ignores prototype.density
                    float prototypeDensity = _useCoverageMode ? prototype.density : 1.0f;
                    float effectiveDensity = terrainDensity * prototypeDensity;

                    // coverage mode: mesh XZ footprint area for density; must use original terrain detail mesh bounds
                    float meshArea = 1.0f;  // default for billboard grass quad
                    if (prototype.prototype != null)
                    {
                        var mf = prototype.prototype.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                        {
                            var bounds = mf.sharedMesh.bounds.size;
                            meshArea = bounds.x * bounds.z;
                        }
                    }

#if DEBUG
                    if (enableDebugLogging)
                    {
                        float avgScale = (prototype.minWidth + prototype.maxWidth) * 0.5f;
                        Debug.Log($"[Detail Config BUILD] Layer {layer} -> globalIdx={globalIdx}: " +
                                  $"proto.density={prototype.density:F3}, avgScale={avgScale:F2}, " +
                                  $"meshArea={meshArea:F3}, " +
                                  $"useDensityScaling={prototype.useDensityScaling}, terrainDensity={terrainDensity:F3}, " +
                                  $"effectiveDensity={effectiveDensity:F3}");
                    }
#endif

                    tempConfigs[cfgIdx++] = new DetailLayerConfig
                    {
                        globalProtoIndex = globalIdx,
                        terrainLayerIndex = layer,
                        effectiveDensity = effectiveDensity,  // used by both InstanceCount and Coverage modes
                        meshArea = meshArea,  // physical mesh XZ footprint area
                        minWidth = prototype.minWidth,
                        maxWidth = prototype.maxWidth,
                        minHeight = prototype.minHeight,
                        maxHeight = prototype.maxHeight,
                        alignToGround = prototype.alignToGround,
                        noiseSpread = prototype.noiseSpread,
                        noiseSeed = prototype.noiseSeed,
                        effectiveSeed = prototype.noiseSeed != 0 ? prototype.noiseSeed : layer * 1024,
                        positionJitter = prototype.positionJitter,
                    };
                }
                _cachedRelevantLayerCount = cfgIdx;
                _cachedLayerConfigs = new NativeArray<DetailLayerConfig>(cfgIdx, Allocator.Persistent);
                NativeArray<DetailLayerConfig>.Copy(tempConfigs, _cachedLayerConfigs, cfgIdx);
                tempConfigs.Dispose();

#if DEBUG
                // log layer configuration summary
                if (enableDebugLogging)
                {
                    Debug.Log($"[Detail Config] Terrain: {_terrain.name}, ScatterMode: {_terrainData.detailScatterMode}, " +
                              $"detailObjectDensity: {densityScale:F3}, Relevant layers: {cfgIdx}");

                    for (int i = 0; i < cfgIdx; i++)
                    {
                        var cfg = _cachedLayerConfigs[i];
                        var proto = _cachedDetailPrototypes[cfg.terrainLayerIndex];
                        Debug.Log($"  Layer {cfg.terrainLayerIndex}: proto.density={proto.density:F3}, " +
                                  $"useDensityScaling={proto.useDensityScaling}, " +
                                  $"effectiveDensity={cfg.effectiveDensity:F3}, " +
                                  $"meshArea={cfg.meshArea:F3}");
                    }
                }
#endif
            }

#if DEBUG
            if (enableDebugLogging)
            {
                long hmBytes = _cachedHeightmap.Length * 2L;
                long detailBytes = _cachedDetailLayers.IsCreated ? _cachedDetailLayers.Length : 0;
                long holesBytes = _cachedHoles.IsCreated ? (_cachedHoles.Length + 7) / 8 : 0;
                long total = hmBytes + detailBytes + holesBytes;
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Cached terrain data - heightmap {hmRes}^2 ({hmBytes / 1024}KB)" +
                          (_cachedDetailLayers.IsCreated ? $", details {detRes}^2 * {layerCount} layers ({detailBytes / 1024}KB)" : "") +
                          (_cachedHoles.IsCreated ? $", holes ({holesBytes / 1024}KB bit-packed)" : "") +
                          $", total {total / 1024}KB" +
                          (useUnityTransforms ? " (Unity path: holes/layerConfigs skipped)" : ""));
            }
#endif
        }

        private void DisposeTerrainDataCache()
        {
            _cachedHeightmap.DisposeIfCreated();
            _cachedDetailLayers.DisposeIfCreated();
            if (_cachedHoles.IsCreated) _cachedHoles.Dispose();
            _cachedLayerConfigs.DisposeIfCreated();
            // don't null _cachedDetailPrototypes here, needed by CacheTerrainData()
            // and refreshed at the start of InitializeDetails()
#if UNITY_EDITOR
            _detailTerrainDataCached = false;
#endif
        }

#if UNITY_EDITOR
        // refresh only the cached heightmap from terrain data
        private void RefreshCachedHeightmap()
        {
            if (_terrainData == null || !_cachedHeightmap.IsCreated) return;
            FlushPendingJobs();

            int hmRes = _cachedHeightmapRes;
            float[,] heights = _terrainData.GetHeights(0, 0, hmRes, hmRes);
            for (int z = 0; z < hmRes; z++)
                for (int x = 0; x < hmRes; x++)
                    _cachedHeightmap[z * hmRes + x] = (ushort)(heights[z, x] * 65535f + 0.5f);
        }

        // refresh only the cached holes mask from terrain data
        private void RefreshCachedHoles()
        {
            if (_terrainData == null) return;

            int holesRes = _cachedHolesRes;
            if (holesRes <= 0 || !_cachedHoles.IsCreated) return;
            FlushPendingJobs();

            bool[,] holes = _terrainData.GetHoles(0, 0, holesRes, holesRes);
            for (int z = 0; z < holesRes; z++)
                for (int x = 0; x < holesRes; x++)
                    _cachedHoles.Set(z * holesRes + x, holes[z, x]);
        }
#endif

        #endregion

        #region shared

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector2Int PatchToDetailChunk(Vector2Int patchCoord)
        {
            return new Vector2Int(
                patchCoord.x / _patchesPerDetailChunk,
                patchCoord.y / _patchesPerDetailChunk
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (int startX, int startZ, int endX, int endZ) GetDetailChunkPatchRange(Vector2Int detailChunkCoord)
        {
            int startX = detailChunkCoord.x * _patchesPerDetailChunk;
            int startZ = detailChunkCoord.y * _patchesPerDetailChunk;
            return (startX, startZ,
                    math.min(startX + _patchesPerDetailChunk, _patchCount),
                    math.min(startZ + _patchesPerDetailChunk, _patchCount));
        }

        // dispatches patch extraction to the Unity transforms or job-based path
        private NativeArray<InstanceData> ExtractPatchInstances(Vector2Int coord, out Bounds bounds)
        {
            // clear and reuse buffer
            _detailExtractBuffer.Clear();
            bounds = new Bounds();
            bool boundsInitialized = false;

            if (useUnityTransforms)
            {
                // legacy path: use Unity's ComputeDetailInstanceTransforms, allocates GC each call
                ExtractPatchInstancesUnity(coord, ref bounds, ref boundsInitialized);
                bounds.min -= _detailProtrusionNeg;
                bounds.max += _detailProtrusionPos;
            }
            else
            {
                // run extraction job synchronously, Burst-compiled same code as async path
                bounds = RunPatchJobSync(coord);
            }

            return _detailExtractBuffer.AsArray();
        }

        // allocates a BRG chunk, uploads instances, and updates all tracking state; returns false on failure
        private bool UploadDetailChunk(Vector2Int detailChunkCoord, NativeArray<InstanceData> instArray, Bounds bounds)
        {
#if DEBUG
            s_MarkerUploadChunk.Begin();
#endif
            var link = AllocChunk();
            if (!link.IsValid)
            {
                Debug.LogWarning($"TerrainGPUBatcherRegisterer[Details]: Failed to allocate BRG chunk for detail chunk ({detailChunkCoord.x},{detailChunkCoord.y})");
#if DEBUG
                s_MarkerUploadChunk.End();
#endif
                return false;
            }

            float detailRenderDist = Mathf.Min(_detailDistance, s_detailRenderDistance);
            float instanceCullDist = GPUBatcherConfig.Instance.cullDetailsAtDetailDistance ? detailRenderDist : float.MaxValue;
            WriteChunk(link, instArray, bounds, detailRenderDist, instanceCullDistance: instanceCullDist);

#if DEBUG
            if (enableDebugLogging && _detailChunkBRGChunkIds.Count < 5)
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Wrote BRG chunk {link.Id} for detail chunk ({detailChunkCoord.x},{detailChunkCoord.y}) with {instArray.Length} instances, bounds={bounds}");
#endif

            _detailChunkBRGChunkIds[detailChunkCoord] = link.Id;
            _allocatedDetailChunkCount++;
            _totalLoadedDetailInstances += instArray.Length;
            _loadedDetailChunks.Add(detailChunkCoord);
#if DEBUG
            s_MarkerUploadChunk.End();
#endif
            return true;
        }

        // re-applies DetailRenderDistance to every loaded detail chunk after a runtime change
        private void ApplyDetailRenderDistance()
        {
            if (_terrain == null) return;
            float detailRenderDist = Mathf.Min(_detailDistance, s_detailRenderDistance);
            float instanceCullDist = GPUBatcherConfig.Instance.cullDetailsAtDetailDistance ? detailRenderDist : float.MaxValue;
            foreach (var coord in _loadedDetailChunks)
                if (_detailChunkBRGChunkIds.TryGetValue(coord, out int brgChunkId))
                    SetChunkCullDistances(new ChunkLink(brgChunkId), detailRenderDist, instanceCullDist);
        }

        // frees the BRG chunk and cleans up all tracking data for a detail chunk
        private void ReleaseDetailChunk(Vector2Int detailChunkCoord)
        {
            if (_detailChunkBRGChunkIds.TryGetValue(detailChunkCoord, out int chunkId))
            {
                if (!debugSkipBRGUpload)
                {
                    var link = new ChunkLink(chunkId);
                    _totalLoadedDetailInstances -= GetTotalChunkInstanceCount(link);
                    ReleaseChunk(link);
                }
                _allocatedDetailChunkCount--;
            }

            _detailChunkBRGChunkIds.Remove(detailChunkCoord);
            _loadedDetailChunks.Remove(detailChunkCoord);
        }

        #endregion

        #region Unity transforms extraction

        // extracts all patches in a detail chunk via Unity's ComputeDetailInstanceTransforms and uploads to BRG
        private void LoadDetailChunkUnity(Vector2Int chunkCoord)
        {
            var (startX, startZ, endX, endZ) = GetDetailChunkPatchRange(chunkCoord);

            Bounds bounds = default;
            bool hasBounds = false;
            var allInstances = new NativeList<InstanceData>(4096, Allocator.TempJob);

            for (int pz = startZ; pz < endZ; pz++)
            {
                for (int px = startX; px < endX; px++)
                {
                    var patchInstances = ExtractPatchInstances(new Vector2Int(px, pz), out Bounds patchBounds);
                    if (patchInstances.Length == 0) continue;

                    allInstances.AddRange(patchInstances);
                    if (!hasBounds) { bounds = patchBounds; hasBounds = true; }
                    else bounds.Encapsulate(patchBounds);
                }
            }

            if (allInstances.Length > 0 && !debugSkipBRGUpload)
                UploadDetailChunk(chunkCoord, allInstances.AsArray(), bounds);

            allInstances.Dispose();
        }

        // legacy extraction using Unity's ComputeDetailInstanceTransforms; allocates GC each call
        private void ExtractPatchInstancesUnity(Vector2Int coord, ref Bounds bounds, ref bool boundsInitialized)
        {
            float invTerrainSizeX = 1f / _terrainSize.x;
            float invTerrainSizeZ = 1f / _terrainSize.z;

            // build HeightmapContext for normal computation, uses cached heightmap
            var hm = new HeightmapContext(_cachedHeightmap, _cachedHeightmapRes,
                0, 0, _cachedHeightmapRes, _cachedHeightmapRes, _terrainSize.y);
            float normalScaleX = (_cachedHeightmapRes - 1) * _terrainSize.y / (2f * _terrainSize.x);
            float normalScaleZ = (_cachedHeightmapRes - 1) * _terrainSize.y / (2f * _terrainSize.z);

            // use cached prototypes, property access allocates
            int layerCount = _cachedDetailPrototypes?.Length ?? 0;

            for (int layer = 0; layer < layerCount; layer++)
            {
                int globalProtoIdx = (_detailLayerToGlobalProto != null && layer < _detailLayerToGlobalProto.Length)
                    ? _detailLayerToGlobalProto[layer] : -1;
                if (globalProtoIdx < 0) continue;

                var prototype = _cachedDetailPrototypes[layer];
                float alignToGround = prototype.alignToGround;

                // allocates per call, Unity API limitation
                Bounds patchBounds;
                var transforms = _terrainData.ComputeDetailInstanceTransforms(
                    coord.x, coord.y, layer, _terrain.detailObjectDensity, out patchBounds);

#if DEBUG
                // check raw detail layer data
                int totalRaw = 0;
                if (enableDebugLogging && transforms.Length > 0)
                {
                    int[,] rawLayer = _terrainData.GetDetailLayer(
                        coord.x * _detailResolutionPerPatch, coord.y * _detailResolutionPerPatch,
                        _detailResolutionPerPatch, _detailResolutionPerPatch, layer);
                    for (int z = 0; z < _detailResolutionPerPatch; z++)
                        for (int x = 0; x < _detailResolutionPerPatch; x++)
                            totalRaw += rawLayer[z, x];
                }
                if (enableDebugLogging && transforms.Length > 0)
                {
                    int cellsPerPatch = _detailResolutionPerPatch * _detailResolutionPerPatch;
                    float avgRawPerCell = totalRaw / (float)cellsPerPatch;
                    float cellSizeX = _terrainSize.x / _detailResolution;
                    float cellSizeZ = _terrainSize.z / _detailResolution;
                    float cellArea = cellSizeX * cellSizeZ;

                    Debug.Log($"[Detail Unity] Patch ({coord.x},{coord.y}) Layer {layer}: " +
                              $"prototype.density={prototype.density:F3}, " +
                              $"detailObjectDensity={_terrain.detailObjectDensity:F3}, " +
                              $"useDensityScaling={prototype.useDensityScaling}, " +
                              $"scatterMode={_terrainData.detailScatterMode}, " +
                              $"patchSize={_detailResolutionPerPatch}x{_detailResolutionPerPatch} ({cellsPerPatch} cells), " +
                              $"cellArea={cellArea:F3}m^2, totalRaw={totalRaw}, avgRawPerCell={avgRawPerCell:F2}, " +
                              $"Unity spawned={transforms.Length} instances");
                }
#endif

                foreach (var t in transforms)
                {
                    float3 worldPos = (float3)_terrainPosition + new float3(t.posX, t.posY, t.posZ);

                    // computeDetailInstanceTransforms only returns Y rotation; alignToGround is handled by
                    // Unity's detail shader, not baked into transforms. we approximate via quaternion rotation.
                    float normX = t.posX * invTerrainSizeX;
                    float normZ = t.posZ * invTerrainSizeZ;
                    float3 terrainNormal = DetailExtractionHelpers.ComputeNormal(in hm, normX, normZ, normalScaleX, normalScaleZ);
                    quaternion rotation = DetailExtractionHelpers.ComputeAlignedRotation(t.rotationY, terrainNormal, alignToGround);

                    _detailExtractBuffer.Add(new InstanceData
                    {
                        position = worldPos,
                        rotation = rotation,
                        scale = new float3(t.scaleXZ, t.scaleY, t.scaleXZ),
                        signatureIndex = globalProtoIdx,
                    });

                    // expand bounds
                    if (!boundsInitialized)
                    {
                        bounds = new Bounds(worldPos, Vector3.zero);
                        boundsInitialized = true;
                    }
                    else
                    {
                        bounds.Encapsulate(worldPos);
                    }
                }
            }
        }

        #endregion

        #region custom job-based extraction

        private class PendingPatchJob
        {
            public Vector2Int coord;
            public JobHandle handle;
            // job outputs, read after completion then returned to pool
            public NativeList<InstanceData> instances;
            public NativeArray<float> boundsMinMax;
        }

        private List<PendingPatchJob> _pendingJobs = new();
        private HashSet<Vector2Int> _pendingCoords = new();  // fast lookup for coords with in-flight jobs

        // static output buffer pool shared across all terrains, avoids NativeList alloc/dealloc per patch
        private const int kMaxJobBufferPoolSize = 32;
        private static Stack<PendingPatchJob> s_jobBufferPool = new();

        // static scratch for synchronous single-patch extraction
        private static NativeArray<float> s_syncBoundsMinMax;

        private static PendingPatchJob AcquireJobBuffers()
        {
            if (s_jobBufferPool.Count > 0)
            {
                var job = s_jobBufferPool.Pop();
                job.instances.Clear();
                return job;
            }
            return new PendingPatchJob
            {
                instances = new NativeList<InstanceData>(4096, Allocator.Persistent),
                boundsMinMax = new NativeArray<float>(6, Allocator.Persistent),
            };
        }

        private static void ReleaseJobBuffers(PendingPatchJob job)
        {
            if (s_jobBufferPool.Count >= kMaxJobBufferPoolSize)
            {
                if (job.instances.IsCreated) job.instances.Dispose();
                if (job.boundsMinMax.IsCreated) job.boundsMinMax.Dispose();
                return;
            }
            s_jobBufferPool.Push(job);
        }

        private static void DisposeAllJobBuffers()
        {
            foreach (var job in s_jobBufferPool)
            {
                if (job.instances.IsCreated) job.instances.Dispose();
                if (job.boundsMinMax.IsCreated) job.boundsMinMax.Dispose();
            }
            s_jobBufferPool.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Bounds ReconstructBoundsFromJob(NativeArray<float> boundsMinMax)
        {
            Vector3 pMin = new Vector3(boundsMinMax[0], boundsMinMax[1], boundsMinMax[2]) - _detailProtrusionNeg;
            Vector3 pMax = new Vector3(boundsMinMax[3], boundsMinMax[4], boundsMinMax[5]) + _detailProtrusionPos;
            return new Bounds((pMin + pMax) * 0.5f, pMax - pMin);
        }

        private DetailExtractionJobInstanceCount CreateJobInstanceCount(
            int cellStartX, int cellStartZ, int cellsPerAxis,
            NativeList<InstanceData> instances, NativeArray<float> boundsMinMax)
        {
            float cellSizeX = _terrainSize.x / _detailResolution;
            float cellSizeZ = _terrainSize.z / _detailResolution;
            return new DetailExtractionJobInstanceCount
            {
                heightmap = _cachedHeightmap,
                heightmapRegionX = 0, heightmapRegionZ = 0,
                heightmapRegionW = _cachedHeightmapRes, heightmapRegionH = _cachedHeightmapRes,
                heightmapResolution = _cachedHeightmapRes,
                detailLayers = _cachedDetailLayers, layerCount = _cachedRelevantLayerCount,
                holes = _cachedHoles,
                holeRegionX = 0, holeRegionZ = 0,
                holeRegionW = _cachedHolesRes, holeRegionH = _cachedHolesRes,
                holesResolution = _cachedHolesRes, hasHoles = _cachedHolesRes > 0,
                layerConfigs = _cachedLayerConfigs,
                terrainPosition = _terrainPosition, terrainSize = _terrainSize,
                detailResolution = _detailResolution,
                detailResPerPatch = cellsPerAxis,
                patchStartX = cellStartX, patchStartZ = cellStartZ,
                cellSizeX = cellSizeX, cellSizeZ = cellSizeZ,
                instances = instances, boundsMinMax = boundsMinMax,
            };
        }

        private DetailExtractionJobCoverage CreateJobCoverage(
            int cellStartX, int cellStartZ, int cellsPerAxis,
            NativeList<InstanceData> instances, NativeArray<float> boundsMinMax)
        {
            float cellSizeX = _terrainSize.x / _detailResolution;
            float cellSizeZ = _terrainSize.z / _detailResolution;
            return new DetailExtractionJobCoverage
            {
                heightmap = _cachedHeightmap,
                heightmapRegionX = 0, heightmapRegionZ = 0,
                heightmapRegionW = _cachedHeightmapRes, heightmapRegionH = _cachedHeightmapRes,
                heightmapResolution = _cachedHeightmapRes,
                detailLayers = _cachedDetailLayers, layerCount = _cachedRelevantLayerCount,
                holes = _cachedHoles,
                holeRegionX = 0, holeRegionZ = 0,
                holeRegionW = _cachedHolesRes, holeRegionH = _cachedHolesRes,
                holesResolution = _cachedHolesRes, hasHoles = _cachedHolesRes > 0,
                layerConfigs = _cachedLayerConfigs,
                terrainPosition = _terrainPosition, terrainSize = _terrainSize,
                detailResolution = _detailResolution,
                detailResPerPatch = cellsPerAxis,
                patchStartX = cellStartX, patchStartZ = cellStartZ,
                cellSizeX = cellSizeX, cellSizeZ = cellSizeZ,
                instances = instances, boundsMinMax = boundsMinMax,
            };
        }

        private void ScheduleChunkJob(Vector2Int chunkCoord)
        {
            if (_pendingCoords.Contains(chunkCoord))
                return;

            int cellStartX = chunkCoord.x * _patchesPerDetailChunk * _detailResolutionPerPatch;
            int cellStartZ = chunkCoord.y * _patchesPerDetailChunk * _detailResolutionPerPatch;
            int cellsPerAxis = _detailResolutionPerPatch * _patchesPerDetailChunk;

            var pending = AcquireJobBuffers();
            pending.coord = chunkCoord;

            pending.handle = _useCoverageMode
                ? CreateJobCoverage(cellStartX, cellStartZ, cellsPerAxis, pending.instances, pending.boundsMinMax).Schedule()
                : CreateJobInstanceCount(cellStartX, cellStartZ, cellsPerAxis, pending.instances, pending.boundsMinMax).Schedule();

            _pendingJobs.Add(pending);
            _pendingCoords.Add(chunkCoord);
        }

        // runs extraction job synchronously via Run(), writes directly into _detailExtractBuffer
        private Bounds RunPatchJobSync(Vector2Int coord)
        {
            int cellStartX = coord.x * _detailResolutionPerPatch;
            int cellStartZ = coord.y * _detailResolutionPerPatch;

            if (!s_syncBoundsMinMax.IsCreated)
                s_syncBoundsMinMax = new NativeArray<float>(6, Allocator.Persistent);

            if (_useCoverageMode)
                CreateJobCoverage(cellStartX, cellStartZ, _detailResolutionPerPatch, _detailExtractBuffer, s_syncBoundsMinMax).Run();
            else
                CreateJobInstanceCount(cellStartX, cellStartZ, _detailResolutionPerPatch, _detailExtractBuffer, s_syncBoundsMinMax).Run();

            return _detailExtractBuffer.Length > 0
                ? ReconstructBoundsFromJob(s_syncBoundsMinMax)
                : new Bounds();
        }

        // uploads results from a completed detail chunk extraction job to BRG
        private void CompleteChunkJob(PendingPatchJob pending)
        {
            var detailChunkCoord = pending.coord;
            var instances = pending.instances;

#if DEBUG
            if (enableDebugLogging)
            {
                var (patchStartX, patchStartZ, _, _) = GetDetailChunkPatchRange(detailChunkCoord);
                int cellsPerDetailChunk = _detailResolutionPerPatch * _patchesPerDetailChunk;
                int totalCells = cellsPerDetailChunk * cellsPerDetailChunk;
                int cellStartX = patchStartX * _detailResolutionPerPatch;
                int cellStartZ = patchStartZ * _detailResolutionPerPatch;

                if (_cachedRelevantLayerCount > 0 && _cachedLayerConfigs.IsCreated)
                {
                    Debug.Log($"[Detail Manual] Chunk ({detailChunkCoord.x},{detailChunkCoord.y}) TOTAL: {instances.Length} instances across {_cachedRelevantLayerCount} layers");

                    for (int cfgIdx = 0; cfgIdx < _cachedRelevantLayerCount; cfgIdx++)
                    {
                        var config = _cachedLayerConfigs[cfgIdx];
                        int layerIdx = config.terrainLayerIndex;
                        int layerBaseOffset = layerIdx * _detailResolution * _detailResolution;

                        int samples = math.min(5, totalCells);
                        string rawSamples = "";
                        int totalRaw = 0;
                        for (int i = 0; i < samples; i++)
                        {
                            int linearIdx = i * totalCells / samples;
                            int localX = linearIdx / cellsPerDetailChunk;
                            int localZ = linearIdx % cellsPerDetailChunk;
                            int globalX = math.min(cellStartX + localX, _detailResolution - 1);
                            int globalZ = math.min(cellStartZ + localZ, _detailResolution - 1);
                            int rawCount = _cachedDetailLayers[layerBaseOffset + globalZ * _detailResolution + globalX];
                            totalRaw += rawCount;
                            rawSamples += $"{rawCount},";
                        }
                        float avgRaw = totalRaw / (float)samples;

                        Debug.Log($"  Layer {layerIdx} (cfg{cfgIdx}): effectiveDensity={config.effectiveDensity:F3}, " +
                                  $"meshArea={config.meshArea:F3}, sample rawCounts=[{rawSamples}] avgRaw={avgRaw:F2}");
                    }
                }
                else
                {
                    Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Job completed for detail chunk ({detailChunkCoord.x},{detailChunkCoord.y}) - {instances.Length} instances");
                }
            }
#endif

            if (instances.Length == 0 || debugSkipBRGUpload)
            {
                ReleaseJobBuffers(pending);
                _pendingCoords.Remove(detailChunkCoord);
                // keep in _activeChunksSet so streaming doesn't re-queue empty chunks every frame
                return;
            }

            Bounds chunkBounds = ReconstructBoundsFromJob(pending.boundsMinMax);
            if (!UploadDetailChunk(detailChunkCoord, instances.AsArray(), chunkBounds))
                _activeChunksSet.Remove(detailChunkCoord);

            ReleaseJobBuffers(pending);
            _pendingCoords.Remove(detailChunkCoord);
        }

        // force-completes and disposes all in-flight extraction jobs
        private void FlushPendingJobs()
        {
            for (int i = 0; i < _pendingJobs.Count; i++)
            {
                try
                {
                    _pendingJobs[i].handle.Complete();
                }
                finally
                {
                    // remove from _activeChunksSet so streaming can re-queue the chunk
                    _activeChunksSet.Remove(_pendingJobs[i].coord);
                    ReleaseJobBuffers(_pendingJobs[i]);
                }
            }
            _pendingJobs.Clear();
            _pendingCoords.Clear();
        }

        #endregion
    }
}
