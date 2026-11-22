// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEditor;

namespace Fraxinus.GPUBatcher.Editor
{
    [CustomEditor(typeof(GPUBatcherGameObjectLink))]
    internal class GPUBatcherGameObjectLinkEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var link = (GPUBatcherGameObjectLink)target;
            var instance = link.instanceLink;

            EditorGUI.BeginDisabledGroup(true);
            if (instance.isValid)
                EditorGUILayout.IntField("Global Instance ID", instance.globalBRGSlotIndex);
            else
                EditorGUILayout.LabelField("Global Instance ID", "Not assigned");
            EditorGUI.EndDisabledGroup();
        }
    }
}
