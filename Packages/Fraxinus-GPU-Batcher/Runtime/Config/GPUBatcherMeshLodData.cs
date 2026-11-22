// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    /// <summary>Mesh LOD configuration for BRG prototypes (Unity 6.2+). Presence on a GameObject enables mesh LOD extraction.</summary>
    [AddComponentMenu("Fraxinus GPU Batcher/Mesh LOD Data")]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class GPUBatcherMeshLodData : MonoBehaviour
    {
        [Range(1, 8)]
        [Tooltip("Number of BRG LOD levels to use. Each maps to a mesh LOD index.")]
        public int meshLodNumLevels = 4;

        [Tooltip("Per BRG LOD level: which mesh LOD index to draw. Always 8 elements; only the first meshLodNumLevels are used.")]
        public int[] meshLodIndices = { 0, 1, 2, 3, 4, 5, 6, 7 };

        [Tooltip("Per BRG LOD level: screen size threshold for transitioning to this LOD. Always 8 elements; only the first meshLodNumLevels are used.")]
        public float[] meshLodScreenSizes = { 1f, 0.5f, 0.25f, 0.1f, 0.05f, 0.025f, 0.01f, 0.005f };

        [Tooltip("Per BRG LOD level: crossfade transition width (only used with stateless Crossfade mode). Always 8 elements; only the first meshLodNumLevels are used.")]
        public float[] meshLodFadeWidths = { 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f };

        [Tooltip("Crossfade mode for Mesh LOD inter-LOD transitions. " +
                 "None = hard cut. Crossfade = stateless fade zone. " +
                 "Animated = time-based fade using LOD Fade Duration from config.")]
        public MinScreenSizeFadeMode meshLodFadeMode = MinScreenSizeFadeMode.None;

#if UNITY_EDITOR
        private int _prevMeshLodNumLevels;
        private int[] _prevMeshLodIndices;
        private float[] _prevMeshLodScreenSizes;
        private float[] _prevMeshLodFadeWidths;
        private MinScreenSizeFadeMode _prevMeshLodFadeMode;

        private void OnValidate()
        {
            EnsureArraySizes();

            bool changed = _prevMeshLodNumLevels != meshLodNumLevels ||
                           !ArrayEquals(_prevMeshLodIndices, meshLodIndices) ||
                           !ArrayEquals(_prevMeshLodScreenSizes, meshLodScreenSizes) ||
                           !ArrayEquals(_prevMeshLodFadeWidths, meshLodFadeWidths) ||
                           _prevMeshLodFadeMode != meshLodFadeMode;

            CachePrevValues();

            if (changed)
            {
                var root = gameObject;
                while (root.transform.parent != null)
                    root = root.transform.parent.gameObject;

                GPUBatcherRenderer.MarkPrototypeDirty(root);
            }
        }

        private void CachePrevValues()
        {
            _prevMeshLodNumLevels = meshLodNumLevels;
            _prevMeshLodIndices = (int[])meshLodIndices?.Clone();
            _prevMeshLodScreenSizes = (float[])meshLodScreenSizes?.Clone();
            _prevMeshLodFadeWidths = (float[])meshLodFadeWidths?.Clone();
            _prevMeshLodFadeMode = meshLodFadeMode;
        }

        internal void EnsureArraySizes()
        {
            const int k_maxLevels = 8;
            if (meshLodIndices == null || meshLodIndices.Length < k_maxLevels)
            {
                var old = meshLodIndices;
                meshLodIndices = new int[k_maxLevels];
                for (int i = 0; i < k_maxLevels; i++)
                    meshLodIndices[i] = (old != null && i < old.Length) ? old[i] : i;
            }
            if (meshLodScreenSizes == null || meshLodScreenSizes.Length < k_maxLevels)
            {
                var old = meshLodScreenSizes;
                meshLodScreenSizes = new float[k_maxLevels];
                for (int i = 0; i < k_maxLevels; i++)
                {
                    if (old != null && i < old.Length)
                        meshLodScreenSizes[i] = old[i];
                    else
                        meshLodScreenSizes[i] = (i > 0) ? meshLodScreenSizes[i - 1] * 0.5f : 1f;
                }
            }
            if (meshLodFadeWidths == null || meshLodFadeWidths.Length < k_maxLevels)
            {
                var old = meshLodFadeWidths;
                meshLodFadeWidths = new float[k_maxLevels];
                for (int i = 0; i < k_maxLevels; i++)
                    meshLodFadeWidths[i] = (old != null && i < old.Length) ? old[i] : 0.2f;
            }
        }

        private static bool ArrayEquals(int[] a, int[] b)
        {
            if (a == b) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static bool ArrayEquals(float[] a, float[] b)
        {
            if (a == b) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private void Reset() => CachePrevValues();
        private void Awake() => CachePrevValues();
        private void OnEnable() => CachePrevValues();
#endif
    }
}
