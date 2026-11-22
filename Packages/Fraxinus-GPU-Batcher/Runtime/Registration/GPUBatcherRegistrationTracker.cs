// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    // handle to a BRG chunk, tracked for automatic cleanup on shutdown
    public readonly struct ChunkLink : IEquatable<ChunkLink>
    {
        public readonly int id;
        public readonly int generation;
        public bool isValid => id >= 0;
        // ABA-safe liveness check
        public bool isAlive => id >= 0 && GPUBatcherRenderer.instance != null && GPUBatcherRenderer.instance.IsChunkValid(id, generation);
        public ChunkLink(int id) { this.id = id; generation = GPUBatcherRenderer.instance?.GetChunkGeneration(id) ?? 0; }
        public ChunkLink(int id, int generation) { this.id = id; this.generation = generation; }
        public int numActivePools => GPUBatcherRenderer.instance.GetChunkPoolCount(id);
        public int GetPoolGlobalIndex(int poolLocalIdx) => GPUBatcherRenderer.instance.GetChunkPoolIndex(id, poolLocalIdx);
        public int NumInstancesInPool(int poolLocalIdx) => GPUBatcherRenderer.instance.GetPoolOccupancy(GetPoolGlobalIndex(poolLocalIdx));
        public static readonly ChunkLink invalid = new(-1, -1);

        public bool Equals(ChunkLink other) => id == other.id && generation == other.generation;
        public override bool Equals(object obj) => obj is ChunkLink other && Equals(other);
        public override int GetHashCode() => id ^ (generation << 16);
        public override string ToString() => isValid ? $"Chunk({id}g{generation})" : "Chunk(Invalid)";
        public static bool operator ==(ChunkLink a, ChunkLink b) => a.id == b.id && a.generation == b.generation;
        public static bool operator !=(ChunkLink a, ChunkLink b) => a.id != b.id || a.generation != b.generation;
    }

    // handle to a single instance within a BRG chunk (chunk ID + global slot index)
    public readonly struct InstanceLink : IEquatable<InstanceLink>
    {
        // May be an overflow chunk; use this ID for removal, not the primary chunk ID.
        public readonly int chunkId;
        public readonly int globalBRGSlotIndex;
        public bool isValid => globalBRGSlotIndex >= 0;
        public InstanceLink(int chunkId, int slotIndex) { this.chunkId = chunkId; globalBRGSlotIndex = slotIndex; }
        public static readonly InstanceLink invalid = new(-1, -1);

        public bool Equals(InstanceLink other) => chunkId == other.chunkId && globalBRGSlotIndex == other.globalBRGSlotIndex;
        public override bool Equals(object obj) => obj is InstanceLink other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(chunkId, globalBRGSlotIndex);
        public override string ToString() => isValid ? $"Instance(chunk={chunkId}, slot={globalBRGSlotIndex})" : "Instance(Invalid)";
        public static bool operator ==(InstanceLink a, InstanceLink b) => a.Equals(b);
        public static bool operator !=(InstanceLink a, InstanceLink b) => !a.Equals(b);
    }

    public class GPUBatcherRegistrationTracker
    {
        public GPUBatcherRenderer gpuBatcherSystem { get; private set; }
        public bool isRegistered { get; private set; }

        public string name { get; set; }

        // fires sync during immediate writes, deferred after CommitDeferredWrites
        public event Action<int> chunkWritten;

        public Action initializeCallback { get; set; }
        public Action shutdownCallback { get; set; }

        private readonly List<int> _registeredProtos = new();

        private readonly HashSet<int> _allocatedChunkIds = new();
        private bool _renderEnabled = true;

        // overflow chunk tracking - maps primary chunk ID -> overflow chunk IDs
        private readonly Dictionary<int, List<int>> _chunkOverflows = new();
#if UNITY_EDITOR
        private readonly HashSet<int> _overflowChunkIds = new();

        /// <summary>Enumerates primary (non-overflow) chunk IDs owned by this tracker.</summary>
        public IEnumerable<int> primaryChunkIds
        {
            get
            {
                foreach (int id in _allocatedChunkIds)
                {
                    if (!_overflowChunkIds.Contains(id))
                        yield return id;
                }
            }
        }
#endif

        /// <summary>Returns overflow chunk IDs for a primary chunk, or null if none.</summary>
        public List<int> GetOverflowChunkIds(int primaryChunkId)
        {
            return _chunkOverflows.TryGetValue(primaryChunkId, out var list) ? list : null;
        }
        private readonly List<int> _pendingHandleChunkIds = new();

        // sub-chunk readiness - coalesces overflow notifications into one primary callback
        private readonly Dictionary<int, int> _pendingReadinessCount = new(); // primary chunk ID -> sub-chunks still pending
        private readonly Dictionary<int, int> _subChunkToPrimary = new(); // sub-chunk ID -> primary chunk ID

        public GPUBatcherRegistrationTracker(string name = null)
        {
            this.name = name ?? "GPUBatcherRegistrationTracker";
        }

        #region core lifecycle

        public bool Initialize()
        {
            if (isRegistered) return true;

            var instance = GPUBatcherRenderer.instance;
            if (instance == null)
            {
                Debug.LogError($"{name}: GPUBatcherRenderer.Instance is null");
                return false;
            }
            if (!instance.isInitialized)
            {
                Debug.LogError($"{name}: GPUBatcherRenderer.Instance exists but is not initialized");
                return false;
            }

            gpuBatcherSystem = instance;
            isRegistered = true;
            gpuBatcherSystem.TrackRegistration(this);

            if (initializeCallback != null)
            {
                try
                {
                    initializeCallback();
                }
                catch (Exception e)
                {
                    Shutdown(); // Roll back chunks and prototypes acquired by the callback.
                    Debug.LogError($"{name}: InitializeCallback failed: {e.Message}\n{e.StackTrace}");
                    return false;
                }
            }

            return true;
        }

        // frees all tracked chunks and unregisters all prototypes
        public void Shutdown()
        {
            if (!isRegistered) return;

            shutdownCallback?.Invoke();
            gpuBatcherSystem?.UntrackRegistration(this);
            CleanupBRGResources();
            gpuBatcherSystem = null;
            isRegistered = false;
        }

        #endregion

        #region prototype registration

        public RenderSignature ExtractRenderSignature(GameObject prefab)
            => GPUBatcherRenderer.ExtractRenderSignature(prefab, gpuBatcherSystem?.meshCombineCache);

        // tracked for automatic unregister on Shutdown
        public int RegisterRenderSignatureFromPrefab(GameObject prefab)
        {
            int globalIdx = gpuBatcherSystem.RegisterRenderSignatureFromPrefab(prefab);
            if (globalIdx >= 0)
                _registeredProtos.Add(globalIdx);
            return globalIdx;
        }

        public int[] RegisterRenderSignaturesFromPrefab(GameObject prefab)
        {
            var indices = gpuBatcherSystem.RegisterRenderSignaturesFromPrefab(prefab);
            if (indices != null)
                foreach (var idx in indices)
                    if (idx >= 0) _registeredProtos.Add(idx);
            return indices;
        }

        public int RegisterRenderSignature(RenderSignature signature)
        {
            int globalIdx = gpuBatcherSystem.RegisterRenderSignature(signature);
            if (globalIdx >= 0)
                _registeredProtos.Add(globalIdx);
            return globalIdx;
        }

        public void UnregisterRenderSignature(int globalIdx)
        {
            if (globalIdx < 0 || gpuBatcherSystem == null) return;
            gpuBatcherSystem.UnregisterRenderSignature(globalIdx);
            _registeredProtos.Remove(globalIdx);
        }

        #endregion

        #region chunk lifecycle

        public int maxInstancesPerChunk => gpuBatcherSystem?.maxInstancesPerChunk ?? 2048;

        public ChunkLink AllocChunk()
        {
            if (gpuBatcherSystem == null) return new ChunkLink(-1);
            int id = gpuBatcherSystem.AllocateChunk();
            if (id >= 0)
            {
                _allocatedChunkIds.Add(id);
                if (!_renderEnabled)
                    gpuBatcherSystem.SetChunkEnabled(id, false);
            }
            return new ChunkLink(id);
        }

        public void WriteChunk(ChunkLink link, NativeArray<InstanceData> instances, Bounds bounds,
                               float cullDistance = 0f, float instanceCullDistance = float.MaxValue)
        {
            // clear any previous overflow chunks (re-write on same ChunkLink)
            CancelPendingReadiness(link.id);
            DropPendingForChunk(link.id);
            if (_chunkOverflows.Remove(link.id, out var oldOverflows))
            {
                foreach (int id in oldOverflows)
                {
                    DropPendingForChunk(id); // before FreeChunk zeroes pool indices
#if UNITY_EDITOR
                    _overflowChunkIds.Remove(id);
#endif
                    if (_allocatedChunkIds.Remove(id))
                        gpuBatcherSystem.FreeChunk(id);
                }
            }

            int max = gpuBatcherSystem.maxInstancesPerChunk;

            // fast path - fits in single chunk
            if (instances.Length <= max)
            {
                gpuBatcherSystem.WriteChunk(link.id, instances, bounds, cullDistance, instanceCullDistance);
                BeginTrackingHandles(link.id, 1);
                TrackSubChunkReady(link.id);
                return;
            }

            // overflow: split across primary + overflow chunks
            int remaining = instances.Length;
            int offset = 0;

            int count = math.min(remaining, max);
            gpuBatcherSystem.WriteChunk(link.id, instances.GetSubArray(0, count), bounds,
                                 cullDistance, instanceCullDistance);
            offset += count;
            remaining -= count;

            var overflowIds = new List<int>();
            while (remaining > 0)
            {
                count = math.min(remaining, max);
                var overflow = AllocChunk();
                if (!overflow.isValid)
                {
                    Debug.LogWarning($"{name}: Failed to allocate overflow chunk, {remaining} instances dropped");
                    break;
                }
                gpuBatcherSystem.WriteChunk(overflow.id, instances.GetSubArray(offset, count), bounds,
                                     cullDistance, instanceCullDistance);

                GetOrCreateOverflowList(link.id).Add(overflow.id);
                overflowIds.Add(overflow.id);
#if UNITY_EDITOR
                _overflowChunkIds.Add(overflow.id);
#endif
                offset += count;
                remaining -= count;
            }

            // track readiness after all sub-chunks are written
            BeginTrackingHandles(link.id, 1 + overflowIds.Count);
            foreach (int id in overflowIds)
                _subChunkToPrimary[id] = link.id;
            TrackSubChunkReady(link.id);
            foreach (int id in overflowIds)
                TrackSubChunkReady(id);
        }

        // resolves a logical instance index into its current InstanceLink.
        // localIndex spans primary + overflow chunks at fixed maxPerChunk stride.
        public InstanceLink GetInstanceLink(ChunkLink link, int localIndex)
        {
            if (gpuBatcherSystem == null || !gpuBatcherSystem.IsChunkValid(link.id, link.generation))
                return InstanceLink.invalid;
            return ResolveInstanceLink(link.id, localIndex);
        }

        // includes overflow chunks
        public int GetTotalChunkInstanceCount(ChunkLink link)
        {
            if (gpuBatcherSystem == null) return 0;
            int total = gpuBatcherSystem.GetChunkInstanceCount(link.id);
            if (_chunkOverflows.TryGetValue(link.id, out var overflows))
            {
                foreach (int id in overflows)
                    total += gpuBatcherSystem.GetChunkInstanceCount(id);
            }
            return total;
        }

        public int GetOverflowChunkCount(ChunkLink link)
        {
            return _chunkOverflows.TryGetValue(link.id, out var overflows) ? overflows.Count : 0;
        }

        // outCounts: primary chunk [0] then each overflow chunk [1..N]
        public void GetPerChunkInstanceCounts(ChunkLink link, List<int> outCounts)
        {
            outCounts.Clear();
            if (gpuBatcherSystem == null) return;
            outCounts.Add(gpuBatcherSystem.GetChunkInstanceCount(link.id));
            if (_chunkOverflows.TryGetValue(link.id, out var overflows))
            {
                foreach (int id in overflows)
                    outCounts.Add(gpuBatcherSystem.GetChunkInstanceCount(id));
            }
        }

        // idempotent - also frees overflow chunks
        public void ReleaseChunk(ChunkLink link)
        {
            CancelPendingReadiness(link.id);

            // drop pending ops for slots in chunks about to be freed, before pool indices go stale
            DropPendingForChunk(link.id);
            if (_chunkOverflows.TryGetValue(link.id, out var overflowIdsForDrop))
                foreach (int id in overflowIdsForDrop)
                    DropPendingForChunk(id);

            if (link.isValid && _allocatedChunkIds.Remove(link.id))
                gpuBatcherSystem.FreeChunk(link.id);

            if (_chunkOverflows.Remove(link.id, out var overflows))
            {
                foreach (int id in overflows)
                {
#if UNITY_EDITOR
                    _overflowChunkIds.Remove(id);
#endif
                    if (_allocatedChunkIds.Remove(id))
                        gpuBatcherSystem.FreeChunk(id);
                }
            }
        }

        // primary covers [0, maxPerChunk), overflows[i] covers [(i+1)*maxPerChunk, (i+2)*maxPerChunk)
        private InstanceLink ResolveInstanceLink(int primaryChunkId, int localIndex)
        {
            if (localIndex < 0) return InstanceLink.invalid;
            int maxPerChunk = gpuBatcherSystem.maxInstancesPerChunk;
            int chunkOffset = localIndex / maxPerChunk;
            int localInChunk = localIndex % maxPerChunk;

            int chunkId;
            if (chunkOffset == 0)
                chunkId = primaryChunkId;
            else if (_chunkOverflows.TryGetValue(primaryChunkId, out var overflows)
                     && chunkOffset - 1 < overflows.Count)
                chunkId = overflows[chunkOffset - 1];
            else
                return InstanceLink.invalid;

            int poolSize = gpuBatcherSystem.poolSize;
            int poolSlot = localInChunk / poolSize;
            int slotInPool = localInChunk % poolSize;
            int poolIdx = gpuBatcherSystem.GetChunkPoolIndex(chunkId, poolSlot);
            if (poolIdx < 0) return InstanceLink.invalid;
            return new InstanceLink(chunkId, poolIdx * poolSize + slotInPool);
        }

        private void DropPendingForChunk(int chunkId)
        {
            if (gpuBatcherSystem == null) return;
            if (_pendingPatches == null && _pendingColors == null && _pendingSigOverrides == null) return;

            int poolCount = gpuBatcherSystem.GetChunkPoolCount(chunkId);
            int poolSize = gpuBatcherSystem.poolSize;
            for (int p = 0; p < poolCount; p++)
            {
                int poolIdx = gpuBatcherSystem.GetChunkPoolIndex(chunkId, p);
                if (poolIdx < 0) continue;
                int slotBase = poolIdx * poolSize;
                for (int s = 0; s < poolSize; s++)
                {
                    int slot = slotBase + s;
                    _pendingPatches?.Remove(slot);
                    _pendingColors?.Remove(slot);
                    _pendingSigOverrides?.Remove(slot);
                }
            }
        }

        public void RecalculateChunkBounds(ChunkLink link, Bounds newBounds)
        {
            gpuBatcherSystem.RecalculateChunkBounds(link.id, newBounds);
            if (_chunkOverflows.TryGetValue(link.id, out var overflows))
                foreach (int id in overflows)
                    gpuBatcherSystem.RecalculateChunkBounds(id, newBounds);
        }

        public void SetChunkCullDistances(ChunkLink link, float cullDistance, float instanceCullDistance)
        {
            gpuBatcherSystem.SetChunkCullDistances(link.id, cullDistance, instanceCullDistance);
            if (_chunkOverflows.TryGetValue(link.id, out var overflows))
                foreach (int id in overflows)
                    gpuBatcherSystem.SetChunkCullDistances(id, cullDistance, instanceCullDistance);
        }

        public void SetChunkEnabled(ChunkLink link, bool enabled)
        {
            gpuBatcherSystem.SetChunkEnabled(link.id, enabled);
            if (_chunkOverflows.TryGetValue(link.id, out var overflows))
                foreach (int id in overflows)
                    gpuBatcherSystem.SetChunkEnabled(id, enabled);
        }

        public bool GetChunkEnabled(ChunkLink link)
        {
            return gpuBatcherSystem.GetChunkEnabled(link.id);
        }

        public void SetRenderEnabled(bool enabled)
        {
            _renderEnabled = enabled;
            if (gpuBatcherSystem == null) return;
            foreach (int id in _allocatedChunkIds)
                gpuBatcherSystem.SetChunkEnabled(id, enabled);
        }

        #endregion

        #region tracked staging (safe path)

        private const uint k_patchPosition = 1u;
        private const uint k_patchRotation = 2u;
        private const uint k_patchScale    = 4u;

        // per-slot staging accumulators - merged on write, flushed in FlushToCore
        private Dictionary<int, GPUBatcherRenderer.GPUTRSPatchEntry> _pendingPatches;
        private Dictionary<int, uint> _pendingColors;
        private Dictionary<int, bool> _pendingSigOverrides;
        private bool _hasPendingStaging;

        // shared scratch for FlushToCore across all trackers, disposed via DisposeStaticScratch
        private static NativeList<GPUBatcherRenderer.GPUTRSPatchEntry> s_scratchPatchEntries;
        private static NativeList<int> s_scratchPatchSlots;
        private static NativeList<GPUBatcherRenderer.GPUColorEntry> s_scratchColorEntries;

        private void InitStaging()
        {
            _pendingPatches ??= new Dictionary<int, GPUBatcherRenderer.GPUTRSPatchEntry>(64);
            _pendingColors ??= new Dictionary<int, uint>(16);
            _pendingSigOverrides ??= new Dictionary<int, bool>(16);
        }

        private static void EnsureScratch()
        {
            if (s_scratchPatchEntries.IsCreated) return;
            s_scratchPatchEntries = new NativeList<GPUBatcherRenderer.GPUTRSPatchEntry>(64, Allocator.Persistent);
            s_scratchPatchSlots = new NativeList<int>(64, Allocator.Persistent);
            s_scratchColorEntries = new NativeList<GPUBatcherRenderer.GPUColorEntry>(16, Allocator.Persistent);
        }

        public static void DisposeStaticScratch()
        {
            s_scratchPatchEntries.DisposeIfCreated();
            s_scratchPatchSlots.DisposeIfCreated();
            s_scratchColorEntries.DisposeIfCreated();
        }

        private void ClearStaging()
        {
            _pendingPatches?.Clear();
            _pendingColors?.Clear();
            _pendingSigOverrides?.Clear();
            _hasPendingStaging = false;
        }

        public void StagePosition(InstanceLink link, float3 position)
            => StagePatchInternal(link.globalBRGSlotIndex, k_patchPosition, position, default, default);

        public void StageRotation(InstanceLink link, quaternion rotation)
            => StagePatchInternal(link.globalBRGSlotIndex, k_patchRotation, default, rotation.value, default);

        public void StageScale(InstanceLink link, float3 scale)
            => StagePatchInternal(link.globalBRGSlotIndex, k_patchScale, default, default, scale);

        public void StageMoveAndRotate(InstanceLink link, float3 position, quaternion rotation)
            => StagePatchInternal(link.globalBRGSlotIndex, k_patchPosition | k_patchRotation, position, rotation.value, default);

        public void StageMove(InstanceLink link, float3 pos, quaternion rot, float3 scale)
            => StagePatchInternal(link.globalBRGSlotIndex, k_patchPosition | k_patchRotation | k_patchScale, pos, rot.value, scale);

        public void StageColor(InstanceLink link, Color32 color)
        {
            InitStaging();
            _pendingColors[link.globalBRGSlotIndex] = GPUBatcherBurstHelpers.PackColor(color);
            _hasPendingStaging = true;
        }

        public void StagePosition(int globalSlot, float3 position)
            => StagePatchInternal(globalSlot, k_patchPosition, position, default, default);

        public void StageRotation(int globalSlot, quaternion rotation)
            => StagePatchInternal(globalSlot, k_patchRotation, default, rotation.value, default);

        public void StageScale(int globalSlot, float3 scale)
            => StagePatchInternal(globalSlot, k_patchScale, default, default, scale);

        public void StageMoveAndRotate(int globalSlot, float3 position, quaternion rotation)
            => StagePatchInternal(globalSlot, k_patchPosition | k_patchRotation, position, rotation.value, default);

        public void StageMove(int globalSlot, float3 pos, quaternion rot, float3 scale)
            => StagePatchInternal(globalSlot, k_patchPosition | k_patchRotation | k_patchScale, pos, rot.value, scale);

        public void StageColor(int globalSlot, Color32 color)
        {
            InitStaging();
            _pendingColors[globalSlot] = GPUBatcherBurstHelpers.PackColor(color);
            _hasPendingStaging = true;
        }

        public void StageSetEnabled(InstanceLink link, bool enabled)
            => StageSetEnabled(link.globalBRGSlotIndex, enabled);

        public void StageSetEnabled(int globalSlot, bool enabled)
        {
            InitStaging();
            _pendingSigOverrides[globalSlot] = enabled;
            _hasPendingStaging = true;
        }

        // removes immediately, drops any tracker-side pending ops targeting the old instance
        public void StageRemove(int chunkId, int globalSlot)
        {
            if (gpuBatcherSystem == null) return;
            InitStaging();
            gpuBatcherSystem.RemoveInstance(chunkId, globalSlot);
            _pendingPatches.Remove(globalSlot);
            _pendingColors.Remove(globalSlot);
            _pendingSigOverrides.Remove(globalSlot);
        }

        public void StageRemove(InstanceLink link)
        {
            if (gpuBatcherSystem == null) return;
            InitStaging();

            gpuBatcherSystem.RemoveInstance(link.chunkId, link.globalBRGSlotIndex);
            _pendingPatches.Remove(link.globalBRGSlotIndex);
            _pendingColors.Remove(link.globalBRGSlotIndex);
            _pendingSigOverrides.Remove(link.globalBRGSlotIndex);
        }

        private void StagePatchInternal(int slot, uint flags, float3 pos, float4 rot, float3 scale)
        {
            if (slot < 0) return;
            InitStaging();

            if (_pendingPatches.TryGetValue(slot, out var existing))
            {
                if ((flags & k_patchPosition) != 0) existing.position = pos;
                if ((flags & k_patchRotation) != 0) existing.rotation = rot;
                if ((flags & k_patchScale)    != 0) existing.scale = scale;
                existing.flags |= flags;
                _pendingPatches[slot] = existing;
            }
            else
            {
                _pendingPatches[slot] = new GPUBatcherRenderer.GPUTRSPatchEntry
                {
                    slotIndex = (uint)slot,
                    flags = flags,
                    position = pos,
                    rotation = rot,
                    scale = scale
                };
            }
            _hasPendingStaging = true;
        }

        internal void FlushToCore()
        {
            if (!_hasPendingStaging || gpuBatcherSystem == null) return;
            EnsureScratch();

            if (_pendingPatches != null && _pendingPatches.Count > 0)
            {
                s_scratchPatchEntries.Clear();
                s_scratchPatchSlots.Clear();
                foreach (var kvp in _pendingPatches)
                {
                    s_scratchPatchEntries.Add(kvp.Value);
                    s_scratchPatchSlots.Add(kvp.Key);
                }
                int patchCount = s_scratchPatchEntries.Length;
                gpuBatcherSystem.BulkStageTRSPatches(s_scratchPatchEntries.AsArray(), s_scratchPatchSlots.AsArray(), patchCount);
            }

            if (_pendingColors != null && _pendingColors.Count > 0)
            {
                s_scratchColorEntries.Clear();
                foreach (var kvp in _pendingColors)
                {
                    s_scratchColorEntries.Add(new GPUBatcherRenderer.GPUColorEntry
                    {
                        slotIndex = (uint)kvp.Key,
                        packedColor = kvp.Value
                    });
                }
                int colorCount = s_scratchColorEntries.Length;
                gpuBatcherSystem.BulkStageColorWrites(s_scratchColorEntries.AsArray(), colorCount);
            }

            if (_pendingSigOverrides != null && _pendingSigOverrides.Count > 0)
            {
                foreach (var kvp in _pendingSigOverrides)
                    gpuBatcherSystem.SetInstanceEnabled(kvp.Key, kvp.Value);
            }

            ClearStaging();
        }

        #endregion

        #region unsafe direct staging

        // Bypasses tracker buffering and conflict resolution.
        public void SetPositionUnsafe(int globalSlot, float3 position)
            => gpuBatcherSystem?.SetInstancePosition(globalSlot, position);

        public void SetRotationUnsafe(int globalSlot, quaternion rotation)
            => gpuBatcherSystem?.SetInstanceRotation(globalSlot, rotation);

        public void SetScaleUnsafe(int globalSlot, float3 scale)
            => gpuBatcherSystem?.SetInstanceScale(globalSlot, scale);

        public void MoveAndRotateUnsafe(int globalSlot, float3 position, quaternion rotation)
            => gpuBatcherSystem?.MoveAndRotateInstance(globalSlot, position, rotation);

        public void MoveUnsafe(int globalSlot, float3 pos, quaternion rot, float3 scale)
            => gpuBatcherSystem?.MoveInstance(globalSlot, pos, rot, scale);

        public void SetColorUnsafe(int globalSlot, Color32 color)
            => gpuBatcherSystem?.SetInstanceColor(globalSlot, color);

        public void RemoveUnsafe(int chunkId, int globalSlot)
            => gpuBatcherSystem?.RemoveInstance(chunkId, globalSlot);

        public void SetEnabledUnsafe(int globalSlot, bool enabled)
            => gpuBatcherSystem?.SetInstanceEnabled(globalSlot, enabled);

        public void SetPositionUnsafe(InstanceLink link, float3 position)
            => gpuBatcherSystem?.SetInstancePosition(link.globalBRGSlotIndex, position);

        public void SetRotationUnsafe(InstanceLink link, quaternion rotation)
            => gpuBatcherSystem?.SetInstanceRotation(link.globalBRGSlotIndex, rotation);

        public void SetScaleUnsafe(InstanceLink link, float3 scale)
            => gpuBatcherSystem?.SetInstanceScale(link.globalBRGSlotIndex, scale);

        public void MoveAndRotateUnsafe(InstanceLink link, float3 position, quaternion rotation)
            => gpuBatcherSystem?.MoveAndRotateInstance(link.globalBRGSlotIndex, position, rotation);

        public void MoveUnsafe(InstanceLink link, float3 pos, quaternion rot, float3 scale)
            => gpuBatcherSystem?.MoveInstance(link.globalBRGSlotIndex, pos, rot, scale);

        public void SetColorUnsafe(InstanceLink link, Color32 color)
            => gpuBatcherSystem?.SetInstanceColor(link.globalBRGSlotIndex, color);

        public void RemoveUnsafe(InstanceLink link)
            => gpuBatcherSystem?.RemoveInstance(link.chunkId, link.globalBRGSlotIndex);

        public void SetEnabledUnsafe(InstanceLink link, bool enabled)
            => gpuBatcherSystem?.SetInstanceEnabled(link.globalBRGSlotIndex, enabled);

        public void SampleLightProbeUnsafe(int globalSlot, float3 position)
            => gpuBatcherSystem?.SampleInstanceLightProbe(globalSlot, position);

        public void SampleLightProbeUnsafe(InstanceLink link, float3 position)
            => gpuBatcherSystem?.SampleInstanceLightProbe(link.globalBRGSlotIndex, position);

        #endregion

        #region instance lifecycle

        public InstanceLink AddInstance(ChunkLink chunk, InstanceData instance)
        {
            // reject freed/stale ChunkLinks
            if (!chunk.isAlive || !_allocatedChunkIds.Contains(chunk.id))
                return InstanceLink.invalid;

            int slot = gpuBatcherSystem.AddInstance(chunk.id, instance);
            if (slot >= 0)
                return new InstanceLink(chunk.id, slot);

            if (_chunkOverflows.TryGetValue(chunk.id, out var overflows))
            {
                for (int i = overflows.Count - 1; i >= 0; i--)
                {
                    slot = gpuBatcherSystem.AddInstance(overflows[i], instance);
                    if (slot >= 0)
                        return new InstanceLink(overflows[i], slot);
                }
            }

            // all full - allocate new overflow chunk
            var overflow = AllocChunk();
            if (!overflow.isValid) return InstanceLink.invalid;

            slot = gpuBatcherSystem.AddInstance(overflow.id, instance);
            if (slot < 0)
            {
                if (_allocatedChunkIds.Remove(overflow.id))
                    gpuBatcherSystem.FreeChunk(overflow.id);
                return InstanceLink.invalid;
            }

#if DEBUG
            // GetInstanceLink fixed-stride formula assumes primary + non-last overflows are at max capacity
            Debug.Assert(gpuBatcherSystem.GetChunkPoolCount(chunk.id) == gpuBatcherSystem.maxInstancesPerChunk / gpuBatcherSystem.poolSize,
                "Overflow allocated while previous chunk was not at max capacity");
#endif
            GetOrCreateOverflowList(chunk.id).Add(overflow.id);
            return new InstanceLink(overflow.id, slot);
        }

        // add-before-remove - returns Invalid if destination full, source untouched
        public InstanceLink MoveInstanceToChunk(InstanceLink source, ChunkLink destChunk,
            InstanceData instanceData)
        {
            var newLink = AddInstance(destChunk, instanceData);
            if (!newLink.isValid) return InstanceLink.invalid;
            RemoveUnsafe(source);
            return newLink;
        }

        // adds via fresh pools with auto-overflow
        public InstanceLink[] AddInstances(ChunkLink chunk, NativeArray<InstanceData> instances, int count)
        {
            if (!_allocatedChunkIds.Contains(chunk.id))
                return Array.Empty<InstanceLink>();

            var result = new InstanceLink[count];
            using var slots = new NativeArray<int>(count, Allocator.Temp);

            int placed = gpuBatcherSystem.AppendInstances(chunk.id, instances, 0, count, slots);
            for (int i = 0; i < placed; i++)
                result[i] = new InstanceLink(chunk.id, slots[i]);

            int remaining = count - placed;
            int srcOffset = placed;

            while (remaining > 0)
            {
                var overflow = AllocChunk();
                if (!overflow.isValid)
                {
                    Debug.LogWarning($"{name}: Failed to allocate overflow chunk, {remaining} instances dropped");
                    for (int i = srcOffset; i < count; i++)
                        result[i] = InstanceLink.invalid;
                    break;
                }

                using var overflowSlots = new NativeArray<int>(remaining, Allocator.Temp);
                int overflowPlaced = gpuBatcherSystem.AppendInstances(overflow.id, instances, srcOffset, remaining, overflowSlots);

                if (overflowPlaced == 0)
                {
                    if (_allocatedChunkIds.Remove(overflow.id))
                        gpuBatcherSystem.FreeChunk(overflow.id);
                    Debug.LogWarning($"{name}: Pool allocation failed, {remaining} instances dropped");
                    for (int i = srcOffset; i < count; i++)
                        result[i] = InstanceLink.invalid;
                    break;
                }

                GetOrCreateOverflowList(chunk.id).Add(overflow.id);
                for (int i = 0; i < overflowPlaced; i++)
                    result[srcOffset + i] = new InstanceLink(overflow.id, overflowSlots[i]);

                srcOffset += overflowPlaced;
                remaining -= overflowPlaced;
            }

            return result;
        }

        #endregion

        #region private

        private List<int> GetOrCreateOverflowList(int primaryChunkId)
        {
            if (!_chunkOverflows.TryGetValue(primaryChunkId, out var list))
            {
                list = new List<int>();
                _chunkOverflows[primaryChunkId] = list;
            }
            return list;
        }

        private void CleanupBRGResources()
        {
            ClearStaging();

            if (gpuBatcherSystem != null && _pendingHandleChunkIds.Count > 0)
                gpuBatcherSystem.onDeferredWritesCompleted -= FlushPendingHandles;
            _pendingHandleChunkIds.Clear();
            _pendingReadinessCount.Clear();
            _subChunkToPrimary.Clear();

            if (gpuBatcherSystem != null)
            {
                if (gpuBatcherSystem.chunkSystemInitialized)
                {
                    foreach (int id in _allocatedChunkIds)
                        gpuBatcherSystem.FreeChunk(id);
                    gpuBatcherSystem.FlushStagedUploads();
                }
                UnregisterAllRenderSignatures();
            }

            _allocatedChunkIds.Clear();
            _chunkOverflows.Clear();
#if UNITY_EDITOR
            _overflowChunkIds.Clear();
#endif
            _registeredProtos.Clear();
        }

        // registers that primaryChunkId expects totalSubChunks readiness signals
        private void BeginTrackingHandles(int primaryChunkId, int totalSubChunks)
        {
            _pendingReadinessCount[primaryChunkId] = totalSubChunks;
            _subChunkToPrimary[primaryChunkId] = primaryChunkId;
        }

        // called per sub-chunk - checks immediate readiness or defers
        private void TrackSubChunkReady(int subChunkId)
        {
            if (gpuBatcherSystem.IsChunkWritten(subChunkId))
            {
                DecrementAndMaybeNotify(_subChunkToPrimary[subChunkId]);
            }
            else
            {
                if (_pendingHandleChunkIds.Count == 0)
                    gpuBatcherSystem.onDeferredWritesCompleted += FlushPendingHandles;
                _pendingHandleChunkIds.Add(subChunkId);
            }
        }

        private void DecrementAndMaybeNotify(int primaryChunkId)
        {
            if (!_pendingReadinessCount.TryGetValue(primaryChunkId, out int remaining))
                return;

            remaining--;
            if (remaining <= 0)
            {
                _pendingReadinessCount.Remove(primaryChunkId);
                _subChunkToPrimary.Remove(primaryChunkId);
                if (_chunkOverflows.TryGetValue(primaryChunkId, out var overflows))
                    foreach (int id in overflows)
                        _subChunkToPrimary.Remove(id);

                chunkWritten?.Invoke(primaryChunkId);
            }
            else
            {
                _pendingReadinessCount[primaryChunkId] = remaining;
            }
        }

        // cancels pending readiness tracking for a primary chunk and its overflows
        private void CancelPendingReadiness(int primaryChunkId)
        {
            if (!_pendingReadinessCount.Remove(primaryChunkId))
                return;

            _subChunkToPrimary.Remove(primaryChunkId);
            if (_chunkOverflows.TryGetValue(primaryChunkId, out var overflows))
            {
                foreach (int id in overflows)
                {
                    _subChunkToPrimary.Remove(id);
                    _pendingHandleChunkIds.Remove(id);
                }
            }
            _pendingHandleChunkIds.Remove(primaryChunkId);
        }

        private void FlushPendingHandles()
        {
            if (isRegistered)
            {
                foreach (int subChunkId in _pendingHandleChunkIds)
                {
                    if (_subChunkToPrimary.TryGetValue(subChunkId, out int primaryId))
                        DecrementAndMaybeNotify(primaryId);
                }
            }
            _pendingHandleChunkIds.Clear();
        }

        private void UnregisterAllRenderSignatures()
        {
            foreach (int globalIdx in _registeredProtos)
            {
                if (globalIdx >= 0)
                    gpuBatcherSystem.UnregisterRenderSignature(globalIdx);
            }
        }

        #endregion
    }
}
