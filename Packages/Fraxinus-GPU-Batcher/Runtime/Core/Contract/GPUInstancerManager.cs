using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using System.Threading;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fraxinus.GPUBatcher
{
    public abstract class GPUInstancerManager : IGPUInstancerProfileHost, IDisposable
    {
        private static readonly bool s_supportsComputeShaders = SystemInfo.supportsComputeShaders;

        private bool _isDisposed;

        public string name { get; set; }

        public bool isDisposed => _isDisposed;

        public virtual UnityEngine.Object diagnosticContext => null;

        private GPUInstancerProfile _profile;
        private bool _profileDataApplied;

        private sealed class PrototypeReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => ReferenceEquals(obj, null) ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private Dictionary<GPUInstancerPrototype, GPUInstancerPrototype> _runtimePrototypeMap;
        private Dictionary<GPUInstancerPrototype, GPUInstancerPrototype> _runtimePrototypeReverseMap;
        private List<GPUInstancerPrototype> _ownedRuntimeProfilePrototypes;
        private bool _profilePrototypeIsolationMode;
        private List<GPUInstancerPrototype> _profilePrototypeSources;

        private bool _deactivationCompleted;

        internal void MarkDeactivationCompleted()
        {
            _deactivationCompleted = true;
        }

        public GPUInstancerProfile profile => _profile;

        public virtual void SetProfile(GPUInstancerProfile value)
        {
            ThrowIfDisposed();
            bool profileChanged = !ReferenceEquals(_profile, value);
            if (!profileChanged && _profileDataApplied)
                return;

            if (profileChanged)
            {
                bool wasActive = _isActive;
                ReleaseProfileRuntime(wasActive);
                _profile = value;
                name = value == null ? GetType().Name : value.displayName + " Manager";
                _profileDataApplied = false;
                SyncProfileToRuntime();
                OnProfileConfigurationApplied(value);
                if (wasActive)
                    Activate();

                return;
            }

            _profile = value;
            name = value == null ? GetType().Name : value.displayName + " Manager";
            _profileDataApplied = false;
            SyncProfileToRuntime();
        }

        protected virtual void OnProfileConfigurationReleased()
        {
            ReleaseOwnedRuntimeProfilePrototypes();
        }

        protected virtual void OnProfileConfigurationApplied(GPUInstancerProfile value) { }

        protected virtual void ApplyProfile(GPUInstancerProfile value, bool copyCollections)
        {
            if (value == null)
                return;

            List<GPUInstancerPrototype> sourcePrototypeList = value.prototypeList;
            bool isolatePrototypes = Application.isPlaying;
            bool modeChanged = _profileDataApplied &&
                _profilePrototypeIsolationMode != isolatePrototypes;
            bool sourceChanged = !ArePrototypeSourcesEqual(sourcePrototypeList);
            if (copyCollections || prototypeList == null || modeChanged || sourceChanged)
            {
                ReleaseOwnedRuntimeProfilePrototypes();
                prototypeList = CopyProfilePrototypeList(sourcePrototypeList, isolatePrototypes);
                _profilePrototypeSources = sourcePrototypeList == null
                    ? new List<GPUInstancerPrototype>()
                    : new List<GPUInstancerPrototype>(sourcePrototypeList);
                _profilePrototypeIsolationMode = isolatePrototypes;
            }
            autoSelectCamera = value.autoSelectCamera;
            useFloatingOriginHandler = value.useFloatingOriginHandler;
            applyFloatingOriginRotationAndScale = value.applyFloatingOriginRotationAndScale;
            isFrustumCulling = value.isFrustumCulling;
            isOcclusionCulling = value.isOcclusionCulling;
            minCullingDistance = value.minCullingDistance;
            layerMask = value.layerMask;
            lightProbeDisabled = value.lightProbeDisabled;
        }

        public void SyncProfileToRuntime()
        {
            if (_profile == null)
                return;

            bool modeChanged = _profileDataApplied &&
                _profilePrototypeIsolationMode != Application.isPlaying;
            if (_profileDataApplied && (modeChanged || !ArePrototypeSourcesEqual(_profile.prototypeList)))
            {
                RefreshProfileCollections();
                return;
            }
            ApplyProfile(_profile, !_profileDataApplied || modeChanged);
            _profileDataApplied = true;
        }

        public void RefreshProfileCollections()
        {
            if (_profile == null)
                return;
            bool wasActive = _isActive;
            ReleaseProfileRuntime(wasActive);
            _profileDataApplied = false;
            SyncProfileToRuntime();
            OnProfileConfigurationApplied(_profile);
            if (wasActive)
                Activate();
        }

        private void ReleaseProfileRuntime(bool wasActive)
        {
            if (wasActive)
                Deactivate();
            else if (hasRuntimeState)
            {
                GPUInstancerLoopScheduler.CancelCoroutines(this);
                ClearInstancingData();
            }

            OnProfileConfigurationReleased();
        }

        private bool hasRuntimeState => isInitialized ||
            (runtimeDataList != null && runtimeDataList.Count > 0) ||
            (runtimeDataDictionary != null && runtimeDataDictionary.Count > 0);

        protected bool IsProfileDataApplied => _profileDataApplied;

        private List<GPUInstancerPrototype> CopyProfilePrototypeList(
            List<GPUInstancerPrototype> source,
            bool isolate)
        {
            List<GPUInstancerPrototype> result = new List<GPUInstancerPrototype>();
            if (source == null)
                return result;

            if (!isolate)
            {
                result.AddRange(source);
                return result;
            }

            EnsureRuntimePrototypeMaps();
            for (int i = 0; i < source.Count; i++)
            {
                GPUInstancerPrototype original = source[i];
                if (original == null)
                {
                    result.Add(null);
                    continue;
                }

                GPUInstancerPrototype clone = CloneProfilePrototype(original);
                if (clone == null)
                {
                    _runtimePrototypeMap[original] = null;
                    Debug.LogWarning("GPU Instancer could not clone profile prototype for Play Mode: " + original, diagnosticContext);
                    result.Add(null);
                    continue;
                }

                clone.hideFlags = HideFlags.HideAndDontSave;
                _runtimePrototypeMap[original] = clone;
                _runtimePrototypeReverseMap[clone] = original;
                _ownedRuntimeProfilePrototypes.Add(clone);
                result.Add(clone);
            }
            return result;
        }

        private bool ArePrototypeSourcesEqual(List<GPUInstancerPrototype> source)
        {
            if (_profilePrototypeSources == null)
                return false;
            int count = source == null ? 0 : source.Count;
            if (_profilePrototypeSources.Count != count)
                return false;
            for (int i = 0; i < count; i++)
            {
                if (!ReferenceEquals(_profilePrototypeSources[i], source[i]))
                    return false;
            }
            return true;
        }

        private void EnsureRuntimePrototypeMaps()
        {
            if (_runtimePrototypeMap == null)
                _runtimePrototypeMap = new Dictionary<GPUInstancerPrototype, GPUInstancerPrototype>(
                    new PrototypeReferenceComparer<GPUInstancerPrototype>());
            if (_runtimePrototypeReverseMap == null)
                _runtimePrototypeReverseMap = new Dictionary<GPUInstancerPrototype, GPUInstancerPrototype>(
                    new PrototypeReferenceComparer<GPUInstancerPrototype>());
            if (_ownedRuntimeProfilePrototypes == null)
                _ownedRuntimeProfilePrototypes = new List<GPUInstancerPrototype>();
        }

        private static GPUInstancerPrototype CloneProfilePrototype(GPUInstancerPrototype source)
        {
            if (source == null)
                return null;

            GPUInstancerPrototype clone = UnityEngine.Object.Instantiate(source);
            if (clone == null)
                return null;

            clone.name = string.IsNullOrEmpty(source.name)
                ? "GPUInstancerPrototype (Runtime)"
                : source.name + " (Runtime)";

            clone.shadowLODMap = source.shadowLODMap == null
                ? null
                : (float[])source.shadowLODMap.Clone();
            clone.billboard = CloneBillboard(source.billboard);

            GPUInstancerPrefabPrototype sourcePrefab = source as GPUInstancerPrefabPrototype;
            GPUInstancerPrefabPrototype clonePrefab = clone as GPUInstancerPrefabPrototype;
            if (sourcePrefab != null && clonePrefab != null && sourcePrefab.rigidbodyData != null)
            {
                GPUInstancerPrefabPrototype.RigidbodyData data = sourcePrefab.rigidbodyData;
                clonePrefab.rigidbodyData = new GPUInstancerPrefabPrototype.RigidbodyData
                {
                    useGravity = data.useGravity,
                    angularDrag = data.angularDrag,
                    mass = data.mass,
                    constraints = data.constraints,
                    drag = data.drag,
                    isKinematic = data.isKinematic,
                    interpolation = data.interpolation
                };
            }
            return clone;
        }

        private static GPUInstancerBillboard CloneBillboard(GPUInstancerBillboard source)
        {
            if (source == null)
                return null;
            return new GPUInstancerBillboard
            {
                billboardQuality = source.billboardQuality,
                atlasResolution = source.atlasResolution,
                frameCount = source.frameCount,
                replaceLODCullWithBillboard = source.replaceLODCullWithBillboard,
                isOverridingOriginalCutoff = source.isOverridingOriginalCutoff,
                cutoffOverride = source.cutoffOverride,
                billboardBrightness = source.billboardBrightness,
                billboardDistance = source.billboardDistance,
                quadSize = source.quadSize,
                yPivotOffset = source.yPivotOffset,
                albedoAtlasTexture = source.albedoAtlasTexture,
                normalAtlasTexture = source.normalAtlasTexture,
                customBillboardInLODGroup = source.customBillboardInLODGroup,
                useCustomBillboard = source.useCustomBillboard,
                customBillboardMesh = source.customBillboardMesh,
                customBillboardMaterial = source.customBillboardMaterial,
                isBillboardShadowCasting = source.isBillboardShadowCasting,
                billboardFaceCamPos = source.billboardFaceCamPos,
                normalStrength = source.normalStrength,
                isBeingDeleted = source.isBeingDeleted
            };
        }

        public GPUInstancerPrototype ResolveRuntimePrototype(GPUInstancerPrototype prototype)
        {
            if (prototype == null)
                return null;
            if (_runtimePrototypeMap != null &&
                _runtimePrototypeMap.TryGetValue(prototype, out GPUInstancerPrototype clone))
                return clone == null ? null : clone;
            return prototype;
        }

        protected bool IsRuntimeProfilePrototype(GPUInstancerPrototype prototype)
        {
            if (prototype == null)
                return false;

            if (_profilePrototypeSources != null)
            {
                for (int i = 0; i < _profilePrototypeSources.Count; i++)
                {
                    if (ReferenceEquals(_profilePrototypeSources[i], prototype))
                        return true;
                }
            }

            if (_profile != null && _profile.prototypeList != null)
            {
                for (int i = 0; i < _profile.prototypeList.Count; i++)
                {
                    if (ReferenceEquals(_profile.prototypeList[i], prototype))
                        return true;
                }
            }

            if (_runtimePrototypeReverseMap != null &&
                _runtimePrototypeReverseMap.TryGetValue(prototype, out GPUInstancerPrototype source) &&
                source != null)
            {
                if (_profilePrototypeSources != null)
                {
                    for (int i = 0; i < _profilePrototypeSources.Count; i++)
                    {
                        if (ReferenceEquals(_profilePrototypeSources[i], source))
                            return true;
                    }
                }

                if (_profile != null && _profile.prototypeList != null)
                {
                    for (int i = 0; i < _profile.prototypeList.Count; i++)
                    {
                        if (ReferenceEquals(_profile.prototypeList[i], source))
                            return true;
                    }
                }
            }
            return false;
        }

        internal void RegisterRuntimePrototypeMapping(
            GPUInstancerPrototype source,
            GPUInstancerPrototype clone)
        {
            if (source == null || clone == null || ReferenceEquals(source, clone))
                return;
            EnsureRuntimePrototypeMaps();
            _runtimePrototypeMap[source] = clone;
            _runtimePrototypeReverseMap[clone] = source;
        }

        internal void OwnRuntimePrototype(GPUInstancerPrototype prototype)
        {
            if (prototype == null)
                return;
            EnsureRuntimePrototypeMaps();
            if (!_ownedRuntimeProfilePrototypes.Contains(prototype))
                _ownedRuntimeProfilePrototypes.Add(prototype);
            prototype.hideFlags = HideFlags.HideAndDontSave;
        }

        protected bool IsOwnedRuntimePrototype(GPUInstancerPrototype prototype)
        {
            if (prototype == null || _ownedRuntimeProfilePrototypes == null)
                return false;
            for (int i = 0; i < _ownedRuntimeProfilePrototypes.Count; i++)
            {
                if (ReferenceEquals(_ownedRuntimeProfilePrototypes[i], prototype))
                    return true;
            }
            return false;
        }

        protected bool ReleaseOwnedRuntimePrototype(GPUInstancerPrototype prototype)
        {
            if (!IsOwnedRuntimePrototype(prototype))
                return false;

            if (_ownedRuntimeProfilePrototypes != null)
            {
                for (int i = _ownedRuntimeProfilePrototypes.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_ownedRuntimeProfilePrototypes[i], prototype))
                        _ownedRuntimeProfilePrototypes.RemoveAt(i);
                }
                if (_ownedRuntimeProfilePrototypes.Count == 0)
                    _ownedRuntimeProfilePrototypes = null;
            }

            if (_runtimePrototypeMap != null)
            {
                List<GPUInstancerPrototype> aliasesToRemove = null;
                foreach (KeyValuePair<GPUInstancerPrototype, GPUInstancerPrototype> pair in _runtimePrototypeMap)
                {
                    if (ReferenceEquals(pair.Value, prototype))
                    {
                        if (aliasesToRemove == null)
                            aliasesToRemove = new List<GPUInstancerPrototype>();
                        aliasesToRemove.Add(pair.Key);
                    }
                }
                if (aliasesToRemove != null)
                {
                    for (int i = 0; i < aliasesToRemove.Count; i++)
                        _runtimePrototypeMap.Remove(aliasesToRemove[i]);
                }
            }
            if (_runtimePrototypeReverseMap != null)
                _runtimePrototypeReverseMap.Remove(prototype);

            if (prototypeList != null)
            {
                for (int i = prototypeList.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(prototypeList[i], prototype))
                        prototypeList.RemoveAt(i);
                }
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEngine.Object.DestroyImmediate(prototype);
            else
                UnityEngine.Object.Destroy(prototype);
#else
            UnityEngine.Object.Destroy(prototype);
#endif
            return true;
        }

        public bool CanResolvePrototype(GPUInstancerPrototype prototype)
        {
            GPUInstancerPrototype resolved = ResolveRuntimePrototype(prototype);
            if (resolved == null || prototypeList == null)
                return false;
            for (int i = 0; i < prototypeList.Count; i++)
            {
                if (ReferenceEquals(prototypeList[i], resolved))
                    return true;
            }
            return false;
        }

        protected bool IsPrototypeInFilter(List<GPUInstancerPrototype> filter, GPUInstancerPrototype prototype)
        {
            if (filter == null)
                return true;
            GPUInstancerPrototype resolved = ResolveRuntimePrototype(prototype);
            for (int i = 0; i < filter.Count; i++)
            {
                if (ReferenceEquals(ResolveRuntimePrototype(filter[i]), resolved))
                    return true;
            }
            return false;
        }

        protected void ReleaseOwnedRuntimeProfilePrototypes()
        {
            List<GPUInstancerPrototype> owned = _ownedRuntimeProfilePrototypes;
            if (_runtimePrototypeMap != null)
                _runtimePrototypeMap.Clear();

            if (_runtimePrototypeReverseMap != null)
                _runtimePrototypeReverseMap.Clear();

            if (owned == null || owned.Count == 0)
                return;

            _ownedRuntimeProfilePrototypes = null;

            if (prototypeList != null)
            {
                for (int i = prototypeList.Count - 1; i >= 0; i--)
                {
                    for (int j = 0; j < owned.Count; j++)
                    {
                        if (ReferenceEquals(prototypeList[i], owned[j]))
                        {
                            prototypeList.RemoveAt(i);
                            break;
                        }
                    }
                }
            }

            for (int i = 0; i < owned.Count; i++)
            {
                GPUInstancerPrototype clone = owned[i];
                if (clone == null)
                    continue;
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    UnityEngine.Object.DestroyImmediate(clone);
                else
                    UnityEngine.Object.Destroy(clone);
#else
                UnityEngine.Object.Destroy(clone);
#endif
            }
        }

        private bool _isActive;
        internal bool runtimeDataInitializedDuringLastActivate;
        public bool isActive => _isActive;
        public bool isActiveAndEnabled => _isActive && !isDisposed;

        public List<GPUInstancerPrototype> prototypeList;

        public bool autoSelectCamera = true;
        public GPUInstancerCameraData cameraData = new GPUInstancerCameraData(null);

#if GPUI_FRAXINUS
        private Dictionary<int, GPUInstancerCameraData> _fraxinusCameraData;
#endif

        public bool useFloatingOriginHandler = false;
        public bool applyFloatingOriginRotationAndScale = false;
        public Transform floatingOriginTransform;
        public GPUInstancerFloatingOriginHandler floatingOriginHandler;

        public List<GPUInstancerRuntimeData> runtimeDataList;
        public Bounds instancingBounds;

        public bool isFrustumCulling = true;
        public bool isOcclusionCulling = true;
        public float minCullingDistance = 0;

        protected GPUInstancerSpatialPartitioningData<GPUInstancerCell> spData;

        public static bool showRenderedAmount;

        protected static ComputeShader _cameraComputeShader;
        protected static ComputeShader _cameraComputeShaderVR;
        protected static int[] _cameraComputeKernelIDs;
        protected static ComputeShader _visibilityComputeShader;
        protected static int[] _instanceVisibilityComputeKernelIDs;
        protected static ComputeShader _bufferToTextureComputeShader;
        protected static int _bufferToTextureComputeKernelID;
        protected static int _bufferToTextureCrossFadeComputeKernelID;
        protected static ComputeShader _argsBufferComputeShader;
        protected static int _argsBufferDoubleInstanceCountComputeKernelID;

#if UNITY_EDITOR
        public List<GPUInstancerPrototype> selectedPrototypeList;
        public GPUInstancerLoopScheduler.EditorLoopState editorLoopState;
        public bool isPrototypeTextMode = false;

        public bool showSceneSettingsBox = true;
        public bool showPrototypeBox = true;
        public bool showAdvancedBox = false;
        public bool showHelpText = false;
        public bool showDebugBox = true;
        public bool showGlobalValuesBox = true;
        public bool showRegisteredPrefabsBox = true;
        public bool showPrototypesBox = true;

        public bool keepSimulationLive = false;
        public bool updateSimulation = true;
#endif

        public class GPUIThreadData
        {
            public Thread thread;
            public object parameter;
        }
        public static int maxThreads = 3;
        public readonly List<Thread> activeThreads = new List<Thread>();
        public readonly Queue<GPUIThreadData> threadStartQueue = new Queue<GPUIThreadData>();
        public readonly Queue<Action> threadQueue = new Queue<Action>();

        private GameObject _treeProxyParent;
        private Dictionary<GameObject, Transform> _treeProxyList;
        private int _lastTreePositionUpdate = -1;

        public static int lastDrawCallFrame;
        public static float lastDrawCallTime;
        public static float timeSinceLastDrawCall;

        protected static Vector4 _windVector = Vector4.zero;

        protected bool isInitial = true;

        public bool isInitialized = false;

#if UNITY_EDITOR && UNITY_2017_2_OR_NEWER
        public PlayModeStateChange playModeState;
#endif
        public Dictionary<GPUInstancerPrototype, GPUInstancerRuntimeData> runtimeDataDictionary;

        public LayerMask layerMask = ~0;
        public bool lightProbeDisabled = false;


        public virtual void Initialize()
        {
            ThrowIfDisposed();
            _deactivationCompleted = false;

            SyncProfileToRuntime();

            if (System.Environment.CommandLine.Contains("-nographics"))
            {
                _isActive = false;
                return;
            }

            GPUInstancerConstants.projectSettings.SetDefaultBindings();
            GPUInstancerUtility.SetPlatformDependentVariables();

#if UNITY_EDITOR
            if (!Application.isPlaying)
                CheckPrototypeChanges();
#endif
            if (s_supportsComputeShaders)
            {
                if (_visibilityComputeShader == null || _instanceVisibilityComputeKernelIDs == null || _cameraComputeKernelIDs == null)
                {
                    switch (GPUInstancerUtility.matrixHandlingType)
                    {
                        case GPUIMatrixHandlingType.MatrixAppend:
                            _visibilityComputeShader = (ComputeShader)Resources.Load(GPUInstancerConstants.VISIBILITY_COMPUTE_RESOURCE_PATH_VULKAN);
                            GPUInstancerConstants.DETAIL_STORE_INSTANCE_DATA = true;
                            GPUInstancerConstants.COMPUTE_MAX_LOD_BUFFER = 2;
                            break;
                        case GPUIMatrixHandlingType.CopyToTexture:
                            _visibilityComputeShader = (ComputeShader)Resources.Load(GPUInstancerConstants.VISIBILITY_COMPUTE_RESOURCE_PATH);
                            _bufferToTextureComputeShader = (ComputeShader)Resources.Load(GPUInstancerConstants.BUFFER_TO_TEXTURE_COMPUTE_RESOURCE_PATH);
                            _bufferToTextureComputeKernelID = _bufferToTextureComputeShader.FindKernel(GPUInstancerConstants.BUFFER_TO_TEXTURE_KERNEL);
                            _bufferToTextureCrossFadeComputeKernelID = _bufferToTextureComputeShader.FindKernel(GPUInstancerConstants.BUFFER_TO_TEXTURE_CROSSFADE_KERNEL);
                            break;
                        default:
                            _visibilityComputeShader = (ComputeShader)Resources.Load(GPUInstancerConstants.VISIBILITY_COMPUTE_RESOURCE_PATH);
                            break;
                    }

                    _instanceVisibilityComputeKernelIDs = new int[GPUInstancerConstants.VISIBILITY_COMPUTE_KERNELS.Length];
                    for (int i = 0; i < _instanceVisibilityComputeKernelIDs.Length; i++)
                        _instanceVisibilityComputeKernelIDs[i] = _visibilityComputeShader.FindKernel(GPUInstancerConstants.VISIBILITY_COMPUTE_KERNELS[i]);
                    GPUInstancerConstants.TEXTURE_MAX_SIZE = SystemInfo.maxTextureSize;

                    _cameraComputeShader = (ComputeShader)Resources.Load(GPUInstancerConstants.CAMERA_COMPUTE_RESOURCE_PATH);
                    _cameraComputeShaderVR = (ComputeShader)Resources.Load(GPUInstancerConstants.CAMERA_VR_COMPUTE_RESOURCE_PATH);
                    _cameraComputeKernelIDs = new int[GPUInstancerConstants.CAMERA_COMPUTE_KERNELS.Length];
                    for (int i = 0; i < _cameraComputeKernelIDs.Length; i++)
                        _cameraComputeKernelIDs[i] = _cameraComputeShader.FindKernel(GPUInstancerConstants.CAMERA_COMPUTE_KERNELS[i]);

                    _argsBufferComputeShader = Resources.Load<ComputeShader>(GPUInstancerConstants.ARGS_BUFFER_COMPUTE_RESOURCE_PATH);
                    _argsBufferDoubleInstanceCountComputeKernelID = _argsBufferComputeShader.FindKernel(GPUInstancerConstants.ARGS_BUFFER_DOUBLE_INSTANCE_COUNT_KERNEL);
                }

                GPUInstancerConstants.SetupComputeRuntimeModification();
                GPUInstancerConstants.SetupComputeSetDataPartial();
            }
            else if (Application.isPlaying)
            {
                Debug.LogError("Target Graphics API does not support Compute Shaders. Please refer to Minimum Requirements on GPUInstancer/ReadMe.txt for detailed information.");
                _isActive = false;
            }

            showRenderedAmount = false;

            InitializeCameraData();

#if UNITY_EDITOR && UNITY_2017_2_OR_NEWER
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
#endif
        }

        protected virtual void StartRuntime()
        {
#if UNITY_EDITOR
            if (_instanceVisibilityComputeKernelIDs == null || _cameraComputeKernelIDs == null)
                Initialize();
#endif
        }

        public virtual void Activate()
        {
            ThrowIfDisposed();
            _deactivationCompleted = false;
            SyncProfileToRuntime();
            runtimeDataInitializedDuringLastActivate = false;
            if (cameraData == null)
                cameraData = new GPUInstancerCameraData(null);
#if UNITY_EDITOR
            if (_instanceVisibilityComputeKernelIDs == null || _cameraComputeKernelIDs == null)
                Initialize();
            if (editorLoopState == null)
                editorLoopState = new GPUInstancerLoopScheduler.EditorLoopState(this);
#endif

            _isActive = true;

            if (!Application.isPlaying)
                return;

            if (cameraData.mainCamera == null)
            {
                InitializeCameraData();
                if (cameraData.mainCamera == null)
                    Debug.LogWarning(GPUInstancerConstants.ERRORTEXT_cameraNotFound);
            }

            if (s_supportsComputeShaders)
            {
                if (GPUInstancerConstants.projectSettings == null || GPUInstancerConstants.projectSettings.shaderBindings == null)
                    Debug.LogWarning("No shader bindings file was supplied. Instancing will terminate!");

                if (runtimeDataList == null || runtimeDataList.Count == 0)
                {
                    runtimeDataInitializedDuringLastActivate = true;
                    InitializeRuntimeDataAndBuffers();
                }
                isInitial = true;
            }

            HandleFloatingOrigin();
            StartRuntime();
        }

        public virtual void Tick()
        {
            ThrowIfDisposed();
            SyncProfileToRuntime();
            ClearCompletedThreads();
            while (true)
            {
                GPUIThreadData threadData = null;
                lock (threadStartQueue)
                {
                    if (threadStartQueue.Count == 0 || activeThreads.Count >= maxThreads)
                        break;
                    threadData = threadStartQueue.Dequeue();
                }
                if (threadData == null || threadData.thread == null)
                    continue;
                threadData.thread.Start(threadData.parameter);
                activeThreads.Add(threadData.thread);
            }
            Action action = null;
            lock (threadQueue)
            {
                if (threadQueue.Count > 0)
                    action = threadQueue.Dequeue();
            }
            if (action != null)
                action.Invoke();

            if (Application.isPlaying && _treeProxyParent &&
                _lastTreePositionUpdate != Time.frameCount &&
                cameraData != null && cameraData.mainCamera != null)
            {
                _treeProxyParent.transform.position = cameraData.mainCamera.transform.position;
                _treeProxyParent.transform.rotation = cameraData.mainCamera.transform.rotation;
                _lastTreePositionUpdate = Time.frameCount;
            }
        }

        public virtual void PrepareFrame()
        {
            ThrowIfDisposed();
            SyncProfileToRuntime();
#if UNITY_EDITOR
            if (!Application.isPlaying)
                CheckPrototypeChanges();
            else
            {
#endif
#if GPUI_FRAXINUS
                if (Fraxinus.GPUBatcher.GPUInstancerFraxinusRenderBridge.isActive)
                    return;
#endif
                if (cameraData != null && cameraData.mainCamera != null)
                {
                    UpdateTreeMPB();
                    UpdateBuffers(cameraData);
                }
#if UNITY_EDITOR
            }
#endif
        }

        public virtual void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                if (_deactivationCompleted)
                {
                    ReleaseTreeProxyContext();
                    ReleaseOwnedRuntimeProfilePrototypes();
                    return;
                }

                try
                {
                    DeactivateCore();
                }
                finally
                {
                    ReleaseTreeProxyContext();
                    ReleaseOwnedRuntimeProfilePrototypes();
                }
            }
            finally
            {
                GPUInstancerLoopScheduler.CancelCoroutines(this);
#if UNITY_EDITOR
                if (editorLoopState != null)
                    editorLoopState.ClearEditorUpdates();
                editorLoopState = null;

#if UNITY_2017_2_OR_NEWER
                EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
#endif
#endif
                if (floatingOriginHandler != null && floatingOriginHandler.gPUIManagers != null)
                    floatingOriginHandler.gPUIManagers.Remove(this);
                floatingOriginHandler = null;
                floatingOriginTransform = null;
            }
        }

        protected void ThrowIfDisposed()
        {
            if (isDisposed)
                throw new ObjectDisposedException(name ?? GetType().Name);
        }

        public virtual void ResetConfiguration()
        {
            SyncProfileToRuntime();
            GPUInstancerConstants.projectSettings.SetDefaultBindings();
#if UNITY_EDITOR
            CheckPrototypeChanges();
#endif
        }

        public void Deactivate()
        {
            if (_isDisposed)
                return;

            DeactivateCore();
        }

        protected virtual void DeactivateCore()
        {
            GPUInstancerLoopScheduler.CancelCoroutines(this);

            _isActive = false;
#if GPUI_FRAXINUS
            if (_fraxinusCameraData != null)
                _fraxinusCameraData.Clear();
#endif

            if (runtimeDataList != null)
            {
                foreach (var rd in runtimeDataList)
                {
                    if (rd != null)
                        rd.DisableLODFade();
                }
            }

            ClearInstancingData();
#if UNITY_EDITOR
            if (editorLoopState != null)
            {
                editorLoopState.ClearEditorUpdates();
                editorLoopState = null;
            }
#endif

            if (floatingOriginHandler != null && floatingOriginHandler.gPUIManagers != null && floatingOriginHandler.gPUIManagers.Contains(this))
            {
                floatingOriginHandler.gPUIManagers.Remove(this);
            }

            _deactivationCompleted = true;
        }



        public virtual void ClearInstancingData()
        {
            ReleaseTreeProxyContext();
            if (runtimeDataList != null)
            {
                foreach (var rd in runtimeDataList)
                {
                    if (rd == null)
                        continue;
                    foreach (var l in rd.instanceLODs)
                    {
                        if (l == null)
                            continue;
                        foreach (var r in l.renderers)
                        {
                            if (r == null)
                                continue;
                            foreach (var m in r.materials)
                            {
                                if (m == null)
                                    continue;
                                if (m.name.EndsWith("_GPUITemp"))
                                    GPUInstancerUtility.DestroyObject(m);
                            }
                        }
                    }
                }
            }

            GPUInstancerUtility.ReleaseInstanceBuffers(runtimeDataList);
            GPUInstancerUtility.ReleaseSPBuffers(spData);
            if (runtimeDataList != null)
                runtimeDataList.Clear();
            if (runtimeDataDictionary != null)
                runtimeDataDictionary.Clear();
            spData = null;
            lock (threadStartQueue)
                threadStartQueue.Clear();
            lock (threadQueue)
                threadQueue.Clear();
            isInitialized = false;
        }

        public virtual void GeneratePrototypes(bool forceNew = false)
        {
            SyncProfileToRuntime();
            if (profile != null && Application.isPlaying &&
                (forceNew || prototypeList == null))
                ReleaseOwnedRuntimeProfilePrototypes();
            ClearInstancingData();

            if (forceNew || prototypeList == null)
                prototypeList = new List<GPUInstancerPrototype>();
            else
                prototypeList.RemoveAll(p => p == null);

#if UNITY_EDITOR
            if (_profile != null && !Application.isPlaying)
                SyncProfilePrototypeList();
#endif

            GPUInstancerConstants.projectSettings.SetDefaultBindings();
        }

#if UNITY_EDITOR
        protected void SyncProfilePrototypeList()
        {
            if (_profile == null || Application.isPlaying)
                return;
            _profile.prototypeList = prototypeList == null
                ? new List<GPUInstancerPrototype>()
                : new List<GPUInstancerPrototype>(prototypeList);
        }
#endif

#if UNITY_EDITOR
        public virtual void CheckPrototypeChanges()
        {
            SyncProfileToRuntime();
            GPUInstancerConstants.projectSettings.SetDefaultBindings();

            if (prototypeList == null)
                GeneratePrototypes();
            else
                prototypeList.RemoveAll(p => p == null);

            if (GPUInstancerConstants.projectSettings != null && GPUInstancerConstants.projectSettings.shaderBindings != null)
            {
                GPUInstancerConstants.projectSettings.shaderBindings.ClearEmptyShaderInstances();
                foreach (GPUInstancerPrototype prototype in prototypeList)
                {
                    if (prototype.prefabObject != null)
                    {
                        GPUInstancerUtility.GenerateInstancedShadersForGameObject(prototype);
                        if (string.IsNullOrEmpty(prototype.warningText))
                        {
                            if (prototype.prefabObject.GetComponentInChildren<MeshRenderer>() == null)
                            {
                                prototype.warningText = "Prefab object does not contain any Mesh Renderers.";
                            }
                        }
                    }
                    else
                    {
                        if (Shader.Find(GPUInstancerConstants.SHADER_GPUI_FOLIAGE_URP) != null)
                            GPUInstancerConstants.projectSettings.AddShaderVariantToCollection(GPUInstancerConstants.SHADER_GPUI_FOLIAGE_URP);
                    }
                }
            }
            if (GPUInstancerConstants.projectSettings != null && GPUInstancerConstants.projectSettings.billboardAtlasBindings != null)
            {
                GPUInstancerConstants.projectSettings.billboardAtlasBindings.ClearEmptyBillboardAtlases();
            }
        }
#endif
        public virtual void InitializeRuntimeDataAndBuffers(bool forceNew = true)
        {
            ThrowIfDisposed();
            _deactivationCompleted = false;
            GPUInstancerUtility.SetPlatformDependentVariables();
            if (forceNew || !isInitialized)
            {
                instancingBounds = new Bounds(Vector3.zero, Vector3.one * GPUInstancerConstants.projectSettings.instancingBoundsSize);

                GPUInstancerUtility.ReleaseInstanceBuffers(runtimeDataList);
                GPUInstancerUtility.ReleaseSPBuffers(spData);
                if (runtimeDataList != null)
                    runtimeDataList.Clear();
                else
                    runtimeDataList = new List<GPUInstancerRuntimeData>();

                if (runtimeDataDictionary != null)
                    runtimeDataDictionary.Clear();
                else
                    runtimeDataDictionary = new Dictionary<GPUInstancerPrototype, GPUInstancerRuntimeData>();

                if (prototypeList == null)
                    prototypeList = new List<GPUInstancerPrototype>();
            }
        }

        public virtual void InitializeSpatialPartitioning()
        {

        }

        public virtual void UpdateSpatialPartitioningCells(GPUInstancerCameraData renderingCameraData)
        {

        }

        public virtual void DeletePrototype(GPUInstancerPrototype prototype, bool removeSO = true)
        {
            prototypeList.Remove(prototype);

            if (removeSO && prototype.useGeneratedBillboard && prototype.billboard != null)
            {
                if (GPUInstancerConstants.projectSettings.billboardAtlasBindings.DeleteBillboardTextures(prototype, false))
                    prototype.billboard = null;
            }
        }

        public virtual void RemoveInstancesInsideBounds(Bounds bounds, float offset, List<GPUInstancerPrototype> prototypeFilter = null)
        {
            if (runtimeDataList != null)
            {
                foreach (GPUInstancerRuntimeData rd in runtimeDataList)
                {
                    if (rd == null)
                        continue;
                    if (!IsPrototypeInFilter(prototypeFilter, rd.prototype))
                        continue;
                    GPUInstancerUtility.RemoveInstancesInsideBounds(rd.transformationMatrixVisibilityBuffer, bounds.center, bounds.extents, offset);
                }
            }
        }
        public virtual void RemoveInstancesInsideCollider(Collider collider, float offset, List<GPUInstancerPrototype> prototypeFilter = null)
        {
            if (collider == null)
                return;
            if (runtimeDataList != null)
            {
                foreach (GPUInstancerRuntimeData rd in runtimeDataList)
                {
                    if (rd == null)
                        continue;
                    if (!IsPrototypeInFilter(prototypeFilter, rd.prototype))
                        continue;
                    if (collider is BoxCollider)
                        GPUInstancerUtility.RemoveInstancesInsideBoxCollider(rd.transformationMatrixVisibilityBuffer, (BoxCollider)collider, offset);
                    else if (collider is SphereCollider)
                        GPUInstancerUtility.RemoveInstancesInsideSphereCollider(rd.transformationMatrixVisibilityBuffer, (SphereCollider)collider, offset);
                    else if (collider is CapsuleCollider)
                        GPUInstancerUtility.RemoveInstancesInsideCapsuleCollider(rd.transformationMatrixVisibilityBuffer, (CapsuleCollider)collider, offset);
                    else
                        GPUInstancerUtility.RemoveInstancesInsideBounds(rd.transformationMatrixVisibilityBuffer, collider.bounds.center, collider.bounds.extents, offset);
                }
            }
        }

        public virtual void SetGlobalPositionOffset(Vector3 offsetPosition)
        {
        }

        public virtual void SetGlobalMatrixOffset(Matrix4x4 offsetMatrix)
        {
        }


        public void ClearCompletedThreads()
        {
            if (activeThreads.Count > 0)
            {
                for (int i = activeThreads.Count - 1; i >= 0; i--)
                {
                    Thread thread = activeThreads[i];
                    if (thread == null || !thread.IsAlive)
                        activeThreads.RemoveAt(i);
                }
            }
        }

        public void InitializeCameraData()
        {
            if (cameraData == null)
                cameraData = new GPUInstancerCameraData(null);
            if (autoSelectCamera || cameraData.mainCamera == null)
                cameraData.SetCamera(Camera.main);
            else
                cameraData.CalculateHalfAngle();
        }

        public void HandleFloatingOrigin()
        {
            if (useFloatingOriginHandler && floatingOriginTransform != null)
            {
                if (floatingOriginHandler == null)
                {
                    floatingOriginHandler = floatingOriginTransform.gameObject.GetComponent<GPUInstancerFloatingOriginHandler>();
                    if (floatingOriginHandler == null)
                        floatingOriginHandler = floatingOriginTransform.gameObject.AddComponent<GPUInstancerFloatingOriginHandler>();
                }
                floatingOriginHandler.applyRotationAndScale = applyFloatingOriginRotationAndScale;
                if (floatingOriginHandler.gPUIManagers == null)
                    floatingOriginHandler.gPUIManagers = new List<GPUInstancerManager>();
                if (!floatingOriginHandler.gPUIManagers.Contains(this))
                    floatingOriginHandler.gPUIManagers.Add(this);
            }
        }

#if GPUI_FRAXINUS
        internal void RenderForCamera(Camera renderingCamera)
        {
            if (renderingCamera == null || !isActiveAndEnabled)
                return;

            if (cameraData != null && cameraData.renderOnlySelectedCamera &&
                cameraData.mainCamera != renderingCamera)
                return;

            if (_fraxinusCameraData == null)
                _fraxinusCameraData = new Dictionary<int, GPUInstancerCameraData>();

            int cameraId = renderingCamera.GetInstanceID();
            if (!_fraxinusCameraData.TryGetValue(cameraId, out GPUInstancerCameraData renderingCameraData)
                || renderingCameraData.mainCamera != renderingCamera)
            {
                renderingCameraData = new GPUInstancerCameraData(renderingCamera)
                {
                    renderOnlySelectedCamera = true
                };
                _fraxinusCameraData[cameraId] = renderingCameraData;
            }

            if (_treeProxyParent && _lastTreePositionUpdate != Time.frameCount)
            {
                _treeProxyParent.transform.position = renderingCamera.transform.position;
                _treeProxyParent.transform.rotation = renderingCamera.transform.rotation;
                _lastTreePositionUpdate = Time.frameCount;
            }

            UpdateTreeMPB();
            UpdateBuffers(renderingCameraData);
        }
#endif

        public void UpdateBuffers(GPUInstancerCameraData renderingCameraData)
        {
            if (renderingCameraData != null && renderingCameraData.mainCamera != null && s_supportsComputeShaders)
            {
                renderingCameraData.CalculateCameraData(isOcclusionCulling);

                instancingBounds.center = renderingCameraData.mainCamera.transform.position;

                if (lastDrawCallFrame != Time.frameCount)
                {
                    lastDrawCallFrame = Time.frameCount;
                    timeSinceLastDrawCall = Time.realtimeSinceStartup - lastDrawCallTime;
                    lastDrawCallTime = Time.realtimeSinceStartup;
                }

                UpdateSpatialPartitioningCells(renderingCameraData);

                GPUInstancerUtility.UpdateGPUBuffers(GPUInstancerConstants.projectSettings.IsUseBothEyesVRCulling() ? _cameraComputeShaderVR : _cameraComputeShader, _cameraComputeKernelIDs, _visibilityComputeShader, _instanceVisibilityComputeKernelIDs, runtimeDataList, renderingCameraData, isFrustumCulling,
                    isOcclusionCulling, showRenderedAmount, isInitial);
                isInitial = false;

                if (GPUInstancerUtility.matrixHandlingType == GPUIMatrixHandlingType.CopyToTexture)
                    GPUInstancerUtility.DispatchBufferToTexture(runtimeDataList, _bufferToTextureComputeShader, _bufferToTextureComputeKernelID, _bufferToTextureCrossFadeComputeKernelID);

#if GPUI_XR
                if (GPUInstancerProjectSettings.IsVREnabled() && UnityEngine.XR.XRSettings.stereoRenderingMode == UnityEngine.XR.XRSettings.StereoRenderingMode.SinglePassInstanced)
                {
                    for (int i = 0; i < runtimeDataList.Count; i++)
                    {
                        GPUInstancerRuntimeData runtimeData = runtimeDataList[i];

                        if (runtimeData == null || runtimeData.argsBuffer == null || runtimeData.argsBuffer.count == 0)
                            continue;

                        int count = runtimeData.argsBuffer.count / 5;
                        _argsBufferComputeShader.SetBuffer(_argsBufferDoubleInstanceCountComputeKernelID, GPUInstancerConstants.VisibilityKernelPoperties.ARGS_BUFFER, runtimeData.argsBuffer);
                        _argsBufferComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.COUNT, count);
                        GPUInstancerUtility.DispatchCompute(_argsBufferComputeShader, _argsBufferDoubleInstanceCountComputeKernelID,
                            Mathf.CeilToInt(count / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
                    }
                }
#endif

                GPUInstancerUtility.GPUIDrawMeshInstancedIndirect(runtimeDataList, instancingBounds, renderingCameraData, layerMask, lightProbeDisabled);
            }
        }

        public void SetCamera(Camera camera)
        {
            if (cameraData == null)
                cameraData = new GPUInstancerCameraData(camera);
            else
                cameraData.SetCamera(camera);
        }

        public ComputeBuffer GetTransformDataBuffer(GPUInstancerPrototype prototype)
        {
            GPUInstancerRuntimeData runtimeData = GetRuntimeData(prototype);
            if (runtimeData != null)
                return runtimeData.transformationMatrixVisibilityBuffer;
            return null;
        }

        public void SetLODBias(float newLODBias, List<GPUInstancerPrototype> prototypeFilter)
        {
            if (runtimeDataList == null || newLODBias <= 0)
                return;

            foreach (GPUInstancerRuntimeData runtimeData in runtimeDataList)
            {
                if (runtimeData == null || runtimeData.lodBiasApplied <= 0 || runtimeData.lodBiasApplied == newLODBias || runtimeData.instanceLODs == null
                    || runtimeData.instanceLODs.Count == 0 || runtimeData.lodSizes == null || runtimeData.lodSizes.Length < 2 ||
                    !IsPrototypeInFilter(prototypeFilter, runtimeData.prototype))
                    continue;

                for (int i = 0; i < runtimeData.instanceLODs.Count; i++)
                {
                    int lodSizeIndex = i < 4 ? i * 4 : (i - 4) * 4 + 1;
                    if (lodSizeIndex < 0 || lodSizeIndex >= runtimeData.lodSizes.Length)
                        continue;
                    if (i < 4)
                        runtimeData.lodSizes[i * 4] *= runtimeData.lodBiasApplied / newLODBias;
                    else
                        runtimeData.lodSizes[(i - 4) * 4 + 1] *= runtimeData.lodBiasApplied / newLODBias;
                }
                runtimeData.lodBiasApplied = newLODBias;
            }
        }

        public void UpdateTreeMPB()
        {
            if (_treeProxyList != null && _treeProxyList.Count > 0)
            {
                GPUInstancerPrototypeLOD rdLOD;
                GPUInstancerRenderer rdRenderer;
                MeshRenderer meshRenderer;
                Transform proxyTransform;
                if (runtimeDataList == null)
                    return;
                foreach (GPUInstancerRuntimeData runtimeData in runtimeDataList)
                {
                    if (runtimeData == null || runtimeData.prototype == null || runtimeData.instanceLODs == null)
                        continue;
                    if (runtimeData.bufferSize == 0)
                        continue;

                    if (runtimeData.prototype.treeType != GPUInstancerTreeType.SpeedTree && runtimeData.prototype.treeType != GPUInstancerTreeType.SpeedTree8)
                        continue;

                    if (runtimeData.prototype.prefabObject == null ||
                        !_treeProxyList.TryGetValue(runtimeData.prototype.prefabObject, out proxyTransform))
                        continue;

                    if (!proxyTransform)
                        continue;

                    for (int lod = 0; lod < runtimeData.instanceLODs.Count; lod++)
                    {
                        if (proxyTransform.childCount <= lod)
                            continue;

                        rdLOD = runtimeData.instanceLODs[lod];
                        if (rdLOD == null || rdLOD.renderers == null)
                            continue;
                        meshRenderer = proxyTransform.GetChild(lod).GetComponent<MeshRenderer>();
                        if (meshRenderer == null)
                            continue;

                        for (int r = 0; r < rdLOD.renderers.Count; r++)
                        {
                            rdRenderer = rdLOD.renderers[r];
                            if (rdRenderer == null || rdRenderer.mpb == null)
                                continue;

                            meshRenderer.GetPropertyBlock(rdRenderer.mpb);
                            if (rdRenderer.shadowMPB != null)
                                meshRenderer.GetPropertyBlock(rdRenderer.shadowMPB);
                        }
                    }

                    GPUInstancerUtility.SetAppendBuffers(runtimeData);
                }
            }
        }

        private void AddTreeProxyForRuntime(GPUInstancerPrototype treePrototype, GPUInstancerRuntimeData runtimeData)
        {
            if (treePrototype == null || runtimeData == null || treePrototype.prefabObject == null)
                return;

            switch (treePrototype.treeType)
            {
                case GPUInstancerTreeType.SpeedTree:
                case GPUInstancerTreeType.SpeedTree8:

                    if (_treeProxyParent == null)
                    {
                        _treeProxyParent = new GameObject("GPUI Tree Manager Proxy");
                        _treeProxyParent.hideFlags = HideFlags.HideAndDontSave;
                        _treeProxyList = null;
                        _lastTreePositionUpdate = -1;
                    }

                    if (_treeProxyList == null)
                    {
                        _treeProxyList = new Dictionary<GameObject, Transform>(); // Dict[TreePrefab, TreeProxyGO]
                    }
                    else if (_treeProxyList.ContainsKey(treePrototype.prefabObject))
                    {
                        Transform existingProxy = _treeProxyList[treePrototype.prefabObject];
                        if (existingProxy == null || existingProxy.childCount == 0)
                        {
                            _treeProxyList.Remove(treePrototype.prefabObject);
                            if (existingProxy != null)
                                DestroyTreeProxyObject(existingProxy.gameObject);
                        }
                        else
                            return;
                    }

                    Mesh treeProxyMesh = new Mesh();
                    treeProxyMesh.name = "TreeProxyMesh";
                    treeProxyMesh.hideFlags = HideFlags.HideAndDontSave;

                    GameObject treeProxyObjectParent = new GameObject(_treeProxyList.Count + "_" + treePrototype.name);
                    treeProxyObjectParent.hideFlags = HideFlags.HideAndDontSave;
                    treeProxyObjectParent.transform.SetParent(_treeProxyParent.transform);
                    treeProxyObjectParent.transform.localPosition = Vector3.zero;
                    treeProxyObjectParent.transform.localRotation = Quaternion.identity;
                    _treeProxyList.Add(treePrototype.prefabObject, treeProxyObjectParent.transform);

                    bool createdTreeProxy = false;
                    if (treePrototype.prefabObject.GetComponent<LODGroup>() != null)
                    {
                        LOD[] speedTreeLODs = treePrototype.prefabObject.GetComponent<LODGroup>().GetLODs();
                        if (speedTreeLODs != null)
                        {
                            for (int lod = 0; lod < speedTreeLODs.Length; lod++)
                            {
                                if (speedTreeLODs[lod].renderers == null || speedTreeLODs[lod].renderers.Length == 0 ||
                                    speedTreeLODs[lod].renderers[0] == null)
                                    continue;
                                if (speedTreeLODs[lod].renderers[0].GetComponent<BillboardRenderer>())
                                    continue;

                                Material proxyMaterial = CreateTreeProxyMaterial();
                                if (proxyMaterial == null)
                                    continue;
                                Material[] treeProxyMaterials = new Material[1] { proxyMaterial };
                                if (TryInstantiateTreeProxyObject(speedTreeLODs[lod].renderers[0].gameObject,
                                    treeProxyObjectParent, treeProxyMaterials, treeProxyMesh, !createdTreeProxy))
                                    createdTreeProxy = true;
                                else
                                    DestroyTreeProxyObject(proxyMaterial);
                            }
                        }
                    }
                    else
                    {
                        Material proxyMaterial = CreateTreeProxyMaterial();
                        if (proxyMaterial != null)
                        {
                            Material[] treeProxyMaterials = new Material[1] { proxyMaterial };
                            if (TryInstantiateTreeProxyObject(treePrototype.prefabObject,
                                treeProxyObjectParent, treeProxyMaterials, treeProxyMesh, true))
                                createdTreeProxy = true;
                            else
                                DestroyTreeProxyObject(proxyMaterial);
                        }
                    }
                    if (!createdTreeProxy)
                    {
                        _treeProxyList.Remove(treePrototype.prefabObject);
                        DestroyTreeProxyObject(treeProxyObjectParent);
                        DestroyTreeProxyObject(treeProxyMesh);
                    }
                    break;

                case GPUInstancerTreeType.TreeCreatorTree:

                    Shader.SetGlobalVector("_Wind", GetWindVector());
                    break;
            }
        }

        public static void AddTreeProxy(GPUInstancerPrototype treePrototype, GPUInstancerRuntimeData runtimeData)
        {
            if (treePrototype == null || runtimeData == null || treePrototype.prefabObject == null)
                return;

            if (runtimeData.ownerManager != null)
                runtimeData.ownerManager.AddTreeProxyForRuntime(treePrototype, runtimeData);
        }

        private static Material CreateTreeProxyMaterial()
        {
            Shader treeProxyShader = Shader.Find(GPUInstancerConstants.SHADER_GPUI_TREE_PROXY);
            if (treeProxyShader == null)
            {
                Debug.LogWarning("GPU Instancer could not find the tree proxy shader; tree proxy creation was skipped.");
                return null;
            }
            Material material = new Material(treeProxyShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            return material;
        }

        private static bool TryInstantiateTreeProxyObject(GameObject treePrefab, GameObject proxyObjectParent, Material[] proxyMaterials, Mesh proxyMesh, bool setBounds)
        {
            if (treePrefab == null || proxyObjectParent == null || proxyMaterials == null ||
                proxyMaterials.Length == 0 || proxyMesh == null)
                return false;

            GameObject treeProxyObject = null;
            bool succeeded = false;
            try
            {
                treeProxyObject = UnityEngine.Object.Instantiate(treePrefab, proxyObjectParent.transform);
                if (treeProxyObject == null)
                    return false;
                treeProxyObject.name = treePrefab.name;

                MeshFilter treeProxyObjectMF = treeProxyObject.GetComponent<MeshFilter>();
                MeshRenderer treeProxyObjectMR = treeProxyObject.GetComponent<MeshRenderer>();
                if (treeProxyObjectMF == null || treeProxyObjectMR == null ||
                    (setBounds && treeProxyObjectMF.sharedMesh == null))
                    return false;

                Material[] sourceMaterials = treeProxyObjectMR.sharedMaterials;
                if (sourceMaterials == null || sourceMaterials.Length == 0)
                    return false;

                if (setBounds)
                    proxyMesh.bounds = treeProxyObjectMF.sharedMesh.bounds;

                treeProxyObjectMR.shadowCastingMode = ShadowCastingMode.Off;
                treeProxyObjectMR.receiveShadows = false;
                treeProxyObjectMR.lightProbeUsage = LightProbeUsage.Off;

                bool copiedMaterial = false;
                int materialCount = Mathf.Min(proxyMaterials.Length, sourceMaterials.Length);
                for (int i = 0; i < materialCount; i++)
                {
                    if (proxyMaterials[i] == null || sourceMaterials[i] == null ||
                        proxyMaterials[i].shader == null || sourceMaterials[i].shader == null)
                        continue;
                    proxyMaterials[i].CopyPropertiesFromMaterial(sourceMaterials[i]);
                    proxyMaterials[i].enableInstancing = true;
                    proxyMaterials[i].hideFlags = HideFlags.HideAndDontSave;
                    copiedMaterial = true;
                }
                if (!copiedMaterial)
                    return false;

                treeProxyObjectMR.sharedMaterials = proxyMaterials;
                treeProxyObjectMF.sharedMesh = proxyMesh;

                Component[] allComponents = treeProxyObject.GetComponents(typeof(Component));
                for (int i = 0; i < allComponents.Length; i++)
                {
                    if (allComponents[i] is Transform || allComponents[i] is MeshFilter ||
                        allComponents[i] is MeshRenderer || allComponents[i] is Tree)
                        continue;

                    UnityEngine.Object.Destroy(allComponents[i]);
                }
                succeeded = true;
                return true;
            }
            finally
            {
                if (!succeeded && treeProxyObject != null)
                    DestroyTreeProxyObject(treeProxyObject);
            }
        }

        private void ReleaseTreeProxyContext()
        {
            ReleaseTreeProxyObjects(ref _treeProxyParent, ref _treeProxyList);
            _lastTreePositionUpdate = -1;
        }

        private static void ReleaseTreeProxyObjects(
            ref GameObject parent,
            ref Dictionary<GameObject, Transform> proxyList)
        {
            GameObject proxyParent = parent;
            parent = null;
            if (proxyList != null)
                proxyList.Clear();
            proxyList = null;

            if (proxyParent == null)
                return;

            HashSet<Mesh> meshes = new HashSet<Mesh>();
            HashSet<Material> materials = new HashSet<Material>();
            CollectTreeProxyResources(proxyParent.transform, meshes, materials);
            foreach (Material material in materials)
                DestroyTreeProxyObject(material);
            foreach (Mesh mesh in meshes)
                DestroyTreeProxyObject(mesh);

            DestroyTreeProxyObject(proxyParent);
        }

        private static void CollectTreeProxyResources(
            Transform root,
            HashSet<Mesh> meshes,
            HashSet<Material> materials)
        {
            if (root == null)
                return;

            MeshFilter meshFilter = root.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null &&
                (meshFilter.sharedMesh.hideFlags & HideFlags.DontSave) != 0)
                meshes.Add(meshFilter.sharedMesh);

            MeshRenderer meshRenderer = root.GetComponent<MeshRenderer>();
            if (meshRenderer != null && meshRenderer.sharedMaterials != null)
            {
                Material[] sharedMaterials = meshRenderer.sharedMaterials;
                for (int materialIndex = 0; materialIndex < sharedMaterials.Length; materialIndex++)
                {
                    Material material = sharedMaterials[materialIndex];
                    if (material != null &&
                        (material.hideFlags & HideFlags.DontSave) != 0)
                        materials.Add(material);
                }
            }

            for (int i = 0; i < root.childCount; i++)
                CollectTreeProxyResources(root.GetChild(i), meshes, materials);
        }

        private static void DestroyTreeProxyObject(UnityEngine.Object value)
        {
            if (value == null)
                return;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEngine.Object.DestroyImmediate(value);
            else
                UnityEngine.Object.Destroy(value);
#else
            UnityEngine.Object.Destroy(value);
#endif
        }


        public static Vector4 GetWindVector()
        {
            if (_windVector != Vector4.zero)
                return _windVector;

            UpdateSceneWind();

            return _windVector;
        }

        public static void UpdateSceneWind()
        {
            WindZone[] sceneWindZones = UnityEngine.Object.FindObjectsOfType<WindZone>();

            for (int i = 0; i < sceneWindZones.Length; i++)
            {
                if (sceneWindZones[i].mode == WindZoneMode.Directional)
                {
                    _windVector = new Vector4(sceneWindZones[i].windTurbulence, sceneWindZones[i].windPulseMagnitude, sceneWindZones[i].windPulseFrequency, sceneWindZones[i].windMain);
                    break;
                }
            }
        }


#if UNITY_EDITOR && UNITY_2017_2_OR_NEWER
        public void HandlePlayModeStateChanged(PlayModeStateChange state)
        {
            playModeState = state;
        }
#endif

        public GPUInstancerRuntimeData GetRuntimeData(GPUInstancerPrototype prototype, bool logError = false)
        {
            GPUInstancerRuntimeData runtimeData = null;
            GPUInstancerPrototype resolvedPrototype = ResolveRuntimePrototype(prototype);
            if (resolvedPrototype == null || runtimeDataDictionary == null)
            {
                if (logError)
                    Debug.LogError("Can not find runtime data for prototype: " + prototype + ". Please check if the prototype was added to the Manager and the initialize method was called.");
                return null;
            }

            if (!runtimeDataDictionary.TryGetValue(resolvedPrototype, out runtimeData) && logError)
                Debug.LogError("Can not find runtime data for prototype: " + prototype + ". Please check if the prototype was added to the Manager and the initialize method was called.");
            return runtimeData;
        }
    }

}
