// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace Fraxinus.GPUBatcher
{
    /// <summary>registers trees and details with the Fraxinus.GPUBatcher. Attach to a Terrain GameObject.</summary>
    [RequireComponent(typeof(Terrain))]
    public partial class TerrainGPUBatcherRegisterer : GPUBatcherRegisterer
    {
        #region shared fields

        [FormerlySerializedAs("disableBuiltInRendering")]
        [Header("Settings")]
        [Tooltip("Disable Unity's built-in tree and detail rendering when BRG indirect draws are available.")]
        [SerializeField] private bool _disableBuiltInRendering = true;

        [FormerlySerializedAs("snapCrossfadeOnStart")]
        [Tooltip("Snap animated crossfade to target LOD on initialization. " +
                 "Prevents mass fade-in on scene load. Has no effect if animated crossfade is disabled on the prototypes.")]
        [SerializeField] private bool _snapCrossfadeOnStart = true;

#if DEBUG
        [FormerlySerializedAs("enableDebugLogging")]
        [SerializeField] private bool _enableDebugLogging = false;
#endif

        [FormerlySerializedAs("detailChunkSize")]
        [Header("Detail Chunks")]
        [Tooltip("World-space size of detail chunks in meters. Larger chunks = fewer draw calls but coarser culling. Will be snapped to a multiple of Unity's patch size.")]
        [SerializeField] private float _requestedDetailChunkSize = 32f;

        private Terrain _terrain;
        private TerrainData _terrainData;
        private bool _ownsFoliageRendering;

        public bool canUseIndirectRendering => GPUBatcherRenderer.canSubmitDraws &&
            (!Rendering.D3D11SourceSupport.requiresValidation || SupportsD3D11Registration());

        protected override bool SupportsD3D11Registration()
        {
            if (!Rendering.D3D11SourceSupport.SupportsCameraPasses) return false;
            var terrain = GetComponent<Terrain>();
            if (!terrain || !terrain.terrainData) return false;
            foreach (var tree in terrain.terrainData.treePrototypes)
                if (tree.prefab && !Rendering.D3D11SourceSupport.Supports(tree.prefab)) return false;
            foreach (var detail in terrain.terrainData.detailPrototypes)
            {
                if (!detail.usePrototypeMesh || !detail.prototype) return false;
                var prefab = detail.prototype;
                if (prefab.TryGetComponent<GPUBatcherTerrainDetailOverride>(out var replacement) &&
                    replacement.enabled && replacement.overridePrefab)
                    prefab = replacement.overridePrefab;
                if (!Rendering.D3D11SourceSupport.Supports(prefab)) return false;
            }
            return true;
        }

        #endregion

        #region cache management

        private void RefreshTerrainDataCaches()
        {
            if (_terrain == null || _terrain.terrainData == null) return;

            var terrainData = _terrain.terrainData;

            _cachedDetailPrototypes = terrainData.detailPrototypes;
            _cachedTreeInstances = terrainData.treeInstances;
        }

        #endregion

        private static readonly HashSet<TerrainGPUBatcherRegisterer> s_activeRegisterers = new();

        // global streaming budget, shared across all terrains per frame
        private static uint s_budgetResetFrame;
        private static int s_extractBudget;
        private static int s_unloadBudget;
        private static int s_pendingJobCount; // total in-flight jobs across all terrains

        #region lifecycle

        protected override void OnEnable()
        {
            s_activeRegisterers.Add(this);
#if UNITY_EDITOR
            if (s_activeRegisterers.Count == 1)
                GPUBatcherTerrainDetailOverride.onAnyOverrideChanged += OnAnyDetailOverrideChanged;
#endif
            _terrain = GetComponent<Terrain>();
            base.OnEnable(); // triggers Initialize -> OnInitialize, or deferred in editor
        }

        protected override void OnInitialize()
        {
            RefreshTerrainDataCaches();
            InitializeTrees();
            InitializeDetails();
            SetFoliageTakeover(_disableBuiltInRendering);

            if (_snapCrossfadeOnStart)
                GPUBatcherSystem.RequestSnapAllViews();

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.update -= EditorUpdate;
                UnityEditor.EditorApplication.update += EditorUpdate;
                UnityEditor.SceneView.duringSceneGui -= OnSceneGUI;
                UnityEditor.SceneView.duringSceneGui += OnSceneGUI;
            }
#endif
        }

        protected override void OnShutdown()
        {
            SetFoliageTakeover(false);
            ShutdownDetails();

            ShutdownTrees();
        }

        protected override void OnDisable()
        {
            s_activeRegisterers.Remove(this);
#if UNITY_EDITOR
            if (s_activeRegisterers.Count == 0)
                GPUBatcherTerrainDetailOverride.onAnyOverrideChanged -= OnAnyDetailOverrideChanged;
            UnityEditor.EditorApplication.update -= EditorUpdate;
            UnityEditor.SceneView.duringSceneGui -= OnSceneGUI;
#endif
            base.OnDisable();
        }

        private void SetFoliageTakeover(bool requested)
        {
            if (_terrain == null) return;
            if (requested && GPUBatcherRenderer.canSubmitDraws)
            {
                if (!_terrain.drawTreesAndFoliage) return;
                _terrain.drawTreesAndFoliage = false;
                _ownsFoliageRendering = true;
            }
            else if (_ownsFoliageRendering)
            {
                if (gameObject.scene.isLoaded) _terrain.drawTreesAndFoliage = true;
                _ownsFoliageRendering = false;
            }
        }

        #endregion

        #region combined public API

        public void Refresh()
        {
            Shutdown();
            if (isActiveAndEnabled)
                Initialize();
        }

        // trees-only refresh - avoids tearing down and rebuilding detail streaming state
        private void RefreshTrees()
        {
            if (GPUBatcherSystem == null) return;
            ShutdownTrees();
            RefreshTerrainDataCaches();
            InitializeTrees();
        }

        // returns null if trees aren't registered yet
        public InstanceLink[] GetTreeInstanceLinks()
        {
            if (!_treesRegistered) return null;
            var all = new List<InstanceLink>();
            foreach (var kvp in _treeChunkData)
            {
                var chunkLink = new ChunkLink(kvp.Key);
                int count = kvp.Value.instanceCount;
                for (int i = 0; i < count; i++)
                {
                    var link = GetInstanceLink(chunkLink, i);
                    if (link.isValid) all.Add(link);
                }
            }
            return all.Count > 0 ? all.ToArray() : null;
        }

        protected override void OnChunkWritten(int chunkId)
        {
            if (!_treeChunkData.ContainsKey(chunkId)) return;
            OnTreeChunkWrittenInternal(chunkId);
        }

        #endregion

        #region static initialization

        // dispose static NativeCollections before domain reload to prevent leaks
        internal static void DisposeStaticNativeCollections()
        {
            if (s_targetChunkBits.IsCreated) s_targetChunkBits.Dispose();
            if (s_syncBoundsMinMax.IsCreated) s_syncBoundsMinMax.Dispose();
            DisposeAllJobBuffers();
        }

        // clear static caches on subsystem registration
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RuntimeStaticInitialize()
        {
#if UNITY_EDITOR
            GPUBatcherTerrainDetailOverride.onAnyOverrideChanged -= OnAnyDetailOverrideChanged;
#endif
            s_activeRegisterers.Clear();
            s_budgetResetFrame = 0;
            s_extractBudget = 0;
            s_unloadBudget = 0;
            s_pendingJobCount = 0;
            s_treeRenderDistance = float.MaxValue;
            s_detailRenderDistance = float.MaxValue;
            DisposeStaticNativeCollections();
            s_chunkSortBuffer.Clear();
        }

#if UNITY_EDITOR
        private static void OnAnyDetailOverrideChanged()
        {
            foreach (var reg in s_activeRegisterers)
            {
                if (reg == null) continue;
                reg._rendererHashDirty = true;
                reg.CheckForDetailPrototypeChanges();
            }
        }
#endif

        #endregion
    }
}
