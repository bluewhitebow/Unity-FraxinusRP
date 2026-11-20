// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    public partial class TerrainGPUBatcherRegisterer
    {
        #region configuration

        // cached TerrainData properties, used at runtime and in editor change detection
        private TreeInstance[] _cachedTreeInstances;

        [Header("Chunking")]
        [Tooltip("World-space size of each spatial chunk (meters). " +
                 "Smaller = more chunks = better culling but more overhead. " +
                 "A 2048m terrain with 256m chunks gives 8x8 = 64 chunks.")]
        [Min(1f)]
        [SerializeField] private float treeChunkSize = 256f;

        [SerializeField] private bool debugSkipTreeBRGUpload = false;

        private static float s_treeRenderDistance = float.MaxValue;

        /// <summary>Global per-instance tree render distance. Assigning re-applies to all registered tree chunks.</summary>
        public static float TreeRenderDistance
        {
            get => s_treeRenderDistance;
            set
            {
                if (s_treeRenderDistance == value) return;
                s_treeRenderDistance = value;
                foreach (var reg in s_activeRegisterers)
                    reg.ApplyTreeRenderDistance();
            }
        }

        private GameObject[] _resolvedTreePrefabs;

        #endregion

        #region runtime state

        private bool _treesRegistered;

        // extracted instances with signatureIndex set to global BRG proto index
        private List<InstanceData> _treeCachedInstances = new();
        // sigs we registered, iterated by ComputeMaxMeshExtent
        private List<RenderSignature> _treeCachedPrototypes = new();
        // terrain proto index -> global BRG proto index, size = treePrototypes.Length, -1 = unmapped
        private int[] _treeProtoToGlobal;

        // chunk state: chunkId -> (bounds, instanceCount)
        private Dictionary<int, (Bounds bounds, int instanceCount)> _treeChunkData = new();
        private int _totalTreeInstances;

        // parallel to _treeCachedInstances: _cachedToTerrainTreeIndex[i] = terrain treeInstances index
        private List<int> _cachedToTerrainTreeIndex = new();
        // chunk ID -> ordered terrain tree indices, same order as InstanceLinks for that chunk
        private Dictionary<int, List<int>> _chunkIdToTerrainTreeIndices = new();
        // per-axis asymmetric mesh protrusion from instance origin
        private Vector3 _maxMeshProtrusionPos = Vector3.one;  // max positive protrusion (+X,+Y,+Z)
        private Vector3 _maxMeshProtrusionNeg = Vector3.one;  // max negative protrusion (-X,-Y,-Z) stored as positive
        private float _maxHorizontalProtrusion = 1f;

        // native buffer for instance data packing
        private NativeList<InstanceData> _treeInstanceBuffer;

        #endregion

        #region initialization & shutdown

        private void InitializeTrees()
        {
            if (_treesRegistered) return;
            if (_terrain == null || _terrain.terrainData == null)
            {
                Debug.LogWarning("TerrainGPUBatcherRegisterer[Trees]: Cannot initialize - terrain or terrainData is null");
                return;
            }

            if (_cachedTreeInstances == null)
            {
                Debug.LogError("TerrainGPUBatcherRegisterer[Trees]: Failed to cache tree data - terrainData.treeInstances returned null");
                return;
            }

            try
            {
                _treeCachedInstances.Clear();
                _treeCachedPrototypes.Clear();

                ResolveTreePrefabs();

                ExtractTrees();

                if (_treeCachedInstances.Count == 0)
                {
#if DEBUG
                    if (enableDebugLogging)
                        Debug.LogWarning($"TerrainGPUBatcherRegisterer[Trees]: No trees extracted from {gameObject.name}");
#endif
                    return;
                }

#if DEBUG
                if (enableDebugLogging)
                    Debug.Log($"TerrainGPUBatcherRegisterer[Trees]: Extracted {_treeCachedInstances.Count} trees from {gameObject.name}");
#endif

                Register();

                _treesRegistered = true;

#if UNITY_EDITOR
                UpdateTreeStateCache();
#endif
            }
            catch (Exception e)
            {
                _treeInstanceBuffer.DisposeIfCreated();
                foreach (int chunkId in _treeChunkData.Keys)
                    ReleaseChunk(new ChunkLink(chunkId));
                _treeChunkData.Clear();
                _chunkIdToTerrainTreeIndices.Clear();
                _totalTreeInstances = 0;
                Debug.LogError($"TerrainGPUBatcherRegisterer[Trees]: Failed to register {gameObject.name}: {e.Message}\n{e.StackTrace}");
            }
        }

        private void ShutdownTrees()
        {
            if (!_treesRegistered) return;

            // release chunks from BRG system
            foreach (int chunkId in _treeChunkData.Keys)
                ReleaseChunk(new ChunkLink(chunkId));

            Unregister();

            _treesRegistered = false;
        }

        #endregion

        #region registration

        private void Register()
        {
            _treeInstanceBuffer = new NativeList<InstanceData>(4096, Allocator.Persistent);

            ComputeMaxMeshExtent();
            UploadChunks();

#if DEBUG
            if (enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Trees]: Registered {_totalTreeInstances:N0} trees in {_treeChunkData.Count} chunks");
#endif
        }

        private void Unregister()
        {
            // chunks freed by ShutdownTrees before this call, just clear local metadata
            if (_treeProtoToGlobal != null)
            {
                for (int i = 0; i < _treeProtoToGlobal.Length; i++)
                {
                    int globalIdx = _treeProtoToGlobal[i];
                    if (globalIdx >= 0) UnregisterRenderSignature(globalIdx);
                }
                _treeProtoToGlobal = null;
            }
            _treeChunkData.Clear();
            _chunkIdToTerrainTreeIndices.Clear();
            _cachedToTerrainTreeIndex.Clear();
            _totalTreeInstances = 0;

            _treeInstanceBuffer.DisposeIfCreated();
        }

        #endregion

        #region extraction

        // resolve tree prefabs from terrain prototypes
        public GameObject[] ResolveTreePrefabs()
        {
            if (_terrain == null) _terrain = GetComponent<Terrain>();
            var td = _terrain != null ? _terrain.terrainData : null;
            if (td == null) return _resolvedTreePrefabs = Array.Empty<GameObject>();

            var protos = td.treePrototypes;
            var resolved = new GameObject[protos.Length];
            for (int i = 0; i < protos.Length; i++)
                resolved[i] = protos[i].prefab;
            return _resolvedTreePrefabs = resolved;
        }

        private void ExtractTrees()
        {
            var terrainData = _terrain.terrainData;
            var terrainPos = _terrain.transform.position;

#if UNITY_EDITOR
            if (_terrain.transform.rotation != Quaternion.identity)
                Debug.LogWarning($"TerrainGPUBatcherRegisterer[Trees]: Terrain '{_terrain.name}' has non-identity rotation. " +
                    "Unity terrains are axis-aligned; rotation is ignored and tree positions may appear incorrect.", _terrain);
#endif

            var treeInstances = _cachedTreeInstances ?? terrainData.treeInstances;

            if (_resolvedTreePrefabs == null) ResolveTreePrefabs();

            int protoCount = _resolvedTreePrefabs.Length;
            _treeProtoToGlobal = new int[protoCount];
            var prefabScales = new NativeArray<float3>(protoCount, Allocator.Temp);
            for (int i = 0; i < protoCount; i++)
            {
                _treeProtoToGlobal[i] = -1;

                GameObject prefab = _resolvedTreePrefabs[i];
                if (prefab == null) continue;

                var sig = ExtractRenderSignature(prefab);
                if (sig == null) continue;

                if (!_terrain.preserveTreePrototypeLayers)
                    sig.OverrideLayer(_terrain.gameObject.layer);

                if (sig.mesh == null)
                {
                    Debug.LogError($"TerrainGPUBatcherRegisterer[Trees]: SKIPPED tree[{i}] '{prefab.name}' - mesh is NULL!");
                    continue;
                }

                int globalIdx = RegisterRenderSignature(sig);
                if (globalIdx < 0) continue;

                _treeProtoToGlobal[i] = globalIdx;
                _treeCachedPrototypes.Add(sig);
                prefabScales[i] = (float3)prefab.transform.localScale;

#if DEBUG
                if (enableDebugLogging)
                    Debug.Log($"TerrainGPUBatcherRegisterer[Trees]: Registered tree[{i}] '{prefab.name}' as global proto[{globalIdx}] with {sig.LODCount} LODs");
#endif
            }

            var protoMap = new NativeArray<int>(_treeProtoToGlobal, Allocator.Temp);
            var nativeTreeInstances = new NativeArray<TreeInstance>(treeInstances, Allocator.Temp);
            var outInstances = new NativeList<InstanceData>(treeInstances.Length, Allocator.Temp);
            var outIndices = new NativeList<int>(treeInstances.Length, Allocator.Temp);

            TreeExtractionBurstHelpers.ExtractTreeInstances(
                ref nativeTreeInstances, ref protoMap, ref prefabScales,
                terrainPos, (float3)terrainData.size,
                ref outInstances, ref outIndices);

            _cachedToTerrainTreeIndex.Clear();
            for (int i = 0; i < outInstances.Length; i++)
            {
                _treeCachedInstances.Add(outInstances[i]);
                _cachedToTerrainTreeIndex.Add(outIndices[i]);
            }

            outIndices.Dispose();
            outInstances.Dispose();
            nativeTreeInstances.Dispose();
            protoMap.Dispose();
            prefabScales.Dispose();
        }

        #endregion

        #region chunking & upload

        private void ComputeMaxMeshExtent()
        {
            _maxMeshProtrusionPos = Vector3.one;
            _maxMeshProtrusionNeg = Vector3.one;

            // find largest prototype mesh protrusion in each direction from origin
            // raw mesh protrusion, no instance scale baked in - per-instance scale
            // applied in CalculateTreeBounds for tight per-chunk bounds
            foreach (var proto in _treeCachedPrototypes)
            {
                if (proto.mesh == null) continue;

                var meshBounds = proto.mesh.bounds;
                Vector3 meshMax = meshBounds.center + meshBounds.extents;
                Vector3 meshMin = meshBounds.center - meshBounds.extents;

                // positive protrusion: how far mesh extends in +X, +Y, +Z from origin
                Vector3 posProtrusion = new Vector3(
                    math.max(0, meshMax.x),
                    math.max(0, meshMax.y),
                    math.max(0, meshMax.z)
                );

                // negative protrusion: how far mesh extends in -X, -Y, -Z from origin, stored positive
                Vector3 negProtrusion = new Vector3(
                    math.max(0, -meshMin.x),
                    math.max(0, -meshMin.y),
                    math.max(0, -meshMin.z)
                );

                _maxMeshProtrusionPos = Vector3.Max(_maxMeshProtrusionPos, posProtrusion);
                _maxMeshProtrusionNeg = Vector3.Max(_maxMeshProtrusionNeg, negProtrusion);
            }

            // collapse horizontal protrusion to one radius so per-instance Y rotation can't poke the canopy out of the AABB
            _maxHorizontalProtrusion = math.max(
                math.max(_maxMeshProtrusionPos.x, _maxMeshProtrusionNeg.x),
                math.max(_maxMeshProtrusionPos.z, _maxMeshProtrusionNeg.z));

#if DEBUG
            if (enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Trees]: Mesh protrusion pos={_maxMeshProtrusionPos} neg={_maxMeshProtrusionNeg}");
#endif
        }

        private Bounds CalculateTreeBounds(NativeArray<InstanceData> instances)
        {
            if (instances.Length == 0)
                return new Bounds();

            // per-instance expansion: each tree's bounds depend on its actual scale for tight chunks
            var inst0 = instances[0];
            float horiz0 = math.max(inst0.scale.x, inst0.scale.z) * _maxHorizontalProtrusion;
            Vector3 min = (Vector3)inst0.position - new Vector3(horiz0, _maxMeshProtrusionNeg.y * inst0.scale.y, horiz0);
            Vector3 max = (Vector3)inst0.position + new Vector3(horiz0, _maxMeshProtrusionPos.y * inst0.scale.y, horiz0);

            for (int i = 1; i < instances.Length; i++)
            {
                var inst = instances[i];
                float horiz = math.max(inst.scale.x, inst.scale.z) * _maxHorizontalProtrusion;
                Vector3 instMin = (Vector3)inst.position - new Vector3(horiz, _maxMeshProtrusionNeg.y * inst.scale.y, horiz);
                Vector3 instMax = (Vector3)inst.position + new Vector3(horiz, _maxMeshProtrusionPos.y * inst.scale.y, horiz);
                min = Vector3.Min(min, instMin);
                max = Vector3.Max(max, instMax);
            }

            return new Bounds((min + max) * 0.5f, max - min);
        }

        private void UploadChunks()
        {
            _treeChunkData.Clear();
            _chunkIdToTerrainTreeIndices.Clear();
            _totalTreeInstances = 0;

            if (_treeCachedInstances.Count == 0)
                return;

            UploadWithGrid();

#if DEBUG
            if (enableDebugLogging)
            {
                int avg = _treeChunkData.Count > 0 ? _totalTreeInstances / _treeChunkData.Count : 0;
                Debug.Log($"TerrainGPUBatcherRegisterer[Trees]: Grid ({treeChunkSize}m) produced {_treeChunkData.Count} chunks " +
                          $"for {_totalTreeInstances} instances (avg {avg} per chunk, max {GPUBatcherSystem.MaxInstancesPerChunk})");
            }
#endif
        }

        // grid-based chunking: groups instances into fixed-size spatial cells, auto-split by WriteChunk if oversized
        private void UploadWithGrid()
        {
            var chunkGroups = new Dictionary<int2, List<int>>();

            for (int i = 0; i < _treeCachedInstances.Count; i++)
            {
                var pos = _treeCachedInstances[i].position;
                int2 gridCoord = new int2(
                    (int)math.floor(pos.x / treeChunkSize),
                    (int)math.floor(pos.z / treeChunkSize)
                );

                if (!chunkGroups.TryGetValue(gridCoord, out var list))
                {
                    list = new List<int>();
                    chunkGroups[gridCoord] = list;
                }
                list.Add(i);
            }

            foreach (var kvp in chunkGroups)
            {
                int2 cell = kvp.Key;
                var indices = kvp.Value;

                // writeChunk auto-splits if instances exceed MaxInstancesPerChunk
                int chunkId = UploadChunkFromList(indices, 0, indices.Count);
                if (chunkId < 0)
                {
                    Debug.LogWarning($"TerrainGPUBatcherRegisterer[Trees]: Chunk allocation failed for cell ({cell.x},{cell.y}), " +
                        $"{indices.Count} instances lost. Pool may be full.");
                    continue;
                }
            }
        }

        // upload a contiguous range of indices as a single chunk
        private int UploadChunkFromList(List<int> indices, int start, int count)
        {
            // fill instance buffer
            _treeInstanceBuffer.Clear();
            if (_treeInstanceBuffer.Capacity < count)
                _treeInstanceBuffer.SetCapacity(count);

            for (int i = 0; i < count; i++)
                _treeInstanceBuffer.Add(_treeCachedInstances[indices[start + i]]);

            var link = AllocChunk();
            if (!link.IsValid)
            {
                Debug.LogWarning($"TerrainGPUBatcherRegisterer[Trees]: Failed to allocate chunk, {count} instances dropped");
                return -1;
            }

            // write chunk data - OTW+WTO matrices computed on GPU from TRS
            var instSlice = _treeInstanceBuffer.AsArray().GetSubArray(0, count);

            Bounds chunkBounds = CalculateTreeBounds(instSlice);

            _treeChunkData[link.Id] = (chunkBounds, count);

            // populated before WriteChunk since OnChunkWritten may fire synchronously
            if (_cachedToTerrainTreeIndex.Count > 0)
            {
                var terrainIndices = new List<int>(count);
                for (int i = 0; i < count; i++)
                    terrainIndices.Add(_cachedToTerrainTreeIndex[indices[start + i]]);
                _chunkIdToTerrainTreeIndices[link.Id] = terrainIndices;
            }

            if (!debugSkipTreeBRGUpload)
            {
                float chunkCull = Mathf.Min(_terrain.treeDistance, s_treeRenderDistance);
                WriteChunk(link, instSlice, chunkBounds, chunkCull, s_treeRenderDistance);
            }

            _totalTreeInstances += count;

            return link.Id;
        }

        // re-applies TreeRenderDistance to every registered tree chunk after a runtime change
        private void ApplyTreeRenderDistance()
        {
            if (_terrain == null) return;
            float chunkCull = Mathf.Min(_terrain.treeDistance, s_treeRenderDistance);
            foreach (int chunkId in _treeChunkData.Keys)
                SetChunkCullDistances(new ChunkLink(chunkId), chunkCull, s_treeRenderDistance);
        }

        #endregion

        #region InstanceLink data

        public event Action<ChunkLink, List<int>> OnTreeChunkWritten;
        public Dictionary<int, List<int>> ChunkOwnedTrees => _chunkIdToTerrainTreeIndices;
        public ICollection<int> TreeChunkIds => _treeChunkData.Keys;
        public TreeInstance[] CachedTreeInstanceData => _cachedTreeInstances;
        private void OnTreeChunkWrittenInternal(int chunkId)
        {
            _chunkIdToTerrainTreeIndices.TryGetValue(chunkId, out var terrainIndices);
            OnTreeChunkWritten?.Invoke(new ChunkLink(chunkId), terrainIndices);
        }

        #endregion

    }

    [BurstCompile]
    internal static class TreeExtractionBurstHelpers
    {
        [BurstCompile]
        public static void ExtractTreeInstances(
            ref NativeArray<TreeInstance> treeInstances,
            ref NativeArray<int> protoMap,
            ref NativeArray<float3> prefabScales,
            in float3 terrainPos,
            in float3 terrainSize,
            ref NativeList<InstanceData> outInstances,
            ref NativeList<int> outTerrainIndices)
        {
            int protoCount = protoMap.Length;
            for (int i = 0; i < treeInstances.Length; i++)
            {
                var tree = treeInstances[i];
                if (tree.prototypeIndex < 0 || tree.prototypeIndex >= protoCount) continue;

                int globalProtoIdx = protoMap[tree.prototypeIndex];
                if (globalProtoIdx < 0) continue;

                float3 worldPos = terrainPos + (float3)tree.position * terrainSize;

                // Unity terrain widthScale/heightScale are multipliers on top of the prefab's own scale
                float3 ps = prefabScales[tree.prototypeIndex];
                float3 scale = new float3(tree.widthScale * ps.x, tree.heightScale * ps.y, tree.widthScale * ps.z);

                outInstances.Add(new InstanceData
                {
                    position = worldPos,
                    rotation = quaternion.Euler(0, tree.rotation, 0),
                    scale = scale,
                    signatureIndex = globalProtoIdx,
                });
                outTerrainIndices.Add(i);
            }
        }
    }
}
