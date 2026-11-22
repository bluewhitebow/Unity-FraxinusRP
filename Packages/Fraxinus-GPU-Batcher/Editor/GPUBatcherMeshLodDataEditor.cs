// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEngine;
using UnityEditor;

namespace Fraxinus.GPUBatcher.Editor
{
    [CustomEditor(typeof(GPUBatcherMeshLodData))]
    [CanEditMultipleObjects]
    internal class GPUBatcherMeshLodDataEditor : UnityEditor.Editor
    {
        static readonly GUIContent s_fadeMode = new GUIContent("Fade Mode");
        static readonly GUIContent s_meshLodNumLevels = new GUIContent("Num LODs", "Number of GPU Batcher LOD levels. Each maps to a mesh LOD index.");
        static readonly GUIContent s_meshLodIndex = new GUIContent("Mesh LOD Index", "Which mesh LOD index to draw for this GPU Batcher LOD level.");
        static readonly GUIContent s_screenSize = new GUIContent("Screen Size", "Screen size threshold for transitioning to this LOD level.");
        static readonly GUIContent s_lodFadeWidth = new GUIContent("Fade Width", "Crossfade transition width for this LOD level.");

        private SerializedProperty _propMeshLodNumLevels;
        private SerializedProperty _propMeshLodIndices;
        private SerializedProperty _propMeshLodScreenSizes;
        private SerializedProperty _propMeshLodFadeWidths;
        private SerializedProperty _propMeshLodFadeMode;

        private void OnEnable()
        {
            _propMeshLodNumLevels = serializedObject.FindProperty("meshLodNumLevels");
            _propMeshLodIndices = serializedObject.FindProperty("meshLodIndices");
            _propMeshLodScreenSizes = serializedObject.FindProperty("meshLodScreenSizes");
            _propMeshLodFadeWidths = serializedObject.FindProperty("meshLodFadeWidths");
            _propMeshLodFadeMode = serializedObject.FindProperty("meshLodFadeMode");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            GPUBatcherInspectorGUI.DrawScriptField(serializedObject);
            var meshLodData = (GPUBatcherMeshLodData)target;
            int meshLodCount = GetMeshLodCount(meshLodData);
            bool hasMeshLods = meshLodCount > 1;

            if (!hasMeshLods)
            {
                EditorGUILayout.HelpBox("Mesh has no LODs. Add a mesh with multiple LOD levels.", MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.LabelField($"Mesh has {meshLodCount} LODs", EditorStyles.miniLabel);

            EditorGUILayout.PropertyField(_propMeshLodNumLevels, s_meshLodNumLevels);

            EditorGUILayout.PropertyField(_propMeshLodFadeMode, s_fadeMode);

            if (meshLodData.meshLodFadeMode == MinScreenSizeFadeMode.AnimatedCrossfade)
                EditorGUILayout.LabelField("Change animated crossfade duration using Prototype Extra Data.", EditorStyles.miniLabel);

            bool showFadeWidth = meshLodData.meshLodFadeMode == MinScreenSizeFadeMode.Crossfade;
            int numLevels = meshLodData.meshLodNumLevels;

            const int k_maxLevels = 8;
            if (_propMeshLodIndices.arraySize < k_maxLevels)
                _propMeshLodIndices.arraySize = k_maxLevels;
            if (_propMeshLodScreenSizes.arraySize < k_maxLevels)
                _propMeshLodScreenSizes.arraySize = k_maxLevels;
            if (_propMeshLodFadeWidths.arraySize < k_maxLevels)
                _propMeshLodFadeWidths.arraySize = k_maxLevels;

            int maxIndex = Mathf.Max(0, meshLodCount - 1);

            EditorGUILayout.Space(2);
            for (int i = 0; i < numLevels; i++)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField($"Mesh LOD {i}", EditorStyles.boldLabel);

                EditorGUI.indentLevel++;

                var indexProp = _propMeshLodIndices.GetArrayElementAtIndex(i);
                indexProp.intValue = EditorGUILayout.IntSlider(s_meshLodIndex, indexProp.intValue, 0, maxIndex);

                var screenSizeProp = _propMeshLodScreenSizes.GetArrayElementAtIndex(i);
                EditorGUI.BeginDisabledGroup(i == 0);
                screenSizeProp.floatValue = EditorGUILayout.Slider(s_screenSize, screenSizeProp.floatValue, 0f, 1f);
                EditorGUI.EndDisabledGroup();
                if (i == 0)
                    screenSizeProp.floatValue = 1f;

                if (showFadeWidth)
                {
                    var fadeWidthProp = _propMeshLodFadeWidths.GetArrayElementAtIndex(i);
                    fadeWidthProp.floatValue = EditorGUILayout.Slider(s_lodFadeWidth, fadeWidthProp.floatValue, 0f, 1f);
                }

                EditorGUI.indentLevel--;

                EditorGUILayout.EndVertical();

                if (i < numLevels - 1)
                    EditorGUILayout.Space(2);
            }

            // culled level
            var extraData = meshLodData.GetComponent<GPUBatcherPrototypeExtraData>();
            if (extraData != null && extraData.enabled)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Culled", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField($"Min Screen Size: {extraData.minScreenSize:F2}% (from Prototype Extra Data)", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
                EditorGUILayout.EndVertical();
            }

            // enforce monotonically decreasing screen sizes
            for (int i = 1; i < numLevels; i++)
            {
                var prev = _propMeshLodScreenSizes.GetArrayElementAtIndex(i - 1);
                var curr = _propMeshLodScreenSizes.GetArrayElementAtIndex(i);
                if (curr.floatValue > prev.floatValue)
                    curr.floatValue = prev.floatValue;
            }

            // extract screen sizes from mesh LOD curve
            EditorGUILayout.Space(2);
            {
                var mf = meshLodData.GetComponent<MeshFilter>();
                var mr = meshLodData.GetComponent<MeshRenderer>();
                bool canExtract = mf != null && mf.sharedMesh != null && mr != null
                    && GetMeshLodCurveParams(mf.sharedMesh, mr, out _, out _, out _);

#if UNITY_6000_2_OR_NEWER
                if (mr != null)
                {
                    EditorGUI.BeginChangeCheck();
                    float newBias = EditorGUILayout.Slider(
                        new GUIContent("LOD Selection Bias", "MeshRenderer LOD selection bias. Positive = lower quality sooner, negative = higher quality longer."),
                        mr.meshLodSelectionBias, -10f, 10f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(mr, "Change LOD Selection Bias");
                        mr.meshLodSelectionBias = newBias;
                    }
                }
#endif

                EditorGUI.BeginDisabledGroup(!canExtract);
                if (GUILayout.Button("Extract Screen Sizes from Mesh LOD Curve"))
                {
                    GetMeshLodCurveParams(mf.sharedMesh, mr, out float slope, out _, out float rBias);
                    int m0 = _propMeshLodIndices.GetArrayElementAtIndex(0).intValue;

                    _propMeshLodScreenSizes.GetArrayElementAtIndex(0).floatValue = 1f;
                    for (int i = 1; i < numLevels; i++)
                    {
                        int mi = _propMeshLodIndices.GetArrayElementAtIndex(i).intValue;
                        float exponent = (m0 - mi + rBias) / slope;
                        float screenSize = Mathf.Clamp01(Mathf.Pow(2f, exponent));
                        _propMeshLodScreenSizes.GetArrayElementAtIndex(i).floatValue = screenSize;
                    }
                }
                EditorGUI.EndDisabledGroup();
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static int GetMeshLodCount(GPUBatcherMeshLodData meshLodData)
        {
            var meshFilter = meshLodData.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
                return 1;

#if UNITY_6000_2_OR_NEWER
            return meshFilter.sharedMesh.lodCount;
#else
            return 1;
#endif
        }

        private static bool GetMeshLodCurveParams(Mesh mesh, MeshRenderer meshRenderer,
            out float slope, out float bias, out float rendererBias)
        {
            slope = 0f;
            bias = 0f;
            rendererBias = 0f;

#if UNITY_6000_2_OR_NEWER
            if (mesh.lodCount <= 1) return false;

            var curve = mesh.lodSelectionCurve;
            slope = curve.lodSlope;
            bias = curve.lodBias;
            rendererBias = meshRenderer != null ? meshRenderer.meshLodSelectionBias : 0f;

            return slope != 0f;
#else
            return false;
#endif
        }
    }
}
