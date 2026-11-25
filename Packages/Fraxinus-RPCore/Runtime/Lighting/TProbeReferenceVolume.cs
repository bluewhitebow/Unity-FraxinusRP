using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using Brick = UnityEngine.Rendering.ProbeBrickIndex.Brick;
using Chunk = UnityEngine.Rendering.ProbeBrickPoolBase.BrickChunkAlloc;

namespace UnityEngine.Rendering
{
    internal static class SceneExtensions
    {
        static PropertyInfo s_SceneGUID = typeof(Scene).GetProperty("guid", BindingFlags.NonPublic | BindingFlags.Instance);
        public static string GetGUID(this Scene scene)
        {
            Debug.Assert(s_SceneGUID != null, "Reflection for scene GUID failed");
            return (string)s_SceneGUID.GetValue(scene);
        }
    }

    /// <summary>
    /// Possible values for the probe volume memory budget (determines the size of the textures used).
    /// </summary>
    [Serializable]
    public enum ProbeVolumeTextureMemoryBudget
    {
        /// <summary>Low Budget</summary>
        MemoryBudgetLow = 512,
        /// <summary>Medium Budget</summary>
        MemoryBudgetMedium = 1024,
        /// <summary>High Budget</summary>
        MemoryBudgetHigh = 2048,
    }

    /// <summary>
    /// Possible values for the probe volume scenario blending memory budget (determines the size of the textures used).
    /// </summary>
    [Serializable]
    public enum ProbeVolumeBlendingTextureMemoryBudget
    {
        /// <summary>Disable scenario blending. Kept for Unity 2022 compatibility.</summary>
        None = 0,
        /// <summary>Low Budget</summary>
        MemoryBudgetLow = 128,
        /// <summary>Medium Budget</summary>
        MemoryBudgetMedium = 256,
        /// <summary>High Budget</summary>
        MemoryBudgetHigh = 512,
    }

    public struct ProbeVolumeShadingParameters
    {
        public float normalBias;
        public float viewBias;
        public bool scaleBiasByMinDistanceBetweenProbes;
        public float samplingNoise;
        public float weight;
        public APVLeakReductionMode leakReductionMode;
        /// <summary>Legacy contribution of leak-reduction occlusion weights.</summary>
        [Obsolete("This field is kept for Unity 2022 source compatibility and is not used by the Unity 6 APV shader layout.")]
        public float occlusionWeightContribution;
        /// <summary>Legacy minimum valid normal weight.</summary>
        [Obsolete("This field is kept for Unity 2022 source compatibility and is not used by the Unity 6 APV shader layout.")]
        public float minValidNormalWeight;
        public int frameIndexForNoise;
        public float reflNormalizationLowerClamp;
        public float reflNormalizationUpperClamp;
        public float skyOcclusionIntensity;
        public bool skyOcclusionShadingDirection;
        public int regionCount;
        public uint4 regionLayerMasks;
        public Vector3 worldOffset;
    }

    /// <summary>
    /// Initialization parameters for the probe volume system.
    /// </summary>
    public struct ProbeVolumeSystemParameters
    {
        /// <summary>
        /// Enables the APV backend. When omitted by older callers, APV remains
        /// the default unless APRT is explicitly enabled.
        /// </summary>
        public bool enableAPV;
        /// <summary>
        /// The memory budget determining the size of the textures containing SH data.
        /// </summary>
        public ProbeVolumeTextureMemoryBudget memoryBudget;
        /// <summary>
        /// The memory budget determining the size of the textures used for blending between scenarios.
        /// </summary>
        public ProbeVolumeBlendingTextureMemoryBudget blendingMemoryBudget;
        /// <summary>
        /// The <see cref="ProbeVolumeSHBands"/>
        /// </summary>
        public ProbeVolumeSHBands shBands;
        /// <summary>True if APV should support lighting scenarios.</summary>
        public bool supportScenarios;
        /// <summary>True if APV should support lighting scenario blending.</summary>
        public bool supportScenarioBlending;
        /// <summary>True if APV should support streaming of cell data to the GPU.</summary>
        public bool supportGPUStreaming;
        /// <summary>True if APV should support streaming of cell data from the disk.</summary>
        public bool supportDiskStreaming;
        /// <summary>Enables the optional Adaptive Precompute Radiance Transfer backend.</summary>
        public bool enableAPRT;
    }

    internal abstract partial class TProbeReferenceVolumeBase
    {
        internal struct Volume : IEquatable<Volume>
        {
            internal Vector3 corner;
            internal Vector3 X;   // the vectors are NOT normalized, their length determines the size of the box
            internal Vector3 Y;
            internal Vector3 Z;

            internal float maxSubdivisionMultiplier;
            internal float minSubdivisionMultiplier;

            public Volume(Matrix4x4 trs, float maxSubdivision, float minSubdivision)
            {
                X = trs.GetColumn(0);
                Y = trs.GetColumn(1);
                Z = trs.GetColumn(2);
                corner = (Vector3)trs.GetColumn(3) - X * 0.5f - Y * 0.5f - Z * 0.5f;
                this.maxSubdivisionMultiplier = maxSubdivision;
                this.minSubdivisionMultiplier = minSubdivision;
            }

            public Volume(Vector3 corner, Vector3 X, Vector3 Y, Vector3 Z, float maxSubdivision = 1, float minSubdivision = 0)
            {
                this.corner = corner;
                this.X = X;
                this.Y = Y;
                this.Z = Z;
                this.maxSubdivisionMultiplier = maxSubdivision;
                this.minSubdivisionMultiplier = minSubdivision;
            }

            public Volume(Volume copy)
            {
                X = copy.X;
                Y = copy.Y;
                Z = copy.Z;
                corner = copy.corner;
                maxSubdivisionMultiplier = copy.maxSubdivisionMultiplier;
                minSubdivisionMultiplier = copy.minSubdivisionMultiplier;
            }

            public Volume(Bounds bounds)
            {
                var size = bounds.size;
                corner = bounds.center - size * 0.5f;
                X = new Vector3(size.x, 0, 0);
                Y = new Vector3(0, size.y, 0);
                Z = new Vector3(0, 0, size.z);

                maxSubdivisionMultiplier = minSubdivisionMultiplier = 0;
            }

            public Bounds CalculateAABB()
            {
                Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                for (int x = 0; x < 2; x++)
                {
                    for (int y = 0; y < 2; y++)
                    {
                        for (int z = 0; z < 2; z++)
                        {
                            Vector3 dir = new Vector3(x, y, z);

                            Vector3 pt = corner
                                + X * dir.x
                                + Y * dir.y
                                + Z * dir.z;

                            min = Vector3.Min(min, pt);
                            max = Vector3.Max(max, pt);
                        }
                    }
                }

                return new Bounds((min + max) / 2, max - min);
            }

            public void CalculateCenterAndSize(out Vector3 center, out Vector3 size)
            {
                size = new Vector3(X.magnitude, Y.magnitude, Z.magnitude);
                center = corner + X * 0.5f + Y * 0.5f + Z * 0.5f;
            }

            public void Transform(Matrix4x4 trs)
            {
                corner = trs.MultiplyPoint(corner);
                X = trs.MultiplyVector(X);
                Y = trs.MultiplyVector(Y);
                Z = trs.MultiplyVector(Z);
            }

            public override string ToString()
            {
                return $"Corner: {corner}, X: {X}, Y: {Y}, Z: {Z}, MaxSubdiv: {maxSubdivisionMultiplier}";
            }

            public bool Equals(Volume other)
            {
                return corner == other.corner
                    && X == other.X
                    && Y == other.Y
                    && Z == other.Z
                    && minSubdivisionMultiplier == other.minSubdivisionMultiplier
                    && maxSubdivisionMultiplier == other.maxSubdivisionMultiplier;
            }
        }

        /// <summary>
        ///  The input to the retrieveExtraDataAction action.
        /// </summary>
        public struct ExtraDataActionInput
        {
            // Empty, but defined to make this future proof without having to change public API
        }

        /// <summary>
        ///  An action that is used by the SRP to retrieve extra data that was baked together with the bake
        /// </summary>
        public Action<ExtraDataActionInput> retrieveExtraDataAction;

        /// <summary>
        /// An action that is used by the SRP to perform checks every frame during baking.
        /// </summary>
        public Action checksDuringBakeAction;

        internal static string defaultLightingScenario = "Default";

        static internal string GetSceneGUID(Scene scene) => scene.GetGUID();


        // Shared baking and scene state required by every probe-volume backend.
        public abstract bool isInitialized { get; }
        internal abstract string backendId { get; }
        internal abstract bool enabledBySRP { get; }
        internal abstract List<ProbeVolumePerSceneData> perSceneDataList { get; }
        internal abstract bool clearAssetsOnVolumeClear { get; set; }
        internal abstract Bounds globalBounds { get; set; }
#if UNITY_EDITOR
        internal abstract bool EnsureCurrentBakingSet(ProbeVolumeBakingSet bakingSet);
#endif
        internal abstract void Clear();
        // todo: 这些肯定不能留在这里，做完APV后挪出去
        public abstract ProbeVolumeSHBands shBands { get; }
        public abstract bool probeOcclusion { get; }
        internal abstract bool skyOcclusion { get; }
        internal abstract bool skyOcclusionShadingDirection { get; }

        public abstract ProbeVolumeBakingSet currentBakingSet { get; }
        public abstract string lightingScenario { get; set; }
        public abstract string otherScenario { get; }
        public abstract float scenarioBlendingFactor { get; set; }
        public abstract ProbeVolumeTextureMemoryBudget memoryBudget { get; }

        public abstract void SetEnableStateFromSRP(bool srpEnablesPV);
        public abstract void SetVertexSamplingEnabled(bool value);
        public abstract void BlendLightingScenario(string otherScenario, float blendingFactor);
        public abstract void SetActiveScene(Scene scene);
        public abstract void SetActiveBakingSet(ProbeVolumeBakingSet bakingSet);
        public abstract int GetVideoMemoryCost();
        public abstract void PerformPendingOperations();
        public abstract bool DataHasBeenLoaded();
        internal abstract float BrickSize(int subdivisionLevel);
        internal abstract float MinBrickSize();
        internal abstract float MaxBrickSize();
        internal abstract Vector3 ProbeOffset();
        internal abstract int GetMaxSubdivision();
        internal abstract int GetEntrySubdivLevel();
        internal abstract float MinDistanceBetweenProbes();
        internal abstract void SetSubdivisionDimensions(float minBrickSize, int maxSubdiv, Vector3 offset);
        internal abstract void RegisterPerSceneData(ProbeVolumePerSceneData data);
        internal abstract void UnregisterPerSceneData(ProbeVolumePerSceneData data);
        internal abstract void RegisterBakingSet(ProbeVolumePerSceneData data);
        internal abstract bool TryGetPerSceneData(string sceneGUID, out ProbeVolumePerSceneData perSceneData);
        /// <summary>Current shared probe/brick layout.</summary>
        internal ProbeVolumeLayout layout => new ProbeVolumeLayout(GetMaxSubdivision() - 1, MinDistanceBetweenProbes(), ProbeOffset());

        internal static int CellSize(int subdivisionLevel) => ProbeVolumeLayout.CellSize(subdivisionLevel);
    }

    internal abstract partial class TProbeReferenceVolume<TPRV, TData, TPerScenarioData, TCell, TProbeBrickPool, TProbeBrickBlendingPool, TDataLocation>
        : TProbeReferenceVolumeBase
        where TPRV : TProbeReferenceVolume<TPRV, TData, TPerScenarioData, TCell, TProbeBrickPool, TProbeBrickBlendingPool, TDataLocation>, new()
        where TData : class, ICellData<TPerScenarioData>
        where TPerScenarioData : struct, IPerScenarioData
        where TCell : Cell<TData, TPerScenarioData>, new()
        where TProbeBrickPool : TProbeBrickPool<TDataLocation>
        where TProbeBrickBlendingPool : TProbeBrickBlendingPool<TProbeBrickPool, TDataLocation>
        where TDataLocation : struct, ProbeBrickPoolBase.IDataLocation
    {

        internal struct RefVolTransform
        {
            public Vector3 posWS;
            public Quaternion rot;
            public float scale;
        }

        protected bool m_IsInitialized = false;
        protected bool m_SupportScenarios = false;
        protected bool m_SupportScenarioBlending = false;
        protected bool m_ForceNoDiskStreaming = false;
        protected bool m_SupportDiskStreaming = false;
        protected bool m_SupportGPUStreaming = false;
        protected bool m_UseStreamingAssets = true;
        protected float m_MinBrickSize;
        protected int m_MaxSubdivision;
        protected Vector3 m_ProbeOffset;
        protected TProbeBrickPool m_Pool;
        protected TProbeBrickBlendingPool m_BlendingPool;
        protected ProbeBrickIndex m_Index;
        protected ProbeGlobalIndirection m_CellIndices;
        protected IProbeVolumeConstantRuntimeResources m_ConstantRuntimeResources;

        protected List<Chunk> m_TmpSrcChunks = new List<Chunk>();
        protected float[] m_PositionOffsets = new float[ProbeBrickPoolBase.brickProbeCountPerDim];
        protected Bounds m_CurrGlobalBounds = new Bounds();

        internal override Bounds globalBounds { get { return m_CurrGlobalBounds; } set { m_CurrGlobalBounds = value; } }

        internal Dictionary<int, TCell> cells = new Dictionary<int, TCell>();
        protected ObjectPool<TCell> m_CellPool = new ObjectPool<TCell>(x => x.Clear(), null, false);

        protected TDataLocation m_TemporaryDataLocation;
        protected int m_TemporaryDataLocationMemCost;

        // We need to keep track the area, in cells, that is currently loaded. The index buffer will cover even unloaded areas, but we want to avoid sampling outside those areas.
        protected Vector3Int _minLoadedCellPos = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
        protected Vector3Int _maxLoadedCellPos = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

        // Information of the probe volume scenes that is being loaded (if one is pending)
        Dictionary<string, (ProbeVolumeBakingSet, List<int>)> m_PendingScenesToBeLoaded = new Dictionary<string, (ProbeVolumeBakingSet, List<int>)>();

        // Information on probes we need to remove.
        Dictionary<string, List<int>> m_PendingScenesToBeUnloaded = new Dictionary<string, List<int>>();
        // Information of the probe volume scenes that is being loaded (if one is pending)
        List<string> m_ActiveScenes = new List<string>();

        ProbeVolumeBakingSetWeakReference m_CurrentBakingSetReference = new();
        protected ProbeVolumeBakingSet m_CurrentBakingSet
        {
            get => m_CurrentBakingSetReference.Get();
            set => m_CurrentBakingSetReference.Set(value);
        }

        ProbeVolumeBakingSetWeakReference m_LazyBakingSetReference = new();
        protected ProbeVolumeBakingSet m_LazyBakingSet
        {
            get => m_LazyBakingSetReference.Get();
            set => m_LazyBakingSetReference.Set(value);
        }

        bool m_NeedLoadAsset = false;
        bool m_ProbeReferenceVolumeInit = false;
        bool m_EnabledBySRP = false;
        bool m_VertexSampling = false;

        /// <summary>Is Probe Volume initialized.</summary>
        public override bool isInitialized => m_IsInitialized;
        internal override bool enabledBySRP => m_EnabledBySRP;
        internal bool vertexSampling => m_VertexSampling;

        internal bool hasUnloadedCells => m_ToBeLoadedCells.size != 0;

        internal override bool supportLightingScenarios => m_SupportScenarios;
        internal override bool supportScenarioBlending => m_SupportScenarioBlending;
        internal bool gpuStreamingEnabled => m_SupportGPUStreaming;
        internal override bool diskStreamingEnabled => m_SupportDiskStreaming && !m_ForceNoDiskStreaming;

        bool m_NeedsIndexRebuild = false;
        bool m_HasChangedIndex = false;

        protected ProbeVolumeTextureMemoryBudget m_MemoryBudget;
        protected ProbeVolumeBlendingTextureMemoryBudget m_BlendingMemoryBudget;

        internal override bool clearAssetsOnVolumeClear { get; set; }

        /// <summary>The active baking set.</summary>
        public override ProbeVolumeBakingSet currentBakingSet => m_CurrentBakingSet;

        /// <summary>The active lighting scenario.</summary>
        public override string lightingScenario
        {
            get => m_CurrentBakingSet ? m_CurrentBakingSet.GetLightingScenario(backendId) : null;
            set
            {
                SetActiveScenario(value);
            }
        }


        /// <summary>The lighting scenario blending target.</summary>
        public override string otherScenario
        {
            get => m_CurrentBakingSet ? m_CurrentBakingSet.GetOtherScenario(backendId) : null;
        }

        /// <summary>The blending factor currently used to blend probe data. A value of 0 means blending is not active.</summary>
        public override float scenarioBlendingFactor
        {
            get => m_CurrentBakingSet ? m_CurrentBakingSet.GetScenarioBlendingFactor(backendId) : 0.0f;
            set
            {
                if (m_CurrentBakingSet != null)
                    m_CurrentBakingSet.BlendLightingScenario(m_CurrentBakingSet.GetOtherScenario(backendId), value, this);
            }
        }

        internal void SetActiveScenario(string scenario, bool verbose = true)
        {
            if (m_CurrentBakingSet != null)
                m_CurrentBakingSet.SetActiveScenario(scenario, verbose, this);
        }

        /// <summary>Allows smooth transitions between two lighting scenarios. This only affects the runtime data used for lighting.</summary>
        /// <param name="otherScenario">The name of the scenario to load.</param>
        /// <param name="blendingFactor">The factor used to interpolate between the active scenario and otherScenario. Accepted values range from 0 to 1 and will progressively blend from the active scenario to otherScenario.</param>
        public override void BlendLightingScenario(string otherScenario, float blendingFactor)
        {
            if (m_CurrentBakingSet != null)
                m_CurrentBakingSet.BlendLightingScenario(otherScenario, blendingFactor, this);
        }

        /// <summary>
        /// Get the memory budget for the Probe Volume system.
        /// </summary>
        public override ProbeVolumeTextureMemoryBudget memoryBudget => m_MemoryBudget;


        static readonly TPRV s_instance = new TPRV();

        public static TPRV instance => s_instance;

        protected TProbeReferenceVolume()
        {
            m_MinBrickSize = 1.0f;
            m_MaxSubdivision = 1;
            m_ProbeOffset = Vector3.zero;
        }

        internal override List<ProbeVolumePerSceneData> perSceneDataList { get; } = new List<ProbeVolumePerSceneData>();

        internal override void RegisterPerSceneData(ProbeVolumePerSceneData data)
        {
            if (!perSceneDataList.Contains(data))
            {
                perSceneDataList.Add(data);

                // Registration can happen before the reference volume (or even the current pipeline) is initialized, so in this case we need to delay the init.
                if (m_IsInitialized)
                    data.Initialize(backendId);
            }
        }

        /// <summary>
        /// Setting a BakingSet while it is uninitialized schedules it to be set after initialization.
        /// </summary>
        /// <param name="bakingSet">BakingSet to set.</param>
        /// <returns>Returns true when scheduled.</returns>
        internal bool ScheduleBakingSet(ProbeVolumeBakingSet bakingSet)
        {
            if (m_IsInitialized)
            {
                return false;
            }

            m_LazyBakingSet = bakingSet;
            return true;
        }

        /// <summary>
        /// Set the scheduled BakingSet if it exists.
        /// </summary>
        /// <returns>Returns true if the scheduling is executed.</returns>
        internal bool ProcessScheduledBakingSet()
        {
            if (m_LazyBakingSet == null)
            {
                return false;
            }

            SetActiveBakingSet(m_LazyBakingSet);
            m_LazyBakingSet = null;

            return true;
        }

        /// <summary>
        /// Loads the baking set the given scene is part of if it exists.
        /// </summary>
        /// <param name="scene">The scene for which to load the baking set.</param>
        public override void SetActiveScene(Scene scene)
        {
            if (TryGetPerSceneData(GetSceneGUID(scene), out var perSceneData))
                SetActiveBakingSet(perSceneData.serializedBakingSet);
        }

        /// <summary>
        /// Set the currently active baking set.
        /// Can be used when loading additively two scenes belonging to different baking sets to control which one is active.
        /// </summary>
        /// <param name="bakingSet">The baking set to load.</param>
        public override void SetActiveBakingSet(ProbeVolumeBakingSet bakingSet)
        {
            if (m_CurrentBakingSet == bakingSet)
                return;

            if (ScheduleBakingSet(bakingSet))
            {
                return;
            }

            foreach (var data in perSceneDataList)
                data.QueueSceneRemoval();

            UnloadBakingSet();
            SetBakingSetAsCurrent(bakingSet);

            if (m_CurrentBakingSet != null)
            {
                foreach (var data in perSceneDataList)
                    data.QueueSceneLoading();
            }
        }

        void SetBakingSetAsCurrent(ProbeVolumeBakingSet bakingSet)
        {
            m_CurrentBakingSet = bakingSet;

            // Can happen when you have only one scene loaded and you remove it from any baking set.
            if (m_CurrentBakingSet != null)
            {
                // Delay first time init to after baking set is loaded to ensure we allocate what's needed
                InitializeProbeReferenceVolume();

                m_CurrentBakingSet.Initialize(m_UseStreamingAssets, this);
                m_CurrGlobalBounds = m_CurrentBakingSet.GetGlobalBounds(backendId);
                SetSubdivisionDimensions(bakingSet.GetMinBrickSize(backendId), bakingSet.GetMaxSubdivision(backendId), bakingSet.GetBakedProbeOffset(backendId));

                m_NeedsIndexRebuild = true;
            }
        }

        internal override void RegisterBakingSet(ProbeVolumePerSceneData data)
        {
            // Scene components are registered with every backend before the SRP
            // initializes them. Defer selecting a baking set until this particular
            // volume has completed initialization; otherwise initializing the first
            // backend would implicitly initialize all secondary volumes while it
            // forwards scene notifications.
            if (!m_IsInitialized)
            {
                m_LazyBakingSet = data.serializedBakingSet;
                return;
            }

            if (m_CurrentBakingSet == null)
            {
                SetBakingSetAsCurrent(data.serializedBakingSet);
            }
        }

        internal virtual void UnloadBakingSet()
        {
            // Need to make sure everything is unloaded before killing the baking set ref (we need it to unload cell CPU data).
            PerformPendingOperations();
            CancelStreamingRequests();

            if (m_CurrentBakingSet != null)
                m_CurrentBakingSet.Cleanup(backendId);

            m_CurrentBakingSet = null;
            m_CurrGlobalBounds = new Bounds();

            // Restart pool from zero to avoid unnecessary memory consumption when going from a big to a small scene.
            if (m_ScratchBufferPool != null)
            {
                m_ScratchBufferPool.Cleanup();
                m_ScratchBufferPool = null;
            }
        }

        internal override void UnregisterPerSceneData(ProbeVolumePerSceneData data)
        {
            perSceneDataList.Remove(data);
            if (perSceneDataList.Count == 0)
                UnloadBakingSet();
        }

        internal override bool TryGetPerSceneData(string sceneGUID, out ProbeVolumePerSceneData perSceneData)
        {
            foreach (var data in perSceneDataList)
            {
                if (GetSceneGUID(data.gameObject.scene) == sceneGUID)
                {
                    perSceneData = data;
                    return true;
                }
            }

            perSceneData = null;
            return false;
        }

        internal float indexFragmentationRate { get => m_ProbeReferenceVolumeInit ? m_Index.fragmentationRate : 0; }

        /// <summary>
        /// Initialize the Probe Volume system
        /// </summary>
        /// <param name="parameters">Initialization parameters.</param>
        public void Initialize(in ProbeVolumeSystemParameters parameters)
        {
            if (m_IsInitialized)
            {
                Debug.LogError("Probe Volume System has already been initialized.");
                return;
            }

            var probeVolumeSettings = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ProbeVolumeGlobalSettings>();

            m_MemoryBudget = parameters.memoryBudget;
            m_BlendingMemoryBudget = parameters.blendingMemoryBudget;
            m_SupportScenarios = parameters.supportScenarios;
            m_SupportScenarioBlending = parameters.supportScenarios && parameters.supportScenarioBlending && SystemInfo.supportsComputeShaders && m_BlendingMemoryBudget != 0;
            // A Unity 2022 project can legitimately have no APV graphics
            // settings entry (for example, an asset created before the
            // backported registry was populated).  Unity 6 treats the absent
            // entry as the default value, i.e. streaming assets enabled.
            m_UseStreamingAssets = probeVolumeSettings == null || !probeVolumeSettings.probeVolumeDisableStreamingAssets;
#if UNITY_EDITOR
            // In editor we can always use Streaming Assets. This optimizes memory usage for editing.
            m_UseStreamingAssets = true;
#endif
            m_SupportGPUStreaming = parameters.supportGPUStreaming;
            // GPU Streaming is required for Disk Streaming
            var streamingUploadCS = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ProbeVolumeRuntimeResources>()?.probeVolumeUploadDataCS;
            var streamingUploadL2CS = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ProbeVolumeRuntimeResources>()?.probeVolumeUploadDataL2CS;
            m_SupportDiskStreaming = parameters.supportDiskStreaming && SystemInfo.supportsComputeShaders && m_SupportGPUStreaming && m_UseStreamingAssets && streamingUploadCS != null && streamingUploadL2CS != null;
            // For now this condition is redundant with m_SupportDiskStreaming but we plan to support disk streaming without compute in the future.
            // So we need to split the conditions to plan for that.
            m_DiskStreamingUseCompute = SystemInfo.supportsComputeShaders && streamingUploadCS != null && streamingUploadL2CS != null;
            InitializeSpecializedParameters(parameters);
            InitializeDebug();
            m_ConstantRuntimeResources = CreateConstantRuntimeResources();
            m_ConstantRuntimeResources?.Initialize();
            InitStreaming();

            m_IsInitialized = true;
            m_NeedsIndexRebuild = true;

#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += ProbeVolumeBakingSet.OnSceneSaving;
            ProbeVolumeBakingSet.SyncBakingSets();
#endif
            m_EnabledBySRP = true;

            foreach (var data in perSceneDataList)
                data.Initialize(backendId);

            ProcessScheduledBakingSet();
        }

        /// <summary>
        /// Communicate to the Probe Volume system whether the SRP enables Probe Volume.
        /// It is important to keep in mind that this is not used by the system for anything else but book-keeping,
        /// the SRP is still responsible to disable anything Probe volume related on SRP side.
        /// </summary>
        /// <param name="srpEnablesPV">The value of the new enabled</param>
        public override void SetEnableStateFromSRP(bool srpEnablesPV)
        {
            m_EnabledBySRP = srpEnablesPV;
        }

        /// <summary>
        /// Communicate to the Probe Volume system whether the SRP uses per vertex sampling
        /// </summary>
        /// <param name="value">True for vertex sampling, false for pixel sampling</param>
        public override void SetVertexSamplingEnabled(bool value)
        {
            m_VertexSampling = value;
        }

        internal void ForceMemoryBudget(ProbeVolumeTextureMemoryBudget budget)
        {
            m_MemoryBudget = budget;
        }

        internal void ForceNoDiskStreaming(bool state)
        {
            m_ForceNoDiskStreaming = state;
        }

        internal bool forceNoDiskStreaming => m_ForceNoDiskStreaming;

        /// <summary>
        /// Cleanup the Probe Volume system.
        /// </summary>
        public void Cleanup()
        {
            CoreUtils.SafeRelease(m_EmptyIndexBuffer);
            m_EmptyIndexBuffer = null;
            m_ConstantRuntimeResources?.Cleanup();
            m_ConstantRuntimeResources = null;

#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= ProbeVolumeBakingSet.OnSceneSaving;
#endif

            if (!m_IsInitialized)
            {
                Debug.LogError("Adaptive Probe Volumes have not been initialized before calling Cleanup.");
                return;
            }

            CleanupLoadedData();
            CleanupDebug();
            CleanupStreaming();
            DeinitProbeReferenceVolume();
            m_IsInitialized = false;
        }

        /// <summary>
        /// Get approximate video memory impact, in bytes, of the system.
        /// </summary>
        /// <returns>An approximation of the video memory impact, in bytes, of the system</returns>
        public override int GetVideoMemoryCost()
        {
            if (!m_ProbeReferenceVolumeInit)
                return 0;

            return m_Pool.estimatedVMemCost 
                + m_Index.estimatedVMemCost 
                + m_CellIndices.estimatedVMemCost 
                + m_BlendingPool.estimatedVMemCost 
                + m_TemporaryDataLocationMemCost;
        }

        protected virtual void RemoveCell(int cellIndex)
        {
            if (cells.TryGetValue(cellIndex, out var cellInfo))
            {
                cellInfo.referenceCount--;
                if (cellInfo.referenceCount <= 0)
                {
                    cells.Remove(cellIndex);

                    if (cellInfo.loaded)
                    {
                        m_LoadedCells.Remove(cellInfo);
                        UnloadCell(cellInfo);
                    }
                    else
                    {
                        m_ToBeLoadedCells.Remove(cellInfo);
                    }

                    // Cancellation is asynchronous. Bricks and support data are
                    // direct I/O destinations and cannot be disposed until it ends.
                    CancelAndWaitForCellRequests(cellInfo);
                    m_CurrentBakingSet.ReleaseCell(backendId, cellIndex);
                    m_CellPool.Release(cellInfo);
                }
            }
        }

        // This one is internal for baking purpose only.
        // Calling this from "outside" will not properly update Loaded/ToBeLoadedCells arrays and thus will break the state of streaming.
        internal virtual void UnloadCell(TCell cell)
        {
            // Streaming might have never loaded the cell in the first place
            if (cell.loaded)
            {
                if (cell.blendingInfo.blending)
                {
                    m_LoadedBlendingCells.Remove(cell);
                    UnloadBlendingCell(cell);
                }
                else
                    m_ToBeLoadedBlendingCells.Remove(cell);

                if (cell.indexInfo.flatIndicesInGlobalIndirection != null)
                    m_CellIndices.MarkEntriesAsUnloaded(cell.indexInfo.flatIndicesInGlobalIndirection);

                if (diskStreamingEnabled)
                {
                    if (cell.streamingInfo.IsStreaming())
                    {
                        CancelStreamingRequest(cell);
                    }
                    else
                    {
                        ReleaseBricks(cell);
                        cell.data.Cleanup(!diskStreamingEnabled); // Release CPU data
                    }
                }
                else
                    ReleaseBricks(cell);

                cell.loaded = false;
                cell.debugProbes = null;

                ClearDebugData();
            }
        }

        internal virtual void UnloadBlendingCell(TCell cell)
        {
            if (diskStreamingEnabled && cell.streamingInfo.IsBlendingStreaming())
                CancelBlendingStreamingRequest(cell);

            if (cell.blendingInfo.blending)
            {
                m_BlendingPool.Deallocate(cell.blendingInfo.chunkList);
                cell.blendingInfo.chunkList.Clear();
                cell.blendingInfo.blending = false;
            }
        }

        internal override void UnloadAllCells()
        {
            for (int i = 0; i < m_LoadedCells.size; ++i)
                UnloadCell(m_LoadedCells[i]);

            m_ToBeLoadedCells.AddRange(m_LoadedCells);
            m_LoadedCells.Clear();
        }

        internal void UnloadAllBlendingCells()
        {
            for (int i = 0; i < m_LoadedBlendingCells.size; ++i)
                UnloadBlendingCell(m_LoadedBlendingCells[i]);

            m_ToBeLoadedBlendingCells.AddRange(m_LoadedBlendingCells);
            m_LoadedBlendingCells.Clear();
        }

        protected virtual void AddCell(int cellIndex)
        {
            // The same cell can exist in more than one scene
            // Need to check existence because we don't want to add cells more than once to streaming structures
            // TODO: Check perf if relevant?
            if (!cells.TryGetValue(cellIndex, out var cell))
            {
                var cellDesc = m_CurrentBakingSet.GetCellDesc(backendId, cellIndex);

                // This can happen if a baking set was cleared and not all scene were loaded.
                // This results in stray ProbeVolumeAssets for unloaded scenes that contains cell indices not present in the baking set if it was rebaked partially.
                if (cellDesc != null)
                {
                    cell = m_CellPool.Get();
                    cell.desc = cellDesc;
                    cell.data = m_CurrentBakingSet.GetCellData<TData>(backendId, cellIndex);
                    cell.poolInfo.shChunkCount = cell.desc.shChunkCount;
                    cell.indexInfo.flatIndicesInGlobalIndirection = m_CellIndices.GetFlatIndicesForCell(cellDesc.position);
                    cell.indexInfo.indexChunkCount = cell.desc.indexChunkCount;
                    cell.indexInfo.indirectionEntryInfo = cell.desc.indirectionEntryInfo;
                    cell.indexInfo.updateInfo.entriesInfo = new ProbeBrickIndex.IndirectionEntryUpdateInfo[cellDesc.indirectionEntryInfo.Length];
                    cell.referenceCount = 1;

                    cells[cellIndex] = cell;

                    m_ToBeLoadedCells.Add(cell);
                }
            }
            else
            {
                cell.referenceCount++;
            }
        }

        // This one is internal for baking purpose only.
        // Calling this from "outside" will not properly update Loaded/ToBeLoadedCells arrays and thus will break the state of streaming.
        internal virtual bool LoadCell(TCell cell, bool ignoreErrorLog = false)
        {
            // First try to allocate pool memory. This is what is most likely to fail.
            if (ReservePoolChunks(cell.desc.bricksCount, cell.poolInfo.chunkList, ignoreErrorLog))
            {
                int indirectionBufferEntries = cell.indexInfo.indirectionEntryInfo.Length;

                var indexInfo = cell.indexInfo;

                for (int entry = 0; entry < indirectionBufferEntries; ++entry)
                {
                    // TODO: remove, this is for migration
                    if (!cell.indexInfo.indirectionEntryInfo[entry].hasMinMax)
                    {
                        if (cell.data.bricks.IsCreated)
                            ComputeEntryMinMax(ref cell.indexInfo.indirectionEntryInfo[entry], cell.data.bricks);
                        else
                        {
                            int entrySize = CellSize(GetEntrySubdivLevel());
                            cell.indexInfo.indirectionEntryInfo[entry].minBrickPos = Vector3Int.zero;
                            cell.indexInfo.indirectionEntryInfo[entry].maxBrickPosPlusOne = new Vector3Int(entrySize + 1, entrySize + 1, entrySize + 1);
                            cell.indexInfo.indirectionEntryInfo[entry].hasMinMax = true;
                        }
                    }

                    int brickCountAtResForEntry = GetNumberOfBricksAtSubdiv(cell.indexInfo.indirectionEntryInfo[entry]);
                    indexInfo.updateInfo.entriesInfo[entry].numberOfChunks = m_Index.GetNumberOfChunks(brickCountAtResForEntry);
                }

                bool canAllocateCell = m_Index.FindSlotsForEntries(ref indexInfo.updateInfo.entriesInfo);
                if (canAllocateCell)
                {
                    bool scenarioValid = cell.UpdateCellScenarioData(lightingScenario, otherScenario);

                    bool successfulReserve = m_Index.ReserveChunks(indexInfo.updateInfo.entriesInfo, ignoreErrorLog);
                    Debug.Assert(successfulReserve);

                    for (int entry = 0; entry < indirectionBufferEntries; ++entry)
                    {
                        indexInfo.updateInfo.entriesInfo[entry].minValidBrickIndexForCellAtMaxRes = indexInfo.indirectionEntryInfo[entry].minBrickPos;
                        indexInfo.updateInfo.entriesInfo[entry].maxValidBrickIndexForCellAtMaxResPlusOne = indexInfo.indirectionEntryInfo[entry].maxBrickPosPlusOne;
                        indexInfo.updateInfo.entriesInfo[entry].entryPositionInBricksAtMaxRes = indexInfo.indirectionEntryInfo[entry].positionInBricks;
                        indexInfo.updateInfo.entriesInfo[entry].minSubdivInCell = indexInfo.indirectionEntryInfo[entry].minSubdiv;
                        indexInfo.updateInfo.entriesInfo[entry].hasOnlyBiggerBricks = indexInfo.indirectionEntryInfo[entry].hasOnlyBiggerBricks;
                    }
                    cell.loaded = true;

                    // Copy proper data inside index buffers and pool textures or kick off streaming request.
                    if (scenarioValid)
                        AddBricks(cell);

                    _minLoadedCellPos = Vector3Int.Min(_minLoadedCellPos, cell.desc.position);
                    _maxLoadedCellPos = Vector3Int.Max(_maxLoadedCellPos, cell.desc.position);

                    ClearDebugData();

                    return true;
                }
                else
                {
                    // Index allocation failed, we need to release the pool chunks.
                    ReleasePoolChunks(cell.poolInfo.chunkList);
                    // We know we should have the space (test done in TryLoadCell above) so it's because of fragmentation.
                    StartIndexDefragmentation();
                }

                return false;
            }

            return false;
        }

        // May not load all cells if there is not enough space given the current budget.
        internal void LoadAllCells()
        {
            int loadedCellsCount = m_LoadedCells.size;
            for (int i = 0; i < m_ToBeLoadedCells.size; ++i)
            {
                TCell cell = m_ToBeLoadedCells[i];
                if (LoadCell(cell, ignoreErrorLog: true))
                    m_LoadedCells.Add(cell);
            }

            for (int i = loadedCellsCount; i < m_LoadedCells.size; ++i)
            {
                m_ToBeLoadedCells.Remove(m_LoadedCells[i]);
            }
        }

        // This will compute the min/max position of loaded cells as well as the max number of SH chunk for a cell.
        void ComputeCellGlobalInfo()
        {
            _minLoadedCellPos = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
            _maxLoadedCellPos = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);

            foreach (var cell in cells.Values)
            {
                if (cell.loaded)
                {
                    _minLoadedCellPos = Vector3Int.Min(cell.desc.position, _minLoadedCellPos);
                    _maxLoadedCellPos = Vector3Int.Max(cell.desc.position, _maxLoadedCellPos);
                }
            }
        }

        internal override void AddPendingSceneLoading(string sceneGUID, ProbeVolumeBakingSet bakingSet)
        {
            if (m_PendingScenesToBeLoaded.ContainsKey(sceneGUID))
            {
                m_PendingScenesToBeLoaded.Remove(sceneGUID);
            }

            // User might have loaded other scenes with probe volumes but not belonging to the "single scene" baking set.
            if (bakingSet == null && m_CurrentBakingSet != null && m_CurrentBakingSet.singleSceneMode)
                return;

            if (bakingSet.chunkSizeInBricks != ProbeBrickPoolBase.GetChunkSizeInBrickCount())
            {
                Debug.LogError($"Trying to load probe volume data ({bakingSet.name}) baked with an older incompatible version. Please rebake your data.");
                return;
            }

            if (m_CurrentBakingSet != null && !m_CurrentBakingSet.HasSameSceneGUIDs(bakingSet))
            {
                // Trying to load data for a scene from a different baking set than currently loaded ones.
                // This should not throw an error, but it's not supported
                return;
            }

            // If we don't have any loaded asset yet, we need to verify the other queued assets.
            // Only need to check one entry here, they should all have the same baking set by construction.
            if (m_PendingScenesToBeLoaded.Count != 0)
            {
                foreach (var toBeLoadedBakingSet in m_PendingScenesToBeLoaded.Values)
                {
                    if (bakingSet != toBeLoadedBakingSet.Item1)
                    {
                        Debug.LogError($"Trying to load Adaptive Probe Volumes data for a scene from a different baking set from other scenes that are being loaded. " +
                                    $"Please make sure all loaded scenes are in the same baking set.");
                        return;
                    }

                    break;
                }
            }

            // A secondary backend can receive the scene notification before its
            // volume has selected a current baking set. Use the set carried by the
            // notification in that case instead of dereferencing a null current set.
            var cellSet = m_CurrentBakingSet ?? bakingSet;
            m_PendingScenesToBeLoaded.Add(sceneGUID, (bakingSet, cellSet?.GetSceneCellIndexList(backendId, sceneGUID)));
            m_NeedLoadAsset = true;
        }

        internal override void AddPendingSceneRemoval(string sceneGUID)
        {
            if (m_PendingScenesToBeLoaded.ContainsKey(sceneGUID))
                m_PendingScenesToBeLoaded.Remove(sceneGUID);
            if (m_ActiveScenes.Contains(sceneGUID) && m_CurrentBakingSet != null)
                m_PendingScenesToBeUnloaded.TryAdd(sceneGUID, m_CurrentBakingSet.GetSceneCellIndexList(backendId, sceneGUID));
        }

        internal virtual void RemovePendingScene(string sceneGUID, List<int> cellList)
        {
            if (m_ActiveScenes.Contains(sceneGUID))
            {
                m_ActiveScenes.Remove(sceneGUID);
            }

            // Remove bricks and empty cells
            foreach (var cellIndex in cellList)
            {
                RemoveCell(cellIndex);
            }

            ClearDebugData();
            ComputeCellGlobalInfo();
        }

        void PerformPendingIndexChangeAndInit()
        {
            if (m_NeedsIndexRebuild)
            {
                CleanupLoadedData();
                InitializeGlobalIndirection();
                m_HasChangedIndex = true;
                m_NeedsIndexRebuild = false;
            }
            else
            {
                m_HasChangedIndex = false;
            }
        }

        internal override void SetSubdivisionDimensions(float minBrickSize, int maxSubdiv, Vector3 offset)
        {
            m_MinBrickSize = minBrickSize;
            SetMaxSubdivision(maxSubdiv);
            m_ProbeOffset = offset;
        }

        bool LoadCells(List<int> cellIndices)
        {
            if (m_CurrentBakingSet.ResolveCellData(backendId, this, cellIndices))
            {
                ClearDebugData();

                // Add all the cells to the system.
                // They'll be streamed in later on.
                for (int i = 0; i < cellIndices.Count; ++i)
                {
                    AddCell(cellIndices[i]);
                }

                return true;
            }

            return false;
        }

        void PerformPendingLoading()
        {
            if ((m_PendingScenesToBeLoaded.Count == 0 && m_ActiveScenes.Count == 0) || !m_NeedLoadAsset || !m_ProbeReferenceVolumeInit)
                return;

            m_Pool.EnsureTextureValidity();
            m_BlendingPool.EnsureTextureValidity();

            // Load the ones that are already active but reload if we said we need to load
            if (m_HasChangedIndex)
            {
                foreach (var sceneGUID in m_ActiveScenes)
                {
                    LoadCells(m_CurrentBakingSet.GetSceneCellIndexList(backendId, sceneGUID));
                }
            }

            foreach (var loadRequest in m_PendingScenesToBeLoaded)
            {
                var sceneGUID = loadRequest.Key;
                if (LoadCells(loadRequest.Value.Item2) && !m_ActiveScenes.Contains(sceneGUID))
                {
                    m_ActiveScenes.Add(sceneGUID);
                }
            }

            m_PendingScenesToBeLoaded.Clear();

            // Mark the loading as done.
            m_NeedLoadAsset = false;
        }

        void PerformPendingDeletion()
        {
            foreach (var unloadRequest in m_PendingScenesToBeUnloaded)
            {
                RemovePendingScene(unloadRequest.Key, unloadRequest.Value);
            }

            m_PendingScenesToBeUnloaded.Clear();
        }

        internal void ComputeEntryMinMax(ref IndirectionEntryInfo entryInfo, ReadOnlySpan<Brick> bricks)
        {
            int entrySize = CellSize(GetEntrySubdivLevel());
            Vector3Int entry_min = entryInfo.positionInBricks;
            Vector3Int entry_max = entryInfo.positionInBricks + new Vector3Int(entrySize, entrySize, entrySize);

            if (entryInfo.hasOnlyBiggerBricks)
            {
                entryInfo.minBrickPos = entry_min;
                entryInfo.maxBrickPosPlusOne = entry_max;
            }
            else
            {
                entryInfo.minBrickPos = entryInfo.maxBrickPosPlusOne = Vector3Int.zero;

                bool initialized = false;
                for (int i = 0; i < bricks.Length; i++)
                {
                    int brickSize = CellSize(bricks[i].subdivisionLevel);
                    var brickMin = bricks[i].position;
                    var brickMax = bricks[i].position + new Vector3Int(brickSize, brickSize, brickSize);
                    if (!ProbeBrickIndex.BrickOverlapEntry(brickMin, brickMax, entry_min, entry_max))
                        continue;

                    // Bricks can be bigger than entries !
                    brickMin = Vector3Int.Max(brickMin, entry_min);
                    brickMax = Vector3Int.Min(brickMax, entry_max);

                    if (initialized)
                    {
                        entryInfo.minBrickPos = Vector3Int.Min(brickMin, entryInfo.minBrickPos);
                        entryInfo.maxBrickPosPlusOne = Vector3Int.Max(brickMax, entryInfo.maxBrickPosPlusOne);
                    }
                    else
                    {
                        entryInfo.minBrickPos = brickMin;
                        entryInfo.maxBrickPosPlusOne = brickMax;
                        initialized = true;
                    }
                }
            }

            entryInfo.minBrickPos = entryInfo.minBrickPos - entry_min;
            entryInfo.maxBrickPosPlusOne = Vector3Int.one + entryInfo.maxBrickPosPlusOne - entry_min;
            entryInfo.hasMinMax = true;
        }

        static internal int GetNumberOfBricksAtSubdiv(IndirectionEntryInfo entryInfo)
        {
            // This is a special case that can be handled manually easily.
            if (entryInfo.hasOnlyBiggerBricks)
                return 1;

            Vector3Int sizeOfValidIndicesAtMaxRes = entryInfo.maxBrickPosPlusOne - entryInfo.minBrickPos;
            Vector3Int bricksForEntry = sizeOfValidIndicesAtMaxRes / CellSize(entryInfo.minSubdiv);
            return bricksForEntry.x * bricksForEntry.y * bricksForEntry.z;
        }

        /// <summary>
        /// Perform all the operations that are relative to changing the content or characteristics of the probe reference volume.
        /// </summary>
        public override void PerformPendingOperations()
        {

#if UNITY_EDITOR
            checksDuringBakeAction?.Invoke();
#endif
            PerformPendingDeletion();
            PerformPendingIndexChangeAndInit();
            PerformPendingLoading();
        }

        internal void InitializeGlobalIndirection()
        {
            // Current baking set can be null at init and we still need the buffers to valid.
            var minCellPosition = m_CurrentBakingSet ? m_CurrentBakingSet.GetMinCellPosition(backendId) : Vector3Int.zero;
            var maxCellPosition = m_CurrentBakingSet ? m_CurrentBakingSet.GetMaxCellPosition(backendId) : Vector3Int.zero;
            if (m_CellIndices != null)
                m_CellIndices.Cleanup();
            m_CellIndices = new ProbeGlobalIndirection(minCellPosition, maxCellPosition, Mathf.Max(1, (int)Mathf.Pow(3, m_MaxSubdivision - 1)));
            if (m_SupportGPUStreaming)
            {
                if (m_DefragCellIndices != null)
                    m_DefragCellIndices.Cleanup();
                m_DefragCellIndices = new ProbeGlobalIndirection(minCellPosition, maxCellPosition, Mathf.Max(1, (int)Mathf.Pow(3, m_MaxSubdivision - 1)));
            }
        }
  
        /// <summary>
        /// Initialize the reference volume.
        /// </summary>
        private void InitializeProbeReferenceVolume()
        {
            if (!m_ProbeReferenceVolumeInit)
                m_NeedLoadAsset = true;

            m_ProbeReferenceVolumeInit = InitProbeReferenceVolume(m_ProbeReferenceVolumeInit);

            // Refresh debug menu
            if (DebugManager.instance.GetPanel(debugPanelName, false) != null)
            {
                instance.UnregisterDebug(false);
                instance.RegisterDebug();
            }
        }
#if UNITY_EDITOR
        internal override bool EnsureCurrentBakingSet(ProbeVolumeBakingSet bakingSet)
        {
            //Ensure that all currently loaded scenes belong to the same set.
            foreach (var data in perSceneDataList)
            {
                if (UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(data.gameObject.scene))
                    continue; // Ignore preview scenes - they are needed to make closed subscenes work

                var set = ProbeVolumeBakingSet.GetBakingSetForScene(data.gameObject.scene);
                if (set != bakingSet)
                    return false;
            }

            SetBakingSetAsCurrent(bakingSet);
            return true;
        }
#endif

        /// <summary>
        /// Get the resources that are bound to the runtime shaders for sampling Adaptive Probe Volume data.
        /// </summary>
        /// <returns>The resources to bind to runtime shaders.</returns>
        public TResources GetRuntimeResources<TResources>() where TResources : struct, IRuntimeResources
        {
            if (!m_ProbeReferenceVolumeInit)
                return default(TResources);

            TResources rr = new TResources();
            m_Index.GetRuntimeResources<TResources>(ref rr);
            m_CellIndices.GetRuntimeResources<TResources>(ref rr);
            m_Pool.GetRuntimeResources<TResources>(ref rr);
            m_ConstantRuntimeResources?.GetRuntimeResources<TResources>(ref rr);
            return rr;
        }

        internal void SetMaxSubdivision(int maxSubdivision)
        {
            int newValue = Math.Min(maxSubdivision, ProbeBrickIndex.kMaxSubdivisionLevels);
            if (newValue != m_MaxSubdivision)
            {
                m_MaxSubdivision = System.Math.Min(maxSubdivision, ProbeBrickIndex.kMaxSubdivisionLevels);
                if (m_CellIndices != null)
                {
                    m_CellIndices.Cleanup();
                }
                if (m_SupportGPUStreaming && m_DefragCellIndices != null)
                {
                    m_DefragCellIndices.Cleanup();
                }
                InitializeGlobalIndirection();
            }
        }

        internal override float BrickSize(int subdivisionLevel) => m_MinBrickSize * CellSize(subdivisionLevel);
        internal override float MinBrickSize() => m_MinBrickSize;
        internal override float MaxBrickSize() => BrickSize(m_MaxSubdivision - 1);
        internal override Vector3 ProbeOffset() => m_ProbeOffset;
        internal override int GetMaxSubdivision() => m_MaxSubdivision;
        internal int GetMaxSubdivision(float multiplier) => Mathf.CeilToInt(m_MaxSubdivision * multiplier);
        internal float GetDistanceBetweenProbes(int subdivisionLevel) => BrickSize(subdivisionLevel) / 3.0f;
        internal override float MinDistanceBetweenProbes() => GetDistanceBetweenProbes(0);

        // IMPORTANT! IF THIS VALUE CHANGES DATA NEEDS TO BE REBAKED.
        internal int GetGlobalIndirectionEntryMaxSubdiv() => ProbeGlobalIndirection.kEntryMaxSubdivLevel;

        internal override int GetEntrySubdivLevel() => Mathf.Min(ProbeGlobalIndirection.kEntryMaxSubdivLevel, m_MaxSubdivision - 1);
        internal float GetEntrySize() => BrickSize(GetEntrySubdivLevel());
        /// <summary>
        /// Returns whether any brick data has been loaded.
        /// </summary>
        /// <returns>True if brick data is present, otherwise false.</returns>
        public override bool DataHasBeenLoaded() => m_LoadedCells.size != 0;

        internal override void Clear()
        {
            ClearDebugData();
            if (m_ProbeReferenceVolumeInit)
            {
                try
                {
                    // Need to do that first because some assets may be in the process of being removed.
                    PerformPendingOperations();
                }
                finally
                {
                    UnloadAllCells();
                    CancelStreamingRequests();
                    // Release only the streamable assets owned by this backend.
                    m_CurrentBakingSet?.GetBakedData(backendId)?.CleanupStreamableAssets();
                    m_ToBeLoadedCells.Clear();
                    m_Pool.Clear();
                    m_BlendingPool.Clear();
                    m_Index.Clear();
                    cells.Clear();

                    Debug.Assert(m_LoadedCells.size == 0);
                }
            }

            if (clearAssetsOnVolumeClear)
            {
                m_PendingScenesToBeLoaded.Clear();
                m_ActiveScenes.Clear();
            }
        }

        // Currently only used for 1 chunk at a time but kept in case we need more in the future.
        protected List<Chunk> GetSourceLocations(int count, int chunkSize, TDataLocation dataLoc)
        {
            var c = new Chunk();
            m_TmpSrcChunks.Clear();
            m_TmpSrcChunks.Add(c);

            // currently this code assumes that the texture width is a multiple of the allocation chunk size
            for (int j = 1; j < count; j++)
            {
                c.x += chunkSize * ProbeBrickPoolBase.brickProbeCountPerDim;
                if (c.x >= dataLoc.width)
                {
                    c.x = 0;
                    c.y += ProbeBrickPoolBase.brickProbeCountPerDim;
                    if (c.y >= dataLoc.height)
                    {
                        c.y = 0;
                        c.z += ProbeBrickPoolBase.brickProbeCountPerDim;
                    }
                }
                m_TmpSrcChunks.Add(c);
            }

            return m_TmpSrcChunks;
        }

        protected void UpdateDataLocationTexture<T>(Texture output, NativeArray<T> input) where T : struct
        {
            var outputNativeArray = (output as Texture3D).GetPixelData<T>(0);
            Debug.Assert(outputNativeArray.Length >= input.Length);
            outputNativeArray.GetSubArray(0, input.Length).CopyFrom(input);
            (output as Texture3D).Apply();
        }

        protected void UpdateValidityTextureWithoutMask(Texture output, NativeArray<byte> input)
        {
            // On some platforms, single channel unorm format isn't supported, so validity uses 4 channel unorm format.
            // Then we can't directly copy the data, but need to account for the 3 unused channels.
            uint numComponents = GraphicsFormatUtility.GetComponentCount(output.graphicsFormat);
            if (numComponents == 1)
            {
                UpdateDataLocationTexture(output, input);
            }
            else
            {
                Debug.Assert(output.graphicsFormat == GraphicsFormat.R8G8B8A8_UNorm);
                var outputNativeArray = (output as Texture3D).GetPixelData<(byte, byte, byte, byte)>(0);
                Debug.Assert(outputNativeArray.Length >= input.Length);
                for (int i = 0; i < input.Length; i++)
                {
                    outputNativeArray[i] = (input[i], input[i], input[i], input[i]);
                }
                (output as Texture3D).Apply();
            }
        }

        bool ReservePoolChunks(int brickCount, List<Chunk> chunkList, bool ignoreErrorLog)
        {
            // calculate the number of chunks necessary
            int brickChunksCount = ProbeBrickPoolBase.GetChunkCount(brickCount);
            chunkList.Clear();

            // Try to allocate texture space
            return m_Pool.Allocate(brickChunksCount, chunkList, ignoreErrorLog);
        }

        void ReleasePoolChunks(List<Chunk> chunkList)
        {
            m_Pool.Deallocate(chunkList);
            chunkList.Clear();
        }
 
        bool AddBricks(TCell cell)
        {
            using var pm = new ProfilerMarker("AddBricks").Auto();

            if (supportScenarioBlending) // Register this cell for blending system
                m_ToBeLoadedBlendingCells.Add(cell);

            // If blending is enabled, we rely on it to upload data already blended to avoid popping
            // If enabled but blending factor is 0, upload here in case blending pool is not already allocated
            if (!supportScenarioBlending || scenarioBlendingFactor == 0.0f || !cell.hasTwoScenarios)
            {
                if (diskStreamingEnabled)
                {
                    PushDiskStreamingRequest(cell, lightingScenario, -1, m_OnStreamingComplete);
                }
                else
                {
                    UpdatePoolAndIndex(cell, null, null, -1, null);
                }

                cell.blendingInfo.blendingFactor = 0.0f;
            }
            else if (supportScenarioBlending)
            {
                cell.blendingInfo.Prioritize();
                // Cell index update is delayed until probe data is loaded
                cell.indexInfo.indexUpdated = false;
            }

            cell.loaded = true;
            ClearDebugData();

            return true;
        }

        protected void UpdateCellIndex(TCell cell)
        {
            cell.indexInfo.indexUpdated = true;

            // Build index
            var bricks = cell.data.bricks;
            m_Index.AddBricks(cell.indexInfo, bricks, cell.poolInfo.chunkList, ProbeBrickPoolBase.GetChunkSizeInBrickCount(), m_Pool.GetPoolWidth(), m_Pool.GetPoolHeight());

            // Update indirection buffer
            m_CellIndices.UpdateCell(cell.indexInfo);
        }

        protected void ReleaseBricks(TCell cell)
        {
            if (cell.poolInfo.chunkList.Count == 0)
            {
                Debug.Log("Tried to release bricks from an empty Cell.");
                return;
            }

            // clean up the index
            m_Index.RemoveBricks(cell.indexInfo);
            cell.indexInfo.indexUpdated = false;

            // clean up the pool
            m_Pool.Deallocate(cell.poolInfo.chunkList);

            cell.poolInfo.chunkList.Clear();
        }

        protected void DeinitProbeReferenceVolume()
        {
            if (m_ProbeReferenceVolumeInit)
            {
                foreach (var data in perSceneDataList)
                    AddPendingSceneRemoval(data.sceneGUID);

                PerformPendingDeletion();
                CancelStreamingRequests();

                m_Index.Cleanup();
                m_CellIndices.Cleanup();

                if (m_SupportGPUStreaming)
                {
                    m_DefragIndex.Cleanup();
                    m_DefragCellIndices.Cleanup();
                }

                if (m_Pool != null)
                {
                    m_Pool.Cleanup();
                    m_BlendingPool.Cleanup();
                }

                m_TemporaryDataLocation.Cleanup();
                m_ProbeReferenceVolumeInit = false;

                if (m_CurrentBakingSet != null)
                    m_CurrentBakingSet.Cleanup(backendId);
                m_CurrentBakingSet = null;
            }
            else
            {
                m_CellIndices?.Cleanup();
                m_DefragCellIndices?.Cleanup();
            }

            ClearDebugData();

            Debug.Assert(m_LoadedCells.size == 0);
        }

        /// <summary>
        /// Cleanup loaded data.
        /// </summary>
        void CleanupLoadedData()
        {
            UnloadAllCells();
        }


        #region abstract method

        protected abstract bool InitProbeReferenceVolume(bool probeReferenceVolumeInit);

        protected virtual void InitializeSpecializedParameters(in ProbeVolumeSystemParameters parameters) { }

        protected virtual IProbeVolumeConstantRuntimeResources CreateConstantRuntimeResources() => null;

        protected abstract void UpdatePool(CellStreamingRequest request, CommandBuffer cmd, List<Chunk> chunkList, CellStreamingScratchBuffer dataBuffer, int poolIndex);

        // Runtime API starts here
        protected abstract bool AddBlendingBricks(TCell cell);

        protected abstract void UpdatePoolAndIndex(TCell cell, CellStreamingRequest request, CellStreamingScratchBuffer dataBuffer, int poolIndex, CommandBuffer cmd);

        protected abstract TProbeVolumeScratchBufferPoolBase CreateScratchBufferPool(ProbeVolumeBakingSet bakingSet);

        protected virtual int GetScratchBufferChunkSize(ProbeVolumeBakingSet bakingSet) => 0;

        protected virtual int GetScratchBufferMaxChunkCount(ProbeVolumeBakingSet bakingSet) => 0;

        protected virtual bool HasValidScenarioDataForStreaming(ProbeVolumeBakingSet.PerScenarioDataInfo scenarioData) => true;

        protected virtual int GetOptionalScenarioDataReadSize(ProbeVolumeBakingSet bakingSet, int chunkCount) => 0;

        protected virtual unsafe void AddAdditionalScenarioDataReadCommands(CellStreamingRequest request, int cellIndex, int chunkCount, ref byte* mappedBufferAddr) { }

        protected virtual void CloseAdditionalScenarioDataFiles(ProbeVolumeBakingSet.PerScenarioDataInfo scenarioData) { }

        #endregion
    }
}
