#if !UNITY_6000_0_OR_NEWER && (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN)
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher
{
    public partial class GPUBatcherRenderer
    {
        // A pooled view can outlive its GPU allocation. Rebuild the decorated BatchIDs when
        // their source metadata is invalidated (RemoveAllBatchIds), or when this layout changes.
        private sealed class VulkanVisibilityView
        {
            public GraphicsBuffer combined;
            public int matrixBytes;
            public readonly Dictionary<BatchID, BatchID> batches = new();

            public void Dispose(BatchRendererGroup group)
            {
                if (group != null)
                    foreach (var batch in batches.Values)
                        group.RemoveBatch(batch);
                batches.Clear();
                combined?.Dispose();
                combined = null;
            }
        }

        private readonly Dictionary<CullingBufferSet, VulkanVisibilityView> _vkVisibilityViews = new();
        private static readonly int s_vkVisibilityByteBase = Shader.PropertyToID("_FraxinusVkVisibilityByteBase");

        private bool VkEnsureVisibility(CullingBufferSet set, out VulkanVisibilityView view)
        {
            view = null;
            if (_brg == null || _uploadShader == null || _copyBufferRegionKernel < 0 ||
                BatchRendererGroup.BufferTarget != BatchBufferTarget.RawBuffer ||
                _matrixBuffer == null || set?.visibilityBuffer == null)
                return false;

            int matrixWords = _matrixBuffer.count;
            long combinedWords = (long)matrixWords + set.visibilityBuffer.count;
            if (_matrixBuffer.stride != sizeof(uint) || set.visibilityBuffer.stride != sizeof(uint) ||
                combinedWords * sizeof(uint) > SystemInfo.maxGraphicsBufferSize ||
                combinedWords * sizeof(uint) > int.MaxValue)
                return false;

            int matrixBytes = matrixWords * sizeof(uint);
            if (_vkVisibilityViews.TryGetValue(set, out view))
            {
                if (view.matrixBytes == matrixBytes && view.combined.count == (int)combinedWords)
                    return true;
                view.Dispose(_brg);
                _vkVisibilityViews.Remove(set);
            }

            view = new VulkanVisibilityView
            {
                matrixBytes = matrixBytes,
                combined = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)combinedWords, sizeof(uint))
                    { name = "FraxinusGPUBatcher_VulkanViewInstanceData" }
            };
            _vkVisibilityViews.Add(set, view);
            return true;
        }

        private bool VkGetCarrierBatch(CullingBufferSet set, BatchID original, out BatchID carrier)
        {
            carrier = default;
            if (!VkEnsureVisibility(set, out var view))
                return false;
            if (view.batches.TryGetValue(original, out carrier))
                return true;

            int protoIndex = -1;
            if (!_sharedBatchIDValid || original != _sharedBatchID)
            {
                if (_protoSpeedTreeSlotIndex == null || _speedTreeBatchIDs == null)
                    return false;
                for (int i = 0; i < _protoSpeedTreeSlotIndex.Length; ++i)
                {
                    int windSlot = _protoSpeedTreeSlotIndex[i];
                    if ((uint)windSlot < (uint)_speedTreeBatchIDs.Length && _speedTreeBatchIDs[windSlot] == original)
                    {
                        protoIndex = i;
                        break;
                    }
                }
                if (protoIndex < 0)
                    return false;
            }

            using var source = BuildBatchMetadata(protoIndex);
            var metadata = new NativeArray<MetadataValue>(source.Length + 1, Allocator.Temp,
                NativeArrayOptions.UninitializedMemory);
            try
            {
                NativeArray<MetadataValue>.Copy(source, metadata, source.Length);
                // The shader uses the metadata word as a byte offset. Ordinary BRG batches see zero.
                metadata[source.Length] = new MetadataValue { NameID = s_vkVisibilityByteBase, Value = (uint)view.matrixBytes };
                carrier = _brg.AddBatch(metadata, view.combined.bufferHandle);
            }
            finally { metadata.Dispose(); }
            if (carrier.value == 0)
                return false;
            view.batches.Add(original, carrier);
            return true;
        }

        private bool VkCopyVisibility(CommandBuffer cmd, CullingBufferSet set)
        {
            if (cmd == null || !VkEnsureVisibility(set, out var view))
                return false;
            // Copies are recorded after this view's scatter/finalize and recorded again on
            // HZB retest. Camera and shadow batches bind distinct allocations via AddBatch.
            VkCopyWords(cmd, _matrixBuffer, view.combined, 0);
            VkCopyWords(cmd, set.visibilityBuffer, view.combined, view.matrixBytes);
            return true;
        }

        private void VkCopyWords(CommandBuffer cmd, GraphicsBuffer source, GraphicsBuffer destination, int dstByteOffset)
        {
            int words = source.count;
            int vectors = words / 4, tail = words % 4;
            cmd.SetComputeBufferParam(_uploadShader, _copyBufferRegionKernel, s_idCopySrc, source);
            cmd.SetComputeBufferParam(_uploadShader, _copyBufferRegionKernel, s_idCopyDst, destination);
            cmd.SetComputeIntParam(_uploadShader, s_idCopySrcOffsetBytes, 0);
            cmd.SetComputeIntParam(_uploadShader, s_idCopyDstOffsetBytes, dstByteOffset);
            cmd.SetComputeIntParam(_uploadShader, s_idCopyCountUint4, vectors);
            cmd.SetComputeIntParam(_uploadShader, s_idCopyTailUints, tail);
            DispatchSafe(cmd, _uploadShader, _copyBufferRegionKernel, (vectors + tail + 255) / 256);
        }

        private void VkDisposeVisibility()
        {
            foreach (var view in _vkVisibilityViews.Values)
                view.Dispose(_brg);
            _vkVisibilityViews.Clear();
        }

        internal GraphicsBuffer VkGetViewInstanceBuffer(CullingBufferSet set)
            => _vkVisibilityViews.TryGetValue(set, out var view) ? view.combined : null;
    }
}
#endif
