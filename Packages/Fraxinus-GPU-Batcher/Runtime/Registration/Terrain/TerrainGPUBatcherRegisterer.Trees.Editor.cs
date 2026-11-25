// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace Fraxinus.GPUBatcher
{
    public partial class TerrainGPUBatcherRegisterer
    {
        #region Tree Change Detection

        private long _treeFingerprint;
        private int _treeFingerprintCount = -1;

        private void UpdateTreeStateCache()
        {
            if (_terrain == null) return;
            _lastKnownTreeDistance = _terrain.treeDistance;
            _treeFingerprint = ComputeTreeFingerprint(_cachedTreeInstances);
            _treeFingerprintCount = _cachedTreeInstances?.Length ?? 0;
        }

        // hashes count + per-instance proto/transform; undo/redo doesn't fire OnTerrainChanged so we diff against this
        private long ComputeTreeFingerprint(TreeInstance[] trees)
        {
            var h = new Hash64();
            h.Add(trees?.Length ?? 0);
            if (trees != null)
            {
                for (int i = 0; i < trees.Length; i++)
                {
                    var t = trees[i];
                    h.Add(t.prototypeIndex);
                    h.Add(t.position.x);
                    h.Add(t.position.y);
                    h.Add(t.position.z);
                    h.Add(t.rotation);
                    h.Add(t.widthScale);
                    h.Add(t.heightScale);
                }
            }
            return h.ToHashCode();
        }

        // called from OnTerrainChanged(TreeInstances) and undo callback
        public void CheckForTreeChanges()
        {
            if (_terrain == null || _terrain.terrainData == null) return;
            if (!_treesRegistered)
            {
                // trees weren't registered during init (e.g. procedural spawner hadn't placed them yet)
                // check if trees exist now and do a full init
                if (_terrain.terrainData.treeInstanceCount > 0)
                {
                    RefreshTerrainDataCaches();
                    InitializeTrees();
                }
                return;
            }

            // skip rebuild when tree data is unchanged; count-first avoids the treeInstances alloc on add/remove undos
            int count = _terrain.terrainData.treeInstanceCount;
            if (count == _treeFingerprintCount
                && ComputeTreeFingerprint(_terrain.terrainData.treeInstances) == _treeFingerprint)
                return;

#if DEBUG
            if (_enableDebugLogging)
                Debug.Log($"TerrainGPUBatcherRegisterer[Trees]: tree change on {gameObject.name}, doing full rebuild");
#endif

            // full rebuild - OnTerrainChanged events are coalesced per editor frame by EditorUpdate
            RefreshTrees();
            GPUBatcherSystem.RequestSnapAllViews();
        }

        #endregion

        #region Tree Debug

        [FormerlySerializedAs("showTreeChunkGizmos")]
        [SerializeField] private bool _showTreeChunkGizmos = false;

        [ContextMenu("Refresh Trees")]
        private void EditorRefresh()
        {
            if (_registered)
                Refresh();
        }

        [ContextMenu("Log Tree Count")]
        private void EditorLogTreeCount()
        {
            if (_terrain == null) _terrain = GetComponent<Terrain>();
            Debug.Log($"Terrain {gameObject.name}: {_terrain.terrainData.treeInstanceCount} trees");
        }

        #endregion
    }
}
#endif
