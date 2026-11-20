// Adapted for Fraxinus GPU Batcher from BRG Instanced Renderer.
// Original implementation copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    /// <summary>Camera-relative occlusion projection using public camera matrix APIs.</summary>
    public static class GPUBatcherHZBBridge
    {
        public struct HZBCameraData
        {
            public Matrix4x4 viewProjMatrix;
            public Vector4 viewOriginWorldSpace;
            public Vector4 facingDirWorldSpace;
            public Vector4 radialDirWorldSpace;
        }

        public static HZBCameraData ComputeCameraData(Camera camera)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            return ComputeCameraData(camera.worldToCameraMatrix, GL.GetGPUProjectionMatrix(camera.projectionMatrix, true));
        }

        public static HZBCameraData ComputeCameraData(Matrix4x4 view, Matrix4x4 gpuProjection)
        {
            var inverseView = view.inverse;
            view.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
            var right = (Vector3)inverseView.GetColumn(0);
            var up = (Vector3)inverseView.GetColumn(1);
            var towardsViewer = (Vector3)inverseView.GetColumn(2);
            return new HZBCameraData
            {
                viewProjMatrix = gpuProjection * view,
                viewOriginWorldSpace = (Vector3)inverseView.GetColumn(3),
                facingDirWorldSpace = towardsViewer.normalized,
                radialDirWorldSpace = (right + up).normalized
            };
        }
    }
}
