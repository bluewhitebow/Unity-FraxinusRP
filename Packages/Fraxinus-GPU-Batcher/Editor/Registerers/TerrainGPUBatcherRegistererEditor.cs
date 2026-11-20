// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEngine;
using UnityEditor;

namespace Fraxinus.GPUBatcher.Editor
{
    [CustomEditor(typeof(TerrainGPUBatcherRegisterer))]
    [CanEditMultipleObjects]
    internal class TerrainGPUBatcherRegistererEditor : UnityEditor.Editor
    {
        static readonly GUIContent s_DisableBuiltIn = new GUIContent("Disable Built-in Rendering", "Disable Unity's built-in tree and detail rendering (sets drawTreesAndFoliage = false).");
        static readonly GUIContent s_TreeCullDistance = new GUIContent("Cull Distance", "Read from Terrain > Tree Distance");
        static readonly GUIContent s_ChunkSize = new GUIContent("Chunk Size (m)", "World-space size of each grid cell.");
        static readonly GUIContent s_ShowTreeChunkGizmos = new GUIContent("Show Chunk Gizmos");
        static readonly GUIContent s_PreserveProtoLayers = new GUIContent("Preserve Prototype Layers");
        static readonly GUIContent s_DetailCullDistance = new GUIContent("Cull Distance", "Read from Terrain > Detail Distance");
        static readonly GUIContent s_DetailChunkSize = new GUIContent("Detail Chunk Size (m)", "Desired world-space size of detail chunks. Will be snapped to closest multiple of terrain patch size.");
        static readonly GUIContent s_UseStreaming = new GUIContent("Use Streaming");
        static readonly GUIContent s_StreamingLimitsNote = new GUIContent("Streaming settings are global (FraxinusGPUBatcherConfig > Terrain)");
        static readonly GUIContent s_UseUnityTransforms = new GUIContent("Use Unity Transforms");
        static readonly GUIContent s_SnapCrossfadeOnStart = new GUIContent("Snap Crossfade On Start");
        static readonly GUIContent s_ShowDetailChunkGizmos = new GUIContent("Show Chunk Gizmos");
        static readonly GUIContent s_SkipBRGUpload = new GUIContent("Skip GPU Batcher Upload");
        static readonly GUIContent s_SkipTreeBRGUpload = new GUIContent("Skip GPU Batcher Upload");

        private bool _showResolvedPrefabs;
        private bool _showResolvedTreePrefabs;
        private bool _showTreeSection = true;
        private bool _showDetailSection = true;

        // cached SerializedProperties (avoid FindProperty per frame)
        private SerializedProperty _propDisableBuiltInRendering;
        private SerializedProperty _propEnableDebugLogging;
        private SerializedProperty _propTreeChunkSize;
        private SerializedProperty _propShowTreeChunkGizmos;
        private GameObject[] _displayTreePrefabs;
        private GameObject[] _displayDetailPrefabs;
        private SerializedProperty _propPreservePrototypeLayers;
        private SerializedProperty _propDetailChunkSize;
        private SerializedProperty _propUseStreaming;
        private SerializedProperty _propUseUnityTransforms;
        private SerializedProperty _propSnapCrossfadeOnStart;
        private SerializedProperty _propShowDetailChunkGizmos;
        private SerializedProperty _propDebugSkipBRGUpload;
        private SerializedProperty _propDebugSkipTreeBRGUpload;
        private Terrain _cachedTerrain;
        private int _treeProtoCount;
        private int _detailProtoCount;

        private void OnEnable()
        {
            var primary = (TerrainGPUBatcherRegisterer)target;
            _displayTreePrefabs = primary.ResolveTreePrefabs();
            _displayDetailPrefabs = primary.ResolveDetailPrefabs();

            _propDisableBuiltInRendering = serializedObject.FindProperty("disableBuiltInRendering");
            _propEnableDebugLogging = serializedObject.FindProperty("enableDebugLogging");
            _propTreeChunkSize = serializedObject.FindProperty("treeChunkSize");
            _propShowTreeChunkGizmos = serializedObject.FindProperty("showTreeChunkGizmos");
            _propPreservePrototypeLayers = serializedObject.FindProperty("preservePrototypeLayers");
            _propDetailChunkSize = serializedObject.FindProperty("detailChunkSize");
            _propUseStreaming = serializedObject.FindProperty("useStreaming");
            _propUseUnityTransforms = serializedObject.FindProperty("useUnityTransforms");
            _propSnapCrossfadeOnStart = serializedObject.FindProperty("snapCrossfadeOnStart");
            _propShowDetailChunkGizmos = serializedObject.FindProperty("showDetailChunkGizmos");
            _propDebugSkipBRGUpload = serializedObject.FindProperty("debugSkipBRGUpload");
            _propDebugSkipTreeBRGUpload = serializedObject.FindProperty("debugSkipTreeBRGUpload");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            GPUBatcherInspectorGUI.DrawScriptField(serializedObject);
            var registerer = (TerrainGPUBatcherRegisterer)target;
            if (_cachedTerrain == null) _cachedTerrain = registerer.GetComponent<Terrain>();
            var terrain = _cachedTerrain;

            // treePrototypes/detailPrototypes getters allocate; refresh counts only on layout, not every repaint
            if (Event.current.type == EventType.Layout && terrain != null && terrain.terrainData != null)
            {
                _treeProtoCount = terrain.terrainData.treePrototypes.Length;
                _detailProtoCount = terrain.terrainData.detailPrototypes.Length;
            }

            #region Shared Settings
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_propDisableBuiltInRendering, s_DisableBuiltIn);
            EditorGUILayout.PropertyField(_propSnapCrossfadeOnStart, s_SnapCrossfadeOnStart);
            EditorGUILayout.PropertyField(_propEnableDebugLogging);

            EditorGUILayout.Space();
            #endregion

            #region Trees
            _showTreeSection = EditorGUILayout.Foldout(_showTreeSection, "Trees", true, EditorStyles.foldoutHeader);
            if (_showTreeSection)
            {
                EditorGUI.indentLevel++;

                // tree distance from terrain (read-only)
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.FloatField(s_TreeCullDistance, terrain != null ? terrain.treeDistance : 0f);
                EditorGUI.EndDisabledGroup();

                int resolvedTreeCount = _displayTreePrefabs != null ? _displayTreePrefabs.Length : 0;
                _showResolvedTreePrefabs = EditorGUILayout.Foldout(_showResolvedTreePrefabs, $"Resolved Prefabs ({resolvedTreeCount})", true);
                if (_showResolvedTreePrefabs && resolvedTreeCount > 0)
                {
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(true);
                    for (int i = 0; i < resolvedTreeCount; i++)
                    {
                        var prefab = _displayTreePrefabs[i];
                        string label = prefab != null ? $"[{i}] {prefab.name}" : $"[{i}] (none)";
                        EditorGUILayout.ObjectField(label, prefab, typeof(GameObject), false);
                    }
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                }

                // chunking
                EditorGUILayout.PropertyField(_propTreeChunkSize, s_ChunkSize);

                EditorGUILayout.PropertyField(_propShowTreeChunkGizmos, s_ShowTreeChunkGizmos);
                EditorGUILayout.PropertyField(_propDebugSkipTreeBRGUpload, s_SkipTreeBRGUpload);

                // tree status
                if (terrain != null && terrain.terrainData != null)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField($"Prototypes: {_treeProtoCount}  |  Instances: {terrain.terrainData.treeInstanceCount:N0}", EditorStyles.miniLabel);

                    if (registerer.IsTreesRegistered)
                    {
                        EditorGUILayout.LabelField($"Registered: {registerer.TreeInstanceCount:N0} in {registerer.TreeChunkCount} chunks", EditorStyles.miniLabel);
                    }
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            #endregion

            #region Details
            _showDetailSection = EditorGUILayout.Foldout(_showDetailSection, "Details", true, EditorStyles.foldoutHeader);
            if (_showDetailSection)
            {
                EditorGUI.indentLevel++;

                // resolved prefab list (readonly)
                EditorGUILayout.HelpBox("Tip: Add the 'GPU Batcher Terrain Detail Override' component to your detail prefab to render details with LOD groups!", MessageType.Info);
                int resolvedCount = _displayDetailPrefabs != null ? _displayDetailPrefabs.Length : 0;
                _showResolvedPrefabs = EditorGUILayout.Foldout(_showResolvedPrefabs, $"Resolved Prefabs ({resolvedCount})", true);
                if (_showResolvedPrefabs && resolvedCount > 0)
                {
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(true);
                    for (int i = 0; i < resolvedCount; i++)
                    {
                        var prefab = _displayDetailPrefabs[i];
                        string label = prefab != null ? $"[{i}] {prefab.name}" : $"[{i}] (none)";
                        EditorGUILayout.ObjectField(label, prefab, typeof(GameObject), false);
                    }
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(4);

                // settings
                EditorGUILayout.PropertyField(_propPreservePrototypeLayers, s_PreserveProtoLayers);

                // detail distance from terrain (read-only)
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.FloatField(s_DetailCullDistance, terrain != null ? terrain.detailObjectDistance : 0f);
                EditorGUI.EndDisabledGroup();

                // detail chunk size
                EditorGUILayout.PropertyField(_propDetailChunkSize, s_DetailChunkSize);

                // actual chunk size after snapping to patch size
                if (terrain != null && terrain.terrainData != null)
                {
                    float patchWorldSize = terrain.terrainData.size.x / terrain.terrainData.detailPatchCount;
                    float desiredChunkSize = _propDetailChunkSize.floatValue;
                    int patchesPerChunk = Mathf.Max(1, Mathf.RoundToInt(desiredChunkSize / patchWorldSize));
                    float actualChunkSize = patchesPerChunk * patchWorldSize;

                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.FloatField(new GUIContent("  -> Actual Chunk Size (m)", $"Snapped to {patchesPerChunk}*{patchesPerChunk} patches ({patchesPerChunk * patchesPerChunk} total)"), actualChunkSize);
                    EditorGUILayout.FloatField(new GUIContent("  -> Patch Size (m)"), patchWorldSize);
                    EditorGUI.EndDisabledGroup();
                }

                EditorGUILayout.Space(4);

                // streaming
                EditorGUILayout.PropertyField(_propUseStreaming, s_UseStreaming);

                if (_propUseStreaming.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.HelpBox(s_StreamingLimitsNote.text, MessageType.None);
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(4);

                // transform generation
                EditorGUILayout.PropertyField(_propUseUnityTransforms, s_UseUnityTransforms);

                EditorGUILayout.Space(4);

                // detail debug
                EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(_propShowDetailChunkGizmos, s_ShowDetailChunkGizmos);
                EditorGUILayout.PropertyField(_propDebugSkipBRGUpload, s_SkipBRGUpload);

                // detail status
                if (terrain != null && terrain.terrainData != null)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField($"Prototypes: {_detailProtoCount}  |  Patches: {terrain.terrainData.detailPatchCount}^2", EditorStyles.miniLabel);

                    if (registerer.IsDetailsRegistered)
                    {
                        EditorGUILayout.LabelField($"Loaded: {registerer.DetailLoadedInstanceCount:N0} instances, {registerer.DetailLoadedChunkCount} detail chunks, {registerer.DetailAllocatedChunkCount} GPU Batcher chunks", EditorStyles.miniLabel);
                        if (registerer.DetailPendingChunkCount > 0)
                            EditorGUILayout.LabelField($"Pending: {registerer.DetailPendingChunkCount} chunks", EditorStyles.miniLabel);
                    }
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            #endregion

            #region Actions
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh All"))
            {
                foreach (var t in targets)
                {
                    var reg = (TerrainGPUBatcherRegisterer)t;
                    reg.Refresh();
                }
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Refresh"))
            {
                foreach (var t in targets)
                {
                    var reg = (TerrainGPUBatcherRegisterer)t;
                    reg.Refresh();
                }
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();
            #endregion

            serializedObject.ApplyModifiedProperties();
        }
    }
}
