// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fraxinus.GPUBatcher
{
    public enum ChunkMode
    {
        /// <summary>Single chunk with tight encapsulated bounds. Best for small groups of static instances.</summary>
        [InspectorName("Static (Single Chunk)")]
        StaticSingleChunk,
        /// <summary>Tight encapsulated bounds per spatial chunk. Best for large groups of static instances.</summary>
        [InspectorName("Static (Multi Chunk)")]
        Static,
        /// <summary>Single chunk with world-sized bounds. Best for instances that move frequently.</summary>
        [InspectorName("Dynamic Global Chunk")]
        DynamicGlobalChunk,
    }

    /// <summary>walks child hierarchies and registers each MeshRenderer/LODGroup as a separate BRG instance.</summary>
    [AddComponentMenu("Fraxinus GPU Batcher/GPU Batcher GameObject Group")]
    [ExecuteAlways]
    public class GPUBatcherGameObjectGroup : GPUBatcherRegisterer
    {
        [Serializable]
        struct CachedChunk
        {
            public InstanceData[] instances;
            public Bounds bounds;
            public int[] sourceObjectIndices; // maps each chunk instance -> index in _sourceObjects
        }

#if UNITY_EDITOR
        [SerializeField] ChunkMode _chunkMode = ChunkMode.StaticSingleChunk;
#endif
        [SerializeField, Min(10f)] float _chunkSize = 256f;
        [SerializeField, Tooltip("Max render distance per chunk (0 = no limit)")]
        float _cullDistance = 0f;
#if UNITY_EDITOR
        [SerializeField, Tooltip("When enabled, instances with different property overrides (e.g. crossfade, shadow settings) " +
            "will register as separate prototypes. When disabled, all instances of the same prefab share one prototype.")]
        bool _detectPrefabOverrides = false;
#endif
        [SerializeField] List<GameObject> _sourceObjects = new(); // individual renderer/LODGroup GOs
        [SerializeField] List<int> _sourceProtoIndices = new(); // parallel to _sourceObjects
        [SerializeField] List<GameObject> _prototypeSources = new(); // one representative GO per unique prototype
        [SerializeField, HideInInspector] CachedChunk[] _cachedChunks;
        [SerializeField] bool _converted;

        // runtime state (rebuilt on OnEnable from cached data)
        int[] _globalProtoIndices;
        long[] _prototypeSourceHashes; // parallel to _prototypeSources, resolves runtime clones to local proto
        Dictionary<int, int> _chunkIdToCachedIndex; // chunkId -> index in _cachedChunks
        ChunkLink _primaryChunk = ChunkLink.invalid; // first allocated chunk, used for AddInstance on clones
        readonly HashSet<GPUBatcherGameObjectLink> _trackedLinks = new(); // every wired link this group owns, cleared on Shutdown

        public bool isConverted => _converted;
        public int chunkCount => _cachedChunks?.Length ?? 0;
        public int prototypeCount => _prototypeSources.Count;
        public IReadOnlyList<GameObject> prototypeSources => _prototypeSources;
        public float chunkSize { get => _chunkSize; set => _chunkSize = value; }

        public int GetGlobalProtoIndex(int localIndex)
        {
            if (_globalProtoIndices == null || localIndex < 0 || localIndex >= _globalProtoIndices.Length)
                return -1;
            return _globalProtoIndices[localIndex];
        }

        public int instanceCount
        {
            get
            {
                if (_cachedChunks == null) return 0;
                int c = 0;
                foreach (var ch in _cachedChunks)
                    c += ch.instances?.Length ?? 0;
                return c;
            }
        }

        protected override void OnEnable()
        {
            if (!_converted || _cachedChunks == null || _cachedChunks.Length == 0) return;
            base.OnEnable();
        }

        protected override void OnInitialize()
        {
            if (!_converted || _cachedChunks == null || _cachedChunks.Length == 0) return;

            // forceRenderingOff isn't serialized - re-apply each init
            SetSourceRenderersHidden(true);

            if (_globalProtoIndices == null || _globalProtoIndices.Length != _prototypeSources.Count)
                _globalProtoIndices = new int[_prototypeSources.Count];
            if (_prototypeSourceHashes == null || _prototypeSourceHashes.Length != _prototypeSources.Count)
                _prototypeSourceHashes = new long[_prototypeSources.Count];
            for (int i = 0; i < _prototypeSources.Count; i++)
            {
                var src = _prototypeSources[i];
                _prototypeSourceHashes[i] = ComputeSimpleProtoHash(src);
                if (src == null) { _globalProtoIndices[i] = -1; continue; }

                var mf = src.GetComponentInChildren<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                {
                    Debug.LogWarning($"GPUBatcherGameObjectGroup: Prototype source '{src.name}' has no mesh, skipping");
                    _globalProtoIndices[i] = -1;
                    continue;
                }

                _globalProtoIndices[i] = RegisterRenderSignatureFromPrefab(src);
            }

            UploadCachedChunks();

            GPUBatcherSystem.RequestSnapAllViews();
        }

        // write GPU chunks from _cachedChunks; prototypes must already be registered
        void UploadCachedChunks()
        {
            _chunkIdToCachedIndex = new Dictionary<int, int>(_cachedChunks.Length);

            for (int c = 0; c < _cachedChunks.Length; c++)
            {
                var cached = _cachedChunks[c];
                if (cached.instances == null || cached.instances.Length == 0) continue;

                var link = AllocChunk();
                if (!link.isValid)
                {
                    Debug.LogWarning($"GPUBatcherGameObjectGroup: Failed to allocate chunk, {cached.instances.Length} instances dropped");
                    continue;
                }

                if (!_primaryChunk.isValid)
                    _primaryChunk = link;

                _chunkIdToCachedIndex[link.id] = c;

                // remap local prototype indices to global
                var instances = new NativeArray<InstanceData>(cached.instances.Length, Allocator.TempJob);
                for (int i = 0; i < cached.instances.Length; i++)
                {
                    var inst = cached.instances[i];
                    int localProto = inst.signatureIndex;
                    inst.signatureIndex = (localProto >= 0 && localProto < _globalProtoIndices.Length)
                        ? _globalProtoIndices[localProto] : -1;
                    instances[i] = inst;
                }

                WriteChunk(link, instances, cached.bounds, _cullDistance);
                instances.Dispose();
            }
        }

        protected override void OnShutdown()
        {
            // restore source renderers unless they're being torn down with the scene (play-mode unload/quit)
            if (!Application.isPlaying || gameObject.scene.isLoaded)
                SetSourceRenderersHidden(false);

            foreach (var link in _trackedLinks)
                if (link != null) link.instanceLink = InstanceLink.invalid;
            _trackedLinks.Clear();

            _globalProtoIndices = null;
            _prototypeSourceHashes = null;
            _chunkIdToCachedIndex = null;
            _primaryChunk = ChunkLink.invalid;
        }

        internal void RegisterClonedInstance(GPUBatcherGameObjectLink link)
        {
            if (!_registered || !_primaryChunk.isValid) return;

            // skip existing source objects (fires after domain reload when _instanceLink resets)
            if (_sourceObjects.Contains(link.gameObject)) return;

            int globalProto = ResolveCloneGlobalProto(link.gameObject);
            if (globalProto < 0)
            {
                Debug.LogWarning($"GPUBatcherGameObjectGroup: cloned instance '{link.gameObject.name}' did not match any registered prototype, skipping");
                return;
            }

            var t = link.transform;
            var instanceData = new InstanceData
            {
                position = t.position,
                rotation = t.rotation,
                scale = t.lossyScale,
                signatureIndex = globalProto
            };

            var instanceLink = AddInstance(_primaryChunk, instanceData);
            if (instanceLink.isValid)
            {
                link.instanceLink = instanceLink;
                link.group = this;
                link.globalProtoIndex = globalProto; // refresh stale serialized value to current session
                _trackedLinks.Add(link);
#if UNITY_EDITOR
                GPUBatcherSystem.SetEntityId(instanceLink.globalBRGSlotIndex,
                    GetPickingId(link.gameObject),
                    globalProto, link.transform);
#endif
            }
        }

        int ResolveCloneGlobalProto(GameObject clone)
        {
            if (_prototypeSourceHashes == null || _globalProtoIndices == null) return -1;
            long hash = ComputeSimpleProtoHash(clone);
            if (hash == 0) return -1;
            for (int i = 0; i < _prototypeSourceHashes.Length; i++)
            {
                if (_prototypeSourceHashes[i] != hash) continue;
                if (i >= _globalProtoIndices.Length) return -1;
                return _globalProtoIndices[i];
            }
            return -1;
        }

        static long ComputeSimpleProtoHash(GameObject go)
        {
            if (go == null) return 0;
            var h = new Hash64();
            if (go.TryGetComponent<LODGroup>(out var lodGroup))
            {
                var lods = lodGroup.GetLODs();
                for (int i = 0; i < lods.Length; i++)
                {
                    var renderers = lods[i].renderers;
                    if (renderers == null || renderers.Length == 0) { h.Add(0); continue; }
                    var mr = renderers[0] as MeshRenderer;
                    if (mr == null) { h.Add(0); continue; }
                    h.Add(mr.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null ? GPUBatcherHelpers.IdentityHash(mf.sharedMesh) : 0);
                    var mats = mr.sharedMaterials;
                    if (mats != null)
                        for (int m = 0; m < mats.Length; m++)
                            h.Add(mats[m] != null ? GPUBatcherHelpers.IdentityHash(mats[m]) : 0);
                }
            }
            else
            {
                h.Add(go.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null ? GPUBatcherHelpers.IdentityHash(mf.sharedMesh) : 0);
                if (go.TryGetComponent<MeshRenderer>(out var mr))
                {
                    var mats = mr.sharedMaterials;
                    if (mats != null)
                        for (int m = 0; m < mats.Length; m++)
                            h.Add(mats[m] != null ? GPUBatcherHelpers.IdentityHash(mats[m]) : 0);
                }
            }
            return h.ToHashCode();
        }

        protected override void OnChunkWritten(int chunkId)
        {
            if (_chunkIdToCachedIndex == null || !_chunkIdToCachedIndex.TryGetValue(chunkId, out int cachedIdx))
                return;

            var chunkLink = new ChunkLink(chunkId);
            var sourceIndices = _cachedChunks[cachedIdx].sourceObjectIndices;
            if (sourceIndices == null) return;

            for (int i = 0; i < sourceIndices.Length; i++)
            {
                int srcIdx = sourceIndices[i];
                if (srcIdx < 0 || srcIdx >= _sourceObjects.Count) continue;

                var go = _sourceObjects[srcIdx];
                if (go == null)
                {
                    // dead source on a build re-upload: disable the re-written ghost slot
                    var deadLink = GetInstanceLink(chunkLink, i);
                    if (deadLink.isValid)
                        registration.SetEnabledUnsafe(deadLink, false);
                    continue;
                }

                var goLink = go.GetComponent<GPUBatcherGameObjectLink>();
                if (goLink != null)
                {
                    var link = GetInstanceLink(chunkLink, i);
                    goLink.instanceLink = link;

                    int srcProtoIdx = (srcIdx < _sourceProtoIndices.Count) ? _sourceProtoIndices[srcIdx] : -1;
                    goLink.globalProtoIndex = (srcProtoIdx >= 0 && srcProtoIdx < _globalProtoIndices.Length)
                        ? _globalProtoIndices[srcProtoIdx] : -1;

                    if (!link.isValid) continue;

                    _trackedLinks.Add(goLink);

                    if (!goLink.shouldRender)
                        registration.SetEnabledUnsafe(link, false);

#if UNITY_EDITOR
                    GPUBatcherSystem.SetEntityId(link.globalBRGSlotIndex,
                        GetPickingId(go),
                        goLink.globalProtoIndex, go.transform);
#endif
                }
            }
        }

        void SetSourceRenderersHidden(bool hidden)
        {
            foreach (var go in _sourceObjects)
            {
                if (go == null) continue;
                HideSourceRenderers(go, hidden);
            }
        }

        void HideSourceRenderers(GameObject go, bool hidden)
        {
            var lodGroup = go.GetComponent<LODGroup>();
            if (lodGroup != null)
            {
                // forceRenderingOff hides the renderers; disable the LODGroup itself only at runtime to skip
                // its per-frame LOD work. play/build changes aren't serialized, so no prefab override is written.
                if (!hidden) lodGroup.enabled = true; // restore + clear any legacy enabled=false override
                else if (Application.isPlaying) lodGroup.enabled = false;
                var lods = lodGroup.GetLODs();
                foreach (var lod in lods)
                {
                    if (lod.renderers == null) continue;
                    foreach (var r in lod.renderers)
                        if (r != null) HideRenderer(r, hidden);
                }
                return;
            }

            if (go.TryGetComponent<MeshRenderer>(out var mr))
                HideRenderer(mr, hidden);
        }

        static void HideRenderer(Renderer r, bool hidden)
        {
            r.forceRenderingOff = hidden;
            if (!hidden) r.enabled = true; // restore + clear any legacy enabled=false override
        }

        internal void NotifyInstanceDestroyed(GPUBatcherGameObjectLink link)
        {
            if (!_converted) return;

            // shifting _sourceObjects only makes sense paired with the BuildCache rebuild below
#if UNITY_EDITOR
            var go = link.gameObject;
            int idx = _sourceObjects.IndexOf(go);
            if (idx >= 0)
            {
                _sourceObjects.RemoveAt(idx);
                _sourceProtoIndices.RemoveAt(idx);
                _chunkIdToCachedIndex?.Clear();
            }

            // schedule rebuild - multiple destroys in same frame coalesce into one
            if (!_pendingRebuild)
            {
                _pendingRebuild = true;
                EditorApplication.delayCall += () =>
                {
                    _pendingRebuild = false;
                    if (this == null || !_converted) return;

                    if (_sourceObjects.Count == 0)
                    {
                        Shutdown();
                        _cachedChunks = null;
                        _converted = false;
                        EditorUtility.SetDirty(this);
                        return;
                    }

                    Shutdown();
                    BuildCache();
                    EditorUtility.SetDirty(this);
                    if (isActiveAndEnabled) Initialize();
                };
            }
#endif
        }

#if UNITY_EDITOR
        // called by GPUBatcherGameObjectLink when mesh/material changes in editor
        internal void NotifySourceChanged()
        {
            if (!_converted)
                return;
            RebuildPrototypes();
        }

        // called by GPUBatcherGameObjectLink when a clone is created in editor
        bool _pendingCloneRebuild;

        internal void NotifyCloneCreated(GPUBatcherGameObjectLink link)
        {
            if (!_converted) return;

            var go = link.gameObject;
            if (_sourceObjects.Contains(go)) return;

            _sourceObjects.Add(go);
            _sourceProtoIndices.Add(0); // placeholder, RebuildPrototypes fixes it
            link.group = this;

            if (!_pendingCloneRebuild)
            {
                _pendingCloneRebuild = true;
                EditorApplication.delayCall += () =>
                {
                    _pendingCloneRebuild = false;
                    if (this == null || !_converted) return;
                    RebuildPrototypes();
                };
            }
        }

        // rebuild prototype dedup from current source objects (picks up mesh/material changes)
        void RebuildPrototypes()
        {
            Shutdown();
            CleanNullSourceObjects();

            bool changed = false;
            int oldProtoCount = _prototypeSources.Count;
            _prototypeSources.Clear();
            var keyToLocalIndex = new Dictionary<long, int>();

            for (int i = 0; i < _sourceObjects.Count; i++)
            {
                var go = _sourceObjects[i];
                var key = ComputeSourceSignatureKey(go);
                if (!keyToLocalIndex.TryGetValue(key, out int localIndex))
                {
                    localIndex = _prototypeSources.Count;
                    _prototypeSources.Add(_detectPrefabOverrides ? go : (GetPrefabSource(go) ?? go));
                    keyToLocalIndex[key] = localIndex;
                }
                if (_sourceProtoIndices[i] != localIndex)
                {
                    _sourceProtoIndices[i] = localIndex;
                    changed = true;
                }
            }

            if (_prototypeSources.Count != oldProtoCount)
                changed = true;

            BuildCache();
            if (changed) EditorUtility.SetDirty(this);
            if (isActiveAndEnabled) Initialize();
        }
#endif

        #region editor-only: conversion, cache building, revert

#if UNITY_EDITOR
        float _prevChunkSize;
        float _prevCullDistance;
        ChunkMode _prevChunkMode;
        bool _prevDetectPrefabOverrides;
        bool _prevFieldsInitialized;
        bool _pendingRebuild;
        bool _rebuildNeedsProtos; // pending rebuild changed the prototype set (overrides toggle)
        double _rebuildTime;
        bool _pendingReregister;

        void OnValidate()
        {
            if (!_prevFieldsInitialized)
            {
                _prevChunkSize = _chunkSize;
                _prevCullDistance = _cullDistance;
                _prevChunkMode = _chunkMode;
                _prevDetectPrefabOverrides = _detectPrefabOverrides;
                _prevFieldsInitialized = true;
            }

            if (!_converted)
            {
                _prevChunkSize = _chunkSize;
                _prevCullDistance = _cullDistance;
                _prevChunkMode = _chunkMode;
                _prevDetectPrefabOverrides = _detectPrefabOverrides;
                return;
            }

            bool chunkModeChanged = _chunkMode != _prevChunkMode;
            bool chunkSizeChanged = _prevChunkSize > 0 && !GPUBatcherBurstHelpers.Approximately(_chunkSize, _prevChunkSize);
            bool cullDistChanged = !GPUBatcherBurstHelpers.Approximately(_cullDistance, _prevCullDistance);
            bool overridesChanged = _detectPrefabOverrides != _prevDetectPrefabOverrides;

            _prevChunkSize = _chunkSize;
            _prevCullDistance = _cullDistance;
            _prevChunkMode = _chunkMode;
            _prevDetectPrefabOverrides = _detectPrefabOverrides;

            if (chunkModeChanged || chunkSizeChanged || overridesChanged)
            {
                // only an overrides change alters the prototype set; size/mode just regroup
                if (overridesChanged) _rebuildNeedsProtos = true;
                _rebuildTime = EditorApplication.timeSinceStartup + 0.3;
                if (!_pendingRebuild)
                {
                    _pendingRebuild = true;
                    EditorApplication.update += DebouncedRebuild;
                }
            }
            else if (cullDistChanged && _registered && !_pendingReregister)
            {
                _pendingReregister = true;
                EditorApplication.delayCall += () =>
                {
                    _pendingReregister = false;
                    if (this == null || !_converted) return;
                    Shutdown();
                    if (isActiveAndEnabled) Initialize();
                };
            }
        }

        void DebouncedRebuild()
        {
            if (EditorApplication.timeSinceStartup < _rebuildTime) return;
            EditorApplication.update -= DebouncedRebuild;
            _pendingRebuild = false;
            bool needsProtos = _rebuildNeedsProtos;
            _rebuildNeedsProtos = false;
            if (this == null || !_converted) return;

            // fast path: size/mode changed but prototypes didn't - re-chunk without the full rebuild
            if (!needsProtos && _registered && _sourceObjects.Count > 0
                && _globalProtoIndices != null && _globalProtoIndices.Length == _prototypeSources.Count)
            {
                RechunkInPlace();
                EditorUtility.SetDirty(this);
                return;
            }

            Shutdown();
            BuildCache();
            if (_sourceObjects.Count == 0)
            {
                _cachedChunks = null;
                _converted = false;
                EditorUtility.SetDirty(this);
                return;
            }
            EditorUtility.SetDirty(this);
            if (isActiveAndEnabled) Initialize();
        }

        // regroup + re-upload chunks without re-registering prototypes (skips the mesh re-combine)
        void RechunkInPlace()
        {
            // detach links and release chunks; prototypes stay registered
            foreach (var link in _trackedLinks)
                if (link != null) link.instanceLink = InstanceLink.invalid;
            _trackedLinks.Clear();

            if (_chunkIdToCachedIndex != null)
            {
                foreach (var chunkId in _chunkIdToCachedIndex.Keys)
                    ReleaseChunk(new ChunkLink(chunkId));
            }
            _primaryChunk = ChunkLink.invalid;

            BuildCache();
            UploadCachedChunks(); // reassigns _chunkIdToCachedIndex

            GPUBatcherSystem.RequestSnapAllViews();
        }

        public void ConvertChildren()
        {
            if (_converted) return;

            _sourceObjects.Clear();
            _sourceProtoIndices.Clear();
            _prototypeSources.Clear();

            var keyToLocalIndex = new Dictionary<long, int>();

            foreach (Transform child in transform)
                CollectFromHierarchy(child.gameObject, keyToLocalIndex);

            if (_sourceObjects.Count == 0)
            {
                Debug.LogWarning("GPUBatcherGameObjectGroup: No valid renderers found among children");
                return;
            }

            SetSourceRenderersHidden(true);
            AddInstanceLinks();
            BuildCache();
            _converted = true;
            EditorUtility.SetDirty(this);

            if (isActiveAndEnabled) Initialize();
        }

        public void ConvertObjects(GameObject[] objects)
        {
            if (_converted) return;

            _sourceObjects.Clear();
            _sourceProtoIndices.Clear();
            _prototypeSources.Clear();

            var keyToLocalIndex = new Dictionary<long, int>();

            foreach (var go in objects)
            {
                if (go != null) CollectFromHierarchy(go, keyToLocalIndex);
            }

            if (_sourceObjects.Count == 0)
            {
                Debug.LogWarning("GPUBatcherGameObjectGroup: No valid renderers found");
                return;
            }

            SetSourceRenderersHidden(true);
            AddInstanceLinks();
            BuildCache();
            _converted = true;
            EditorUtility.SetDirty(this);

            if (isActiveAndEnabled) Initialize();
        }

        public void RevertToGameObjects()
        {
            Shutdown();
            SetSourceRenderersHidden(false);
            RemoveInstanceLinks();
            _sourceObjects.Clear();
            _sourceProtoIndices.Clear();
            _prototypeSources.Clear();
            _cachedChunks = null;
            _converted = false;
            EditorUtility.SetDirty(this);
        }

        public void Refresh()
        {
            if (!_converted) return;
            CleanNullSourceObjects();
            Shutdown();
            BuildCache();
            EditorUtility.SetDirty(this);
            if (isActiveAndEnabled) Initialize();
        }

        public void RegisterNewChildren()
        {
            if (!_converted) return;

            var existingSet = new HashSet<GameObject>(_sourceObjects);

            // rebuild dedup map from current prototype list
            var keyToLocalIndex = new Dictionary<long, int>();
            for (int i = 0; i < _prototypeSources.Count; i++)
            {
                var proto = _prototypeSources[i];
                if (proto == null) continue;
                keyToLocalIndex[ComputeSourceSignatureKey(proto)] = i;
            }

            int prevCount = _sourceObjects.Count;
            foreach (Transform child in transform)
                CollectFromHierarchy(child.gameObject, keyToLocalIndex, existingSet);

            int addedCount = _sourceObjects.Count - prevCount;
            if (addedCount == 0)
            {
                Debug.Log("GPUBatcherGameObjectGroup: No new renderers found to register.");
                return;
            }

            for (int i = prevCount; i < _sourceObjects.Count; i++)
            {
                var go = _sourceObjects[i];
                if (go == null) continue;

                HideSourceRenderers(go, true);

                var link = go.GetComponent<GPUBatcherGameObjectLink>();
                if (link == null)
                    link = go.AddComponent<GPUBatcherGameObjectLink>();
                link.group = this;
            }

            Shutdown();
            BuildCache();
            EditorUtility.SetDirty(this);
            if (isActiveAndEnabled) Initialize();

            Debug.Log($"GPUBatcherGameObjectGroup: Registered {addedCount} new renderers ({_sourceObjects.Count} total).");
        }

        // walk a root GO's hierarchy and register each LODGroup and standalone renderer as a source
        void CollectFromHierarchy(GameObject root, Dictionary<long, int> keyToLocalIndex,
            HashSet<GameObject> existingSourceObjects = null)
        {
            var existingGroup = root.GetComponentInChildren<GPUBatcherGameObjectGroup>();
            if (existingGroup != null && existingGroup != this) return;

            var lodGroups = root.GetComponentsInChildren<LODGroup>();

            // build exclusion set of renderers claimed by any LODGroup
            var excludedRenderers = new HashSet<MeshRenderer>();
            foreach (var lg in lodGroups)
            {
                var lods = lg.GetLODs();
                foreach (var lod in lods)
                    if (lod.renderers != null)
                        foreach (var r in lod.renderers)
                            if (r is MeshRenderer mr)
                                excludedRenderers.Add(mr);
            }

            // track GOs added this call to prevent duplicates (e.g. LODGroup GO also has a standalone renderer)
            var addedGOs = new HashSet<GameObject>();

            foreach (var lg in lodGroups)
            {
                var go = lg.gameObject;
                if (existingSourceObjects != null && existingSourceObjects.Contains(go)) continue;
                if (!addedGOs.Add(go)) continue;

                var key = ComputeSourceSignatureKey(go);
                if (!keyToLocalIndex.TryGetValue(key, out int localIndex))
                {
                    localIndex = _prototypeSources.Count;
                    _prototypeSources.Add(_detectPrefabOverrides ? go : (GetPrefabSource(go) ?? go));
                    keyToLocalIndex[key] = localIndex;
                }
                _sourceObjects.Add(go);
                _sourceProtoIndices.Add(localIndex);
            }

            var renderers = root.GetComponentsInChildren<MeshRenderer>();
            foreach (var mr in renderers)
            {
                if (!mr.enabled) continue;
                if (excludedRenderers.Contains(mr)) continue;

                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var go = mr.gameObject;
                if (existingSourceObjects != null && existingSourceObjects.Contains(go)) continue;
                if (!addedGOs.Add(go)) continue;

                var key = ComputeSourceSignatureKey(go);
                if (!keyToLocalIndex.TryGetValue(key, out int localIndex))
                {
                    localIndex = _prototypeSources.Count;
                    _prototypeSources.Add(_detectPrefabOverrides ? go : (GetPrefabSource(go) ?? go));
                    keyToLocalIndex[key] = localIndex;
                }
                _sourceObjects.Add(go);
                _sourceProtoIndices.Add(localIndex);
            }
        }

        // picking ID for scene view selection
#if UNITY_6000_2_OR_NEWER
        static EntityId GetPickingId(GameObject go)
        {
            var mr = go.GetComponent<MeshRenderer>();
            return mr != null ? mr.GetEntityId() : go.GetEntityId();
        }
#else
        static int GetPickingId(GameObject go)
        {
            var mr = go.GetComponent<MeshRenderer>();
            return mr != null ? mr.GetInstanceID() : go.GetInstanceID();
        }
#endif

        long ComputeSourceSignatureKey(GameObject go)
        {
            if (go == null) return 0;
            if (!_detectPrefabOverrides)
            {
                var prefab = GetPrefabSource(go) ?? go;
                return GPUBatcherHelpers.IdentityHash(prefab);
            }
            return ComputeSourceDataHash(go);
        }

        static GameObject GetPrefabSource(GameObject go)
            => PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);

        static readonly List<Mesh> s_hashMeshes = new();
        static readonly List<Matrix4x4> s_hashMatrices = new();
        static readonly List<Material> s_hashMaterials = new();

        internal static long ComputeSourceDataHash(GameObject go)
        {
            var h = new Hash64();
            h.Add(go.layer);

            var lodGroup = go.GetComponent<LODGroup>();
            if (lodGroup != null)
            {
                h.Add(lodGroup.fadeMode);
                h.Add(lodGroup.animateCrossFading);
                h.Add(lodGroup.size);
                var root = lodGroup.transform;
                var lods = lodGroup.GetLODs();
                for (int i = 0; i < lods.Length; i++)
                {
                    h.Add(lods[i].screenRelativeTransitionHeight);
                    h.Add(lods[i].fadeTransitionWidth);
                    if (lods[i].renderers == null) continue;

                    s_hashMeshes.Clear();
                    s_hashMatrices.Clear();
                    s_hashMaterials.Clear();
                    foreach (var r in lods[i].renderers)
                    {
                        if (r is not MeshRenderer mr) continue;
                        if (!mr.enabled) continue;
                        if (!mr.TryGetComponent<MeshFilter>(out var mf) || mf.sharedMesh == null) continue;
                        s_hashMeshes.Add(mf.sharedMesh);
                        s_hashMatrices.Add(GPUBatcherHelpers.LocalMatrixRelativeTo(mr.transform, root));
                        h.Add(mr.shadowCastingMode);
                        h.Add(mr.motionVectorGenerationMode);
                        h.Add(mr.renderingLayerMask);
                        foreach (var mat in mr.sharedMaterials)
                            s_hashMaterials.Add(mat);
                    }
                    h.Add(MeshCombineCache.ComputeKey(s_hashMeshes, s_hashMatrices));
                    foreach (var mat in s_hashMaterials)
                        h.Add(mat != null ? GPUBatcherHelpers.IdentityHash(mat) : 0);
                }
            }
            else
            {
                MeshRenderer mr = null;
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
                    if (r.enabled) { mr = r; break; }
                var mf = mr != null ? mr.GetComponent<MeshFilter>() : null;
                h.Add(mf?.sharedMesh != null ? GPUBatcherHelpers.IdentityHash(mf.sharedMesh) : 0);
                if (mr != null)
                {
                    h.Add(mr.shadowCastingMode);
                    h.Add(mr.motionVectorGenerationMode);
                    h.Add(mr.renderingLayerMask);
                    foreach (var mat in mr.sharedMaterials)
                        h.Add(mat != null ? GPUBatcherHelpers.IdentityHash(mat) : 0);
                }
            }

            if (go.TryGetComponent<GPUBatcherPrototypeExtraData>(out var extra) && extra.enabled)
            {
                h.Add(extra.maxShadowCascade);
                h.Add(extra.minShadowLOD);
                h.Add(extra.overrideMaxShadowDistance);
                h.Add(extra.maxShadowDistance);
                h.Add(extra.overrideMaxShadowCrossfadeCascade);
                h.Add(extra.maxShadowCrossfadeCascade);
                h.Add(extra.enableDistanceDensity);
                h.Add(extra.distanceDensityMode);
                h.Add(extra.densityStartScreenSize);
                h.Add(extra.densityStartDistance);
                h.Add(extra.densityEndDistance);
                h.Add(extra.densityMinFraction);
                h.Add(extra.densityFadeWidth);
                h.Add(extra.overrideLodFadeDuration);
                h.Add(extra.lodFadeDuration);
                h.Add(extra.prototypeGroup != null ? GPUBatcherHelpers.IdentityHash(extra.prototypeGroup) : 0);
                h.Add(extra.minScreenSize);
                h.Add(extra.minScreenSizeFadeMode);
                h.Add(extra.minScreenSizeFadeWidth);
            }

            return h.ToHashCode();
        }

        void AddInstanceLinks()
        {
            foreach (var go in _sourceObjects)
            {
                if (go == null) continue;
                var link = go.GetComponent<GPUBatcherGameObjectLink>();
                if (link == null)
                    link = go.AddComponent<GPUBatcherGameObjectLink>();
                link.group = this;
            }
        }

        void RemoveInstanceLinks()
        {
            foreach (var go in _sourceObjects)
            {
                if (go == null) continue;
                var link = go.GetComponent<GPUBatcherGameObjectLink>();
                if (link != null)
                {
                    link.group = null; // prevent OnDestroy callback from modifying _sourceObjects
                    DestroyImmediate(link);
                }
            }
        }

        void CleanNullSourceObjects()
        {
            for (int i = _sourceObjects.Count - 1; i >= 0; i--)
            {
                if (_sourceObjects[i] == null)
                {
                    _sourceObjects.RemoveAt(i);
                    _sourceProtoIndices.RemoveAt(i);
                }
            }

            // if a prototype source was deleted, promote another source with the same local index
            for (int p = 0; p < _prototypeSources.Count; p++)
            {
                if (_prototypeSources[p] != null) continue;
                for (int i = 0; i < _sourceObjects.Count; i++)
                {
                    if (_sourceProtoIndices[i] == p)
                    {
                        _prototypeSources[p] = _sourceObjects[i];
                        break;
                    }
                }
            }
        }

        void BuildCache()
        {
            CleanNullSourceObjects();

            var allInstances = new List<InstanceData>(_sourceObjects.Count);

            for (int i = 0; i < _sourceObjects.Count; i++)
            {
                var go = _sourceObjects[i];
                if (go == null) continue;

                var t = go.transform;
                allInstances.Add(new InstanceData
                {
                    position = t.position,
                    rotation = t.rotation,
                    scale = t.lossyScale,
                    signatureIndex = _sourceProtoIndices[i]
                });
            }

            if (allInstances.Count == 0)
            {
                _cachedChunks = Array.Empty<CachedChunk>();
                return;
            }

            var protoExtents = ComputePerPrototypeExtent();
            var cfg = GPUBatcherConfig.instance;
            int maxPerChunk = cfg != null ? cfg.maxPoolRefsPerChunk * GPUBatcherRenderer.instancesPerPool : MaxInstancesPerChunk;
            var chunks = new List<CachedChunk>();

            switch (_chunkMode)
            {
                case ChunkMode.StaticSingleChunk:
                {
                    var allIndices = new List<int>(allInstances.Count);
                    for (int i = 0; i < allInstances.Count; i++)
                        allIndices.Add(i);
                    BuildChunksFromIndices(allInstances, allIndices, 0, allIndices.Count,
                        protoExtents, maxPerChunk, chunks, globalBounds: false);
                    break;
                }

                case ChunkMode.DynamicGlobalChunk:
                {
                    var allIndices = new List<int>(allInstances.Count);
                    for (int i = 0; i < allInstances.Count; i++)
                        allIndices.Add(i);
                    BuildChunksFromIndices(allInstances, allIndices, 0, allIndices.Count,
                        protoExtents, maxPerChunk, chunks, globalBounds: true);
                    break;
                }

                case ChunkMode.Static:
                {
                    var chunkGroups = new Dictionary<int2, List<int>>();
                    for (int i = 0; i < allInstances.Count; i++)
                    {
                        var pos = allInstances[i].position;
                        int2 gridCoord = new int2(
                            (int)math.floor(pos.x / _chunkSize),
                            (int)math.floor(pos.z / _chunkSize)
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
                        BuildChunksFromIndices(allInstances, kvp.Value, 0, kvp.Value.Count,
                            protoExtents, maxPerChunk, chunks, globalBounds: false);
                    }
                    break;
                }

            }

            _cachedChunks = chunks.ToArray();
        }

        void BuildChunksFromIndices(List<InstanceData> allInstances, List<int> indices,
            int start, int total, float[] protoExtents, int maxPerChunk,
            List<CachedChunk> chunks, bool globalBounds)
        {
            int offset = start;
            int end = start + total;

            while (offset < end)
            {
                int count = math.min(end - offset, maxPerChunk);
                var chunkInstances = new InstanceData[count];
                var chunkSourceIndices = new int[count];

                Vector3 bMin = Vector3.positiveInfinity;
                Vector3 bMax = Vector3.negativeInfinity;

                for (int i = 0; i < count; i++)
                {
                    int srcIdx = indices[offset + i];
                    chunkInstances[i] = allInstances[srcIdx];
                    chunkSourceIndices[i] = srcIdx;
                    var inst = chunkInstances[i];
                    Vector3 pos = (Vector3)inst.position;

                    float protoExtent = (inst.signatureIndex >= 0 && inst.signatureIndex < protoExtents.Length)
                        ? protoExtents[inst.signatureIndex] : 1f;
                    float maxScale = math.cmax(math.abs(inst.scale));
                    float expand = protoExtent * maxScale;

                    bMin = Vector3.Min(bMin, pos - Vector3.one * expand);
                    bMax = Vector3.Max(bMax, pos + Vector3.one * expand);
                }

                Bounds bounds;
                if (globalBounds)
                {
                    Vector3 center = (bMin + bMax) * 0.5f;
                    bounds = new Bounds(center, Vector3.one * 100000f);
                }
                else
                {
                    bounds = new Bounds((bMin + bMax) * 0.5f, bMax - bMin);
                }

                chunks.Add(new CachedChunk
                {
                    instances = chunkInstances,
                    bounds = bounds,
                    sourceObjectIndices = chunkSourceIndices
                });

                offset += count;
            }
        }

        float[] ComputePerPrototypeExtent()
        {
            var extents = new float[_prototypeSources.Count];

            for (int i = 0; i < _prototypeSources.Count; i++)
            {
                var proto = _prototypeSources[i];
                if (proto == null) { extents[i] = 1f; continue; }

                var lodGroup = proto.GetComponent<LODGroup>();
                if (lodGroup != null)
                {
                    extents[i] = math.max(lodGroup.size * 0.5f, 1f);
                    continue;
                }

                var meshFilter = proto.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    var meshBounds = meshFilter.sharedMesh.bounds;
                    extents[i] = math.max(math.cmax((float3)meshBounds.extents), 1f);
                }
                else
                {
                    extents[i] = 1f;
                }
            }

            return extents;
        }
#endif

        #endregion
    }
}
