// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// groups prototypes for runtime LOD-bias and density control. holds no serialized data -
    /// prototypes reference one of these, and the live values below are tuned at runtime via
    /// SetLodBias / SetDensity. values reset to defaults each session (never persisted).
    /// </summary>
    [CreateAssetMenu(fileName = "PrototypeGroup", menuName = "Fraxinus GPU Batcher/Prototype Group")]
    public class GPUBatcherPrototypeGroup : ScriptableObject
    {
        [NonSerialized] public float lodBias = 1f;
        [NonSerialized] public float density = 1f;

        /// <summary>set LOD bias for every prototype in this group. above 1 holds finer LODs longer, below 1 drops sooner. applies live.</summary>
        public void SetLodBias(float value)
        {
            lodBias = Mathf.Max(0f, value);
            // guard avoids spinning up the renderer just to set a value; it re-reads on registration anyway
            if (GPUBatcherRenderer.HasInstance) GPUBatcherRenderer.Instance.NotifyGroupChanged(this);
        }

        /// <summary>set density (0-1) for every prototype in this group. 1 = full, 0 = all culled. applies live.</summary>
        public void SetDensity(float value)
        {
            density = Mathf.Clamp01(value);
            if (GPUBatcherRenderer.HasInstance) GPUBatcherRenderer.Instance.NotifyGroupChanged(this);
        }
    }
}
