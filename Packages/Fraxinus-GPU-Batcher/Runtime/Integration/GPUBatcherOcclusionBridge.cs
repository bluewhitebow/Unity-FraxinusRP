// Adapted for Fraxinus GPU Batcher from BRG Instanced Renderer.
// Original implementation copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Unity 2022.3 has neither BatchCullingContext.occlusionBuffer nor the
    /// BatchRendererGroup.OcclusionTestAABB binding, including internal APIs.
    /// Keep candidates visible here; frustum, distance and GPU HZB tests still apply.
    /// </summary>
    public static class GPUBatcherOcclusionBridge
    {
        public static bool IsSupported => false;
        public static bool OcclusionTestAABB(IntPtr occlusionBuffer, Bounds bounds) => true;
        public static IntPtr GetOcclusionBuffer(in BatchCullingContext context) => IntPtr.Zero;
    }
}
