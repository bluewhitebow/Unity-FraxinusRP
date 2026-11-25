using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnityEngine.Rendering.Tests
{
    public class APRTSamplingTests
    {
        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(0, false)]
        [TestCase(1, false)]
        public void GPURequiresEveryInterpolationCornerBeforeReplacingReceiverGI(int lightingModel, bool regularGrid)
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new SamplingFixture(lightingModel, regularGrid);
            foreach (int residentCorners in new[] { 0, 1, 7, 8 })
            {
                fixture.SetResidentCorners(residentCorners);
                var result = fixture.Sample();
                if (residentCorners == 8)
                {
                    Assert.That(result[0].w, Is.EqualTo(1), "A complete resident interpolation footprint supplies receiver GI.");
                    Assert.That(Vector3.Distance(result[0], new Vector3(2, 3, 4)), Is.LessThan(.001f));
                    Assert.That(result[1], Is.EqualTo(new Vector4(0, 1, 0, 1)));
                }
                else
                {
                    Assert.That(result[0], Is.EqualTo(Vector4.zero), "Partial residency must return false and clear GI so URP can use its fallback.");
                    Assert.That(result[1], Is.EqualTo(new Vector4(1, 0, 1, 1)), "Raw coverage marks an incomplete resident footprint magenta.");
                }
            }
        }

        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(0, false)]
        [TestCase(1, false)]
        public void GPUZeroReceiverWeightRetainsTheGIUnavailableResult(int lightingModel, bool regularGrid)
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new SamplingFixture(lightingModel, regularGrid);
            fixture.SetResidentCorners(8);
            fixture.SetValidity(0);
            var result = fixture.Sample();
            Assert.That(result[0], Is.EqualTo(Vector4.zero), "Resident probes with no valid receiver contribution must leave fallback GI available.");
            Assert.That(result[1], Is.EqualTo(new Vector4(0, 1, 0, 1)), "Raw coverage still reports the complete geometric footprint.");
        }

        sealed class SamplingFixture : IDisposable
        {
            readonly ComputeShader shader;
            readonly int kernel;
            readonly bool regularGrid;
            readonly List<GraphicsBuffer> buffers = new List<GraphicsBuffer>();
            readonly GraphicsBuffer indices, validity, results;

            internal SamplingFixture(int lightingModel, bool regularGrid)
            {
                this.regularGrid = regularGrid;
                shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.unity.render-pipelines.core/Tests/Editor/APRT/APRTSamplingTests.compute");
                Assert.That(shader, Is.Not.Null);
                Assert.That(shader.HasKernel("Sample"), Is.True, "The actual production sampling include must compile on the test GPU.");
                kernel = shader.FindKernel("Sample");
                var sh = new Vector4[32];
                var positions = new Vector3[8];
                for (int corner = 0; corner < 8; ++corner)
                {
                    sh[corner * 4] = new Vector4(2, 3, 4, 0);
                    var coordinate = new Vector3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
                    positions[corner] = regularGrid ? coordinate : (coordinate + Vector3.one) / 3;
                }
                Bind("_APRTRuntimeSH", sh, 16);
                Bind("_APRTSkyIrradianceSH", new Vector4[32], 16);
                Bind("_APRTBakedAxis", new Vector4[48], 16);
                Bind("_APRTProbePositions", positions, 12);
                validity = Bind("_APRTProbeValidity", Enumerable.Repeat(1f, 8).ToArray(), 4);
                Bind("_APRTVisibilityMoments", Enumerable.Repeat(new Vector4(100, 10000, 0, 0), 48).ToArray(), 16);
                Bind("_APRTBricks", new[] { Vector4.zero }, 16);
                Bind("_APRTBrickHash", new[] { 1 }, 4);
                Bind("_APRTWorldToDomains", new[] { Matrix4x4.identity }, 64);
                var grid = Bind("_APRTGridProbeIndices", new int[8], 4);
                var bricks = Bind("_APRTBrickProbeIndices", new int[64], 4);
                indices = regularGrid ? grid : bricks;
                results = Bind("_SampleResults", new Vector4[2], 16);
                shader.SetFloat("_EnableAPRT", 1);
                shader.SetFloat("_APRTGIIntensity", 1);
                shader.SetInt("_APRTProbeCount", 8);
                shader.SetInt("_APRTLightingModel", lightingModel);
                shader.SetInt("_APRTDebugMode", 4);
                shader.SetInt("_APRTRegularGrid", regularGrid ? 1 : 0);
                shader.SetInt("_APRTDomainCount", 0);
                shader.SetInt("_APRTBrickCount", 1);
                shader.SetInt("_APRTBrickHashMask", 0);
                shader.SetInt("_APRTBrickHashMaxSteps", 1);
                shader.SetInt("_APRTMaxSubdivision", 0);
                shader.SetVector("_APRTGridDimensions", new Vector4(2, 2, 2, 0));
                shader.SetMatrix("_APRTWorldToGrid", Matrix4x4.identity);
                shader.SetMatrix("_APRTWorldToSamplingGrid", Matrix4x4.identity);
                shader.SetMatrix("_APRTSamplingGridToWorld", Matrix4x4.identity);
                shader.SetVector("_APRTGridSamplingSpacing", Vector3.one);
                shader.SetVector("_APRTProbeOffset", Vector3.zero);
                shader.SetFloat("_APRTMinBrickSize", 1);
                shader.SetFloat("_APRTNormalBias", 0);
                shader.SetFloat("_APRTVisibilityBias", 0);
                shader.SetFloat("_APRTValidityThreshold", .01f);
                shader.SetFloat("_APRTLeakReduction", 1);
                shader.SetFloat("_APRTFloorBoost", 0);
                shader.SetFloat("_APRTFloorPower", 1);
            }

            GraphicsBuffer Bind<T>(string property, T[] values, int stride) where T : struct
            {
                var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, values.Length, stride);
                buffers.Add(buffer);
                buffer.SetData(values);
                shader.SetBuffer(kernel, property, buffer);
                return buffer;
            }

            internal void SetResidentCorners(int count)
            {
                var remap = Enumerable.Repeat(-1, regularGrid ? 8 : 64).ToArray();
                for (int corner = 0; corner < count; ++corner)
                {
                    int index = regularGrid ? corner :
                        1 + (corner & 1) + 4 * (1 + ((corner >> 1) & 1)) + 16 * (1 + ((corner >> 2) & 1));
                    remap[index] = corner;
                }
                indices.SetData(remap);
            }

            internal void SetValidity(float value) => validity.SetData(Enumerable.Repeat(value, 8).ToArray());

            internal Vector4[] Sample()
            {
                shader.Dispatch(kernel, 1, 1, 1);
                var result = new Vector4[2];
                results.GetData(result);
                return result;
            }

            public void Dispose()
            {
                foreach (var buffer in buffers) buffer.Dispose();
            }
        }
    }
}
