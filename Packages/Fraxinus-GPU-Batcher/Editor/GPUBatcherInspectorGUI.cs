// Copyright (c) 2026 Milk_Drinker01. All rights reserved.
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

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

        public static void DrawRenderingDebuggerButton()
        {
            int panelIndex = DebugManager.instance.PanelIndex(GPUBatcherDebugUI.panelName);
            using (new EditorGUI.DisabledScope(panelIndex < 0))
            {
                if (GUILayout.Button(new GUIContent("Open Rendering Debugger",
                    "Open the GPU Batcher panel in the Rendering Debugger.")))
                    OpenRenderingDebugger(panelIndex);
            }
        }

        private static void OpenRenderingDebugger(int panelIndex)
        {
            if (EditorApplication.ExecuteMenuItem("Window/Analysis/Rendering Debugger"))
                DebugManager.instance.RequestEditorWindowPanelIndex(panelIndex);
        }
    }
}
