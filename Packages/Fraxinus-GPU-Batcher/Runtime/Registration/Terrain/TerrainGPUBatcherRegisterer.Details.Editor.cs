// Copyright (c) 2026 Milk_Drinker01. All rights reserved.
// editor-only partial: detail change detection, painting state, terrain editing, gizmo drawing, context menus.

#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Serialization;

namespace Fraxinus.GPUBatcher
{
    public partial class TerrainGPUBatcherRegisterer
    {

        #region detail change detection

        private bool _rendererHashDirty = true;

        // cached terrain state for change detection
        private int _lastKnownDetailPrototypeCount;
        private float _lastKnownDetailDistance;
        private float _lastKnownDetailDensity;
        private float _lastKnownTreeDistance;
        private int _detailPatchCheckOffset;  // round-robin offset for incremental checking
        private bool _isMouseDown;
        private bool _wasMouseDownLastFrame;
        private bool _paintingJustStarted;
        private bool _paintingJustEnded;
        private bool _wasPainting;
        private int _detailLastHotControl;
        private Vector2 _cachedMousePos;
        // reused per-frame scratch collections
        private readonly List<Vector2Int> _patchesToReload = new();
        private readonly List<Vector2Int> _patchListScratch = new();
        private readonly HashSet<Vector2Int> _checkedPatches = new();

        private int _detailPrototypePropertiesHash;

        // cached hash of prefab renderer data, recomputed only when _rendererHashDirty is set
        private int _detailCachedRendererHash;

        private bool _detailTerrainDataCached;

        private void OnSceneGUI(UnityEditor.SceneView sceneView)
        {
            Event e = Event.current;

            // cache mouse position while Event.current is valid
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDown ||
                e.type == EventType.MouseDrag || e.type == EventType.MouseUp)
            {
                _cachedMousePos = e.mousePosition;
            }

            int hotControl = GUIUtility.hotControl;

            if (_detailLastHotControl != 0 && hotControl == 0 && _wasPainting)
            {
                _isMouseDown = false;
                _wasPainting = false;
            }

            if (hotControl != 0)
            {
                _wasPainting = true;
                _isMouseDown = true;
            }

            _detailLastHotControl = hotControl;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (GUIUtility.hotControl != 0) return;
                _isMouseDown = true;
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                _isMouseDown = false;
            }
        }

        // called from OnTerrainChanged - checks prototype count/properties and distance
        public void CheckForDetailPrototypeChanges()
        {
            if (_terrain == null || _terrainData == null) return;

            _cachedDetailPrototypes = _terrainData.detailPrototypes;
            int currentPrototypeCount = _cachedDetailPrototypes?.Length ?? 0;

            if (currentPrototypeCount != _lastKnownDetailPrototypeCount)
            {
#if DEBUG
                if (_enableDebugLogging)
                    Debug.Log($"TerrainGPUBatcherRegisterer[Details]: prototype count changed ({_lastKnownDetailPrototypeCount} -> {currentPrototypeCount}), full refresh");
#endif
                Refresh();
                return;
            }

            int currentPropsHash = ComputePrototypePropertiesHash();
            if (currentPropsHash != _detailPrototypePropertiesHash)
            {
#if DEBUG
                if (_enableDebugLogging)
                    Debug.Log($"TerrainGPUBatcherRegisterer[Details]: prototype properties changed, full refresh");
#endif
                Refresh();
                return;
            }

            float currentDetailDistance = _terrain.detailObjectDistance;
            if (!GPUBatcherBurstHelpers.Approximately(currentDetailDistance, _lastKnownDetailDistance))
            {
                _lastKnownDetailDistance = currentDetailDistance;
                _detailDistance = currentDetailDistance;
#if DEBUG
                if (_enableDebugLogging)
                    Debug.Log($"TerrainGPUBatcherRegisterer[Details]: detail distance changed ({currentDetailDistance}m), refreshing...");
#endif
                Refresh();
            }
        }

        // checks terrain detail data for changes and performs incremental reload if needed
        public void CheckForDetailChanges()
        {
            if (_terrain == null || _terrainData == null) return;

            if (!ShouldScanForDetailChanges())
                return;

            int layerCount = _cachedDetailPrototypes?.Length ?? 0;
            if (layerCount == 0) return;

            const int k_nearbyPatchRadius = 1;  // current patch + immediate neighbours
            const int k_patchesPerFrame = 20;  // base scan rate

            _patchesToReload.Clear();
            _checkedPatches.Clear();

            Vector2Int? cursorPatch = GetMouseTerrainPatchCoord();
            if (cursorPatch.HasValue)
            {
                Vector2Int center = cursorPatch.Value;
                for (int dy = -k_nearbyPatchRadius; dy <= k_nearbyPatchRadius; dy++)
                {
                    for (int dx = -k_nearbyPatchRadius; dx <= k_nearbyPatchRadius; dx++)
                    {
                        Vector2Int patch = new(center.x + dx, center.y + dy);
                        if (patch.x < 0 || patch.y < 0 || patch.x >= _patchCount || patch.y >= _patchCount)
                            continue;
                        _checkedPatches.Add(patch);
                        if (HasPatchChanged(patch, layerCount))
                            _patchesToReload.Add(patch);
                    }
                }
            }

            // round-robin check loaded patches for changes outside cursor area
            _patchListScratch.Clear();
            foreach (var chunkCoord in _loadedDetailChunks)
            {
                var (startX, startZ, endX, endZ) = GetDetailChunkPatchRange(chunkCoord);
                for (int z = startZ; z < endZ; z++)
                    for (int x = startX; x < endX; x++)
                        _patchListScratch.Add(new Vector2Int(x, z));
            }
            int patchCount = _patchListScratch.Count;

            if (patchCount > 0)
            {
                bool checkAllPatches = (_detailPatchCheckOffset < 0);
                int actualPatchesPerFrame = math.min(k_patchesPerFrame, patchCount);

                int startIdx, endIdx;
                if (checkAllPatches || actualPatchesPerFrame >= patchCount)
                {
                    startIdx = 0;
                    endIdx = patchCount;
                }
                else
                {
                    startIdx = _detailPatchCheckOffset % patchCount;
                    endIdx = math.min(startIdx + actualPatchesPerFrame, patchCount);
                }

                for (int i = startIdx; i < endIdx; i++)
                {
                    Vector2Int patch = _patchListScratch[i];
                    if (_checkedPatches.Contains(patch)) continue;
                    if (HasPatchChanged(patch, layerCount))
                        _patchesToReload.Add(patch);
                }

                if (checkAllPatches || actualPatchesPerFrame >= patchCount)
                    _detailPatchCheckOffset = 0;
                else
                    _detailPatchCheckOffset = endIdx % patchCount;
            }

            ReloadChangedPatches(layerCount);
        }

        // called from OnTerrainChanged when not painting - scans all loaded patches at once
        private void ScanAllLoadedPatches()
        {
            if (_terrain == null || _terrainData == null) return;
            if (_loadedDetailChunks.Count == 0) return;

            int layerCount = _cachedDetailPrototypes?.Length ?? 0;
            if (layerCount == 0) return;

            _patchesToReload.Clear();
            foreach (var chunkCoord in _loadedDetailChunks)
            {
                var (startX, startZ, endX, endZ) = GetDetailChunkPatchRange(chunkCoord);
                for (int z = startZ; z < endZ; z++)
                    for (int x = startX; x < endX; x++)
                    {
                        var patch = new Vector2Int(x, z);
                        if (HasPatchChanged(patch, layerCount))
                            _patchesToReload.Add(patch);
                    }
            }

            ReloadChangedPatches(layerCount);
        }

        // reloads patches in _patchesToReload, updates cache, deduplicates by detail chunk
        private void ReloadChangedPatches(int layerCount)
        {
            if (_patchesToReload.Count == 0) return;

#if DEBUG
            if (_enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: detected changes in {_patchesToReload.Count} patches, reloading");
#endif

            FlushPendingJobs();

            foreach (var patch in _patchesToReload)
                UpdateCachedPatchLayers(patch, layerCount);

            _checkedPatches.Clear();
            foreach (var patch in _patchesToReload)
            {
                Vector2Int detailChunk = PatchToDetailChunk(patch);
                if (_checkedPatches.Add(detailChunk))
                    ReloadChunk(detailChunk);
            }

            GPUBatcherSystem.RequestSnapAllViews();
            UnityEditor.SceneView.RepaintAll();
        }

        // force-reloads all currently loaded detail chunks
        private void ForceReloadAllLoadedChunks()
        {
            if (_loadedDetailChunks.Count == 0) return;

            FlushPendingJobs();

            // snapshot - ReloadChunk modifies _loadedDetailChunks
            var snapshot = new Vector2Int[_loadedDetailChunks.Count];
            _loadedDetailChunks.CopyTo(snapshot);
            foreach (var chunkCoord in snapshot)
                ReloadChunk(chunkCoord);

            UnityEditor.SceneView.RepaintAll();
        }

        private Vector2Int? GetMouseTerrainPatchCoord()
        {
            if (UnityEditor.SceneView.lastActiveSceneView == null)
                return null;

            Ray ray;
            try
            {
                ray = UnityEditor.HandleUtility.GUIPointToWorldRay(_cachedMousePos);
            }
            catch
            {
                // EditorUpdate can run without a valid SceneView GUI context.
                // Skip cursor prioritization; round-robin scanning still runs.
                return null;
            }

            if (Physics.Raycast(ray, out RaycastHit hit, 5000f))
            {
                if (hit.collider.gameObject == _terrain.gameObject)
                {
                    Vector3 terrainPos = hit.point - _terrain.transform.position;
                    Vector3 terrainSize = _terrain.terrainData.size;

                    float normX = math.saturate(terrainPos.x / terrainSize.x);
                    float normZ = math.saturate(terrainPos.z / terrainSize.z);

                    int patchX = (int)math.floor(normX * _patchCount);
                    int patchY = (int)math.floor(normZ * _patchCount);

                    return new Vector2Int(patchX, patchY);
                }
            }

            return null;
        }

        // update mouse painting state once per EditorUpdate, before tree/detail callers read it
        private void UpdatePaintingState()
        {
            _paintingJustStarted = _isMouseDown && !_wasMouseDownLastFrame;
            _paintingJustEnded = !_isMouseDown && _wasMouseDownLastFrame;
            _wasMouseDownLastFrame = _isMouseDown;

            if (_paintingJustStarted)
                _detailPatchCheckOffset = 0;
            else if (_paintingJustEnded)
                _detailPatchCheckOffset = -1;
        }

        // check if terrain is selected and user is painting; must be called after UpdatePaintingState()
        private bool ShouldScanForDetailChanges()
        {
            // check if terrain or its GameObject is selected
            var selection = UnityEditor.Selection.activeGameObject;
            if (selection == null || selection != _terrain.gameObject)
            {
                // also check if TerrainData is selected in project
                if (UnityEditor.Selection.activeObject != _terrainData)
                    return false;
            }

            // return true if we need a full scan, ensures it runs after painting ends
            if (_detailPatchCheckOffset < 0)
                return true;

            // check every frame when terrain selected
            return GUIUtility.hotControl != 0;
        }

        private int ComputePrototypePropertiesHash()
        {
            if (_cachedDetailPrototypes == null) return 0; // cache not initialized yet

            var h = new System.HashCode();
            // cheap fields from the cached prototype array
            for (int i = 0; i < _cachedDetailPrototypes.Length; i++)
            {
                var proto = _cachedDetailPrototypes[i];
                h.Add(proto.minWidth);
                h.Add(proto.maxWidth);
                h.Add(proto.minHeight);
                h.Add(proto.maxHeight);
                h.Add(proto.noiseSeed);
                h.Add(proto.noiseSpread);
                h.Add(proto.density);
                h.Add(proto.useDensityScaling);
                h.Add(proto.holeEdgePadding);
                h.Add(proto.alignToGround);
                h.Add(proto.positionJitter);
                h.Add(proto.usePrototypeMesh);
                h.Add(proto.prototype != null ? GPUBatcherHelpers.IdentityHash(proto.prototype) : 0);
                h.Add(proto.prototypeTexture != null ? GPUBatcherHelpers.IdentityHash(proto.prototypeTexture) : 0);
            }

            // expensive renderer hash, only recomputed when _rendererHashDirty is set
            if (_rendererHashDirty)
            {
                var rh = new System.HashCode();
                for (int i = 0; i < _cachedDetailPrototypes.Length; i++)
                {
                    var proto = _cachedDetailPrototypes[i];
                    GameObject prefabToHash = proto.prototype;
                    if (prefabToHash != null
                        && prefabToHash.TryGetComponent<GPUBatcherTerrainDetailOverride>(out var detailOverride)
                        && detailOverride.enabled
                        && detailOverride.overridePrefab != null)
                    {
                        prefabToHash = detailOverride.overridePrefab;
                    }
                    if (prefabToHash != null)
                        HashPrefabRenderers(ref rh, prefabToHash);
                }
                _detailCachedRendererHash = rh.ToHashCode();
                _rendererHashDirty = false;
            }

            return h.ToHashCode() ^ _detailCachedRendererHash;
        }

        // hash a prefab's renderer meshes and materials for change detection
        private static void HashPrefabRenderers(ref System.HashCode h, GameObject prefab)
        {
            var lodGroup = prefab.GetComponent<LODGroup>();
            if (lodGroup != null)
            {
                var lods = lodGroup.GetLODs();
                h.Add(lods.Length);
                foreach (var lod in lods)
                {
                    foreach (var renderer in lod.renderers)
                    {
                        if (renderer == null) continue;
                        if (!renderer.enabled) continue;
                        var mf = renderer.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                            h.Add(GPUBatcherHelpers.IdentityHash(mf.sharedMesh));

                        var mats = renderer.sharedMaterials;
                        h.Add(mats.Length);
                        foreach (var mat in mats)
                        {
                            if (mat == null) continue;
                            h.Add(GPUBatcherHelpers.IdentityHash(mat));
                            h.Add(mat.ComputeCRC());
                        }
                    }
                }
            }
            else
            {
                // no LODGroup - hash direct renderer
                var mf = prefab.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                    h.Add(GPUBatcherHelpers.IdentityHash(mf.sharedMesh));

                var renderer = prefab.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var mats = renderer.sharedMaterials;
                    h.Add(mats.Length);
                    foreach (var mat in mats)
                    {
                        if (mat == null) continue;
                        h.Add(GPUBatcherHelpers.IdentityHash(mat));
                        h.Add(mat.ComputeCRC());
                    }
                }
            }
        }

        // checks if a patch changed by comparing terrain API data against _cachedDetailLayers
        private bool HasPatchChanged(Vector2Int patchCoord, int layerCount)
        {
            if (!_detailTerrainDataCached || !_cachedDetailLayers.IsCreated)
                return false;

            int startX = patchCoord.x * _detailResolutionPerPatch;
            int startY = patchCoord.y * _detailResolutionPerPatch;
            int res = _detailResolutionPerPatch;
            int cellsPerLayer = _detailResolution * _detailResolution;

            for (int layer = 0; layer < layerCount && layer < _cachedDetailLayerCount; layer++)
            {
                var current = _terrainData.GetDetailLayer(startX, startY, res, res, layer);
                int baseOffset = layer * cellsPerLayer;

                for (int y = 0; y < res; y++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        int cachedValue = _cachedDetailLayers[baseOffset + (startY + y) * _detailResolution + (startX + x)];
                        if (current[y, x] != cachedValue)
                            return true;
                    }
                }
            }

            return false;
        }

        // incrementally updates _cachedDetailLayers for a single patch from terrain API
        private void UpdateCachedPatchLayers(Vector2Int patchCoord, int layerCount)
        {
            int startX = patchCoord.x * _detailResolutionPerPatch;
            int startY = patchCoord.y * _detailResolutionPerPatch;
            int res = _detailResolutionPerPatch;
            int cellsPerLayer = _detailResolution * _detailResolution;

            for (int layer = 0; layer < layerCount && layer < _cachedDetailLayerCount; layer++)
            {
                var current = _terrainData.GetDetailLayer(startX, startY, res, res, layer);
                int baseOffset = layer * cellsPerLayer;

                for (int y = 0; y < res; y++)
                    for (int x = 0; x < res; x++)
                        _cachedDetailLayers[baseOffset + (startY + y) * _detailResolution + (startX + x)] = (byte)current[y, x];
            }
        }

        private void UpdateDetailStateCache()
        {
            if (_terrainData == null) return;
            if (_cachedDetailPrototypes == null) return;

            _lastKnownDetailPrototypeCount = _cachedDetailPrototypes.Length;
            _lastKnownDetailDistance = _terrain != null ? _terrain.detailObjectDistance : 0f;
            _lastKnownDetailDensity = _terrain != null ? _terrain.detailObjectDensity : 1f;
            _detailPrototypePropertiesHash = ComputePrototypePropertiesHash();
        }

        // force-completes any pending job for the given detail chunk, returns true if one was found
        private bool ForceCompletePendingJob(Vector2Int detailChunkCoord, bool uploadResults)
        {
            if (!_pendingCoords.Contains(detailChunkCoord))
                return false;

            for (int i = _pendingJobs.Count - 1; i >= 0; i--)
            {
                if (_pendingJobs[i].coord == detailChunkCoord)
                {
                    var pending = _pendingJobs[i];
                    pending.handle.Complete();

                    if (uploadResults)
                        CompleteChunkJob(pending);
                    else
                        ReleaseJobBuffers(pending);

                    _pendingJobs.RemoveAt(i);
                    _pendingCoords.Remove(detailChunkCoord);
                    return true;
                }
            }

            return false;
        }

        // reloads a detail chunk: releases, re-extracts all patches, uploads
        private void ReloadChunk(Vector2Int detailChunkCoord)
        {
#if DEBUG
            if (_enableDebugLogging && _pendingCoords.Contains(detailChunkCoord))
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: ReloadChunk - force-completing pending job for detail chunk ({detailChunkCoord.x},{detailChunkCoord.y})");
#endif
            ForceCompletePendingJob(detailChunkCoord, uploadResults: false);
            ReleaseDetailChunk(detailChunkCoord);

#if DEBUG
            if (_enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Details]: ReloadChunk ({detailChunkCoord.x},{detailChunkCoord.y}), useUnityTransforms={_useUnityTransforms}");
#endif

            if (_useUnityTransforms)
            {
                LoadDetailChunkUnity(detailChunkCoord);
            }
            else
            {
                ScheduleChunkJob(detailChunkCoord);
                ForceCompletePendingJob(detailChunkCoord, uploadResults: true);
            }
        }

        #endregion

        #region profiler markers




        #endregion

        #region detail editor update

        private const float k_updateInterval = 0.02f; // 50Hz throttle for editor streaming/change detection

        private double _editorLastUpdateTime;

        private void DoDetailEditorUpdate()
        {
            if (this == null || !enabled || GPUBatcherSystem == null)
                return;

            // distance is on Terrain component, not TerrainData - OnTerrainChanged won't catch it
            CheckDistanceChanges();
            CheckForDetailChanges();
            ProcessPatchQueues();
        }

        // polls Terrain.treeDistance and detailObjectDistance
        private void CheckDistanceChanges()
        {
            if (_terrain == null) return;

            float currentTreeDistance = _terrain.treeDistance;
            if (_treesRegistered && !GPUBatcherBurstHelpers.Approximately(currentTreeDistance, _lastKnownTreeDistance))
            {
                _lastKnownTreeDistance = currentTreeDistance;
#if DEBUG
                if (_enableDebugLogging)
                    Debug.Log($"TerrainGPUBatcherRegisterer: tree distance changed ({currentTreeDistance}m), refreshing...");
#endif
                Refresh();
                return;
            }

            float currentDetailDistance = _terrain.detailObjectDistance;
            if (!GPUBatcherBurstHelpers.Approximately(currentDetailDistance, _lastKnownDetailDistance))
            {
                _lastKnownDetailDistance = currentDetailDistance;
                _detailDistance = currentDetailDistance;
#if DEBUG
                if (_enableDebugLogging)
                    Debug.Log($"TerrainGPUBatcherRegisterer: detail distance changed ({currentDetailDistance}m), refreshing...");
#endif
                Refresh();
                return;
            }

            float currentDetailDensity = _terrain.detailObjectDensity;
            if (!GPUBatcherBurstHelpers.Approximately(currentDetailDensity, _lastKnownDetailDensity))
            {
                _lastKnownDetailDensity = currentDetailDensity;
#if DEBUG
                if (_enableDebugLogging)
                    Debug.Log($"TerrainGPUBatcherRegisterer: detail density changed ({currentDetailDensity:F3}), refreshing...");
#endif
                Refresh();
            }
        }

        #endregion

        #region detail debug

        [FormerlySerializedAs("showDetailChunkGizmos")]
        [SerializeField] private bool _showDetailChunkGizmos = false;

        public int detailLoadedChunkCount => _loadedDetailChunks.Count;
        public int detailLoadedInstanceCount => _totalLoadedDetailInstances;
        public int detailPendingChunkCount => _loadQueue.Count + _extractQueue.Count + _pendingJobs.Count;
        public int detailAllocatedChunkCount => _allocatedDetailChunkCount;

        public void RefreshDetails() => Refresh();

        [ContextMenu("Log Status")]
        private void EditorLogStatus()
        {
            Debug.Log($"TerrainGPUBatcherRegisterer[Details]: {_loadedDetailChunks.Count} chunks, " +
                      $"{_allocatedDetailChunkCount} BRG chunks, {_totalLoadedDetailInstances} instances, " +
                      $"{_loadQueue.Count + _extractQueue.Count + _pendingJobs.Count} pending");
        }

        [ContextMenu("Force Reload All")]
        private void EditorForceReload()
        {
            if (!_registered) return;

            // use full Refresh() to ensure prototypes are unregistered before re-registration
            Refresh();

#if DEBUG
            if (_enableDebugLogging)
                Debug.Log("TerrainGPUBatcherRegisterer[Details]: Force reload triggered");
#endif
        }

        protected override void OnDrawGizmosSelected()
        {
            if (!_registered || GPUBatcherSystem == null) return;
            if (_showTreeChunkGizmos) DrawChunkBoundsGizmos(registration, _treeChunkData.Keys);
            if (_showDetailChunkGizmos) DrawChunkBoundsGizmos(registration, _detailChunkBRGChunkIds.Values);
        }

        #endregion
    }
}
#endif
