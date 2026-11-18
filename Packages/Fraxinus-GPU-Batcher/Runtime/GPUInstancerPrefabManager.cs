using System;
using System.Collections.Generic;
using UnityEngine;
using System.Threading;
using Unity.Collections;
using System.Linq;
using UnityEngine.Jobs;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fraxinus.GPUBatcher
{
    public class GPUInstancerPrefabManager : GPUInstancerManager
    {
        public List<RegisteredPrefabsData> registeredPrefabs = new List<RegisteredPrefabsData>();
        public List<GameObject> prefabList;
        public bool enableMROnManagerDisable = true;
        public bool enableMROnRemoveInstance = true;

        protected List<GPUInstancerModificationCollider> _modificationColliders;
        protected Dictionary<GPUInstancerPrototype, List<GPUInstancerPrefab>> _registeredPrefabsRuntimeData;
        protected List<IPrefabVariationData> _variationDataList;

        /// <summary>Applies the prefab channel recipe to this runtime engine.</summary>
        protected override void ApplyProfile(GPUInstancerProfile value, bool copyCollections)
        {
            base.ApplyProfile(value, copyCollections);
            GPUInstancerPrefabProfile prefabProfile = value as GPUInstancerPrefabProfile;
            if (prefabProfile == null)
                return;

            // Copy serialized collections into the transient manager.  In
            // particular, InitializeRuntimeDataAndBuffers consumes and clears
            // registeredPrefabs; sharing that list would dirty the project
            // settings asset during Play Mode.
            if (copyCollections || registeredPrefabs == null)
            {
                registeredPrefabs = new List<RegisteredPrefabsData>();
                if (prefabProfile.registeredPrefabs != null)
                {
                    foreach (RegisteredPrefabsData source in prefabProfile.registeredPrefabs)
                    {
                        if (source == null)
                            continue;
                        GPUInstancerPrefabPrototype runtimePrototype =
                            ResolveRuntimePrototype(source.prefabPrototype) as GPUInstancerPrefabPrototype;
                        if (runtimePrototype == null && source.prefabPrototype != null)
                            continue;
                        RegisteredPrefabsData clone = new RegisteredPrefabsData(runtimePrototype);
                        if (source.registeredPrefabs != null)
                            clone.registeredPrefabs.AddRange(source.registeredPrefabs);
                        registeredPrefabs.Add(clone);
                    }
                }
            }
            if (copyCollections || prefabList == null)
                prefabList = prefabProfile.prefabList == null
                    ? new List<GameObject>()
                    : new List<GameObject>(prefabProfile.prefabList);
            enableMROnManagerDisable = prefabProfile.enableMROnManagerDisable;
            enableMROnRemoveInstance = prefabProfile.enableMROnRemoveInstance;
        }

        /// <summary>
        /// Drops Prefab-only runtime indexes before the base manager replaces
        /// profile prototypes. Those indexes are keyed by the transient clone
        /// identities and therefore cannot survive a profile/collection
        /// rebind.
        /// </summary>
        protected override void OnProfileConfigurationReleased()
        {
            if (_variationDataList != null)
            {
                for (int i = 0; i < _variationDataList.Count; i++)
                {
                    IPrefabVariationData variation = _variationDataList[i];
                    if (variation != null)
                        variation.ReleaseBuffer();
                }
                _variationDataList.Clear();
            }

            if (_registeredPrefabsRuntimeData != null)
                _registeredPrefabsRuntimeData.Clear();
            _registeredPrefabsRuntimeData = null;

            // The base hook owns the transient prototype clones created when
            // this manager is backed by a Profile.  Keep the call here even
            // though ApplyProfile also has a defensive release path: a
            // profile can be detached (SetProfile(null)) without a subsequent
            // ApplyProfile invocation.
            base.OnProfileConfigurationReleased();
        }

        #region Explicit lifecycle

        public override void Activate()
        {
            if (Application.isPlaying)
                RegisterPrefabsInScene();

            base.Activate();
        }

        public override void Initialize()
        {
            base.Initialize();

            if (prefabList == null)
                prefabList = new List<GameObject>();
        }

        protected override void DeactivateCore()
        {
            // Runtime handlers cache prototype-to-manager aliases for fast
            // instance lookup. Retire those aliases before teardown so no
            // static dictionary can retain or rediscover this managed engine
            // while it is leaving the Hub (including direct Dispose calls).
            GPUInstancerPrefabRuntimeHandler.RemoveManager(this);
            base.DeactivateCore();
        }

        public override void ResetConfiguration()
        {
            base.ResetConfiguration();

            RegisterPrefabsInScene();
        }

        public override void Tick()
        {
            base.Tick();

            if (runtimeDataList != null && Application.isPlaying)
            {
                foreach (GPUInstancerRuntimeData runtimeData in runtimeDataList)
                {
                    if (runtimeData.prototype.autoUpdateTransformData && runtimeData.instanceCount > 0 && runtimeData.instanceDataNativeArray.IsCreated && runtimeData.instanceTransformAccessArray.isCreated)
                    {
                        runtimeData.dependentJob.Complete();
                        runtimeData.dependentJob = new AutoUpdateTransformsJob()
                        {
                            instanceDataNativeArray = runtimeData.instanceDataNativeArray
                        }.Schedule(runtimeData.instanceTransformAccessArray);
                        runtimeData.transformDataModified = true;
                    }
                }
            }
        }

        public override void PrepareFrame()
        {
            if (runtimeDataList != null && Application.isPlaying)
            {
                foreach (GPUInstancerRuntimeData runtimeData in runtimeDataList)
                {
                    if (runtimeData.transformDataModified)
                    {
                        runtimeData.dependentJob.Complete();
                        if (runtimeData.instanceDataNativeArray.IsCreated && runtimeData.transformationMatrixVisibilityBuffer != null)
                            runtimeData.transformationMatrixVisibilityBuffer.SetData(runtimeData.instanceDataNativeArray);
                        runtimeData.transformDataModified = false;
                    }
                }
            }
            base.PrepareFrame();
        }
        #endregion Explicit lifecycle

        public override void ClearInstancingData()
        {
            base.ClearInstancingData();

            if (Application.isPlaying && _registeredPrefabsRuntimeData != null && enableMROnManagerDisable)
            {
                foreach (GPUInstancerPrototype key in _registeredPrefabsRuntimeData.Keys)
                {
                    GPUInstancerPrefabPrototype p = key as GPUInstancerPrefabPrototype;
                    if (p == null || !_registeredPrefabsRuntimeData.TryGetValue(key, out List<GPUInstancerPrefab> instances) || instances == null)
                        continue;
                    if (p.meshRenderersDisabled)
                        continue;
                    foreach (GPUInstancerPrefab prefabInstance in instances)
                    {
                        if (!prefabInstance)
                            continue;
#if UNITY_EDITOR && UNITY_2017_2_OR_NEWER
                        if (playModeState != PlayModeStateChange.EnteredEditMode && playModeState != PlayModeStateChange.ExitingPlayMode)
#endif
                            SetRenderersEnabled(prefabInstance, true);
                    }
                }
            }

            if (_variationDataList != null)
            {
                foreach (IPrefabVariationData pvd in _variationDataList)
                    pvd.ReleaseBuffer();
            }
        }

        public override void GeneratePrototypes(bool forceNew = false)
        {
            // A Profile is the persistent source of prefab prototypes. During
            // Play Mode its list has already been copied to transient clones;
            // asking the editor-oriented utility to regenerate from prefab
            // components would reinsert those persistent prototype assets (and
            // could mutate them). A forced refresh therefore rebuilds the
            // manager's clone graph from the Profile and leaves runtime
            // dynamic definitions to DefineGameObjectAsPrefabPrototypeAtRuntime.
            if (profile != null && Application.isPlaying)
            {
                if (forceNew)
                    RefreshProfileCollections();
                else
                    base.GeneratePrototypes(false);
                return;
            }

            // Keep the editor behaviour: the utility needs
            // the existing list in order to remove old generated assets before
            // creating the replacement set, so do not pass forceNew to the
            // base list-reset implementation here.
            base.GeneratePrototypes(false);

            GPUInstancerUtility.SetPrefabInstancePrototypes(diagnosticContext, prototypeList, prefabList, forceNew);
#if UNITY_EDITOR
            SyncProfilePrototypeList();
#endif
        }

#if UNITY_EDITOR
        public override void CheckPrototypeChanges()
        {
            base.CheckPrototypeChanges();

            if (prefabList == null)
                prefabList = new List<GameObject>();

            prefabList.RemoveAll(p => p == null);
            prefabList.RemoveAll(p => p.GetComponent<GPUInstancerPrefab>() == null);
            prototypeList.RemoveAll(p => p == null);
            prototypeList.RemoveAll(p => !prefabList.Contains(p.prefabObject));

            if (prefabList.Count != prototypeList.Count)
                GeneratePrototypes();

            registeredPrefabs.RemoveAll(rpd => rpd == null || !prototypeList.Contains(rpd.prefabPrototype));
            foreach (GPUInstancerPrototype key in prototypeList)
            {
                GPUInstancerPrefabPrototype prototype = key as GPUInstancerPrefabPrototype;
                if (prototype == null)
                    continue;
                if (!registeredPrefabs.Exists(rpd => rpd.prefabPrototype == prototype))
                    registeredPrefabs.Add(new RegisteredPrefabsData(prototype));
            }
        }
#endif
        public override void InitializeRuntimeDataAndBuffers(bool forceNew = true)
        {
            base.InitializeRuntimeDataAndBuffers(forceNew);

            if (!forceNew && isInitialized)
                return;

            if (_registeredPrefabsRuntimeData == null)
                _registeredPrefabsRuntimeData = new Dictionary<GPUInstancerPrototype, List<GPUInstancerPrefab>>();

#if UNITY_EDITOR
            if (Application.isPlaying)
            {
#endif
                if (registeredPrefabs != null && registeredPrefabs.Count > 0)
                {
                    foreach (RegisteredPrefabsData rpd in registeredPrefabs)
                    {
                        if (rpd == null || rpd.prefabPrototype == null)
                            continue;
                        GPUInstancerPrefabPrototype runtimePrototype =
                            ResolveRuntimePrototype(rpd.prefabPrototype) as GPUInstancerPrefabPrototype;
                        if (runtimePrototype == null)
                            continue;
                        if (!_registeredPrefabsRuntimeData.ContainsKey(runtimePrototype))
                            _registeredPrefabsRuntimeData.Add(runtimePrototype, rpd.registeredPrefabs ?? new List<GPUInstancerPrefab>());
                        else
                        {
                            if (rpd.registeredPrefabs != null)
                                _registeredPrefabsRuntimeData[runtimePrototype].AddRange(rpd.registeredPrefabs);
                        }
                    }
                    registeredPrefabs.Clear();
                }

                if (_registeredPrefabsRuntimeData.Count != prototypeList.Count)
                {
                    foreach (GPUInstancerPrototype p in prototypeList)
                    {
                        if (p != null && p is GPUInstancerPrefabPrototype && !_registeredPrefabsRuntimeData.ContainsKey(p))
                            _registeredPrefabsRuntimeData.Add(p, new List<GPUInstancerPrefab>());
                    }
                }
#if UNITY_EDITOR
            }
#endif

            InitializeRuntimeDataRegisteredPrefabs();
            GPUInstancerUtility.InitializeGPUBuffers(runtimeDataList);
            isInitial = true;
            isInitialized = true;
        }

        public override void DeletePrototype(GPUInstancerPrototype prototype, bool removeSO = true)
        {
            prototype = ResolveRuntimePrototype(prototype);
            if (prototype == null)
                return;
            base.DeletePrototype(prototype, removeSO);

            if (prefabList != null)
                prefabList.Remove(prototype.prefabObject);
            if (removeSO)
            {
#if UNITY_2018_3_OR_NEWER && UNITY_EDITOR
                GPUInstancerUtility.RemoveComponentFromPrefab<GPUInstancerPrefab>(prototype.prefabObject);
                GPUInstancerUtility.RemoveComponentFromPrefab<GPUInstancerPrefabRuntimeHandler>(prototype.prefabObject);
#else
                UnityEngine.Object.DestroyImmediate(prototype.prefabObject.GetComponent<GPUInstancerPrefab>(), true);
                if (prototype.prefabObject.GetComponent<GPUInstancerPrefabRuntimeHandler>() != null)
                    UnityEngine.Object.DestroyImmediate(prototype.prefabObject.GetComponent<GPUInstancerPrefabRuntimeHandler>(), true);
#endif
#if UNITY_EDITOR
                EditorUtility.SetDirty(prototype.prefabObject);
                string prototypeAssetPath = AssetDatabase.GetAssetPath(prototype);
                if (!GPUInstancerConstants.IsPackageAssetPath(prototypeAssetPath) && !string.IsNullOrEmpty(prototypeAssetPath))
                    AssetDatabase.DeleteAsset(prototypeAssetPath);
                else if (GPUInstancerConstants.IsPackageAssetPath(prototypeAssetPath))
                    Debug.LogWarning("Skipping deletion of package-owned prefab prototype: " + prototypeAssetPath);
#endif
            }
            GeneratePrototypes(false);
        }

        public virtual void InitializeRuntimeDataRegisteredPrefabs(int additionalBufferSize = 0)
        {
            if (runtimeDataList == null)
                runtimeDataList = new List<GPUInstancerRuntimeData>();
            if (runtimeDataDictionary == null)
                runtimeDataDictionary = new Dictionary<GPUInstancerPrototype, GPUInstancerRuntimeData>();

            if (prototypeList == null)
                return;

            foreach (GPUInstancerPrototype key in prototypeList)
            {
                GPUInstancerPrefabPrototype p = key as GPUInstancerPrefabPrototype;
                if (p == null)
                    continue;
#if UNITY_EDITOR
                if (!Application.isPlaying && !p.isTransformsSerialized && !p.meshRenderersDisabled)
                    continue;
#endif
                InitializeRuntimeDataForPrefabPrototype(p, additionalBufferSize);
            }
        }

        public virtual GPUInstancerRuntimeData InitializeRuntimeDataForPrefabPrototype(GPUInstancerPrefabPrototype p, int additionalBufferSize = 0)
        {
            p = ResolveRuntimePrototype(p) as GPUInstancerPrefabPrototype;
            if (p == null)
                return null;

            // SRP requires the original material for the shadow pass, but
            // that is a runtime rendering decision. Do not write it back to
            // a Profile-backed prototype while initializing buffers.
            bool useOriginalShaderForShadow = true;

            GPUInstancerRuntimeData runtimeData = GetRuntimeData(p);
            if (runtimeData == null)
            {
                runtimeData = new GPUInstancerRuntimeData(p, this);
                if (!runtimeData.CreateRenderersFromGameObject(p))
                    return null;
                runtimeDataList.Add(runtimeData);
                runtimeDataDictionary.Add(p, runtimeData);
                if (p.isShadowCasting)
                {
                    runtimeData.hasShadowCasterBuffer = true;

                    if (!useOriginalShaderForShadow)
                    {
                        runtimeData.shadowCasterMaterial = new Material(Shader.Find(GPUInstancerConstants.SHADER_GPUI_SHADOWS_ONLY));
                    }
                }

                GPUInstancerUtility.AddBillboardToRuntimeData(runtimeData);

                if (p.treeType == GPUInstancerTreeType.SpeedTree || p.treeType == GPUInstancerTreeType.SpeedTree8 || p.treeType == GPUInstancerTreeType.TreeCreatorTree)
                    AddTreeProxy(p, runtimeData);
            }
            runtimeData.dependentJob.Complete();
            int instanceCount = 0;
            List<GPUInstancerPrefab> registeredPrefabsList = null;
            if (p.isTransformsSerialized)
            {
                string matrixStr;
                System.IO.StringReader strReader = new System.IO.StringReader(p.serializedTransformData.text);
                List<Matrix4x4> matrixData = new List<Matrix4x4>();
                while (true)
                {
                    matrixStr = strReader.ReadLine();
                    if (!string.IsNullOrEmpty(matrixStr))
                    {
                        matrixData.Add(GPUInstancerUtility.Matrix4x4FromString(matrixStr));
                    }
                    else
                        break;
                }
                if (runtimeData.instanceDataNativeArray.IsCreated)
                    runtimeData.instanceDataNativeArray.Dispose();
                runtimeData.instanceDataNativeArray = new NativeArray<Matrix4x4>(matrixData.ToArray(), Allocator.Persistent);
                runtimeData.bufferSize = matrixData.Count + (p.enableRuntimeModifications && p.addRemoveInstancesAtRuntime ? p.extraBufferSize : 0) + additionalBufferSize;
                instanceCount = matrixData.Count;
            }
#if UNITY_EDITOR
            else if (!Application.isPlaying && p.meshRenderersDisabled)
            {
                List<GPUInstancerPrefab> prefabInstances = registeredPrefabs.Find(rpd => rpd.prefabPrototype == p).registeredPrefabs;
                runtimeData.ReleaseBuffers();
                runtimeData.bufferSize = prefabInstances.Count;
                runtimeData.instanceDataNativeArray = new NativeArray<Matrix4x4>(runtimeData.bufferSize, Allocator.Persistent);
                instanceCount = runtimeData.bufferSize;
                for (int i = 0; i < instanceCount; i++)
                {
                    runtimeData.instanceDataNativeArray[i] = prefabInstances[i].GetInstanceTransform().localToWorldMatrix;
                }
            }
#endif
            else if (_registeredPrefabsRuntimeData.TryGetValue(p, out registeredPrefabsList))
            {
                for (int i = registeredPrefabsList.Count - 1; i >= 0; i--)
                {
                    GPUInstancerPrefab prefabInstance = registeredPrefabsList[i];
                    if (prefabInstance == null || prefabInstance.GetInstanceTransform() == null)
                        registeredPrefabsList.RemoveAt(i);
                }

                runtimeData.ReleaseBuffers();
                runtimeData.bufferSize = registeredPrefabsList.Count + (p.enableRuntimeModifications && p.addRemoveInstancesAtRuntime ? p.extraBufferSize : 0) + additionalBufferSize;
                if (runtimeData.bufferSize > 0)
                {
                    runtimeData.instanceDataNativeArray = new NativeArray<Matrix4x4>(runtimeData.bufferSize, Allocator.Persistent);

                    Matrix4x4 instanceData;
                    bool hasModificationColliders = p.enableRuntimeModifications && _modificationColliders != null && _modificationColliders.Count > 0;
                    bool hasAutoTransformUpdate = p.enableRuntimeModifications && p.autoUpdateTransformData;
                    if (hasAutoTransformUpdate && runtimeData.bufferSize > 0)
                        TransformAccessArray.Allocate(runtimeData.bufferSize, -1, out runtimeData.instanceTransformAccessArray);

                    foreach (GPUInstancerPrefab prefabInstance in registeredPrefabsList)
                    {
                        if (!prefabInstance)
                            continue;

                        Transform instanceTransform = prefabInstance.GetInstanceTransform();
                        instanceData = instanceTransform.localToWorldMatrix;
                        prefabInstance.state = PrefabInstancingState.Instanced;

                        bool disableRenderers = true;

                        if (hasModificationColliders)
                        {
                            bool isInsideCollider = false;
                            foreach (GPUInstancerModificationCollider mc in _modificationColliders)
                            {
                                if (mc.IsInsideCollider(prefabInstance))
                                {
                                    isInsideCollider = true;
                                    mc.AddEnteredInstance(prefabInstance);
                                    instanceData = GPUInstancerConstants.zeroMatrix;
                                    prefabInstance.state = PrefabInstancingState.Disabled;
                                    disableRenderers = false;
                                    break;
                                }
                            }
                            if (!isInsideCollider)
                            {
                                if (p.startWithRigidBody && prefabInstance.GetComponent<Rigidbody>() != null)
                                {
                                    isInsideCollider = true;
                                    _modificationColliders[0].AddEnteredInstance(prefabInstance);
                                    instanceData = GPUInstancerConstants.zeroMatrix;
                                    prefabInstance.state = PrefabInstancingState.Disabled;
                                    disableRenderers = false;
                                }
                            }
                        }

                        if (disableRenderers && !p.meshRenderersDisabled)
                            SetRenderersEnabled(prefabInstance, false);

                        if (hasAutoTransformUpdate)
                            runtimeData.instanceTransformAccessArray.Add(instanceTransform);
                        runtimeData.instanceDataNativeArray[instanceCount] = instanceData;
                        instanceCount++;
                        prefabInstance.gpuInstancerID = instanceCount;
                    }
                }
            }

            // set instanceCount
            runtimeData.instanceCount = instanceCount;

            // variations
            if (_variationDataList != null)
            {
                foreach (IPrefabVariationData pvd in _variationDataList)
                {
                    if (pvd.GetPrototype() == p)
                    {
                        pvd.InitializeBufferAndArray(runtimeData.bufferSize);
                        if (registeredPrefabsList != null)
                        {
                            foreach (GPUInstancerPrefab prefabInstance in registeredPrefabsList)
                            {
                                pvd.SetInstanceData(prefabInstance);
                            }
                        }
                        pvd.SetBufferData(0, 0, runtimeData.bufferSize);

                        for (int i = 0; i < runtimeData.instanceLODs.Count; i++)
                        {
                            for (int j = 0; j < runtimeData.instanceLODs[i].renderers.Count; j++)
                            {
                                pvd.SetVariation(runtimeData.instanceLODs[i].renderers[j]);
                            }
                        }
                    }
                }
            }
            return runtimeData;
        }

        public virtual void SetRenderersEnabled(GPUInstancerPrefab prefabInstance, bool enabled)
        {
            if (!prefabInstance || !prefabInstance.prefabPrototype || !prefabInstance.prefabPrototype.prefabObject)
                return;

            GPUInstancerPrefabPrototype runtimePrototype =
                ResolveRuntimePrototype(prefabInstance.prefabPrototype) as GPUInstancerPrefabPrototype;
            if (runtimePrototype == null)
                return;

            MeshRenderer[] meshRenderers = prefabInstance.GetComponentsInChildren<MeshRenderer>(true);
            if (meshRenderers != null && meshRenderers.Length > 0)
                for (int mr = 0; mr < meshRenderers.Length; mr++)
                    if (GPUInstancerUtility.IsInLayer(layerMask, meshRenderers[mr].gameObject.layer))
                        meshRenderers[mr].enabled = enabled;

            BillboardRenderer[] billboardRenderers = prefabInstance.GetComponentsInChildren<BillboardRenderer>(true);
            if (billboardRenderers != null && billboardRenderers.Length > 0)
                for (int mr = 0; mr < billboardRenderers.Length; mr++)
                    if (GPUInstancerUtility.IsInLayer(layerMask, billboardRenderers[mr].gameObject.layer))
                        billboardRenderers[mr].enabled = enabled;

            LODGroup lodGroup = prefabInstance.GetComponent<LODGroup>();
            if (lodGroup != null)
                lodGroup.enabled = enabled;

            Rigidbody rigidbody = prefabInstance.GetComponent<Rigidbody>();

            if (enabled)
            {
                if (rigidbody == null)
                {
                    GPUInstancerPrefabPrototype.RigidbodyData rigidbodyData = runtimePrototype.rigidbodyData;
                        if (rigidbodyData != null && runtimePrototype.hasRigidBody)
                    {
                        rigidbody = prefabInstance.gameObject.AddComponent<Rigidbody>();
                        rigidbody.useGravity = rigidbodyData.useGravity;
                        rigidbody.angularDrag = rigidbodyData.angularDrag;
                        rigidbody.mass = rigidbodyData.mass;
                        rigidbody.constraints = rigidbodyData.constraints;
                        rigidbody.detectCollisions = true;
                        rigidbody.drag = rigidbodyData.drag;
                        rigidbody.isKinematic = rigidbodyData.isKinematic;
                        rigidbody.interpolation = rigidbodyData.interpolation;
                    }
                }
            }
            else if (rigidbody != null && !runtimePrototype.autoUpdateTransformData)
                UnityEngine.Object.Destroy(rigidbody);
        }

        #region API Methods

        public static void ExpandBufferSize(GPUInstancerRuntimeData runtimeData, int newBufferSize)
        {
            if (runtimeData.bufferSize < newBufferSize)
            {
                runtimeData.bufferSize = newBufferSize;
                runtimeData.instanceDataNativeArray = GPUInstancerUtility.ResizeNativeArray(runtimeData.instanceDataNativeArray, newBufferSize, Allocator.Persistent);

                if (runtimeData.instanceTransformAccessArray.isCreated)
                {
                    runtimeData.instanceTransformAccessArray = GPUInstancerUtility.ResizeTransformAccessArray(runtimeData.instanceTransformAccessArray, newBufferSize);
                }
                else if (runtimeData.prototype.autoUpdateTransformData)
                    TransformAccessArray.Allocate(newBufferSize, -1, out runtimeData.instanceTransformAccessArray);
            }
        }

        public virtual void SetInstanceTransform(GPUInstancerRuntimeData runtimeData, int index, Transform transform)
        {
            runtimeData.instanceDataNativeArray[index] = transform ? transform.localToWorldMatrix : GPUInstancerConstants.zeroMatrix;
            if (runtimeData.instanceTransformAccessArray.isCreated)
            {
                if (index == runtimeData.instanceTransformAccessArray.length && transform != null)
                    runtimeData.instanceTransformAccessArray.Add(transform);
                else if (index < runtimeData.instanceTransformAccessArray.length)
                    runtimeData.instanceTransformAccessArray[index] = transform;
            }
        }

        public virtual void DisableIntancingForInstance(GPUInstancerPrefab prefabInstance, bool setRenderersEnabled = true)
        {
            if (!prefabInstance)
                return;

            GPUInstancerPrefabPrototype runtimePrototype =
                ResolveRuntimePrototype(prefabInstance.prefabPrototype) as GPUInstancerPrefabPrototype;
            if (runtimePrototype == null)
                return;
            GPUInstancerRuntimeData runtimeData = GetRuntimeData(runtimePrototype, true);
            if (runtimeData != null && prefabInstance.gpuInstancerID > 0 && prefabInstance.gpuInstancerID <= runtimeData.bufferSize)
            {
                runtimeData.dependentJob.Complete();
                prefabInstance.state = PrefabInstancingState.Disabled;
                SetInstanceTransform(runtimeData, prefabInstance.gpuInstancerID - 1, null);

                runtimeData.transformationMatrixVisibilityBuffer.SetData(runtimeData.instanceDataNativeArray, prefabInstance.gpuInstancerID - 1, prefabInstance.gpuInstancerID - 1, 1);
                if (setRenderersEnabled)
                    SetRenderersEnabled(prefabInstance, true);
                prefabInstance.SetupPrefabInstance(runtimeData);
            }
            else
            {
                Debug.LogWarning("Can not disable instancing for instance with id: " + prefabInstance.gpuInstancerID);
            }
        }

        public virtual void EnableInstancingForInstance(GPUInstancerPrefab prefabInstance, bool setRenderersDisabled = true)
        {
            if (!prefabInstance)
                return;

            GPUInstancerRuntimeData runtimeData = GetRuntimeData(prefabInstance.prefabPrototype, true);
            if (runtimeData != null && prefabInstance.gpuInstancerID > 0 && prefabInstance.gpuInstancerID <= runtimeData.bufferSize)
            {
                runtimeData.dependentJob.Complete();
                prefabInstance.state = PrefabInstancingState.Instanced;
                SetInstanceTransform(runtimeData, prefabInstance.gpuInstancerID - 1, prefabInstance.GetInstanceTransform());

                runtimeData.transformationMatrixVisibilityBuffer.SetData(runtimeData.instanceDataNativeArray, prefabInstance.gpuInstancerID - 1, prefabInstance.gpuInstancerID - 1, 1);
                if (setRenderersDisabled)
                    SetRenderersEnabled(prefabInstance, false);
                prefabInstance.SetupPrefabInstance(runtimeData);
            }
            else
            {
                Debug.LogWarning("Can not enable instancing for instance with id: " + prefabInstance.gpuInstancerID);
            }
        }

        public virtual void UpdateTransformDataForInstance(GPUInstancerPrefab prefabInstance)
        {
            if (!prefabInstance)
                return;

            GPUInstancerRuntimeData runtimeData = GetRuntimeData(prefabInstance.prefabPrototype, true);
            if (runtimeData != null && prefabInstance.gpuInstancerID > 0 && prefabInstance.gpuInstancerID <= runtimeData.bufferSize)
            {
                runtimeData.dependentJob.Complete();
                runtimeData.instanceDataNativeArray[prefabInstance.gpuInstancerID - 1] = prefabInstance.GetInstanceTransform().localToWorldMatrix;

                // automatically set in Update method
                runtimeData.transformDataModified = true;
                //runtimeData.transformationMatrixVisibilityBuffer.SetDataPartial(runtimeData.instanceDataArray, prefabInstance.gpuInstancerID - 1, prefabInstance.gpuInstancerID - 1, 1);
            }
            else
            {
                Debug.LogWarning("Can not update transform for instance with id: " + prefabInstance.gpuInstancerID);
            }
        }

        public virtual void AddPrefabInstance(GPUInstancerPrefab prefabInstance, bool automaticallyIncreaseBufferSize = false)
        {
            //#if UNITY_EDITOR
            //            UnityEngine.Profiling.Profiler.BeginSample("GPUInstancerPrefabManager.AddPrefabInstance");
            //#endif
            if (!prefabInstance || prefabInstance.state == PrefabInstancingState.Instanced)
                return;

            if (runtimeDataList == null)
                return;

            GPUInstancerPrefabPrototype runtimePrototype =
                ResolveRuntimePrototype(prefabInstance.prefabPrototype) as GPUInstancerPrefabPrototype;
            if (runtimePrototype == null)
                return;
            GPUInstancerRuntimeData runtimeData = GetRuntimeData(runtimePrototype, true);
            if (runtimeData != null)
            {
                runtimeData.dependentJob.Complete();
                Transform instanceTransform = prefabInstance.GetInstanceTransform();
                if (runtimeData.bufferSize == runtimeData.instanceCount)
                {
                    if (automaticallyIncreaseBufferSize)
                    {
                        ExpandBufferSize(runtimeData, runtimeData.bufferSize + 1024);
                        SetInstanceTransform(runtimeData, runtimeData.instanceCount, instanceTransform);
                        runtimeData.instanceCount++;
                        prefabInstance.gpuInstancerID = runtimeData.instanceCount;
                        GetOrCreateRuntimeRegisteredPrefabs(runtimePrototype).Add(prefabInstance);
                        if (!runtimePrototype.meshRenderersDisabled)
                            SetRenderersEnabled(prefabInstance, false);
                        prefabInstance.state = PrefabInstancingState.Instanced;
                        GPUInstancerUtility.InitializeGPUBuffer(runtimeData);
                        prefabInstance.SetupPrefabInstance(runtimeData, true);

                        // variations
                        if (_variationDataList != null)
                        {
                            foreach (IPrefabVariationData pvd in _variationDataList)
                            {
                                if (ReferenceEquals(pvd.GetPrototype(), runtimePrototype))
                                {
                                    pvd.SetNewBufferSize(runtimeData.bufferSize);
                                    pvd.SetInstanceData(prefabInstance);
                                    pvd.SetBufferData(prefabInstance.gpuInstancerID - 1, prefabInstance.gpuInstancerID - 1, 1);

                                    for (int i = 0; i < runtimeData.instanceLODs.Count; i++)
                                    {
                                        for (int j = 0; j < runtimeData.instanceLODs[i].renderers.Count; j++)
                                        {
                                            pvd.SetVariation(runtimeData.instanceLODs[i].renderers[j]);
                                        }
                                    }
                                }
                            }
                        }

                        return;
                    }
                    else
                    {
                        Debug.LogWarning("Can not add instance. Buffer is full.");
                        return;
                    }
                }
                prefabInstance.state = PrefabInstancingState.Instanced;
                SetInstanceTransform(runtimeData, runtimeData.instanceCount, instanceTransform);
                runtimeData.instanceCount++;
                prefabInstance.gpuInstancerID = runtimeData.instanceCount;

                runtimeData.transformationMatrixVisibilityBuffer.SetData(runtimeData.instanceDataNativeArray, prefabInstance.gpuInstancerID - 1, prefabInstance.gpuInstancerID - 1, 1);
                if (!runtimePrototype.meshRenderersDisabled)
                    SetRenderersEnabled(prefabInstance, false);

                GetOrCreateRuntimeRegisteredPrefabs(runtimePrototype).Add(prefabInstance);

                // variations
                if (_variationDataList != null)
                {
                    foreach (IPrefabVariationData pvd in _variationDataList)
                    {
                        if (ReferenceEquals(pvd.GetPrototype(), runtimePrototype))
                        {
                            pvd.SetInstanceData(prefabInstance);
                            pvd.SetBufferData(prefabInstance.gpuInstancerID - 1, prefabInstance.gpuInstancerID - 1, 1);
                        }
                    }
                }

                prefabInstance.SetupPrefabInstance(runtimeData, true);
            }
            //#if UNITY_EDITOR
            //            UnityEngine.Profiling.Profiler.EndSample();
            //#endif
        }

        /// <summary>
        /// Adds prefab instances for multiple prototypes
        /// </summary>
        public virtual void AddPrefabInstances(IEnumerable<GPUInstancerPrefab> prefabInstances)
        {
            if (prefabInstances == null || prototypeList == null)
                return;
            List<GPUInstancerPrefab>[] instanceLists = new List<GPUInstancerPrefab>[prototypeList.Count];
            Dictionary<GPUInstancerPrototype, int> indexDict = new Dictionary<GPUInstancerPrototype, int>();
            for (int i = 0; i < instanceLists.Length; i++)
            {
                instanceLists[i] = new List<GPUInstancerPrefab>();
                GPUInstancerPrototype prototype = prototypeList[i];
                if (prototype != null && !indexDict.ContainsKey(prototype))
                    indexDict.Add(prototype, i);
            }

            foreach (GPUInstancerPrefab prefabInstance in prefabInstances)
            {
                if (prefabInstance == null)
                    continue;
                GPUInstancerPrototype runtimePrototype =
                    ResolveRuntimePrototype(prefabInstance.prefabPrototype);
                if (runtimePrototype != null && indexDict.TryGetValue(runtimePrototype, out int index))
                    instanceLists[index].Add(prefabInstance);
            }

            for (int i = 0; i < instanceLists.Length; i++)
            {
                GPUInstancerPrefabPrototype prototype = prototypeList[i] as GPUInstancerPrefabPrototype;
                if (prototype != null)
                    AddPrefabInstances(prototype, instanceLists[i]);
            }
        }

        /// <summary>
        /// Adds prefab instances for single prototype
        /// </summary>
        public virtual void AddPrefabInstances(GPUInstancerPrefabPrototype prototype, IEnumerable<GPUInstancerPrefab> prefabInstances)
        {
            if (prefabInstances == null)
                return;

            prototype = ResolveRuntimePrototype(prototype) as GPUInstancerPrefabPrototype;
            if (prototype == null)
                return;

            GPUInstancerRuntimeData runtimeData = GetRuntimeData(prototype, true);
            if (runtimeData == null)
                return;

            int count = prefabInstances.Count();

            if (count == 0)
                return;

            runtimeData.dependentJob.Complete();
            GPUInstancerPrefab prefabInstance;
            if (runtimeData.instanceCount + count > runtimeData.bufferSize)
            {
                ExpandBufferSize(runtimeData, runtimeData.instanceCount + count);

                for (int i = 0; i < count; i++)
                {
                    prefabInstance = prefabInstances.ElementAt(i);
                    SetInstanceTransform(runtimeData, runtimeData.instanceCount + i, prefabInstance.GetInstanceTransform());
                    prefabInstance.gpuInstancerID = runtimeData.instanceCount + i + 1;
                    if (!prototype.meshRenderersDisabled)
                        SetRenderersEnabled(prefabInstance, false);
                    prefabInstance.state = PrefabInstancingState.Instanced;
                    prefabInstance.SetupPrefabInstance(runtimeData, true);
                }
                GetOrCreateRuntimeRegisteredPrefabs(prototype).AddRange(prefabInstances);
                runtimeData.instanceCount = runtimeData.bufferSize;

                GPUInstancerUtility.InitializeGPUBuffer(runtimeData);
                return;
            }

            for (int i = 0; i < count; i++)
            {
                prefabInstance = prefabInstances.ElementAt(i);
                SetInstanceTransform(runtimeData, runtimeData.instanceCount + i, prefabInstance.GetInstanceTransform());
                prefabInstance.gpuInstancerID = runtimeData.instanceCount + i + 1;
                if (!prototype.meshRenderersDisabled)
                    SetRenderersEnabled(prefabInstance, false);
                prefabInstance.state = PrefabInstancingState.Instanced;
                prefabInstance.SetupPrefabInstance(runtimeData, true);
            }
            GetOrCreateRuntimeRegisteredPrefabs(prototype).AddRange(prefabInstances);
            runtimeData.transformationMatrixVisibilityBuffer.SetData(runtimeData.instanceDataNativeArray);
            runtimeData.instanceCount += count;
        }

        public virtual void UpdateInstanceDataArray(GPUInstancerRuntimeData runtimeData, List<GPUInstancerPrefab> prefabList)
        {
            runtimeData.ReleaseBuffers();
            int instanceCount = prefabList.Count;
            int bufferSize = instanceCount + ((GPUInstancerPrefabPrototype)runtimeData.prototype).extraBufferSize;

            runtimeData.instanceDataNativeArray = new NativeArray<Matrix4x4>(bufferSize, Allocator.Persistent);
            if (runtimeData.prototype.autoUpdateTransformData)
                TransformAccessArray.Allocate(bufferSize, -1, out runtimeData.instanceTransformAccessArray);

            for (int i = 0; i < prefabList.Count;)
            {
                GPUInstancerPrefab gPUInstancerPrefab = prefabList[i];
                SetInstanceTransform(runtimeData, i, gPUInstancerPrefab.GetInstanceTransform());
                gPUInstancerPrefab.gpuInstancerID = ++i;
            }
            runtimeData.instanceCount = instanceCount;
            runtimeData.bufferSize = bufferSize;
            GPUInstancerUtility.InitializeGPUBuffer(runtimeData);
        }

        public virtual void RemovePrefabInstance(GPUInstancerPrefab prefabInstance, bool setRenderersEnabled = true)
        {
            //#if UNITY_EDITOR
            //            UnityEngine.Profiling.Profiler.BeginSample("GPUInstancerPrefabManager.RemovePrefabInstance");
            //#endif
            if (!prefabInstance || prefabInstance.state == PrefabInstancingState.None)
                return;

            GPUInstancerRuntimeData runtimeData = GetRuntimeData(prefabInstance.prefabPrototype);
            if (runtimeData != null)
            {
                if (prefabInstance.gpuInstancerID > runtimeData.bufferSize || prefabInstance.gpuInstancerID <= 0)
                {
                    Debug.LogWarning("Instance can not be removed.");
                    return;
                }

                runtimeData.dependentJob.Complete();
                GPUInstancerPrefabPrototype runtimePrototype =
                    ResolveRuntimePrototype(prefabInstance.prefabPrototype) as GPUInstancerPrefabPrototype;
                if (runtimePrototype == null ||
                    !_registeredPrefabsRuntimeData.TryGetValue(runtimePrototype, out List<GPUInstancerPrefab> prefabInstanceList))
                    return;

                if (prefabInstance.gpuInstancerID == runtimeData.instanceCount)
                {
                    prefabInstance.state = PrefabInstancingState.None;
                    SetInstanceTransform(runtimeData, prefabInstance.gpuInstancerID - 1, null);
                    runtimeData.instanceCount--;
                    prefabInstanceList.RemoveAt(prefabInstance.gpuInstancerID - 1);
                    if (setRenderersEnabled && enableMROnRemoveInstance && !runtimePrototype.meshRenderersDisabled)
                        SetRenderersEnabled(prefabInstance, true);
                }
                else
                {
                    GPUInstancerPrefab lastIndexPrefabInstance = null;
                    for (int i = prefabInstanceList.Count - 1; i >= 0; i--)
                    {
                        GPUInstancerPrefab loopPI = prefabInstanceList[i];
                        if (loopPI == null)
                        {
                            prefabInstanceList.RemoveAt(i);
                            if (i < prefabInstanceList.Count - 1)
                                i++;
                        }
                        else if (loopPI.gpuInstancerID == runtimeData.instanceCount)
                        {
                            lastIndexPrefabInstance = loopPI;
                            break;
                        }
                    }
                    if (!lastIndexPrefabInstance)
                    {
                        prefabInstanceList.RemoveAll(pi => pi == null);
                        Debug.LogWarning("Prefab instance was destroyed before being removed from instance list in GPUI Prefab Manager!");
                        return;
                    }

                    prefabInstance.state = PrefabInstancingState.None;

                    // exchange last index with this one
                    SetInstanceTransform(runtimeData, prefabInstance.gpuInstancerID - 1, lastIndexPrefabInstance.GetInstanceTransform());
                    // set last index data to Matrix4x4.zero
                    SetInstanceTransform(runtimeData, lastIndexPrefabInstance.gpuInstancerID - 1, null);
                    runtimeData.instanceCount--;

                    runtimeData.transformationMatrixVisibilityBuffer.SetData(runtimeData.instanceDataNativeArray, prefabInstance.gpuInstancerID - 1, prefabInstance.gpuInstancerID - 1, 1);

                    prefabInstanceList.RemoveAt(lastIndexPrefabInstance.gpuInstancerID - 1);
                    lastIndexPrefabInstance.gpuInstancerID = prefabInstance.gpuInstancerID;
                    prefabInstanceList[lastIndexPrefabInstance.gpuInstancerID - 1] = lastIndexPrefabInstance;

                    if (setRenderersEnabled && enableMROnRemoveInstance && !runtimePrototype.meshRenderersDisabled)
                        SetRenderersEnabled(prefabInstance, true);
                    //Destroy(prefabInstance);

                    // variations
                    if (_variationDataList != null)
                    {
                        foreach (IPrefabVariationData pvd in _variationDataList)
                        {
                            if (ReferenceEquals(pvd.GetPrototype(), runtimePrototype))
                            {
                                pvd.SetInstanceData(lastIndexPrefabInstance);
                                pvd.SetBufferData(lastIndexPrefabInstance.gpuInstancerID - 1, lastIndexPrefabInstance.gpuInstancerID - 1, 1);
                            }
                        }
                    }

                    lastIndexPrefabInstance.SetupPrefabInstance(runtimeData);
                }
            }

            //#if UNITY_EDITOR
            //            UnityEngine.Profiling.Profiler.EndSample();
            //#endif
        }

        /// <summary>
        /// Removes prefab instances for multiple prototypes
        /// </summary>
        public virtual void RemovePrefabInstances(IEnumerable<GPUInstancerPrefab> prefabInstances)
        {
            if (prefabInstances == null || prototypeList == null)
                return;
            List<GPUInstancerPrefab>[] instanceLists = new List<GPUInstancerPrefab>[prototypeList.Count];
            Dictionary<GPUInstancerPrototype, int> indexDict = new Dictionary<GPUInstancerPrototype, int>();
            for (int i = 0; i < instanceLists.Length; i++)
            {
                instanceLists[i] = new List<GPUInstancerPrefab>();
                GPUInstancerPrototype prototype = prototypeList[i];
                if (prototype != null && !indexDict.ContainsKey(prototype))
                    indexDict.Add(prototype, i);
            }

            foreach (GPUInstancerPrefab prefabInstance in prefabInstances)
            {
                if (prefabInstance == null)
                    continue;
                GPUInstancerPrototype runtimePrototype =
                    ResolveRuntimePrototype(prefabInstance.prefabPrototype);
                if (runtimePrototype != null && indexDict.TryGetValue(runtimePrototype, out int index))
                    instanceLists[index].Add(prefabInstance);
            }
            for (int i = 0; i < instanceLists.Length; i++)
            {
                GPUInstancerPrefabPrototype prototype = prototypeList[i] as GPUInstancerPrefabPrototype;
                if (prototype != null)
                    RemovePrefabInstances(prototype, instanceLists[i]);
            }
        }

        /// <summary>
        /// Removes prefab instances for single prototype
        /// </summary>
        public virtual void RemovePrefabInstances(GPUInstancerPrefabPrototype prototype, IEnumerable<GPUInstancerPrefab> prefabInstances)
        {
            if (prefabInstances == null || prefabInstances.Count() == 0)
                return;

            prototype = ResolveRuntimePrototype(prototype) as GPUInstancerPrefabPrototype;
            if (prototype == null)
                return;

            int count = prefabInstances.Count();

            GPUInstancerRuntimeData runtimeData = GetRuntimeData(prototype, true);
            if (runtimeData == null)
                return;

            if (!_registeredPrefabsRuntimeData.TryGetValue(prototype, out List<GPUInstancerPrefab> prefabInstanceList))
                return;
            prefabInstanceList.RemoveRange(prefabInstances.ElementAt(0).gpuInstancerID - 1, count);
            foreach (GPUInstancerPrefab pi in prefabInstances)
            {
                if (enableMROnRemoveInstance && !prototype.meshRenderersDisabled)
                    SetRenderersEnabled(pi, true);
                pi.state = PrefabInstancingState.None;
                pi.gpuInstancerID = 0;
            }

            UpdateInstanceDataArray(runtimeData, prefabInstanceList);
        }

        public virtual void RegisterPrefabsInScene()
        {
            if (registeredPrefabs == null)
                registeredPrefabs = new List<RegisteredPrefabsData>();
            registeredPrefabs.Clear();
            if (prototypeList != null)
            {
                foreach (GPUInstancerPrototype key in prototypeList)
                {
                    GPUInstancerPrefabPrototype pp = key as GPUInstancerPrefabPrototype;
                    if (pp != null)
                        registeredPrefabs.Add(new RegisteredPrefabsData(pp));
                }
            }

            GPUInstancerPrefab[] scenePrefabInstances = UnityEngine.Object.FindObjectsOfType<GPUInstancerPrefab>();
            foreach (GPUInstancerPrefab prefabInstance in scenePrefabInstances)
                AddRegisteredPrefab(prefabInstance);
        }

        public virtual void RegisterPrefabInstanceList(IEnumerable<GPUInstancerPrefab> prefabInstanceList)
        {
            if (prefabInstanceList == null)
                return;
            if (prototypeList == null)
                prototypeList = new List<GPUInstancerPrototype>();
            if (_registeredPrefabsRuntimeData == null)
                _registeredPrefabsRuntimeData = new Dictionary<GPUInstancerPrototype, List<GPUInstancerPrefab>>();
            if (_registeredPrefabsRuntimeData.Keys.Count != prototypeList.Count)
            {
                foreach (GPUInstancerPrototype prototype in prototypeList)
                    if (prototype != null && prototype is GPUInstancerPrefabPrototype && !_registeredPrefabsRuntimeData.ContainsKey(prototype))
                        _registeredPrefabsRuntimeData.Add(prototype, new List<GPUInstancerPrefab>());
            }

            foreach (GPUInstancerPrefab prefabInstance in prefabInstanceList)
            {
                if (prefabInstance == null)
                    continue;
                GPUInstancerPrefabPrototype runtimePrototype =
                    ResolveRuntimePrototype(prefabInstance.prefabPrototype) as GPUInstancerPrefabPrototype;
                if (runtimePrototype != null)
                    GetOrCreateRuntimeRegisteredPrefabs(runtimePrototype).Add(prefabInstance);
            }
        }

        public virtual void UnregisterPrefabInstanceList(IEnumerable<GPUInstancerPrefab> prefabInstanceList)
        {
            if (prefabInstanceList == null)
                return;
            if (prototypeList == null)
                prototypeList = new List<GPUInstancerPrototype>();
            if (_registeredPrefabsRuntimeData == null)
                _registeredPrefabsRuntimeData = new Dictionary<GPUInstancerPrototype, List<GPUInstancerPrefab>>();
            if (_registeredPrefabsRuntimeData.Keys.Count != prototypeList.Count)
            {
                foreach (GPUInstancerPrototype prototype in prototypeList)
                    if (prototype != null && prototype is GPUInstancerPrefabPrototype && !_registeredPrefabsRuntimeData.ContainsKey(prototype))
                        _registeredPrefabsRuntimeData.Add(prototype, new List<GPUInstancerPrefab>());
            }

            foreach (GPUInstancerPrefab prefabInstance in prefabInstanceList)
            {
                if (prefabInstance == null)
                    continue;
                GPUInstancerPrefabPrototype runtimePrototype =
                    ResolveRuntimePrototype(prefabInstance.prefabPrototype) as GPUInstancerPrefabPrototype;
                if (runtimePrototype != null &&
                    _registeredPrefabsRuntimeData.TryGetValue(runtimePrototype, out List<GPUInstancerPrefab> list))
                    list.Remove(prefabInstance);
            }
        }

        public virtual void ClearRegisteredPrefabInstances()
        {
            if (_registeredPrefabsRuntimeData == null)
                return;
            foreach (GPUInstancerPrototype p in _registeredPrefabsRuntimeData.Keys)
            {
                List<GPUInstancerPrefab> list;
                if (_registeredPrefabsRuntimeData.TryGetValue(p, out list) && list != null)
                    list.Clear();
            }
        }

        public void ClearRegisteredPrefabInstances(GPUInstancerPrototype p)
        {
            p = ResolveRuntimePrototype(p);
            if (_registeredPrefabsRuntimeData != null &&
                _registeredPrefabsRuntimeData.TryGetValue(p, out List<GPUInstancerPrefab> prefabList))
                prefabList.Clear();
        }

        public virtual PrefabVariationData<T> DefinePrototypeVariationBuffer<T>(GPUInstancerPrefabPrototype prototype, string bufferName)
        {
            prototype = ResolveRuntimePrototype(prototype) as GPUInstancerPrefabPrototype;
            if (prototype == null)
                return null;
            if (_variationDataList == null)
                _variationDataList = new List<IPrefabVariationData>();
            if (GPUInstancerUtility.matrixHandlingType == GPUIMatrixHandlingType.MatrixAppend)
            {
                Debug.LogError("GPUI can not define material variations in this platform and/or with this rendering settings.");
                return null;
            }
            PrefabVariationData<T> result = null;
            foreach (IPrefabVariationData item in _variationDataList)
            {
                if (item.GetPrototype() == prototype && item.GetBufferName() == bufferName && item is PrefabVariationData<T>)
                {
                    result = (PrefabVariationData<T>)item;
                    break;
                }
            }
            if (result == null)
            {
                result = new PrefabVariationData<T>(prototype, bufferName);
                _variationDataList.Add(result);

                if (isInitialized)
                {
                    GPUInstancerRuntimeData runtimeData = GetRuntimeData(prototype);
                    result.InitializeBufferAndArray(runtimeData.bufferSize);
                    if (_registeredPrefabsRuntimeData != null && _registeredPrefabsRuntimeData.TryGetValue(prototype, out List<GPUInstancerPrefab> prefabList))
                    {
                        foreach (GPUInstancerPrefab prefabInstance in prefabList)
                        {
                            result.SetInstanceData(prefabInstance);
                        }
                    }
                    result.SetBufferData(0, 0, runtimeData.bufferSize);

                    for (int i = 0; i < runtimeData.instanceLODs.Count; i++)
                    {
                        for (int j = 0; j < runtimeData.instanceLODs[i].renderers.Count; j++)
                        {
                            result.SetVariation(runtimeData.instanceLODs[i].renderers[j]);
                        }
                    }
                }
            }
            return result;
        }

        public virtual void UpdateVariationData<T>(GPUInstancerPrefab prefabInstance, string bufferName, T value)
        {
            if (!prefabInstance || !prefabInstance.prefabPrototype)
                return;
            GPUInstancerPrefabPrototype runtimePrototype =
                ResolveRuntimePrototype(prefabInstance.prefabPrototype) as GPUInstancerPrefabPrototype;
            if (runtimePrototype == null)
                return;
            PrefabVariationData<T> variationData = null;
            foreach (IPrefabVariationData item in _variationDataList)
            {
                if (ReferenceEquals(item.GetPrototype(), runtimePrototype) && item.GetBufferName() == bufferName && item is PrefabVariationData<T>)
                {
                    variationData = (PrefabVariationData<T>)item;
                    break;
                }
            }
            if (variationData != null && variationData.dataArray != null)
            {
                int index = prefabInstance.gpuInstancerID - 1;
                if (index >= 0 && index < variationData.dataArray.Length)
                {
                    variationData.dataArray[index] = value;
#if UNITY_2017_1_OR_NEWER
                    variationData.variationBuffer.SetData(variationData.dataArray, index, index, 1);
#else
                    variationData.variationBuffer.SetData(variationData.dataArray);
#endif
                }
            }
        }

        public virtual PrefabVariationData<T> DefineAndAddVariationFromArray<T>(GPUInstancerPrefabPrototype prototype, string bufferName, T[] variationArray)
        {
            PrefabVariationData<T> variationData = DefinePrototypeVariationBuffer<T>(prototype, bufferName);
            GPUInstancerRuntimeData runtimeData = GetRuntimeData(prototype);
            if (runtimeData != null && variationData != null)
            {
                variationData.SetArrayAndInitializeBuffer(variationArray);
                variationData.SetBufferData(0, 0, Math.Min(runtimeData.bufferSize, variationArray.Length));
                for (int l = 0; l < runtimeData.instanceLODs.Count; l++)
                {
                    for (int r = 0; r < runtimeData.instanceLODs[l].renderers.Count; r++)
                    {
                        variationData.SetVariation(runtimeData.instanceLODs[l].renderers[r]);
                    }
                }
            }

            return variationData;
        }

        public virtual PrefabVariationData<T> DefineAndAddVariationFromArray<T>(GPUInstancerPrefabPrototype prototype, string bufferName, NativeArray<T> variationArray) where T : struct
        {
            PrefabVariationData<T> variationData = DefinePrototypeVariationBuffer<T>(prototype, bufferName);
            GPUInstancerRuntimeData runtimeData = GetRuntimeData(prototype);
            if (runtimeData != null && variationData != null)
            {
                variationData.SetArrayAndInitializeBuffer(variationArray);
                for (int l = 0; l < runtimeData.instanceLODs.Count; l++)
                {
                    for (int r = 0; r < runtimeData.instanceLODs[l].renderers.Count; r++)
                    {
                        variationData.SetVariation(runtimeData.instanceLODs[l].renderers[r]);
                    }
                }
            }

            return variationData;
        }

        public virtual PrefabVariationData<T> UpdateVariationsFromArray<T>(GPUInstancerPrefabPrototype prototype, string bufferName, T[] variationArray,
            int arrayStartIndex = 0, int bufferStartIndex = 0, int count = 0)
        {
            prototype = ResolveRuntimePrototype(prototype) as GPUInstancerPrefabPrototype;
            if (prototype == null)
                return null;
            PrefabVariationData<T> variationData = null;
            foreach (IPrefabVariationData item in _variationDataList)
            {
                if (item.GetPrototype() == prototype && item.GetBufferName() == bufferName && item is PrefabVariationData<T>)
                {
                    variationData = (PrefabVariationData<T>)item;
                    break;
                }
            }
            if (variationData != null)
            {
                GPUInstancerRuntimeData runtimeData = GetRuntimeData(prototype);
                if (runtimeData != null)
                {
                    variationData.dataArray = variationArray;
                    if (count > 0)
                        variationData.SetBufferData(arrayStartIndex, bufferStartIndex, count);
                    else
                        variationData.SetBufferData(0, 0, runtimeData.bufferSize);
                    for (int l = 0; l < runtimeData.instanceLODs.Count; l++)
                    {
                        for (int r = 0; r < runtimeData.instanceLODs[l].renderers.Count; r++)
                        {
                            variationData.SetVariation(runtimeData.instanceLODs[l].renderers[r]);
                        }
                    }
                }
            }

            return variationData;
        }

        public virtual PrefabVariationData<T> UpdateVariationsFromArray<T>(GPUInstancerPrefabPrototype prototype, string bufferName, NativeArray<T> variationArray,
            int arrayStartIndex = 0, int bufferStartIndex = 0, int count = 0) where T : struct
        {
            prototype = ResolveRuntimePrototype(prototype) as GPUInstancerPrefabPrototype;
            if (prototype == null)
                return null;
            PrefabVariationData<T> variationData = null;
            foreach (IPrefabVariationData item in _variationDataList)
            {
                if (item.GetPrototype() == prototype && item.GetBufferName() == bufferName && item is PrefabVariationData<T>)
                {
                    variationData = (PrefabVariationData<T>)item;
                    break;
                }
            }
            if (variationData != null)
            {
                GPUInstancerRuntimeData runtimeData = GetRuntimeData(prototype);
                if (runtimeData != null)
                {
                    if (count > 0)
                        variationData.variationBuffer.SetData(variationArray, arrayStartIndex, bufferStartIndex, count);
                    else
                        variationData.variationBuffer.SetData(variationArray);
                    for (int l = 0; l < runtimeData.instanceLODs.Count; l++)
                    {
                        for (int r = 0; r < runtimeData.instanceLODs[l].renderers.Count; r++)
                        {
                            variationData.SetVariation(runtimeData.instanceLODs[l].renderers[r]);
                        }
                    }
                }
            }

            return variationData;
        }

        public ComputeBuffer GetVariationBuffer(GPUInstancerPrefabPrototype prototype, string bufferName)
        {
            prototype = ResolveRuntimePrototype(prototype) as GPUInstancerPrefabPrototype;
            if (prototype == null)
                return null;
            foreach (IPrefabVariationData item in _variationDataList)
            {
                if (item.GetPrototype() == prototype && item.GetBufferName() == bufferName)
                    return item.GetBuffer();
            }
            Debug.LogError(prototype + " does not contain a variation buffer with name: " + bufferName);
            return null;
        }

        public virtual GPUInstancerPrefabPrototype DefineGameObjectAsPrefabPrototypeAtRuntime(GameObject prototypeGameObject, bool attachScript = true)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("DefineGameObjectAsPrefabPrototypeAtRuntime method is designed to use at runtime. Prototype generation canceled.");
                return null;
            }

            if (prototypeGameObject == null)
            {
                Debug.LogWarning("DefineGameObjectAsPrefabPrototypeAtRuntime requires a non-null GameObject.", diagnosticContext);
                return null;
            }

            if (prefabList == null)
                prefabList = new List<GameObject>();

            if (prototypeList == null)
                prototypeList = new List<GPUInstancerPrototype>();

            // When attachScript is false the utility intentionally has no
            // component on the source GameObject to use as a stable key.  A
            // second call would therefore create another transient
            // ScriptableObject every time, even though this manager already
            // owns an equivalent runtime prototype.  Reuse the manager's
            // owned entry by its prefab object before asking the utility to
            // generate anything.  Restrict the fast path to explicitly-owned
            // runtime objects so an externally supplied/persistent prototype
            // is never claimed (or mutated) merely because its prefabObject
            // happens to match.
            for (int i = 0; i < prototypeList.Count; i++)
            {
                GPUInstancerPrefabPrototype existing =
                    prototypeList[i] as GPUInstancerPrefabPrototype;
                if (existing == null || !ReferenceEquals(existing.prefabObject, prototypeGameObject) ||
                    !IsOwnedRuntimePrototype(existing))
                    continue;

                if (!prefabList.Contains(prototypeGameObject))
                    prefabList.Add(prototypeGameObject);
                if (existing.minCullingDistance < minCullingDistance)
                    existing.minCullingDistance = minCullingDistance;
                return existing;
            }

            // GeneratePrefabPrototype may return a persistent prototype that
            // is already referenced by the prefab component. Runtime API
            // changes (culling distance, registration and generated caches)
            // must never mutate that asset. Capture the source before the
            // utility call so we can distinguish a newly-created transient
            // object from an external/persistent one.
            GPUInstancerPrefab prefabComponent = prototypeGameObject.GetComponent<GPUInstancerPrefab>();
            GPUInstancerPrefabPrototype sourcePrototype = prefabComponent == null
                ? null
                : prefabComponent.prefabPrototype;

            GPUInstancerPrefabPrototype generatedPrototype =
                GPUInstancerUtility.GeneratePrefabPrototype(prototypeGameObject, false, attachScript);
            if (generatedPrototype == null)
            {
                Debug.LogWarning("Could not generate a prefab prototype for '" + prototypeGameObject.name + "'.", diagnosticContext);
                return null;
            }

            GPUInstancerPrefabPrototype prefabPrototype =
                ResolveRuntimePrototype(generatedPrototype) as GPUInstancerPrefabPrototype;
            if (prefabPrototype == null)
                return null;

            // If the utility returned an object outside this manager's
            // Profile clone map, isolate it before applying runtime-only
            // changes. A component can point at a prototype owned by another
            // manager (or at a persistent asset), so checking only the object
            // type is insufficient: claiming that object here would make the
            // two managers share a destroyable runtime object. Manager-owned
            // profile/dynamic prototypes are marked DontSave; every other
            // pre-existing source is cloned before it is touched.
            bool isManagerOwnedRuntimePrototype = IsOwnedRuntimePrototype(prefabPrototype);
            bool isPreExistingPrototype = sourcePrototype != null;
            if (isPreExistingPrototype && !isManagerOwnedRuntimePrototype)
            {
                GPUInstancerPrefabPrototype runtimeClone =
                    UnityEngine.Object.Instantiate(prefabPrototype);
                if (runtimeClone == null)
                {
                    Debug.LogWarning("Could not clone persistent prefab prototype '" + prefabPrototype.name + "'.", diagnosticContext);
                    return null;
                }
                runtimeClone.name = prefabPrototype.name + " (Runtime)";
                runtimeClone.hideFlags = HideFlags.HideAndDontSave;
                OwnRuntimePrototype(runtimeClone);
                // Keep source references resolvable when the source component
                // belongs to a persistent prefab asset and therefore cannot
                // be rewritten in Play Mode.
                RegisterRuntimePrototypeMapping(prefabPrototype, runtimeClone);
                prefabPrototype = runtimeClone;
            }
            else if (!isManagerOwnedRuntimePrototype)
            {
                // This is normally the ScriptableObject created by the
                // utility for a GameObject without a prototype component.
                // Own it even if the utility later changes its hide flags.
                OwnRuntimePrototype(prefabPrototype);
            }

            // The component must point at the runtime object so subsequent
            // registration callbacks resolve the same manager-owned key.
            // Do not write a persistent prefab asset while running; adding a
            // component/prototype reference at runtime is inherently
            // transient and Unity will discard it with the scene instance.
            bool canWritePrefabComponent = prefabComponent != null;
#if UNITY_EDITOR
            // A component obtained from a persistent Prefab asset is still a
            // serialized object while Play Mode is running. Pointing it at a
            // transient prototype would dirty the asset's in-memory state and
            // can leak a destroyed reference back into editor serialization.
            canWritePrefabComponent = canWritePrefabComponent &&
                !EditorUtility.IsPersistent(prefabComponent);
#endif
            if (canWritePrefabComponent &&
                !ReferenceEquals(prefabComponent.prefabPrototype, prefabPrototype))
            {
                prefabComponent.prefabPrototype = prefabPrototype;
            }

            if (!prototypeList.Contains(prefabPrototype))
                prototypeList.Add(prefabPrototype);
            if (!prefabList.Contains(prototypeGameObject))
                prefabList.Add(prototypeGameObject);
            if (prefabPrototype.minCullingDistance < minCullingDistance)
                prefabPrototype.minCullingDistance = minCullingDistance;

            return prefabPrototype;
        }

        public virtual void AddInstancesToPrefabPrototypeAtRuntime(GPUInstancerPrefabPrototype prefabPrototype, IEnumerable<GameObject> instances)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("AddInstancesToPrefabPrototypeAtRuntime method is designed to use at runtime. Adding instances canceled.");
                return;
            }

            prefabPrototype = ResolveRuntimePrototype(prefabPrototype) as GPUInstancerPrefabPrototype;
            if (prefabPrototype == null)
                return;

            if (isActiveAndEnabled)
            {
                List<GPUInstancerPrefab> instanceList =
                    GetOrCreateRuntimeRegisteredPrefabs(prefabPrototype);

                GPUInstancerPrefab prefabInstance;
                foreach (GameObject instance in instances)
                {
                    prefabInstance = instance.GetComponent<GPUInstancerPrefab>();
                    if (prefabInstance == null)
                    {
                        prefabInstance = instance.AddComponent<GPUInstancerPrefab>();
                        prefabInstance.prefabPrototype = prefabPrototype;
                    }
                    if (prefabInstance != null && !instanceList.Contains(prefabInstance))
                        instanceList.Add(prefabInstance);
                }

                GPUInstancerRuntimeData runtimeData = InitializeRuntimeDataForPrefabPrototype(prefabPrototype, 0);
                GPUInstancerUtility.InitializeGPUBuffer(runtimeData);
            }
            else
            {
                if (registeredPrefabs == null)
                    registeredPrefabs = new List<RegisteredPrefabsData>();

                RegisteredPrefabsData data = null;
                foreach (RegisteredPrefabsData item in registeredPrefabs)
                {
                    if (item.prefabPrototype == prefabPrototype)
                    {
                        data = item;
                        break;
                    }
                }
                if (data == null)
                {
                    data = new RegisteredPrefabsData(prefabPrototype);
                    registeredPrefabs.Add(data);
                }

                GPUInstancerPrefab prefabInstance;
                foreach (GameObject instance in instances)
                {
                    prefabInstance = instance.GetComponent<GPUInstancerPrefab>();
                    if (prefabInstance == null)
                    {
                        prefabInstance = instance.AddComponent<GPUInstancerPrefab>();
                        prefabInstance.prefabPrototype = prefabPrototype;
                    }
                    if (prefabInstance != null && !data.registeredPrefabs.Contains(prefabInstance))
                        data.registeredPrefabs.Add(prefabInstance);
                }
            }
        }

        public virtual void RemovePrototypeAtRuntime(GPUInstancerPrefabPrototype prefabPrototype)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("RemovePrototypeAtRuntime method is designed to use at runtime. Prototype removal canceled.");
                return;
            }

            prefabPrototype = ResolveRuntimePrototype(prefabPrototype) as GPUInstancerPrefabPrototype;
            if (prefabPrototype == null)
                return;

            // A Profile-backed prototype is the runtime clone of the
            // persistent channel recipe.  Removing that clone would leave
            // the manager's source snapshot intact while its runtime list no
            // longer represented the Profile; subsequent SyncProfileToRuntime
            // calls would (correctly) see no source edit and therefore never
            // rebuild the missing entry.  The public runtime-removal API is
            // intended for definitions created dynamically at runtime, so
            // leave Profile prototypes alone and require the caller to edit
            // the Profile (or explicitly refresh it) for a persistent change.
            if (IsRuntimeProfilePrototype(prefabPrototype))
            {
                Debug.LogWarning(
                    "RemovePrototypeAtRuntime cannot remove a Profile-backed prototype. " +
                    "Edit the Profile or use a dynamically-created runtime prototype.",
                    prefabPrototype);
                return;
            }

            // Detach every registered instance before releasing the runtime
            // buffers. Otherwise the components remain in Instanced state with
            // stale IDs, keep their renderers disabled, and later OnDisable
            // callbacks will dereference a runtime data entry that no longer
            // exists.
            List<GPUInstancerPrefab> removedInstances = new List<GPUInstancerPrefab>();
            if (_registeredPrefabsRuntimeData != null &&
                _registeredPrefabsRuntimeData.TryGetValue(prefabPrototype, out List<GPUInstancerPrefab> runtimeInstances))
            {
                if (runtimeInstances != null)
                    removedInstances.AddRange(runtimeInstances);
                _registeredPrefabsRuntimeData.Remove(prefabPrototype);
            }

            if (registeredPrefabs != null)
            {
                for (int i = registeredPrefabs.Count - 1; i >= 0; i--)
                {
                    RegisteredPrefabsData data = registeredPrefabs[i];
                    if (data == null)
                    {
                        registeredPrefabs.RemoveAt(i);
                        continue;
                    }
                    GPUInstancerPrefabPrototype dataPrototype =
                        ResolveRuntimePrototype(data.prefabPrototype) as GPUInstancerPrefabPrototype;
                    if (!ReferenceEquals(dataPrototype, prefabPrototype))
                        continue;
                    if (data.registeredPrefabs != null)
                        removedInstances.AddRange(data.registeredPrefabs);
                    registeredPrefabs.RemoveAt(i);
                }
            }

            for (int i = 0; i < removedInstances.Count; i++)
            {
                GPUInstancerPrefab instance = removedInstances[i];
                if (instance == null)
                    continue;
                if (instance.state != PrefabInstancingState.None &&
                    enableMROnRemoveInstance && !prefabPrototype.meshRenderersDisabled)
                    SetRenderersEnabled(instance, true);
                instance.state = PrefabInstancingState.None;
                instance.gpuInstancerID = 0;
            }

            GPUInstancerRuntimeData runtimeData = GetRuntimeData(prefabPrototype);
            if (runtimeData != null)
            {
                GPUInstancerUtility.ReleaseInstanceBuffers(runtimeData);
                if (runtimeDataList != null)
                    runtimeDataList.Remove(runtimeData);
                if (runtimeDataDictionary != null)
                    runtimeDataDictionary.Remove(runtimeData.prototype);
            }

            // Variation buffers are keyed by the runtime prototype and must
            // be released together with its instance data. Retaining them
            // keeps ComputeBuffers alive even after the prototype is removed.
            if (_variationDataList != null)
            {
                for (int i = _variationDataList.Count - 1; i >= 0; i--)
                {
                    IPrefabVariationData variation = _variationDataList[i];
                    GPUInstancerPrefabPrototype variationPrototype = variation == null
                        ? null
                        : ResolveRuntimePrototype(variation.GetPrototype()) as GPUInstancerPrefabPrototype;
                    if (variation == null || ReferenceEquals(variationPrototype, prefabPrototype))
                    {
                        if (variation != null)
                            variation.ReleaseBuffer();
                        _variationDataList.RemoveAt(i);
                    }
                }
            }

            if (prototypeList != null)
            {
                for (int i = prototypeList.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(prototypeList[i], prefabPrototype))
                        prototypeList.RemoveAt(i);
                }
            }
            if (prefabList != null && prefabPrototype.prefabObject != null)
            {
                bool stillUsed = false;
                if (prototypeList != null)
                {
                    for (int i = 0; i < prototypeList.Count; i++)
                    {
                        GPUInstancerPrefabPrototype remaining = prototypeList[i] as GPUInstancerPrefabPrototype;
                        if (remaining != null && ReferenceEquals(remaining.prefabObject, prefabPrototype.prefabObject))
                        {
                            stillUsed = true;
                            break;
                        }
                    }
                }
                if (!stillUsed)
                    prefabList.Remove(prefabPrototype.prefabObject);
            }

            // Only destroy objects explicitly owned by this manager. Profile
            // assets or prototypes supplied by another manager remain intact.
            ReleaseOwnedRuntimePrototype(prefabPrototype);
        }
        #endregion API Methods

        private List<GPUInstancerPrefab> GetOrCreateRuntimeRegisteredPrefabs(
            GPUInstancerPrefabPrototype prototype)
        {
            if (prototype == null)
                return null;
            if (_registeredPrefabsRuntimeData == null)
                _registeredPrefabsRuntimeData = new Dictionary<GPUInstancerPrototype, List<GPUInstancerPrefab>>();
            prototype = ResolveRuntimePrototype(prototype) as GPUInstancerPrefabPrototype;
            if (prototype == null)
                return null;
            if (!_registeredPrefabsRuntimeData.TryGetValue(prototype, out List<GPUInstancerPrefab> list))
            {
                list = new List<GPUInstancerPrefab>();
                _registeredPrefabsRuntimeData.Add(prototype, list);
            }
            return list;
        }

        public virtual void AddRegisteredPrefab(GPUInstancerPrefab prefabInstance)
        {
            if (prefabInstance == null)
                return;
            if (registeredPrefabs == null)
                registeredPrefabs = new List<RegisteredPrefabsData>();
            GPUInstancerPrefabPrototype runtimePrototype =
                ResolveRuntimePrototype(prefabInstance.prefabPrototype) as GPUInstancerPrefabPrototype;
            if (runtimePrototype == null)
                return;
            RegisteredPrefabsData data = null;
            foreach (RegisteredPrefabsData item in registeredPrefabs)
            {
                if (item != null && ReferenceEquals(item.prefabPrototype, runtimePrototype))
                {
                    data = item;
                    break;
                }
            }
            if (data != null)
                data.registeredPrefabs.Add(prefabInstance);
        }

        public virtual void AddRuntimeRegisteredPrefab(GPUInstancerPrefab prefabInstance)
        {
            if (prefabInstance == null)
                return;
            GPUInstancerPrefabPrototype runtimePrototype =
                ResolveRuntimePrototype(prefabInstance.prefabPrototype) as GPUInstancerPrefabPrototype;
            if (runtimePrototype == null)
                return;
            List<GPUInstancerPrefab> list = GetOrCreateRuntimeRegisteredPrefabs(runtimePrototype);

            if (!list.Contains(prefabInstance))
                list.Add(prefabInstance);
        }

        public virtual void AddModificationCollider(GPUInstancerModificationCollider modificationCollider)
        {
            if (_modificationColliders == null)
                _modificationColliders = new List<GPUInstancerModificationCollider>();

            _modificationColliders.Add(modificationCollider);
        }

        public virtual int GetEnabledPrefabCount()
        {
            int sum = 0;
            if (_modificationColliders != null)
            {
                for (int i = 0; i < _modificationColliders.Count; i++)
                    sum += _modificationColliders[i].GetEnteredInstanceCount();
            }
            return sum;
        }

        public virtual Dictionary<GPUInstancerPrototype, List<GPUInstancerPrefab>> GetRegisteredPrefabsRuntimeData()
        {
            return _registeredPrefabsRuntimeData;
        }
    }

    [Serializable]
    public class RegisteredPrefabsData
    {
        public GPUInstancerPrefabPrototype prefabPrototype;
        public List<GPUInstancerPrefab> registeredPrefabs;

        public RegisteredPrefabsData(GPUInstancerPrefabPrototype prefabPrototype)
        {
            this.prefabPrototype = prefabPrototype;
            registeredPrefabs = new List<GPUInstancerPrefab>();
        }
    }

    public interface IPrefabVariationData
    {
        void InitializeBufferAndArray(int count, bool setDefaults = true);
        void SetInstanceData(GPUInstancerPrefab prefabInstance);
        void SetBufferData(int managedBufferStartIndex, int computeBufferStartIndex, int count);
        void SetVariation(GPUInstancerRenderer gpuiRenderer);
        void SetNewBufferSize(int newCount);
        GPUInstancerPrefabPrototype GetPrototype();
        string GetBufferName();
        ComputeBuffer GetBuffer();
        void ReleaseBuffer();
    }

    public class PrefabVariationData<T> : IPrefabVariationData
    {
        public GPUInstancerPrefabPrototype prototype;
        public string bufferName;
        public ComputeBuffer variationBuffer;
        public T[] dataArray;
        public T defaultValue;

        public PrefabVariationData(GPUInstancerPrefabPrototype prototype, string bufferName, T defaultValue = default(T))
        {
            this.prototype = prototype;
            this.bufferName = bufferName;
            this.defaultValue = defaultValue;
        }

        public void InitializeBufferAndArray(int count, bool setDefaults = true)
        {
            if (count == 0)
                return;
            dataArray = new T[count];
            if (setDefaults)
            {
                for (int i = 0; i < count; i++)
                {
                    dataArray[i] = defaultValue;
                }
            }
            if (variationBuffer != null)
                variationBuffer.Release();
            variationBuffer = new ComputeBuffer(count, System.Runtime.InteropServices.Marshal.SizeOf(typeof(T)));
        }

        public void SetArrayAndInitializeBuffer(T[] dataArray)
        {
            if (dataArray == null || dataArray.Length == 0)
                return;
            this.dataArray = dataArray;
            if (variationBuffer != null)
                variationBuffer.Release();
            variationBuffer = new ComputeBuffer(dataArray.Length, System.Runtime.InteropServices.Marshal.SizeOf(typeof(T)));
        }

        public void SetArrayAndInitializeBuffer<K>(NativeArray<K> dataArray) where K : struct
        {
            if (dataArray == null || dataArray.Length == 0)
                return;
            if (variationBuffer != null)
                variationBuffer.Release();
            variationBuffer = new ComputeBuffer(dataArray.Length, System.Runtime.InteropServices.Marshal.SizeOf(typeof(K)));
            variationBuffer.SetData(dataArray);
        }

        public void SetInstanceData(GPUInstancerPrefab prefabInstance)
        {
            if (prefabInstance.variationDataList != null && dataArray != null && prefabInstance.variationDataList.ContainsKey(bufferName) && dataArray.Length > prefabInstance.gpuInstancerID - 1)
                dataArray[prefabInstance.gpuInstancerID - 1] = (T)prefabInstance.variationDataList[bufferName];
        }

        public void SetBufferData(int managedBufferStartIndex, int computeBufferStartIndex, int count)
        {
            if (variationBuffer != null && count > 0 && dataArray != null)
            {
#if UNITY_2017_1_OR_NEWER
                variationBuffer.SetData(dataArray, managedBufferStartIndex, computeBufferStartIndex, count);
#else
                variationBuffer.SetData(dataArray);
#endif
            }
        }

        public void SetVariation(GPUInstancerRenderer gpuiRenderer)
        {
            if (variationBuffer != null && gpuiRenderer != null)
            {
                if (gpuiRenderer.mpb != null)
                    gpuiRenderer.mpb.SetBuffer(bufferName, variationBuffer);
                if (gpuiRenderer.shadowMPB != null)
                    gpuiRenderer.shadowMPB.SetBuffer(bufferName, variationBuffer);
            }
        }

        public void SetNewBufferSize(int newCount)
        {
            if (newCount <= 0)
                return;
            int count = 0;
            if (dataArray != null)
            {
                count = dataArray.Length;
                if (count < newCount)
                    Array.Resize<T>(ref dataArray, newCount);
            }
            else
                dataArray = new T[newCount];

            if (count < newCount)
            {
                if (variationBuffer != null)
                    variationBuffer.Release();
                variationBuffer = new ComputeBuffer(newCount, System.Runtime.InteropServices.Marshal.SizeOf(typeof(T)));
                Array.Resize<T>(ref dataArray, newCount);
                for (int i = count; i < newCount; i++)
                {
                    dataArray[i] = defaultValue;
                }
                variationBuffer.SetData(dataArray);
            }
        }

        public GPUInstancerPrefabPrototype GetPrototype()
        {
            return prototype;
        }

        public ComputeBuffer GetBuffer()
        {
            return variationBuffer;
        }

        public string GetBufferName()
        {
            return bufferName;
        }

        public void ReleaseBuffer()
        {
            if (variationBuffer != null)
                variationBuffer.Release();
            variationBuffer = null;
            dataArray = null;
        }
    }
}
