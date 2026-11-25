using System.Collections.Generic;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering
{
    internal abstract partial class ProbeVolumeBaking<TVolume>
    {
        protected ProbeVolumeLayout m_Layout;
        protected Bounds globalBounds;
        protected Vector3Int minCellPosition = Vector3Int.one * int.MaxValue;
        protected Vector3Int maxCellPosition = Vector3Int.one * int.MinValue;
        protected Vector3Int cellCount;
        int pvHashesAtBakeStart = -1;

        internal bool isBakingSceneSubset => partialBakeSceneList != null;
        internal List<ProbeVolume> GetProbeVolumeList() => ProbeVolumePlacement.GetProbeVolumeList(partialBakeSceneList);

        bool InitializeBake()
        {
            if (!volume.isInitialized || !volume.enabledBySRP) return false;

            // Verify to make sure we can still do it. Shortcircuting so that we don't run CanFreezePlacement unless is needed.
            isFreezingPlacement = isFreezingPlacement && CanFreezePlacement();
            if (!isFreezingPlacement && !EnsurePerSceneDataInOpenScenes())
                return false;

            if (volume.perSceneDataList.Count == 0) return false;

            var sceneDataList = GetPerSceneDataList();
            if (sceneDataList.Count == 0) return false;

            var pvList = GetProbeVolumeList();
            if (pvList.Count == 0) return false; // We have no probe volumes.

            CachePVHashes(pvList);

            if (!SetBakingContext(sceneDataList))
                return false;

            m_Layout = bakingSet.layout;
            if (isFreezingPlacement)
            {
                ModifyProfileFromLoadedData(bakingSet);
            }
            else
            {
                FindWorldBounds();
            }

            // Get min/max
            CellCountInDirections(out minCellPosition, out maxCellPosition, m_Layout.cellSizeInMeters, m_Layout.probeOffset);
            cellCount = maxCellPosition + Vector3Int.one - minCellPosition;

            if (!volume.EnsureCurrentBakingSet(bakingSet))
                return false;

            PrepareBackend();

            foreach (var data in volume.perSceneDataList)
            {
                // It can be null if the scene was never added to a baking set and we are baking in single scene mode, in that case we don't have a baking set for it yet and we need to skip
                if (data.serializedBakingSet != null)
                    data.Initialize(backendId);
            }

            return true;
        }

        internal override void Clear()
        {
            m_BakePrepared = false;
            var targetSet = bakingSet;
            if (targetSet == null)
                return;

            // Asset deletion is independent of SRP initialization/enabling.
            // Only detach scenes which referenced this set at their last bake.
            var sceneData = Object.FindObjectsByType<ProbeVolumePerSceneData>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var data in sceneData)
            {
                if (data.serializedBakingSet != targetSet)
                    continue;

                data.Clear(backendId);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(data.gameObject.scene);
            }

            // Process scene removals before deleting the files so loaded cells
            // release their CPU data and streaming handles first.
            if (volume.currentBakingSet == targetSet)
                volume.Clear();

            targetSet.Clear(backendId);
            ClearAlgorithmData();
        }

        protected bool SetBakingContext(List<ProbeVolumePerSceneData> perSceneData)
        {
            bool isBakingSingleScene = false;
            for (int i = 0; i < perSceneData.Count; ++i)
            {
                var data = perSceneData[i];
                var scene = data.gameObject.scene;
                var bakingSet = ProbeVolumeBakingSet.GetBakingSetForScene(scene);
                if (bakingSet != null && bakingSet.singleSceneMode)
                {
                    isBakingSingleScene = true;
                    break;
                }
            }

            // We need to make sure all scenes we are baking are from the same baking set.
            // TODO: This should be ensured by the controlling panel, until we have that we need to assert.
            for (int i = 0; i < perSceneData.Count; ++i)
            {
                var data = perSceneData[i];
                var sceneGUID = data.sceneGUID;
                var bakingSet = ProbeVolumeBakingSet.GetBakingSetForScene(sceneGUID);

                if (bakingSet == null)
                {
                    if (isBakingSingleScene)
                        continue;

                    var sceneName = data.gameObject.scene.name;
                    Debug.LogError($"Scene '{sceneName}' does not belong to any Baking Set. Please add it to a Baking Set in the Probe Volumes tab of the Lighting Window.");
                    return false;
                }

                bakingSet.SetActiveScenario(bakingSet.lightingScenario, verbose: false); // Ensure we are not blending any other scenario.
                bakingSet.BlendLightingScenario(null, 0.0f);

                if (this.bakingSet != bakingSet && !this.bakingSet.IsEquivalent(bakingSet))
                    return false;
            }

            return true;
        }

        bool EnsurePerSceneDataInOpenScenes()
        {
            var activeScene = SceneManager.GetActiveScene();

            var activeSet = ProbeVolumeBakingSet.GetBakingSetForScene(activeScene);
            if (activeSet == null && ProbeVolumeBakingSet.SceneHasProbeVolumes(activeScene.GetGUID()))
            {
                Debug.LogError($"Active scene at {activeScene.path} is not part of any baking set.");
                return false;
            }

            // We assume that all the per scene data for all the scenes in the set have been set with the scene been saved at least once. However we also update the scenes that are currently loaded anyway for security.
            // and to have a new trigger to update the bounds we have.
            int openedScenesCount = SceneManager.sceneCount;
            for (int i = 0; i < openedScenesCount; ++i)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;

                ProbeVolumeBakingSet.OnSceneSaving(scene); // We need to perform the same actions we do when the scene is saved.
                var sceneBakingSet = ProbeVolumeBakingSet.GetBakingSetForScene(scene);

                if (sceneBakingSet != null && sceneBakingSet != activeSet && ProbeVolumeBakingSet.SceneHasProbeVolumes(scene.GetGUID()))
                {
                    Debug.LogError($"Scene at {scene.path} is loaded and has probe volumes, but not part of the same baking set as the active scene. This will result in an error. Please make sure all loaded scenes are part of the same baking set.");
                    return false;
                }
            }

            // Make sure there are no remaining per scene data in scenes where probe volume was deleted
            // iterate in reverse order because destroy will pop element from the array
            for (int i = volume.perSceneDataList.Count - 1; i >= 0; i--)
            {
                var perSceneData = volume.perSceneDataList[i];
                bool usedByOtherBackend = false;
                if (perSceneData.serializedBakingSet != null)
                {
                    foreach (var backend in ProbeReferenceVolumeProvider.backends)
                    {
                        if (backend.id == backendId)
                            continue;
                        var cells = perSceneData.serializedBakingSet.GetSceneCellIndexList(backend.id, perSceneData.sceneGUID);
                        usedByOtherBackend |= cells != null && cells.Count > 0;
                    }
                }
                if (!ProbeVolumeBakingSet.SceneHasProbeVolumes(perSceneData.sceneGUID) && !usedByOtherBackend)
                    CoreUtils.Destroy(perSceneData.gameObject);
            }

            return true;
        }

        void CachePVHashes(List<ProbeVolume> probeVolumes)
        {
            pvHashesAtBakeStart = 0;
            foreach (var pv in probeVolumes)
            {
                pvHashesAtBakeStart += pvHashesAtBakeStart * 23 + pv.GetHashCode();
            }
        }

        protected void CheckPVChanges()
        {
            // If we have baking in flight.
            if (ProbeVolumeBakingProvider.isRunning && (GUIUtility.hotControl == 0))
            {
                var pvList = GetProbeVolumeList();
                int currHash = 0;
                foreach (var pv in pvList)
                {
                    currHash += currHash * 23 + pv.GetHashCode();
                }

                if (currHash != pvHashesAtBakeStart)
                    ProbeVolumeBakingProvider.RestartAfterSceneChanges(this);
            }
        }

        protected void CellCountInDirections(out Vector3Int minCellPositionXYZ, out Vector3Int maxCellPositionXYZ, float cellSizeInMeters, Vector3 worldOffset)
        {
            // Sync with ProbeVolumeLayout.PositionToCell
            minCellPositionXYZ = Vector3Int.FloorToInt((globalBounds.min - worldOffset) / cellSizeInMeters);
            maxCellPositionXYZ = Vector3Int.FloorToInt((globalBounds.max - worldOffset) / cellSizeInMeters);
        }

        void FindWorldBounds()
        {
            var prv = volume;
            prv.clearAssetsOnVolumeClear = true;

            var activeSet = bakingSet;

            bool hasFoundBounds = false;

            foreach (var sceneGUID in activeSet.sceneGUIDs)
            {
                var bakeData = activeSet.GetSceneBakeData(sceneGUID);
                if (bakeData.hasProbeVolume)
                {
                    if (hasFoundBounds)
                    {
                        globalBounds.Encapsulate(bakeData.bounds);
                    }
                    else
                    {
                        globalBounds = bakeData.bounds;
                        hasFoundBounds = true;
                    }
                }
            }

            volume.globalBounds = globalBounds;
        }

        protected List<ProbeVolumePerSceneData> GetPerSceneDataList()
        {
            var fullPerSceneDataList = volume.perSceneDataList;
            if (!isBakingSceneSubset)
                return fullPerSceneDataList;

            List<ProbeVolumePerSceneData> usedPerSceneDataList = new ();
            foreach (var sceneData in fullPerSceneDataList)
            {
                if (partialBakeSceneList.Contains(sceneData.gameObject.scene.GetGUID()))
                    usedPerSceneDataList.Add(sceneData);
            }
            return usedPerSceneDataList;
        }

        protected void ModifyProfileFromLoadedData(ProbeVolumeBakingSet bakingSet)
        {
            m_Layout = new ProbeVolumeLayout(bakingSet.bakedSimplificationLevels, bakingSet.bakedMinDistanceBetweenProbes, bakingSet.bakedProbeOffset);
            globalBounds = bakingSet.globalBounds;
        }

    }
}
