using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Threading;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Runtime engine for GPU-instanced Terrain details. Instances are created
    /// from Detail Profiles and bound to scene Terrains by the Terrain binder.
    /// </summary>
    public class GPUInstancerDetailManager : GPUInstancerTerrainManager
    {
        public int detailLayer;

        public bool runInThreads = false;

        private static ComputeShader _grassInstantiationComputeShader;

        private ComputeBuffer _generatingVisibilityBuffer;
        private int _generatingVisibilityBufferGeneration = -1;
#if !UNITY_2017_1_OR_NEWER || UNITY_ANDROID
        private ComputeBuffer _managedBuffer;
        private Matrix4x4[] _managedData;
#endif
        private bool _triggerEvent;
        private ComputeBuffer counterBuffer;
        private int[] counterData = new int[1];

        // Coroutines are driven by the package PlayerLoop rather than by a
        // MonoBehaviour.  A queued iterator can therefore be disposed before
        // its first MoveNext(), and worker-thread callbacks can arrive after a
        // manager has been cleared.  Bump this generation whenever runtime
        // state is invalidated so every continuation can cheaply reject stale
        // work without touching released buffers/cell data.
        private volatile int _detailCoroutineGeneration;
        private int _detailCallbackPending;

        private sealed class DetailThreadRequest
        {
            public Vector4 coordinate;
            public int generation;
            public GPUInstancerSpatialPartitioningData<GPUInstancerCell> spatialData;
            public float[,] heightMapData;
            public List<int[,]> detailMapData;
            public int heightResolution;
            public int detailResolution;
        }

        protected override void ApplyProfile(GPUInstancerProfile value, bool copyCollections)
        {
            base.ApplyProfile(value, copyCollections);
            GPUInstancerDetailProfile detailProfile = value as GPUInstancerDetailProfile;
            if (detailProfile == null)
                return;

            detailLayer = detailProfile.detailLayer;
            runInThreads = detailProfile.runInThreads;
        }

        #region Explicit lifecycle
        public override void Initialize()
        {
            base.Initialize();

            if (_grassInstantiationComputeShader == null)
                _grassInstantiationComputeShader = (ComputeShader)Resources.Load(GPUInstancerConstants.GRASS_INSTANTIATION_RESOURCE_PATH);
        }

        protected override void DeactivateCore()
        {
            // Invalidate all iterators before releasing any state they may
            // still reference. Hub normally cancels owner-bound coroutines
            // before calling Deactivate; the generation guard also covers
            // direct Dispose/Deactivate calls and worker callbacks queued in
            // the same frame.
            InvalidateDetailCoroutineState(true);

            if (runInThreads && activeThreads.Count > 0)
            {
                for (int i = activeThreads.Count - 1; i >= 0; i--)
                {
                    Thread thread = activeThreads[i];
                    if (thread == null || !thread.IsAlive)
                        continue;
                    thread.Abort();
                }
            }
            ClearCompletedThreads();

            try
            {
                base.DeactivateCore();
            }
            finally
            {
                // Always repeat the transient cleanup here in case base
                // teardown failed before reaching all resource releases.
                ReleaseGeneratingVisibilityBuffer();
#if !UNITY_2017_1_OR_NEWER || UNITY_ANDROID
                ReleaseManagedBuffer();
#endif
                ReleaseCounterBuffer();
                threadHeightMapData = null;
                threadDetailMapData = null;
                replacingInstances = false;
                initalizingInstances = false;
                _triggerEvent = false;
            }
        }

        public override void ResetConfiguration()
        {
            base.ResetConfiguration();
#if UNITY_EDITOR
            keepSimulationLive = true;
#endif
        }
        #endregion Explicit lifecycle

        #region Override Methods

        public override void ClearInstancingData()
        {
            // Clear can be called by GeneratePrototypes/Initialize while a
            // previous frame's iterator is still queued. Invalidate it first
            // and release our in-flight output before the base class walks
            // runtime data, avoiding a stale field alias to a released GPU
            // buffer.
            InvalidateDetailCoroutineState(true);
            ReleaseGeneratingVisibilityBuffer();

            try
            {
                base.ClearInstancingData();
            }
            finally
            {
                // One profile manager can own multiple Terrains. Restore every
                // bound Terrain before teardown; using only the primary terrain
                // left additional Terrains with Unity detail rendering disabled.
                List<Terrain> terrains = GetTerrains();
                if (terrains != null)
                {
                    for (int i = 0; i < terrains.Count; i++)
                        RestoreTerrainSettingsAfterUnbind(terrains[i]);
                }

#if !UNITY_2017_1_OR_NEWER || UNITY_ANDROID
                ReleaseManagedBuffer();
#endif
                ReleaseCounterBuffer();
                threadHeightMapData = null;
                threadDetailMapData = null;
                replacingInstances = false;
                initalizingInstances = false;
                _triggerEvent = false;
            }
        }

        /// <summary>
        /// RemoveTerrain queues the list mutation, so restore the Unity detail
        /// distance before that queue is consumed (otherwise the removed
        /// terrain is no longer visited by ClearInstancingData).
        /// </summary>
        protected override void RestoreTerrainSettingsAfterUnbind(Terrain terrain)
        {
            if (terrain != null && terrainSettings != null && terrain.detailObjectDistance == 0)
                terrain.detailObjectDistance = terrainSettings.maxDetailDistance > 250
                    ? 250
                    : terrainSettings.maxDetailDistance;
        }

        protected override void ReapplyTerrainSettingsAfterRebind(Terrain terrain)
        {
            // Detail initialization suppresses Unity's native detail renderer.
            // Reassert that ownership only for a live initialized manager; a
            // profile waiting for its first valid Terrain remains non-invasive.
            if (terrain != null && isActive && isInitialized)
                terrain.detailObjectDistance = 0f;
        }

        public override void GeneratePrototypes(bool forceNew = false)
        {
            // Profile data must survive a forced runtime refresh.  The base
            // implementation intentionally releases Play Mode clones when
            // forceNew is true; allowing the terrain utility to run after
            // that would replace the authored recipe with TerrainData
            // defaults and lose the source -> clone mapping. An empty Profile
            // still uses the terrain-layout bootstrap path below.
            if (forceNew && Application.isPlaying && profile != null &&
                profile.prototypeList != null && profile.prototypeList.Count > 0)
            {
                RefreshProfileCollections();
                return;
            }

            Terrain boundTerrain = terrain;
            int terrainPrototypeCount = -1;
            if (boundTerrain != null && boundTerrain.terrainData != null)
            {
                DetailPrototype[] terrainDetailPrototypes = boundTerrain.terrainData.detailPrototypes;
                terrainPrototypeCount = terrainDetailPrototypes == null ? 0 : terrainDetailPrototypes.Length;
            }

            bool tracking = false;
            try
            {
                // Profile-backed Play Mode managers receive a transient copy
                // of the profile prototype list. Observe the utility pass so
                // any newly-created terrain prototypes can be owned and
                // released by the manager; editor paths keep the
                // utility's original asset semantics.
                tracking = BeginRuntimeTerrainPrototypeGeneration<GPUInstancerDetailPrototype>(
                    forceNew, terrainPrototypeCount);

                base.GeneratePrototypes(forceNew);

                if (terrainSettings != null && boundTerrain != null && boundTerrain.terrainData != null)
                {
                    GPUInstancerUtility.SetDetailInstancePrototypes(diagnosticContext, prototypeList, boundTerrain.terrainData.detailPrototypes, 2, terrainSettings, forceNew);
                }
#if UNITY_EDITOR
                SyncProfilePrototypeList();
#endif
            }
            finally
            {
                // The finally is intentional: prototype generation can throw
                // after allocating a transient prototype but before the new
                // object is appended to prototypeList.
                if (tracking)
                    EndRuntimeTerrainPrototypeGeneration<GPUInstancerDetailPrototype>();
            }
        }

#if UNITY_EDITOR
        public override void CheckPrototypeChanges()
        {
            base.CheckPrototypeChanges();

            if (!Application.isPlaying && terrainSettings != null && terrain != null && terrain.terrainData != null)
            {
                DetailPrototype[] terrainDetailPrototypes = terrain.terrainData.detailPrototypes;
                if (terrainDetailPrototypes == null)
                    return;
                if (prototypeList == null)
                    prototypeList = new List<GPUInstancerPrototype>();
                if (prototypeList.Count != terrainDetailPrototypes.Length)
                {
                    GeneratePrototypes();
                }

                for (int index = 0; index < prototypeList.Count; index++)
                {
                    GPUInstancerDetailPrototype prototype = prototypeList[index] as GPUInstancerDetailPrototype;
                    if (prototype == null)
                        continue;
                    prototype.prototypeIndex = index;
                }

                if (prototypeList.Count != terrainDetailPrototypes.Length)
                    terrainSettings.warningText = "Detail Prototypes on the Unity Terrain do not match the Detail Prototypes in this Detail Manager. Adding and removing Detail Prototypes should be done from the Detail Manager and not from the Unity Terrain. To fix this error, you can press the Generate Prototypes button below. This will reset the prototypes in this manager with the default settings.";
#if UNITY_2022_2_OR_NEWER
                else if (terrain.terrainData.detailScatterMode == DetailScatterMode.CoverageMode)
                    terrainSettings.warningText = "\"Detail Scatter Mode\" terrain setting is set to \"Coverage Mode\" which is not supported. Go to terrain settings and change the \"Detail Scatter Mode\" setting to \"Instance Count Mode\" to be able to use the Detail Manager.";
#endif
                else
                    terrainSettings.warningText = null;

                AddProxyToTerrain();
            }
        }
#endif
        public override void InitializeRuntimeDataAndBuffers(bool forceNew = true)
        {
            base.InitializeRuntimeDataAndBuffers(forceNew);

            if (!forceNew && isInitialized)
                return;

            // Terrain managers can be created before TerrainProxy performs its
            // lazy scene binding.  Do not initialize against a missing terrain
            // (which would dereference terrain below) and, importantly, leave
            // isInitialized=false so the first proxy bind can retry.  Terrain
            // data may also be assigned a frame after the component appears.
            if (terrainSettings == null || terrain == null || terrain.terrainData == null)
            {
                if (terrain != null && terrain.terrainData == null)
                    RequestRuntimeInitializationRetry();
                return;
            }

            // TerrainData can be temporarily incomplete while a scene or
            // prefab is importing. Keep the manager dormant until the
            // dimensions needed by the cell/height-map code are valid; this
            // avoids divide-by-zero and out-of-range reads in the first tick.
            if (terrain.terrainData.detailResolution <= 0 ||
                terrain.terrainData.heightmapResolution <= 0)
            {
                RequestRuntimeInitializationRetry();
                return;
            }

            if (!string.IsNullOrEmpty(terrainSettings.warningText))
            {
                Debug.LogError("A GPU Instancer Detail Manager currently has errors. Please refer to the error description in the Detail manager.", diagnosticContext);
#if UNITY_EDITOR
                if (editorLoopState != null)
                    editorLoopState.StopSimulation();
                keepSimulationLive = false;
#endif
                return;
            }

            replacingInstances = false;
            initalizingInstances = true;

            if (prototypeList != null && prototypeList.Count > 0)
            {
                GPUInstancerUtility.AddDetailInstanceRuntimeDataToList(runtimeDataList, prototypeList, terrainSettings, detailLayer, this);
            }

            // Do not disable Unity's native detail renderer for an empty
            // channel. A profile may intentionally contain no prototypes (or
            // a Terrain may have no detail layers); in that case there is no
            // GPUI draw to replace and native details should remain visible.
            if (prototypeList != null && prototypeList.Count > 0)
                terrain.detailObjectDistance = 0;

            InitializeSpatialPartitioning();

            if (spData == null || spData.cellRowAndCollumnCountPerTerrain <= 0)
            {
                initalizingInstances = false;
                return;
            }

            isInitialized = true;
        }

        public override void UpdateSpatialPartitioningCells(GPUInstancerCameraData renderingCameraData)
        {
            base.UpdateSpatialPartitioningCells(renderingCameraData);

            if (terrainSettings == null || spData == null || renderingCameraData == null ||
                renderingCameraData.mainCamera == null || terrain == null || terrain.terrainData == null)
                return;

            if (!initalizingInstances && !replacingInstances && spData.IsActiveCellUpdateRequired(renderingCameraData.mainCamera.transform.position))
            {
                replacingInstances = true;
                if (GPUInstancerConstants.DETAIL_STORE_INSTANCE_DATA)
                    StartCoroutineAlt(GenerateVisibilityBufferFromActiveCellsCoroutine(_detailCoroutineGeneration));
                else
                    StartCoroutineAlt(MergeVisibilityBufferFromActiveCellsCoroutine(_detailCoroutineGeneration));
            }
        }

        public override void DeletePrototype(GPUInstancerPrototype prototype, bool removeSO = true)
        {
            if (prototype == null)
            {
                if (prototypeList != null)
                    prototypeList.Remove(null);
                return;
            }
            if (terrainSettings != null && terrain != null && terrain.terrainData != null)
            {
                if (prototypeList == null)
                    prototypeList = new List<GPUInstancerPrototype>();
                int detailPrototypeIndex = prototypeList.IndexOf(prototype);
                if (detailPrototypeIndex < 0)
                    return;

                DetailPrototype[] detailPrototypes = terrain.terrainData.detailPrototypes;
                if (detailPrototypes == null || detailPrototypeIndex >= detailPrototypes.Length)
                {
                    base.DeletePrototype(prototype, removeSO);
                    return;
                }
                List<DetailPrototype> newDetailPrototypes = new List<DetailPrototype>();
                List<int[,]> newDetailLayers = new List<int[,]>();

                for (int i = 0; i < detailPrototypes.Length; i++)
                {
                    if (i != detailPrototypeIndex)
                    {
                        newDetailPrototypes.Add(detailPrototypes[i]);
                        newDetailLayers.Add(terrain.terrainData.GetDetailLayer(0, 0, terrain.terrainData.detailResolution, terrain.terrainData.detailResolution, i));
                    }
                    terrain.terrainData.SetDetailLayer(0, 0, i, new int[terrain.terrainData.detailResolution, terrain.terrainData.detailResolution]);
                }

                terrain.terrainData.detailPrototypes = newDetailPrototypes.ToArray();
                for (int i = 0; i < newDetailLayers.Count; i++)
                {
                    terrain.terrainData.SetDetailLayer(0, 0, i, newDetailLayers[i]);
                }
                terrain.terrainData.RefreshPrototypes();

                if (removeSO)
                    base.DeletePrototype(prototype, removeSO);
                GeneratePrototypes(false);
                if (!removeSO)
                    base.DeletePrototype(prototype, removeSO);
            }
            else
                base.DeletePrototype(prototype, removeSO);
        }

        public override void RemoveInstancesInsideBounds(Bounds bounds, float offset, List<GPUInstancerPrototype> prototypeFilter = null)
        {
            //#if UNITY_EDITOR
            //            UnityEngine.Profiling.Profiler.BeginSample("GPUInstancerDetailManager.RemoveInstancesInsideBounds");
            //#endif
            base.RemoveInstancesInsideBounds(bounds, offset, prototypeFilter);
            if (spData != null && !initalizingInstances && terrain != null && terrain.terrainData != null &&
                spData.cellRowAndCollumnCountPerTerrain > 0 && terrain.terrainData.detailResolution > 0)
            {
                int detailMapSize = terrain.terrainData.detailResolution / spData.cellRowAndCollumnCountPerTerrain;
                if (detailMapSize <= 0)
                    return;
                float unitSizeX = terrain.terrainData.size.x / spData.cellRowAndCollumnCountPerTerrain / detailMapSize;
                float unitSizeZ = terrain.terrainData.size.z / spData.cellRowAndCollumnCountPerTerrain / detailMapSize;
                int sizeX = Mathf.CeilToInt((bounds.extents.x * 2 + offset) / unitSizeX);
                int sizeZ = Mathf.CeilToInt((bounds.extents.z * 2 + offset) / unitSizeZ);

                foreach (GPUInstancerCell baseCell in spData.GetCellList())
                {
                    GPUInstancerDetailCell detailCell = baseCell as GPUInstancerDetailCell;
                    if (detailCell == null)
                        continue;
                        if (detailCell == null || detailCell.detailMapData == null)
                            continue;
                        if (detailCell.cellInnerBounds.Intersects(bounds))
                    {
                        if (detailCell.isActive && detailCell.detailInstanceBuffers != null)
                        {
                            foreach (int i in detailCell.detailInstanceBuffers.Keys)
                            {
                                if (prototypeList == null || i < 0 || i >= prototypeList.Count ||
                                    !IsPrototypeInFilter(prototypeFilter, prototypeList[i]))
                                    continue;
                                GPUInstancerUtility.RemoveInstancesInsideBounds(detailCell.detailInstanceBuffers[i], bounds.center, bounds.extents, offset);
                            }
                        }
                        int startX = Mathf.FloorToInt((bounds.center.x - bounds.extents.x - detailCell.instanceStartPosition.x - offset) / unitSizeX);
                        int startZ = Mathf.FloorToInt((bounds.center.z - bounds.extents.z - detailCell.instanceStartPosition.z - offset) / unitSizeZ);

                        for (int i = 0; i < detailCell.detailMapData.Count; i++)
                        {
                            if (prototypeList == null || i < 0 || i >= prototypeList.Count ||
                                !IsPrototypeInFilter(prototypeFilter, prototypeList[i]))
                                continue;
                            for (int y = startZ; y < sizeZ + startZ; y++)
                            {
                                if (y < 0 || y >= detailMapSize)
                                    continue;
                                for (int x = startX; x < sizeX + startX; x++)
                                {
                                    if (x < 0 || x >= detailMapSize)
                                        continue;
                            if (i < detailCell.detailMapData.Count && detailCell.detailMapData[i] != null &&
                                x + y * detailMapSize < detailCell.detailMapData[i].Length)
                                detailCell.detailMapData[i][x + y * detailMapSize] = 0;
                                }
                            }
                        }
                    }
                }
            }
            //#if UNITY_EDITOR
            //            UnityEngine.Profiling.Profiler.EndSample();
            //#endif
        }

        public override void RemoveInstancesInsideCollider(Collider collider, float offset, List<GPUInstancerPrototype> prototypeFilter = null)
        {
            //#if UNITY_EDITOR
            //            UnityEngine.Profiling.Profiler.BeginSample("GPUInstancerDetailManager.RemoveInstancesInsideCollider");
            //#endif
            base.RemoveInstancesInsideCollider(collider, offset, prototypeFilter);
            if (collider == null)
                return;
            if (spData != null && !initalizingInstances && terrain != null && terrain.terrainData != null &&
                spData.cellRowAndCollumnCountPerTerrain > 0 && terrain.terrainData.detailResolution > 0)
            {
                Bounds bounds = collider.bounds;
                int detailMapSize = terrain.terrainData.detailResolution / spData.cellRowAndCollumnCountPerTerrain;
                if (detailMapSize <= 0)
                    return;
                float unitSizeX = terrain.terrainData.size.x / spData.cellRowAndCollumnCountPerTerrain / detailMapSize;
                float unitSizeZ = terrain.terrainData.size.z / spData.cellRowAndCollumnCountPerTerrain / detailMapSize;
                int sizeX = Mathf.CeilToInt((bounds.extents.x * 2 + offset) / unitSizeX);
                int sizeZ = Mathf.CeilToInt((bounds.extents.z * 2 + offset) / unitSizeZ);

                Vector3 testCenter = Vector3.zero;
                Vector3 closestPoint = Vector3.zero;

                foreach (GPUInstancerCell baseCell in spData.GetCellList())
                {
                    GPUInstancerDetailCell detailCell = baseCell as GPUInstancerDetailCell;
                    if (detailCell == null)
                        continue;
                        if (detailCell == null || detailCell.detailMapData == null)
                            continue;
                        if (detailCell.cellInnerBounds.Intersects(bounds))
                    {
                        if (detailCell.isActive && detailCell.detailInstanceBuffers != null)
                        {
                            foreach (int i in detailCell.detailInstanceBuffers.Keys)
                            {
                                if (prototypeList == null || i < 0 || i >= prototypeList.Count ||
                                    !IsPrototypeInFilter(prototypeFilter, prototypeList[i]))
                                    continue;

                                if (collider is BoxCollider)
                                    GPUInstancerUtility.RemoveInstancesInsideBoxCollider(detailCell.detailInstanceBuffers[i], (BoxCollider)collider, offset);
                                else if (collider is SphereCollider)
                                    GPUInstancerUtility.RemoveInstancesInsideSphereCollider(detailCell.detailInstanceBuffers[i], (SphereCollider)collider, offset);
                                else if (collider is CapsuleCollider)
                                    GPUInstancerUtility.RemoveInstancesInsideCapsuleCollider(detailCell.detailInstanceBuffers[i], (CapsuleCollider)collider, offset);
                                else
                                    GPUInstancerUtility.RemoveInstancesInsideBounds(detailCell.detailInstanceBuffers[i], collider.bounds.center, collider.bounds.extents, offset);
                            }
                        }
                        int startX = Mathf.FloorToInt((bounds.center.x - bounds.extents.x - detailCell.instanceStartPosition.x - offset) / unitSizeX);
                        int startZ = Mathf.FloorToInt((bounds.center.z - bounds.extents.z - detailCell.instanceStartPosition.z - offset) / unitSizeZ);

                        for (int y = startZ; y < sizeZ + startZ; y++)
                        {
                            if (y < 0 || y >= detailMapSize)
                                continue;
                            for (int x = startX; x < sizeX + startX; x++)
                            {
                                if (x < 0 || x >= detailMapSize)
                                    continue;

                                testCenter.x = detailCell.instanceStartPosition.x + x * unitSizeX;
                                testCenter.z = detailCell.instanceStartPosition.z + y * unitSizeZ;
                                testCenter.y = terrain.SampleHeight(testCenter);
                                closestPoint = collider.ClosestPoint(testCenter);
                                if (Vector3.Distance(closestPoint, testCenter) > unitSizeX + offset)
                                    continue;
                                for (int i = 0; i < detailCell.detailMapData.Count; i++)
                                {
                                    if (prototypeList == null || i < 0 || i >= prototypeList.Count ||
                                        !IsPrototypeInFilter(prototypeFilter, prototypeList[i]))
                                        continue;
                                    if (i < detailCell.detailMapData.Count && detailCell.detailMapData[i] != null &&
                                        x + y * detailMapSize < detailCell.detailMapData[i].Length)
                                        detailCell.detailMapData[i][x + y * detailMapSize] = 0;
                                }
                            }
                        }
                    }
                }
            }
            //#if UNITY_EDITOR
            //            UnityEngine.Profiling.Profiler.EndSample();
            //#endif
        }

        public override void SetGlobalPositionOffset(Vector3 offsetPosition)
        {
            base.SetGlobalPositionOffset(offsetPosition);
            if (spData != null)
            {
                foreach (GPUInstancerCell baseCell in spData.GetCellList())
                {
                    GPUInstancerDetailCell cell = baseCell as GPUInstancerDetailCell;
                    if (cell == null)
                        continue;
                    if (cell == null)
                        continue;
                    cell.instanceStartPosition += offsetPosition;
                    cell.cellBounds.center += offsetPosition;
                    if (cell.detailInstanceBuffers != null)
                    {
                        foreach (ComputeBuffer detailBuffer in cell.detailInstanceBuffers.Values)
                        {
                            if (detailBuffer != null && GPUInstancerConstants.computeRuntimeModification != null)
                            {
                                GPUInstancerConstants.computeRuntimeModification.SetBuffer(GPUInstancerConstants.computeBufferTransformOffsetId,
                                    GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, detailBuffer);
                                GPUInstancerConstants.computeRuntimeModification.SetInt(
                                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, detailBuffer.count);
                                GPUInstancerConstants.computeRuntimeModification.SetVector(
                                    GPUInstancerConstants.RuntimeModificationKernelProperties.BUFFER_PARAMETER_POSITION_OFFSET, offsetPosition);

                                GPUInstancerUtility.DispatchCompute(GPUInstancerConstants.computeRuntimeModification,
                                    GPUInstancerConstants.computeBufferTransformOffsetId,
                                    Mathf.CeilToInt(detailBuffer.count / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
                            }
                        }
                    }
                    if (GPUInstancerConstants.DETAIL_STORE_INSTANCE_DATA && cell.detailInstanceList != null)
                    {
                        foreach (Matrix4x4[] instanceDataArray in cell.detailInstanceList.Values)
                        {
                            if (instanceDataArray == null)
                                continue;
                            for (int i = 0; i < instanceDataArray.Length; i++)
                            {
                                instanceDataArray[i].SetColumn(3, instanceDataArray[i].GetColumn(3) + new Vector4(offsetPosition.x, offsetPosition.y, offsetPosition.z, 0));
                            }
                        }
                    }
                }
            }
        }
        #endregion Override Methods

        #region Helper Methods

        private void InvalidateDetailCoroutineState(bool resetInitialization)
        {
            unchecked
            {
                _detailCoroutineGeneration++;
            }

            // A queued retry may have been dropped when the base manager
            // clears its thread queue.  Reset the coalescing bit so the next
            // generation can enqueue its own completion callback.
            Interlocked.Exchange(ref _detailCallbackPending, 0);

            if (resetInitialization)
                initalizingInstances = false;
            replacingInstances = false;
            _triggerEvent = false;
        }

        private bool IsDetailCoroutineCurrent(int generation)
        {
            return generation == _detailCoroutineGeneration;
        }

        /// <summary>
        /// Enqueues at most one main-thread completion/retry callback for a
        /// generation. Multiple worker partitions can finish in the same
        /// frame; without coalescing, each completion could enqueue a retry
        /// while another worker is still alive and grow the queue indefinitely.
        /// </summary>
        private void EnqueueFillCellsDetailCallback(int generation)
        {
            if (!IsDetailCoroutineCurrent(generation) ||
                Interlocked.CompareExchange(ref _detailCallbackPending, 1, 0) != 0)
                return;

            lock (threadQueue)
            {
                if (!IsDetailCoroutineCurrent(generation))
                {
                    Interlocked.Exchange(ref _detailCallbackPending, 0);
                    return;
                }

                threadQueue.Enqueue(() =>
                {
                    Interlocked.Exchange(ref _detailCallbackPending, 0);
                    FillCellsDetailDataCallBack(generation);
                });
            }
        }

        private void ReleaseGeneratingVisibilityBuffer()
        {
            ReleaseComputeBuffer(ref _generatingVisibilityBuffer);
            _generatingVisibilityBufferGeneration = -1;
        }

        private void ReleaseGeneratingVisibilityBuffer(int generation)
        {
            // A stale iterator can be disposed after a newer generation has
            // already started. Never release the newer pass's output buffer.
            if (_generatingVisibilityBufferGeneration != generation)
                return;
            ReleaseGeneratingVisibilityBuffer();
        }

        private void SetGeneratingVisibilityBuffer(ComputeBuffer buffer, int generation)
        {
            _generatingVisibilityBuffer = buffer;
            _generatingVisibilityBufferGeneration = buffer == null ? -1 : generation;
        }

#if !UNITY_2017_1_OR_NEWER || UNITY_ANDROID
        private void ReleaseManagedBuffer()
        {
            ReleaseComputeBuffer(ref _managedBuffer);
            _managedData = null;
        }
#endif

        private void ReleaseCounterBuffer()
        {
            ReleaseComputeBuffer(ref counterBuffer);
        }

        private static void ReleaseComputeBuffer(ref ComputeBuffer buffer)
        {
            ComputeBuffer candidate = buffer;
            buffer = null;
            if (candidate == null)
                return;

            candidate.Release();
        }

        private static int FixBounds(int value, int max, int failValue)
        {
            if (value >= max)
                return failValue;
            return value;
        }

        private static bool CanSlice<T>(T[,] source, int xBase, int yBase, int width, int height)
        {
            if (source == null || width <= 0 || height <= 0 || xBase < 0 || yBase < 0)
                return false;
            return xBase + height <= source.GetLength(1) &&
                yBase + width <= source.GetLength(0);
        }

        public static Matrix4x4[] GetInstanceDataForDetailPrototype(GPUInstancerDetailPrototype detailPrototype, int[] detailMap, float[] heightMapData,
                                                                int detailMapSize, int heightMapSize,
                                                                int detailResolution, int heightResolution,
                                                                Vector3 startPosition, Vector3 terrainSize,
                                                                int instanceCount)
        {
            if (instanceCount < 0)
                instanceCount = 0;
            Matrix4x4[] result = new Matrix4x4[instanceCount];

            long expectedDetailData = (long)detailMapSize * detailMapSize;
            long expectedHeightData = (long)heightMapSize * heightMapSize;
            if (instanceCount == 0 || detailPrototype == null || detailMap == null ||
                detailMapSize <= 0 || heightMapData == null || heightMapData.Length == 0 ||
                heightMapSize <= 0 || detailResolution <= 0 || heightResolution <= 0 ||
                expectedDetailData > detailMap.Length || expectedHeightData > heightMapData.Length)
                return result;

            System.Random randomNumberGenerator = new System.Random();
            float detailHeightMapScale = (heightResolution - 1.0f) / detailResolution;
            int heightDataSize = heightMapSize * heightMapSize;
            float sizeDetailXScale = terrainSize.x / detailResolution;
            float sizeDetailZScale = terrainSize.z / detailResolution;
            float normalScale = heightResolution / (terrainSize.x / terrainSize.y);

            float px, py, leftBottomH, leftTopH, rightBottomH, rightTopH;
            int heightIndex;
            Vector3 position;
            Vector3 scale = Vector3.zero;
            Quaternion rotation = Quaternion.identity;

            Vector3 P = Vector3.zero;
            Vector3 Q = new Vector3(0, 0, 1);
            Vector3 R = new Vector3(1, 0, 0);
            Vector3 terrainPointNormal = Vector3.zero;

            int counter = 0;
            for (int y = 0; y < detailMapSize; y++)
            {
                for (int x = 0; x < detailMapSize; x++)
                {
                    for (int j = 0; j < detailMap[y * detailMapSize + x]; j++) // for the amount of detail at this point and for this prototype
                    {
                        position.x = x + randomNumberGenerator.Range(0f, 0.99f);
                        position.y = 0;
                        position.z = y + randomNumberGenerator.Range(0f, 0.99f);

                        // set height
                        px = position.x * detailHeightMapScale;
                        py = position.z * detailHeightMapScale;
                        heightIndex = Mathf.FloorToInt(px) + Mathf.FloorToInt(py) * heightMapSize;
                        leftBottomH = heightMapData[heightIndex];
                        leftTopH = heightMapData[FixBounds(heightIndex + heightMapSize, heightDataSize, heightIndex)];
                        rightBottomH = heightMapData[heightIndex + 1];
                        rightTopH = heightMapData[FixBounds(heightIndex + heightMapSize + 1, heightDataSize, heightIndex)];

                        position.x *= sizeDetailXScale;
                        position.y = GPUInstancerUtility.SampleTerrainHeight(px - Mathf.Floor(px), py - Mathf.Floor(py), leftBottomH, leftTopH, rightBottomH, rightTopH) * terrainSize.y;
                        position.z *= sizeDetailZScale;
                        position += startPosition;

                        // get normal
                        //Vector3 terrainPointNormal = GPUInstancerUtility.ComputeTerrainNormal(leftBottomH, leftTopH, rightBottomH, normalScale);
                        P.y = leftBottomH * normalScale;
                        Q.y = leftTopH * normalScale;
                        P.y = rightBottomH * normalScale;
                        terrainPointNormal = Vector3.Cross(Q - R, R - P).normalized;

                        rotation.SetFromToRotation(Vector3.up, terrainPointNormal);
                        rotation *= Quaternion.AngleAxis(randomNumberGenerator.Range(0.0f, 360.0f), Vector3.up);

                        float randomScale = randomNumberGenerator.Range(0.0f, 1.0f);

                        float xzScale = detailPrototype.detailScale.x + (detailPrototype.detailScale.y - detailPrototype.detailScale.x) * randomScale;
                        float yScale = detailPrototype.detailScale.z + (detailPrototype.detailScale.w - detailPrototype.detailScale.z) * randomScale;

                        scale.x = xzScale;
                        scale.y = yScale;
                        scale.z = xzScale;

                        result[counter].SetTRS(position, rotation, scale);
                        counter++;
                    }
                }
            }

            return result;
        }

        private static Matrix4x4[] GetInstanceDataForDetailPrototypeWithComputeShader(GPUInstancerDetailPrototype detailPrototype, int[] detailMap, float[] heightMapData,
                                                                int detailMapSize, int heightMapSize,
                                                                int detailResolution, int heightResolution,
                                                                Vector3 startPosition, Vector3 terrainSize,
                                                                int instanceCount,
                                                                ComputeShader grassInstantiationComputeShader, GPUInstancerTerrainSettings terrainSettings,
                                                                ComputeBuffer counterBuffer, int[] counterData)
        {
            if (instanceCount < 0)
                instanceCount = 0;
            Matrix4x4[] result = new Matrix4x4[instanceCount];

            long expectedDetailData = (long)detailMapSize * detailMapSize;
            long expectedHeightData = (long)heightMapSize * heightMapSize;
            if (instanceCount == 0 || detailPrototype == null || detailMap == null ||
                detailMapSize <= 0 || heightMapData == null || heightMapData.Length == 0 ||
                heightMapSize <= 0 || detailResolution <= 0 || heightResolution <= 0 ||
                grassInstantiationComputeShader == null || counterBuffer == null ||
                counterData == null || counterData.Length == 0 ||
                expectedDetailData > detailMap.Length || expectedHeightData > heightMapData.Length)
                return result;

            ComputeBuffer visibilityBuffer = null;
            ComputeBuffer heightMapBuffer = null;
            ComputeBuffer detailMapBuffer = null;

            try
            {
                // set compute shader
                int grassInstantiationComputeKernelId = grassInstantiationComputeShader.FindKernel(GPUInstancerConstants.GRASS_INSTANTIATION_KERNEL);

                heightMapBuffer = new ComputeBuffer(heightMapData.Length, GPUInstancerConstants.STRIDE_SIZE_INT);
                heightMapBuffer.SetData(heightMapData);

                visibilityBuffer = new ComputeBuffer(instanceCount, GPUInstancerConstants.STRIDE_SIZE_MATRIX4X4);

                counterBuffer.SetData(counterData);

                detailMapBuffer = new ComputeBuffer(Mathf.CeilToInt(detailMapSize * detailMapSize), GPUInstancerConstants.STRIDE_SIZE_INT);
                detailMapBuffer.SetData(detailMap);

                // dispatch compute shader
                DispatchDetailComputeShader(grassInstantiationComputeShader, grassInstantiationComputeKernelId,
                    visibilityBuffer, detailMapBuffer, heightMapBuffer,
                    new Vector4(detailMapSize, detailMapSize, heightMapSize, heightMapSize), startPosition, terrainSize, detailResolution, heightResolution, detailPrototype.detailScale,
                    terrainSettings.GetHealthyDryNoiseTexture(detailPrototype), detailPrototype.noiseSpread, detailPrototype.GetInstanceID(), detailPrototype.detailDensity, counterBuffer, detailPrototype.terrainNormalEffect);

                visibilityBuffer.GetData(result);
                return result;
            }
            finally
            {
                // These buffers are scratch allocations for one prototype.
                // A failed dispatch/GetData must not strand them on the GPU.
                ReleaseComputeBuffer(ref detailMapBuffer);
                ReleaseComputeBuffer(ref visibilityBuffer);
                ReleaseComputeBuffer(ref heightMapBuffer);
            }
        }

        private static ComputeBuffer GetComputeBufferForDetailPrototypeWithComputeShader(GPUInstancerDetailPrototype detailPrototype,
                                                                int detailMapSize, int heightMapSize,
                                                                int detailResolution, int heightResolution,
                                                                Vector3 startPosition, Vector3 terrainSize,
                                                                int instanceCount,
                                                                ComputeShader grassInstantiationComputeShader, GPUInstancerTerrainSettings terrainSettings,
                                                                ComputeBuffer heightMapBuffer, ComputeBuffer detailMapBuffer, ComputeBuffer counterBuffer, int[] counterData)
        {
            if (instanceCount <= 0 || detailPrototype == null || detailMapSize <= 0 ||
                heightMapSize <= 0 || detailResolution <= 0 || heightResolution <= 0 ||
                grassInstantiationComputeShader == null || terrainSettings == null ||
                heightMapBuffer == null || detailMapBuffer == null || counterBuffer == null ||
                counterData == null || counterData.Length == 0 ||
                detailMapBuffer.count < detailMapSize * detailMapSize ||
                heightMapBuffer.count < heightMapSize * heightMapSize)
                return null;

            ComputeBuffer visibilityBuffer = null;
            bool ownershipTransferred = false;

            try
            {
                // set compute shader
                int grassInstantiationComputeKernelId = grassInstantiationComputeShader.FindKernel(GPUInstancerConstants.GRASS_INSTANTIATION_KERNEL);

                visibilityBuffer = new ComputeBuffer(instanceCount, GPUInstancerConstants.STRIDE_SIZE_MATRIX4X4);

                counterBuffer.SetData(counterData);

                // dispatch compute shader
                DispatchDetailComputeShader(grassInstantiationComputeShader, grassInstantiationComputeKernelId,
                    visibilityBuffer, detailMapBuffer, heightMapBuffer,
                    new Vector4(detailMapSize, detailMapSize, heightMapSize, heightMapSize), startPosition, terrainSize, detailResolution, heightResolution, detailPrototype.detailScale,
                    terrainSettings.GetHealthyDryNoiseTexture(detailPrototype), detailPrototype.noiseSpread, detailPrototype.GetInstanceID(), detailPrototype.detailDensity, counterBuffer, detailPrototype.terrainNormalEffect);

                // The caller stores the returned buffer in the cell dictionary.
                ownershipTransferred = true;
                return visibilityBuffer;
            }
            finally
            {
                // If dispatch/setup fails before the caller can claim it, the
                // visibility buffer is still a temporary allocation here.
                if (!ownershipTransferred)
                    ReleaseComputeBuffer(ref visibilityBuffer);
            }
        }

        private static void DispatchDetailComputeShader(ComputeShader grassComputeShader, int grassInstantiationComputeKernelId, ComputeBuffer visibilityBuffer, ComputeBuffer detailMapBuffer, ComputeBuffer heightMapBuffer, Vector4 detailAndHeightMapSize, Vector3 startPosition, Vector3 terrainSize, int detailResolution, int heightResolution, Vector4 detailScale, Texture healthyDryNoiseTexture, float noiseSpread, int instanceID, float detailDensity, ComputeBuffer counterBuffer, float terrainNormalEffect)
        {
            // setup compute shader
            grassComputeShader.SetBuffer(grassInstantiationComputeKernelId, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, visibilityBuffer);
            grassComputeShader.SetBuffer(grassInstantiationComputeKernelId, GPUInstancerConstants.GrassKernelProperties.DETAIL_MAP_DATA_BUFFER, detailMapBuffer);
            grassComputeShader.SetBuffer(grassInstantiationComputeKernelId, GPUInstancerConstants.GrassKernelProperties.HEIGHT_MAP_DATA_BUFFER, heightMapBuffer);
            grassComputeShader.SetBuffer(grassInstantiationComputeKernelId, GPUInstancerConstants.GrassKernelProperties.COUNTER_BUFFER, counterBuffer);
            grassComputeShader.SetInt(GPUInstancerConstants.GrassKernelProperties.TERRAIN_DETAIL_RESOLUTION_DATA, detailResolution);
            grassComputeShader.SetInt(GPUInstancerConstants.GrassKernelProperties.TERRAIN_HEIGHT_RESOLUTION_DATA, heightResolution);
            grassComputeShader.SetVector(GPUInstancerConstants.GrassKernelProperties.GRASS_START_POSITION_DATA, startPosition);
            grassComputeShader.SetVector(GPUInstancerConstants.GrassKernelProperties.TERRAIN_SIZE_DATA, terrainSize);
            grassComputeShader.SetVector(GPUInstancerConstants.GrassKernelProperties.DETAIL_SCALE_DATA, detailScale);
            grassComputeShader.SetVector(GPUInstancerConstants.GrassKernelProperties.DETAIL_AND_HEIGHT_MAP_SIZE_DATA, detailAndHeightMapSize);
            if (healthyDryNoiseTexture != null)
            {
                grassComputeShader.SetTexture(grassInstantiationComputeKernelId, GPUInstancerConstants.GrassKernelProperties.HEALTHY_DRY_NOISE_TEXTURE, healthyDryNoiseTexture);
                grassComputeShader.SetFloat(GPUInstancerConstants.GrassKernelProperties.NOISE_SPREAD, noiseSpread);
            }
            grassComputeShader.SetFloat(GPUInstancerConstants.GrassKernelProperties.DETAIL_UNIQUE_VALUE, instanceID / 1000f);
            grassComputeShader.SetFloat(GPUInstancerConstants.GrassKernelProperties.DETAIL_DENSITY, detailDensity);
            grassComputeShader.SetFloat(GPUInstancerConstants.GrassKernelProperties.TERRAIN_NORMAL_EFFECT, terrainNormalEffect);

            // dispatch
            GPUInstancerUtility.DispatchCompute(grassComputeShader, grassInstantiationComputeKernelId,
                Mathf.CeilToInt(detailAndHeightMapSize.x / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT_2D),
                1,
                Mathf.CeilToInt(detailAndHeightMapSize.y / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT_2D));
        }

        private void StartCoroutineAlt(IEnumerator routine)
        {
            if (Application.isPlaying)
                GPUInstancerLoopScheduler.StartCoroutine(this, routine);
            else
                while (routine.MoveNext()) ;
        }
        #endregion Helper Methods

        #region Spatial Partitioning Cell Management
        public override void InitializeSpatialPartitioning()
        {
            base.InitializeSpatialPartitioning();

            if (terrain == null || terrain.terrainData == null || terrainSettings == null ||
                terrain.terrainData.detailResolution <= 0 ||
                terrain.terrainData.heightmapResolution <= 0)
                return;

            GPUInstancerUtility.ReleaseSPBuffers(spData);
            spData = new GPUInstancerSpatialPartitioningData<GPUInstancerCell>();
            GPUInstancerUtility.CalculateSpatialPartitioningValuesFromTerrain(spData, terrain, terrainSettings.maxDetailDistance, terrainSettings.autoSPCellSize ? 0 : terrainSettings.preferedSPCellSize);

            if (spData == null || spData.cellRowAndCollumnCountPerTerrain <= 0)
                return;

            // initialize cells
            GenerateCellsInstanceDataFromTerrain();
        }

        private IEnumerator GenerateVisibilityBufferFromActiveCellsCoroutine()
        {
            return GenerateVisibilityBufferFromActiveCellsCoroutine(_detailCoroutineGeneration);
        }

        private IEnumerator GenerateVisibilityBufferFromActiveCellsCoroutine(int generation)
        {
            bool completed = false;
            try
            {
            if (!initalizingInstances && IsDetailCoroutineCurrent(generation))
            {
#if !UNITY_2017_1_OR_NEWER || UNITY_ANDROID
                if (_managedBuffer == null)
                    _managedBuffer = new ComputeBuffer(GPUInstancerConstants.BUFFER_COROUTINE_STEP_NUMBER, GPUInstancerConstants.STRIDE_SIZE_MATRIX4X4);
                if (_managedData == null)
                    _managedData = new Matrix4x4[GPUInstancerConstants.BUFFER_COROUTINE_STEP_NUMBER];
#endif
                if (runtimeDataList == null || spData == null ||
                    spData.cellRowAndCollumnCountPerTerrain <= 0)
                    yield break;

                List<GPUInstancerRuntimeData> runtimeDatas = new List<GPUInstancerRuntimeData>(runtimeDataList);

                int totalCount = 0;
                int lastbreak = 0;
                for (int r = 0; r < runtimeDatas.Count; r++)
                {
                    if (!IsDetailCoroutineCurrent(generation))
                        yield break;
                    GPUInstancerRuntimeData rd = runtimeDatas[r];
                    if (rd == null)
                        continue;

                    if (spData != null && spData.activeCellList != null && spData.activeCellList.Count > 0)
                    {
                        int totalInstanceCount = 0;

                        foreach (GPUInstancerCell baseCell in spData.activeCellList)
                        {
                            GPUInstancerDetailCell cell = baseCell as GPUInstancerDetailCell;
                            if (cell == null)
                                continue;
                            if (cell != null && cell.detailInstanceList != null &&
                                cell.detailInstanceList.TryGetValue(r, out Matrix4x4[] detailInstances) &&
                                detailInstances != null)
                            {
                                totalInstanceCount += detailInstances.Length;
                            }
                        }

                        rd.bufferSize = totalInstanceCount;
                        rd.instanceCount = totalInstanceCount;

                        if (totalInstanceCount == 0)
                        {
                            if (rd.transformationMatrixVisibilityBuffer != null)
                                rd.transformationMatrixVisibilityBuffer.Release();
                            rd.transformationMatrixVisibilityBuffer = null;
                            continue;
                        }

                        SetGeneratingVisibilityBuffer(
                            new ComputeBuffer(totalInstanceCount, GPUInstancerConstants.STRIDE_SIZE_MATRIX4X4),
                            generation);

                        int startIndex = 0;
                        int cellStartIndex = 0;
                        int count = 0;
                        for (int c = 0; c < spData.activeCellList.Count; c++)
                        {
                            if (!IsDetailCoroutineCurrent(generation))
                                yield break;
                            GPUInstancerDetailCell detailCell = spData.activeCellList[c] as GPUInstancerDetailCell;
                            if (detailCell == null || detailCell.detailInstanceList == null ||
                                !detailCell.detailInstanceList.ContainsKey(r) || detailCell.detailInstanceList[r] == null)
                                continue;
                            cellStartIndex = 0;
                            for (int i = 0; i < Mathf.Ceil((float)detailCell.detailInstanceList[r].Length / (float)GPUInstancerConstants.BUFFER_COROUTINE_STEP_NUMBER); i++)
                            {
                                cellStartIndex = i * GPUInstancerConstants.BUFFER_COROUTINE_STEP_NUMBER;
                                count = GPUInstancerConstants.BUFFER_COROUTINE_STEP_NUMBER;
                                if (cellStartIndex + count > detailCell.detailInstanceList[r].Length)
                                    count = detailCell.detailInstanceList[r].Length - cellStartIndex;
#if UNITY_2017_1_OR_NEWER && !UNITY_ANDROID
                                _generatingVisibilityBuffer.SetDataPartial(detailCell.detailInstanceList[r], cellStartIndex, startIndex, count);
#else
                                _generatingVisibilityBuffer.SetDataPartial(detailCell.detailInstanceList[r], cellStartIndex, startIndex, count, _managedBuffer, _managedData);
#endif
                                startIndex += count;
                                totalCount += count;

                                if (count + cellStartIndex < detailCell.detailInstanceList[r].Length - 1 && totalCount - lastbreak > GPUInstancerConstants.BUFFER_COROUTINE_STEP_NUMBER)
                                {
                                    lastbreak = totalCount;
                                    yield return null;
                                    if (!IsDetailCoroutineCurrent(generation))
                                        yield break;
                                }
                            }

                            if (initalizingInstances)
                                break;
                        }
                        if (initalizingInstances)
                            break;

                        if (initalizingInstances)
                            break;

                        if (rd.transformationMatrixVisibilityBuffer != null)
                            rd.transformationMatrixVisibilityBuffer.Release();
                        rd.transformationMatrixVisibilityBuffer = _generatingVisibilityBuffer;
                        // Ownership moved to runtimeData. Keep the field null
                        // so cancellation/teardown cannot release the same
                        // buffer a second time.
                        SetGeneratingVisibilityBuffer(null, generation);
                    }
                    if (initalizingInstances)
                        break;

                    GPUInstancerUtility.InitializeGPUBuffer(rd);
                    lastbreak = totalCount;
                    yield return null;
                    if (!IsDetailCoroutineCurrent(generation))
                        yield break;
                }
                if (initalizingInstances)
                {
                    if (IsDetailCoroutineCurrent(generation))
                    {
                        ReleaseGeneratingVisibilityBuffer(generation);
                        GPUInstancerUtility.ReleaseInstanceBuffers(runtimeDatas);
                    }
                    yield break;
                }

                replacingInstances = false;

                if (!initalizingInstances)
                {
                    if (_triggerEvent)
                        GPUInstancerUtility.TriggerEvent(GPUInstancerEventType.DetailInitializationFinished);
                    _triggerEvent = false;
                    isInitial = true;
                }
                completed = true;
            }
            }
            finally
            {
                // A scheduler cancellation disposes the iterator and runs
                // this block even when it was suspended at a yield. The
                // output buffer is only temporary until assigned to runtime
                // data, so release any uncommitted allocation here.
                ReleaseGeneratingVisibilityBuffer(generation);
#if !UNITY_2017_1_OR_NEWER || UNITY_ANDROID
                if (!completed && IsDetailCoroutineCurrent(generation))
                    ReleaseManagedBuffer();
#endif
                if (IsDetailCoroutineCurrent(generation))
                {
                    replacingInstances = false;
                    if (!completed)
                        _triggerEvent = false;
                }
            }
        }

        private IEnumerator MergeVisibilityBufferFromActiveCellsCoroutine()
        {
            return MergeVisibilityBufferFromActiveCellsCoroutine(_detailCoroutineGeneration);
        }

        private IEnumerator MergeVisibilityBufferFromActiveCellsCoroutine(int generation)
        {
            bool completed = false;
            ComputeBuffer heightMapBuffer = null;
            ComputeBuffer detailMapBuffer = null;
            try
            {
            if (!initalizingInstances && IsDetailCoroutineCurrent(generation))
            {
                if (runtimeDataList == null || spData == null ||
                    spData.cellRowAndCollumnCountPerTerrain <= 0 ||
                    terrain == null || terrain.terrainData == null ||
                    terrain.terrainData.detailResolution <= 0 ||
                    terrain.terrainData.heightmapResolution <= 0)
                    yield break;
                if (_grassInstantiationComputeShader == null || terrainSettings == null)
                {
                    Debug.LogError("GPU Instancer could not load the detail instantiation compute shader. Detail replacement was canceled.", diagnosticContext);
                    yield break;
                }

                List<GPUInstancerRuntimeData> runtimeDatas = new List<GPUInstancerRuntimeData>(runtimeDataList);

                int detailMapSize = terrain.terrainData.detailResolution / spData.cellRowAndCollumnCountPerTerrain;
                int heightMapSize = (terrain.terrainData.heightmapResolution - 1) / spData.cellRowAndCollumnCountPerTerrain + 1;
                int detailResolution = terrain.terrainData.detailResolution;
                int heightmapResolution = terrain.terrainData.heightmapResolution;
                Vector3 terrainSize = terrain.terrainData.size;
                GPUInstancerDetailCell heightMapCell = null;
                int generatedBuffers = 0;

                if (spData.activeCellList != null && spData.activeCellList.Count > 0)
                {
                    foreach (GPUInstancerCell baseCell in spData.activeCellList)
                    {
                        GPUInstancerDetailCell cell = baseCell as GPUInstancerDetailCell;
                        if (cell == null)
                            continue;
                        if (!IsDetailCoroutineCurrent(generation))
                            yield break;
                        generatedBuffers = 0;
                        if (cell != null && cell.heightMapData != null && cell.heightMapData.Length > 0 &&
                            cell.totalDetailCounts != null && cell.detailMapData != null)
                        {
                            if (cell.detailInstanceBuffers == null)
                                cell.detailInstanceBuffers = new Dictionary<int, ComputeBuffer>();

                            for (int r = 0; r < runtimeDatas.Count; r++)
                            {
                                if (!IsDetailCoroutineCurrent(generation))
                                    yield break;
                                if (r >= cell.totalDetailCounts.Count || r >= cell.detailMapData.Count ||
                                    prototypeList == null || r >= prototypeList.Count ||
                                    !(prototypeList[r] is GPUInstancerDetailPrototype))
                                    continue;
                                if (cell.totalDetailCounts[r] > 0 && cell.detailMapData[r] != null &&
                                    cell.detailMapData[r].Length == detailMapSize * detailMapSize &&
                                    cell.heightMapData.Length == heightMapSize * heightMapSize)
                                {
                                    if (!cell.detailInstanceBuffers.ContainsKey(r) || cell.detailInstanceBuffers[r] == null)
                                    {
#if UNITY_EDITOR
                                        UnityEngine.Profiling.Profiler.BeginSample("GPUInstancerDetailManager.MergeVisibilityBufferFromActiveCellsCoroutine");
#endif

                                        if (heightMapCell != cell || heightMapBuffer == null ||
                                            heightMapBuffer.count != cell.heightMapData.Length)
                                        {
                                            ReleaseComputeBuffer(ref heightMapBuffer);
                                            heightMapBuffer = new ComputeBuffer(cell.heightMapData.Length, GPUInstancerConstants.STRIDE_SIZE_INT);
                                            heightMapBuffer.SetData(cell.heightMapData);
                                            heightMapCell = cell;
                                        }

                                        if (detailMapBuffer == null ||
                                            detailMapBuffer.count != detailMapSize * detailMapSize)
                                        {
                                            ReleaseComputeBuffer(ref detailMapBuffer);
                                            detailMapBuffer = new ComputeBuffer(detailMapSize * detailMapSize, GPUInstancerConstants.STRIDE_SIZE_INT);
                                        }
                                        detailMapBuffer.SetData(cell.detailMapData[r]);

                                        ComputeBuffer generatedBuffer = null;
                                        try
                                        {
                                            generatedBuffer =
                                                GetComputeBufferForDetailPrototypeWithComputeShader((GPUInstancerDetailPrototype)prototypeList[r],
                                                    detailMapSize, heightMapSize,
                                                    detailResolution, heightmapResolution,
                                                    cell.instanceStartPosition,
                                                    terrainSize, cell.totalDetailCounts[r], _grassInstantiationComputeShader, terrainSettings,
                                                    heightMapBuffer, detailMapBuffer, counterBuffer, counterData);
                                            cell.detailInstanceBuffers[r] = generatedBuffer;
                                            generatedBuffer = null;
                                        }
                                        finally
                                        {
                                            // Dictionary assignment is the
                                            // ownership hand-off. If it fails,
                                            // release the just-created buffer.
                                            ReleaseComputeBuffer(ref generatedBuffer);
                                        }
                                        generatedBuffers += detailMapSize;

#if UNITY_EDITOR
                                        UnityEngine.Profiling.Profiler.EndSample();
#endif
                                        if (generatedBuffers >= GPUInstancerConstants.DETAIL_BUFFER_MERGE_FRAME_LIMIT)
                                        {
                                            generatedBuffers = 0;
                                            yield return null;
                                            if (!IsDetailCoroutineCurrent(generation))
                                                yield break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                for (int r = 0; r < runtimeDatas.Count; r++)
                {
                    if (!IsDetailCoroutineCurrent(generation))
                        yield break;
                    GPUInstancerRuntimeData rd = runtimeDatas[r];
                    if (rd == null)
                        continue;

                    if (spData != null && spData.activeCellList != null && spData.activeCellList.Count > 0)
                    {
                        int totalInstanceCount = 0;

                        foreach (GPUInstancerCell baseCell in spData.activeCellList)
                        {
                            GPUInstancerDetailCell cell = baseCell as GPUInstancerDetailCell;
                            if (cell == null)
                                continue;
                            if (cell != null && cell.heightMapData != null && cell.heightMapData.Length > 0 &&
                                cell.totalDetailCounts != null && cell.detailMapData != null &&
                                r < cell.totalDetailCounts.Count && r < cell.detailMapData.Count &&
                                cell.detailMapData[r] != null && cell.detailMapData[r].Length > 0)
                            {
                                totalInstanceCount += cell.totalDetailCounts[r];
                            }
                        }

                        rd.bufferSize = totalInstanceCount;
                        rd.instanceCount = totalInstanceCount;

                        if (totalInstanceCount == 0)
                        {
                            if (rd.transformationMatrixVisibilityBuffer != null)
                                rd.transformationMatrixVisibilityBuffer.Release();
                            rd.transformationMatrixVisibilityBuffer = null;
                            continue;
                        }

                        SetGeneratingVisibilityBuffer(
                            new ComputeBuffer(totalInstanceCount, GPUInstancerConstants.STRIDE_SIZE_MATRIX4X4),
                            generation);

                        int startIndex = 0;
                        for (int c = 0; c < spData.activeCellList.Count; c++)
                        {
                            if (!IsDetailCoroutineCurrent(generation))
                                yield break;
                            GPUInstancerDetailCell detailCell = spData.activeCellList[c] as GPUInstancerDetailCell;

                            if (detailCell == null || detailCell.detailInstanceBuffers == null ||
                                !detailCell.detailInstanceBuffers.ContainsKey(r) || detailCell.detailInstanceBuffers[r] == null)
                                continue;

                            _generatingVisibilityBuffer.CopyComputeBuffer(startIndex, detailCell.detailInstanceBuffers[r].count, detailCell.detailInstanceBuffers[r]);

                            startIndex += detailCell.detailInstanceBuffers[r].count;

                            //yield return null;
                        }

                        if (rd.transformationMatrixVisibilityBuffer != null)
                            rd.transformationMatrixVisibilityBuffer.Release();
                        rd.transformationMatrixVisibilityBuffer = _generatingVisibilityBuffer;
                        SetGeneratingVisibilityBuffer(null, generation);
                    }

                    GPUInstancerUtility.InitializeGPUBuffer(rd);
                }
                replacingInstances = false;

                if (_triggerEvent)
                    GPUInstancerUtility.TriggerEvent(GPUInstancerEventType.DetailInitializationFinished);
                _triggerEvent = false;
                completed = true;
            }
            }
            finally
            {
                // Both map buffers are scratch state shared by this pass and
                // must be released when the scheduler disposes it mid-frame.
                ReleaseComputeBuffer(ref heightMapBuffer);
                ReleaseComputeBuffer(ref detailMapBuffer);
                ReleaseGeneratingVisibilityBuffer(generation);
                if (IsDetailCoroutineCurrent(generation))
                {
                    replacingInstances = false;
                    if (!completed)
                        _triggerEvent = false;
                }
            }
        }

        private void GenerateCellsInstanceDataFromTerrain()
        {
            if (counterBuffer == null)
            {
                if (SystemInfo.supportsComputeShaders)
                    counterBuffer = new ComputeBuffer(1, GPUInstancerConstants.STRIDE_SIZE_INT);
            }

            StartCoroutineAlt(FillCellsDetailData(terrain, _detailCoroutineGeneration));
        }

        public void FillCellsDetailDataCallBack()
        {
            FillCellsDetailDataCallBack(_detailCoroutineGeneration);
        }

        private void FillCellsDetailDataCallBack(int generation)
        {
            if (!IsDetailCoroutineCurrent(generation))
                return;

            // This method may also be invoked directly by a synchronous
            // (non-threaded) pass while an older coalesced callback is still
            // queued. Mark the queued slot consumed so a successful direct
            // completion cannot leave the generation permanently blocked.
            Interlocked.Exchange(ref _detailCallbackPending, 0);

            ClearCompletedThreads();
            bool pendingThreadStarts;
            lock (threadStartQueue)
                pendingThreadStarts = threadStartQueue.Count > 0;
            if (threadHeightMapData == null ||
                (runInThreads && (activeThreads.Count > 0 || pendingThreadStarts)))
            {
                // A worker can enqueue its completion action just before its
                // thread transitions to the stopped state.  If Tick consumes
                // that action in the tiny window where IsAlive is still true,
                // simply returning would strand initialization forever.  Keep
                // a single retry in the main-thread queue; the generation
                // guard makes retries harmless after teardown.
                if (threadHeightMapData != null && IsDetailCoroutineCurrent(generation))
                    EnqueueFillCellsDetailCallback(generation);
                return;
            }

            threadHeightMapData = null;
            threadDetailMapData = null;

            if (spData == null || terrain == null || terrain.terrainData == null ||
                terrainSettings == null || counterBuffer == null)
            {
                CancelDetailInitialization(generation);
                return;
            }

            if (GPUInstancerConstants.DETAIL_STORE_INSTANCE_DATA)
            {
                int detailMapSize = terrain.terrainData.detailResolution / spData.cellRowAndCollumnCountPerTerrain;
                int heightMapSize = (terrain.terrainData.heightmapResolution - 1) / spData.cellRowAndCollumnCountPerTerrain + 1;
                int detailResolution = terrain.terrainData.detailResolution;
                int heightmapResolution = terrain.terrainData.heightmapResolution;
                Vector3 terrainSize = terrain.terrainData.size;

                StartCoroutineAlt(SetInstanceDataForDetailCells(spData, prototypeList, detailMapSize, heightMapSize, detailResolution, heightmapResolution, terrainSize,
                    counterBuffer, counterData, terrainSettings,
                    () => SetInstanceDataForDetailCellsCallback(generation),
                    () => CancelDetailInitialization(generation),
                    () => IsDetailCoroutineCurrent(generation)));
            }
            else
                SetInstanceDataForDetailCellsCallback(generation);
        }

        private void SetInstanceDataForDetailCellsCallback()
        {
            SetInstanceDataForDetailCellsCallback(_detailCoroutineGeneration);
        }

        private void SetInstanceDataForDetailCellsCallback(int generation)
        {
            if (!IsDetailCoroutineCurrent(generation))
                return;

            initalizingInstances = false;
            if (spData == null)
                return;
            if (spData.activeCellList != null)
            {
                foreach (GPUInstancerCell cell in spData.activeCellList)
                {
                    if (cell != null)
                        cell.isActive = false;
                }
                spData.activeCellList.Clear();
            }
            _triggerEvent = true;
        }

        private void CancelDetailInitialization(int generation)
        {
            if (!IsDetailCoroutineCurrent(generation))
                return;

            unchecked
            {
                _detailCoroutineGeneration++;
            }
            threadHeightMapData = null;
            threadDetailMapData = null;
            initalizingInstances = false;
            replacingInstances = false;
            _triggerEvent = false;
        }

        private static IEnumerator SetInstanceDataForDetailCells(GPUInstancerSpatialPartitioningData<GPUInstancerCell> spData, List<GPUInstancerPrototype> prototypeList,
            int detailMapSize, int heightMapSize, int detailResolution, int heightmapResolution, Vector3 terrainSize, ComputeBuffer counterBuffer, int[] counterData,
            GPUInstancerTerrainSettings terrainSettings, Action callback, Action cancellationCallback, Func<bool> isCurrent)
        {
            bool completed = false;
            try
            {
                if (spData == null || prototypeList == null ||
                    (isCurrent != null && !isCurrent()))
                    yield break;

                int totalCreated = 0;
                foreach (GPUInstancerCell baseCell in spData.GetCellList())
                {
                    GPUInstancerDetailCell cell = baseCell as GPUInstancerDetailCell;
                    if (cell == null)
                        continue;
                    if (isCurrent != null && !isCurrent())
                        yield break;
                    if (cell == null || cell.heightMapData == null || cell.heightMapData.Length == 0 ||
                        cell.detailMapData == null || cell.totalDetailCounts == null)
                        continue;

                    cell.detailInstanceList = new Dictionary<int, Matrix4x4[]>();
                    int prototypeCount = Mathf.Min(prototypeList.Count,
                        Mathf.Min(cell.detailMapData.Count, cell.totalDetailCounts.Count));
                    for (int i = 0; i < prototypeCount; i++)
                    {
                        if (isCurrent != null && !isCurrent())
                            yield break;
                        if (!(prototypeList[i] is GPUInstancerDetailPrototype detailPrototype))
                            continue;

                        int[] detailMap = cell.detailMapData[i];
                        if (detailMap == null || detailMap.Length == 0)
                        {
                            if (i < cell.totalDetailCounts.Count)
                                cell.totalDetailCounts[i] = 0;
                            continue;
                        }

                        totalCreated += cell.totalDetailCounts[i];
#if !UNITY_EDITOR && UNITY_ANDROID
                        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
                        {
                            cell.detailInstanceList[i] = GetInstanceDataForDetailPrototype(detailPrototype, cell.detailMapData[i], cell.heightMapData,
                                detailMapSize, heightMapSize,
                                detailResolution, heightmapResolution,
                                cell.instanceStartPosition,
                                terrainSize, cell.totalDetailCounts[i]);
                        }
                        else
                        {
#endif
                        cell.detailInstanceList[i] = GetInstanceDataForDetailPrototypeWithComputeShader(detailPrototype, detailMap, cell.heightMapData,
                            detailMapSize, heightMapSize,
                            detailResolution, heightmapResolution,
                            cell.instanceStartPosition,
                            terrainSize, cell.totalDetailCounts[i], _grassInstantiationComputeShader, terrainSettings, counterBuffer, counterData);
#if !UNITY_EDITOR && UNITY_ANDROID
                        }
#endif
                        if (totalCreated >= GPUInstancerConstants.BUFFER_COROUTINE_STEP_NUMBER)
                        {
                            totalCreated = 0;
                            yield return null;
                            if (isCurrent != null && !isCurrent())
                                yield break;
                        }
                    }
                }

                if (callback != null)
                    callback();
                completed = true;
            }
            finally
            {
                if (!completed && cancellationCallback != null)
                    cancellationCallback();
            }
        }

        public float[,] threadHeightMapData;
        public List<int[,]> threadDetailMapData;
        public int threadHeightResolution;
        public int threadDetailResolution;

        public IEnumerator FillCellsDetailData(Terrain terrain)
        {
            // Capture the generation at scheduling time. Iterator bodies do
            // not execute until the PlayerLoop calls MoveNext(), so reading
            // the field inside the body would let a stale queued routine
            // accidentally adopt a newer generation after teardown.
            return FillCellsDetailData(terrain, _detailCoroutineGeneration);
        }

        private IEnumerator FillCellsDetailData(Terrain terrain, int generation)
        {
            bool completed = false;
            try
            {
                if (!IsDetailCoroutineCurrent(generation) || terrain == null || terrain.terrainData == null ||
                    terrain.terrainData.heightmapResolution <= 0 || terrain.terrainData.detailResolution <= 0 ||
                    spData == null || spData.cellRowAndCollumnCountPerTerrain <= 0)
                    yield break;

                GPUInstancerSpatialPartitioningData<GPUInstancerCell> spatialData = spData;
                int heightResolution = terrain.terrainData.heightmapResolution;
                int detailResolution = terrain.terrainData.detailResolution;
                threadHeightResolution = heightResolution;
                threadDetailResolution = detailResolution;

                if (threadHeightMapData == null)
                {
#if UNITY_EDITOR
                    UnityEngine.Profiling.Profiler.BeginSample("GPUInstancerDetailManager.FillCellsDetailData.GetHeights");
#endif
                    threadHeightMapData = terrain.terrainData.GetHeights(0, 0, heightResolution, heightResolution);
#if UNITY_EDITOR
                    UnityEngine.Profiling.Profiler.EndSample();
#endif
                }
                if (threadDetailMapData == null)
                {
                    threadDetailMapData = new List<int[,]>();
                    DetailPrototype[] detailPrototypes = terrain.terrainData.detailPrototypes;
                    int detailPrototypeCount = detailPrototypes == null ? 0 : detailPrototypes.Length;
                    for (int i = 0; i < detailPrototypeCount; i++)
                    {
                        if (!IsDetailCoroutineCurrent(generation))
                            yield break;
#if UNITY_EDITOR
                        UnityEngine.Profiling.Profiler.BeginSample("GPUInstancerDetailManager.FillCellsDetailData.GetDetailLayer");
#endif
                        threadDetailMapData.Add(terrain.terrainData.GetDetailLayer(0, 0, detailResolution, detailResolution, i));
#if UNITY_EDITOR
                        UnityEngine.Profiling.Profiler.EndSample();
#endif
                        if (runInThreads && i % 3 == 0)
                        {
                            yield return null;
                            if (!IsDetailCoroutineCurrent(generation))
                                yield break;
                        }
                    }
                }

                // Snapshot the arrays before queuing work. ClearInstancingData
                // nulls the manager fields during teardown, but workers that
                // are already running can safely finish a stale snapshot and
                // will stop at their generation check.
                float[,] heightMapData = threadHeightMapData;
                List<int[,]> detailMapData = threadDetailMapData;
                if (heightMapData == null || heightMapData.Length == 0 || detailMapData == null)
                    yield break;

                // A Terrain with no detail layers is valid. There is no cell
                // work to queue, but the initialization callback still must
                // run so initalizingInstances cannot remain stuck forever.
                if (detailMapData.Count == 0)
                {
                    FillCellsDetailDataCallBack(generation);
                    completed = true;
                    yield break;
                }

                if (runInThreads)
                {
#if UNITY_EDITOR
                    UnityEngine.Profiling.Profiler.BeginSample("GPUInstancerDetailManager.FillCellsDetailData.Threading");
#endif
                    ParameterizedThreadStart fillCellsDetailData = new ParameterizedThreadStart(FillCellsDetailDataThread);
                    Vector4 coord = Vector4.zero;
                    coord.z = Mathf.CeilToInt(spatialData.cellRowAndCollumnCountPerTerrain / 2.0f);
                    coord.w = spatialData.cellRowAndCollumnCountPerTerrain;
                    lock (threadStartQueue)
                    {
                        threadStartQueue.Enqueue(new GPUIThreadData()
                        {
                            thread = new Thread(fillCellsDetailData) { IsBackground = true },
                            parameter = new DetailThreadRequest()
                            {
                                coordinate = coord,
                                generation = generation,
                                spatialData = spatialData,
                                heightMapData = heightMapData,
                                detailMapData = detailMapData,
                                heightResolution = heightResolution,
                                detailResolution = detailResolution
                            }
                        });
                    }

                    if (spatialData.cellRowAndCollumnCountPerTerrain > 1)
                    {
                        coord.x = coord.z;
                        coord.z = spatialData.cellRowAndCollumnCountPerTerrain;
                        lock (threadStartQueue)
                        {
                            threadStartQueue.Enqueue(new GPUIThreadData()
                            {
                                thread = new Thread(fillCellsDetailData) { IsBackground = true },
                                parameter = new DetailThreadRequest()
                                {
                                    coordinate = coord,
                                    generation = generation,
                                    spatialData = spatialData,
                                    heightMapData = heightMapData,
                                    detailMapData = detailMapData,
                                    heightResolution = heightResolution,
                                    detailResolution = detailResolution
                                }
                            });
                        }
                    }
#if UNITY_EDITOR
                    UnityEngine.Profiling.Profiler.EndSample();
#endif
                }
                else
                {
#if UNITY_EDITOR
                    UnityEngine.Profiling.Profiler.BeginSample("GPUInstancerDetailManager.FillCellsDetailData.Main");
#endif
                    Vector4 coord = new Vector4(0, 0, spatialData.cellRowAndCollumnCountPerTerrain, spatialData.cellRowAndCollumnCountPerTerrain);
                    StartCoroutineAlt(FillCellsDetailDataCoroutine(coord, generation, spatialData, heightMapData, detailMapData,
                        heightResolution, detailResolution));
#if UNITY_EDITOR
                    UnityEngine.Profiling.Profiler.EndSample();
#endif
                }

                completed = true;
            }
            finally
            {
                // If this outer iterator is cancelled while collecting maps,
                // leave no initialization marker or stale arrays behind. On
                // the normal path the nested cell iterator owns the callback.
                if (!completed && IsDetailCoroutineCurrent(generation))
                    CancelDetailInitialization(generation);
            }
        }

        public void FillCellsDetailDataThread(object parameter)
        {
            DetailThreadRequest request = parameter as DetailThreadRequest;
            int generation = request != null ? request.generation : _detailCoroutineGeneration;
            try
            {
                Vector4 coord = request != null ? request.coordinate : (Vector4)parameter;
                int startX = (int)coord.x;
                int startZ = (int)coord.y;
                int endX = (int)coord.z;
                int endZ = (int)coord.w;

                GPUInstancerSpatialPartitioningData<GPUInstancerCell> spatialData =
                    request != null ? request.spatialData : spData;
                float[,] heightMapData = request != null ? request.heightMapData : threadHeightMapData;
                List<int[,]> detailMapData = request != null ? request.detailMapData : threadDetailMapData;
                int heightResolution = request != null ? request.heightResolution : threadHeightResolution;
                int detailResolution = request != null ? request.detailResolution : threadDetailResolution;
                if (spatialData == null || heightMapData == null || heightMapData.Length == 0 ||
                    detailMapData == null || detailMapData.Count == 0 ||
                    heightResolution <= 0 || detailResolution <= 0 ||
                    spatialData.cellRowAndCollumnCountPerTerrain <= 0 ||
                    !IsDetailCoroutineCurrent(generation))
                    return;

                int detailMapSize = detailResolution / spatialData.cellRowAndCollumnCountPerTerrain;
                int heightMapSize = ((heightResolution - 1) / spatialData.cellRowAndCollumnCountPerTerrain) + 1;
                if (detailMapSize <= 0 || heightMapSize <= 0)
                    return;
                //Debug.Log("Cell DetailMapSize: " + detailMapSize + " Cell HeightMapSize: " + heightMapSize);

                GPUInstancerCell cell = null;

                for (int z = startZ; z < endZ; z++)
                {
                    for (int x = startX; x < endX; x++)
                    {
                        if (!IsDetailCoroutineCurrent(generation) || spatialData == null)
                            return;
                        int hash = GPUInstancerCell.CalculateHash(x, 0, z);
                        spatialData.GetCell(hash, out cell);

                        if (cell is GPUInstancerDetailCell detailCell)
                        {
                            if (detailCell.coordX < 0 || detailCell.coordZ < 0 ||
                                !CanSlice(heightMapData,
                                    detailCell.coordX * (heightMapSize - 1),
                                    detailCell.coordZ * (heightMapSize - 1),
                                    heightMapSize, heightMapSize))
                                continue;
                            detailCell.heightMapData = heightMapData.MirrorAndFlatten(detailCell.coordX * (heightMapSize - 1), detailCell.coordZ * (heightMapSize - 1),
                                heightMapSize, heightMapSize);
                            if (detailCell.heightMapData == null || detailCell.heightMapData.Length == 0)
                                continue;

                            detailCell.detailMapData = new List<int[]>();
                            detailCell.totalDetailCounts = new List<int>();

                            for (int i = 0; i < detailMapData.Count; i++)
                            {
                                if (!IsDetailCoroutineCurrent(generation))
                                    return;
                                if (detailMapData[i] == null ||
                                    !CanSlice(detailMapData[i], detailCell.coordX * detailMapSize,
                                        detailCell.coordZ * detailMapSize, detailMapSize, detailMapSize))
                                {
                                    detailCell.detailMapData.Add(new int[detailMapSize * detailMapSize]);
                                    detailCell.totalDetailCounts.Add(0);
                                    continue;
                                }
                                int[] flattenedDetailMapData = detailMapData[i].MirrorAndFlatten(detailCell.coordX * detailMapSize, detailCell.coordZ * detailMapSize,
                                    detailMapSize, detailMapSize);
                                detailCell.detailMapData.Add(flattenedDetailMapData);
                                int total = 0;
                                foreach (int num in flattenedDetailMapData)
                                    total += num;
                                detailCell.totalDetailCounts.Add(total);
                            }
                        }
                        else
                        {
                            // A scene can unload/rebuild a cell between the
                            // snapshot and the worker pass. Treat the missing
                            // entry as an empty cell; cancellation/retry is
                            // handled by the generation token rather than
                            // turning a benign stale lookup into a failed
                            // initialization.
                            continue;
                        }
                    }
                }

                EnqueueFillCellsDetailCallback(generation);
            }
            catch (Exception e)
            {
                lock (threadQueue)
                    threadQueue.Enqueue(() =>
                    {
                        if (!IsDetailCoroutineCurrent(generation))
                            return;
                        Debug.LogException(e, diagnosticContext);
                        CancelDetailInitialization(generation);
                    });
            }
        }

        public IEnumerator FillCellsDetailDataCoroutine(Vector4 coord)
        {
            return FillCellsDetailDataCoroutine(coord, _detailCoroutineGeneration, spData,
                threadHeightMapData, threadDetailMapData, threadHeightResolution, threadDetailResolution);
        }

        private IEnumerator FillCellsDetailDataCoroutine(Vector4 coord, int generation,
            GPUInstancerSpatialPartitioningData<GPUInstancerCell> spatialData,
            float[,] heightMapData, List<int[,]> detailMapData,
            int heightResolution, int detailResolution)
        {
            bool completed = false;
            try
            {
                if (!IsDetailCoroutineCurrent(generation) || spatialData == null ||
                    heightMapData == null || heightMapData.Length == 0 ||
                    detailMapData == null ||
                    heightResolution <= 0 || detailResolution <= 0)
                    yield break;

                int startX = (int)coord.x;
                int startZ = (int)coord.y;
                int endX = (int)coord.z;
                int endZ = (int)coord.w;

                int cellCount = spatialData.cellRowAndCollumnCountPerTerrain;
                if (cellCount <= 0)
                    yield break;
                int detailMapSize = detailResolution / cellCount;
                int heightMapSize = ((heightResolution - 1) / cellCount) + 1;
                if (detailMapSize <= 0 || heightMapSize <= 0)
                    yield break;

                if (detailMapData.Count == 0)
                {
                    FillCellsDetailDataCallBack(generation);
                    completed = true;
                    yield break;
                }
                //Debug.Log("Cell DetailMapSize: " + detailMapSize + " Cell HeightMapSize: " + heightMapSize);

                GPUInstancerCell cell = null;

                for (int z = startZ; z < endZ; z++)
                {
                    for (int x = startX; x < endX; x++)
                    {
                        if (!IsDetailCoroutineCurrent(generation))
                            yield break;
                        int hash = GPUInstancerCell.CalculateHash(x, 0, z);
                        spatialData.GetCell(hash, out cell);

                        if (cell is GPUInstancerDetailCell detailCell)
                        {
                            if (detailCell.coordX < 0 || detailCell.coordZ < 0 ||
                                !CanSlice(heightMapData,
                                    detailCell.coordX * (heightMapSize - 1),
                                    detailCell.coordZ * (heightMapSize - 1),
                                    heightMapSize, heightMapSize))
                                continue;
                            detailCell.heightMapData = heightMapData.MirrorAndFlatten(detailCell.coordX * (heightMapSize - 1), detailCell.coordZ * (heightMapSize - 1),
                            heightMapSize, heightMapSize);
                            if (detailCell.heightMapData == null || detailCell.heightMapData.Length == 0)
                                continue;

                            detailCell.detailMapData = new List<int[]>();
                            detailCell.totalDetailCounts = new List<int>();

                            for (int i = 0; i < detailMapData.Count; i++)
                            {
                                if (!IsDetailCoroutineCurrent(generation))
                                    yield break;
                                if (detailMapData[i] == null ||
                                    !CanSlice(detailMapData[i], detailCell.coordX * detailMapSize,
                                        detailCell.coordZ * detailMapSize, detailMapSize, detailMapSize))
                                {
                                    detailCell.detailMapData.Add(new int[detailMapSize * detailMapSize]);
                                    detailCell.totalDetailCounts.Add(0);
                                    continue;
                                }
                                int[] flattenedDetailMapData = detailMapData[i].MirrorAndFlatten(detailCell.coordX * detailMapSize, detailCell.coordZ * detailMapSize,
                                    detailMapSize, detailMapSize);
                                detailCell.detailMapData.Add(flattenedDetailMapData);
                                int total = 0;
                                foreach (int num in flattenedDetailMapData)
                                    total += num;
                                detailCell.totalDetailCounts.Add(total);
                            }
                            yield return null;
                            if (!IsDetailCoroutineCurrent(generation))
                                yield break;
                        }
                        else
                        {
                            // A missing cell is equivalent to an empty cell
                            // during a scene reload; avoid aborting the entire
                            // initialization pass for a stale hash lookup.
                            continue;
                        }
                    }
                }

                FillCellsDetailDataCallBack(generation);
                completed = true;
            }
            finally
            {
                // The callback clears these arrays on success. If the player
                // loop disposes this iterator while suspended, clear them and
                // release the initialization marker so a later bind can retry.
                if (!completed && IsDetailCoroutineCurrent(generation))
                    CancelDetailInitialization(generation);
            }
        }
        #endregion Spatial Partitioning Cell Management

        #region Public Methods
        public void SetDetailMapData(List<int[,]> detailMapData)
        {
            threadDetailMapData = detailMapData;
        }

        public int[,] GetDetailLayer(int layer)
        {
            if (!isInitialized || terrain == null || terrain.terrainData == null ||
                spData == null || spData.cellRowAndCollumnCountPerTerrain <= 0 ||
                layer < 0)
                return null;
            int detailResolution = terrain.terrainData.detailResolution;
            int detailMapSize = detailResolution / spData.cellRowAndCollumnCountPerTerrain;
            if (detailResolution <= 0 || detailMapSize <= 0)
                return null;
            int[,] result = new int[detailResolution, detailResolution];
            GPUInstancerCell cell;
            GPUInstancerDetailCell detailCell;
            for (int cx = 0; cx < spData.cellRowAndCollumnCountPerTerrain; cx++)
            {
                for (int cz = 0; cz < spData.cellRowAndCollumnCountPerTerrain; cz++)
                {
                    if (spData.GetCell(GPUInstancerCell.CalculateHash(cx, 0, cz), out cell))
                    {
                        detailCell = cell as GPUInstancerDetailCell;
                        if (detailCell != null && detailCell.detailMapData != null &&
                            layer < detailCell.detailMapData.Count &&
                            detailCell.detailMapData[layer] != null &&
                            detailCell.detailMapData[layer].Length >= detailMapSize * detailMapSize)
                        {
                            for (int x = 0; x < detailMapSize; x++)
                            {
                                for (int y = 0; y < detailMapSize; y++)
                                {
                                    result[y + cz * detailMapSize, x + cx * detailMapSize] = detailCell.detailMapData[layer][x + y * detailMapSize];
                                }
                            }
                        }
                    }
                }
            }
            return result;
        }

        public List<int[,]> GetDetailMapData()
        {
            if (!isInitialized || terrain == null || spData == null)
                return null;
            List<int[,]> result = new List<int[,]>();
            if (prototypeList == null)
                return result;
            for (int i = 0; i < prototypeList.Count; i++)
            {
                result.Add(GetDetailLayer(i));
            }
            return result;
        }
        #endregion
    }

}
