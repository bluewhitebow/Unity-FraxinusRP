using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using Object = UnityEngine.Object;

namespace UnityEngine.Rendering.Tests
{
    public class APRTMultiSamplingTests
    {
        [Test]
        public void RepeatedMultiSetAtlasPreparationAndRecordingDoesNotAllocateAfterWarmup()
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new SamplingFixture();
            var sources = new[] { fixture.AddGrid(Vector3.one, Vector3.zero),
                fixture.AddGrid(Vector3.one * 2, Vector3.right * 8) };
            var coordinator = new APRTProbeReferenceVolume();
            using var cmd = new CommandBuffer();
            try
            {
                for (int i = 0; i < 16; ++i)
                {
                    Assert.That(coordinator.PrepareSamplingAtlas(sources), Is.True);
                    cmd.Clear();
                    coordinator.CopyAndBindSamplingAtlas(cmd, sources);
                }
                using var recorder = new APRTAllocationRecorder();
                recorder.Begin();
                for (int i = 0; i < 64; ++i)
                {
                    coordinator.PrepareSamplingAtlas(sources);
                    cmd.Clear();
                    coordinator.CopyAndBindSamplingAtlas(cmd, sources);
                }
                int allocated = recorder.End();
                Assert.That(allocated, Is.Zero);
            }
            finally { coordinator.Cleanup(); }
        }

        [Test]
        public void GPUIndependentGridsKeepLocalIndicesTransformsAndFallbackIsolated()
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new SamplingFixture();
            var first = fixture.AddGrid(new Vector3(3, 0, 0), Vector3.zero);
            var second = fixture.AddGrid(new Vector3(0, 0, 5), new Vector3(8, 1, -2),
                Quaternion.Euler(0, 35, 0), 2);
            var secondCenter = Matrix4x4.TRS(new Vector3(8, 1, -2), Quaternion.Euler(0, 35, 0), Vector3.one * 2)
                .MultiplyPoint3x4(Vector3.one * .5f);
            var result = fixture.Sample(new[] { first, second }, Vector3.one * .5f, secondCenter, Vector3.one * 100);
            AssertGI(result, 0, new Vector3(3, 0, 0));
            AssertGI(result, 1, new Vector3(0, 0, 5));
            Assert.That(result[8], Is.EqualTo(Vector4.zero), "A receiver outside every independent domain must keep ordinary GI fallback available.");
            Assert.That(result[9].w, Is.Zero);
            Assert.That(result[10].w, Is.Zero);
            Assert.That(Shader.GetGlobalInt("_APRTSamplingSetCount"), Is.EqualTo(2));
        }

        [Test]
        public void GPUOverlappingIndependentGridsAverageTheirSuccessfulLighting()
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new SamplingFixture();
            var first = fixture.AddGrid(new Vector3(4, 0, 0), Vector3.zero);
            var second = fixture.AddGrid(new Vector3(0, 2, 0), Vector3.right * .5f);
            var result = fixture.Sample(new[] { first, second }, new Vector3(.75f, .5f, .5f));
            AssertGI(result, 0, new Vector3(2, 1, 0));
            Assert.That(Vector3.Distance(result[1], new Vector3(2, 1, 0)), Is.LessThan(.001f));
            Assert.That(result[3].x, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GPUUnavailableOverlappingSetCannotDarkenAValidSet(bool missingCorner)
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new SamplingFixture();
            var first = fixture.AddGrid(new Vector3(3, 2, 1), Vector3.zero);
            var second = fixture.AddGrid(new Vector3(0, 0, 5), Vector3.right * .5f,
                validity: missingCorner ? 1 : 0);
            if (missingCorner)
            {
                var indices = Enumerable.Range(0, 8).ToArray();
                indices[0] = -1;
                second.gpuBuffers[7].SetData(indices);
            }
            var result = fixture.Sample(new[] { first, second }, new Vector3(.75f, .5f, .5f));
            AssertGI(result, 0, new Vector3(3, 2, 1));
        }

        [Test]
        public void GPUIndependentSetsRetainDifferentLightingScenarioBlendWeights()
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new SamplingFixture();
            var first = fixture.AddGrid(new Vector3(4, 0, 0), Vector3.zero,
                otherColor: new Vector3(0, 4, 0));
            var second = fixture.AddGrid(new Vector3(0, 0, 4), Vector3.right * 8,
                otherColor: Vector3.one * 4);
            fixture.Blend(first, .25f);
            fixture.Blend(second, .75f);
            var result = fixture.Sample(new[] { first, second }, Vector3.one * .5f, new Vector3(8.5f, .5f, .5f));
            AssertGI(result, 0, new Vector3(3, 1, 0));
            AssertGI(result, 1, new Vector3(3, 3, 4));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GPUMixedLightingModelsKeepDiffuseAndEmissionComposition(bool addAmbient)
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new SamplingFixture();
            var diffuse = fixture.AddGrid(new Vector3(4, 0, 0), Vector3.zero, addAmbient: addAmbient);
            var emission = fixture.AddGrid(new Vector3(0, 2, 0), Vector3.right * .5f,
                lightingModel: APRTLightingModel.APRTV);
            var result = fixture.Sample(new[] { diffuse, emission }, new Vector3(.75f, .5f, .5f));
            AssertGI(result, 0, new Vector3(2, 1, 0));
            Assert.That(Vector3.Distance(result[1], new Vector3(2, 0, 0)), Is.LessThan(.001f));
            Assert.That(Vector3.Distance(result[2], new Vector3(0, 1, 0)), Is.LessThan(.001f));
            Assert.That(result[3].x, Is.EqualTo(addAmbient ? 1 : .5f).Within(.001f));
        }

        [Test]
        public void GPUReturningToOneSetClearsTheAggregateBindingAndUsesOnlyTheRemainingSet()
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new SamplingFixture();
            var first = fixture.AddGrid(new Vector3(4, 0, 0), Vector3.zero);
            var second = fixture.AddGrid(new Vector3(0, 2, 0), Vector3.right * .5f);
            AssertGI(fixture.Sample(new[] { first, second }, new Vector3(.75f, .5f, .5f)), 0, new Vector3(2, 1, 0));
            var result = fixture.Sample(new[] { first }, new Vector3(.75f, .5f, .5f));
            Assert.That(Shader.GetGlobalInt("_APRTSamplingSetCount"), Is.Zero);
            Assert.That(fixture.atlasBufferCount, Is.Zero);
            AssertGI(result, 0, new Vector3(4, 0, 0));
        }

        [Test]
        public void GPUPinnedCamerasCanDisableAndReenableDifferentSetsWithoutChangingAtlasTopology()
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new SamplingFixture();
            var first = fixture.AddGrid(new Vector3(4, 0, 0), Vector3.zero);
            var second = fixture.AddGrid(new Vector3(0, 2, 0), Vector3.right * .5f);
            var position = new Vector3(.75f, .5f, .5f);
            AssertGI(fixture.Sample(new[] { first, second }, position), 0, new Vector3(2, 1, 0));
            var atlas = fixture.atlasBuffers;
            fixture.Pin();
            AssertGI(fixture.Sample(new[] { first }, position), 0, new Vector3(4, 0, 0));
            Assert.That(fixture.atlasBuffers, Is.SameAs(atlas));
            Assert.That(Shader.GetGlobalInt("_APRTSamplingSetCount"), Is.EqualTo(2));
            AssertGI(fixture.Sample(new[] { second }, position), 0, new Vector3(0, 2, 0));
            Assert.That(fixture.atlasBuffers, Is.SameAs(atlas));
            AssertGI(fixture.Sample(new[] { first, second }, position), 0, new Vector3(2, 1, 0));
        }

        static void AssertGI(Vector4[] results, int sample, Vector3 expected)
        {
            Assert.That(results[sample * 4].w, Is.EqualTo(1));
            Assert.That(Vector3.Distance(results[sample * 4], expected), Is.LessThan(.001f));
        }

        sealed class SamplingFixture : IDisposable
        {
            static readonly string[] k_Buffers =
            {
                "_APRTRuntimeSH", "_APRTSkyIrradianceSH", "_APRTBakedAxis", "_APRTProbePositions", "_APRTProbeValidity", "_APRTVisibilityMoments",
                "_APRTBricks", "_APRTBrickProbeIndices", "_APRTBrickHash", "_APRTGridProbeIndices", "_APRTWorldToDomains", "_APRTSamplingContexts"
            };
            readonly APRTProbeReferenceVolume coordinator = new();
            readonly List<APRTProbeReferenceVolume> contexts = new();
            readonly List<ProbeVolumeBakingSet> sets = new();
            readonly ComputeShader shader;
            readonly int kernel;
            readonly GraphicsBuffer dummyDescriptor;
            internal int atlasBufferCount => coordinator.samplingAtlasBuffers.Length;
            internal GraphicsBuffer[] atlasBuffers => coordinator.samplingAtlasBuffers;

            internal SamplingFixture()
            {
                shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.unity.render-pipelines.core/Tests/Editor/APRT/APRTMultiSamplingTests.compute");
                Assert.That(shader, Is.Not.Null);
                Assert.That(shader.HasKernel("Sample"), Is.True, "The production multi-set HLSL must compile on the test GPU.");
                kernel = shader.FindKernel("Sample");
                coordinator.Initialize(APRTStreamingTests.Parameters(gpu: false));
                dummyDescriptor = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 368);
            }

            internal APRTProbeReferenceVolume AddGrid(Vector3 color, Vector3 origin,
                Quaternion? rotation = null, float spacing = 1, APRTLightingModel lightingModel = APRTLightingModel.DiffuseTransfer,
                float validity = 1, Vector3? otherColor = null, bool addAmbient = false)
            {
                var data = APRTSkyIrradianceTests.RGBSkyGridData();
                data.lightingModel = lightingModel;
                data.regularGridOrigin = origin;
                data.regularGridRotation = rotation ?? Quaternion.identity;
                data.regularGridSpacing = spacing;
                var transform = Matrix4x4.TRS(origin, data.regularGridRotation, Vector3.one * spacing);
                var cell = data.cells[0];
                for (int i = 0; i < cell.probeCount; ++i)
                {
                    cell.probePositions[i] = transform.MultiplyPoint3x4(cell.probePositions[i]);
                    cell.probeValidity[i] = validity;
                }
                data.globalBounds = new Bounds(cell.probePositions[0], Vector3.zero);
                foreach (var position in cell.probePositions) data.globalBounds.Encapsulate(position);
                data.SetBakedLightingScenario(Scenario("Base", color));
                data.SetBakedLightingScenario(Scenario("Other", otherColor ?? color));
                var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                sets.Add(set);
                set.SetDefaults();
                set.TryAddScenario("Base");
                set.TryAddScenario("Other");
                set.CommitAPRTBakedData(data);
                set.aprtGIIntensity = 1;
                set.aprtTemporalBlend = 1;
                set.aprtFloorBoost = 0;
                set.aprtNormalBias = 0;
                set.aprtVisibilityBias = 0;
                set.aprtLeakReduction = true;
                set.aprtAddAmbientLighting = addAmbient;
                var context = new APRTProbeReferenceVolume();
                contexts.Add(context);
                context.Initialize(APRTStreamingTests.Parameters(gpu: false));
                context.SetActiveBakingSet(set);
                context.lightingScenario = "Base";
                Assert.That(context.PrepareRelight(null), Is.True);
                APRTStreamingTests.Dispatch(context, set);
                return context;
            }

            static APRTLightingScenario Scenario(string name, Vector3 color)
            {
                var directional = new Vector4[32];
                for (int probe = 0; probe < 8; ++probe) directional[probe * 4] = color;
                return new APRTLightingScenario
                {
                    name = name, bakedDirectionalSH = directional, bakedSkyIrradianceSH = new Vector4[32]
                };
            }

            internal void Blend(APRTProbeReferenceVolume context, float weight)
            {
                context.BlendLightingScenario("Other", weight);
                APRTStreamingTests.Dispatch(context, context.currentBakingSet);
            }

            internal void Pin() => coordinator.PinBakingSetRuntimesForFrame(coordinator.preparedSamplingContexts);

            internal Vector4[] Sample(APRTProbeReferenceVolume[] active, params Vector3[] positions)
            {
                Assert.That(coordinator.PrepareSamplingAtlas(active), Is.True);
                using (var cmd = new CommandBuffer())
                {
                    coordinator.CopyAndBindSamplingAtlas(cmd, active);
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                // Explicit compute bindings use the production atlas and the globals
                // written by CopyAndBindSamplingAtlas. Compute kernels have their own
                // parameter bindings, separate from the material global state.
                GraphicsBuffer[] source;
                if (coordinator.samplingAtlasBuffers.Length > 0) source = coordinator.samplingAtlasBuffers;
                else
                {
                    var buffers = active[0].gpuBuffers;
                    source = new[] { buffers[9], buffers[12], buffers[0], buffers[2], buffers[3], buffers[1],
                        buffers[4], buffers[5], buffers[6], buffers[7], buffers[8], dummyDescriptor };
                }
                for (int i = 0; i < k_Buffers.Length; ++i) shader.SetBuffer(kernel, k_Buffers[i], source[i]);
                foreach (string property in new[] { "_APRTSamplingSetCount", "_APRTProbeCount", "_APRTLightingModel", "_APRTDebugMode",
                    "_APRTRegularGrid", "_APRTDomainCount", "_APRTBrickCount", "_APRTBrickHashMask", "_APRTBrickHashMaxSteps", "_APRTMaxSubdivision" })
                    shader.SetInt(property, Shader.GetGlobalInt(property));
                foreach (string property in new[] { "_EnableAPRT", "_APRTGIIntensity", "_APRTAddAmbientLighting", "_APRTMinBrickSize", "_APRTNormalBias",
                    "_APRTVisibilityBias", "_APRTValidityThreshold", "_APRTLeakReduction", "_APRTFloorBoost", "_APRTFloorPower" })
                    shader.SetFloat(property, Shader.GetGlobalFloat(property));
                foreach (string property in new[] { "_APRTGridDimensions", "_APRTGridSamplingSpacing", "_APRTProbeOffset" })
                    shader.SetVector(property, Shader.GetGlobalVector(property));
                foreach (string property in new[] { "_APRTWorldToGrid", "_APRTWorldToSamplingGrid", "_APRTSamplingGridToWorld" })
                    shader.SetMatrix(property, Shader.GetGlobalMatrix(property));
                using var input = new GraphicsBuffer(GraphicsBuffer.Target.Structured, positions.Length, 12);
                using var output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, positions.Length * 4, 16);
                input.SetData(positions);
                shader.SetBuffer(kernel, "_SamplePositions", input);
                shader.SetBuffer(kernel, "_SampleResults", output);
                shader.Dispatch(kernel, positions.Length, 1, 1);
                var result = new Vector4[positions.Length * 4];
                output.GetData(result);
                return result;
            }

            public void Dispose()
            {
                coordinator.Cleanup();
                foreach (var context in contexts) context.Cleanup();
                foreach (var set in sets) Object.DestroyImmediate(set);
                dummyDescriptor.Dispose();
            }
        }
    }
}
