// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Fraxinus.GPUBatcher.Editor
{
    [CustomEditor(typeof(GPUBatcherInstanceGroup))]
    internal class GPUBatcherInstanceGroupEditor : UnityEditor.Editor
    {
        static readonly GUIContent s_chunkMode = new GUIContent("Chunk Mode", "How instances are spatially partitioned into chunks.");
        static readonly GUIContent s_chunkSize = new GUIContent("Chunk Size (m)", "World-space size of each spatial grid cell.");
        static readonly GUIContent s_cullDistance = new GUIContent("Chunk Cull Distance (m)", "Max render distance per chunk. 0 = no distance culling.");

        static readonly GUIContent s_detectOverrides = new GUIContent("Detect Prefab Overrides", "When enabled, instances with different property overrides (e.g. material, shadow, LOD settings) bake as separate prototypes. When disabled, all instances of the same prefab share one prototype.");

        SerializedProperty _propChunkMode;
        SerializedProperty _propChunkSize;
        SerializedProperty _propCullDistance;
        SerializedProperty _propDetectOverrides;

        void OnEnable()
        {
            _propChunkMode = serializedObject.FindProperty("_chunkMode");
            _propChunkSize = serializedObject.FindProperty("_chunkSize");
            _propCullDistance = serializedObject.FindProperty("_cullDistance");
            _propDetectOverrides = serializedObject.FindProperty("_detectPrefabOverrides");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            GPUBatcherInspectorGUI.DrawScriptField(serializedObject);

            var group = (GPUBatcherInstanceGroup)target;

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
                EditorGUILayout.PropertyField(_propCullDistance, s_cullDistance);

            // bake-time only; ignored once baked
            if (!group.isBaked)
                EditorGUILayout.PropertyField(_propDetectOverrides, s_detectOverrides);

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();

            if (!GPUBatcherRenderer.canSubmitDraws)
            {
                EditorGUILayout.HelpBox("GPU Batcher rendering requires native BRG indirect draws and a compatible graphics device. Expand an existing bake to use regular GameObjects.", MessageType.Info);
                if (!group.isBaked) return;
            }

            if (!group.isBaked)
            {
                EditorGUILayout.HelpBox(
                    "Place instance GameObjects as children, then Bake. Each LODGroup/renderer becomes a BRG " +
                    "instance and its GameObject is destroyed - only transforms and render signatures are kept.",
                    MessageType.Info);

                EditorGUILayout.HelpBox(
                    "Baking permanently destroys the source GameObjects. Only position/rotation/scale and the " +
                    "render signature are preserved - GameObject names, tags, lightmap indices, extra components, " +
                    "and per-instance overrides will be lost. Back up your scene first. Expand can recreate " +
                    "editable GameObjects (without that lost data).",
                    MessageType.Warning);

                if (GUILayout.Button("Bake Children", GUILayout.Height(30)))
                {
                    if (!EditorUtility.DisplayDialog("Bake Instance Group",
                        "This permanently destroys the child GameObjects, keeping only their transforms and render " +
                        "data. Back up your scene first. This cannot be undone.\n\nContinue?", "Bake Instances", "Cancel"))
                        return;

                    group.Bake();
                    if (!Application.isPlaying)
                        EditorSceneManager.MarkSceneDirty(group.gameObject.scene);
                }
                return;
            }

            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Instances", group.instanceCount.ToString());
            EditorGUILayout.LabelField("Prototypes", group.prototypeCount.ToString());
            EditorGUI.indentLevel--;

            EditorGUILayout.Space();

            GUI.color = new Color(1f, 0.7f, 0.7f);
            if (GUILayout.Button("Expand to GameObjects", GUILayout.Height(24)))
            {
                group.ExpandToGameObjects();
                if (!Application.isPlaying)
                    EditorSceneManager.MarkSceneDirty(group.gameObject.scene);
            }
            GUI.color = Color.white;
        }

        #region Hierarchy Context Menu

        [MenuItem("GameObject/Fraxinus GPU Batcher/Convert to GPU Batcher Instances (Instance Group, removes original GameObjects)", false, 1)]
        static void ConvertSelectionToInstanceGroup()
        {
            var selected = GPUBatcherGameObjectGroupEditor.GatherConvertSelection();
            if (selected.Length == 0)
            {
                Debug.LogWarning("BRG: No GameObjects with MeshRenderers or LODGroups selected.");
                return;
            }

            if (!EditorUtility.DisplayDialog("Convert to GPU Batcher Instance Group",
                $"This permanently destroys {selected.Length} object(s), keeping only their transforms and render " +
                "data. Back up your scene first. This cannot be undone.\n\nContinue?", "Bake Instances", "Cancel"))
                return;

            var parent = new GameObject("BRG Instance Group");
            parent.transform.position = GPUBatcherGameObjectGroupEditor.SelectionCenter(selected);
            var group = parent.AddComponent<GPUBatcherInstanceGroup>();

            foreach (var go in selected)
                go.transform.SetParent(parent.transform, worldPositionStays: true);

            group.Bake(); // destroys the reparented source GameObjects
            if (!Application.isPlaying)
                EditorSceneManager.MarkSceneDirty(parent.scene);
            Selection.activeGameObject = parent;
        }

        [MenuItem("GameObject/Fraxinus GPU Batcher/Convert to GPU Batcher Instances (Instance Group, removes original GameObjects)", true)]
        static bool ValidateConvertSelectionToInstanceGroup() => GPUBatcherGameObjectGroupEditor.HasConvertibleSelection();

        #endregion
    }
}
