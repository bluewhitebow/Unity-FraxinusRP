using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine.Serialization;


namespace UnityEngine.Rendering
{
    internal class LogarithmicAttribute : PropertyAttribute
    {
        public int min;
        public int max;

        public LogarithmicAttribute(int min, int max)
        {
            this.min = min;
            this.max = max;
        }
    }

    /// <summary>
    /// An Asset which holds a set of settings to use with a <see cref="Probe Reference Volume"/>.
    /// </summary>
    public sealed partial class ProbeVolumeBakingSet : ScriptableObject, ISerializationCallbackReceiver
    {
        internal enum Version
        {
            Initial,
            AssetsAlwaysReferenced = 2,
        }

        [Serializable]
        internal struct CellCounts
        {
            public int bricksCount;
            public int chunksCount;

            public void Add(CellCounts o)
            {
                bricksCount += o.bricksCount;
                chunksCount += o.chunksCount;
            }
        }

        // Baking Set Data
        [SerializeField] internal bool singleSceneMode = true;
        [SerializeField] internal bool dialogNoProbeVolumeInSetShown = false;
        [SerializeField] internal ProbeVolumeBakingProcessSettings settings;

        internal bool hasDilation => settings.dilationSettings.enableDilation && settings.dilationSettings.dilationDistance > 0.0f;

        // We keep a separate list with only the guids for the sake of convenience when iterating from outside this class.
        [SerializeField] private List<string> m_SceneGUIDs = new List<string>();
        [SerializeField, FormerlySerializedAs("lightingScenarios")] internal List<string> m_LightingScenarios = new List<string>();

        /// <summary>The list of scene GUIDs.</summary>
        public IReadOnlyList<string> sceneGUIDs => m_SceneGUIDs;
        /// <summary>The list of lighting scenarios.</summary>
        public IReadOnlyList<string> lightingScenarios => m_LightingScenarios;

        // List of cell descriptors.
        [SerializeField] internal SerializedDictionary<int, CellDesc> cellDescs = new SerializedDictionary<int, CellDesc>();

        internal Dictionary<int, ICellData> cellDataMap = new Dictionary<int, ICellData>();
        List<int> m_TotalIndexList = new List<int>();

        [Serializable]
        struct SerializedPerSceneCellList
        {
            public string sceneGUID;
            public List<int> cellList;
        }
        [SerializeField] List<SerializedPerSceneCellList> m_SerializedPerSceneCellList;

        // Can't use SerializedDictionary here because we can't serialize a List of List T_T
        internal Dictionary<string, List<int>> perSceneCellLists = new Dictionary<string, List<int>>();

        // APV predates the backend abstraction and keeps its layout in the fields above.  Additional
        // backends get an independent serialized layout and an independent runtime cell data map.  Keeping
        // APV in the legacy fields is intentional: existing assets and editor code continue to deserialize
        // without a migration while APRT (and future backends) cannot accidentally consume APV cells.
        [Serializable]
        class BackendCellLayout
        {
            public string backendId;
            public SerializedDictionary<int, CellDesc> cellDescs = new SerializedDictionary<int, CellDesc>();
            public List<SerializedPerSceneCellList> perSceneCellLists = new List<SerializedPerSceneCellList>();
        }

        [SerializeField] List<BackendCellLayout> m_BackendCellLayouts = new List<BackendCellLayout>();
        [NonSerialized] Dictionary<string, BackendCellLayout> m_BackendCellLayoutsById = new Dictionary<string, BackendCellLayout>();
        [NonSerialized] Dictionary<string, Dictionary<int, ICellData>> m_BackendCellDataMaps = new Dictionary<string, Dictionary<int, ICellData>>();
        [NonSerialized] Dictionary<string, Dictionary<string, List<int>>> m_BackendPerSceneCellLists = new Dictionary<string, Dictionary<string, List<int>>>();

        [SerializeReference] List<ProbeVolumeBakedData> m_BackendBakedData = new List<ProbeVolumeBakedData>();
        internal IReadOnlyList<ProbeVolumeBakedData> backendBakedData => m_BackendBakedData;

        internal ProbeVolumeBakedData bakedData
        {
            get
            {
                var format = GetBakingDataFormat(logError: false);
                return format != null ? GetOrCreateBakedData(format) : null;
            }
        }

        ProbeVolumeBakedData GetOrCreateBakedData(IProbeVolumeBakingDataFormat format)
        {
            var data = FindBakedData(format.backendId);
            if (data == null)
            {
                data = format.CreateBakedData();
                if (data == null)
                {
                    Debug.LogError($"Probe Volume baking data format '{format.backendId}' did not create baked data.");
                    return null;
                }

                if (data.backendId != format.backendId)
                {
                    Debug.LogError($"Probe Volume baking data format '{format.backendId}' created baked data for backend '{data.backendId}'.");
                    return null;
                }

                AddOrReplaceBakedData(data);
            }

            return data;
        }

        ProbeVolumeBakedData FindBakedData(string backendId)
        {
            if (m_BackendBakedData == null)
                m_BackendBakedData = new List<ProbeVolumeBakedData>();

            foreach (var data in m_BackendBakedData)
            {
                if (data != null && data.backendId == backendId)
                    return data;
            }

            return null;
        }

        void AddOrReplaceBakedData(ProbeVolumeBakedData data)
        {
            if (m_BackendBakedData == null)
                m_BackendBakedData = new List<ProbeVolumeBakedData>();

            for (int i = 0; i < m_BackendBakedData.Count; ++i)
            {
                var existing = m_BackendBakedData[i];
                if (existing != null && existing.backendId == data.backendId)
                {
                    m_BackendBakedData[i] = data;
                    return;
                }
            }

            m_BackendBakedData.Add(data);
        }

        internal Vector3Int maxCellPosition { get => bakedData.maxCellPosition; set => bakedData.maxCellPosition = value; }
        internal Vector3Int minCellPosition { get => bakedData.minCellPosition; set => bakedData.minCellPosition = value; }
        internal Bounds globalBounds { get => bakedData.globalBounds; set => bakedData.globalBounds = value; }
        internal int bakedSimplificationLevels { get => bakedData.bakedSimplificationLevels; set => bakedData.bakedSimplificationLevels = value; }
        internal float bakedMinDistanceBetweenProbes { get => bakedData.bakedMinDistanceBetweenProbes; set => bakedData.bakedMinDistanceBetweenProbes = value; }
        internal Vector3 bakedProbeOffset { get => bakedData.bakedProbeOffset; set => bakedData.bakedProbeOffset = value; }

        // Metadata access for generic backends. The legacy properties above intentionally continue to
        // target the active APV data for source compatibility.
        internal ProbeVolumeBakedData GetBakedData(string backendId) => FindBakedData(backendId);
        internal Vector3Int GetMaxCellPosition(string backendId) => FindBakedData(backendId)?.maxCellPosition ?? Vector3Int.zero;
        internal Vector3Int GetMinCellPosition(string backendId) => FindBakedData(backendId)?.minCellPosition ?? Vector3Int.zero;
        internal Bounds GetGlobalBounds(string backendId) => FindBakedData(backendId)?.globalBounds ?? default;
        internal int GetBakedSimplificationLevels(string backendId)
        {
            var data = FindBakedData(backendId);
            return data != null ? data.bakedSimplificationLevels : (backendId == APVProbeReferenceVolumeBackend.backendId ? bakedSimplificationLevels : -1);
        }
        internal float GetBakedMinDistanceBetweenProbes(string backendId)
        {
            var data = FindBakedData(backendId);
            return data != null ? data.bakedMinDistanceBetweenProbes : (backendId == APVProbeReferenceVolumeBackend.backendId ? bakedMinDistanceBetweenProbes : -1.0f);
        }
        internal Vector3 GetBakedProbeOffset(string backendId)
        {
            var data = FindBakedData(backendId);
            return data != null ? data.bakedProbeOffset : (backendId == APVProbeReferenceVolumeBackend.backendId ? bakedProbeOffset : Vector3.zero);
        }
        internal int GetMaxSubdivision(string backendId)
        {
            var levels = GetBakedSimplificationLevels(backendId);
            // Simplification level zero is a valid single-level layout and
            // still maps to one runtime subdivision. Negative means that this
            // backend has no baked layout yet.
            return levels >= 0 ? GetMaxSubdivision(levels) : 0;
        }
        internal float GetMinBrickSize(string backendId)
        {
            var distance = GetBakedMinDistanceBetweenProbes(backendId);
            return distance > 0.0f ? GetMinBrickSize(distance) : 0.0f;
        }

        [SerializeField] internal string lightingScenario = TProbeReferenceVolumeBase.defaultLightingScenario;
        string m_OtherScenario = null;
        float m_ScenarioBlendingFactor = 0.0f;

        // Secondary backends keep scenario selection independent from APV. The
        // legacy fields above remain serialized for existing APV assets.
        [SerializeField] SerializedDictionary<string, string> m_BackendLightingScenarios = new SerializedDictionary<string, string>();
        [NonSerialized] Dictionary<string, string> m_BackendOtherScenarios = new Dictionary<string, string>();
        [NonSerialized] Dictionary<string, float> m_BackendScenarioBlendingFactors = new Dictionary<string, float>();

        internal string otherScenario => m_OtherScenario;
        internal float scenarioBlendingFactor => m_ScenarioBlendingFactor;

        void EnsureBackendScenarioState()
        {
            m_BackendLightingScenarios ??= new SerializedDictionary<string, string>();
            m_BackendOtherScenarios ??= new Dictionary<string, string>();
            m_BackendScenarioBlendingFactors ??= new Dictionary<string, float>();
        }

        internal string GetLightingScenario(string backendId)
        {
            if (string.IsNullOrEmpty(backendId) || backendId == APVProbeReferenceVolumeBackend.backendId)
                return lightingScenario;
            EnsureBackendScenarioState();
            return m_BackendLightingScenarios.TryGetValue(backendId, out var scenario) && !string.IsNullOrEmpty(scenario)
                ? scenario : TProbeReferenceVolumeBase.defaultLightingScenario;
        }

        internal string GetOtherScenario(string backendId)
        {
            if (string.IsNullOrEmpty(backendId) || backendId == APVProbeReferenceVolumeBackend.backendId)
                return m_OtherScenario;
            EnsureBackendScenarioState();
            return m_BackendOtherScenarios.TryGetValue(backendId, out var scenario) ? scenario : null;
        }

        internal float GetScenarioBlendingFactor(string backendId)
        {
            if (string.IsNullOrEmpty(backendId) || backendId == APVProbeReferenceVolumeBackend.backendId)
                return m_ScenarioBlendingFactor;
            EnsureBackendScenarioState();
            return m_BackendScenarioBlendingFactors.TryGetValue(backendId, out var factor) ? factor : 0.0f;
        }

        ReadCommandArray m_ReadCommandArray = new ReadCommandArray();
        NativeArray<ReadCommand> m_ReadCommandBuffer = new NativeArray<ReadCommand>();
        Stack<NativeArray<byte>> m_ReadOperationScratchBuffers = new Stack<NativeArray<byte>>();
        List<int> m_PrunedIndexList = new List<int>();

        internal const int k_MaxSkyOcclusionBakingSamples = 8192;

        // Baking Profile
        [SerializeField]
        Version version = CoreUtils.GetLastEnumValue<Version>();

        [SerializeField]
        internal bool freezePlacement = false;

        /// <summary>
        /// Offset on world origin used during baking. Can be used to have cells on positions that are not multiples of the probe spacing.
        /// </summary>
        [SerializeField]
        public Vector3 probeOffset = Vector3.zero;

        /// <summary>
        /// How many levels contains the probes hierarchical structure.
        /// </summary>
        [Range(2, 5)]
        public int simplificationLevels = 3;

        /// <summary>
        /// The size of a Cell in number of bricks.
        /// </summary>
        public int cellSizeInBricks => GetCellSizeInBricks(bakedSimplificationLevels);

        /// <summary>
        /// The minimum distance between two probes in meters.
        /// </summary>
        [Min(0.1f)]
        public float minDistanceBetweenProbes = 1.0f;

        /// <summary>
        /// Maximum subdivision in the structure.
        /// </summary>
        public int maxSubdivision => GetMaxSubdivision(bakedSimplificationLevels);

        /// <summary>
        /// Minimum size of a brick in meters.
        /// </summary>
        public float minBrickSize => GetMinBrickSize(bakedMinDistanceBetweenProbes);

        /// <summary>
        /// Size of the cell in meters.
        /// </summary>
        public float cellSizeInMeters => (float)cellSizeInBricks * minBrickSize;

        internal ProbeVolumeLayout layout => new ProbeVolumeLayout(simplificationLevels, minDistanceBetweenProbes, probeOffset);

        /// <summary>
        /// Layer mask filter for all renderers.
        /// </summary>
        public LayerMask renderersLayerMask = -1;

        /// <summary>
        /// Specifies the minimum bounding box volume of renderers to consider placing probes around.
        /// </summary>
        [Min(0)]
        public float minRendererVolumeSize = 0.1f;

        /// <summary>
        /// Specifies whether the baking set will have sky handled dynamically.
        /// </summary>
        public bool skyOcclusion = false;

        /// <summary>
        /// Controls the number of samples per probe for dynamic sky baking.
        /// </summary>
        [Logarithmic(1, k_MaxSkyOcclusionBakingSamples)]
        public int skyOcclusionBakingSamples = 2048;

        /// <summary>
        /// Controls the number of bounces per light path for dynamic sky baking.
        /// </summary>
        [Range(0, 5)]
        public int skyOcclusionBakingBounces = 2;

        /// <summary>
        /// Average albedo for dynamic sky bounces
        /// </summary>
        [Range(0, 1)]
        public float skyOcclusionAverageAlbedo = 0.6f;

        /// <summary>
        /// Sky Occlusion backface culling
        /// </summary>
        public bool skyOcclusionBackFaceCulling = false;

        /// <summary>
        /// Bake sky shading direction.
        /// </summary>
        public bool skyOcclusionShadingDirection = false;

        [Serializable]
        internal struct ProbeLayerMask
        {
            public RenderingLayerMask mask;
            public string name;
        }

        [SerializeField]
        internal bool useRenderingLayers = false;
        [SerializeField]
        internal ProbeLayerMask[] renderingLayerMasks;

        internal uint4 ComputeRegionMasks()
        {
            uint4 masks = 0;
            if (!useRenderingLayers || renderingLayerMasks == null || renderingLayerMasks.Length == 0)
                masks.x = 0xFFFFFFFF;
            else
            {
                for (int i = 0; i < renderingLayerMasks.Length && i < APVDefinitions.probeMaxRegionCount; i++)
                    masks[i] = renderingLayerMasks[i].mask;

            }
            return masks;
        }

        internal static int GetCellSizeInBricks(int simplificationLevels) => ProbeVolumeLayout.CellSize(simplificationLevels);
        internal static int GetMaxSubdivision(int simplificationLevels) => ProbeVolumeLayout.GetMaxSubdivision(simplificationLevels);
        internal static float GetMinBrickSize(float minDistanceBetweenProbes) => ProbeVolumeLayout.GetMinBrickSize(minDistanceBetweenProbes);

        bool m_HasSupportData = false;
        bool m_SharedDataIsValid = false;
        bool m_UseStreamingAsset = true;
        internal bool useStreamingAsset => m_UseStreamingAsset;
        // Runtime validity is backend scoped. A baking set can be initialized
        // once for APV and once for APRT, so a single pair of cached flags
        // would let the second backend overwrite the first one's result.
        [NonSerialized] Dictionary<string, bool> m_BackendHasSupportData = new Dictionary<string, bool>();
        [NonSerialized] Dictionary<string, bool> m_BackendSharedDataIsValid = new Dictionary<string, bool>();

        private void OnValidate()
        {
            singleSceneMode &= m_SceneGUIDs.Count <= 1;

            if (m_LightingScenarios.Count == 0)
                m_LightingScenarios = new List<string>() { TProbeReferenceVolumeBase.defaultLightingScenario };

            settings.Upgrade();
        }

        void OnEnable()
        {
            EnsureBackendStatusCaches();
            m_HasSupportData = ComputeHasSupportData();
            m_SharedDataIsValid = ComputeHasValidSharedData();
            m_BackendHasSupportData[APVProbeReferenceVolumeBackend.backendId] = m_HasSupportData;
            m_BackendSharedDataIsValid[APVProbeReferenceVolumeBackend.backendId] = m_SharedDataIsValid;
        }

        void EnsureBackendStatusCaches()
        {
            m_BackendHasSupportData ??= new Dictionary<string, bool>();
            m_BackendSharedDataIsValid ??= new Dictionary<string, bool>();
        }

        // For functions below:
        // In editor users can delete asset at any moment, so we need to compute the result from scratch all the time.
        // In builds however, we want to avoid the expensive I/O operations.
        bool ComputeHasValidSharedData()
        {
            return bakedData?.HasValidSharedData() ?? false;
        }

        bool ComputeHasValidSharedData(string backendId)
        {
            if (string.IsNullOrEmpty(backendId))
                return ComputeHasValidSharedData();
            return FindBakedData(backendId)?.HasValidSharedData() ?? false;
        }

        internal bool HasValidSharedData()
        {
#if UNITY_EDITOR
            return ComputeHasValidSharedData();
#else
            return m_SharedDataIsValid;
#endif
        }

        internal bool HasValidSharedData(string backendId)
        {
            if (string.IsNullOrEmpty(backendId))
                return HasValidSharedData();
#if UNITY_EDITOR
            return ComputeHasValidSharedData(backendId);
#else
            EnsureBackendStatusCaches();
            return m_BackendSharedDataIsValid.TryGetValue(backendId, out var valid) ? valid : ComputeHasValidSharedData(backendId);
#endif
        }

        // Return true if baking settings are incompatible with already baked data
        internal bool CheckCompatibleCellLayout() => bakedData?.CheckCompatibleCellLayout(this) ?? false;

        bool ComputeHasSupportData()
        {
            return bakedData?.HasSupportData() ?? false;
        }

        bool ComputeHasSupportData(string backendId)
        {
            if (string.IsNullOrEmpty(backendId))
                return ComputeHasSupportData();
            return FindBakedData(backendId)?.HasSupportData() ?? false;
        }

        internal bool HasSupportData()
        {
#if UNITY_EDITOR
            return ComputeHasSupportData();
#else
            return m_HasSupportData;
#endif
        }

        internal bool HasSupportData(string backendId)
        {
            if (string.IsNullOrEmpty(backendId))
                return HasSupportData();
#if UNITY_EDITOR
            return ComputeHasSupportData(backendId);
#else
            EnsureBackendStatusCaches();
            return m_BackendHasSupportData.TryGetValue(backendId, out var hasData) ? hasData : ComputeHasSupportData(backendId);
#endif
        }

        IProbeVolumeBakingDataFormat GetBakingDataFormat(bool logError = true)
        {
            var format = ProbeReferenceVolumeProvider.activeBackend?.bakingDataFormat;
            if (format == null && logError)
                Debug.LogError($"No Probe Volume baking data format is available for baking set {name}.");
            return format;
        }

        IProbeVolumeBakingDataFormat GetBakingDataFormat(string backendId, bool logError = true)
        {
            IProbeVolumeBakingDataFormat format = null;
            if (!string.IsNullOrEmpty(backendId))
            {
                // A caller can install a temporary active context without
                // registering its backend globally. Only an exact id match may
                // supply that context's format; explicit other backends stay scoped.
                var active = ProbeReferenceVolumeProvider.activeBackend;
                if (active?.id == backendId)
                    format = active.bakingDataFormat;
                else if (ProbeReferenceVolumeProvider.TryGetBackend(backendId, out var backend))
                    format = backend.bakingDataFormat;
            }
            else
                format = GetBakingDataFormat(logError: false);
            if (format == null && logError)
                Debug.LogError($"No Probe Volume baking data format is available for baking set {name} (backend '{backendId}').");
            return format;
        }

        // Backend-scoped layout access.  The APV overloads below are retained for source and asset
        // compatibility; new backends must use these methods with their own stable backend id.
        void EnsureBackendLayouts()
        {
            // Unity does not serialize the legacy dictionaries. They are
            // normally initialized by the field initializers, but assets
            // created by older versions (and domain reloads in the editor)
            // can still leave them null.
            cellDataMap ??= new Dictionary<int, ICellData>();
            perSceneCellLists ??= new Dictionary<string, List<int>>();
            if (m_BackendCellLayouts == null)
                m_BackendCellLayouts = new List<BackendCellLayout>();
            if (m_BackendCellLayoutsById == null)
                m_BackendCellLayoutsById = new Dictionary<string, BackendCellLayout>();
            if (m_BackendCellDataMaps == null)
                m_BackendCellDataMaps = new Dictionary<string, Dictionary<int, ICellData>>();
            if (m_BackendPerSceneCellLists == null)
                m_BackendPerSceneCellLists = new Dictionary<string, Dictionary<string, List<int>>>();

            // APV data is stored in the legacy fields.  Register those fields as the APV scope without
            // copying them, so old editor/runtime code and the new API observe the same collection.
            m_BackendCellDataMaps[APVProbeReferenceVolumeBackend.backendId] = cellDataMap;

            foreach (var layout in m_BackendCellLayouts)
            {
                if (layout == null || string.IsNullOrEmpty(layout.backendId) || layout.backendId == APVProbeReferenceVolumeBackend.backendId)
                    continue;
                layout.cellDescs ??= new SerializedDictionary<int, CellDesc>();
                layout.perSceneCellLists ??= new List<SerializedPerSceneCellList>();
                m_BackendCellLayoutsById[layout.backendId] = layout;
                if (!m_BackendCellDataMaps.ContainsKey(layout.backendId))
                    m_BackendCellDataMaps.Add(layout.backendId, new Dictionary<int, ICellData>());
                if (!m_BackendPerSceneCellLists.ContainsKey(layout.backendId))
                {
                    var lists = new Dictionary<string, List<int>>();
                    foreach (var item in layout.perSceneCellLists)
                        lists[item.sceneGUID] = item.cellList ?? new List<int>();
                    m_BackendPerSceneCellLists.Add(layout.backendId, lists);
                }
            }
        }

        BackendCellLayout GetOrCreateBackendLayout(string backendId)
        {
            if (string.IsNullOrEmpty(backendId) || backendId == APVProbeReferenceVolumeBackend.backendId)
                return null;

            EnsureBackendLayouts();
            if (m_BackendCellLayoutsById.TryGetValue(backendId, out var existing))
                return existing;

            var layout = new BackendCellLayout { backendId = backendId };
            m_BackendCellLayouts.Add(layout);
            m_BackendCellLayoutsById.Add(backendId, layout);

            // GetCellDataMap/GetPerSceneCellLists can be called before the
            // serialized layout is first materialized (for example while a
            // streaming cell is being resolved). Preserve those runtime maps
            // instead of adding a second value for the same backend id.
            if (!m_BackendCellDataMaps.ContainsKey(backendId))
                m_BackendCellDataMaps.Add(backendId, new Dictionary<int, ICellData>());
            if (!m_BackendPerSceneCellLists.ContainsKey(backendId))
                m_BackendPerSceneCellLists.Add(backendId, new Dictionary<string, List<int>>());
            return layout;
        }

        internal Dictionary<int, ICellData> GetCellDataMap(string backendId)
        {
            EnsureBackendLayouts();
            if (string.IsNullOrEmpty(backendId) || backendId == APVProbeReferenceVolumeBackend.backendId)
                return cellDataMap;
            if (!m_BackendCellDataMaps.TryGetValue(backendId, out var map))
            {
                map = new Dictionary<int, ICellData>();
                m_BackendCellDataMaps.Add(backendId, map);
            }
            return map;
        }

        internal SerializedDictionary<int, CellDesc> GetCellDescs(string backendId)
        {
            if (string.IsNullOrEmpty(backendId) || backendId == APVProbeReferenceVolumeBackend.backendId)
                return cellDescs;
            return GetOrCreateBackendLayout(backendId).cellDescs;
        }

        internal Dictionary<string, List<int>> GetPerSceneCellLists(string backendId)
        {
            if (string.IsNullOrEmpty(backendId) || backendId == APVProbeReferenceVolumeBackend.backendId)
                return perSceneCellLists;

            var layout = GetOrCreateBackendLayout(backendId);
            EnsureBackendLayouts();
            return m_BackendPerSceneCellLists[backendId];
        }

        internal void SetPerSceneCellLists(string backendId, Dictionary<string, List<int>> lists)
        {
            if (string.IsNullOrEmpty(backendId) || backendId == APVProbeReferenceVolumeBackend.backendId)
            {
                perSceneCellLists = lists ?? new Dictionary<string, List<int>>();
                return;
            }

            var layout = GetOrCreateBackendLayout(backendId);
            EnsureBackendLayouts();
            m_BackendPerSceneCellLists[backendId] = lists ?? new Dictionary<string, List<int>>();
            layout.perSceneCellLists.Clear();
            if (lists == null)
                return;
            foreach (var item in lists)
                layout.perSceneCellLists.Add(new SerializedPerSceneCellList { sceneGUID = item.Key, cellList = item.Value });
        }

        /// <summary>
        /// Tests if the baking set data has already been baked.
        /// </summary>
        /// <param name="scenario">The name of the scenario to test. If null or if scenarios are disabled, the function will test for the default scenario.</param>
        /// <returns>True if the baking set data has been baked.</returns>
        public bool HasBakedData(string scenario = null)
        {
            return bakedData?.HasBakedData(scenario, ProbeReferenceVolumeProvider.instance.supportLightingScenarios) ?? false;
        }

        // Backend-scoped counterpart used by secondary volumes. The legacy
        // overload intentionally follows the provider's active context.
        internal bool HasBakedData(string scenario, TProbeReferenceVolumeBase refVolume)
        {
            if (refVolume == null)
                return HasBakedData(scenario);

            return FindBakedData(refVolume.backendId)?.HasBakedData(scenario, refVolume.supportLightingScenarios) ?? false;
        }

        /// <summary>Called after deserializing</summary>
        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (m_BackendBakedData == null)
                m_BackendBakedData = new List<ProbeVolumeBakedData>();

            // Rebuild backend scoped runtime maps before any backend data resolves cells.
            m_BackendCellLayoutsById = new Dictionary<string, BackendCellLayout>();
            m_BackendCellDataMaps = new Dictionary<string, Dictionary<int, ICellData>>();
            m_BackendPerSceneCellLists = new Dictionary<string, Dictionary<string, List<int>>>();
            m_BackendHasSupportData = new Dictionary<string, bool>();
            m_BackendSharedDataIsValid = new Dictionary<string, bool>();
            EnsureBackendLayouts();

            foreach (var data in m_BackendBakedData)
                data?.OnAfterDeserialize(this);

            if (!m_LightingScenarios.Contains(lightingScenario))
            {
                if (m_LightingScenarios.Count != 0)
                    lightingScenario = m_LightingScenarios[0];
                else
                    lightingScenario = TProbeReferenceVolumeBase.defaultLightingScenario;
            }

            perSceneCellLists.Clear();
            if (m_SerializedPerSceneCellList != null)
            foreach(var scene in m_SerializedPerSceneCellList)
            {
                perSceneCellLists.Add(scene.sceneGUID, scene.cellList);
            }

            // Legacy APV assets have no backend layout record.  Keep the old fields as the canonical APV
            // scope; records for other backends are loaded by EnsureBackendLayouts above.
            m_BackendCellDataMaps[APVProbeReferenceVolumeBackend.backendId] = cellDataMap;

            if (m_OtherScenario == "")
                m_OtherScenario = null;

            // Hack T_T
            // Added the new bricksCount in Disk Streaming PR to have everything ready in the serialized desc but old data does not have it so we need to recompute it...
            // Might as well not serialize it but it's bad to have non-serialized data in the serialized desc.
            if (cellDescs.Count != 0)
            {
                // Check first cell to see if we need to recompute for all cells.
                var enumerator = cellDescs.Values.GetEnumerator();
                enumerator.MoveNext();
                var cellDesc = enumerator.Current;
                if (cellDesc.bricksCount == 0)
                {
                    foreach(var value in cellDescs.Values)
                        value.bricksCount = value.probeCount / ProbeBrickPoolBase.brickProbeCountTotal;
                }
            }
        }

        /// <summary>Called before serializing</summary>
        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            m_SerializedPerSceneCellList = new List<SerializedPerSceneCellList>();
            foreach (var kvp in perSceneCellLists)
            {
                m_SerializedPerSceneCellList.Add(new SerializedPerSceneCellList { sceneGUID = kvp.Key, cellList = kvp.Value });
            }

            EnsureBackendLayouts();
            foreach (var pair in m_BackendPerSceneCellLists)
            {
                if (!m_BackendCellLayoutsById.TryGetValue(pair.Key, out var layout))
                    continue;
                layout.perSceneCellLists.Clear();
                foreach (var item in pair.Value)
                    layout.perSceneCellLists.Add(new SerializedPerSceneCellList { sceneGUID = item.Key, cellList = item.Value });
            }
        }

        internal void Initialize(bool useStreamingAsset)
        {
            // Preserve the legacy overload's semantics: callers that do not
            // provide a volume operate on the provider's active backend. This
            // is used by baking-data format tests and editor utilities that
            // install a temporary backend context.
            Initialize(useStreamingAsset, ProbeReferenceVolumeProvider.instance, ProbeReferenceVolumeProvider.activeBackend?.id);
        }

        internal void Initialize(bool useStreamingAsset, TProbeReferenceVolumeBase refVolume)
        {
            // An explicit volume owns this initialization. Do not use the
            // provider's compatibility context here: secondary backends are
            // initialized while the provider context may still point at the
            // primary APV backend, and selecting the active format would make
            // both backends resolve the same cell data.
            Initialize(useStreamingAsset, refVolume, refVolume?.backendId);
        }

        void Initialize(bool useStreamingAsset, TProbeReferenceVolumeBase refVolume, string scopedBackendId)
        {
            if (string.IsNullOrEmpty(scopedBackendId))
                scopedBackendId = ProbeReferenceVolumeProvider.activeBackend?.id;

            var format = GetBakingDataFormat(scopedBackendId, logError: false);
            var data = format != null ? GetOrCreateBakedData(format) : null;
            data?.Initialize(refVolume);
            var hasSupportData = ComputeHasSupportData(scopedBackendId);
            var sharedDataIsValid = ComputeHasValidSharedData(scopedBackendId);
            EnsureBackendStatusCaches();
            if (!string.IsNullOrEmpty(scopedBackendId))
            {
                m_BackendHasSupportData[scopedBackendId] = hasSupportData;
                m_BackendSharedDataIsValid[scopedBackendId] = sharedDataIsValid;
                if (scopedBackendId == APVProbeReferenceVolumeBackend.backendId)
                {
                    m_HasSupportData = hasSupportData;
                    m_SharedDataIsValid = sharedDataIsValid;
                }
            }
            m_UseStreamingAsset = useStreamingAsset;

            if (!useStreamingAsset)
            {
                m_TotalIndexList.Clear();
                var scopedDescs = GetCellDescs(scopedBackendId);
                foreach (var index in scopedDescs.Keys)
                    m_TotalIndexList.Add(index);

                ResolveAllCellData(scopedBackendId, refVolume);
            }

            // Reset blending.
            if (refVolume != null && refVolume.supportScenarioBlending)
                BlendLightingScenario(null, 0.0f, refVolume);
        }

        internal void Cleanup()
        {
            if (m_BackendBakedData != null)
            {
                foreach (var data in m_BackendBakedData)
                    data?.CleanupStreamableAssets();
            }

            CleanupReadScratch();
        }

        void CleanupReadScratch()
        {
            if (m_ReadCommandBuffer.IsCreated)
                m_ReadCommandBuffer.Dispose();

            foreach(var buffer in m_ReadOperationScratchBuffers)
                buffer.Dispose();

            m_ReadOperationScratchBuffers.Clear();
        }

        internal void Cleanup(string backendId)
        {
            if (string.IsNullOrEmpty(backendId))
            {
                Cleanup();
                return;
            }

            FindBakedData(backendId)?.CleanupStreamableAssets();
            var map = GetCellDataMap(backendId);
            foreach (var data in map.Values)
                data?.Cleanup(true);
            map.Clear();

            // These buffers are synchronous read scratch shared by the decoders.
            // Retain them while another backend still owns resolved cells.
            if (cellDataMap.Count != 0)
                return;
            foreach (var otherMap in m_BackendCellDataMaps.Values)
                if (otherMap.Count != 0)
                    return;
            CleanupReadScratch();
        }

        internal void SetActiveScenario(string scenario, bool verbose = true)
        {
            SetActiveScenario(scenario, verbose, ProbeReferenceVolumeProvider.instance);
        }

        internal void SetActiveScenario(string scenario, bool verbose, TProbeReferenceVolumeBase refVolume)
        {
            refVolume ??= ProbeReferenceVolumeProvider.instance;

            string backendId = refVolume?.backendId;
            bool scoped = !string.IsNullOrEmpty(backendId) && backendId != APVProbeReferenceVolumeBackend.backendId;
            string currentScenario = GetLightingScenario(backendId);

            if (currentScenario == scenario)
                return;

            if (!m_LightingScenarios.Contains(scenario))
            {
                if (verbose)
                    Debug.LogError($"Scenario '{scenario}' does not exist.");
                return;
            }

            if (!HasBakedData(scenario, refVolume))
            {
                // We don't return here as it's still valid to enable a scenario that wasn't baked in the editor.
                if (verbose)
                    Debug.LogError($"Scenario '{scenario}' has not been baked.");
            }

            if (scoped)
            {
                EnsureBackendScenarioState();
                m_BackendLightingScenarios[backendId] = scenario;
                m_BackendOtherScenarios[backendId] = null;
                m_BackendScenarioBlendingFactors[backendId] = 0.0f;
            }
            else
            {
                lightingScenario = scenario;
                m_ScenarioBlendingFactor = 0.0f;
            }

            if (refVolume.supportScenarioBlending)
            {
                // Trigger blending system to replace old cells with the one from the new active scenario.
                // Although we technically don't need blending for that, it is better than unloading all cells
                // because it will replace them progressively. There is no real performance cost to using blending
                // rather than regular load thanks to the bypassBlending branch in AddBlendingBricks.
                refVolume.ScenarioBlendingChanged(true);
            }
            else
                refVolume.UnloadAllCells();

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        internal void BlendLightingScenario(string otherScenario, float blendingFactor)
        {
            BlendLightingScenario(otherScenario, blendingFactor, ProbeReferenceVolumeProvider.instance);
        }

        internal void BlendLightingScenario(string otherScenario, float blendingFactor, TProbeReferenceVolumeBase refVolume)
        {
            if (refVolume == null)
                return;

            string backendId = refVolume.backendId;
            bool scoped = !string.IsNullOrEmpty(backendId) && backendId != APVProbeReferenceVolumeBackend.backendId;
            string currentScenario = GetLightingScenario(backendId);
            string currentOtherScenario = GetOtherScenario(backendId);
            float currentFactor = GetScenarioBlendingFactor(backendId);

            if (!string.IsNullOrEmpty(otherScenario) && !refVolume.supportScenarioBlending)
            {
                return;
            }

            // null scenario is valid in order to reset blending.
            if (otherScenario != null && !m_LightingScenarios.Contains(otherScenario))
            {
                Debug.LogError($"Scenario '{otherScenario}' does not exist.");
                return;
            }

            if (otherScenario != null && !HasBakedData(otherScenario, refVolume))
            {
                Debug.LogError($"Scenario '{otherScenario}' has not been baked.");
                return;
            }

            blendingFactor = Mathf.Clamp01(blendingFactor);

            if (otherScenario == currentScenario || string.IsNullOrEmpty(otherScenario))
                otherScenario = null;
            if (otherScenario == null)
                blendingFactor = 0.0f;
            if (otherScenario == currentOtherScenario && Mathf.Approximately(blendingFactor, currentFactor))
                return;

            bool scenarioChanged = otherScenario != currentOtherScenario;
            if (scoped)
            {
                EnsureBackendScenarioState();
                m_BackendOtherScenarios[backendId] = otherScenario;
                m_BackendScenarioBlendingFactors[backendId] = blendingFactor;
            }
            else
            {
                m_OtherScenario = otherScenario;
                m_ScenarioBlendingFactor = blendingFactor;
            }

            refVolume.ScenarioBlendingChanged(scenarioChanged);
        }

        internal int GetBakingHashCode()
        {
            int hash = maxCellPosition.GetHashCode();
            hash = hash * 23 + minCellPosition.GetHashCode();
            hash = hash * 23 + globalBounds.GetHashCode();
            hash = hash * 23 + cellSizeInBricks.GetHashCode();
            hash = hash * 23 + simplificationLevels.GetHashCode();
            hash = hash * 23 + minDistanceBetweenProbes.GetHashCode();

            return hash;
        }


        static int AlignUp16(int count)
        {
            var alignment = 16;
            var remainder = count % alignment;
            return count + (remainder == 0 ? 0 : alignment - remainder);
        }

        NativeArray<T> GetSubArray<T>(NativeArray<byte> input, int count, ref int offset) where T : struct
        {
            var size = count * UnsafeUtility.SizeOf<T>();
            if (offset + size > input.Length)
                return default;

            var result = input.GetSubArray(offset, size).Reinterpret<T>(1);
            offset = AlignUp16(offset + size);
            return result;
        }

        NativeArray<byte> RequestScratchBuffer(int size)
        {
            if (m_ReadOperationScratchBuffers.Count == 0)
            {
                return new NativeArray<byte>(size, Allocator.Persistent);
            }
            else
            {
                var buffer = m_ReadOperationScratchBuffers.Pop();
                Debug.Assert(buffer.IsCreated);
                if (buffer.Length < size)
                {
                    buffer.Dispose();
                    return new NativeArray<byte>(size, Allocator.Persistent);
                }
                else
                {
                    return buffer;
                }
            }
        }

        private unsafe bool FileExists(string path)
        {
            // Can't use System.IO.File.Exists as it doesn't work with compressed streaming assets folder (iOS, Android)
            FileInfoResult infoResult;
            ReadHandle h = AsyncReadManager.GetFileInfo(path, &infoResult);
            h.JobHandle.Complete();
            bool exists = infoResult.FileState == FileState.Exists;
            h.Dispose();
            return exists;
        }

        NativeArray<T> CopyAssetReferenceData<T>(ProbeVolumeStreamableAsset asset, List<int> cellIndices) where T : struct
        {
            var source = asset.asset.GetData<byte>();
            int totalSize = 0;
            foreach (var cellIndex in cellIndices)
                totalSize += asset.streamableCellDescs[cellIndex].elementCount * asset.elementSize;

            var scratch = RequestScratchBuffer(totalSize);
            int destinationOffset = 0;
            foreach (var cellIndex in cellIndices)
            {
                var cell = asset.streamableCellDescs[cellIndex];
                int size = cell.elementCount * asset.elementSize;
                if (cell.offset < 0 || size < 0 || cell.offset > source.Length - size || destinationOffset > scratch.Length - size)
                {
                    ReleaseStreamableAssetData(scratch);
                    return default;
                }

                NativeArray<byte>.Copy(source, cell.offset, scratch, destinationOffset, size);
                destinationOffset += size;
            }

            return scratch.Reinterpret<T>(1);
        }

        // Load from disk all data related to the required cells only.
        // This allows us to avoid loading the whole file in memory which could be a huge spike for multi scene setups.
        internal unsafe NativeArray<T> LoadStreamableAssetData<T>(ProbeVolumeStreamableAsset asset, List<int> cellIndices) where T : struct
        {
            if (asset == null)
                return default;

            if (!m_UseStreamingAsset)
            {
                Debug.Assert(asset.asset != null);
                return asset.asset.GetData<byte>().Reinterpret<T>(1);
            }
            else
            {
                if (!FileExists(asset.GetAssetPath()))
                {
                    asset.RefreshAssetPath();
                    if (!FileExists(asset.GetAssetPath()))
                    {
                        // If we can't load from StreamingAssets, but we have a valid asset reference, try to use the asset reference instead.
                        if (asset.HasValidAssetReference())
                        {
                            return CopyAssetReferenceData<T>(asset, cellIndices);
                        }
                        else
                        {
                            Debug.LogAssertion($"File {asset.GetAssetPath()} does not exist on disk. If you are trying to load Adaptive Probe Volumes from AssetBundles or Addressables, " +
                                               "tick the \"Probe Volume Disable Streaming Assets\" project setting in the Graphics tab when building the Player.");
                            return default;
                        }
                    }
                }

                // Prepare read commands.
                // Reallocate read commands buffer if needed.
                if (!m_ReadCommandBuffer.IsCreated || m_ReadCommandBuffer.Length < cellIndices.Count)
                {
                    if (m_ReadCommandBuffer.IsCreated)
                        m_ReadCommandBuffer.Dispose();
                    m_ReadCommandBuffer = new NativeArray<ReadCommand>(cellIndices.Count, Allocator.Persistent);
                }

                // Compute total size and fill read command offsets/sizes
                int totalSize = 0;
                int commandIndex = 0;
                foreach (int cellIndex in cellIndices)
                {
                    var streamableCellDesc = asset.streamableCellDescs[cellIndex];
                    ReadCommand command = new ReadCommand();
                    command.Offset = streamableCellDesc.offset;
                    command.Size = streamableCellDesc.elementCount * asset.elementSize;
                    command.Buffer = null;
                    m_ReadCommandBuffer[commandIndex++] = command;

                    totalSize += (int)command.Size;
                }

                var scratchBuffer = RequestScratchBuffer(totalSize);

                // Update output buffer pointers
                commandIndex = 0;
                long outputOffset = 0;
                byte* scratchPtr = (byte*)scratchBuffer.GetUnsafePtr();
                foreach (int cellIndex in cellIndices)
                {
                    // Stupid C# and no ref returns by default...
                    var command = m_ReadCommandBuffer[commandIndex];
                    command.Buffer = scratchPtr + outputOffset;
                    outputOffset += command.Size;
                    m_ReadCommandBuffer[commandIndex++] = command;
                }

                m_ReadCommandArray.CommandCount = cellIndices.Count;
                m_ReadCommandArray.ReadCommands = (ReadCommand*)m_ReadCommandBuffer.GetUnsafePtr();

                // We don't need async read here but only partial read to avoid loading up the whole file for ever.
                // So we just wait for the result.
                var readHandle = AsyncReadManager.Read(asset.OpenFile(), m_ReadCommandArray);
                readHandle.JobHandle.Complete();
                bool completed = readHandle.Status == ReadStatus.Complete;
                Debug.Assert(completed);
                asset.CloseFile();
                readHandle.Dispose();
                if (!completed)
                {
                    ReleaseStreamableAssetData(scratchBuffer);
                    return default;
                }

                return scratchBuffer.Reinterpret<T>(1);
            }
        }

        internal void ReleaseStreamableAssetData<T>(NativeArray<T> buffer) where T : struct
        {
            if (m_UseStreamingAsset && buffer.IsCreated)
                m_ReadOperationScratchBuffers.Push(buffer.Reinterpret<byte>(UnsafeUtility.SizeOf<T>()));
        }

        void PruneCellIndexList(List<int> cellIndices, List<int> prunedIndexList)
        {
            PruneCellIndexList(APVProbeReferenceVolumeBackend.backendId, cellIndices, prunedIndexList);
        }

        internal void PruneCellIndexList(string backendId, List<int> cellIndices, List<int> prunedIndexList)
        {
            prunedIndexList.Clear();
            var dataMap = GetCellDataMap(backendId);
            foreach (var cellIndex in cellIndices)
            {
                // When clearing data only partially (ie: not all scenes are loaded), there can be left over indices here but no cells in the set.
                if (!dataMap.ContainsKey(cellIndex))
                {
                    prunedIndexList.Add(cellIndex);
                }
            }
        }

        internal List<int> GetSceneCellIndexList(string sceneGUID)
        {
            return GetSceneCellIndexList(APVProbeReferenceVolumeBackend.backendId, sceneGUID);
        }

        internal List<int> GetSceneCellIndexList(string backendId, string sceneGUID)
        {
            var sceneLists = GetPerSceneCellLists(backendId);
            if (sceneLists.TryGetValue(sceneGUID, out var indexList))
                return indexList;
            else
                return null;
        }

        bool ResolveAllCellData()
        {
            return ResolveAllCellData(APVProbeReferenceVolumeBackend.backendId, ProbeReferenceVolumeProvider.instance);
        }

        bool ResolveAllCellData(string backendId, TProbeReferenceVolumeBase referenceVolume)
        {
            Debug.Assert(!m_UseStreamingAsset);
            Debug.Assert(m_TotalIndexList.Count != 0);

            if (ResolveSharedCellData(backendId, referenceVolume, m_TotalIndexList))
                return ResolvePerScenarioCellData(backendId, referenceVolume, m_TotalIndexList);
            else
                return false;
        }

        internal bool ResolveCellData(List<int> cellIndices)
        {
            return ResolveCellData(APVProbeReferenceVolumeBackend.backendId, ProbeReferenceVolumeProvider.instance, cellIndices);
        }

        internal bool ResolveCellData(string backendId, TProbeReferenceVolumeBase referenceVolume, List<int> cellIndices)
        {
            // All cells should already be resolved in CPU memory.
            if (!m_UseStreamingAsset)
                return true;

            if (cellIndices == null)
                return false;

            // Prune index list (some cells might already be resolved from another scene).
            PruneCellIndexList(backendId, cellIndices, m_PrunedIndexList);

            // When disk streaming is enabled, we should never resolve Cell data in CPU memory.
            // The streaming system will upload them directly to the GPU.
            if (referenceVolume != null && referenceVolume.diskStreamingEnabled)
            {
                var format = GetBakingDataFormat(backendId);
                if (format == null)
                    return false;

                var dataMap = GetCellDataMap(backendId);

                // Prepare data structures.
                // GPU data will stay empty but CPU data (bricks, support) will be streamed here.
                foreach (var cell in m_PrunedIndexList)
                {
                    Debug.Assert(!dataMap.ContainsKey(cell));
                    // The backend retains the metadata needed for streaming its payload.
                    dataMap.Add(cell, format.CreateCellData(this, cell, streamingOnly: true));
                }

                return true;
            }
            else
            {
                if (ResolveSharedCellData(backendId, referenceVolume, m_PrunedIndexList))
                {
                    return ResolvePerScenarioCellData(backendId, referenceVolume, m_PrunedIndexList);
                }
            }

            return false;
        }

        internal bool ResolveSharedCellData(List<int> cellIndices)
        {
            Debug.Assert(!ProbeReferenceVolumeProvider.instance.diskStreamingEnabled);
            var format = GetBakingDataFormat();
            return format != null && format.ResolveSharedCellData(new ProbeVolumeBakingDataResolveContext(this, ProbeReferenceVolumeProvider.instance, cellIndices, m_UseStreamingAsset));
        }

        internal bool ResolveSharedCellData(string backendId, TProbeReferenceVolumeBase referenceVolume, List<int> cellIndices)
        {
            var format = GetBakingDataFormat(backendId);
            return format != null && format.ResolveSharedCellData(new ProbeVolumeBakingDataResolveContext(this, referenceVolume, cellIndices, m_UseStreamingAsset, backendId));
        }

        internal bool ResolvePerScenarioCellData(List<int> cellIndices)
        {
            Debug.Assert(!ProbeReferenceVolumeProvider.instance.diskStreamingEnabled);
            var format = GetBakingDataFormat();
            return format != null && format.ResolveScenarioCellData(new ProbeVolumeBakingDataResolveContext(this, ProbeReferenceVolumeProvider.instance, cellIndices, m_UseStreamingAsset));
        }

        internal bool ResolvePerScenarioCellData(string backendId, TProbeReferenceVolumeBase referenceVolume, List<int> cellIndices)
        {
            var format = GetBakingDataFormat(backendId);
            return format != null && format.ResolveScenarioCellData(new ProbeVolumeBakingDataResolveContext(this, referenceVolume, cellIndices, m_UseStreamingAsset, backendId));
        }

        internal void ReleaseCell(int cellIndex)
        {
            ReleaseCell(APVProbeReferenceVolumeBackend.backendId, cellIndex);
        }

        internal void ReleaseCell(string backendId, int cellIndex)
        {
            var cellMap = GetCellDataMap(backendId);
            var cellData = cellMap[cellIndex];
            cellData.Cleanup(true);
            cellMap.Remove(cellIndex);
        }

        internal CellDesc GetCellDesc(int cellIndex)
        {
            return GetCellDesc(APVProbeReferenceVolumeBackend.backendId, cellIndex);
        }

        internal CellDesc GetCellDesc(string backendId, int cellIndex)
        {
            if (GetCellDescs(backendId).TryGetValue(cellIndex, out var cellDesc))
                return cellDesc;
            else
                return null;
        }

        internal T GetCellData<T>(int cellIndex) where T : class, ICellData
        {
            return GetCellData<T>(APVProbeReferenceVolumeBackend.backendId, cellIndex);
        }

        internal T GetCellData<T>(string backendId, int cellIndex) where T : class, ICellData
        {
            if (GetCellDataMap(backendId).TryGetValue(cellIndex, out var cellData) && cellData is T typed)
                return typed;
                
            return null;
        }


        internal int GetChunkGPUMemory(TProbeReferenceVolumeBase referenceVolume)
        {
            var format = GetBakingDataFormat(referenceVolume?.backendId);
            return format?.GetChunkGPUMemory(this, referenceVolume) ?? 0;
        }

        internal int GetChunkGPUMemory<T>(T cType) where T : Enum
        {
            int size = 0;

            return size;
        }

        internal bool HasSameSceneGUIDs(ProbeVolumeBakingSet other)
        {
            var otherSceneGUIDs = other.sceneGUIDs;
            if (m_SceneGUIDs.Count != otherSceneGUIDs.Count)
                return false;

            for (var i = 0; i < m_SceneGUIDs.Count; ++i)
            {
                if (m_SceneGUIDs[i] != otherSceneGUIDs[i])
                    return false;
            }
            return true;
        }
    }
}
