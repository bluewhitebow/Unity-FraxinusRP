// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_6000_4_OR_NEWER
using WindID = UnityEngine.EntityId;
#else
using WindID = System.Int32;
#endif

namespace Fraxinus.GPUBatcher
{
    public partial class GPUBatcherRenderer
    {
        // The capture pass writes [slot0_cur(16)][slot0_hist(16)][slot1...] on the GPU.
        private WindID[] _speedTreeRendererIds; // slot -> renderer instance ID
        private bool _warnedWindCaptureFailure;

        // Assign dense wind slots after allocating the matrix buffer.
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

            _speedTreeRendererIds = new WindID[needed];

            _speedTreeSlotHWM = 0;
            for (int p = 0; p < sigCount; p++)
            {
                var sig = _renderSignatures[p];
                if (sig == null || sig.speedTreeRendererID == default) continue;
                int slot = _speedTreeSlotHWM++;
                _protoSpeedTreeSlotIndex[p] = slot;
                _speedTreeRendererIds[slot] = sig.speedTreeRendererID;
                GPUBatcherSpeedTreeWindBridge.RetainRenderer(sig.speedTreeRendererID);
            }
        }

        private void ShutdownSpeedTreeState()
        {
            GPUBatcherSpeedTreeWindBridge.Dispose();
            _speedTreeRendererIds = null;
            _warnedWindCaptureFailure = false;
        }

        // allocates a stable SpeedTree slot for protoIndex. Reuses freed slots before extending HWM.
        // grows wind/renderer/batch-ID arrays and matrix buffer wind region only when HWM increases.
        // called from FinalizeNewProtoSlot when a new SpeedTree proto registers after chunk system init.
        private void RegisterSpeedTreeProto(int protoIndex)
        {
            var sig = _renderSignatures[protoIndex];
            if (sig == null || sig.speedTreeRendererID == default) return;

            EnsureProtoSpeedTreeSlotIndexCapacity(_renderSignatures.Count);

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
                // Grow slot IDs and the GPU wind region; no CPU wind upload can overwrite capture.
                if (_speedTreeRendererIds == null || _speedTreeRendererIds.Length < _speedTreeSlotHWM)
                    Array.Resize(ref _speedTreeRendererIds, _speedTreeSlotHWM);

                // grow matrix buffer wind region if needed
                if (_matrixBuffer != null)
                {
                    FlushStagedUploads();

                    int totalInstanceSlots = _poolCapacity * k_poolSize;
                    int matrixElements = totalInstanceSlots * 3 * 12;
                    int shElements = lightProbesEnabled ? totalInstanceSlots * k_shFloat4sPerInstance * 4 : 0;
                    int colorElements = _perInstanceColorEnabled ? totalInstanceSlots : 0;
                    int newTotal = matrixElements + shElements + colorElements + _speedTreeSlotHWM * k_speedTreeParamsPerProto;

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

            GPUBatcherSpeedTreeWindBridge.RetainRenderer(sig.speedTreeRendererID);
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
            {
                GPUBatcherSpeedTreeWindBridge.ReleaseRenderer(_speedTreeRendererIds[slot]);
                _speedTreeRendererIds[slot] = default;
            }

            _protoSpeedTreeSlotIndex[protoIndex] = -1;
            _freeSpeedTreeSlots.Push(slot);
        }

        private void CaptureSpeedTreeWind(ScriptableRenderContext context)
        {
            if (!_chunkSystemInitialized || _speedTreeSlotHWM == 0 || _speedTreeRendererIds == null || _matrixBuffer == null) return;
            if (!GPUBatcherSpeedTreeWindBridge.Capture(context, _speedTreeRendererIds, _speedTreeSlotHWM,
                _matrixBuffer, _speedTreeRegionByteOffset) && !_warnedWindCaptureFailure)
            {
                _warnedWindCaptureFailure = true;
                Debug.LogWarning("Fraxinus GPU Batcher could not capture native SpeedTree wind for one or more source renderers.");
            }
        }
    }
}
