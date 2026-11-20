// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEditor;

namespace Fraxinus.GPUBatcher.Editor
{
    [CustomEditor(typeof(GPUBatcherTerrainDetailOverride))]
    [CanEditMultipleObjects]
    internal class GPUBatcherTerrainDetailOverrideEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
        }
    }
}
