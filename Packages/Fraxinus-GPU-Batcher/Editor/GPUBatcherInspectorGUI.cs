// Copyright (c) 2026 Milk_Drinker01. All rights reserved.
using UnityEngine;
using UnityEditor;

namespace Fraxinus.GPUBatcher.Editor
{
    internal static class GPUBatcherInspectorGUI
    {
        public static void DrawScriptField(SerializedObject serializedObject)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                var script = serializedObject.FindProperty("m_Script");
                if (script != null)
                    EditorGUILayout.PropertyField(script);
            }
        }

        public static void DrawSectionHeader(string title) =>
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
    }
}
