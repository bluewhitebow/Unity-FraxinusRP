using static UnityEngine.Rendering.AdaptiveProbeVolumes;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEditor;
#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
using UnityEngine.Rendering.UnifiedRayTracing;
#endif

namespace UnityEngine.Rendering
{
    public partial class AdaptiveProbeVolumes
    {
        /// <summary>
        /// Virtual offset baker
        /// </summary>
        public abstract class VirtualOffsetBaker : IDisposable
        {
            /// <summary>The current baking step.</summary>
            public abstract ulong currentStep { get; }
            /// <summary>The total amount of step.</summary>
            public abstract ulong stepCount { get; }

            /// <summary>Array storing the resulting virtual offsets to be applied to probe positions.</summary>
            public abstract NativeArray<Vector3> offsets { get; }

            /// <summary>
            /// This is called before the start of baking to allow allocating necessary resources.
            /// </summary>
            /// <param name="bakingSet">The baking set that is currently baked.</param>
            /// <param name="probePositions">The probe positions.</param>
            public abstract void Initialize(ProbeVolumeBakingSet bakingSet, NativeArray<Vector3> probePositions);

            /// <summary>
            /// Run a step of virtual offset baking. Baking is considered done when currentStep property equals stepCount.
            /// </summary>
            /// <returns>Return false if bake failed and should be stopped.</returns>
            public abstract bool Step();

            /// <summary>
            /// Performs necessary tasks to free allocated resources.
            /// </summary>
            public abstract void Dispose();
        }
    }

    internal partial class APVProbeVolumeBaking
    {

#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
        class DefaultVirtualOffset : VirtualOffsetBaker
        {
            readonly APVProbeVolumeBaking m_Owner;

            internal DefaultVirtualOffset(APVProbeVolumeBaking owner) => m_Owner = owner;

            static int k_MaxProbeCountPerBatch = 65535;

            static readonly int _Probes = Shader.PropertyToID("_Probes");
            static readonly int _Offsets = Shader.PropertyToID("_Offsets");

            // Duplicated in HLSL
            struct ProbeData
            {
                public Vector3 position;
                public float originBias;
                public float tMax;
                public float geometryBias;
                public int probeIndex;
                public float validityThreshold;
            };

            int batchPosIdx;
            NativeArray<Vector3> positions;
            NativeArray<Vector3> results;
            Dictionary<int, TouchupsPerCell> cellToVolumes;
            ProbeData[] probeData;
            Vector3[] batchResult;

            float scaleForSearchDist;
            float defaultRayOriginBias;
            float defaultGeometryBias;
            float defaultValidityThreshold;
            float rayOriginBias;
            float geometryBias;
            float validityThreshold;

            // Output buffer
            public override NativeArray<Vector3> offsets => results;

            private AccelStructAdapter m_AccelerationStructure;
            private StandaloneAPVMaterialResources m_Materials;
            private GraphicsBuffer probeBuffer;
            private GraphicsBuffer offsetBuffer;
            private GraphicsBuffer scratchBuffer;

            public override ulong currentStep => (ulong)batchPosIdx;
            public override ulong stepCount => batchResult == null ? 0 : (ulong)positions.Length;

            public override void Initialize(ProbeVolumeBakingSet bakingSet, NativeArray<Vector3> probePositions)
            {
                var voSettings = bakingSet.settings.virtualOffsetSettings;
                if (!voSettings.useVirtualOffset)
                    return;

                batchPosIdx = 0;
                scaleForSearchDist = voSettings.searchMultiplier;
                defaultRayOriginBias = voSettings.rayOriginBias;
                defaultGeometryBias = voSettings.outOfGeoOffset;
                defaultValidityThreshold = voSettings.validityThreshold;

                results = new NativeArray<Vector3>(probePositions.Length, Allocator.Persistent);
                cellToVolumes = m_Owner.GetTouchupsPerCell(out bool hasAppliers);

                if (scaleForSearchDist == 0.0f)
                {
                    if (hasAppliers)
                        m_Owner.DoApplyVirtualOffsetsFromAdjustmentVolumes(probePositions, results, cellToVolumes);
                    return;
                }

                positions = probePositions;
                probeData = new ProbeData[k_MaxProbeCountPerBatch];
                batchResult = new Vector3[k_MaxProbeCountPerBatch];

                var computeBufferTarget = GraphicsBuffer.Target.CopyDestination | GraphicsBuffer.Target.CopySource
                    | GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Raw;

                // Create acceletation structure
                m_Materials = new StandaloneAPVMaterialResources(opacityOnly: true);
                BuildAccelerationStructure(voSettings.collisionMask);
                m_Materials.Build();
                var virtualOffsetShader = m_Owner.s_TracingContext.shaderVO;

                probeBuffer = new GraphicsBuffer(computeBufferTarget, k_MaxProbeCountPerBatch, Marshal.SizeOf<ProbeData>());
                offsetBuffer = new GraphicsBuffer(computeBufferTarget, k_MaxProbeCountPerBatch, Marshal.SizeOf<Vector3>());
                scratchBuffer = RayTracingHelper.CreateScratchBufferForBuildAndDispatch(m_AccelerationStructure.GetAccelerationStructure(), virtualOffsetShader,
                    (uint)k_MaxProbeCountPerBatch, 1, 1);

                var cmd = new CommandBuffer();
                m_AccelerationStructure.Build(cmd, ref scratchBuffer);
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Dispose();
            }

            void BuildAccelerationStructure(int mask)
            {
                // Retain the resource immediately so Dispose also covers failures
                // while extracting material data or adding instances.
                m_AccelerationStructure = m_Owner.s_TracingContext.CreateAccelerationStructure();
                var contributors = m_Owner.m_BakingBatch.contributors;

                foreach (var renderer in contributors.renderers)
                {
                    int layerMask = 1 << renderer.component.gameObject.layer;
                    if ((layerMask & mask) == 0)
                        continue;

                    if (!m_Owner.s_TracingContext.TryGetMeshForAccelerationStructure(renderer.component, out var mesh))
                        continue;

                    int subMeshCount = mesh.subMeshCount;
                    var perSubMeshMask = new uint[subMeshCount];
                    System.Array.Fill(perSubMeshMask, 0xFFFFFFFF);
                    var materialIDs = new uint[subMeshCount];
                    var materials = renderer.component.sharedMaterials;
                    for (int i = 0; i < subMeshCount; ++i)
                    {
                        var material = materials.Length != 0 ? materials[Mathf.Min(i, materials.Length - 1)] : null;
                        materialIDs[i] = material != null ? m_Materials.GetMaterialID(material) : 0u;
                    }

                    m_AccelerationStructure.AddInstance(renderer.component.GetInstanceID(), renderer.component, perSubMeshMask, materialIDs, 1);
                }

                foreach (var terrain in contributors.terrains)
                {
                    int layerMask = 1 << terrain.component.gameObject.layer;
                    if ((layerMask & mask) == 0)
                        continue;

                    m_AccelerationStructure.AddInstance(terrain.component.GetInstanceID(), terrain.component, new uint[1] { 0xFFFFFFFF }, new uint[1] { 0 }, 1);
                }
            }

            public override bool Step()
            {
                if (currentStep >= stepCount)
                    return true;

                float minBrickSize = m_Owner.m_Layout.minBrickSize;

                // Prepare batch
                int probeCountInBatch = 0;
                do
                {
                    // Adjustment-volume overrides apply to one probe only. Reset
                    // the per-probe values before checking the next position so
                    // an override cannot leak into later probes or batches.
                    rayOriginBias = defaultRayOriginBias;
                    geometryBias = defaultGeometryBias;
                    validityThreshold = defaultValidityThreshold;

                    int subdivLevel = m_Owner.m_BakingBatch.GetSubdivLevelAt(positions[batchPosIdx]);
                    var brickSize = APVProbeReferenceVolume.CellSize(subdivLevel);
                    var searchDistance = (brickSize * minBrickSize) / ProbeBrickPoolBase.brickCellCount;
                    var distanceSearch = scaleForSearchDist * searchDistance;

                    int cellIndex = m_Owner.PosToIndex(m_Owner.m_Layout.PositionToCell(positions[batchPosIdx]));
                    if (cellToVolumes.TryGetValue(cellIndex, out var volumes))
                    {
                        bool adjusted = false;
                        foreach (var (touchup, obb, center, offset) in volumes.appliers)
                        {
                            if (touchup.ContainsPoint(obb, center, positions[batchPosIdx]))
                            {
                                results[batchPosIdx] = offset;
                                adjusted = true;
                                break;
                            }
                        }

                        if (adjusted)
                            continue;

                        foreach (var (touchup, obb, center) in volumes.overriders)
                        {
                            if (touchup.ContainsPoint(obb, center, positions[batchPosIdx]))
                            {
                                rayOriginBias = touchup.rayOriginBias;
                                geometryBias = touchup.geometryBias;
                                validityThreshold = 1.0f - touchup.virtualOffsetThreshold;
                                break;
                            }
                        }
                    }

                    probeData[probeCountInBatch++] = new ProbeData
                    {
                        position = positions[batchPosIdx],
                        originBias = rayOriginBias,
                        tMax = distanceSearch,
                        geometryBias = geometryBias,
                        validityThreshold = validityThreshold,
                        probeIndex = batchPosIdx,
                    };
                }
                while (++batchPosIdx < positions.Length && probeCountInBatch < k_MaxProbeCountPerBatch);

                if (probeCountInBatch == 0)
                    return true;

                // Execute job
                var cmd = new CommandBuffer();
                var virtualOffsetShader = m_Owner.s_TracingContext.shaderVO;
                m_AccelerationStructure.Bind(cmd, "_AccelStruct", virtualOffsetShader);
                m_Materials.Bind(cmd, virtualOffsetShader);
                virtualOffsetShader.SetBufferParam(cmd, _Probes, probeBuffer);
                virtualOffsetShader.SetBufferParam(cmd, _Offsets, offsetBuffer);

                cmd.SetBufferData(probeBuffer, probeData);
                virtualOffsetShader.Dispatch(cmd, scratchBuffer, (uint)probeCountInBatch, 1, 1);

                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                offsetBuffer.GetData(batchResult);
                for (int i = 0; i < probeCountInBatch; i++)
                    results[probeData[i].probeIndex] = batchResult[i];

                cmd.Dispose();
                return true;
            }

            public override void Dispose()
            {
                if (results.IsCreated)
                    results.Dispose();

                // Initialization can fail before all GPU resources are allocated.
                m_AccelerationStructure?.Dispose();
                m_Materials?.Dispose();
                m_Materials = null;
                probeBuffer?.Dispose();
                offsetBuffer?.Dispose();
                scratchBuffer?.Dispose();
                m_AccelerationStructure = null;
                probeBuffer = null;
                offsetBuffer = null;
                scratchBuffer = null;
                batchResult = null;
            }
        }

#else
        // Physics queries only see Collider components, while GI contributors
        // commonly consist of a MeshRenderer and MeshFilter only. Keep the
        // Core 14 baking setup shared by all Unity 2022 physics bakers so the
        // scene queried here represents the same static GI geometry as the
        // lightmapper. Scene state is restored between query batches and all
        // temporary components are removed on cancellation or assembly reload.
        readonly Unity2022PhysicsBakingScene m_PhysicsScene = new Unity2022PhysicsBakingScene();

        sealed class Unity2022PhysicsBakingScene
        {
            readonly List<MeshCollider> s_AddedOccluders = new List<MeshCollider>();
            readonly List<Collider> s_ExcludedColliders = new List<Collider>();
            readonly List<Rigidbody> s_ExcludedRigidBodies = new List<Rigidbody>();

            int s_ReferenceCount;
            int s_TraceDepth;

            internal void Acquire()
            {
                if (s_ReferenceCount++ != 0)
                    return;

                AssemblyReloadEvents.beforeAssemblyReload += ForceCleanup;
                EditorApplication.quitting += ForceCleanup;

                try
                {
                    GeneratePhysicsComponentLists();

                    // A non-convex MeshCollider cannot be attached to a
                    // non-kinematic Rigidbody. Protect rigid bodies while the
                    // temporary colliders are installed, then restore the
                    // original scene state before returning to the editor.
                    ModifyPhysicsComponentsForBaking();
                    try
                    {
                        AddOccluders();
                        Physics.SyncTransforms();
                    }
                    finally
                    {
                        RestorePhysicsComponentsAfterBaking();
                        Physics.SyncTransforms();
                    }
                }
                catch
                {
                    ForceCleanup();
                    throw;
                }
            }

            internal void Release()
            {
                if (s_ReferenceCount == 0 || --s_ReferenceCount != 0)
                    return;

                ForceCleanup();
            }

            internal void BeginTrace()
            {
                if (s_ReferenceCount == 0 || s_TraceDepth++ != 0)
                    return;

                try
                {
                    ModifyPhysicsComponentsForBaking();
                    Physics.SyncTransforms();
                }
                catch
                {
                    s_TraceDepth = 0;
                    RestorePhysicsComponentsAfterBaking();
                    Physics.SyncTransforms();
                    throw;
                }
            }

            internal void EndTrace()
            {
                if (s_TraceDepth == 0 || --s_TraceDepth != 0)
                    return;

                RestorePhysicsComponentsAfterBaking();
                Physics.SyncTransforms();
            }

            void GeneratePhysicsComponentLists()
            {
                s_ExcludedColliders.Clear();
                s_ExcludedRigidBodies.Clear();

                // Volume and gameplay colliders must not invalidate probes.
                // Keep colliders only when they belong to an enabled static-GI
                // mesh or terrain contributor.
                foreach (var collider in Object.FindObjectsOfType<Collider>(includeInactive: false))
                {
                    if (!collider.enabled)
                        continue;

                    bool contributesGI = GIContributors.ContributesGI(collider.gameObject);
                    bool hasEnabledRenderer =
                        (collider.TryGetComponent<MeshRenderer>(out var meshRenderer) && meshRenderer.enabled) ||
                        (collider.TryGetComponent<Terrain>(out var terrain) && terrain.enabled);

                    if (!contributesGI || !hasEnabledRenderer)
                        s_ExcludedColliders.Add(collider);
                }

                foreach (var rigidBody in Object.FindObjectsOfType<Rigidbody>(includeInactive: false))
                {
                    if (!rigidBody.isKinematic)
                        s_ExcludedRigidBodies.Add(rigidBody);
                }
            }

            void ModifyPhysicsComponentsForBaking()
            {
                foreach (var collider in s_ExcludedColliders)
                {
                    if (collider != null)
                        collider.enabled = false;
                }

                foreach (var rigidBody in s_ExcludedRigidBodies)
                {
                    if (rigidBody != null)
                        rigidBody.isKinematic = true;
                }
            }

            void RestorePhysicsComponentsAfterBaking()
            {
                foreach (var collider in s_ExcludedColliders)
                {
                    if (collider != null)
                        collider.enabled = true;
                }

                foreach (var rigidBody in s_ExcludedRigidBodies)
                {
                    if (rigidBody != null)
                        rigidBody.isKinematic = false;
                }
            }

            void AddOccluders()
            {
                s_AddedOccluders.Clear();

                for (int sceneIndex = 0; sceneIndex < SceneManagement.SceneManager.sceneCount; ++sceneIndex)
                {
                    var scene = SceneManagement.SceneManager.GetSceneAt(sceneIndex);
                    if (!scene.isLoaded)
                        continue;

                    foreach (var root in scene.GetRootGameObjects())
                    {
                        foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>())
                        {
                            if (!renderer.enabled || !GIContributors.ContributesGI(renderer.gameObject))
                                continue;
                            if (!renderer.TryGetComponent<MeshFilter>(out var meshFilter) || meshFilter.sharedMesh == null)
                                continue;

                            bool hasUsableMeshCollider = false;
                            foreach (var existing in renderer.GetComponents<MeshCollider>())
                            {
                                if (existing.enabled && existing.sharedMesh != null)
                                {
                                    hasUsableMeshCollider = true;
                                    break;
                                }
                            }

                            if (hasUsableMeshCollider)
                                continue;

                            var meshCollider = renderer.gameObject.AddComponent<MeshCollider>();
                            meshCollider.sharedMesh = meshFilter.sharedMesh;
                            meshCollider.hideFlags |= HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
                            s_AddedOccluders.Add(meshCollider);
                        }
                    }
                }
            }

            void ForceCleanup()
            {
                AssemblyReloadEvents.beforeAssemblyReload -= ForceCleanup;
                EditorApplication.quitting -= ForceCleanup;
                RestorePhysicsComponentsAfterBaking();

                s_TraceDepth = 0;
                s_ReferenceCount = 0;

                foreach (var collider in s_AddedOccluders)
                {
                    if (collider != null)
                        Object.DestroyImmediate(collider);
                }

                s_AddedOccluders.Clear();
                s_ExcludedColliders.Clear();
                s_ExcludedRigidBodies.Clear();
                Physics.SyncTransforms();
            }
        }

        // Unity 2022 has no UnifiedRayTracing package. Reuse the physics-based
        // virtual-offset strategy from Core 14 while preserving the Unity 6
        // incremental baker contract and adjustment-volume behavior.
        sealed class DefaultVirtualOffset : VirtualOffsetBaker
        {
            readonly APVProbeVolumeBaking m_Owner;

            internal DefaultVirtualOffset(APVProbeVolumeBaking owner) => m_Owner = owner;

            const int k_ProbeCountPerStep = 256;

            static readonly Vector3[] k_RayDirections =
            {
                Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
                new Vector3(1, 1, 0).normalized, new Vector3(1, -1, 0).normalized,
                new Vector3(-1, 1, 0).normalized, new Vector3(-1, -1, 0).normalized,
                new Vector3(1, 0, 1).normalized, new Vector3(1, 0, -1).normalized,
                new Vector3(-1, 0, 1).normalized, new Vector3(-1, 0, -1).normalized,
                new Vector3(0, 1, 1).normalized, new Vector3(0, 1, -1).normalized,
                new Vector3(0, -1, 1).normalized, new Vector3(0, -1, -1).normalized,
                new Vector3(1, 1, 1).normalized, new Vector3(1, 1, -1).normalized,
                new Vector3(1, -1, 1).normalized, new Vector3(1, -1, -1).normalized,
                new Vector3(-1, 1, 1).normalized, new Vector3(-1, 1, -1).normalized,
                new Vector3(-1, -1, 1).normalized, new Vector3(-1, -1, -1).normalized,
            };

            NativeArray<Vector3> m_Positions;
            NativeArray<Vector3> m_Offsets;
            Dictionary<int, TouchupsPerCell> m_CellToVolumes;
            VirtualOffsetSettings m_Settings;
            int m_PositionIndex;
            bool m_PhysicsSceneAcquired;

            public override ulong currentStep => (ulong)m_PositionIndex;
            public override ulong stepCount => (ulong)(m_Positions.IsCreated ? m_Positions.Length : 0);
            public override NativeArray<Vector3> offsets => m_Offsets;

            public override void Initialize(ProbeVolumeBakingSet bakingSet, NativeArray<Vector3> probePositions)
            {
                m_Settings = bakingSet.settings.virtualOffsetSettings;
                if (!m_Settings.useVirtualOffset)
                    return;

                m_Owner.m_PhysicsScene.Acquire();
                m_PhysicsSceneAcquired = true;
                try
                {
                    m_Positions = probePositions;
                    m_Offsets = new NativeArray<Vector3>(probePositions.Length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    m_CellToVolumes = m_Owner.GetTouchupsPerCell(out _);
                    m_PositionIndex = 0;
                }
                catch
                {
                    if (m_Offsets.IsCreated)
                        m_Offsets.Dispose();
                    m_Owner.m_PhysicsScene.Release();
                    m_PhysicsSceneAcquired = false;
                    throw;
                }
            }

            public override bool Step()
            {
                int end = Mathf.Min(m_PositionIndex + k_ProbeCountPerStep, m_Positions.Length);
                bool previousBackfaceState = Physics.queriesHitBackfaces;
                bool traceBegun = false;
                try
                {
                    Physics.queriesHitBackfaces = true;
                    m_Owner.m_PhysicsScene.BeginTrace();
                    traceBegun = true;
                    for (; m_PositionIndex < end; ++m_PositionIndex)
                        m_Offsets[m_PositionIndex] = ComputeOffset(m_PositionIndex);
                }
                finally
                {
                    if (traceBegun)
                        m_Owner.m_PhysicsScene.EndTrace();
                    Physics.queriesHitBackfaces = previousBackfaceState;
                }

                return true;
            }

            Vector3 ComputeOffset(int index)
            {
                Vector3 position = m_Positions[index];
                float searchMultiplier = m_Settings.searchMultiplier;
                float originBias = m_Settings.rayOriginBias;
                float geometryBias = m_Settings.outOfGeoOffset;
                int collisionMask = m_Settings.collisionMask;

                Vector3Int cellPosition = m_Owner.m_Layout.PositionToCell(position);
                cellPosition.Clamp(m_Owner.minCellPosition, m_Owner.maxCellPosition);
                int cellIndex = m_Owner.PosToIndex(cellPosition);
                if (m_CellToVolumes.TryGetValue(cellIndex, out var volumes))
                {
                    foreach (var (touchup, obb, center, offset) in volumes.appliers)
                        if (touchup.ContainsPoint(obb, center, position))
                            return offset;

                    foreach (var (touchup, obb, center) in volumes.overriders)
                    {
                        if (!touchup.ContainsPoint(obb, center, position))
                            continue;
                        originBias = touchup.rayOriginBias;
                        geometryBias = touchup.geometryBias;
                        break;
                    }
                }

                int subdivision = m_Owner.m_BakingBatch.GetSubdivLevelAt(position);
                float searchDistance = searchMultiplier * APVProbeReferenceVolume.CellSize(subdivision)
                    * m_Owner.m_Layout.minBrickSize / ProbeBrickPoolBase.brickCellCount;
                if (searchDistance <= 0.0f)
                    return Vector3.zero;

                float bestDistance = float.MaxValue;
                Vector3 bestDirection = Vector3.zero;
                foreach (var direction in k_RayDirections)
                {
                    Vector3 origin = position + direction * originBias;
                    if (!Physics.Raycast(origin, direction, out var hit, searchDistance, collisionMask, QueryTriggerInteraction.Ignore))
                        continue;

                    // A front-face hit proves that the probe is outside the
                    // closed surface. Back-face hits identify an escape path.
                    if (Vector3.Dot(direction, hit.normal) < 0.0f)
                        return Vector3.zero;

                    if (hit.distance < bestDistance)
                    {
                        bestDistance = hit.distance;
                        bestDirection = direction;
                    }
                }

                return bestDistance < float.MaxValue
                    ? bestDirection * (bestDistance * 1.05f + geometryBias)
                    : Vector3.zero;
            }

            public override void Dispose()
            {
                if (m_Offsets.IsCreated)
                    m_Offsets.Dispose();
                if (m_PhysicsSceneAcquired)
                {
                    m_Owner.m_PhysicsScene.Release();
                    m_PhysicsSceneAcquired = false;
                }
            }
        }
#endif

        internal void RecomputeVOForDebugOnly()
        {
            var prv = volume;
            if (prv.perSceneDataList.Count == 0)
                return;

            SetBakingContext(prv.perSceneDataList);

            if (!m_BakingSet.HasBeenBaked())
                return;

            globalBounds = prv.globalBounds;
            CellCountInDirections(out minCellPosition, out maxCellPosition, prv.MaxBrickSize(), prv.ProbeOffset());
            cellCount = maxCellPosition + Vector3Int.one - minCellPosition;

            m_BakingBatch = new BakingBatch(cellCount, volume);
            ModifyProfileFromLoadedData(m_BakingSet);

            var positionList = new NativeList<Vector3>(Allocator.Persistent);
            Dictionary<int, int> positionToIndex = new();
            foreach (var cell in volume.cells.Values)
            {
                var bakingCell = ConvertCellToBakingCell(cell.desc, cell.data);

                int numProbes = bakingCell.probePositions.Length;
                int uniqueIndex = positionToIndex.Count;
                var indices = new int[numProbes];

                // DeduplicateProbePositions
                for (int i = 0; i < numProbes; i++)
                {
                    var pos = bakingCell.probePositions[i];
                    int brickSubdiv = bakingCell.bricks[i / 64].subdivisionLevel;
                    int probeHash = m_BakingBatch.GetProbePositionHash(pos);

                    if (positionToIndex.TryGetValue(probeHash, out var index))
                    {
                        indices[i] = index;
                        int oldBrickLevel = m_BakingBatch.uniqueBrickSubdiv[probeHash];
                        if (brickSubdiv < oldBrickLevel)
                            m_BakingBatch.uniqueBrickSubdiv[probeHash] = brickSubdiv;
                    }
                    else
                    {
                        positionToIndex[probeHash] = uniqueIndex;
                        indices[i] = uniqueIndex;
                        m_BakingBatch.uniqueBrickSubdiv[probeHash] = brickSubdiv;
                        positionList.Add(pos);
                        uniqueIndex++;
                    }
                }

                bakingCell.probeIndices = indices;
                m_BakingBatch.cells.Add(bakingCell);

                // We need to force rebuild debug stuff.
                cell.debugProbes = null;
            }

            VirtualOffsetBaker job = virtualOffsetOverride ?? new DefaultVirtualOffset(this);
            job.Initialize(m_BakingSet, positionList.AsArray());

            while (job.currentStep < job.stepCount)
                job.Step();

            foreach (var cell in m_BakingBatch.cells)
            {
                int numProbes = cell.probePositions.Length;
                for (int i = 0; i < numProbes; ++i)
                {
                    int j = cell.probeIndices[i];
                    cell.offsetVectors[i] = job.offsets[j];
                }
            }

            job.Dispose();

            // Unload it all as we are gonna load back with newly written cells.
            foreach (var sceneData in prv.perSceneDataList)
                prv.AddPendingSceneRemoval(sceneData.sceneGUID);

            // Make sure unloading happens.
            prv.PerformPendingOperations();

            // Write back the assets.
            WriteBakingCells(m_BakingBatch.cells.ToArray());
            m_BakingBatch = null;

            foreach (var data in prv.perSceneDataList)
                data.ResolveCellData();

            // We can now finally reload.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            foreach (var sceneData in prv.perSceneDataList)
            {
                prv.AddPendingSceneLoading(sceneData.sceneGUID, sceneData.serializedBakingSet);
            }

            prv.PerformPendingOperations();
        }
    }

    internal partial class APVProbeVolumeBaking
    {
        struct TouchupsPerCell
        {
            public List<(ProbeAdjustmentVolume touchup, TProbeReferenceVolumeBase.Volume obb, Vector3 center, Vector3 offset)> appliers;
            public List<(ProbeAdjustmentVolume touchup, TProbeReferenceVolumeBase.Volume obb, Vector3 center)> overriders;
        }

        Dictionary<int, TouchupsPerCell> GetTouchupsPerCell(out bool hasAppliers)
        {
            hasAppliers = false;
            var adjustmentVolumes = s_AdjustmentVolumes != null ? s_AdjustmentVolumes : GetAdjustementVolumes();

            Dictionary<int, TouchupsPerCell> cellToVolumes = new();
            foreach (var adjustment in adjustmentVolumes)
            {
                var volume = adjustment.volume;
                var mode = volume.mode;
                if (mode != ProbeAdjustmentVolume.Mode.ApplyVirtualOffset && mode != ProbeAdjustmentVolume.Mode.OverrideVirtualOffsetSettings)
                    continue;

                hasAppliers |= mode == ProbeAdjustmentVolume.Mode.ApplyVirtualOffset;

                Vector3Int min = Vector3Int.Max(m_Layout.PositionToCell(adjustment.aabb.min), minCellPosition);
                Vector3Int max = Vector3Int.Min(m_Layout.PositionToCell(adjustment.aabb.max), maxCellPosition);

                for (int x = min.x; x <= max.x; x++)
                {
                    for (int y = min.y; y <= max.y; y++)
                    {
                        for (int z = min.z; z <= max.z; z++)
                        {
                            var cell = PosToIndex(new Vector3Int(x, y, z));
                            if (!cellToVolumes.TryGetValue(cell, out var volumes))
                                cellToVolumes[cell] = volumes = new TouchupsPerCell() { appliers = new(), overriders = new() };

                            if (mode == ProbeAdjustmentVolume.Mode.ApplyVirtualOffset)
                                volumes.appliers.Add((volume, adjustment.obb, volume.transform.position, volume.GetVirtualOffset()));
                            else
                                volumes.overriders.Add((volume, adjustment.obb, volume.transform.position));
                        }

                    }
                }
            }

            return cellToVolumes;
        }

        void DoApplyVirtualOffsetsFromAdjustmentVolumes(NativeArray<Vector3> positions, NativeArray<Vector3> offsets, Dictionary<int, TouchupsPerCell> cellToVolumes)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                var cellPos = m_Layout.PositionToCell(positions[i]);
                cellPos.Clamp(minCellPosition, maxCellPosition);
                int cellIndex = PosToIndex(cellPos);
                if (cellToVolumes.TryGetValue(cellIndex, out var volumes))
                {
                    foreach (var (touchup, obb, center, offset) in volumes.appliers)
                    {
                        if (touchup.ContainsPoint(obb, center, positions[i]))
                        {
                            offsets[i] = offset;
                            break;
                        }
                    }
                }
            }
        }

        enum InstanceFlags
        {
            DIRECT_RAY_VIS_MASK = 1,
            INDIRECT_RAY_VIS_MASK = 2,
            SHADOW_RAY_VIS_MASK = 4,
        }

        private static uint GetInstanceMask(ShadowCastingMode shadowMode)
        {
            uint instanceMask = 0u;

            if (shadowMode != ShadowCastingMode.Off)
                instanceMask |= (uint)InstanceFlags.SHADOW_RAY_VIS_MASK;

            if (shadowMode != ShadowCastingMode.ShadowsOnly)
            {
                instanceMask |= (uint)InstanceFlags.DIRECT_RAY_VIS_MASK;
                instanceMask |= (uint)InstanceFlags.INDIRECT_RAY_VIS_MASK;
            }

            return instanceMask;
        }

        static uint[] GetMaterialIndices(Renderer renderer)
        {
            int submeshCount = 1;
            var meshFilter = renderer.GetComponent<MeshFilter>();
            if (meshFilter)
                submeshCount = renderer.GetComponent<MeshFilter>().sharedMesh.subMeshCount;

            uint[] matIndices = new uint[submeshCount];
            for (int i = 0; i < matIndices.Length; ++i)
            {
                if (i < renderer.sharedMaterials.Length && renderer.sharedMaterials[i] != null)
                    matIndices[i] = (uint)renderer.sharedMaterials[i].GetInstanceID();
                else
                    matIndices[i] = 0;
            }

            return matIndices;
        }
    }
}
