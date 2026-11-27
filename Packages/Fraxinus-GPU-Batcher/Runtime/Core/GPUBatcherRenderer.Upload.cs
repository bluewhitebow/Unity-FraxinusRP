// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
#if DEBUG
using Unity.Profiling;
#endif

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Upload / staging pipeline: GPU buffer copies, compute dispatch wrappers
    /// for GPUBatcher_Upload.compute, deferred disposal, and flush orchestration.
    /// </summary>
    public partial class GPUBatcherRenderer
    {
        // Linear upload kernels share THREADS_INSTANCES. Query the compiled width at startup.
        private int _uploadThreads = 256;

        private int UploadGroupCount(int count) =>
            count > 0 ? 1 + (count - 1) / _uploadThreads : 0;

        #region Upload Fields

        // staging capacities - fixed size, flush on full, never resize
        private const int k_chunkStagingCapacity = 256; // diag: 2
        private const int k_poolRefStagingCapacity = 4096; // diag: 8
        private const int k_instanceStagingCapacity = 65536; // diag: 256
        private const int k_trsPatchStagingCapacity = 8192; // diag: 128
        private const int k_colorStagingCapacity = 1024; // diag: 128
        private const int k_sigOverrideStagingCapacity = 512; // diag: 32


        // mid-frame overflow flush counters (reset each frame in FlushStagedUploads)
        public int statInstanceOverflowFlushes { get; private set; }
        public int statPoolRefOverflowFlushes { get; private set; }
        public int statTRSPatchOverflowFlushes { get; private set; }
        public int statChunkOverflowFlushes { get; private set; }
        public int statColorOverflowFlushes { get; private set; }

        // staged GPU uploads: CPU array -> single SetData -> GPU copy -> live buffer
        private NativeArray<GPUStagedInstanceData> _stagingCpuArray; // CPU-side accumulator
        private GraphicsBuffer _stagingInstanceBuffer;               // GPU buffer - written once per flush
        private int _stagingCapacity;
        private int _stagingWriteCursor;
        private bool _stagingDirty;

        internal struct StagedUpload
        {
            public int stagingOffset;   // start index in staging buffer
            public int liveOffset;      // start index in live buffer
            public int count;
        }

        private NativeList<StagedUpload> _stagedUploads;
        private NativeList<StagedUpload> _preFlushedUploads; // overflow: uploads already dispatched, pending matrix compute
        private NativeList<StagedUpload> _stagedClears;

        // patch-based move staging: GPU read-modify-write for changed TRS components
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct GPUTRSPatchEntry
        {
            public uint slotIndex;   // 4
            public uint flags;       // 4  (bit 0=position, bit 1=rotation, bit 2=scale)
            public float3 position;  // 12
            public float4 rotation;  // 16
            public float3 scale;     // 12
        }                            // total = 48

        private NativeArray<GPUTRSPatchEntry> _trsPatchEntries;       // fixed-size CPU staging array
        private NativeArray<int> _trsPatchSlotIndices;              // parallel compact slot index array for flush bookkeeping
        private int _trsPatchWriteCursor;
        private GraphicsBuffer _trsPatchStagingBuffer;
        private int _trsPatchStagingCapacity;
        private NativeList<StagedUpload> _stagedFadeResets;
        private NativeArray<int> _poolRefStagingCpuArray;           // compact CPU-side pool ref accumulator
        private int _poolRefStagingWriteCursor;
        private int _poolRefStagingCapacity;                       // fixed capacity of staging array + GPU buffer
        private GraphicsBuffer _stagingPoolRefBuffer;              // GPU staging buffer
        private NativeList<StagedUpload> _stagedPoolRefUploads;
        private NativeHashMap<int, int> _poolRefSlotToCursor;      // liveOffset -> cursor for dedup

        // staged chunk metadata (fixed-size, flush-on-full)
        private NativeArray<int> _stagedChunkIndices;
        private NativeArray<GPUChunk> _stagedChunkData;
        private int _chunkStagingWriteCursor;
        private NativeHashMap<int, int> _chunkSlotToCursor;  // chunkId -> cursor index for dedup
        private GraphicsBuffer _stagingChunkBuffer;
        private GraphicsBuffer _stagingChunkIndexBuffer;

        // batched dispatch command buffer
        private GraphicsBuffer _batchCmdBuffer;
        private int _batchCmdCapacity;

        // motion vector settle tracking
        private NativeArray<int> _movedThisFrame;  // flat array indexed by write cursor, max = totalSlots
        private int _movedThisFrameCount;
        private NativeList<int> _settleList;
        private NativeArray<bool> _slotMovedThisFrame;   // flat array indexed by slot
        private NativeArray<bool> _slotNeedsMatrix;      // patched this flush -> holds raw position, must be recomputed
        private NativeList<int> _recomputeScratch;
        private GraphicsBuffer _settleIndexBuffer;

        // cached config values - refreshed once per flush cycle to avoid 400K+ singleton lookups per frame
        private NegativeScaleMode _cachedNegativeScaleMode;

        // cached unsafe pointers - refreshed on init, resize, and flush to avoid per-call GetUnsafePtr overhead
        private unsafe int* _ptrSlotProtoIndex;
        private unsafe bool* _ptrSlotFlipped;
        private unsafe int* _ptrProtoFlipCounts;
        private unsafe int* _ptrProtoFlipCountHWM;
        private unsafe GPUTRSPatchEntry* _ptrTRSPatchEntries;
        private unsafe bool* _ptrSlotMovedThisFrame;
        private unsafe bool* _ptrSlotNeedsMatrix;
        private unsafe int* _ptrTRSPatchSlotIndices;
        private unsafe int* _ptrMovedThisFrame;

#if DEBUG
        // diagnostic: upload history for debugging stale data issues
        public struct DebugUploadRecord
        {
            public int liveOffset;
            public int count;
            public bool isClear; // true = clear, false = upload
            public bool isOverflow; // true = dispatched during overflow flush
        }
        public readonly List<DebugUploadRecord> debugUploadHistory = new();
        public bool debugResizedThisFrame { get; private set; }
        public int debugTRSPatchesDispatchedPreDiag { get; private set; }

#endif

        // deferred resize: writes queued when pool resize is needed
        private struct DeferredChunkWrite
        {
            public int chunkId;         // -1 = tombstoned
            public int generation;      // _chunkGeneration at queue time
            public int instanceOffset;  // into _deferredInstances
            public int instanceCount;
            public Bounds bounds;
            public float cullDistance;
            public float instanceCullDistance;
        }
        private NativeList<DeferredChunkWrite> _deferredChunkWrites;
        private NativeList<InstanceData> _deferredInstances;
        private bool _resizeDeferred;

        // one-shot event, cleared after invocation
        public event Action onDeferredWritesCompleted;

        // per-instance color staging (only allocated when _perInstanceColorEnabled)
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct GPUColorEntry
        {
            public uint slotIndex;
            public uint packedColor;
        }

        private NativeArray<GPUColorEntry> _colorStagingCpuArray;
        private int _colorStagingWriteCursor;
        private GraphicsBuffer _colorStagingBuffer;

        // sig override scatter write (enable/disable instances)
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct GPUSigOverrideEntry
        {
            public uint slotIndex;
            public int sigIdx;
        }
        private NativeArray<GPUSigOverrideEntry> _sigOverrideStagingCpuArray;
        private int _sigOverrideStagingWriteCursor;
        private GraphicsBuffer _sigOverrideStagingBuffer;

        // animated crossfade pool list (rebuilt when membership changes)
        private NativeList<int> _animatedCrossfadePools;
        private GraphicsBuffer _animatedPoolsBuffer;
        private int _animatedPoolsBufferCapacity;
        private bool _animatedPoolListDirty = true;

        // per-prototype crossfade cycle state (PerPrototype mode)
        private GraphicsBuffer _protoCrossfadeBuffer;
        private GPUProtoCrossfadeState[] _protoCrossfadeStates;
        private int[] _protoCycleIndex;
        private bool _anyPrototypeBoundary;  // true if any prototype has hit a cycle boundary (sticky until all views consume)

        // upload shader property IDs
        private static readonly int s_idCopySrc = Shader.PropertyToID("_CopySrc");
        private static readonly int s_idCopyDst = Shader.PropertyToID("_CopyDst");
        private static readonly int s_idCopySrcOffsetBytes = Shader.PropertyToID("_CopySrcOffsetBytes");
        private static readonly int s_idCopyDstOffsetBytes = Shader.PropertyToID("_CopyDstOffsetBytes");
        private static readonly int s_idCopyCountUint4 = Shader.PropertyToID("_CopyCountUint4");
        private static readonly int s_idCopyTailUints = Shader.PropertyToID("_CopyTailUints");
        private static readonly int s_idTrsTotalSlots = Shader.PropertyToID("_TRSTotalSlots");
        private static readonly int s_idTrsPrevMMode = Shader.PropertyToID("_TRSPrevMMode");
        private static readonly int s_idTrsBatchCount = Shader.PropertyToID("_TRSBatchCount");
        private static readonly int s_idUploadSrcOffset = Shader.PropertyToID("_UploadSrcOffset");
        private static readonly int s_idUploadDstOffset = Shader.PropertyToID("_UploadDstOffset");
        private static readonly int s_idUploadCount = Shader.PropertyToID("_UploadCount");
        private static readonly int s_idUploadInstanceDst = Shader.PropertyToID("_UploadInstanceDst");
        private static readonly int s_idUploadIntSrc = Shader.PropertyToID("_UploadIntSrc");
        private static readonly int s_idUploadIntDst = Shader.PropertyToID("_UploadIntDst");
        private static readonly int s_idClearInstanceDst = Shader.PropertyToID("_ClearInstanceDst");
        private static readonly int s_idInitFadeDst = Shader.PropertyToID("_InitFadeDst");
        private static readonly int s_idInitFadeDstOffset = Shader.PropertyToID("_InitFadeDstOffset");
        private static readonly int s_idInitFadeCount = Shader.PropertyToID("_InitFadeCount");
        private static readonly int s_idCopyChunkSrc = Shader.PropertyToID("_CopyChunkSrc");
        private static readonly int s_idCopyChunkDst = Shader.PropertyToID("_CopyChunkDst");
        private static readonly int s_idCopyFadeSrc = Shader.PropertyToID("_CopyFadeSrc");
        private static readonly int s_idCopyFadeDst = Shader.PropertyToID("_CopyFadeDst");
        private static readonly int s_idChunkStagingSrc = Shader.PropertyToID("_ChunkStagingSrc");
        private static readonly int s_idChunkStagingIndices = Shader.PropertyToID("_ChunkStagingIndices");
        private static readonly int s_idChunkScatterDst = Shader.PropertyToID("_ChunkScatterDst");
        private static readonly int s_idChunkScatterCount = Shader.PropertyToID("_ChunkScatterCount");
        private static readonly int s_idSettleSlotIndices = Shader.PropertyToID("_SettleSlotIndices");
        private static readonly int s_idSettleCount = Shader.PropertyToID("_SettleCount");
        private static readonly int s_idSettlePrevMBaseBytes = Shader.PropertyToID("_SettlePrevMBaseBytes");
        private static readonly int s_idBatchCmds = Shader.PropertyToID("_BatchCmds");
        private static readonly int s_idBatchCmdCount = Shader.PropertyToID("_BatchCmdCount");
        private static readonly int s_idTrsPatchSrc = Shader.PropertyToID("_TRSPatchSrc");
        private static readonly int s_idTrsPatchCount = Shader.PropertyToID("_TRSPatchCount");
        private static readonly int s_idSigOverrideSrc = Shader.PropertyToID("_SigOverrideSrc");
        private static readonly int s_idSigOverrideCount = Shader.PropertyToID("_SigOverrideCount");

        // split buffer property IDs
        private static readonly int s_idUploadStagedSrc = Shader.PropertyToID("_UploadStagedSrc");
        private static readonly int s_idUploadRotScaleDst = Shader.PropertyToID("_UploadRotScaleDst");
        private static readonly int s_idClearRotScaleDst = Shader.PropertyToID("_ClearRotScaleDst");
        private static readonly int s_idInstanceRotScale = Shader.PropertyToID("_InstanceRotScale");
        private static readonly int s_idInstancesRW = Shader.PropertyToID("_InstancesRW");
        private static readonly int s_idCopyInstanceSrc = Shader.PropertyToID("_CopyInstanceSrc");
        private static readonly int s_idCopyInstanceDst = Shader.PropertyToID("_CopyInstanceDst");
        private static readonly int s_idCopyRotScaleSrc = Shader.PropertyToID("_CopyRotScaleSrc");
        private static readonly int s_idCopyRotScaleDst = Shader.PropertyToID("_CopyRotScaleDst");

        private static readonly int s_idShiftOffset = Shader.PropertyToID("_ShiftOffset");
        private static readonly int s_idShiftCount = Shader.PropertyToID("_ShiftCount");
        private static readonly int s_idShiftTotalSlots = Shader.PropertyToID("_ShiftTotalSlots");

        #endregion

        #region Cached Pointer Refresh

        // call after init, resize, or any operation that reallocates NativeArrays
        private unsafe void RefreshCachedPointers()
        {
            _ptrSlotProtoIndex = _slotProtoIndex.IsCreated ? (int*)_slotProtoIndex.GetUnsafeReadOnlyPtr() : null;
            _ptrSlotFlipped = _slotFlipped.IsCreated ? (bool*)_slotFlipped.GetUnsafePtr() : null;
            _ptrProtoFlipCounts = _protoFlipCounts.IsCreated ? (int*)_protoFlipCounts.GetUnsafePtr() : null;
            _ptrProtoFlipCountHWM = _protoFlipCountHWM.IsCreated ? (int*)_protoFlipCountHWM.GetUnsafePtr() : null;
            _ptrTRSPatchEntries = _trsPatchEntries.IsCreated ? (GPUTRSPatchEntry*)_trsPatchEntries.GetUnsafePtr() : null;
            _ptrSlotMovedThisFrame = _slotMovedThisFrame.IsCreated ? (bool*)_slotMovedThisFrame.GetUnsafePtr() : null;
            _ptrSlotNeedsMatrix = _slotNeedsMatrix.IsCreated ? (bool*)_slotNeedsMatrix.GetUnsafePtr() : null;
            _ptrTRSPatchSlotIndices = _trsPatchSlotIndices.IsCreated ? (int*)_trsPatchSlotIndices.GetUnsafePtr() : null;
            _ptrMovedThisFrame = _movedThisFrame.IsCreated ? (int*)_movedThisFrame.GetUnsafePtr() : null;
        }

        #endregion

        #region GPU Dispatch Helpers

        // GPU-side buffer region copy (both buffers must have Target.Raw)
        private void GPUCopyBufferRegion(GraphicsBuffer src, int srcOffsetBytes,
                                          GraphicsBuffer dst, int dstOffsetBytes,
                                          int sizeBytes)
        {
            if (_copyBufferRegionKernel < 0 || _uploadShader == null || sizeBytes <= 0)
                return;

            int countUint4 = sizeBytes / 16;
            int tailBytes = sizeBytes - countUint4 * 16;
            int tailUints = tailBytes / 4;
            int totalThreads = countUint4 + tailUints;
            if (totalThreads <= 0) return;

            _uploadShader.SetBuffer(_copyBufferRegionKernel, s_idCopySrc, src);
            _uploadShader.SetBuffer(_copyBufferRegionKernel, s_idCopyDst, dst);
            _uploadShader.SetInt(s_idCopySrcOffsetBytes, srcOffsetBytes);
            _uploadShader.SetInt(s_idCopyDstOffsetBytes, dstOffsetBytes);
            _uploadShader.SetInt(s_idCopyCountUint4, countUint4);
            _uploadShader.SetInt(s_idCopyTailUints, tailUints);

            DispatchSafe(_uploadShader, _copyBufferRegionKernel, UploadGroupCount(totalThreads));
        }

        // _TRSPrevMMode: how the matrix kernel updates PrevM. snapshotting is NOT done here - the settle kernel solely owns PrevM
        const int k_prevMPreserve = 0; // compute O2W, leave PrevM untouched (moved slot - PrevM already snapshotted at patch time)
        const int k_prevMInit = 1;     // PrevM = new O2W (new instance -> zero motion this frame)

        // batched matrix compute (prevMMode-driven) for scattered slot indices; returns false if it could not dispatch
        private bool GPUComputeMatricesFromTRSBatched(NativeArray<int> slots, int count, int prevMMode)
        {
#if DEBUG
            using var _gm = s_markerGPUComputeMatrices.Auto();
#endif
            if (_computeMatricesBatchedKernel < 0 || _uploadShader == null || count <= 0
                || _instanceBuffer == null || _instanceRotScaleBuffer == null
                || _gpuSignatureBuffer == null || _matrixBuffer == null)
                return false;

            UploadSettleSlotIndices(slots, count);

            int totalInstanceSlots = _poolCapacity * k_poolSize;
            _uploadShader.SetBuffer(_computeMatricesBatchedKernel, s_idInstancesRW, _instanceBuffer);
            _uploadShader.SetBuffer(_computeMatricesBatchedKernel, s_idInstanceRotScale, _instanceRotScaleBuffer);
            _uploadShader.SetBuffer(_computeMatricesBatchedKernel, s_idSignatures, _gpuSignatureBuffer);
            _uploadShader.SetBuffer(_computeMatricesBatchedKernel, s_idCopyDst, _matrixBuffer);
            _uploadShader.SetBuffer(_computeMatricesBatchedKernel, s_idSettleSlotIndices, _settleIndexBuffer);
            _uploadShader.SetInt(s_idTrsBatchCount, count);
            _uploadShader.SetInt(s_idTrsTotalSlots, totalInstanceSlots);
            _uploadShader.SetInt(s_idTrsPrevMMode, prevMMode);

            DispatchSafe(_uploadShader, _computeMatricesBatchedKernel, UploadGroupCount(count));
            return true;
        }

        // typed upload dispatches
        private void DispatchTypedCopy(int kernel, int srcBufId, GraphicsBuffer src,
            int dstBufId, GraphicsBuffer dst, int srcOffset, int dstOffset, int count)
        {
            if (kernel < 0 || _uploadShader == null || count <= 0) return;
            _uploadShader.SetBuffer(kernel, srcBufId, src);
            _uploadShader.SetBuffer(kernel, dstBufId, dst);
            _uploadShader.SetInt(s_idUploadSrcOffset, srcOffset);
            _uploadShader.SetInt(s_idUploadDstOffset, dstOffset);
            _uploadShader.SetInt(s_idUploadCount, count);
            DispatchSafe(_uploadShader, kernel, UploadGroupCount(count));
        }

        private void DispatchTypedInit(int kernel, int dstBufId, GraphicsBuffer dst,
            int offsetId, int offset, int countId, int count)
        {
            if (kernel < 0 || _uploadShader == null || count <= 0) return;
            _uploadShader.SetBuffer(kernel, dstBufId, dst);
            _uploadShader.SetInt(offsetId, offset);
            _uploadShader.SetInt(countId, count);
            DispatchSafe(_uploadShader, kernel, UploadGroupCount(count));
        }

        // copy 20B InstanceInput between buffers
        private void GPUCopyInstanceBuffer(GraphicsBuffer src, int srcOffset, GraphicsBuffer dst, int dstOffset, int count)
            => DispatchTypedCopy(_copyInstanceBufferKernel, s_idCopyInstanceSrc, src, s_idCopyInstanceDst, dst, srcOffset, dstOffset, count);

        // copy 28B InstanceRotationScale between buffers
        private void GPUCopyRotScaleBuffer(GraphicsBuffer src, int srcOffset, GraphicsBuffer dst, int dstOffset, int count)
            => DispatchTypedCopy(_copyRotScaleBufferKernel, s_idCopyRotScaleSrc, src, s_idCopyRotScaleDst, dst, srcOffset, dstOffset, count);

        private void GPUCopyChunkRegion(GraphicsBuffer src, GraphicsBuffer dst, int count)
            => DispatchTypedCopy(_copyChunkRegionKernel, s_idCopyChunkSrc, src, s_idCopyChunkDst, dst, 0, 0, count);

        private void GPUCopyFadeStateRegion(GraphicsBuffer src, GraphicsBuffer dst, int count)
            => DispatchTypedCopy(_copyFadeStateRegionKernel, s_idCopyFadeSrc, src, s_idCopyFadeDst, dst, 0, 0, count);

        private void GPUInitFadeStateTyped(GraphicsBuffer dst, int dstOffset, int count)
            => DispatchTypedInit(_initFadeStateTypedKernel, s_idInitFadeDst, dst, s_idInitFadeDstOffset, dstOffset, s_idInitFadeCount, count);

        private void GPUBatchInitFadeStates(GraphicsBuffer cmdBuffer, int cmdCount, GraphicsBuffer fadeDst)
        {
            if (_batchInitFadeStatesKernel < 0 || _uploadShader == null || cmdCount <= 0) return;
            _uploadShader.SetBuffer(_batchInitFadeStatesKernel, s_idBatchCmds, cmdBuffer);
            _uploadShader.SetBuffer(_batchInitFadeStatesKernel, s_idInitFadeDst, fadeDst);
            _uploadShader.SetInt(s_idBatchCmdCount, cmdCount);
            DispatchSafe(_uploadShader, _batchInitFadeStatesKernel, cmdCount);
        }

        private void GPUScatterWriteChunks(GraphicsBuffer stagingSrc, GraphicsBuffer indexBuffer,
            GraphicsBuffer dst, int count)
        {
            if (_scatterWriteChunksKernel < 0 || _uploadShader == null || count <= 0)
                return;

            _uploadShader.SetBuffer(_scatterWriteChunksKernel, s_idChunkStagingSrc, stagingSrc);
            _uploadShader.SetBuffer(_scatterWriteChunksKernel, s_idChunkStagingIndices, indexBuffer);
            _uploadShader.SetBuffer(_scatterWriteChunksKernel, s_idChunkScatterDst, dst);
            _uploadShader.SetInt(s_idChunkScatterCount, count);

            DispatchSafe(_uploadShader, _scatterWriteChunksKernel, UploadGroupCount(count));
        }

        private void GPUBatchUploadInstances(GraphicsBuffer cmdBuffer, int cmdCount,
            GraphicsBuffer stagedSrc, GraphicsBuffer instanceDst)
        {
            if (_batchUploadInstancesKernel < 0 || _uploadShader == null || cmdCount <= 0) return;
            _uploadShader.SetBuffer(_batchUploadInstancesKernel, s_idBatchCmds, cmdBuffer);
            _uploadShader.SetBuffer(_batchUploadInstancesKernel, s_idUploadStagedSrc, stagedSrc);
            _uploadShader.SetBuffer(_batchUploadInstancesKernel, s_idUploadInstanceDst, instanceDst);
            _uploadShader.SetBuffer(_batchUploadInstancesKernel, s_idUploadRotScaleDst, _instanceRotScaleBuffer);
            _uploadShader.SetInt(s_idBatchCmdCount, cmdCount);
            DispatchSafe(_uploadShader, _batchUploadInstancesKernel, cmdCount);
        }

        private void GPUTRSPatchInstances(GraphicsBuffer patchSrc, int patchCount, GraphicsBuffer instanceDst)
        {
            if (_trsPatchInstancesKernel < 0 || _uploadShader == null || patchCount <= 0) return;
            _uploadShader.SetBuffer(_trsPatchInstancesKernel, s_idTrsPatchSrc, patchSrc);
            _uploadShader.SetBuffer(_trsPatchInstancesKernel, s_idUploadInstanceDst, instanceDst);
            _uploadShader.SetBuffer(_trsPatchInstancesKernel, s_idUploadRotScaleDst, _instanceRotScaleBuffer);
            _uploadShader.SetBuffer(_trsPatchInstancesKernel, s_idCopyDst, _matrixBuffer); // OTW position recovery
            _uploadShader.SetInt(s_idTrsPatchCount, patchCount);
            DispatchSafe(_uploadShader, _trsPatchInstancesKernel, UploadGroupCount(patchCount));
        }

        private void GPUBatchComputeMatrices(GraphicsBuffer cmdBuffer, int cmdCount,
            GraphicsBuffer instanceBuffer, GraphicsBuffer matrixBuffer, int totalSlots, int prevMMode)
        {
            if (_batchComputeMatricesKernel < 0 || _uploadShader == null || cmdCount <= 0) return;
            if (_gpuSignatureBuffer == null || _instanceRotScaleBuffer == null || instanceBuffer == null || matrixBuffer == null) return;
            _uploadShader.SetBuffer(_batchComputeMatricesKernel, s_idBatchCmds, cmdBuffer);
            _uploadShader.SetBuffer(_batchComputeMatricesKernel, s_idInstancesRW, instanceBuffer);
            _uploadShader.SetBuffer(_batchComputeMatricesKernel, s_idInstanceRotScale, _instanceRotScaleBuffer);
            _uploadShader.SetBuffer(_batchComputeMatricesKernel, s_idSignatures, _gpuSignatureBuffer);
            _uploadShader.SetBuffer(_batchComputeMatricesKernel, s_idCopyDst, matrixBuffer);
            _uploadShader.SetInt(s_idBatchCmdCount, cmdCount);
            _uploadShader.SetInt(s_idTrsTotalSlots, totalSlots);
            _uploadShader.SetInt(s_idTrsPrevMMode, prevMMode);
            _uploadShader.SetInt(_ColorEnabled, _perInstanceColorEnabled ? 1 : 0);
            if (_perInstanceColorEnabled)
                _uploadShader.SetInt(_ColorRegionOffsetUint, _colorRegionByteOffset / 4);
            DispatchSafe(_uploadShader, _batchComputeMatricesKernel, cmdCount);
        }

        private void GPUBatchClearInstances(GraphicsBuffer cmdBuffer, int cmdCount, GraphicsBuffer dst)
        {
            if (_batchClearInstancesKernel < 0 || _uploadShader == null || cmdCount <= 0) return;
            _uploadShader.SetBuffer(_batchClearInstancesKernel, s_idBatchCmds, cmdBuffer);
            _uploadShader.SetBuffer(_batchClearInstancesKernel, s_idClearInstanceDst, dst);
            _uploadShader.SetBuffer(_batchClearInstancesKernel, s_idClearRotScaleDst, _instanceRotScaleBuffer);
            _uploadShader.SetInt(s_idBatchCmdCount, cmdCount);
            DispatchSafe(_uploadShader, _batchClearInstancesKernel, cmdCount);
        }

        private void GPUBatchUploadInts(GraphicsBuffer cmdBuffer, int cmdCount,
            GraphicsBuffer src, GraphicsBuffer dst)
        {
            if (_batchUploadIntsKernel < 0 || _uploadShader == null || cmdCount <= 0) return;
            _uploadShader.SetBuffer(_batchUploadIntsKernel, s_idBatchCmds, cmdBuffer);
            _uploadShader.SetBuffer(_batchUploadIntsKernel, s_idUploadIntSrc, src);
            _uploadShader.SetBuffer(_batchUploadIntsKernel, s_idUploadIntDst, dst);
            _uploadShader.SetInt(s_idBatchCmdCount, cmdCount);
            DispatchSafe(_uploadShader, _batchUploadIntsKernel, cmdCount);
        }

        private void UploadSettleSlotIndices(NativeArray<int> slots, int count)
        {
            if (_settleIndexBuffer == null || _settleIndexBuffer.count < count)
            {
                _settleIndexBuffer?.Dispose();
                int cap = math.ceilpow2(count);
                _settleIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(int));
                _settleIndexBuffer.name = "FraxinusGPUBatcher_SettleIdx";
            }
            _settleIndexBuffer.SetData(slots, 0, 0, count);
        }

        // copies O2W -> PrevM for the given slots (the sole writer of PrevM: at first-move and at stop-move)
        private void GPUSettleMotionVectors(NativeArray<int> slots, int count)
        {
            if (_settleMotionVectorsKernel < 0 || _uploadShader == null || count <= 0)
                return;

            UploadSettleSlotIndices(slots, count);

            int totalSlots = _poolCapacity * k_poolSize;
            _uploadShader.SetBuffer(_settleMotionVectorsKernel, s_idCopyDst, _matrixBuffer);
            _uploadShader.SetBuffer(_settleMotionVectorsKernel, s_idSettleSlotIndices, _settleIndexBuffer);
            _uploadShader.SetInt(s_idSettleCount, count);
            _uploadShader.SetInt(s_idSettlePrevMBaseBytes, totalSlots * 2 * k_sizeOfPackedMatrix);

            DispatchSafe(_uploadShader, _settleMotionVectorsKernel, UploadGroupCount(count));
        }

        // dispatch upload shader with profiler markers
        private void CmdDispatchUpload(CommandBuffer cmd, string name, int kernel, int groupsX)
        {
#if DEBUG
            cmd.BeginSample(name);
#endif
            DispatchSafe(cmd, _uploadShader, kernel, groupsX);
#if DEBUG
            cmd.EndSample(name);
#endif
        }

        #endregion

        #region Staging Pipeline

        // extend the last entry when the new range continues it, keeps command lists
        // short so batch kernels run full-width groups instead of one group per pool
        private static void AddStagedMerged(NativeList<StagedUpload> list,
            int stagingOffset, int liveOffset, int count, bool matchStaging)
        {
            int n = list.Length;
            if (n > 0)
            {
                var last = list[n - 1];
                if (last.count > 0 && last.liveOffset + last.count == liveOffset
                    && (!matchStaging || last.stagingOffset + last.count == stagingOffset))
                {
                    last.count += count;
                    list[n - 1] = last;
                    return;
                }
            }
            list.Add(new StagedUpload { stagingOffset = stagingOffset, liveOffset = liveOffset, count = count });
        }

        // stage instance data for deferred GPU copy.
        private void StageUpload(
            NativeArray<GPUStagedInstanceData> instanceSrc, int instanceSrcOffset,
            int liveOffset, int count)
        {
            if (count <= 0 || !_stagingCpuArray.IsCreated) return;
            _poolStagedFrame[liveOffset / k_poolSize] = _renderCycleCounter;

            int remaining = count;
            int srcOffset = 0;

            while (remaining > 0)
            {
                int available = _stagingCapacity - _stagingWriteCursor;

                // staging buffer full - flush and reset
                if (available <= 0)
                {
                    statInstanceOverflowFlushes++;

                    // clears must run before uploads - freed+reallocated slots need zeroing
                    FlushPendingClears();

                    if (_stagedUploads.Length > 0)
                    {
                        _stagingInstanceBuffer.SetData(_stagingCpuArray, 0, 0, _stagingWriteCursor);

                        EnsureBatchCmdBuffer(_stagedUploads.Length);
                        _batchCmdBuffer.SetData(_stagedUploads.AsArray(), 0, 0, _stagedUploads.Length);
                        GPUBatchUploadInstances(_batchCmdBuffer, _stagedUploads.Length,
                            _stagingInstanceBuffer, _instanceBuffer);
#if UNITY_EDITOR
                        DebugMirrorApplyUploads(_stagedUploads);
#endif
                        _preFlushedUploads.AddRange(_stagedUploads.AsArray());
                        _stagedUploads.Clear();
                    }

                    _stagingWriteCursor = 0;
                    available = _stagingCapacity;
                }

                int batch = math.min(remaining, available);

                NativeArray<GPUStagedInstanceData>.Copy(instanceSrc, instanceSrcOffset + srcOffset,
                    _stagingCpuArray, _stagingWriteCursor, batch);

                int uploadStart = liveOffset + srcOffset;

                AddStagedMerged(_stagedUploads, _stagingWriteCursor, uploadStart, batch, matchStaging: true);

                _stagingWriteCursor += batch;
                srcOffset += batch;
                remaining -= batch;
            }

            _stagingDirty = true;
        }

        #region Bulk Staging APIs (for tracker-side staging)

        /// <summary>
        /// Bulk-submit TRS patches from a tracker. No per-slot dedup - tracker already merged.
        /// Entries are appended directly to the core staging array.
        /// </summary>
        public unsafe void BulkStageTRSPatches(NativeArray<GPUTRSPatchEntry> entries,
            NativeArray<int> slotIndices, int count, bool skipFlipProcessing = false)
        {
            if (count <= 0) return;

            int stampSlots = _poolCapacity * k_poolSize;
            for (int i = 0; i < count; i++)
            {
                int slot = slotIndices[i];
                if ((uint)slot < (uint)stampSlots)
                {
                    _poolStagedFrame[slot / k_poolSize] = _renderCycleCounter;
                    _poolRmwStagedFrame[slot / k_poolSize] = _renderCycleCounter;
                }
            }

            // process flip winding / ForcePositiveScale for entries with scale flag
            if (!skipFlipProcessing)
            {
                int totalSlots = _poolCapacity * k_poolSize;
                for (int i = 0; i < count; i++)
                {
                    var e = entries[i];
                    int slot = slotIndices[i];
                    if ((e.flags & 4u) != 0 && slot >= 0 && slot < totalSlots)
                    {
                        e.scale = ProcessScaleFlip(slot, e.scale);
                        entries[i] = e;
                    }
                }
            }

            int remaining = count;
            int srcOffset = 0;

            while (remaining > 0)
            {
                int available = _trsPatchStagingCapacity - _trsPatchWriteCursor;

                // staging buffer full - flush and reset
                if (available <= 0)
                {
                    statTRSPatchOverflowFlushes++;
                    FlushInstanceUploads();
                    FlushTRSPatchUploads();
                    available = _trsPatchStagingCapacity;
                }

                int batch = math.min(remaining, available);

                NativeArray<GPUTRSPatchEntry>.Copy(entries, srcOffset, _trsPatchEntries, _trsPatchWriteCursor, batch);
                NativeArray<int>.Copy(slotIndices, srcOffset, _trsPatchSlotIndices, _trsPatchWriteCursor, batch);

                _trsPatchWriteCursor += batch;
                srcOffset += batch;
                remaining -= batch;
            }
        }

        /// <summary>
        /// Bulk-submit color writes from a tracker. No per-slot dedup.
        /// </summary>
        public void BulkStageColorWrites(NativeArray<GPUColorEntry> entries, int count)
        {
            if (count <= 0 || !_perInstanceColorEnabled) return;

            int stampSlots = _poolCapacity * k_poolSize;
            for (int i = 0; i < count; i++)
            {
                int slot = (int)entries[i].slotIndex;
                if ((uint)slot < (uint)stampSlots)
                {
                    _poolStagedFrame[slot / k_poolSize] = _renderCycleCounter;
                    _poolRmwStagedFrame[slot / k_poolSize] = _renderCycleCounter;
                }
            }

            int remaining = count;
            int srcOffset = 0;

            while (remaining > 0)
            {
                int available = k_colorStagingCapacity - _colorStagingWriteCursor;

                if (available <= 0)
                {
                    statColorOverflowFlushes++;
                    FlushInstanceUploads(); // matrix compute resets colors - must run first
                    FlushColorUploads();
                    available = k_colorStagingCapacity;
                }

                int batch = math.min(remaining, available);
                NativeArray<GPUColorEntry>.Copy(entries, srcOffset, _colorStagingCpuArray, _colorStagingWriteCursor, batch);

                _colorStagingWriteCursor += batch;
                srcOffset += batch;
                remaining -= batch;
            }
        }

        #endregion

        private void StageColorWrite(int globalSlot, uint packedColor)
        {
            _poolStagedFrame[globalSlot / k_poolSize] = _renderCycleCounter;
            _poolRmwStagedFrame[globalSlot / k_poolSize] = _renderCycleCounter;

            if (_colorStagingWriteCursor >= k_colorStagingCapacity)
            {
                statColorOverflowFlushes++;
                FlushInstanceUploads(); // matrix compute resets colors - must run first
                FlushColorUploads();
            }

            _colorStagingCpuArray[_colorStagingWriteCursor++] = new GPUColorEntry
            {
                slotIndex = (uint)globalSlot,
                packedColor = packedColor
            };
            _stagingDirty = true;
        }

        /// <summary>Sets per-instance color. No-op if per-instance color is disabled.
        /// Use <see cref="GPUBatcherRegistrationTracker.StageColor(InstanceLink, Color32)"/> to coalesce repeated writes.</summary>
        public void SetInstanceColor(int globalSlotIndex, Color32 color)
        {
            if (!_perInstanceColorEnabled) return;
            StageColorWrite(globalSlotIndex, GPUBatcherBurstHelpers.PackColor(color));
        }

#if UNITY_EDITOR
        // picking registry: globalSlot -> (protoIndex, entityId, transform)
        internal struct PickingEntry
        {
            public int protoIndex;
#if UNITY_6000_2_OR_NEWER
            public EntityId entityId;
#else
            public int entityId;       // renderer instance ID for Unity picking resolution
#endif
            public Transform transform; // for CPU frustum culling (Transform.position)
        }
        internal readonly Dictionary<int, PickingEntry> pickingEntries = new();

        /// <summary>Registers a picking entry for scene view click/drag selection. Editor-only.</summary>
#if UNITY_6000_2_OR_NEWER
        public void SetEntityId(int globalSlotIndex, EntityId entityId, int protoIndex = -1, Transform transform = null)
#else
        public void SetEntityId(int globalSlotIndex, int entityId, int protoIndex = -1, Transform transform = null)
#endif
        {
            pickingEntries[globalSlotIndex] = new PickingEntry
            {
                protoIndex = protoIndex, entityId = entityId, transform = transform
            };
        }
#endif

        // stage pool ref update - deduplicates writes to the same live offset
        private void StagePoolRefUpload(int offset, int count)
        {
            if (count <= 0) return;

            for (int i = 0; i < count; i++)
            {
                int liveOffset = offset + i;
                int value = _chunkPools[liveOffset];

                // dedup: if this offset was already staged, overwrite in-place
                if (_poolRefSlotToCursor.TryGetValue(liveOffset, out int existingCursor))
                {
                    _poolRefStagingCpuArray[existingCursor] = value;
                    continue;
                }

                // overflow: flush staged pool refs, reset cursor
                if (_poolRefStagingWriteCursor >= _poolRefStagingCapacity)
                {
                    statPoolRefOverflowFlushes++;
                    FlushPoolRefUploads();
                }

                int cursor = _poolRefStagingWriteCursor++;
                _poolRefStagingCpuArray[cursor] = value;
                _poolRefSlotToCursor[liveOffset] = cursor;
                AddStagedMerged(_stagedPoolRefUploads, cursor, liveOffset, 1, matchStaging: true);
            }

            _stagingDirty = true;
        }

        private void FlushTRSPatchUploads()
        {
            int count = _trsPatchWriteCursor;
            if (count <= 0) return;

            // sig buffer must exist - TRS patch kernel reads position from OTW matrix
            FlushSignatureUploads();

#if DEBUG
            debugTRSPatchesDispatchedPreDiag += count;
#endif
            _trsPatchStagingBuffer.SetData(_trsPatchEntries, 0, 0, count);
            GPUTRSPatchInstances(_trsPatchStagingBuffer, count, _instanceBuffer);
#if UNITY_EDITOR
            DebugMirrorApplyTRSPatches(_trsPatchEntries, count);
#endif

            // accumulate moved slots; needsMatrix marks slots holding raw position for the recompute.
            // dedup across flushes: only add to _movedThisFrame (and snapshot) on a slot's first move this frame.
            int firstMovedStart = _movedThisFrameCount;
            unsafe
            {
                for (int i = 0; i < count; i++)
                {
                    int slot = _ptrTRSPatchSlotIndices[i];
                    _ptrSlotNeedsMatrix[slot] = true;
                    if (!_ptrSlotMovedThisFrame[slot])
                    {
                        _ptrMovedThisFrame[_movedThisFrameCount++] = slot;
                        _ptrSlotMovedThisFrame[slot] = true;
                    }
                }
            }

            // snapshot frame-start O2W -> PrevM for first-moved slots, before the recompute overwrites O2W (settle owns PrevM)
            int firstMovedCount = _movedThisFrameCount - firstMovedStart;
            if (firstMovedCount > 0)
                GPUSettleMotionVectors(_movedThisFrame.GetSubArray(firstMovedStart, firstMovedCount), firstMovedCount);

            _trsPatchWriteCursor = 0;
        }

        private void FlushColorUploads()
        {
            int count = _colorStagingWriteCursor;
            if (count <= 0) return;

            _colorStagingBuffer.SetData(_colorStagingCpuArray, 0, 0, count);
            GPUScatterWriteColors(_colorStagingBuffer, count);
#if UNITY_EDITOR
            DebugMirrorApplyColors(count);
#endif

            _colorStagingWriteCursor = 0;
        }

        private void GPUScatterWriteColors(GraphicsBuffer colorSrc, int count)
        {
            if (_scatterWriteColorsKernel < 0 || _uploadShader == null || count <= 0) return;
            _uploadShader.SetBuffer(_scatterWriteColorsKernel, _ColorStagingSrc, colorSrc);
            _uploadShader.SetBuffer(_scatterWriteColorsKernel, s_idCopyDst, _matrixBuffer);
            _uploadShader.SetInt(_ColorScatterCount, count);
            _uploadShader.SetInt(_ColorRegionOffsetUint, _colorRegionByteOffset / 4);
            DispatchSafe(_uploadShader, _scatterWriteColorsKernel, UploadGroupCount(count));
        }

        private void FlushChunkMetadataUploads()
        {
#if DEBUG
            using var _cm = s_markerFlushChunkMetadata.Auto();
#endif
            int count = _chunkStagingWriteCursor;
            if (count <= 0) return;

            _stagingChunkBuffer.SetData(_stagedChunkData, 0, 0, count);
            _stagingChunkIndexBuffer.SetData(_stagedChunkIndices, 0, 0, count);
            GPUScatterWriteChunks(_stagingChunkBuffer, _stagingChunkIndexBuffer,
                _chunkBuffer, count);

            _chunkStagingWriteCursor = 0;
            _chunkSlotToCursor.Clear();
        }

        private void FlushPoolRefUploads()
        {
#if DEBUG
            using var _pr = s_markerFlushPoolRefs.Auto();
#endif
            if (_stagedPoolRefUploads.Length <= 0) return;
            int cursor = _poolRefStagingWriteCursor;
            _stagingPoolRefBuffer.SetData(_poolRefStagingCpuArray, 0, 0, cursor);
            EnsureBatchCmdBuffer(_stagedPoolRefUploads.Length);
            _batchCmdBuffer.SetData(_stagedPoolRefUploads.AsArray(), 0, 0, _stagedPoolRefUploads.Length);
            GPUBatchUploadInts(_batchCmdBuffer, _stagedPoolRefUploads.Length,
                _stagingPoolRefBuffer, _poolRefBuffer);
            _stagedPoolRefUploads.Clear();
            _poolRefSlotToCursor.Clear();
            _poolRefStagingWriteCursor = 0;
        }

        // stage a clear - invalidates overlapping staged uploads to prevent scatter races
        private void StageClear(int liveOffset, int count)
        {
            if (count <= 0) return;

            int clearEnd = liveOffset + count;

            // invalidate overlapping staged uploads
            for (int i = _stagedUploads.Length - 1; i >= 0; i--)
            {
                var cmd = _stagedUploads[i];
                if (cmd.count > 0 && cmd.liveOffset < clearEnd && cmd.liveOffset + cmd.count > liveOffset)
                {
                    cmd.count = 0;
                    _stagedUploads[i] = cmd;
                }
            }


            AddStagedMerged(_stagedClears, 0, liveOffset, count, matchStaging: false);
            _stagingDirty = true;
        }

        // flush all pending staged data if the pool had anything staged this frame.
        // called at allocation time to prevent stale entries from leaking to the new occupant.
        private void InvalidateStagedForPool(int poolIdx)
        {
            if (_poolStagedFrame[poolIdx] != _renderCycleCounter) return;
#if DEBUG
            LogMessageDevelopment($"InvalidateStagedForPool: pool {poolIdx} had pending staging this frame - flushing all staging arrays");
#endif
            FlushInstanceUploads();
            FlushTRSPatchUploads();
            FlushColorUploads();
            FlushSigOverrideUploads();

            // flushed patches mark this pool's dying slots for recompute; the new
            // occupants must not double-consume their positions
            if (_slotNeedsMatrix.IsCreated)
            {
                int slotBase = poolIdx * k_poolSize;
                for (int i = 0; i < k_poolSize; i++)
                    _slotNeedsMatrix[slotBase + i] = false;
            }
        }

        // stage a clear that runs AFTER uploads. used by RemoveInstance so that
        // split overlapping upload commands to exclude a slot range, preventing
        // GPU write races when the slot is reused by AddInstance in the same frame.
        // then stage a pre-clear so culling doesn't read stale data if the slot isn't reused.
        private void SplitUploadsAndClear(int liveOffset, int count)
        {
            if (count <= 0) return;

            int clearEnd = liveOffset + count;

            // split overlapping upload commands - two commands writing the same
            // destination slot in a single dispatch is a GPU thread race
            for (int i = _stagedUploads.Length - 1; i >= 0; i--)
            {
                var cmd = _stagedUploads[i];
                if (cmd.count <= 0) continue;
                int cmdEnd = cmd.liveOffset + cmd.count;
                if (cmd.liveOffset >= clearEnd || cmdEnd <= liveOffset) continue;

                // overlap detected - punch a hole for the removed slot range
                int beforeCount = liveOffset - cmd.liveOffset;
                int afterCount = cmdEnd - clearEnd;

                if (beforeCount > 0 && afterCount > 0)
                {
                    // split into two: shrink existing to "before", add "after"
                    var before = cmd;
                    before.count = beforeCount;
                    _stagedUploads[i] = before;

                    _stagedUploads.Add(new StagedUpload
                    {
                        stagingOffset = cmd.stagingOffset + (clearEnd - cmd.liveOffset),
                        liveOffset = clearEnd,
                        count = afterCount
                    });
                }
                else if (beforeCount > 0)
                {
                    cmd.count = beforeCount;
                    _stagedUploads[i] = cmd;
                }
                else if (afterCount > 0)
                {
                    cmd.stagingOffset += (clearEnd - cmd.liveOffset);
                    cmd.liveOffset = clearEnd;
                    cmd.count = afterCount;
                    _stagedUploads[i] = cmd;
                }
                else
                {
                    // entire command within clear range - invalidate
                    cmd.count = 0;
                    _stagedUploads[i] = cmd;
                }
            }

            // pre-clear: zeros the slot before uploads so culling doesn't read stale data.
            // if AddInstance reuses the slot, its upload overwrites the zeroed data.
            AddStagedMerged(_stagedClears, 0, liveOffset, count, matchStaging: false);
            _stagingDirty = true;
        }

        private void EnsureBatchCmdBuffer(int needed)
        {
            if (_batchCmdBuffer != null && needed <= _batchCmdCapacity) return;
            int newCap = math.max(needed, math.max(_batchCmdCapacity * 2, 64));
            _batchCmdBuffer?.Dispose();
            _batchCmdBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newCap, sizeof(int) * 3);
            _batchCmdBuffer.name = "FraxinusGPUBatcher_BatchCmd";
            _batchCmdCapacity = newCap;
        }

        private void CommitDeferredWrites()
        {
#if DEBUG
            using var _cd = s_markerCommitDeferredWrites.Auto();
#endif
            if (!_resizeDeferred || _deferredChunkWrites.Length == 0)
            {
                _resizeDeferred = false;
                return;
            }

            _resizeDeferred = false;

            int totalDeferredPools = 0;
            for (int i = 0; i < _deferredChunkWrites.Length; i++)
            {
                var dwp = _deferredChunkWrites[i];
                if (dwp.chunkId < 0 || !_chunkSlotActive[dwp.chunkId] || _chunkGeneration[dwp.chunkId] != dwp.generation)
                    continue;
                totalDeferredPools += (int)math.ceil((float)dwp.instanceCount / k_poolSize);
            }

            // check if we have enough free pools
            int totalAvailable = _freePools.Length;

            if (totalAvailable < totalDeferredPools)
            {
                // grow capacity
                int needed = _allocatedPoolCount + totalDeferredPools;
                int newCapacity = math.max(needed, _poolCapacity + _poolCapacity / 2);
                LogMessageDevelopment($"GPUBatcherRenderer: Resizing pools from {_poolCapacity} to {newCapacity} (needed {totalDeferredPools} for {_deferredChunkWrites.Length} deferred writes, available: {totalAvailable} free)");
                ResizePoolBuffer(newCapacity);
            }

            int droppedChunks = 0;
            int droppedInstances = 0;
            for (int i = 0; i < _deferredChunkWrites.Length; i++)
            {
                var dw = _deferredChunkWrites[i];

                // skip tombstoned, freed, or stale entries
                if (dw.chunkId < 0)
                    continue;
                if (!_chunkSlotActive[dw.chunkId])
                    continue;
                if (_chunkGeneration[dw.chunkId] != dw.generation)
                    continue;

                var instances = _deferredInstances.AsArray().GetSubArray(dw.instanceOffset, dw.instanceCount);

                int poolCount = AllocatePools(dw.chunkId, dw.instanceCount);
                if (poolCount == -2)
                {
                    // secondary resize needed
                    int poolsNeeded = (int)math.ceil((float)dw.instanceCount / k_poolSize);
                    int newCap = math.max(_allocatedPoolCount + poolsNeeded, _poolCapacity + _poolCapacity / 2);
                    LogMessageDevelopment($"GPUBatcherRenderer: Secondary resize for chunk {dw.chunkId} ({dw.instanceCount} instances, {poolsNeeded} pools). Resizing to {newCap}");
                    ResizePoolBuffer(newCap);
                    poolCount = AllocatePools(dw.chunkId, dw.instanceCount, isRetry: true);
                }
                if (poolCount <= 0)
                {
                    int poolsNeeded = (int)math.ceil((float)dw.instanceCount / k_poolSize);
                    Debug.LogError($"GPUBatcherRenderer: CommitDeferredWrites failed to allocate pools for chunk {dw.chunkId} ({dw.instanceCount} instances, needs {poolsNeeded} pools) - capacity: {_poolCapacity}, allocated: {_allocatedPoolCount}, free: {_freePools.Length}");
                    droppedChunks++;
                    droppedInstances += dw.instanceCount;
                    continue;
                }

            #if DEBUG
                _totalInstanceCount += dw.instanceCount;
            #endif
                ExecuteChunkWrite(dw.chunkId, instances, dw.instanceCount, poolCount,
                    dw.bounds, dw.cullDistance, dw.instanceCullDistance);
            }

            if (droppedChunks > 0)
                Debug.LogWarning($"GPUBatcherRenderer: CommitDeferredWrites dropped {droppedChunks} chunk(s) ({droppedInstances} instances) due to pool allocation failure.");

            _deferredChunkWrites.Clear();
            _deferredInstances.Clear();

            // fire one-shot event, then clear subscribers
            onDeferredWritesCompleted?.Invoke();
            onDeferredWritesCompleted = null;
        }

        // flush staged clears to GPU. idempotent - no-op if empty.
        // called before any dispatch that writes to slots that may have been freed+reallocated.
        private void FlushPendingClears()
        {
            if (_stagedClears.Length <= 0) return;
#if DEBUG
            for (int i = 0; i < _stagedClears.Length; i++)
            {
                var c = _stagedClears[i];
                debugUploadHistory.Add(new DebugUploadRecord {
                    liveOffset = c.liveOffset, count = c.count,
                    isClear = true, isOverflow = statInstanceOverflowFlushes > 0
                });
            }
#endif
            EnsureBatchCmdBuffer(_stagedClears.Length);
            _batchCmdBuffer.SetData(_stagedClears.AsArray(), 0, 0, _stagedClears.Length);
            GPUBatchClearInstances(_batchCmdBuffer, _stagedClears.Length, _instanceBuffer);
#if UNITY_EDITOR
            DebugMirrorApplyClears(_stagedClears);
#endif
            _stagedClears.Clear();
        }

        // flush only instance uploads + matrix compute. safe to call from CommitDeferredWrites.
        private void FlushInstanceUploads()
        {
#if DEBUG
            using var _fi = s_markerFlushInstanceUploads.Auto();
#endif
            // sig buffer must exist before matrix compute can run
            FlushSignatureUploads();

            // clears must run before uploads - freed+reallocated slots need zeroing first
            FlushPendingClears();

            bool hasPreFlushed = _preFlushedUploads.IsCreated && _preFlushedUploads.Length > 0;
            if (!_stagingDirty && !hasPreFlushed) return;

            int totalMatrixCmds = _stagedUploads.Length + (hasPreFlushed ? _preFlushedUploads.Length : 0);
            if (totalMatrixCmds > 0)
                EnsureBatchCmdBuffer(totalMatrixCmds);

            if (_stagedUploads.Length > 0)
            {
#if DEBUG
                for (int _du = 0; _du < _stagedUploads.Length; _du++)
                {
                    var _u = _stagedUploads[_du];
                    debugUploadHistory.Add(new DebugUploadRecord { liveOffset = _u.liveOffset, count = _u.count, isClear = false, isOverflow = statInstanceOverflowFlushes > 0 });
                }
#endif
                _stagingInstanceBuffer.SetData(_stagingCpuArray, 0, 0, _stagingWriteCursor);
                _batchCmdBuffer.SetData(_stagedUploads.AsArray(), 0, 0, _stagedUploads.Length);
                GPUBatchUploadInstances(_batchCmdBuffer, _stagedUploads.Length,
                    _stagingInstanceBuffer, _instanceBuffer);
#if UNITY_EDITOR
                DebugMirrorApplyUploads(_stagedUploads);
#endif
            }

            if (totalMatrixCmds > 0)
            {
                if (_gpuSignatureBuffer == null)
                {
                    // defense: sig buffer still null after FlushSignatureUploads (no prototypes).
                    // defer matrix compute - instance data is on GPU, next flush will process.
                    if (_stagedUploads.Length > 0)
                        _preFlushedUploads.AddRange(_stagedUploads.AsArray());
                }
                else
                {
                    int offset = 0;
                    if (hasPreFlushed)
                    {
                        _batchCmdBuffer.SetData(_preFlushedUploads.AsArray(), 0, 0, _preFlushedUploads.Length);
                        offset = _preFlushedUploads.Length;
#if UNITY_EDITOR
                        DebugMirrorApplyMatrixInitColorReset(_preFlushedUploads);
#endif
                        _preFlushedUploads.Clear();
                    }
                    if (_stagedUploads.Length > 0)
                        _batchCmdBuffer.SetData(_stagedUploads.AsArray(), 0, offset, _stagedUploads.Length);

                    GPUBatchComputeMatrices(_batchCmdBuffer, totalMatrixCmds,
                        _instanceBuffer, _matrixBuffer, _poolCapacity * k_poolSize, k_prevMInit);
#if UNITY_EDITOR
                    DebugMirrorApplyMatrixInitColorReset(_stagedUploads);
#endif
                }
            }

            _stagedUploads.Clear();
            _stagingWriteCursor = 0;
            _stagingDirty = false;
        }

        /// <summary>
        /// Flush all staged GPU uploads. Called before culling reads live buffers.
        /// </summary>
        public void FlushStagedUploads()
        {
#if DEBUG
            using var _flush = s_markerFlushStagedUploads.Auto();
#endif
            if (!_chunkSystemInitialized) return;
            _cachedNegativeScaleMode = _config.negativeScaleMode;
            statInstanceOverflowFlushes = 0;
            statPoolRefOverflowFlushes = 0;
            statTRSPatchOverflowFlushes = 0;
            statChunkOverflowFlushes = 0;
            statColorOverflowFlushes = 0;
#if DEBUG
            debugUploadHistory.Clear();
            debugResizedThisFrame = false;
            debugTRSPatchesDispatchedPreDiag = 0;
#endif
            // resize pools/chunks if needed (replayed writes stage more data)
            CommitDeferredWrites();

            // sig buffer must be ready before matrix compute or TRS patches
            FlushSignatureUploads();

            // flush tracked staging from all registered trackers (patches, colors)
            // runs after resize/sig so buffers are valid, before GPU upload dispatch
            for (int i = 0; i < _registrations.Count; i++)
                _registrations[i].FlushToCore();

            // instance uploads - patches override on top
            FlushInstanceUploads();

            FlushPoolRefUploads();

            if (_perInstanceColorEnabled)
                FlushColorUploads();

            FlushChunkMetadataUploads();

            // SH light probe uploads
            _lightProbes?.Tick();

            // batched fade state resets (one dispatch per view)
            if (_stagedFadeResets.Length > 0)
            {
                EnsureBatchCmdBuffer(_stagedFadeResets.Length);
                _batchCmdBuffer.SetData(_stagedFadeResets.AsArray(), 0, 0, _stagedFadeResets.Length);
                for (int i = 0; i < _viewFadeStateKeys.Count; i++)
                {
                    if (_viewFadeStates.TryGetValue(_viewFadeStateKeys[i], out var fadeState))
                    {
                        var buf = fadeState.fadeStateBuffer;
                        if (buf != null)
                            GPUBatchInitFadeStates(_batchCmdBuffer, _stagedFadeResets.Length, buf);
                    }
                }
            }

            _stagedFadeResets.Clear();

            FlushTRSPatchUploads();

            // must precede recompute: recompute skips the cull write for sig < 0
            FlushSigOverrideUploads();

            // recompute ONLY slots patched this flush (consume-once: the instance buffer holds raw position only until
            // the matrix kernel overwrites it with bounds-center). PrevM was already snapshotted at patch time, so always preserve.
            if (_movedThisFrame.IsCreated && _movedThisFrameCount > 0)
            {
                _recomputeScratch.Clear();
                for (int i = 0; i < _movedThisFrameCount; i++)
                {
                    int slot = _movedThisFrame[i];
                    if (_slotNeedsMatrix[slot]) _recomputeScratch.Add(slot); // skip slots not re-patched this flush (already consumed)
                }
                if (_recomputeScratch.Length > 0
                    && GPUComputeMatricesFromTRSBatched(_recomputeScratch.AsArray(), _recomputeScratch.Length, k_prevMPreserve))
                {
                    for (int i = 0; i < _recomputeScratch.Length; i++)
                        _slotNeedsMatrix[_recomputeScratch[i]] = false;
                }
            }

#if UNITY_EDITOR
            DebugUploadValidationTick();
            onPostFlushStagedUploads?.Invoke();
#endif
        }

        /// <summary>
        /// Settle motion vectors for moved slots. Called once per frame in SystemUpdate,
        /// AFTER the final FlushStagedUploads, so all TRS patches are guaranteed dispatched.
        /// </summary>
        private void SettleMotionVectors()
        {
            // settle previous frame's moved slots (exclude slots moved again this frame)
            if (_settleList.IsCreated && _settleList.Length > 0)
            {
                if (_movedThisFrameCount > 0)
                {
                    int write = 0;
                    for (int i = 0; i < _settleList.Length; i++)
                    {
                        if (!_slotMovedThisFrame[_settleList[i]])
                            _settleList[write++] = _settleList[i];
                    }
                    _settleList.Length = write;
                }

                if (_settleList.Length > 0)
                    GPUSettleMotionVectors(_settleList.AsArray(), _settleList.Length);
            }

            // targeted clear: reset moved flags before clearing the list
            if (_slotMovedThisFrame.IsCreated && _movedThisFrame.IsCreated)
            {
                for (int i = 0; i < _movedThisFrameCount; i++)
                    _slotMovedThisFrame[_movedThisFrame[i]] = false;
            }

            // transfer to settle list for next frame, then clear
            if (_settleList.IsCreated) _settleList.Clear();
            if (_movedThisFrame.IsCreated && _movedThisFrameCount > 0)
            {
                _settleList.AddRange(_movedThisFrame.GetSubArray(0, _movedThisFrameCount));
                _movedThisFrameCount = 0;
            }
        }

        #endregion

        #region Animated Crossfade Pool List

        // called once per render cycle after FlushStagedUploads, no-op until membership changes
        private void RebuildAnimatedCrossfadePoolList()
        {
            if (!_chunkSystemInitialized || !_animatedPoolListDirty)
                return;
            _animatedPoolListDirty = false;

            _animatedCrossfadePools.Clear();

            if (_anyPrototypeUsesAnimatedCrossfade)
            {
                for (int a = 0; a < _allocatedCount; a++)
                {
                    int c = _activeChunks[a];
                    if (!_chunkHasAnimatedCrossfade[c]) continue;

                    // add all pools
                    var chunk = _chunks[c];
                    int poolBase = c * _maxPoolRefsPerChunk;
                    for (int i = 0; i < chunk.poolCount; i++)
                    {
                        int poolIdx = _chunkPools[poolBase + i];
                        if (poolIdx >= 0) _animatedCrossfadePools.Add(poolIdx);
                    }
                }
            }

            // upload to GPU buffer
            int count = _animatedCrossfadePools.Length;
            if (count > 0)
            {
                if (_animatedPoolsBuffer == null || count > _animatedPoolsBufferCapacity)
                {
                    _animatedPoolsBuffer?.Dispose();
                    _animatedPoolsBufferCapacity = math.max(count, math.max(_animatedPoolsBufferCapacity * 2, 64));
                    _animatedPoolsBuffer = new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured,
                        _animatedPoolsBufferCapacity,
                        sizeof(int));
                }
                _animatedPoolsBuffer.SetData(_animatedCrossfadePools.AsArray(), 0, 0, count);
            }
        }

        #endregion

        #region Instance Transform API

        /// <summary>
        /// Apply ForcePositiveScale / FlipWinding logic for a scale change. Returns the
        /// (possibly corrected) scale. Called from StageTRSPatch and BulkStageTRSPatches.
        /// </summary>
        internal unsafe float3 ProcessScaleFlip(int globalSlotIndex, float3 scale)
        {
            if (_cachedNegativeScaleMode == NegativeScaleMode.ForcePositiveScale
                && scale.x * scale.y * scale.z < 0f)
                scale.x = -scale.x;

            if (_cachedNegativeScaleMode == NegativeScaleMode.FlipWinding
                && _ptrSlotFlipped != null && _ptrProtoFlipCounts != null)
            {
                bool wasFlipped = _ptrSlotFlipped[globalSlotIndex];
                bool nowFlipped = scale.x * scale.y * scale.z < 0f;
                if (wasFlipped != nowFlipped)
                {
                    int protoIdx = _ptrSlotProtoIndex[globalSlotIndex];
                    if (protoIdx >= 0 && protoIdx < _protoFlipCounts.Length)
                    {
                        if (nowFlipped)
                        {
                            int oldFlip = _ptrProtoFlipCounts[protoIdx];
                            _ptrProtoFlipCounts[protoIdx] = oldFlip + 1;
                            if (oldFlip == 0) { _pendingProtoResizes.Add(protoIdx); RequestBatchRebuild(BatchRebuildScope.IncrementalFlush); }
                            if (_ptrProtoFlipCountHWM != null && protoIdx < _protoFlipCountHWM.Length
                                && _ptrProtoFlipCounts[protoIdx] > _ptrProtoFlipCountHWM[protoIdx])
                            {
                                _ptrProtoFlipCountHWM[protoIdx] = math.max(
                                    _ptrProtoFlipCounts[protoIdx] + _ptrProtoFlipCounts[protoIdx] / 4,
                                    _ptrProtoFlipCounts[protoIdx] + 64);
                                _pendingProtoResizes.Add(protoIdx);
                                RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
                            }
                        }
                        else
                        {
                            _ptrProtoFlipCounts[protoIdx] = math.max(0, _ptrProtoFlipCounts[protoIdx] - 1);
                        }
                        _ptrSlotFlipped[globalSlotIndex] = nowFlipped;
                    }
                }
            }
            return scale;
        }

        // patch-based instance update: append-only, caller (tracker) handles dedup
        // uses unsafe pointers to bypass NativeArray safety checks in this hot path
        private unsafe void StageTRSPatch(int globalSlotIndex, uint flags, float3 pos, float4 rot, float3 scale)
        {
            if (!_chunkSystemInitialized) return;

            int totalSlots = _poolCapacity * k_poolSize;
            if (globalSlotIndex < 0 || globalSlotIndex >= totalSlots) return;
            if (_ptrSlotProtoIndex[globalSlotIndex] < 0) return; // slot not in use
            _poolStagedFrame[globalSlotIndex / k_poolSize] = _renderCycleCounter;
            _poolRmwStagedFrame[globalSlotIndex / k_poolSize] = _renderCycleCounter;

            if ((flags & 4u) != 0)
                scale = ProcessScaleFlip(globalSlotIndex, scale);

            if (_trsPatchWriteCursor >= _trsPatchStagingCapacity)
            {
                statTRSPatchOverflowFlushes++;
                FlushInstanceUploads();
                FlushTRSPatchUploads();
            }

            _ptrTRSPatchSlotIndices[_trsPatchWriteCursor] = globalSlotIndex;
            _ptrTRSPatchEntries[_trsPatchWriteCursor++] = new GPUTRSPatchEntry
            {
                slotIndex = (uint)globalSlotIndex,
                flags = flags,
                position = pos,
                rotation = rot,
                scale = scale
            };
        }

        // stage a sig override for enable/disable via separate scatter write kernel.
        // does not go through the _slotProtoIndex guard - caller is responsible for state management.
        private void StageSigOverride(int globalSlotIndex, int sigOverride)
        {
            if (!_chunkSystemInitialized) return;
            int totalSlots = _poolCapacity * k_poolSize;
            if (globalSlotIndex < 0 || globalSlotIndex >= totalSlots) return;
            _poolStagedFrame[globalSlotIndex / k_poolSize] = _renderCycleCounter;
            _poolRmwStagedFrame[globalSlotIndex / k_poolSize] = _renderCycleCounter;

            if (_sigOverrideStagingWriteCursor >= k_sigOverrideStagingCapacity)
            {
                FlushInstanceUploads();
                FlushSigOverrideUploads();
            }

            _sigOverrideStagingCpuArray[_sigOverrideStagingWriteCursor++] = new GPUSigOverrideEntry
            {
                slotIndex = (uint)globalSlotIndex,
                sigIdx = sigOverride
            };
        }

        private void FlushSigOverrideUploads()
        {
            int count = _sigOverrideStagingWriteCursor;
            if (count <= 0) return;

            _sigOverrideStagingBuffer.SetData(_sigOverrideStagingCpuArray, 0, 0, count);
            GPUScatterWriteSigOverrides(_sigOverrideStagingBuffer, count);
#if UNITY_EDITOR
            DebugMirrorApplySigOverrides(count);
#endif
            _sigOverrideStagingWriteCursor = 0;
        }

        private void GPUScatterWriteSigOverrides(GraphicsBuffer src, int count)
        {
            if (_scatterWriteSigOverridesKernel < 0 || _uploadShader == null || count <= 0) return;
            _uploadShader.SetBuffer(_scatterWriteSigOverridesKernel, s_idSigOverrideSrc, src);
            _uploadShader.SetBuffer(_scatterWriteSigOverridesKernel, s_idUploadInstanceDst, _instanceBuffer);
            _uploadShader.SetInt(s_idSigOverrideCount, count);
            DispatchSafe(_uploadShader, _scatterWriteSigOverridesKernel, UploadGroupCount(count));
        }

        /// <summary>Sets position directly; use <see cref="GPUBatcherRegistrationTracker"/> to coalesce repeated patches.</summary>
        public void SetInstancePosition(int globalSlotIndex, float3 position)
            => StageTRSPatch(globalSlotIndex, 1u, position, default, default);

        /// <summary>Sets rotation directly; use <see cref="GPUBatcherRegistrationTracker"/> to coalesce repeated patches.</summary>
        public void SetInstanceRotation(int globalSlotIndex, quaternion rotation)
            => StageTRSPatch(globalSlotIndex, 2u, default, rotation.value, default);

        /// <summary>Sets scale directly; use <see cref="GPUBatcherRegistrationTracker"/> to coalesce repeated patches.</summary>
        public void SetInstanceScale(int globalSlotIndex, float3 scale)
            => StageTRSPatch(globalSlotIndex, 4u, default, default, scale);

        /// <summary>Sets position and rotation directly; use <see cref="GPUBatcherRegistrationTracker"/> to coalesce repeated patches.</summary>
        public void MoveAndRotateInstance(int globalSlotIndex, float3 position, quaternion rotation)
            => StageTRSPatch(globalSlotIndex, 3u, position, rotation.value, default);

        /// <summary>
        /// Set full TRS. Light probes are not re-sampled - call SampleInstanceLightProbe if needed.
        /// Use <see cref="GPUBatcherRegistrationTracker"/> to coalesce repeated patches.
        /// </summary>
        public void MoveInstance(int globalSlotIndex, float3 newPosition, quaternion newRotation, float3 newScale)
            => StageTRSPatch(globalSlotIndex, 7u, newPosition, newRotation.value, newScale);

        /// <summary>Re-sample light probes for an instance. Not called automatically by move APIs.</summary>
        public void SampleInstanceLightProbe(int globalSlotIndex, float3 position)
        {
            if (_lightProbes == null || !_chunkSystemInitialized) return;

            int totalSlots = _poolCapacity * k_poolSize;
            if (globalSlotIndex < 0 || globalSlotIndex >= totalSlots) return;

            int protoIdx = _slotProtoIndex[globalSlotIndex];
            if (protoIdx < 0) return;

            var inst = new InstanceData
            {
                position = position,
                rotation = quaternion.identity,
                scale = new float3(1, 1, 1),
                signatureIndex = protoIdx
            };
            _lightProbes.SampleAndStageSingle(in inst, globalSlotIndex);
        }

        #endregion
    }
}
