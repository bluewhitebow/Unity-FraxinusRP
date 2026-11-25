using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine.Rendering.RenderGraphModule;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityEngine.Rendering
{
    /// <summary>
    /// Modes for Debugging Probes
    /// </summary>
    [GenerateHLSL]
    public enum DebugProbeShadingMode
    {
        /// <summary>
        /// Based on Spherical Harmonics
        /// </summary>
        SH,
        /// <summary>
        /// Based on Spherical Harmonics first band only (ambient)
        /// </summary>
        SHL0,
        /// <summary>
        /// Based on Spherical Harmonics band zero and one only
        /// </summary>
        SHL0L1,
        /// <summary>
        /// Based on validity
        /// </summary>
        Validity,
        /// <summary>
        /// Based on validity over a dilation threshold
        /// </summary>
        ValidityOverDilationThreshold,
        /// <summary>
        /// Based on rendering layer masks
        /// </summary>
        RenderingLayerMasks,
        /// <summary>
        /// Show in red probes that have been made invalid by adjustment volumes. Important to note that this debug view will only show result for volumes still present in the scene.
        /// </summary>
        InvalidatedByAdjustmentVolumes,
        /// <summary>Unity 2022 name for <see cref="InvalidatedByAdjustmentVolumes"/>.</summary>
        [Obsolete("Use InvalidatedByAdjustmentVolumes instead.")]
        InvalidatedByTouchupVolumes = InvalidatedByAdjustmentVolumes,
        /// <summary>
        /// Based on size
        /// </summary>
        Size,
        /// <summary>
        /// Based on spherical harmonics sky occlusion
        /// </summary>
        SkyOcclusionSH,
        /// <summary>
        /// Based on shading direction
        /// </summary>
        SkyDirection,
        /// <summary>
        /// Occlusion values per probe
        /// </summary>
        ProbeOcclusion,
    }

    internal class CellInstancedDebugProbes
    {
        public List<Matrix4x4[]> probeBuffers;
        public List<Matrix4x4[]> offsetBuffers;
        public List<MaterialPropertyBlock> props;
    }

    enum ProbeSamplingDebugUpdate
    {
        Never,
        Once,
        Always
    }

    class ProbeSamplingDebugData
    {
        public ProbeSamplingDebugUpdate update = ProbeSamplingDebugUpdate.Never; // When compute buffer should be updated
        public Vector2 coordinates = new Vector2(0.5f, 0.5f);
        public bool forceScreenCenterCoordinates = false; // use screen center instead of mouse position
        public Camera camera = null; // useful in editor when multiple scene tabs are opened
        public bool shortcutPressed = false;
        public GraphicsBuffer positionNormalBuffer; // buffer storing position and normal
    }

    class ProbeVolumeDebug : IDebugData
    {
        public bool drawProbes;
        public bool drawBricks;
        public bool drawCells;
        public bool realtimeSubdivision;
        public int subdivisionCellUpdatePerFrame = 4;
        public float subdivisionDelayInSeconds = 1;
        public DebugProbeShadingMode probeShading;
        public float probeSize = 0.3f;
        public float subdivisionViewCullingDistance = 500.0f;
        public float probeCullingDistance = 200.0f;
        public int maxSubdivToVisualize = ProbeBrickIndex.kMaxSubdivisionLevels;
        public int minSubdivToVisualize = 0;
        public float exposureCompensation;
        public bool drawProbeSamplingDebug = false;
        public float probeSamplingDebugSize = 0.3f;
        public bool debugWithSamplingNoise = false;
        public uint samplingRenderingLayer;
        public bool drawVirtualOffsetPush;
        public float offsetSize = 0.025f;
        public bool freezeStreaming;
        public bool displayCellStreamingScore;
        public bool displayIndexFragmentation;
        public int otherStateIndex = 0;
        public bool verboseStreamingLog;
        public bool debugStreaming = false;
        public bool autoDrawProbes = true;
        public bool isolationProbeDebug = true;
        public byte visibleLayers;

        // NOTE: We should get that from the camera directly instead of storing it as static
        // But we can't access the volume parameters as they are specific to the RP
        public static Vector3 currentOffset;

        static internal int s_ActiveAdjustmentVolumes = 0;

        public ProbeVolumeDebug()
        {
            Init();
        }

        void Init()
        {
            drawProbes = false;
            drawBricks = false;
            drawCells = false;
            realtimeSubdivision = false;
            subdivisionCellUpdatePerFrame = 4;
            subdivisionDelayInSeconds = 1;
            probeShading = DebugProbeShadingMode.SH;
            probeSize = 0.3f;
            subdivisionViewCullingDistance = 500.0f;
            probeCullingDistance = 200.0f;
            maxSubdivToVisualize = ProbeBrickIndex.kMaxSubdivisionLevels;
            minSubdivToVisualize = 0;
            exposureCompensation = 0.0f;
            drawProbeSamplingDebug = false;
            probeSamplingDebugSize = 0.3f;
            drawVirtualOffsetPush = false;
            offsetSize = 0.025f;
            freezeStreaming = false;
            displayCellStreamingScore = false;
            displayIndexFragmentation = false;
            otherStateIndex = 0;
            autoDrawProbes = true;
            isolationProbeDebug = true;
            visibleLayers = 0xFF;
        }

        public Action GetReset() => () => Init();
    }


#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    internal class ProbeVolumeDebugColorPreferences
    {
        internal static Func<Color> GetDetailSubdivisionColor;
        internal static Func<Color> GetMediumSubdivisionColor;
        internal static Func<Color> GetLowSubdivisionColor;
        internal static Func<Color> GetVeryLowSubdivisionColor;
        internal static Func<Color> GetSparseSubdivisionColor;
        internal static Func<Color> GetSparsestSubdivisionColor;

        internal static Color s_DetailSubdivision = new Color32(135, 35, 255, 255);
        internal static Color s_MediumSubdivision = new Color32(54, 208, 228, 255);
        internal static Color s_LowSubdivision = new Color32(255, 100, 45, 255);
        internal static Color s_VeryLowSubdivision = new Color32(52, 87, 255, 255);
        internal static Color s_SparseSubdivision = new Color32(255, 71, 97, 255);
        internal static Color s_SparsestSubdivision = new Color32(200, 227, 39, 255);

        static ProbeVolumeDebugColorPreferences()
        {
#if UNITY_EDITOR
            GetDetailSubdivisionColor = CoreRenderPipelinePreferences.RegisterPreferenceColor("Adaptive Probe Volumes/Level 0 Subdivision", s_DetailSubdivision);
            GetMediumSubdivisionColor = CoreRenderPipelinePreferences.RegisterPreferenceColor("Adaptive Probe Volumes/Level 1 Subdivision", s_MediumSubdivision);
            GetLowSubdivisionColor = CoreRenderPipelinePreferences.RegisterPreferenceColor("Adaptive Probe Volumes/Level 2 Subdivision", s_LowSubdivision);
            GetVeryLowSubdivisionColor = CoreRenderPipelinePreferences.RegisterPreferenceColor("Adaptive Probe Volumes/Level 3 Subdivision", s_VeryLowSubdivision);
            GetSparseSubdivisionColor = CoreRenderPipelinePreferences.RegisterPreferenceColor("Adaptive Probe Volumes/Level 4 Subdivision", s_SparseSubdivision);
            GetSparsestSubdivisionColor = CoreRenderPipelinePreferences.RegisterPreferenceColor("Adaptive Probe Volumes/Level 5 Subdivision", s_SparsestSubdivision);
#endif
        }
    }

    internal abstract partial class TProbeReferenceVolumeBase
    {
        internal readonly struct CellDebugInfo
        {
            internal readonly Vector3Int position;
            internal readonly bool loaded;
            internal readonly bool streaming;
            internal readonly float streamingScore;
            internal readonly NativeArray<ProbeBrickIndex.Brick> bricks;

            internal CellDebugInfo(Vector3Int position, bool loaded, bool streaming, float streamingScore, NativeArray<ProbeBrickIndex.Brick> bricks)
            {
                this.position = position;
                this.loaded = loaded;
                this.streaming = streaming;
                this.streamingScore = streamingScore;
                this.bricks = bricks;
            }
        }

        internal static ProbeSamplingDebugData probeSamplingDebugData = new ProbeSamplingDebugData();
        internal abstract ProbeVolumeDebug probeVolumeDebug { get; }
        public abstract Color[] subdivisionDebugColors { get; }
        public abstract void RenderDebug(Camera camera, ProbeVolumesOptions options, Texture exposureTexture);
        public abstract bool IsProbeSamplingDebugEnabled();
        public abstract bool GetProbeSamplingDebugResources(Camera camera, out GraphicsBuffer resultBuffer, out Vector2 coords);
        public abstract void RenderFragmentationOverlay(RenderGraph renderGraph, TextureHandle colorBuffer, TextureHandle depthBuffer, DebugOverlay debugOverlay);
        internal abstract IEnumerable<KeyValuePair<Bounds, ProbeBrickIndex.Brick[]>> GetRealtimeSubdivisionDebugData();
        internal abstract IEnumerable<CellDebugInfo> GetCellDebugData();
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
        public virtual string debugPanelName => "Probe Debug Panel";

        internal override ProbeVolumeDebug probeVolumeDebug { get; } = new ProbeVolumeDebug();

        /// <summary>Colors that can be used for debug visualization of the brick structure subdivision.</summary>
        public override Color[] subdivisionDebugColors { get; } = new Color[ProbeBrickIndex.kMaxSubdivisionLevels];

        DebugUI.Widget[] m_DebugItems;
        protected Material m_DebugFragmentationMaterial;

        // Scenario blending debug data
        GUIContent[] m_DebugScenarioNames = new GUIContent[0];
        int[] m_DebugScenarioValues = new int[0];
        string m_DebugActiveSceneGUID, m_DebugActiveScenario;
        DebugUI.EnumField m_DebugScenarioField;

        // Field used for the realtime subdivision preview
        internal Dictionary<Bounds, ProbeBrickIndex.Brick[]> realtimeSubdivisionInfo = new();

        internal override IEnumerable<KeyValuePair<Bounds, ProbeBrickIndex.Brick[]>> GetRealtimeSubdivisionDebugData()
        {
            return realtimeSubdivisionInfo;
        }

        internal override IEnumerable<CellDebugInfo> GetCellDebugData()
        {
            foreach (var cell in cells.Values)
            {
                if (cell.desc == null)
                    continue;

                yield return new CellDebugInfo(
                    cell.desc.position,
                    cell.loaded,
                    cell.streamingInfo.IsStreaming(),
                    cell.streamingInfo.streamingScore,
                    cell.data != null ? cell.data.bricks : default);
            }
        }

        protected bool m_MaxSubdivVisualizedIsMaxAvailable = false;

        /// <summary>
        /// Render Probe Volume related debug
        /// </summary>
        /// <param name="camera">The <see cref="Camera"/></param>
        /// <param name="options">Options coming from the volume stack.</param>
        /// <param name="exposureTexture">Texture containing the exposure value for this frame.</param>
        public override void RenderDebug(Camera camera, ProbeVolumesOptions options, Texture exposureTexture)
        {
            if (camera.cameraType != CameraType.Reflection && camera.cameraType != CameraType.Preview)
            {
                if (options != null)
                    ProbeVolumeDebug.currentOffset = options.worldOffset.value;

                if (!enabledBySRP || !isInitialized)
                    return;

                DrawProbeDebug(camera, exposureTexture);
            }
        }

        /// <summary>
        /// Checks if probe sampling debug is enabled.
        /// </summary>
        /// <returns>True if probe sampling debug is enabled.</returns>
        public override bool IsProbeSamplingDebugEnabled()
        {
            return probeSamplingDebugData.update != ProbeSamplingDebugUpdate.Never;
        }

        /// <summary>
        /// Returns the resources used for probe sampling debug mode.
        /// </summary>
        /// <param name="camera">The camera for which to evaluate the debug mode</param>
        /// <param name="resultBuffer">The buffer that should be filled with position and normal</param>
        /// <param name="coords">The screen space coords to sample the position and normal</param>
        /// <returns>True if the pipeline should write position and normal at coords in resultBuffer</returns>
        public override bool GetProbeSamplingDebugResources(Camera camera, out GraphicsBuffer resultBuffer, out Vector2 coords)
        {
            resultBuffer = probeSamplingDebugData.positionNormalBuffer;
            coords = probeSamplingDebugData.coordinates;

            if (!probeVolumeDebug.drawProbeSamplingDebug)
                return false;

#if UNITY_EDITOR
            if (probeSamplingDebugData.camera != camera)
                return false;
#endif

            if (probeSamplingDebugData.update == ProbeSamplingDebugUpdate.Never)
                return false;

            if (probeSamplingDebugData.update == ProbeSamplingDebugUpdate.Once)
            {
                probeSamplingDebugData.update = ProbeSamplingDebugUpdate.Never;
                probeSamplingDebugData.forceScreenCenterCoordinates = false;
            }

            return true;
        }

#if UNITY_EDITOR
        static void SceneGUI(SceneView sceneView)
        {
            // Probe sampling debug needs to detect user keyboard and mouse position to update ProbeSamplingPositionDebug.
            Event e = Event.current;

            if (e.control && !TProbeReferenceVolumeBase.probeSamplingDebugData.shortcutPressed)
                TProbeReferenceVolumeBase.probeSamplingDebugData.update = ProbeSamplingDebugUpdate.Always;

            if (!e.control && TProbeReferenceVolumeBase.probeSamplingDebugData.shortcutPressed)
                TProbeReferenceVolumeBase.probeSamplingDebugData.update = ProbeSamplingDebugUpdate.Never;

            TProbeReferenceVolumeBase.probeSamplingDebugData.shortcutPressed = e.control;

            if (e.clickCount > 0 && e.button == 0)
            {
                if (TProbeReferenceVolumeBase.probeSamplingDebugData.shortcutPressed)
                    TProbeReferenceVolumeBase.probeSamplingDebugData.update = ProbeSamplingDebugUpdate.Once;
                else
                    TProbeReferenceVolumeBase.probeSamplingDebugData.update = ProbeSamplingDebugUpdate.Never;
            }

            if (TProbeReferenceVolumeBase.probeSamplingDebugData.update == ProbeSamplingDebugUpdate.Never)
                return;

            Vector2 screenCoordinates;

            if (TProbeReferenceVolumeBase.probeSamplingDebugData.forceScreenCenterCoordinates)
                screenCoordinates = new Vector2(sceneView.camera.scaledPixelWidth / 2.0f, sceneView.camera.scaledPixelHeight / 2.0f);
            else
                screenCoordinates = HandleUtility.GUIPointToScreenPixelCoordinate(e.mousePosition);

            if (screenCoordinates.x < 0 || screenCoordinates.x > sceneView.camera.scaledPixelWidth || screenCoordinates.y < 0 || screenCoordinates.y > sceneView.camera.scaledPixelHeight)
                return;

            TProbeReferenceVolumeBase.probeSamplingDebugData.camera = sceneView.camera;
            TProbeReferenceVolumeBase.probeSamplingDebugData.coordinates = screenCoordinates;

            if (e.type != EventType.Repaint && e.type != EventType.Layout)
            {
                var go = HandleUtility.PickGameObject(e.mousePosition, false);
                if (go != null && go.TryGetComponent<MeshRenderer>(out var renderer))
                    instance.probeVolumeDebug.samplingRenderingLayer = renderer.renderingLayerMask;
            }

            SceneView.currentDrawingSceneView.Repaint(); // useful when 'Always Refresh' is not toggled
        }
#endif

        protected abstract bool TryCreateDebugRenderData();
        protected abstract void CleanupDebugRenderData();
        protected abstract void DrawProbeDebug(Camera camera, Texture exposureTexture);

        protected void InitializeDebug()
        {
            // Hard-coded colors for now.
            Debug.Assert(ProbeBrickIndex.kMaxSubdivisionLevels == 7); // Update list if this changes.

            subdivisionDebugColors[0] = ProbeVolumeDebugColorPreferences.s_DetailSubdivision;
            subdivisionDebugColors[1] = ProbeVolumeDebugColorPreferences.s_MediumSubdivision;
            subdivisionDebugColors[2] = ProbeVolumeDebugColorPreferences.s_LowSubdivision;
            subdivisionDebugColors[3] = ProbeVolumeDebugColorPreferences.s_VeryLowSubdivision;
            subdivisionDebugColors[4] = ProbeVolumeDebugColorPreferences.s_SparseSubdivision;
            subdivisionDebugColors[5] = ProbeVolumeDebugColorPreferences.s_SparsestSubdivision;
            subdivisionDebugColors[6] = ProbeVolumeDebugColorPreferences.s_DetailSubdivision;

#if UNITY_EDITOR
            SceneView.duringSceneGui += SceneGUI; // Used to get click and keyboard event on scene view for Probe Sampling Debug
#endif
            if (TryCreateDebugRenderData())
                RegisterDebug();
        }

        protected void CleanupDebug()
        {
            UnregisterDebug(true);
            CleanupDebugRenderData();
            CoreUtils.Destroy(m_DebugFragmentationMaterial);

#if UNITY_EDITOR
            SceneView.duringSceneGui -= SceneGUI;
#endif
        }

        void RegisterDebug()
        {
            var widgetList = new List<DebugUI.Widget>();

            widgetList.Add(new DebugUI.RuntimeDebugShadersMessageBox());

            RegisterDebug(ref widgetList);

            if (widgetList.Count > 0)
            {
                m_DebugItems = widgetList.ToArray();
                var panel = DebugManager.instance.GetPanel(debugPanelName, true);
                panel.children.Add(m_DebugItems);
            }

            DebugManager debugManager = DebugManager.instance;
            debugManager.RegisterData(probeVolumeDebug);
        }

        protected virtual void RegisterDebug(ref List<DebugUI.Widget> widgetList)
        {
            var subdivContainer = new DebugUI.Container()
            {
                displayName = "Subdivision Visualization",
                isHiddenCallback = () =>
                {
#if UNITY_EDITOR
                    return false;
#else
                    return false; // Cells / Bricks visualization is not implemented in a runtime compatible way atm.
#endif
                }
            };
            subdivContainer.children.Add(new DebugUI.BoolField
            {
                displayName = "Display Cells",
                tooltip = "Draw Cells used for loading and streaming.",
                getter = () => probeVolumeDebug.drawCells,
                setter = value => probeVolumeDebug.drawCells = value,
                onValueChanged = RefreshDebug
            });
            subdivContainer.children.Add(new DebugUI.BoolField
            {
                displayName = "Display Bricks",
                tooltip = "Display Subdivision bricks.",
                getter = () => probeVolumeDebug.drawBricks,
                setter = value => probeVolumeDebug.drawBricks = value,
                onValueChanged = RefreshDebug
            });

            subdivContainer.children.Add(new DebugUI.FloatField { displayName = "Debug Draw Distance", tooltip = "How far from the Scene Camera to draw debug visualization for Cells and Bricks. Large distances can impact Editor performance.", getter = () => probeVolumeDebug.subdivisionViewCullingDistance, setter = value => probeVolumeDebug.subdivisionViewCullingDistance = value, min = () => 0.0f });
            widgetList.Add(subdivContainer);

#if UNITY_EDITOR
            var subdivPreviewContainer = new DebugUI.Container()
            {
                displayName = "Subdivision Preview",
                isHiddenCallback = () =>
                {
                    return (!probeVolumeDebug.drawCells && !probeVolumeDebug.drawBricks);
                }
            };
            subdivPreviewContainer.children.Add(new DebugUI.BoolField
            {
                displayName = "Live Updates",
                tooltip = "Enable a preview of Adaptive Probe Volumes data in the Scene without baking. Can impact Editor performance.",
                getter = () => probeVolumeDebug.realtimeSubdivision,
                setter = value => probeVolumeDebug.realtimeSubdivision = value,
            });

            var realtimeSubdivisonChildContainer = new DebugUI.Container()
            {
                isHiddenCallback = () => !probeVolumeDebug.realtimeSubdivision
            };
            realtimeSubdivisonChildContainer.children.Add(new DebugUI.IntField { displayName = "Cell Updates Per Frame", tooltip = "The number of Cells, bricks, and probe positions updated per frame. Higher numbers can impact Editor performance.", getter = () => probeVolumeDebug.subdivisionCellUpdatePerFrame, setter = value => probeVolumeDebug.subdivisionCellUpdatePerFrame = value, min = () => 1, max = () => 100 });
            realtimeSubdivisonChildContainer.children.Add(new DebugUI.FloatField { displayName = "Update Frequency", tooltip = "Delay in seconds between updates to Cell, Brick, and Probe positions if Live Subdivision Preview is enabled.", getter = () => probeVolumeDebug.subdivisionDelayInSeconds, setter = value => probeVolumeDebug.subdivisionDelayInSeconds = value, min = () => 0.1f, max = () => 10 });
            subdivPreviewContainer.children.Add(realtimeSubdivisonChildContainer);
            widgetList.Add(subdivPreviewContainer);
#endif

            widgetList.Add(new DebugUI.RuntimeDebugShadersMessageBox());

            var streamingContainer = new DebugUI.Container()
            {
                displayName = "Streaming",
                isHiddenCallback = () => !(gpuStreamingEnabled || diskStreamingEnabled)
            };
            streamingContainer.children.Add(new DebugUI.BoolField { displayName = "Freeze Streaming", tooltip = "Stop Unity from streaming probe data in or out of GPU memory.", getter = () => probeVolumeDebug.freezeStreaming, setter = value => probeVolumeDebug.freezeStreaming = value });
            streamingContainer.children.Add(new DebugUI.BoolField { displayName = "Display Streaming Score", getter = () => probeVolumeDebug.displayCellStreamingScore, setter = value => probeVolumeDebug.displayCellStreamingScore = value });
            streamingContainer.children.Add(new DebugUI.BoolField { displayName = "Maximum cell streaming", tooltip = "Enable streaming as many cells as possible every frame.", getter = () => instance.loadMaxCellsPerFrame, setter = value => instance.loadMaxCellsPerFrame = value });

            var maxCellStreamingContainerChildren = new DebugUI.Container()
            {
                isHiddenCallback = () => instance.loadMaxCellsPerFrame
            };
            maxCellStreamingContainerChildren.children.Add(new DebugUI.IntField { displayName = "Loaded Cells Per Frame", tooltip = "Determines the maximum number of Cells Unity streams per frame. Loading more Cells per frame can impact performance.", getter = () => instance.numberOfCellsLoadedPerFrame, setter = value => instance.SetNumberOfCellsLoadedPerFrame(value), min = () => 1, max = () => kMaxCellLoadedPerFrame });
            streamingContainer.children.Add(maxCellStreamingContainerChildren);

            // Those are mostly for internal dev purpose.
            if (Debug.isDebugBuild)
            {
                streamingContainer.children.Add(new DebugUI.BoolField { displayName = "Display Index Fragmentation", getter = () => probeVolumeDebug.displayIndexFragmentation, setter = value => probeVolumeDebug.displayIndexFragmentation = value });
                var indexDefragContainerChildren = new DebugUI.Container()
                {
                    isHiddenCallback = () => !probeVolumeDebug.displayIndexFragmentation
                };
                indexDefragContainerChildren.children.Add(new DebugUI.Value { displayName = "Index Fragmentation Rate", getter = () => instance.indexFragmentationRate });
                streamingContainer.children.Add(indexDefragContainerChildren);
                streamingContainer.children.Add(new DebugUI.BoolField { displayName = "Verbose Log", getter = () => probeVolumeDebug.verboseStreamingLog, setter = value => probeVolumeDebug.verboseStreamingLog = value });
                streamingContainer.children.Add(new DebugUI.BoolField { displayName = "Debug Streaming", getter = () => probeVolumeDebug.debugStreaming, setter = value => probeVolumeDebug.debugStreaming = value });
            }

            widgetList.Add(streamingContainer);

            if (supportScenarioBlending && m_CurrentBakingSet != null)
            {
                var blendingContainer = new DebugUI.Container() { displayName = "Scenario Blending" };
                blendingContainer.children.Add(new DebugUI.IntField { displayName = "Number Of Cells Blended Per Frame", getter = () => instance.numberOfCellsBlendedPerFrame, setter = value => instance.numberOfCellsBlendedPerFrame = value, min = () => 0 });
                blendingContainer.children.Add(new DebugUI.FloatField { displayName = "Turnover Rate", getter = () => instance.turnoverRate, setter = value => instance.turnoverRate = value, min = () => 0, max = () => 1 });

                void RefreshScenarioNames(string guid)
                {
                    HashSet<string> allScenarios = new();
                    foreach (var set in Resources.FindObjectsOfTypeAll<ProbeVolumeBakingSet>())
                    {
                        if (!set.sceneGUIDs.Contains(guid))
                            continue;
                        foreach (var scenario in set.lightingScenarios)
                            allScenarios.Add(scenario);
                    }

                    allScenarios.Remove(m_CurrentBakingSet.lightingScenario);
                    if (m_DebugActiveSceneGUID == guid && allScenarios.Count + 1 == m_DebugScenarioNames.Length && m_DebugActiveScenario == m_CurrentBakingSet.lightingScenario)
                        return;

                    int i = 0;
                    ArrayExtensions.ResizeArray(ref m_DebugScenarioNames, allScenarios.Count + 1);
                    ArrayExtensions.ResizeArray(ref m_DebugScenarioValues, allScenarios.Count + 1);
                    m_DebugScenarioNames[0] = new GUIContent("None");
                    m_DebugScenarioValues[0] = 0;
                    foreach (var scenario in allScenarios)
                    {
                        i++;
                        m_DebugScenarioNames[i] = new GUIContent(scenario);
                        m_DebugScenarioValues[i] = i;
                    }

                    m_DebugActiveSceneGUID = guid;
                    m_DebugActiveScenario = m_CurrentBakingSet.lightingScenario;
                    m_DebugScenarioField.enumNames = m_DebugScenarioNames;
                    m_DebugScenarioField.enumValues = m_DebugScenarioValues;
                    if (probeVolumeDebug.otherStateIndex >= m_DebugScenarioNames.Length)
                        probeVolumeDebug.otherStateIndex = 0;
                }

                m_DebugScenarioField = new DebugUI.EnumField
                {
                    displayName = "Scenario Blend Target",
                    tooltip = "Select another lighting scenario to blend with the active lighting scenario.",
                    enumNames = m_DebugScenarioNames,
                    enumValues = m_DebugScenarioValues,
                    getIndex = () =>
                    {
                        if (m_CurrentBakingSet == null)
                            return 0;
                        RefreshScenarioNames(GetSceneGUID(SceneManagement.SceneManager.GetActiveScene()));

                        probeVolumeDebug.otherStateIndex = 0;
                        if (!string.IsNullOrEmpty(m_CurrentBakingSet.otherScenario))
                        {
                            for (int i = 1; i < m_DebugScenarioNames.Length; i++)
                            {
                                if (m_DebugScenarioNames[i].text == m_CurrentBakingSet.otherScenario)
                                {
                                    probeVolumeDebug.otherStateIndex = i;
                                    break;
                                }
                            }
                        }
                        return probeVolumeDebug.otherStateIndex;
                    },
                    setIndex = value =>
                    {
                        string other = value == 0 ? null : m_DebugScenarioNames[value].text;
                        m_CurrentBakingSet.BlendLightingScenario(other, m_CurrentBakingSet.scenarioBlendingFactor);
                        probeVolumeDebug.otherStateIndex = value;
                    },
                    getter = () => probeVolumeDebug.otherStateIndex,
                    setter = (value) => probeVolumeDebug.otherStateIndex = value,
                };

                blendingContainer.children.Add(m_DebugScenarioField);
                blendingContainer.children.Add(new DebugUI.FloatField { displayName = "Scenario Blending Factor", tooltip = "Blend between lighting scenarios by adjusting this slider.", getter = () => instance.scenarioBlendingFactor, setter = value => instance.scenarioBlendingFactor = value, min = () => 0.0f, max = () => 1.0f });

                widgetList.Add(blendingContainer);
            }
        }


        protected virtual void UnregisterDebug(bool destroyPanel)
        {
            if (destroyPanel)
                DebugManager.instance.RemovePanel(debugPanelName);
            else
                DebugManager.instance.GetPanel(debugPanelName, false).children.Remove(m_DebugItems);
        }


        protected void RefreshDebug<T>(DebugUI.Field<T> field, T value)
        {
            UnregisterDebug(false);
            RegisterDebug();
        }

        class RenderFragmentationOverlayPassData
        {
            public Material debugFragmentationMaterial;
            public Rendering.DebugOverlay debugOverlay;
            public int chunkCount;
            public ComputeBuffer debugFragmentationData;
            public TextureHandle colorBuffer;
            public TextureHandle depthBuffer;
        }

        /// <summary>
        /// Render a debug view showing fragmentation of the GPU memory.
        /// </summary>
        /// <param name="renderGraph">The RenderGraph responsible for executing this pass.</param>
        /// <param name="colorBuffer">The color buffer where the overlay will be rendered.</param>
        /// <param name="depthBuffer">The depth buffer used for depth-testing the overlay.</param>
        /// <param name="debugOverlay">The debug overlay manager to orchestrate multiple overlays.</param>
        public override void RenderFragmentationOverlay(RenderGraph renderGraph, TextureHandle colorBuffer, TextureHandle depthBuffer, DebugOverlay debugOverlay)
        {
            if (!m_ProbeReferenceVolumeInit || !probeVolumeDebug.displayIndexFragmentation)
                return;

            using (var builder = renderGraph.AddRasterRenderPass<RenderFragmentationOverlayPassData>(
                       "ProbeFragmentationOverlay", out var passData))
            {
                passData.debugOverlay = debugOverlay;
                passData.debugFragmentationMaterial = m_DebugFragmentationMaterial;
                passData.colorBuffer = colorBuffer;
                passData.depthBuffer = depthBuffer;
                builder.SetRenderAttachment(colorBuffer, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depthBuffer, AccessFlags.ReadWrite);
                passData.debugFragmentationData = m_Index.GetDebugFragmentationBuffer();
                passData.chunkCount = passData.debugFragmentationData.count;

                builder.SetRenderFunc(
                    (RenderFragmentationOverlayPassData data, RasterGraphContext ctx) =>
                    {
                        var mpb = ctx.renderGraphPool.GetTempMaterialPropertyBlock();

                        ctx.cmd.SetViewport(new Rect(
                            data.debugOverlay.x,
                            data.debugOverlay.y,
                            data.debugOverlay.overlaySize,
                            data.debugOverlay.overlaySize));
                        mpb.SetInt("_ChunkCount", data.chunkCount);
                        mpb.SetBuffer("_DebugFragmentation", data.debugFragmentationData);
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, data.debugFragmentationMaterial, 0, MeshTopology.Triangles, 3, 1, mpb);
                        data.debugOverlay.Next();
                    });
            }
        }

        internal void ResetDebugViewToMaxSubdiv()
        {
            if (m_MaxSubdivVisualizedIsMaxAvailable)
                probeVolumeDebug.maxSubdivToVisualize = GetMaxSubdivision() - 1;
        }

        internal void ClearDebugData()
        {
            realtimeSubdivisionInfo.Clear();
        }
    }
}
