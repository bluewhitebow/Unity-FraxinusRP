using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering
{
    internal sealed partial class APRTProbeReferenceVolume
    {
        bool m_SupportScenarios, m_SupportBlending;
        bool m_ResourcesPinned, m_DeferredReload, m_LoadedCellsDirty = true, m_SharedDataValid;
        readonly List<int> m_ResidentCells = new(), m_SelectedCells = new();
        readonly List<APRTBakedCell> m_ResidentPayloads = new();
        readonly List<string> m_ResidentScenarioNames = new();
        readonly HashSet<int> m_SceneCellIndices = new();
        readonly HashSet<string> m_ValidBakedScenarios = new();
        int[] m_CellProbeStarts;
        int m_ValidatedRevision = -1;
        string m_RequestedActive, m_RequestedOther, m_LoadError;
        APRTLightingScenario m_ResidentActiveScenario, m_ResidentOtherScenario;
        float m_ResidentBlendFactor;

        internal bool usesLightingScenarios => supportLightingScenarios && currentBakingSet?.aprtUseLightingScenarios == true;
        internal IReadOnlyList<int> residentCellIndices => focusedRuntime?.residentCellIndices ?? m_ResidentCells;
        internal IReadOnlyList<APRTBakedCell> residentCellPayloads => focusedRuntime?.residentCellPayloads ?? m_ResidentPayloads;
        internal int residentCellCount => focusedRuntime?.residentCellCount ?? m_ResidentCells.Count;
        internal int pendingCellRequestCount => 0;
        internal IReadOnlyList<string> residentScenarioNames => focusedRuntime?.residentScenarioNames ?? m_ResidentScenarioNames;
        internal long residentScenarioBytes => (long)probeCount * 128 * m_ResidentScenarioNames.Count;

        void ConfigureLoading(in ProbeVolumeSystemParameters parameters)
        {
            m_SupportScenarios = parameters.supportScenarios;
            m_SupportBlending = parameters.supportScenarios && parameters.supportScenarioBlending;
            // Unity adapts endFrameRendering by allocating a Camera[] from the
            // render context's camera list. The context event keeps that list.
            RenderPipelineManager.endContextRendering -= OnFrameEnd;
            RenderPipelineManager.endContextRendering += OnFrameEnd;
        }

        void OnFrameEnd(ScriptableRenderContext context, List<Camera> cameras)
        {
            m_ResourcesPinned = false;
            // The coordinator's callback runs before its child callbacks.
            // Apply their deferred releases before checking atlas generations.
            foreach (var runtime in m_PinnedSetRuntimes)
            {
                if (ReferenceEquals(runtime, this)) continue;
                runtime.m_ResourcesPinned = false;
                if (runtime.m_DeferredReload) runtime.ReloadBakedData();
            }
            if (m_DeferredReload) ReloadBakedData();
            m_PinnedSetRuntimes.Clear();
            ReleaseRetiredBakingSets();
            // Recorded cameras may still own removed or replaced source buffers
            // until execution finishes. Retain their sampling copies until here.
            if (m_LoadedSetRuntimes.Count == 0 || SamplingAtlasNeedsReleaseAtFrameEnd()) ReleaseSamplingAtlas();
        }

        internal void PinRuntimeResourcesForFrame() => m_ResourcesPinned = gpuBuffers.Length != 0;

        // Cell membership depends on loaded scenes, never on a camera or budget.
        // Ordinary frames only inspect the bake revision and selected endpoints.
        bool UpdateLoadedCells(ProbeVolumeBakingSet set)
        {
            if (m_ResourcesPinned) return runtimeSHBuffer != null;
            if (m_DeferredReload) { m_DeferredReload = false; ReloadBakedData(); }
            var data = set.GetBakedData(k_BackendId) as APRTBakedData;
            if (data == null || data.formatVersion != APRTBakedData.k_CurrentVersion)
            {
                if (m_RuntimeData != null) ReloadBakedData();
                return false;
            }
            if (!ReferenceEquals(data, m_RuntimeData) || m_ValidatedRevision != data.lightingScenarioRevision)
            {
                ReloadBakedData();
                m_RuntimeData = data;
                m_ValidatedRevision = data.lightingScenarioRevision;
                m_SharedDataValid = data.HasValidSharedData();
                if (!m_SharedDataValid) return false;
                m_LoadedLayoutHash = data.GetLayoutHash();
                m_CellProbeStarts = new int[data.cells.Count];
                int start = 0;
                for (int i = 0; i < data.cells.Count; ++i)
                {
                    m_CellProbeStarts[i] = start;
                    start += data.cells[i].probeCount;
                }
                if (data.lightingScenarios != null)
                    foreach (var scenario in data.lightingScenarios)
                        if (scenario != null && data.HasBakedLightingScenario(scenario.name)) m_ValidBakedScenarios.Add(scenario.name);
            }
            if (!m_SharedDataValid) return false;
            APRTLightingScenario active = usesLightingScenarios ? data.FindLightingScenario(lightingScenario) : null;
            bool baked = HasBakedLightingMarkers(active);
            bool wantsOther = baked && supportScenarioBlending && !string.IsNullOrEmpty(otherScenario) && otherScenario != lightingScenario;
            APRTLightingScenario other = wantsOther ? data.FindLightingScenario(otherScenario) : null;
            if (usesLightingScenarios && (active == null || (baked && !m_ValidBakedScenarios.Contains(active.name)) ||
                (wantsOther && other == null) || (other != null && !m_ValidBakedScenarios.Contains(other.name)))) return false;
            if (!baked) active = null;
            string activeName = active?.name, otherName = other?.name;
            bool pairChanged = activeName != m_RequestedActive || otherName != m_RequestedOther;
            if (!m_LoadedCellsDirty && !pairChanged) return m_LoadError == null && runtimeSHBuffer != null;

            m_LoadedCellsDirty = false;
            m_RequestedActive = activeName;
            m_RequestedOther = otherName;
            m_LoadError = null;
            m_SelectedCells.Clear();
            m_SceneCellIndices.Clear();
            foreach (string scene in m_LoadedScenes)
            {
                var indices = set.GetSceneCellIndexList(k_BackendId, scene);
                if (indices != null) foreach (int index in indices) m_SceneCellIndices.Add(index);
            }
            int selectedProbeCount = 0;
            for (int i = 0; i < data.cells.Count; ++i)
                if (m_LoadedScenes.Count == 0 || m_SceneCellIndices.Contains(data.cells[i].index))
                {
                    m_SelectedCells.Add(i);
                    selectedProbeCount += data.cells[i].probeCount;
                }

            bool sameCells = m_SelectedCells.Count == m_ResidentCells.Count;
            if (sameCells)
                for (int i = 0; i < m_SelectedCells.Count; ++i)
                    if (data.cells[m_SelectedCells[i]].index != m_ResidentCells[i]) { sameCells = false; break; }
            if (!pairChanged && sameCells && runtimeSHBuffer != null) return true;

            ReleaseRuntimeResources();
            m_ResidentCells.Clear();
            m_ResidentPayloads.Clear();
            m_ResidentScenarioNames.Clear();
            m_ResidentActiveScenario = active;
            m_ResidentOtherScenario = other;
            m_ResidentBlendFactor = scenarioBlendingFactor;
            if (selectedProbeCount == 0) return false;

            int coefficientCount = active != null ? selectedProbeCount * 4 : 0;
            int atlasCount = coefficientCount * (other != null ? 2 : 1);
            var directional = atlasCount == 0 ? Array.Empty<Vector4>() : new Vector4[atlasCount];
            var sky = atlasCount == 0 ? Array.Empty<Vector4>() : new Vector4[atlasCount];
            float activeSkyMagnitude = 0, otherSkyMagnitude = 0;
            try
            {
                int coefficientOffset = 0;
                foreach (int index in m_SelectedCells)
                {
                    var cell = data.cells[index];
                    // Compatibility with external payloads from older bakes.
                    // New bakes embed these arrays directly in the Baking Set.
                    var payload = cell.streamableAsset == null ? cell : APRTDiskData.ReadSharedSync(cell,
                        m_LoadedLayoutHash, data.hasSkyIrradianceTransfer, data.hasSkyBounceTransfer);
                    m_ResidentCells.Add(cell.index);
                    m_ResidentPayloads.Add(payload);
                    if (active != null)
                    {
                        int count = cell.probeCount * 4;
                        CopyScenarioCell(active, cell, m_CellProbeStarts[index], directional, sky, coefficientOffset, ref activeSkyMagnitude);
                        if (other != null)
                            CopyScenarioCell(other, cell, m_CellProbeStarts[index], directional, sky,
                                coefficientCount + coefficientOffset, ref otherSkyMagnitude);
                        coefficientOffset += count;
                    }
                }
                if (active != null)
                {
                    m_BakedScenarioOffsets.Add(active.name, 0);
                    m_ResidentScenarioNames.Add(active.name);
                    m_BakedScenarioSkyMagnitude.Add(active.name, activeSkyMagnitude);
                }
                if (other != null)
                {
                    m_BakedScenarioOffsets.Add(other.name, coefficientCount);
                    m_ResidentScenarioNames.Add(other.name);
                    m_BakedScenarioSkyMagnitude.Add(other.name, otherSkyMagnitude);
                }
                if (LoadRuntimeData(data, m_ResidentPayloads, m_SelectedCells, m_CellProbeStarts, directional, sky)) return true;
                m_LoadError = "Could not upload APRT baked data";
            }
            catch (Exception exception)
            {
                m_LoadError = "Could not load APRT baked data; rebake the affected Baking Set";
                Debug.LogWarning(m_LoadError + ": " + exception.Message, set);
                ReleaseRuntimeResources();
            }
            m_ResidentCells.Clear();
            m_ResidentPayloads.Clear();
            m_ResidentScenarioNames.Clear();
            return false;
        }

        void CopyScenarioCell(APRTLightingScenario scenario, APRTBakedCell cell, int probeStart,
            Vector4[] directional, Vector4[] sky, int destinationOffset, ref float skyMagnitude)
        {
            Vector4[] sourceDirectional = scenario.bakedDirectionalSH, sourceSky = scenario.bakedSkyIrradianceSH;
            int sourceOffset = probeStart * 4, count = cell.probeCount * 4;
            if (scenario.streamableAssets != null && scenario.streamableAssets.Count != 0)
            {
                var payload = APRTDiskData.ReadScenarioSync(scenario, cell.index, cell.probeCount, m_LoadedLayoutHash);
                sourceDirectional = payload.directionalSH;
                sourceSky = payload.skyIrradianceSH;
                sourceOffset = 0;
            }
            Array.Copy(sourceDirectional, sourceOffset, directional, destinationOffset, count);
            Array.Copy(sourceSky, sourceOffset, sky, destinationOffset, count);
            for (int i = sourceOffset; i < sourceOffset + count; ++i)
            {
                var value = sourceSky[i];
                skyMagnitude = Mathf.Max(skyMagnitude, Mathf.Max(Mathf.Abs(value.x), Mathf.Max(Mathf.Abs(value.y), Mathf.Abs(value.z))));
            }
        }
    }
}
