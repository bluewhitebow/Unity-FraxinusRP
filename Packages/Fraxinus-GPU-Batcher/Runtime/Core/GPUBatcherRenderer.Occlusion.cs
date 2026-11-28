using System.Collections.Generic;
#if FRAXINUS_RP
using FraxinusEngine.Rendering.Ultra;
#endif
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_6000_4_OR_NEWER
using ViewID = UnityEngine.EntityId;
#else
using ViewID = System.Int32;
#endif

namespace Fraxinus.GPUBatcher
{
    public partial class GPUBatcherRenderer
    {
        readonly Dictionary<ViewID, GPUBatcherOcclusionBridge.CameraState> _bakedOcclusionViews = new();
        readonly List<ViewID> _staleBakedOcclusionViews = new();

        void InitializeBakedOcclusion()
        {
#if FRAXINUS_RP
            UltraRenderPipeline.beforeCameraCulling -= PrepareBakedOcclusion;
            UltraRenderPipeline.beforeCameraCulling += PrepareBakedOcclusion;
            UltraRenderPipeline.afterCameraCulling -= PublishBakedOcclusion;
            UltraRenderPipeline.afterCameraCulling += PublishBakedOcclusion;
#endif
        }

        void PrepareBakedOcclusion(Camera camera, in ScriptableCullingParameters parameters)
        {
            if (camera == null) return;
            var id = GPUBatcherHelpers.GetViewID(camera);
            _staleBakedOcclusionViews.Clear();
            foreach (var pair in _bakedOcclusionViews)
                if (pair.Value.camera == null) _staleBakedOcclusionViews.Add(pair.Key);
            foreach (var stale in _staleBakedOcclusionViews)
            {
                _bakedOcclusionViews[stale].Dispose();
                _bakedOcclusionViews.Remove(stale);
            }
            _bakedOcclusionViews.TryGetValue(id, out var state);
            if (!_initialized || !_config.useBakedOcclusionCulling || !_chunkSystemInitialized ||
                _allocatedCount == 0 || !GPUBatcherOcclusionBridge.Supports(camera, parameters))
            {
                state?.Cancel();
                return;
            }
            if (state == null)
            {
                state = new GPUBatcherOcclusionBridge.CameraState(camera);
                _bakedOcclusionViews.Add(id, state);
            }
            state.Begin(_chunks.Length, _allocatedCount);
            for (int i = 0; i < _allocatedCount; ++i)
            {
                int chunkId = _activeChunks[i];
                if (_chunkEnabled[chunkId] && _chunks[chunkId].poolCount > 0)
                    state.Add(chunkId, _chunks[chunkId].bounds);
            }
            state.Commit();
        }

        void PublishBakedOcclusion(Camera camera, in ScriptableCullingParameters parameters)
        {
            if (camera != null && _bakedOcclusionViews.TryGetValue(GPUBatcherHelpers.GetViewID(camera), out var state))
                state.Publish();
        }

        GPUBatcherOcclusionBridge.CameraState GetBakedOcclusion(ViewID cameraId) =>
            _bakedOcclusionViews.TryGetValue(cameraId, out var state) ? state : null;

        int FilterBakedOcclusion(in DeferredCullContext split, int count)
        {
            var state = split.bakedOcclusion;
            if (state == null || !state.isPublished || state.generation != split.bakedOcclusionGeneration ||
                !_config.useBakedOcclusionCulling) return count;
            int write = 0;
            var ids = split.viewSet.cpuVisibleChunkIds;
            for (int i = 0; i < count; ++i)
            {
                int chunkId = ids[i];
                if (state.IsVisible(split.bakedOcclusionGeneration, chunkId, _chunks[chunkId].bounds))
                    ids[write++] = chunkId;
            }
            return write;
        }

        void CleanupBakedOcclusion()
        {
#if FRAXINUS_RP
            UltraRenderPipeline.beforeCameraCulling -= PrepareBakedOcclusion;
            UltraRenderPipeline.afterCameraCulling -= PublishBakedOcclusion;
#endif
            foreach (var state in _bakedOcclusionViews.Values) state.Dispose();
            _bakedOcclusionViews.Clear();
            _staleBakedOcclusionViews.Clear();
        }
    }
}
