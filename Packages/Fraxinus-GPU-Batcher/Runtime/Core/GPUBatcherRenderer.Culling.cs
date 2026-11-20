// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Fraxinus.GPUBatcher.Rendering;
using BatchDrawCommandIndirect = Fraxinus.GPUBatcher.Rendering.BatchDrawCommandIndirect;
#if DEBUG
using Unity.Profiling;
#endif

#if UNITY_6000_4_OR_NEWER
using ViewID = UnityEngine.EntityId;
#else
using ViewID = System.Int32;
#endif

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// GPU culling, occlusion, BRG OnPerformCulling callback, and draw command emission.
    /// </summary>
    public partial class GPUBatcherRenderer
    {
        [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
        private struct ChunkCullJob : IJobParallelFor
        {
            [ReadOnly, NativeDisableContainerSafetyRestriction]
            public NativeArray<int> activeChunks;

            [ReadOnly, NativeDisableContainerSafetyRestriction]
            public NativeArray<bool> chunkEnabled;

            [ReadOnly, NativeDisableContainerSafetyRestriction]
            public NativeArray<ChunkData> chunks;

            [ReadOnly, NativeDisableContainerSafetyRestriction]
            public NativeArray<uint> chunkLayerMask;

            [WriteOnly, NativeDisableContainerSafetyRestriction]
            public NativeArray<bool> cullResults; // parallel to activeChunks: true = visible

            // frustum culling
            public float4 plane0, plane1, plane2, plane3, plane4, plane5;

            // distance culling
            public float3 cameraPosition;

            // layer mask culling
            public uint cullingLayerMask;

            // Umbra occlusion
            [NativeDisableUnsafePtrRestriction]
            public IntPtr occlusionBuffer;

            public void Execute(int i)
            {
                int chunkId = activeChunks[i];

                if (!chunkEnabled[chunkId] || chunks[chunkId].poolCount == 0
                    || (chunkLayerMask[chunkId] & cullingLayerMask) == 0)
                {
                    cullResults[i] = false;
                    return;
                }

                var chunk = chunks[chunkId];
                var bounds = chunk.bounds;
                float3 center = bounds.center;
                float3 extents = bounds.extents;
                float3 aabbMin = center - extents;
                float3 aabbMax = center + extents;

                // distance cull (closest point on AABB to camera)
                if (chunk.cullDistanceSq > 0)
                {
                    float3 closest = math.clamp(cameraPosition, aabbMin, aabbMax);
                    float3 delta = closest - cameraPosition;
                    if (math.dot(delta, delta) > chunk.cullDistanceSq)
                    {
                        cullResults[i] = false;
                        return;
                    }
                }

                // frustum cull (6-plane AABB test)
                if (!AABBInFrustum(aabbMin, aabbMax))
                {
                    cullResults[i] = false;
                    return;
                }

                // Umbra occlusion test (optional)
                if (occlusionBuffer != IntPtr.Zero)
                {
                    cullResults[i] = GPUBatcherOcclusionBridge.OcclusionTestAABB(occlusionBuffer, bounds);
                    return;
                }

                cullResults[i] = true;
            }

            private bool AABBInFrustum(float3 aabbMin, float3 aabbMax)
            {
                if (!PlaneTest(plane0, aabbMin, aabbMax)) return false;
                if (!PlaneTest(plane1, aabbMin, aabbMax)) return false;
                if (!PlaneTest(plane2, aabbMin, aabbMax)) return false;
                if (!PlaneTest(plane3, aabbMin, aabbMax)) return false;
                if (!PlaneTest(plane4, aabbMin, aabbMax)) return false;
                if (!PlaneTest(plane5, aabbMin, aabbMax)) return false;
                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool PlaneTest(float4 plane, float3 aabbMin, float3 aabbMax)
            {
                // p-vertex: corner most in direction of plane normal
                float3 p;
                p.x = plane.x >= 0 ? aabbMax.x : aabbMin.x;
                p.y = plane.y >= 0 ? aabbMax.y : aabbMin.y;
                p.z = plane.z >= 0 ? aabbMax.z : aabbMin.z;
                return math.dot(plane.xyz, p) + plane.w >= 0;
            }
        }

        // chains after ChunkCullJob: builds compacted visible chunk list from cull results
        [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
        private struct CompactVisibleChunksJob : IJob
        {
            [ReadOnly, NativeDisableContainerSafetyRestriction]
            public NativeArray<bool> cullResults;

            [ReadOnly, NativeDisableContainerSafetyRestriction]
            public NativeArray<int> activeChunks;

            public int activeChunkCount;

            [WriteOnly, NativeDisableContainerSafetyRestriction]
            public NativeArray<int> visibleChunkIds;

            [WriteOnly, NativeDisableContainerSafetyRestriction]
            public NativeArray<int> visibleCount; // length-1 output

            public void Execute()
            {
                int count = 0;
                for (int a = 0; a < activeChunkCount; a++)
                {
                    if (cullResults[a])
                        visibleChunkIds[count++] = activeChunks[a];
                }
                visibleCount[0] = count;
            }
        }

        #region Per-View Culling Buffers

        /// <summary>
        /// Per-view output buffer set for camera or shadow rendering.
        /// Each OnPerformCulling call acquires one from the pool.
        /// </summary>
        internal sealed class CullingBufferSet : System.IDisposable
        {
            public GraphicsBuffer DrawBucketBuffer;
            public GraphicsBuffer VisibilityBuffer;
            public GraphicsBuffer IndirectArgsBuffer;
            public GraphicsBuffer VisibleChunksBuffer;
            public GraphicsBuffer VisibleChunkCountBuffer;
            public NativeArray<int> CpuVisibleChunkIds; // survives until cmd executes
            // Package-owned submission data. Unity never takes ownership while native submission is stubbed.
            public NativeArray<BatchDrawCommandIndirect> CpuDrawCommands;
            public NativeArray<BatchDrawRange> CpuDrawRanges;
            public int generation;  // matches _bufferPoolGeneration when valid

            public void Dispose()
            {
                DrawBucketBuffer?.Dispose();
                VisibilityBuffer?.Dispose();
                IndirectArgsBuffer?.Dispose();
                VisibleChunksBuffer?.Dispose();
                VisibleChunkCountBuffer?.Dispose();
                CpuVisibleChunkIds.DisposeIfCreated();
                CpuDrawCommands.DisposeIfCreated();
                CpuDrawRanges.DisposeIfCreated();
            }
        }

        /// <summary>
        /// Per-view animated crossfade state. Each camera needs its own fade buffer
        /// so LOD transitions are correct for that view's distance.
        /// </summary>
        private sealed class ViewFadeState : System.IDisposable
        {
            public GraphicsBuffer FadeStateBuffer;
            public uint lastSeenFrame;
            public uint lastFadeUpdateFrame;
            public bool snapRequested;  // set true to snap on next cull, auto-clears
#if UNITY_EDITOR
            public bool isPersistent;  // never evicted or pooled (scene view only)
#endif


#if UNITY_EDITOR
            public float DeltaTime => _calculatedDT;
            private float _calculatedDT;
            private double lastTime;
            public void CalculateDeltaTime()
            {
                double now = Time.realtimeSinceStartupAsDouble;
                _calculatedDT = (float)(now - lastTime);
                lastTime = now;
            }
#else
            public float DeltaTime => Time.unscaledDeltaTime;
#endif

            public void Dispose()
            {
                FadeStateBuffer?.Dispose();
            }
        }

        // per-view buffer pools

        // camera view pool: each OnPerformCulling(Camera) grabs one set, index resets each frame
        private readonly List<CullingBufferSet> _cameraBufferPool = new();
        private int _cameraPoolNextIndex;
        private int _lastCameraPoolActive;

        // shadow view pool: each OnPerformCulling(Light) grabs one set
        private readonly List<CullingBufferSet> _shadowBufferPool = new();
        private int _shadowPoolNextIndex;
        private int _lastShadowPoolActive;
        private int _peakCameraPoolIndex;
        private int _peakShadowPoolIndex;

        // pool acquisition is single-threaded: OnPerformCulling callbacks are sequential

        // replaces Time.frameCount, incremented in OnBeginContextRendering
        private uint _renderCycleCounter = 1;
        internal uint RenderCycleCounter => _renderCycleCounter;

        // bump after modifying bucket/args templates - triggers per-view buffer re-seed via SetData
        private int _bufferPoolGeneration;

        // per-camera animated crossfade state, keyed by BatchCullingContext.viewID
        private readonly Dictionary<ViewID, ViewFadeState> _viewFadeStates = new();
        private readonly List<ViewID> _viewFadeStateKeys = new();  // mirror of _viewFadeStates keys
        private int _trackedTotalFadeStates;  // running total of fade state buffer elements across all views
        private readonly List<ViewID> _staleViewKeys = new();
        private const int kViewFadeEvictFrames = 3;

        private const int kMaxCascades = 6;

        private int _shadowSplitCapacity = 4;
        private bool _pendingShadowSplitWiden;

        // re-creates the set if buffer generation doesn't match (sizes changed)
        private CullingBufferSet AcquireBufferSet(List<CullingBufferSet> pool, ref int nextIndex,
            int bucketCount, int visCount, int drawCmdCount,
            string prefix,
            NativeArray<DrawBucket> bucketTemplate, NativeArray<uint> argsTemplate)
        {
            CullingBufferSet set;
            if (nextIndex < pool.Count)
            {
                set = pool[nextIndex];
                if (set.generation != _bufferPoolGeneration)
                    UpdateBufferSet(set, bucketCount, visCount, drawCmdCount, prefix,
                        bucketTemplate, argsTemplate);
            }
            else
            {
                set = new CullingBufferSet();
                UpdateBufferSet(set, bucketCount, visCount, drawCmdCount, prefix,
                    bucketTemplate, argsTemplate);
                pool.Add(set);
            }
            nextIndex++;
            return set;
        }

        private CullingBufferSet AcquireCameraBufferSet()
        {
            var set = AcquireBufferSet(_cameraBufferPool, ref _cameraPoolNextIndex,
                _bucketCount, _maxVisibleInstances, _drawCommandCount,
                "Camera", _buckets, _cameraIndirectArgsTemplate);
            _peakCameraPoolIndex = math.max(_peakCameraPoolIndex, _cameraPoolNextIndex);
            return set;
        }

        private CullingBufferSet AcquireShadowBufferSet()
        {
            var set = AcquireBufferSet(_shadowBufferPool, ref _shadowPoolNextIndex,
                _shadowBucketCount, _shadowMaxVisibleInstances, _shadowDrawCommandCount,
                "Shadow", _shadowBuckets, _shadowIndirectArgsTemplate);
            _peakShadowPoolIndex = math.max(_peakShadowPoolIndex, _shadowPoolNextIndex);
            return set;
        }

        // recreates buffers whose size doesn't match, seeds static template data
        private void UpdateBufferSet(CullingBufferSet set, int bucketCount, int visCount,
            int drawCmdCount, string prefix,
            NativeArray<DrawBucket> bucketTemplate, NativeArray<uint> argsTemplate)
        {
            bucketCount = math.max(1, bucketCount);
            visCount = math.max(1, visCount);
            int argsCount = math.max(1, drawCmdCount) * 5;
            int chunkCount = math.max(1, _maxChunks);
            int bucketStride = UnsafeUtility.SizeOf<DrawBucket>();

            if (set.DrawBucketBuffer == null || set.DrawBucketBuffer.count != bucketCount)
            {
                set.DrawBucketBuffer?.Dispose();
                set.DrawBucketBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, bucketCount, bucketStride);
#if DEBUG
                set.DrawBucketBuffer.name = $"FraxinusGPUBatcher_{prefix}Buckets";
#endif
            }

            if (set.VisibilityBuffer == null || set.VisibilityBuffer.count < visCount)
            {
                set.VisibilityBuffer?.Dispose();
                set.VisibilityBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Raw, visCount, sizeof(uint));
#if DEBUG
                set.VisibilityBuffer.name = $"FraxinusGPUBatcher_{prefix}Visibility";
#endif
            }

            if (set.IndirectArgsBuffer == null || set.IndirectArgsBuffer.count != argsCount)
            {
                set.IndirectArgsBuffer?.Dispose();
                var indirectArgsFlags = GraphicsBuffer.Target.IndirectArguments |
                    (UseRawIndirectArgs ? GraphicsBuffer.Target.Raw : GraphicsBuffer.Target.Structured);
                set.IndirectArgsBuffer = new GraphicsBuffer(indirectArgsFlags, argsCount, sizeof(uint));
#if DEBUG
                set.IndirectArgsBuffer.name = $"FraxinusGPUBatcher_{prefix}IndirectArgs";
#endif
            }

            // seed static template data (bufferOffset, maxSlots, indexCount, etc.)
            // only runs on batch rebuild / buffer resize, not per-frame
            if (bucketTemplate.IsCreated)
                set.DrawBucketBuffer.SetData(bucketTemplate, 0, 0, bucketCount);
            if (argsTemplate.IsCreated)
                set.IndirectArgsBuffer.SetData(argsTemplate, 0, 0, argsCount);

            if (set.VisibleChunksBuffer == null || set.VisibleChunksBuffer.count < chunkCount)
            {
                set.VisibleChunksBuffer?.Dispose();
                set.VisibleChunksBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, chunkCount, sizeof(uint));
#if DEBUG
                set.VisibleChunksBuffer.name = $"FraxinusGPUBatcher_{prefix}VisibleChunks";
#endif
                set.CpuVisibleChunkIds.DisposeIfCreated();
                set.CpuVisibleChunkIds = new NativeArray<int>(chunkCount, Allocator.Persistent);
            }

            if (set.VisibleChunkCountBuffer == null)
            {
                var chunkCountFlags = GraphicsBuffer.Target.IndirectArguments |
                    (UseRawIndirectArgs ? GraphicsBuffer.Target.Raw : GraphicsBuffer.Target.Structured);
                set.VisibleChunkCountBuffer = new GraphicsBuffer(chunkCountFlags, 4, sizeof(uint));
#if DEBUG
                set.VisibleChunkCountBuffer.name = $"FraxinusGPUBatcher_{prefix}VisibleChunkCount";
#endif
                set.VisibleChunkCountBuffer.SetData(kCounterResetData);
            }

            set.generation = _bufferPoolGeneration;
        }

        // called from OnBeginCameraRendering (main thread, before any culling for this camera)
        private ViewFadeState CreateViewFadeState(ViewID viewID)
        {
            var state = new ViewFadeState();
            int fadeStateCount = math.max(1, _poolCapacity * kPoolSize);
            state.FadeStateBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                fadeStateCount,
                sizeof(int) * 2  // fadeState: lodPacked (int), fadeProgress (float)
            );
            state.FadeStateBuffer.name = $"FraxinusGPUBatcher_FadeState_View{viewID}";
            GPUInitFadeStateTyped(state.FadeStateBuffer, 0, fadeStateCount);
            state.lastSeenFrame = _renderCycleCounter;
            state.lastFadeUpdateFrame = 0;
            state.snapRequested = true;

            _viewFadeStates[viewID] = state;
            _viewFadeStateKeys.Add(viewID);
            _trackedTotalFadeStates += fadeStateCount;
            return state;
        }

        // reset fade states for a slot range across all views to prevent stale data in new occupants
        private void ResetFadeStatesForSlots(int baseOffset, int count)
        {
            if (count <= 0 || _viewFadeStates.Count == 0) return;
            AddStagedMerged(_stagedFadeResets, 0, baseOffset, count, matchStaging: false);
            _stagingDirty = true;
        }

        /// <summary>
        /// Snap all crossfade animations to target LODs, for all cameras. Call after teleportation or scene load.
        /// </summary>
        public void SnapAllCrossfadeStates()
        {
            foreach (var kvp in _viewFadeStates)
                kvp.Value.snapRequested = true;
        }

        /// <summary>
        /// Snap crossfade for a single view. Returns false if the view hasn't been seen yet.
        /// </summary>
        public bool SnapCrossfadeState(Camera camera) => SnapCrossfadeState(GPUBatcherHelpers.GetViewID(camera));

        public bool SnapCrossfadeState(ViewID viewID)
        {
            if (_viewFadeStates.TryGetValue(viewID, out var state))
            {
                state.snapRequested = true;
                return true;
            }
            return false;
        }

        // mark all existing views for snap (new views snap by default in CreateViewFadeState)
        public void RequestSnapAllViews()
        {
            foreach (var kvp in _viewFadeStates)
                kvp.Value.snapRequested = true;
        }

        #endregion

        #region Culling Fields

        // kernel indices
        private int _finalizeDrawArgsKernel = -1;
        private int _resetCullingStateKernel = -1;
        private int _finalizeShadowDrawArgsKernel = -1;
        private int _updateFadeStatesPerInstanceKernel = -1;
        private int _updateFadeStatesPerPrototypeKernel = -1;
        private int _cullChunksKernel = -1;
        private int _cullInstancesKernel = -1;
        private int _cullChunksShadowKernel = -1;
        private int _cullInstancesShadowKernel = -1;
        private LocalKeyword _hzbEnabledKeyword;
        private LocalKeyword _rawIndirectArgsKeyword;
        private int _copyBufferRegionKernel = -1;
        private int _initFadeStateTypedKernel = -1;
        private int _copyChunkRegionKernel = -1;
        private int _copyFadeStateRegionKernel = -1;
        private int _scatterWriteChunksKernel = -1;
        private int _settleMotionVectorsKernel = -1;
        private int _computeMatricesBatchedKernel = -1;
        private int _batchUploadInstancesKernel = -1;
        private int _trsPatchInstancesKernel = -1;
        private int _batchComputeMatricesKernel = -1;
        private int _batchClearInstancesKernel = -1;
        private int _batchUploadIntsKernel = -1;
        private int _batchInitFadeStatesKernel = -1;
        private int _copyInstanceBufferKernel = -1;
        private int _copyRotScaleBufferKernel = -1;
        private int _scatterWriteColorsKernel = -1;
        private int _scatterWriteSigOverridesKernel = -1;
        private int _scatterWriteSignaturesKernel = -1;
        private int _shiftAllInstancesKernel = -1;
        private int _shiftAllChunksKernel = -1;

        private bool _gpuCullingReady;

        // granular buffer binding dirty flags
        [Flags]
        private enum BufferBindingFlags
        {
            None = 0,
            ChunkBuffer = 1 << 0,
            PoolRefs = 1 << 1,
            Instances = 1 << 2,
            Signatures = 1 << 3,
            BucketMapping = 1 << 4,
            DrawBuckets = 1 << 5,
            ArgsToBucket = 1 << 6,
            ProtoCrossfade = 1 << 7,
            All = 0xFFFF
        }
        private BufferBindingFlags _dirtyBufferBindings = BufferBindingFlags.All;

        private CommandBuffer _cullingCmd;

        // cached shader property IDs
        private static readonly int _ID_AnimatedPoolCount = Shader.PropertyToID("_AnimatedPoolCount");
        private static readonly int _ID_AnimatedPools = Shader.PropertyToID("_AnimatedPools");
        private static readonly int _ID_ArgsToBucket = Shader.PropertyToID("_ArgsToBucket");
        private static readonly int _ID_BucketCount = Shader.PropertyToID("_BucketCount");
        private static readonly int _ID_CameraHalfFOV = Shader.PropertyToID("_CameraHalfFOV");
        private static readonly int _ID_CameraPosition = Shader.PropertyToID("_CameraPosition");
        private static readonly int _ID_CascadeCount = Shader.PropertyToID("_CascadeCount");
        private static readonly int _ID_CascadePlaneCounts = Shader.PropertyToID("_CascadePlaneCounts");
        private static readonly int _ID_CascadePlanes = Shader.PropertyToID("_CascadePlanes");
        private static readonly int _ID_CascadeSpheres = Shader.PropertyToID("_CascadeSpheres");
        private static readonly int _ID_CrossFadeWidth = Shader.PropertyToID("_CrossFadeWidth");
        private static readonly int _ID_CullDistanceSq = Shader.PropertyToID("_CullDistanceSq");
        private static readonly int _ID_DeltaTime = Shader.PropertyToID("_DeltaTime");
        private static readonly int _ID_DrawBuckets = Shader.PropertyToID("_DrawBuckets");
        private static readonly int _ID_DrawCommandCount = Shader.PropertyToID("_DrawCommandCount");
        private static readonly int _ID_FadeStates = Shader.PropertyToID("_FadeStates");
        private static readonly int _ID_FrustumPlanes = Shader.PropertyToID("_FrustumPlanes");
        private static readonly int _ID_IndirectArgs = Shader.PropertyToID("_IndirectArgs");
        private static readonly int _ID_InitialVisibleCount = Shader.PropertyToID("_InitialVisibleCount");
        private static readonly int _ID_InstanceMultiplierShift = Shader.PropertyToID("_InstanceMultiplierShift");
        private static readonly int _ID_Instances = Shader.PropertyToID("_Instances");
        private static readonly int _ID_LODBias = Shader.PropertyToID("_LODBias");
        private static readonly int _ID_ScreenRatioScale = Shader.PropertyToID("_ScreenRatioScale");
        private static readonly int _ID_CpuVisibleChunkIds = Shader.PropertyToID("_CpuVisibleChunkIds");
        private static readonly int _ID_CpuVisibleChunkCount = Shader.PropertyToID("_CpuVisibleChunkCount");
        private static readonly int _ID_PoolRefs = Shader.PropertyToID("_PoolRefs");
        private static readonly int _ID_Signatures = Shader.PropertyToID("_Signatures");

        private static readonly int _ID_IsOrthographic = Shader.PropertyToID("_IsOrthographic");
        private static readonly int _ID_OrthoSize = Shader.PropertyToID("_OrthoSize");
        private static readonly int _ID_ShadowArgsToBucket = Shader.PropertyToID("_ShadowArgsToBucket");
        private static readonly int _ID_ShadowDrawBuckets = Shader.PropertyToID("_ShadowDrawBuckets");
        private static readonly int _ID_ShadowDrawCommandCount = Shader.PropertyToID("_ShadowDrawCommandCount");
        private static readonly int _ID_ShadowIndirectArgs = Shader.PropertyToID("_ShadowIndirectArgs");
        private static readonly int _ID_ShadowVisibilityBuffer = Shader.PropertyToID("_ShadowVisibilityBuffer");
        private static readonly int _ID_SkipIntermediateLODs = Shader.PropertyToID("_SkipIntermediateLODs");
        private static readonly int _ID_SnapFadeStates = Shader.PropertyToID("_SnapFadeStates");
        private static readonly int _ID_AnimatedCrossfadeMode = Shader.PropertyToID("_AnimatedCrossfadeMode");
        private static readonly int _ID_ProtoCrossfadeStates = Shader.PropertyToID("_ProtoCrossfadeStates");
        private static readonly int _ID_Chunks = Shader.PropertyToID("_Chunks");
        private static readonly int _ID_VisibilityBuffer = Shader.PropertyToID("_VisibilityBuffer");
        private static readonly int _ID_VisibleChunkCount = Shader.PropertyToID("_VisibleChunkCount");
        private static readonly int _ID_VisibleChunks = Shader.PropertyToID("_VisibleChunks");
        private static readonly int _ID_VisibleShadowChunkCount = Shader.PropertyToID("_VisibleShadowChunkCount");
        private static readonly int _ID_VisibleShadowChunks = Shader.PropertyToID("_VisibleShadowChunks");
        private static readonly int _ID_ShadowChunkIds = Shader.PropertyToID("_ShadowChunkIds");
        private static readonly int _ID_ShadowChunkCount = Shader.PropertyToID("_ShadowChunkCount");
        private static readonly int _ID_CullingLayerMask = Shader.PropertyToID("_CullingLayerMask");
        private static readonly int _ID_CounterBuffer = Shader.PropertyToID("_CounterBuffer");
        // light-space cascade culling (ReceiverSphereCuller port)
        private static readonly int _ID_WorldToLightRotation = Shader.PropertyToID("_WorldToLightRotation");
        private static readonly int _ID_CascadeBlendFactors = Shader.PropertyToID("_CascadeBlendFactors");
        private static readonly int _ID_ApplyCascadeLimits = Shader.PropertyToID("_ApplyCascadeLimits");
        private static readonly int _ID_LightFacingPlanes = Shader.PropertyToID("_LightFacingPlanes");
        private static readonly int _ID_LightFacingPlaneCount = Shader.PropertyToID("_LightFacingPlaneCount");
        // perspective receiver cull planes (spot/point shadow tightening)
        private static readonly int _ID_ReceiverCullPlanes = Shader.PropertyToID("_ReceiverCullPlanes");
        private static readonly int _ID_ReceiverCullPlaneCount = Shader.PropertyToID("_ReceiverCullPlaneCount");

        // cached values
        private Vector4[] _cachedFrustumPlanes = new Vector4[6];

        // per-split chunk visibility arrays, pooled and sized to _maxChunks
        private struct ChunkVisibilitySlot
        {
            public NativeArray<bool> cullResults;    // parallel to activeChunks: true = visible
            public NativeArray<int> visibleCount;    // length-1, written by CompactVisibleChunksJob

            public void Allocate(int maxChunks)
            {
                cullResults = new NativeArray<bool>(maxChunks, Allocator.Persistent);
                visibleCount = new NativeArray<int>(1, Allocator.Persistent);
            }

            public void Dispose()
            {
                cullResults.DisposeIfCreated();
                visibleCount.DisposeIfCreated();
            }
        }

        private readonly Stack<ChunkVisibilitySlot> _visibilitySlotPool = new();
        private readonly Stack<CameraCullingContext> _deferredCameraPool = new();
        private int _visibilitySlotCapacity; // current maxChunks each slot is sized to

        private ChunkVisibilitySlot AcquireVisibilitySlot()
        {
            if (_visibilitySlotPool.Count > 0)
                return _visibilitySlotPool.Pop();
            var slot = new ChunkVisibilitySlot();
            slot.Allocate(_visibilitySlotCapacity);
            return slot;
        }

        private void ReleaseVisibilitySlot(ChunkVisibilitySlot slot)
        {
            _visibilitySlotPool.Push(slot);
        }

        private void DrainVisibilitySlotPool()
        {
            while (_visibilitySlotPool.Count > 0)
                _visibilitySlotPool.Pop().Dispose();
        }

        private void ReleaseSplitVisibilitySlots()
        {
            if (_currentCullingContext == null || _currentCullingContext.splits == null) return;
            for (int s = 0; s < _currentCullingContext.splits.Count; s++)
            {
                if (_currentCullingContext.splits[s].chunkCullJobScheduled)
                    _currentCullingContext.splits[s].chunkCullJobHandle.Complete();
                ReleaseVisibilitySlot(_currentCullingContext.splits[s].visibilitySlot);
            }
        }

        // releases all visibility slots held by pending deferred contexts
        private void DrainDeferredVisibilitySlots()
        {
            CancelPendingHZBRetest();
            foreach (var kvp in _deferredContexts)
            {
                var splits = kvp.Value.splits;
                if (splits == null) continue;
                for (int s = 0; s < splits.Count; s++)
                {
                    if (splits[s].chunkCullJobScheduled)
                        splits[s].chunkCullJobHandle.Complete();
                    ReleaseVisibilitySlot(splits[s].visibilitySlot);
                }
            }
        }

        private void DrainOrphanedDeferredContexts()
        {
            CancelPendingHZBRetest();
            if (_deferredContexts.Count == 0) return;
            foreach (var kvp in _deferredContexts)
            {
                var ctx = kvp.Value;
                if (ctx.splits != null)
                {
                    for (int s = 0; s < ctx.splits.Count; s++)
                    {
                        if (ctx.splits[s].chunkCullJobScheduled)
                            ctx.splits[s].chunkCullJobHandle.Complete();
                        ReleaseVisibilitySlot(ctx.splits[s].visibilitySlot);
                    }
                    ctx.splits.Clear();
                }
                if (ctx.frustumPlanes != null)
                {
                    _planeArrayPool.Push(ctx.frustumPlanes);
                    ctx.frustumPlanes = null;
                }
                _deferredCameraPool.Push(ctx);
            }
            _deferredContexts.Clear();
        }

        // shadow active chunk list - rebuilt only when dirty
        private int[] _shadowActiveChunks;   // compacted list of enabled active chunk IDs
        private int _shadowActiveCount;
        private bool _shadowChunksDirty = true;
        private bool _shadowChunksRebuiltThisFrame; // forces shadow reset dispatch even when count=0
        private GraphicsBuffer _shadowChunkIdsBuffer; // GPU input for CullChunksShadow

        // camera HZB path: compacted CPU-visible chunk IDs uploaded before CullChunks dispatch
        private GraphicsBuffer _cpuVisibleChunkIdsBuffer;

        private void RebuildShadowActiveChunks()
        {
            int count = 0;
            for (int a = 0; a < _allocatedCount; a++)
            {
                int chunkId = _activeChunks[a];
                if (_chunkEnabled[chunkId] && _chunks[chunkId].maxShadowCascade > 0)
                    _shadowActiveChunks[count++] = chunkId;
            }
            _shadowActiveCount = count;
            _shadowChunksDirty = false;
            _shadowChunksRebuiltThisFrame = true;

            if (count > 0 && _shadowChunkIdsBuffer != null)
                _shadowChunkIdsBuffer.SetData(_shadowActiveChunks, 0, 0, count);
        }

        // per-view occlusion
#if UNITY_EDITOR
        // freeze culling: scene view reuses game camera's culling data
        private bool _frozenCullingValid;
        private Vector4[] _frozenPlanes = new Vector4[6];
        private ViewID _frozenFadeViewID = default;
        private CameraCullingContext _frozen = new();
        // persistent scene view buffer sets (not in pool, survive across frames)
        private CullingBufferSet _sceneViewCameraBufferSet;
        private readonly List<CullingBufferSet> _sceneViewShadowBufferSets = new();
        private int _sceneViewShadowCount;
        private int _sceneViewShadowReplayIndex;

        private void EnsureSceneViewCameraBufferSet()
        {
            if (_sceneViewCameraBufferSet == null)
                _sceneViewCameraBufferSet = new CullingBufferSet();
            if (_sceneViewCameraBufferSet.generation != _bufferPoolGeneration)
                UpdateBufferSet(_sceneViewCameraBufferSet, _bucketCount, _maxVisibleInstances,
                    _drawCommandCount, "SceneCamera", _buckets, _cameraIndirectArgsTemplate);
        }

        private CullingBufferSet EnsureSceneViewShadowBufferSet(int index)
        {
            while (_sceneViewShadowBufferSets.Count <= index)
                _sceneViewShadowBufferSets.Add(new CullingBufferSet());
            var set = _sceneViewShadowBufferSets[index];
            if (set.generation != _bufferPoolGeneration)
                UpdateBufferSet(set, _shadowBucketCount, _shadowMaxVisibleInstances,
                    _shadowDrawCommandCount, "SceneShadow", _shadowBuckets, _shadowIndirectArgsTemplate);
            return set;
        }
#endif

        // per-camera SRP data, populated in OnBeginCameraRendering, consumed in OnPerformCulling
        // keyed by ViewID from GPUBatcherHelpers.GetViewID(camera) (== GPUBatcherHelpers.GetViewID(cullingContext.viewID))
        private struct CameraRenderData
        {
            public float lodBias;
            public Matrix4x4 vpMatrix;       // camera-relative VP
            public Vector4 facingDir;         // invViewMatrix column 2 normalized
            public Vector4 radialDir;         // XY axes of camera frame, normalized
            public Vector4 hzbPosition;       // bridge's viewOriginWorldSpace
            public CameraHZBState hzbState;
            public ViewFadeState viewFadeState;
            public int cameraPoolSnapshot;
            public int shadowPoolSnapshot;
#if UNITY_EDITOR
            public bool isRef, isGame;
#endif
        }
        private readonly Dictionary<ViewID, CameraRenderData> _cameraRenderData = new();

        // shadow cascade data (uploaded per-light)
        private Vector4[] _cascadePlanes = new Vector4[kMaxCascades * 6];
        private int[] _cascadePlaneCounts = new int[8]; // int4[2] on GPU
        private Vector4[] _cascadeSpheres = new Vector4[kMaxCascades];

        // light-space cascade culling data
        private const int kMaxLightFacingPlanes = 6;
        private float[] _cascadeBlendFactorValues = new float[kMaxCascades];
        private Vector4[] _cascadeBlendFactorUpload = new Vector4[2];
        private Vector4[] _lightFacingPlanes = new Vector4[kMaxLightFacingPlanes];

        // perspective receiver cull planes (light-facing + silhouette, spot/point only)
        private const int kMaxReceiverCullPlanes = 18; // 6 light-facing + 12 silhouette max
        private Vector4[] _receiverCullPlanes = new Vector4[kMaxReceiverCullPlanes];

        // compute shader thread group sizes
        private const int kThreadsInstances = 256;
        private const int kThreadsChunks = 64;
        private const int kThreadsFinalize = 64;
        private const int kThreadsReset = 64;

        /// <summary>
        /// Per-split culling state - one per OnPerformCulling call for a given camera.
        /// Multiple splits occur when DrawRenderersCustomPass triggers additional culling.
        /// </summary>
        private struct DeferredCullContext
        {
            public CullingBufferSet viewSet;
            public ChunkVisibilitySlot visibilitySlot;
            public bool chunkCullJobScheduled;
            public JobHandle chunkCullJobHandle;
            public int cpuVisibleChunkCount; // stored for Phase 1 re-dispatch
        }

        /// <summary>
        /// Per-camera culling state captured in OnPerformCulling and consumed in ExecuteDeferredCulling.
        /// Contains shared camera parameters and a list of per-split contexts.
        /// Also used as frozen state for scene view visualization (editor only).
        /// </summary>
        private class CameraCullingContext
        {
            // camera culling parameters (frozen/restored for scene view visualization)
            public Vector3 cameraPosition;
            public float cameraHalfFOV;
            public bool isOrthographic;
            public float orthoSize;
            public float perCameraLodBias;
            public Matrix4x4 viewProjMatrix;  // camera-relative VP (view translation removed)
            public Vector4 facingDir;          // camera Z axis in world space (toward viewer)
            public Vector4 radialDir;          // XY axes of camera frame, normalized
            public Vector4 hzbCameraPosition;  // bridge viewOriginWorldSpace (matches VP matrix)
            public CameraHZBState hzbState;
            public bool useHZB;

            // per-dispatch operational state
            public float lodBias;
            public float screenRatioScale;
            public ViewFadeState fadeState;
            public bool shouldDispatchFade;
            public bool shouldSnap;

            // per-context copy of frustum planes
            public Vector4[] frustumPlanes;     // pooled Vector4[6]

            // per-split contexts (one per OnPerformCulling call for this camera)
            public List<DeferredCullContext> splits;



            // editor-only flags
#if UNITY_EDITOR
            public ViewID cameraId;
            public bool usesFrozenData;
            public bool isRefCamera;
            public bool isGameCamera;
            public bool debugBypassCulling;
#endif

            public void CopyFrom(CameraCullingContext other)
            {
                cameraPosition = other.cameraPosition;
                cameraHalfFOV = other.cameraHalfFOV;
                isOrthographic = other.isOrthographic;
                orthoSize = other.orthoSize;
                perCameraLodBias = other.perCameraLodBias;
                viewProjMatrix = other.viewProjMatrix;
                facingDir = other.facingDir;
                radialDir = other.radialDir;
                hzbCameraPosition = other.hzbCameraPosition;
                hzbState = other.hzbState;
                useHZB = other.useHZB;
                lodBias = other.lodBias;
                screenRatioScale = other.screenRatioScale;
                fadeState = other.fadeState;
                shouldDispatchFade = other.shouldDispatchFade;
                shouldSnap = other.shouldSnap;
                // frustumPlanes and splits are NOT copied - they are per-instance owned
#if UNITY_EDITOR
                cameraId = other.cameraId;
                usesFrozenData = other.usesFrozenData;
                isRefCamera = other.isRefCamera;
                isGameCamera = other.isGameCamera;
                debugBypassCulling = other.debugBypassCulling;
#endif
            }
        }

        // per-camera deferred culling flags, keyed by camera instance ID
        // set by GPUBatcherCullingInjector in beginCameraRendering, consumed in OnPerformCullingCamera
        private readonly HashSet<ViewID> _deferredCullingCameras = new();
        private readonly Dictionary<ViewID, bool> _deferredUseHZB = new();

        /// <summary>
        /// Called by injector to enqueue deferred culling for a specific camera.
        /// Per-camera keying prevents nested Camera.Render() from consuming the outer camera's flag.
        /// </summary>
        internal void EnqueueDeferredCulling(ViewID cameraId, bool useHZB)
        {
            _deferredCullingCameras.Add(cameraId);
            _deferredUseHZB[cameraId] = useHZB;
        }

        // per-camera deferred contexts keyed by camera instance ID
        private readonly Dictionary<ViewID, CameraCullingContext> _deferredContexts = new();
        private readonly Stack<Vector4[]> _planeArrayPool = new();
        internal bool HasPendingDeferredCulling => _deferredContexts.Count > 0;

        // active camera - set when storing/retrieving from dict
        private CameraCullingContext _currentCullingContext;
        // last deferred camera ID
        private ViewID _lastDeferredCameraId;
        internal ViewID LastDeferredCameraId => _lastDeferredCameraId;

        // expose buffer refs for render graph import
        internal CullingBufferSet PendingCullingBufferSet =>
            _currentCullingContext?.splits != null && _currentCullingContext.splits.Count > 0 ? _currentCullingContext.splits[0].viewSet : null;

        // callback invoked after CacheCameraCullingContext stores a deferred context.
        // URP injector subscribes to enqueue render passes at the right time (during
        // context.Cull, inside RenderSingleCameraInternal) instead of beginCameraRendering
        // (which is vulnerable to nested Camera.Render clearing the pass queue).
        internal static Action<ViewID> OnDeferredCullingCached;

        #endregion

        #region GPU Culling

        // bind shared read-only buffers to kernels using granular dirty flags
        private void BindSharedBuffers()
        {
            if (cullingShader == null || !_chunkSystemInitialized || _gpuSignatureBuffer == null) return;
            if (_dirtyBufferBindings == BufferBindingFlags.None) return;  // nothing to bind

            // bind ChunkBuffer
            if ((_dirtyBufferBindings & BufferBindingFlags.ChunkBuffer) != 0 && _chunkBuffer != null)
            {
                if (_cullChunksKernel >= 0)
                    cullingShader.SetBuffer(_cullChunksKernel, _ID_Chunks, _chunkBuffer);
                if (_cullInstancesKernel >= 0)
                    cullingShader.SetBuffer(_cullInstancesKernel, _ID_Chunks, _chunkBuffer);
                if (_cullChunksShadowKernel >= 0)
                    cullingShader.SetBuffer(_cullChunksShadowKernel, _ID_Chunks, _chunkBuffer);
                if (_cullInstancesShadowKernel >= 0)
                    cullingShader.SetBuffer(_cullInstancesShadowKernel, _ID_Chunks, _chunkBuffer);
            }

            // bind PoolRefs
            if ((_dirtyBufferBindings & BufferBindingFlags.PoolRefs) != 0 && _poolRefBuffer != null)
            {
                if (_cullInstancesKernel >= 0)
                    cullingShader.SetBuffer(_cullInstancesKernel, _ID_PoolRefs, _poolRefBuffer);
                if (_cullInstancesShadowKernel >= 0)
                    cullingShader.SetBuffer(_cullInstancesShadowKernel, _ID_PoolRefs, _poolRefBuffer);
            }

            // bind Instances + InstanceRotScale
            if ((_dirtyBufferBindings & BufferBindingFlags.Instances) != 0 && _instanceBuffer != null)
            {
                if (_cullInstancesKernel >= 0)
                    cullingShader.SetBuffer(_cullInstancesKernel, _ID_Instances, _instanceBuffer);
                if (_cullInstancesShadowKernel >= 0)
                    cullingShader.SetBuffer(_cullInstancesShadowKernel, _ID_Instances, _instanceBuffer);
                if (uploadShader != null)
                {
                    if (_updateFadeStatesPerInstanceKernel >= 0)
                        uploadShader.SetBuffer(_updateFadeStatesPerInstanceKernel, _ID_Instances, _instanceBuffer);
                    if (_updateFadeStatesPerPrototypeKernel >= 0)
                        uploadShader.SetBuffer(_updateFadeStatesPerPrototypeKernel, _ID_Instances, _instanceBuffer);
                }
            }

            // bind Signatures
            if ((_dirtyBufferBindings & BufferBindingFlags.Signatures) != 0 && _gpuSignatureBuffer != null)
            {
                if (_cullInstancesKernel >= 0)
                    cullingShader.SetBuffer(_cullInstancesKernel, _ID_Signatures, _gpuSignatureBuffer);
                if (_cullInstancesShadowKernel >= 0)
                    cullingShader.SetBuffer(_cullInstancesShadowKernel, _ID_Signatures, _gpuSignatureBuffer);
                if (uploadShader != null)
                {
                    if (_updateFadeStatesPerInstanceKernel >= 0)
                        uploadShader.SetBuffer(_updateFadeStatesPerInstanceKernel, _ID_Signatures, _gpuSignatureBuffer);
                    if (_updateFadeStatesPerPrototypeKernel >= 0)
                        uploadShader.SetBuffer(_updateFadeStatesPerPrototypeKernel, _ID_Signatures, _gpuSignatureBuffer);
                }
            }

            // bind ArgsToBucket
            if ((_dirtyBufferBindings & BufferBindingFlags.ArgsToBucket) != 0)
            {
                if (_finalizeDrawArgsKernel >= 0 && _argsToBucketBuffer != null)
                    cullingShader.SetBuffer(_finalizeDrawArgsKernel, _ID_ArgsToBucket, _argsToBucketBuffer);
                if (_finalizeShadowDrawArgsKernel >= 0 && _shadowArgsToBucketBuffer != null)
                    cullingShader.SetBuffer(_finalizeShadowDrawArgsKernel, _ID_ShadowArgsToBucket, _shadowArgsToBucketBuffer);
            }

            // bind ProtoCrossfadeStates
            if ((_dirtyBufferBindings & BufferBindingFlags.ProtoCrossfade) != 0 && _protoCrossfadeBuffer != null)
            {
                if (_cullInstancesKernel >= 0)
                    cullingShader.SetBuffer(_cullInstancesKernel, _ID_ProtoCrossfadeStates, _protoCrossfadeBuffer);
                if (_cullInstancesShadowKernel >= 0)
                    cullingShader.SetBuffer(_cullInstancesShadowKernel, _ID_ProtoCrossfadeStates, _protoCrossfadeBuffer);
            }

            _dirtyBufferBindings = BufferBindingFlags.None;
        }

        // dispatch compute with profiler markers
        private void CmdDispatchCull(CommandBuffer cmd, string name, int kernel, int groupsX)
        {
#if DEBUG
            cmd.BeginSample(name);
#endif
            DispatchSafe(cmd, cullingShader, kernel, groupsX);
#if DEBUG
            cmd.EndSample(name);
#endif
        }

        // indirect dispatch with profiler markers
        private void CmdDispatchCullIndirect(CommandBuffer cmd, string name, int kernel, GraphicsBuffer argsBuffer)
        {
#if DEBUG
            cmd.BeginSample(name);
#endif
            cmd.DispatchCompute(cullingShader, kernel, argsBuffer, 0);
#if DEBUG
            cmd.EndSample(name);
#endif
        }

        // Reuse per-view CPU submission data; only a native backend may allocate BRG-owned output.
        private static void EnsureIndirectSubmissionCapacity(CullingBufferSet viewSet, int cmdCount, int rangeCount)
        {
            cmdCount = math.max(1, cmdCount);
            rangeCount = math.max(1, rangeCount);
            if (!viewSet.CpuDrawCommands.IsCreated || viewSet.CpuDrawCommands.Length < cmdCount)
            {
                viewSet.CpuDrawCommands.DisposeIfCreated();
                viewSet.CpuDrawCommands = new NativeArray<BatchDrawCommandIndirect>(cmdCount, Allocator.Persistent);
            }
            if (!viewSet.CpuDrawRanges.IsCreated || viewSet.CpuDrawRanges.Length < rangeCount)
            {
                viewSet.CpuDrawRanges.DisposeIfCreated();
                viewSet.CpuDrawRanges = new NativeArray<BatchDrawRange>(rangeCount, Allocator.Persistent);
            }
        }

        /// <summary>
        /// Deferred path: extract all needed values from BatchCullingContext (ref struct),
        /// schedule Umbra occlusion job, and copy template buffers. Returns job handle to Unity.
        /// Compute dispatches happen later in ExecuteDeferredCulling, which calls .Complete() explicitly.
        /// </summary>
        private JobHandle CacheCameraCullingContext(in BatchCullingContext cullingContext, CullingBufferSet viewSet, ViewID cameraId, in CameraRenderData camData)
        {
            bool isFirstSplit = !_deferredContexts.ContainsKey(cameraId);

            if (isFirstSplit)
            {
                // per-camera setup (first split only)
                _currentCullingContext = _deferredCameraPool.Count > 0 ? _deferredCameraPool.Pop() : new CameraCullingContext();

                // extract frustum planes
                int planeCount = math.min(cullingContext.cullingPlanes.Length, 6);
                for (int i = 0; i < planeCount; i++)
                {
                    var plane = cullingContext.cullingPlanes[i];
                    _cachedFrustumPlanes[i] = new Vector4(
                        plane.normal.x, plane.normal.y,
                        plane.normal.z, plane.distance);
                }
                for (int i = planeCount; i < 6; i++)
                    _cachedFrustumPlanes[i] = Vector4.zero;

                // extract LOD parameters
                var lodParams = cullingContext.lodParameters;
                _currentCullingContext.cameraPosition = lodParams.cameraPosition;
                _currentCullingContext.isOrthographic = lodParams.isOrthographic;
                _currentCullingContext.orthoSize = lodParams.orthoSize;
                float fov = lodParams.fieldOfView;
                _currentCullingContext.cameraHalfFOV = fov > 0f ? math.tan(math.radians(fov * 0.5f)) : 0.5f;
                _currentCullingContext.perCameraLodBias = camData.lodBias;
                _currentCullingContext.viewProjMatrix = camData.vpMatrix;
                _currentCullingContext.facingDir = camData.facingDir;
                _currentCullingContext.radialDir = camData.radialDir;
                _currentCullingContext.hzbCameraPosition = camData.hzbPosition;
                _currentCullingContext.hzbState = camData.hzbState;
                _currentCullingContext.useHZB = _deferredUseHZB.Remove(cameraId, out var useHZB) && useHZB;

                // freeze culling (editor only)
#if UNITY_EDITOR
                _currentCullingContext.cameraId = cameraId;
                _currentCullingContext.isRefCamera = camData.isRef;
                _currentCullingContext.isGameCamera = camData.isGame;
                _currentCullingContext.usesFrozenData = false;
                _currentCullingContext.debugBypassCulling = Config.debugDisableCPUChunkCulling;

                if (Config.debugVisualizeCullingInSceneView)
                {
                    if (_currentCullingContext.isRefCamera)
                    {
                        System.Array.Copy(_cachedFrustumPlanes, _frozenPlanes, 6);
                        _frozen.CopyFrom(_currentCullingContext);
                    }
                    else if (_frozenCullingValid && !camData.isGame)
                    {
                        System.Array.Copy(_frozenPlanes, _cachedFrustumPlanes, 6);
                        bool frozenUseHZB = _frozen.useHZB;
                        _currentCullingContext.CopyFrom(_frozen);
                        _currentCullingContext.isRefCamera = camData.isRef;
                        _currentCullingContext.usesFrozenData = true;
                        _currentCullingContext.debugBypassCulling = Config.debugDisableCPUChunkCulling;
                        _currentCullingContext.useHZB = frozenUseHZB;
                    }
                }
                else
                {
                    _frozenCullingValid = false;
                    _frozenFadeViewID = default;
                }
#endif

                // precompute screen ratio scale
                _currentCullingContext.lodBias = QualitySettings.lodBias * _currentCullingContext.perCameraLodBias;
                if (_currentCullingContext.isOrthographic)
                    _currentCullingContext.screenRatioScale = _currentCullingContext.orthoSize > 0f ? _currentCullingContext.lodBias / (2f * _currentCullingContext.orthoSize) : 0f;
                else
                    _currentCullingContext.screenRatioScale = _currentCullingContext.cameraHalfFOV > 0f ? _currentCullingContext.lodBias / (2f * _currentCullingContext.cameraHalfFOV) : 0f;

                // resolve fade buffer
                _currentCullingContext.fadeState = camData.viewFadeState;
#if UNITY_EDITOR
                if (_currentCullingContext.usesFrozenData && _frozenFadeViewID != default &&
                    _viewFadeStates.TryGetValue(_frozenFadeViewID, out var frozenView))
                    _currentCullingContext.fadeState = frozenView;
#endif

                // evaluate fade state mutations
                _currentCullingContext.shouldDispatchFade = false;
                _currentCullingContext.shouldSnap = false;
                if (_currentCullingContext.fadeState?.FadeStateBuffer != null && _anyPrototypeUsesAnimatedCrossfade && !Config.debugDisableCrossfade)
                {
                    bool isPerProtoMode = Config.animatedCrossfadeMode == AnimatedCrossfadeMode.PerPrototype;
                    int activeKernel = isPerProtoMode
                        ? _updateFadeStatesPerPrototypeKernel
                        : _updateFadeStatesPerInstanceKernel;

                    if (activeKernel >= 0)
                    {
#if UNITY_EDITOR
                        if (_currentCullingContext.usesFrozenData && _frozenFadeViewID != default &&
                            _viewFadeStates.TryGetValue(_frozenFadeViewID, out var frozenViewAnim))
                        {
                            _currentCullingContext.fadeState = frozenViewAnim;
                        }
                        else
#endif
                        {
                            var fadeState = camData.viewFadeState;
                            if (_renderCycleCounter != fadeState.lastFadeUpdateFrame)
                            {
                                fadeState.lastFadeUpdateFrame = _renderCycleCounter;
#if UNITY_EDITOR
                                fadeState.CalculateDeltaTime();
#endif
                                _currentCullingContext.shouldSnap = fadeState.snapRequested;
                                if (fadeState.snapRequested) fadeState.snapRequested = false;

                                if (!(isPerProtoMode && !_anyPrototypeBoundary && !_currentCullingContext.shouldSnap))
                                    _currentCullingContext.shouldDispatchFade = true;
                            }
#if UNITY_EDITOR
                            if (Config.debugVisualizeCullingInSceneView && _currentCullingContext.isRefCamera)
                                _frozenFadeViewID = cameraId;
#endif
                        }
                    }
                }

                // snapshot frustum planes
                var planes = _planeArrayPool.Count > 0 ? _planeArrayPool.Pop() : new Vector4[6];
                System.Array.Copy(_cachedFrustumPlanes, planes, 6);
                _currentCullingContext.frustumPlanes = planes;

                // init splits list
                if (_currentCullingContext.splits == null)
                    _currentCullingContext.splits = new List<DeferredCullContext>(2);
                else
                    _currentCullingContext.splits.Clear();
            }
            else
            {
                // subsequent split for same camera
                _currentCullingContext = _deferredContexts[cameraId];
                System.Array.Copy(_currentCullingContext.frustumPlanes, _cachedFrustumPlanes, 6);
            }

            // per-split setup (every call)

            var visSlot = AcquireVisibilitySlot();
            var split = new DeferredCullContext { viewSet = viewSet, visibilitySlot = visSlot };

            // schedule CPU chunk cull job - each split has its own visibility arrays
            JobHandle chunkCullJobHandle = default;

            bool debugBypassCulling;
#if UNITY_EDITOR
            debugBypassCulling = _currentCullingContext.debugBypassCulling;
#else
            debugBypassCulling = false;
#endif
            if (!debugBypassCulling)
#if UNITY_EDITOR
            if (!_currentCullingContext.usesFrozenData)
#endif
            {
                IntPtr occBuffer = Config.useBakedOcclusionCulling
                    ? GPUBatcherOcclusionBridge.GetOcclusionBuffer(cullingContext)
                    : IntPtr.Zero;
#if DEBUG
                if (Config.enableDebugLogging)
                    Debug.Log($"[Fraxinus GPU Batcher] occlusionBuffer: 0x{occBuffer.ToInt64():X}, cc.occlusionBuffer: 0x{GPUBatcherOcclusionBridge.GetOcclusionBuffer(cullingContext).ToInt64():X}, useBaked: {Config.useBakedOcclusionCulling}");
#endif

                chunkCullJobHandle = new ChunkCullJob
                {
                    activeChunks = _activeChunks,
                    chunkEnabled = _chunkEnabled,
                    chunks = _chunks,
                    chunkLayerMask = _chunkLayerMask,
                    cullResults = visSlot.cullResults,
                    occlusionBuffer = occBuffer,
                    cameraPosition = _currentCullingContext.cameraPosition,
                    cullingLayerMask = (uint)cullingContext.cullingLayerMask,
                    plane0 = _cachedFrustumPlanes[0],
                    plane1 = _cachedFrustumPlanes[1],
                    plane2 = _cachedFrustumPlanes[2],
                    plane3 = _cachedFrustumPlanes[3],
                    plane4 = _cachedFrustumPlanes[4],
                    plane5 = _cachedFrustumPlanes[5],
                }.Schedule(_allocatedCount, 64);

                chunkCullJobHandle = new CompactVisibleChunksJob
                {
                    cullResults = visSlot.cullResults,
                    activeChunks = _activeChunks,
                    activeChunkCount = _allocatedCount,
                    visibleChunkIds = viewSet.CpuVisibleChunkIds,
                    visibleCount = visSlot.visibleCount,
                }.Schedule(chunkCullJobHandle);

                split.chunkCullJobScheduled = true;
                split.chunkCullJobHandle = chunkCullJobHandle;
            }

            _currentCullingContext.splits.Add(split);
            _deferredContexts[cameraId] = _currentCullingContext;
            _lastDeferredCameraId = cameraId;

            if (isFirstSplit)
                OnDeferredCullingCached?.Invoke(cameraId);

            return chunkCullJobHandle;
        }

        /// <summary>
        /// Deferred path: record all GPU culling dispatches on a CommandBuffer from the render pass.
        /// Called from GPUBatcherCullingPass (URP) or GPUBatcherCullingPassHDRP (HDRP). Completes chunk cull job explicitly.
        /// When GPU occlusion is disabled, this is the single pass at BeforeRenderingOpaques.
        /// When GPU occlusion is enabled, this serves as Pass 0 (BeforeRenderingPrePasses).
        /// </summary>
        internal void ExecuteDeferredCulling(CommandBuffer cmd, ViewID cameraId, bool skipHZBTextureBinding = false)
        {
            if (!_deferredContexts.TryGetValue(cameraId, out var cam)) return;
            _deferredContexts.Remove(cameraId);

            // Keep other cameras' retests alive across interleaved camera/shadow work.
            if (_hzbRetestContexts.Remove(cameraId, out var previousRetest))
                ReleaseCameraCullingContext(previousRetest);
            _currentCullingContext = cam;
            System.Array.Copy(_currentCullingContext.frustumPlanes, _cachedFrustumPlanes, 6);

            if (cullingShader == null || !_gpuCullingReady)
            {
                ReleaseSplitVisibilitySlots();
                ReleaseCameraCullingContext(_currentCullingContext);
                return;
            }

            // rebind shared read-only buffers if dirty
            if (_dirtyBufferBindings != BufferBindingFlags.None)
                BindSharedBuffers();

            if (!_chunkSystemInitialized || _allocatedCount == 0 || _cullChunksKernel < 0)
            {
                ReleaseSplitVisibilitySlots();
                ReleaseCameraCullingContext(_currentCullingContext);
                return;
            }

            bool hzbActive = _currentCullingContext.useHZB && IsGPUOcclusionEnabled;

            BindCameraCullingUniforms(cmd, cam);

            // HZB keyword + uniforms
            bool enableHZBPhase0 = hzbActive && !Config.useHZBRetestPass;
            if (enableHZBPhase0)
            {
                var hzbState = _currentCullingContext.hzbState;
                bool hzbTestable = hzbState != null && hzbState.hzbValid && hzbState.prevDataValid && hzbState.hzbAtlas != null;

                if (hzbTestable)
                {
                    cmd.EnableKeyword(cullingShader, _hzbEnabledKeyword);

                    float cameraMotionPixels = 0f;
                    if (hzbState.topMipHeight > 0)
                    {
                        float3 prevFwd = new float3(hzbState.prevFacingDir.x, hzbState.prevFacingDir.y, hzbState.prevFacingDir.z);
                        float3 currFwd = new float3(_currentCullingContext.facingDir.x, _currentCullingContext.facingDir.y, _currentCullingContext.facingDir.z);
                        float cosAngle = math.clamp(math.dot(prevFwd, currFwd), -1f, 1f);
                        float angularDelta = math.acos(cosAngle);
                        float verticalFOV = 2f * math.atan(_currentCullingContext.cameraHalfFOV);
                        if (verticalFOV > 0f)
                            cameraMotionPixels = angularDelta * hzbState.topMipHeight / verticalFOV;
                    }
                    float hzbPixelExpansion = Config.hzbPixelExpansion + cameraMotionPixels;

                    float hzbTranslationScale = 0f;
                    if (hzbState.prevDataValid)
                    {
                        float3 prevPos = new float3(hzbState.prevCameraPosition.x, hzbState.prevCameraPosition.y, hzbState.prevCameraPosition.z);
                        float3 currPos = new float3(_currentCullingContext.hzbCameraPosition.x, _currentCullingContext.hzbCameraPosition.y, _currentCullingContext.hzbCameraPosition.z);
                        float posDelta = math.length(currPos - prevPos);
                        if (posDelta > 0.001f && _currentCullingContext.cameraHalfFOV > 0f)
                        {
                            float focalLengthPixels = hzbState.topMipHeight / (2f * _currentCullingContext.cameraHalfFOV);
                            hzbTranslationScale = posDelta * focalLengthPixels;
                        }
                    }

                    cmd.SetComputeFloatParam(cullingShader, _ID_HZBPixelExpansion, hzbPixelExpansion);
                    cmd.SetComputeFloatParam(cullingShader, _ID_HZBTranslationScale, hzbTranslationScale);
                    cmd.SetComputeMatrixParam(cullingShader, _ID_ViewProjMatrix, hzbState.prevViewProjMatrix);
                    cmd.SetComputeVectorParam(cullingShader, _ID_FacingDirWorldSpace, hzbState.prevFacingDir);
                    cmd.SetComputeVectorParam(cullingShader, _ID_RadialDirWorldSpace, hzbState.prevRadialDir);
                    cmd.SetComputeVectorParam(cullingShader, _ID_HZBCameraPosition, hzbState.prevCameraPosition);
                    if (!skipHZBTextureBinding)
                    {
                        cmd.SetComputeTextureParam(cullingShader, _cullChunksKernel, _ID_HZBTexture, hzbState.hzbAtlas);
                        cmd.SetComputeTextureParam(cullingShader, _cullInstancesKernel, _ID_HZBTexture, hzbState.hzbAtlas);
                    }
                    BindHZBAtlasUniforms(cmd, cullingShader, hzbState);
                }
                else
                {
                    cmd.DisableKeyword(cullingShader, _hzbEnabledKeyword);
                }
            }
            else
            {
                cmd.DisableKeyword(cullingShader, _hzbEnabledKeyword);
            }

            var fadeBuffer = _currentCullingContext.fadeState?.FadeStateBuffer;
            if (fadeBuffer == null)
            {
                ReleaseSplitVisibilitySlots();
                ReleaseCameraCullingContext(_currentCullingContext);
                return;
            }

            // fade state dispatch (once per camera, not per split)
            if (_currentCullingContext.shouldDispatchFade && uploadShader != null)
            {
                bool isPerProtoMode = Config.animatedCrossfadeMode == AnimatedCrossfadeMode.PerPrototype;
                int activeKernel = isPerProtoMode
                    ? _updateFadeStatesPerPrototypeKernel
                    : _updateFadeStatesPerInstanceKernel;

                cmd.SetComputeVectorParam(uploadShader, _ID_CameraPosition, _currentCullingContext.cameraPosition);
                cmd.SetComputeFloatParam(uploadShader, _ID_CameraHalfFOV, _currentCullingContext.cameraHalfFOV);
                cmd.SetComputeFloatParam(uploadShader, _ID_LODBias, _currentCullingContext.lodBias);
                cmd.SetComputeFloatParam(uploadShader, _ID_ScreenRatioScale, _currentCullingContext.screenRatioScale);
                cmd.SetComputeFloatParam(uploadShader, _ID_CrossFadeWidth, Config.debugDisableCrossfade ? 0f : 1f);
                cmd.SetComputeFloatParam(uploadShader, _ID_DeltaTime, _currentCullingContext.fadeState.DeltaTime * _crossfadeTimeScale);
                cmd.SetComputeIntParam(uploadShader, _ID_SkipIntermediateLODs, Config.skipIntermediateLODs ? 1 : 0);
                cmd.SetComputeIntParam(uploadShader, _ID_AnimatedCrossfadeMode, (int)Config.animatedCrossfadeMode);
                cmd.SetComputeIntParam(uploadShader, _ID_IsOrthographic, _currentCullingContext.isOrthographic ? 1 : 0);
                cmd.SetComputeFloatParam(uploadShader, _ID_OrthoSize, _currentCullingContext.orthoSize);

                int animatedPoolCount = _animatedCrossfadePools.Length;
                if (animatedPoolCount > 0 && _animatedPoolsBuffer != null)
                {
                    cmd.SetComputeIntParam(uploadShader, _ID_AnimatedPoolCount, animatedPoolCount);
                    cmd.SetComputeIntParam(uploadShader, _ID_SnapFadeStates, _currentCullingContext.shouldSnap ? 1 : 0);
                    cmd.SetComputeBufferParam(uploadShader, activeKernel, _ID_FadeStates, fadeBuffer);
                    cmd.SetComputeBufferParam(uploadShader, activeKernel, _ID_AnimatedPools, _animatedPoolsBuffer);
                    if (isPerProtoMode && _protoCrossfadeBuffer != null)
                        cmd.SetComputeBufferParam(uploadShader, activeKernel, _ID_ProtoCrossfadeStates, _protoCrossfadeBuffer);
                    int animatedSlots = animatedPoolCount * kPoolSize;
                    int fadeGroups = (int)math.ceil(animatedSlots / (float)kThreadsInstances);
#if DEBUG
                    string fadeName = isPerProtoMode ? "FraxinusGPUBatcher.UpdateFadeStatesProto" : "FraxinusGPUBatcher.UpdateFadeStatesInst";
                    cmd.BeginSample(fadeName);
#endif
                    DispatchSafe(cmd, uploadShader, activeKernel, fadeGroups);
#if DEBUG
                    cmd.EndSample(fadeName);
#endif
                }
                _currentCullingContext.shouldDispatchFade = false;
            }

            bool skipCullChunks = !hzbActive;

            // per-split GPU culling dispatch
            for (int s = 0; s < _currentCullingContext.splits.Count; s++)
            {
                var split = _currentCullingContext.splits[s];
                var viewSet = split.viewSet;
                var visSlot = split.visibilitySlot;
                // The job owns the visibility slot until completion, including a skipped split.
                if (split.chunkCullJobScheduled)
                    split.chunkCullJobHandle.Complete();
                if (viewSet == null || viewSet.DrawBucketBuffer == null || viewSet.VisibilityBuffer == null)
                {
                    ReleaseVisibilitySlot(visSlot);
                    continue;
                }

                // populate visible chunk list if no job was scheduled (bypass/frozen)
                if (!split.chunkCullJobScheduled)
                {
                    for (int a = 0; a < _allocatedCount; a++)
                        viewSet.CpuVisibleChunkIds[a] = _activeChunks[a];
                    visSlot.visibleCount[0] = _allocatedCount;
#if DEBUG
                    StatOcclusionPass = _allocatedCount;
                    StatOcclusionFail = 0;
#endif
                }
#if DEBUG
                else
                {
                    int visCount = visSlot.visibleCount[0];
                    StatOcclusionPass = visCount;
                    StatOcclusionFail = _allocatedCount - visCount;
                }
#endif

                int cpuVisCount = visSlot.visibleCount[0];

                if (skipCullChunks)
                {
                    if (cpuVisCount > 0 && viewSet.VisibleChunksBuffer != null)
                        cmd.SetBufferData(viewSet.VisibleChunksBuffer, viewSet.CpuVisibleChunkIds, 0, 0, cpuVisCount);
                }
                else
                {
                    if (cpuVisCount > 0 && _cpuVisibleChunkIdsBuffer != null)
                        cmd.SetBufferData(_cpuVisibleChunkIdsBuffer, viewSet.CpuVisibleChunkIds, 0, 0, cpuVisCount);
                }

                // store cpuVisibleChunkCount for Phase 1 re-dispatch (first split only)
                if (s == 0)
                {
                    split.cpuVisibleChunkCount = cpuVisCount;
                    _currentCullingContext.splits[0] = split;
                }

                ReleaseVisibilitySlot(visSlot);

                // reset buckets, args, counters
                DispatchResetCullingState(cmd, viewSet.DrawBucketBuffer, viewSet.IndirectArgsBuffer,
                    viewSet.VisibleChunkCountBuffer,
                    _bucketCount, _drawCommandCount,
                    skipCullChunks ? cpuVisCount : 0);

                // no visible chunks - reset zeroed the args, skip GPU culling
                if (cpuVisCount == 0)
                    continue;

                // bind per-split output buffers
                cmd.SetComputeBufferParam(cullingShader, _cullInstancesKernel, _ID_DrawBuckets, viewSet.DrawBucketBuffer);
                cmd.SetComputeBufferParam(cullingShader, _cullInstancesKernel, _ID_VisibilityBuffer, viewSet.VisibilityBuffer);
                cmd.SetComputeBufferParam(cullingShader, _cullInstancesKernel, _ID_VisibleChunks, viewSet.VisibleChunksBuffer);
                cmd.SetComputeBufferParam(cullingShader, _cullInstancesKernel, _ID_FadeStates, fadeBuffer);
                cmd.SetComputeBufferParam(cullingShader, _finalizeDrawArgsKernel, _ID_DrawBuckets, viewSet.DrawBucketBuffer);
                cmd.SetComputeBufferParam(cullingShader, _finalizeDrawArgsKernel, _ID_IndirectArgs, viewSet.IndirectArgsBuffer);

                if (!skipCullChunks)
                {
                    cmd.SetComputeBufferParam(cullingShader, _cullChunksKernel, _ID_VisibleChunks, viewSet.VisibleChunksBuffer);
                    cmd.SetComputeBufferParam(cullingShader, _cullChunksKernel, _ID_VisibleChunkCount, viewSet.VisibleChunkCountBuffer);
                    cmd.SetComputeBufferParam(cullingShader, _cullChunksKernel, _ID_CpuVisibleChunkIds, _cpuVisibleChunkIdsBuffer);
                    cmd.SetComputeIntParam(cullingShader, _ID_CpuVisibleChunkCount, cpuVisCount);

                    CmdDispatchCull(cmd, "FraxinusGPUBatcher.CullChunks", _cullChunksKernel,
                        (int)math.ceil(cpuVisCount / (float)kThreadsChunks));
                }

                CmdDispatchCullIndirect(cmd, "FraxinusGPUBatcher.CullInstances", _cullInstancesKernel, viewSet.VisibleChunkCountBuffer);

                DispatchFinalizeDrawArgs(cmd, viewSet);

#if UNITY_EDITOR
                if (s == 0)
                    RequestCameraReadback(cmd, viewSet, cameraId);
#endif
            }

            // signal Pass 1 is pending (if HZB active) - only for first split
            bool retestPending = hzbActive && Config.useHZBRetestPass;
            if (retestPending)
                _hzbRetestContexts.Add(cameraId, cam);

#if UNITY_EDITOR
            if (!retestPending && _currentCullingContext.splits != null && _currentCullingContext.splits.Count > 0)
                DispatchDebugSphereCompaction(cmd, _currentCullingContext.splits[0].viewSet);
#endif

            // return instance to pool if no Phase1 pending
            if (!retestPending)
                ReleaseCameraCullingContext(_currentCullingContext);
        }

        /// <summary>
        /// Phase 1 (single-pass mode): re-dispatch full culling with current-frame HZB.
        /// Phase0 ran with HZB_ENABLED off (frustum+LOD only). After depth prepass + HZB build,
        /// this re-culls with HZB_ENABLED on (current-frame HZB) so opaques render only
        /// truly visible instances. ZTest Equal works because depth prepass had everything.
        /// Called from GPUBatcherRenderGraphPasses.RecordHZBCullPass after HZB build.
        /// </summary>
        internal void ExecuteDeferredCullingPhase1(CommandBuffer cmd, ViewID cameraId,
            bool skipHZBTextureBinding = false)
        {
            if (!_hzbRetestContexts.Remove(cameraId, out var retainedContext)) return;
            try
            {
                if (cullingShader == null || !_gpuCullingReady)
                    return;

                if (!Config.useHZBRetestPass) return;

                if (retainedContext.splits == null || retainedContext.splits.Count == 0) return;
                var viewSet = retainedContext.splits[0].viewSet;
                if (viewSet == null) return;

                var hzbState = retainedContext.hzbState;
                if (hzbState == null || !hzbState.hzbValid || hzbState.hzbAtlas == null)
                    return;

                var fadeBuffer = retainedContext.fadeState?.FadeStateBuffer;
                if (fadeBuffer == null)
                    return;

                // Shadow or another camera may have changed the shader's camera uniforms.
                BindCameraCullingUniforms(cmd, retainedContext);

                // Phase 0 may have processed several splits. Restore this split's outputs
                // instead of inheriting whichever split last bound the compute shader.
                cmd.SetComputeBufferParam(cullingShader, _cullChunksKernel, _ID_VisibleChunks, viewSet.VisibleChunksBuffer);
                cmd.SetComputeBufferParam(cullingShader, _cullChunksKernel, _ID_VisibleChunkCount, viewSet.VisibleChunkCountBuffer);
                cmd.SetComputeBufferParam(cullingShader, _cullInstancesKernel, _ID_DrawBuckets, viewSet.DrawBucketBuffer);
                cmd.SetComputeBufferParam(cullingShader, _cullInstancesKernel, _ID_VisibilityBuffer, viewSet.VisibilityBuffer);
                cmd.SetComputeBufferParam(cullingShader, _cullInstancesKernel, _ID_VisibleChunks, viewSet.VisibleChunksBuffer);
                cmd.SetComputeBufferParam(cullingShader, _cullInstancesKernel, _ID_FadeStates, fadeBuffer);

                // reset draw bucket counts, indirect args, and counters
                DispatchResetCullingState(cmd, viewSet.DrawBucketBuffer, viewSet.IndirectArgsBuffer,
                    viewSet.VisibleChunkCountBuffer,
                    _bucketCount, _drawCommandCount);

                // enable HZB keyword for Phase1 + bind current-frame HZB
                cmd.EnableKeyword(cullingShader, _hzbEnabledKeyword);
                cmd.SetComputeFloatParam(cullingShader, _ID_HZBPixelExpansion, 0f); // current frame, no temporal mismatch
                cmd.SetComputeFloatParam(cullingShader, _ID_HZBTranslationScale, 0f);
                cmd.SetComputeMatrixParam(cullingShader, _ID_ViewProjMatrix, retainedContext.viewProjMatrix);
                cmd.SetComputeVectorParam(cullingShader, _ID_FacingDirWorldSpace, retainedContext.facingDir);
                cmd.SetComputeVectorParam(cullingShader, _ID_RadialDirWorldSpace, retainedContext.radialDir);
                cmd.SetComputeVectorParam(cullingShader, _ID_HZBCameraPosition, retainedContext.hzbCameraPosition);

                if (!skipHZBTextureBinding)
                {
                    cmd.SetComputeTextureParam(cullingShader, _cullChunksKernel, _ID_HZBTexture, hzbState.hzbAtlas);
                    cmd.SetComputeTextureParam(cullingShader, _cullInstancesKernel, _ID_HZBTexture, hzbState.hzbAtlas);
                }
                BindHZBAtlasUniforms(cmd, cullingShader, hzbState);

                // re-bind compacted CPU-visible chunk input (different CommandBuffer from Phase 0).
                int cpuVisCount = retainedContext.splits[0].cpuVisibleChunkCount;
                if (cpuVisCount > 0 && _cpuVisibleChunkIdsBuffer != null)
                    cmd.SetBufferData(_cpuVisibleChunkIdsBuffer, viewSet.CpuVisibleChunkIds, 0, 0, cpuVisCount);
                cmd.SetComputeBufferParam(cullingShader, _cullChunksKernel, _ID_CpuVisibleChunkIds, _cpuVisibleChunkIdsBuffer);
                cmd.SetComputeIntParam(cullingShader, _ID_CpuVisibleChunkCount, cpuVisCount);

                // re-dispatch: CullChunks -> CullInstances -> FinalizeDrawArgs
#if DEBUG
                cmd.BeginSample("FraxinusGPUBatcher.CullChunks_Phase1");
#endif
                DispatchSafe(cmd, cullingShader, _cullChunksKernel,
                    (int)math.ceil(cpuVisCount / (float)kThreadsChunks));
#if DEBUG
                cmd.EndSample("FraxinusGPUBatcher.CullChunks_Phase1");
#endif

#if DEBUG
                cmd.BeginSample("FraxinusGPUBatcher.CullInstances_Phase1");
#endif
                cmd.DispatchCompute(cullingShader, _cullInstancesKernel, viewSet.VisibleChunkCountBuffer, 0u);
#if DEBUG
                cmd.EndSample("FraxinusGPUBatcher.CullInstances_Phase1");
#endif

                DispatchFinalizeDrawArgs(cmd, viewSet);

#if UNITY_EDITOR
                DispatchDebugSphereCompaction(cmd, viewSet);
#endif
            }
            finally
            {
                ReleaseCameraCullingContext(retainedContext);
            }
        }

        private void ReleaseCameraCullingContext(CameraCullingContext context)
        {
            if (context.frustumPlanes != null)
            {
                _planeArrayPool.Push(context.frustumPlanes);
                context.frustumPlanes = null;
            }
            _deferredCameraPool.Push(context);
        }

        private void CancelPendingHZBRetest()
        {
            foreach (var context in _hzbRetestContexts.Values)
                ReleaseCameraCullingContext(context);
            _hzbRetestContexts.Clear();
        }

        private readonly Dictionary<ViewID, CameraCullingContext> _hzbRetestContexts = new();

        private void BindCameraCullingUniforms(CommandBuffer cmd, CameraCullingContext camera)
        {
            cmd.SetComputeVectorArrayParam(cullingShader, _ID_FrustumPlanes, camera.frustumPlanes);
            cmd.SetComputeFloatParam(cullingShader, _ID_LODBias, camera.lodBias);
            cmd.SetComputeFloatParam(cullingShader, _ID_ScreenRatioScale, camera.screenRatioScale);
            cmd.SetComputeFloatParam(cullingShader, _ID_CrossFadeWidth, Config.debugDisableCrossfade ? 0f : 1f);
            cmd.SetComputeIntParam(cullingShader, _ID_IsOrthographic, camera.isOrthographic ? 1 : 0);
            cmd.SetComputeFloatParam(cullingShader, _ID_OrthoSize, camera.orthoSize);
            cmd.SetComputeIntParam(cullingShader, _ID_AnimatedCrossfadeMode, (int)Config.animatedCrossfadeMode);
            cmd.SetComputeVectorParam(cullingShader, _ID_CameraPosition, camera.cameraPosition);
            cmd.SetComputeFloatParam(cullingShader, _ID_CameraHalfFOV, camera.cameraHalfFOV);
            cmd.SetComputeIntParam(cullingShader, _ID_BucketCount, _bucketCount);
        }

        /// <summary>
        /// Reset bucket instanceCounts, indirect args instanceCounts, and counter buffer
        /// in a single dispatch. Replaces CopyBufferx2 + ResetCounter (3 dispatches -> 1).
        /// </summary>
        private void DispatchResetCullingState(CommandBuffer cmd,
            GraphicsBuffer bucketBuffer, GraphicsBuffer argsBuffer,
            GraphicsBuffer counterBuffer,
            int bucketCount, int drawCommandCount,
            int initialVisibleCount = 0)
        {
            int k = _resetCullingStateKernel;
            cmd.SetComputeBufferParam(cullingShader, k, _ID_DrawBuckets, bucketBuffer);
            cmd.SetComputeBufferParam(cullingShader, k, _ID_IndirectArgs, argsBuffer);
            cmd.SetComputeBufferParam(cullingShader, k, _ID_CounterBuffer, counterBuffer);
            cmd.SetComputeIntParam(cullingShader, _ID_BucketCount, bucketCount);
            cmd.SetComputeIntParam(cullingShader, _ID_DrawCommandCount, drawCommandCount);
            cmd.SetComputeIntParam(cullingShader, _ID_InitialVisibleCount, initialVisibleCount);

            int groups = (int)math.ceil(math.max(bucketCount, drawCommandCount) / (float)kThreadsReset);
            groups = math.max(groups, 1);

            CmdDispatchCull(cmd, "FraxinusGPUBatcher.ResetCullingState", k, groups);
        }

        /// <summary>
        /// Dispatch FinalizeDrawArgs kernel. Used by both Pass 0 and Pass 1.
        /// </summary>
        private void DispatchFinalizeDrawArgs(CommandBuffer cmd, CullingBufferSet viewSet)
        {
            cmd.SetComputeBufferParam(cullingShader, _finalizeDrawArgsKernel, _ID_DrawBuckets, viewSet.DrawBucketBuffer);
            cmd.SetComputeBufferParam(cullingShader, _finalizeDrawArgsKernel, _ID_IndirectArgs, viewSet.IndirectArgsBuffer);

            int totalCameraCmds = _drawCommandCount;
            cmd.SetComputeIntParam(cullingShader, _ID_DrawCommandCount, totalCameraCmds);
#if ENABLE_VR_MODULE
            int instanceMultiplierShift = (UnityEngine.XR.XRSettings.enabled && !SystemInfo.supportsMultiview) ? 1 : 0;
            cmd.SetComputeIntParam(cullingShader, _ID_InstanceMultiplierShift, instanceMultiplierShift);
#endif
#if DEBUG
            cmd.BeginSample("FraxinusGPUBatcher.FinalizeDrawArgs");
#endif
            DispatchSafe(cmd, cullingShader, _finalizeDrawArgsKernel,
                (int)math.ceil(totalCameraCmds / (float)kThreadsFinalize));
#if DEBUG
            cmd.EndSample("FraxinusGPUBatcher.FinalizeDrawArgs");
#endif
        }

        // returns true only if shadow culling actually ran (filled the indirect args); false on any skip
        private bool RunShadowCulling(BatchCullingContext cullingContext, CullingBufferSet shadowSet)
        {
#if DEBUG
            using var _sc = s_MarkerShadowCulling.Auto();
#endif
            if (cullingShader == null || !_gpuCullingReady)
                return false;

            // ensure shared buffers are bound (shadow culling can fire before camera dispatch on first frame)
            if (_dirtyBufferBindings != BufferBindingFlags.None)
                BindSharedBuffers();

            // determine cascade count and extract planes
            int cascadeCount = cullingContext.cullingSplits.Length;

            // ortho culling matrix = directional; spot/point are perspective
            bool isOrthoLight = false;
            if (cascadeCount > 0)
            {
                var m0 = cullingContext.cullingSplits[0].cullingMatrix;
                isOrthoLight = m0[15] == 1f && m0[11] == 0f && m0[7] == 0f && m0[3] == 0f;
            }
            int receiverCullPlaneCount = 0;

            // light-space cascade culling data (ReceiverSphereCuller port)
            float3x3 worldToLightRotation = float3x3.identity;
            int lfPlaneCount = 0;

            if (cascadeCount == 0)
            {
                // spot/point light - no explicit splits, use cullingPlanes directly
                cascadeCount = 1;
                _cascadePlaneCounts[0] = math.min(cullingContext.cullingPlanes.Length, 6);

                for (int p = 0; p < _cascadePlaneCounts[0]; p++)
                {
                    var plane = cullingContext.cullingPlanes[p];
                    _cascadePlanes[p] = new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
                }

                // no culling sphere for non-cascade lights
                _cascadeSpheres[0] = Vector4.zero;
                _cascadeBlendFactorValues[0] = 0f;

                // zero out unused cascade plane counts and spheres
                for (int c = 1; c < kMaxCascades; c++)
                {
                    _cascadePlaneCounts[c] = 0;
                    _cascadeSpheres[c] = Vector4.zero;
                    _cascadeBlendFactorValues[c] = 0f;
                }

                // no receiver planes for ReceiverSphereCuller (no cascades) - GPU falls through with all cascades visible
                lfPlaneCount = 0;
            }
            else
            {
                // directional light with cascades
                if (cascadeCount > _shadowSplitCapacity)
                    _pendingShadowSplitWiden = true;
                cascadeCount = math.min(cascadeCount, _shadowSplitCapacity);

                // light transform - worldToLightRotation for light-space capsule test
                // cullingContext.localToWorldMatrix is the light's local-to-world transform
                float4x4 ltw = (float4x4)cullingContext.localToWorldMatrix;
                float3x3 lightToWorldRotation = new float3x3(ltw.c0.xyz, ltw.c1.xyz, ltw.c2.xyz);
                worldToLightRotation = math.transpose(lightToWorldRotation);

                for (int c = 0; c < cascadeCount; c++)
                {
                    var split = cullingContext.cullingSplits[c];
                    _cascadePlaneCounts[c] = math.min(split.cullingPlaneCount, 6);

                    for (int p = 0; p < _cascadePlaneCounts[c]; p++)
                    {
                        var plane = cullingContext.cullingPlanes[split.cullingPlaneOffset + p];
                        _cascadePlanes[c * 6 + p] = new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
                    }

                    // transform cascade sphere center to light space (ReceiverSphereCuller.Create port)
                    float3 centerWS = (float3)split.sphereCenter;
                    float3 centerLS = math.mul(worldToLightRotation, centerWS);
                    _cascadeSpheres[c] = new Vector4(centerLS.x, centerLS.y, centerLS.z, split.sphereRadius);
                    _cascadeBlendFactorValues[c] = split.cascadeBlendCullingFactor;
                }

                // zero out unused cascade plane counts and spheres
                for (int c = cascadeCount; c < kMaxCascades; c++)
                {
                    _cascadePlaneCounts[c] = 0;
                    _cascadeSpheres[c] = Vector4.zero;
                    _cascadeBlendFactorValues[c] = 0f;
                }

                // extract light-facing receiver planes (ReceiverPlanes.Create port)
                // only valid when multiple splits with valid spheres
                bool allSpheresValid = cascadeCount > 1;
                for (int c = 0; c < cascadeCount && allSpheresValid; c++)
                {
                    if (!(cullingContext.cullingSplits[c].sphereRadius > 0f))
                        allSpheresValid = false;
                }

                if (allSpheresValid)
                {
                    float3 lightDir = -ltw.c2.xyz; // negative Z axis of light = light direction
                    int receiverPlaneCount = cullingContext.receiverPlaneCount;
                    int receiverPlaneOffset = cullingContext.receiverPlaneOffset;
                    for (int i = 0; i < receiverPlaneCount && lfPlaneCount < kMaxLightFacingPlanes; i++)
                    {
                        var plane = cullingContext.cullingPlanes[receiverPlaneOffset + i];
                        if (Vector3.Dot(plane.normal, (Vector3)lightDir) >= 0f)
                        {
                            _lightFacingPlanes[lfPlaneCount] = new Vector4(
                                plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
                            lfPlaneCount++;
                        }
                    }
                }
                else if (isOrthoLight)
                {
                    // no valid spheres (single cascade): cull casters against receiver planes instead
                    float3 lightDir = -ltw.c2.xyz;
                    int rcvCount = cullingContext.receiverPlaneCount;
                    int rcvOffset = cullingContext.receiverPlaneOffset;
                    int planeSignBits = 0;

                    // pass 1: keep light-facing receiver planes
                    for (int i = 0; i < rcvCount; i++)
                    {
                        var plane = cullingContext.cullingPlanes[rcvOffset + i];
                        if (Vector3.Dot(plane.normal, (Vector3)lightDir) < 0f)
                        {
                            planeSignBits |= (1 << i);
                        }
                        else if (receiverCullPlaneCount < kMaxReceiverCullPlanes)
                        {
                            _receiverCullPlanes[receiverCullPlaneCount++] = new Vector4(
                                plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
                        }
                    }

                    // pass 2: silhouette edge planes, ortho variant (normal perpendicular to light dir)
                    if (rcvCount == 6)
                    {
                        for (int i = 0; i < rcvCount; i++)
                        {
                            for (int j = i + 1; j < rcvCount; j++)
                            {
                                if ((i / 2) == (j / 2)) continue; // same frustum axis pair
                                if ((((planeSignBits >> i) ^ (planeSignBits >> j)) & 1) == 0) continue; // same sign

                                var (idxA, idxB) = (((planeSignBits >> i) & 1) == 0) ? (i, j) : (j, i);
                                var pA = cullingContext.cullingPlanes[rcvOffset + idxA];
                                var pB = cullingContext.cullingPlanes[rcvOffset + idxB];

                                float4 eqA = new float4(pA.normal, pA.distance);
                                float4 eqB = new float4(pB.normal, pB.distance);

                                // Plucker line of plane intersection: m = d1*n2 - d2*n1, t = cross(n1, n2)
                                float3 m = eqA.w * eqB.xyz - eqB.w * eqA.xyz;
                                float3 t = math.cross(eqA.xyz, eqB.xyz);

                                // plane containing line with normal perpendicular to light dir
                                float3 normal = math.cross(t, lightDir);
                                float dist = -math.dot(m, lightDir);

                                float len = math.length(normal);
                                if (len > 1e-6f && receiverCullPlaneCount < kMaxReceiverCullPlanes)
                                {
                                    float invLen = 1f / len;
                                    _receiverCullPlanes[receiverCullPlaneCount++] = new Vector4(
                                        normal.x * invLen, normal.y * invLen, normal.z * invLen, dist * invLen);
                                }
                            }
                        }
                    }
                }
            }

            // extract perspective receiver cull planes for spot/point lights (ReceiverPlanes.Create port)
            // these tighten shadow frustum by rejecting casters whose shadows can't reach the camera
            if (!isOrthoLight)
            {
                int rcvCount = cullingContext.receiverPlaneCount;
                int rcvOffset = cullingContext.receiverPlaneOffset;

                if (rcvCount > 0)
                {
                    float3 lightPos = ((float4x4)cullingContext.localToWorldMatrix).c3.xyz;
                    int planeSignBits = 0;

                    // pass 1: classify receiver planes by light position (perspective test)
                    for (int i = 0; i < rcvCount; i++)
                    {
                        var plane = cullingContext.cullingPlanes[rcvOffset + i];
                        float distance = plane.GetDistanceToPoint((Vector3)lightPos);
                        if (distance < 0f)
                        {
                            planeSignBits |= (1 << i);
                        }
                        else if (receiverCullPlaneCount < kMaxReceiverCullPlanes)
                        {
                            _receiverCullPlanes[receiverCullPlaneCount++] = new Vector4(
                                plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
                        }
                    }

                    // pass 2: silhouette edge planes (only when 6 receiver planes = full frustum)
                    // pairs of frustum planes with opposing light-facing signs form silhouette edges
                    if (rcvCount == 6)
                    {
                        for (int i = 0; i < rcvCount; i++)
                        {
                            for (int j = i + 1; j < rcvCount; j++)
                            {
                                if ((i / 2) == (j / 2)) continue; // same frustum axis pair
                                if ((((planeSignBits >> i) ^ (planeSignBits >> j)) & 1) == 0) continue; // same sign

                                // consistent ordering: non-sign-bit (light-facing) plane first
                                var (idxA, idxB) = (((planeSignBits >> i) & 1) == 0) ? (i, j) : (j, i);
                                var pA = cullingContext.cullingPlanes[rcvOffset + idxA];
                                var pB = cullingContext.cullingPlanes[rcvOffset + idxB];

                                float4 eqA = new float4(pA.normal, pA.distance);
                                float4 eqB = new float4(pB.normal, pB.distance);

                                // Pl眉cker line of plane intersection: m = d1*n2 - d2*n1, t = cross(n1, n2)
                                float3 m = eqA.w * eqB.xyz - eqB.w * eqA.xyz;
                                float3 t = math.cross(eqA.xyz, eqB.xyz);

                                // plane containing line and point (perspective): n = m + cross(t, p), d = -dot(m, p)
                                float3 normal = m + math.cross(t, lightPos);
                                float dist = -math.dot(m, lightPos);

                                float len = math.length(normal);
                                if (len > 1e-6f && receiverCullPlaneCount < kMaxReceiverCullPlanes)
                                {
                                    float invLen = 1f / len;
                                    _receiverCullPlanes[receiverCullPlaneCount++] = new Vector4(
                                        normal.x * invLen, normal.y * invLen, normal.z * invLen, dist * invLen);
                                }
                            }
                        }
                    }
                }
            }

            // shadow LOD parameters from culling context (per-view, not Camera.main)
            var shadowLodParams = cullingContext.lodParameters;
            Vector3 shadowCameraPosition = shadowLodParams.cameraPosition;
            float shadowFov = shadowLodParams.fieldOfView;
            float shadowCameraHalfFOV = shadowFov > 0f ? math.tan(math.radians(shadowFov * 0.5f)) : 0.5f;
            bool shadowIsOrthographic = shadowLodParams.isOrthographic;
            float shadowOrthoSize = shadowLodParams.orthoSize;

            float shadowLodBias = _currentCullingContext.perCameraLodBias;

            // shadows always cull normally - frozen culling only affects camera visibility

            // shadow chunk culling

            if (_chunkSystemInitialized && _allocatedCount > 0 && _cullChunksShadowKernel >= 0 &&
                shadowSet.DrawBucketBuffer != null && shadowSet.VisibilityBuffer != null)
            {
                GraphicsBuffer shadowFade = _currentCullingContext?.fadeState?.FadeStateBuffer;
                if (shadowFade == null || !shadowFade.IsValid()) return false;

                if (_shadowActiveCount == 0 && !_shadowChunksRebuiltThisFrame) return false;

                // batch shadow culling dispatches into one CommandBuffer (DX12 UAV barriers)
                _cullingCmd.Clear();

                // dispatch pending fade state update before shadow culling - shadows run synchronously
                // but the deferred camera path hasn't executed yet, so _FadeStates are stale
                if (_currentCullingContext.shouldDispatchFade && uploadShader != null)
                {
                    bool isPerProtoMode = Config.animatedCrossfadeMode == AnimatedCrossfadeMode.PerPrototype;
                    int activeKernel = isPerProtoMode
                        ? _updateFadeStatesPerPrototypeKernel
                        : _updateFadeStatesPerInstanceKernel;

                    if (activeKernel >= 0)
                    {
                        _cullingCmd.SetComputeVectorParam(uploadShader, _ID_CameraPosition, _currentCullingContext.cameraPosition);
                        _cullingCmd.SetComputeFloatParam(uploadShader, _ID_CameraHalfFOV, _currentCullingContext.cameraHalfFOV);
                        _cullingCmd.SetComputeFloatParam(uploadShader, _ID_ScreenRatioScale, _currentCullingContext.screenRatioScale);
                        _cullingCmd.SetComputeFloatParam(uploadShader, _ID_LODBias, _currentCullingContext.lodBias);
                        _cullingCmd.SetComputeFloatParam(uploadShader, _ID_DeltaTime, _currentCullingContext.fadeState.DeltaTime * _crossfadeTimeScale);
                        _cullingCmd.SetComputeIntParam(uploadShader, _ID_SkipIntermediateLODs, Config.skipIntermediateLODs ? 1 : 0);
                        _cullingCmd.SetComputeIntParam(uploadShader, _ID_AnimatedCrossfadeMode, (int)Config.animatedCrossfadeMode);
                        _cullingCmd.SetComputeIntParam(uploadShader, _ID_IsOrthographic, _currentCullingContext.isOrthographic ? 1 : 0);
                        _cullingCmd.SetComputeFloatParam(uploadShader, _ID_OrthoSize, _currentCullingContext.orthoSize);

                        int animatedPoolCount = _animatedCrossfadePools.Length;
                        if (animatedPoolCount > 0 && _animatedPoolsBuffer != null)
                        {
                            _cullingCmd.SetComputeIntParam(uploadShader, _ID_AnimatedPoolCount, animatedPoolCount);
                            _cullingCmd.SetComputeIntParam(uploadShader, _ID_SnapFadeStates, _currentCullingContext.shouldSnap ? 1 : 0);
                            _cullingCmd.SetComputeBufferParam(uploadShader, activeKernel, _ID_FadeStates, _currentCullingContext.fadeState.FadeStateBuffer);
                            _cullingCmd.SetComputeBufferParam(uploadShader, activeKernel, _ID_AnimatedPools, _animatedPoolsBuffer);
                            if (isPerProtoMode && _protoCrossfadeBuffer != null)
                                _cullingCmd.SetComputeBufferParam(uploadShader, activeKernel, _ID_ProtoCrossfadeStates, _protoCrossfadeBuffer);
                            int animatedSlots = animatedPoolCount * kPoolSize;
                            int fadeGroups = (int)math.ceil(animatedSlots / (float)kThreadsInstances);
                            CmdDispatchUpload(_cullingCmd, isPerProtoMode ? "FraxinusGPUBatcher.UpdateFadeStatesProto" : "FraxinusGPUBatcher.UpdateFadeStatesInst",
                                activeKernel, fadeGroups);
                        }

                        _currentCullingContext.shouldDispatchFade = false;
                    }
                }

                // common shadow uniforms
                _cullingCmd.SetComputeIntParam(cullingShader, _ID_CascadeCount, cascadeCount);
                _cullingCmd.SetComputeIntParam(cullingShader, _ID_ApplyCascadeLimits, isOrthoLight ? 1 : 0);

                // shadow caster occlusion vs the owning camera's prev-frame HZB (directional only)
                var shadowHzbState = (Config.shadowCasterOcclusion && isOrthoLight && IsGPUOcclusionEnabled &&
                    _currentCullingContext != null && _currentCullingContext.useHZB)
                    ? _currentCullingContext.hzbState : null;
                bool shadowHZBTestable = shadowHzbState != null && shadowHzbState.hzbValid &&
                    shadowHzbState.prevDataValid && shadowHzbState.hzbAtlas != null;
#if UNITY_EDITOR
                if (Config.debugVisualizeCullingInSceneView)
                    shadowHZBTestable = false;
#endif
                if (shadowHZBTestable)
                {
                    _cullingCmd.EnableKeyword(cullingShader, _hzbEnabledKeyword);

                    float cameraMotionPixels = 0f;
                    if (shadowHzbState.topMipHeight > 0)
                    {
                        float3 prevFwd = new float3(shadowHzbState.prevFacingDir.x, shadowHzbState.prevFacingDir.y, shadowHzbState.prevFacingDir.z);
                        float3 currFwd = new float3(_currentCullingContext.facingDir.x, _currentCullingContext.facingDir.y, _currentCullingContext.facingDir.z);
                        float cosAngle = math.clamp(math.dot(prevFwd, currFwd), -1f, 1f);
                        float verticalFOV = 2f * math.atan(_currentCullingContext.cameraHalfFOV);
                        if (verticalFOV > 0f)
                            cameraMotionPixels = math.acos(cosAngle) * shadowHzbState.topMipHeight / verticalFOV;
                    }

                    float hzbTranslationScale = 0f;
                    float3 prevPos = new float3(shadowHzbState.prevCameraPosition.x, shadowHzbState.prevCameraPosition.y, shadowHzbState.prevCameraPosition.z);
                    float3 currPos = new float3(_currentCullingContext.hzbCameraPosition.x, _currentCullingContext.hzbCameraPosition.y, _currentCullingContext.hzbCameraPosition.z);
                    float posDelta = math.length(currPos - prevPos);
                    if (posDelta > 0.001f && _currentCullingContext.cameraHalfFOV > 0f)
                        hzbTranslationScale = posDelta * shadowHzbState.topMipHeight / (2f * _currentCullingContext.cameraHalfFOV);

                    _cullingCmd.SetComputeFloatParam(cullingShader, _ID_HZBPixelExpansion, Config.hzbPixelExpansion + cameraMotionPixels);
                    _cullingCmd.SetComputeFloatParam(cullingShader, _ID_HZBTranslationScale, hzbTranslationScale);
                    _cullingCmd.SetComputeMatrixParam(cullingShader, _ID_ViewProjMatrix, shadowHzbState.prevViewProjMatrix);
                    _cullingCmd.SetComputeVectorParam(cullingShader, _ID_FacingDirWorldSpace, shadowHzbState.prevFacingDir);
                    _cullingCmd.SetComputeVectorParam(cullingShader, _ID_RadialDirWorldSpace, shadowHzbState.prevRadialDir);
                    _cullingCmd.SetComputeVectorParam(cullingShader, _ID_HZBCameraPosition, shadowHzbState.prevCameraPosition);
                    _cullingCmd.SetComputeTextureParam(cullingShader, _cullChunksShadowKernel, _ID_HZBTexture, shadowHzbState.hzbAtlas);
                    _cullingCmd.SetComputeTextureParam(cullingShader, _cullInstancesShadowKernel, _ID_HZBTexture, shadowHzbState.hzbAtlas);
                    BindHZBAtlasUniforms(_cullingCmd, cullingShader, shadowHzbState);
                }
                else
                {
                    _cullingCmd.DisableKeyword(cullingShader, _hzbEnabledKeyword);
                }
                _cullingCmd.SetComputeIntParams(cullingShader, _ID_CascadePlaneCounts, _cascadePlaneCounts);
                _cullingCmd.SetComputeVectorArrayParam(cullingShader, _ID_CascadePlanes, _cascadePlanes);
                _cullingCmd.SetComputeVectorArrayParam(cullingShader, _ID_CascadeSpheres, _cascadeSpheres);

                // light-space cascade culling uniforms (ReceiverSphereCuller port)
                // Unity Matrix4x4 column-major maps 1:1 to HLSL
                Matrix4x4 wlrMat = Matrix4x4.identity;
                wlrMat.SetColumn(0, new Vector4(worldToLightRotation.c0.x, worldToLightRotation.c0.y, worldToLightRotation.c0.z, 0));
                wlrMat.SetColumn(1, new Vector4(worldToLightRotation.c1.x, worldToLightRotation.c1.y, worldToLightRotation.c1.z, 0));
                wlrMat.SetColumn(2, new Vector4(worldToLightRotation.c2.x, worldToLightRotation.c2.y, worldToLightRotation.c2.z, 0));
                _cullingCmd.SetComputeMatrixParam(cullingShader, _ID_WorldToLightRotation, wlrMat);
                _cascadeBlendFactorUpload[0] = new Vector4(
                    _cascadeBlendFactorValues[0], _cascadeBlendFactorValues[1],
                    _cascadeBlendFactorValues[2], _cascadeBlendFactorValues[3]);
                _cascadeBlendFactorUpload[1] = new Vector4(
                    _cascadeBlendFactorValues[4], _cascadeBlendFactorValues[5], 0f, 0f);
                _cullingCmd.SetComputeVectorArrayParam(cullingShader, _ID_CascadeBlendFactors, _cascadeBlendFactorUpload);
                _cullingCmd.SetComputeVectorArrayParam(cullingShader, _ID_LightFacingPlanes, _lightFacingPlanes);
                _cullingCmd.SetComputeIntParam(cullingShader, _ID_LightFacingPlaneCount, lfPlaneCount);

                // perspective receiver cull planes (spot/point shadow tightening)
                _cullingCmd.SetComputeVectorArrayParam(cullingShader, _ID_ReceiverCullPlanes, _receiverCullPlanes);
                _cullingCmd.SetComputeIntParam(cullingShader, _ID_ReceiverCullPlaneCount, receiverCullPlaneCount);

                float shadowLodBiasValue = QualitySettings.lodBias * shadowLodBias;
                float shadowScreenRatioScale;
                if (shadowIsOrthographic)
                    shadowScreenRatioScale = shadowOrthoSize > 0f ? shadowLodBiasValue / (2f * shadowOrthoSize) : 0f;
                else
                    shadowScreenRatioScale = shadowCameraHalfFOV > 0f ? shadowLodBiasValue / (2f * shadowCameraHalfFOV) : 0f;

                _cullingCmd.SetComputeVectorParam(cullingShader, _ID_CameraPosition, shadowCameraPosition);
                _cullingCmd.SetComputeFloatParam(cullingShader, _ID_CameraHalfFOV, shadowCameraHalfFOV);
                _cullingCmd.SetComputeFloatParam(cullingShader, _ID_LODBias, shadowLodBiasValue);
                _cullingCmd.SetComputeFloatParam(cullingShader, _ID_ScreenRatioScale, shadowScreenRatioScale);
                _cullingCmd.SetComputeFloatParam(cullingShader, _ID_CrossFadeWidth, Config.debugDisableCrossfade ? 0f : 1f);
                _cullingCmd.SetComputeIntParam(cullingShader, _ID_IsOrthographic, shadowIsOrthographic ? 1 : 0);
                _cullingCmd.SetComputeFloatParam(cullingShader, _ID_OrthoSize, shadowOrthoSize);
                _cullingCmd.SetComputeIntParam(cullingShader, _ID_AnimatedCrossfadeMode, (int)Config.animatedCrossfadeMode);

                // per-view shadow buffer binds
                _cullingCmd.SetComputeBufferParam(cullingShader, _cullChunksShadowKernel, _ID_VisibleShadowChunks, shadowSet.VisibleChunksBuffer);
                _cullingCmd.SetComputeBufferParam(cullingShader, _cullChunksShadowKernel, _ID_VisibleShadowChunkCount, shadowSet.VisibleChunkCountBuffer);
                _cullingCmd.SetComputeBufferParam(cullingShader, _cullInstancesShadowKernel, _ID_ShadowDrawBuckets, shadowSet.DrawBucketBuffer);
                _cullingCmd.SetComputeBufferParam(cullingShader, _cullInstancesShadowKernel, _ID_ShadowVisibilityBuffer, shadowSet.VisibilityBuffer);
                _cullingCmd.SetComputeBufferParam(cullingShader, _cullInstancesShadowKernel, _ID_VisibleShadowChunks, shadowSet.VisibleChunksBuffer);
                _cullingCmd.SetComputeBufferParam(cullingShader, _cullInstancesShadowKernel, _ID_FadeStates, shadowFade);
                _cullingCmd.SetComputeBufferParam(cullingShader, _finalizeShadowDrawArgsKernel, _ID_ShadowDrawBuckets, shadowSet.DrawBucketBuffer);
                _cullingCmd.SetComputeBufferParam(cullingShader, _finalizeShadowDrawArgsKernel, _ID_ShadowIndirectArgs, shadowSet.IndirectArgsBuffer);

                // reset shadow buckets, args, counters in one dispatch
                DispatchResetCullingState(_cullingCmd, shadowSet.DrawBucketBuffer, shadowSet.IndirectArgsBuffer,
                    shadowSet.VisibleChunkCountBuffer,
                    _shadowBucketCount, _shadowDrawCommandCount);

                _cullingCmd.SetComputeIntParam(cullingShader, _ID_ShadowChunkCount, _shadowActiveCount);
                _cullingCmd.SetComputeIntParam(cullingShader, _ID_CullingLayerMask, (int)(uint)cullingContext.cullingLayerMask);
                _cullingCmd.SetComputeBufferParam(cullingShader, _cullChunksShadowKernel, _ID_ShadowChunkIds, _shadowChunkIdsBuffer);

                CmdDispatchCull(_cullingCmd, "FraxinusGPUBatcher.CullChunksShadow", _cullChunksShadowKernel, (int)math.ceil(_shadowActiveCount / (float)kThreadsChunks));
                CmdDispatchCullIndirect(_cullingCmd, "FraxinusGPUBatcher.CullInstancesShadow", _cullInstancesShadowKernel, shadowSet.VisibleChunkCountBuffer);

                // finalize shadow draw args
                int totalShadowCmds = _shadowDrawCommandCount;
                _cullingCmd.SetComputeIntParam(cullingShader, _ID_ShadowDrawCommandCount, totalShadowCmds);
                CmdDispatchCull(_cullingCmd, "FraxinusGPUBatcher.FinalizeShadowDrawArgs", _finalizeShadowDrawArgsKernel, (int)math.ceil(totalShadowCmds / (float)kThreadsFinalize));

#if UNITY_EDITOR
                RequestShadowReadback(_cullingCmd, shadowSet, GPUBatcherHelpers.GetViewID(cullingContext.viewID));
#endif

                // execute all batched shadow culling dispatches
                Graphics.ExecuteCommandBuffer(_cullingCmd);
                return true;
            }
            return false;
        }

        #endregion

        #region BRG Callback

        private unsafe JobHandle OnPerformCulling(
            BatchRendererGroup rendererGroup,
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput,
            IntPtr userContext)
        {
#if UNITY_6000_0_OR_NEWER
            cullingOutput.customCullingResult[0] = IntPtr.Zero;
#endif

            if (!_gpuCullingReady || _batchSlotHWM == 0)
                return default;

            if (cullingContext.viewType == BatchCullingViewType.Camera)
            {
                return OnPerformCullingCamera(cullingContext, cullingOutput);
            }
            else if (cullingContext.viewType == BatchCullingViewType.Light)
            {
                return OnPerformCullingShadow(cullingContext, cullingOutput);
            }
#if UNITY_EDITOR
            else if (cullingContext.viewType == BatchCullingViewType.Picking
                  || cullingContext.viewType == BatchCullingViewType.SelectionOutline)
            {
                return OnPerformCullingPicking(cullingContext, cullingOutput);
            }
#endif

            // unknown view type
            return default;
        }

        private unsafe JobHandle OnPerformCullingCamera(
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput)
        {
            if (!_chunkSystemInitialized || _allocatedCount == 0 ||
                !_cameraDrawCommandTemplate.IsCreated || !_cameraIndirectArgsTemplate.IsCreated ||
                _drawCommandCount == 0 || cullingContext.cullingLayerMask == 0)
                return default;

            ViewID viewID = GPUBatcherHelpers.GetViewID(cullingContext.viewID);

            bool isDeferred = _deferredCullingCameras.Contains(viewID);

            // look up per-camera SRP data (populated in OnBeginCameraRendering)
            // non-SRP cameras (baked occlusion viz, etc.) won't have an entry - use defaults
            bool hasCamData = _cameraRenderData.TryGetValue(viewID, out var camData);
            if (!hasCamData)
            {
                // ensure a fade state exists for this view
                if (!_viewFadeStates.TryGetValue(viewID, out var fadeState))
                    fadeState = CreateViewFadeState(viewID);
                camData = new CameraRenderData
                {
                    lodBias = 1.0f,
                    viewFadeState = fadeState,
                };
            }

#if UNITY_EDITOR
            // frozen scene view: emit draw commands from persistent scene view buffer set
            if (Config.debugVisualizeCullingInSceneView && _frozenCullingValid &&
                !camData.isGame && !camData.isRef && _sceneViewCameraBufferSet != null
                && _sceneViewCameraBufferSet.IndirectArgsBuffer != null
                && _sceneViewCameraBufferSet.IndirectArgsBuffer.IsValid())
            {
                _deferredCullingCameras.Remove(viewID);
                _sceneViewShadowReplayIndex = 0;
                // clear stale isRefCamera so shadow replay path sees correct state
                _currentCullingContext.isRefCamera = false;
                _currentCullingContext.isGameCamera = false;
                if (Config.debugDisableRendering) return default;
                EmitCameraDrawCommands(cullingOutput, _sceneViewCameraBufferSet);
                return default;
            }
#endif

            // acquire a per-view buffer set
            CullingBufferSet viewSet;
            JobHandle dependency = default;

#if UNITY_EDITOR
            if (Config.debugVisualizeCullingInSceneView && camData.isRef)
            {
                _frozenCullingValid = true;
                EnsureSceneViewCameraBufferSet();
                viewSet = _sceneViewCameraBufferSet;
                _sceneViewShadowCount = 0;
            }
            else
#endif
            {
                viewSet = AcquireCameraBufferSet();
            }

            // compute dispatch
#if DEBUG
            // debugDisableCulling: skip GPU dispatches, reuse stale data from prior frame
            if (!Config.debugDisableCulling)
#endif
            {
                if (isDeferred)
                {
                    dependency = CacheCameraCullingContext(cullingContext, viewSet, viewID, camData);
#if UNITY_6000_0_OR_NEWER
                    cullingOutput.customCullingResult[0] = GPUBatcherHelpers.ViewIDToIntPtr(viewID);
#endif
                }
                else
                {
                    // non-SRP camera: sync fallback
                    dependency = CacheCameraCullingContext(cullingContext, viewSet, viewID, camData);
                    var cmd = CommandBufferPool.Get("GPUBatcher_SyncCull");
                    ExecuteDeferredCulling(cmd, viewID); // completes jobs + releases slots internally
                    Graphics.ExecuteCommandBuffer(cmd);
                    CommandBufferPool.Release(cmd);
#if UNITY_6000_0_OR_NEWER
                    cullingOutput.customCullingResult[0] = GPUBatcherHelpers.ViewIDToIntPtr(viewID);
#endif
                }
            }

#if DEBUG
            // debugDisableRendering: culling ran but skip draw command emission
            if (Config.debugDisableRendering)
                return dependency;
#endif

            // emit draw commands (same for both paths)
            EmitCameraDrawCommands(cullingOutput, viewSet);

            return dependency;
        }

#if UNITY_EDITOR
        // handles Picking and SelectionOutline view types: two-tier CPU cull, direct draw commands
        private unsafe JobHandle OnPerformCullingPicking(
            in BatchCullingContext cullingContext, BatchCullingOutput cullingOutput)
        {
            if (_pickingEntries.Count == 0 || _batchSlots == null || _protoBatchSlots == null)
                return default;

            int planeCount = math.min(cullingContext.cullingPlanes.Length, 6);
            var planes = stackalloc Vector4[6];
            for (int i = 0; i < planeCount; i++)
            {
                var p = cullingContext.cullingPlanes[i];
                planes[i] = new Vector4(p.normal.x, p.normal.y, p.normal.z, p.distance);
            }

            float3 cameraPos = cullingContext.lodParameters.cameraPosition;

            // chunk visibility cache: 0=untested, 1=visible, 2=culled
            int hwm = _chunkHighWaterMark;
            var chunkVis = hwm <= 512 ? stackalloc byte[hwm] : new byte[hwm];
            for (int i = 0; i < hwm; i++) chunkVis[i] = 0;

            // max LOD0 submeshes per prototype, sizes the draw command allocation
            int maxLod0Submeshes = 1;
            for (int p = 0; p < _protoBatchSlots.Length; p++)
            {
                var sl = _protoBatchSlots[p];
                if (sl == null) continue;
                int lod0Count = 0;
                for (int s = 0; s < sl.Count; s++)
                    if (_batchSlots[sl[s]].lodLevel == 0) lod0Count++;
                if (lod0Count > maxLod0Submeshes) maxLod0Submeshes = lod0Count;
            }

            int maxDraws = _pickingEntries.Count * maxLod0Submeshes;
            var drawCommands = (BatchCullingOutputDrawCommands*)cullingOutput.drawCommands.GetUnsafePtr();

            drawCommands->drawCommands = (BatchDrawCommand*)UnsafeUtility.Malloc(
                maxDraws * UnsafeUtility.SizeOf<BatchDrawCommand>(),
                UnsafeUtility.AlignOf<BatchDrawCommand>(), Allocator.TempJob);
            drawCommands->visibleInstances = (int*)UnsafeUtility.Malloc(
                maxDraws * sizeof(int), sizeof(int), Allocator.TempJob);
            drawCommands->drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<BatchDrawRange>(),
                UnsafeUtility.AlignOf<long>(), Allocator.TempJob);
            #if UNITY_6000_3_OR_NEWER
            drawCommands->drawCommandPickingEntityIds = (EntityId*)UnsafeUtility.Malloc(
                maxDraws * UnsafeUtility.SizeOf<EntityId>(),
                UnsafeUtility.AlignOf<EntityId>(), Allocator.TempJob);
            #else
            drawCommands->drawCommandPickingInstanceIDs = (int*)UnsafeUtility.Malloc(
                maxDraws * sizeof(int), sizeof(int), Allocator.TempJob);
        #endif

            int emitted = 0;
            bool hasSlotProto = _slotProtoIndex.IsCreated;
            foreach (var kvp in _pickingEntries)
            {
                int globalSlot = kvp.Key;
                var entry = kvp.Value;

                // -1=empty, <=-2=disabled, both invisible to GPU
                if (!hasSlotProto || globalSlot < 0 || globalSlot >= _slotProtoIndex.Length) continue;
                int protoIdx = _slotProtoIndex[globalSlot];
                if (protoIdx < 0) continue;
                if (_prototypeVacant != null && protoIdx < _prototypeVacant.Length && _prototypeVacant[protoIdx])
                    continue;

                int poolIdx = globalSlot / kPoolSize;
                int chunkId = -1;
                if (_poolToChunk.IsCreated && poolIdx >= 0 && poolIdx < _poolToChunk.Length)
                    chunkId = _poolToChunk[poolIdx];

                // tier 1: chunk poolCount + AABB frustum cull
                if (chunkId >= 0 && chunkId < hwm)
                {
                    if (chunkVis[chunkId] == 0)
                        chunkVis[chunkId] = (_chunks[chunkId].poolCount > 0 && ChunkPassesFrustum(chunkId, planes, planeCount))
                            ? (byte)1 : (byte)2;
                    if (chunkVis[chunkId] == 2) continue;
                }

                // tier 2: per-instance distance + sphere frustum cull
                if (entry.transform != null)
                {
                    float3 pos = entry.transform.position;

                    if (chunkId >= 0 && chunkId < hwm)
                    {
                        float cullDistSq = _chunks[chunkId].instanceCullDistanceSq;
                        if (cullDistSq < float.MaxValue)
                        {
                            float3 toCam = pos - cameraPos;
                            if (math.dot(toCam, toCam) > cullDistSq) continue;
                        }
                    }

                    float radius = (_protoBoundsRadius.IsCreated && protoIdx < _protoBoundsRadius.Length)
                        ? _protoBoundsRadius[protoIdx] * math.cmax(math.abs((float3)entry.transform.lossyScale))
                        : 1f;
                    if (!SpherePassesFrustum(pos, radius, planes, planeCount))
                        continue;
                }

                if (protoIdx >= _protoBatchSlots.Length) continue;
                var slots = _protoBatchSlots[protoIdx];
                if (slots == null || slots.Count == 0) continue;

                // emit one draw command per LOD0 submesh
                for (int s = 0; s < slots.Count; s++)
                {
                    var batch = _batchSlots[slots[s]];
                    if (batch.lodLevel != 0) continue;

                    drawCommands->visibleInstances[emitted] = globalSlot;
                    drawCommands->drawCommands[emitted] = new BatchDrawCommand
                    {
                        batchID = batch.batchID,
                        meshID = batch.meshID,
                        materialID = batch.materialID,
                        submeshIndex = (ushort)batch.submeshIndex,
                        visibleOffset = (uint)emitted,
                        visibleCount = 1,
                        splitVisibilityMask = 0x1,
                        flags = BatchDrawCommandFlags.None,
                        sortingPosition = 0
                    };
                    #if UNITY_6000_3_OR_NEWER
                    drawCommands->drawCommandPickingEntityIds[emitted] = entry.entityId;
                    #else
                    drawCommands->drawCommandPickingInstanceIDs[emitted] = entry.entityId;
                #endif

                    emitted++;
                }
            }

            drawCommands->drawCommandCount = emitted;
#if UNITY_6000_0_OR_NEWER
            drawCommands->indirectDrawCommandCount = 0;
#endif
            drawCommands->visibleInstanceCount = emitted;

            drawCommands->drawRangeCount = 1;
            drawCommands->drawRanges[0] = new BatchDrawRange
            {
#if UNITY_6000_0_OR_NEWER
                drawCommandsType = BatchDrawCommandType.Direct,
#endif
                drawCommandsBegin = 0,
                drawCommandsCount = (uint)emitted,
                filterSettings = new BatchFilterSettings
                {
                    renderingLayerMask = 0xffffffff,
                    layer = 0,
                    motionMode = MotionVectorGenerationMode.Camera,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    staticShadowCaster = false,
                    allDepthSorted = false
                }
            };
#if UNITY_6000_0_OR_NEWER
            drawCommands->indirectDrawCommands = null;
#endif
            drawCommands->instanceSortingPositions = null;
            drawCommands->instanceSortingPositionFloatCount = 0;

            return default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe bool ChunkPassesFrustum(int chunkId, Vector4* planes, int planeCount)
        {
            var bounds = _chunks[chunkId].bounds;
            var center = bounds.center;
            var ext = bounds.extents;

            for (int i = 0; i < planeCount; i++)
            {
                var pl = planes[i];
                float r = ext.x * math.abs(pl.x) + ext.y * math.abs(pl.y) + ext.z * math.abs(pl.z);
                float d = pl.x * center.x + pl.y * center.y + pl.z * center.z + pl.w;
                if (d + r < 0f) return false;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool SpherePassesFrustum(float3 center, float radius, Vector4* planes, int planeCount)
        {
            for (int i = 0; i < planeCount; i++)
            {
                var pl = planes[i];
                float d = pl.x * center.x + pl.y * center.y + pl.z * center.z + pl.w;
                if (d + radius < 0f) return false;
            }
            return true;
        }
#endif

        private unsafe void EmitCameraDrawCommands(BatchCullingOutput cullingOutput, CullingBufferSet viewSet)
        {
            int totalCmdCount = _drawCommandCount;
            int totalRangeCount = _cameraDrawRangeTemplate.IsCreated && _cameraDrawRangeCount > 0 ? _cameraDrawRangeCount : 1;
            EnsureIndirectSubmissionCapacity(viewSet, totalCmdCount, totalRangeCount);
            var commands = (BatchDrawCommandIndirect*)viewSet.CpuDrawCommands.GetUnsafePtr();
            var ranges = (BatchDrawRange*)viewSet.CpuDrawRanges.GetUnsafePtr();

            // Copy templates and patch per-view buffer handles before crossing the submission boundary.
            UnsafeUtility.MemCpy(commands, _cameraDrawCommandTemplate.GetUnsafeReadOnlyPtr(),
                totalCmdCount * UnsafeUtility.SizeOf<BatchDrawCommandIndirect>());
            var visHandle = viewSet.VisibilityBuffer.bufferHandle;
            var argsHandle = viewSet.IndirectArgsBuffer.bufferHandle;
            for (int i = 0; i < totalCmdCount; i++)
            {
                commands[i].visibleInstancesBufferHandle = visHandle;
                commands[i].indirectArgsBufferHandle = argsHandle;
            }

            if (_cameraDrawRangeTemplate.IsCreated && _cameraDrawRangeCount > 0)
            {
                UnsafeUtility.MemCpy(ranges, _cameraDrawRangeTemplate.GetUnsafeReadOnlyPtr(),
                    _cameraDrawRangeCount * UnsafeUtility.SizeOf<BatchDrawRange>());
            }
            else
            {
                ranges[0] = new BatchDrawRange
                {
#if UNITY_6000_0_OR_NEWER
                    drawCommandsType = BatchDrawCommandType.Indirect,
#endif
                    drawCommandsBegin = 0,
                    drawCommandsCount = (uint)totalCmdCount,
                    filterSettings = new BatchFilterSettings
                    {
                        renderingLayerMask = 0xffffffff,
                        layer = 0,
                        motionMode = MotionVectorGenerationMode.Camera,
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = true,
                        staticShadowCaster = false,
                        allDepthSorted = false
                    }
                };
            }

            BRGIndirectSubmission.TrySubmit(in cullingOutput,
                new ReadOnlySpan<BatchDrawCommandIndirect>(commands, totalCmdCount),
                new ReadOnlySpan<BatchDrawRange>(ranges, totalRangeCount));
        }

        private unsafe JobHandle OnPerformCullingShadow(
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput)
        {
            if (_currentCullingContext == null || !_chunkSystemInitialized || _allocatedCount == 0 ||
                !_shadowDrawCommandTemplate.IsCreated || !_shadowIndirectArgsTemplate.IsCreated ||
                _shadowDrawCommandCount == 0 || cullingContext.cullingLayerMask == 0)
                return default;

#if UNITY_EDITOR
            // frozen scene view: emit shadow draw commands from persistent scene view sets
            if (Config.debugVisualizeCullingInSceneView && _frozenCullingValid &&
                !_currentCullingContext.isGameCamera && !_currentCullingContext.isRefCamera &&
                _sceneViewShadowReplayIndex < _sceneViewShadowCount)
            {
                var frozenShadowSet = _sceneViewShadowBufferSets[_sceneViewShadowReplayIndex++];
                if (frozenShadowSet?.IndirectArgsBuffer != null && frozenShadowSet.IndirectArgsBuffer.IsValid())
                {
                    EmitShadowDrawCommands(cullingContext, cullingOutput, frozenShadowSet);
                    return default;
                }
            }
#endif

            // acquire a per-view shadow buffer set
            CullingBufferSet shadowSet;
#if UNITY_EDITOR
            if (Config.debugVisualizeCullingInSceneView && _currentCullingContext.isRefCamera)
                shadowSet = EnsureSceneViewShadowBufferSet(_sceneViewShadowCount++);
            else
#endif
                shadowSet = AcquireShadowBufferSet();

#if DEBUG
            // debugDisableCulling: skip GPU dispatches, reuse stale data from prior frame
            if (!Config.debugDisableCulling)
#endif
            {
                // on skip, zero the pooled args so the unconditional emit doesn't draw a prior render's shadow counts
                if (!RunShadowCulling(cullingContext, shadowSet)
                    && shadowSet.IndirectArgsBuffer != null
                    && _shadowIndirectArgsTemplate.IsCreated && _shadowIndirectArgsTemplate.Length > 0)
                    shadowSet.IndirectArgsBuffer.SetData(_shadowIndirectArgsTemplate, 0, 0,
                        math.min(_shadowIndirectArgsTemplate.Length, shadowSet.IndirectArgsBuffer.count));
            }

#if DEBUG
            // debugDisableRendering: culling ran but skip draw command emission
            if (Config.debugDisableRendering)
                return default;
#endif

            EmitShadowDrawCommands(cullingContext, cullingOutput, shadowSet);
            return default;
        }

        private unsafe void EmitShadowDrawCommands(
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput,
            CullingBufferSet shadowSet)
        {
            int totalCmdCount = _shadowDrawCommandCount;

            // determine cascade count for this light
            int cascadeCount = math.max(1, math.min(cullingContext.cullingSplits.Length, _shadowSplitCapacity));

            // per-proto cascade limits are directional-only; point/spot faces ignore them
            bool applyProtoCascadeLimit = true;
            if (cullingContext.cullingSplits.Length > 0)
            {
                var m0 = cullingContext.cullingSplits[0].cullingMatrix;
                applyProtoCascadeLimit = m0[15] == 1f && m0[11] == 0f && m0[7] == 0f && m0[3] == 0f;
            }

            int maxRanges = _shadowDrawRangeTemplate.IsCreated && _shadowDrawRangeCount > 0
                ? _shadowDrawRangeCount : 1;
            EnsureIndirectSubmissionCapacity(shadowSet, totalCmdCount, maxRanges);
            var ranges = (BatchDrawRange*)shadowSet.CpuDrawRanges.GetUnsafePtr();
            int preparedRangeCount = 1;

            int cmdIdx = 0;

            // combined camera+light layer mask
            ulong cullingLayerMask = cullingContext.cullingLayerMask;

            // per-view buffer handles
            var visHandle = shadowSet.VisibilityBuffer.bufferHandle;
            var argsHandle = shadowSet.IndirectArgsBuffer.bufferHandle;
            var templatePtr = (BatchDrawCommandIndirect*)_shadowDrawCommandTemplate.GetUnsafeReadOnlyPtr();
            var filterPtr = (ShadowCommandFilterInfo*)_shadowCommandFilterInfo.GetUnsafeReadOnlyPtr();
            var outPtr = (BatchDrawCommandIndirect*)shadowSet.CpuDrawCommands.GetUnsafePtr();

            // emit shadow commands using pre-built templates
            // ranges iterated for layer mask early-out and cascade filtering
            if (_shadowDrawRangeTemplate.IsCreated && _shadowDrawRangeCount > 0)
            {
                int emittedRangeCount = 0;

                for (int r = 0; r < _shadowDrawRangeCount; r++)
                {
                    var rangeTemplate = _shadowDrawRangeTemplate[r];

                    // skip entire range if its layer isn't in the culling mask
                    if ((cullingLayerMask & (1UL << rangeTemplate.filterSettings.layer)) == 0)
                        continue;

                    int rangeBeginEmitted = cmdIdx;
                    int srcBegin = (int)rangeTemplate.drawCommandsBegin;
                    int srcEnd = srcBegin + (int)rangeTemplate.drawCommandsCount;

                    for (int i = srcBegin; i < srcEnd; i++)
                    {
                        var filter = filterPtr[i];

                        // skip commands for cascades beyond this view's or prototype's limit
                        int cascadeLimit = applyProtoCascadeLimit ? math.min(cascadeCount, filter.protoMaxCascades) : cascadeCount;
                        if (filter.cascadeIndex >= cascadeLimit)
                            continue;

                        // copy pre-built command and patch per-view fields
                        outPtr[cmdIdx] = templatePtr[i];
                        outPtr[cmdIdx].splitVisibilityMask = (ushort)(1 << filter.cascadeIndex);
                        outPtr[cmdIdx].visibleInstancesBufferHandle = visHandle;
                        outPtr[cmdIdx].indirectArgsBufferHandle = argsHandle;
                        cmdIdx++;
                    }

                    int rangeEmittedCount = cmdIdx - rangeBeginEmitted;
                    if (rangeEmittedCount > 0)
                    {
                        ranges[emittedRangeCount] = rangeTemplate;
                        ranges[emittedRangeCount].drawCommandsBegin = (uint)rangeBeginEmitted;
                        ranges[emittedRangeCount].drawCommandsCount = (uint)rangeEmittedCount;
                        emittedRangeCount++;
                    }
                }

                preparedRangeCount = math.max(1, emittedRangeCount);

                // if all ranges were empty, emit a single empty range
                if (emittedRangeCount == 0)
                {
                    ranges[0] = new BatchDrawRange
                    {
#if UNITY_6000_0_OR_NEWER
                        drawCommandsType = BatchDrawCommandType.Indirect,
#endif
                        drawCommandsBegin = 0,
                        drawCommandsCount = 0,
                        filterSettings = new BatchFilterSettings
                        {
                            renderingLayerMask = 0xffffffff,
                            layer = 0,
                            motionMode = MotionVectorGenerationMode.Camera,
                            shadowCastingMode = ShadowCastingMode.On,
                            receiveShadows = false,
                            staticShadowCaster = false,
                            allDepthSorted = false
                        }
                    };
                }
            }
            else
            {
                // fallback: single range (no layer sorting), iterate all template commands
                for (int i = 0; i < totalCmdCount; i++)
                {
                    var filter = filterPtr[i];

                    int cascadeLimit = applyProtoCascadeLimit ? math.min(cascadeCount, filter.protoMaxCascades) : cascadeCount;
                    if (filter.cascadeIndex >= cascadeLimit)
                        continue;

                    outPtr[cmdIdx] = templatePtr[i];
                    outPtr[cmdIdx].splitVisibilityMask = (ushort)(1 << filter.cascadeIndex);
                    outPtr[cmdIdx].visibleInstancesBufferHandle = visHandle;
                    outPtr[cmdIdx].indirectArgsBufferHandle = argsHandle;
                    cmdIdx++;
                }

                preparedRangeCount = 1;

                ranges[0] = new BatchDrawRange
                {
#if UNITY_6000_0_OR_NEWER
                    drawCommandsType = BatchDrawCommandType.Indirect,
#endif
                    drawCommandsBegin = 0,
                    drawCommandsCount = (uint)cmdIdx,
                    filterSettings = new BatchFilterSettings
                    {
                        renderingLayerMask = 0xffffffff,
                        layer = 0,
                        motionMode = MotionVectorGenerationMode.Camera,
                        shadowCastingMode = ShadowCastingMode.On,
                        receiveShadows = false,
                        staticShadowCaster = false,
                        allDepthSorted = false
                    }
                };
            }
            BRGIndirectSubmission.TrySubmit(in cullingOutput,
                new ReadOnlySpan<BatchDrawCommandIndirect>(outPtr, cmdIdx),
                new ReadOnlySpan<BatchDrawRange>(ranges, preparedRangeCount));
        }

        #endregion

    }
}
