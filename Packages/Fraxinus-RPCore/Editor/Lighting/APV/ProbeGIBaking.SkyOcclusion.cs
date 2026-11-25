using static UnityEngine.Rendering.AdaptiveProbeVolumes;
using System;
using System.Runtime.InteropServices;
#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
using UnityEngine.Rendering.Sampling;
using UnityEngine.Rendering.UnifiedRayTracing;
#endif
using Unity.Collections;

namespace UnityEngine.Rendering
{
    public partial class AdaptiveProbeVolumes
    {
        /// <summary>
        /// Sky occlusion baker
        /// </summary>
        public abstract class SkyOcclusionBaker : IDisposable
        {
            /// <summary>The current baking step.</summary>
            public abstract ulong currentStep { get; }
            /// <summary>The total amount of step.</summary>
            public abstract ulong stepCount { get; }

            /// <summary>Array storing the sky occlusion per probe. Expects Layout DC, x, y, z.</summary>
            public abstract NativeArray<Vector4> occlusion { get; }
            /// <summary>Array storing the sky shading direction per probe.</summary>
            public abstract NativeArray<Vector3> shadingDirections { get; }

            /// <summary>
            /// This is called before the start of baking to allow allocating necessary resources.
            /// </summary>
            /// <param name="bakingSet">The baking set that is currently baked.</param>
            /// <param name="probePositions">The probe positions.</param>
            public abstract void Initialize(ProbeVolumeBakingSet bakingSet, NativeArray<Vector3> probePositions);

            /// <summary>
            /// Run a step of sky occlusion baking. Baking is considered done when currentStep property equals stepCount.
            /// </summary>
            /// <returns>Return false if bake failed and should be stopped.</returns>
            public abstract bool Step();

            /// <summary>
            /// Performs necessary tasks to free allocated resources.
            /// </summary>
            public abstract void Dispose();

            internal NativeArray<uint> encodedDirections;
            internal void Encode(APVProbeReferenceVolume volume) => encodedDirections = EncodeShadingDirection(shadingDirections, volume);

#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
            const int k_MaxProbeCountPerBatch = 65535;
#endif
            static readonly int _SkyShadingPrecomputedDirection = Shader.PropertyToID("_SkyShadingPrecomputedDirection");
            static readonly int _SkyShadingDirections = Shader.PropertyToID("_SkyShadingDirections");
            static readonly int _SkyShadingIndices = Shader.PropertyToID("_SkyShadingIndices");
            static readonly int _ProbeCount = Shader.PropertyToID("_ProbeCount");

            internal static NativeArray<uint> EncodeShadingDirection(NativeArray<Vector3> directions, APVProbeReferenceVolume volume)
            {
#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
                var cs = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ProbeVolumeBakingResources>()?.skyOcclusionCS;
                int kernel = cs.FindKernel("EncodeShadingDirection");


                var precomputedShadingDirections = volume.GetRuntimeResources<APVRuntimeResource>().SkyPrecomputedDirections;

                int probeCount = directions.Length;
                int batchSize = Mathf.Min(k_MaxProbeCountPerBatch, probeCount);
                int batchCount = CoreUtils.DivRoundUp(probeCount, k_MaxProbeCountPerBatch);

                var directionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, batchSize, Marshal.SizeOf<Vector3>());
                var encodedBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, batchSize, Marshal.SizeOf<uint>());

                var directionResults = new NativeArray<uint>(probeCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                {
                    int batchOffset = batchIndex * k_MaxProbeCountPerBatch;
                    int probeInBatch = Mathf.Min(probeCount - batchOffset, k_MaxProbeCountPerBatch);

                    directionBuffer.SetData(directions, batchOffset, 0, probeInBatch);

                    cs.SetBuffer(kernel, _SkyShadingPrecomputedDirection, precomputedShadingDirections);
                    cs.SetBuffer(kernel, _SkyShadingDirections, directionBuffer);
                    cs.SetBuffer(kernel, _SkyShadingIndices, encodedBuffer);

                    cs.SetInt(_ProbeCount, probeInBatch);
                    cs.Dispatch(kernel, CoreUtils.DivRoundUp(probeCount, 64), 1, 1);

                    var batchResult = directionResults.GetSubArray(batchOffset, probeInBatch);
                    AsyncGPUReadback.RequestIntoNativeArray(ref batchResult, encodedBuffer, probeInBatch * sizeof(uint), 0).WaitForCompletion();
                }

                directionBuffer.Dispose();
                encodedBuffer.Dispose();

                return directionResults;
#else
                var directionResults = new NativeArray<uint>(directions.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < directions.Length; ++i)
                    directionResults[i] = directions[i].sqrMagnitude > 1e-8f
                        ? EncodeSkyShadingDirection(directions[i].normalized)
                        : 255u;
                return directionResults;
#endif
            }

            internal static uint EncodeSkyShadingDirection(Vector3 direction)
            {
                var precomputedDirections = APVProbeVolumeConstantRuntimeResources.GetSkySamplingDirections();

                uint indexMax = 255;
                float bestDot = -10.0f;
                uint bestIndex = 0;

                for (uint index = 0; index < indexMax; index++)
                {
                    float currentDot = Vector3.Dot(direction, precomputedDirections[index]);
                    if (currentDot > bestDot)
                    {
                        bestDot = currentDot;
                        bestIndex = index;
                    }
                }

                return bestIndex;
            }
        }
    }

    internal partial class APVProbeVolumeBaking
    {

#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
        class DefaultSkyOcclusion : SkyOcclusionBaker
        {
            readonly APVProbeVolumeBaking m_Owner;

            internal DefaultSkyOcclusion(APVProbeVolumeBaking owner) => m_Owner = owner;

            const int k_MaxProbeCountPerBatch = 128 * 1024;
            const float k_SkyOcclusionOffsetRay = 0.015f;
            const int k_SampleCountPerStep = 16;

            static readonly int _SampleCount = Shader.PropertyToID("_SampleCount");
            static readonly int _SampleId = Shader.PropertyToID("_SampleId");
            static readonly int _MaxBounces = Shader.PropertyToID("_MaxBounces");
            static readonly int _OffsetRay = Shader.PropertyToID("_OffsetRay");
            static readonly int _ProbePositions = Shader.PropertyToID("_ProbePositions");
            static readonly int _SkyOcclusionOut = Shader.PropertyToID("_SkyOcclusionOut");
            static readonly int _SkyShadingOut = Shader.PropertyToID("_SkyShadingOut");
            static readonly int _AverageAlbedo = Shader.PropertyToID("_AverageAlbedo");
            static readonly int _BackFaceCulling = Shader.PropertyToID("_BackFaceCulling");
            static readonly int _BakeSkyShadingDirection = Shader.PropertyToID("_BakeSkyShadingDirection");
            static readonly int _SobolBuffer = Shader.PropertyToID("_SobolMatricesBuffer");

            int skyOcclusionBackFaceCulling;
            float skyOcclusionAverageAlbedo;
            int probeCount;
            ulong step;

            // Input data
            NativeArray<Vector3> probePositions;
            int currentJob;
            int sampleIndex;
            int batchIndex;

            public BakeJob[] jobs;

            // Output buffers
            GraphicsBuffer occlusionOutputBuffer;
            GraphicsBuffer shadingDirectionBuffer;
            NativeArray<Vector4> occlusionResults;
            NativeArray<Vector3> directionResults;

            public override NativeArray<Vector4> occlusion => occlusionResults;
            public override NativeArray<Vector3> shadingDirections => directionResults;

            AccelStructAdapter m_AccelerationStructure;
            StandaloneAPVMaterialResources m_Materials;
            GraphicsBuffer scratchBuffer;
            GraphicsBuffer probePositionsBuffer;
            GraphicsBuffer sobolBuffer;

            public override ulong currentStep => step;
            public override ulong stepCount => (ulong)probeCount;

            public override void Initialize(ProbeVolumeBakingSet bakingSet, NativeArray<Vector3> positions)
            {
                skyOcclusionAverageAlbedo = bakingSet.skyOcclusionAverageAlbedo;
                skyOcclusionBackFaceCulling = 0; // see PR #40707

                currentJob = 0;
                sampleIndex = 0;
                batchIndex = 0;

                step = 0;
                probeCount = bakingSet.skyOcclusion ? positions.Length : 0;
                probePositions = positions;

                if (stepCount == 0)
                    return;

                // Allocate array storing results
                occlusionResults = new NativeArray<Vector4>(probeCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                if (bakingSet.skyOcclusionShadingDirection)
                    directionResults = new NativeArray<Vector3>(probeCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                // Create acceleration structure
                m_Materials = new StandaloneAPVMaterialResources(opacityOnly: true);
                BuildAccelerationStructure();
                m_Materials.Build();
                var skyOcclusionShader = m_Owner.s_TracingContext.shaderSO;
                bool skyDirection = shadingDirections.IsCreated;

                int batchSize = Mathf.Min(k_MaxProbeCountPerBatch, probeCount);
                probePositionsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, batchSize, Marshal.SizeOf<Vector3>());
                occlusionOutputBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, batchSize, Marshal.SizeOf<Vector4>());
                shadingDirectionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, skyDirection ? batchSize : 1, Marshal.SizeOf<Vector3>());
                scratchBuffer = RayTracingHelper.CreateScratchBufferForBuildAndDispatch(m_AccelerationStructure.GetAccelerationStructure(), skyOcclusionShader, (uint)batchSize, 1, 1);

                var buildCmd = new CommandBuffer();
                m_AccelerationStructure.Build(buildCmd, ref scratchBuffer);
                Graphics.ExecuteCommandBuffer(buildCmd);
                buildCmd.Dispose();

                int sobolBufferSize = (int)(SobolData.SobolDims * SobolData.SobolSize);
                sobolBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sobolBufferSize, Marshal.SizeOf<uint>());
                sobolBuffer.SetData(SobolData.SobolMatrices);
            }

            void BuildAccelerationStructure()
            {
                // Retain the resource immediately so initialization failures
                // cannot orphan an acceleration structure.
                m_AccelerationStructure = m_Owner.s_TracingContext.CreateAccelerationStructure();
                var contributors = m_Owner.m_BakingBatch.contributors;

                foreach (var renderer in contributors.renderers)
                {
                    if (!m_Owner.s_TracingContext.TryGetMeshForAccelerationStructure(renderer.component, out var mesh))
                        continue;

                    int subMeshCount = mesh.subMeshCount;
                    var matIndices = new uint[subMeshCount];
                    var materials = renderer.component.sharedMaterials;
                    for (int i = 0; i < subMeshCount; ++i)
                    {
                        var material = materials.Length != 0 ? materials[Mathf.Min(i, materials.Length - 1)] : null;
                        matIndices[i] = material != null ? m_Materials.GetMaterialID(material) : 0u;
                    }
                    var perSubMeshMask = new uint[subMeshCount];
                    Array.Fill(perSubMeshMask, GetInstanceMask(renderer.component.shadowCastingMode));

                    m_AccelerationStructure.AddInstance(renderer.component.GetInstanceID(), renderer.component, perSubMeshMask, matIndices, 1);
                }

                foreach (var terrain in contributors.terrains)
                {
                    uint mask = GetInstanceMask(terrain.component.shadowCastingMode);
                    m_AccelerationStructure.AddInstance(terrain.component.GetInstanceID(), terrain.component, new uint[1] { mask }, new uint[1] { 0 }, 1);
                }
            }

            public override bool Step()
            {
                if (currentStep >= stepCount)
                    return true;

                ref var job = ref jobs[currentJob];
                if (job.probeCount == 0)
                {
                    currentJob++;
                    return true;
                }

                var cmd = new CommandBuffer();
                var skyOccShader = m_Owner.s_TracingContext.shaderSO;

                // Divide the job into batches of 128k probes to reduce memory usage.
                int batchCount = CoreUtils.DivRoundUp(job.probeCount, k_MaxProbeCountPerBatch);

                int batchOffset = batchIndex * k_MaxProbeCountPerBatch;
                int batchSize = Mathf.Min(job.probeCount - batchOffset, k_MaxProbeCountPerBatch);

                if (sampleIndex == 0)
                {
                    cmd.SetBufferData(probePositionsBuffer, probePositions.GetSubArray(job.startOffset + batchOffset, batchSize));
                }

                m_Owner.s_TracingContext.BindSamplingTextures(cmd);
                m_AccelerationStructure.Bind(cmd, "_AccelStruct", skyOccShader);
                m_Materials.Bind(cmd, skyOccShader);

                skyOccShader.SetIntParam(cmd, _BakeSkyShadingDirection, shadingDirections.IsCreated ? 1 : 0);
                skyOccShader.SetIntParam(cmd, _BackFaceCulling, skyOcclusionBackFaceCulling);
                skyOccShader.SetFloatParam(cmd, _AverageAlbedo, skyOcclusionAverageAlbedo);

                skyOccShader.SetFloatParam(cmd, _OffsetRay, k_SkyOcclusionOffsetRay);
                skyOccShader.SetBufferParam(cmd, _ProbePositions, probePositionsBuffer);
                skyOccShader.SetBufferParam(cmd, _SkyOcclusionOut, occlusionOutputBuffer);
                skyOccShader.SetBufferParam(cmd, _SkyShadingOut, shadingDirectionBuffer);

                skyOccShader.SetBufferParam(cmd, _SobolBuffer, sobolBuffer);

                skyOccShader.SetIntParam(cmd, _SampleCount, job.skyOcclusionBakingSamples);
                skyOccShader.SetIntParam(cmd, _MaxBounces, job.skyOcclusionBakingBounces);

                // Sample multiple paths in one step
                for (int i = 0; i < k_SampleCountPerStep; i++)
                {
                    skyOccShader.SetIntParam(cmd, _SampleId, sampleIndex);
                    skyOccShader.Dispatch(cmd, scratchBuffer, (uint)batchSize, 1, 1);
                    sampleIndex++;

                    Graphics.ExecuteCommandBuffer(cmd);
                    cmd.Clear();

                    // If we computed all the samples for this batch, continue with the next one
                    if (sampleIndex >= job.skyOcclusionBakingSamples)
                    {
                        FetchResults(in job, batchOffset, batchSize);

                        batchIndex++;
                        sampleIndex = 0;
                        if (batchIndex >= batchCount)
                        {
                            currentJob++;
                            batchIndex = 0;
                        }

                        // Progress bar
                        step += (ulong)batchSize;
                        break;
                    }
                }

                cmd.Dispose();
                return true;
            }

            void FetchResults(in BakeJob job, int batchOffset, int batchSize)
            {
                var batchOcclusionResults = occlusionResults.GetSubArray(job.startOffset + batchOffset, batchSize);
                var req1 = AsyncGPUReadback.RequestIntoNativeArray(ref batchOcclusionResults, occlusionOutputBuffer, batchSize * 4 * sizeof(float), 0);

                if (directionResults.IsCreated)
                {
                    var batchDirectionResults = directionResults.GetSubArray(job.startOffset + batchOffset, batchSize);
                    var req2 = AsyncGPUReadback.RequestIntoNativeArray(ref batchDirectionResults, shadingDirectionBuffer, batchSize * 3 * sizeof(float), 0);

                    req2.WaitForCompletion();
                }

                // TODO: use double buffering to hide readback latency
                req1.WaitForCompletion();
            }

            public override void Dispose()
            {
                occlusionOutputBuffer?.Dispose();
                shadingDirectionBuffer?.Dispose();

                scratchBuffer?.Dispose();
                probePositionsBuffer?.Dispose();
                sobolBuffer?.Dispose();

                if (occlusionResults.IsCreated)
                    occlusionResults.Dispose();
                if (directionResults.IsCreated)
                    directionResults.Dispose();

                m_AccelerationStructure?.Dispose();
                m_AccelerationStructure = null;
                m_Materials?.Dispose();
                m_Materials = null;
                occlusionOutputBuffer = null;
                shadingDirectionBuffer = null;
                scratchBuffer = null;
                probePositionsBuffer = null;
                sobolBuffer = null;
            }
        }
#else
        // Physics-backed sky visibility for Unity 2022. The integration and
        // SH layout match the Unity 6 shader; tracing uses the 2022 physics
        // scene because UnifiedRayTracing is not available on this editor.
        sealed class DefaultSkyOcclusion : SkyOcclusionBaker
        {
            readonly APVProbeVolumeBaking m_Owner;

            internal DefaultSkyOcclusion(APVProbeVolumeBaking owner) => m_Owner = owner;

            const int k_ProbesPerStep = 8;
            const float k_SHBasis0 = 0.28209479177387814347f;
            const float k_SHBasis1 = 0.48860251190291992159f;

            NativeArray<Vector3> m_Positions;
            NativeArray<Vector4> m_Occlusion;
            NativeArray<Vector3> m_ShadingDirections;
            int m_ProbeIndex;
            int m_SampleCount;
            bool m_BakeDirection;
            bool m_PhysicsSceneAcquired;

            public BakeJob[] jobs;

            public override ulong currentStep => (ulong)m_ProbeIndex;
            public override ulong stepCount => (ulong)(m_Positions.IsCreated ? m_Positions.Length : 0);
            public override NativeArray<Vector4> occlusion => m_Occlusion;
            public override NativeArray<Vector3> shadingDirections => m_ShadingDirections;

            public override void Initialize(ProbeVolumeBakingSet bakingSet, NativeArray<Vector3> probePositions)
            {
                if (!bakingSet.skyOcclusion)
                    return;

                m_Owner.m_PhysicsScene.Acquire();
                m_PhysicsSceneAcquired = true;
                try
                {
                    m_Positions = probePositions;
                    m_SampleCount = Mathf.Max(1, bakingSet.skyOcclusionBakingSamples);
                    m_BakeDirection = bakingSet.skyOcclusionShadingDirection;
                    m_Occlusion = new NativeArray<Vector4>(probePositions.Length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    if (m_BakeDirection)
                        m_ShadingDirections = new NativeArray<Vector3>(probePositions.Length, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                }
                catch
                {
                    if (m_Occlusion.IsCreated) m_Occlusion.Dispose();
                    if (m_ShadingDirections.IsCreated) m_ShadingDirections.Dispose();
                    m_Owner.m_PhysicsScene.Release();
                    m_PhysicsSceneAcquired = false;
                    throw;
                }
            }

            public override bool Step()
            {
                int end = Mathf.Min(m_ProbeIndex + k_ProbesPerStep, m_Positions.Length);
                bool previousBackfaceState = Physics.queriesHitBackfaces;
                bool traceBegun = false;
                try
                {
                    Physics.queriesHitBackfaces = false;
                    m_Owner.m_PhysicsScene.BeginTrace();
                    traceBegun = true;
                    for (; m_ProbeIndex < end; ++m_ProbeIndex)
                        IntegrateProbe(m_ProbeIndex);
                }
                finally
                {
                    if (traceBegun)
                        m_Owner.m_PhysicsScene.EndTrace();
                    Physics.queriesHitBackfaces = previousBackfaceState;
                }
                return true;
            }

            void IntegrateProbe(int probeIndex)
            {
                Vector4 sh = Vector4.zero;
                Vector3 shadingDirection = Vector3.zero;
                float normalization = 4.0f * Mathf.PI / m_SampleCount;
                float goldenAngle = Mathf.PI * (3.0f - Mathf.Sqrt(5.0f));

                for (int sample = 0; sample < m_SampleCount; ++sample)
                {
                    float y = 1.0f - 2.0f * ((sample + 0.5f) / m_SampleCount);
                    float radius = Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - y * y));
                    float angle = goldenAngle * (sample + probeIndex * 0.61803398875f);
                    Vector3 direction = new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);

                    if (Physics.Raycast(m_Positions[probeIndex], direction, Mathf.Infinity,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                        continue;

                    sh.x += normalization * k_SHBasis0;
                    sh.y += direction.x * normalization * k_SHBasis1;
                    sh.z += direction.y * normalization * k_SHBasis1;
                    sh.w += direction.z * normalization * k_SHBasis1;
                    shadingDirection += direction / m_SampleCount;
                }

                Vector3 mainDirection = new Vector3(sh.y, sh.z, sh.w);
                if (mainDirection.sqrMagnitude > 1e-8f)
                {
                    mainDirection.Normalize();
                    float oppositeValue = k_SHBasis0 * sh.x - k_SHBasis1
                        * Vector3.Dot(mainDirection, new Vector3(sh.y, sh.z, sh.w));
                    if (oppositeValue < 0.0f)
                    {
                        float denominator = -k_SHBasis1 * Vector3.Dot(mainDirection,
                            new Vector3(sh.y, sh.z, sh.w));
                        float window = denominator != 0.0f
                            ? Mathf.Clamp01(-(k_SHBasis0 * sh.x) / denominator)
                            : 1.0f;
                        float irradianceWindow = Mathf.Lerp(1.0f, 2.0f / 3.0f, Mathf.Pow(window, 1.125f));
                        sh.y *= window * irradianceWindow;
                        sh.z *= window * irradianceWindow;
                        sh.w *= window * irradianceWindow;
                    }
                }

                m_Occlusion[probeIndex] = sh;
                if (m_BakeDirection)
                    m_ShadingDirections[probeIndex] = shadingDirection;
            }

            public override void Dispose()
            {
                if (m_Occlusion.IsCreated) m_Occlusion.Dispose();
                if (m_ShadingDirections.IsCreated) m_ShadingDirections.Dispose();
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
