// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    /// <summary>optional component on terrain detail prefabs to override which prefab BRG renders.</summary>
    [AddComponentMenu("Fraxinus GPU Batcher/GPU Batcher Terrain Detail Override")]
    public class GPUBatcherTerrainDetailOverride : MonoBehaviour
    {
        [Tooltip("The prefab BRG should render instead of this detail prototype. " +
                 "Typically an LODGroup prefab with the same visual but multiple LODs.")]
        public GameObject overridePrefab;

#if UNITY_EDITOR
        public static event Action onAnyOverrideChanged;

        private void OnValidate()
        {
            // delay to avoid calling during import/deserialization
            UnityEditor.EditorApplication.delayCall += () => onAnyOverrideChanged?.Invoke();
        }
#endif
    }
}
