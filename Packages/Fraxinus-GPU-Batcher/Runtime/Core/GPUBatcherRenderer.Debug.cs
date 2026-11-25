// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEngine;
#if DEBUG
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine.Rendering;
using BatchDrawCommandIndirect = Fraxinus.GPUBatcher.Rendering.BatchDrawCommandIndirect;
#endif

#if UNITY_6000_4_OR_NEWER
using ViewID = UnityEngine.EntityId;
#else
using ViewID = System.Int32;
#endif

namespace Fraxinus.GPUBatcher
{
    public partial class GPUBatcherRenderer
    {
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogMessageDevelopment(string message)
        {
#if DEBUG
            if (GPUBatcherConfig.instance != null && GPUBatcherConfig.instance.enableDebugLogging)
                Debug.Log(message);
#endif
        }

#if DEBUG

#if UNITY_EDITOR

        // editor integration hooks
        public static Action<GameObject> editorOnPrototypeDirty;

        /// <summary>fires after FlushStagedUploads completes.</summary>
        public event Action onPostFlushStagedUploads;

        // called from GPUBatcherPrefabPostprocessor when prefab assets change
        public static void MarkPrototypeDirty(GameObject prefab)
            => editorOnPrototypeDirty?.Invoke(prefab);

        // per-camera rendered tri/vert counts via GPU readback
        private bool _computeRenderedTriangles;
        public bool computeRenderedTriangles
        {
            get => _computeRenderedTriangles;
            set => _computeRenderedTriangles = value;
        }

        internal struct PerCameraMeshStats
        {
            public long cameraTris, cameraVerts, cameraInstances, cameraGpuChunks;
            public bool cameraPending;
        }

        internal struct PerLightShadowStats
        {
            public long tris, verts, instances, gpuChunks;
            public long[] cascadeInstances;
            public bool pending;
        }

        private readonly Dictionary<ViewID, PerCameraMeshStats> _perCameraMeshStats = new();
        internal IReadOnlyDictionary<ViewID, PerCameraMeshStats> perCameraMeshStatsMap => _perCameraMeshStats;
        private readonly Dictionary<ViewID, PerLightShadowStats> _perLightShadowStats = new();
        internal IReadOnlyDictionary<ViewID, PerLightShadowStats> perLightShadowStatsMap => _perLightShadowStats;
        private readonly List<ViewID> _readbackStatKeyScratch = new();

        // Reset once per frame; snapshot keys before writing values back to each dictionary.
        private void ResetReadbackPendingFlags()
        {
            if (!_computeRenderedTriangles) return;

            _readbackStatKeyScratch.Clear();
            _readbackStatKeyScratch.AddRange(_perCameraMeshStats.Keys);
            foreach (var key in _readbackStatKeyScratch)
            {
                var stats = _perCameraMeshStats[key];
                stats.cameraPending = false;
                _perCameraMeshStats[key] = stats;
            }

            _readbackStatKeyScratch.Clear();
            _readbackStatKeyScratch.AddRange(_perLightShadowStats.Keys);
            foreach (var key in _readbackStatKeyScratch)
            {
                var stats = _perLightShadowStats[key];
                stats.pending = false;
                _perLightShadowStats[key] = stats;
            }
            _readbackStatKeyScratch.Clear();
        }

        private int GetVertexCountForCommand(Unity.Collections.NativeArray<BatchDrawCommandIndirect> templates, int cmdIdx)
        {
            if (!templates.IsCreated || cmdIdx >= templates.Length) return 0;
            var meshID = templates[cmdIdx].meshID;
            return _meshIdReverse.TryGetValue(meshID, out var mesh) && mesh != null ? mesh.vertexCount : 0;
        }

        private void ComputeReadbackTotals(AsyncGPUReadbackRequest request, bool isShadow,
            out long tris, out long verts, out long instances, long[] perCascade = null)
        {
            tris = 0; verts = 0; instances = 0;
            if (request.hasError) return;
            var data = request.GetData<uint>();
            var ranges = isShadow ? _shdRanges : _camRanges;
            int rangeCount = isShadow ? _shdRangeCount : _camRangeCount;
            var templates = isShadow ? _shadowDrawCommandTemplate : _cameraDrawCommandTemplate;
            for (int ri = 0; ri < rangeCount; ri++)
            {
                var range = ranges[ri];
                for (int j = range.begin; j < range.begin + range.count; j++)
                {
                    if (j * 5 + 1 < data.Length)
                    {
                        long instanceCount = data[j * 5 + 1];
                        tris += (long)data[j * 5 + 0] * instanceCount / 3;
                        verts += (long)GetVertexCountForCommand(templates, j) * instanceCount;
                        instances += instanceCount;

                        if (perCascade != null && _shadowCommandFilterInfo.IsCreated && j < _shadowCommandFilterInfo.Length)
                        {
                            int c = _shadowCommandFilterInfo[j].cascadeIndex;
                            if (c >= 0 && c < perCascade.Length)
                                perCascade[c] += instanceCount;
                        }
                    }
                }
            }
        }

        internal void RequestCameraReadback(CommandBuffer cmd, CullingBufferSet viewSet, ViewID cameraId)
        {
            if (!_computeRenderedTriangles) return;
            if (_perCameraMeshStats.TryGetValue(cameraId, out var stats) && stats.cameraPending) return;
            stats.cameraPending = true;
            _perCameraMeshStats[cameraId] = stats;
            EnqueueCameraReadbacks(cmd, viewSet, cameraId);
        }

        // Allocate callback state only when a readback is actually scheduled.
        private void EnqueueCameraReadbacks(CommandBuffer cmd, CullingBufferSet viewSet, ViewID cameraId)
        {
            cmd.RequestAsyncReadback(viewSet.indirectArgsBuffer, (req) =>
            {
                ComputeReadbackTotals(req, false, out long tris, out long verts, out long inst);
                if (_perCameraMeshStats.TryGetValue(cameraId, out var s))
                {
                    s.cameraTris = tris; s.cameraVerts = verts; s.cameraInstances = inst;
                    s.cameraPending = false;
                    _perCameraMeshStats[cameraId] = s;
                }
            });
            cmd.RequestAsyncReadback(viewSet.visibleChunkCountBuffer, (req) =>
            {
                if (req.hasError) return;
                var d = req.GetData<uint>();
                if (d.Length >= 4 && _perCameraMeshStats.TryGetValue(cameraId, out var s))
                {
                    s.cameraGpuChunks = d[3];
                    _perCameraMeshStats[cameraId] = s;
                }
            });
        }

        internal void RequestShadowReadback(CommandBuffer cmd, CullingBufferSet shadowSet, ViewID lightId)
        {
            if (!_computeRenderedTriangles) return;
            if (_perLightShadowStats.TryGetValue(lightId, out var stats) && stats.pending) return;
            stats.pending = true;
            _perLightShadowStats[lightId] = stats;
            EnqueueShadowReadbacks(cmd, shadowSet, lightId);
        }

        private void EnqueueShadowReadbacks(CommandBuffer cmd, CullingBufferSet shadowSet, ViewID lightId)
        {
            var cascades = new long[k_maxCascades];
            cmd.RequestAsyncReadback(shadowSet.indirectArgsBuffer, (req) =>
            {
                ComputeReadbackTotals(req, true, out long tris, out long verts, out long inst, cascades);
                if (_perLightShadowStats.TryGetValue(lightId, out var s))
                {
                    s.tris = tris; s.verts = verts; s.instances = inst;
                    s.cascadeInstances = cascades;
                    s.pending = false;
                    _perLightShadowStats[lightId] = s;
                }
            });
            cmd.RequestAsyncReadback(shadowSet.visibleChunkCountBuffer, (req) =>
            {
                if (req.hasError) return;
                var d = req.GetData<uint>();
                if (d.Length >= 4 && _perLightShadowStats.TryGetValue(lightId, out var s))
                {
                    s.gpuChunks = d[3];
                    _perLightShadowStats[lightId] = s;
                }
            });
        }
#endif

        #region Profiler Markers

        static readonly ProfilerMarker s_markerShadowCulling = new ProfilerMarker("FraxinusGPUBatcher.ShadowCulling");
        static readonly ProfilerMarker s_markerWriteChunk = new ProfilerMarker("FraxinusGPUBatcher.WriteChunk");
        static readonly ProfilerMarker s_markerFreeChunk = new ProfilerMarker("FraxinusGPUBatcher.FreeChunk");
        static readonly ProfilerMarker s_markerResizePoolBuffer = new ProfilerMarker("FraxinusGPUBatcher.ResizePoolBuffer");
        static readonly ProfilerMarker s_markerGPUFenceStall = new ProfilerMarker("FraxinusGPUBatcher.GPUFenceStall");
        static readonly ProfilerMarker s_markerOcclusionCulling = new ProfilerMarker("FraxinusGPUBatcher.OcclusionCulling");
        static readonly ProfilerMarker s_markerPreRender = new ProfilerMarker("FraxinusGPUBatcher.PreRender");
        static readonly ProfilerMarker s_markerSystemUpdate = new ProfilerMarker("FraxinusGPUBatcher.SystemUpdate");
        static readonly ProfilerMarker s_markerFlushStagedUploads = new ProfilerMarker("FraxinusGPUBatcher.FlushStagedUploads");
        static readonly ProfilerMarker s_markerCreateBatches = new ProfilerMarker("FraxinusGPUBatcher.CreateBatches");
        static readonly ProfilerMarker s_markerAllocateChunk = new ProfilerMarker("FraxinusGPUBatcher.AllocateChunk");
        static readonly ProfilerMarker s_markerAllocatePools = new ProfilerMarker("FraxinusGPUBatcher.AllocatePools");
        static readonly ProfilerMarker s_markerResizeChunkCapacity = new ProfilerMarker("FraxinusGPUBatcher.ResizeChunkCapacity");
        static readonly ProfilerMarker s_markerAddInstance = new ProfilerMarker("FraxinusGPUBatcher.AddInstance");
        static readonly ProfilerMarker s_markerAddInstances = new ProfilerMarker("FraxinusGPUBatcher.AddInstances");
        static readonly ProfilerMarker s_markerRemoveInstance = new ProfilerMarker("FraxinusGPUBatcher.RemoveInstance");

        // upload pipeline
        static readonly ProfilerMarker s_markerCommitDeferredWrites = new ProfilerMarker("FraxinusGPUBatcher.CommitDeferredWrites");
        static readonly ProfilerMarker s_markerFlushInstanceUploads = new ProfilerMarker("FraxinusGPUBatcher.FlushInstanceUploads");
        static readonly ProfilerMarker s_markerFlushPatches = new ProfilerMarker("FraxinusGPUBatcher.FlushPatches");
        static readonly ProfilerMarker s_markerFlushPoolRefs = new ProfilerMarker("FraxinusGPUBatcher.FlushPoolRefs");
        static readonly ProfilerMarker s_markerFlushChunkMetadata = new ProfilerMarker("FraxinusGPUBatcher.FlushChunkMetadata");
        static readonly ProfilerMarker s_markerGPUComputeMatrices = new ProfilerMarker("FraxinusGPUBatcher.GPUComputeMatrices");
        static readonly ProfilerMarker s_markerStagePatch = new ProfilerMarker("FraxinusGPUBatcher.StagePatch");

        #endregion

        #region Debug Counters

        private int _totalInstanceCount;

        private int _statTotalFadeStates;

        #endregion

        #region Stat Properties

        public int statInstances { get; private set; }
        public int statChunksUsed { get; private set; }
        public int statChunksFree { get; private set; }
        public int statPoolsUsed { get; private set; }
        public int statPoolsFree { get; private set; }
        public int statPoolSize { get; private set; }
        public float statUtilization { get; private set; }
        public int statOcclusionPass { get; private set; }
        public int statOcclusionFail { get; private set; }
        public int statCameraPoolActive { get; private set; }
        public int statCameraPoolTotal { get; private set; }
        public int statShadowPoolActive { get; private set; }
        public int statShadowPoolTotal { get; private set; }
        public int statFadeStateViews { get; private set; }
        public int statCameraDrawCommands => ActiveCameraCommandCount;
        public int statShadowDrawCommands => ActiveShadowCommandCount;
        public int statChunkHighWaterMark => _chunkHighWaterMark;
        public int statWastedSlots => _chunkSystemInitialized ? (_allocatedPoolCount * k_poolSize - _totalInstanceCount) : 0;
        public int statBatchCount => _batchSlotHWM - (_freeBatchSlots?.Count ?? 0);
        public int statCameraBuckets => _bucketCount;
        public int statShadowBuckets => _shadowBucketCount;
        public bool statAnyAnimatedCrossfade => _anyPrototypeUsesAnimatedCrossfade;
        private int _statAnimatedProtoCount;
        public int statAnimatedProtoCount => _statAnimatedProtoCount;
        public int statTotalFadeStates => _statTotalFadeStates;
        public float statLodFadeDuration => _config.defaultAnimatedCrossfadeDuration;
        public bool statSkipIntermediateLODs => _config.skipIntermediateLODs;
        public bool statDebugDisableCPUChunkCulling => _config.debugDisableCPUChunkCulling;
        public bool statDebugDisableCulling => _config.debugDisableCulling;
        public bool statDebugDisableRendering => _config.debugDisableRendering;
        public int statRegisteredMeshes => _meshIdCache?.Count ?? 0;
        public int statRegisteredMaterials => _materialIdCache?.Count ?? 0;

        public IReadOnlyDictionary<Mesh, BatchMeshID> registeredMeshes => _meshIdCache;
        public IReadOnlyDictionary<Material, BatchMaterialID> registeredMaterials => _materialIdCache;
        public IReadOnlyDictionary<BatchMeshID, int> meshRefCounts => _meshRefCount;
        public IReadOnlyDictionary<BatchMaterialID, int> materialRefCounts => _materialRefCount;

#if UNITY_EDITOR
        public int debugCameraDrawRangeCount => _cameraDrawRangeCount;
        public int debugShadowDrawRangeCount => _shadowDrawRangeCount;

        public string DebugDescribeDrawRange(bool isShadow, int index)
        {
            var ranges = isShadow ? _shdRanges : _camRanges;
            int rangeCount = isShadow ? _shdRangeCount : _camRangeCount;
            if (ranges == null || index < 0 || index >= rangeCount) return "(invalid)";
            var r = ranges[index];
            return $"cmds[{r.begin}..{r.begin + r.count - 1}] " +
                   $"layer={r.layer} mask=0x{r.renderingLayerMask:X} motion={r.motionMode} shadow={r.shadowCastingMode}";
        }

        public void DebugGetDrawRangeSpan(bool isShadow, int index, out int begin, out int count)
        {
            var ranges = isShadow ? _shdRanges : _camRanges;
            int rangeCount = isShadow ? _shdRangeCount : _camRangeCount;
            if (ranges == null || index < 0 || index >= rangeCount) { begin = 0; count = 0; return; }
            begin = ranges[index].begin;
            count = ranges[index].count;
        }

        public string DebugDescribeDrawCommand(bool isShadow, int index)
        {
            var cmd2proto = isShadow ? _shdCmd2Proto : _camCmd2Proto;
            int cmdCount = isShadow ? _shdCmdCount : _camCmdCount;
            if (cmd2proto == null || index < 0 || index >= cmdCount) return "(invalid)";
            int protoIdx = cmd2proto[index];
            if (protoIdx < 0 || protoIdx >= _renderSignatures.Count) return "(invalid proto)";
            var proto = _renderSignatures[protoIdx];
            if (proto == null) return "(null proto)";
            string meshName = proto.asset?.mesh?.name ?? "?";
            return $"proto={protoIdx} mesh={meshName}";
        }
#endif

        #endregion

        #region Stat Update

        private void UpdateStats()
        {
            if (!_chunkSystemInitialized)
            {
                statInstances = 0;
                statChunksUsed = 0;
                statChunksFree = 0;
                statPoolsUsed = 0;
                statPoolsFree = 0;
                statPoolSize = 0;
                statUtilization = 0f;
                statOcclusionPass = 0;
                statOcclusionFail = 0;
                statCameraPoolActive = 0;
                statCameraPoolTotal = 0;
                statShadowPoolActive = 0;
                statShadowPoolTotal = 0;
                statFadeStateViews = 0;
                _statTotalFadeStates = 0;
                return;
            }

            statInstances = _totalInstanceCount;
            statChunksUsed = _allocatedCount;
            statChunksFree = _freeChunkSlots.IsCreated ? _freeChunkSlots.Count : 0;
            statPoolsUsed = _allocatedPoolCount;

            // free pools
            statPoolsFree = _freePools.IsCreated ? _freePools.Length : 0;
            statPoolSize = k_poolSize;

            // utilization
            int allocatedSlots = _allocatedPoolCount * k_poolSize;
            statUtilization = allocatedSlots > 0
                ? (float)_totalInstanceCount / allocatedSlots * 100f
                : 0f;

            // view stats
            statCameraPoolActive = _lastCameraPoolActive;
            statCameraPoolTotal = _cameraBufferPool.Count;
            statShadowPoolActive = _lastShadowPoolActive;
            statShadowPoolTotal = _shadowBufferPool.Count;
            statFadeStateViews = _viewFadeStates.Count;
            _statTotalFadeStates = _trackedTotalFadeStates;

#if UNITY_EDITOR
            if (_config.recordAllocations && Application.isPlaying && (_allocatedCount > 0 || _allocatedPoolCount > 0 || _totalInstanceCount > 0))
                _config.RecordHighWaterMarks(_allocatedCount, _allocatedPoolCount, _totalInstanceCount);
#endif
        }

        #endregion

        #region Diagnostics

        public struct MemoryBreakdown
        {
            public long prototypeData;
            public long instanceData;
            public long matrixData;
            public long chunkData;
            public long poolRefData;
            public long cameraViewBuffers;
            public long shadowViewBuffers;
            public long fadeStateBuffers;
            public long bucketMapping;
            public long stagingBuffers;
            public long total;
        }

        public int GetPrototypeInstanceCount(int prototypeIndex)
        {
            if (!_protoInstanceCounts.IsCreated || prototypeIndex < 0 || prototypeIndex >= _protoInstanceCounts.Length) return 0;
            return _protoInstanceCounts[prototypeIndex];
        }

        // hwm instance count
        public int GetPrototypeInstanceCountHWM(int prototypeIndex)
        {
            if (!_protoInstanceCountHWM.IsCreated || prototypeIndex < 0 || prototypeIndex >= _protoInstanceCountHWM.Length) return 0;
            return _protoInstanceCountHWM[prototypeIndex];
        }

        private static long SumPoolBufferSizes(List<CullingBufferSet> pool)
        {
            if (pool == null) return 0;
            long total = 0;
            foreach (var set in pool)
            {
                total += GetBufferSizeStatic(set.drawBucketBuffer);
                total += GetBufferSizeStatic(set.visibilityBuffer);
                total += GetBufferSizeStatic(set.indirectArgsBuffer);
                total += GetBufferSizeStatic(set.visibleChunksBuffer);
                total += GetBufferSizeStatic(set.visibleChunkCountBuffer);
            }
            return total;
        }

        private static long GetBufferSizeStatic(GraphicsBuffer buffer) =>
            buffer != null ? (long)buffer.count * buffer.stride : 0;

        private static long SumBufferSetSize(CullingBufferSet set) =>
            GetBufferSizeStatic(set.drawBucketBuffer) + GetBufferSizeStatic(set.visibilityBuffer) +
            GetBufferSizeStatic(set.indirectArgsBuffer) + GetBufferSizeStatic(set.visibleChunksBuffer) +
            GetBufferSizeStatic(set.visibleChunkCountBuffer);

        public MemoryBreakdown GetMemoryBreakdown()
        {
            var breakdown = new MemoryBreakdown();
            if (!_initialized) return breakdown;

            long GetBufferSize(GraphicsBuffer buffer) => buffer != null ? (long)buffer.count * buffer.stride : 0;

            breakdown.prototypeData = GetBufferSize(_gpuSignatureBuffer);

            breakdown.instanceData = GetBufferSize(_instanceBuffer) + GetBufferSize(_instanceRotScaleBuffer);
            breakdown.matrixData = GetBufferSize(_matrixBuffer);
            breakdown.chunkData = GetBufferSize(_chunkBuffer);
            breakdown.poolRefData = GetBufferSize(_poolRefBuffer);

            breakdown.cameraViewBuffers = SumPoolBufferSizes(_cameraBufferPool);
            breakdown.shadowViewBuffers = SumPoolBufferSizes(_shadowBufferPool);
#if UNITY_EDITOR
            if (_sceneViewCameraBufferSet != null)
                breakdown.cameraViewBuffers += SumBufferSetSize(_sceneViewCameraBufferSet);
            foreach (var set in _sceneViewShadowBufferSets)
                breakdown.shadowViewBuffers += SumBufferSetSize(set);
#endif

            if (_viewFadeStates != null)
            {
                foreach (var kvp in _viewFadeStates)
                    breakdown.fadeStateBuffers += GetBufferSize(kvp.Value.fadeStateBuffer);
            }

            breakdown.bucketMapping = GetBufferSize(_argsToBucketBuffer) + GetBufferSize(_shadowArgsToBucketBuffer);

            breakdown.prototypeData += GetBufferSize(_protoCrossfadeBuffer);

            breakdown.stagingBuffers = GetBufferSize(_stagingInstanceBuffer) + GetBufferSize(_trsPatchStagingBuffer);

            breakdown.total = breakdown.prototypeData + breakdown.instanceData + breakdown.matrixData +
                             breakdown.chunkData + breakdown.poolRefData + breakdown.cameraViewBuffers +
                             breakdown.shadowViewBuffers +
                             breakdown.fadeStateBuffers + breakdown.bucketMapping + breakdown.stagingBuffers;

            return breakdown;
        }

        // per-entry view buffer info for editor display
        public struct ViewBufferEntry
        {
            public string label;
            public long bytes;
        }

        public void GetFadeStateEntries(List<ViewBufferEntry> results)
        {
            results.Clear();
            if (_viewFadeStates == null) return;
            foreach (var kvp in _viewFadeStates)
            {
                long bytes = GetBufferSizeStatic(kvp.Value.fadeStateBuffer);
                string label = $"View {kvp.Key}";
#if UNITY_EDITOR
                if (kvp.Value.isPersistent) label += " (scene view)";
#endif
                results.Add(new ViewBufferEntry { label = label, bytes = bytes });
            }
        }

        public void GetCameraBufferEntries(List<ViewBufferEntry> results)
        {
            results.Clear();
            if (_cameraBufferPool == null) return;
            for (int i = 0; i < _cameraBufferPool.Count; i++)
            {
                var set = _cameraBufferPool[i];
                long bytes = SumBufferSetSize(set);
                string label = i < _lastCameraPoolActive ? $"Camera [{i}] (active)" : $"Camera [{i}] (idle)";
                results.Add(new ViewBufferEntry { label = label, bytes = bytes });
            }
#if UNITY_EDITOR
            if (_sceneViewCameraBufferSet != null)
                results.Add(new ViewBufferEntry { label = "Scene view (persistent)", bytes = SumBufferSetSize(_sceneViewCameraBufferSet) });
#endif
        }

        public void GetShadowBufferEntries(List<ViewBufferEntry> results)
        {
            results.Clear();
            if (_shadowBufferPool == null) return;
            for (int i = 0; i < _shadowBufferPool.Count; i++)
            {
                var set = _shadowBufferPool[i];
                long bytes = SumBufferSetSize(set);
                string label = i < _lastShadowPoolActive ? $"Shadow [{i}] (active)" : $"Shadow [{i}] (idle)";
                results.Add(new ViewBufferEntry { label = label, bytes = bytes });
            }
#if UNITY_EDITOR
            for (int i = 0; i < _sceneViewShadowCount; i++)
            {
                if (i < _sceneViewShadowBufferSets.Count)
                    results.Add(new ViewBufferEntry { label = $"Scene shadow [{i}] (persistent)", bytes = SumBufferSetSize(_sceneViewShadowBufferSets[i]) });
            }
#endif
        }

        #endregion

#if UNITY_EDITOR
        #region Bounding Sphere Overlay

        private static readonly int s_idDebugSphereList = Shader.PropertyToID("_DebugSphereList");
        private static readonly int s_idDebugSphereArgs = Shader.PropertyToID("_DebugSphereArgs");
        private static readonly int s_idDebugBucketLod = Shader.PropertyToID("_DebugBucketLod");
        private static readonly int s_idDebugSphereIndexCount = Shader.PropertyToID("_DebugSphereIndexCount");
        private static readonly int s_idDebugSphereMaxCount = Shader.PropertyToID("_DebugSphereMaxCount");
        private static readonly int s_idDebugBucketCount = Shader.PropertyToID("_DebugBucketCount");
        private static readonly int s_idDebugMatrices = Shader.PropertyToID("_DebugMatrices");

        private ComputeShader _debugSphereCompute;
        private int _clearDebugArgsKernel = -1;
        private int _buildDebugListKernel = -1;
        private Mesh _debugSphereMesh;
        private Material _debugSphereMaterial;
        private MaterialPropertyBlock _debugSphereMPB;
        private GraphicsBuffer _debugSphereList;
        private GraphicsBuffer _debugSphereArgs;
        private GraphicsBuffer _debugBucketLodBuffer;
        private int _debugSphereListCapacity;
        private bool _prevDebugDrawSpheres;

        // called once per frame from SystemUpdate; submits the overlay draw for this frame
        private void PrepareDebugSpheres()
        {
            bool enabled = _config.debugDrawBoundingSpheres;
            // _camBucketLod is filled in EmitCameraCommands only while enabled; force a rebuild on enable
            if (enabled && !_prevDebugDrawSpheres) MarkBatchesDirtyForFullRebuild();
            _prevDebugDrawSpheres = enabled;

            if (!enabled || !_chunkSystemInitialized
                || _instanceBuffer == null || _gpuSignatureBuffer == null)
                return;
            if (!EnsureDebugSphereResources()) return;

            if (_camBucketLod != null && _bucketCount > 0)
                _debugBucketLodBuffer.SetData(_camBucketLod, 0, 0, math.min(_bucketCount, _camBucketLod.Length));

            _debugSphereMPB.SetBuffer(s_idDebugSphereList, _debugSphereList);
            _debugSphereMPB.SetBuffer(s_idInstances, _instanceBuffer);
            _debugSphereMPB.SetBuffer(s_idSignatures, _gpuSignatureBuffer);
            if (_matrixBuffer != null) _debugSphereMPB.SetBuffer(s_idDebugMatrices, _matrixBuffer);

            var rp = new RenderParams(_debugSphereMaterial)
            {
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 1e7f),
                matProps = _debugSphereMPB,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderMeshIndirect(rp, _debugSphereMesh, _debugSphereArgs, 1);
        }

        // appended to the camera cull command buffer; compacts the visible set after culling
        private void DispatchDebugSphereCompaction(CommandBuffer cmd, CullingBufferSet viewSet)
        {
            if (!_config.debugDrawBoundingSpheres) return;
            if (_debugSphereCompute == null || _debugSphereList == null || _debugSphereArgs == null
                || _debugBucketLodBuffer == null || _debugSphereMesh == null)
                return;
            if (_clearDebugArgsKernel < 0 || _buildDebugListKernel < 0) return;
            if (viewSet?.drawBucketBuffer == null || viewSet.visibilityBuffer == null) return;

            var cs = _debugSphereCompute;
            cmd.SetComputeIntParam(cs, s_idDebugSphereIndexCount, (int)_debugSphereMesh.GetIndexCount(0));
            cmd.SetComputeBufferParam(cs, _clearDebugArgsKernel, s_idDebugSphereArgs, _debugSphereArgs);
            cmd.DispatchCompute(cs, _clearDebugArgsKernel, 1, 1, 1);

            cmd.SetComputeIntParam(cs, s_idDebugSphereMaxCount, _debugSphereListCapacity);
            cmd.SetComputeIntParam(cs, s_idDebugBucketCount, _bucketCount);
            cmd.SetComputeBufferParam(cs, _buildDebugListKernel, s_idDrawBuckets, viewSet.drawBucketBuffer);
            cmd.SetComputeBufferParam(cs, _buildDebugListKernel, s_idVisibilityBuffer, viewSet.visibilityBuffer);
            cmd.SetComputeBufferParam(cs, _buildDebugListKernel, s_idDebugBucketLod, _debugBucketLodBuffer);
            cmd.SetComputeBufferParam(cs, _buildDebugListKernel, s_idDebugSphereList, _debugSphereList);
            cmd.SetComputeBufferParam(cs, _buildDebugListKernel, s_idDebugSphereArgs, _debugSphereArgs);
            cmd.DispatchCompute(cs, _buildDebugListKernel, math.max(1, _bucketCount), 1, 1);
        }

        private bool EnsureDebugSphereResources()
        {
            if (_debugSphereCompute == null)
            {
                _debugSphereCompute = LoadDebugSphereCompute();
                _clearDebugArgsKernel = -1;
                _buildDebugListKernel = -1;
            }
            if (_debugSphereCompute == null) return false;

            if (_debugSphereMaterial == null)
            {
                var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(
                    "Packages/com.bluewhitebow.fraxinus-gpu-batcher/Editor/Shaders/DebugBoundingSphere.shader");
                if (shader == null) return false;
                _debugSphereMaterial = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    enableInstancing = true
                };
            }
            if (_debugSphereMesh == null) _debugSphereMesh = BuildDebugIcosphere();
            _debugSphereMPB ??= new MaterialPropertyBlock();
            if (_clearDebugArgsKernel < 0) _clearDebugArgsKernel = _debugSphereCompute.FindKernel("ClearDebugSphereArgs");
            if (_buildDebugListKernel < 0) _buildDebugListKernel = _debugSphereCompute.FindKernel("BuildDebugSphereList");

            int cap = math.max(1, _maxVisibleInstances);
            if (_debugSphereList == null || _debugSphereListCapacity < cap)
            {
                _debugSphereList?.Dispose();
                _debugSphereList = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(uint));
                _debugSphereListCapacity = cap;
            }
            if (_debugSphereArgs == null)
            {
                _debugSphereArgs = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, 1, 5 * sizeof(uint));
                _debugSphereArgs.SetData(new uint[] { _debugSphereMesh.GetIndexCount(0), 0, 0, 0, 0 });
            }
            int bucketCap = math.max(1, _bucketCount);
            if (_debugBucketLodBuffer == null || _debugBucketLodBuffer.count < bucketCap)
            {
                _debugBucketLodBuffer?.Dispose();
                _debugBucketLodBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bucketCap, sizeof(uint));
            }
            return true;
        }

        private static ComputeShader LoadDebugSphereCompute()
        {
            return UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.bluewhitebow.fraxinus-gpu-batcher/Editor/Shaders/GPUBatcherDebugBoundingSphere.compute");
        }

        private void CleanupDebugSpheres()
        {
            _debugSphereList?.Dispose(); _debugSphereList = null;
            _debugSphereArgs?.Dispose(); _debugSphereArgs = null;
            _debugBucketLodBuffer?.Dispose(); _debugBucketLodBuffer = null;
            _debugSphereListCapacity = 0;
            _prevDebugDrawSpheres = false;
            _clearDebugArgsKernel = -1;
            _buildDebugListKernel = -1;
            _debugSphereCompute = null;
            if (_debugSphereMaterial != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_debugSphereMaterial);
                else UnityEngine.Object.DestroyImmediate(_debugSphereMaterial);
                _debugSphereMaterial = null;
            }
            if (_debugSphereMesh != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_debugSphereMesh);
                else UnityEngine.Object.DestroyImmediate(_debugSphereMesh);
                _debugSphereMesh = null;
            }
        }

        private const int k_debugSphereSubdivisions = 2;

        // unit icosphere subdivided kDebugSphereSubdivisions times; normals = positions
        private static Mesh BuildDebugIcosphere()
        {
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var verts = new List<Vector3>
            {
                new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
                new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
                new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
            };
            for (int i = 0; i < verts.Count; i++) verts[i] = verts[i].normalized;
            var tris = new List<int>
            {
                0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
                1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
                4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1,
            };
            var mid = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (mid.TryGetValue(key, out var m)) return m;
                m = verts.Count;
                verts.Add(((verts[a] + verts[b]) * 0.5f).normalized);
                mid[key] = m;
                return m;
            }
            for (int s = 0; s < k_debugSphereSubdivisions; s++)
            {
                mid.Clear();
                var next = new List<int>(tris.Count * 4);
                for (int i = 0; i < tris.Count; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    next.Add(a); next.Add(ab); next.Add(ca);
                    next.Add(b); next.Add(bc); next.Add(ab);
                    next.Add(c); next.Add(ca); next.Add(bc);
                    next.Add(ab); next.Add(bc); next.Add(ca);
                }
                tris = next;
            }
            var mesh = new Mesh { name = "GPUBatcherDebugIcosphere", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.SetNormals(verts);
            mesh.RecalculateBounds();
            return mesh;
        }

        #endregion

        #region Upload Validator

        // cpu mirror of gpu instance state, applied from the same command lists the
        // flush dispatches - a mismatch means the gpu upload pipeline diverged from
        // staged intent. verifies rotScale fully, sigDensityPacked (sig bits always,
        // density bits until a patch makes them gpu-derived), per-instance color, and
        // pool refs. positionOrBoundsCenter/padOrMaxScale are matrix-kernel outputs - skipped.

        public enum DebugUploadOp : byte { None, Upload, Clear, TRSPatch, SigOverride, Color }

        private struct DebugSlotWrite
        {
            public uint cycle;
            public DebugUploadOp op;
        }

        private bool _uploadValidationEnabled;
        private NativeArray<GPUInstanceRotScaleData> _mirrorRotScale;
        private NativeArray<uint> _mirrorPacked;
        private NativeArray<byte> _mirrorDensityUnknown;
        private NativeArray<uint> _mirrorColor;
        private NativeArray<DebugSlotWrite> _mirrorLastWrite;
        private NativeArray<DebugSlotWrite> _mirrorPrevWrite;
        private NativeArray<uint> _mirrorApplyLast;
        private NativeArray<byte> _mirrorRaced;
        private uint _mirrorApplyStamp;
        private int _uploadValidationCounter;

        /// <summary>flush passes between automatic validations. 0 = manual only.</summary>
        public int debugUploadValidationInterval { get; set; } = 30;
        public int statUploadValidationMismatches { get; private set; }
        public int statUploadValidationPasses { get; private set; }

        /// <summary>
        /// Editor-only GPU upload validation. While enabled, a CPU mirror tracks every
        /// upload/clear/patch/override/color command at dispatch time and is compared
        /// against a GPU readback every DebugUploadValidationInterval render cycles.
        /// </summary>
        public bool debugUploadValidation
        {
            get => _uploadValidationEnabled;
            set
            {
                if (_uploadValidationEnabled == value) return;
                _uploadValidationEnabled = value;
                if (!value) DisposeUploadValidator();
            }
        }

        private void DisposeUploadValidator()
        {
            _mirrorRotScale.DisposeIfCreated();
            _mirrorPacked.DisposeIfCreated();
            _mirrorDensityUnknown.DisposeIfCreated();
            _mirrorColor.DisposeIfCreated();
            _mirrorLastWrite.DisposeIfCreated();
            _mirrorPrevWrite.DisposeIfCreated();
            _mirrorApplyLast.DisposeIfCreated();
            _mirrorRaced.DisposeIfCreated();
        }

        // allocate + seed from current gpu state so mid-session enabling starts clean
        private bool EnsureUploadValidator()
        {
            if (!_uploadValidationEnabled || !_chunkSystemInitialized || _uploadShader == null) return false;
            int totalSlots = _poolCapacity * k_poolSize;
            if (_mirrorRotScale.IsCreated && _mirrorRotScale.Length == totalSlots) return true;

            DisposeUploadValidator();
            _mirrorRotScale = new NativeArray<GPUInstanceRotScaleData>(totalSlots, Allocator.Persistent);
            _mirrorPacked = new NativeArray<uint>(totalSlots, Allocator.Persistent);
            _mirrorDensityUnknown = new NativeArray<byte>(totalSlots, Allocator.Persistent);
            _mirrorColor = new NativeArray<uint>(totalSlots, Allocator.Persistent);
            _mirrorLastWrite = new NativeArray<DebugSlotWrite>(totalSlots, Allocator.Persistent);
            _mirrorPrevWrite = new NativeArray<DebugSlotWrite>(totalSlots, Allocator.Persistent);
            _mirrorApplyLast = new NativeArray<uint>(totalSlots, Allocator.Persistent);
            _mirrorRaced = new NativeArray<byte>(totalSlots, Allocator.Persistent);

            var rotReq = AsyncGPUReadback.Request(_instanceRotScaleBuffer);
            var instReq = AsyncGPUReadback.Request(_instanceBuffer);
            rotReq.WaitForCompletion();
            instReq.WaitForCompletion();
            if (rotReq.hasError || instReq.hasError)
            {
                Debug.LogError("[GPUBatcherUploadValidator] seed readback failed - validator disabled");
                _uploadValidationEnabled = false;
                DisposeUploadValidator();
                return false;
            }
            rotReq.GetData<GPUInstanceRotScaleData>().CopyTo(_mirrorRotScale);
            var inst = instReq.GetData<GPUInstanceData>();
            for (int i = 0; i < totalSlots; i++)
                _mirrorPacked[i] = inst[i].sigDensityPacked;
            if (_perInstanceColorEnabled && _matrixBuffer != null)
            {
                var colReq = AsyncGPUReadback.Request(_matrixBuffer, totalSlots * 4, _colorRegionByteOffset);
                colReq.WaitForCompletion();
                if (colReq.hasError)
                {
                    Debug.LogError("[GPUBatcherUploadValidator] seed readback failed - validator disabled");
                    _uploadValidationEnabled = false;
                    DisposeUploadValidator();
                    return false;
                }
                colReq.GetData<uint>().CopyTo(_mirrorColor);
            }
            return true;
        }

        // preserve mirror + ledger across pool buffer resizes
        private void DebugMirrorOnPoolResize(int newTotalSlots)
        {
            if (!_mirrorRotScale.IsCreated || _mirrorRotScale.Length == newTotalSlots) return;
            ResizeMirrorArray(ref _mirrorRotScale, newTotalSlots);
            ResizeMirrorArray(ref _mirrorPacked, newTotalSlots);
            ResizeMirrorArray(ref _mirrorDensityUnknown, newTotalSlots);
            ResizeMirrorArray(ref _mirrorColor, newTotalSlots);
            ResizeMirrorArray(ref _mirrorLastWrite, newTotalSlots);
            ResizeMirrorArray(ref _mirrorPrevWrite, newTotalSlots);
            ResizeMirrorArray(ref _mirrorApplyLast, newTotalSlots);
            ResizeMirrorArray(ref _mirrorRaced, newTotalSlots);
        }

        private static void ResizeMirrorArray<T>(ref NativeArray<T> arr, int newLen) where T : struct
        {
            var next = new NativeArray<T>(newLen, Allocator.Persistent);
            NativeArray<T>.Copy(arr, next, math.min(arr.Length, newLen));
            arr.Dispose();
            arr = next;
        }

        private void DebugMirrorRecordWrite(int slot, DebugUploadOp op)
        {
            _mirrorPrevWrite[slot] = _mirrorLastWrite[slot];
            _mirrorLastWrite[slot] = new DebugSlotWrite { cycle = _renderCycleCounter, op = op };
        }

        // duplicate slots within one scatter dispatch race on the gpu (unordered threads);
        // the mirror keeps list-order-last, so those slots are masked until rewritten
        private void DebugMirrorMarkScatterWrite(int slot)
        {
            if (_mirrorApplyLast[slot] == _mirrorApplyStamp)
            {
                _mirrorRaced[slot] = 1;
                return;
            }
            _mirrorApplyLast[slot] = _mirrorApplyStamp;
            _mirrorRaced[slot] = 0;
        }

        // apply hooks - called at the exact dispatch sites with the same command lists the gpu receives

        private void DebugMirrorApplyUploads(NativeList<StagedUpload> cmds)
        {
            if (!EnsureUploadValidator()) return;
            for (int c = 0; c < cmds.Length; c++)
            {
                var cmd = cmds[c];
                for (int i = 0; i < cmd.count; i++)
                {
                    var s = _stagingCpuArray[cmd.stagingOffset + i];
                    int slot = cmd.liveOffset + i;
                    _mirrorRotScale[slot] = new GPUInstanceRotScaleData { rotation = s.rotation, scale = s.scale };
                    _mirrorPacked[slot] = s.sigDensityPacked;
                    _mirrorDensityUnknown[slot] = 0;
                    _mirrorRaced[slot] = 0;
                    DebugMirrorRecordWrite(slot, DebugUploadOp.Upload);
                }
            }
        }

        private void DebugMirrorApplyClears(NativeList<StagedUpload> cmds)
        {
            if (!EnsureUploadValidator()) return;
            for (int c = 0; c < cmds.Length; c++)
            {
                var cmd = cmds[c];
                for (int i = 0; i < cmd.count; i++)
                {
                    int slot = cmd.liveOffset + i;
                    _mirrorRotScale[slot] = default;
                    _mirrorPacked[slot] = 0xFFFFu;
                    _mirrorDensityUnknown[slot] = 0;
                    _mirrorRaced[slot] = 0;
                    DebugMirrorRecordWrite(slot, DebugUploadOp.Clear);
                }
            }
        }

        private void DebugMirrorApplyTRSPatches(NativeArray<GPUTRSPatchEntry> entries, int count)
        {
            if (!EnsureUploadValidator()) return;
            _mirrorApplyStamp++;
            for (int i = 0; i < count; i++)
            {
                var e = entries[i];
                int slot = (int)e.slotIndex;
                DebugMirrorMarkScatterWrite(slot);
                var rs = _mirrorRotScale[slot];
                if ((e.flags & 2u) != 0) rs.rotation = e.rotation;
                if ((e.flags & 4u) != 0) rs.scale = e.scale;
                _mirrorRotScale[slot] = rs;
                // kernel repacks density from a position the cpu cannot reproduce bit-exactly
                _mirrorPacked[slot] &= 0xFFFFu;
                _mirrorDensityUnknown[slot] = 1;
                DebugMirrorRecordWrite(slot, DebugUploadOp.TRSPatch);
            }
        }

        private void DebugMirrorApplySigOverrides(int count)
        {
            if (!EnsureUploadValidator()) return;
            _mirrorApplyStamp++;
            for (int i = 0; i < count; i++)
            {
                var e = _sigOverrideStagingCpuArray[i];
                int slot = (int)e.slotIndex;
                DebugMirrorMarkScatterWrite(slot);
                uint sigBits = e.sigIdx < 0 ? 0xFFFFu : (uint)e.sigIdx & 0xFFFFu;
                _mirrorPacked[slot] = (_mirrorPacked[slot] & 0xFFFF0000u) | sigBits;
                DebugMirrorRecordWrite(slot, DebugUploadOp.SigOverride);
            }
        }

        private void DebugMirrorApplyColors(int count)
        {
            if (!EnsureUploadValidator()) return;
            _mirrorApplyStamp++;
            for (int i = 0; i < count; i++)
            {
                var e = _colorStagingCpuArray[i];
                int slot = (int)e.slotIndex;
                DebugMirrorMarkScatterWrite(slot);
                _mirrorColor[slot] = e.packedColor;
                DebugMirrorRecordWrite(slot, DebugUploadOp.Color);
            }
        }

        // matrix compute with PrevM init resets the color region for covered slots
        private void DebugMirrorApplyMatrixInitColorReset(NativeList<StagedUpload> cmds)
        {
            if (!_perInstanceColorEnabled || !EnsureUploadValidator()) return;
            for (int c = 0; c < cmds.Length; c++)
            {
                var cmd = cmds[c];
                for (int i = 0; i < cmd.count; i++)
                    _mirrorColor[cmd.liveOffset + i] = 0u;
            }
        }

        /// <summary>Compare gpu buffers against the mirror. Logs mismatches with last-writer info.</summary>
        public void DebugValidateUploadsNow()
        {
            if (!EnsureUploadValidator()) return;

            int totalSlots = _poolCapacity * k_poolSize;
            var rotReq = AsyncGPUReadback.Request(_instanceRotScaleBuffer);
            var instReq = AsyncGPUReadback.Request(_instanceBuffer);
            var refReq = AsyncGPUReadback.Request(_poolRefBuffer);
            bool checkColor = _perInstanceColorEnabled && _matrixBuffer != null;
            AsyncGPUReadbackRequest colReq = default;
            if (checkColor)
                colReq = AsyncGPUReadback.Request(_matrixBuffer, totalSlots * 4, _colorRegionByteOffset);

            rotReq.WaitForCompletion();
            instReq.WaitForCompletion();
            refReq.WaitForCompletion();
            if (checkColor) colReq.WaitForCompletion();
            if (rotReq.hasError || instReq.hasError || refReq.hasError || (checkColor && colReq.hasError))
            {
                Debug.LogError("[GPUBatcherUploadValidator] gpu readback failed");
                return;
            }

            var gpuRot = rotReq.GetData<GPUInstanceRotScaleData>();
            var gpuInst = instReq.GetData<GPUInstanceData>();
            var gpuRefs = refReq.GetData<int>();
            NativeArray<uint> gpuColor = checkColor ? colReq.GetData<uint>() : default;

            int mismatches = 0;
            const int k_maxLogs = 10;

            for (int slot = 0; slot < totalSlots; slot++)
            {
                if (!_poolInUse[slot / k_poolSize]) continue;
                if (_mirrorRaced[slot] != 0) continue;

                var mr = _mirrorRotScale[slot];
                var gr = gpuRot[slot];
                if (math.any(math.asuint(mr.rotation) != math.asuint(gr.rotation))
                    || math.any(math.asuint(mr.scale) != math.asuint(gr.scale)))
                {
                    mismatches++;
                    if (mismatches <= k_maxLogs)
                        LogUploadMismatch(slot, "rotScale",
                            $"expected rot={mr.rotation} scale={mr.scale}, got rot={gr.rotation} scale={gr.scale}");
                }

                uint mask = _mirrorDensityUnknown[slot] != 0 ? 0xFFFFu : 0xFFFFFFFFu;
                uint mp = _mirrorPacked[slot] & mask;
                uint gp = gpuInst[slot].sigDensityPacked & mask;
                if (mp != gp)
                {
                    mismatches++;
                    if (mismatches <= k_maxLogs)
                        LogUploadMismatch(slot, "sigDensityPacked", $"expected 0x{mp:X8}, got 0x{gp:X8} (mask 0x{mask:X8})");
                }

                if (checkColor && _mirrorColor[slot] != gpuColor[slot])
                {
                    mismatches++;
                    if (mismatches <= k_maxLogs)
                        LogUploadMismatch(slot, "color", $"expected 0x{_mirrorColor[slot]:X8}, got 0x{gpuColor[slot]:X8}");
                }
            }

            // pool refs: gpu must match cpu truth for every active chunk's ref range.
            // skipped while refs are staged but unflushed - cpu legitimately leads the gpu then
            if (_stagedPoolRefUploads.Length == 0)
            {
                for (int a = 0; a < _allocatedCount; a++)
                {
                    int c = _activeChunks[a];
                    var chunk = _chunks[c];
                    int baseIdx = c * _maxPoolRefsPerChunk;
                    for (int p = 0; p < chunk.poolCount; p++)
                    {
                        if (gpuRefs[baseIdx + p] != _chunkPools[baseIdx + p])
                        {
                            mismatches++;
                            if (mismatches <= k_maxLogs)
                                Debug.LogError($"[GPUBatcherUploadValidator] poolRef MISMATCH chunk={c} ref={p}: cpu={_chunkPools[baseIdx + p]}, gpu={gpuRefs[baseIdx + p]}");
                        }
                    }
                }
            }

            statUploadValidationPasses++;
            statUploadValidationMismatches += mismatches;
            if (mismatches > 0)
                Debug.LogError($"[GPUBatcherUploadValidator] {mismatches} mismatch(es) at render cycle {_renderCycleCounter}" +
                    (mismatches > k_maxLogs ? $" ({mismatches - k_maxLogs} not shown)" : ""));
            else
                LogMessageDevelopment($"[GPUBatcherUploadValidator] pass {statUploadValidationPasses}: clean ({totalSlots} slots)");
        }

        private void LogUploadMismatch(int slot, string field, string detail)
        {
            int pool = slot / k_poolSize;
            int chunk = pool < _poolToChunk.Length ? _poolToChunk[pool] : -1;
            int proto = slot < _slotProtoIndex.Length ? _slotProtoIndex[slot] : -1;
            var last = _mirrorLastWrite[slot];
            var prev = _mirrorPrevWrite[slot];
            Debug.LogError($"[GPUBatcherUploadValidator] {field} MISMATCH slot={slot} (pool {pool}, chunk {chunk}, proto {proto}): {detail}; last={last.op}@cycle{last.cycle} prev={prev.op}@cycle{prev.cycle}");
        }

        private bool _prevConfigValidateUploads;

        // called at the end of FlushStagedUploads
        private void DebugUploadValidationTick()
        {
            bool cfg = _config.debugValidateUploads;
            if (cfg != _prevConfigValidateUploads)
            {
                _prevConfigValidateUploads = cfg;
                debugUploadValidation = cfg;
            }

            if (!_uploadValidationEnabled || debugUploadValidationInterval <= 0) return;
            if (++_uploadValidationCounter < debugUploadValidationInterval) return;
            _uploadValidationCounter = 0;
            DebugValidateUploadsNow();
        }

        #endregion
#endif

#endif
    }
}
