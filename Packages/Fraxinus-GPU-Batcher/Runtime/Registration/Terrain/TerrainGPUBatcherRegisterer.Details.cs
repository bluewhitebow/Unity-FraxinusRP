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
    public partial class TerrainGPUBatcherRegisterer
    {
        #region configuration

        [Tooltip("When enabled, detail instances keep their prefab's layer. " +
                 "When disabled, details use the terrain GameObject's layer (matches Unity's tree behavior).")]
        [SerializeField] private bool preservePrototypeLayers = false;

        [Header("Streaming")]
        [Tooltip("When enabled, patches are loaded/unloaded based on camera distance. When disabled, all patches are loaded at startup.")]
        [SerializeField] private bool useStreaming = true;

        [Header("Transform Generation")]
        [Tooltip("Use Unity's ComputeDetailInstanceTransforms API (slower, allocates GC, but 1:1 match with Unity rendering). When OFF, uses fast manual extraction.")]
        [SerializeField] private bool useUnityTransforms = false;

        private GameObject[] _resolvedDetailPrefabs;

        [Header("Debug")]
        [Tooltip("Extract patch data but don't upload to BRG system (for profiling chunk loading vs upload)")]
        [SerializeField] private bool debugSkipBRGUpload = false;

        #endregion

#if DEBUG
        static readonly ProfilerMarker s_MarkerUpdate = new ProfilerMarker("DetailRegisterer.Update");
        static readonly ProfilerMarker s_MarkerProcessQueues = new ProfilerMarker("DetailRegisterer.ProcessQueues");
        static readonly ProfilerMarker s_MarkerTargetChunks = new ProfilerMarker("DetailRegisterer.TargetChunks");
        static readonly ProfilerMarker s_MarkerCompleteJobs = new ProfilerMarker("DetailRegisterer.CompleteJobs");
        static readonly ProfilerMarker s_MarkerUploadChunk = new ProfilerMarker("DetailRegisterer.UploadChunk");
#endif

        #region runtime state

        private float _detailDistance;

        private static float s_detailRenderDistance = float.MaxValue;

        /// <summary>Global detail render distance. Assigning re-applies to all loaded detail chunks.</summary>
        public static float DetailRenderDistance
        {
            get => s_detailRenderDistance;
            set
            {
                if (s_detailRenderDistance == value) return;
                s_detailRenderDistance = value;
                foreach (var reg in s_activeRegisterers)
                    reg.ApplyDetailRenderDistance();
            }
        }

        private int _patchCount;
        private float _patchWorldSize;    // x dimension
        private float _patchWorldSizeZ;   // z dimension, differs from x on rectangular terrains
        private Vector3 _terrainPosition;
        private Vector3 _terrainSize;

        // chunk-level streaming queues
        private Queue<Vector2Int> _loadQueue = new();
        private Queue<Vector2Int> _unloadQueue = new();
        private Queue<Vector2Int> _extractQueue = new();
        private HashSet<Vector2Int> _unloadQueueSet = new();
        private HashSet<Vector2Int> _extractQueueSet = new();

        // unified tracking: all chunks that are loaded, pending, or queued
        private HashSet<Vector2Int> _activeChunksSet = new();

        // static scratch buffers shared across all terrains
        private static NativeBitArray s_targetChunkBits; // [y * chunksPerAxis + x]
        private static List<(Vector2Int coord, float distSq)> s_chunkSortBuffer = new();

        private int _totalLoadedDetailInstances;
        private int _allocatedDetailChunkCount;

        // detail chunk tracking
        private HashSet<Vector2Int> _loadedDetailChunks = new();
        private Dictionary<Vector2Int, int> _detailChunkBRGChunkIds = new();

        // terrain detail layer idx -> global BRG proto index, size = _resolvedDetailPrefabs.Length, -1 = unmapped
        private int[] _detailLayerToGlobalProto;
        // per-axis asymmetric mesh protrusion from instance origin, handles off-center bounds
        private Vector3 _detailProtrusionPos = Vector3.one;
        private Vector3 _detailProtrusionNeg = Vector3.one;

        private bool _firstUpdate = true;
        private bool _detailsInitialized;

        // static comparer boxed once at class init
        private static readonly IComparer<(Vector2Int coord, float distSq)> kChunkDistanceComparer = new ChunkDistanceComparer();
        private struct ChunkDistanceComparer : IComparer<(Vector2Int coord, float distSq)>
        {
            public int Compare((Vector2Int coord, float distSq) a, (Vector2Int coord, float distSq) b)
                => a.distSq.CompareTo(b.distSq);
        }

        // cached detail layer data for optimized extraction
        private int _detailResolution;
        private int _detailResolutionPerPatch;
        private float _detailCellSize;          // world size of one detail cell

        // detail chunk grid
        private float _detailChunkSize;
        private float _detailChunkSizeZ;
        private int _detailChunksPerAxis;
        private int _patchesPerDetailChunk;

        // squared distance from point to closest edge of a detail chunk AABB (XZ only)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float ChunkEdgeDistanceSq(Vector2Int chunkCoord, Vector3 point)
        {
            var (startX, startZ, endX, endZ) = GetDetailChunkPatchRange(chunkCoord);
            float minX = _terrainPosition.x + startX * _patchWorldSize;
            float minZ = _terrainPosition.z + startZ * _patchWorldSizeZ;
            float maxX = _terrainPosition.x + endX * _patchWorldSize;
            float maxZ = _terrainPosition.z + endZ * _patchWorldSizeZ;
            float dx = math.clamp(point.x, minX, maxX) - point.x;
            float dz = math.clamp(point.z, minZ, maxZ) - point.z;
            return dx * dx + dz * dz;
        }

        // true if any camera is within unload range of the terrain AABB
        private bool TerrainInRange(NativeList<float3> cameraPositions)
        {
            float maxDist = _detailDistance + GPUBatcherConfig.Instance.terrainDetailUnloadHysteresis;
            float maxDistSq = maxDist * maxDist;
            float tMinX = _terrainPosition.x, tMaxX = _terrainPosition.x + _terrainSize.x;
            float tMinZ = _terrainPosition.z, tMaxZ = _terrainPosition.z + _terrainSize.z;
            for (int c = 0; c < cameraPositions.Length; c++)
            {
                float3 p = cameraPositions[c];
                float dx = math.clamp(p.x, tMinX, tMaxX) - p.x;
                float dz = math.clamp(p.z, tMinZ, tMaxZ) - p.z;
                if (dx * dx + dz * dz <= maxDistSq) return true;
            }
            return false;
        }

        #endregion

        #region initialization & shutdown

        private void InitializeDetails()
        {
            if (_terrain == null || _terrain.terrainData == null)
            {
                Debug.LogError("TerrainGPUBatcherRegisterer[Details]: Cannot initialize - terrain or terrainData is null");
                return;
            }

            _terrainData = _terrain.terrainData;
            _firstUpdate = true;

            // verify cache was populated
            if (_cachedDetailPrototypes == null)
            {
                Debug.LogError("TerrainGPUBatcherRegisterer[Details]: Failed to cache detail prototypes - terrainData.detailPrototypes returned null");
                return;
            }

            if (!_detailExtractBuffer.IsCreated)
                _detailExtractBuffer = new NativeList<InstanceData>(kExtractBufferInitialCapacity, Allocator.Persistent);

            _patchCount = _terrainData.detailPatchCount;
            if (_patchCount <= 0)
            {
                Debug.LogWarning("TerrainGPUBatcherRegisterer[Details]: detailPatchCount is 0, skipping detail init");
                return;
            }
            _terrainPosition = _terrain.transform.position;
            _terrainSize = _terrainData.size;
            _patchWorldSize = _terrainSize.x / _patchCount;
            _patchWorldSizeZ = _terrainSize.z / _patchCount;

            // cache detail resolution data
            _detailResolution = _terrainData.detailResolution;
            if (_detailResolution <= 0)
            {
                Debug.LogWarning("TerrainGPUBatcherRegisterer[Details]: detailResolution is 0, skipping detail init");
                return;
            }
            _detailResolutionPerPatch = _detailResolution / _patchCount;
            _detailCellSize = _terrainSize.x / _detailResolution;

            // snap detail chunk grid to closest multiple of patch size
            _patchesPerDetailChunk = math.max(1, (int)math.round(detailChunkSize / _patchWorldSize));
            _detailChunkSize = _patchesPerDetailChunk * _patchWorldSize;
            _detailChunkSizeZ = _patchesPerDetailChunk * _patchWorldSizeZ;

            // calculate detail chunk grid dimensions
            _detailChunksPerAxis = (int)math.ceil(_terrainSize.x / _detailChunkSize);

            // grow static scratch buffer if this terrain needs more slots
            int totalChunks = _detailChunksPerAxis * _detailChunksPerAxis;
            if (!s_targetChunkBits.IsCreated || s_targetChunkBits.Length < totalChunks)
            {
                if (s_targetChunkBits.IsCreated) s_targetChunkBits.Dispose();
                s_targetChunkBits = new NativeBitArray(totalChunks, Allocator.Persistent);
            }

            _loadQueue = new Queue<Vector2Int>();
            _unloadQueue = new Queue<Vector2Int>();
            _extractQueue = new Queue<Vector2Int>();
            _unloadQueueSet = new HashSet<Vector2Int>();
            _extractQueueSet = new HashSet<Vector2Int>();
            _activeChunksSet = new HashSet<Vector2Int>();
            _loadedDetailChunks = new HashSet<Vector2Int>();
            _detailChunkBRGChunkIds = new Dictionary<Vector2Int, int>();

            _detailDistance = _terrain.detailObjectDistance;
#if UNITY_EDITOR
            _lastKnownDetailDistance = _terrain.detailObjectDistance;
#endif

            if (_detailDistance <= 0)
            {
                Debug.Log("TerrainGPUBatcherRegisterer[Details]: Terrain detailObjectDistance is 0, skipping detail init");
                return;
            }

            // log basic init info if debug enabled
#if DEBUG
            if (enableDebugLogging)
            {
                bool isRectangular = math.abs(_patchWorldSize - _patchWorldSizeZ) > 0.01f;

                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Init - detailDistance={_detailDistance}, patchCount={_patchCount}, patchSize={_patchWorldSize:F1}m * {_patchWorldSizeZ:F1}m{(isRectangular ? " (RECTANGULAR!)" : "")}, " +
                          $"detailRes={_detailResolution}, resPerPatch={_detailResolutionPerPatch}, cellSize={_detailCellSize:F3}m, useUnityTransforms={useUnityTransforms}");
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Detail chunks - chunkSize={_detailChunkSize:F1}m, chunksPerAxis={_detailChunksPerAxis}, " +
                          $"patchesPerChunk={_patchesPerDetailChunk}x{_patchesPerDetailChunk} ({_patchesPerDetailChunk * _patchesPerDetailChunk} patches per chunk)");
            }
#endif

            ResolveDetailPrefabs();

            // build prototype data and register with BRG system
            BuildPrototypeData();

            // cache terrain data into NativeArrays; must run after BuildPrototypeData
            CacheTerrainData();


#if DEBUG
            if (enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Ready - chunk system will auto-init on first alloc");
#endif

#if UNITY_EDITOR
            UpdateDetailStateCache();
#endif

            // decide startup loading mode
            bool loadAllNow = !useStreaming;
            if (loadAllNow)
            {
                LoadAllPatches();
            }

            _detailsInitialized = true;

            if (GPUBatcherSystem != null)
                GPUBatcherSystem.OnStreamingPositionsReady += OnStreamingPositionsReady;
        }

        private void ShutdownDetails()
        {
            if (GPUBatcherSystem != null)
                GPUBatcherSystem.OnStreamingPositionsReady -= OnStreamingPositionsReady;
            FlushPendingJobs();

            _detailExtractBuffer.DisposeIfCreated();
            DisposeTerrainDataCache();

            // release detail chunks first so grass protos have 0 instances at removal
            if (!debugSkipBRGUpload)
            {
                var detailCoords = new List<Vector2Int>(_loadedDetailChunks);
                for (int i = 0; i < detailCoords.Count; i++)
                    ReleaseDetailChunk(detailCoords[i]);
            }
#if FRAXINUS_RP   //texture grass
            DisposeGeneratedGrassAssets();
#endif

            if (_detailLayerToGlobalProto != null && !debugSkipBRGUpload)
            {
                for (int i = 0; i < _detailLayerToGlobalProto.Length; i++)
                {
                    int globalIdx = _detailLayerToGlobalProto[i];
                    if (globalIdx >= 0) UnregisterRenderSignature(globalIdx);
                }
            }
            _detailLayerToGlobalProto = null;

            // clear local metadata
            _loadQueue.Clear();
            _extractQueue.Clear();
            _unloadQueue.Clear();
            _extractQueueSet.Clear();
            _unloadQueueSet.Clear();
            _activeChunksSet.Clear();

            _loadedDetailChunks.Clear();
            _detailChunkBRGChunkIds.Clear();

            _totalLoadedDetailInstances = 0;
            _allocatedDetailChunkCount = 0;
            _detailsInitialized = false;
        }

        #endregion

        #region prototype building

        // resolve detail prefabs from terrain prototypes + GPUBatcherTerrainDetailOverride (null = texture grass)
        public GameObject[] ResolveDetailPrefabs()
        {
            if (_terrain == null) _terrain = GetComponent<Terrain>();
            var td = _terrain != null ? _terrain.terrainData : null;
            var protos = _cachedDetailPrototypes ?? (td != null ? td.detailPrototypes : null);
            if (protos == null) return _resolvedDetailPrefabs = Array.Empty<GameObject>();

            var resolved = new GameObject[protos.Length];
            for (int i = 0; i < protos.Length; i++)
            {
                var proto = protos[i];
                if (!proto.usePrototypeMesh || proto.prototype == null) continue;
                resolved[i] = proto.prototype.TryGetComponent<GPUBatcherTerrainDetailOverride>(out var o)
                    && o.enabled && o.overridePrefab != null
                    ? o.overridePrefab
                    : proto.prototype;
            }
            return _resolvedDetailPrefabs = resolved;
        }

        private void BuildPrototypeData()
        {
            _detailLayerToGlobalProto = null;

            if (_resolvedDetailPrefabs == null) ResolveDetailPrefabs();

            DetailPrototype[] detailProtos = _terrainData != null ? _terrainData.detailPrototypes : null;

            _detailLayerToGlobalProto = new int[_resolvedDetailPrefabs.Length];
            _detailProtrusionPos = Vector3.one;
            _detailProtrusionNeg = Vector3.one;
            for (int i = 0; i < _resolvedDetailPrefabs.Length; i++)
            {
                _detailLayerToGlobalProto[i] = -1;
                if (_resolvedDetailPrefabs[i] == null)
                {
#if FRAXINUS_RP   //texture grass is urp only
                    if (!debugSkipBRGUpload && detailProtos != null && i < detailProtos.Length)
                    {
                        int texIdx = BuildTextureGrassPrototype(detailProtos[i]);
                        if (texIdx >= 0) _detailLayerToGlobalProto[i] = texIdx;
                    }
#endif
                    continue;
                }

                var sig = ExtractRenderSignature(_resolvedDetailPrefabs[i]);
                if (sig?.mesh == null) continue;

                // protrusion = mesh bounds * detail width/height, expands runtime patch bounds
                if (detailProtos != null && i < detailProtos.Length)
                {
                    var b = sig.mesh.bounds;
                    var dp = detailProtos[i];
                    Vector3 mMax = b.center + b.extents, mMin = b.center - b.extents;
                    _detailProtrusionPos = Vector3.Max(_detailProtrusionPos, new Vector3(
                        math.max(0f, mMax.x) * dp.maxWidth, math.max(0f, mMax.y) * dp.maxHeight, math.max(0f, mMax.z) * dp.maxWidth));
                    _detailProtrusionNeg = Vector3.Max(_detailProtrusionNeg, new Vector3(
                        math.max(0f, -mMin.x) * dp.maxWidth, math.max(0f, -mMin.y) * dp.maxHeight, math.max(0f, -mMin.z) * dp.maxWidth));
                }

                if (!preservePrototypeLayers)
                    sig.OverrideLayer(_terrain.gameObject.layer);

                // terrain details default to camera motion vectors
                if (!GPUBatcherConfig.Instance.terrainDetailObjectMotionVectors)
                    sig.OverrideMotionVectorMode(MotionVectorGenerationMode.Camera);

                if (debugSkipBRGUpload)
                {
                    // fake global so extraction math flows without touching BRG
                    _detailLayerToGlobalProto[i] = i;
                }
                else
                {
                    int globalIdx = RegisterRenderSignature(sig);
                    if (globalIdx < 0) continue;
                    _detailLayerToGlobalProto[i] = globalIdx;
                }
            }

#if UNITY_EDITOR
            _rendererHashDirty = true;
#endif
        }

        #endregion

        #region streaming

        private void OnStreamingPositionsReady()
        {
            if (!_detailsInitialized) return;

#if FRAXINUS_RP
            if (GPUBatcherConfig.Instance.allowTextureGrass)
                UpdateGrassWindIfChanged();
#endif

            if (!useStreaming) return;

#if UNITY_EDITOR
            // in edit mode, throttle streaming updates
            if (!Application.isPlaying)
            {
                double currentTime = UnityEditor.EditorApplication.timeSinceStartup;
                if (updateInterval > 0 && currentTime - _editorLastUpdateTime < updateInterval)
                {
                    ProcessPatchQueues();
                    return;
                }
                _editorLastUpdateTime = currentTime;
            }
#endif

#if DEBUG
            s_MarkerUpdate.Begin();

            if (_firstUpdate && enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Details] [{GPUBatcherHelpers.EntityIDString(this)}]: First streaming update, chunkSystemInitialized={GPUBatcherSystem.ChunkSystemInitialized}");
#endif

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // poll for detailObjectDistance changes in editor
                if (_terrain != null && (Time.frameCount & 63) == 0)
                {
                    float currentDetailDistance = _terrain.detailObjectDistance;
                    if (!GPUBatcherBurstHelpers.Approximately(currentDetailDistance, _detailDistance))
                    {
                        _detailDistance = currentDetailDistance;
                        if (enableDebugLogging)
                            Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Detail distance changed ({_detailDistance}m), refreshing...");
                        Refresh();
                        s_MarkerUpdate.End();
                        return;
                    }
                }
            }
#endif

            var positions = GPUBatcherSystem.StreamingCameraPositions;
            if (positions.Length == 0)
            {
#if DEBUG
                s_MarkerUpdate.End();
                if (_firstUpdate && enableDebugLogging)
                    Debug.LogWarning("TerrainGPUBatcherRegisterer[Details]: No streaming camera positions available!");
#endif
                return;
            }

            // skip terrain entirely if all cameras are beyond unload range
            if (!_firstUpdate && !TerrainInRange(positions))
            {
                // queue all loaded chunks for unload
                foreach (var chunkCoord in _loadedDetailChunks)
                {
                    if (!_unloadQueueSet.Contains(chunkCoord))
                    {
                        _unloadQueue.Enqueue(chunkCoord);
                        _unloadQueueSet.Add(chunkCoord);
                    }
                }
                ProcessPatchQueues();
#if DEBUG
                s_MarkerUpdate.End();
#endif
                return;
            }

            UpdateTargetChunks(positions);
            ProcessPatchQueues();
            _firstUpdate = false;

#if DEBUG
            s_MarkerUpdate.End();
#endif
        }

        private void UpdateTargetChunks(NativeList<float3> cameraPositions)
        {
#if DEBUG
            s_MarkerTargetChunks.Begin();
#endif
            int camCount = cameraPositions.Length;
            float hysteresis = GPUBatcherConfig.Instance.terrainDetailUnloadHysteresis;
            float unloadDistanceSq = (_detailDistance + hysteresis) * (_detailDistance + hysteresis);

            // step 1: build target chunk set
            int cpa = _detailChunksPerAxis;
            int totalChunks = cpa * cpa;

            var camArray = cameraPositions.AsArray();

            DetailExtractionHelpers.BuildTargetChunkBits(
                ref s_targetChunkBits, in camArray, camCount,
                cpa, _detailDistance,
                _terrainPosition.x, _terrainPosition.z,
                _detailChunkSize, _detailChunkSizeZ,
                _patchWorldSize, _patchWorldSizeZ,
                _patchesPerDetailChunk, _patchCount);

#if DEBUG
            if (_firstUpdate && enableDebugLogging)
            {
                int targetCount = 0;
                for (int i = 0; i < totalChunks; i++) if (s_targetChunkBits.IsSet(i)) targetCount++;
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: FIRST UpdateTargetChunks - {camCount} cameras, detailDist={_detailDistance}, target={targetCount} chunks");
            }
#endif

            // step 2: cancel pending chunk unloads for chunks back in target
            int unloadCount = _unloadQueue.Count;
            for (int i = 0; i < unloadCount; i++)
            {
                var chunkCoord = _unloadQueue.Dequeue();
                if (s_targetChunkBits.IsSet(chunkCoord.y * cpa + chunkCoord.x))
                    _unloadQueueSet.Remove(chunkCoord); // cancelled, back in target
                else
                    _unloadQueue.Enqueue(chunkCoord); // still out of range, keep queued
            }

            // step 3: queue loads for target chunks not yet active
            s_chunkSortBuffer.Clear();
            for (int ci = 0; ci < totalChunks; ci++)
            {
                if (!s_targetChunkBits.IsSet(ci)) continue;
                var chunkCoord = new Vector2Int(ci % cpa, ci / cpa);
                if (_activeChunksSet.Contains(chunkCoord)) continue;

                float minDistSq = float.MaxValue;
                for (int c = 0; c < camCount; c++)
                {
                    float d = ChunkEdgeDistanceSq(chunkCoord, cameraPositions[c]);
                    if (d < minDistSq) minDistSq = d;
                }
                s_chunkSortBuffer.Add((chunkCoord, minDistSq));
            }

#if DEBUG
            if (_firstUpdate && enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: FIRST - {s_chunkSortBuffer.Count} new chunks to queue");
#endif

            s_chunkSortBuffer.Sort(kChunkDistanceComparer);
            foreach (var (chunkCoord, _) in s_chunkSortBuffer)
            {
                _loadQueue.Enqueue(chunkCoord);
                _activeChunksSet.Add(chunkCoord);
            }

            // step 4: queue unloads for loaded chunks no longer in target, with hysteresis
            foreach (var chunkCoord in _loadedDetailChunks)
            {
                if (s_targetChunkBits.IsSet(chunkCoord.y * cpa + chunkCoord.x)) continue;
                if (_unloadQueueSet.Contains(chunkCoord)) continue;

                float minDistSq = float.MaxValue;
                for (int c = 0; c < camCount; c++)
                {
                    float d = ChunkEdgeDistanceSq(chunkCoord, cameraPositions[c]);
                    if (d < minDistSq) minDistSq = d;
                }

                if (minDistSq > unloadDistanceSq)
                {
                    _unloadQueue.Enqueue(chunkCoord);
                    _unloadQueueSet.Add(chunkCoord);
                }
            }
#if DEBUG
            s_MarkerTargetChunks.End();
#endif
        }

        // resets global streaming budgets once per frame
        private void ResetBudgetIfNewFrame()
        {
            uint frame = GPUBatcherSystem.RenderCycleCounter;
            if (s_budgetResetFrame == frame) return;
            s_budgetResetFrame = frame;
            var config = GPUBatcherConfig.Instance;
            s_extractBudget = config.terrainMaxUploadsPerFrame;
            s_unloadBudget = config.terrainMaxUnloadsPerFrame;
            s_pendingJobCount = 0;
            // count all in-flight jobs across active terrains
            foreach (var reg in s_activeRegisterers)
            {
                if (reg != null)
                    s_pendingJobCount += reg._pendingJobs.Count;
            }
        }

        private void ProcessPatchQueues()
        {
#if DEBUG
            s_MarkerProcessQueues.Begin();
#endif
            ResetBudgetIfNewFrame();

            int extracted = 0;
            int queued = 0;
            var config = GPUBatcherConfig.Instance;

            bool forceCompleteAll = _firstUpdate && config.terrainSkipThrottleOnStart;
            int extractLimit = forceCompleteAll ? int.MaxValue : s_extractBudget;
            int queueLimit = forceCompleteAll ? int.MaxValue : s_extractBudget * 2;

#if DEBUG
            if (_firstUpdate && enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: FIRST ProcessPatchQueues - loadQueue={_loadQueue.Count}, extractQueue={_extractQueue.Count}, loaded={_loadedDetailChunks.Count} chunks, extractLimit={extractLimit}, queueLimit={queueLimit}");
#endif

            // phase 1: move chunks from load queue to extract queue
            while (_loadQueue.Count > 0 && queued < queueLimit)
            {
                var chunkCoord = _loadQueue.Dequeue();
                if (!_loadedDetailChunks.Contains(chunkCoord) && !_pendingCoords.Contains(chunkCoord) && !_extractQueueSet.Contains(chunkCoord))
                {
                    _extractQueue.Enqueue(chunkCoord);
                    _extractQueueSet.Add(chunkCoord);
                    _activeChunksSet.Add(chunkCoord);
                    queued++;
                }
            }

#if DEBUG
            if (_firstUpdate && enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Phase1 complete - moved {queued} chunks, loadQueue now={_loadQueue.Count}, extractQueue now={_extractQueue.Count}");
#endif

            // phase 2: extract and upload chunks
            bool useJobPath = !useUnityTransforms;
            int maxConcurrentJobs = config.terrainMaxConcurrentJobs;

            // phase 2a: complete finished extraction jobs and upload results
#if DEBUG
            s_MarkerCompleteJobs.Begin();
#endif
            for (int i = _pendingJobs.Count - 1; i >= 0; i--)
            {
                var pending = _pendingJobs[i];
                if (!pending.handle.IsCompleted && !forceCompleteAll) continue;

                pending.handle.Complete();
                CompleteChunkJob(pending);
                _pendingJobs.RemoveAt(i);
                s_pendingJobCount--;
                extracted++;

                if (!forceCompleteAll && extracted >= extractLimit) break;
            }
#if DEBUG
            s_MarkerCompleteJobs.End();
#endif

            // phase 2b: schedule new extraction jobs / sync-extract
            while (_extractQueue.Count > 0)
            {
                if (!forceCompleteAll)
                {
                    if (useJobPath && s_pendingJobCount >= math.max(1, maxConcurrentJobs)) break;
                    if (!useJobPath && extracted >= extractLimit) break;
                }

                var chunkCoord = _extractQueue.Dequeue();
                _extractQueueSet.Remove(chunkCoord);
                if (_loadedDetailChunks.Contains(chunkCoord) || _pendingCoords.Contains(chunkCoord))
                    continue;

                if (useJobPath)
                {
                    ScheduleChunkJob(chunkCoord);
                    s_pendingJobCount++;
                }
                else
                {
                    LoadDetailChunkUnity(chunkCoord);
                    extracted++;

#if DEBUG
                    if (enableDebugLogging)
                        Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Loaded chunk ({chunkCoord.x}, {chunkCoord.y})");
#endif
                }
            }

            // phase 2c: force-complete pending jobs if required
            bool forceCompleteNow = forceCompleteAll || (config.terrainCompleteJobsSameFrame && _pendingJobs.Count > 0);
            if (forceCompleteNow)
            {
                for (int i = _pendingJobs.Count - 1; i >= 0; i--)
                {
                    _pendingJobs[i].handle.Complete();
                    CompleteChunkJob(_pendingJobs[i]);
                    extracted++;
                }
                s_pendingJobCount -= _pendingJobs.Count;
                _pendingJobs.Clear();
                _pendingCoords.Clear();
            }

            // deduct extracts from global budget
            s_extractBudget -= extracted;

#if DEBUG
            if (_firstUpdate && enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Phase2 complete - extracted {extracted} chunks, loaded now={_loadedDetailChunks.Count}, {_pendingJobs.Count} jobs pending");
#endif

            if (_firstUpdate && extracted > 0)
            {
#if DEBUG
                if (enableDebugLogging)
                    Debug.Log($"TerrainGPUBatcherRegisterer[Details]: FIRST load complete - {_loadedDetailChunks.Count} chunks, {_totalLoadedDetailInstances} instances, {_pendingJobs.Count} jobs pending");
#endif
                if (_pendingJobs.Count == 0)
                    _firstUpdate = false;
            }
            else if (_firstUpdate && _loadQueue.Count == 0 && _extractQueue.Count == 0 && _pendingJobs.Count == 0)
            {
#if DEBUG
                if (enableDebugLogging)
                    Debug.Log($"TerrainGPUBatcherRegisterer[Details]: FIRST update - no chunks to load (queues empty)");
#endif
                _firstUpdate = false;
            }

            // phase 3: unload chunks atomically
            int unloaded = 0;
            while (_unloadQueue.Count > 0 && unloaded < s_unloadBudget)
            {
                var chunkCoord = _unloadQueue.Dequeue();
                _unloadQueueSet.Remove(chunkCoord);
                if (_loadedDetailChunks.Contains(chunkCoord))
                {
                    ReleaseDetailChunk(chunkCoord);
                    _activeChunksSet.Remove(chunkCoord);
                    unloaded++;

#if DEBUG
                    if (enableDebugLogging)
                        Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Unloaded chunk ({chunkCoord.x}, {chunkCoord.y})");
#endif
                }
            }
            s_unloadBudget -= unloaded;
#if DEBUG
            s_MarkerProcessQueues.End();
#endif
        }

        // loads all terrain detail chunks at once for non-streaming mode
        private void LoadAllPatches()
        {
            int chunksPerAxis = (_patchCount + _patchesPerDetailChunk - 1) / _patchesPerDetailChunk;

            if (useUnityTransforms)
            {
                for (int cy = 0; cy < chunksPerAxis; cy++)
                    for (int cx = 0; cx < chunksPerAxis; cx++)
                        LoadDetailChunkUnity(new Vector2Int(cx, cy));
            }
            else
            {
                for (int cy = 0; cy < chunksPerAxis; cy++)
                    for (int cx = 0; cx < chunksPerAxis; cx++)
                        ScheduleChunkJob(new Vector2Int(cx, cy));

                for (int i = 0; i < _pendingJobs.Count; i++)
                {
                    _pendingJobs[i].handle.Complete();
                    CompleteChunkJob(_pendingJobs[i]);
                }
                _pendingJobs.Clear();
                _pendingCoords.Clear();
            }

#if DEBUG
            if (enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: Loaded all {_loadedDetailChunks.Count} chunks ({_totalLoadedDetailInstances:N0} instances in {_allocatedDetailChunkCount} BRG chunks)");
#endif
        }

        #endregion
    }
}
