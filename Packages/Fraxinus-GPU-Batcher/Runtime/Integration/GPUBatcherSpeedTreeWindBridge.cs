// Adapted for Fraxinus GPU Batcher from BRG Instanced Renderer.
// Original implementation copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using Unity.Collections;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Boundary for Unity's native SpeedTree wind extraction, including history.
    /// Unity 2022.3 has no SpeedTreeWindManager.UpdateWindAndWriteBufferWindParams
    /// binding, including internal APIs. Buffer layout and call sites remain intact.
    /// </summary>
    public static class GPUBatcherSpeedTreeWindBridge
    {
        public const int kMaxWindParamsCount = 16;
        public static bool IsSupported => false;

        public static void ComputeWindParams(int rendererInstanceID, NativeArray<Vector4> output, bool history)
        {
            var ids = new NativeArray<int>(1, Allocator.Temp);
            try
            {
                ids[0] = rendererInstanceID;
                ComputeWindParams(ids, output, history);
            }
            finally { ids.Dispose(); }
        }

        public static void ComputeWindParams(NativeArray<int> ids, NativeArray<Vector4> output, bool history)
            => TryComputeWindParams(ids, output, history);

        /// <summary>Returns false when native extraction is unavailable; clears the requested wind slots.</summary>
        public static bool TryComputeWindParams(NativeArray<int> ids, NativeArray<Vector4> output, bool history)
        {
            if (!ids.IsCreated) throw new ArgumentException("Renderer IDs must be allocated.", nameof(ids));
            if (!output.IsCreated || output.Length < ids.Length * kMaxWindParamsCount)
                throw new ArgumentException("Output must hold 16 wind vectors per renderer.", nameof(output));
            for (int i = 0; i < ids.Length * kMaxWindParamsCount; ++i) output[i] = Vector4.zero;
            return false;
        }
    }
}
