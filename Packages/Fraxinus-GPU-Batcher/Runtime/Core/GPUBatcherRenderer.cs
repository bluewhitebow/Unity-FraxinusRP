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
    public partial class GPUBatcherRenderer : IDisposable
    {
        #region singleton

        private static GPUBatcherRenderer s_instance;
        private static bool s_isShuttingDown;
        private bool _disposed;

        private GPUBatcherRenderer() { }

        public static GPUBatcherRenderer instance
        {
            get
            {
                if (s_isShuttingDown)
                    return null;

                if (s_instance == null)
                    s_instance = new GPUBatcherRenderer();

                if (!s_instance.isInitialized)
                    s_instance.Initialize();

                return s_instance;
            }
        }

        public static bool hasInstance => s_instance != null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal static GPUBatcherRenderer initializedRenderer => s_instance is { _initialized: true, _disposed: false } ? s_instance : null;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticInstance()
        {
            Shutdown();
            s_isShuttingDown = false;
        }

        public bool isInitialized => _initialized;

        public static bool canSubmitDraws => Rendering.BRGIndirectSubmission.isAvailable;

        public static void Shutdown()
        {
            s_isShuttingDown = true;

            if (s_instance != null)
            {
                s_instance.Dispose();
                s_instance = null;
            }
        }

        public static void ClearShutdownGuard() => s_isShuttingDown = false;

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
            if (s_instance == this)
                s_instance = null;
        }

        #endregion

        #region configuration

        private GPUBatcherConfig _config => GPUBatcherConfig.instance;

        // compute shaders
        private ComputeShader _cullingShader;
        private ComputeShader _uploadShader;

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
        private const int k_compactionCooldownCycles = 300; // ~5s at 60fps

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
        private static readonly int _ColorRegionByteOffset = Shader.PropertyToID("_ColorRegionByteOffset");
        private static readonly int _ColorStagingSrc = Shader.PropertyToID("_ColorStagingSrc");
        private static readonly int _ColorScatterCount = Shader.PropertyToID("_ColorScatterCount");
        private static readonly int _ColorRegionOffsetUint = Shader.PropertyToID("_ColorRegionOffsetUint");
        private static readonly int _ColorEnabled = Shader.PropertyToID("_ColorEnabled");

        // speedtree
        private int _speedTreeRegionByteOffset;
        // high water mark: max slot index ever assigned + 1. Buffer/iteration size.
        // Live count = _speedTreeSlotHWM - _freeSpeedTreeSlots.Count.
        private int _speedTreeSlotHWM;
        private readonly Stack<int> _freeSpeedTreeSlots = new();
        private int[] _protoSpeedTreeSlotIndex; // protoIndex -> speedtree slot, -1 if not speedtree

        // constants
        private const int k_poolSize = 16; // Must equal (1 << _PoolSizeShift) in GPUBatcher_Common.hlsl.
        internal static int instancesPerPool => k_poolSize;
        private const int k_sizeOfPackedMatrix = sizeof(float) * 4 * 3;
        private const int k_shFloat4sPerInstance = 8;                    // 7 SH + 1 ProbesOcclusion, packed as unity_SHCoefficients
        private const int k_shBytesPerInstance = k_shFloat4sPerInstance * 16;
        internal static int shBytesPerInstance => k_shBytesPerInstance;
        private const int k_speedTreeParamsPerProto = 128; // 32 float4s, 512 bytes per prototype

        internal bool lightProbesEnabled => _lightProbes != null;

        // max work groups per dispatch dimension (D3D11/D3D12/Vulkan/GLES3.1)
        private const int k_dispatchMaxX = 65535;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DispatchSafe(ComputeShader cs, int kernel, int groupCount)
        {
            if (groupCount <= 0) return;
            if (groupCount <= k_dispatchMaxX)
                cs.Dispatch(kernel, groupCount, 1, 1);
            else
            {
                int y = (groupCount + k_dispatchMaxX - 1) / k_dispatchMaxX;
                cs.Dispatch(kernel, k_dispatchMaxX, y, 1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DispatchSafe(CommandBuffer cmd, ComputeShader cs, int kernel, int groupCount)
        {
            if (groupCount <= 0) return;
            if (groupCount <= k_dispatchMaxX)
                cmd.DispatchCompute(cs, kernel, groupCount, 1, 1);
            else
            {
                int y = (groupCount + k_dispatchMaxX - 1) / k_dispatchMaxX;
                cmd.DispatchCompute(cs, kernel, k_dispatchMaxX, y, 1);
            }
        }

        /// <summary>
        /// Whether the current graphics API requires Raw | IndirectArguments buffers.
        /// Currently only DX12 needs this (AMD AoSoA corruption + Intel Arc rejection with Structured).
        /// All other platforms (DX11, PS5, Vulkan, Metal, etc.) use Structured | IndirectArguments.
        /// </summary>
        internal static bool useRawIndirectArgs => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;

        // grow _protoSpeedTreeSlotIndex to cover [0, needed); new entries default to -1
        private void EnsureProtoSpeedTreeSlotIndexCapacity(int needed)
        {
            int oldLen = _protoSpeedTreeSlotIndex?.Length ?? 0;
            if (oldLen >= needed)
                return;

            Array.Resize(ref _protoSpeedTreeSlotIndex, needed);
            for (int i = oldLen; i < needed; i++) _protoSpeedTreeSlotIndex[i] = -1;
        }

        internal NativeArray<bool> poolInUse => _poolInUse;
        internal NativeArray<ulong> poolSlotMask => _poolSlotMask;
        public GraphicsBuffer matrixBuffer => _matrixBuffer;

        internal void DispatchCopyBufferRegion(GraphicsBuffer src, int srcOffsetBytes, GraphicsBuffer dst, int dstOffsetBytes, int sizeBytes)
        {
            GPUCopyBufferRegion(src, srcOffsetBytes, dst, dstOffsetBytes, sizeBytes);
        }

        #endregion

        #region initialization

        private static int FindKernelSafe(ComputeShader cs, string name)
        {
            return cs.HasKernel(name) ? cs.FindKernel(name) : -1;
        }

        private void Initialize()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(GPUBatcherRenderer));

            if (_initialized) return;

            _crossfadeTimeScale = 1f;
            _protoFadeElapsed = 0;

            if (GPUBatcherConfig.instance == null)
                return;

            _cullingShader = _config.cullingShader;
            _uploadShader = _config.uploadShader;

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
            var pickingShader = _config.pickingShader;
            if (pickingShader != null)
            {
                _pickingMaterial = new Material(pickingShader);
                _pickingMaterial.hideFlags = HideFlags.HideAndDontSave;
                _brg.SetPickingMaterial(_pickingMaterial);
            }
#endif

            if (_cullingShader != null)
            {
                _finalizeDrawArgsKernel = FindKernelSafe(_cullingShader, "FinalizeDrawArgs");

                // shadow culling kernels
                _finalizeShadowDrawArgsKernel = FindKernelSafe(_cullingShader, "FinalizeShadowDrawArgs");

                // chunk kernels
                _cullChunksKernel = FindKernelSafe(_cullingShader, "CullChunks");
                _cullInstancesKernel = FindKernelSafe(_cullingShader, "CullInstances");
                _cullChunksShadowKernel = FindKernelSafe(_cullingShader, "CullChunksShadow");
                _cullInstancesShadowKernel = FindKernelSafe(_cullingShader, "CullInstancesShadow");

                // combined reset kernel
                _resetCullingStateKernel = FindKernelSafe(_cullingShader, "ResetCullingState");

                // multi_compile keywords
                _hzbEnabledKeyword = new LocalKeyword(_cullingShader, "HZB_ENABLED");
                _rawIndirectArgsKeyword = new LocalKeyword(_cullingShader, "RAW_INDIRECT_ARGS");
                if (useRawIndirectArgs)
                    _cullingShader.EnableKeyword(_rawIndirectArgsKeyword);

                bool hasCameraKernels = _finalizeDrawArgsKernel >= 0 && _cullChunksKernel >= 0 && _cullInstancesKernel >= 0;
                bool hasShadowKernels = _finalizeShadowDrawArgsKernel >= 0 && _cullChunksShadowKernel >= 0 && _cullInstancesShadowKernel >= 0;

                if (!hasCameraKernels)
                {
                    Debug.LogError("GPUBatcherRenderer: Missing camera culling compute shader kernels!");
                    _cullingShader = null;
                }
                else if (!hasShadowKernels)
                    Debug.LogWarning("GPUBatcherRenderer: Missing shadow culling kernels - shadows disabled");
            }

            if (_uploadShader != null)
            {
                // animated crossfade kernels
                _updateFadeStatesPerInstanceKernel = FindKernelSafe(_uploadShader, "UpdateFadeStatesPerInstance");
                _updateFadeStatesPerPrototypeKernel = FindKernelSafe(_uploadShader, "UpdateFadeStatesPerPrototype");

                // GPU buffer copy kernel
                _copyBufferRegionKernel = FindKernelSafe(_uploadShader, "CopyBufferRegion");

                // typed upload kernels
                _initFadeStateTypedKernel = FindKernelSafe(_uploadShader, "InitFadeStateTyped");
                _copyChunkRegionKernel = FindKernelSafe(_uploadShader, "CopyChunkRegion");
                _copyFadeStateRegionKernel = FindKernelSafe(_uploadShader, "CopyFadeStateRegion");
                _scatterWriteChunksKernel = FindKernelSafe(_uploadShader, "ScatterWriteChunks");
                _settleMotionVectorsKernel = FindKernelSafe(_uploadShader, "SettleMotionVectors");
                _computeMatricesBatchedKernel = FindKernelSafe(_uploadShader, "ComputeMatricesFromTRSBatched");

                // batched dispatch kernels
                _batchUploadInstancesKernel = FindKernelSafe(_uploadShader, "BatchUploadInstances");
                _trsPatchInstancesKernel = FindKernelSafe(_uploadShader, "TRSPatchInstances");
                _batchComputeMatricesKernel = FindKernelSafe(_uploadShader, "BatchComputeMatrices");
                _batchClearInstancesKernel = FindKernelSafe(_uploadShader, "BatchClearInstances");
                _batchUploadIntsKernel = FindKernelSafe(_uploadShader, "BatchUploadInts");
                _batchInitFadeStatesKernel = FindKernelSafe(_uploadShader, "BatchInitFadeStates");
                _copyInstanceBufferKernel = FindKernelSafe(_uploadShader, "CopyInstanceBuffer");
                _scatterWriteColorsKernel = FindKernelSafe(_uploadShader, "ScatterWriteColors");
                _scatterWriteSigOverridesKernel = FindKernelSafe(_uploadShader, "ScatterWriteSigOverrides");
                _scatterWriteSignaturesKernel = FindKernelSafe(_uploadShader, "ScatterWriteSignatures");
                _copyRotScaleBufferKernel = FindKernelSafe(_uploadShader, "CopyRotScaleBuffer");

                // floating origin shift kernels
                _shiftAllInstancesKernel = FindKernelSafe(_uploadShader, "ShiftAllInstances");
                _shiftAllChunksKernel = FindKernelSafe(_uploadShader, "ShiftAllChunks");

                bool hasAnimatedCrossfadeKernels = _updateFadeStatesPerInstanceKernel >= 0 &&
                    _updateFadeStatesPerPrototypeKernel >= 0;

                if (!hasAnimatedCrossfadeKernels)
                    Debug.LogWarning("GPUBatcherRenderer: Missing animated crossfade kernels - animated crossfade disabled");
                else
                    LogMessageDevelopment("GPUBatcherRenderer: Hierarchical GPU culling initialized (camera + shadows + animated crossfade)");
            }

            _cullingCmd = new CommandBuffer { name = "FraxinusGPUBatcher Culling" };

            if (GPUBatcherConfig.instance.supportLegacyLightProbes)
                _lightProbes = new GPUBatcherLightProbes();

            // native transform change tracking for GPUBatcherGameObjectLink
            GPUBatcherObjectChangeTracker.Initialize();
            GPUBatcherObjectChangeTracker.TrackTransforms<GPUBatcherGameObjectLink>();
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                GPUBatcherObjectChangeTracker.TrackType<MeshRenderer>();
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
            InitializeBakedOcclusion();

            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

            InitHZB();

            try
            {
                InitializeChunkSystem();
            }
            catch (Exception ex)
            {
                Cleanup();
                Debug.LogException(ex);
            }
        }

        #endregion

        #region render cycle callbacks

        private float _lastRenderCycleTime;
        private float _realDeltaTime;
        private double _protoFadeElapsed;
        private float _crossfadeTimeScale = 1f;
        public float crossfadeTimeScale
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
#endif

        // called once per frame from Application.onBeforeRender
        private void BeforeRender()
        {
            if (!_initialized)
                return;

#if DEBUG
            using var _su = s_markerSystemUpdate.Auto();
#endif

            SystemUpdate();
            _gameCamPositions.Clear();
        }

        // Flush before every render; advance counters, eviction and compaction once per frame.
        // Scene and game views can invoke this at different times in the same frame.
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

#if UNITY_EDITOR
            // merge camera accumulators -> published positions
            if (!_config.debugDisableCulling)
            {
                _streamingCameraPositions.Clear();
                _streamingCameraPositions.AddRange(_gameCamPositions.AsArray());

                // exclude scene view when debugVisualizeCullingInSceneView is active and game cameras exist
                if (!_config.debugVisualizeCullingInSceneView || _gameCamPositions.Length == 0)
                    _streamingCameraPositions.AddRange(_sceneViewCamPositions.AsArray());
            }
#endif

            // fire streaming event
            onStreamingPositionsReady?.Invoke();

            // apply native transform changes from tracked GPUBatcherGameObjectLink components
            ProcessTrackedTransformChanges();
#if UNITY_EDITOR
            if (!Application.isPlaying)
                ProcessTrackedRendererChanges();
#endif

#if DEBUG
            using var _plu = s_markerPreRender.Auto();
#endif

            ProcessPendingPrototypePurges();

            bool batchesRebuiltThisFrame = false;

#if UNITY_EDITOR
            // auto-rebuild batches when shadow crossfade cascade count changes
            if (_cachedMaxShadowCrossfadeCascade != _config.maxShadowCrossfadeCascade)
            {
                _cachedMaxShadowCrossfadeCascade = _config.maxShadowCrossfadeCascade;
                if (_chunkSystemInitialized && _renderSignatures.Count > 0)
                    RequestBatchRebuild(BatchRebuildScope.FullRebuild);
            }
            // auto-rebuild batches when negative scale mode changes
            if (_cachedNegativeScaleMode != _config.negativeScaleMode)
            {
                _cachedNegativeScaleMode = _config.negativeScaleMode;
                if (_chunkSystemInitialized && _renderSignatures.Count > 0)
                    RequestBatchRebuild(BatchRebuildScope.FullRebuild);
            }
#endif
            // widen shadow buckets to 6 splits on first point light view
            if (_pendingShadowSplitWiden)
            {
                _pendingShadowSplitWiden = false;
                if (_shadowSplitCapacity < k_maxCascades)
                {
                    _shadowSplitCapacity = k_maxCascades;
                    if (_chunkSystemInitialized && _renderSignatures.Count > 0)
                        RequestBatchRebuild(BatchRebuildScope.FullRebuild);
                    LogMessageDevelopment("GPUBatcherRenderer: widening shadow buckets to 6 splits for point light shadows");
                }
            }

            var rebuildScope = _batchRebuildNeeded;
            if (rebuildScope == BatchRebuildScope.FullRebuild || rebuildScope == BatchRebuildScope.IncrementalFlush)
            {
                CachePrototypeBounds();
                if (_chunkSystemInitialized)
                {
                    if (rebuildScope == BatchRebuildScope.FullRebuild)
                        IncFullRebuild();
                    else
                        IncFlush();
                }
                UpdateGPUSignatureBuffer();
                _gpuCullingReady = _chunkSystemInitialized;
                _dirtyBufferBindings |= BufferBindingFlags.Signatures;
                _prototypeBufferDirty = false;
                batchesRebuiltThisFrame = true;
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
            ResetReadbackPendingFlags();
#endif

            // evict stale per-view fade states
            if (_viewFadeStates.Count > 0 && _renderCycleCounter % k_viewFadeEvictFrames == 0)
            {
                _staleViewKeys.Clear();
                for (int i = _viewFadeStateKeys.Count - 1; i >= 0; i--)
                {
                    ViewID key = _viewFadeStateKeys[i];
                    if (_viewFadeStates.TryGetValue(key, out var state) &&
#if UNITY_EDITOR
                        !state.isPersistent &&
#endif
                        _renderCycleCounter - state.lastSeenFrame > k_viewFadeEvictFrames)
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
                        _trackedTotalFadeStates -= staleState.fadeStateBuffer?.count ?? 0;
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
            if (_config.enableAutoCompaction && _chunkSystemInitialized && !batchesRebuiltThisFrame)
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
                        _compactionCooldown = k_compactionCooldownCycles; // grace frame done - start cooldown
                    else if (compacted)
                        _compactionCooldown = -1;  // compaction fired - allow one grace frame first
                }
            }
            else if (!_config.enableAutoCompaction || !_chunkSystemInitialized)// when batchesRebuiltThisFrame skipped compaction, preserve cooldown from ResizePoolBuffer
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
            if (!_config.debugDisableCulling)
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
            CaptureSpeedTreeWind(ctx);
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
            if (isGPUOcclusionEnabled)
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
            camData.isSceneView = camera.cameraType == CameraType.SceneView;
            if (camData.isSceneView)
                fadeState.isPersistent = true;
            if (_config.debugVisualizeCullingInSceneView)
            {
                var reference = GPUBatcherDebugCameras.referenceCamera;
                if (reference != null)
                    camData.referenceCameraId = GPUBatcherHelpers.GetViewID(reference);
            }
#endif
            fadeState.lastSeenFrame = _renderCycleCounter;
            camData.viewFadeState = fadeState;

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
            if (!_initialized)
                return;

            RequestSnapAllViews();
        }

        private void ProcessTrackedTransformChanges()
        {
            using ChangeTrackerTransformData changes = GPUBatcherObjectChangeTracker.GetTransformChanges<GPUBatcherGameObjectLink>(
                Unity.Collections.Allocator.TempJob);

            GPUBatcherGameObjectLink.TryProcessTransformChanges(changes, this);
        }

#if UNITY_EDITOR
        private void ProcessTrackedRendererChanges()
        {
            using ChangeTrackerTypeData changes = GPUBatcherObjectChangeTracker.GetTypeChanges<MeshRenderer>(Unity.Collections.Allocator.TempJob);

            if (!Application.isPlaying)
                GPUBatcherGameObjectLink.TryProcessRendererChanges(changes);
        }

        private void EditorDrainRendererChanges()
        {
            if (Application.isPlaying || !_initialized || !GPUBatcherObjectChangeTracker.isInitialized)
                return;

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
            UnityEditor.EditorApplication.update -= EditorDrainRendererChanges;
            UnityEditor.EditorApplication.update -= ResetUpdateFlag;
#endif

            _gpuCullingReady = false; // prevent OnPerformCulling from dispatching during teardown
            DrainDeferredVisibilitySlots();
            _deferredContexts.Clear();
            _deferredCullingCameras.Clear();
            _deferredUseHZB.Clear();
            _cameraRenderData.Clear();

            GPUBatcherObjectChangeTracker.Dispose();
            CleanupBakedOcclusion();

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
            _cullingShader = null;
            _uploadShader = null;
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
            if (!_initialized) return;

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
        public NativeList<float3> streamingCameraPositions => _streamingCameraPositions;
        #else
        public NativeList<float3> streamingCameraPositions => _gameCamPositions;
        #endif
        public event Action onStreamingPositionsReady;

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
            if (!_chunkSystemInitialized || _uploadShader == null)
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

            int totalSlots = _poolCapacity * k_poolSize;
            if (totalSlots <= 0)
                return;

            // shift instance slots
            if (_shiftAllInstancesKernel >= 0)
            {
                _uploadShader.SetVector(s_idShiftOffset, (Vector3)(float3)offset);
                _uploadShader.SetInt(s_idShiftCount, totalSlots);
                _uploadShader.SetInt(s_idShiftTotalSlots, totalSlots);
                _uploadShader.SetBuffer(_shiftAllInstancesKernel, s_idInstancesRW, _instanceBuffer);
                _uploadShader.SetBuffer(_shiftAllInstancesKernel, s_idCopyDst, _matrixBuffer);

                DispatchSafe(_uploadShader, _shiftAllInstancesKernel, (totalSlots + 255) / 256);
            }

            // shift chunk bounds
            if (_shiftAllChunksKernel >= 0 && _chunkHighWaterMark > 0)
            {
                _uploadShader.SetVector(s_idShiftOffset, (Vector3)(float3)offset);
                _uploadShader.SetInt(s_idShiftCount, _chunkHighWaterMark);
                _uploadShader.SetBuffer(_shiftAllChunksKernel, s_idChunkScatterDst, _chunkBuffer);

                DispatchSafe(_uploadShader, _shiftAllChunksKernel, (_chunkHighWaterMark + 63) / 64);
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
            int oldCapacity = _poolCapacity;
            if (!TryGetCompactedCapacity(_poolInUse, usedPools, oldCapacity, _initialPoolCapacity,
                out int newCapacity, out int highestUsedPool)) return false;

            ResizePoolBuffer(newCapacity, allowShrink: true);
            LogMessageDevelopment($"GPUBatcherRenderer: Pools compacted ({oldCapacity} -> {newCapacity}, freed {(oldCapacity - newCapacity) * k_poolSize} slots, used={usedPools}, highest={highestUsedPool})");
            return true;
        }

        // try to compact chunk capacity, returns true if compaction occurred
        private bool TryCompactChunks()
        {
            if (!_initialized || !_chunkSystemInitialized) return false;

            int chunkCount = _allocatedCount;
            int chunkCapacity = _maxChunks;
            if (!TryGetCompactedCapacity(_chunkSlotActive, chunkCount, chunkCapacity, _initialChunkCapacity,
                out int newCapacity, out int highestActiveChunk)) return false;

            ResizeChunkCapacity(newCapacity, allowShrink: true);
            LogMessageDevelopment($"GPUBatcherRenderer: Chunks compacted ({chunkCapacity} -> {newCapacity}, used={chunkCount}, highest ID={highestActiveChunk})");
            return true;
        }

        private static bool TryGetCompactedCapacity(NativeArray<bool> usedSlots, int usedCount,
            int capacity, int initialCapacity, out int newCapacity, out int highestUsedSlot)
        {
            newCapacity = capacity;
            highestUsedSlot = -1;

            // Only compact at half capacity or less, retaining all live slot IDs.
            if (usedCount > capacity / 2 || capacity <= initialCapacity) return false;

            highestUsedSlot = capacity - 1;
            while (highestUsedSlot >= 0 && !usedSlots[highestUsedSlot])
                highestUsedSlot--;

            newCapacity = math.max((int)math.ceil(usedCount * 1.25f), initialCapacity);
            newCapacity = math.max(newCapacity, highestUsedSlot + 1);

            // Require at least 25% savings to avoid ping-pong.
            return newCapacity < capacity && newCapacity <= capacity * 3 / 4;
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
