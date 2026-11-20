// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEngine;
using UnityEditor;

namespace Fraxinus.GPUBatcher.Editor
{
    [CustomEditor(typeof(GPUBatcherGameObjectLink))]
    internal class GPUBatcherGameObjectLinkEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var link = (GPUBatcherGameObjectLink)target;
            var instance = link.InstanceLink;

            EditorGUI.BeginDisabledGroup(true);
            if (instance.IsValid)
                EditorGUILayout.IntField("Global Instance ID", instance.GlobalBRGSlotIndex);
            else
                EditorGUILayout.LabelField("Global Instance ID", "Not assigned");
            EditorGUI.EndDisabledGroup();
        }
    }
}
