// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
#if DEBUG
using Unity.Profiling;
#endif

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Chunk system: allocation, writing, freeing, pool/buffer resize,
    /// fade state management, and GPU buffer copy/init helpers.
    /// </summary>
    public partial class GPUBatcherRenderer
    {
        #region Types & Fields

        private bool _chunkSystemInitialized;

        // pool configuration
        private int _poolCapacity;           // current pool buffer capacity
        private int _initialPoolCapacity;    // initial capacity
        private int _initialChunkCapacity;   // initial chunk capacity
        private int _maxPoolRefsPerChunk;    // max pools a single chunk can own

        // pool allocation
        // todo: switch to NativeQueue so freed pools cycle to the back - reduces same-frame reuse hits in InvalidateStagedForPool
        private NativeList<int> _freePools;
#if UNITY_EDITOR
        private NativeList<int> _deferredPoolFrees;
#endif
        private int _allocatedPoolCount;
        private NativeArray<bool> _poolInUse;
        private NativeArray<uint> _poolStagedFrame; // render cycle when last staged - for invalidation on reuse
        private NativeArray<uint> _poolRmwStagedFrame; // patches/colors/sig overrides only - gates the remove-time flush
        private NativeArray<int> _poolOccupancy;    // per-pool live instance count
        private NativeArray<int> _poolToChunk;  //pool index -> owning chunk ID. used for RemoveInstance()

        // chunk slot management
        private int _maxChunks;
        private NativeQueue<int> _freeChunkSlots;
        private NativeArray<bool> _chunkSlotActive;
        private NativeArray<bool> _chunkEnabled;     // user-togglable per-chunk enable
        private NativeArray<int> _chunkGeneration;   // ABA prevention: incremented on free
        private int _allocatedCount;         // current active chunk count
        private int _chunkHighWaterMark;     // max(chunkId)+1, bounds GPU dispatch

        // compacted active chunk list
        private NativeArray<int> _activeChunks;         // [0.._allocatedCount) = active chunk IDs
        private NativeArray<int> _chunkIdToActiveIndex;  // chunkId -> index in _activeChunks

        // per-chunk data
        private NativeArray<ChunkData> _chunks;
        private GraphicsBuffer _chunkBuffer;
        // pool list per chunk; freed slots become -1 sentinels for InstanceLink stability
        private NativeArray<int> _chunkPools;
        private GraphicsBuffer _poolRefBuffer;

        // gpu instance data
        private GraphicsBuffer _instanceBuffer;          // InstanceInput (20B) - position/cull data + sigDensityPacked
        private GraphicsBuffer _instanceRotScaleBuffer;  // InstanceRotationScale (28B) - rotation + scale
        private GraphicsBuffer _matrixBuffer;            // float3x4 matrices for rendering

        // test accessors
        public GraphicsBuffer instanceBuffer => _instanceBuffer;
        public GraphicsBuffer instanceRotScaleBuffer => _instanceRotScaleBuffer;
        public GraphicsBuffer poolRefBuffer => _poolRefBuffer;
        public GraphicsBuffer chunkBuffer => _chunkBuffer;
        public int maxPoolRefsPerChunk => _maxPoolRefsPerChunk;
        public int colorRegionByteOffset => _colorRegionByteOffset;
        public bool perInstanceColorEnabled => _perInstanceColorEnabled;

        // reusable buffers for WriteChunk
        private NativeArray<GPUStagedInstanceData> _writeChunkGpuBuffer;

        // shared across all counter resets
        private static readonly uint[] s_counterResetData = new uint[] { 0, 1, 1, 0 };

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct GPUChunk
        {
            public float2 boundsMinXZ;
            public float2 boundsMaxXZ;
            public float boundsMinY;
            public float boundsMaxY;
            public uint poolRefStart;
            public uint poolCount;
            public float cullDistanceSq;
            public float shadowDistanceSq;
            public uint maxShadowCascade;
            public uint layerMask; // bit N = layer N has instances in this chunk
            public float instanceCullDistanceSq;
        }

        private struct ChunkData
        {
            public int instanceCount;
            public int poolCount;        // length of stable pool list (includes -1 holes)
            public Bounds bounds;
            public float cullDistanceSq;
            public float shadowDistanceSq;
            public uint maxShadowCascade;
            public float instanceCullDistanceSq;
            public bool isWritten;
        }

        // per-prototype instance counts
        private NativeArray<int> _protoInstanceCounts;
        private NativeArray<int> _protoInstanceCountHWM;  //max instances ever seen per proto

        // per-prototype flipped instance counts
        private NativeArray<int> _protoFlipCounts;
        private NativeArray<int> _protoFlipCountHWM;
        private NativeArray<int> _chunkProtoBreakdown; // per-chunk per-prototype instance counts
        private int _protoBreakdownStride; // row width of _chunkProtoBreakdown
        private NativeArray<bool> _chunkHasAnimatedCrossfade; // per-chunk flag for animated crossfade
        private NativeArray<uint> _chunkLayerMask; // per-chunk: bit N = layer N has instances in this chunk

        //used to find which slots in pools are empty
        //bit set = slot occupied. free slot = tzcnt(~mask)
        //[pool0 mask0][pool0 mask1]...[pool1 mask0]...
        private NativeArray<ulong> _poolSlotMask;

        // index into chunk's pool list to start scanning for free space
        private NativeArray<int> _chunkFirstFreePoolHint;

        //global instance slot index -> prototype index
        private NativeArray<int> _slotProtoIndex;
        private NativeArray<bool> _slotFlipped;

        // 44B staged instance data - matches GPU StagedInstance struct
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct GPUStagedInstanceData
        {
            public float3 position;
            public float4 rotation;
            public float3 scale;
            public uint sigDensityPacked; // [0:15] signatureIndex (0xFFFF = invalid), [16:31] densityHash as uint16

            public static uint Pack(int signatureIndex, float densityHash)
            {
                uint sigBits = signatureIndex < 0 ? 0xFFFFu : (uint)signatureIndex & 0xFFFFu;
                uint denBits = (uint)(densityHash * 65535f + 0.5f) & 0xFFFFu;
                return sigBits | (denBits << 16);
            }
        }

        // 20B live instance data - matches GPU InstanceInput struct
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct GPUInstanceData
        {
            public float3 positionOrBoundsCenter;
            public float padOrMaxScale;
            public uint sigDensityPacked;
        }

        // 28B rotation + scale - matches GPU InstanceRotationScale struct
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct GPUInstanceRotScaleData
        {
            public float4 rotation;
            public float3 scale;
        }

        internal static readonly int gpuInstanceDataStride = UnsafeUtility.SizeOf<GPUInstanceData>();
        internal static readonly int gpuStagedInstanceDataStride = UnsafeUtility.SizeOf<GPUStagedInstanceData>();
        internal static readonly int gpuInstanceRotScaleStride = UnsafeUtility.SizeOf<GPUInstanceRotScaleData>();

        [BurstCompile(OptimizeFor = OptimizeFor.Performance)]
        private struct PackInstanceDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InstanceData> instances;
            public int srcOffset;  // offset into instances array
            [ReadOnly] public bool forcePositiveScale;

            [NativeDisableContainerSafetyRestriction] [NativeDisableParallelForRestriction] [WriteOnly]
            public NativeArray<GPUStagedInstanceData> gpuBuffer;

            public void Execute(int i)
            {
                var inst = instances[srcOffset + i];
                var s = inst.scale;
                if (forcePositiveScale && s.x * s.y * s.z < 0f) s.x = -s.x;

                gpuBuffer[i] = new GPUStagedInstanceData
                {
                    position = inst.position,
                    rotation = inst.rotation.value,
                    scale = s,
                    sigDensityPacked = GPUStagedInstanceData.Pack(inst.signatureIndex, GPUBatcherBurstHelpers.StableDensityHash(inst.position))
                };
            }
        }

        #endregion

        #region Public Properties

        public int maxInstancesPerChunk
        {
            get
            {
                if (_chunkSystemInitialized)
                    return _maxPoolRefsPerChunk * k_poolSize;
                return _config.maxPoolRefsPerChunk * k_poolSize;
            }
        }

        public bool chunkSystemInitialized => _chunkSystemInitialized;

        public int poolCapacity => _poolCapacity;

        public int poolSize => k_poolSize;

        public int GetChunkPoolCount(int chunkId)
        {
            if (!_chunkSystemInitialized || chunkId < 0 || chunkId >= _chunks.Length) return 0;
            return _chunks[chunkId].poolCount;
        }

        // returns -1 for hole positions (pool was freed but position retained for link stability)
        public int GetChunkPoolIndex(int chunkId, int localPoolIdx)
        {
            if (!_chunkSystemInitialized || chunkId < 0 || chunkId >= _chunks.Length) return -1;
            if (localPoolIdx < 0 || localPoolIdx >= _chunks[chunkId].poolCount) return -1;
            return _chunkPools[chunkId * _maxPoolRefsPerChunk + localPoolIdx];
        }

        public int GetPoolOccupancy(int globalPoolIndex)
        {
            if (!_chunkSystemInitialized || globalPoolIndex < 0 || globalPoolIndex >= _poolOccupancy.Length) return 0;
            return _poolOccupancy[globalPoolIndex];
        }

        #endregion

        #region Initialization

        private void InitializeChunkSystem()
        {
            if (_chunkSystemInitialized) return;

            int maxChunks = math.max(16, _config.initialChunkCount);
            int ps = k_poolSize;

            _maxChunks = maxChunks;
            _initialChunkCapacity = maxChunks;
            _maxPoolRefsPerChunk = _config.maxPoolRefsPerChunk;

            int initPoolCount = math.max(16, _config.initialPoolCapacity);
            _poolCapacity = initPoolCount;
            _initialPoolCapacity = initPoolCount;

            _freeChunkSlots = new NativeQueue<int>(Allocator.Persistent);
            _chunkSlotActive = new NativeArray<bool>(maxChunks, Allocator.Persistent);
            _chunkEnabled = new NativeArray<bool>(maxChunks, Allocator.Persistent);
            _chunkGeneration = new NativeArray<int>(maxChunks, Allocator.Persistent);
            _activeChunks = new NativeArray<int>(maxChunks, Allocator.Persistent);
            _visibilitySlotCapacity = maxChunks;
            _shadowActiveChunks = new int[maxChunks];
            _shadowActiveCount = 0;
            _shadowChunksDirty = true;
            _shadowChunkIdsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxChunks, sizeof(int));
            _cpuVisibleChunkIdsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxChunks, sizeof(int));
            _chunkIdToActiveIndex = new NativeArray<int>(maxChunks, Allocator.Persistent);
            unsafe { UnsafeUtility.MemSet(_chunkIdToActiveIndex.GetUnsafePtr(), 0xFF, maxChunks * sizeof(int)); } // -1 = inactive
            _chunks = new NativeArray<ChunkData>(maxChunks, Allocator.Persistent);
            _chunkPools = new NativeArray<int>(maxChunks * _maxPoolRefsPerChunk, Allocator.Persistent);
            unsafe { UnsafeUtility.MemSet(_chunkPools.GetUnsafePtr(), 0xFF, _chunkPools.Length * sizeof(int)); }

            for (int i = 0; i < maxChunks; i++)
                _freeChunkSlots.Enqueue(i);
            _allocatedCount = 0;
            _chunkHighWaterMark = 0;
        #if DEBUG
            _totalInstanceCount = 0;
        #endif
            int protoCount = _renderSignatures.Count;
            _protoBreakdownStride = protoCount;
            _chunkProtoBreakdown.DisposeIfCreated();
            _chunkProtoBreakdown = new NativeArray<int>(maxChunks * protoCount, Allocator.Persistent);
            _chunkHasAnimatedCrossfade = new NativeArray<bool>(maxChunks, Allocator.Persistent);
            _chunkLayerMask = new NativeArray<uint>(maxChunks, Allocator.Persistent);
            _animatedCrossfadePools = new NativeList<int>(64, Allocator.Persistent);
            // per-proto instance/flip arrays are owned by EnsureProtoArrayCapacity
            EnsureProtoArrayCapacity(protoCount);

            // seed free pool stack
            _freePools = new NativeList<int>(_poolCapacity, Allocator.Persistent);
            for (int i = _poolCapacity - 1; i >= 0; i--)
                _freePools.Add(i);
#if UNITY_EDITOR
            _deferredPoolFrees = new NativeList<int>(64, Allocator.Persistent);
#endif
            _poolInUse = new NativeArray<bool>(_poolCapacity, Allocator.Persistent);
            _poolStagedFrame = new NativeArray<uint>(_poolCapacity, Allocator.Persistent);
            _poolRmwStagedFrame = new NativeArray<uint>(_poolCapacity, Allocator.Persistent);
            _allocatedPoolCount = 0;

            // incremental instance tracking
            _poolSlotMask = new NativeArray<ulong>(_poolCapacity, Allocator.Persistent);
            _poolOccupancy = new NativeArray<int>(_poolCapacity, Allocator.Persistent);
            _poolToChunk = new NativeArray<int>(_poolCapacity, Allocator.Persistent);
            unsafe { UnsafeUtility.MemSet(_poolToChunk.GetUnsafePtr(), 0xFF, _poolCapacity * sizeof(int)); } // -1 = free
            _chunkFirstFreePoolHint = new NativeArray<int>(maxChunks, Allocator.Persistent);
            _slotProtoIndex = new NativeArray<int>(_poolCapacity * ps, Allocator.Persistent);
            unsafe
            {
                UnsafeUtility.MemSet(_slotProtoIndex.GetUnsafePtr(), 0xFF, _slotProtoIndex.Length * sizeof(int));
            }
            _slotFlipped = new NativeArray<bool>(_poolCapacity * ps, Allocator.Persistent);

            // pool reference buffer, each chunk gets _maxPoolRefsPerChunk slots
            int totalPoolRefSlots = maxChunks * _maxPoolRefsPerChunk;

            _poolRefBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, totalPoolRefSlots, sizeof(int));
            _poolRefBuffer.name = "FraxinusGPUBatcher_poolRefBuffer";
            _poolRefBuffer.SetData(_chunkPools);

            int totalInstanceSlots = _poolCapacity * ps;
            int instanceStride = gpuInstanceDataStride;
            int rotScaleStride = gpuInstanceRotScaleStride;
            int stagedStride = gpuStagedInstanceDataStride;
            int chunkStride = UnsafeUtility.SizeOf<GPUChunk>();

            // instance data buffer for GPU culling
            _instanceBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                totalInstanceSlots,
                instanceStride
            );
            _instanceBuffer.name = "FraxinusGPUBatcher_instanceBuffer";

            // rotation + scale buffer
            _instanceRotScaleBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                totalInstanceSlots,
                rotScaleStride
            );
            _instanceRotScaleBuffer.name = "FraxinusGPUBatcher_instanceRotScaleBuffer";

            // matrix buffer layout: [O2W * N][W2O * N][PrevM * N][SH AoS (if LP)][Color (if enabled)][SpeedTree (if any)]
            int matrixElements = totalInstanceSlots * 3 * 12;  // 3 matrices per instance (O2W, W2O, PrevM), 12 floats each
            int shElements = lightProbesEnabled ? totalInstanceSlots * k_shFloat4sPerInstance * 4 : 0;
            _perInstanceColorEnabled = _config.enablePerInstanceColor;
            int colorElements = _perInstanceColorEnabled ? totalInstanceSlots : 0; // 1 uint (packed RGBA) per instance

            // speedtree region: allocate only for speedtree prototypes (512 bytes each)
            // count once for sizing; InitSpeedTreeState will assign slots densely
            int initialSpeedTreeCount = 0;
            for (int i = 0; i < (_renderSignatures?.Count ?? 0); i++)
                if (_renderSignatures[i] != null && _renderSignatures[i].speedTreeRendererID != default)
                    initialSpeedTreeCount++;
            int speedTreeElements = initialSpeedTreeCount * k_speedTreeParamsPerProto;
            _speedTreeRegionByteOffset = (matrixElements + shElements + colorElements) * sizeof(float);

            _matrixBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw,
                matrixElements + shElements + colorElements + speedTreeElements,
                sizeof(float)
            );
            _matrixBuffer.name = "FraxinusGPUBatcher_matrixBuffer";
            // inverted encoding, zero = white
            _colorRegionByteOffset = _perInstanceColorEnabled ? (matrixElements + shElements) * sizeof(float) : 0;
            Shader.SetGlobalFloat(_ColorRegionByteOffset, _colorRegionByteOffset);

            _lightProbes?.Initialize(this, totalInstanceSlots);

            InitSpeedTreeState();

            // reusable buffers for WriteChunk
            int writeBufferSize = _maxPoolRefsPerChunk * ps;
            _writeChunkGpuBuffer = new NativeArray<GPUStagedInstanceData>(writeBufferSize, Allocator.Persistent);

            // staged GPU uploads: CPU -> NativeArray -> staging -> live
            _stagingCapacity = k_instanceStagingCapacity;

            // CPU-side staging array + GPU buffer
            _stagingCpuArray = new NativeArray<GPUStagedInstanceData>(_stagingCapacity, Allocator.Persistent);
            _stagingInstanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _stagingCapacity, stagedStride);
            _stagingInstanceBuffer.name = "FraxinusGPUBatcher_StagingInstance";

            _stagingWriteCursor = 0;
            _stagingDirty = false;
            int stagingPrealloc = math.max(64, maxChunks / 4);
            _stagedUploads = new NativeList<StagedUpload>(stagingPrealloc, Allocator.Persistent);
            _preFlushedUploads = new NativeList<StagedUpload>(64, Allocator.Persistent);
            _stagedClears = new NativeList<StagedUpload>(stagingPrealloc, Allocator.Persistent);
            _stagedFadeResets = new NativeList<StagedUpload>(stagingPrealloc, Allocator.Persistent);
            _stagedPoolRefUploads = new NativeList<StagedUpload>(stagingPrealloc, Allocator.Persistent);
            _poolRefSlotToCursor = new NativeHashMap<int, int>(k_poolRefStagingCapacity, Allocator.Persistent);
            _poolRefStagingCapacity = k_poolRefStagingCapacity;
            _poolRefStagingCpuArray = new NativeArray<int>(_poolRefStagingCapacity, Allocator.Persistent);
            _poolRefStagingWriteCursor = 0;
            _stagedChunkIndices = new NativeArray<int>(k_chunkStagingCapacity, Allocator.Persistent);
            _stagedChunkData = new NativeArray<GPUChunk>(k_chunkStagingCapacity, Allocator.Persistent);
            _chunkSlotToCursor = new NativeHashMap<int, int>(k_chunkStagingCapacity, Allocator.Persistent);
            _chunkStagingWriteCursor = 0;
            _stagingChunkBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_chunkStagingCapacity, chunkStride);
            _stagingChunkBuffer.name = "FraxinusGPUBatcher_StagingChunk";
            _stagingChunkIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_chunkStagingCapacity, sizeof(int));
            _stagingChunkIndexBuffer.name = "FraxinusGPUBatcher_StagingChunkIdx";
            _settleList = new NativeList<int>(64, Allocator.Persistent);
            // patch-based move staging, GPU patches only changed TRS components
            _trsPatchStagingCapacity = k_trsPatchStagingCapacity;
            _trsPatchStagingBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _trsPatchStagingCapacity,
                UnsafeUtility.SizeOf<GPUTRSPatchEntry>());
            _trsPatchStagingBuffer.name = "FraxinusGPUBatcher_PatchStaging";
            _trsPatchEntries = new NativeArray<GPUTRSPatchEntry>(_trsPatchStagingCapacity, Allocator.Persistent);
            _trsPatchSlotIndices = new NativeArray<int>(_trsPatchStagingCapacity, Allocator.Persistent);
            int totalSlots = _poolCapacity * k_poolSize;
            _slotMovedThisFrame = new NativeArray<bool>(totalSlots, Allocator.Persistent);
            _slotNeedsMatrix = new NativeArray<bool>(totalSlots, Allocator.Persistent);
            _recomputeScratch = new NativeList<int>(256, Allocator.Persistent);
            _movedThisFrame = new NativeArray<int>(totalSlots, Allocator.Persistent);
            _movedThisFrameCount = 0;
            _trsPatchWriteCursor = 0;
            _deferredChunkWrites = new NativeList<DeferredChunkWrite>(16, Allocator.Persistent);
            _deferredInstances = new NativeList<InstanceData>(256, Allocator.Persistent);
            _resizeDeferred = false;

            _stagingPoolRefBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, _poolRefStagingCapacity, sizeof(int));
            _stagingPoolRefBuffer.name = "FraxinusGPUBatcher_StagingPoolRef";

            // per-instance color staging
            if (_perInstanceColorEnabled)
            {
                _colorStagingCpuArray = new NativeArray<GPUColorEntry>(k_colorStagingCapacity, Allocator.Persistent);
                _colorStagingWriteCursor = 0;
                _colorStagingBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_colorStagingCapacity,
                    UnsafeUtility.SizeOf<GPUColorEntry>());
                _colorStagingBuffer.name = "FraxinusGPUBatcher_ColorStaging";
            }

            // sig override staging (enable/disable instances via separate scatter write)
            _sigOverrideStagingCpuArray = new NativeArray<GPUSigOverrideEntry>(k_sigOverrideStagingCapacity, Allocator.Persistent);
            _sigOverrideStagingWriteCursor = 0;
            _sigOverrideStagingBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, k_sigOverrideStagingCapacity,
                UnsafeUtility.SizeOf<GPUSigOverrideEntry>());
            _sigOverrideStagingBuffer.name = "FraxinusGPUBatcher_SigOverrideStaging";

            _chunkBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxChunks, chunkStride);
            _chunkBuffer.name = "FraxinusGPUBatcher_chunkBuffer";

            var emptyChunks = new NativeArray<GPUChunk>(maxChunks, Allocator.Temp);
            _chunkBuffer.SetData(emptyChunks);
            emptyChunks.Dispose();

            // invalidate pool entries
            _bufferPoolGeneration++;

            // Batch rebuilding sizes the draw buckets and visibility buffers.
            _bucketCount = 0;
            _drawCommandCount = 0;
            _maxVisibleInstances = 1;

            _chunkSystemInitialized = true;
            _cachedNegativeScaleMode = _config.negativeScaleMode;
            RefreshCachedPointers();

            // resize pre-existing view fade buffers
            ResizeViewFadeStateBuffers(totalInstanceSlots);

            // trigger rebuild to create batches
            RequestBatchRebuild(BatchRebuildScope.FullRebuild);

            long memBytes = (long)totalInstanceSlots * instanceStride +
                            (long)totalInstanceSlots * 2 * 12 * sizeof(float) +
                            (long)maxChunks * chunkStride +
                            (long)totalPoolRefSlots * sizeof(int) +
                            (long)maxChunks * sizeof(uint) * 2 +
                            sizeof(uint) * 8;
            LogMessageDevelopment($"GPUBatcherRenderer: Chunk system initialized (pool-based) - {maxChunks} chunks, {_poolCapacity} pools * {ps} = {GPUBatcherConfig.FormatBytes(memBytes)}");
        }

        #endregion

        #region Capacity Management

        private static void ResizeNativeArray<T>(ref NativeArray<T> arr, int newSize, int copyCount) where T : struct
        {
            var newArr = new NativeArray<T>(newSize, Allocator.Persistent);
            if (arr.IsCreated && copyCount > 0)
                NativeArray<T>.Copy(arr, 0, newArr, 0, copyCount);
            arr.DisposeIfCreated();
            arr = newArr;
        }

        private void ResizeChunkCapacity(int newMaxChunks, bool allowShrink = false)
        {
            if (newMaxChunks == _maxChunks)
                return;

            if (!allowShrink && newMaxChunks < _maxChunks)
                return;
#if DEBUG
            using var _rcc = s_markerResizeChunkCapacity.Auto();
#endif

            int oldMax = _maxChunks;

            int copyCount = math.min(oldMax, newMaxChunks);
            int newPoolRefSlots = newMaxChunks * _maxPoolRefsPerChunk;
            int chunkStride = UnsafeUtility.SizeOf<GPUChunk>();

            // allocate gpu buffers up front so a VRAM failure aborts before any state is mutated
            GraphicsBuffer newShadowChunkIdsBuffer = null;
            GraphicsBuffer newCpuVisibleChunkIdsBuffer = null;
            GraphicsBuffer newPoolRefBuffer = null;
            GraphicsBuffer newChunkBuffer = null;
            try
            {
                newShadowChunkIdsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newMaxChunks, sizeof(int));
                newCpuVisibleChunkIdsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newMaxChunks, sizeof(int));
                newPoolRefBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newPoolRefSlots, sizeof(int));
                newPoolRefBuffer.name = "FraxinusGPUBatcher_poolRefBuffer";
                newChunkBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newMaxChunks, chunkStride);
                newChunkBuffer.name = "FraxinusGPUBatcher_chunkBuffer";
            }
            catch (System.Exception ex)
            {
                newShadowChunkIdsBuffer?.Dispose();
                newCpuVisibleChunkIdsBuffer?.Dispose();
                newPoolRefBuffer?.Dispose();
                newChunkBuffer?.Dispose();
                Debug.LogError($"GPUBatcherRenderer: Failed to resize chunk capacity to {newMaxChunks}. VRAM may be exhausted. {ex.Message}");
                return;
            }

            // cpu arrays
            ResizeNativeArray(ref _chunkSlotActive, newMaxChunks, copyCount);
            ResizeNativeArray(ref _chunkEnabled, newMaxChunks, copyCount);
            ResizeNativeArray(ref _chunkGeneration, newMaxChunks, copyCount);
            ResizeNativeArray(ref _activeChunks, newMaxChunks, _allocatedCount); // only copy active portion
            // visibility slot pool is sized to old capacity - drain and re-allocate on demand
            DrainVisibilitySlotPool();
            _visibilitySlotCapacity = newMaxChunks;
            System.Array.Resize(ref _shadowActiveChunks, newMaxChunks);
            _shadowChunksDirty = true;
            _shadowChunkIdsBuffer?.Dispose();
            _shadowChunkIdsBuffer = newShadowChunkIdsBuffer;
            _cpuVisibleChunkIdsBuffer?.Dispose();
            _cpuVisibleChunkIdsBuffer = newCpuVisibleChunkIdsBuffer;
            ResizeNativeArray(ref _chunkIdToActiveIndex, newMaxChunks, copyCount);
            // fill new reverse map slots with -1
            unsafe { UnsafeUtility.MemSet((byte*)_chunkIdToActiveIndex.GetUnsafePtr() + copyCount * sizeof(int), 0xFF, (newMaxChunks - copyCount) * sizeof(int)); }
            ResizeNativeArray(ref _chunks, newMaxChunks, copyCount);

            // chunk pool lists, fill new slots with -1
            ResizeNativeArray(ref _chunkPools, newMaxChunks * _maxPoolRefsPerChunk, copyCount * _maxPoolRefsPerChunk);
            unsafe { UnsafeUtility.MemSet((byte*)_chunkPools.GetUnsafePtr() + copyCount * _maxPoolRefsPerChunk * sizeof(int), 0xFF, (newMaxChunks - copyCount) * _maxPoolRefsPerChunk * sizeof(int)); }

            ResizeNativeArray(ref _chunkFirstFreePoolHint, newMaxChunks, copyCount);

            // per-chunk prototype breakdown
            if (_chunkProtoBreakdown.IsCreated)
                ResizeNativeArray(ref _chunkProtoBreakdown, newMaxChunks * _protoBreakdownStride, copyCount * _protoBreakdownStride);
            if (_chunkHasAnimatedCrossfade.IsCreated)
                ResizeNativeArray(ref _chunkHasAnimatedCrossfade, newMaxChunks, copyCount);
            if (_chunkLayerMask.IsCreated)
                ResizeNativeArray(ref _chunkLayerMask, newMaxChunks, copyCount);

            if (newMaxChunks < oldMax)
            {
                // rebuild free chunk slots after shrink
                while (_freeChunkSlots.Count > 0) _freeChunkSlots.Dequeue();
                for (int i = 0; i < newMaxChunks; i++)
                    if (!_chunkSlotActive[i]) _freeChunkSlots.Enqueue(i);

                // clamp HWM
                _chunkHighWaterMark = math.min(_chunkHighWaterMark, newMaxChunks);
            }
            else
            {
                for (int i = oldMax; i < newMaxChunks; i++)
                    _freeChunkSlots.Enqueue(i);
            }

            _poolRefBuffer?.Dispose();
            _poolRefBuffer = newPoolRefBuffer;
            _poolRefBuffer.SetData(_chunkPools);

            // staging pool ref buffer: fixed size, overflow flushes mid-frame - no resize needed

            // chunk metadata buffer
            var oldChunkBuffer = _chunkBuffer;
            _chunkBuffer = newChunkBuffer;

            if (oldChunkBuffer != null)
            {
                // copy existing chunks
                GPUCopyChunkRegion(oldChunkBuffer, _chunkBuffer, copyCount);
                oldChunkBuffer?.Dispose();
            }


            // per-view buffers recreated lazily on next acquire
            _bufferPoolGeneration++;

            _maxChunks = newMaxChunks;
        }

        private void ResizePoolBuffer(int newPoolCapacity, bool allowShrink = false)
        {
#if DEBUG
            using var _rpb = s_markerResizePoolBuffer.Auto();
#endif
            if (newPoolCapacity == _poolCapacity)
                return;

            if (!allowShrink && newPoolCapacity < _poolCapacity)
                return;

#if DEBUG
            debugResizedThisFrame = true;
#endif

            int oldCapacity = _poolCapacity;
            int totalInstanceSlots = newPoolCapacity * k_poolSize;

            GraphicsBuffer newInstanceBuffer = null;
            GraphicsBuffer newRotScaleBuffer = null;
            GraphicsBuffer newMatrixBuffer = null;
            try
            {
                newInstanceBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    totalInstanceSlots,
                    gpuInstanceDataStride
                );
                newInstanceBuffer.name = "FraxinusGPUBatcher_instanceBuffer";

                newRotScaleBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    totalInstanceSlots,
                    gpuInstanceRotScaleStride
                );
                newRotScaleBuffer.name = "FraxinusGPUBatcher_instanceRotScaleBuffer";

                int matrixElements = totalInstanceSlots * 3 * 12;  // O2W + W2O + PrevM
                int shElements = lightProbesEnabled ? totalInstanceSlots * k_shFloat4sPerInstance * 4 : 0;
                int colorElements = _perInstanceColorEnabled ? totalInstanceSlots : 0;
                int speedTreeElements = _speedTreeSlotHWM * k_speedTreeParamsPerProto;
                newMatrixBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Raw,
                    matrixElements + shElements + colorElements + speedTreeElements,
                    sizeof(float)
                );
                newMatrixBuffer.name = "FraxinusGPUBatcher_matrixBuffer";
            }
            catch (System.Exception ex)
            {
                newInstanceBuffer?.Dispose();
                newRotScaleBuffer?.Dispose();
                newMatrixBuffer?.Dispose();
                Debug.LogError($"GPUBatcherRenderer: Failed to resize pool buffer to {newPoolCapacity} pools. VRAM may be exhausted. {ex.Message}");
                return;
            }

            int oldInstanceSlots = oldCapacity * k_poolSize;
            int copyInstanceSlots = math.min(oldInstanceSlots, totalInstanceSlots); // clamp for shrink
            if (copyInstanceSlots > 0)
            {
                // instance buffer (20B)
                GPUCopyInstanceBuffer(_instanceBuffer, 0, newInstanceBuffer, 0, copyInstanceSlots);
                // rotation + scale buffer (28B)
                GPUCopyRotScaleBuffer(_instanceRotScaleBuffer, 0, newRotScaleBuffer, 0, copyInstanceSlots);

                // matrix buffer: [O2W * N][W2O * N][PrevM * N][SH (if LP)]
                int matrixRegionBytes = copyInstanceSlots * k_sizeOfPackedMatrix;
                for (int region = 0; region < 3; region++) // O2W, W2O, PrevM
                    GPUCopyBufferRegion(_matrixBuffer, oldInstanceSlots * region * k_sizeOfPackedMatrix,
                        newMatrixBuffer, totalInstanceSlots * region * k_sizeOfPackedMatrix, matrixRegionBytes);

                // SH region
                if (lightProbesEnabled)
                {
                    int oldSHBase = oldInstanceSlots * 3 * 12 * sizeof(float);
                    int newSHBase = totalInstanceSlots * 3 * 12 * sizeof(float);
                    int shCopyBytes = copyInstanceSlots * k_shBytesPerInstance;
                    GPUCopyBufferRegion(_matrixBuffer, oldSHBase, newMatrixBuffer, newSHBase, shCopyBytes);
                }

                // color region
                if (_perInstanceColorEnabled)
                {
                    int oldSHElem = lightProbesEnabled ? oldInstanceSlots * k_shFloat4sPerInstance * 4 : 0;
                    int newSHElem = lightProbesEnabled ? totalInstanceSlots * k_shFloat4sPerInstance * 4 : 0;
                    int oldColorBase = (oldInstanceSlots * 3 * 12 + oldSHElem) * sizeof(float);
                    int newColorBase = (totalInstanceSlots * 3 * 12 + newSHElem) * sizeof(float);
                    int colorCopyBytes = copyInstanceSlots * sizeof(uint);
                    GPUCopyBufferRegion(_matrixBuffer, oldColorBase, newMatrixBuffer, newColorBase, colorCopyBytes);
                }

            }

            // copy wind region before replacing buffers
            if (_speedTreeSlotHWM > 0 && copyInstanceSlots > 0)
            {
                int oldSHElem = lightProbesEnabled ? oldInstanceSlots * k_shFloat4sPerInstance * 4 : 0;
                int oldColorElem = _perInstanceColorEnabled ? oldInstanceSlots : 0;
                int oldWindBase = (oldInstanceSlots * 3 * 12 + oldSHElem + oldColorElem) * sizeof(float);

                int newSHElem2 = lightProbesEnabled ? totalInstanceSlots * k_shFloat4sPerInstance * 4 : 0;
                int newColorElem2 = _perInstanceColorEnabled ? totalInstanceSlots : 0;
                int newWindBase = (totalInstanceSlots * 3 * 12 + newSHElem2 + newColorElem2) * sizeof(float);

                int windBytes = _speedTreeSlotHWM * k_speedTreeParamsPerProto * sizeof(float);
                GPUCopyBufferRegion(_matrixBuffer, oldWindBase, newMatrixBuffer, newWindBase, windBytes);
            }

            _instanceBuffer?.Dispose();
            _instanceRotScaleBuffer?.Dispose();
            _matrixBuffer?.Dispose();
            _instanceBuffer = newInstanceBuffer;
            _instanceRotScaleBuffer = newRotScaleBuffer;
            _matrixBuffer = newMatrixBuffer;

            // recompute region byte offsets after resize
            {
                int newMatrixElem = totalInstanceSlots * 3 * 12;
                int newSHElem = lightProbesEnabled ? totalInstanceSlots * k_shFloat4sPerInstance * 4 : 0;
                int newColorElem = _perInstanceColorEnabled ? totalInstanceSlots : 0;
                if (_perInstanceColorEnabled)
                {
                    _colorRegionByteOffset = (newMatrixElem + newSHElem) * sizeof(float);
                    Shader.SetGlobalFloat(_ColorRegionByteOffset, _colorRegionByteOffset);
                }
                _speedTreeRegionByteOffset = (newMatrixElem + newSHElem + newColorElem) * sizeof(float);

            }

            _lightProbes?.OnPoolResize(totalInstanceSlots);

            int copyPools = math.min(oldCapacity, newPoolCapacity);
            int copySlots = copyPools * k_poolSize;

            ResizeNativeArray(ref _poolInUse, newPoolCapacity, copyPools);
            ResizeNativeArray(ref _poolStagedFrame, newPoolCapacity, copyPools);
            ResizeNativeArray(ref _poolRmwStagedFrame, newPoolCapacity, copyPools);
            ResizeNativeArray(ref _poolSlotMask, newPoolCapacity, copyPools);
            ResizeNativeArray(ref _poolOccupancy, newPoolCapacity, copyPools);

            // pool-to-chunk reverse map, fill new region with -1
            ResizeNativeArray(ref _poolToChunk, newPoolCapacity, copyPools);
            if (newPoolCapacity > oldCapacity)
                unsafe { UnsafeUtility.MemSet((byte*)_poolToChunk.GetUnsafePtr() + oldCapacity * sizeof(int), 0xFF, (newPoolCapacity - oldCapacity) * sizeof(int)); }

            // slot proto index, fill new region with -1
            ResizeNativeArray(ref _slotProtoIndex, totalInstanceSlots, copySlots);
            if (totalInstanceSlots > copySlots)
                unsafe { UnsafeUtility.MemSet((byte*)_slotProtoIndex.GetUnsafePtr() + copySlots * sizeof(int), 0xFF, (totalInstanceSlots - copySlots) * sizeof(int)); }

            if (_slotFlipped.IsCreated)
                ResizeNativeArray(ref _slotFlipped, totalInstanceSlots, copySlots);

            // moved-this-frame flat arrays
            if (_slotMovedThisFrame.IsCreated)
                ResizeNativeArray(ref _slotMovedThisFrame, totalInstanceSlots, copySlots);
            if (_slotNeedsMatrix.IsCreated)
                ResizeNativeArray(ref _slotNeedsMatrix, totalInstanceSlots, copySlots);

            // purge moved-this-frame entries that reference slots beyond new capacity (per-slot flags stay valid by index)
            if (newPoolCapacity < oldCapacity && _movedThisFrame.IsCreated && _movedThisFrameCount > 0)
            {
                int w = 0;
                for (int i = 0; i < _movedThisFrameCount; i++)
                    if (_movedThisFrame[i] < totalInstanceSlots)
                        _movedThisFrame[w++] = _movedThisFrame[i];
                _movedThisFrameCount = w;
            }

            if (_movedThisFrame.IsCreated)
                ResizeNativeArray(ref _movedThisFrame, totalInstanceSlots, _movedThisFrameCount);

            _poolCapacity = newPoolCapacity;

#if UNITY_EDITOR
            DebugMirrorOnPoolResize(totalInstanceSlots);
#endif

            // purge settle list entries that reference slots beyond new capacity
            if (newPoolCapacity < oldCapacity && _settleList.IsCreated && _settleList.Length > 0)
            {
                int w = 0;
                for (int i = 0; i < _settleList.Length; i++)
                    if (_settleList[i] < totalInstanceSlots)
                        _settleList[w++] = _settleList[i];
                _settleList.Length = w;
            }

            if (newPoolCapacity < oldCapacity)
                RebuildFreePoolStructures();  // shrink: full rebuild
            else
                for (int i = oldCapacity; i < newPoolCapacity; i++)
                    _freePools.Add(i);  // grow: add the new indices

            // resize per-view fade state buffers
            ResizeViewFadeStateBuffers(totalInstanceSlots);

            RemoveAllBatchIds();
            RebindBatchBufferHandle();

            // suppress auto-compaction after grow to avoid shrink-grow ping-pong
            if (!allowShrink)
                _compactionCooldown = k_compactionCooldownCycles;

            RefreshCachedPointers();
        }

        // called after pool buffer resize
        private void RebuildFreePoolStructures()
        {
            _freePools.Clear();

            for (int i = 0; i < _poolCapacity; i++)
            {
                if (!_poolInUse[i])
                    _freePools.Add(i);
            }
        }

        private void ResizeViewFadeStateBuffers(int newCount)
        {
            foreach (var kvp in _viewFadeStates)
            {
                var state = kvp.Value;
                var oldBuffer = state.fadeStateBuffer;
                int oldCount = oldBuffer?.count ?? 0;

                // Keep exact matches; shrink only if savings exceed 50%.
                if (oldCount == newCount || (oldCount > newCount && newCount >= oldCount / 2))
                    continue;

                var newBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    newCount,
                    sizeof(int) * 2
                );
                newBuffer.name = $"FraxinusGPUBatcher_FadeState_View{kvp.Key}";

                int copyCount = math.min(oldCount, newCount);
                if (oldBuffer != null && copyCount > 0)
                    GPUCopyFadeStateRegion(oldBuffer, newBuffer, copyCount);
                oldBuffer?.Dispose();

                // init new slots to "culled, not fading"
                if (newCount > oldCount)
                    GPUInitFadeStateTyped(newBuffer, oldCount, newCount - oldCount);

                state.fadeStateBuffer = newBuffer;
            }

            // recalculate tracked total
            _trackedTotalFadeStates = 0;
            foreach (var kvp2 in _viewFadeStates)
                _trackedTotalFadeStates += kvp2.Value.fadeStateBuffer?.count ?? 0;
        }

        #endregion

        #region Shutdown

        private void ShutdownChunkSystem()
        {
            _currentCullingContext = null;
            ClearBatchSlots();
            DisposeDrawOrderState();

            _instanceBuffer?.Dispose();
            _instanceRotScaleBuffer?.Dispose();
            _matrixBuffer?.Dispose();
            _colorRegionByteOffset = 0;
            _speedTreeRegionByteOffset = 0;
            _speedTreeSlotHWM = 0;
            _freeSpeedTreeSlots.Clear();
            _protoSpeedTreeSlotIndex = null;
            ShutdownSpeedTreeState();
#if UNITY_EDITOR
            pickingEntries.Clear();
#endif
            Shader.SetGlobalFloat(_ColorRegionByteOffset, 0);

            _writeChunkGpuBuffer.DisposeIfCreated();

            _stagingCpuArray.DisposeIfCreated();
            _stagingInstanceBuffer?.Dispose();
            _stagingPoolRefBuffer?.Dispose();
            _stagingChunkBuffer?.Dispose();
            _stagingChunkIndexBuffer?.Dispose();
            _trsPatchStagingBuffer?.Dispose();
            _stagingInstanceBuffer = null;
            _stagingPoolRefBuffer = null;
            _stagingChunkBuffer = null;
            _stagingChunkIndexBuffer = null;
            _poolRefStagingCapacity = 0;
            _poolRefStagingWriteCursor = 0;
            _trsPatchStagingBuffer = null;
            _trsPatchStagingCapacity = 0;
            _trsPatchWriteCursor = 0;
            _trsPatchEntries.DisposeIfCreated();
            _trsPatchSlotIndices.DisposeIfCreated();
            _sigOverrideStagingCpuArray.DisposeIfCreated();
            _sigOverrideStagingWriteCursor = 0;
            _sigOverrideStagingBuffer?.Dispose();
            _sigOverrideStagingBuffer = null;
            _stagedUploads.DisposeIfCreated();
            _preFlushedUploads.DisposeIfCreated();
            _stagedClears.DisposeIfCreated();
            _stagedFadeResets.DisposeIfCreated();
            _stagedPoolRefUploads.DisposeIfCreated();
            _poolRefSlotToCursor.DisposeIfCreated();
            _poolRefStagingCpuArray.DisposeIfCreated();
            _colorStagingCpuArray.DisposeIfCreated();
            _colorStagingWriteCursor = 0;
            _colorStagingBuffer?.Dispose();
            _colorStagingBuffer = null;
            _stagedChunkData.DisposeIfCreated();
            _stagedChunkIndices.DisposeIfCreated();
            _chunkSlotToCursor.DisposeIfCreated();
            _chunkStagingWriteCursor = 0;
            _movedThisFrame.DisposeIfCreated();
            _movedThisFrameCount = 0;
            _settleList.DisposeIfCreated();
            _slotMovedThisFrame.DisposeIfCreated();
            _slotNeedsMatrix.DisposeIfCreated();
            _recomputeScratch.DisposeIfCreated();
            _deferredChunkWrites.DisposeIfCreated();
            _deferredInstances.DisposeIfCreated();
            _resizeDeferred = false;
            _settleIndexBuffer?.Dispose();
            _settleIndexBuffer = null;
            _batchCmdBuffer?.Dispose();
            _batchCmdBuffer = null;
            _batchCmdCapacity = 0;
            _stagingWriteCursor = 0;
            _stagingDirty = false;

            _animatedPoolsBuffer?.Dispose();
            _animatedPoolsBuffer = null;
            _animatedPoolsBufferCapacity = 0;
            _animatedCrossfadePools.DisposeIfCreated();
            _animatedPoolListDirty = true;

#if UNITY_EDITOR
            DisposeUploadValidator();
#endif

            _protoCrossfadeBuffer?.Dispose();
            _protoCrossfadeBuffer = null;
            _protoCrossfadeStates = null;
            _protoCycleIndex = null;

            _chunkBuffer?.Dispose();
            _poolRefBuffer?.Dispose();

            // dispose per-view buffer pools
            if (_cameraBufferPool != null)
            {
                foreach (var set in _cameraBufferPool) set.Dispose();
                _cameraBufferPool.Clear();
            }
            if (_shadowBufferPool != null)
            {
                foreach (var set in _shadowBufferPool) set.Dispose();
                _shadowBufferPool.Clear();
            }
#if UNITY_EDITOR
            InvalidateFrozenCulling();
            _activeCullingCameraId = default;
            _activeCullingCameraData = default;
            _sceneViewCameraBufferSet?.Dispose();
            _sceneViewCameraBufferSet = null;
            foreach (var set in _sceneViewShadowBufferSets) set.Dispose();
            _sceneViewShadowBufferSets.Clear();
#endif
            if (_viewFadeStates != null)
            {
                foreach (var kvp in _viewFadeStates) kvp.Value.Dispose();
                _viewFadeStates.Clear();
                _viewFadeStateKeys.Clear();
                _trackedTotalFadeStates = 0;
            }
            _argsToBucketBuffer?.Dispose();
            _shadowArgsToBucketBuffer?.Dispose();

            _instanceBuffer = null;
            _instanceRotScaleBuffer = null;
            _matrixBuffer = null;
            _chunkBuffer = null;
            _poolRefBuffer = null;
            _argsToBucketBuffer = null;
            _shadowArgsToBucketBuffer = null;
            _buckets.DisposeIfCreated();
            _shadowBuckets.DisposeIfCreated();
            _cameraIndirectArgsTemplate.DisposeIfCreated();
            _shadowIndirectArgsTemplate.DisposeIfCreated();

            _freeChunkSlots.DisposeIfCreated();
            _chunkSlotActive.DisposeIfCreated();
            _chunkEnabled.DisposeIfCreated();
            _chunkGeneration.DisposeIfCreated();
            _activeChunks.DisposeIfCreated();
            DrainVisibilitySlotPool();
            _visibilitySlotCapacity = 0;
            _shadowChunkIdsBuffer?.Dispose();
            _shadowChunkIdsBuffer = null;
            _cpuVisibleChunkIdsBuffer?.Dispose();
            _cpuVisibleChunkIdsBuffer = null;
            _shadowActiveChunks = null;
            _shadowActiveCount = 0;
            _chunkIdToActiveIndex.DisposeIfCreated();
            _chunks.DisposeIfCreated();
            _chunkPools.DisposeIfCreated();
            _freePools.DisposeIfCreated();
#if UNITY_EDITOR
            _deferredPoolFrees.DisposeIfCreated();
#endif
            _poolInUse.DisposeIfCreated();
            _poolStagedFrame.DisposeIfCreated();
            _poolRmwStagedFrame.DisposeIfCreated();
            _poolSlotMask.DisposeIfCreated();
            _poolOccupancy.DisposeIfCreated();
            _poolToChunk.DisposeIfCreated();
            _chunkFirstFreePoolHint.DisposeIfCreated();
            _slotProtoIndex.DisposeIfCreated();
            _slotFlipped.DisposeIfCreated();
            _chunkProtoBreakdown.DisposeIfCreated();
            _chunkHasAnimatedCrossfade.DisposeIfCreated();
            _chunkLayerMask.DisposeIfCreated();
            _allocatedCount = 0;
            _chunkHighWaterMark = 0;
            _allocatedPoolCount = 0;
            _maxChunks = 0;
        #if DEBUG
            _totalInstanceCount = 0;
        #endif
            _chunkSystemInitialized = false;

            LogMessageDevelopment("GPUBatcherRenderer: Chunk system shutdown");
        }

        #endregion

        #region Chunk & Pool Allocation

        /// <summary>Returns the instance count stored in the given chunk, or 0 if invalid.</summary>
        public int GetChunkInstanceCount(int chunkId)
        {
            if (!_chunks.IsCreated || chunkId < 0 || chunkId >= _chunks.Length) return 0;
            return _chunks[chunkId].instanceCount;
        }

        /// <summary>Returns the bounds of the given chunk, or default if invalid.</summary>
        public Bounds GetChunkBounds(int chunkId)
        {
            if (!_chunks.IsCreated || chunkId < 0 || chunkId >= _chunks.Length) return default;
            return _chunks[chunkId].bounds;
        }

        public struct ChunkDebugInfo
        {
            public Bounds bounds;
            public int instanceCount;
            public int poolCount;
            public float cullDistance;
            public float shadowDistance;
            public uint maxShadowCascade;
            public float instanceCullDistance;
        }

        /// <summary>Returns debug metadata for the given chunk, or default if invalid.</summary>
        public ChunkDebugInfo GetChunkDebugInfo(int chunkId)
        {
            if (!_chunks.IsCreated || chunkId < 0 || chunkId >= _chunks.Length) return default;
            var c = _chunks[chunkId];
            return new ChunkDebugInfo
            {
                bounds = c.bounds,
                instanceCount = c.instanceCount,
                poolCount = c.poolCount,
                cullDistance = c.cullDistanceSq > 0 ? math.sqrt(c.cullDistanceSq) : 0f,
                shadowDistance = c.shadowDistanceSq > 0 ? math.sqrt(c.shadowDistanceSq) : 0f,
                maxShadowCascade = c.maxShadowCascade,
                instanceCullDistance = c.instanceCullDistanceSq >= float.MaxValue ? float.MaxValue : math.sqrt(c.instanceCullDistanceSq),
            };
        }

        public int GetChunkGeneration(int chunkId)
        {
            if (!_chunkGeneration.IsCreated || chunkId < 0 || chunkId >= _chunkGeneration.Length) return -1;
            return _chunkGeneration[chunkId];
        }

        public bool IsChunkWritten(int chunkId)
        {
            if (!_chunks.IsCreated || chunkId < 0 || chunkId >= _chunks.Length) return false;
            return _chunks[chunkId].isWritten;
        }

        /// <summary>
        /// Returns true if the chunk is active and its generation matches. Use to detect stale ChunkLinks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsChunkValid(int chunkId, int generation)
        {
            return chunkId >= 0 && chunkId < _maxChunks
                && _chunkSlotActive[chunkId]
                && _chunkGeneration[chunkId] == generation;
        }

        public int AllocateChunk()
        {
#if DEBUG
            using var _ac = s_markerAllocateChunk.Auto();
#endif
            if (!_chunkSystemInitialized) return -1;
            if (_freeChunkSlots.Count == 0)
                ResizeChunkCapacity(_maxChunks * 2);
            if (!_freeChunkSlots.TryDequeue(out int slotId)) return -1;
            _chunkSlotActive[slotId] = true;
            _chunkEnabled[slotId] = true;
            if (_chunkLayerMask.IsCreated) _chunkLayerMask[slotId] = 0;
            _chunkFirstFreePoolHint[slotId] = 0;
            _chunkIdToActiveIndex[slotId] = _allocatedCount;
            _activeChunks[_allocatedCount] = slotId;
            _chunks[slotId] = new ChunkData { instanceCount = 0, poolCount = 0, instanceCullDistanceSq = float.MaxValue };
            _allocatedCount++;
            _shadowChunksDirty = true;
            if (slotId + 1 > _chunkHighWaterMark)
                _chunkHighWaterMark = slotId + 1;

            return slotId;
        }

        // returns pool count, or -2 to request resize
        private int AllocatePools(int chunkId, int instanceCount, bool isRetry = false)
        {
#if DEBUG
            using var _ap = s_markerAllocatePools.Auto();
#endif
            int poolsNeeded = (int)math.ceil((float)instanceCount / k_poolSize);

            int baseIdx = chunkId * _maxPoolRefsPerChunk;
            int poolCount = 0;

            // allocate from free pool queue
            if (_freePools.Length >= poolsNeeded)
            {
                for (int i = 0; i < poolsNeeded; i++)
                {
                    int idx = _freePools[_freePools.Length - 1]; _freePools.RemoveAtSwapBack(_freePools.Length - 1);
                    InvalidateStagedForPool(idx);
#if DEBUG
                    if (_poolInUse[idx])
                        Debug.LogError($"[Fraxinus GPU Batcher] DOUBLE-ALLOC pool {idx} already in use by chunk {_poolToChunk[idx]}, now claimed by chunk {chunkId}");
#endif
                    _poolInUse[idx] = true;
                    _poolToChunk[idx] = chunkId;
                    _chunkPools[baseIdx + poolCount] = idx;
                    poolCount++;
                }
                // ascending refs give contiguous slot runs, so staged commands coalesce
                if (poolCount > 1)
                    _chunkPools.GetSubArray(baseIdx, poolCount).Sort();
                _allocatedPoolCount += poolsNeeded;
                var cd = _chunks[chunkId]; cd.poolCount = poolCount; _chunks[chunkId] = cd;
                _chunkFirstFreePoolHint[chunkId] = 0;
                return poolCount;
            }

            if (isRetry)
            {
                Debug.LogError($"GPUBatcherRenderer: AllocatePools: resize failed to free enough pools (needed {poolsNeeded}, cap {_poolCapacity})");
                return -1;
            }

            // not enough free pools, defer resize
            return -2;
        }

        private void FreePools(int chunkId)
        {
            var chunk = _chunks[chunkId];
            int baseIdx = chunkId * _maxPoolRefsPerChunk;
            int freedCount = 0;

            // mark pools as freed - staged data invalidation deferred to allocation time via _poolStagedFrame
            for (int i = 0; i < chunk.poolCount; i++)
            {
                int poolIdx = _chunkPools[baseIdx + i];
                if (poolIdx < 0) continue;
                if (poolIdx < _poolCapacity && _poolInUse[poolIdx])
                {
                    _poolInUse[poolIdx] = false;
                    _poolToChunk[poolIdx] = -1;
                    _allocatedPoolCount--;
                    freedCount++;

                    if (_poolOccupancy.IsCreated)
                        ClearPoolTrackingState(poolIdx);
                }
            }

            // return freed pools to the free list
            if (freedCount > 0)
            {
                for (int i = 0; i < chunk.poolCount; i++)
                {
                    int poolIdx = _chunkPools[baseIdx + i];
                    if (poolIdx >= 0 && poolIdx < _poolCapacity)
                        ReturnPoolToFreeList(poolIdx);
                }
            }

            if (chunk.poolCount > 0)
            {
                unsafe { UnsafeUtility.MemSet((byte*)_chunkPools.GetUnsafePtr() + baseIdx * sizeof(int), 0xFF, chunk.poolCount * sizeof(int)); }
            }

            chunk.poolCount = 0;
            chunk.isWritten = false;
            _chunks[chunkId] = chunk;
            _chunkFirstFreePoolHint[chunkId] = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReturnPoolToFreeList(int poolIdx)
        {
#if UNITY_EDITOR
            if (_config.debugDeferPoolFrees) { _deferredPoolFrees.Add(poolIdx); return; }
#endif
            _freePools.Add(poolIdx);
        }

#if UNITY_EDITOR
        internal void DrainDeferredPoolFrees()
        {
            if (!_deferredPoolFrees.IsCreated || _deferredPoolFrees.Length == 0) return;
            _freePools.AddRange(_deferredPoolFrees.AsArray());
            _deferredPoolFrees.Clear();
        }
#endif

        #endregion

        #region Chunk Write

        /// <summary>
        /// Write instances to a chunk. Matrices computed on GPU from TRS after upload.
        /// </summary>
        public void WriteChunk(int chunkId, NativeArray<InstanceData> instances, Bounds bounds, float cullDistance = 0f, float instanceCullDistance = float.MaxValue)
        {
#if DEBUG
            using var _wc = s_markerWriteChunk.Auto();
#endif
            if (!_chunkSystemInitialized)
            {
                Debug.LogError("GPUBatcherRenderer: Chunk system not initialized.");
                return;
            }

            if (chunkId < 0 || chunkId >= _maxChunks)
            {
                Debug.LogError($"GPUBatcherRenderer: Invalid chunk ID {chunkId}");
                return;
            }

            if (!_chunkSlotActive[chunkId])
            {
                Debug.LogError($"GPUBatcherRenderer: Chunk {chunkId} is not allocated.");
                return;
            }

            int count = instances.Length;
            int maxInstances = _maxPoolRefsPerChunk * k_poolSize;
            if (count > maxInstances)
            {
                Debug.LogWarning($"GPUBatcherRenderer: Chunk {chunkId} has {count} instances, exceeds max {maxInstances}. Truncating.");
                count = maxInstances;
            }

        #if DEBUG
            _totalInstanceCount -= _chunks[chunkId].instanceCount;
        #endif
            // bump generation on rewrite so prior InstanceLinks/ChunkLinks fail IsAlive
            if (_chunks[chunkId].isWritten)
                _chunkGeneration[chunkId]++;
            SubtractProtoInstanceCounts(chunkId);
            FreePools(chunkId);

            if (count == 0)
            {
                _chunks[chunkId] = new ChunkData
                {
                    instanceCount = 0, bounds = bounds,
                    cullDistanceSq = cullDistance * cullDistance,
                    instanceCullDistanceSq = instanceCullDistance >= float.MaxValue ? float.MaxValue : instanceCullDistance * instanceCullDistance,
                    isWritten = true,
                };
                UpdateGPUChunkMetadata(chunkId, bounds, 0, 0, cullDistanceSq: cullDistance * cullDistance, shadowDistanceSq: 0f, layerMask: 0,
                    instanceCullDistanceSq: instanceCullDistance >= float.MaxValue ? float.MaxValue : instanceCullDistance * instanceCullDistance);
                return;
            }

            if (_resizeDeferred)
            {
                QueueDeferredWrite(chunkId, instances, count, bounds, cullDistance, instanceCullDistance);
                return;
            }

            int poolCount = AllocatePools(chunkId, count);
            if (poolCount == -2)
            {
                _resizeDeferred = true;
                QueueDeferredWrite(chunkId, instances, count, bounds, cullDistance, instanceCullDistance);
                return;
            }
            if (poolCount <= 0)
            {
                Debug.LogError($"GPUBatcherRenderer: Failed to allocate pools for chunk {chunkId}");
                return;
            }

        #if DEBUG
            _totalInstanceCount += count;
        #endif
            ExecuteChunkWrite(chunkId, instances, count, poolCount, bounds, cullDistance, instanceCullDistance);
        }

        private void QueueDeferredWrite(int chunkId, NativeArray<InstanceData> instances, int count,
            Bounds bounds, float cullDistance, float instanceCullDistance)
        {
            // tombstone any prior queued write for this chunk; only the latest replays
            for (int i = _deferredChunkWrites.Length - 1; i >= 0; i--)
            {
                if (_deferredChunkWrites[i].chunkId == chunkId)
                {
                    var dead = _deferredChunkWrites[i];
                    dead.chunkId = -1;
                    _deferredChunkWrites[i] = dead;
                    break;
                }
            }

            int offset = _deferredInstances.Length;
            _deferredInstances.AddRange(instances.GetSubArray(0, count));
            _deferredChunkWrites.Add(new DeferredChunkWrite
            {
                chunkId = chunkId,
                generation = _chunkGeneration[chunkId],
                instanceOffset = offset,
                instanceCount = count,
                bounds = bounds,
                cullDistance = cullDistance,
                instanceCullDistance = instanceCullDistance,
            });
        }

        // stage chunk's pool refs for GPU upload
        private void RebuildChunkPoolRefs(int chunkId, int count)
        {
            if (count <= 0) return;
            int poolRefStart = chunkId * _maxPoolRefsPerChunk;
            StagePoolRefUpload(poolRefStart, count);
        }

        private void ExecuteChunkWrite(int chunkId, NativeArray<InstanceData> instances, int count,
            int poolCount, Bounds bounds, float cullDistance,
            float instanceCullDistance = float.MaxValue)
        {
            int poolBaseIdx = chunkId * _maxPoolRefsPerChunk;
            RebuildChunkPoolRefs(chunkId, poolCount);

            {
                new PackInstanceDataJob
                {
                    instances = instances, srcOffset = 0,
                    forcePositiveScale = _cachedNegativeScaleMode == NegativeScaleMode.ForcePositiveScale,
                    gpuBuffer = _writeChunkGpuBuffer
                }.Run(count);

                int instancesPerPool = k_poolSize;
                int remaining = count;
                int srcOffset = 0;

                _lightProbes?.BeginBatchSample(instances, 0, count);

                for (int poolLocalIdx = 0; poolLocalIdx < poolCount; poolLocalIdx++)
                {
                    int poolIdx = _chunkPools[poolBaseIdx + poolLocalIdx];
                    int poolInstanceCount = math.min(remaining, instancesPerPool);
                    int dstOffset = poolIdx * instancesPerPool;

                    StageUpload(_writeChunkGpuBuffer, srcOffset, dstOffset, poolInstanceCount);
                    _lightProbes?.StageBatchRange(srcOffset, poolInstanceCount, dstOffset);
                    ResetFadeStatesForSlots(dstOffset, poolInstanceCount);

                    remaining -= poolInstanceCount;
                    srcOffset += poolInstanceCount;
                }
            }

            int protoCount = _renderSignatures.Count;
            int breakdownBase = chunkId * _protoBreakdownStride;
            bool trackProtoCounts = _protoInstanceCounts.IsCreated;
            bool hasBreakdown = _chunkProtoBreakdown.IsCreated;
            bool flipWinding = _cachedNegativeScaleMode == NegativeScaleMode.FlipWinding;
            bool writeSlotFlipped = flipWinding && _slotFlipped.IsCreated;
            bool trackProtoFlipCounts = flipWinding && _protoFlipCounts.IsCreated && trackProtoCounts;
            bool computeFlipBit = writeSlotFlipped || trackProtoFlipCounts;
            bool hasCascadeData = _prototypeMaxShadowCascade.IsCreated;
            bool hasDistData = _prototypeMaxShadowDistance.IsCreated;

            // clear chunk's breakdown region
            if (trackProtoCounts && hasBreakdown && _protoBreakdownStride == protoCount)
            {
                for (int i = 0; i < protoCount; i++)
                    _chunkProtoBreakdown[breakdownBase + i] = 0;
            }

            int maxCascades = 0;
            float maxDist = 0f;
            bool anyUnlimited = false;
            bool cascadeMaxed = false;

            // fused per-pool loop: bookkeeping + breakdown + shadow reduction + flip count
            {
                int remaining = count;
                int srcIdx = 0;
                for (int p = 0; p < poolCount; p++)
                {
                    int poolIdx = _chunkPools[poolBaseIdx + p];
                    int slotsFilled = math.min(remaining, k_poolSize);

                    _poolOccupancy[poolIdx] = slotsFilled;
                    SetPoolSlotMask(poolIdx, slotsFilled);

                    int slotBase = poolIdx * k_poolSize;
                    for (int s = 0; s < slotsFilled; s++)
                    {
                        var inst = instances[srcIdx + s];
                        int pi = inst.signatureIndex;

                        _slotProtoIndex[slotBase + s] = pi;

                        bool flipped = false;
                        if (computeFlipBit)
                        {
                            var sc = inst.scale;
                            flipped = sc.x * sc.y * sc.z < 0f;
                        }
                        if (writeSlotFlipped)
                            _slotFlipped[slotBase + s] = flipped;

                        if (pi >= 0)
                        {
                            if (trackProtoCounts && hasBreakdown && pi < protoCount)
                                _chunkProtoBreakdown[breakdownBase + pi]++;

                            if (!cascadeMaxed && hasCascadeData
                                && pi < _prototypeMaxShadowCascade.Length
                                && pi < _prototypeShadowMaskCache.Length)
                            {
                                if (_prototypeShadowMaskCache[pi] != 0)
                                {
                                    int c = _prototypeMaxShadowCascade[pi];
                                    if (c > maxCascades) maxCascades = c;
                                    if (maxCascades >= 4) cascadeMaxed = true;
                                }
                            }

                            if (hasDistData && !anyUnlimited && pi < _prototypeMaxShadowDistance.Length)
                            {
                                float d = _prototypeMaxShadowDistance[pi];
                                if (d <= 0f) anyUnlimited = true;
                                else if (d > maxDist) maxDist = d;
                            }

                            if (trackProtoFlipCounts && flipped && pi < protoCount)
                            {
                                int oldFlip = _protoFlipCounts[pi];
                                _protoFlipCounts[pi] = oldFlip + 1;
                                if (oldFlip == 0)
                                {
                                    _pendingProtoResizes.Add(pi);
                                    RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
                                }
                            }
                        }
                    }
                    for (int s = slotsFilled; s < k_poolSize; s++)
                    {
                        _slotProtoIndex[slotBase + s] = -1;
                        if (writeSlotFlipped) _slotFlipped[slotBase + s] = false;
                    }

                    if (slotsFilled < k_poolSize)
                        StageClear(slotBase + slotsFilled, k_poolSize - slotsFilled);

                    remaining -= slotsFilled;
                    srcIdx += slotsFilled;
                }
            }

            // per-proto instance count totals + HWM
            if (trackProtoCounts)
            {
                for (int i = 0; i < protoCount; i++)
                {
                    int addCount = hasBreakdown ? _chunkProtoBreakdown[breakdownBase + i] : 0;
                    _protoInstanceCounts[i] += addCount;

                    if (_protoInstanceCountHWM.IsCreated && i < _protoInstanceCountHWM.Length
                        && _protoInstanceCounts[i] > _protoInstanceCountHWM[i])
                    {
                        _protoInstanceCountHWM[i] = math.max(
                            _protoInstanceCounts[i] + _protoInstanceCounts[i] / 4,
                            _protoInstanceCounts[i] + 256);
                        _pendingProtoResizes.Add(i);
                        RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
                    }

                    // proto gained instances but has no commands yet: materialize
                    if (addCount > 0 && !ProtoMaterialized(i))
                    {
                        _pendingProtoResizes.Add(i);
                        RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
                    }
                }
            }

            // per-proto flip HWM
            if (trackProtoFlipCounts)
            {
                for (int i = 0; i < protoCount; i++)
                {
                    if (_protoFlipCountHWM.IsCreated && i < _protoFlipCountHWM.Length
                        && _protoFlipCounts[i] > _protoFlipCountHWM[i])
                    {
                        _protoFlipCountHWM[i] = math.max(
                            _protoFlipCounts[i] + _protoFlipCounts[i] / 4,
                            _protoFlipCounts[i] + 64);
                        _pendingProtoResizes.Add(i);
                        RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
                    }
                }
            }

            // animated crossfade flag
            if (trackProtoCounts && _chunkHasAnimatedCrossfade.IsCreated
                && _prototypeAnimatedCrossfade.IsCreated && hasBreakdown)
            {
                bool has = false;
                int limit = math.min(protoCount, _prototypeAnimatedCrossfade.Length);
                for (int i = 0; i < limit; i++)
                {
                    if (_prototypeAnimatedCrossfade[i] && _chunkProtoBreakdown[breakdownBase + i] > 0)
                    { has = true; break; }
                }
                if (has || _chunkHasAnimatedCrossfade[chunkId])
                    _animatedPoolListDirty = true;
                _chunkHasAnimatedCrossfade[chunkId] = has;
            }

            // layer mask
            if (trackProtoCounts && _chunkLayerMask.IsCreated && hasBreakdown)
            {
                uint mask = 0;
                int layerLimit = math.min(protoCount, _renderSignatures.Count);
                for (int i = 0; i < layerLimit; i++)
                    if (_chunkProtoBreakdown[breakdownBase + i] > 0)
                        mask |= 1u << _renderSignatures[i].layer;
                _chunkLayerMask[chunkId] = mask;
            }

            // path A fallback when no shadow data arrays exist
            uint chunkMaxCascades = (!hasCascadeData && !hasDistData) ? 4u : (uint)maxCascades;
            float chunkShadowDistSq = anyUnlimited ? 0f : maxDist * maxDist;

            float cullDistSq = cullDistance * cullDistance;
            float instCullDistSq = instanceCullDistance >= float.MaxValue ? float.MaxValue : instanceCullDistance * instanceCullDistance;
            _chunks[chunkId] = new ChunkData
            {
                instanceCount = count, bounds = bounds,
                poolCount = poolCount,
                cullDistanceSq = cullDistSq, shadowDistanceSq = chunkShadowDistSq,
                maxShadowCascade = chunkMaxCascades,
                instanceCullDistanceSq = instCullDistSq,
                isWritten = true,
            };

            UpdateGPUChunkMetadata(chunkId, bounds, (uint)poolBaseIdx, (uint)poolCount, cullDistSq, chunkShadowDistSq, chunkMaxCascades, _chunkLayerMask.IsCreated ? _chunkLayerMask[chunkId] : 0xFFFFFFFF, instCullDistSq);
        }

        // update chunk shadow params if a prototype exceeds current limits
        private void UpdateChunkShadowParamsForProto(ref ChunkData chunk, int protoIdx)
        {
            if (protoIdx >= 0 && protoIdx < _prototypeMaxShadowCascade.Length
                && _prototypeShadowMaskCache.IsCreated && protoIdx < _prototypeShadowMaskCache.Length
                && _prototypeShadowMaskCache[protoIdx] != 0)
            {
                int protoCascades = _prototypeMaxShadowCascade[protoIdx];
                if ((uint)protoCascades > chunk.maxShadowCascade)
                    chunk.maxShadowCascade = (uint)protoCascades;

                float protoDist = _prototypeMaxShadowDistance[protoIdx];
                if (protoDist <= 0f)
                    chunk.shadowDistanceSq = 0f;
                else if (chunk.shadowDistanceSq != 0f && protoDist * protoDist > chunk.shadowDistanceSq)
                    chunk.shadowDistanceSq = protoDist * protoDist;
            }
        }

        // per-chunk signature range - packs (minSigIdx << 16 | count) for groupshared signature caching on GPU
        [MethodImpl(MethodImplOptions.AggressiveInlining)]


        private void UpdateGPUChunkMetadata(int chunkId, Bounds bounds, uint poolRefStart = 0, uint poolCount = 0, float cullDistanceSq = 0f, float shadowDistanceSq = 0f, uint maxShadowCascade = 4, uint layerMask = 0xFFFFFFFF, float instanceCullDistanceSq = float.MaxValue)
        {
            // overwrite existing entry for same chunk
            if (_chunkSlotToCursor.TryGetValue(chunkId, out int existingIdx))
            {
                _stagedChunkData[existingIdx] = new GPUChunk
                {
                    boundsMinXZ = new float2(bounds.min.x, bounds.min.z),
                    boundsMaxXZ = new float2(bounds.max.x, bounds.max.z),
                    boundsMinY = bounds.min.y,
                    boundsMaxY = bounds.max.y,
                    poolRefStart = poolRefStart,
                    poolCount = poolCount,

                    cullDistanceSq = cullDistanceSq,
                    shadowDistanceSq = shadowDistanceSq,
                    maxShadowCascade = maxShadowCascade,
                    layerMask = layerMask,
                    instanceCullDistanceSq = instanceCullDistanceSq
                };
                _stagingDirty = true;
                return;
            }

            // overflow: flush chunk metadata, reset cursor
            if (_chunkStagingWriteCursor >= k_chunkStagingCapacity)
            {
                statChunkOverflowFlushes++;
                FlushChunkMetadataUploads();
            }

            int idx = _chunkStagingWriteCursor++;
            _chunkSlotToCursor[chunkId] = idx;
            _stagedChunkIndices[idx] = chunkId;
            _stagedChunkData[idx] = new GPUChunk
            {
                boundsMinXZ = new float2(bounds.min.x, bounds.min.z),
                boundsMaxXZ = new float2(bounds.max.x, bounds.max.z),
                boundsMinY = bounds.min.y,
                boundsMaxY = bounds.max.y,
                poolRefStart = poolRefStart,
                poolCount = poolCount,
                cullDistanceSq = cullDistanceSq,
                shadowDistanceSq = shadowDistanceSq,
                maxShadowCascade = maxShadowCascade,
                layerMask = layerMask,
                instanceCullDistanceSq = instanceCullDistanceSq
            };
            _stagingDirty = true;
        }

        #endregion

        #region Chunk Free

        public void FreeChunk(int chunkId)
        {
            if (!_chunkSystemInitialized)
            {
                // silently ignore during teardown
                return;
            }

            if (chunkId < 0 || chunkId >= _maxChunks)
            {
                Debug.LogError($"GPUBatcherRenderer: Invalid chunk ID {chunkId}");
                return;
            }

            if (!_chunkSlotActive[chunkId])
            {
                Debug.LogWarning($"GPUBatcherRenderer: Chunk {chunkId} is already free.");
                return;
            }

#if DEBUG
            using var _fc = s_markerFreeChunk.Auto();
#endif
        #if DEBUG
            _totalInstanceCount -= _chunks[chunkId].instanceCount;
        #endif
            SubtractProtoInstanceCounts(chunkId);

            // clear fade states and light probes for freed pools
            var chunk = _chunks[chunkId];
            int poolBaseIdx = chunkId * _maxPoolRefsPerChunk;
            bool clearFades = _anyPrototypeUsesAnimatedCrossfade;
            bool clearProbes = _lightProbes != null;

            if (clearFades || clearProbes)
            {
                for (int p = 0; p < chunk.poolCount; p++)
                {
                    int poolIdx = _chunkPools[poolBaseIdx + p];
                    if (poolIdx < 0) continue;
                    int offset = poolIdx * k_poolSize;

                    if (clearFades)
                        ResetFadeStatesForSlots(offset, k_poolSize);

                    if (clearProbes)
                        _lightProbes.ClearSlots(offset, k_poolSize);
                }
            }

#if UNITY_EDITOR
            // remove picking entries for all live slots in this chunk
            {
                var freeChunk = _chunks[chunkId];
                int freeBase = chunkId * _maxPoolRefsPerChunk;
                for (int p = 0; p < freeChunk.poolCount; p++)
                {
                    int poolIdx = _chunkPools[freeBase + p];
                    if (poolIdx < 0) continue;
                    ulong mask = _poolSlotMask[poolIdx];
                    int slotBase = poolIdx * k_poolSize;
                    while (mask != 0)
                    {
                        int bit = math.tzcnt(mask);
                        pickingEntries.Remove(slotBase + bit);
                        mask &= mask - 1;
                    }
                }
            }
#endif

            FreePools(chunkId);

            _chunkSlotActive[chunkId] = false;
            _chunkEnabled[chunkId] = false;
            if (_chunkLayerMask.IsCreated) _chunkLayerMask[chunkId] = 0;
            _chunkGeneration[chunkId]++;

            // swap-remove from active list
            int activeIdx = _chunkIdToActiveIndex[chunkId];
            int lastActive = _allocatedCount - 1;
            if (activeIdx != lastActive)
            {
                int movedChunkId = _activeChunks[lastActive];
                _activeChunks[activeIdx] = movedChunkId;
                _chunkIdToActiveIndex[movedChunkId] = activeIdx;
            }
            _chunkIdToActiveIndex[chunkId] = -1;

            _chunks[chunkId] = new ChunkData { instanceCount = 0 };
            _allocatedCount--;
            _shadowChunksDirty = true;

            // lazy-shrink chunk high water mark
            if (chunkId + 1 == _chunkHighWaterMark)
            {
                while (_chunkHighWaterMark > 0 && !_chunkSlotActive[_chunkHighWaterMark - 1])
                    _chunkHighWaterMark--;
            }

            UpdateGPUChunkMetadata(chunkId, new Bounds(), 0, layerMask: 0);
            _freeChunkSlots.Enqueue(chunkId);
            _chunkCompactionDirty = true;
            _poolCompactionDirty = true;
            _protoCompactionDirty = true;
        }

        #endregion

        #region Pool Utilities

        // update GPU chunk metadata from current ChunkData state
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateChunkGPUState(int chunkId)
        {
            var chunk = _chunks[chunkId];
            int poolRefStart = chunkId * _maxPoolRefsPerChunk;
            UpdateGPUChunkMetadata(chunkId, chunk.bounds,
                (uint)poolRefStart, (uint)chunk.poolCount,
                chunk.cullDistanceSq, chunk.shadowDistanceSq, chunk.maxShadowCascade,
                _chunkLayerMask.IsCreated ? _chunkLayerMask[chunkId] : 0xFFFFFFFF,
                chunk.instanceCullDistanceSq);
        }

        // fills first hole, else appends. returns slot, or -1 if chunk has no room.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int PlacePoolInChunk(int chunkId, int poolIdx, ref ChunkData chunk)
        {
            int baseIdx = chunkId * _maxPoolRefsPerChunk;

            int slot = -1;
            for (int p = 0; p < chunk.poolCount; p++)
            {
                if (_chunkPools[baseIdx + p] < 0) { slot = p; break; }
            }
            if (slot < 0)
            {
                if (chunk.poolCount >= _maxPoolRefsPerChunk) return -1;
                slot = chunk.poolCount++;
            }
            _chunkPools[baseIdx + slot] = poolIdx;

            if (slot < _chunkFirstFreePoolHint[chunkId])
                _chunkFirstFreePoolHint[chunkId] = slot;

            if (_chunkHasAnimatedCrossfade.IsCreated && _chunkHasAnimatedCrossfade[chunkId])
                _animatedPoolListDirty = true;

            StagePoolRefUpload(baseIdx + slot, 1);
            return slot;
        }

        // returns a pool to the free list when PlacePoolInChunk fails post-allocation
        private void ReleaseUnusedPool(int poolIdx)
        {
            _poolInUse[poolIdx] = false;
            _poolToChunk[poolIdx] = -1;
            _allocatedPoolCount--;
            ReturnPoolToFreeList(poolIdx);
        }

        // returns pool index, or -1 on failure. resizes if needed.
        private int AllocateSinglePool()
        {
            if (_freePools.Length > 0)
            {
                int idx = _freePools[_freePools.Length - 1]; _freePools.RemoveAtSwapBack(_freePools.Length - 1);
                InvalidateStagedForPool(idx);
                _poolInUse[idx] = true;
                _allocatedPoolCount++;
                return idx;
            }

            // resize
            int newCapacity = _poolCapacity + math.max(1, _poolCapacity / 2);
            ResizePoolBuffer(newCapacity);

            if (_freePools.Length > 0)
            {
                int idx = _freePools[_freePools.Length - 1]; _freePools.RemoveAtSwapBack(_freePools.Length - 1);
                // no InvalidateStagedForPool needed - freshly grown pools have never been staged
                _poolInUse[idx] = true;
                _allocatedPoolCount++;
                return idx;
            }

            return -1;
        }

        // expand chunk bounds to include a new instance
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Bounds ExpandBoundsForInstance(Bounds current, in InstanceData instance, in NativeArray<float> protoBoundsRadius, bool isEmpty)
        {
            int pi = instance.signatureIndex;
            float radius;
            if (protoBoundsRadius.IsCreated && pi >= 0 && pi < protoBoundsRadius.Length)
                radius = protoBoundsRadius[pi];
            else
                radius = 1f;

            float maxScale = math.max(math.max(math.abs(instance.scale.x), math.abs(instance.scale.y)), math.abs(instance.scale.z));
            float extent = radius * maxScale;
            float3 pos = instance.position;

            if (isEmpty)
            {
                return new Bounds((Vector3)pos, new Vector3(extent * 2f, extent * 2f, extent * 2f));
            }

            current.Encapsulate((Vector3)(pos - extent));
            current.Encapsulate((Vector3)(pos + extent));
            return current;
        }

        // clear pool tracking state
        private void ClearPoolTrackingState(int poolIdx)
        {
            _poolOccupancy[poolIdx] = 0;
            _poolSlotMask[poolIdx] = 0;
            unsafe { UnsafeUtility.MemSet((byte*)_slotProtoIndex.GetUnsafePtr() + poolIdx * k_poolSize * sizeof(int), 0xFF, k_poolSize * sizeof(int)); }
        }

        // set slot bitmask for occupied slot count
        private void SetPoolSlotMask(int poolIdx, int slotsOccupied)
        {
            _poolSlotMask[poolIdx] = slotsOccupied >= 64 ? ~0UL
                : slotsOccupied > 0 ? (1UL << slotsOccupied) - 1UL : 0UL;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InitializeNewPool(int poolIndex, int chunkId)
        {
            StageClear(poolIndex * k_poolSize, k_poolSize);
            ClearPoolTrackingState(poolIndex);
            _poolToChunk[poolIndex] = chunkId;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void IncrementProtoInstanceCount(int chunkId, int protoIdx, int globalSlot, bool isFlipped)
        {
            if (protoIdx < 0 || protoIdx >= _protoInstanceCounts.Length) return;
            // first instance for a proto with no commands yet: materialize
            if (_protoInstanceCounts[protoIdx] == 0 && !ProtoMaterialized(protoIdx))
            {
                _pendingProtoResizes.Add(protoIdx);
                RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
            }
            _protoInstanceCounts[protoIdx]++;
            if (_chunkProtoBreakdown.IsCreated && protoIdx < _protoBreakdownStride)
                _chunkProtoBreakdown[chunkId * _protoBreakdownStride + protoIdx]++;
            if (_chunkLayerMask.IsCreated && protoIdx < _renderSignatures.Count)
                _chunkLayerMask[chunkId] |= 1u << _renderSignatures[protoIdx].layer;
            if (_chunkHasAnimatedCrossfade.IsCreated && _prototypeAnimatedCrossfade.IsCreated
                && protoIdx < _prototypeAnimatedCrossfade.Length && _prototypeAnimatedCrossfade[protoIdx]
                && !_chunkHasAnimatedCrossfade[chunkId])
            {
                _chunkHasAnimatedCrossfade[chunkId] = true;
                _animatedPoolListDirty = true;
            }
            if (_protoInstanceCountHWM != null && protoIdx < _protoInstanceCountHWM.Length
                && _protoInstanceCounts[protoIdx] > _protoInstanceCountHWM[protoIdx])
            {
                _protoInstanceCountHWM[protoIdx] = math.max(
                    _protoInstanceCounts[protoIdx] + _protoInstanceCounts[protoIdx] / 4,
                    _protoInstanceCounts[protoIdx] + 256);
                _pendingProtoResizes.Add(protoIdx);
                RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
            }

            if (_cachedNegativeScaleMode == NegativeScaleMode.FlipWinding && _slotFlipped.IsCreated)
            {
                _slotFlipped[globalSlot] = isFlipped;
                if (isFlipped && _protoFlipCounts.IsCreated && protoIdx < _protoFlipCounts.Length)
                {
                    int oldFlip = _protoFlipCounts[protoIdx];
                    _protoFlipCounts[protoIdx] = oldFlip + 1;
                    if (oldFlip == 0)
                    { _pendingProtoResizes.Add(protoIdx); RequestBatchRebuild(BatchRebuildScope.IncrementalFlush); }
                    if (_protoFlipCountHWM.IsCreated && protoIdx < _protoFlipCountHWM.Length
                        && _protoFlipCounts[protoIdx] > _protoFlipCountHWM[protoIdx])
                    {
                        _protoFlipCountHWM[protoIdx] = math.max(
                            _protoFlipCounts[protoIdx] + _protoFlipCounts[protoIdx] / 4,
                            _protoFlipCounts[protoIdx] + 64);
                        _pendingProtoResizes.Add(protoIdx);
                        RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecrementProtoInstanceCount(int chunkId, int protoIdx, int globalSlot)
        {
            if (protoIdx < 0 || protoIdx >= _protoInstanceCounts.Length) return;
            _protoInstanceCounts[protoIdx] = math.max(0, _protoInstanceCounts[protoIdx] - 1);
            if (_chunkProtoBreakdown.IsCreated && protoIdx < _protoBreakdownStride)
            {
                int idx = chunkId * _protoBreakdownStride + protoIdx;
                int newCount = math.max(0, _chunkProtoBreakdown[idx] - 1);
                _chunkProtoBreakdown[idx] = newCount;
                if (newCount == 0)
                {
                    if (_chunkHasAnimatedCrossfade.IsCreated
                        && _prototypeAnimatedCrossfade.IsCreated && protoIdx < _prototypeAnimatedCrossfade.Length
                        && _prototypeAnimatedCrossfade[protoIdx])
                    {
                        bool has = RecomputeChunkHasAnimatedCrossfade(chunkId);
                        if (_chunkHasAnimatedCrossfade[chunkId] != has)
                            _animatedPoolListDirty = true;
                        _chunkHasAnimatedCrossfade[chunkId] = has;
                    }
                    if (_chunkLayerMask.IsCreated)
                        _chunkLayerMask[chunkId] = RecomputeChunkLayerMask(chunkId);
                }
            }

            if (_slotFlipped.IsCreated && _slotFlipped[globalSlot]
                && _protoFlipCounts.IsCreated && protoIdx < _protoFlipCounts.Length)
            {
                _protoFlipCounts[protoIdx] = math.max(0, _protoFlipCounts[protoIdx] - 1);
                _slotFlipped[globalSlot] = false;
            }
        }

        private bool RecomputeChunkHasAnimatedCrossfade(int chunkId)
        {
            if (!_chunkProtoBreakdown.IsCreated || !_prototypeAnimatedCrossfade.IsCreated) return false;
            int baseOffset = chunkId * _protoBreakdownStride;
            int limit = math.min(_protoBreakdownStride, _prototypeAnimatedCrossfade.Length);
            for (int p = 0; p < limit; p++)
                if (_chunkProtoBreakdown[baseOffset + p] > 0 && _prototypeAnimatedCrossfade[p])
                    return true;
            return false;
        }

        private uint RecomputeChunkLayerMask(int chunkId)
        {
            if (!_chunkProtoBreakdown.IsCreated) return 0;
            uint mask = 0;
            int baseOffset = chunkId * _protoBreakdownStride;
            int limit = math.min(_protoBreakdownStride, _renderSignatures.Count);
            for (int p = 0; p < limit; p++)
                if (_chunkProtoBreakdown[baseOffset + p] > 0)
                    mask |= 1u << _renderSignatures[p].layer;
            return mask;
        }

        #endregion

        #region Incremental Instance API

        /// <summary>
        /// Add a single instance to a chunk. Returns global slot index, or -1 if chunk is full.
        /// </summary>
        public int AddInstance(int chunkId, InstanceData instance)
        {
#if DEBUG
            using var _ai = s_markerAddInstance.Auto();
#endif
            if (!_chunkSystemInitialized)
            {
                Debug.LogError("GPUBatcherRenderer: Chunk system not initialized.");
                return -1;
            }
            if (chunkId < 0 || chunkId >= _maxChunks || !_chunkSlotActive[chunkId])
            {
                Debug.LogError($"GPUBatcherRenderer: Invalid or inactive chunk ID {chunkId}");
                return -1;
            }

            var chunk = _chunks[chunkId];
            int poolBaseIdx = chunkId * _maxPoolRefsPerChunk;

            // find pool with free slot - iterate stable, skip -1 holes
            int hint = _chunkFirstFreePoolHint[chunkId];
            int targetPool = -1;
            for (int p = hint; p < chunk.poolCount; p++)
            {
                int candidate = _chunkPools[poolBaseIdx + p];
                if (candidate < 0) continue;
                if (_poolOccupancy[candidate] < k_poolSize)
                {
                    targetPool = candidate;
                    _chunkFirstFreePoolHint[chunkId] = p;
                    break;
                }
            }

            // allocate new pool if needed
            if (targetPool < 0)
            {
                int newPool = AllocateSinglePool();
                if (newPool < 0) return -1;

                InitializeNewPool(newPool, chunkId);
                if (PlacePoolInChunk(chunkId, newPool, ref chunk) < 0)
                {
                    ReleaseUnusedPool(newPool);
                    return -1;
                }

                _chunks[chunkId] = chunk;
                targetPool = newPool;
            }

            int localSlot = GPUBatcherBurstHelpers.FindAndClaimFreeSlot(ref _poolSlotMask, targetPool);
#if DEBUG
            Debug.Assert(localSlot >= 0, "FindAndClaimFreeSlot returned -1 despite occupancy < kPoolSize");
#endif

            int globalSlot = targetPool * k_poolSize + localSlot;

            // pack and stage
#if DEBUG
            Debug.Assert(instance.signatureIndex >= 0 && instance.signatureIndex < _renderSignatures.Count);
#endif
            var s = instance.scale;
            if (_cachedNegativeScaleMode == NegativeScaleMode.ForcePositiveScale && s.x * s.y * s.z < 0f) s.x = -s.x;
            _writeChunkGpuBuffer[0] = new GPUStagedInstanceData
            {
                position = instance.position,
                rotation = instance.rotation.value,
                scale = s,
                sigDensityPacked = GPUStagedInstanceData.Pack(instance.signatureIndex, GPUBatcherBurstHelpers.StableDensityHash(instance.position))
            };
            StageUpload(_writeChunkGpuBuffer, 0, globalSlot, 1);
            _lightProbes?.SampleAndStageSingle(in instance, globalSlot);
            ResetFadeStatesForSlots(globalSlot, 1);

            _poolOccupancy[targetPool]++;
            // advance hint past full pool
            if (_poolOccupancy[targetPool] >= k_poolSize)
                _chunkFirstFreePoolHint[chunkId] = _chunkFirstFreePoolHint[chunkId] + 1;
            _slotProtoIndex[globalSlot] = instance.signatureIndex;
            IncrementProtoInstanceCount(chunkId, instance.signatureIndex, globalSlot, s.x * s.y * s.z < 0f);

            // expand bounds
            Bounds newBounds = ExpandBoundsForInstance(chunk.bounds, in instance, in _protoBoundsRadius, chunk.instanceCount == 0);
            chunk.instanceCount++;
            chunk.bounds = newBounds;
            chunk.isWritten = true;

            UpdateChunkShadowParamsForProto(ref chunk, instance.signatureIndex);

            _chunks[chunkId] = chunk;

        #if DEBUG
            _totalInstanceCount++;
        #endif

            UpdateChunkGPUState(chunkId);
            return globalSlot;
        }


        /// <summary>
        /// Add multiple instances to a chunk. Writes global slot indices to outSlotIndices (-1 if full).
        /// </summary>
        public void AddInstances(int chunkId, NativeArray<InstanceData> instances, int count, NativeArray<int> outSlotIndices)
        {
#if DEBUG
            using var _ais = s_markerAddInstances.Auto();
            Debug.Assert(count >= 0 && count <= instances.Length);
            Debug.Assert(outSlotIndices.Length >= count);
#endif
            if (!_chunkSystemInitialized)
            {
                Debug.LogError("GPUBatcherRenderer: Chunk system not initialized.");
                for (int i = 0; i < count; i++) outSlotIndices[i] = -1;
                return;
            }
            if (chunkId < 0 || chunkId >= _maxChunks || !_chunkSlotActive[chunkId])
            {
                Debug.LogError($"GPUBatcherRenderer: Invalid or inactive chunk ID {chunkId}");
                for (int i = 0; i < count; i++) outSlotIndices[i] = -1;
                return;
            }

            var chunk = _chunks[chunkId];
            int poolBaseIdx = chunkId * _maxPoolRefsPerChunk;
            Bounds bounds = chunk.bounds;
            int addedCount = 0;

            // contiguous ranges within same pool for batched staging
            int batchPool = -1;
            int batchStartSlot = -1;
            int batchCount = 0;

            for (int i = 0; i < count; i++)
            {
                var inst = instances[i];

                // find pool with free slot - iterate stable, skip -1 holes
                int hintP = _chunkFirstFreePoolHint[chunkId];
                int targetPool = -1;
                for (int p = hintP; p < chunk.poolCount; p++)
                {
                    int candidate = _chunkPools[poolBaseIdx + p];
                    if (candidate < 0) continue;
                    if (_poolOccupancy[candidate] < k_poolSize)
                    {
                        targetPool = candidate;
                        _chunkFirstFreePoolHint[chunkId] = p;
                        break;
                    }
                }

                // need new pool
                if (targetPool < 0)
                {
                    // flush pending batch before pool allocation
                    if (batchCount > 0)
                    {
                        StageUpload(_writeChunkGpuBuffer, 0, batchStartSlot, batchCount);
                        ResetFadeStatesForSlots(batchStartSlot, batchCount);
                        batchCount = 0;
                        batchPool = -1;
                    }

                    int newPool = AllocateSinglePool();
                    if (newPool < 0) { outSlotIndices[i] = -1; continue; }

                    InitializeNewPool(newPool, chunkId);
                    if (PlacePoolInChunk(chunkId, newPool, ref chunk) < 0)
                    {
                        ReleaseUnusedPool(newPool);
                        outSlotIndices[i] = -1;
                        continue;
                    }

                    targetPool = newPool;
                }

                // find free slot via bitmask
                int localSlot = GPUBatcherBurstHelpers.FindAndClaimFreeSlot(ref _poolSlotMask, targetPool);
#if DEBUG
                Debug.Assert(localSlot >= 0, "FindAndClaimFreeSlot returned -1 despite occupancy < kPoolSize");
#endif

                int globalSlot = targetPool * k_poolSize + localSlot;
                outSlotIndices[i] = globalSlot;

                // pack GPU data
                var s = inst.scale;
                if (_cachedNegativeScaleMode == NegativeScaleMode.ForcePositiveScale && s.x * s.y * s.z < 0f) s.x = -s.x;
                var gpuData = new GPUStagedInstanceData
                {
                    position = inst.position,
                    rotation = inst.rotation.value,
                    scale = s,
                    sigDensityPacked = GPUStagedInstanceData.Pack(inst.signatureIndex, GPUBatcherBurstHelpers.StableDensityHash(inst.position))
                };

                // try to extend current batch
                if (targetPool == batchPool && globalSlot == batchStartSlot + batchCount)
                {
                    _writeChunkGpuBuffer[batchCount] = gpuData;
                    batchCount++;
                }
                else
                {
                    // flush previous batch
                    if (batchCount > 0)
                    {
                        StageUpload(_writeChunkGpuBuffer, 0, batchStartSlot, batchCount);
                        ResetFadeStatesForSlots(batchStartSlot, batchCount);
                    }
                    // start new batch
                    _writeChunkGpuBuffer[0] = gpuData;
                    batchPool = targetPool;
                    batchStartSlot = globalSlot;
                    batchCount = 1;
                }

                _lightProbes?.SampleAndStageSingle(in inst, globalSlot);

                // update tracking
                _poolOccupancy[targetPool]++;
                if (_poolOccupancy[targetPool] >= k_poolSize)
                    _chunkFirstFreePoolHint[chunkId] = _chunkFirstFreePoolHint[chunkId] + 1;
                _slotProtoIndex[globalSlot] = inst.signatureIndex;
                IncrementProtoInstanceCount(chunkId, inst.signatureIndex, globalSlot, inst.scale.x * inst.scale.y * inst.scale.z < 0f);
                UpdateChunkShadowParamsForProto(ref chunk, inst.signatureIndex);

                bounds = ExpandBoundsForInstance(bounds, in inst, in _protoBoundsRadius, chunk.instanceCount == 0 && addedCount == 0);
                addedCount++;
            }

            // flush final batch
            if (batchCount > 0)
            {
                StageUpload(_writeChunkGpuBuffer, 0, batchStartSlot, batchCount);
                ResetFadeStatesForSlots(batchStartSlot, batchCount);
            }

            if (addedCount > 0)
            {
                chunk.instanceCount += addedCount;
                chunk.bounds = bounds;
                chunk.isWritten = true;
                _chunks[chunkId] = chunk;

            #if DEBUG
                _totalInstanceCount += addedCount;
            #endif

                UpdateChunkGPUState(chunkId);
            }
        }

        /// <summary>
        /// Append instances using only new pools. Returns number placed.
        /// </summary>
        public int AppendInstances(int chunkId, NativeArray<InstanceData> instances, int srcOffset, int count, NativeArray<int> outSlotIndices)
        {
            if (!_chunkSystemInitialized)
            {
                for (int i = 0; i < count; i++) outSlotIndices[i] = -1;
                return 0;
            }
            if (chunkId < 0 || chunkId >= _maxChunks || !_chunkSlotActive[chunkId])
            {
                for (int i = 0; i < count; i++) outSlotIndices[i] = -1;
                return 0;
            }
            if (count <= 0) return 0;

            var chunk = _chunks[chunkId];
            int poolBaseIdx = chunkId * _maxPoolRefsPerChunk;
            Bounds bounds = chunk.bounds;
            int placed = 0;

            _lightProbes?.BeginBatchSample(instances, srcOffset, math.min(count, _maxPoolRefsPerChunk * k_poolSize));

            while (placed < count)
            {
                int newPool = AllocateSinglePool();
                if (newPool < 0) break;

                _poolToChunk[newPool] = chunkId;
                if (PlacePoolInChunk(chunkId, newPool, ref chunk) < 0)
                {
                    ReleaseUnusedPool(newPool);
                    break;
                }

                int slotBase = newPool * k_poolSize;
                int slotsToFill = math.min(count - placed, k_poolSize);

                // pack GPU data via Burst job
                new PackInstanceDataJob
                {
                    instances = instances,
                    srcOffset = srcOffset + placed,
                    forcePositiveScale = _cachedNegativeScaleMode == NegativeScaleMode.ForcePositiveScale,
                    gpuBuffer = _writeChunkGpuBuffer
                }.Run(slotsToFill);
                StageUpload(_writeChunkGpuBuffer, 0, slotBase, slotsToFill);
                _lightProbes?.StageBatchRange(placed, slotsToFill, slotBase);
                ResetFadeStatesForSlots(slotBase, slotsToFill);

                // set occupancy and bitmasks
                _poolOccupancy[newPool] = slotsToFill;
                SetPoolSlotMask(newPool, slotsToFill);

                // track slot -> proto mapping
                for (int s = 0; s < slotsToFill; s++)
                {
                    int globalSlot = slotBase + s;
                    outSlotIndices[placed + s] = globalSlot;
                    var inst = instances[srcOffset + placed + s];
                    int pi = inst.signatureIndex;
                    _slotProtoIndex[globalSlot] = pi;
                    IncrementProtoInstanceCount(chunkId, pi, globalSlot, inst.scale.x * inst.scale.y * inst.scale.z < 0f);

                    bounds = ExpandBoundsForInstance(bounds, in inst, in _protoBoundsRadius, chunk.instanceCount == 0 && placed == 0 && s == 0);

                    UpdateChunkShadowParamsForProto(ref chunk, pi);
                }

                // clear trailing empty slots
                for (int s = slotsToFill; s < k_poolSize; s++)
                    _slotProtoIndex[slotBase + s] = -1;
                if (slotsToFill < k_poolSize)
                    StageClear(slotBase + slotsToFill, k_poolSize - slotsToFill);

                placed += slotsToFill;
            }

            // fill remaining outputs with -1
            for (int i = placed; i < count; i++)
                outSlotIndices[i] = -1;

            if (placed > 0)
            {
                chunk.instanceCount += placed;
                chunk.bounds = bounds;
                chunk.isWritten = true;
                _chunks[chunkId] = chunk;

            #if DEBUG
                _totalInstanceCount += placed;
            #endif

                UpdateChunkGPUState(chunkId);
            }

            return placed;
        }

        /// <summary>
        /// Remove a single instance by global slot index. Bounds are not recalculated.
        /// </summary>
        public void RemoveInstance(int chunkId, int globalSlotIndex)
        {
#if DEBUG
            using var _ri = s_markerRemoveInstance.Auto();
#endif
#if UNITY_EDITOR
            pickingEntries.Remove(globalSlotIndex);
#endif
            if (!_chunkSystemInitialized)
            {
                Debug.LogError("GPUBatcherRenderer: Chunk system not initialized.");
                return;
            }
            if (chunkId < 0 || chunkId >= _maxChunks || !_chunkSlotActive[chunkId])
            {
                Debug.LogError($"GPUBatcherRenderer: Invalid or inactive chunk ID {chunkId}");
                return;
            }
            if (globalSlotIndex < 0 || globalSlotIndex >= _poolCapacity * k_poolSize)
                return;

            int poolIdx = globalSlotIndex / k_poolSize;
            int localSlot = globalSlotIndex % k_poolSize;

            // ownership check via reverse map
            if (_poolToChunk[poolIdx] != chunkId)
            {
                Debug.LogError($"GPUBatcherRenderer: Pool {poolIdx} does not belong to chunk {chunkId}");
                return;
            }

            // check slot is occupied
            int bit = localSlot;
            if ((_poolSlotMask[poolIdx] & (1UL << bit)) == 0)
            {
                Debug.LogWarning($"GPUBatcherRenderer: Slot {globalSlotIndex} is already empty");
                return;
            }

            // pending direct patches/colors/overrides must not outlive the slot -
            // flush them onto the dying instance before the slot can be reused
            if (_poolRmwStagedFrame[poolIdx] == _renderCycleCounter
                && (_trsPatchWriteCursor != 0 || _colorStagingWriteCursor != 0 || _sigOverrideStagingWriteCursor != 0))
            {
                FlushInstanceUploads();
                FlushTRSPatchUploads();
                FlushColorUploads();
                FlushSigOverrideUploads();
            }

            // a flushed patch marks the dying slot for recompute; a same-frame reuse
            // must not double-consume the new occupant's position
            if (_slotNeedsMatrix.IsCreated)
                _slotNeedsMatrix[globalSlotIndex] = false;

            SplitUploadsAndClear(globalSlotIndex, 1);
            _lightProbes?.ClearSlots(globalSlotIndex, 1);
            ResetFadeStatesForSlots(globalSlotIndex, 1);

            _poolSlotMask[poolIdx] &= ~(1UL << bit);
            _poolOccupancy[poolIdx]--;
            // reset hint
            _chunkFirstFreePoolHint[chunkId] = 0;

            int encoded = _slotProtoIndex[globalSlotIndex];
            int protoIdx = encoded <= -2 ? -(encoded + 2) : encoded; // decode disabled slots
            _slotProtoIndex[globalSlotIndex] = -1;
            DecrementProtoInstanceCount(chunkId, protoIdx, globalSlotIndex);

            var rmCd = _chunks[chunkId];
            rmCd.instanceCount--;
            _chunks[chunkId] = rmCd;

        #if DEBUG
            _totalInstanceCount--;
        #endif

            if (_poolOccupancy[poolIdx] == 0)
                FreePoolFromChunk(chunkId, poolIdx);
            else
                UpdateChunkGPUState(chunkId);
        }

        /// <summary>
        /// Enable or disable a single instance. Disabled instances are invisible to culling
        /// but keep their slot, TRS data, and pool allocation. Re-enabling restores visibility.
        /// Use <see cref="GPUBatcherRegistrationTracker"/> to coalesce repeated enable/disable changes.
        /// </summary>
        public void SetInstanceEnabled(int globalSlotIndex, bool enabled)
        {
            if (!_chunkSystemInitialized) return;
            int totalSlots = _poolCapacity * k_poolSize;
            if (globalSlotIndex < 0 || globalSlotIndex >= totalSlots) return;

            int encoded = _slotProtoIndex[globalSlotIndex];

            if (enabled)
            {
                // already enabled or empty
                if (encoded >= 0 || encoded == -1) return;

                int realProto = -(encoded + 2);
                _slotProtoIndex[globalSlotIndex] = realProto;
                StageSigOverride(globalSlotIndex, realProto);
            }
            else
            {
                // already disabled or empty
                if (encoded < 0) return;

                _slotProtoIndex[globalSlotIndex] = -(encoded + 2);
                StageSigOverride(globalSlotIndex, -1);
            }
        }

        // returns true if the slot is occupied and enabled (positive _slotProtoIndex)
        public bool IsInstanceEnabled(int globalSlotIndex)
        {
            if (!_chunkSystemInitialized) return false;
            int totalSlots = _poolCapacity * k_poolSize;
            if (globalSlotIndex < 0 || globalSlotIndex >= totalSlots) return false;
            return _slotProtoIndex[globalSlotIndex] >= 0;
        }

        // free a single pool via sentinel write; position retained for InstanceLink stability
        private void FreePoolFromChunk(int chunkId, int poolIdx)
        {
            var chunk = _chunks[chunkId];
            int poolBaseIdx = chunkId * _maxPoolRefsPerChunk;

            int freedPos = -1;
            for (int p = 0; p < chunk.poolCount; p++)
            {
                if (_chunkPools[poolBaseIdx + p] == poolIdx) { _chunkPools[poolBaseIdx + p] = -1; freedPos = p; break; }
            }
            if (freedPos < 0) return;

            // staged data invalidation deferred to allocation time via _poolStagedFrame
            if (_poolInUse[poolIdx])
            {
                _poolInUse[poolIdx] = false;
                _allocatedPoolCount--;
                _poolToChunk[poolIdx] = -1;
                ReturnPoolToFreeList(poolIdx);
            }

            if (freedPos < _chunkFirstFreePoolHint[chunkId])
                _chunkFirstFreePoolHint[chunkId] = freedPos;
            _poolSlotMask[poolIdx] = 0;

            if (_chunkHasAnimatedCrossfade.IsCreated && _chunkHasAnimatedCrossfade[chunkId])
                _animatedPoolListDirty = true;

            StagePoolRefUpload(poolBaseIdx + freedPos, 1);
            _chunks[chunkId] = chunk;

            UpdateChunkGPUState(chunkId);
            _poolCompactionDirty = true;
        }

        /// <summary>
        /// Update chunk bounds. Use after removals have made bounds too loose.
        /// </summary>
        public void RecalculateChunkBounds(int chunkId, Bounds newBounds)
        {
            if (!_chunkSystemInitialized) return;
            if (chunkId < 0 || chunkId >= _maxChunks || !_chunkSlotActive[chunkId])
            {
                Debug.LogError($"GPUBatcherRenderer: Invalid or inactive chunk ID {chunkId}");
                return;
            }

            var rcCd = _chunks[chunkId];
            rcCd.bounds = newBounds;
            _chunks[chunkId] = rcCd;

            UpdateChunkGPUState(chunkId);
        }

        /// <summary>
        /// Update a chunk's cull distances at runtime. cullDistance is the CPU chunk-level cull,
        /// instanceCullDistance is the per-instance GPU cull. Pass float.MaxValue to disable either.
        /// </summary>
        public void SetChunkCullDistances(int chunkId, float cullDistance, float instanceCullDistance)
        {
            if (!_chunkSystemInitialized) return;
            if (chunkId < 0 || chunkId >= _maxChunks || !_chunkSlotActive[chunkId])
            {
                Debug.LogError($"GPUBatcherRenderer: Invalid or inactive chunk ID {chunkId}");
                return;
            }

            var cd = _chunks[chunkId];
            cd.cullDistanceSq = cullDistance * cullDistance;
            cd.instanceCullDistanceSq = instanceCullDistance >= float.MaxValue ? float.MaxValue : instanceCullDistance * instanceCullDistance;
            _chunks[chunkId] = cd;

            UpdateChunkGPUState(chunkId);
        }

        /// <summary>
        /// Enable or disable a chunk for rendering. Disabled chunks are culled on CPU before any GPU work.
        /// </summary>
        public void SetChunkEnabled(int chunkId, bool enabled)
        {
            if (!_chunkSystemInitialized) return;
            if (chunkId < 0 || chunkId >= _maxChunks || !_chunkSlotActive[chunkId])
            {
                Debug.LogError($"GPUBatcherRenderer: Invalid or inactive chunk ID {chunkId}");
                return;
            }
            _chunkEnabled[chunkId] = enabled;
            _shadowChunksDirty = true;
        }

        public bool GetChunkEnabled(int chunkId)
        {
            if (!_chunkSystemInitialized) return false;
            if (chunkId < 0 || chunkId >= _maxChunks || !_chunkSlotActive[chunkId]) return false;
            return _chunkEnabled[chunkId];
        }

        #endregion

    }
}
