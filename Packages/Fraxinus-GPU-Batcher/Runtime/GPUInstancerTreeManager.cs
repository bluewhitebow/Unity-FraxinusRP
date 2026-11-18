using System.Collections.Generic;
using UnityEngine;
using System.Collections;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Runtime engine for GPU-instanced Terrain trees. Instances are created
    /// from Tree Profiles and bound to scene Terrains by the Terrain binder.
    /// </summary>
    public class GPUInstancerTreeManager : GPUInstancerTerrainManager
    {
        private static ComputeShader _treeInstantiationComputeShader;
        public bool initializeWithCoroutine = true;
        private bool _isCoroutineActive;

        protected override void ApplyProfile(GPUInstancerProfile value, bool copyCollections)
        {
            base.ApplyProfile(value, copyCollections);
            GPUInstancerTreeProfile treeProfile = value as GPUInstancerTreeProfile;
            if (treeProfile != null)
                initializeWithCoroutine = treeProfile.initializeWithCoroutine;
        }

        #region Explicit lifecycle
        public override void Initialize()
        {
            base.Initialize();

            if (_treeInstantiationComputeShader == null)
                _treeInstantiationComputeShader = Resources.Load<ComputeShader>(GPUInstancerConstants.TREE_INSTANTIATION_RESOURCE_PATH);
        }

        public override void Tick()
        {
            base.Tick();

            if (Application.isPlaying && _requiresTerrainUpdate && !_isCoroutineActive)
            {
                GPUInstancerLoopScheduler.StartCoroutine(this, ReplaceUnityTrees());
                _requiresTerrainUpdate = false;
            }
        }

        protected override void DeactivateCore()
        {
            // A queued iterator can be disposed before its first MoveNext;
            // in that case ReplaceUnityTrees' body/finally never runs and the
            // active flag would otherwise permanently block future rebinds.
            _isCoroutineActive = false;
            try
            {
                base.DeactivateCore();
            }
            finally
            {
                _isCoroutineActive = false;
            }
        }

        #endregion Explicit lifecycle

        #region Override Methods

        public override void ClearInstancingData()
        {
            // GeneratePrototypes/terrain rebinding can clear runtime data
            // without going through Deactivate. Cancel any owner-bound tree
            // replacement first so a stale iterator cannot repopulate buffers
            // after the lists below have been released.
            _isCoroutineActive = false;
            GPUInstancerLoopScheduler.CancelCoroutines(this);
            try
            {
                base.ClearInstancingData();
            }
            finally
            {
                _isCoroutineActive = false;
            }

            List<Terrain> terrains = GetTerrains();
            if (terrains != null)
            {
                for (int i = 0; i < terrains.Count; i++)
                    RestoreTerrainSettingsAfterUnbind(terrains[i]);
            }
        }

        /// <summary>
        /// RemoveTerrain queues the list mutation, so restore the Unity tree
        /// distance before that queue is consumed (otherwise the removed
        /// terrain is no longer visited by ClearInstancingData).
        /// </summary>
        protected override void RestoreTerrainSettingsAfterUnbind(Terrain terrain)
        {
            if (terrain != null && terrainSettings != null && terrain.treeDistance == 0)
                terrain.treeDistance = terrainSettings.maxTreeDistance;
        }

        protected override void ReapplyTerrainSettingsAfterRebind(Terrain terrain)
        {
            // ReplaceUnityTrees owns this value only after the manager has
            // actually initialized a tree channel. A dormant profile must not
            // hide Unity's terrain trees merely because its proxy was toggled.
            if (terrain != null && isActive && isInitialized &&
                prototypeList != null && prototypeList.Count > 0)
            {
                terrain.treeDistance = 0f;
            }
        }

        public override void GeneratePrototypes(bool forceNew = false)
        {
            // In Play Mode a Profile is the authoritative recipe.  A forced
            // rebuild used to clear the transient Profile clones and then
            // repopulate the manager from TerrainData defaults, silently
            // discarding per-prototype Profile edits (and the original ->
            // clone map). Refresh the Profile-backed runtime collection
            // instead; terrain-driven generation remains available for an
            // empty Profile, which is the layout-bootstrap path.
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
                TreePrototype[] terrainTreePrototypes = boundTerrain.terrainData.treePrototypes;
                terrainPrototypeCount = terrainTreePrototypes == null ? 0 : terrainTreePrototypes.Length;
            }

            bool tracking = false;
            try
            {
                // Profile-backed Play Mode managers receive a transient copy
                // of the profile prototype list. Observe the utility pass so
                // any newly-created terrain prototypes can be owned and
                // released by the manager; editor paths keep the
                // utility's original asset semantics.
                tracking = BeginRuntimeTerrainPrototypeGeneration<GPUInstancerTreePrototype>(
                    forceNew, terrainPrototypeCount);

                base.GeneratePrototypes(forceNew);

                if (terrainSettings != null && boundTerrain != null && boundTerrain.terrainData != null)
                {
                    GPUInstancerUtility.SetTreeInstancePrototypes(diagnosticContext, prototypeList, boundTerrain.terrainData.treePrototypes, terrainSettings, forceNew);
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
                    EndRuntimeTerrainPrototypeGeneration<GPUInstancerTreePrototype>();
            }
        }

#if UNITY_EDITOR
        public override void CheckPrototypeChanges()
        {
            base.CheckPrototypeChanges();

            if (!Application.isPlaying && terrainSettings != null && terrain != null && terrain.terrainData != null)
            {
                TreePrototype[] terrainPrototypes = terrain.terrainData.treePrototypes;
                if (terrainPrototypes == null)
                    return;
                if (prototypeList == null)
                    prototypeList = new List<GPUInstancerPrototype>();
                if (prototypeList.Count != terrainPrototypes.Length)
                {
                    GeneratePrototypes();
                }

                for (int index = 0; index < prototypeList.Count; index++)
                {
                    GPUInstancerTreePrototype prototype = prototypeList[index] as GPUInstancerTreePrototype;
                    if (prototype == null)
                        continue;
                    prototype.prototypeIndex = index;
                }
            }
        }
#endif

        public override void InitializeRuntimeDataAndBuffers(bool forceNew = true)
        {
            base.InitializeRuntimeDataAndBuffers(forceNew);

            if (!forceNew && isInitialized)
                return;

            // Terrain managers can be created before any
            // scene proxy has necessarily bound a Terrain.  Keep the manager
            // dormant in that state: running the replacement coroutine against
            // an empty terrain list would mark isInitialized=true and prevent
            // the first proxy bind from ever building runtime data.  A Terrain
            // component can also exist briefly without TerrainData while a
            // scene/prefab is being imported, so defer until both are present.
            if (terrainSettings == null || terrain == null || terrain.terrainData == null)
            {
                if (terrain != null && terrain.terrainData == null)
                    RequestRuntimeInitializationRetry();
                return;
            }

            // A missing instantiation shader is a recoverable setup failure,
            // not a reason to mark the channel initialized and leave a
            // coroutine that will throw on its first tick.  Keep the manager
            // dormant so a later resource reload/rebind can retry cleanly.
            if (_treeInstantiationComputeShader == null)
            {
                Debug.LogError("GPU Instancer could not load the tree instantiation compute shader. Tree initialization was deferred.", diagnosticContext);
                return;
            }

            // A profile can legitimately describe a Terrain with no tree
            // prototypes. There is no native renderer to suppress and no
            // replacement pass to schedule in that case.
            if (prototypeList == null || prototypeList.Count == 0)
            {
                isInitialized = true;
                return;
            }

            if (prototypeList != null && prototypeList.Count > 0)
            {
                GPUInstancerUtility.AddTreeInstanceRuntimeDataToList(runtimeDataList, prototypeList, terrainSettings, this);
            }

            GPUInstancerLoopScheduler.StartCoroutine(this, ReplaceUnityTrees());

            isInitialized = true;
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
                int treePrototypeIndex = prototypeList.IndexOf(prototype);
                if (treePrototypeIndex < 0)
                    return;

                TreePrototype[] treePrototypes = terrain.terrainData.treePrototypes;
                if (treePrototypes == null || treePrototypeIndex >= treePrototypes.Length)
                {
                    base.DeletePrototype(prototype, removeSO);
                    return;
                }
                List<TreePrototype> newTreePrototypes = new List<TreePrototype>(treePrototypes);
                List<TreeInstance> newTreeInstanceList = new List<TreeInstance>();
                TreeInstance treeInstance;

                TreeInstance[] treeInstances = terrain.terrainData.treeInstances;
                if (treeInstances == null)
                    treeInstances = new TreeInstance[0];
                for (int i = 0; i < treeInstances.Length; i++)
                {
                    treeInstance = treeInstances[i];
                    if (treeInstance.prototypeIndex < treePrototypeIndex)
                    {
                        newTreeInstanceList.Add(treeInstance);
                    }
                    else if (treeInstance.prototypeIndex > treePrototypeIndex)
                    {
                        treeInstance.prototypeIndex = treeInstance.prototypeIndex - 1;
                        newTreeInstanceList.Add(treeInstance);
                    }
                }

                if (newTreePrototypes.Count > treePrototypeIndex)
                    newTreePrototypes.RemoveAt(treePrototypeIndex);

                terrain.terrainData.treeInstances = newTreeInstanceList.ToArray();
                terrain.terrainData.treePrototypes = newTreePrototypes.ToArray();

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

        #endregion Override Methods

        public IEnumerator ReplaceUnityTrees()
        {
            _isCoroutineActive = true;
            ComputeBuffer treeDataBuffer = null;
            ComputeBuffer treeScalesBuffer = null;
            ComputeBuffer counterBuffer = null;
            List<Terrain> suppressedTerrains = new List<Terrain>();
            bool completed = false;
            int instanceTotal = 0;
            try
            {
                if (_treeInstantiationComputeShader == null)
                {
                    Debug.LogError("GPU Instancer could not load the tree instantiation compute shader. Tree replacement was canceled.", diagnosticContext);
                    yield break;
                }

                if (prototypeList != null && prototypeList.Count > 0)
                {
                    Vector4[] treeScales = new Vector4[prototypeList.Count];
                    int count = 0;
                    foreach (GPUInstancerPrototype sourcePrototype in prototypeList)
                    {
                        GPUInstancerTreePrototype tp = sourcePrototype as GPUInstancerTreePrototype;
                        if (tp == null || tp.prefabObject == null)
                        {
                            // Keep the array index aligned with prototype
                            // order. A malformed entry is skipped by the
                            // runtime-data pass below instead of aborting the
                            // whole channel with a NullReferenceException.
                            treeScales[count] = Vector4.one;
                            count++;
                            continue;
                        }
                        treeScales[count] = tp.isApplyPrefabScale
                            ? new Vector4(tp.prefabObject.transform.localScale.x,
                                tp.prefabObject.transform.localScale.y,
                                tp.prefabObject.transform.localScale.z, 0f)
                            : Vector4.one;
                        count++;
                    }
                    int[] instanceCounts = new int[prototypeList.Count];

                    List<Vector4> treeDataList = new List<Vector4>(); // prototypeIndex - positionx3 - rotation - scalex2

                    instanceTotal = 0;
                    if (_terrains != null)
                    {
                        foreach (Terrain terrain in _terrains)
                        {
                            if (terrain == null || terrain.terrainData == null)
                                continue;
                            TreePrototype[] terrainPrototypes = terrain.terrainData.treePrototypes;
                            if (terrainPrototypes == null)
                                continue;
                            if (terrainPrototypes.Length > prototypeList.Count)
                            {
                                Debug.LogError("Additional Terrain has more Tree prototypes than defined prototypes on the Tree Manager. Tree Manager requires every Terrain to have the same Tree prototypes defined.", terrain);
                                continue;
                            }

                            Vector3 terrainSize = terrain.terrainData.size;
                            Vector3 terrainPosition = terrain.GetPosition();
                            TreeInstance[] treeInstances = terrain.terrainData.treeInstances;
                            bool terrainHasInstances = false;
                            if (treeInstances == null)
                                treeInstances = new TreeInstance[0];
                            foreach (TreeInstance treeInstance in treeInstances)
                            {
                                if (treeInstance.prototypeIndex < 0 ||
                                    treeInstance.prototypeIndex >= instanceCounts.Length)
                                    continue;

                                terrainHasInstances = true;

                                treeDataList.Add(new Vector4(
                                    treeInstance.prototypeIndex,
                                    treeInstance.position.x * terrainSize.x + terrainPosition.x,
                                    treeInstance.position.y * terrainSize.y + terrainPosition.y,
                                    treeInstance.position.z * terrainSize.z + terrainPosition.z
                                    ));

                                treeDataList.Add(new Vector4(
                                    treeInstance.rotation,
                                    treeInstance.widthScale,
                                    treeInstance.heightScale,
                                    0
                                    ));
                                instanceCounts[treeInstance.prototypeIndex]++;
                                instanceTotal++;
                            }

                            // Suppress Unity's native tree renderer only for a
                            // Terrain that actually contributes instances. If
                            // a multi-Terrain channel has an empty Terrain,
                            // leaving its native distance untouched avoids
                            // hiding perfectly valid Unity trees there.
                            if (terrainHasInstances)
                            {
                                terrain.treeDistance = 0f; // will not persist at runtime.
                                suppressedTerrains.Add(terrain);
                            }
                            else
                            {
                                // The previous replacement may have owned this
                                // terrain even though the current TerrainData
                                // no longer contains instances. Restore Unity's
                                // renderer immediately so an empty channel does
                                // not leave native trees hidden at distance 0.
                                RestoreTerrainSettingsAfterUnbind(terrain);
                            }
                        }
                    }

                    if (instanceTotal > 0 && treeDataList.Count > 0)
                    {
                        if (initializeWithCoroutine && !isInitialized)
                            yield return null;

                        treeDataBuffer = new ComputeBuffer(treeDataList.Count, GPUInstancerConstants.STRIDE_SIZE_FLOAT4);
#if UNITY_2019_1_OR_NEWER
                        treeDataBuffer.SetData(treeDataList);
#else
                        treeDataBuffer.SetData(treeDataList.ToArray());
#endif
                        treeScalesBuffer = new ComputeBuffer(treeScales.Length, GPUInstancerConstants.STRIDE_SIZE_FLOAT4);
                        treeScalesBuffer.SetData(treeScales);
                        counterBuffer = new ComputeBuffer(1, GPUInstancerConstants.STRIDE_SIZE_INT);
                        uint[] emptyCounterData = new uint[1];

                        treeDataList = null;
                        treeScales = null;

                        GPUInstancerRuntimeData runtimeData;
                        if (runtimeDataList != null)
                        {
                            for (int i = 0; i < runtimeDataList.Count && i < instanceCounts.Length; i++)
                            {
                                runtimeData = runtimeDataList[i];
                                if (runtimeData == null)
                                    continue;
                                GPUInstancerTreePrototype runtimePrototype = runtimeData.prototype as GPUInstancerTreePrototype;
                                if (runtimePrototype == null)
                                    continue;
                                int instanceCount = instanceCounts[i];
                                runtimeData.bufferSize = instanceCount;
                                runtimeData.instanceCount = instanceCount;
                                if (instanceCount == 0)
                                {
                                    GPUInstancerUtility.ReleaseInstanceBuffers(runtimeData);
                                    continue;
                                }

                                counterBuffer.SetData(emptyCounterData);
                                if (runtimeData.transformationMatrixVisibilityBuffer != null)
                                    runtimeData.transformationMatrixVisibilityBuffer.Release();
                                runtimeData.transformationMatrixVisibilityBuffer = new ComputeBuffer(instanceCount, GPUInstancerConstants.STRIDE_SIZE_MATRIX4X4);

                                _treeInstantiationComputeShader.SetBuffer(0,
                                    GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, runtimeData.transformationMatrixVisibilityBuffer);
                                _treeInstantiationComputeShader.SetBuffer(0,
                                    GPUInstancerConstants.TreeKernelProperties.TREE_DATA, treeDataBuffer);
                                _treeInstantiationComputeShader.SetBuffer(0,
                                    GPUInstancerConstants.TreeKernelProperties.TREE_SCALES, treeScalesBuffer);
                                _treeInstantiationComputeShader.SetBuffer(0,
                                    GPUInstancerConstants.GrassKernelProperties.COUNTER_BUFFER, counterBuffer);
                                _treeInstantiationComputeShader.SetInt(
                                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, instanceTotal);
                                //_treeInstantiationComputeShader.SetVector(
                                //    GPUInstancerConstants.GrassKernelProperties.TERRAIN_SIZE_DATA, terrain.terrainData.size);
                                //_treeInstantiationComputeShader.SetVector(
                                //    GPUInstancerConstants.TreeKernelProperties.TERRAIN_POSITION, terrain.GetPosition());
                                _treeInstantiationComputeShader.SetBool(
                                    GPUInstancerConstants.TreeKernelProperties.IS_APPLY_ROTATION, runtimePrototype.isApplyRotation);
                                _treeInstantiationComputeShader.SetBool(
                                    GPUInstancerConstants.TreeKernelProperties.IS_APPLY_TERRAIN_HEIGHT, runtimePrototype.isApplyTerrainHeight);
                                _treeInstantiationComputeShader.SetInt(
                                    GPUInstancerConstants.TreeKernelProperties.PROTOTYPE_INDEX, i);

                                GPUInstancerUtility.DispatchCompute(_treeInstantiationComputeShader, 0,
                                    Mathf.CeilToInt(instanceTotal / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);

                                GPUInstancerUtility.InitializeGPUBuffer(runtimeData);

                                if (initializeWithCoroutine && !isInitialized)
                                    yield return null;
                            }
                        }
                    }
                    else if (runtimeDataList != null)
                    {
                        GPUInstancerUtility.ReleaseInstanceBuffers(runtimeDataList);
                    }
                }

                isInitial = true;
                if (!isInitialized)
                    GPUInstancerUtility.TriggerEvent(GPUInstancerEventType.TreeInitializationFinished);
                completed = true;
            }
            finally
            {
                if (treeDataBuffer != null)
                    treeDataBuffer.Release();
                if (treeScalesBuffer != null)
                    treeScalesBuffer.Release();
                if (counterBuffer != null)
                    counterBuffer.Release();

                // If the scheduler disposes this iterator (or a setup call
                // throws part-way through), Unity's native renderer must be
                // restored. On a successful pass, keep distance zero only for
                // Terrains that had at least one instanced tree; empty
                // Terrains were never suppressed above.
                if (!completed || instanceTotal == 0)
                {
                    // Cancellation or an empty replacement leaves no GPUI draw
                    // owning any terrain; restore every bound Terrain, not just
                    // the ones encountered with instances in this pass.
                    RestoreAllBoundTerrainSettings();
                }
                else if (_terrains != null)
                {
                    // A Terrain can transition from populated to empty while
                    // another bound Terrain still has trees. Restore only the
                    // non-contributing entries; populated ones remain hidden
                    // from Unity's native renderer for GPUI drawing.
                    for (int i = 0; i < _terrains.Count; i++)
                    {
                        Terrain boundTerrain = _terrains[i];
                        if (boundTerrain != null &&
                            (suppressedTerrains == null || !suppressedTerrains.Contains(boundTerrain)))
                            RestoreTerrainSettingsAfterUnbind(boundTerrain);
                    }
                }
                _isCoroutineActive = false;
            }
        }

        private void RestoreAllBoundTerrainSettings()
        {
            if (_terrains == null)
                return;
            for (int i = 0; i < _terrains.Count; i++)
                RestoreTerrainSettingsAfterUnbind(_terrains[i]);
        }
    }
}
