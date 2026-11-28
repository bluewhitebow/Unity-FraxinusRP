// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Fraxinus.GPUBatcher.Editor
{
    [CustomEditor(typeof(GPUBatcherGameObjectGroup))]
    internal class GPUBatcherGameObjectGroupEditor : UnityEditor.Editor
    {
        static readonly GUIContent s_chunkMode = new GUIContent("Chunk Mode", "How instances are spatially partitioned into chunks.");
        static readonly GUIContent s_chunkSize = new GUIContent("Chunk Size (m)", "World-space size of each spatial grid cell.");
        static readonly GUIContent s_cullDistance = new GUIContent("Chunk Cull Distance (m)", "Max render distance per chunk. 0 = no distance culling.");
        static readonly GUIContent s_detectOverrides = new GUIContent("Detect Prefab Overrides", "When enabled, instances with different property overrides (e.g. crossfade, shadow settings) register as separate prototypes. When disabled, all instances of the same prefab share one prototype.");
        private SerializedProperty _propChunkMode;
        private SerializedProperty _propChunkSize;
        private SerializedProperty _propCullDistance;
        private SerializedProperty _propDetectPrefabOverrides;

        private void OnEnable()
        {
            _propChunkMode = serializedObject.FindProperty("_chunkMode");
            _propChunkSize = serializedObject.FindProperty("_chunkSize");
            _propCullDistance = serializedObject.FindProperty("_cullDistance");
            _propDetectPrefabOverrides = serializedObject.FindProperty("_detectPrefabOverrides");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            GPUBatcherInspectorGUI.DrawScriptField(serializedObject);

            var group = (GPUBatcherGameObjectGroup)target;

            if (!GPUBatcherRenderer.canSubmitDraws)
            {
                EditorGUILayout.HelpBox("Regular renderers own these objects. GPU Batcher conversion requires native BRG indirect draws and a compatible graphics device.", MessageType.Info);
                if (group.isConverted) DrawRevertButton(group);
                return;
            }

            EditorGUILayout.PropertyField(_propDetectPrefabOverrides, s_detectOverrides);
            EditorGUILayout.PropertyField(_propChunkMode, s_chunkMode);
            var mode = (ChunkMode)_propChunkMode.enumValueIndex;
            if (mode == ChunkMode.Static)
            {
                // delayed field so chunk size only commits on end-of-edit, not every frame mid-drag
                EditorGUI.BeginChangeCheck();
                float newSize = EditorGUILayout.DelayedFloatField(s_chunkSize, _propChunkSize.floatValue);
                if (EditorGUI.EndChangeCheck())
                    _propChunkSize.floatValue = Mathf.Max(10f, newSize);
            }
            if (mode != ChunkMode.DynamicGlobalChunk)
            {
                EditorGUILayout.PropertyField(_propCullDistance, s_cullDistance);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();

            if (!group.isConverted)
            {
                EditorGUILayout.HelpBox(
                    "Add GameObjects with renderers as children, then click Convert. Each MeshRenderer and LODGroup becomes a separate BRG instance.",
                    MessageType.Info);

                EditorGUILayout.HelpBox(
                    "Converting modifies this scene's GameObjects. Back up or save your scene before converting.",
                    MessageType.Warning);

                if (GUILayout.Button("Convert Children to Instances", GUILayout.Height(30)))
                {
                    if (!EditorUtility.DisplayDialog("Convert to GPU Batcher Instances",
                        "This converts the child GameObjects to BRG instances and modifies the scene.\n\n" +
                        "Back up your scene first. Continue?", "Convert", "Cancel"))
                        return;

                    Undo.RecordObject(group, "Convert Children to BRG Instances");
                    group.ConvertChildren();
                    if (!Application.isPlaying)
                        EditorSceneManager.MarkSceneDirty(group.gameObject.scene);
                }
            }
            else
            {
                // stats
                EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField("Instances", group.instanceCount.ToString());
                EditorGUILayout.LabelField("Chunks", group.chunkCount.ToString());
                EditorGUILayout.LabelField("Prototypes", group.prototypeCount.ToString());
                EditorGUI.indentLevel--;

                // read-only prototype list
                if (group.prototypeCount > 0)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Prototype Sources", EditorStyles.boldLabel);
                    EditorGUI.indentLevel++;
                    for (int i = 0; i < group.prototypeSources.Count; i++)
                    {
                        var rowRect = EditorGUILayout.GetControlRect();
                        const float k_globalIdWidth = 70f;
                        var objRect = new Rect(rowRect.x, rowRect.y, rowRect.width - k_globalIdWidth - 4f, rowRect.height);
                        var idRect = new Rect(rowRect.x + rowRect.width - k_globalIdWidth, rowRect.y, k_globalIdWidth, rowRect.height);

                        EditorGUI.BeginDisabledGroup(true);
                        EditorGUI.ObjectField(objRect, $"[{i}]", group.prototypeSources[i], typeof(GameObject), false);
                        EditorGUI.EndDisabledGroup();

                        int globalIdx = group.GetGlobalProtoIndex(i);
                        var prevColor = GUI.color;
                        if (globalIdx < 0) GUI.color = new Color(1f, 0.5f, 0.5f);
                        GUI.Label(idRect, globalIdx >= 0 ? $"global: {globalIdx}" : "global: -", EditorStyles.miniLabel);
                        GUI.color = prevColor;
                    }
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space();

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Refresh", GUILayout.Height(24)))
                {
                    Undo.RecordObject(group, "Refresh BRG GameObject Group");
                    group.Refresh();
                    if (!Application.isPlaying)
                        EditorSceneManager.MarkSceneDirty(group.gameObject.scene);
                }
                if (GUILayout.Button("Register New Children", GUILayout.Height(24)))
                {
                    Undo.RecordObject(group, "Register New BRG Children");
                    group.RegisterNewChildren();
                    if (!Application.isPlaying)
                        EditorSceneManager.MarkSceneDirty(group.gameObject.scene);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space();

                DrawRevertButton(group);
            }
        }

        static void DrawRevertButton(GPUBatcherGameObjectGroup group)
        {
            GUI.color = new Color(1f, 0.7f, 0.7f);
            if (GUILayout.Button("Revert to GameObjects", GUILayout.Height(24)))
            {
                Undo.RecordObject(group, "Revert BRG GameObject Group");
                group.RevertToGameObjects();
                if (!Application.isPlaying)
                    EditorSceneManager.MarkSceneDirty(group.gameObject.scene);
            }
            GUI.color = Color.white;
        }

        #region Hierarchy Context Menu

        [MenuItem("GameObject/Fraxinus GPU Batcher/Convert to GPU Batcher Instances (GameObject Group)", false, 0)]
        static void ConvertSelectionToInstances()
        {
            var selected = GatherConvertSelection();
            if (selected.Length == 0)
            {
                Debug.LogWarning("BRG: No GameObjects with MeshRenderers or LODGroups selected.");
                return;
            }

            if (!EditorUtility.DisplayDialog("Convert to GPU Batcher Instances",
                $"This reparents {selected.Length} object(s) under a new BRG GameObject Group and modifies the scene.\n\n" +
                "Back up your scene first. Continue?", "Convert", "Cancel"))
                return;

            var parent = new GameObject("BRG GameObject Group");
            Undo.RegisterCreatedObjectUndo(parent, "Convert to GPU Batcher Instances");
            parent.transform.position = SelectionCenter(selected);

            var group = parent.AddComponent<GPUBatcherGameObjectGroup>();
            foreach (var go in selected)
                Undo.SetTransformParent(go.transform, parent.transform, "Convert to GPU Batcher Instances");

            Undo.RecordObject(group, "Convert to GPU Batcher Instances");
            group.ConvertObjects(selected);
            Selection.activeGameObject = parent;
        }

        [MenuItem("GameObject/Fraxinus GPU Batcher/Convert to GPU Batcher Instances (GameObject Group)", true)]
        static bool ValidateConvertSelection() => HasConvertibleSelection();

        // top-level selected GameObjects with a renderer/LODGroup, not already under a BRG registerer
        internal static GameObject[] GatherConvertSelection()
        {
            if (!GPUBatcherRenderer.canSubmitDraws) return System.Array.Empty<GameObject>();
            var allSelected = Selection.gameObjects;
            var selectedSet = new HashSet<GameObject>(allSelected);
            var list = new List<GameObject>(allSelected.Length);

            foreach (var go in allSelected)
            {
                if (go.GetComponentInChildren<MeshRenderer>() == null && go.GetComponent<LODGroup>() == null)
                    continue;
                if (go.GetComponentInParent<GPUBatcherRegisterer>() != null)
                    continue;

                // skip if an ancestor is also selected, to avoid duplicates
                bool ancestorSelected = false;
                for (var t = go.transform.parent; t != null; t = t.parent)
                    if (selectedSet.Contains(t.gameObject)) { ancestorSelected = true; break; }
                if (ancestorSelected) continue;

                list.Add(go);
            }
            return list.ToArray();
        }

        internal static bool HasConvertibleSelection()
        {
            if (!GPUBatcherRenderer.canSubmitDraws) return false;
            foreach (var go in Selection.gameObjects)
                if (go.GetComponentInChildren<MeshRenderer>() != null || go.GetComponent<LODGroup>() != null)
                    return true;
            return false;
        }

        internal static Vector3 SelectionCenter(GameObject[] objects)
        {
            if (objects.Length == 0) return Vector3.zero;
            Vector3 center = Vector3.zero;
            foreach (var go in objects) center += go.transform.position;
            return center / objects.Length;
        }

        #endregion
    }
}
