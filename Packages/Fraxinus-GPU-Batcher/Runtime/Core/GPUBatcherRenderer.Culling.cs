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
                cullResults[i] = AABBInFrustum(aabbMin, aabbMax);
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
            public GraphicsBuffer drawBucketBuffer;
            public GraphicsBuffer visibilityBuffer;
            public GraphicsBuffer indirectArgsBuffer;
            public GraphicsBuffer visibleChunksBuffer;
            public GraphicsBuffer visibleChunkCountBuffer;
            public NativeArray<int> cpuVisibleChunkIds; // survives until cmd executes
            // Renderer-owned scratch; native submission copies it into engine-owned allocations.
            public NativeArray<BatchDrawCommandIndirect> cpuDrawCommands;
            public NativeArray<BatchDrawRange> cpuDrawRanges;
            public int generation;  // matches _bufferPoolGeneration when valid

            public void Dispose()
            {
                drawBucketBuffer?.Dispose();
                visibilityBuffer?.Dispose();
                indirectArgsBuffer?.Dispose();
                visibleChunksBuffer?.Dispose();
                visibleChunkCountBuffer?.Dispose();
                cpuVisibleChunkIds.DisposeIfCreated();
                cpuDrawCommands.DisposeIfCreated();
                cpuDrawRanges.DisposeIfCreated();
            }
        }

        /// <summary>
        /// Per-view animated crossfade state. Each camera needs its own fade buffer
        /// so LOD transitions are correct for that view's distance.
        /// </summary>
        private sealed class ViewFadeState : System.IDisposable
        {
            public GraphicsBuffer fadeStateBuffer;
            public uint lastSeenFrame;
            public uint lastFadeUpdateFrame;
            public bool snapRequested;  // set true to snap on next cull, auto-clears
#if UNITY_EDITOR
            public bool isPersistent;  // never evicted or pooled (scene view only)
#endif


#if UNITY_EDITOR
            public float deltaTime => _calculatedDT;
            private float _calculatedDT;
            private double _lastTime;
            public void CalculateDeltaTime()
            {
                double now = Time.realtimeSinceStartupAsDouble;
                _calculatedDT = (float)(now - _lastTime);
                _lastTime = now;
            }
#else
            public float deltaTime => Time.unscaledDeltaTime;
#endif

            public void Dispose()
            {
                fadeStateBuffer?.Dispose();
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

        // Advances in SystemUpdate independently of Time.frameCount for editor rendering.
        private uint _renderCycleCounter = 1;
        internal uint renderCycleCounter => _renderCycleCounter;

        // bump after modifying bucket/args templates - triggers per-view buffer re-seed via SetData
        private int _bufferPoolGeneration;

        // per-camera animated crossfade state, keyed by BatchCullingContext.viewID
        private readonly Dictionary<ViewID, ViewFadeState> _viewFadeStates = new();
        private readonly List<ViewID> _viewFadeStateKeys = new();  // mirror of _viewFadeStates keys
        private int _trackedTotalFadeStates;  // running total of fade state buffer elements across all views
        private readonly List<ViewID> _staleViewKeys = new();
        private const int k_viewFadeEvictFrames = 3;

        private const int k_maxCascades = 6;

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
                    UpdateBufferSet(set, bucketCount, visCount, drawCmdCount, prefix, bucketTemplate, argsTemplate);
            }
            else
            {
                set = new CullingBufferSet();
                UpdateBufferSet(set, bucketCount, visCount, drawCmdCount, prefix, bucketTemplate, argsTemplate);
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

            if (set.drawBucketBuffer == null || set.drawBucketBuffer.count != bucketCount)
            {
                set.drawBucketBuffer?.Dispose();
                set.drawBucketBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bucketCount, bucketStride);
#if DEBUG
                set.drawBucketBuffer.name = $"FraxinusGPUBatcher_{prefix}Buckets";
#endif
            }

            if (set.visibilityBuffer == null || set.visibilityBuffer.count < visCount)
            {
                set.visibilityBuffer?.Dispose();
                set.visibilityBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, visCount, sizeof(uint));
#if DEBUG
                set.visibilityBuffer.name = $"FraxinusGPUBatcher_{prefix}Visibility";
#endif
            }

            if (set.indirectArgsBuffer == null || set.indirectArgsBuffer.count != argsCount)
            {
                set.indirectArgsBuffer?.Dispose();
                var indirectArgsFlags = GraphicsBuffer.Target.IndirectArguments |
                    (useRawIndirectArgs ? GraphicsBuffer.Target.Raw : GraphicsBuffer.Target.Structured);
                set.indirectArgsBuffer = new GraphicsBuffer(indirectArgsFlags, argsCount, sizeof(uint));
#if DEBUG
                set.indirectArgsBuffer.name = $"FraxinusGPUBatcher_{prefix}IndirectArgs";
#endif
            }

            // seed static template data (bufferOffset, maxSlots, indexCount, etc.)
            // only runs on batch rebuild / buffer resize, not per-frame
            if (bucketTemplate.IsCreated)
                set.drawBucketBuffer.SetData(bucketTemplate, 0, 0, bucketCount);
            if (argsTemplate.IsCreated)
                set.indirectArgsBuffer.SetData(argsTemplate, 0, 0, argsCount);

            if (set.visibleChunksBuffer == null || set.visibleChunksBuffer.count < chunkCount)
            {
                set.visibleChunksBuffer?.Dispose();
                set.visibleChunksBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, chunkCount, sizeof(uint));
#if DEBUG
                set.visibleChunksBuffer.name = $"FraxinusGPUBatcher_{prefix}VisibleChunks";
#endif
                set.cpuVisibleChunkIds.DisposeIfCreated();
                set.cpuVisibleChunkIds = new NativeArray<int>(chunkCount, Allocator.Persistent);
            }

            if (set.visibleChunkCountBuffer == null)
            {
                var chunkCountFlags = GraphicsBuffer.Target.IndirectArguments |
                    (useRawIndirectArgs ? GraphicsBuffer.Target.Raw : GraphicsBuffer.Target.Structured);
                set.visibleChunkCountBuffer = new GraphicsBuffer(chunkCountFlags, 4, sizeof(uint));
#if DEBUG
                set.visibleChunkCountBuffer.name = $"FraxinusGPUBatcher_{prefix}VisibleChunkCount";
#endif
                set.visibleChunkCountBuffer.SetData(s_counterResetData);
            }

            set.generation = _bufferPoolGeneration;
        }

        // called from OnBeginCameraRendering (main thread, before any culling for this camera)
        private ViewFadeState CreateViewFadeState(ViewID viewID)
        {
            var state = new ViewFadeState();
            int fadeStateCount = math.max(1, _poolCapacity * k_poolSize);
            state.fadeStateBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                fadeStateCount,
                sizeof(int) * 2  // fadeState: lodPacked (int), fadeProgress (float)
            );
            state.fadeStateBuffer.name = $"FraxinusGPUBatcher_FadeState_View{viewID}";
            GPUInitFadeStateTyped(state.fadeStateBuffer, 0, fadeStateCount);
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
            if (count <= 0 || _viewFadeStates.Count == 0)
                return;

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
        public void RequestSnapAllViews() => SnapAllCrossfadeStates();

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
        private static readonly int s_idAnimatedPoolCount = Shader.PropertyToID("_AnimatedPoolCount");
        private static readonly int s_idAnimatedPools = Shader.PropertyToID("_AnimatedPools");
        private static readonly int s_idArgsToBucket = Shader.PropertyToID("_ArgsToBucket");
        private static readonly int s_idBucketCount = Shader.PropertyToID("_BucketCount");
        private static readonly int s_idCameraHalfFOV = Shader.PropertyToID("_CameraHalfFOV");
        private static readonly int s_idCameraPosition = Shader.PropertyToID("_CameraPosition");
        private static readonly int s_idCascadeCount = Shader.PropertyToID("_CascadeCount");
        private static readonly int s_idCascadePlaneCounts = Shader.PropertyToID("_CascadePlaneCounts");
        private static readonly int s_idCascadePlanes = Shader.PropertyToID("_CascadePlanes");
        private static readonly int s_idCascadeSpheres = Shader.PropertyToID("_CascadeSpheres");
        private static readonly int s_idCrossFadeWidth = Shader.PropertyToID("_CrossFadeWidth");
        private static readonly int s_idCullDistanceSq = Shader.PropertyToID("_CullDistanceSq");
        private static readonly int s_idDeltaTime = Shader.PropertyToID("_DeltaTime");
        private static readonly int s_idDrawBuckets = Shader.PropertyToID("_DrawBuckets");
        private static readonly int s_idDrawCommandCount = Shader.PropertyToID("_DrawCommandCount");
        private static readonly int s_idFadeStates = Shader.PropertyToID("_FadeStates");
        private static readonly int s_idFrustumPlanes = Shader.PropertyToID("_FrustumPlanes");
        private static readonly int s_idIndirectArgs = Shader.PropertyToID("_IndirectArgs");
        private static readonly int s_idInitialVisibleCount = Shader.PropertyToID("_InitialVisibleCount");
        private static readonly int s_idInstanceMultiplierShift = Shader.PropertyToID("_InstanceMultiplierShift");
        private static readonly int s_idInstances = Shader.PropertyToID("_Instances");
        private static readonly int s_idLodBias = Shader.PropertyToID("_LODBias");
        private static readonly int s_idScreenRatioScale = Shader.PropertyToID("_ScreenRatioScale");
        private static readonly int s_idCpuVisibleChunkIds = Shader.PropertyToID("_CpuVisibleChunkIds");
        private static readonly int s_idCpuVisibleChunkCount = Shader.PropertyToID("_CpuVisibleChunkCount");
        private static readonly int s_idPoolRefs = Shader.PropertyToID("_PoolRefs");
        private static readonly int s_idSignatures = Shader.PropertyToID("_Signatures");

        private static readonly int s_idIsOrthographic = Shader.PropertyToID("_IsOrthographic");
        private static readonly int s_idOrthoSize = Shader.PropertyToID("_OrthoSize");
        private static readonly int s_idShadowArgsToBucket = Shader.PropertyToID("_ShadowArgsToBucket");
        private static readonly int s_idShadowDrawBuckets = Shader.PropertyToID("_ShadowDrawBuckets");
        private static readonly int s_idShadowDrawCommandCount = Shader.PropertyToID("_ShadowDrawCommandCount");
        private static readonly int s_idShadowIndirectArgs = Shader.PropertyToID("_ShadowIndirectArgs");
        private static readonly int s_idShadowVisibilityBuffer = Shader.PropertyToID("_ShadowVisibilityBuffer");
        private static readonly int s_idSkipIntermediateLODs = Shader.PropertyToID("_SkipIntermediateLODs");
        private static readonly int s_idSnapFadeStates = Shader.PropertyToID("_SnapFadeStates");
        private static readonly int s_idAnimatedCrossfadeMode = Shader.PropertyToID("_AnimatedCrossfadeMode");
        private static readonly int s_idProtoCrossfadeStates = Shader.PropertyToID("_ProtoCrossfadeStates");
        private static readonly int s_idChunks = Shader.PropertyToID("_Chunks");
        private static readonly int s_idVisibilityBuffer = Shader.PropertyToID("_VisibilityBuffer");
        private static readonly int s_idVisibleChunkCount = Shader.PropertyToID("_VisibleChunkCount");
        private static readonly int s_idVisibleChunks = Shader.PropertyToID("_VisibleChunks");
        private static readonly int s_idVisibleShadowChunkCount = Shader.PropertyToID("_VisibleShadowChunkCount");
        private static readonly int s_idVisibleShadowChunks = Shader.PropertyToID("_VisibleShadowChunks");
        private static readonly int s_idShadowChunkIds = Shader.PropertyToID("_ShadowChunkIds");
        private static readonly int s_idShadowChunkCount = Shader.PropertyToID("_ShadowChunkCount");
        private static readonly int s_idCullingLayerMask = Shader.PropertyToID("_CullingLayerMask");
        private static readonly int s_idCounterBuffer = Shader.PropertyToID("_CounterBuffer");
        // light-space cascade culling (ReceiverSphereCuller port)
        private static readonly int s_idWorldToLightRotation = Shader.PropertyToID("_WorldToLightRotation");
        private static readonly int s_idCascadeBlendFactors = Shader.PropertyToID("_CascadeBlendFactors");
        private static readonly int s_idApplyCascadeLimits = Shader.PropertyToID("_ApplyCascadeLimits");
        private static readonly int s_idLightFacingPlanes = Shader.PropertyToID("_LightFacingPlanes");
        private static readonly int s_idLightFacingPlaneCount = Shader.PropertyToID("_LightFacingPlaneCount");
        // perspective receiver cull planes (spot/point shadow tightening)
        private static readonly int s_idReceiverCullPlanes = Shader.PropertyToID("_ReceiverCullPlanes");
        private static readonly int s_idReceiverCullPlaneCount = Shader.PropertyToID("_ReceiverCullPlaneCount");

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

        private void ReleaseSplitVisibilitySlots(CameraCullingContext context)
        {
            var splits = context?.splits;
            if (splits == null) return;
            foreach (var split in splits)
            {
                if (split.chunkCullJobScheduled)
                    split.chunkCullJobHandle.Complete();
                ReleaseVisibilitySlot(split.visibilitySlot);
            }
        }

        // releases all visibility slots held by pending deferred contexts
        private void DrainDeferredVisibilitySlots()
        {
            CancelPendingHZBRetest();
            foreach (var context in _deferredContexts.Values)
                ReleaseSplitVisibilitySlots(context);
        }

        private void DrainOrphanedDeferredContexts()
        {
            CancelPendingHZBRetest();
            if (_deferredContexts.Count == 0) return;
            foreach (var context in _deferredContexts.Values)
            {
                ReleaseSplitVisibilitySlots(context);
                context.splits?.Clear();
                ReleaseCameraCullingContext(context);
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
        private ViewID _frozenReferenceCameraId;
        private ViewID _activeCullingCameraId;
        private CameraRenderData _activeCullingCameraData;
        private bool _replayingFrozenCulling;
        // persistent scene view buffer sets (not in pool, survive across frames)
        private CullingBufferSet _sceneViewCameraBufferSet;
        private readonly List<CullingBufferSet> _sceneViewShadowBufferSets = new();
        private int _sceneViewShadowCount;
        private int _sceneViewShadowReplayIndex;

        private void InvalidateFrozenCulling()
        {
            _frozenCullingValid = false;
            _frozenReferenceCameraId = default;
            _sceneViewShadowCount = 0;
            _sceneViewShadowReplayIndex = 0;
            _replayingFrozenCulling = false;
        }

        private bool IsCurrentCullingBuffer(CullingBufferSet set) =>
            set != null && set.generation == _bufferPoolGeneration &&
            set.indirectArgsBuffer != null && set.indirectArgsBuffer.IsValid() &&
            set.visibilityBuffer != null && set.visibilityBuffer.IsValid();

        private bool CanReplayFrozenCulling(ViewID referenceCameraId) =>
            _config.debugVisualizeCullingInSceneView && referenceCameraId != default &&
            referenceCameraId == _frozenReferenceCameraId && _frozenCullingValid &&
            IsCurrentCullingBuffer(_sceneViewCameraBufferSet);

        private void PublishFrozenCulling(CameraCullingContext camera)
        {
            if (camera.referenceCameraId == default || camera.referenceCameraId != _frozenReferenceCameraId ||
                camera.cameraId != camera.referenceCameraId || !_config.debugVisualizeCullingInSceneView ||
                camera.splits == null || camera.splits.Count == 0)
                return;
            _frozenCullingValid = ReferenceEquals(camera.splits[0].viewSet, _sceneViewCameraBufferSet) &&
                IsCurrentCullingBuffer(_sceneViewCameraBufferSet);
        }

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
            public bool isSceneView;
            public ViewID referenceCameraId;
#endif
        }
        private readonly Dictionary<ViewID, CameraRenderData> _cameraRenderData = new();

        // shadow cascade data (uploaded per-light)
        private Vector4[] _cascadePlanes = new Vector4[k_maxCascades * 6];
        private int[] _cascadePlaneCounts = new int[8]; // int4[2] on GPU
        private Vector4[] _cascadeSpheres = new Vector4[k_maxCascades];

        // light-space cascade culling data
        private const int k_maxLightFacingPlanes = 6;
        private float[] _cascadeBlendFactorValues = new float[k_maxCascades];
        private Vector4[] _cascadeBlendFactorUpload = new Vector4[2];
        private Vector4[] _lightFacingPlanes = new Vector4[k_maxLightFacingPlanes];

        // perspective receiver cull planes (light-facing + silhouette, spot/point only)
        private const int k_maxReceiverCullPlanes = 18; // 6 light-facing + 12 silhouette max
        private Vector4[] _receiverCullPlanes = new Vector4[k_maxReceiverCullPlanes];

        // compute shader thread group sizes
        private const int k_threadsInstances = 256;
        private const int k_threadsChunks = 64;
        private const int k_threadsFinalize = 64;
        private const int k_threadsReset = 64;

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
            public GPUBatcherOcclusionBridge.CameraState bakedOcclusion;
            public uint bakedOcclusionGeneration;
        }

        /// <summary>
        /// Per-camera culling state captured in OnPerformCulling and consumed in ExecuteDeferredCulling.
        /// Contains shared camera parameters and a list of per-split contexts.
        /// </summary>
        private class CameraCullingContext
        {
            // Parameters of this view's native culling request.
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
            public ViewID referenceCameraId;
            public bool debugBypassCulling;
#endif
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
        internal bool hasPendingDeferredCulling => _deferredContexts.Count > 0;

        // active camera - set when storing/retrieving from dict
        private CameraCullingContext _currentCullingContext;
        // last deferred camera ID
        private ViewID _lastDeferredCameraId;
        internal ViewID lastDeferredCameraId => _lastDeferredCameraId;

        // expose buffer refs for render graph import
        internal CullingBufferSet pendingCullingBufferSet =>
            _currentCullingContext?.splits != null && _currentCullingContext.splits.Count > 0 ? _currentCullingContext.splits[0].viewSet : null;

        // Invoked during native culling after the camera's deferred context is stored.
        internal static Action<ViewID> onDeferredCullingCached;

        #endregion

        #region GPU Culling

        // bind shared read-only buffers to kernels using granular dirty flags
        private void BindSharedBuffers()
        {
            if (_cullingShader == null || !_chunkSystemInitialized || _gpuSignatureBuffer == null) return;
            if (_dirtyBufferBindings == BufferBindingFlags.None) return;  // nothing to bind

            // bind ChunkBuffer
            if ((_dirtyBufferBindings & BufferBindingFlags.ChunkBuffer) != 0 && _chunkBuffer != null)
            {
                if (_cullChunksKernel >= 0)
                    _cullingShader.SetBuffer(_cullChunksKernel, s_idChunks, _chunkBuffer);
                if (_cullInstancesKernel >= 0)
                    _cullingShader.SetBuffer(_cullInstancesKernel, s_idChunks, _chunkBuffer);
                if (_cullChunksShadowKernel >= 0)
                    _cullingShader.SetBuffer(_cullChunksShadowKernel, s_idChunks, _chunkBuffer);
                if (_cullInstancesShadowKernel >= 0)
                    _cullingShader.SetBuffer(_cullInstancesShadowKernel, s_idChunks, _chunkBuffer);
            }

            // bind PoolRefs
            if ((_dirtyBufferBindings & BufferBindingFlags.PoolRefs) != 0 && _poolRefBuffer != null)
            {
                if (_cullInstancesKernel >= 0)
                    _cullingShader.SetBuffer(_cullInstancesKernel, s_idPoolRefs, _poolRefBuffer);
                if (_cullInstancesShadowKernel >= 0)
                    _cullingShader.SetBuffer(_cullInstancesShadowKernel, s_idPoolRefs, _poolRefBuffer);
            }

            // bind Instances + InstanceRotScale
            if ((_dirtyBufferBindings & BufferBindingFlags.Instances) != 0 && _instanceBuffer != null)
            {
                if (_cullInstancesKernel >= 0)
                    _cullingShader.SetBuffer(_cullInstancesKernel, s_idInstances, _instanceBuffer);
                if (_cullInstancesShadowKernel >= 0)
                    _cullingShader.SetBuffer(_cullInstancesShadowKernel, s_idInstances, _instanceBuffer);
                if (_uploadShader != null)
                {
                    if (_updateFadeStatesPerInstanceKernel >= 0)
                        _uploadShader.SetBuffer(_updateFadeStatesPerInstanceKernel, s_idInstances, _instanceBuffer);
                    if (_updateFadeStatesPerPrototypeKernel >= 0)
                        _uploadShader.SetBuffer(_updateFadeStatesPerPrototypeKernel, s_idInstances, _instanceBuffer);
                }
            }

            // bind Signatures
            if ((_dirtyBufferBindings & BufferBindingFlags.Signatures) != 0 && _gpuSignatureBuffer != null)
            {
                if (_cullInstancesKernel >= 0)
                    _cullingShader.SetBuffer(_cullInstancesKernel, s_idSignatures, _gpuSignatureBuffer);
                if (_cullInstancesShadowKernel >= 0)
                    _cullingShader.SetBuffer(_cullInstancesShadowKernel, s_idSignatures, _gpuSignatureBuffer);
                if (_uploadShader != null)
                {
                    if (_updateFadeStatesPerInstanceKernel >= 0)
                        _uploadShader.SetBuffer(_updateFadeStatesPerInstanceKernel, s_idSignatures, _gpuSignatureBuffer);
                    if (_updateFadeStatesPerPrototypeKernel >= 0)
                        _uploadShader.SetBuffer(_updateFadeStatesPerPrototypeKernel, s_idSignatures, _gpuSignatureBuffer);
                }
            }

            // bind ArgsToBucket
            if ((_dirtyBufferBindings & BufferBindingFlags.ArgsToBucket) != 0)
            {
                if (_finalizeDrawArgsKernel >= 0 && _argsToBucketBuffer != null)
                    _cullingShader.SetBuffer(_finalizeDrawArgsKernel, s_idArgsToBucket, _argsToBucketBuffer);
                if (_finalizeShadowDrawArgsKernel >= 0 && _shadowArgsToBucketBuffer != null)
                    _cullingShader.SetBuffer(_finalizeShadowDrawArgsKernel, s_idShadowArgsToBucket, _shadowArgsToBucketBuffer);
            }

            // bind ProtoCrossfadeStates
            if ((_dirtyBufferBindings & BufferBindingFlags.ProtoCrossfade) != 0 && _protoCrossfadeBuffer != null)
            {
                if (_cullInstancesKernel >= 0)
                    _cullingShader.SetBuffer(_cullInstancesKernel, s_idProtoCrossfadeStates, _protoCrossfadeBuffer);
                if (_cullInstancesShadowKernel >= 0)
                    _cullingShader.SetBuffer(_cullInstancesShadowKernel, s_idProtoCrossfadeStates, _protoCrossfadeBuffer);
            }

            _dirtyBufferBindings = BufferBindingFlags.None;
        }

        // dispatch compute with profiler markers
        private void CmdDispatchCull(CommandBuffer cmd, string name, int kernel, int groupsX)
        {
#if DEBUG
            cmd.BeginSample(name);
#endif
            DispatchSafe(cmd, _cullingShader, kernel, groupsX);
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
            cmd.DispatchCompute(_cullingShader, kernel, argsBuffer, 0);
#if DEBUG
            cmd.EndSample(name);
#endif
        }

        // Reuse per-view CPU submission data; only a native backend may allocate BRG-owned output.
        private static void EnsureIndirectSubmissionCapacity(CullingBufferSet viewSet, int cmdCount, int rangeCount)
        {
            cmdCount = math.max(1, cmdCount);
            rangeCount = math.max(1, rangeCount);
            if (!viewSet.cpuDrawCommands.IsCreated || viewSet.cpuDrawCommands.Length < cmdCount)
            {
                viewSet.cpuDrawCommands.DisposeIfCreated();
                viewSet.cpuDrawCommands = new NativeArray<BatchDrawCommandIndirect>(cmdCount, Allocator.Persistent);
            }
            if (!viewSet.cpuDrawRanges.IsCreated || viewSet.cpuDrawRanges.Length < rangeCount)
            {
                viewSet.cpuDrawRanges.DisposeIfCreated();
                viewSet.cpuDrawRanges = new NativeArray<BatchDrawRange>(rangeCount, Allocator.Persistent);
            }
        }

        /// <summary>
        /// Deferred path: extract all needed values from BatchCullingContext (ref struct),
        /// schedule frustum/distance chunk jobs, and copy template buffers. Returns job handle to Unity.
        /// Native baked occlusion is applied after the same camera Cull completes.
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

#if UNITY_EDITOR
                _currentCullingContext.cameraId = cameraId;
                _currentCullingContext.referenceCameraId = camData.referenceCameraId;
                _currentCullingContext.debugBypassCulling = _config.debugDisableCPUChunkCulling;
#endif

                // precompute screen ratio scale
                _currentCullingContext.lodBias = QualitySettings.lodBias * _currentCullingContext.perCameraLodBias;
                if (_currentCullingContext.isOrthographic)
                    _currentCullingContext.screenRatioScale = _currentCullingContext.orthoSize > 0f ? _currentCullingContext.lodBias / (2f * _currentCullingContext.orthoSize) : 0f;
                else
                    _currentCullingContext.screenRatioScale = _currentCullingContext.cameraHalfFOV > 0f ? _currentCullingContext.lodBias / (2f * _currentCullingContext.cameraHalfFOV) : 0f;

                // resolve fade buffer
                _currentCullingContext.fadeState = camData.viewFadeState;

                // evaluate fade state mutations
                _currentCullingContext.shouldDispatchFade = false;
                _currentCullingContext.shouldSnap = false;
                if (_currentCullingContext.fadeState?.fadeStateBuffer != null && _anyPrototypeUsesAnimatedCrossfade && !_config.debugDisableCrossfade)
                {
                    bool isPerProtoMode = _config.animatedCrossfadeMode == AnimatedCrossfadeMode.PerPrototype;
                    int activeKernel = isPerProtoMode
                        ? _updateFadeStatesPerPrototypeKernel
                        : _updateFadeStatesPerInstanceKernel;

                    if (activeKernel >= 0)
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
            var bakedOcclusion = GetBakedOcclusion(cameraId);
            var split = new DeferredCullContext
            {
                viewSet = viewSet,
                visibilitySlot = visSlot,
                bakedOcclusion = bakedOcclusion,
                bakedOcclusionGeneration = bakedOcclusion?.generation ?? 0,
            };

            // schedule CPU chunk cull job - each split has its own visibility arrays
            JobHandle chunkCullJobHandle = default;

            bool debugBypassCulling;
#if UNITY_EDITOR
            debugBypassCulling = _currentCullingContext.debugBypassCulling;
#else
            debugBypassCulling = false;
#endif
            if (!debugBypassCulling)
            {
                chunkCullJobHandle = new ChunkCullJob
                {
                    activeChunks = _activeChunks,
                    chunkEnabled = _chunkEnabled,
                    chunks = _chunks,
                    chunkLayerMask = _chunkLayerMask,
                    cullResults = visSlot.cullResults,
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
                    visibleChunkIds = viewSet.cpuVisibleChunkIds,
                    visibleCount = visSlot.visibleCount,
                }.Schedule(chunkCullJobHandle);

                split.chunkCullJobScheduled = true;
                split.chunkCullJobHandle = chunkCullJobHandle;
            }

            _currentCullingContext.splits.Add(split);
            _deferredContexts[cameraId] = _currentCullingContext;
            _lastDeferredCameraId = cameraId;

            if (isFirstSplit)
                onDeferredCullingCached?.Invoke(cameraId);

            return chunkCullJobHandle;
        }

        /// <summary>
        /// Completes CPU chunk jobs and records camera GPU culling before depth/shadow rendering.
        /// History mode tests the previous HZB; retest mode first runs without occlusion.
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

            if (_cullingShader == null || !_gpuCullingReady)
            {
                ReleaseSplitVisibilitySlots(_currentCullingContext);
                ReleaseCameraCullingContext(_currentCullingContext);
                return;
            }

            // rebind shared read-only buffers if dirty
            if (_dirtyBufferBindings != BufferBindingFlags.None)
                BindSharedBuffers();

            if (!_chunkSystemInitialized || _allocatedCount == 0 || _cullChunksKernel < 0)
            {
                ReleaseSplitVisibilitySlots(_currentCullingContext);
                ReleaseCameraCullingContext(_currentCullingContext);
                return;
            }

            bool hzbActive = _currentCullingContext.useHZB && isGPUOcclusionEnabled;

            BindCameraCullingUniforms(cmd, cam);

            // HZB keyword + uniforms
            bool enableHZBPhase0 = hzbActive && !_config.useHZBRetestPass;
            if (enableHZBPhase0)
            {
                var hzbState = _currentCullingContext.hzbState;
                bool hzbTestable = hzbState != null && hzbState.hzbValid && hzbState.prevDataValid && hzbState.hzbAtlas != null;

                if (hzbTestable)
                {
                    cmd.EnableKeyword(_cullingShader, _hzbEnabledKeyword);

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
                    float hzbPixelExpansion = _config.hzbPixelExpansion + cameraMotionPixels;

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

                    cmd.SetComputeFloatParam(_cullingShader, s_idHzbPixelExpansion, hzbPixelExpansion);
                    cmd.SetComputeFloatParam(_cullingShader, s_idHzbTranslationScale, hzbTranslationScale);
                    cmd.SetComputeMatrixParam(_cullingShader, s_idViewProjMatrix, hzbState.prevViewProjMatrix);
                    cmd.SetComputeVectorParam(_cullingShader, s_idFacingDirWorldSpace, hzbState.prevFacingDir);
                    cmd.SetComputeVectorParam(_cullingShader, s_idRadialDirWorldSpace, hzbState.prevRadialDir);
                    cmd.SetComputeVectorParam(_cullingShader, s_idHzbCameraPosition, hzbState.prevCameraPosition);
                    if (!skipHZBTextureBinding)
                    {
                        cmd.SetComputeTextureParam(_cullingShader, _cullChunksKernel, s_idHzbTexture, hzbState.hzbAtlas);
                        cmd.SetComputeTextureParam(_cullingShader, _cullInstancesKernel, s_idHzbTexture, hzbState.hzbAtlas);
                    }
                    BindHZBAtlasUniforms(cmd, _cullingShader, hzbState);
                }
                else
                {
                    cmd.DisableKeyword(_cullingShader, _hzbEnabledKeyword);
                }
            }
            else
            {
                cmd.DisableKeyword(_cullingShader, _hzbEnabledKeyword);
            }

            var fadeBuffer = _currentCullingContext.fadeState?.fadeStateBuffer;
            if (fadeBuffer == null)
            {
                ReleaseSplitVisibilitySlots(_currentCullingContext);
                ReleaseCameraCullingContext(_currentCullingContext);
                return;
            }

            // fade state dispatch (once per camera, not per split)
            if (_currentCullingContext.shouldDispatchFade && _uploadShader != null)
            {
                bool isPerProtoMode = _config.animatedCrossfadeMode == AnimatedCrossfadeMode.PerPrototype;
                int activeKernel = isPerProtoMode
                    ? _updateFadeStatesPerPrototypeKernel
                    : _updateFadeStatesPerInstanceKernel;

                cmd.SetComputeVectorParam(_uploadShader, s_idCameraPosition, _currentCullingContext.cameraPosition);
                cmd.SetComputeFloatParam(_uploadShader, s_idCameraHalfFOV, _currentCullingContext.cameraHalfFOV);
                cmd.SetComputeFloatParam(_uploadShader, s_idLodBias, _currentCullingContext.lodBias);
                cmd.SetComputeFloatParam(_uploadShader, s_idScreenRatioScale, _currentCullingContext.screenRatioScale);
                cmd.SetComputeFloatParam(_uploadShader, s_idCrossFadeWidth, _config.debugDisableCrossfade ? 0f : 1f);
                cmd.SetComputeFloatParam(_uploadShader, s_idDeltaTime, _currentCullingContext.fadeState.deltaTime * _crossfadeTimeScale);
                cmd.SetComputeIntParam(_uploadShader, s_idSkipIntermediateLODs, _config.skipIntermediateLODs ? 1 : 0);
                cmd.SetComputeIntParam(_uploadShader, s_idAnimatedCrossfadeMode, (int)_config.animatedCrossfadeMode);
                cmd.SetComputeIntParam(_uploadShader, s_idIsOrthographic, _currentCullingContext.isOrthographic ? 1 : 0);
                cmd.SetComputeFloatParam(_uploadShader, s_idOrthoSize, _currentCullingContext.orthoSize);

                int animatedPoolCount = _animatedCrossfadePools.Length;
                if (animatedPoolCount > 0 && _animatedPoolsBuffer != null)
                {
                    cmd.SetComputeIntParam(_uploadShader, s_idAnimatedPoolCount, animatedPoolCount);
                    cmd.SetComputeIntParam(_uploadShader, s_idSnapFadeStates, _currentCullingContext.shouldSnap ? 1 : 0);
                    cmd.SetComputeBufferParam(_uploadShader, activeKernel, s_idFadeStates, fadeBuffer);
                    cmd.SetComputeBufferParam(_uploadShader, activeKernel, s_idAnimatedPools, _animatedPoolsBuffer);
                    if (isPerProtoMode && _protoCrossfadeBuffer != null)
                        cmd.SetComputeBufferParam(_uploadShader, activeKernel, s_idProtoCrossfadeStates, _protoCrossfadeBuffer);
                    int animatedSlots = animatedPoolCount * k_poolSize;
                    int fadeGroups = (int)math.ceil(animatedSlots / (float)k_threadsInstances);
#if DEBUG
                    string fadeName = isPerProtoMode ? "FraxinusGPUBatcher.UpdateFadeStatesProto" : "FraxinusGPUBatcher.UpdateFadeStatesInst";
                    cmd.BeginSample(fadeName);
#endif
                    DispatchSafe(cmd, _uploadShader, activeKernel, fadeGroups);
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
                if (viewSet == null || viewSet.drawBucketBuffer == null || viewSet.visibilityBuffer == null)
                {
                    ReleaseVisibilitySlot(visSlot);
                    continue;
                }

                // Populate the visible chunk list when CPU culling was bypassed.
                if (!split.chunkCullJobScheduled)
                {
                    for (int a = 0; a < _allocatedCount; a++)
                        viewSet.cpuVisibleChunkIds[a] = _activeChunks[a];
                    visSlot.visibleCount[0] = _allocatedCount;
#if DEBUG
                    statOcclusionPass = _allocatedCount;
                    statOcclusionFail = 0;
#endif
                }
#if DEBUG
                else
                {
                    int visCount = visSlot.visibleCount[0];
                    statOcclusionPass = visCount;
                    statOcclusionFail = _allocatedCount - visCount;
                }
#endif

                int cpuVisCount = visSlot.visibleCount[0];
                // CullingGroup publishes native baked occlusion after Cull returns.
                // Filter camera chunks here; shadow culling keeps its own visibility.
                if (split.chunkCullJobScheduled)
                {
                    cpuVisCount = FilterBakedOcclusion(split, cpuVisCount);
#if DEBUG
                    statOcclusionPass = cpuVisCount;
                    statOcclusionFail = _allocatedCount - cpuVisCount;
#endif
                }

                if (skipCullChunks)
                {
                    if (cpuVisCount > 0 && viewSet.visibleChunksBuffer != null)
                        cmd.SetBufferData(viewSet.visibleChunksBuffer, viewSet.cpuVisibleChunkIds, 0, 0, cpuVisCount);
                }
                else
                {
                    if (cpuVisCount > 0 && _cpuVisibleChunkIdsBuffer != null)
                        cmd.SetBufferData(_cpuVisibleChunkIdsBuffer, viewSet.cpuVisibleChunkIds, 0, 0, cpuVisCount);
                }

                // store cpuVisibleChunkCount for Phase 1 re-dispatch (first split only)
                if (s == 0)
                {
                    split.cpuVisibleChunkCount = cpuVisCount;
                    _currentCullingContext.splits[0] = split;
                }

                ReleaseVisibilitySlot(visSlot);

                // reset buckets, args, counters
                DispatchResetCullingState(cmd, viewSet.drawBucketBuffer, viewSet.indirectArgsBuffer,
                    viewSet.visibleChunkCountBuffer,
                    _bucketCount, _drawCommandCount,
                    skipCullChunks ? cpuVisCount : 0);

                // no visible chunks - reset zeroed the args, skip GPU culling
                if (cpuVisCount == 0)
                    continue;

                // bind per-split output buffers
                cmd.SetComputeBufferParam(_cullingShader, _cullInstancesKernel, s_idDrawBuckets, viewSet.drawBucketBuffer);
                cmd.SetComputeBufferParam(_cullingShader, _cullInstancesKernel, s_idVisibilityBuffer, viewSet.visibilityBuffer);
                cmd.SetComputeBufferParam(_cullingShader, _cullInstancesKernel, s_idVisibleChunks, viewSet.visibleChunksBuffer);
                cmd.SetComputeBufferParam(_cullingShader, _cullInstancesKernel, s_idFadeStates, fadeBuffer);
                cmd.SetComputeBufferParam(_cullingShader, _finalizeDrawArgsKernel, s_idDrawBuckets, viewSet.drawBucketBuffer);
                cmd.SetComputeBufferParam(_cullingShader, _finalizeDrawArgsKernel, s_idIndirectArgs, viewSet.indirectArgsBuffer);

                if (!skipCullChunks)
                {
                    cmd.SetComputeBufferParam(_cullingShader, _cullChunksKernel, s_idVisibleChunks, viewSet.visibleChunksBuffer);
                    cmd.SetComputeBufferParam(_cullingShader, _cullChunksKernel, s_idVisibleChunkCount, viewSet.visibleChunkCountBuffer);
                    cmd.SetComputeBufferParam(_cullingShader, _cullChunksKernel, s_idCpuVisibleChunkIds, _cpuVisibleChunkIdsBuffer);
                    cmd.SetComputeIntParam(_cullingShader, s_idCpuVisibleChunkCount, cpuVisCount);

                    CmdDispatchCull(cmd, "FraxinusGPUBatcher.CullChunks", _cullChunksKernel,
                        (int)math.ceil(cpuVisCount / (float)k_threadsChunks));
                }

                CmdDispatchCullIndirect(cmd, "FraxinusGPUBatcher.CullInstances", _cullInstancesKernel, viewSet.visibleChunkCountBuffer);

                DispatchFinalizeDrawArgs(cmd, viewSet);

#if UNITY_EDITOR
                if (s == 0)
                    RequestCameraReadback(cmd, viewSet, cameraId);
#endif
            }

            // signal Pass 1 is pending (if HZB active) - only for first split
            bool retestPending = hzbActive && _config.useHZBRetestPass;
            if (retestPending)
                _hzbRetestContexts.Add(cameraId, cam);

#if UNITY_EDITOR
            PublishFrozenCulling(cam);
            if (!retestPending && _currentCullingContext.splits != null && _currentCullingContext.splits.Count > 0)
                DispatchDebugSphereCompaction(cmd, _currentCullingContext.splits[0].viewSet);
#endif

            // return instance to pool if no Phase1 pending
            if (!retestPending)
                ReleaseCameraCullingContext(_currentCullingContext);
        }

        /// <summary>
        /// Re-culls the first split against the current-frame HZB after the full depth prepass.
        /// Depth priming remains valid because phase zero included every candidate in that prepass.
        /// </summary>
        internal void ExecuteDeferredCullingPhase1(CommandBuffer cmd, ViewID cameraId,
            bool skipHZBTextureBinding = false)
        {
            if (!_hzbRetestContexts.Remove(cameraId, out var retainedContext)) return;
            try
            {
                if (_cullingShader == null || !_gpuCullingReady)
                    return;

                if (!_config.useHZBRetestPass) return;

                if (retainedContext.splits == null || retainedContext.splits.Count == 0) return;
                var viewSet = retainedContext.splits[0].viewSet;
                if (viewSet == null) return;

                var hzbState = retainedContext.hzbState;
                if (hzbState == null || !hzbState.hzbValid || hzbState.hzbAtlas == null)
                    return;

                var fadeBuffer = retainedContext.fadeState?.fadeStateBuffer;
                if (fadeBuffer == null)
                    return;

                // Shadow or another camera may have changed the shader's camera uniforms.
                BindCameraCullingUniforms(cmd, retainedContext);

                // Phase 0 may have processed several splits. Restore this split's outputs
                // instead of inheriting whichever split last bound the compute shader.
                cmd.SetComputeBufferParam(_cullingShader, _cullChunksKernel, s_idVisibleChunks, viewSet.visibleChunksBuffer);
                cmd.SetComputeBufferParam(_cullingShader, _cullChunksKernel, s_idVisibleChunkCount, viewSet.visibleChunkCountBuffer);
                cmd.SetComputeBufferParam(_cullingShader, _cullInstancesKernel, s_idDrawBuckets, viewSet.drawBucketBuffer);
                cmd.SetComputeBufferParam(_cullingShader, _cullInstancesKernel, s_idVisibilityBuffer, viewSet.visibilityBuffer);
                cmd.SetComputeBufferParam(_cullingShader, _cullInstancesKernel, s_idVisibleChunks, viewSet.visibleChunksBuffer);
                cmd.SetComputeBufferParam(_cullingShader, _cullInstancesKernel, s_idFadeStates, fadeBuffer);

                // reset draw bucket counts, indirect args, and counters
                DispatchResetCullingState(cmd, viewSet.drawBucketBuffer, viewSet.indirectArgsBuffer,
                    viewSet.visibleChunkCountBuffer,
                    _bucketCount, _drawCommandCount);

                // enable HZB keyword for Phase1 + bind current-frame HZB
                cmd.EnableKeyword(_cullingShader, _hzbEnabledKeyword);
                cmd.SetComputeFloatParam(_cullingShader, s_idHzbPixelExpansion, 0f); // current frame, no temporal mismatch
                cmd.SetComputeFloatParam(_cullingShader, s_idHzbTranslationScale, 0f);
                cmd.SetComputeMatrixParam(_cullingShader, s_idViewProjMatrix, retainedContext.viewProjMatrix);
                cmd.SetComputeVectorParam(_cullingShader, s_idFacingDirWorldSpace, retainedContext.facingDir);
                cmd.SetComputeVectorParam(_cullingShader, s_idRadialDirWorldSpace, retainedContext.radialDir);
                cmd.SetComputeVectorParam(_cullingShader, s_idHzbCameraPosition, retainedContext.hzbCameraPosition);

                if (!skipHZBTextureBinding)
                {
                    cmd.SetComputeTextureParam(_cullingShader, _cullChunksKernel, s_idHzbTexture, hzbState.hzbAtlas);
                    cmd.SetComputeTextureParam(_cullingShader, _cullInstancesKernel, s_idHzbTexture, hzbState.hzbAtlas);
                }
                BindHZBAtlasUniforms(cmd, _cullingShader, hzbState);

                // re-bind compacted CPU-visible chunk input (different CommandBuffer from Phase 0).
                int cpuVisCount = retainedContext.splits[0].cpuVisibleChunkCount;
                if (cpuVisCount > 0 && _cpuVisibleChunkIdsBuffer != null)
                    cmd.SetBufferData(_cpuVisibleChunkIdsBuffer, viewSet.cpuVisibleChunkIds, 0, 0, cpuVisCount);
                cmd.SetComputeBufferParam(_cullingShader, _cullChunksKernel, s_idCpuVisibleChunkIds, _cpuVisibleChunkIdsBuffer);
                cmd.SetComputeIntParam(_cullingShader, s_idCpuVisibleChunkCount, cpuVisCount);

                // re-dispatch: CullChunks -> CullInstances -> FinalizeDrawArgs
#if DEBUG
                cmd.BeginSample("FraxinusGPUBatcher.CullChunks_Phase1");
#endif
                DispatchSafe(cmd, _cullingShader, _cullChunksKernel,
                    (int)math.ceil(cpuVisCount / (float)k_threadsChunks));
#if DEBUG
                cmd.EndSample("FraxinusGPUBatcher.CullChunks_Phase1");
#endif

#if DEBUG
                cmd.BeginSample("FraxinusGPUBatcher.CullInstances_Phase1");
#endif
                cmd.DispatchCompute(_cullingShader, _cullInstancesKernel, viewSet.visibleChunkCountBuffer, 0u);
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
            cmd.SetComputeVectorArrayParam(_cullingShader, s_idFrustumPlanes, camera.frustumPlanes);
            cmd.SetComputeFloatParam(_cullingShader, s_idLodBias, camera.lodBias);
            cmd.SetComputeFloatParam(_cullingShader, s_idScreenRatioScale, camera.screenRatioScale);
            cmd.SetComputeFloatParam(_cullingShader, s_idCrossFadeWidth, _config.debugDisableCrossfade ? 0f : 1f);
            cmd.SetComputeIntParam(_cullingShader, s_idIsOrthographic, camera.isOrthographic ? 1 : 0);
            cmd.SetComputeFloatParam(_cullingShader, s_idOrthoSize, camera.orthoSize);
            cmd.SetComputeIntParam(_cullingShader, s_idAnimatedCrossfadeMode, (int)_config.animatedCrossfadeMode);
            cmd.SetComputeVectorParam(_cullingShader, s_idCameraPosition, camera.cameraPosition);
            cmd.SetComputeFloatParam(_cullingShader, s_idCameraHalfFOV, camera.cameraHalfFOV);
            cmd.SetComputeIntParam(_cullingShader, s_idBucketCount, _bucketCount);
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
            cmd.SetComputeBufferParam(_cullingShader, k, s_idDrawBuckets, bucketBuffer);
            cmd.SetComputeBufferParam(_cullingShader, k, s_idIndirectArgs, argsBuffer);
            cmd.SetComputeBufferParam(_cullingShader, k, s_idCounterBuffer, counterBuffer);
            cmd.SetComputeIntParam(_cullingShader, s_idBucketCount, bucketCount);
            cmd.SetComputeIntParam(_cullingShader, s_idDrawCommandCount, drawCommandCount);
            cmd.SetComputeIntParam(_cullingShader, s_idInitialVisibleCount, initialVisibleCount);

            int groups = (int)math.ceil(math.max(bucketCount, drawCommandCount) / (float)k_threadsReset);
            groups = math.max(groups, 1);

            CmdDispatchCull(cmd, "FraxinusGPUBatcher.ResetCullingState", k, groups);
        }

        /// <summary>
        /// Dispatch FinalizeDrawArgs kernel. Used by both Pass 0 and Pass 1.
        /// </summary>
        private void DispatchFinalizeDrawArgs(CommandBuffer cmd, CullingBufferSet viewSet)
        {
            cmd.SetComputeBufferParam(_cullingShader, _finalizeDrawArgsKernel, s_idDrawBuckets, viewSet.drawBucketBuffer);
            cmd.SetComputeBufferParam(_cullingShader, _finalizeDrawArgsKernel, s_idIndirectArgs, viewSet.indirectArgsBuffer);

            int totalCameraCmds = _drawCommandCount;
            cmd.SetComputeIntParam(_cullingShader, s_idDrawCommandCount, totalCameraCmds);
#if ENABLE_VR_MODULE
            int instanceMultiplierShift = (UnityEngine.XR.XRSettings.enabled && !SystemInfo.supportsMultiview) ? 1 : 0;
            cmd.SetComputeIntParam(_cullingShader, s_idInstanceMultiplierShift, instanceMultiplierShift);
#endif
#if DEBUG
            cmd.BeginSample("FraxinusGPUBatcher.FinalizeDrawArgs");
#endif
            DispatchSafe(cmd, _cullingShader, _finalizeDrawArgsKernel,
                (int)math.ceil(totalCameraCmds / (float)k_threadsFinalize));
#if DEBUG
            cmd.EndSample("FraxinusGPUBatcher.FinalizeDrawArgs");
#endif
        }

        // returns true only if shadow culling actually ran (filled the indirect args); false on any skip
        private bool RunShadowCulling(BatchCullingContext cullingContext, CullingBufferSet shadowSet)
        {
#if DEBUG
            using var _sc = s_markerShadowCulling.Auto();
#endif
            if (_cullingShader == null || !_gpuCullingReady)
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
                for (int c = 1; c < k_maxCascades; c++)
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
                for (int c = cascadeCount; c < k_maxCascades; c++)
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
                    for (int i = 0; i < receiverPlaneCount && lfPlaneCount < k_maxLightFacingPlanes; i++)
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
                        else if (receiverCullPlaneCount < k_maxReceiverCullPlanes)
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
                                if (len > 1e-6f && receiverCullPlaneCount < k_maxReceiverCullPlanes)
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
                        else if (receiverCullPlaneCount < k_maxReceiverCullPlanes)
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
                                if (len > 1e-6f && receiverCullPlaneCount < k_maxReceiverCullPlanes)
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
                shadowSet.drawBucketBuffer != null && shadowSet.visibilityBuffer != null)
            {
                GraphicsBuffer shadowFade = _currentCullingContext?.fadeState?.fadeStateBuffer;
                if (shadowFade == null || !shadowFade.IsValid()) return false;

                if (_shadowActiveCount == 0 && !_shadowChunksRebuiltThisFrame) return false;

                // batch shadow culling dispatches into one CommandBuffer (DX12 UAV barriers)
                _cullingCmd.Clear();

                // dispatch pending fade state update before shadow culling - shadows run synchronously
                // but the deferred camera path hasn't executed yet, so _FadeStates are stale
                if (_currentCullingContext.shouldDispatchFade && _uploadShader != null)
                {
                    bool isPerProtoMode = _config.animatedCrossfadeMode == AnimatedCrossfadeMode.PerPrototype;
                    int activeKernel = isPerProtoMode
                        ? _updateFadeStatesPerPrototypeKernel
                        : _updateFadeStatesPerInstanceKernel;

                    if (activeKernel >= 0)
                    {
                        _cullingCmd.SetComputeVectorParam(_uploadShader, s_idCameraPosition, _currentCullingContext.cameraPosition);
                        _cullingCmd.SetComputeFloatParam(_uploadShader, s_idCameraHalfFOV, _currentCullingContext.cameraHalfFOV);
                        _cullingCmd.SetComputeFloatParam(_uploadShader, s_idScreenRatioScale, _currentCullingContext.screenRatioScale);
                        _cullingCmd.SetComputeFloatParam(_uploadShader, s_idLodBias, _currentCullingContext.lodBias);
                        _cullingCmd.SetComputeFloatParam(_uploadShader, s_idDeltaTime, _currentCullingContext.fadeState.deltaTime * _crossfadeTimeScale);
                        _cullingCmd.SetComputeIntParam(_uploadShader, s_idSkipIntermediateLODs, _config.skipIntermediateLODs ? 1 : 0);
                        _cullingCmd.SetComputeIntParam(_uploadShader, s_idAnimatedCrossfadeMode, (int)_config.animatedCrossfadeMode);
                        _cullingCmd.SetComputeIntParam(_uploadShader, s_idIsOrthographic, _currentCullingContext.isOrthographic ? 1 : 0);
                        _cullingCmd.SetComputeFloatParam(_uploadShader, s_idOrthoSize, _currentCullingContext.orthoSize);

                        int animatedPoolCount = _animatedCrossfadePools.Length;
                        if (animatedPoolCount > 0 && _animatedPoolsBuffer != null)
                        {
                            _cullingCmd.SetComputeIntParam(_uploadShader, s_idAnimatedPoolCount, animatedPoolCount);
                            _cullingCmd.SetComputeIntParam(_uploadShader, s_idSnapFadeStates, _currentCullingContext.shouldSnap ? 1 : 0);
                            _cullingCmd.SetComputeBufferParam(_uploadShader, activeKernel, s_idFadeStates, _currentCullingContext.fadeState.fadeStateBuffer);
                            _cullingCmd.SetComputeBufferParam(_uploadShader, activeKernel, s_idAnimatedPools, _animatedPoolsBuffer);
                            if (isPerProtoMode && _protoCrossfadeBuffer != null)
                                _cullingCmd.SetComputeBufferParam(_uploadShader, activeKernel, s_idProtoCrossfadeStates, _protoCrossfadeBuffer);
                            int animatedSlots = animatedPoolCount * k_poolSize;
                            int fadeGroups = (int)math.ceil(animatedSlots / (float)k_threadsInstances);
                            CmdDispatchUpload(_cullingCmd, isPerProtoMode ? "FraxinusGPUBatcher.UpdateFadeStatesProto" : "FraxinusGPUBatcher.UpdateFadeStatesInst",
                                activeKernel, fadeGroups);
                        }

                        _currentCullingContext.shouldDispatchFade = false;
                    }
                }

                // common shadow uniforms
                _cullingCmd.SetComputeIntParam(_cullingShader, s_idCascadeCount, cascadeCount);
                _cullingCmd.SetComputeIntParam(_cullingShader, s_idApplyCascadeLimits, isOrthoLight ? 1 : 0);

                // shadow caster occlusion vs the owning camera's prev-frame HZB (directional only)
                var shadowHzbState = (_config.shadowCasterOcclusion && isOrthoLight && isGPUOcclusionEnabled &&
                    _currentCullingContext != null && _currentCullingContext.useHZB)
                    ? _currentCullingContext.hzbState : null;
                bool shadowHZBTestable = shadowHzbState != null && shadowHzbState.hzbValid &&
                    shadowHzbState.prevDataValid && shadowHzbState.hzbAtlas != null;
#if UNITY_EDITOR
                if (_config.debugVisualizeCullingInSceneView)
                    shadowHZBTestable = false;
#endif
                if (shadowHZBTestable)
                {
                    _cullingCmd.EnableKeyword(_cullingShader, _hzbEnabledKeyword);

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

                    _cullingCmd.SetComputeFloatParam(_cullingShader, s_idHzbPixelExpansion, _config.hzbPixelExpansion + cameraMotionPixels);
                    _cullingCmd.SetComputeFloatParam(_cullingShader, s_idHzbTranslationScale, hzbTranslationScale);
                    _cullingCmd.SetComputeMatrixParam(_cullingShader, s_idViewProjMatrix, shadowHzbState.prevViewProjMatrix);
                    _cullingCmd.SetComputeVectorParam(_cullingShader, s_idFacingDirWorldSpace, shadowHzbState.prevFacingDir);
                    _cullingCmd.SetComputeVectorParam(_cullingShader, s_idRadialDirWorldSpace, shadowHzbState.prevRadialDir);
                    _cullingCmd.SetComputeVectorParam(_cullingShader, s_idHzbCameraPosition, shadowHzbState.prevCameraPosition);
                    _cullingCmd.SetComputeTextureParam(_cullingShader, _cullChunksShadowKernel, s_idHzbTexture, shadowHzbState.hzbAtlas);
                    _cullingCmd.SetComputeTextureParam(_cullingShader, _cullInstancesShadowKernel, s_idHzbTexture, shadowHzbState.hzbAtlas);
                    BindHZBAtlasUniforms(_cullingCmd, _cullingShader, shadowHzbState);
                }
                else
                {
                    _cullingCmd.DisableKeyword(_cullingShader, _hzbEnabledKeyword);
                }
                _cullingCmd.SetComputeIntParams(_cullingShader, s_idCascadePlaneCounts, _cascadePlaneCounts);
                _cullingCmd.SetComputeVectorArrayParam(_cullingShader, s_idCascadePlanes, _cascadePlanes);
                _cullingCmd.SetComputeVectorArrayParam(_cullingShader, s_idCascadeSpheres, _cascadeSpheres);

                // light-space cascade culling uniforms (ReceiverSphereCuller port)
                // Unity Matrix4x4 column-major maps 1:1 to HLSL
                Matrix4x4 wlrMat = Matrix4x4.identity;
                wlrMat.SetColumn(0, new Vector4(worldToLightRotation.c0.x, worldToLightRotation.c0.y, worldToLightRotation.c0.z, 0));
                wlrMat.SetColumn(1, new Vector4(worldToLightRotation.c1.x, worldToLightRotation.c1.y, worldToLightRotation.c1.z, 0));
                wlrMat.SetColumn(2, new Vector4(worldToLightRotation.c2.x, worldToLightRotation.c2.y, worldToLightRotation.c2.z, 0));
                _cullingCmd.SetComputeMatrixParam(_cullingShader, s_idWorldToLightRotation, wlrMat);
                _cascadeBlendFactorUpload[0] = new Vector4(
                    _cascadeBlendFactorValues[0], _cascadeBlendFactorValues[1],
                    _cascadeBlendFactorValues[2], _cascadeBlendFactorValues[3]);
                _cascadeBlendFactorUpload[1] = new Vector4(
                    _cascadeBlendFactorValues[4], _cascadeBlendFactorValues[5], 0f, 0f);
                _cullingCmd.SetComputeVectorArrayParam(_cullingShader, s_idCascadeBlendFactors, _cascadeBlendFactorUpload);
                _cullingCmd.SetComputeVectorArrayParam(_cullingShader, s_idLightFacingPlanes, _lightFacingPlanes);
                _cullingCmd.SetComputeIntParam(_cullingShader, s_idLightFacingPlaneCount, lfPlaneCount);

                // perspective receiver cull planes (spot/point shadow tightening)
                _cullingCmd.SetComputeVectorArrayParam(_cullingShader, s_idReceiverCullPlanes, _receiverCullPlanes);
                _cullingCmd.SetComputeIntParam(_cullingShader, s_idReceiverCullPlaneCount, receiverCullPlaneCount);

                float shadowLodBiasValue = QualitySettings.lodBias * shadowLodBias;
                float shadowScreenRatioScale;
                if (shadowIsOrthographic)
                    shadowScreenRatioScale = shadowOrthoSize > 0f ? shadowLodBiasValue / (2f * shadowOrthoSize) : 0f;
                else
                    shadowScreenRatioScale = shadowCameraHalfFOV > 0f ? shadowLodBiasValue / (2f * shadowCameraHalfFOV) : 0f;

                _cullingCmd.SetComputeVectorParam(_cullingShader, s_idCameraPosition, shadowCameraPosition);
                _cullingCmd.SetComputeFloatParam(_cullingShader, s_idCameraHalfFOV, shadowCameraHalfFOV);
                _cullingCmd.SetComputeFloatParam(_cullingShader, s_idLodBias, shadowLodBiasValue);
                _cullingCmd.SetComputeFloatParam(_cullingShader, s_idScreenRatioScale, shadowScreenRatioScale);
                _cullingCmd.SetComputeFloatParam(_cullingShader, s_idCrossFadeWidth, _config.debugDisableCrossfade ? 0f : 1f);
                _cullingCmd.SetComputeIntParam(_cullingShader, s_idIsOrthographic, shadowIsOrthographic ? 1 : 0);
                _cullingCmd.SetComputeFloatParam(_cullingShader, s_idOrthoSize, shadowOrthoSize);
                _cullingCmd.SetComputeIntParam(_cullingShader, s_idAnimatedCrossfadeMode, (int)_config.animatedCrossfadeMode);

                // per-view shadow buffer binds
                _cullingCmd.SetComputeBufferParam(_cullingShader, _cullChunksShadowKernel, s_idVisibleShadowChunks, shadowSet.visibleChunksBuffer);
                _cullingCmd.SetComputeBufferParam(_cullingShader, _cullChunksShadowKernel, s_idVisibleShadowChunkCount, shadowSet.visibleChunkCountBuffer);
                _cullingCmd.SetComputeBufferParam(_cullingShader, _cullInstancesShadowKernel, s_idShadowDrawBuckets, shadowSet.drawBucketBuffer);
                _cullingCmd.SetComputeBufferParam(_cullingShader, _cullInstancesShadowKernel, s_idShadowVisibilityBuffer, shadowSet.visibilityBuffer);
                _cullingCmd.SetComputeBufferParam(_cullingShader, _cullInstancesShadowKernel, s_idVisibleShadowChunks, shadowSet.visibleChunksBuffer);
                _cullingCmd.SetComputeBufferParam(_cullingShader, _cullInstancesShadowKernel, s_idFadeStates, shadowFade);
                _cullingCmd.SetComputeBufferParam(_cullingShader, _finalizeShadowDrawArgsKernel, s_idShadowDrawBuckets, shadowSet.drawBucketBuffer);
                _cullingCmd.SetComputeBufferParam(_cullingShader, _finalizeShadowDrawArgsKernel, s_idShadowIndirectArgs, shadowSet.indirectArgsBuffer);

                // reset shadow buckets, args, counters in one dispatch
                DispatchResetCullingState(_cullingCmd, shadowSet.drawBucketBuffer, shadowSet.indirectArgsBuffer,
                    shadowSet.visibleChunkCountBuffer,
                    _shadowBucketCount, _shadowDrawCommandCount);

                _cullingCmd.SetComputeIntParam(_cullingShader, s_idShadowChunkCount, _shadowActiveCount);
                _cullingCmd.SetComputeIntParam(_cullingShader, s_idCullingLayerMask, (int)(uint)cullingContext.cullingLayerMask);
                _cullingCmd.SetComputeBufferParam(_cullingShader, _cullChunksShadowKernel, s_idShadowChunkIds, _shadowChunkIdsBuffer);

                CmdDispatchCull(_cullingCmd, "FraxinusGPUBatcher.CullChunksShadow", _cullChunksShadowKernel, (int)math.ceil(_shadowActiveCount / (float)k_threadsChunks));
                CmdDispatchCullIndirect(_cullingCmd, "FraxinusGPUBatcher.CullInstancesShadow", _cullInstancesShadowKernel, shadowSet.visibleChunkCountBuffer);

                // finalize shadow draw args
                int totalShadowCmds = _shadowDrawCommandCount;
                _cullingCmd.SetComputeIntParam(_cullingShader, s_idShadowDrawCommandCount, totalShadowCmds);
                CmdDispatchCull(_cullingCmd, "FraxinusGPUBatcher.FinalizeShadowDrawArgs", _finalizeShadowDrawArgsKernel, (int)math.ceil(totalShadowCmds / (float)k_threadsFinalize));

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

            if (cullingContext.viewType == BatchCullingViewType.Camera &&
                GPUBatcherSpeedTreeWindBridge.IsCaptureCamera(GPUBatcherHelpers.GetViewID(cullingContext.viewID)))
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
            ViewID viewID = GPUBatcherHelpers.GetViewID(cullingContext.viewID);
            bool hasCamData = _cameraRenderData.TryGetValue(viewID, out var camData);
#if UNITY_EDITOR
            // Even an empty or masked view must not leave the preceding camera's
            // role/context active for the next shadow cull.
            _currentCullingContext = null;
            _activeCullingCameraId = viewID;
            _activeCullingCameraData = camData;
            _replayingFrozenCulling = false;
            bool isReferenceCamera = camData.referenceCameraId != default && camData.referenceCameraId == viewID;
            if (!_config.debugVisualizeCullingInSceneView || camData.referenceCameraId == default ||
                camData.referenceCameraId != _frozenReferenceCameraId ||
                (_sceneViewCameraBufferSet != null && _sceneViewCameraBufferSet.generation != _bufferPoolGeneration))
                InvalidateFrozenCulling();
            if (_config.debugVisualizeCullingInSceneView && isReferenceCamera)
            {
                _frozenReferenceCameraId = camData.referenceCameraId;
                _frozenCullingValid = false;
                _sceneViewShadowCount = 0;
            }
#endif
            if (!_chunkSystemInitialized || _allocatedCount == 0 ||
                !_cameraDrawCommandTemplate.IsCreated || !_cameraIndirectArgsTemplate.IsCreated ||
                _drawCommandCount == 0 || cullingContext.cullingLayerMask == 0)
                return default;

            bool isDeferred = _deferredCullingCameras.Contains(viewID);

            // look up per-camera SRP data (populated in OnBeginCameraRendering)
            // non-SRP cameras (baked occlusion viz, etc.) won't have an entry - use defaults
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
            if (camData.isSceneView && !isReferenceCamera && CanReplayFrozenCulling(camData.referenceCameraId))
            {
                _deferredCullingCameras.Remove(viewID);
                _deferredUseHZB.Remove(viewID);
                _sceneViewShadowReplayIndex = 0;
                _replayingFrozenCulling = true;
                if (_config.debugDisableRendering) return default;
                EmitCameraDrawCommands(cullingOutput, _sceneViewCameraBufferSet);
                return default;
            }
#endif

            // acquire a per-view buffer set
            CullingBufferSet viewSet;
            JobHandle dependency = default;

#if UNITY_EDITOR
            if (_config.debugVisualizeCullingInSceneView && isReferenceCamera)
            {
                EnsureSceneViewCameraBufferSet();
                viewSet = _sceneViewCameraBufferSet;
            }
            else
#endif
            {
                viewSet = AcquireCameraBufferSet();
            }

            // compute dispatch
#if DEBUG
            // debugDisableCulling: skip GPU dispatches, reuse stale data from prior frame
            if (!_config.debugDisableCulling)
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
            if (_config.debugDisableRendering)
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
            if (pickingEntries.Count == 0 || _batchSlots == null || _protoBatchSlots == null)
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

            int maxDraws = pickingEntries.Count * maxLod0Submeshes;
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
            foreach (var kvp in pickingEntries)
            {
                int globalSlot = kvp.Key;
                var entry = kvp.Value;

                // -1=empty, <=-2=disabled, both invisible to GPU
                if (!hasSlotProto || globalSlot < 0 || globalSlot >= _slotProtoIndex.Length) continue;
                int protoIdx = _slotProtoIndex[globalSlot];
                if (protoIdx < 0) continue;
                if (_prototypeVacant != null && protoIdx < _prototypeVacant.Length && _prototypeVacant[protoIdx])
                    continue;

                int poolIdx = globalSlot / k_poolSize;
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
#if FRAXINUS_BRG_INDIRECT_API
            drawCommands->indirectDrawCommandCount = 0;
#endif
            drawCommands->visibleInstanceCount = emitted;

            drawCommands->drawRangeCount = 1;
            drawCommands->drawRanges[0] = new BatchDrawRange
            {
#if FRAXINUS_BRG_INDIRECT_API
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
#if FRAXINUS_BRG_INDIRECT_API
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
            if (!BRGIndirectSubmission.isAvailable) return;
            int totalCmdCount = _drawCommandCount;
            bool hasRanges = _cameraDrawRangeTemplate.IsCreated && _cameraDrawRangeCount > 0;
            int totalRangeCount = hasRanges ? _cameraDrawRangeCount : 1;
            EnsureIndirectSubmissionCapacity(viewSet, totalCmdCount, totalRangeCount);
            var commands = (BatchDrawCommandIndirect*)viewSet.cpuDrawCommands.GetUnsafePtr();
            var ranges = (BatchDrawRange*)viewSet.cpuDrawRanges.GetUnsafePtr();

            // Copy templates and patch per-view buffer handles before crossing the submission boundary.
            UnsafeUtility.MemCpy(commands, _cameraDrawCommandTemplate.GetUnsafeReadOnlyPtr(),
                totalCmdCount * UnsafeUtility.SizeOf<BatchDrawCommandIndirect>());
            var visHandle = viewSet.visibilityBuffer.bufferHandle;
            var argsHandle = viewSet.indirectArgsBuffer.bufferHandle;
            for (int i = 0; i < totalCmdCount; i++)
            {
                commands[i].visibleInstancesBufferHandle = visHandle;
                commands[i].indirectArgsBufferHandle = argsHandle;
            }

            if (hasRanges)
            {
                UnsafeUtility.MemCpy(ranges, _cameraDrawRangeTemplate.GetUnsafeReadOnlyPtr(),
                    _cameraDrawRangeCount * UnsafeUtility.SizeOf<BatchDrawRange>());
            }
            else
            {
                ranges[0] = DefaultDrawRange(totalCmdCount, shadow: false);
            }

            BRGIndirectSubmission.TrySubmit(in cullingOutput, new(commands, totalCmdCount), new(ranges, totalRangeCount));
        }

        private static BatchDrawRange DefaultDrawRange(int commandCount, bool shadow) => new()
        {
            drawCommandsCount = (uint)commandCount,
            filterSettings = new BatchFilterSettings
            {
                renderingLayerMask = uint.MaxValue,
                motionMode = MotionVectorGenerationMode.Camera,
                shadowCastingMode = shadow ? ShadowCastingMode.On : ShadowCastingMode.Off,
                receiveShadows = !shadow
            }
        };

        private unsafe JobHandle OnPerformCullingShadow(
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput)
        {
            if (!_chunkSystemInitialized || _allocatedCount == 0 ||
                !_shadowDrawCommandTemplate.IsCreated || !_shadowIndirectArgsTemplate.IsCreated ||
                _shadowDrawCommandCount == 0 || cullingContext.cullingLayerMask == 0)
                return default;

#if UNITY_EDITOR
            // frozen scene view: emit shadow draw commands from persistent scene view sets
            if (_replayingFrozenCulling)
            {
                if (_activeCullingCameraData.isSceneView &&
                    CanReplayFrozenCulling(_activeCullingCameraData.referenceCameraId) &&
                    _sceneViewShadowReplayIndex < _sceneViewShadowCount)
                {
                    var frozenShadowSet = _sceneViewShadowBufferSets[_sceneViewShadowReplayIndex++];
                    if (IsCurrentCullingBuffer(frozenShadowSet) && !_config.debugDisableRendering)
                        EmitShadowDrawCommands(cullingContext, cullingOutput, frozenShadowSet);
                }
                // No camera context was created for a replay view. Never borrow
                // a preceding camera's fade/HZB state for uncaptured shadow views.
                return default;
            }
#endif
            if (_currentCullingContext == null) return default;

            // acquire a per-view shadow buffer set
            CullingBufferSet shadowSet;
#if UNITY_EDITOR
            if (_config.debugVisualizeCullingInSceneView && _activeCullingCameraData.referenceCameraId != default &&
                _activeCullingCameraId == _activeCullingCameraData.referenceCameraId &&
                _currentCullingContext.cameraId == _activeCullingCameraId)
                shadowSet = EnsureSceneViewShadowBufferSet(_sceneViewShadowCount++);
            else
#endif
                shadowSet = AcquireShadowBufferSet();

#if DEBUG
            // debugDisableCulling: skip GPU dispatches, reuse stale data from prior frame
            if (!_config.debugDisableCulling)
#endif
            {
                // on skip, zero the pooled args so the unconditional emit doesn't draw a prior render's shadow counts
                if (!RunShadowCulling(cullingContext, shadowSet)
                    && shadowSet.indirectArgsBuffer != null
                    && _shadowIndirectArgsTemplate.IsCreated && _shadowIndirectArgsTemplate.Length > 0)
                    shadowSet.indirectArgsBuffer.SetData(_shadowIndirectArgsTemplate, 0, 0,
                        math.min(_shadowIndirectArgsTemplate.Length, shadowSet.indirectArgsBuffer.count));
            }

#if DEBUG
            // debugDisableRendering: culling ran but skip draw command emission
            if (_config.debugDisableRendering)
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
            if (!BRGIndirectSubmission.isAvailable) return;
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

            bool hasRanges = _shadowDrawRangeTemplate.IsCreated && _shadowDrawRangeCount > 0;
            int maxRanges = hasRanges ? _shadowDrawRangeCount : 1;
            EnsureIndirectSubmissionCapacity(shadowSet, totalCmdCount, maxRanges);
            var ranges = (BatchDrawRange*)shadowSet.cpuDrawRanges.GetUnsafePtr();
            int emittedRangeCount = 0;
            int cmdIdx = 0;

            // combined camera+light layer mask
            ulong cullingLayerMask = cullingContext.cullingLayerMask;

            // per-view buffer handles
            var visHandle = shadowSet.visibilityBuffer.bufferHandle;
            var argsHandle = shadowSet.indirectArgsBuffer.bufferHandle;
            var templatePtr = (BatchDrawCommandIndirect*)_shadowDrawCommandTemplate.GetUnsafeReadOnlyPtr();
            var filterPtr = (ShadowCommandFilterInfo*)_shadowCommandFilterInfo.GetUnsafeReadOnlyPtr();
            var outPtr = (BatchDrawCommandIndirect*)shadowSet.cpuDrawCommands.GetUnsafePtr();

            for (int r = 0; r < maxRanges; r++)
            {
                // Without layer-sorted templates, process all commands as one unfiltered range.
                var range = hasRanges ? _shadowDrawRangeTemplate[r] : DefaultDrawRange(totalCmdCount, shadow: true);
                if (hasRanges && (cullingLayerMask & (1UL << range.filterSettings.layer)) == 0)
                    continue;

                int rangeBegin = cmdIdx;
                int srcBegin = (int)range.drawCommandsBegin;
                int srcEnd = srcBegin + (int)range.drawCommandsCount;
                for (int i = srcBegin; i < srcEnd; i++)
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

                if (cmdIdx == rangeBegin) continue;
                range.drawCommandsBegin = (uint)rangeBegin;
                range.drawCommandsCount = (uint)(cmdIdx - rangeBegin);
                ranges[emittedRangeCount++] = range;
            }

            // BRG receives one empty range when every command was filtered out.
            if (emittedRangeCount == 0)
            {
                ranges[0] = DefaultDrawRange(0, shadow: true);
                emittedRangeCount = 1;
            }
            BRGIndirectSubmission.TrySubmit(in cullingOutput, new(outPtr, cmdIdx), new(ranges, emittedRangeCount));
        }

        #endregion

    }
}
