// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

#if UNITY_6000_4_OR_NEWER
using WindID = UnityEngine.EntityId;
#else
using WindID = System.Int32;
#endif

namespace Fraxinus.GPUBatcher
{
    public partial class GPUBatcherRenderer
    {
        // SpeedTree wind state - contiguous layout mirrors GPU: [slot0_cur(16)][slot0_hist(16)][slot1...]
        private NativeArray<Vector4> _windBuffer;
        private WindID[] _speedTreeRendererIds; // slot -> renderer instance ID

        // called from InitChunkSystem after matrix buffer alloc.
        // pre-allocates slots densely for all currently-registered SpeedTree protos.
        private void InitSpeedTreeState()
        {
            int sigCount = _renderSignatures?.Count ?? 0;
            EnsureProtoSpeedTreeSlotIndexCapacity(sigCount);
            for (int i = 0; i < (_protoSpeedTreeSlotIndex?.Length ?? 0); i++)
                _protoSpeedTreeSlotIndex[i] = -1;

            int needed = 0;
            for (int i = 0; i < sigCount; i++)
                if (_renderSignatures[i] != null && _renderSignatures[i].speedTreeRendererID != default)
                    needed++;

            if (needed == 0) return;

            int k = GPUBatcherSpeedTreeWindBridge.kMaxWindParamsCount;
            int stride = k * 2;

            _windBuffer = new NativeArray<Vector4>(needed * stride, Allocator.Persistent);
            _speedTreeRendererIds = new WindID[needed];

            _speedTreeSlotHWM = 0;
            for (int p = 0; p < sigCount; p++)
            {
                var sig = _renderSignatures[p];
                if (sig == null || sig.speedTreeRendererID == default) continue;
                int slot = _speedTreeSlotHWM++;
                _protoSpeedTreeSlotIndex[p] = slot;
                _speedTreeRendererIds[slot] = sig.speedTreeRendererID;
                GPUBatcherSpeedTreeWindBridge.ComputeWindParams(
                    sig.speedTreeRendererID, _windBuffer.GetSubArray(slot * stride, k), history: false);
                GPUBatcherSpeedTreeWindBridge.ComputeWindParams(
                    sig.speedTreeRendererID, _windBuffer.GetSubArray(slot * stride + k, k), history: true);
            }
        }

        private void ShutdownSpeedTreeState()
        {
            _windBuffer.DisposeIfCreated();
            _speedTreeRendererIds = null;
        }

        // allocates a stable SpeedTree slot for protoIndex. Reuses freed slots before extending HWM.
        // grows wind/renderer/batch-ID arrays and matrix buffer wind region only when HWM increases.
        // called from FinalizeNewProtoSlot when a new SpeedTree proto registers after chunk system init.
        private void RegisterSpeedTreeProto(int protoIndex)
        {
            var sig = _renderSignatures[protoIndex];
            if (sig == null || sig.speedTreeRendererID == default) return;

            EnsureProtoSpeedTreeSlotIndexCapacity(_renderSignatures.Count);

            int k = GPUBatcherSpeedTreeWindBridge.kMaxWindParamsCount;
            int stride = k * 2;

            bool grewHWM;
            int slot;
            if (_freeSpeedTreeSlots.Count > 0)
            {
                slot = _freeSpeedTreeSlots.Pop();
                grewHWM = false;
            }
            else
            {
                slot = _speedTreeSlotHWM++;
                grewHWM = true;
            }

            _protoSpeedTreeSlotIndex[protoIndex] = slot;

            if (grewHWM)
            {
                // grow CPU side arrays
                var oldBuffer = _windBuffer;
                int newWindLen = _speedTreeSlotHWM * stride;
                if (!oldBuffer.IsCreated || oldBuffer.Length < newWindLen)
                {
                    _windBuffer = new NativeArray<Vector4>(newWindLen, Allocator.Persistent);
                    if (oldBuffer.IsCreated)
                    {
                        NativeArray<Vector4>.Copy(oldBuffer, 0, _windBuffer, 0, oldBuffer.Length);
                        oldBuffer.Dispose();
                    }
                }
                if (_speedTreeRendererIds == null || _speedTreeRendererIds.Length < _speedTreeSlotHWM)
                    Array.Resize(ref _speedTreeRendererIds, _speedTreeSlotHWM);

                // grow matrix buffer wind region if needed
                if (_matrixBuffer != null)
                {
                    FlushStagedUploads();

                    int totalInstanceSlots = _poolCapacity * kPoolSize;
                    int matrixElements = totalInstanceSlots * 3 * 12;
                    int shElements = LightProbesEnabled ? totalInstanceSlots * kSHFloat4sPerInstance * 4 : 0;
                    int colorElements = _perInstanceColorEnabled ? totalInstanceSlots : 0;
                    int newTotal = matrixElements + shElements + colorElements + _speedTreeSlotHWM * kSpeedTreeParamsPerProto;

                    if (newTotal > _matrixBuffer.count)
                    {
                        int oldBytes = _matrixBuffer.count * sizeof(float);
                        var newBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newTotal, sizeof(float));
                        newBuffer.name = "FraxinusGPUBatcher_matrixBuffer";
                        GPUCopyBufferRegion(_matrixBuffer, 0, newBuffer, 0, oldBytes);
                        _matrixBuffer?.Dispose();
                        _matrixBuffer = newBuffer;

                        RemoveAllBatchIds();
                    }
                    RebindBatchBufferHandle();
                }
            }

            _speedTreeRendererIds[slot] = sig.speedTreeRendererID;

            // initial wind data so the first frame's draws don't see stale/zero values
            if (_windBuffer.IsCreated)
            {
                int baseIdx = slot * stride;
                GPUBatcherSpeedTreeWindBridge.ComputeWindParams(
                    sig.speedTreeRendererID, _windBuffer.GetSubArray(baseIdx, k), history: false);
                GPUBatcherSpeedTreeWindBridge.ComputeWindParams(
                    sig.speedTreeRendererID, _windBuffer.GetSubArray(baseIdx + k, k), history: true);
            }
        }

        // releases a SpeedTree slot owned by protoIndex. Idempotent for non-SpeedTree protos.
        // called from TrulyRemovePrototype before the proto index goes back into _freeProtoIndices.
        private void UnregisterSpeedTreeProto(int protoIndex)
        {
            if (_protoSpeedTreeSlotIndex == null || protoIndex < 0 || protoIndex >= _protoSpeedTreeSlotIndex.Length)
                return;
            int slot = _protoSpeedTreeSlotIndex[protoIndex];
            if (slot < 0) return;

            if (_brg != null && _speedTreeBatchIDs != null && slot < _speedTreeBatchIDs.Length
                && _speedTreeBatchIDs[slot].value != 0)
            {
                _brg.RemoveBatch(_speedTreeBatchIDs[slot]);
                _speedTreeBatchIDs[slot] = default;
            }
            if (_speedTreeRendererIds != null && slot < _speedTreeRendererIds.Length)
                _speedTreeRendererIds[slot] = default;

            // zero the slot's wind region so the per-frame upload doesn't ship stale data
            if (_windBuffer.IsCreated)
            {
                int k = GPUBatcherSpeedTreeWindBridge.kMaxWindParamsCount;
                int stride = k * 2;
                int baseIdx = slot * stride;
                int end = math.min(baseIdx + stride, _windBuffer.Length);
                for (int i = baseIdx; i < end; i++) _windBuffer[i] = default;
            }

            _protoSpeedTreeSlotIndex[protoIndex] = -1;
            _freeSpeedTreeSlots.Push(slot);
        }

        private void UpdateSpeedTree()
        {
            if (_speedTreeSlotHWM == 0 || !_windBuffer.IsCreated || _matrixBuffer == null) return;

            int k = GPUBatcherSpeedTreeWindBridge.kMaxWindParamsCount;
            var ids = new NativeArray<WindID>(1, Allocator.Temp);
            for (int slot = 0; slot < _speedTreeSlotHWM; slot++)
            {
                if (_speedTreeRendererIds[slot] == default) continue;
                int baseIdx = slot * k * 2;
                ids[0] = _speedTreeRendererIds[slot];

                GPUBatcherSpeedTreeWindBridge.ComputeWindParams(
                    ids, _windBuffer.GetSubArray(baseIdx, k), history: false);
                GPUBatcherSpeedTreeWindBridge.ComputeWindParams(
                    ids, _windBuffer.GetSubArray(baseIdx + k, k), history: true);
            }
            ids.Dispose();

            // single upload for entire wind region
            int vec4Offset = _speedTreeRegionByteOffset / 16;
            _matrixBuffer.SetData(_windBuffer, 0, vec4Offset, _speedTreeSlotHWM * k * 2);
        }
    }
}
