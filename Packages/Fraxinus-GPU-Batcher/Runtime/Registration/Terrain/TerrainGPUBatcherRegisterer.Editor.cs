// Copyright (c) 2026 Milk_Drinker01. All rights reserved.
// Editor-only partial: OnValidate, settings hash, editor update loop, undo/redo.

#if UNITY_EDITOR
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    public partial class TerrainGPUBatcherRegisterer
    {
        #region OnValidate

        // cached previous values for change detection
        private bool _prevDisableBuiltIn;
        private int _prevSettingsHash = int.MinValue;
        private bool _refreshScheduled;

        private int ComputeSettingsHash()
        {
            var h = new System.HashCode();
            h.Add(_treeChunkSize);
            h.Add(_requestedDetailChunkSize);
            h.Add(_useUnityTransforms);
            h.Add(_useStreaming);
            h.Add(_preservePrototypeLayers);
            h.Add(_snapCrossfadeOnStart);
            var config = GPUBatcherConfig.instance;
            if (config != null)
            {
                h.Add(config.terrainMaxUploadsPerFrame);
                h.Add(config.terrainMaxUnloadsPerFrame);
                h.Add(config.terrainMaxConcurrentJobs);
            }
            return h.ToHashCode();
        }

        private void OnValidate()
        {
            if (_treeChunkSize < 1f) _treeChunkSize = 1f;

            // handle drawTreesAndFoliage toggle immediately
            if (_terrain != null)
            {
                bool shouldDisable = _disableBuiltInRendering && enabled;
                if (shouldDisable != _prevDisableBuiltIn)
                {
                    _terrain.drawTreesAndFoliage = !shouldDisable;
                    _prevDisableBuiltIn = shouldDisable;
                }
            }

            // don't refresh if not initialized yet
            if (!_registered) return;

            // check if any setting that affects registration changed
            int currentHash = ComputeSettingsHash();
            if (currentHash != _prevSettingsHash && _prevSettingsHash != int.MinValue)
            {
                _prevSettingsHash = currentHash;

                // batch changes with delayCall (OnValidate fires per-field)
                if (!_refreshScheduled)
                {
                    _refreshScheduled = true;
                    UnityEditor.EditorApplication.delayCall += () =>
                    {
                        _refreshScheduled = false;
                        if (this != null && enabled)
                        {
#if DEBUG
                            if (_enableDebugLogging)
                                Debug.Log("TerrainGPUBatcherRegisterer: Settings changed, refreshing...");
#endif
                            Refresh();
                        }
                    };
                }
            }
            else
            {
                _prevSettingsHash = currentHash;
            }
        }

        #endregion

        #region OnTerrainChanged

        private TerrainChangedFlags _pendingTerrainFlags;

        // called by Unity when terrain data changes - just accumulates flags,
        // processed once per editor frame in EditorUpdate
        private void OnTerrainChanged(TerrainChangedFlags flags)
        {
            if (!_registered) return;
            _pendingTerrainFlags |= flags;
        }

        private void ProcessPendingTerrainChanges()
        {
            if (this == null || !enabled || !_registered) return;

            var flags = _pendingTerrainFlags;
            _pendingTerrainFlags = 0;

            if ((flags & TerrainChangedFlags.FlushEverythingImmediately) != 0)
            {
                RefreshTerrainDataCaches();
                Refresh();
                return;
            }

            if ((flags & TerrainChangedFlags.TreeInstances) != 0)
                CheckForTreeChanges();

            if ((flags & TerrainChangedFlags.RemoveDirtyDetailsImmediately) != 0)
            {
                CheckForDetailPrototypeChanges();

                // while painting, the custom painting path handles detail layer changes
                // while NOT painting, scan all loaded patches (catches procedural tools, scripts, etc.)
                bool isActuallyPainting = _wasPainting
                    && UnityEditor.Selection.activeGameObject == _terrain.gameObject;
                if (!isActuallyPainting)
                    ScanAllLoadedPatches();
            }

            // heightmap/holes edits invalidate cached NativeArrays used by detail extraction
            if ((flags & TerrainChangedFlags.Heightmap) != 0 && _detailsInitialized)
            {
                RefreshCachedHeightmap();
                ForceReloadAllLoadedChunks();
            }

            if ((flags & TerrainChangedFlags.Holes) != 0 && _detailsInitialized)
            {
                RefreshCachedHoles();
                ForceReloadAllLoadedChunks();
            }
        }

        #endregion

        #region Editor Update Loop

        private void EditorUpdate()
        {
            if (this == null || !enabled)
            {
                UnityEditor.EditorApplication.update -= EditorUpdate;
                return;
            }

            if (GPUBatcherSystem != null && GPUBatcherConfig.instance.debugDisableCulling)
                return;

            if (_pendingTerrainFlags != 0)
                ProcessPendingTerrainChanges();

            UpdatePaintingState();
            DoDetailEditorUpdate();
        }

        #endregion

        #region Public Accessors (for custom inspector)

        public bool isTreesRegistered => _treesRegistered;
        public bool isDetailsRegistered => GPUBatcherSystem != null && _detailsInitialized;
        public int treeChunkCount => _treeChunkData.Count;
        public int treeInstanceCount => _totalTreeInstances;
        public float treeCullDistance => _terrain != null ? _terrain.treeDistance : 0f;

        #endregion

        #region Static Editor Initialization

        [UnityEditor.InitializeOnLoadMethod]
        private static void EditorStaticInitialize()
        {
            UnityEditor.Undo.undoRedoPerformed -= OnGlobalUndoRedo;
            UnityEditor.Undo.undoRedoPerformed += OnGlobalUndoRedo;
        }

        private static void OnGlobalUndoRedo()
        {
            foreach (var registerer in s_activeRegisterers)
            {
                if (registerer != null && registerer.enabled)
                {
                    registerer.CheckForTreeChanges();
                    registerer.CheckForDetailPrototypeChanges();
                    registerer.ScanAllLoadedPatches();
                }
            }
        }

        #endregion
    }
}
#endif
