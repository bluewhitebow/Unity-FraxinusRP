// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using Unity.Profiling;
#endif

namespace Fraxinus.GPUBatcher
{
    // prototype management: registration, extraction, LOD/material setup, instance tracking
    public partial class GPUBatcherRenderer
    {
        #region Prototype Fields

        private List<RenderSignature> _renderSignatures = new();
        private Dictionary<long, int> _protoDedup = new();
#if UNITY_EDITOR
        private Dictionary<GameObject, List<int>> _prefabToAllIndices = new();
#endif
        private NativeArray<float> _prototypeMaxShadowDistance;
        private NativeArray<int> _prototypeMaxShadowCascade;
        private NativeArray<bool> _prototypeCrossfadeEnabled;
        private NativeArray<bool> _prototypeAnimatedCrossfade;
        private NativeArray<uint> _prototypeShadowMaskCache;
        private int _protoArrayCapacity;
        private bool _anyPrototypeUsesAnimatedCrossfade;
        private bool[] _prototypeVacant;
        private int[] _protoRefCount;
        private Dictionary<int, float> _pendingProtoPurge = new(); // protoIndex -> timestamp when refcount hit 0
        private List<int> _tempPurgeList = new(); // reusable scratch list
        private Stack<int> _freeProtoIndices = new();
        private MeshCombineCache _meshCombineCache = new();

        internal MeshCombineCache meshCombineCache => _meshCombineCache;

        // prototype bounds cache
        private NativeArray<float3> _protoBoundsCenter;
        private NativeArray<float> _protoBoundsRadius;
        private NativeArray<float> _protoLodReferenceSize;

        private GraphicsBuffer _gpuSignatureBuffer;

        #endregion

        #region Prototype Queries

#if UNITY_EDITOR
        public bool IsRegisteredPrototype(GameObject prefab)
        {
            if (prefab == null || _renderSignatures == null) return false;
            return _prefabToAllIndices.ContainsKey(prefab);
        }
#endif

        public int prototypeCount => _renderSignatures?.Count ?? 0;

        public bool TryGetPrototypeCullBounds(int globalProtoIndex, out float3 boundsCenter, out float boundsRadius)
        {
            if (_protoBoundsCenter.IsCreated && globalProtoIndex >= 0 && globalProtoIndex < _protoBoundsCenter.Length)
            {
                boundsCenter = _protoBoundsCenter[globalProtoIndex];
                boundsRadius = _protoBoundsRadius[globalProtoIndex];
                return true;
            }
            boundsCenter = default;
            boundsRadius = 0;
            return false;
        }

        public int activePrototypeCount
        {
            get
            {
                if (_renderSignatures == null) return 0;
                int count = _renderSignatures.Count;
                if (_prototypeVacant != null)
                {
                    for (int i = 0; i < _prototypeVacant.Length && i < _renderSignatures.Count; i++)
                        if (_prototypeVacant[i]) count--;
                }
                return count;
            }
        }

        public RenderSignature GetPrototype(int prototypeIndex)
        {
            if (_renderSignatures == null || prototypeIndex < 0 || prototypeIndex >= _renderSignatures.Count) return null;
            if (_prototypeVacant != null && prototypeIndex < _prototypeVacant.Length && _prototypeVacant[prototypeIndex])
                return null;
            return _renderSignatures[prototypeIndex];
        }

        public bool IsPrototypeRemoved(int prototypeIndex)
        {
            if (prototypeIndex < 0 || _renderSignatures == null || prototypeIndex >= _renderSignatures.Count) return true;
            return _prototypeVacant != null && prototypeIndex < _prototypeVacant.Length && _prototypeVacant[prototypeIndex];
        }

        public int GetPrototypeRefCount(int prototypeIndex)
        {
            if (prototypeIndex < 0 || _protoRefCount == null || prototypeIndex >= _protoRefCount.Length) return 0;
            return _protoRefCount[prototypeIndex];
        }


        #endregion

        #region Prototype Removal & Unregistration

        // release combined-mesh refs held by a discarded signature (dedup hit, refresh transient)
        private void ReleaseSignatureCombinedMeshes(RenderSignature sig)
        {
            if (sig?.asset?.lodMeshes == null) return;
            for (int i = 0; i < sig.asset.lodMeshes.Length; i++)
                if (sig.asset.lodMeshes[i] != null)
                    _meshCombineCache.Release(sig.asset.lodMeshes[i]);
        }

        private void TrulyRemovePrototype(int protoIndex)
        {
            // ensure vacant array is large enough
            if (_prototypeVacant == null || _prototypeVacant.Length < _renderSignatures.Count)
                Array.Resize(ref _prototypeVacant, _renderSignatures.Count);

            // remove commands + allocations before batch slots
            IncRemovePrototypeCommands(protoIndex);
            IncRemovePrototype(protoIndex);

            RemoveBatchesForPrototype(protoIndex);

            // release SpeedTree slot (no-op for non-SpeedTree protos)
            UnregisterSpeedTreeProto(protoIndex);

            _prototypeVacant[protoIndex] = true;

            var sig = _renderSignatures[protoIndex];
            if (sig?.asset != null)
            {
                _protoDedup.Remove(sig.GetSignatureKey());

                ReleaseSignatureCombinedMeshes(sig);

#if UNITY_EDITOR
                var prefab = sig.asset.sourcePrefab;
                if (prefab != null && _prefabToAllIndices.TryGetValue(prefab, out var list))
                {
                    list.Remove(protoIndex);
                    if (list.Count == 0)
                        _prefabToAllIndices.Remove(prefab);
                }
#endif
            }
            _renderSignatures[protoIndex] = null;

            if (_protoRefCount != null && protoIndex < _protoRefCount.Length)
                _protoRefCount[protoIndex] = 0;

            _freeProtoIndices.Push(protoIndex);

            RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
            _prototypeBufferDirty = true;

            _dirtySignatureProtos.Add(protoIndex);

#if DEBUG
            if (_protoInstanceCounts.IsCreated && protoIndex < _protoInstanceCounts.Length
                && _protoInstanceCounts[protoIndex] > 0)
                Debug.LogWarning($"GPUBatcherRenderer: Removing prototype {protoIndex} but {_protoInstanceCounts[protoIndex]} instances still reference it - free chunks first");
#endif
        }

        /// decrement refcount. returns true if reached 0.
        public bool UnregisterRenderSignature(int protoIndex)
        {
            if (protoIndex < 0 || _renderSignatures == null || protoIndex >= _renderSignatures.Count)
                return false;
            if (_prototypeVacant != null && protoIndex < _prototypeVacant.Length && _prototypeVacant[protoIndex])
                return false; // truly removed

            if (_protoRefCount == null || protoIndex >= _protoRefCount.Length)
                return false;
            if (_protoRefCount[protoIndex] <= 0)
                return false; // already at zero, don't re-arm grace period

            _protoRefCount[protoIndex]--;

            bool reachedZero = _protoRefCount[protoIndex] == 0;
            if (reachedZero)
                _pendingProtoPurge[protoIndex] = Time.realtimeSinceStartup;

            LogMessageDevelopment($"GPUBatcherRenderer: Unregistered render signature {protoIndex} (refcount now {_protoRefCount[protoIndex]})");
            return reachedZero;
        }

/// <summary>Force-remove a prototype regardless of reference count. Other registerers may break.</summary>
        public bool RemovePrototype(int protoIndex)
        {
            if (protoIndex < 0 || _renderSignatures == null || protoIndex >= _renderSignatures.Count)
                return false;
            if (_prototypeVacant != null && protoIndex < _prototypeVacant.Length && _prototypeVacant[protoIndex])
                return false; // already removed

#if DEBUG
            int refCount = (_protoRefCount != null && protoIndex < _protoRefCount.Length) ? _protoRefCount[protoIndex] : 0;
            if (refCount > 1)
                Debug.LogWarning($"GPUBatcherRenderer: Force-removing prototype {protoIndex} with refcount {refCount}");
#endif

            TrulyRemovePrototype(protoIndex);
            return true;
        }

        public int PurgeUnusedPrototypes()
        {
            if (_renderSignatures == null || _protoRefCount == null) return 0;

            int purged = 0;
            for (int i = 0; i < _renderSignatures.Count; i++)
            {
                if (_prototypeVacant != null && i < _prototypeVacant.Length && _prototypeVacant[i])
                    continue; // already vacant
                if (i >= _protoRefCount.Length || _protoRefCount[i] > 0)
                    continue; // still in use

                TrulyRemovePrototype(i);
                purged++;
            }

            if (purged > 0)
            {
                _pendingProtoPurge.Clear();
                LogMessageDevelopment($"GPUBatcherRenderer: Purged {purged} unused prototypes");
            }
            return purged;
        }

        internal void ProcessPendingPrototypePurges()
        {
            if (_pendingProtoPurge.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            float grace = _config.prototypeIdleGracePeriod;

            _tempPurgeList.Clear();
            foreach (var kvp in _pendingProtoPurge)
            {
                if (now - kvp.Value >= grace)
                    _tempPurgeList.Add(kvp.Key);
            }

            if (_tempPurgeList.Count == 0) return;

            foreach (int idx in _tempPurgeList)
            {
                _pendingProtoPurge.Remove(idx);
                if (_protoRefCount != null && idx < _protoRefCount.Length && _protoRefCount[idx] == 0
                    && !(_prototypeVacant != null && idx < _prototypeVacant.Length && _prototypeVacant[idx]))
                {
                    TrulyRemovePrototype(idx);
                }
            }

            LogMessageDevelopment($"GPUBatcherRenderer: Auto-purged {_tempPurgeList.Count} idle prototypes");
        }

        #endregion

        #region Prototype Registration

        /// increments refcount if already registered.
        public int RegisterRenderSignatureFromPrefab(GameObject prefab)
        {
            if (prefab == null)
            {
                Debug.LogError("GPUBatcherRenderer: Cannot register null prefab");
                return -1;
            }

            RenderSignature sig = ExtractRenderSignature(prefab, _meshCombineCache);
            if (sig == null)
                return -1;

            return RegisterRenderSignature(sig);
        }

        /// <summary>Register all render signatures from a multi-renderer prefab. Returns global indices per extracted prototype.</summary>
        public int[] RegisterRenderSignaturesFromPrefab(GameObject prefab)
        {
            if (prefab == null)
            {
                Debug.LogError("GPUBatcherRenderer: Cannot register null prefab");
                return System.Array.Empty<int>();
            }

            var sigs = ExtractRenderSignatures(prefab, _meshCombineCache);
            if (sigs == null || sigs.Count == 0)
                return System.Array.Empty<int>();

            var indices = new int[sigs.Count];
            for (int i = 0; i < sigs.Count; i++)
                indices[i] = RegisterRenderSignature(sigs[i]);
            return indices;
        }

        /// increments refcount if already registered.
        public int RegisterRenderSignature(RenderSignature signature)
        {
            if (!IsValidSignature(signature))
            {
                Debug.LogError("GPUBatcherRenderer: Cannot register null signature or signature without mesh");
                return -1;
            }

            if (!_initialized) Initialize();

            int index = AllocOrDedup(signature, out bool isNewSlot);
            if (isNewSlot)
            {
                FinalizeNewProtoSlot(index);
                LogMessageDevelopment($"GPUBatcherRenderer: Registered prototype {index} ({signature.lodCount} LODs), refcount=1");
            }
            return index;
        }

        static bool IsValidSignature(RenderSignature sig)
            => sig?.asset != null && sig.mesh != null && sig.materials != null && sig.materials.Length > 0;

        // dedup hit: bumps refcount + releases discarded combined-mesh refs, returns existing index.
        // dedup miss: allocates a new slot, returns its index.
        private int AllocOrDedup(RenderSignature signature, out bool isNewSlot)
        {
            long key = signature.GetSignatureKey();

            if (_protoDedup.TryGetValue(key, out int existIdx))
            {
                EnsureRefCountArray(existIdx);
                _protoRefCount[existIdx]++;
                _pendingProtoPurge.Remove(existIdx);
                ReleaseSignatureCombinedMeshes(signature);
                isNewSlot = false;
                return existIdx;
            }

            int index = AllocProtoSlot(signature);
            _protoDedup[key] = index;

#if UNITY_EDITOR
            var prefab = signature.asset.sourcePrefab;
            if (prefab != null)
            {
                if (!_prefabToAllIndices.TryGetValue(prefab, out var list))
                {
                    list = new List<int>();
                    _prefabToAllIndices[prefab] = list;
                }
                list.Add(index);
            }
#endif
            isNewSlot = true;
            return index;
        }

        // post-processing for a newly allocated slot
        private void FinalizeNewProtoSlot(int slot)
        {
            UpdatePrototypeFlag(slot);

            if (_chunkSystemInitialized && _matrixBuffer != null)
            {
                // claim SpeedTree slot first - must precede batch creation so GetBatchID resolves
                if (_renderSignatures[slot].speedTreeRendererID != default)
                    RegisterSpeedTreeProto(slot);
                CreateBatchesForPrototype(slot);
            }

            _pendingProtoAdds.Add(slot);
            _dirtySignatureProtos.Add(slot);
            RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
        }

        public void ClearPrototypes()
        {
            _renderSignatures.Clear();
            _protoDedup.Clear();
#if UNITY_EDITOR
            _prefabToAllIndices.Clear();
#endif
            _meshIdCache.Clear();
            _materialIdCache.Clear();
            _meshRefCount.Clear();
            _materialRefCount.Clear();
            _meshIdReverse.Clear();
            _materialIdReverse.Clear();
            _prototypeVacant = null;
            _protoRefCount = null;
            _freeProtoIndices.Clear();
            _pendingProtoPurge.Clear();
            _pendingProtoAdds.Clear();
            _pendingProtoResizes.Clear();
            _materializedProtos.Clear();
            _dirtySignatureProtos.Clear();
            _anyPrototypeUsesAnimatedCrossfade = false;
            _meshCombineCache.Clear();
            _totalCameraBuckets = 0;
            _totalShadowBuckets = 0;
            RemoveAllBatchIds();
            ClearBatchSlots();
            // SpeedTree slot state - batch IDs are already gone via RemoveAllBatchIds above
            _freeSpeedTreeSlots.Clear();
            _speedTreeSlotHWM = 0;
            if (_protoSpeedTreeSlotIndex != null)
                for (int i = 0; i < _protoSpeedTreeSlotIndex.Length; i++) _protoSpeedTreeSlotIndex[i] = -1;
            if (_speedTreeRendererIds != null)
                for (int i = 0; i < _speedTreeRendererIds.Length; i++) _speedTreeRendererIds[i] = default;
            _prototypeMaxShadowDistance.DisposeIfCreated();
            _prototypeMaxShadowCascade.DisposeIfCreated();
            _prototypeCrossfadeEnabled.DisposeIfCreated();
            _prototypeAnimatedCrossfade.DisposeIfCreated();
            _prototypeShadowMaskCache.DisposeIfCreated();
            _protoBoundsCenter.DisposeIfCreated();
            _protoBoundsRadius.DisposeIfCreated();
            _protoLodReferenceSize.DisposeIfCreated();
            _protoInstanceCounts.DisposeIfCreated();
            _protoInstanceCountHWM.DisposeIfCreated();
            _protoFlipCounts.DisposeIfCreated();
            _protoFlipCountHWM.DisposeIfCreated();
            _protoArrayCapacity = 0;
            RequestBatchRebuild(BatchRebuildScope.FullRebuild);

            _allSignaturesDirty = true;
        }

        // reuse free slot or append
        private int AllocProtoSlot(RenderSignature sig)
        {
            int index;
            if (_freeProtoIndices.Count > 0)
            {
                index = _freeProtoIndices.Pop();

#if DEBUG
                if (_protoInstanceCounts.IsCreated && index < _protoInstanceCounts.Length && _protoInstanceCounts[index] > 0)
                    Debug.LogWarning($"GPUBatcherRenderer: Reusing proto slot {index} but {_protoInstanceCounts[index]} instances still reference it");
#endif

                // reset counters so new proto starts clean
                _materializedProtos.Remove(index);
                if (_protoInstanceCounts.IsCreated && index < _protoInstanceCounts.Length)
                    _protoInstanceCounts[index] = 0;
                if (_protoInstanceCountHWM.IsCreated && index < _protoInstanceCountHWM.Length)
                    _protoInstanceCountHWM[index] = 0;
                if (_protoFlipCountHWM.IsCreated && index < _protoFlipCountHWM.Length)
                    _protoFlipCountHWM[index] = 0;
                if (_protoFlipCounts.IsCreated && index < _protoFlipCounts.Length)
                    _protoFlipCounts[index] = 0;

                _renderSignatures[index] = sig;

                if (_prototypeVacant != null && index < _prototypeVacant.Length)
                    _prototypeVacant[index] = false;
            }
            else
            {
                index = _renderSignatures.Count;
                _renderSignatures.Add(sig);
            }

            EnsureRefCountArray(index);
            _protoRefCount[index] = 1;

            return index;
        }

        private void EnsureRefCountArray(int index)
        {
            if (_protoRefCount == null || index >= _protoRefCount.Length)
            {
                int newLen = math.max(8, _protoRefCount?.Length ?? 0);
                while (newLen <= index) newLen *= 2;
                Array.Resize(ref _protoRefCount, newLen);
            }
        }

#if UNITY_EDITOR
        /// <summary>Refresh all variants of a prototype from its prefab. Respects override masks.</summary>
        public bool RefreshPrototype(GameObject prefab)
        {
            if (prefab == null) return false;

            string assetPath = UnityEditor.AssetDatabase.GetAssetPath(prefab);
            if (!string.IsNullOrEmpty(assetPath))
            {
                var freshPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (freshPrefab != null)
                    prefab = freshPrefab;
            }

            if (!_prefabToAllIndices.TryGetValue(prefab, out var indices) || indices.Count == 0)
                return false;

            var freshSigs = ExtractRenderSignatures(prefab, _meshCombineCache);
            if (freshSigs == null || freshSigs.Count == 0)
            {
                Debug.LogWarning($"GPUBatcherRenderer: Failed to re-extract prototype '{prefab.name}'");
                return false;
            }

            // structure changed: refresh the aligned prefix, full rebuild reconciles the rest
            int pairCount = indices.Count;
            if (freshSigs.Count != indices.Count)
            {
                pairCount = math.min(indices.Count, freshSigs.Count);
                RequestBatchRebuild(BatchRebuildScope.FullRebuild);
            }

            bool anyCrossfadeChanged = false;
            int assignedVariants = 0;

            for (int v = 0; v < pairCount; v++)
            {
                int protoIndex = indices[v];
                if (_prototypeVacant != null && protoIndex < _prototypeVacant.Length && _prototypeVacant[protoIndex])
                    continue;

                var oldSig = _renderSignatures[protoIndex];
                if (oldSig == null) continue;

                var freshSig = freshSigs[v];

                bool oldAnimatedCrossfade = oldSig.animateCrossFading;
                var oldFadeMode = oldSig.fadeMode;

                // capture old key before update
                long oldKey = oldSig.GetSignatureKey();

                // release this variant's old refs
                ReleaseSignatureCombinedMeshes(oldSig);

                oldSig.asset = freshSig.asset;

                // every variant takes its own ref; freshSig's transient is dropped after the loop
                if (freshSig.asset?.lodMeshes != null)
                {
                    for (int m = 0; m < freshSig.asset.lodMeshes.Length; m++)
                        if (freshSig.asset.lodMeshes[m] != null)
                            _meshCombineCache.AddRef(freshSig.asset.lodMeshes[m]);
                }
                assignedVariants++;

                // update non-overridden config fields
                oldSig.ApplyDefaults(freshSig);

                // re-key dedup if signature changed
                long newKey = oldSig.GetSignatureKey();
                if (newKey != oldKey)
                {
                    _protoDedup.Remove(oldKey);
                    if (!_protoDedup.ContainsKey(newKey))
                        _protoDedup[newKey] = protoIndex;
                }

                bool crossfadeSettingsChanged = oldAnimatedCrossfade != oldSig.animateCrossFading ||
                                               oldFadeMode != oldSig.fadeMode;

                RemoveBatchesForPrototype(protoIndex);

                if (_prototypeMaxShadowDistance.IsCreated && protoIndex < _prototypeMaxShadowDistance.Length)
                    _prototypeMaxShadowDistance[protoIndex] = oldSig.maxShadowDistance;
                if (_prototypeCrossfadeEnabled.IsCreated && protoIndex < _prototypeCrossfadeEnabled.Length)
                    _prototypeCrossfadeEnabled[protoIndex] = oldSig.fadeMode != LODFadeMode.None;
                if (_prototypeAnimatedCrossfade.IsCreated && protoIndex < _prototypeAnimatedCrossfade.Length)
                    _prototypeAnimatedCrossfade[protoIndex] = oldSig.animateCrossFading;
                if (_prototypeMaxShadowCascade.IsCreated && protoIndex < _prototypeMaxShadowCascade.Length)
                    _prototypeMaxShadowCascade[protoIndex] = math.clamp(oldSig.maxShadowCascade, 1, 4);
                if (_prototypeShadowMaskCache.IsCreated && protoIndex < _prototypeShadowMaskCache.Length)
                    _prototypeShadowMaskCache[protoIndex] = oldSig.GetLODShadowMask();

                if (_chunkSystemInitialized && _matrixBuffer != null)
                    CreateBatchesForPrototype(protoIndex);

                if (crossfadeSettingsChanged)
                {
                    ResetFadeStatesForPrototype(protoIndex);
                    anyCrossfadeChanged = true;
                }
            }

            // drop each fresh sig's transient extract refs
            for (int f = 0; f < freshSigs.Count; f++)
                ReleaseSignatureCombinedMeshes(freshSigs[f]);

            _batchFullRecomputeNeeded = true; // replace removes+re-adds protos, need full recompute
            RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
            _gpuCullingReady = false; // prevent stale templates from being used before SystemUpdate rebuilds them

            if (anyCrossfadeChanged)
            {
                foreach (var kvp in _viewFadeStates)
                    kvp.Value.snapRequested = true;
            }

            return true;
        }
#endif

        private readonly List<int> _resetFadePoolIndices = new List<int>();
        private readonly List<(int start, int count)> _resetFadeRanges = new List<(int, int)>();

        private void ResetFadeStatesForPrototype(int prototypeIndex)
        {
            if (_viewFadeStates == null || _viewFadeStates.Count == 0) return;
            if (!_chunkPools.IsCreated || !_chunkProtoBreakdown.IsCreated) return;

            _resetFadePoolIndices.Clear();
            var poolIndices = _resetFadePoolIndices;
            for (int a = 0; a < _allocatedCount; a++)
            {
                int c = _activeChunks[a];
                if (prototypeIndex >= _protoBreakdownStride) continue;
                if (_chunkProtoBreakdown[c * _protoBreakdownStride + prototypeIndex] <= 0) continue;

                // this chunk has instances of the target prototype, collect its pools
                var chunk = _chunks[c];
                int poolBaseIdx = c * _maxPoolRefsPerChunk;
                for (int p = 0; p < chunk.poolCount; p++)
                {
                    int poolIdx = _chunkPools[poolBaseIdx + p];
                    if (poolIdx >= 0) poolIndices.Add(poolIdx);
                }
            }

            if (poolIndices.Count == 0) return;

            poolIndices.Sort();
            _resetFadeRanges.Clear();
            var ranges = _resetFadeRanges;
            int ps = k_poolSize;
            int rangeStart = poolIndices[0] * ps;
            int rangeEnd = rangeStart + ps;

            for (int i = 1; i < poolIndices.Count; i++)
            {
                int slotStart = poolIndices[i] * ps;
                if (slotStart == rangeEnd)
                {
                    // contiguous with previous, extend
                    rangeEnd += ps;
                }
                else
                {
                    ranges.Add((rangeStart, rangeEnd - rangeStart));
                    rangeStart = slotStart;
                    rangeEnd = slotStart + ps;
                }
            }
            ranges.Add((rangeStart, rangeEnd - rangeStart));

            foreach (var kvp in _viewFadeStates)
            {
                var buf = kvp.Value.fadeStateBuffer;
                if (buf == null) continue;

                foreach (var (start, count) in ranges)
                {
                    if (start + count <= buf.count)
                        GPUInitFadeStateTyped(buf, start, count);
                }
            }
        }

        public void MarkBatchesDirtyForFullRebuild()
        {
            RequestBatchRebuild(BatchRebuildScope.FullRebuild);
        }

        /// <summary>Mark GPU prototype buffer as dirty. Use when material properties change.</summary>
        public void MarkSignatureBufferDirty()
        {
            _prototypeBufferDirty = true;

            _allSignaturesDirty = true;
        }

        // walk up from a transform to find the nearest GPUBatcherPrototypeExtraData
        static GPUBatcherPrototypeExtraData FindNearestExtraData(Transform from)
        {
            var t = from;
            while (t != null)
            {
                var data = t.GetComponent<GPUBatcherPrototypeExtraData>();
                if (data != null && data.enabled) return data;
                t = t.parent;
            }
            return null;
        }

#if UNITY_EDITOR
        public void RefreshDefaultCrossfadeDuration()
        {
            if (_renderSignatures == null) return;
            float def = math.clamp(GPUBatcherConfig.instance.defaultAnimatedCrossfadeDuration, 0f, 10f);
            for (int i = 0; i < _renderSignatures.Count; i++)
            {
                var sig = _renderSignatures[i];
                if (sig == null || !sig.isDefaultCrossfadeDuration) continue;
                sig.lodFadeDuration = def;
            }
        }
#endif

        static void ApplyExtraData(RenderSignature sig, GPUBatcherPrototypeExtraData extraData)
        {
            bool useConfigDefault = extraData == null || !extraData.overrideLodFadeDuration;
            sig.lodFadeDuration = math.clamp(
                useConfigDefault
                    ? GPUBatcherConfig.instance.defaultAnimatedCrossfadeDuration
                    : extraData.lodFadeDuration,
                0f, 10f);
#if UNITY_EDITOR
            sig.isDefaultCrossfadeDuration = useConfigDefault;
#endif

            if (extraData == null) return;
            sig.prototypeGroup = extraData.prototypeGroup;
            sig.maxShadowCascade = extraData.maxShadowCascade;
            sig.minShadowLOD = extraData.minShadowLOD;
            sig.maxShadowDistance = math.max(0f, extraData.effectiveMaxShadowDistance);
            sig.maxShadowCrossfadeCascade = extraData.effectiveMaxShadowCrossfadeCascade; // -99 = use global config
            sig.enableDistanceDensity = extraData.enableDistanceDensity;
            sig.distanceDensityMode = extraData.distanceDensityMode;
            sig.densityStartScreenSize = math.max(0f, extraData.densityStartScreenSize * 0.01f);
            sig.densityStartDistance = math.max(0f, extraData.densityStartDistance);
            sig.densityEndDistance = math.max(sig.densityStartDistance + 0.01f, extraData.densityEndDistance);
            sig.densityMinFraction = math.saturate(extraData.densityMinFraction);
            sig.densityFadeWidth = math.max(0f, extraData.densityFadeWidth);
        }

        static void ApplyMinScreenSize(RenderSignature sig, PrototypeData proto,
            GPUBatcherPrototypeExtraData extraData, bool hasLodGroup)
        {
            float minScreenSizeRatio = extraData != null ? extraData.minScreenSize * 0.01f : 0f;
            if (minScreenSizeRatio > 0f)
            {
                int lastLod = proto.lodScreenSizes.Length - 1;
#if UNITY_6000_2_OR_NEWER
                // mesh LOD: minScreenSize already applied as last LOD's lower bound during extraction
                if (!proto.usesMeshLod)
#endif
                if (proto.lodScreenSizes[lastLod] < minScreenSizeRatio)
                    proto.lodScreenSizes[lastLod] = minScreenSizeRatio;

                // fade mode only applies to non-LOD prototypes
                if (proto.fadeMode == LODFadeMode.None && !hasLodGroup
#if UNITY_6000_2_OR_NEWER
                    && !proto.usesMeshLod
#endif
                )
                {
                    switch (extraData.minScreenSizeFadeMode)
                    {
                        case MinScreenSizeFadeMode.Crossfade:
                            proto.lodFadeTransitionWidths[lastLod] = extraData.minScreenSizeFadeWidth;
                            proto.fadeMode = LODFadeMode.CrossFade;
                            break;
                        case MinScreenSizeFadeMode.AnimatedCrossfade:
                            proto.fadeMode = LODFadeMode.CrossFade;
                            sig.animateCrossFading = true;
                            break;
                    }
                }
            }
            // default small mesh culling from config
            else if (!hasLodGroup
#if UNITY_6000_2_OR_NEWER
                     && !proto.usesMeshLod
#endif
                     && GPUBatcherConfig.instance.defaultSmallMeshSize > 0f)
            {
                float defaultRatio = GPUBatcherConfig.instance.defaultSmallMeshSize * 0.01f;
                int lastLod = proto.lodScreenSizes.Length - 1;
                if (proto.lodScreenSizes[lastLod] < defaultRatio)
                    proto.lodScreenSizes[lastLod] = defaultRatio;

                switch (GPUBatcherConfig.instance.defaultSmallMeshFadeMode)
                {
                    case MinScreenSizeFadeMode.Crossfade:
                        proto.lodFadeTransitionWidths[lastLod] = GPUBatcherConfig.instance.defaultSmallMeshFadeWidth;
                        proto.fadeMode = LODFadeMode.CrossFade;
                        break;
                    case MinScreenSizeFadeMode.AnimatedCrossfade:
                        proto.fadeMode = LODFadeMode.CrossFade;
                        sig.animateCrossFading = true;
                        break;
                }
            }
        }

        static void ApplyMotionVectorMode(RenderSignature sig, PrototypeData proto)
        {
            if (proto.lodMotionVectorModes != null && proto.lodMotionVectorModes.Length > 0)
                sig.motionVectorMode = proto.lodMotionVectorModes[0];
            else
                sig.motionVectorMode = MotionVectorGenerationMode.Camera;

#if HDRP_INSTALLED
            if (GPUBatcherConfig.Instance.hdrpForwardDepthFix
                && sig.motionVectorMode == MotionVectorGenerationMode.Camera)
            {
                bool anyMaterialHasMVPass = false;
                for (int l = 0; l < proto.lodMaterials.Length; l++)
                {
                    var mats = proto.lodMaterials[l];
                    for (int m = 0; m < mats.Length; m++)
                    {
                        if (mats[m] != null && mats[m].GetShaderPassEnabled("MotionVectors"))
                        {
                            anyMaterialHasMVPass = true;
                            break;
                        }
                    }
                    if (anyMaterialHasMVPass) break;
                }
                if (anyMaterialHasMVPass)
                    sig.motionVectorMode = MotionVectorGenerationMode.Object;
            }
#endif
        }

        internal static RenderSignature ExtractRenderSignature(GameObject prefab)
            => ExtractRenderSignature(prefab, null);

        internal static RenderSignature ExtractRenderSignature(GameObject prefab, MeshCombineCache combineCache)
        {
            var lodGroup = prefab.GetComponent<LODGroup>();

            MeshRenderer renderer = null;
            foreach (var mr in prefab.GetComponentsInChildren<MeshRenderer>())
                if (mr.enabled) { renderer = mr; break; }
            var meshFilter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;

            if (renderer == null || meshFilter == null)
            {
                Debug.LogError($"GPUBatcherRenderer: Prefab '{prefab.name}' has no MeshRenderer/MeshFilter");
                return null;
            }

            var proto = new PrototypeData();
#if UNITY_EDITOR
            proto.sourcePrefab = prefab;
#endif

            var sig = new RenderSignature { asset = proto };

            sig.layer = prefab.layer;
            sig.renderingLayerMask = renderer.renderingLayerMask;
            var extraData = prefab.GetComponent<GPUBatcherPrototypeExtraData>();
            if (extraData != null && !extraData.enabled) extraData = null;
            ApplyExtraData(sig, extraData);

            if (lodGroup != null)
            {
                // extract from LODGroup
                var lods = lodGroup.GetLODs();
                proto.lodReferenceSize = lodGroup.size;
                proto.fadeMode = lodGroup.fadeMode;
                sig.animateCrossFading = lodGroup.animateCrossFading;

#if DEBUG
                if (lodGroup.size <= 0f)
                    Debug.LogError($"GPUBatcherRenderer: LODGroup on '{prefab.name}' has size=0 at registration time. " +
                        "LOD screen sizes will be incorrect. This can happen if the prefab is not fully loaded (e.g. asset bundles).");
#endif

                proto.lodMeshes = new Mesh[lods.Length];
                proto.lodMaterials = new Material[lods.Length][];
                proto.lodScreenSizes = new float[lods.Length];
                proto.lodFadeTransitionWidths = new float[lods.Length];
                proto.lodShadowCastingModes = new ShadowCastingMode[lods.Length];
                proto.lodMotionVectorModes = new MotionVectorGenerationMode[lods.Length];

                for (int i = 0; i < lods.Length; i++)
                {
                    var lod = lods[i];
                    proto.lodScreenSizes[i] = lod.screenRelativeTransitionHeight;
                    proto.lodFadeTransitionWidths[i] = lod.fadeTransitionWidth;

                    ExtractLODRenderers(lod, prefab.transform, combineCache, prefab.name, i,
                        out proto.lodMeshes[i], out proto.lodMaterials[i],
                        out proto.lodShadowCastingModes[i], out proto.lodMotionVectorModes[i]);
                }

                proto.mesh = proto.lodMeshes[0];
                proto.materials = proto.lodMaterials[0];

                // SpeedTree: mesh LODs use percentage fade, billboard transition uses dithered crossfade
                if (proto.fadeMode == LODFadeMode.SpeedTree)
                {
                    int billboardIdx = -1;
                    for (int i = 0; i < proto.lodMeshes.Length; i++)
                    {
                        if (proto.lodMeshes[i] == null) { billboardIdx = i; break; }
                    }
                    int pctCount = (billboardIdx >= 0) ? billboardIdx - 1 : proto.lodMeshes.Length - 1;
                    proto.percentageFadeMask = pctCount > 0 ? (1u << pctCount) - 1u : 0u;
                }

                // capture renderer ID for SpeedTree wind
                if (GPUBatcherConfig.instance.enableSpeedTreeWind)
                {
                    var tree = prefab.GetComponentInChildren<Tree>();
                    bool hasLod0Renderer = lods[0].renderers?.Length > 0 && lods[0].renderers[0] != null;
                    if (tree != null && hasLod0Renderer)
                        proto.AssignRendererID(lods[0].renderers[0]);
                }
            }
#if UNITY_6000_2_OR_NEWER
            else if (meshFilter.sharedMesh != null && meshFilter.sharedMesh.lodCount > 1
                     && prefab.GetComponentInChildren<GPUBatcherMeshLodData>() is GPUBatcherMeshLodData meshLodData)
            {
                // mesh LOD path - single mesh with internal LOD ranges (Unity 6.2+)
                Mesh mesh = meshFilter.sharedMesh;
                int numLevels = meshLodData.meshLodNumLevels;
                int maxMeshLod = mesh.lodCount - 1;

                proto.mesh = mesh;
                proto.materials = renderer.sharedMaterials;
                proto.usesMeshLod = true;
                proto.meshLodIndices = new int[numLevels];

                proto.lodMeshes = new Mesh[numLevels];
                proto.lodMaterials = new Material[numLevels][];
                proto.lodScreenSizes = new float[numLevels];
                proto.lodFadeTransitionWidths = new float[numLevels];
                proto.lodShadowCastingModes = new ShadowCastingMode[numLevels];
                proto.lodMotionVectorModes = new MotionVectorGenerationMode[numLevels];

                float meshLodMinScreen = extraData != null ? extraData.minScreenSize * 0.01f : 0f;

                for (int i = 0; i < numLevels; i++)
                {
                    proto.lodMeshes[i] = mesh;
                    proto.lodMaterials[i] = renderer.sharedMaterials;

                    // shift: UI stores upper bounds, GPU expects lower bounds
                    // LOD i's lower bound = LOD (i+1)'s upper bound; last LOD = minScreenSize
                    if (i < numLevels - 1)
                    {
                        proto.lodScreenSizes[i] = (meshLodData.meshLodScreenSizes != null && i + 1 < meshLodData.meshLodScreenSizes.Length)
                            ? meshLodData.meshLodScreenSizes[i + 1] : 0f;
                    }
                    else
                    {
                        proto.lodScreenSizes[i] = meshLodMinScreen;
                    }

                    // fade widths NOT shifted - each controls fade at its own lower boundary
                    proto.lodFadeTransitionWidths[i] = (meshLodData.meshLodFadeWidths != null && i < meshLodData.meshLodFadeWidths.Length)
                        ? meshLodData.meshLodFadeWidths[i] : 0.5f;
                    proto.lodShadowCastingModes[i] = renderer.shadowCastingMode;
                    proto.lodMotionVectorModes[i] = renderer.motionVectorGenerationMode;
                    proto.meshLodIndices[i] = (meshLodData.meshLodIndices != null && i < meshLodData.meshLodIndices.Length)
                        ? math.clamp(meshLodData.meshLodIndices[i], 0, maxMeshLod) : math.min(i, maxMeshLod);
                }

                proto.lodReferenceSize = mesh.bounds.size.magnitude;    //matches GRD

                switch (meshLodData.meshLodFadeMode)
                {
                    case MinScreenSizeFadeMode.Crossfade:
                        proto.fadeMode = LODFadeMode.CrossFade;
                        break;
                    case MinScreenSizeFadeMode.AnimatedCrossfade:
                        proto.fadeMode = LODFadeMode.CrossFade;
                        sig.animateCrossFading = true;
                        break;
                    default:
                        proto.fadeMode = LODFadeMode.None;
                        break;
                }
            }
#endif
            else
            {
                // single LOD
                proto.mesh = meshFilter.sharedMesh;
                proto.materials = renderer.sharedMaterials;
                proto.lodMeshes = new Mesh[] { proto.mesh };
                proto.lodMaterials = new Material[][] { proto.materials };
                proto.lodScreenSizes = new float[] { 0f };
                proto.lodFadeTransitionWidths = new float[] { 0f };
                proto.lodShadowCastingModes = new ShadowCastingMode[] { renderer.shadowCastingMode };
                proto.lodMotionVectorModes = new MotionVectorGenerationMode[] { renderer.motionVectorGenerationMode };
                proto.fadeMode = LODFadeMode.None;
                if (meshFilter.sharedMesh != null)
                {
                    var s = meshFilter.sharedMesh.bounds.size;
                    proto.lodReferenceSize = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                }
            }

            ApplyMinScreenSize(sig, proto, extraData, lodGroup != null);
            ApplyMotionVectorMode(sig, proto);

            return sig;
        }

        /// <summary>Extract all prototypes from a prefab hierarchy. Each LODGroup becomes a LOD prototype,
        /// each unclaimed MeshRenderer becomes a single-LOD prototype. Extra data inherited from nearest parent.</summary>
        internal static List<RenderSignature> ExtractRenderSignatures(GameObject prefab, MeshCombineCache combineCache)
        {
            var results = new List<RenderSignature>();

            // collect all LODGroups and build exclusion set of claimed renderers
            var lodGroups = prefab.GetComponentsInChildren<LODGroup>();
            HashSet<Renderer> claimedRenderers = null;

            if (lodGroups.Length > 0)
            {
                claimedRenderers = new HashSet<Renderer>();
                foreach (var lodGroup in lodGroups)
                {
                    var lods = lodGroup.GetLODs();
                    foreach (var lod in lods)
                        if (lod.renderers != null)
                            foreach (var r in lod.renderers)
                                if (r != null) claimedRenderers.Add(r);
                }
            }

            // extract LODGroup prototypes
            foreach (var lodGroup in lodGroups)
            {
                var extraData = FindNearestExtraData(lodGroup.transform);
                var sig = ExtractLODGroupSignature(lodGroup, extraData, combineCache, prefab);
                if (sig != null)
                    results.Add(sig);
            }

            // extract unclaimed MeshRenderers
            var renderers = prefab.GetComponentsInChildren<MeshRenderer>();
            foreach (var renderer in renderers)
            {
                if (!renderer.enabled) continue;
                if (claimedRenderers != null && claimedRenderers.Contains(renderer))
                    continue;

                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;

                var extraData = FindNearestExtraData(renderer.transform);
                var meshLodData = renderer.GetComponent<GPUBatcherMeshLodData>();
                var sig = ExtractSingleRendererSignature(renderer, filter, extraData, meshLodData);
                if (sig != null)
                    results.Add(sig);
            }

            return results;
        }

        // extract a LODGroup into a single RenderSignature
        static RenderSignature ExtractLODGroupSignature(LODGroup lodGroup,
            GPUBatcherPrototypeExtraData extraData, MeshCombineCache combineCache, GameObject prefab)
        {
            var lods = lodGroup.GetLODs();

            // find a valid renderer to get renderingLayerMask from
            MeshRenderer firstRenderer = null;
            foreach (var lod in lods)
                if (lod.renderers != null)
                    foreach (var r in lod.renderers)
                        if (r is MeshRenderer mr && mr.enabled) { firstRenderer = mr; goto foundRenderer; }
            foundRenderer:

            if (firstRenderer == null) return null;

            var proto = new PrototypeData();
#if UNITY_EDITOR
            proto.sourcePrefab = prefab;
#endif

            var sig = new RenderSignature { asset = proto };
            sig.layer = lodGroup.gameObject.layer;
            sig.renderingLayerMask = firstRenderer.renderingLayerMask;
            ApplyExtraData(sig, extraData);

            proto.lodReferenceSize = lodGroup.size;
            proto.fadeMode = lodGroup.fadeMode;
            sig.animateCrossFading = lodGroup.animateCrossFading;

            proto.lodMeshes = new Mesh[lods.Length];
            proto.lodMaterials = new Material[lods.Length][];
            proto.lodScreenSizes = new float[lods.Length];
            proto.lodFadeTransitionWidths = new float[lods.Length];
            proto.lodShadowCastingModes = new ShadowCastingMode[lods.Length];
            proto.lodMotionVectorModes = new MotionVectorGenerationMode[lods.Length];

            for (int i = 0; i < lods.Length; i++)
            {
                var lod = lods[i];
                proto.lodScreenSizes[i] = lod.screenRelativeTransitionHeight;
                proto.lodFadeTransitionWidths[i] = lod.fadeTransitionWidth;

                ExtractLODRenderers(lod, lodGroup.transform, combineCache, prefab.name, i,
                    out proto.lodMeshes[i], out proto.lodMaterials[i],
                    out proto.lodShadowCastingModes[i], out proto.lodMotionVectorModes[i]);
            }

            proto.mesh = proto.lodMeshes[0];
            proto.materials = proto.lodMaterials[0];

            // SpeedTree percentage fade
            if (proto.fadeMode == LODFadeMode.SpeedTree)
            {
                int billboardIdx = -1;
                for (int i = 0; i < proto.lodMeshes.Length; i++)
                {
                    if (proto.lodMeshes[i] == null) { billboardIdx = i; break; }
                }
                int pctCount = (billboardIdx >= 0) ? billboardIdx - 1 : proto.lodMeshes.Length - 1;
                proto.percentageFadeMask = pctCount > 0 ? (1u << pctCount) - 1u : 0u;
            }

            // SpeedTree renderer ID (for wind)
            if (GPUBatcherConfig.instance.enableSpeedTreeWind)
            {
                var tree = lodGroup.GetComponentInChildren<Tree>();
                bool hasLod0Renderer = lods[0].renderers?.Length > 0 && lods[0].renderers[0] != null;
                if (tree != null && hasLod0Renderer)
                    proto.AssignRendererID(lods[0].renderers[0]);
            }

            ApplyMinScreenSize(sig, proto, extraData, true);
            ApplyMotionVectorMode(sig, proto);

            return sig;
        }

        // extract a single MeshRenderer into a RenderSignature, with optional mesh LOD
        static RenderSignature ExtractSingleRendererSignature(MeshRenderer renderer, MeshFilter filter,
            GPUBatcherPrototypeExtraData extraData, GPUBatcherMeshLodData meshLodData)
        {
            var proto = new PrototypeData();
#if UNITY_EDITOR
            proto.sourcePrefab = renderer.gameObject;
#endif

            var sig = new RenderSignature { asset = proto };
            sig.layer = renderer.gameObject.layer;
            sig.renderingLayerMask = renderer.renderingLayerMask;
            ApplyExtraData(sig, extraData);

#if UNITY_6000_2_OR_NEWER
            if (meshLodData != null && filter.sharedMesh.lodCount > 1)
            {
                // mesh LOD path
                Mesh mesh = filter.sharedMesh;
                int numLevels = meshLodData.meshLodNumLevels;
                int maxMeshLod = mesh.lodCount - 1;

                proto.mesh = mesh;
                proto.materials = renderer.sharedMaterials;
                proto.usesMeshLod = true;
                proto.meshLodIndices = new int[numLevels];

                proto.lodMeshes = new Mesh[numLevels];
                proto.lodMaterials = new Material[numLevels][];
                proto.lodScreenSizes = new float[numLevels];
                proto.lodFadeTransitionWidths = new float[numLevels];
                proto.lodShadowCastingModes = new ShadowCastingMode[numLevels];
                proto.lodMotionVectorModes = new MotionVectorGenerationMode[numLevels];

                float minScreen = extraData != null ? extraData.minScreenSize * 0.01f : 0f;

                for (int i = 0; i < numLevels; i++)
                {
                    proto.lodMeshes[i] = mesh;
                    proto.lodMaterials[i] = renderer.sharedMaterials;

                    if (i < numLevels - 1)
                    {
                        proto.lodScreenSizes[i] = (meshLodData.meshLodScreenSizes != null && i + 1 < meshLodData.meshLodScreenSizes.Length)
                            ? meshLodData.meshLodScreenSizes[i + 1] : 0f;
                    }
                    else
                    {
                        proto.lodScreenSizes[i] = minScreen;
                    }

                    proto.lodFadeTransitionWidths[i] = (meshLodData.meshLodFadeWidths != null && i < meshLodData.meshLodFadeWidths.Length)
                        ? meshLodData.meshLodFadeWidths[i] : 0.5f;
                    proto.lodShadowCastingModes[i] = renderer.shadowCastingMode;
                    proto.lodMotionVectorModes[i] = renderer.motionVectorGenerationMode;
                    proto.meshLodIndices[i] = (meshLodData.meshLodIndices != null && i < meshLodData.meshLodIndices.Length)
                        ? math.clamp(meshLodData.meshLodIndices[i], 0, maxMeshLod) : math.min(i, maxMeshLod);
                }

                proto.lodReferenceSize = mesh.bounds.size.magnitude;

                switch (meshLodData.meshLodFadeMode)
                {
                    case MinScreenSizeFadeMode.Crossfade:
                        proto.fadeMode = LODFadeMode.CrossFade;
                        break;
                    case MinScreenSizeFadeMode.AnimatedCrossfade:
                        proto.fadeMode = LODFadeMode.CrossFade;
                        sig.animateCrossFading = true;
                        break;
                    default:
                        proto.fadeMode = LODFadeMode.None;
                        break;
                }
            }
            else
#endif
            {
                // single LOD path
                proto.mesh = filter.sharedMesh;
                proto.materials = renderer.sharedMaterials;
                proto.lodMeshes = new Mesh[] { proto.mesh };
                proto.lodMaterials = new Material[][] { proto.materials };
                proto.lodScreenSizes = new float[] { 0f };
                proto.lodFadeTransitionWidths = new float[] { 0f };
                proto.lodShadowCastingModes = new ShadowCastingMode[] { renderer.shadowCastingMode };
                proto.lodMotionVectorModes = new MotionVectorGenerationMode[] { renderer.motionVectorGenerationMode };
                proto.fadeMode = LODFadeMode.None;
                if (filter.sharedMesh != null)
                {
                    var s = filter.sharedMesh.bounds.size;
                    proto.lodReferenceSize = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                }
            }

            ApplyMinScreenSize(sig, proto, extraData, false);
            ApplyMotionVectorMode(sig, proto);

            return sig;
        }

        private static readonly List<(MeshRenderer mr, MeshFilter mf)> s_extractValidRenderers = new();
        private static readonly List<Material> s_extractMaterials = new();

        // extract mesh/materials from a single LOD level
        static void ExtractLODRenderers(LOD lod, Transform root, MeshCombineCache combineCache,
            string prefabName, int lodIndex,
            out Mesh mesh, out Material[] materials,
            out ShadowCastingMode shadowMode, out MotionVectorGenerationMode motionVectorMode)
        {
            mesh = null;
            materials = null;
            shadowMode = ShadowCastingMode.On;
            motionVectorMode = MotionVectorGenerationMode.Camera;

            if (lod.renderers == null || lod.renderers.Length == 0) return;

            var validRenderers = s_extractValidRenderers;
            validRenderers.Clear();
            for (int r = 0; r < lod.renderers.Length; r++)
            {
                if (lod.renderers[r] == null) continue;
                var mr = lod.renderers[r] as MeshRenderer;
                if (mr == null) continue;
                if (!mr.enabled) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                validRenderers.Add((mr, mf));
            }

            if (validRenderers.Count == 0) return;

            bool needsCombine = false;
            // combining disabled: fall through to first-renderer fast path, ignore offset transforms
            if (GPUBatcherConfig.instance.combineMultiMeshLODs)
            {
                if (validRenderers.Count > 1)
                {
                    needsCombine = true;
                }
                else if (root != null)
                {
                    // single renderer - check for non-identity local transform relative to root
                    var t = validRenderers[0].mr.transform;
                    if (t != root && !IsApproxIdentity(GPUBatcherHelpers.LocalMatrixRelativeTo(t, root)))
                        needsCombine = true;
                }
            }

            if (!needsCombine)
            {
                // fast path: single renderer, identity transform
                mesh = validRenderers[0].mf.sharedMesh;
                materials = validRenderers[0].mr.sharedMaterials;
                shadowMode = validRenderers[0].mr.shadowCastingMode;
                motionVectorMode = validRenderers[0].mr.motionVectorGenerationMode;
                return;
            }

            // combine path: merge meshes and collect all materials
            var meshes = new Mesh[validRenderers.Count];
            var localMatrices = new Matrix4x4[validRenderers.Count];
            var allMaterials = s_extractMaterials;
            allMaterials.Clear();

            shadowMode = ShadowCastingMode.Off;
            for (int r = 0; r < validRenderers.Count; r++)
            {
                var (mr, mf) = validRenderers[r];
                meshes[r] = mf.sharedMesh;
                localMatrices[r] = root != null ? GPUBatcherHelpers.LocalMatrixRelativeTo(mr.transform, root) : mr.transform.localToWorldMatrix;

                // collect materials from each renderer in order
                var mats = mr.sharedMaterials;
                for (int m = 0; m < mats.Length; m++)
                    allMaterials.Add(mats[m]);

                if (mr.shadowCastingMode > shadowMode)
                    shadowMode = mr.shadowCastingMode;

                // use first renderer's motion vector mode
                if (r == 0)
                    motionVectorMode = mr.motionVectorGenerationMode;
            }

            materials = allMaterials.ToArray();

            if (combineCache != null)
            {
                string debugName = $"{prefabName}_LOD{lodIndex}_Combined";
                mesh = combineCache.GetOrCombine(meshes, localMatrices, debugName);
            }
            else
            {
                // no cache available - create a one-off combined mesh
                Debug.LogWarning($"GPUBatcherRenderer: Mesh combining for '{prefabName}' LOD{lodIndex} without cache (mesh will leak)");
                var combines = new CombineInstance[validRenderers.Count];
                for (int r = 0; r < validRenderers.Count; r++)
                {
                    combines[r].mesh = meshes[r];
                    combines[r].transform = localMatrices[r];
                    combines[r].subMeshIndex = 0;
                }
                mesh = new Mesh();
                mesh.CombineMeshes(combines, mergeSubMeshes: false, useMatrices: true);
                mesh.name = $"{prefabName}_LOD{lodIndex}_Combined";
                mesh.RecalculateBounds();
            }
        }

        static bool IsApproxIdentity(Matrix4x4 m)
        {
            const float k_eps = 0.001f;
            return math.abs(m.m00 - 1f) < k_eps && math.abs(m.m01) < k_eps && math.abs(m.m02) < k_eps && math.abs(m.m03) < k_eps
                && math.abs(m.m10) < k_eps && math.abs(m.m11 - 1f) < k_eps && math.abs(m.m12) < k_eps && math.abs(m.m13) < k_eps
                && math.abs(m.m20) < k_eps && math.abs(m.m21) < k_eps && math.abs(m.m22 - 1f) < k_eps && math.abs(m.m23) < k_eps;
        }

        private void UpdatePrototypeFlag(int slot)
        {
            int protoCount = _renderSignatures.Count;
            EnsureProtoArrayCapacity(protoCount);
            EnsureProtoBreakdownStride(protoCount);

            if (_prototypeVacant != null && slot < _prototypeVacant.Length && _prototypeVacant[slot])
                return;

            var sig = _renderSignatures[slot];
            _prototypeMaxShadowDistance[slot] = sig.maxShadowDistance;
            _prototypeCrossfadeEnabled[slot] = sig.fadeMode != LODFadeMode.None;
            _prototypeAnimatedCrossfade[slot] = sig.animateCrossFading;
            _prototypeMaxShadowCascade[slot] = math.clamp(sig.maxShadowCascade, 1, 4);
            _prototypeShadowMaskCache[slot] = sig.GetLODShadowMask();
        }

        private void EnsureProtoArrayCapacity(int required)
        {
            if (_protoArrayCapacity >= required && _prototypeMaxShadowDistance.IsCreated)
                return;

            int newCap = math.max(8, _protoArrayCapacity);
            while (newCap < required) newCap *= 2;

            int copyCount = math.min(_protoArrayCapacity, required);
            ResizeNativeArray(ref _prototypeMaxShadowDistance, newCap, copyCount);
            ResizeNativeArray(ref _prototypeCrossfadeEnabled, newCap, copyCount);
            ResizeNativeArray(ref _prototypeAnimatedCrossfade, newCap, copyCount);
            ResizeNativeArray(ref _prototypeMaxShadowCascade, newCap, copyCount);
            ResizeNativeArray(ref _prototypeShadowMaskCache, newCap, copyCount);
            ResizeNativeArray(ref _protoInstanceCounts, newCap, copyCount);
            ResizeNativeArray(ref _protoInstanceCountHWM, newCap, copyCount);
            ResizeNativeArray(ref _protoFlipCounts, newCap, copyCount);
            ResizeNativeArray(ref _protoFlipCountHWM, newCap, copyCount);

            _protoArrayCapacity = newCap;
            RefreshCachedPointers();
        }

        // resize per-chunk breakdown 2D array when stride changes (kept exact-sized, not amortized)
        private void EnsureProtoBreakdownStride(int newStride)
        {
            if (!_chunkProtoBreakdown.IsCreated || _protoBreakdownStride == newStride) return;

            int chunkCount = _maxChunks;
            var newBreakdown = new NativeArray<int>(chunkCount * newStride, Allocator.Persistent);
            int copyStride = math.min(_protoBreakdownStride, newStride);
            int copyBytes = copyStride * sizeof(int);
            unsafe
            {
                byte* src = (byte*)_chunkProtoBreakdown.GetUnsafeReadOnlyPtr();
                byte* dst = (byte*)newBreakdown.GetUnsafePtr();
                for (int c = 0; c < chunkCount; c++)
                    UnsafeUtility.MemCpy(dst + c * newStride * sizeof(int),
                                         src + c * _protoBreakdownStride * sizeof(int),
                                         copyBytes);
            }
            _chunkProtoBreakdown.Dispose();
            _chunkProtoBreakdown = newBreakdown;
            _protoBreakdownStride = newStride;
        }

        #endregion

        #region Instance Tracking

        // subtract a chunk's per-prototype counts when freed
        private void SubtractProtoInstanceCounts(int chunkId)
        {
            if (!_protoInstanceCounts.IsCreated || !_chunkProtoBreakdown.IsCreated) return;

            int breakdownBase = chunkId * _protoBreakdownStride;
            int limit = math.min(_protoBreakdownStride, _protoInstanceCounts.Length);
            for (int i = 0; i < limit; i++)
            {
                _protoInstanceCounts[i] = math.max(0, _protoInstanceCounts[i] - _chunkProtoBreakdown[breakdownBase + i]);
                _chunkProtoBreakdown[breakdownBase + i] = 0;
            }
            if (_chunkHasAnimatedCrossfade.IsCreated)
            {
                if (_chunkHasAnimatedCrossfade[chunkId])
                    _animatedPoolListDirty = true;
                _chunkHasAnimatedCrossfade[chunkId] = false;
            }
            if (_chunkLayerMask.IsCreated)
                _chunkLayerMask[chunkId] = 0;

            // subtract flip counts by scanning pool slots
            if (_slotFlipped.IsCreated && _protoFlipCounts.IsCreated)
            {
                int poolBaseIdx = chunkId * _maxPoolRefsPerChunk;
                var chunk = _chunks[chunkId];
                for (int p = 0; p < chunk.poolCount; p++)
                {
                    int poolIdx = _chunkPools[poolBaseIdx + p];
                    if (poolIdx < 0) continue;
                    int slotBase = poolIdx * k_poolSize;
                    for (int s = 0; s < k_poolSize; s++)
                    {
                        int globalSlot = slotBase + s;
                        if (_slotFlipped[globalSlot] && _slotProtoIndex[globalSlot] >= 0)
                        {
                            int pi = _slotProtoIndex[globalSlot];
                            if (pi < _protoFlipCounts.Length)
                                _protoFlipCounts[pi] = math.max(0, _protoFlipCounts[pi] - 1);
                            _slotFlipped[globalSlot] = false;
                        }
                    }
                }
            }
        }

        #endregion
    }
}
