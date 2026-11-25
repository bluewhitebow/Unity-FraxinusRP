using static UnityEngine.Rendering.AdaptiveProbeVolumes;
using System;
using System.Runtime.InteropServices;
using Unity.Collections;
#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
using UnityEngine.Rendering.UnifiedRayTracing;
using UnityEngine.Rendering.Sampling;
#endif

namespace UnityEngine.Rendering
{
    public partial class AdaptiveProbeVolumes
    {
        /// <summary>
        /// Rendering Layer baker
        /// </summary>
        internal abstract class RenderingLayerBaker : IDisposable
        {
            /// <summary>The current baking step.</summary>
            public abstract ulong currentStep { get; }
            /// <summary>The total amount of step.</summary>
            public abstract ulong stepCount { get; }

            /// <summary>Array storing the rendering layer mask per probe. Only the first 4 bits are used.</summary>
            public abstract NativeArray<uint> renderingLayerMasks { get; }

            /// <summary>
            /// This is called before the start of baking to allow allocating necessary resources.
            /// </summary>
            /// <param name="bakingSet">The baking set that is currently baked.</param>
            /// <param name="probePositions">The probe positions.</param>
            public abstract void Initialize(ProbeVolumeBakingSet bakingSet, NativeArray<Vector3> probePositions);

            /// <summary>
            /// Run a baking step. Baking is considered done when currentStep property equals stepCount.
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
        class DefaultRenderingLayer : RenderingLayerBaker
        {
            readonly APVProbeVolumeBaking m_Owner;

            internal DefaultRenderingLayer(APVProbeVolumeBaking owner) => m_Owner = owner;

            const int k_MaxProbeCountPerBatch = 65535 * 64;

            static readonly int _ProbePositions = Shader.PropertyToID("_ProbePositions");
            static readonly int _LayerMasks = Shader.PropertyToID("_LayerMasks");
            static readonly int _RenderingLayerMasks = Shader.PropertyToID("_RenderingLayerMasks");
            static readonly int _SobolBuffer = Shader.PropertyToID("_SobolMatricesBuffer");

            int batchIndex, batchCount;
            Vector4 regionMasks;

            // Input data
            NativeArray<Vector3> probePositions;

            // Output buffers
            GraphicsBuffer layerMaskBuffer;
            NativeArray<uint> layerMask;

            public override NativeArray<uint> renderingLayerMasks => layerMask;

            CommandBuffer cmd;
            AccelStructAdapter m_AccelerationStructure;
            GraphicsBuffer scratchBuffer;
            GraphicsBuffer probePositionsBuffer;
            GraphicsBuffer sobolBuffer;

            public override ulong currentStep => (ulong)batchIndex;
            public override ulong stepCount => (ulong)batchCount;

            public override void Initialize(ProbeVolumeBakingSet bakingSet, NativeArray<Vector3> positions)
            {
                // Divide the job into batches to reduce memory usage.
                batchCount = CoreUtils.DivRoundUp(bakingSet.useRenderingLayers ? positions.Length : 0, k_MaxProbeCountPerBatch);
                batchIndex = 0;

                probePositions = positions;
                if (batchCount == 0)
                    return;

                regionMasks = Vector4.zero;
                var configuredMasks = bakingSet.renderingLayerMasks;
                if (configuredMasks == null || configuredMasks.Length == 0)
                    regionMasks.x = Unity.Mathematics.math.asfloat(uint.MaxValue);
                else
                    for (int i = 0; i < configuredMasks.Length && i < APVDefinitions.probeMaxRegionCount; i++)
                        regionMasks[i] = Unity.Mathematics.math.asfloat(configuredMasks[i].mask);

                // Allocate array storing results
                layerMask = new NativeArray<uint>(probePositions.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                // Create acceleration structure
                m_AccelerationStructure = BuildAccelerationStructure();

                int batchSize = Mathf.Min(k_MaxProbeCountPerBatch, probePositions.Length);
                probePositionsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, batchSize, Marshal.SizeOf<Vector3>());
                layerMaskBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, batchSize, Marshal.SizeOf<uint>());
                scratchBuffer = RayTracingHelper.CreateScratchBufferForBuildAndDispatch(m_AccelerationStructure.GetAccelerationStructure(), m_Owner.s_TracingContext.shaderRL, (uint)batchSize, 1, 1);

                int sobolBufferSize = (int)(SobolData.SobolDims * SobolData.SobolSize);
                sobolBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sobolBufferSize, Marshal.SizeOf<uint>());
                sobolBuffer.SetData(SobolData.SobolMatrices);

                cmd = new CommandBuffer();
                m_AccelerationStructure.Build(cmd, ref scratchBuffer);
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Clear();
            }

            AccelStructAdapter BuildAccelerationStructure()
            {
                var accelStruct = m_Owner.s_TracingContext.CreateAccelerationStructure();
                var contributors = m_Owner.m_BakingBatch.contributors;

                foreach (var renderer in contributors.renderers)
                {
                    var mesh = renderer.component.GetComponent<MeshFilter>().sharedMesh;
                    if (mesh == null)
                        continue;

                    int subMeshCount = mesh.subMeshCount;
                    var matIndices = new uint[subMeshCount];
                    Array.Fill(matIndices, renderer.component.renderingLayerMask); // repurpose the material id as we don't need it here
                    var perSubMeshMask = new uint[subMeshCount];
                    Array.Fill(perSubMeshMask, GetInstanceMask(renderer.component.shadowCastingMode));
                    accelStruct.AddInstance(renderer.component.GetInstanceID(), renderer.component, perSubMeshMask, matIndices, 1);
                }

                foreach (var terrain in contributors.terrains)
                {
                    uint mask = GetInstanceMask(terrain.component.shadowCastingMode);
                    uint materialID = terrain.component.renderingLayerMask; // repurpose the material id as we don't need it here
                    accelStruct.AddInstance(terrain.component.GetInstanceID(), terrain.component, new uint[1] { mask }, new uint[1] { materialID }, 1);
                }

                return accelStruct;
            }

            public override bool Step()
            {
                if (currentStep >= stepCount)
                    return true;

                var shader = m_Owner.s_TracingContext.shaderRL;

                int batchOffset = batchIndex * k_MaxProbeCountPerBatch;
                int batchSize = Mathf.Min(probePositions.Length - batchOffset, k_MaxProbeCountPerBatch);
                cmd.SetBufferData(probePositionsBuffer, probePositions.GetSubArray(batchOffset, batchSize));

                m_AccelerationStructure.Bind(cmd, "_AccelStruct", shader);
                shader.SetVectorParam(cmd, _RenderingLayerMasks, regionMasks);
                shader.SetBufferParam(cmd, _ProbePositions, probePositionsBuffer);
                shader.SetBufferParam(cmd, _LayerMasks, layerMaskBuffer);
                shader.SetBufferParam(cmd, _SobolBuffer, sobolBuffer);

                shader.Dispatch(cmd, scratchBuffer, (uint)batchSize, 1, 1);
                batchIndex++;

                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                FetchResults(batchOffset, batchSize);

                return true;
            }

            void FetchResults(int batchOffset, int batchSize)
            {
                var batchLayers = layerMask.GetSubArray(batchOffset, batchSize);
                var req = AsyncGPUReadback.RequestIntoNativeArray(ref batchLayers, layerMaskBuffer, batchSize * sizeof(uint), 0);

                // TODO: use double buffering to hide readback latency
                req.WaitForCompletion();
            }

            public override void Dispose()
            {
                if (m_AccelerationStructure == null)
                    return;

                cmd.Dispose();

                scratchBuffer?.Dispose();
                probePositionsBuffer.Dispose();
                m_AccelerationStructure.Dispose();
                sobolBuffer?.Dispose();

                layerMaskBuffer.Dispose();
                layerMask.Dispose();
            }
        }
#else
        // Unity 2022 fallback for the UnifiedRayTracing pass. It traces the
        // same 32 uniformly distributed directions through the physics scene
        // and selects the region receiving the most hits.
        sealed class DefaultRenderingLayer : RenderingLayerBaker
        {
            readonly APVProbeVolumeBaking m_Owner;

            internal DefaultRenderingLayer(APVProbeVolumeBaking owner) => m_Owner = owner;

            const int k_ProbesPerStep = 128;
            const int k_SampleCount = 32;

            NativeArray<Vector3> m_Positions;
            NativeArray<uint> m_LayerMasks;
            uint[] m_RegionMasks;
            int m_ProbeIndex;
            bool m_PhysicsSceneAcquired;

            public override ulong currentStep => (ulong)m_ProbeIndex;
            public override ulong stepCount => (ulong)(m_Positions.IsCreated ? m_Positions.Length : 0);
            public override NativeArray<uint> renderingLayerMasks => m_LayerMasks;

            public override void Initialize(ProbeVolumeBakingSet bakingSet, NativeArray<Vector3> probePositions)
            {
                if (!bakingSet.useRenderingLayers)
                    return;

                m_Owner.m_PhysicsScene.Acquire();
                m_PhysicsSceneAcquired = true;
                try
                {
                    m_Positions = probePositions;
                    m_LayerMasks = new NativeArray<uint>(probePositions.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    var configuredMasks = bakingSet.renderingLayerMasks;
                    m_RegionMasks = new uint[Mathf.Min(configuredMasks?.Length ?? 0, APVDefinitions.probeMaxRegionCount)];
                    for (int i = 0; i < m_RegionMasks.Length; ++i)
                        m_RegionMasks[i] = configuredMasks[i].mask;
                }
                catch
                {
                    if (m_LayerMasks.IsCreated)
                        m_LayerMasks.Dispose();
                    m_Owner.m_PhysicsScene.Release();
                    m_PhysicsSceneAcquired = false;
                    throw;
                }
            }

            public override bool Step()
            {
                int end = Mathf.Min(m_ProbeIndex + k_ProbesPerStep, m_Positions.Length);
                bool traceBegun = false;
                try
                {
                    m_Owner.m_PhysicsScene.BeginTrace();
                    traceBegun = true;
                    for (; m_ProbeIndex < end; ++m_ProbeIndex)
                        m_LayerMasks[m_ProbeIndex] = TraceMask(m_Positions[m_ProbeIndex], m_ProbeIndex);
                }
                finally
                {
                    if (traceBegun)
                        m_Owner.m_PhysicsScene.EndTrace();
                }
                return true;
            }

            uint TraceMask(Vector3 position, int seed)
            {
                var hitCounts = new int[APVDefinitions.probeMaxRegionCount];
                float goldenAngle = Mathf.PI * (3.0f - Mathf.Sqrt(5.0f));
                for (int sample = 0; sample < k_SampleCount; ++sample)
                {
                    float y = 1.0f - 2.0f * ((sample + 0.5f) / k_SampleCount);
                    float radius = Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - y * y));
                    float angle = goldenAngle * (sample + seed * 0.61803398875f);
                    var direction = new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);
                    if (!Physics.Raycast(position, direction, out var hit, Mathf.Infinity,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                        continue;

                    var renderer = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>();
                    uint objectMask = renderer != null ? renderer.renderingLayerMask : uint.MaxValue;
                    for (int region = 0; region < hitCounts.Length && region < m_RegionMasks.Length; ++region)
                        if ((m_RegionMasks[region] & objectMask) != 0)
                            ++hitCounts[region];
                }

                int bestRegion = 0;
                for (int region = 1; region < hitCounts.Length; ++region)
                    if (hitCounts[region] > hitCounts[bestRegion])
                        bestRegion = region;

                return hitCounts[bestRegion] == 0 ? 0xFu : 1u << bestRegion;
            }

            public override void Dispose()
            {
                if (m_LayerMasks.IsCreated)
                    m_LayerMasks.Dispose();
                if (m_PhysicsSceneAcquired)
                {
                    m_Owner.m_PhysicsScene.Release();
                    m_PhysicsSceneAcquired = false;
                }
            }
        }
#endif
    }
}
