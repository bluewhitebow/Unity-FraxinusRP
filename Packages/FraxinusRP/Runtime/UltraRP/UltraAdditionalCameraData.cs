using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using FraxinusEngine.Base;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FraxinusEngine.Rendering.Ultra
{
    public enum CameraOverrideOption
    {
        Off,
        On,
        [InspectorName("Use settings from Render Pipeline Asset")]
        UsePipelineSettings,
    }

    public enum RendererOverrideOption
    {
        Custom,
        [InspectorName("Use settings from Render Pipeline Asset")]
        UsePipelineSettings,
    }

    public enum CameraRenderType
    {
        Base,
        Overlay,
    }

    public enum AntialiasingMode
    {
        /// <summary>
        /// Use this to have no post-processing anti-aliasing pass performed.
        /// </summary>
        [InspectorName("No Anti-aliasing")]
        None,

        /// <summary>
        /// Use this to have a fast approximated anti-aliasing pass rendered when resolving the camera to screen
        /// </summary>
        [InspectorName("Fast Approximate Anti-aliasing (FXAA)")]
        FastApproximateAntialiasing,

        /// <summary>
        /// Use this to have a <c>SubpixelMorphologicalAntiAliasing</c> SMAA pass rendered when resolving the camera to screen
        /// You can choose the SMAA quality by setting <seealso cref="AntialiasingQuality"/>.
        /// </summary>
        [InspectorName("Subpixel Morphological Anti-aliasing (SMAA)")]
        SubpixelMorphologicalAntiAliasing,

        /// <summary>
        /// Use this to have a temporal anti-aliasing pass rendered when resolving camera to screen.
        /// </summary>
        [InspectorName("Temporal Anti-aliasing (TAA)")]
        TemporalAntiAliasing,
    }


    public enum AntialiasingQuality
    {
        /// <summary>
        /// Use this to select the low <c>SubpixelMorphologicalAntiAliasing</c> SMAA quality
        /// </summary>
        Low,

        /// <summary>
        /// Use this to select the medium <c>SubpixelMorphologicalAntiAliasing</c> SMAA quality
        /// </summary>
        Medium,

        /// <summary>
        /// Use this to select the high <c>SubpixelMorphologicalAntiAliasing</c> SMAA quality
        /// </summary>
        High
    }

    public static class CameraExtensions
    {
        public static UltraAdditionalCameraData GetUltraAdditionalCameraData(this Camera camera)
        {
            var gameObject = camera.gameObject;
            bool componentExists = gameObject.TryGetComponent<UltraAdditionalCameraData>(out var cameraData);
            if (!componentExists)
                cameraData = gameObject.AddComponent<UltraAdditionalCameraData>();

            return cameraData;
        }

        public static VolumeFrameworkUpdateMode GetVolumeFrameworkUpdateMode(this Camera camera)
        {
            UltraAdditionalCameraData cameraData = camera.GetUltraAdditionalCameraData();
            return cameraData.volumeFrameworkUpdateMode;
        }

        public static void SetVolumeFrameworkUpdateMode(this Camera camera, VolumeFrameworkUpdateMode mode)
        {
            UltraAdditionalCameraData cameraData = camera.GetUltraAdditionalCameraData();
            if (cameraData.volumeFrameworkUpdateMode == mode)
                return;

            bool requiredUpdatePreviously = cameraData.requiresVolumeFrameworkUpdate;
            cameraData.volumeFrameworkUpdateMode = mode;

            if (requiredUpdatePreviously && !cameraData.requiresVolumeFrameworkUpdate)
                camera.UpdateVolumeStack(cameraData);
        }

        public static void UpdateVolumeStack(this Camera camera)
        {
            UltraAdditionalCameraData cameraData = camera.GetUltraAdditionalCameraData();
            camera.UpdateVolumeStack(cameraData);
        }

        public static void UpdateVolumeStack(this Camera camera, UltraAdditionalCameraData cameraData)
        {
            Debug.Assert(cameraData != null, "cameraData can not be null when updating the volume stack.");

            // Repro: enter play mode with a script that trigger this API at Start.
            if (!VolumeManager.instance.isInitialized)
            {
                Debug.LogError($"{nameof(UpdateVolumeStack)} must not be called before {nameof(VolumeManager)}.{nameof(VolumeManager.instance)}.{nameof(VolumeManager.instance.Initialize)}. If you tries calling this from Awake or Start, try instead to use the {nameof(RenderPipelineManager)}.{nameof(RenderPipelineManager.activeRenderPipelineCreated)} callback to be sure your render pipeline is fully initialized before calling this.");
                return;
            }

            // We only update the local volume stacks for cameras set to ViaScripting.
            // Otherwise it will be updated in the frame.
            if (cameraData.requiresVolumeFrameworkUpdate)
                return;

            // Create stack for camera
            if (cameraData.volumeStack == null)
                cameraData.GetOrCreateVolumeStack();

            camera.GetVolumeLayerMaskAndTrigger(cameraData, out LayerMask layerMask, out Transform trigger);
            VolumeManager.instance.Update(cameraData.volumeStack, trigger, layerMask);
        }


        public static void DestroyVolumeStack(this Camera camera)
        {
            UltraAdditionalCameraData cameraData = camera.GetUltraAdditionalCameraData();
            camera.DestroyVolumeStack(cameraData);
        }

        public static void DestroyVolumeStack(this Camera camera, UltraAdditionalCameraData cameraData)
        {
            if (cameraData == null || cameraData.volumeStack == null)
                return;

            cameraData.volumeStack = null;
        }

        internal static void GetVolumeLayerMaskAndTrigger(this Camera camera, UltraAdditionalCameraData cameraData, out LayerMask layerMask, out Transform trigger)
        {
            // Default values when there's no additional camera data available
            layerMask = 1; // "Default"
            trigger = camera.transform;

            if (cameraData != null)
            {
                layerMask = cameraData.volumeLayerMask;
                trigger = (cameraData.volumeTrigger != null) ? cameraData.volumeTrigger : trigger;
            }
            else if (camera.cameraType == CameraType.SceneView)
            {
                // Try to mirror the MainCamera volume layer mask for the scene view - do not mirror the target
                var mainCamera = Camera.main;
                UltraAdditionalCameraData mainAdditionalCameraData = null;

                if (mainCamera != null && mainCamera.TryGetComponent(out mainAdditionalCameraData))
                    layerMask = mainAdditionalCameraData.volumeLayerMask;

                trigger = (mainAdditionalCameraData != null && mainAdditionalCameraData.volumeTrigger != null) ? mainAdditionalCameraData.volumeTrigger : trigger;
            }
        }
    }

    static class CameraTypeUtility
    {
        static string[] s_cameraTypeNames = Enum.GetNames(typeof(CameraRenderType)).ToArray();
        public static string GetName(this CameraRenderType type)
        {
            int typeInt = (int)type;
            if (typeInt < 0 || typeInt >= s_cameraTypeNames.Length)
                typeInt = (int)CameraRenderType.Base;
            return s_cameraTypeNames[typeInt];
        }
    }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    [ImageEffectAllowedInSceneView]
    [ExecuteAlways]
    public class UltraAdditionalCameraData : MonoBehaviour, IAdditionalData
    {
        const string k_gizmoPath = "Packages/com.bluewhitebow.fraxinus-rp/Editor/UltraRPEditor/Gizmos/";
        const string k_baseCameraGizmoPath = k_gizmoPath + "Camera_Base.png";
        const string k_overlayCameraGizmoPath = k_gizmoPath + "Camera_Base.png";
        const string k_postProcessingGizmoPath = k_gizmoPath + "Camera_PostProcessing.png";

        [FormerlySerializedAs("renderShadows"), SerializeField]
        bool _renderShadows = true;

        [SerializeField] CameraRenderType _cameraType = CameraRenderType.Base;
        [SerializeField] List<Camera> _cameras = new List<Camera>();
        [SerializeField] int _rendererIndex = -1;

        [SerializeField] VolumeFrameworkUpdateMode _volumeFrameworkUpdateModeOption = VolumeFrameworkUpdateMode.UsePipelineSettings;
        [SerializeField] LayerMask _volumeLayerMask = 1; // "Default"
        [SerializeField] Transform _volumeTrigger = null;

        [SerializeField] bool _renderPostProcessing = false;
        [SerializeField] AntialiasingMode _antialiasing = AntialiasingMode.None;
        [SerializeField] AntialiasingQuality _antialiasingQuality = AntialiasingQuality.High;
        [SerializeField] bool _stopNaN = false;
        [SerializeField] bool _dithering = false;
        [SerializeField] bool _clearDepth = true;
        [SerializeField] bool _allowHDROutput = true;

        [SerializeField] bool _useScreenCoordOverride;
        [SerializeField] Vector4 _screenSizeOverride;
        [SerializeField] Vector4 _screenCoordScaleBias;

        [NonSerialized] Camera _camera;

        [NonSerialized] MotionVectorsPersistentData _motionVectorsPersistentData = new MotionVectorsPersistentData();

        [NonSerialized] internal UltraCameraHistory history = new UltraCameraHistory();

        [SerializeField] internal TAASettings taaSettings = TAASettings.Create();

        /// <summary>
        /// Discards temporal feedback on the next render after a camera cut or teleport.
        /// Applies to TAA, STP, SGSR2 and automatic exposure adaptation.
        /// </summary>
        public void ResetTemporalHistory()
        {
            taaSettings.resetHistoryFrames = 1;
        }

        /// <summary>
        /// Motion data that persists over a frame.
        /// </summary>
        internal MotionVectorsPersistentData motionVectorsPersistentData => _motionVectorsPersistentData;

        static UltraAdditionalCameraData s_defaultAdditionalCameraData = null;
        internal static UltraAdditionalCameraData defaultAdditionalCameraData
        {
            get
            {
                if (s_defaultAdditionalCameraData == null)
                    s_defaultAdditionalCameraData = new UltraAdditionalCameraData();

                return s_defaultAdditionalCameraData;
            }
        }

        public CameraRenderType renderType
        {
            get => _cameraType;
            set => _cameraType = value;
        }

        public List<Camera> cameraStack
        {
            get
            {
                if (renderType != CameraRenderType.Base)
                {
                    var camera = gameObject.GetComponent<Camera>();
                    Debug.LogWarning(string.Format("{0}: This camera is of {1} type. Only Base cameras can have a camera stack.", camera.name, renderType));
                    return null;
                }

                return _cameras;
            }
        }

#if UNITY_EDITOR
        internal new Camera camera
#else
        internal Camera camera
#endif
        {
            get
            {
                if (!_camera)
                    gameObject.TryGetComponent<Camera>(out _camera);

                return _camera;
            }
        }


        public bool renderShadows
        {
            get => _renderShadows;
            set => _renderShadows = value;
        }

        internal void UpdateCameraStack()
        {
#if UNITY_EDITOR
            Undo.RecordObject(this, "Update camera stack");
#endif
            int prev = _cameras.Count;
            _cameras.RemoveAll(cam => cam == null);
            int curr = _cameras.Count;
            int removedCamsCount = prev - curr;
            if (removedCamsCount != 0)
                Debug.LogWarning(name + ": " + removedCamsCount + " camera overlay" + (removedCamsCount > 1 ? "s" : "") + " no longer exists and will be removed from the camera stack.");
        }

        public bool clearDepth
        {
            get => _clearDepth;
        }

        public ScriptableRenderer scriptableRenderer
        {
            get
            {
                if (UltraRenderPipeline.asset == null)
                    return null;
                if (!UltraRenderPipeline.asset.ValidateRendererGraph(_rendererIndex))
                {
                    int defaultIndex = UltraRenderPipeline.asset.defaultRendererIndex;
                    Debug.LogWarning(
                        $"Renderer at <b>index {_rendererIndex.ToString()}</b> is missing for camera <b>{GetComponent<Camera>().name}</b>, falling back to Default Renderer. <b>{UltraRenderPipeline.asset.rendererGraphList[defaultIndex].name}</b>",
                        UltraRenderPipeline.asset);

                    return UltraRenderPipeline.asset.GetRenderer(defaultIndex);
                }
                return UltraRenderPipeline.asset.GetRenderer(_rendererIndex);
            }
        }


        public int rendererIndex => _rendererIndex;

        public void SetRenderer(int index)
        {
            _rendererIndex = index;
        }

        internal VolumeFrameworkUpdateMode volumeFrameworkUpdateMode
        {
            get => _volumeFrameworkUpdateModeOption;
            set => _volumeFrameworkUpdateModeOption = value;
        }

        public bool requiresVolumeFrameworkUpdate
        {
            get
            {
                if (_volumeFrameworkUpdateModeOption == VolumeFrameworkUpdateMode.UsePipelineSettings)
                    return UltraRenderPipeline.asset.volumeFrameworkUpdateMode != VolumeFrameworkUpdateMode.ViaScripting;

                return _volumeFrameworkUpdateModeOption == VolumeFrameworkUpdateMode.EveryFrame;
            }
        }

        public LayerMask volumeLayerMask
        {
            get => _volumeLayerMask;
            set => _volumeLayerMask = value;
        }

        public Transform volumeTrigger
        {
            get => _volumeTrigger;
            set => _volumeTrigger = value;
        }

        public bool stopNaN
        {
            get => _stopNaN;
            set => _stopNaN = value;
        }

        public bool dithering
        {
            get => _dithering;
            set => _dithering = value;
        }


        public bool allowHDROutput
        {
            get => _allowHDROutput;
            set => _allowHDROutput = value;
        }

        public bool useScreenCoordOverride
        {
            get => _useScreenCoordOverride;
            set => _useScreenCoordOverride = value;
        }

        public Vector4 screenSizeOverride
        {
            get => _screenSizeOverride;
            set => _screenSizeOverride = value;
        }

        public Vector4 screenCoordScaleBias
        {
            get => _screenCoordScaleBias;
            set => _screenCoordScaleBias = value;
        }


        private static List<VolumeStack> s_cachedVolumeStacks;

        VolumeStack _volumeStack = null;
        public VolumeStack volumeStack
        {
            get => _volumeStack;
            set
            {
                // If the volume stack is being removed,
                // add it back to the list so it can be reused later
                if (value == null && _volumeStack != null && _volumeStack.isValid)
                {
                    if (s_cachedVolumeStacks == null)
                        s_cachedVolumeStacks = new List<VolumeStack>(4);

                    s_cachedVolumeStacks.Add(_volumeStack);
                }

                _volumeStack = value;
            }
        }
        public bool renderPostProcessing
        {
            get =>_renderPostProcessing;
            set =>_renderPostProcessing = value;
        }

        public AntialiasingMode antialiasing
        {
            get => _antialiasing;
            set => _antialiasing = value;
        }

        public AntialiasingQuality antialiasingQuality
        {
            get => _antialiasingQuality;
            set => _antialiasingQuality = value;
        }

        internal UltraCameraHistory historyManager => history;

        void Start()
        {
            // Need to ensure correct behavoiur for overlay cameras settings their clear flag to nothing.
            // This can't be done in the upgrade since the camera component can't be retrieved in the deserialization phase.
            // OnValidate ensure future cameras won't have this issue.
            if (_cameraType == CameraRenderType.Overlay)
                camera.clearFlags = CameraClearFlags.Nothing;
        }

        public void OnDrawGizmos()
        {
            string gizmoName = "";
            Color tint = Color.white;

            if (_cameraType == CameraRenderType.Base)
                gizmoName = k_baseCameraGizmoPath;
            else if (_cameraType == CameraRenderType.Overlay)
                gizmoName = k_overlayCameraGizmoPath;

#if UNITY_2019_2_OR_NEWER
#if UNITY_EDITOR
            if (Selection.activeObject == gameObject)
            {
                // Get the preferences selection color
                tint = SceneView.selectedOutlineColor;
            }
#endif
            if (!string.IsNullOrEmpty(gizmoName))
                Gizmos.DrawIcon(transform.position, gizmoName, true, tint);

            //if (renderPostProcessing)
            //{
            //    Gizmos.DrawIcon(transform.position, k_postProcessingGizmoPath, true, tint);
            //}
#else
            if (renderPostProcessing)
            {
                Gizmos.DrawIcon(transform.position, k_postProcessingGizmoPath);
            }
            Gizmos.DrawIcon(transform.position, gizmoName);
#endif
        }

        /// <inheritdoc/>
        public void OnDestroy()
        {
            //You cannot call scriptableRenderer here. If you where not in UltraRP, this will actually create the renderer.
            //This can occurs in cross pipeline but also on Dedicated Server where the gfx device do not run. (UUM-75237)
            //Use GetRawRenderer() instead.

            _camera.DestroyVolumeStack(this);
            if (camera.cameraType != CameraType.SceneView)
                GetRawRenderer()?.ReleaseRenderTargets();

            history?.Dispose();
            history = null;
        }

        internal void GetOrCreateVolumeStack()
        {
            // Try first to reuse a volume stack
            if (s_cachedVolumeStacks != null && s_cachedVolumeStacks.Count > 0)
            {
                int index = s_cachedVolumeStacks.Count - 1;
                var stack = s_cachedVolumeStacks[index];
                s_cachedVolumeStacks.RemoveAt(index);
                if (stack.isValid)
                    volumeStack = stack;
            }

            // Create a new stack if was not possible to reuse an old one
            if (volumeStack == null)
                volumeStack = VolumeManager.instance.CreateStack();
        }

        ScriptableRenderer GetRawRenderer()
        {
            if (UltraRenderPipeline.asset is null)
                return null;

            ReadOnlySpan<ScriptableRenderer> renderers = UltraRenderPipeline.asset.renderers;
            if (renderers == null || renderers.IsEmpty)
                return null;

            if (_rendererIndex >= renderers.Length || _rendererIndex < 0)
                return null;

            return renderers[_rendererIndex];
        }
    }
}
