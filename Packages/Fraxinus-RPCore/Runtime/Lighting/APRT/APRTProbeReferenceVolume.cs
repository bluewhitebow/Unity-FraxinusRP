using System;
using System.Collections.Generic;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering
{
    /// <summary>Adaptive six-axis radiance transfer with GPU relighting and probe interpolation.</summary>
    internal sealed partial class APRTProbeReferenceVolume : TProbeReferenceVolumeBase
    {
        internal const string k_BackendId = "APRT";
        internal static APRTProbeReferenceVolume instance { get; } = new();
        readonly List<ProbeVolumePerSceneData> m_PerSceneData = new();
        readonly ProbeVolumeBakingSetWeakReference m_BakingSet = new();
        readonly ProbeVolumeDebug m_Debug = new();
        bool m_Initialized, m_Enabled;
        float m_MinBrickSize = 3f;
        int m_MaxSubdivision = 1;
        Vector3 m_ProbeOffset;

        public override bool isInitialized => m_Initialized;
        internal override string backendId => k_BackendId;
        internal override bool enabledBySRP => m_Enabled;
        internal override List<ProbeVolumePerSceneData> perSceneDataList => m_PerSceneData;
        internal override bool clearAssetsOnVolumeClear { get; set; }
        internal override Bounds globalBounds { get; set; }
        public override ProbeVolumeSHBands shBands => ProbeVolumeSHBands.SphericalHarmonicsL2;
        public override bool probeOcclusion => false;
        internal override bool skyOcclusion => false;
        internal override bool skyOcclusionShadingDirection => false;
        public override ProbeVolumeBakingSet currentBakingSet => m_BakingSet.Get();
        public override string lightingScenario
        {
            get => currentBakingSet != null ? currentBakingSet.GetLightingScenario(k_BackendId) : defaultLightingScenario;
            set { if (focusedRuntime is { } runtime) runtime.lightingScenario = value; else SetLightingScenario(value); }
        }
        public override string otherScenario => currentBakingSet != null ? currentBakingSet.GetOtherScenario(k_BackendId) : null;
        public override float scenarioBlendingFactor
        {
            get => currentBakingSet != null ? currentBakingSet.GetScenarioBlendingFactor(k_BackendId) : 0;
            set => BlendLightingScenario(otherScenario, value);
        }
        public override ProbeVolumeTextureMemoryBudget memoryBudget => ProbeVolumeTextureMemoryBudget.MemoryBudgetHigh;
        internal override float debugMinStreamingScore => 0;
        internal override float debugMaxStreamingScore => 0;
        internal override bool supportLightingScenarios => m_SupportScenarios;
        internal override bool supportScenarioBlending => m_SupportBlending;
        internal override bool diskStreamingEnabled => false;
        public override bool loadMaxCellsPerFrame { get => true; set { } }
        public override int numberOfCellsBlendedPerFrame { get => int.MaxValue; set { } }
        public override float turnoverRate { get => 0; set { } }
        internal override ProbeVolumeDebug probeVolumeDebug => m_Debug;
        public override Color[] subdivisionDebugColors => Array.Empty<Color>();

        internal void Initialize(in ProbeVolumeSystemParameters parameters)
        {
            ClearBakingSetRuntimes();
            m_SystemParameters = parameters;
            ReloadBakedData();
            m_Initialized = true;
            m_Enabled = parameters.enableAPRT;
            ConfigureLoading(parameters);
            if (ReferenceEquals(this, instance))
            {
#if UNITY_EDITOR
                ProbeVolumeBakingSet.SyncBakingSets();
                UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= OnSceneSaving;
                UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += OnSceneSaving;
                UnityEditor.Undo.undoRedoPerformed -= OnEditorUndoRedo;
                UnityEditor.Undo.undoRedoPerformed += OnEditorUndoRedo;
#endif
                foreach (var data in Object.FindObjectsByType<ProbeVolumePerSceneData>(FindObjectsSortMode.None))
                    if (data.isActiveAndEnabled) RegisterPerSceneData(data);
                foreach (var data in m_PerSceneData.ToArray())
                    if (data != null && data.isActiveAndEnabled) data.Initialize(k_BackendId);
            }
            if (m_RuntimeOwner == null) InitializeDebug();
        }

        internal void Cleanup()
        {
            RenderPipelineManager.endContextRendering -= OnFrameEnd;
            m_ResourcesPinned = false;
            ClearBakingSetRuntimes();
            ReleaseRetiredBakingSets();
            ReleaseSamplingAtlas();
            m_ResidentSamplingScratch.Clear();
            m_PinnedSetRuntimes.Clear();
            CleanupDebug();
#if UNITY_EDITOR
            if (ReferenceEquals(this, instance))
            {
                UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= OnSceneSaving;
                UnityEditor.Undo.undoRedoPerformed -= OnEditorUndoRedo;
            }
#endif
            Clear();
            CoreUtils.Destroy(relightCompute);
            relightCompute = null;
            m_CellProbeStarts = null;
            m_PerSceneData.Clear();
            m_Initialized = m_Enabled = false;
            retrieveExtraDataAction = null;
            checksDuringBakeAction = null;
        }

        internal override void Clear()
        {
            ClearBakingSetRuntimes();
            UnloadAllCells();
            m_LoadedScenes.Clear();
            m_BakingSet.Set(null);
            globalBounds = default;
        }

#if UNITY_EDITOR
        static void OnSceneSaving(Scene scene, string path) => ProbeVolumeBakingSet.OnSceneSaving(scene, path);

        internal void OnEditorUndoRedo()
        {
            var set = currentBakingSet;
            if (set != null) set.SetAPRTScenarioBlend(null, 0, this);
            ReloadBakedData();
        }

        internal override bool EnsureCurrentBakingSet(ProbeVolumeBakingSet bakingSet)
        {
            SetActiveBakingSet(bakingSet);
            return bakingSet != null;
        }
#endif

        public override void SetEnableStateFromSRP(bool srpEnablesPV)
        {
            m_Enabled = srpEnablesPV;
            foreach (var runtime in m_LoadedSetRuntimes) runtime.SetEnableStateFromSRP(srpEnablesPV);
            if (!m_Enabled) InvalidateGlobalBinding();
            UpdateDebugRegistration();
        }
        public override void SetVertexSamplingEnabled(bool value) { }
        public override void BlendLightingScenario(string otherScenario, float blendingFactor)
        {
            if (focusedRuntime is { } runtime) { runtime.BlendLightingScenario(otherScenario, blendingFactor); return; }
            if (!supportScenarioBlending) return;
            var set = currentBakingSet;
            if (set == null || !ValidIntensity(blendingFactor) || !HasLightingScenario(lightingScenario) ||
                (!string.IsNullOrEmpty(otherScenario) && !HasLightingScenario(otherScenario))) return;
            var data = set.GetBakedData(k_BackendId) as APRTBakedData;
            bool baked = HasBakedLightingMarkers(data.FindLightingScenario(lightingScenario));
            if (!string.IsNullOrEmpty(otherScenario) &&
                baked != HasBakedLightingMarkers(data.FindLightingScenario(otherScenario))) return;
            set.SetAPRTScenarioBlend(otherScenario, blendingFactor, this);
            set.aprtUseLightingScenarios = true;
        }
        public override void SetActiveScene(Scene scene)
        {
            foreach (var data in m_PerSceneData)
                if (data != null && data.gameObject.scene == scene)
                {
                    SetActiveBakingSet(data.serializedBakingSet);
                    return;
                }
#if UNITY_EDITOR
            SetActiveBakingSet(ProbeVolumeBakingSet.GetBakingSetForScene(scene));
#endif
        }
        public override void SetActiveBakingSet(ProbeVolumeBakingSet bakingSet)
        {
            if (currentBakingSet == bakingSet) return;
            if (coordinatesBakingSets)
            {
                m_BakingSet.Set(bakingSet);
                if (bakingSet != null) GetBakingSetRuntime(bakingSet, true);
                return;
            }
            Clear();
            m_BakingSet.Set(bakingSet);
            // A scene already enabled in another set won't send a new OnEnable
            // notification when users switch sets explicitly.
            if (bakingSet != null)
                foreach (var data in m_PerSceneData)
                    if (data != null && data.isActiveAndEnabled && data.serializedBakingSet == bakingSet)
                        m_LoadedScenes.Add(data.sceneGUID);
        }
        public override int GetVideoMemoryCost()
        {
            long bytes = RuntimeMemoryCost() + samplingAtlasMemoryBytes;
            foreach (var runtime in m_LoadedSetRuntimes) bytes += runtime.RuntimeMemoryCost();
            foreach (var runtime in m_RetiredSetRuntimes) bytes += runtime.RuntimeMemoryCost();
            return (int)Math.Min(int.MaxValue, bytes);
        }
        public override void PerformPendingOperations()
        {
            if (m_ResourcesPinned) return;
            if (coordinatesBakingSets)
            {
                foreach (var runtime in m_LoadedSetRuntimes) runtime.PerformPendingOperations();
            }
            else if (currentBakingSet != null) UpdateLoadedCells(currentBakingSet);
        }
        public override bool DataHasBeenLoaded()
        {
            if (coordinatesBakingSets)
            {
                foreach (var runtime in m_LoadedSetRuntimes) if (runtime.DataHasBeenLoaded()) return true;
                return false;
            }
            return m_RuntimeInitialized && runtimeSHBuffer != null && m_RuntimeData != null && m_RuntimeData.formatVersion == APRTBakedData.k_CurrentVersion;
        }
        internal override float BrickSize(int subdivisionLevel) => m_MinBrickSize * CellSize(subdivisionLevel);
        internal override float MinBrickSize() => m_MinBrickSize;
        internal override float MaxBrickSize() => BrickSize(m_MaxSubdivision - 1);
        internal override Vector3 ProbeOffset() => m_ProbeOffset;
        internal override int GetMaxSubdivision() => m_MaxSubdivision;
        internal override int GetEntrySubdivLevel() => 0;
        internal override float MinDistanceBetweenProbes() => m_MinBrickSize / 3f;
        internal override void SetSubdivisionDimensions(float minBrickSize, int maxSubdiv, Vector3 offset)
        {
            m_MinBrickSize = minBrickSize;
            m_MaxSubdivision = maxSubdiv;
            m_ProbeOffset = offset;
        }
        internal override void RegisterPerSceneData(ProbeVolumePerSceneData data)
        {
            if (data == null || m_PerSceneData.Contains(data)) return;
            m_PerSceneData.Add(data);
            if (m_Initialized && data.isActiveAndEnabled && data.serializedBakingSet != null)
                AddPendingSceneLoading(data.sceneGUID, data.serializedBakingSet);
        }
        internal override void UnregisterPerSceneData(ProbeVolumePerSceneData data)
        {
            m_PerSceneData.Remove(data);
            if (data != null) AddPendingSceneRemoval(data.sceneGUID);
        }
        internal override void RegisterBakingSet(ProbeVolumePerSceneData data)
        {
            if (data != null && data.serializedBakingSet != null && currentBakingSet == null)
                SetActiveBakingSet(data.serializedBakingSet);
        }
        internal override bool TryGetPerSceneData(string sceneGUID, out ProbeVolumePerSceneData perSceneData)
        {
            foreach (var data in m_PerSceneData)
                if (data != null && data.sceneGUID == sceneGUID) { perSceneData = data; return true; }
            perSceneData = null;
            return false;
        }
        internal override void AddPendingSceneLoading(string sceneGUID, ProbeVolumeBakingSet bakingSet)
        {
            if (bakingSet == null) return;
            if (m_RuntimeOwner == null) RegisterSceneBakingSet(sceneGUID, bakingSet);
            else if (currentBakingSet == bakingSet && m_LoadedScenes.Add(sceneGUID)) m_LoadedCellsDirty = true;
        }
        internal override void AddPendingSceneRemoval(string sceneGUID)
        {
            if (coordinatesBakingSets) { RemoveSceneBakingSet(sceneGUID); return; }
            if (!m_LoadedScenes.Remove(sceneGUID)) return;
            m_LoadedCellsDirty = true;
            if (m_LoadedScenes.Count != 0) return;
            Clear();
            // Legacy explicit activation can have no per-set registrations.
            // Select another available scene after its active set leaves.
            foreach (var data in m_PerSceneData)
                if (data != null && data.isActiveAndEnabled && data.sceneGUID != sceneGUID && data.serializedBakingSet != null)
                {
                    SetActiveBakingSet(data.serializedBakingSet);
                    break;
                }
        }
        internal override void ScenarioBlendingChanged(bool scenarioChanged)
        {
            // The recorded snapshot identifies its endpoints. History is reset
            // when that snapshot executes, even if selection changes meanwhile.
        }
        internal override void UnloadAllCells()
        {
            foreach (var runtime in m_LoadedSetRuntimes) runtime.ReloadBakedData();
            ReloadBakedData();
        }
        // The shared backend contract exposes APV streaming controls. APRT
        // loads complete scene data through PrepareRelight instead.
        public override void EnableMaxCellStreaming(bool value) { }
        public override void SetNumberOfCellsLoadedPerFrame(int numberOfCells) { }
        public override void UpdateCellStreaming(CommandBuffer cmd, Camera camera) { }
        public override void UpdateCellStreaming(CommandBuffer cmd, Camera camera, ProbeVolumesOptions options) { }

        // Never publish uninitialized or historical transport buffers.
        static void InvalidateGlobalBinding()
        {
            Shader.SetGlobalFloat("_EnableAPRT", 0);
            Shader.SetGlobalFloat("_APRTGIIntensity", 0);
            Shader.SetGlobalFloat("_APRTAddAmbientLighting", 1);
            Shader.SetGlobalInt("_APRTProbeCount", 0);
            Shader.SetGlobalInt("_APRTLightingModel", 0);
            Shader.SetGlobalInt("_APRTDebugMode", 0);
            Shader.SetGlobalInt("_APRTSamplingSetCount", 0);
        }

        public override void BindPRVRuntimeResources(CommandBuffer cmdBuffer, bool isProbeVolumeEnabled)
        {
            BindRuntimeResources(cmdBuffer, isProbeVolumeEnabled);
        }
        public override bool UpdateShaderVariablesProbeVolumes(CommandBuffer cmd, ProbeVolumesOptions probeVolumeOptions,
            int taaFrameIndex, bool supportRenderingLayers = false)
        {
            BindPRVRuntimeResources(cmd, m_Enabled);
            return m_Enabled && DataHasBeenLoaded();
        }
        public override void RenderFragmentationOverlay(RenderGraph renderGraph, TextureHandle colorBuffer,
            TextureHandle depthBuffer, DebugOverlay debugOverlay) { }
        internal override IEnumerable<KeyValuePair<Bounds, ProbeBrickIndex.Brick[]>> GetRealtimeSubdivisionDebugData() =>
            Array.Empty<KeyValuePair<Bounds, ProbeBrickIndex.Brick[]>>();
        internal override IEnumerable<CellDebugInfo> GetCellDebugData() => Array.Empty<CellDebugInfo>();
    }
}
