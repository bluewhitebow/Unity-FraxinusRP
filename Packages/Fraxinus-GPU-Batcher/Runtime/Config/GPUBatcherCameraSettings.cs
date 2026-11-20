// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System.Collections.Generic;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    [AddComponentMenu("Fraxinus GPU Batcher/GPU Batcher Camera Settings")]
    [RequireComponent(typeof(Camera))]
    public class GPUBatcherCameraSettings : MonoBehaviour
    {
        [Range(0.01f, 10f)]
        [Tooltip("LOD bias multiplier for this camera. Higher = higher quality LODs at greater distance. " +
                 "Combined with QualitySettings.lodBias. Default 1.0.")]
        public float lodBiasMultiplier = 1.0f;

        public Camera Camera { get; private set; }

        private static readonly List<GPUBatcherCameraSettings> s_activeInstances = new();
        public static IReadOnlyList<GPUBatcherCameraSettings> ActiveInstances => s_activeInstances;

        private void OnEnable()
        {
            Camera = GetComponent<Camera>();
            s_activeInstances.Add(this);
        }
        private void OnDisable() => s_activeInstances.Remove(this);

        public void SnapCrossfade()
        {
            if (GPUBatcherRenderer.HasInstance)
                GPUBatcherRenderer.Instance.SnapCrossfadeState(Camera);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState() => s_activeInstances.Clear();
    }
}
