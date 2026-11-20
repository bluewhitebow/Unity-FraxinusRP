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
        public readonly int Id;
        public readonly int Generation;
        public bool IsValid => Id >= 0;
        // ABA-safe liveness check
        public bool IsAlive => Id >= 0 && GPUBatcherRenderer.Instance != null && GPUBatcherRenderer.Instance.IsChunkValid(Id, Generation);
        public ChunkLink(int id) { Id = id; Generation = GPUBatcherRenderer.Instance?.GetChunkGeneration(id) ?? 0; }
        public ChunkLink(int id, int generation) { Id = id; Generation = generation; }
        public int NumActivePools => GPUBatcherRenderer.Instance.GetChunkPoolCount(Id);
        public int GetPoolGlobalIndex(int poolLocalIdx) => GPUBatcherRenderer.Instance.GetChunkPoolIndex(Id, poolLocalIdx);
        public int NumInstancesInPool(int poolLocalIdx) => GPUBatcherRenderer.Instance.GetPoolOccupancy(GetPoolGlobalIndex(poolLocalIdx));
        public static readonly ChunkLink Invalid = new(-1, -1);

        public bool Equals(ChunkLink other) => Id == other.Id && Generation == other.Generation;
        public override bool Equals(object obj) => obj is ChunkLink other && Equals(other);
        public override int GetHashCode() => Id ^ (Generation << 16);
        public override string ToString() => IsValid ? $"Chunk({Id}g{Generation})" : "Chunk(Invalid)";
        public static bool operator ==(ChunkLink a, ChunkLink b) => a.Id == b.Id && a.Generation == b.Generation;
        public static bool operator !=(ChunkLink a, ChunkLink b) => a.Id != b.Id || a.Generation != b.Generation;
    }

    // handle to a single instance within a BRG chunk (chunk ID + global slot index)
    public readonly struct InstanceLink : IEquatable<InstanceLink>
    {
        // may be an overflow chunk - use this ID for Remove, not the primary ChunkLink.Id
        public readonly int ChunkId;
        public readonly int GlobalBRGSlotIndex;
        public bool IsValid => GlobalBRGSlotIndex >= 0;
        public InstanceLink(int chunkId, int slotIndex) { ChunkId = chunkId; GlobalBRGSlotIndex = slotIndex; }
        public static readonly InstanceLink Invalid = new(-1, -1);

        public bool Equals(InstanceLink other) => ChunkId == other.ChunkId && GlobalBRGSlotIndex == other.GlobalBRGSlotIndex;
        public override bool Equals(object obj) => obj is InstanceLink other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ChunkId, GlobalBRGSlotIndex);
        public override string ToString() => IsValid ? $"Instance(chunk={ChunkId}, slot={GlobalBRGSlotIndex})" : "Instance(Invalid)";
        public static bool operator ==(InstanceLink a, InstanceLink b) => a.Equals(b);
        public static bool operator !=(InstanceLink a, InstanceLink b) => !a.Equals(b);
    }

    public class GPUBatcherRegistrationTracker
    {
        public GPUBatcherRenderer GPUBatcherSystem { get; private set; }
        public bool IsRegistered { get; private set; }

        public string Name { get; set; }

        // fires sync during immediate writes, deferred after CommitDeferredWrites
        public event Action<int> ChunkWritten;

        public Action InitializeCallback { get; set; }
        public Action ShutdownCallback { get; set; }

        // prototype tracking - global proto indices this tracker has registered
        private readonly List<int> _registeredProtos = new();

        // chunk tracking
        private readonly HashSet<int> _allocatedChunkIds = new();
        private bool _renderEnabled = true;

        // overflow chunk tracking - maps primary chunk ID -> overflow chunk IDs
        private readonly Dictionary<int, List<int>> _chunkOverflows = new();
#if UNITY_EDITOR
        private readonly HashSet<int> _overflowChunkIds = new();

        /// <summary>Enumerates primary (non-overflow) chunk IDs owned by this tracker.</summary>
        public IEnumerable<int> PrimaryChunkIds
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
            Name = name ?? "GPUBatcherRegistrationTracker";
        }

        #region core lifecycle

        public bool Initialize()
        {
            if (IsRegistered) return true;

            var instance = GPUBatcherRenderer.Instance;
            if (instance == null)
            {
                Debug.LogError($"{Name}: GPUBatcherRenderer.Instance is null");
                return false;
            }
            if (!instance.IsInitialized)
            {
                Debug.LogError($"{Name}: GPUBatcherRenderer.Instance exists but is not initialized");
                return false;
            }

            GPUBatcherSystem = instance;
            IsRegistered = true;
            GPUBatcherSystem.TrackRegistration(this);

            if (InitializeCallback != null)
            {
                try
                {
                    InitializeCallback();
                }
                catch (Exception e)
                {
                    Shutdown(); // invokes ShutdownCallback once and resets IsRegistered
                    Debug.LogError($"{Name}: InitializeCallback failed: {e.Message}\n{e.StackTrace}");
                    return false;
                }
            }

            return true;
        }

        // frees all tracked chunks and unregisters all prototypes
        public void Shutdown()
        {
            if (!IsRegistered) return;

            ShutdownCallback?.Invoke();
            GPUBatcherSystem?.UntrackRegistration(this);
            CleanupBRGResources();
            GPUBatcherSystem = null;
            IsRegistered = false;
        }

        #endregion

        #region prototype registration

        public RenderSignature ExtractRenderSignature(GameObject prefab)
            => GPUBatcherRenderer.ExtractRenderSignature(prefab, GPUBatcherSystem?.MeshCombineCache);

        // tracked for automatic unregister on Shutdown
        public int RegisterRenderSignatureFromPrefab(GameObject prefab)
        {
            int globalIdx = GPUBatcherSystem.RegisterRenderSignatureFromPrefab(prefab);
            if (globalIdx >= 0)
                _registeredProtos.Add(globalIdx);
            return globalIdx;
        }

        public int[] RegisterRenderSignaturesFromPrefab(GameObject prefab)
        {
            var indices = GPUBatcherSystem.RegisterRenderSignaturesFromPrefab(prefab);
            if (indices != null)
                foreach (var idx in indices)
                    if (idx >= 0) _registeredProtos.Add(idx);
            return indices;
        }

        public int RegisterRenderSignature(RenderSignature signature)
        {
            int globalIdx = GPUBatcherSystem.RegisterRenderSignature(signature);
            if (globalIdx >= 0)
                _registeredProtos.Add(globalIdx);
            return globalIdx;
        }

        public void UnregisterRenderSignature(int globalIdx)
        {
            if (globalIdx < 0 || GPUBatcherSystem == null) return;
            GPUBatcherSystem.UnregisterRenderSignature(globalIdx);
            _registeredProtos.Remove(globalIdx);
        }

        #endregion

        #region chunk lifecycle

        public int MaxInstancesPerChunk => GPUBatcherSystem?.MaxInstancesPerChunk ?? 2048;

        public ChunkLink AllocChunk()
        {
            if (GPUBatcherSystem == null) return new ChunkLink(-1);
            int id = GPUBatcherSystem.AllocateChunk();
            if (id >= 0)
            {
                _allocatedChunkIds.Add(id);
                if (!_renderEnabled)
                    GPUBatcherSystem.SetChunkEnabled(id, false);
            }
            return new ChunkLink(id);
        }

        public void WriteChunk(ChunkLink link, NativeArray<InstanceData> instances, Bounds bounds,
                               float cullDistance = 0f, float instanceCullDistance = float.MaxValue)
        {
            // clear any previous overflow chunks (re-write on same ChunkLink)
            CancelPendingReadiness(link.Id);
            DropPendingForChunk(link.Id);
            if (_chunkOverflows.Remove(link.Id, out var oldOverflows))
            {
                foreach (int id in oldOverflows)
                {
                    DropPendingForChunk(id); // before FreeChunk zeroes pool indices
#if UNITY_EDITOR
                    _overflowChunkIds.Remove(id);
#endif
                    if (_allocatedChunkIds.Remove(id))
                        GPUBatcherSystem.FreeChunk(id);
                }
            }

            int max = GPUBatcherSystem.MaxInstancesPerChunk;

            // fast path - fits in single chunk
            if (instances.Length <= max)
            {
                GPUBatcherSystem.WriteChunk(link.Id, instances, bounds, cullDistance, instanceCullDistance);
                BeginTrackingHandles(link.Id, 1);
                TrackSubChunkReady(link.Id);
                return;
            }

            // overflow: split across primary + overflow chunks
            int remaining = instances.Length;
            int offset = 0;

            // primary chunk gets first batch
            int count = math.min(remaining, max);
            GPUBatcherSystem.WriteChunk(link.Id, instances.GetSubArray(0, count), bounds,
                                 cullDistance, instanceCullDistance);
            offset += count;
            remaining -= count;

            // overflow batches
            var overflowIds = new List<int>();
            while (remaining > 0)
            {
                count = math.min(remaining, max);
                var overflow = AllocChunk();
                if (!overflow.IsValid)
                {
                    Debug.LogWarning($"{Name}: Failed to allocate overflow chunk, {remaining} instances dropped");
                    break;
                }
                GPUBatcherSystem.WriteChunk(overflow.Id, instances.GetSubArray(offset, count), bounds,
                                     cullDistance, instanceCullDistance);

                GetOrCreateOverflowList(link.Id).Add(overflow.Id);
                overflowIds.Add(overflow.Id);
#if UNITY_EDITOR
                _overflowChunkIds.Add(overflow.Id);
#endif
                offset += count;
                remaining -= count;
            }

            // track readiness after all sub-chunks are written
            BeginTrackingHandles(link.Id, 1 + overflowIds.Count);
            foreach (int id in overflowIds)
                _subChunkToPrimary[id] = link.Id;
            TrackSubChunkReady(link.Id);
            foreach (int id in overflowIds)
                TrackSubChunkReady(id);
        }

        // resolves a logical instance index into its current InstanceLink.
        // localIndex spans primary + overflow chunks at fixed maxPerChunk stride.
        public InstanceLink GetInstanceLink(ChunkLink link, int localIndex)
        {
            if (GPUBatcherSystem == null || !GPUBatcherSystem.IsChunkValid(link.Id, link.Generation))
                return InstanceLink.Invalid;
            return ResolveInstanceLink(link.Id, localIndex);
        }

        // includes overflow chunks
        public int GetTotalChunkInstanceCount(ChunkLink link)
        {
            if (GPUBatcherSystem == null) return 0;
            int total = GPUBatcherSystem.GetChunkInstanceCount(link.Id);
            if (_chunkOverflows.TryGetValue(link.Id, out var overflows))
            {
                foreach (int id in overflows)
                    total += GPUBatcherSystem.GetChunkInstanceCount(id);
            }
            return total;
        }

        public int GetOverflowChunkCount(ChunkLink link)
        {
            return _chunkOverflows.TryGetValue(link.Id, out var overflows) ? overflows.Count : 0;
        }

        // outCounts: primary chunk [0] then each overflow chunk [1..N]
        public void GetPerChunkInstanceCounts(ChunkLink link, List<int> outCounts)
        {
            outCounts.Clear();
            if (GPUBatcherSystem == null) return;
            outCounts.Add(GPUBatcherSystem.GetChunkInstanceCount(link.Id));
            if (_chunkOverflows.TryGetValue(link.Id, out var overflows))
            {
                foreach (int id in overflows)
                    outCounts.Add(GPUBatcherSystem.GetChunkInstanceCount(id));
            }
        }

        // idempotent - also frees overflow chunks
        public void ReleaseChunk(ChunkLink link)
        {
            CancelPendingReadiness(link.Id);

            // drop pending ops for slots in chunks about to be freed, before pool indices go stale
            DropPendingForChunk(link.Id);
            if (_chunkOverflows.TryGetValue(link.Id, out var overflowIdsForDrop))
                foreach (int id in overflowIdsForDrop)
                    DropPendingForChunk(id);

            if (link.IsValid && _allocatedChunkIds.Remove(link.Id))
                GPUBatcherSystem.FreeChunk(link.Id);

            // free overflow chunks
            if (_chunkOverflows.Remove(link.Id, out var overflows))
            {
                foreach (int id in overflows)
                {
#if UNITY_EDITOR
                    _overflowChunkIds.Remove(id);
#endif
                    if (_allocatedChunkIds.Remove(id))
                        GPUBatcherSystem.FreeChunk(id);
                }
            }
        }

        // primary covers [0, maxPerChunk), overflows[i] covers [(i+1)*maxPerChunk, (i+2)*maxPerChunk)
        private InstanceLink ResolveInstanceLink(int primaryChunkId, int localIndex)
        {
            if (localIndex < 0) return InstanceLink.Invalid;
            int maxPerChunk = GPUBatcherSystem.MaxInstancesPerChunk;
            int chunkOffset = localIndex / maxPerChunk;
            int localInChunk = localIndex % maxPerChunk;

            int chunkId;
            if (chunkOffset == 0)
                chunkId = primaryChunkId;
            else if (_chunkOverflows.TryGetValue(primaryChunkId, out var overflows)
                     && chunkOffset - 1 < overflows.Count)
                chunkId = overflows[chunkOffset - 1];
            else
                return InstanceLink.Invalid;

            int poolSize = GPUBatcherSystem.PoolSize;
            int poolSlot = localInChunk / poolSize;
            int slotInPool = localInChunk % poolSize;
            int poolIdx = GPUBatcherSystem.GetChunkPoolIndex(chunkId, poolSlot);
            if (poolIdx < 0) return InstanceLink.Invalid;
            return new InstanceLink(chunkId, poolIdx * poolSize + slotInPool);
        }

        private void DropPendingForChunk(int chunkId)
        {
            if (GPUBatcherSystem == null) return;
            if (_pendingPatches == null && _pendingColors == null && _pendingSigOverrides == null) return;

            int poolCount = GPUBatcherSystem.GetChunkPoolCount(chunkId);
            int poolSize = GPUBatcherSystem.PoolSize;
            for (int p = 0; p < poolCount; p++)
            {
                int poolIdx = GPUBatcherSystem.GetChunkPoolIndex(chunkId, p);
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
            GPUBatcherSystem.RecalculateChunkBounds(link.Id, newBounds);
            if (_chunkOverflows.TryGetValue(link.Id, out var overflows))
                foreach (int id in overflows)
                    GPUBatcherSystem.RecalculateChunkBounds(id, newBounds);
        }

        public void SetChunkCullDistances(ChunkLink link, float cullDistance, float instanceCullDistance)
        {
            GPUBatcherSystem.SetChunkCullDistances(link.Id, cullDistance, instanceCullDistance);
            if (_chunkOverflows.TryGetValue(link.Id, out var overflows))
                foreach (int id in overflows)
                    GPUBatcherSystem.SetChunkCullDistances(id, cullDistance, instanceCullDistance);
        }

        public void SetChunkEnabled(ChunkLink link, bool enabled)
        {
            GPUBatcherSystem.SetChunkEnabled(link.Id, enabled);
            if (_chunkOverflows.TryGetValue(link.Id, out var overflows))
                foreach (int id in overflows)
                    GPUBatcherSystem.SetChunkEnabled(id, enabled);
        }

        public bool GetChunkEnabled(ChunkLink link)
        {
            return GPUBatcherSystem.GetChunkEnabled(link.Id);
        }

        public void SetRenderEnabled(bool enabled)
        {
            _renderEnabled = enabled;
            if (GPUBatcherSystem == null) return;
            foreach (int id in _allocatedChunkIds)
                GPUBatcherSystem.SetChunkEnabled(id, enabled);
        }

        #endregion

        #region tracked staging (safe path)

        private const uint kPatchPosition = 1u;
        private const uint kPatchRotation = 2u;
        private const uint kPatchScale    = 4u;

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
            => StagePatchInternal(link.GlobalBRGSlotIndex, kPatchPosition, position, default, default);

        public void StageRotation(InstanceLink link, quaternion rotation)
            => StagePatchInternal(link.GlobalBRGSlotIndex, kPatchRotation, default, rotation.value, default);

        public void StageScale(InstanceLink link, float3 scale)
            => StagePatchInternal(link.GlobalBRGSlotIndex, kPatchScale, default, default, scale);

        public void StageMoveAndRotate(InstanceLink link, float3 position, quaternion rotation)
            => StagePatchInternal(link.GlobalBRGSlotIndex, kPatchPosition | kPatchRotation, position, rotation.value, default);

        public void StageMove(InstanceLink link, float3 pos, quaternion rot, float3 scale)
            => StagePatchInternal(link.GlobalBRGSlotIndex, kPatchPosition | kPatchRotation | kPatchScale, pos, rot.value, scale);

        public void StageColor(InstanceLink link, Color32 color)
        {
            InitStaging();
            _pendingColors[link.GlobalBRGSlotIndex] = GPUBatcherBurstHelpers.PackColor(color);
            _hasPendingStaging = true;
        }

        public void StagePosition(int globalSlot, float3 position)
            => StagePatchInternal(globalSlot, kPatchPosition, position, default, default);

        public void StageRotation(int globalSlot, quaternion rotation)
            => StagePatchInternal(globalSlot, kPatchRotation, default, rotation.value, default);

        public void StageScale(int globalSlot, float3 scale)
            => StagePatchInternal(globalSlot, kPatchScale, default, default, scale);

        public void StageMoveAndRotate(int globalSlot, float3 position, quaternion rotation)
            => StagePatchInternal(globalSlot, kPatchPosition | kPatchRotation, position, rotation.value, default);

        public void StageMove(int globalSlot, float3 pos, quaternion rot, float3 scale)
            => StagePatchInternal(globalSlot, kPatchPosition | kPatchRotation | kPatchScale, pos, rot.value, scale);

        public void StageColor(int globalSlot, Color32 color)
        {
            InitStaging();
            _pendingColors[globalSlot] = GPUBatcherBurstHelpers.PackColor(color);
            _hasPendingStaging = true;
        }

        public void StageSetEnabled(InstanceLink link, bool enabled)
            => StageSetEnabled(link.GlobalBRGSlotIndex, enabled);

        public void StageSetEnabled(int globalSlot, bool enabled)
        {
            InitStaging();
            _pendingSigOverrides[globalSlot] = enabled;
            _hasPendingStaging = true;
        }

        // removes immediately, drops any tracker-side pending ops targeting the old instance
        public void StageRemove(int chunkId, int globalSlot)
        {
            if (GPUBatcherSystem == null) return;
            InitStaging();
            GPUBatcherSystem.RemoveInstance(chunkId, globalSlot);
            _pendingPatches.Remove(globalSlot);
            _pendingColors.Remove(globalSlot);
            _pendingSigOverrides.Remove(globalSlot);
        }

        public void StageRemove(InstanceLink link)
        {
            if (GPUBatcherSystem == null) return;
            InitStaging();

            GPUBatcherSystem.RemoveInstance(link.ChunkId, link.GlobalBRGSlotIndex);
            _pendingPatches.Remove(link.GlobalBRGSlotIndex);
            _pendingColors.Remove(link.GlobalBRGSlotIndex);
            _pendingSigOverrides.Remove(link.GlobalBRGSlotIndex);
        }

        private void StagePatchInternal(int slot, uint flags, float3 pos, float4 rot, float3 scale)
        {
            if (slot < 0) return;
            InitStaging();

            if (_pendingPatches.TryGetValue(slot, out var existing))
            {
                // merge: overlay new components onto existing entry
                if ((flags & kPatchPosition) != 0) existing.position = pos;
                if ((flags & kPatchRotation) != 0) existing.rotation = rot;
                if ((flags & kPatchScale)    != 0) existing.scale = scale;
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
            if (!_hasPendingStaging || GPUBatcherSystem == null) return;
            EnsureScratch();

            // TRS patches
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
                GPUBatcherSystem.BulkStageTRSPatches(s_scratchPatchEntries.AsArray(), s_scratchPatchSlots.AsArray(), patchCount);
            }

            // color writes
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
                GPUBatcherSystem.BulkStageColorWrites(s_scratchColorEntries.AsArray(), colorCount);
            }

            // sig overrides
            if (_pendingSigOverrides != null && _pendingSigOverrides.Count > 0)
            {
                foreach (var kvp in _pendingSigOverrides)
                    GPUBatcherSystem.SetInstanceEnabled(kvp.Key, kvp.Value);
            }

            ClearStaging();
        }

        #endregion

        #region unsafe direct staging

        // direct pass-through to core, without saftey checks to prevent double-changes
        public void SetPositionUnsafe(int globalSlot, float3 position)
            => GPUBatcherSystem?.SetInstancePosition(globalSlot, position);

        public void SetRotationUnsafe(int globalSlot, quaternion rotation)
            => GPUBatcherSystem?.SetInstanceRotation(globalSlot, rotation);

        public void SetScaleUnsafe(int globalSlot, float3 scale)
            => GPUBatcherSystem?.SetInstanceScale(globalSlot, scale);

        public void MoveAndRotateUnsafe(int globalSlot, float3 position, quaternion rotation)
            => GPUBatcherSystem?.MoveAndRotateInstance(globalSlot, position, rotation);

        public void MoveUnsafe(int globalSlot, float3 pos, quaternion rot, float3 scale)
            => GPUBatcherSystem?.MoveInstance(globalSlot, pos, rot, scale);

        public void SetColorUnsafe(int globalSlot, Color32 color)
            => GPUBatcherSystem?.SetInstanceColor(globalSlot, color);

        public void RemoveUnsafe(int chunkId, int globalSlot)
            => GPUBatcherSystem?.RemoveInstance(chunkId, globalSlot);

        public void SetEnabledUnsafe(int globalSlot, bool enabled)
            => GPUBatcherSystem?.SetInstanceEnabled(globalSlot, enabled);

        public void SetPositionUnsafe(InstanceLink link, float3 position)
            => GPUBatcherSystem?.SetInstancePosition(link.GlobalBRGSlotIndex, position);

        public void SetRotationUnsafe(InstanceLink link, quaternion rotation)
            => GPUBatcherSystem?.SetInstanceRotation(link.GlobalBRGSlotIndex, rotation);

        public void SetScaleUnsafe(InstanceLink link, float3 scale)
            => GPUBatcherSystem?.SetInstanceScale(link.GlobalBRGSlotIndex, scale);

        public void MoveAndRotateUnsafe(InstanceLink link, float3 position, quaternion rotation)
            => GPUBatcherSystem?.MoveAndRotateInstance(link.GlobalBRGSlotIndex, position, rotation);

        public void MoveUnsafe(InstanceLink link, float3 pos, quaternion rot, float3 scale)
            => GPUBatcherSystem?.MoveInstance(link.GlobalBRGSlotIndex, pos, rot, scale);

        public void SetColorUnsafe(InstanceLink link, Color32 color)
            => GPUBatcherSystem?.SetInstanceColor(link.GlobalBRGSlotIndex, color);

        public void RemoveUnsafe(InstanceLink link)
            => GPUBatcherSystem?.RemoveInstance(link.ChunkId, link.GlobalBRGSlotIndex);

        public void SetEnabledUnsafe(InstanceLink link, bool enabled)
            => GPUBatcherSystem?.SetInstanceEnabled(link.GlobalBRGSlotIndex, enabled);

        public void SampleLightProbeUnsafe(int globalSlot, float3 position)
            => GPUBatcherSystem?.SampleInstanceLightProbe(globalSlot, position);

        public void SampleLightProbeUnsafe(InstanceLink link, float3 position)
            => GPUBatcherSystem?.SampleInstanceLightProbe(link.GlobalBRGSlotIndex, position);

        #endregion

        #region instance lifecycle

        public InstanceLink AddInstance(ChunkLink chunk, InstanceData instance)
        {
            // reject freed/stale ChunkLinks
            if (!chunk.IsAlive || !_allocatedChunkIds.Contains(chunk.Id))
                return InstanceLink.Invalid;

            // try primary chunk
            int slot = GPUBatcherSystem.AddInstance(chunk.Id, instance);
            if (slot >= 0)
                return new InstanceLink(chunk.Id, slot);

            // try existing overflow chunks
            if (_chunkOverflows.TryGetValue(chunk.Id, out var overflows))
            {
                for (int i = overflows.Count - 1; i >= 0; i--)
                {
                    slot = GPUBatcherSystem.AddInstance(overflows[i], instance);
                    if (slot >= 0)
                        return new InstanceLink(overflows[i], slot);
                }
            }

            // all full - allocate new overflow chunk
            var overflow = AllocChunk();
            if (!overflow.IsValid) return InstanceLink.Invalid;

            slot = GPUBatcherSystem.AddInstance(overflow.Id, instance);
            if (slot < 0)
            {
                if (_allocatedChunkIds.Remove(overflow.Id))
                    GPUBatcherSystem.FreeChunk(overflow.Id);
                return InstanceLink.Invalid;
            }

#if DEBUG
            // GetInstanceLink fixed-stride formula assumes primary + non-last overflows are at max capacity
            Debug.Assert(GPUBatcherSystem.GetChunkPoolCount(chunk.Id) == GPUBatcherSystem.MaxInstancesPerChunk / GPUBatcherSystem.PoolSize,
                "Overflow allocated while previous chunk was not at max capacity");
#endif
            GetOrCreateOverflowList(chunk.Id).Add(overflow.Id);
            return new InstanceLink(overflow.Id, slot);
        }

        // add-before-remove - returns Invalid if destination full, source untouched
        public InstanceLink MoveInstanceToChunk(InstanceLink source, ChunkLink destChunk,
            InstanceData instanceData)
        {
            var newLink = AddInstance(destChunk, instanceData);
            if (!newLink.IsValid) return InstanceLink.Invalid;
            RemoveUnsafe(source);
            return newLink;
        }

        // adds via fresh pools with auto-overflow
        public InstanceLink[] AddInstances(ChunkLink chunk, NativeArray<InstanceData> instances, int count)
        {
            if (!_allocatedChunkIds.Contains(chunk.Id))
                return Array.Empty<InstanceLink>();

            var result = new InstanceLink[count];
            using var slots = new NativeArray<int>(count, Allocator.Temp);

            int placed = GPUBatcherSystem.AppendInstances(chunk.Id, instances, 0, count, slots);
            for (int i = 0; i < placed; i++)
                result[i] = new InstanceLink(chunk.Id, slots[i]);

            int remaining = count - placed;
            int srcOffset = placed;

            while (remaining > 0)
            {
                var overflow = AllocChunk();
                if (!overflow.IsValid)
                {
                    Debug.LogWarning($"{Name}: Failed to allocate overflow chunk, {remaining} instances dropped");
                    for (int i = srcOffset; i < count; i++)
                        result[i] = InstanceLink.Invalid;
                    break;
                }

                using var overflowSlots = new NativeArray<int>(remaining, Allocator.Temp);
                int overflowPlaced = GPUBatcherSystem.AppendInstances(overflow.Id, instances, srcOffset, remaining, overflowSlots);

                if (overflowPlaced == 0)
                {
                    if (_allocatedChunkIds.Remove(overflow.Id))
                        GPUBatcherSystem.FreeChunk(overflow.Id);
                    Debug.LogWarning($"{Name}: Pool allocation failed, {remaining} instances dropped");
                    for (int i = srcOffset; i < count; i++)
                        result[i] = InstanceLink.Invalid;
                    break;
                }

                GetOrCreateOverflowList(chunk.Id).Add(overflow.Id);
                for (int i = 0; i < overflowPlaced; i++)
                    result[srcOffset + i] = new InstanceLink(overflow.Id, overflowSlots[i]);

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

            if (GPUBatcherSystem != null && _pendingHandleChunkIds.Count > 0)
                GPUBatcherSystem.OnDeferredWritesCompleted -= FlushPendingHandles;
            _pendingHandleChunkIds.Clear();
            _pendingReadinessCount.Clear();
            _subChunkToPrimary.Clear();

            if (GPUBatcherSystem != null)
            {
                if (GPUBatcherSystem.ChunkSystemInitialized)
                {
                    foreach (int id in _allocatedChunkIds)
                        GPUBatcherSystem.FreeChunk(id);
                    GPUBatcherSystem.FlushStagedUploads();
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
            if (GPUBatcherSystem.IsChunkWritten(subChunkId))
            {
                DecrementAndMaybeNotify(_subChunkToPrimary[subChunkId]);
            }
            else
            {
                if (_pendingHandleChunkIds.Count == 0)
                    GPUBatcherSystem.OnDeferredWritesCompleted += FlushPendingHandles;
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
                // clean up sub-chunk mappings for this primary
                _subChunkToPrimary.Remove(primaryChunkId);
                if (_chunkOverflows.TryGetValue(primaryChunkId, out var overflows))
                    foreach (int id in overflows)
                        _subChunkToPrimary.Remove(id);

                ChunkWritten?.Invoke(primaryChunkId);
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
            if (IsRegistered)
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
                    GPUBatcherSystem.UnregisterRenderSignature(globalIdx);
            }
        }

        #endregion
    }
}
