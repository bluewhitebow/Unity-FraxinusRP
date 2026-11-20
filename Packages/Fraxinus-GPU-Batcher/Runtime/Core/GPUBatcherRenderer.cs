// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

#if UNITY_6000_4_OR_NEWER
using ViewID = UnityEngine.EntityId;
#else
using ViewID = System.Int32;
#endif

[assembly: InternalsVisibleTo("Fraxinus.GPUBatcher.Editor")]

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Core BRG renderer. Split into partial classes: .Batches, .Chunks, .Culling, .Debug, .Prototypes
    /// </summary>
    public partial class GPUBatcherRenderer : IDisposable
    {
        #region singleton

        private static GPUBatcherRenderer _instance;
        private static bool _isShuttingDown;  // prevents auto-creation during disposal/domain reload
        private bool _disposed;

        private GPUBatcherRenderer() { }

        public static GPUBatcherRenderer Instance
        {
            get
            {
                if (_isShuttingDown)
                    return null;

                if (_instance == null)
                {
                    _instance = new GPUBatcherRenderer();
                }
                if (!_instance.IsInitialized)
                    _instance.Initialize();
                return _instance;
            }
        }

        public static bool HasInstance => _instance != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticInstance()
        {
            Shutdown();
            _isShuttingDown = false;
            _configWarningShown = false;
        }

        public bool IsInitialized => _initialized;

        public static bool CanSubmitDraws => Rendering.BRGIndirectSubmission.IsAvailable;

        public static void Shutdown()
        {
            _isShuttingDown = true;

            if (_instance != null)
            {
                _instance.Dispose();
                _instance = null;
            }
        }

        public static void ClearShutdownGuard() => _isShuttingDown = false;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // shut down all registrations while BRG is still valid, so trackers
            // can FreeChunk cleanly before arrays are disposed
            ShutdownAllRegistrations();
            Cleanup();
            GPUBatcherRegistrationTracker.DisposeStaticScratch();
            _streamingCameraPositions.DisposeIfCreated();
            _gameCamPositions.DisposeIfCreated();
#if UNITY_EDITOR
            _sceneViewCamPositions.DisposeIfCreated();
#endif
            if (_instance == this)
                _instance = null;
        }

        private static bool _configWarningShown;
        private static void ShowConfigMissingWarning()
        {
            if (_configWarningShown) return;
            _configWarningShown = true;
            var go = new GameObject("GPU Batcher Config Missing Warning");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<ConfigMissingWarningGUI>();
        }

        private class ConfigMissingWarningGUI : MonoBehaviour
        {
            private void OnGUI()
            {
                var style = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 24,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.red }
                };
                GUI.Box(new Rect(10, 10, 600, 40), "GPUBatcherConfig is null", style);
            }
        }

        #endregion

        #region configuration

#if UNITY_EDITOR

        /// <summary>
        /// Camera used as freeze culling reference (scene view reuses its culling data). Falls back to Camera.main.
        /// </summary>
        public Camera FreezeCullingReferenceCamera { get; set; }

        // just used when FreezeCullingReferenceCamera is not set
        private Camera _cachedMainCamera;

        // active game cameras seen this frame
        private readonly List<Camera> _activeGameCameras = new();
        public IReadOnlyList<Camera> ActiveGameCameras => _activeGameCameras;

#endif

        private GPUBatcherConfig Config => GPUBatcherConfig.Instance;

        // compute shaders
        private ComputeShader cullingShader;
        private ComputeShader uploadShader;

        #endregion

        #region fields

        // BRG core
        private BatchRendererGroup _brg;
        private bool _initialized;
        private enum BatchRebuildScope
        {
            None,
            IncrementalFlush,   // re-add/remove changed protos, rebuild templates
            FullRebuild         // destroy + recreate all batches from scratch
        }
        private BatchRebuildScope _batchRebuildNeeded;
        private bool _prototypeBufferDirty;

#if UNITY_EDITOR
        private int _cachedMaxShadowCrossfadeCascade = -1;
#endif

        private bool _poolCompactionDirty;
        private bool _chunkCompactionDirty;
        private bool _protoCompactionDirty;
        private int _compactionCooldown;        // render cycles to skip after compaction
        private const int kCompactionCooldownCycles = 300; // ~5s at 60fps

        // request a batch rebuild - higher scopes cannot be downgraded by lower ones
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RequestBatchRebuild(BatchRebuildScope scope)
        {
            if (scope > _batchRebuildNeeded)
                _batchRebuildNeeded = scope;
        }

        // streaming camera positions: accumulated across contexts, merged once per frame in SystemUpdate
        private NativeList<float3> _streamingCameraPositions = new(4, Allocator.Persistent);
        private NativeList<float3> _gameCamPositions = new(4, Allocator.Persistent);
    #if UNITY_EDITOR
        private NativeList<float3> _sceneViewCamPositions = new(4, Allocator.Persistent);
    #endif

        // tracked registrations (both MonoBehaviour-backed and standalone)
        private readonly List<GPUBatcherRegistrationTracker> _registrations = new();

        private GPUBatcherLightProbes _lightProbes;             // optional SH light probe sampling
#if UNITY_EDITOR
        private Material _pickingMaterial;
#endif

        // per-instance color
        private bool _perInstanceColorEnabled;
        private int _colorRegionByteOffset;
        private static readonly int _ID_ColorRegionByteOffset = Shader.PropertyToID("_ColorRegionByteOffset");
        private static readonly int _ID_ColorStagingSrc = Shader.PropertyToID("_ColorStagingSrc");
        private static readonly int _ID_ColorScatterCount = Shader.PropertyToID("_ColorScatterCount");
        private static readonly int _ID_ColorRegionOffsetUint = Shader.PropertyToID("_ColorRegionOffsetUint");
        private static readonly int _ID_ColorEnabled = Shader.PropertyToID("_ColorEnabled");

        // speedtree
        private int _speedTreeRegionByteOffset;
        // high water mark: max slot index ever assigned + 1. Buffer/iteration size.
        // Live count = _speedTreeSlotHWM - _freeSpeedTreeSlots.Count.
        private int _speedTreeSlotHWM;
        private readonly Stack<int> _freeSpeedTreeSlots = new();
        private int[] _protoSpeedTreeSlotIndex; // protoIndex -> speedtree slot, -1 if not speedtree

        // constants
        internal const int kPoolSize = 16; // must match _PoolSizeShift in GPUBatcher_Common.hlsl
        internal const int kSizeOfPackedMatrix = sizeof(float) * 4 * 3;
        internal const int kSHFloat4sPerInstance = 8;                    // 7 SH + 1 ProbesOcclusion, packed as unity_SHCoefficients
        internal const int kSHBytesPerInstance = kSHFloat4sPerInstance * 16;
        private const int kSpeedTreeParamsPerProto = 128; // 32 float4s, 512 bytes per prototype

        internal bool LightProbesEnabled => _lightProbes != null;

        // max work groups per dispatch dimension (D3D11/D3D12/Vulkan/GLES3.1)
        private const int kDispatchMaxX = 65535;

        /// <summary>
        /// Dispatch a compute shader, wrapping overflow from X into Y when groupCount exceeds 65535.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DispatchSafe(ComputeShader cs, int kernel, int groupCount)
        {
            if (groupCount <= 0) return;
            if (groupCount <= kDispatchMaxX)
            {
                cs.Dispatch(kernel, groupCount, 1, 1);
            }
            else
            {
                int y = (groupCount + kDispatchMaxX - 1) / kDispatchMaxX;
                cs.Dispatch(kernel, kDispatchMaxX, y, 1);
            }
        }

        /// <summary>
        /// Dispatch a compute shader via CommandBuffer, wrapping overflow from X into Y when groupCount exceeds 65535.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DispatchSafe(CommandBuffer cmd, ComputeShader cs, int kernel, int groupCount)
        {
            if (groupCount <= 0) return;
            if (groupCount <= kDispatchMaxX)
            {
                cmd.DispatchCompute(cs, kernel, groupCount, 1, 1);
            }
            else
            {
                int y = (groupCount + kDispatchMaxX - 1) / kDispatchMaxX;
                cmd.DispatchCompute(cs, kernel, kDispatchMaxX, y, 1);
            }
        }

        /// <summary>
        /// Whether the current graphics API requires Raw | IndirectArguments buffers.
        /// Currently only DX12 needs this (AMD AoSoA corruption + Intel Arc rejection with Structured).
        /// All other platforms (DX11, PS5, Vulkan, Metal, etc.) use Structured | IndirectArguments.
        /// </summary>
        internal static bool UseRawIndirectArgs =>
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;

        // grow _protoSpeedTreeSlotIndex to cover [0, needed); new entries default to -1
        private void EnsureProtoSpeedTreeSlotIndexCapacity(int needed)
        {
            int oldLen = _protoSpeedTreeSlotIndex?.Length ?? 0;
            if (oldLen >= needed) return;
            Array.Resize(ref _protoSpeedTreeSlotIndex, needed);
            for (int i = oldLen; i < needed; i++) _protoSpeedTreeSlotIndex[i] = -1;
        }

        internal NativeArray<bool> PoolInUse => _poolInUse;
        internal NativeArray<ulong> PoolSlotMask => _poolSlotMask;
        public GraphicsBuffer MatrixBuffer => _matrixBuffer;

        internal void DispatchCopyBufferRegion(GraphicsBuffer src, int srcOffsetBytes,
            GraphicsBuffer dst, int dstOffsetBytes, int sizeBytes)
        {
            GPUCopyBufferRegion(src, srcOffsetBytes, dst, dstOffsetBytes, sizeBytes);
        }

        #endregion

        #region initialization

        private static int FindKernelSafe(ComputeShader cs, string name)
        {
            try { return cs.FindKernel(name); }
            catch (System.ArgumentException) { return -1; }
        }

        private void Initialize()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GPUBatcherRenderer));
            if (_initialized) return;

            _crossfadeTimeScale = 1f;
            _protoFadeElapsed = 0;

            if (GPUBatcherConfig.Instance == null)
            {
                Debug.LogError("GPUBatcherRenderer: No GPUBatcherConfig asset found. Create one via Assets > Create > Fraxinus GPU Batcher Config.");
                ShowConfigMissingWarning();
                return;
            }

            cullingShader = Config.cullingShader;
            uploadShader = Config.uploadShader;

            _brg = new BatchRendererGroup(OnPerformCulling, IntPtr.Zero);

            _brg.SetEnabledViewTypes(new BatchCullingViewType[] {
                BatchCullingViewType.Camera,
                BatchCullingViewType.Light,
#if UNITY_EDITOR
                BatchCullingViewType.Picking,
                BatchCullingViewType.SelectionOutline,
#endif
            });

#if UNITY_EDITOR
            var pickingShader = Shader.Find("Hidden/Ultra Render Pipeline/BRGPicking");
            if (pickingShader != null)
            {
                _pickingMaterial = new Material(pickingShader);
                _pickingMaterial.hideFlags = HideFlags.HideAndDontSave;
                _brg.SetPickingMaterial(_pickingMaterial);
            }
#endif

            if (cullingShader != null)
            {
                _finalizeDrawArgsKernel = FindKernelSafe(cullingShader, "FinalizeDrawArgs");

                // shadow culling kernels
                _finalizeShadowDrawArgsKernel = FindKernelSafe(cullingShader, "FinalizeShadowDrawArgs");

                // chunk kernels
                _cullChunksKernel = FindKernelSafe(cullingShader, "CullChunks");
                _cullInstancesKernel = FindKernelSafe(cullingShader, "CullInstances");
                _cullChunksShadowKernel = FindKernelSafe(cullingShader, "CullChunksShadow");
                _cullInstancesShadowKernel = FindKernelSafe(cullingShader, "CullInstancesShadow");
                // combined reset kernel
                _resetCullingStateKernel = FindKernelSafe(cullingShader, "ResetCullingState");

                // multi_compile keywords
                _hzbEnabledKeyword = new LocalKeyword(cullingShader, "HZB_ENABLED");
                _rawIndirectArgsKeyword = new LocalKeyword(cullingShader, "RAW_INDIRECT_ARGS");
                if (UseRawIndirectArgs)
                    cullingShader.EnableKeyword(_rawIndirectArgsKeyword);

                bool hasCameraKernels = _finalizeDrawArgsKernel >= 0 &&
                                        _cullChunksKernel >= 0 && _cullInstancesKernel >= 0;

                bool hasShadowKernels = _finalizeShadowDrawArgsKernel >= 0 &&
                                        _cullChunksShadowKernel >= 0 && _cullInstancesShadowKernel >= 0;

                if (!hasCameraKernels)
                {
                    Debug.LogError("GPUBatcherRenderer: Missing camera culling compute shader kernels!");
                    cullingShader = null;
                }
                else if (!hasShadowKernels)
                {
                    Debug.LogWarning("GPUBatcherRenderer: Missing shadow culling kernels - shadows disabled");
                }
            }

            if (uploadShader != null)
            {
                // animated crossfade kernels
                _updateFadeStatesPerInstanceKernel = FindKernelSafe(uploadShader, "UpdateFadeStatesPerInstance");
                _updateFadeStatesPerPrototypeKernel = FindKernelSafe(uploadShader, "UpdateFadeStatesPerPrototype");

                // GPU buffer copy kernel
                _copyBufferRegionKernel = FindKernelSafe(uploadShader, "CopyBufferRegion");

                // typed upload kernels
                _initFadeStateTypedKernel = FindKernelSafe(uploadShader, "InitFadeStateTyped");
                _copyChunkRegionKernel = FindKernelSafe(uploadShader, "CopyChunkRegion");
                _copyFadeStateRegionKernel = FindKernelSafe(uploadShader, "CopyFadeStateRegion");
                _scatterWriteChunksKernel = FindKernelSafe(uploadShader, "ScatterWriteChunks");
                _settleMotionVectorsKernel = FindKernelSafe(uploadShader, "SettleMotionVectors");
                _computeMatricesBatchedKernel = FindKernelSafe(uploadShader, "ComputeMatricesFromTRSBatched");

                // batched dispatch kernels
                _batchUploadInstancesKernel = FindKernelSafe(uploadShader, "BatchUploadInstances");
                _trsPatchInstancesKernel = FindKernelSafe(uploadShader, "TRSPatchInstances");
                _batchComputeMatricesKernel = FindKernelSafe(uploadShader, "BatchComputeMatrices");
                _batchClearInstancesKernel = FindKernelSafe(uploadShader, "BatchClearInstances");
                _batchUploadIntsKernel = FindKernelSafe(uploadShader, "BatchUploadInts");
                _batchInitFadeStatesKernel = FindKernelSafe(uploadShader, "BatchInitFadeStates");
                _copyInstanceBufferKernel = FindKernelSafe(uploadShader, "CopyInstanceBuffer");
                _scatterWriteColorsKernel = FindKernelSafe(uploadShader, "ScatterWriteColors");
                _scatterWriteSigOverridesKernel = FindKernelSafe(uploadShader, "ScatterWriteSigOverrides");
                _scatterWriteSignaturesKernel = FindKernelSafe(uploadShader, "ScatterWriteSignatures");
                _copyRotScaleBufferKernel = FindKernelSafe(uploadShader, "CopyRotScaleBuffer");

                // floating origin shift kernels
                _shiftAllInstancesKernel = FindKernelSafe(uploadShader, "ShiftAllInstances");
                _shiftAllChunksKernel = FindKernelSafe(uploadShader, "ShiftAllChunks");

                bool hasAnimatedCrossfadeKernels = _updateFadeStatesPerInstanceKernel >= 0 &&
                    _updateFadeStatesPerPrototypeKernel >= 0;

                if (!hasAnimatedCrossfadeKernels)
                {
                    Debug.LogWarning("GPUBatcherRenderer: Missing animated crossfade kernels - animated crossfade disabled");
                }
                else
                {
                    LogMessageDevelopment("GPUBatcherRenderer: Hierarchical GPU culling initialized (camera + shadows + animated crossfade)");
                }
            }

            _cullingCmd = new CommandBuffer { name = "FraxinusGPUBatcher Culling" };

            if (GPUBatcherConfig.Instance.supportLegacyLightProbes)
                _lightProbes = new GPUBatcherLightProbes();

            // native transform change tracking for GPUBatcherGameObjectLink
            GPUBatcherChangeTracker.Initialize();
            GPUBatcherChangeTracker.TrackTransforms<GPUBatcherGameObjectLink>();
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                GPUBatcherChangeTracker.TrackType<MeshRenderer>();
                // drain ObjectDispatcher even when no view is rendering (e.g. debugger view)
                // to prevent 64-frame auto-disable of type tracking
                UnityEditor.EditorApplication.update -= EditorDrainRendererChanges;
                UnityEditor.EditorApplication.update += EditorDrainRendererChanges;
            }
            UnityEditor.EditorApplication.update -= ResetUpdateFlag;
            UnityEditor.EditorApplication.update += ResetUpdateFlag;
#endif

            _initialized = true;
            _lastRenderCycleTime = Time.realtimeSinceStartup;

            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;

            Application.onBeforeRender -=BeforeRender;
            Application.onBeforeRender += BeforeRender;

            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;

#if UNITY_EDITOR
            UnityEditor.SceneView.beforeSceneGui -= BeforeSceneGUI;
            UnityEditor.SceneView.beforeSceneGui += BeforeSceneGUI;
#endif

            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

            InitHZB();

            InitializeChunkSystem();
        }

        #endregion

        #region render cycle callbacks

        private float _lastRenderCycleTime;
        private float _realDeltaTime;
        private double _protoFadeElapsed;
        private float _crossfadeTimeScale = 1f;
        public float CrossfadeTimeScale
        {
            get => _crossfadeTimeScale;
            set => _crossfadeTimeScale = math.max(0f, value);
        }
#if UNITY_EDITOR
        private bool _updateRanThisFrame = false;
        private void ResetUpdateFlag()
        {
            _updateRanThisFrame = false;
        }

        public void BeforeSceneGUI(UnityEditor.SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!_initialized) return;
            //Debug.Log("Scene Render");
            //SystemUpdate();
        }
#endif

        // called once per frame from Application.onBeforeRender
        private void BeforeRender()
        {
            if (!_initialized) return;
            //Debug.Log("Game Render");
#if DEBUG
            using var _su = s_MarkerSystemUpdate.Auto();
#endif

            SystemUpdate();
            _gameCamPositions.Clear();
#if UNITY_EDITOR
            _activeGameCameras.Clear();
#endif
        }

        // per-frame update: merge camera positions, fire streaming event, flush, batch rebuilds, etc.
        // split into two phases: idempotent work (safe to run multiple times per frame) runs first,
        // then once-per-frame work (counters, eviction, compaction) runs behind a dedup guard.
        // protects against scene view and game view having weird unsynced render timings.
        private void SystemUpdate()
        {
#if UNITY_EDITOR
            float now = Time.realtimeSinceStartup;
            float deltaTime = now - _lastRenderCycleTime;
            _lastRenderCycleTime = now;
#else
            float deltaTime = Time.unscaledDeltaTime;
#endif

            _realDeltaTime = deltaTime;

            #region idempotent phase
            //small chance that SystemUpdate can run multiple times per frame.
            // we need to guarentee that some work (like batch flushges) runs before rendering EVERY frame.

#if UNITY_EDITOR
            // merge camera accumulators -> published positions
            if (!Config.debugDisableCulling)
            {
                _streamingCameraPositions.Clear();
                _streamingCameraPositions.AddRange(_gameCamPositions.AsArray());

                // exclude scene view when debugVisualizeCullingInSceneView is active and game cameras exist
                if (!Config.debugVisualizeCullingInSceneView || _gameCamPositions.Length == 0)
                    _streamingCameraPositions.AddRange(_sceneViewCamPositions.AsArray());
            }
#endif

            // fire streaming event
            OnStreamingPositionsReady?.Invoke();

            // apply native transform changes from tracked GPUBatcherGameObjectLink components
            ProcessTrackedTransformChanges();
#if UNITY_EDITOR
            if (!Application.isPlaying)
                ProcessTrackedRendererChanges();
#endif

#if DEBUG
            using var _plu = s_MarkerPreRender.Auto();
#endif

            ProcessPendingPrototypePurges();

            bool batchesRebuiltThisFrame = false;

#if UNITY_EDITOR
            // auto-rebuild batches when shadow crossfade cascade count changes
            if (_cachedMaxShadowCrossfadeCascade != Config.maxShadowCrossfadeCascade)
            {
                _cachedMaxShadowCrossfadeCascade = Config.maxShadowCrossfadeCascade;
                if (_chunkSystemInitialized && _renderSignatures.Count > 0)
                    RequestBatchRebuild(BatchRebuildScope.FullRebuild);
            }
            // auto-rebuild batches when negative scale mode changes
            if (_cachedNegativeScaleMode != Config.negativeScaleMode)
            {
                _cachedNegativeScaleMode = Config.negativeScaleMode;
                if (_chunkSystemInitialized && _renderSignatures.Count > 0)
                    RequestBatchRebuild(BatchRebuildScope.FullRebuild);
            }
#endif
            // widen shadow buckets to 6 splits on first point light view
            if (_pendingShadowSplitWiden)
            {
                _pendingShadowSplitWiden = false;
                if (_shadowSplitCapacity < kMaxCascades)
                {
                    _shadowSplitCapacity = kMaxCascades;
                    if (_chunkSystemInitialized && _renderSignatures.Count > 0)
                        RequestBatchRebuild(BatchRebuildScope.FullRebuild);
                    LogMessageDevelopment("GPUBatcherRenderer: widening shadow buckets to 6 splits for point light shadows");
                }
            }

            switch (_batchRebuildNeeded)
            {
                case BatchRebuildScope.FullRebuild:
                    CachePrototypeBounds();
                    if (_chunkSystemInitialized)
                        IncFullRebuild();
                    UpdateGPUSignatureBuffer();
                    _gpuCullingReady = _chunkSystemInitialized;
                    _dirtyBufferBindings |= BufferBindingFlags.Signatures;
                    _prototypeBufferDirty = false;
                    batchesRebuiltThisFrame = true;
                    break;

                case BatchRebuildScope.IncrementalFlush:
                    CachePrototypeBounds();
                    if (_chunkSystemInitialized)
                        IncFlush();
                    UpdateGPUSignatureBuffer();
                    _gpuCullingReady = _chunkSystemInitialized;
                    _dirtyBufferBindings |= BufferBindingFlags.Signatures;
                    _prototypeBufferDirty = false;
                    batchesRebuiltThisFrame = true;
                    break;
            }
            _batchRebuildNeeded = BatchRebuildScope.None;

            if (_prototypeBufferDirty)
            {
                CachePrototypeBounds();
                UpdateGPUSignatureBuffer();
                _dirtyBufferBindings |= BufferBindingFlags.Signatures;
                _prototypeBufferDirty = false;
            }


            if (_chunkSystemInitialized)
                FlushStagedUploads();

#if UNITY_EDITOR
            if (_chunkSystemInitialized)
                DrainDeferredPoolFrees();
#endif

            if (_chunkSystemInitialized)
                SettleMotionVectors();

            _shadowChunksRebuiltThisFrame = false;
            if (_chunkSystemInitialized && _shadowChunksDirty)
                RebuildShadowActiveChunks();

            if (_chunkSystemInitialized)
                UpdateSpeedTree();

            RebuildAnimatedCrossfadePoolList();

            #endregion

            #region Once Per Frame Guard

#if UNITY_EDITOR
            if (_updateRanThisFrame) return;
            _updateRanThisFrame = true;
#endif

            UpdateProtoCrossfadeCycles();

            // snapshot peak pool usage before reset
            _lastCameraPoolActive = _peakCameraPoolIndex;
            _lastShadowPoolActive = _peakShadowPoolIndex;
            _peakCameraPoolIndex = 0;
            _peakShadowPoolIndex = 0;

            // safety net: reset pool indices (OnEndCameraRendering should have already restored them)
            _cameraPoolNextIndex = 0;
            _shadowPoolNextIndex = 0;
            _renderCycleCounter++;

            // reclaim deferred culling contexts whose render pass never executed last frame
            DrainOrphanedDeferredContexts();

#if UNITY_EDITOR
            // reset readback pending flags so one readback fires per view per frame
            if (_computeRenderedTriangles)
            {
                var keys = new List<ViewID>(_perCameraMeshStats.Keys);
                foreach (var k in keys)
                {
                    var s = _perCameraMeshStats[k];
                    s.cameraPending = false;
                    _perCameraMeshStats[k] = s;
                }
                var lightKeys = new List<ViewID>(_perLightShadowStats.Keys);
                foreach (var k in lightKeys)
                {
                    var s = _perLightShadowStats[k];
                    s.pending = false;
                    _perLightShadowStats[k] = s;
                }
            }
#endif

            // evict stale per-view fade states
            if (_viewFadeStates.Count > 0 && _renderCycleCounter % kViewFadeEvictFrames == 0)
            {
                _staleViewKeys.Clear();
                for (int i = _viewFadeStateKeys.Count - 1; i >= 0; i--)
                {
                    ViewID key = _viewFadeStateKeys[i];
                    if (_viewFadeStates.TryGetValue(key, out var state) &&
#if UNITY_EDITOR
                        !state.isPersistent &&
#endif
                        _renderCycleCounter - state.lastSeenFrame > kViewFadeEvictFrames)
                    {
                        _staleViewKeys.Add(key);
                        // swap-remove from key list
                        _viewFadeStateKeys[i] = _viewFadeStateKeys[_viewFadeStateKeys.Count - 1];
                        _viewFadeStateKeys.RemoveAt(_viewFadeStateKeys.Count - 1);
                    }
                }
                foreach (var key in _staleViewKeys)
                {
                    if (_viewFadeStates.Remove(key, out var staleState))
                    {
                        _trackedTotalFadeStates -= staleState.FadeStateBuffer?.count ?? 0;
                        staleState.Dispose();
                    }
#if UNITY_EDITOR
                    _perCameraMeshStats.Remove(key);
#endif
                }
            }

            // evict stale per-camera HZB states
            EvictStaleHZBStates();

            // update editor window stats
#if DEBUG
            UpdateStats();
#endif
#if UNITY_EDITOR
            PrepareDebugSpheres();
#endif

            // auto-compaction
            // _compactionCooldown: >0 = cooling down, 0 = ready, -1 = grace frame before cooldown
            if (Config.enableAutoCompaction && _chunkSystemInitialized && !batchesRebuiltThisFrame)
            {
                if (_compactionCooldown > 0)
                {
                    _compactionCooldown--;
                }
                else
                {
                    bool compacted = false;
                    if (_chunkCompactionDirty)
                    {
                        compacted = TryCompactChunks();
                        _chunkCompactionDirty = false;
                    }
                    else if (_poolCompactionDirty)
                    {
                        compacted = TryCompactPools();
                        _poolCompactionDirty = false;
                    }
                    else if (_protoCompactionDirty)
                    {
                        compacted = TryCompactProtoHWM();
                        _protoCompactionDirty = false;
                    }

                    if (_compactionCooldown < 0)
                        _compactionCooldown = kCompactionCooldownCycles; // grace frame done - start cooldown
                    else if (compacted)
                        _compactionCooldown = -1;  // compaction fired - allow one grace frame first
                }
            }
            else if (!Config.enableAutoCompaction || !_chunkSystemInitialized)// when batchesRebuiltThisFrame skipped compaction, preserve cooldown from ResizePoolBuffer
            {
                _compactionCooldown = 0;
            }

            // clear per-frame accumulators after all consumers have run
            _cameraRenderData.Clear();
            #endregion
        }

        private void UpdateProtoCrossfadeCycles()
        {
            int protoCount = _renderSignatures.Count;
            if (protoCount == 0) { _anyPrototypeBoundary = false; return; }

            // resize arrays if needed
            if (_protoCrossfadeStates == null || _protoCrossfadeStates.Length < protoCount)
            {
                _protoCrossfadeStates = new GPUProtoCrossfadeState[protoCount];
                Array.Resize(ref _protoCycleIndex, protoCount);
            }

            _protoFadeElapsed += _realDeltaTime * _crossfadeTimeScale;
            double elapsed = _protoFadeElapsed;
            bool anyBoundary = false;

            for (int i = 0; i < protoCount; i++)
            {
                var sig = _renderSignatures[i];
                if (sig == null || !sig.animateCrossFading)
                {
                    _protoCrossfadeStates[i] = default;
                    continue;
                }

                double dur = Math.Max(0.001, sig.lodFadeDuration);
                int cycleIdx = (int)Math.Floor(elapsed / dur);
                float progress = (float)((elapsed - cycleIdx * dur) / dur);

                bool boundary = cycleIdx != _protoCycleIndex[i];
                _protoCycleIndex[i] = cycleIdx;
                anyBoundary |= boundary;

                _protoCrossfadeStates[i] = new GPUProtoCrossfadeState
                {
                    cycleProgress = progress,
                    isBoundary = boundary ? 1 : 0
                };
            }

            _anyPrototypeBoundary = anyBoundary;

            // resize GPU buffer if needed
            if (_protoCrossfadeBuffer == null || _protoCrossfadeBuffer.count < protoCount)
            {
                _protoCrossfadeBuffer?.Dispose();
                _protoCrossfadeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    math.max(protoCount, 8),
                    UnsafeUtility.SizeOf<GPUProtoCrossfadeState>());
                _dirtyBufferBindings |= BufferBindingFlags.ProtoCrossfade;
            }
            _protoCrossfadeBuffer.SetData(_protoCrossfadeStates, 0, 0, protoCount);
        }

        // called per rendering context
        private void OnBeginContextRendering(ScriptableRenderContext ctx, List<Camera> cameras)
        {
            if (!_initialized) return;

            // accumulate this context's cameras for next SystemUpdate merge
#if UNITY_EDITOR
            if (!Config.debugDisableCulling)
#endif
            {
                for (int i = 0; i < cameras.Count; i++)
                {
                    var cam = cameras[i];
                    if (cam == null) continue;
                    var ct = cam.cameraType;
                    if (ct == CameraType.Game)
                    {
#if UNITY_EDITOR
                        if (i == 0)
                            _gameCamPositions.Clear();
#endif
                        _gameCamPositions.Add(cam.transform.position);
                    }
#if UNITY_EDITOR
                    else if (ct == CameraType.SceneView)
                    {
                        _sceneViewCamPositions.Clear();
                        _sceneViewCamPositions.Add(cam.transform.position);
                    }
#endif
                }
            }
#if UNITY_EDITOR
            if (!_updateRanThisFrame)
                SystemUpdate();
#endif
        }


        private void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera camera)
        {
            ViewID viewID = GPUBatcherHelpers.GetViewID(camera);


            // per-camera LOD bias
            float lodBias = camera.TryGetComponent<GPUBatcherCameraSettings>(out var settings) && settings.enabled
                ? settings.lodBiasMultiplier : 1.0f;

            // HZB projection data
            CameraHZBState hzbState = null;
            var camData = new CameraRenderData
            {
                lodBias = lodBias,
                cameraPoolSnapshot = _cameraPoolNextIndex,
                shadowPoolSnapshot = _shadowPoolNextIndex,
            };
            if (IsGPUOcclusionEnabled)
            {
                var hzbCamData = GPUBatcherHZBBridge.ComputeCameraData(camera);
                camData.vpMatrix = hzbCamData.viewProjMatrix;
                camData.facingDir = hzbCamData.facingDirWorldSpace;
                camData.radialDir = hzbCamData.radialDirWorldSpace;
                camData.hzbPosition = hzbCamData.viewOriginWorldSpace;

                if (camera.cameraType == CameraType.Game)
                    hzbState = AcquireCameraHZBState(viewID);
                camData.hzbState = hzbState;
            }

            // fade state: create if missing
            if (!_viewFadeStates.TryGetValue(viewID, out var fadeState))
                fadeState = CreateViewFadeState(viewID);
#if UNITY_EDITOR
            if (camera.cameraType == CameraType.SceneView)
                fadeState.isPersistent = true;
#endif
            fadeState.lastSeenFrame = _renderCycleCounter;
            camData.viewFadeState = fadeState;

#if UNITY_EDITOR
            // freeze culling: identify the reference camera
            if (_cachedMainCamera == null)
                _cachedMainCamera = Camera.main;
            var refCam = FreezeCullingReferenceCamera != null ? FreezeCullingReferenceCamera : _cachedMainCamera;
            camData.isRef = (refCam != null && camera == refCam);
            camData.isGame = camera.cameraType == CameraType.Game;
            if (camData.isGame && !_activeGameCameras.Contains(camera))
                _activeGameCameras.Add(camera);
#endif

            _cameraRenderData[viewID] = camData;
        }

        private void OnEndCameraRendering(ScriptableRenderContext ctx, Camera camera)
        {
            ViewID viewID = GPUBatcherHelpers.GetViewID(camera);
            if (_cameraRenderData.TryGetValue(viewID, out var camData))
            {
                _cameraPoolNextIndex = camData.cameraPoolSnapshot;
                _shadowPoolNextIndex = camData.shadowPoolSnapshot;
            }
        }

        private void OnSceneUnloaded(Scene scene)
        {
            if (!_initialized) return;

            // snap fade states for new cameras in next scene
            RequestSnapAllViews();
        }

        private void ProcessTrackedTransformChanges()
        {
            using ChangeTrackerTransformData changes = GPUBatcherChangeTracker.GetTransformChanges<GPUBatcherGameObjectLink>(
                Unity.Collections.Allocator.TempJob);

            GPUBatcherGameObjectLink.TryProcessTransformChanges(changes, this);
        }

#if UNITY_EDITOR
        // detect mesh/material changes on renderers that have a GPUBatcherGameObjectLink
        private void ProcessTrackedRendererChanges()
        {
            using ChangeTrackerTypeData changes = GPUBatcherChangeTracker.GetTypeChanges<MeshRenderer>(
                Unity.Collections.Allocator.TempJob);

            if (!Application.isPlaying)
                GPUBatcherGameObjectLink.TryProcessRendererChanges(changes);
        }

        // fallback drain for when no view is rendering (e.g. debugger tab active)
        // prevents ObjectDispatcher from auto-disabling type tracking after 64 uncollected frames
        private void EditorDrainRendererChanges()
        {
            if (Application.isPlaying || !_initialized || !GPUBatcherChangeTracker.IsInitialized) return;
            ProcessTrackedRendererChanges();
        }
#endif

        #endregion

        #region cleanup and reinit

        private void Cleanup()
        {
            _chunkSystemInitialized = false;
            _crossfadeTimeScale = 1f;
            _protoFadeElapsed = 0;

            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            Application.onBeforeRender -= BeforeRender;
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
#if UNITY_EDITOR
            UnityEditor.SceneView.beforeSceneGui -= BeforeSceneGUI;
            UnityEditor.EditorApplication.update -= EditorDrainRendererChanges;
            UnityEditor.EditorApplication.update -= ResetUpdateFlag;
#endif

            _gpuCullingReady = false; // prevent OnPerformCulling from dispatching during teardown
            DrainDeferredVisibilitySlots();
            _deferredContexts.Clear();
            _deferredCullingCameras.Clear();
            _deferredUseHZB.Clear();
            _cameraRenderData.Clear();

            GPUBatcherChangeTracker.Dispose();

            _brg?.Dispose();
            _brg = null;
            _sharedBatchIDValid = false;
            if (_speedTreeBatchIDs != null)
                Array.Clear(_speedTreeBatchIDs, 0, _speedTreeBatchIDs.Length);

            // invalidate cached BRG registration IDs - tied to the disposed BRG instance
            _meshIdCache.Clear();
            _materialIdCache.Clear();
            _meshRefCount.Clear();
            _materialRefCount.Clear();
            _meshIdReverse.Clear();
            _materialIdReverse.Clear();
#if UNITY_EDITOR
            // re-validate prototypes after reinit
            _dotsCheckedProtos?.Clear();
            _crossfadeCheckedProtos?.Clear();
#endif

            ShutdownChunkSystem();

            _lightProbes?.Dispose();
            _lightProbes = null;

            _gpuSignatureBuffer?.Dispose();
            _gpuSignatureBuffer = null;

            _cullingCmd?.Release();
            _cullingCmd = null;

            // reset buffer binding flags so buffers get rebound after reinit
            _dirtyBufferBindings = BufferBindingFlags.All;

            _prototypeMaxShadowDistance.DisposeIfCreated();
            _prototypeMaxShadowCascade.DisposeIfCreated();
            _prototypeCrossfadeEnabled.DisposeIfCreated();
            _prototypeAnimatedCrossfade.DisposeIfCreated();
            _prototypeShadowMaskCache.DisposeIfCreated();
            _protoInstanceCounts.DisposeIfCreated();
            _protoInstanceCountHWM.DisposeIfCreated();
            _protoFlipCounts.DisposeIfCreated();
            _protoFlipCountHWM.DisposeIfCreated();
            _protoArrayCapacity = 0;
            _protoBoundsCenter.DisposeIfCreated();
            _protoBoundsRadius.DisposeIfCreated();
            _protoLodReferenceSize.DisposeIfCreated();

            ShutdownHZB();

            _streamingCameraPositions.Clear();
            _gameCamPositions.Clear();
#if UNITY_EDITOR
            _sceneViewCamPositions.Clear();
            if (_pickingMaterial != null)
            {
                UnityEngine.Object.DestroyImmediate(_pickingMaterial);
                _pickingMaterial = null;
            }
#endif

            _meshCombineCache.Clear();

#if UNITY_EDITOR
            CleanupDebugSpheres();
#endif

            _initialized = false;
            _gpuCullingReady = false;
            DrainDeferredVisibilitySlots();
            _deferredContexts.Clear();
            cullingShader = null;
            uploadShader = null;
        }

        /// <summary>
        /// A slow refresh that should *only* be used in the editor. please also see RuntimeRefresh()
        /// Clears all caches including static registerer caches and re-extracts from terrain.
        /// Use when prefabs, materials, or meshes have changed.
        /// </summary>
        public void Reinitialize()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GPUBatcherRenderer));
            var prevRegistrations = new List<GPUBatcherRegistrationTracker>(_registrations);
            ShutdownAllRegistrations();

            Cleanup();

            // clear managed prototype state
            _renderSignatures.Clear();
            _protoDedup.Clear();
#if UNITY_EDITOR
            _prefabToAllIndices.Clear();
#endif
            _prototypeVacant = null;
            _protoRefCount = null;
            _freeProtoIndices.Clear();
            _pendingProtoPurge.Clear();
            _meshCombineCache.Clear();
            _anyPrototypeUsesAnimatedCrossfade = false;
            _batchRebuildNeeded = BatchRebuildScope.None;
            _batchFullRecomputeNeeded = false;
            _pendingProtoAdds.Clear();
            _pendingProtoResizes.Clear();
            _materializedProtos.Clear();
            _dirtySignatureProtos.Clear();
            _allSignaturesDirty = false;
            _prototypeBufferDirty = false;
            _poolCompactionDirty = false;
            _chunkCompactionDirty = false;
            _protoCompactionDirty = false;
            _compactionCooldown = 0;

            Initialize();

            foreach (var reg in prevRegistrations)
                reg.Initialize();

            LogMessageDevelopment($"GPUBatcherRenderer: Reinitialized {prevRegistrations.Count} registrations");
        }

        /// <summary>A faster refresh path that you can use at runtime.</summary>
        public void RuntimeRefresh()
        {
            RefreshAllRegistrations();
        }

        #endregion

        #region registration management

        internal void TrackRegistration(GPUBatcherRegistrationTracker registration)
        {
            if (!_registrations.Contains(registration))
                _registrations.Add(registration);
        }

        internal void UntrackRegistration(GPUBatcherRegistrationTracker registration)
        {
            _registrations.Remove(registration);
        }

        private void ShutdownAllRegistrations()
        {
            for (int i = _registrations.Count - 1; i >= 0; i--)
                _registrations[i].Shutdown();
        }

        private void RefreshAllRegistrations()
        {
            var snapshot = new List<GPUBatcherRegistrationTracker>(_registrations);
            for (int i = snapshot.Count - 1; i >= 0; i--)
                snapshot[i].Shutdown();

            foreach (var reg in snapshot)
                reg.Initialize();

            LogMessageDevelopment($"GPUBatcherRenderer: Refreshed {snapshot.Count} registrations");
        }

        #endregion

        #region public API

        // re-pack every signature in a group after its live lodBias/density changed
        internal void NotifyGroupChanged(GPUBatcherPrototypeGroup group)
        {
            if (group == null) return;
            for (int p = 0; p < _renderSignatures.Count; p++)
            {
                var sig = _renderSignatures[p];
                if (sig != null && ReferenceEquals(sig.prototypeGroup, group))
                    _dirtySignatureProtos.Add(p);
            }
        }

        #if UNITY_EDITOR
        public NativeList<float3> StreamingCameraPositions => _streamingCameraPositions;
        #else
        public NativeList<float3> StreamingCameraPositions => _gameCamPositions;
        #endif
        public event Action OnStreamingPositionsReady;

        /// <summary>Forces all compute shader buffer bindings to be refreshed on the next dispatch.</summary>
        internal void InvalidateBufferBindings() => _dirtyBufferBindings = BufferBindingFlags.All;

        /// <summary>Rebuilds batches and updates GPU prototype buffer.</summary>
        public void RebuildBatches()
        {
            if (!_initialized) return;

            CachePrototypeBounds();
            IncFullRebuild();
            UpdateGPUSignatureBuffer();

            LogMessageDevelopment("GPUBatcherRenderer: Batches rebuilt");
        }

        /// <summary>
        /// Compacts instance buffers to reduce memory waste, leaves +25% headroom.
        /// Causes a small hitch - use when pausing or loading.
        /// </summary>
        public void CompactBuffers()
        {
            if (!_initialized || !_chunkSystemInitialized) return;
            TryCompactPools();
            TryCompactChunks();

            // decay per-prototype HWM to current counts so buckets shrink
            int liveCount = _renderSignatures.Count;
            if (_protoInstanceCountHWM.IsCreated && _protoInstanceCounts.IsCreated)
                NativeArray<int>.Copy(_protoInstanceCounts, 0, _protoInstanceCountHWM, 0, liveCount);
            if (_protoFlipCountHWM.IsCreated && _protoFlipCounts.IsCreated)
                NativeArray<int>.Copy(_protoFlipCounts, 0, _protoFlipCountHWM, 0, liveCount);
            _batchFullRecomputeNeeded = true;
            RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
        }

        /// <summary>
        /// Shifts all registered instances and chunk bounds by the given offset.
        /// Use for Floating Origin.
        /// Caller is responsible for shifting GameObjects, terrain, cameras, etc.
        /// </summary>
        public void ShiftInstances(Vector3 offset)
        {
            if (!_chunkSystemInitialized || uploadShader == null)
                return;

            if (math.lengthsq((float3)offset) < 1e-12f)
                return;

            // signature buffer must exist before FlushStagedUploads can compute matrices
            if (_gpuSignatureBuffer == null && _renderSignatures.Count > 0)
            {
                CachePrototypeBounds();
                UpdateGPUSignatureBuffer();
            }

            FlushStagedUploads();

            int totalSlots = _poolCapacity * kPoolSize;
            if (totalSlots <= 0)
                return;

            // shift instance slots
            if (_shiftAllInstancesKernel >= 0)
            {
                uploadShader.SetVector(_ID_ShiftOffset, (Vector3)(float3)offset);
                uploadShader.SetInt(_ID_ShiftCount, totalSlots);
                uploadShader.SetInt(_ID_ShiftTotalSlots, totalSlots);
                uploadShader.SetBuffer(_shiftAllInstancesKernel, _ID_InstancesRW, _instanceBuffer);
                uploadShader.SetBuffer(_shiftAllInstancesKernel, _ID_CopyDst, _matrixBuffer);

                DispatchSafe(uploadShader, _shiftAllInstancesKernel, (totalSlots + 255) / 256);
            }

            // shift chunk bounds
            if (_shiftAllChunksKernel >= 0 && _chunkHighWaterMark > 0)
            {
                uploadShader.SetVector(_ID_ShiftOffset, (Vector3)(float3)offset);
                uploadShader.SetInt(_ID_ShiftCount, _chunkHighWaterMark);
                uploadShader.SetBuffer(_shiftAllChunksKernel, _ID_ChunkScatterDst, _chunkBuffer);

                DispatchSafe(uploadShader, _shiftAllChunksKernel, (_chunkHighWaterMark + 63) / 64);
            }

            // CPU-side cached chunk bounds
            for (int a = 0; a < _allocatedCount; a++)
            {
                int i = _activeChunks[a];
                var cd = _chunks[i];
                cd.bounds.center += offset;
                _chunks[i] = cd;
            }
        }

        #endregion

        #region compaction

        private bool TryCompactPools()
        {
            if (!_initialized || !_chunkSystemInitialized) return false;

            int usedPools = _allocatedPoolCount;

            // only compact if waste exceeds 50% and capacity is above initial
            if (usedPools <= _poolCapacity / 2 && _poolCapacity > _initialPoolCapacity)
            {
                // find highest used pool index
                int highestUsedPool = -1;
                for (int i = _poolCapacity - 1; i >= 0; i--)
                {
                    if (_poolInUse[i])
                    {
                        highestUsedPool = i;
                        break;
                    }
                }

                int newPoolCapacity = math.max(
                    (int)math.ceil(usedPools * 1.25f),
                    highestUsedPool + 1 // must fit all used pools
                );
                newPoolCapacity = math.max(newPoolCapacity, _initialPoolCapacity);

                // require at least 25% savings to avoid ping-pong
                if (newPoolCapacity < _poolCapacity && newPoolCapacity <= _poolCapacity * 3 / 4)
                {
                    int oldCapacity = _poolCapacity;
                    ResizePoolBuffer(newPoolCapacity, allowShrink: true);
                    LogMessageDevelopment($"GPUBatcherRenderer: Pools compacted ({oldCapacity} -> {newPoolCapacity}, freed {(oldCapacity - newPoolCapacity) * kPoolSize} slots, used={usedPools}, highest={highestUsedPool})");
                    return true;
                }
            }
            return false;
        }

        // try to compact chunk capacity, returns true if compaction occurred
        private bool TryCompactChunks()
        {
            if (!_initialized || !_chunkSystemInitialized) return false;

            int chunkCount = _allocatedCount;
            int chunkCapacity = _maxChunks;

            // only compact if waste exceeds 50% and capacity is above initial
            if (chunkCount <= chunkCapacity / 2 && chunkCapacity > _initialChunkCapacity)
            {
                int highestActiveChunk = -1;
                for (int i = chunkCapacity - 1; i >= 0; i--)
                {
                    if (_chunkSlotActive[i])
                    {
                        highestActiveChunk = i;
                        break;
                    }
                }

                int newChunkCapacity = (int)math.ceil(chunkCount * 1.25f);
                newChunkCapacity = math.max(newChunkCapacity, _initialChunkCapacity);
                newChunkCapacity = math.max(newChunkCapacity, highestActiveChunk + 1);

                // require at least 25% savings to avoid ping-pong
                if (newChunkCapacity < chunkCapacity && newChunkCapacity <= chunkCapacity * 3 / 4)
                {
                    ResizeChunkCapacity(newChunkCapacity, allowShrink: true);

                    LogMessageDevelopment($"GPUBatcherRenderer: Chunks compacted ({chunkCapacity} -> {newChunkCapacity}, used={chunkCount}, highest ID={highestActiveChunk})");
                    return true;
                }
            }
            return false;
        }

        // decay per-proto HWM when waste exceeds 50%
        private bool TryCompactProtoHWM()
        {
            if (!_initialized || !_chunkSystemInitialized) return false;
            if (!_protoInstanceCountHWM.IsCreated || !_protoInstanceCounts.IsCreated) return false;

            bool anyDecayed = false;
            int protoCount = _renderSignatures.Count;
            for (int i = 0; i < protoCount; i++)
            {
                int current = _protoInstanceCounts[i];
                int hwm = _protoInstanceCountHWM[i];
                if (hwm > 64 && current <= hwm / 2)
                {
                    _protoInstanceCountHWM[i] = math.max(64, (int)math.ceil(current * 1.25f));
                    anyDecayed = true;
                }
            }

            if (anyDecayed && _protoFlipCountHWM.IsCreated && _protoFlipCounts.IsCreated)
            {
                for (int i = 0; i < protoCount; i++)
                {
                    int current = _protoFlipCounts[i];
                    int hwm = _protoFlipCountHWM[i];
                    if (hwm > 64 && current <= hwm / 2)
                        _protoFlipCountHWM[i] = math.max(64, (int)math.ceil(current * 1.25f));
                }
            }

            if (anyDecayed)
            {
                _batchFullRecomputeNeeded = true;
                RequestBatchRebuild(BatchRebuildScope.IncrementalFlush);
                LogMessageDevelopment("GPUBatcherRenderer: Proto HWM compacted");
            }
            return anyDecayed;
        }

        #endregion
    }

    [BurstCompile]
    internal static class GPUBatcherBurstHelpers
    {
        [BurstCompile]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Approximately(float a, float b)
        {
            return math.abs(a - b) < math.max(1E-06f * math.max(math.abs(a), math.abs(b)), math.EPSILON * 8f);
        }

        // mirrors StableDensityHash() in GPUBatcher_Common.hlsl - must stay in sync
        [BurstCompile]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static float StableDensityHash(in float3 position)
        {
            int3 ip = (int3)(position * 1024.0f);
            uint h = (uint)ip.x * 0x45d9f3bu ^ (uint)ip.y * 0x8e3779b9u ^ (uint)ip.z * 0x9e3769b1u;
            h ^= h >> 16;
            h *= 0x45d9f3bu;
            h ^= h >> 16;
            return (float)(h & 0xFFFFu) / 65535.0f;
        }

        // find the first zero bit in a ulong, returns 0-63 or -1 if full
        [BurstCompile]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int FindFirstZeroBit(ulong mask)
        {
            ulong inv = ~mask;
            if (inv == 0) return -1;
            uint lo = (uint)inv;
            if (lo != 0) return math.tzcnt(lo);
            return 32 + math.tzcnt((uint)(inv >> 32));
        }

        // find and claim the first free slot in a pool's bitmask, returns the claimed bit or -1 if full
        [BurstCompile]
        internal static int FindAndClaimFreeSlot(ref NativeArray<ulong> poolSlotMask, int maskIndex)
        {
            int bit = FindFirstZeroBit(poolSlotMask[maskIndex]);
            if (bit >= 0)
                poolSlotMask[maskIndex] |= 1UL << bit;
            return bit;
        }

        // inverted encoding: (255-r, 255-g, 255-b, 255-a) so buffer zeros = white
        // shader decodes with 1.0 - unpackedValue, default Color32 (0,0,0,0) special-cased to 0
        [BurstCompile]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint PackColor(in Color32 c)
        {
            if (c.r == 0 && c.g == 0 && c.b == 0 && c.a == 0) return 0;
            return (uint)(255 - c.a) << 24 | (uint)(255 - c.b) << 16 | (uint)(255 - c.g) << 8 | (uint)(255 - c.r);
        }
    }

    internal static class GPUBatcherHelpers
    {
        public static int IdentityHash(UnityEngine.Object thing)
        {
#if UNITY_6000_3_OR_NEWER
            return thing.GetEntityId().GetHashCode();
#else
            return thing.GetInstanceID();
#endif
        }

        // transform of t relative to ancestor via local TRS chain - bit-identical across
        // world placement, unlike worldToLocalMatrix * localToWorldMatrix
        public static Matrix4x4 LocalMatrixRelativeTo(Transform t, Transform ancestor)
        {
            var m = Matrix4x4.identity;
            while (t != null && t != ancestor)
            {
                m = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) * m;
                t = t.parent;
            }
            return m;
        }

        public static string EntityIDString(UnityEngine.Object thing)
        {
#if UNITY_6000_2_OR_NEWER
            return thing.GetEntityId().ToString();
#else
            return thing.GetInstanceID().ToString();
#endif
        }

#if UNITY_6000_4_OR_NEWER
        public static EntityId GetEntityId(UnityEngine.Object thing) => thing.GetEntityId();
        public static EntityId GetViewID(Camera camera) => NormalizeViewID(camera.GetEntityId());
        public static EntityId GetViewID(BatchPackedCullingViewID v) => NormalizeViewID(v.GetEntityId());
        // 6.5+ camera EntityId carries a high word the culling view id lacks; key on the low 32
        static EntityId NormalizeViewID(EntityId id) => EntityId.FromULong(unchecked((uint)EntityId.ToULong(id)));

        public static IntPtr ViewIDToIntPtr(EntityId v) => (IntPtr)EntityId.ToULong(v);
#else
        public static int GetEntityId(UnityEngine.Object thing) => thing.GetInstanceID();
        public static int GetViewID(Camera camera) => camera.GetInstanceID();
        public static int GetViewID(BatchPackedCullingViewID v) => v.GetInstanceID();

        public static IntPtr ViewIDToIntPtr(int v) => (IntPtr)v;
#endif
    }

    // extension methods for native containers
    internal static class NativeContainerExtensions
    {
        public static void DisposeIfCreated<T>(this ref NativeArray<T> array) where T : struct
        {
            if (array.IsCreated) array.Dispose();
        }

        public static void DisposeIfCreated<T>(this ref NativeList<T> list) where T : unmanaged
        {
            if (list.IsCreated) list.Dispose();
        }

        public static void DisposeIfCreated<TKey, TValue>(this ref NativeHashMap<TKey, TValue> map)
            where TKey : unmanaged, IEquatable<TKey>
            where TValue : unmanaged
        {
            if (map.IsCreated) map.Dispose();
        }

        public static void DisposeIfCreated<T>(this ref NativeHashSet<T> set)
            where T : unmanaged, IEquatable<T>
        {
            if (set.IsCreated) set.Dispose();
        }

        public static void DisposeIfCreated<T>(this ref NativeQueue<T> queue) where T : unmanaged
        {
            if (queue.IsCreated) queue.Dispose();
        }
    }
}
