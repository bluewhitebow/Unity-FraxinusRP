#if UNITY_EDITOR

using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.SceneManagement;
using Unity.Mathematics;

namespace UnityEngine.Rendering
{
    // Everything here is only needed in editor for baking or managing baking sets.
    public partial class ProbeVolumeBakingSet
    {
        [Serializable]
        internal class SceneBakeData
        {
            public bool hasProbeVolume = false;
            public bool bakeScene = true;
            public Bounds bounds = new();
        }

        [SerializeField]
        SerializedDictionary<string, SceneBakeData> m_SceneBakeData = new();
        internal static Dictionary<string, ProbeVolumeBakingSetWeakReference> sceneToBakingSet = new Dictionary<string, ProbeVolumeBakingSetWeakReference>();

        /// <summary>
        /// Tries to add a scene to the baking set.
        /// </summary>
        /// <param name ="guid">The GUID of the scene to add.</param>
        /// <returns>Whether the scene was successfull added to the baking set.</returns>
        public bool TryAddScene(string guid)
        {
            var sceneSet = GetBakingSetForScene(guid);
            if (sceneSet != null)
                return false;
            AddScene(guid);
            return true;
        }

        internal void AddScene(string guid, SceneBakeData bakeData = null)
        {
            m_SceneGUIDs.Add(guid);
            m_SceneBakeData.Add(guid, bakeData != null ? bakeData : new SceneBakeData());
            sceneToBakingSet[guid] = new ProbeVolumeBakingSetWeakReference(this);

            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Removes a scene from the baking set.
        /// </summary>
        /// <param name ="guid">The GUID of the scene to remove.</param>
        public void RemoveScene(string guid)
        {
            m_SceneGUIDs.Remove(guid);
            m_SceneBakeData.Remove(guid);
            sceneToBakingSet.Remove(guid);

            EditorUtility.SetDirty(this);
        }

        internal void SetScene(string guid, int index, SceneBakeData bakeData = null)
        {
            var previousSceneGUID = m_SceneGUIDs[index];
            m_SceneGUIDs[index] = guid;
            sceneToBakingSet.Remove(previousSceneGUID);
            sceneToBakingSet[guid] = new ProbeVolumeBakingSetWeakReference(this);
            m_SceneBakeData.Add(guid, bakeData != null ? bakeData : new SceneBakeData());

            EditorUtility.SetDirty(this);
        }

        internal void MoveSceneToBakingSet(string guid, int index)
        {
            var oldBakingSet = GetBakingSetForScene(guid);
            var oldBakeData = oldBakingSet.GetSceneBakeData(guid);

            if (oldBakingSet.singleSceneMode)
                AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(oldBakingSet));
            else
                oldBakingSet.RemoveScene(guid);

            if (index == -1)
                AddScene(guid, oldBakeData);
            else
                SetScene(guid, index, oldBakeData);
        }

        /// <summary>
        /// Changes the baking status of a scene. Objects in scenes disabled for baking will still contribute to
        /// lighting for other scenes.
        /// </summary>
        /// <param name ="guid">The GUID of the scene to remove.</param>
        /// <param name ="enableForBaking">Wheter or not this scene should be included when baking lighting.</param>
        public void SetSceneBaking(string guid, bool enableForBaking)
        {
            if (m_SceneBakeData.TryGetValue(guid, out var sceneData))
                sceneData.bakeScene = enableForBaking;

            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Changes the baking status of all scenes. Objects in scenes disabled for baking will still contribute to
        /// lighting for other scenes.
        /// </summary>
        /// <param name="enableForBaking">Wheter or not scenes should be included when baking lighting.</param>
        public void SetAllSceneBaking(bool enableForBaking)
        {
            foreach (var kvp in m_SceneBakeData)
                kvp.Value.bakeScene = enableForBaking;

            EditorUtility.SetDirty(this);
        }


        /// <summary>
        /// Tries to add a lighting scenario to the baking set.
        /// </summary>
        /// <param name ="name">The name of the scenario to add.</param>
        /// <returns>Whether the scenario was successfully created.</returns>
        public bool TryAddScenario(string name)
        {
            if (m_LightingScenarios.Contains(name))
                return false;
            m_LightingScenarios.Add(name);
            EditorUtility.SetDirty(this);

            return true;
        }

        internal string CreateScenario(string name)
        {
            int index = 1;
            string renamed = name;
            while (!TryAddScenario(renamed))
                renamed = $"{name} ({index++})";

            return renamed;
        }

        /// <summary>
        /// Tries to remove a given scenario from the baking set. This will delete associated baked data.
        /// </summary>
        /// <param name="name">The name of the scenario to remove</param>
        /// <returns>Whether the scenario was successfully deleted.</returns>
        public bool RemoveScenario(string name)
        {
            foreach (var data in backendBakedData)
                data.RemoveScenario(name);

            EnsureBackendLayouts();
            foreach (var cellDataMapForBackend in m_BackendCellDataMaps.Values)
            foreach (var cellData in cellDataMapForBackend.Values)
            {
                if (cellData.TryGetScenarioEntry(name, out _))
                    cellData.RemoveScenarioEntry(name);
            }

            bool removed = m_LightingScenarios.Remove(name);
            if (removed)
                RemapScenarioSelection(name, m_LightingScenarios.Count > 0 ? m_LightingScenarios[0] : TProbeReferenceVolumeBase.defaultLightingScenario, true);
            EditorUtility.SetDirty(this);
            return removed;
        }

        internal ProbeVolumeBakingSet Clone()
        {
            var newSet = CreateInstance<ProbeVolumeBakingSet>();

            // We don't want to clone everything in the set
            // Especially don't copy reference to baked data !
            newSet.probeOffset = probeOffset;
            newSet.simplificationLevels = simplificationLevels;
            newSet.minDistanceBetweenProbes = minDistanceBetweenProbes;
            newSet.renderersLayerMask = renderersLayerMask;
            newSet.minRendererVolumeSize = minRendererVolumeSize;
            newSet.skyOcclusion = skyOcclusion;
            newSet.skyOcclusionBakingSamples = skyOcclusionBakingSamples;
            newSet.skyOcclusionBakingBounces = skyOcclusionBakingBounces;
            newSet.skyOcclusionAverageAlbedo = skyOcclusionAverageAlbedo;
            newSet.skyOcclusionBackFaceCulling = skyOcclusionBackFaceCulling;
            newSet.skyOcclusionShadingDirection = skyOcclusionShadingDirection;
            newSet.useRenderingLayers = useRenderingLayers;
            newSet.renderingLayerMasks = renderingLayerMasks != null ? (ProbeLayerMask[])renderingLayerMasks.Clone() : null;
            return newSet;
        }

        /// <summary>
        /// Determines if the Probe Reference Volume Profile is equivalent to another one.
        /// </summary>
        /// <param name ="otherProfile">The profile to compare with.</param>
        /// <returns>Whether the Probe Reference Volume Profile is equivalent to another one.</returns>
        public bool IsEquivalent(ProbeVolumeBakingSet otherProfile)
        {
            return minDistanceBetweenProbes == otherProfile.minDistanceBetweenProbes &&
                cellSizeInMeters == otherProfile.cellSizeInMeters &&
                simplificationLevels == otherProfile.simplificationLevels &&
                renderersLayerMask == otherProfile.renderersLayerMask;
        }

        internal void Clear()
        {
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var data in backendBakedData)
                    data.ClearAssets();
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
                EditorUtility.SetDirty(this);
            }

            cellDescs.Clear();

            EnsureBackendLayouts();
            foreach (var layout in m_BackendCellLayoutsById.Values)
                layout.cellDescs.Clear();

            // All cells should have been released through unloading the scenes first.
            Debug.Assert(cellDataMap.Count == 0);

            perSceneCellLists.Clear();
            foreach (var sceneGUID in sceneGUIDs)
                perSceneCellLists.Add(sceneGUID, new List<int>());
            foreach (var lists in m_BackendPerSceneCellLists.Values)
            {
                lists.Clear();
                foreach (var sceneGUID in sceneGUIDs)
                    lists.Add(sceneGUID, new List<int>());
            }
        }

        internal void Clear(string backendId)
        {
            if (string.IsNullOrEmpty(backendId))
            {
                Clear();
                return;
            }

            Cleanup(backendId);
            try
            {
                AssetDatabase.StartAssetEditing();
                FindBakedData(backendId)?.ClearAssets();
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
                EditorUtility.SetDirty(this);
            }

            GetCellDescs(backendId).Clear();
            var lists = new Dictionary<string, List<int>>();
            foreach (var sceneGUID in sceneGUIDs)
                lists.Add(sceneGUID, new List<int>());
            SetPerSceneCellLists(backendId, lists);
        }

        /// <summary>
        /// Renames a given scenario.
        /// </summary>
        /// <param name="scenario">The original scenario name.</param>
        /// <param name="newName">The requested new scenario name.</param>
        /// <returns>The new scenario name. The is different than the requested name in case a scenario with the same name already exists.</returns>
        public string RenameScenario(string scenario, string newName)
        {
            if (!m_LightingScenarios.Contains(scenario))
                return newName;

            m_LightingScenarios.Remove(scenario);
            newName = CreateScenario(newName);

            foreach (var data in backendBakedData)
                data.RenameScenario(scenario, newName, name);
            EnsureBackendLayouts();
            foreach (var cellDataMapForBackend in m_BackendCellDataMaps.Values)
            foreach (var cellData in cellDataMapForBackend.Values)
                cellData.RenameScenarioEntry(scenario, newName);

            RemapScenarioSelection(scenario, newName, false);
            EditorUtility.SetDirty(this);

            return newName;
        }

        void RemapScenarioSelection(string previous, string replacement, bool removed)
        {
            if (lightingScenario == previous)
                lightingScenario = replacement;
            if (m_OtherScenario == previous)
            {
                m_OtherScenario = removed ? null : replacement;
                if (removed) m_ScenarioBlendingFactor = 0;
            }
            EnsureBackendScenarioState();
            foreach (var id in new List<string>(m_BackendLightingScenarios.Keys))
                if (m_BackendLightingScenarios[id] == previous)
                    m_BackendLightingScenarios[id] = replacement;
            foreach (var id in new List<string>(m_BackendOtherScenarios.Keys))
            {
                if (m_BackendOtherScenarios[id] != previous)
                    continue;
                m_BackendOtherScenarios[id] = removed ? null : replacement;
                if (removed) m_BackendScenarioBlendingFactors[id] = 0;
            }
            foreach (var volume in ProbeReferenceVolumeProvider.EnumerateInitializedVolumes())
            {
                if (volume.currentBakingSet != this)
                    continue;
                if (volume.supportScenarioBlending) volume.ScenarioBlendingChanged(true);
                else volume.UnloadAllCells();
            }
        }

        internal static void SyncBakingSets()
        {
            sceneToBakingSet = new Dictionary<string, ProbeVolumeBakingSetWeakReference>();

            var setGUIDs = AssetDatabase.FindAssets("t:" + nameof(ProbeVolumeBakingSet));

            foreach (var setGUID in setGUIDs)
            {
                string bakingSetPath = AssetDatabase.GUIDToAssetPath(setGUID);
                bool alreadyLoaded = AssetDatabase.IsMainAssetAtPathLoaded(bakingSetPath);

                var set = AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(bakingSetPath);
                if (set != null)
                {
                    var reference = new ProbeVolumeBakingSetWeakReference(set);
                    foreach (var guid in set.sceneGUIDs)
                        sceneToBakingSet[guid] = reference;

                    // If the asset wasn't already in-memory, and we just loaded it into memory, free it, so we don't waste memory.
                    if (!alreadyLoaded)
                        reference.Unload();
                }
            }
        }

        internal static ProbeVolumeBakingSet GetBakingSetForScene(string sceneGUID) => sceneToBakingSet.GetValueOrDefault(sceneGUID, null)?.Get();
        internal static ProbeVolumeBakingSet GetBakingSetForScene(Scene scene) => GetBakingSetForScene(scene.GetGUID());

        internal void SetDefaults()
        {
            settings.SetDefaults();
            m_LightingScenarios = new List<string> { TProbeReferenceVolumeBase.defaultLightingScenario };

            // We have to initialize that to not trigger a warning on new baking sets
            chunkSizeInBricks = ProbeBrickPoolBase.GetChunkSizeInBrickCount();

        }

        internal void EnsureScenarioAssetNameConsistencyForUndo()
        {
            foreach (var data in backendBakedData)
                data.EnsureScenarioAssetNames(name);
        }

        internal long GetDiskSizeOfSharedData() => bakedData?.GetDiskSizeOfSharedData() ?? 0;
        internal long GetDiskSizeOfScenarioData(string scenario) => bakedData?.GetDiskSizeOfScenarioData(scenario) ?? 0;

        internal void SanitizeScenes()
        {
            // Remove entries in the list pointing to deleted scenes
            for (int i = m_SceneGUIDs.Count - 1; i >= 0; i--)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(m_SceneGUIDs[i]);
                if (UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.SceneAsset>(path) == null)
                    RemoveScene(m_SceneGUIDs[i]);
            }
        }

        void DeleteAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            AssetDatabase.DeleteAsset(assetPath);
        }

        internal bool HasBeenBaked()
        {
            return bakedData?.HasValidSharedData() ?? false;
        }

        internal static string GetDirectory(string scenePath, string sceneName)
        {
            string sceneDir = Path.GetDirectoryName(scenePath);
            string assetPath = Path.Combine(sceneDir, sceneName);
            if (!AssetDatabase.IsValidFolder(assetPath))
                AssetDatabase.CreateFolder(sceneDir, sceneName);

            return assetPath;
        }

        internal static void OnSceneSaving(Scene scene, string path = null)
        {
            // If we are called from the scene callback, we want to update all global volumes that are potentially affected
            bool onSceneSave = path != null;

            string sceneGUID = TProbeReferenceVolumeBase.GetSceneGUID(scene);
            var bakingSet = GetBakingSetForScene(sceneGUID);

            if (bakingSet != null)
            {
                bakingSet.UpdateSceneBounds(scene, sceneGUID, onSceneSave);
                bakingSet.EnsurePerSceneData(scene, sceneGUID);
            }
        }

        static internal int MaxSubdivLevelInProbeVolume(Vector3 volumeSize, int maxSubdiv)
        {
            float maxSizedDim = Mathf.Max(volumeSize.x, Mathf.Max(volumeSize.y, volumeSize.z));
            var refVolume = ProbeReferenceVolumeProvider.instance;
            float maxSideInBricks = maxSizedDim / refVolume.MinDistanceBetweenProbes();
            int subdiv = Mathf.FloorToInt(Mathf.Log(maxSideInBricks, 3)) - 1;

            return Mathf.Max(subdiv, maxSubdiv);
        }

        static void InflateBound(ref Bounds bounds, ProbeVolume pv)
        {
            Bounds originalBounds = bounds;
            var refVolume = ProbeReferenceVolumeProvider.instance;
            // Round the probe volume bounds to cell size
            float cellSize = refVolume.MaxBrickSize();
            Vector3 cellOffset = refVolume.ProbeOffset();

            // Expand the probe volume bounds to snap on the cell size grid
            bounds.Encapsulate((Vector3)math.floor((bounds.min - cellOffset) / cellSize) * cellSize + cellOffset);
            bounds.Encapsulate((Vector3)math.ceil((bounds.max - cellOffset) / cellSize) * cellSize + cellOffset);

            // calculate how much padding we need to remove according to the brick generation in ProbePlacement.cs:
            var cellSizeVector = new Vector3(cellSize, cellSize, cellSize);
            var minPadding = (bounds.min - originalBounds.min);
            var maxPadding = (bounds.max - originalBounds.max);
            minPadding = cellSizeVector - new Vector3(Mathf.Abs(minPadding.x), Mathf.Abs(minPadding.y), Mathf.Abs(minPadding.z));
            maxPadding = cellSizeVector - new Vector3(Mathf.Abs(maxPadding.x), Mathf.Abs(maxPadding.y), Mathf.Abs(maxPadding.z));

            // Find the size of the brick we can put for every axis given the padding size
            int maxSubdiv = refVolume.GetMaxSubdivision() - 1;
            if (pv.overridesSubdivLevels) maxSubdiv = Mathf.Min(pv.highestSubdivLevelOverride, maxSubdiv);

            float rightPaddingSubdivLevel = refVolume.BrickSize(MaxSubdivLevelInProbeVolume(new Vector3(maxPadding.x, originalBounds.size.y, originalBounds.size.z), maxSubdiv));
            float leftPaddingSubdivLevel = refVolume.BrickSize(MaxSubdivLevelInProbeVolume(new Vector3(minPadding.x, originalBounds.size.y, originalBounds.size.z), maxSubdiv));
            float topPaddingSubdivLevel = refVolume.BrickSize(MaxSubdivLevelInProbeVolume(new Vector3(originalBounds.size.x, maxPadding.y, originalBounds.size.z), maxSubdiv));
            float bottomPaddingSubdivLevel = refVolume.BrickSize(MaxSubdivLevelInProbeVolume(new Vector3(originalBounds.size.x, minPadding.y, originalBounds.size.z), maxSubdiv));
            float forwardPaddingSubdivLevel = refVolume.BrickSize(MaxSubdivLevelInProbeVolume(new Vector3(originalBounds.size.x, originalBounds.size.y, maxPadding.z), maxSubdiv));
            float backPaddingSubdivLevel = refVolume.BrickSize(MaxSubdivLevelInProbeVolume(new Vector3(originalBounds.size.x, originalBounds.size.y, minPadding.z), maxSubdiv));

            // Remove the extra padding caused by cell rounding
            bounds.min = bounds.min + new Vector3(
                leftPaddingSubdivLevel * Mathf.Floor(Mathf.Abs(bounds.min.x - originalBounds.min.x) / (float)leftPaddingSubdivLevel),
                bottomPaddingSubdivLevel * Mathf.Floor(Mathf.Abs(bounds.min.y - originalBounds.min.y) / (float)bottomPaddingSubdivLevel),
                backPaddingSubdivLevel * Mathf.Floor(Mathf.Abs(bounds.min.z - originalBounds.min.z) / (float)backPaddingSubdivLevel)
            );
            bounds.max = bounds.max - new Vector3(
                rightPaddingSubdivLevel * Mathf.Floor(Mathf.Abs(bounds.max.x - originalBounds.max.x) / (float)rightPaddingSubdivLevel),
                topPaddingSubdivLevel * Mathf.Floor(Mathf.Abs(bounds.max.y - originalBounds.max.y) / (float)topPaddingSubdivLevel),
                forwardPaddingSubdivLevel * Mathf.Floor(Mathf.Abs(bounds.max.z - originalBounds.max.z) / (float)forwardPaddingSubdivLevel)
            );
        }

        internal void UpdateSceneBounds(Scene scene, string sceneGUID, bool onSceneSave)
        {
            // Scene saves update APV bounds only; other backends own their layouts.
            if (ProbeReferenceVolumeProvider.instance.backendId != APVProbeReferenceVolumeBackend.backendId)
                return;
            var volumes = FindObjectsByType<ProbeVolume>(FindObjectsSortMode.None);
            var refVolume = ProbeReferenceVolumeProvider.instance;
            float prevBrickSize = refVolume.MinBrickSize();
            int prevMaxSubdiv = refVolume.GetMaxSubdivision();
            Vector3 prevOffset = refVolume.ProbeOffset();

            if (onSceneSave) // Use baked values
                refVolume.SetSubdivisionDimensions(minBrickSize, maxSubdivision, bakedProbeOffset);
            else // Use displayed values
                refVolume.SetSubdivisionDimensions(GetMinBrickSize(minDistanceBetweenProbes), GetMaxSubdivision(simplificationLevels), probeOffset);

            bool boundFound = false;
            Bounds newBound = new Bounds();
            foreach (var volume in volumes)
            {
                bool forceUpdate = onSceneSave && volume.mode == ProbeVolume.Mode.Global;
                if (!forceUpdate && volume.gameObject.scene != scene)
                    continue;

                if (volume.mode != ProbeVolume.Mode.Local)
                    volume.UpdateGlobalVolume(volume.mode == ProbeVolume.Mode.Global ? GIContributors.ContributorFilter.All : GIContributors.ContributorFilter.Scene);

                var obb = new TProbeReferenceVolumeBase.Volume(volume.GetVolume(), 0, 0);
                Bounds localBounds = obb.CalculateAABB();

                InflateBound(ref localBounds, volume);

                if (!boundFound)
                {
                    newBound = localBounds;
                    boundFound = true;
                }
                else
                {
                    newBound.Encapsulate(localBounds);
                }
            }

            bool bakeDataExist = m_SceneBakeData.TryGetValue(sceneGUID, out var bakeData);
            Debug.Assert(bakeDataExist, "Scene should have been added to the baking set with default bake data instance.");
            bakeData.hasProbeVolume = boundFound;
            if (boundFound)
                bakeData.bounds = newBound;

            refVolume.SetSubdivisionDimensions(prevBrickSize, prevMaxSubdiv, prevOffset);
            EditorUtility.SetDirty(this);
        }

        // It is important this is called after UpdateSceneBounds is called otherwise SceneHasProbeVolumes might be out of date
        internal void EnsurePerSceneData(Scene scene, string sceneGUID)
        {
            // This scene-data creation path belongs to APV.
            if (ProbeReferenceVolumeProvider.instance.backendId != APVProbeReferenceVolumeBackend.backendId)
                return;
            bool bakeDataExist = m_SceneBakeData.TryGetValue(sceneGUID, out var bakeData);
            Debug.Assert(bakeDataExist, "Scene should have been added to the baking set with default bake data instance.");

            if (bakeData.hasProbeVolume)
            {
                if (!ProbeReferenceVolumeProvider.instance.TryGetPerSceneData(sceneGUID, out var data))
                {
                    GameObject go = new GameObject("ProbeVolumePerSceneData");
                    go.hideFlags |= HideFlags.HideInHierarchy;
                    var perSceneData = go.AddComponent<ProbeVolumePerSceneData>();
                    SceneManager.MoveGameObjectToScene(go, scene);
                }
            }
        }

        internal ProbeVolumePerSceneData EnsureAPRTPerSceneData(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                throw new ArgumentException("APRT scene data requires a loaded scene.", nameof(scene));

            ProbeVolumePerSceneData data = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                data = root.GetComponentInChildren<ProbeVolumePerSceneData>(true);
                if (data != null) break;
            }
            if (data == null)
            {
                var owner = new GameObject("ProbeVolumePerSceneData") { hideFlags = HideFlags.HideInHierarchy };
                SceneManager.MoveGameObjectToScene(owner, scene);
                data = owner.AddComponent<ProbeVolumePerSceneData>();
            }
            data.sceneGUID = scene.GetGUID();
            data.serializedBakingSet = this;
            EditorUtility.SetDirty(data);
            return data;
        }

        internal SceneBakeData GetSceneBakeData(string sceneGUID, bool addIfMissing = true)
        {
            if (!m_SceneBakeData.TryGetValue(sceneGUID, out var bakeData) && addIfMissing)
            {
                if (m_SceneGUIDs.Contains(sceneGUID))
                    m_SceneBakeData[sceneGUID] = bakeData = new SceneBakeData();
            }
            return bakeData;
        }

        internal static bool SceneHasProbeVolumes(string sceneGUID)
        {
            var bakingSet = GetBakingSetForScene(sceneGUID);
            return bakingSet?.GetSceneBakeData(sceneGUID)?.hasProbeVolume ?? false;
        }

        internal bool DialogNoProbeVolumeInSetShown()
        {
            return dialogNoProbeVolumeInSetShown;
        }

        internal void SetDialogNoProbeVolumeInSetShown(bool value)
        {
            dialogNoProbeVolumeInSetShown = value;
        }
    }
}

#endif
