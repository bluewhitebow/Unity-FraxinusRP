using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering
{
    // A standalone baker: placement/capture run on editor ticks and publish only a complete result.
    internal sealed class APRTProbeVolumeBaking : ProbeVolumeBaking
    {
        const int k_MaxProbeCount = 250000;
        readonly APRTProbeReferenceVolume m_Volume;
        readonly ProbeVolumeBakingSet m_Set;
        bool m_Running, m_Committed;
        internal bool committed => m_Committed;
        APRTBakedData m_Staged;
        GIContributors m_Contributors;
        List<APRTVolume> m_Regions;
        HashSet<string> m_Scenes;
        ProbeSubdivisionContext m_Context;
        ProbeSubdivisionResult m_Placement;
        ProbePlacement.GPUSubdivisionContext m_Subdivision;
        APRTProbeCapture m_Capture;
        APRTSkyBounceBaker m_SkyBounce;
        int m_PlacementCursor, m_CellCursor, m_ProbeCursor, m_CompletedProbes;
        int m_SourceRevision;
        string m_ScenarioName;
        APRTLightingScenario m_LightingInputs;
        APRTRelightSettings m_LightingSettings;
        Dictionary<string, List<int>> m_SceneLists;
        SceneSetupSnapshot m_SceneSetup;

        sealed class SceneSetupSnapshot
        {
            readonly int[] handles;
            readonly bool[] loaded;
            internal SceneSetupSnapshot()
            {
                handles = new int[SceneManager.sceneCount];
                loaded = new bool[handles.Length];
                for (int i = 0; i < handles.Length; ++i)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    handles[i] = scene.handle;
                    loaded[i] = scene.isLoaded;
                }
            }
            internal bool Matches()
            {
                if (handles.Length != SceneManager.sceneCount) return false;
                for (int i = 0; i < handles.Length; ++i)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (handles[i] != scene.handle || loaded[i] != scene.isLoaded) return false;
                }
                return true;
            }
        }

        internal APRTProbeVolumeBaking(APRTProbeReferenceVolume volume, ProbeVolumeBakingSet bakingSet)
        { m_Volume = volume; m_Set = bakingSet; }
        internal override string backendId => APRTProbeReferenceVolumeBackend.backendId;
        internal override TProbeReferenceVolumeBase referenceVolume => m_Volume;
        internal override ProbeVolumeBakingSet activeBakingSet => m_Set;
        public override bool isRunning => m_Running;

        internal override bool BakeStandaloneAsync()
        {
            if (m_Running || m_Set == null || EditorApplication.isPlayingOrWillChangePlaymode) return false;
            m_Committed = false;
            try
            {
                if (partialBakeSceneList != null && partialBakeSceneList.Count > 0)
                    throw new InvalidOperationException("APRT currently requires a complete Baking Set bake.");
                m_Scenes = new HashSet<string>(m_Set.sceneGUIDs);
                if (m_Scenes.Count == 0) throw new InvalidOperationException("The APRT Baking Set does not contain a saved scene.");
                foreach (var guid in m_Scenes)
                {
                    var scene = SceneManager.GetSceneByPath(AssetDatabase.GUIDToAssetPath(guid));
                    if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
                        throw new InvalidOperationException("Load and save every scene in the APRT Baking Set before baking.");
                }
                m_Regions = UnityEngine.Object.FindObjectsByType<APRTVolume>(FindObjectsSortMode.InstanceID)
                    .Where(v => v.isActiveAndEnabled && m_Scenes.Contains(v.gameObject.scene.GetGUID())).ToList();
                if (m_Regions.Count == 0) throw new InvalidOperationException("Add an active Adaptive PRT Volume to a scene in the Baking Set.");
                bool grid = m_Regions.Any(v => v.layout == APRTVolume.Layout.ReferenceGrid);
                if (grid && m_Regions.Count != 1)
                    throw new InvalidOperationException("Reference Grid requires exactly one APRT Volume in the Baking Set. Multiple Adaptive volumes are supported.");
                m_Contributors = FilterContributors(GIContributors.Find(GIContributors.ContributorFilter.All));
                m_ScenarioName = m_Set.GetLightingScenario(backendId);
                if (!APRTLightingScenarioCapture.TryCaptureInputs(m_Set, m_ScenarioName, out m_LightingInputs, out string lightingError))
                    throw new InvalidOperationException(lightingError);
                m_LightingSettings = ResolveLightingSettings(m_Set);
                m_SourceRevision = SourceRevision();
                m_Staged = new APRTBakedData
                {
                    formatVersion = APRTBakedData.k_CurrentVersion,
                    lightingModel = APRTLightingModel.APRTV,
                    bakedSimplificationLevels = Mathf.Clamp(m_Set.simplificationLevels, 0, ProbeBrickIndex.kMaxSubdivisionLevels - 1),
                    bakedMinDistanceBetweenProbes = Mathf.Max(0.01f, m_Set.minDistanceBetweenProbes),
                    bakedProbeOffset = m_Set.probeOffset,
                    regularGrid = grid,
                    hasLeakReductionData = true,
                    hasSkyIrradianceTransfer = true,
                    hasSkyBounceTransfer = m_Set.aprtBakeSkyBounce
                };
                m_Staged.bakedRegionSettingsHash = RegionSettingsHash();
                m_SceneSetup = new SceneSetupSnapshot();
                m_CellCursor = m_ProbeCursor = m_PlacementCursor = m_CompletedProbes = 0;
                m_SceneLists = m_Scenes.ToDictionary(s => s, _ => new List<int>());
                if (m_Set.freezePlacement)
                {
                    var previous = m_Set.GetBakedData(backendId) as APRTBakedData;
                    if (!FrozenPlacementMatches(previous))
                        throw new InvalidOperationException("APRT cannot reuse the previous probe positions because the Baking Set layout, volumes or loaded scenes changed. Select Recalculate under Probe Positions and bake again.");
                    m_Staged = CreateFrozenLayout(previous, m_Set.aprtBakeSkyBounce);
                    m_Staged.bakedRegionSettingsHash = RegionSettingsHash();
                    m_SceneLists = m_Set.GetPerSceneCellLists(backendId).ToDictionary(item => item.Key, item => new List<int>(item.Value));
                }
                else if (grid) BuildReferenceGrid(m_Regions[0]);
                else PrepareAdaptivePlacement();
                m_Running = true;
                EditorApplication.update += Update;
                return true;
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError("APRT bake could not start: " + exception.Message, m_Set);
                Cleanup();
                return false;
            }
        }

        internal static bool CanFreezePlacement(APRTProbeReferenceVolume volume, ProbeVolumeBakingSet set)
        {
            if (set == null) return false;
            try
            {
                var data = set.GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
                if (data == null || !data.CheckCompatibleCellLayout(set) || data.lightingModel != APRTLightingModel.APRTV) return false;
                var checker = new APRTProbeVolumeBaking(volume, set)
                {
                    m_Staged = data,
                    m_Scenes = new HashSet<string>(set.sceneGUIDs)
                };
                checker.m_Regions = UnityEngine.Object.FindObjectsByType<APRTVolume>(FindObjectsSortMode.InstanceID)
                    .Where(region => region.isActiveAndEnabled && checker.m_Scenes.Contains(region.gameObject.scene.GetGUID())).ToList();
                checker.m_Contributors = checker.FilterContributors(GIContributors.Find(GIContributors.ContributorFilter.All));
                return checker.FrozenPlacementMatches(data);
            }
            catch { return false; }
        }

        bool FrozenPlacementMatches(APRTBakedData previous)
        {
            if (previous == null || previous.lightingModel != APRTLightingModel.APRTV || !previous.CheckCompatibleCellLayout(m_Set) ||
                m_Scenes.Count == 0 || m_Regions.Count == 0) return false;
            if (previous.bakedRegionSettingsHash != 0 && previous.bakedRegionSettingsHash != RegionSettingsHash()) return false;
            foreach (var guid in m_Scenes)
            {
                var scene = SceneManager.GetSceneByPath(AssetDatabase.GUIDToAssetPath(guid));
                if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path)) return false;
            }
            var sceneLists = m_Set.GetPerSceneCellLists(backendId);
            if (sceneLists.Count != m_Scenes.Count || !m_Scenes.SetEquals(sceneLists.Keys)) return false;
            var validCells = new HashSet<int>(previous.cells.Select(cell => cell.index));
            foreach (var list in sceneLists.Values)
                if (list == null || list.Any(index => !validCells.Contains(index))) return false;
            bool grid = m_Regions.Any(region => region.layout == APRTVolume.Layout.ReferenceGrid);
            if (grid != previous.regularGrid) return false;
            if (grid)
            {
                if (m_Regions.Count != 1) return false;
                var region = m_Regions[0];
                return region.regularGridOrigin.Equals(previous.regularGridOrigin) &&
                    region.transform.rotation.Equals(previous.regularGridRotation) &&
                    region.regularGridDimensions.Equals(previous.regularGridDimensions) &&
                    region.regularGridSpacing.Equals(previous.regularGridSpacing) &&
                    region.regularGridSamplingSize.Equals(previous.regularGridSamplingSize) &&
                    (region.regularGridOrigin + region.transform.rotation * region.regularGridSamplingOriginOffset).Equals(previous.regularGridSamplingOrigin);
            }
            if (previous.samplingDomains.Length != m_Regions.Count) return false;
            var unmatchedDomains = new List<Matrix4x4>(previous.samplingDomains);
            for (int i = 0; i < m_Regions.Count; ++i)
            {
                var region = m_Regions[i];
                var bounds = FitBounds(region);
                var domain = region.mode == APRTVolume.Mode.Local ? region.volumeMatrix : Matrix4x4.TRS(bounds.center, Quaternion.identity, bounds.size);
                int matching = unmatchedDomains.FindIndex(candidate => candidate.Equals(domain));
                if (matching < 0) return false;
                unmatchedDomains.RemoveAt(matching);
            }
            return true;
        }

        internal static APRTBakedData CreateFrozenLayout(APRTBakedData previous, bool skyBounce)
        {
            if (previous == null || !previous.HasValidSharedData())
                throw new ArgumentException("A valid APRT bake is required to reuse probe positions.", nameof(previous));
            var result = new APRTBakedData
            {
                formatVersion = APRTBakedData.k_CurrentVersion,
                lightingModel = APRTLightingModel.APRTV,
                bakedSimplificationLevels = previous.bakedSimplificationLevels,
                bakedMinDistanceBetweenProbes = previous.bakedMinDistanceBetweenProbes,
                bakedProbeOffset = previous.bakedProbeOffset,
                minCellPosition = previous.minCellPosition, maxCellPosition = previous.maxCellPosition, globalBounds = previous.globalBounds,
                regularGrid = previous.regularGrid, regularGridOrigin = previous.regularGridOrigin,
                regularGridRotation = previous.regularGridRotation, regularGridDimensions = previous.regularGridDimensions,
                regularGridSpacing = previous.regularGridSpacing, regularGridSamplingSize = previous.regularGridSamplingSize,
                regularGridSamplingOrigin = previous.regularGridSamplingOrigin,
                regularGridProbeIndices = (int[])previous.regularGridProbeIndices.Clone(),
                samplingDomains = (Matrix4x4[])previous.samplingDomains.Clone(),
                bakedRegionSettingsHash = previous.bakedRegionSettingsHash,
                hasLeakReductionData = true, hasSkyIrradianceTransfer = true, hasSkyBounceTransfer = skyBounce
            };
            foreach (var cell in previous.cells)
            {
                result.cells.Add(new APRTBakedCell
                {
                    index = cell.index, position = cell.position, probeCount = cell.probeCount,
                    probePositions = (Vector3[])cell.probePositions.Clone(),
                    brickPositions = (Vector3Int[])cell.brickPositions.Clone(),
                    brickSubdivisions = (int[])cell.brickSubdivisions.Clone(),
                    brickProbeIndices = (int[])cell.brickProbeIndices.Clone(),
                    axisData = new Vector4[cell.probeCount * 6], visibilityMoments = new Vector4[cell.probeCount * 6],
                    probeValidity = new float[cell.probeCount],
                    skyIrradianceTransfer = new float[cell.probeCount * APRTBakedData.k_SkyIrradianceTransferCoefficientCount],
                    skyIrradianceTransferRGB = skyBounce
                        ? new Vector3[cell.probeCount * APRTBakedData.k_SkyIrradianceTransferCoefficientCount] : Array.Empty<Vector3>()
                });
                result.sampleCount += cell.probeCount;
            }
            return result;
        }

        int RegionSettingsHash()
        {
            unchecked
            {
                int hash = 17;
                void Add(int value) => hash = hash * 23 + value;
                Add(m_Set.renderersLayerMask.value); Add(m_Set.minRendererVolumeSize.GetHashCode());
                foreach (string guid in m_Scenes.OrderBy(guid => guid, StringComparer.Ordinal))
                    foreach (char character in guid) Add(character);
                var regionHashes = new List<int>();
                foreach (var region in m_Regions)
                {
                    int regionHash = 17;
                    void RegionAdd(int value) => regionHash = regionHash * 23 + value;
                    foreach (char character in region.gameObject.scene.GetGUID()) RegionAdd(character);
                    RegionAdd((int)region.mode); RegionAdd((int)region.layout); RegionAdd(region.transform.localToWorldMatrix.GetHashCode());
                    RegionAdd(region.size.GetHashCode()); RegionAdd(region.regularGridDimensions.GetHashCode()); RegionAdd(region.regularGridSpacing.GetHashCode());
                    RegionAdd(region.regularGridOriginOffset.GetHashCode()); RegionAdd(region.regularGridSamplingSize.GetHashCode());
                    RegionAdd(region.regularGridSamplingOriginOffset.GetHashCode()); RegionAdd(region.overridesSubdivLevels ? 1 : 0);
                    RegionAdd(region.lowestSubdivLevelOverride); RegionAdd(region.highestSubdivLevelOverride); RegionAdd(region.fillEmptySpaces ? 1 : 0);
                    RegionAdd(region.overrideRendererFilters ? 1 : 0); RegionAdd(region.objectLayerMask.value); RegionAdd(region.minRendererVolumeSize.GetHashCode());
                    regionHashes.Add(regionHash);
                }
                regionHashes.Sort();
                foreach (int regionHash in regionHashes) Add(regionHash);
                // Zero denotes a legacy bake without region provenance.
                return hash != 0 ? hash : 1;
            }
        }

        static APRTRelightSettings ResolveLightingSettings(ProbeVolumeBakingSet set) =>
            APRTRelightSettings.Resolve(set, APRTLightingModel.APRTV, VolumeManager.instance.stack?.GetComponent<APRTOptions>());

        GIContributors FilterContributors(GIContributors all)
        {
            var result = new GIContributors { renderers = new(), terrains = new() };
            bool Allowed(GameObject go, Bounds bounds)
            {
                if (!m_Scenes.Contains(go.scene.GetGUID())) return false;
                var extent = bounds.size;
                float size = extent.x * extent.y * extent.z;
                foreach (var region in m_Regions)
                {
                    if (!region.IncludesScene(go.scene)) continue;
                    int mask = region.overrideRendererFilters ? region.objectLayerMask.value : m_Set.renderersLayerMask.value;
                    float minimum = region.overrideRendererFilters ? region.minRendererVolumeSize : m_Set.minRendererVolumeSize;
                    if ((mask & (1 << go.layer)) != 0 && size >= minimum) return true;
                }
                return false;
            }
            foreach (var item in all.renderers)
                if (Allowed(item.component.gameObject, item.bounds)) result.renderers.Add(item);
            foreach (var item in all.terrains)
                if (Allowed(item.component.gameObject, item.boundsWithTrees)) result.terrains.Add(item);
            return result;
        }

        Bounds FitBounds(APRTVolume region)
        {
            if (region.mode == APRTVolume.Mode.Local) return region.worldBounds;
            bool found = false;
            var bounds = default(Bounds);
            void Add(GameObject go, Bounds candidate)
            {
                if (!region.IncludesScene(go.scene)) return;
                if (found) bounds.Encapsulate(candidate);
                else { bounds = candidate; found = true; }
            }
            foreach (var item in m_Contributors.renderers) Add(item.component.gameObject, item.bounds);
            foreach (var item in m_Contributors.terrains) Add(item.component.gameObject, item.boundsWithTrees);
            if (!found) throw new InvalidOperationException("Scene and Global APRT regions need GI contributors to determine their bounds.");
            bounds.Expand(ProbeVolumeBakingSet.GetMinBrickSize(m_Staged.bakedMinDistanceBetweenProbes));
            return bounds;
        }

        int SourceRevision()
        {
            // Check again before publication: changing lighting or geometry while
            // capture is in flight must not publish a mixture of scene states.
            unchecked
            {
                int hash = 17;
                void Add(int value) { hash = hash * 23 + value; }
                void ObjectRevision(UnityEngine.Object value)
                {
                    Add(value != null ? value.GetInstanceID() : 0);
                    if (value != null) Add(EditorUtility.GetDirtyCount(value));
                }
                void Materials(Material[] materials)
                {
                    foreach (var material in materials)
                    {
                        ObjectRevision(material);
                        if (material == null) continue;
                        Add((int)material.ComputeCRC());
                        ObjectRevision(material.shader);
                        foreach (string property in material.GetTexturePropertyNames())
                        {
                            var texture = material.GetTexture(property);
                            ObjectRevision(texture);
                            if (texture != null) Add((int)texture.updateCount);
                        }
                    }
                }
                Add(m_Set.minDistanceBetweenProbes.GetHashCode()); Add(m_Set.simplificationLevels);
                Add(m_Set.freezePlacement ? 1 : 0); Add(m_Set.GetLightingScenario(backendId)?.GetHashCode() ?? 0);
                Add(APRTLightingScenarioCapture.GetSourceRevision(m_Set));
                // The effective stack is captured once at bake start. It can
                // change when cameras render without any authoring edits, so
                // validate source profiles instead of the camera stack here.
                var lighting = APRTRelightSettings.FromBakingSet(m_Set, APRTLightingModel.APRTV);
                Add(lighting.bounceIntensity.GetHashCode()); Add(lighting.indirectIntensity.GetHashCode());
                Add(lighting.indirectContrast.GetHashCode()); Add(lighting.saturationBoost.GetHashCode());
                Add(lighting.shadowGIVisibility.GetHashCode()); Add(lighting.skyVisibilityPower.GetHashCode()); Add(lighting.skyIntensity.GetHashCode());
                void ProfileRevision(VolumeProfile profile)
                {
                    ObjectRevision(profile);
                    if (profile == null || !profile.TryGet<APRTOptions>(out var options)) return;
                    ObjectRevision(options);
                    Add(EditorJsonUtility.ToJson(options).GetHashCode());
                }
                var defaults = APRTOptions.GetDefaultOptions();
                if (defaults != null) Add(EditorJsonUtility.ToJson(defaults).GetHashCode());
                foreach (var volume in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.InstanceID))
                {
                    if (!volume.isActiveAndEnabled || !m_Scenes.Contains(volume.gameObject.scene.GetGUID())) continue;
                    var profile = volume.HasInstantiatedProfile() ? volume.profile : volume.sharedProfile;
                    if (profile == null || !profile.TryGet<APRTOptions>(out _)) continue;
                    ObjectRevision(volume); Add(volume.transform.localToWorldMatrix.GetHashCode());
                    Add(volume.gameObject.layer); Add(volume.isGlobal ? 1 : 0);
                    Add(volume.priority.GetHashCode()); Add(volume.weight.GetHashCode()); Add(volume.blendDistance.GetHashCode());
                    ProfileRevision(profile);
                    if (!volume.isGlobal)
                        foreach (var collider in volume.GetComponents<Collider>())
                        { ObjectRevision(collider); Add(EditorJsonUtility.ToJson(collider).GetHashCode()); }
                }
                Add(m_Set.probeOffset.GetHashCode()); Add(m_Set.renderersLayerMask.value);
                Add(m_Set.minRendererVolumeSize.GetHashCode()); Add(m_Set.aprtCaptureResolution); Add(m_Set.aprtCaptureDistance.GetHashCode());
                Add((int)APRTLightingModel.APRTV); Add(m_Set.aprtBakeSaturationPriority.GetHashCode()); Add(m_Set.aprtBakeCenterWeight.GetHashCode());
                Add(m_Set.aprtBakeSkyBounce.GetHashCode()); Add(m_Set.aprtSkyBouncePrimarySamples); Add(m_Set.aprtSkyBounceSkySamples);
                foreach (string guid in m_Set.sceneGUIDs) Add(guid.GetHashCode());
                foreach (var region in UnityEngine.Object.FindObjectsByType<APRTVolume>(FindObjectsSortMode.InstanceID))
                {
                    if (!region.isActiveAndEnabled || !m_Scenes.Contains(region.gameObject.scene.GetGUID())) continue;
                    ObjectRevision(region); Add(region.transform.localToWorldMatrix.GetHashCode());
                    // Scripts can change these fields without marking the component dirty.
                    Add((int)region.mode); Add((int)region.layout); Add(region.size.GetHashCode());
                    Add(region.regularGridDimensions.GetHashCode()); Add(region.regularGridSpacing.GetHashCode());
                    Add(region.regularGridOriginOffset.GetHashCode()); Add(region.regularGridSamplingSize.GetHashCode());
                    Add(region.regularGridSamplingOriginOffset.GetHashCode());
                    Add(region.overridesSubdivLevels ? 1 : 0); Add(region.lowestSubdivLevelOverride); Add(region.highestSubdivLevelOverride);
                    Add(region.fillEmptySpaces ? 1 : 0); Add(region.overrideRendererFilters ? 1 : 0);
                    Add(region.objectLayerMask.value); Add(region.minRendererVolumeSize.GetHashCode());
                }
                var contributors = FilterContributors(GIContributors.Find(GIContributors.ContributorFilter.All));
                foreach (var item in contributors.renderers)
                {
                    ObjectRevision(item.component); Add(item.component.localToWorldMatrix.GetHashCode());
                    Add(item.bounds.GetHashCode()); ObjectRevision(item.component.GetComponent<MeshFilter>().sharedMesh);
                    Materials(item.component.sharedMaterials);
                }
                foreach (var item in contributors.terrains)
                {
                    ObjectRevision(item.component); ObjectRevision(item.component.terrainData);
                    Add(item.component.transform.localToWorldMatrix.GetHashCode());
                    foreach (var layer in item.component.terrainData.terrainLayers)
                    { ObjectRevision(layer); if (layer != null) ObjectRevision(layer.diffuseTexture); }
                    foreach (var prototype in item.treePrototypes)
                        if (prototype.component != null) Materials(prototype.component.sharedMaterials);
                }
                return hash;
            }
        }

        void PrepareAdaptivePlacement()
        {
            var descriptors = new List<ProbeVolumePlacementDescriptor>();
            var domains = new List<Matrix4x4>();
            bool first = true;
            foreach (var region in m_Regions)
            {
                var bounds = FitBounds(region);
                descriptors.Add(new ProbeVolumePlacementDescriptor(region, bounds));
                domains.Add(region.mode == APRTVolume.Mode.Local ? region.volumeMatrix : Matrix4x4.TRS(bounds.center, Quaternion.identity, bounds.size));
                if (first) { m_Staged.globalBounds = bounds; first = false; }
                else m_Staged.globalBounds.Encapsulate(bounds);
            }
            m_Staged.samplingDomains = domains.ToArray();
            m_Context = new ProbeSubdivisionContext();
            var profile = new ProbeVolumeLayout(m_Staged.bakedSimplificationLevels, m_Staged.bakedMinDistanceBetweenProbes, m_Staged.bakedProbeOffset);
            m_Context.Initialize(m_Set, profile, descriptors);
            if (m_Context.cells.Count > 100000) throw new InvalidOperationException("APRT placement exceeds 100000 cells. Reduce the volume bounds or increase cell size.");
            m_Placement = new ProbeSubdivisionResult();
        }

        void BuildReferenceGrid(APRTVolume region)
        {
            var dimensions = region.regularGridDimensions;
            if (dimensions.x < 2 || dimensions.y < 2 || dimensions.z < 2 ||
                (long)dimensions.x * dimensions.y * dimensions.z > k_MaxProbeCount ||
                float.IsNaN(region.regularGridSpacing) || float.IsInfinity(region.regularGridSpacing) || region.regularGridSpacing < 0.01f)
                throw new InvalidOperationException("APRT Reference Grid needs at least two probes per axis, positive spacing and at most 250000 probes.");
            bool Finite(Vector3 value) =>
                !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
            var samplingSize = region.regularGridSamplingSize;
            if (!Finite(samplingSize) || (samplingSize != Vector3.zero &&
                (samplingSize.x <= 0 || samplingSize.y <= 0 || samplingSize.z <= 0)) ||
                !Finite(region.regularGridSamplingOriginOffset))
                throw new InvalidOperationException("APRT Reference Grid sampling size must be zero or positive on every axis, and its sampling offset must be finite.");
            m_Staged.regularGridOrigin = region.regularGridOrigin;
            m_Staged.regularGridRotation = region.transform.rotation;
            m_Staged.regularGridDimensions = dimensions;
            m_Staged.regularGridSpacing = region.regularGridSpacing;
            m_Staged.regularGridSamplingSize = samplingSize;
            m_Staged.regularGridSamplingOrigin = region.regularGridOrigin + region.transform.rotation * region.regularGridSamplingOriginOffset;
            var extent = (Vector3)(dimensions - Vector3Int.one) * region.regularGridSpacing;
            var gridMatrix = Matrix4x4.TRS(region.regularGridOrigin + region.transform.rotation * (extent * 0.5f), region.transform.rotation, extent);
            m_Staged.samplingDomains = new[] { gridMatrix };
            var bounds = new TProbeReferenceVolumeBase.Volume(gridMatrix, 1, -1).CalculateAABB();
            m_Staged.globalBounds = bounds;
            var profile = new ProbeVolumeLayout(m_Staged.bakedSimplificationLevels, m_Staged.bakedMinDistanceBetweenProbes, m_Staged.bakedProbeOffset);
            var positions = new Dictionary<Vector3Int, List<(int gridIndex, Vector3 position)>>();
            for (int z = 0; z < dimensions.z; ++z)
                for (int y = 0; y < dimensions.y; ++y)
                    for (int x = 0; x < dimensions.x; ++x)
                    {
                        var position = region.regularGridOrigin + region.transform.rotation * (new Vector3(x, y, z) * region.regularGridSpacing);
                        var cellPosition = profile.PositionToCell(position);
                        if (!positions.TryGetValue(cellPosition, out var list)) positions.Add(cellPosition, list = new());
                        list.Add((x + dimensions.x * (y + dimensions.y * z), position));
                    }
            m_Staged.regularGridProbeIndices = new int[dimensions.x * dimensions.y * dimensions.z];
            int globalOffset = 0;
            foreach (var group in positions.OrderBy(p => p.Key.z).ThenBy(p => p.Key.y).ThenBy(p => p.Key.x))
            {
                var cell = AllocateCell(group.Key, group.Value.Select(p => p.position).ToArray());
                foreach (var scene in m_Scenes) m_SceneLists[scene].Add(cell.index);
                for (int i = 0; i < group.Value.Count; ++i) m_Staged.regularGridProbeIndices[group.Value[i].gridIndex] = globalOffset + i;
                globalOffset += cell.probeCount;
            }
            FinalizePlacementMetadata();
        }

        APRTBakedCell AllocateCell(Vector3Int position, Vector3[] probes)
        {
            if ((long)m_Staged.sampleCount + probes.Length > k_MaxProbeCount)
                throw new InvalidOperationException("APRT exceeds 250000 probes. Increase probe spacing, simplify placement or reduce bounds.");
            var cell = new APRTBakedCell
            {
                index = m_Staged.cells.Count, position = position, probeCount = probes.Length, probePositions = probes,
                axisData = new Vector4[probes.Length * 6], visibilityMoments = new Vector4[probes.Length * 6], probeValidity = new float[probes.Length],
                skyIrradianceTransfer = new float[probes.Length * APRTBakedData.k_SkyIrradianceTransferCoefficientCount],
                skyIrradianceTransferRGB = m_Staged.hasSkyBounceTransfer
                    ? new Vector3[probes.Length * APRTBakedData.k_SkyIrradianceTransferCoefficientCount] : Array.Empty<Vector3>(),
                brickPositions = Array.Empty<Vector3Int>(), brickSubdivisions = Array.Empty<int>(), brickProbeIndices = Array.Empty<int>()
            };
            m_Staged.cells.Add(cell); m_Staged.sampleCount += probes.Length;
            return cell;
        }

        void FinishAdaptivePlacement()
        {
            foreach (var source in m_Placement.cells.OrderBy(c => c.position.z).ThenBy(c => c.position.y).ThenBy(c => c.position.x))
            {
                // Quantize in the shared minimum-probe coordinate system so shared brick edges deduplicate exactly.
                ProbeVolumePlacement.ConvertBricksToPositions(m_Context.profile, source.bricks, out var all, out _);
                var unique = new List<Vector3>();
                var indices = new Dictionary<Vector3Int, int>();
                var brickIndices = new int[all.Length];
                for (int i = 0; i < all.Length; ++i)
                {
                    var key = Vector3Int.RoundToInt((all[i] - m_Context.profile.probeOffset) / m_Context.profile.minDistanceBetweenProbes);
                    if (!indices.TryGetValue(key, out int local))
                    { local = unique.Count; unique.Add(all[i]); indices.Add(key, local); }
                    brickIndices[i] = local;
                }
                var cell = AllocateCell(source.position, unique.ToArray());
                cell.brickPositions = source.bricks.Select(b => b.position).ToArray();
                cell.brickSubdivisions = source.bricks.Select(b => b.subdivisionLevel).ToArray();
                cell.brickProbeIndices = brickIndices;
                if (m_Placement.scenesPerCells.TryGetValue(source.position, out var scenes))
                    foreach (var scene in scenes)
                        if (m_SceneLists.TryGetValue(scene, out var list)) list.Add(cell.index);
            }
            m_Subdivision?.Dispose(); m_Subdivision = null;
            FinalizePlacementMetadata();
        }

        void FinalizePlacementMetadata()
        {
            if (m_Staged.cells.Count == 0) throw new InvalidOperationException("No APRT probes were placed. Check the volume bounds, layer mask, Contribute GI and Fill Empty Spaces settings.");
            m_Staged.minCellPosition = m_Staged.maxCellPosition = m_Staged.cells[0].position;
            foreach (var cell in m_Staged.cells)
            {
                m_Staged.minCellPosition = Vector3Int.Min(m_Staged.minCellPosition, cell.position);
                m_Staged.maxCellPosition = Vector3Int.Max(m_Staged.maxCellPosition, cell.position);
            }
        }

        void Update()
        {
            if (!m_Running) return;
            try
            {
                if (m_Set != null && m_Set.GetLightingScenario(backendId) != m_ScenarioName)
                {
                    UnityEngine.Debug.LogError("APRT lighting scenario selection changed during baking. The previous bake was retained; bake again after editing.", m_Set);
                    Cancel(); return;
                }
                if (m_Set == null || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode || !m_SceneSetup.Matches())
                { Cancel(); return; }
                if (m_Context != null && m_PlacementCursor < m_Context.cells.Count)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Bake APRT", $"Placing Adaptive cell {m_PlacementCursor + 1}/{m_Context.cells.Count}",
                        0.1f * m_PlacementCursor / Mathf.Max(1, m_Context.cells.Count)))
                    { Cancel(); return; }
                    m_Subdivision ??= ProbePlacement.AllocateGPUResources(m_Context.probeVolumes.Count, m_Context.profile);
                    var cell = m_Context.cells[m_PlacementCursor++];
                    ProbeVolumePlacement.BakeCell(m_Context, m_Contributors, m_Subdivision, cell.position, cell.bounds, m_Placement);
                    return;
                }
                if (m_Context != null && m_Capture == null) FinishAdaptivePlacement();
                m_Capture ??= new APRTProbeCapture(m_Contributors, Mathf.Clamp(m_Set.aprtCaptureResolution, 8, 128), Mathf.Clamp(m_Set.aprtCaptureDistance, 0.1f, 10000),
                    m_Staged.lightingModel, m_Set.aprtBakeSaturationPriority, m_Set.aprtBakeCenterWeight);
                if (m_Staged.hasSkyBounceTransfer)
                    m_SkyBounce ??= new APRTSkyBounceBaker(m_Contributors, Mathf.Clamp(m_Set.aprtSkyBouncePrimarySamples, 64, 1024),
                        Mathf.Clamp(m_Set.aprtSkyBounceSkySamples, 16, 256), Mathf.Clamp(m_Set.aprtCaptureDistance, 0.1f, 10000));
                string captureStage = m_Staged.hasSkyBounceTransfer ? "Capturing sky and one wall bounce" : "Capturing";
                if (EditorUtility.DisplayCancelableProgressBar("Bake APRT", $"{captureStage} probe {m_CompletedProbes + 1}/{m_Staged.sampleCount}",
                    0.1f + 0.9f * m_CompletedProbes / Mathf.Max(1, m_Staged.sampleCount)))
                { Cancel(); return; }
                var stopwatch = Stopwatch.StartNew();
                do
                {
                    var cell = m_Staged.cells[m_CellCursor];
                    m_Capture.Capture(cell.probePositions[m_ProbeCursor], cell.axisData, cell.visibilityMoments, m_ProbeCursor,
                        out cell.probeValidity[m_ProbeCursor], cell.skyIrradianceTransfer);
                    if (m_SkyBounce != null)
                    {
                        m_SkyBounce.Capture(cell.probePositions[m_ProbeCursor], cell.skyIrradianceTransferRGB, m_ProbeCursor);
                        int first = m_ProbeCursor * APRTBakedData.k_SkyIrradianceTransferCoefficientCount;
                        for (int coefficient = 0; coefficient < APRTBakedData.k_SkyIrradianceTransferCoefficientCount; ++coefficient)
                            cell.skyIrradianceTransferRGB[first + coefficient] += Vector3.one * cell.skyIrradianceTransfer[first + coefficient];
                    }
                    ++m_CompletedProbes;
                    if (++m_ProbeCursor == cell.probeCount) { ++m_CellCursor; m_ProbeCursor = 0; }
                }
                while (m_CellCursor < m_Staged.cells.Count && stopwatch.ElapsedMilliseconds < 15);
                if (m_CellCursor == m_Staged.cells.Count) Publish();
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogException(exception, m_Set);
                Cleanup(); ProbeVolumeBakingProvider.Complete(this);
            }
        }

        void Publish()
        {
            // Cancellation never reaches this point, so the last successful asset remains intact throughout capture.
            if (SourceRevision() != m_SourceRevision)
                throw new InvalidOperationException("APRT lighting, geometry, materials, regions or bake settings changed during baking. The previous bake was retained; bake again after editing.");
            if (!m_Staged.HasValidSharedData())
                throw new InvalidOperationException("APRT capture produced invalid transport data: " + DescribeInvalidCapture());
            EvaluateBakedLighting(m_Staged, m_LightingInputs, m_LightingSettings);
            m_Staged.SetBakedLightingScenario(m_LightingInputs);
            if (SourceRevision() != m_SourceRevision)
                throw new InvalidOperationException("APRT lighting or bake inputs changed while evaluating the scenario. The previous bake was retained; bake again after editing.");
            // Keep new transport and scenario SH inline in the Baking Set.
            // Property-diff Undo can spend minutes matching large arrays in native Flush;
            // a complete snapshot retains bake Undo without that array diff.
            Undo.RegisterCompleteObjectUndo(m_Set, "Bake APRT");
            m_Set.CommitAPRTBakedData(m_Staged);
            m_Set.aprtUseLightingScenarios = true;
            var descriptions = m_Set.GetCellDescs(backendId);
            descriptions.Clear();
            foreach (var cell in m_Staged.cells)
                descriptions.Add(cell.index, new CellDesc
                {
                    index = cell.index, position = cell.position, probeCount = cell.probeCount,
                    bricksCount = cell.brickPositions.Length,
                    minSubdiv = cell.brickSubdivisions.Length > 0 ? cell.brickSubdivisions.Min() : 0
                });
            m_Set.SetPerSceneCellLists(backendId, m_SceneLists);
            m_Volume.SetActiveBakingSet(m_Set);
            foreach (var guid in m_Scenes)
            {
                var scene = SceneManager.GetSceneByPath(AssetDatabase.GUIDToAssetPath(guid));
                var perScene = m_Set.EnsureAPRTPerSceneData(scene);
                perScene.Initialize(backendId);
                EditorSceneManager.MarkSceneDirty(scene);
            }
            EditorUtility.SetDirty(m_Set);
            AssetDatabase.SaveAssetIfDirty(m_Set);
            m_Committed = true;
            m_Volume.ReloadBakedData();
            UnityEngine.Debug.Log($"APRT baked lighting scenario '{m_ScenarioName}' with {m_Staged.sampleCount} probes in {m_Staged.cells.Count} cells" +
                (m_Staged.hasSkyBounceTransfer ? " with offline sky one-bounce transfer." : "."), m_Set);
            Cleanup(); ProbeVolumeBakingProvider.Complete(this);
        }

        internal static void EvaluateBakedLighting(APRTBakedData data, APRTLightingScenario lighting, APRTRelightSettings settings)
        {
            if (data == null || !data.HasValidSharedData() || lighting == null || !lighting.IsValid())
                throw new ArgumentException("Valid APRT transport and lighting inputs are required to bake a scenario.");
            var shader = Resources.Load<ComputeShader>("APRTRelight");
            if (!SystemInfo.supportsComputeShaders || shader == null)
                throw new InvalidOperationException("APRT scenario baking requires compute shader support and the APRTRelight resource.");
            int probeCount = data.cells.Sum(cell => cell.probeCount);
            var axis = data.cells.SelectMany(cell => cell.axisData).ToArray();
            var scalarSky = data.hasSkyIrradianceTransfer && !data.hasSkyBounceTransfer
                ? data.cells.SelectMany(cell => cell.skyIrradianceTransfer).ToArray() : new float[1];
            var rgbSky = data.hasSkyBounceTransfer
                ? data.cells.SelectMany(cell => cell.skyIrradianceTransferRGB).ToArray() : new Vector3[1];
            var owned = new List<GraphicsBuffer>();
            GraphicsBuffer Upload<T>(T[] values, int stride) where T : struct
            {
                var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, values.Length), stride);
                owned.Add(buffer);
                if (values.Length > 0) buffer.SetData(values);
                return buffer;
            }
            float Intensity(float value) => !float.IsNaN(value) && !float.IsInfinity(value) ? Mathf.Max(0, value) : 0;
            using var command = new CommandBuffer { name = "Bake APRT Lighting Scenario" };
            try
            {
                int kernel = shader.FindKernel("Relight");
                var axisBuffer = Upload(axis, 16);
                var scalarBuffer = Upload(scalarSky, 4);
                var rgbBuffer = Upload(rgbSky, 12);
                var directionalBuffer = Upload(new Vector4[probeCount * 4], 16);
                var skyBuffer = Upload(new Vector4[probeCount * 4], 16);
                var unusedScenarioBuffer = Upload(new Vector4[1], 16);
                command.SetComputeIntParam(shader, "_APRTProbeCount", probeCount);
                command.SetComputeIntParam(shader, "_APRTLightingModel", (int)data.lightingModel);
                command.SetComputeFloatParam(shader, "_APRTTemporalBlend", 1);
                command.SetComputeFloatParam(shader, "_APRTBounceIntensity", Intensity(settings.bounceIntensity));
                command.SetComputeFloatParam(shader, "_APRTIndirectIntensity", Intensity(settings.indirectIntensity));
                command.SetComputeFloatParam(shader, "_APRTIndirectContrast", Intensity(settings.indirectContrast));
                command.SetComputeFloatParam(shader, "_APRTSaturationBoost", Intensity(settings.saturationBoost));
                command.SetComputeFloatParam(shader, "_APRTShadowGIVisibility", Mathf.Clamp01(Intensity(settings.shadowGIVisibility)));
                command.SetComputeFloatParam(shader, "_APRTSkyVisibilityPower", Intensity(settings.skyVisibilityPower));
                command.SetComputeFloatParam(shader, "_APRTSkyIrradianceIntensity", Intensity(settings.skyIntensity));
                command.SetComputeIntParam(shader, "_APRTHasSkyIrradianceTransfer", data.hasSkyIrradianceTransfer ? 1 : 0);
                command.SetComputeIntParam(shader, "_APRTHasSkyBounceTransfer", data.hasSkyBounceTransfer ? 1 : 0);
                new APRTScenarioBlendSnapshot(lighting.colors, lighting.directions, lighting.skyRadiance).Bind(command, shader);
                command.SetComputeBufferParam(shader, kernel, "_APRTBakedAxis", axisBuffer);
                command.SetComputeBufferParam(shader, kernel, "_APRTSkyTransfer", scalarBuffer);
                command.SetComputeBufferParam(shader, kernel, "_APRTSkyTransferRGB", rgbBuffer);
                command.SetComputeBufferParam(shader, kernel, "_APRTRuntimeSH", directionalBuffer);
                command.SetComputeBufferParam(shader, kernel, "_APRTSkyIrradianceSH", skyBuffer);
                command.SetComputeBufferParam(shader, kernel, "_APRTBakedDirectionalSH", unusedScenarioBuffer);
                command.SetComputeBufferParam(shader, kernel, "_APRTBakedSkyIrradianceSH", unusedScenarioBuffer);
                ShaderUtil.SetAsyncCompilation(command, false);
                command.DispatchCompute(shader, kernel, (probeCount + 63) / 64, 1, 1);
                Graphics.ExecuteCommandBuffer(command);
                var directionalSH = new Vector4[probeCount * 4];
                var skySH = new Vector4[probeCount * 4];
                directionalBuffer.GetData(directionalSH);
                skyBuffer.GetData(skySH);
                // The staged snapshot changes only after both readbacks finish.
                // Final validation and the asset commit happen in Publish.
                lighting.bakedDirectionalSH = directionalSH;
                lighting.bakedSkyIrradianceSH = skySH;
            }
            finally
            {
                foreach (var buffer in owned) buffer.Dispose();
            }
        }

        string DescribeInvalidCapture()
        {
            bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
            foreach (var cell in m_Staged.cells)
            {
                if (m_Staged.hasSkyIrradianceTransfer && (cell.skyIrradianceTransfer == null ||
                    cell.skyIrradianceTransfer.LongLength != (long)cell.probeCount * APRTBakedData.k_SkyIrradianceTransferCoefficientCount))
                    return $"cell {cell.index}, sky irradiance transfer length={cell.skyIrradianceTransfer?.Length ?? 0}, expected={(long)cell.probeCount * APRTBakedData.k_SkyIrradianceTransferCoefficientCount}";
                if (m_Staged.hasSkyBounceTransfer && (cell.skyIrradianceTransferRGB == null ||
                    cell.skyIrradianceTransferRGB.LongLength != (long)cell.probeCount * APRTBakedData.k_SkyIrradianceTransferCoefficientCount))
                    return $"cell {cell.index}, RGB sky transfer length={cell.skyIrradianceTransferRGB?.Length ?? 0}, expected={(long)cell.probeCount * APRTBakedData.k_SkyIrradianceTransferCoefficientCount}";
                for (int probe = 0; probe < cell.probeCount; ++probe)
                {
                    float value = cell.probeValidity[probe];
                    if (!Finite(value) || value < 0 || value > 1)
                        return $"cell {cell.index}, probe {probe} validity={value:R}, position={cell.probePositions[probe]}";
                    if (m_Staged.hasSkyIrradianceTransfer)
                        for (int coefficient = 0; coefficient < APRTBakedData.k_SkyIrradianceTransferCoefficientCount; ++coefficient)
                        {
                            float transfer = cell.skyIrradianceTransfer[probe * APRTBakedData.k_SkyIrradianceTransferCoefficientCount + coefficient];
                            if (!Finite(transfer))
                                return $"cell {cell.index}, probe {probe}, sky irradiance transfer coefficient {coefficient}={transfer:R}";
                            if (m_Staged.hasSkyBounceTransfer)
                            {
                                var rgb = cell.skyIrradianceTransferRGB[probe * APRTBakedData.k_SkyIrradianceTransferCoefficientCount + coefficient];
                                if (!Finite(rgb.x) || !Finite(rgb.y) || !Finite(rgb.z))
                                    return $"cell {cell.index}, probe {probe}, RGB sky transfer coefficient {coefficient}={rgb}";
                            }
                        }
                    for (int face = 0; face < 6; ++face)
                    {
                        int index = probe * 6 + face;
                        var axis = cell.axisData[index]; var moments = cell.visibilityMoments[index];
                        if (!Finite(axis.x) || !Finite(axis.y) || !Finite(axis.z) || !Finite(axis.w) ||
                            axis.x < 0 || axis.y < 0 || axis.z < 0 || axis.w < 0 || axis.w > 1 ||
                            !Finite(moments.x) || !Finite(moments.y) || moments.x < 0 || moments.y < 0)
                            return $"cell {cell.index}, probe {probe}, face {face}, axis=({axis.x:R},{axis.y:R},{axis.z:R},{axis.w:R}), moments=({moments.x:R},{moments.y:R})";
                    }
                }
            }
            return $"layout metadata failed validation (format={m_Staged.formatVersion}, regularGrid={m_Staged.regularGrid}, cells={m_Staged.cells.Count}, probes={m_Staged.sampleCount})";
        }

        public override bool Cancel()
        {
            if (!m_Running) return false;
            Cleanup(); ProbeVolumeBakingProvider.Complete(this);
            return true;
        }

        internal override void Clear()
        {
            Cancel();
            (m_Set.GetBakedData(backendId) as APRTBakedData)?.ClearAssets();
            m_Set.GetCellDescs(backendId).Clear();
            m_Set.SetPerSceneCellLists(backendId, new Dictionary<string, List<int>>());
            m_Set.GetCellDataMap(backendId).Clear();
            EditorUtility.SetDirty(m_Set);
            m_Volume.ReloadBakedData();
        }

        void Cleanup()
        {
            EditorApplication.update -= Update;
            m_Running = false;
            m_Capture?.Dispose(); m_Capture = null;
            m_SkyBounce?.Dispose(); m_SkyBounce = null;
            m_Subdivision?.Dispose(); m_Subdivision = null;
            EditorUtility.ClearProgressBar();
            m_Staged = null; m_Context = null; m_Placement = null;
            m_LightingInputs = null; m_ScenarioName = null;
            m_Regions = null; m_Scenes = null; m_SceneLists = null;
        }
        internal override void Dispose() { Cancel(); Cleanup(); }
    }
}
