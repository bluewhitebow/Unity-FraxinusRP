using System;
using System.Linq;
using NUnit.Framework;

namespace UnityEngine.Rendering.Tests
{
    public class APRTReferenceLightingTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void SingleProbeAxisRequiresExplicitNoncollapsedReferenceSamplingBounds(bool explicitSamplingBounds)
        {
            var data = APRTLightingTests.GridData();
            data.lightingModel = APRTLightingModel.APRTV;
            data.regularGridDimensions = new Vector3Int(1, 2, 2);
            data.regularGridProbeIndices = new[] { 0, 2, 4, 6 };
            if (explicitSamplingBounds) data.regularGridSamplingSize = Vector3.one;
            Assert.That(data.HasValidSharedData(), Is.EqualTo(explicitSamplingBounds));
        }

        [Test]
        public void ReferenceSamplingBoundsRemainIndependentOfCapturedLattice()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45)
                Assert.Ignore("Requires a real shader model 4.5 compute device.");
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var data = APRTLightingTests.GridData();
                data.lightingModel = APRTLightingModel.APRTV;
                data.regularGridSpacing = 39;
                data.regularGridSamplingSize = Vector3.one * 40;
                data.regularGridRotation = Quaternion.Euler(0, 37, 0);
                data.regularGridSamplingOrigin = new Vector3(-20, -20, -20);
                data.regularGridOrigin = data.regularGridSamplingOrigin + data.regularGridRotation * (Vector3.one * .5f);
                for (int i = 0; i < data.cells[0].probePositions.Length; ++i)
                    data.cells[0].probePositions[i] = data.regularGridOrigin + data.regularGridRotation * (data.cells[0].probePositions[i] * 39);
                set.CommitAPRTBakedData(data);
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                volume.SetActiveBakingSet(set);
                using var cmd = new CommandBuffer();
                volume.DispatchRelight(cmd, new Vector4[4], new Vector4[4], new Vector4[7]);
                Graphics.ExecuteCommandBuffer(cmd);
                var captureMatrix = Shader.GetGlobalMatrix("_APRTWorldToGrid");
                var sampleMatrix = Shader.GetGlobalMatrix("_APRTWorldToSamplingGrid");
                var sampleToWorld = Shader.GetGlobalMatrix("_APRTSamplingGridToWorld");
                var lastCapture = data.regularGridOrigin + data.regularGridRotation * (Vector3.one * 39);
                var samplerBoundary = data.regularGridSamplingOrigin + data.regularGridRotation * (Vector3.one * 40);
                Assert.That((captureMatrix.MultiplyPoint3x4(lastCapture) - Vector3.one).sqrMagnitude, Is.LessThan(1e-8f));
                Assert.That((sampleMatrix.MultiplyPoint3x4(lastCapture) - Vector3.one * .9875f).sqrMagnitude, Is.LessThan(1e-8f));
                Assert.That((sampleMatrix.MultiplyPoint3x4(samplerBoundary) - Vector3.one).sqrMagnitude, Is.LessThan(1e-8f));
                Assert.That((sampleToWorld.MultiplyPoint3x4(Vector3.one) - samplerBoundary).sqrMagnitude, Is.LessThan(1e-8f));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void ReferenceDirectionalResponseStaysIdenticalWithIndependentSkyEnabled()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45)
                Assert.Ignore("Requires a real shader model 4.5 compute device.");
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var data = APRTSkyIrradianceTests.SkyGridData();
                data.lightingModel = APRTLightingModel.APRTV;
                var axes = new[]
                {
                    new Vector4(1.1f, .03f, .1f, .25f), new Vector4(.08f, .8f, .06f, .06f),
                    new Vector4(.15f, .07f, .3f, .85f), new Vector4(.45f, .4f, .3f, 0),
                    new Vector4(.2f, .08f, .5f, .35f), new Vector4(.02f, .75f, .1f, .1f)
                };
                for (int probe = 0; probe < data.cells[0].probeCount; ++probe)
                    Array.Copy(axes, 0, data.cells[0].axisData, probe * 6, 6);
                set.CommitAPRTBakedData(data);
                set.aprtTemporalBlend = 1;
                set.aprtReferenceSkyIntensity = .7f;
                var colors = new[] { new Vector4(2, 1, .4f, 1), new Vector4(.1f, .3f, .6f, 1), Vector4.zero, Vector4.zero };
                var a = new Vector3(.3f, .8f, -.2f).normalized;
                var b = new Vector3(-.7f, .2f, .6f).normalized;
                var directions = new[] { new Vector4(a.x, a.y, a.z, 0), new Vector4(b.x, b.y, b.z, 0), Vector4.zero, Vector4.zero };
                var sky = APRTSkyIrradianceTests.ConstantRadiance(new Vector3(.2f, .3f, .4f));
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                volume.SetActiveBakingSet(set);
                using var cmd = new CommandBuffer();
                volume.DispatchRelight(cmd, colors, directions, new Vector4[7], sky);
                Graphics.ExecuteCommandBuffer(cmd);
                var actual = new Vector4[volume.runtimeSHBuffer.count];
                volume.runtimeSHBuffer.GetData(actual);
                var expected = LegacyReference(axes, colors, directions, new Vector4[4], set);
                Assert.That(expected[0].x, Is.GreaterThan(.01f), "The reference includes the deliberate directional contribution.");
                Assert.That(expected[0].w, Is.GreaterThan(.01f), "The fixture must exercise the sky and shadow response gates.");
                for (int i = 0; i < actual.Length; ++i)
                    Assert.That((actual[i] - expected[i % 4]).magnitude, Is.LessThan(0.00001f), $"Probe {i / 4}, coefficient {i % 4}");
                var independentSky = APRTSkyIrradianceTests.ReadSky(volume);
                Assert.That(independentSky.Where((_, i) => i % 4 == 0).All(v => v.x > .01f), Is.True,
                    "The fixture must exercise the separate physical sky buffer.");

                cmd.Clear();
                set.aprtReferenceSkyIntensity = 0;
                volume.DispatchRelight(cmd, colors, directions, new Vector4[7], sky);
                Graphics.ExecuteCommandBuffer(cmd);
                var noSky = new Vector4[actual.Length]; volume.runtimeSHBuffer.GetData(noSky);
                var legacyNoSky = LegacyReference(axes, colors, directions, new Vector4[4], set);
                for (int i = 0; i < noSky.Length; ++i)
                    Assert.That((noSky[i] - legacyNoSky[i % 4]).magnitude, Is.LessThan(0.00001f),
                        $"Disabling sky must preserve the original reference response: probe {i / 4}, coefficient {i % 4}");
                for (int i = 0; i < actual.Length; ++i)
                    Assert.That((actual[i] - noSky[i]).magnitude, Is.LessThan(.00001f),
                        "Physical sky must never alter the reference directional response or alpha.");
                Assert.That(APRTSkyIrradianceTests.ReadSky(volume), Is.All.EqualTo(Vector4.zero));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        // Independent CPU oracle follows the reference directional response, including
        // its artistic purity uplift and two applications of indirect intensity.
        // Preserve the original coupled sky formula as the independent compatibility oracle.
        static Vector4[] LegacyReference(Vector4[] axes, Vector4[] colors, Vector4[] directions, Vector4[] sky, ProbeVolumeBakingSet settings)
        {
            Vector3 l0 = Vector3.zero, x = Vector3.zero, y = Vector3.zero, z = Vector3.zero;
            for (int light = 0; light < 4; ++light)
            {
                if (colors[light].w < .5f) continue;
                var direction = (Vector3)directions[light];
                var direct = Sample(axes, direction); var bounce = Sample(axes, -direction);
                var albedo = (Vector3)bounce;
                float max = Mathf.Max(albedo.x, Mathf.Max(albedo.y, albedo.z));
                float min = Mathf.Min(albedo.x, Mathf.Min(albedo.y, albedo.z));
                var pure = new Vector3(
                    Mathf.Pow(Mathf.Max(1e-5f, albedo.x / (max + .001f)), Mathf.Max(1, settings.aprtSaturationBoost)),
                    Mathf.Pow(Mathf.Max(1e-5f, albedo.y / (max + .001f)), Mathf.Max(1, settings.aprtSaturationBoost)),
                    Mathf.Pow(Mathf.Max(1e-5f, albedo.z / (max + .001f)), Mathf.Max(1, settings.aprtSaturationBoost))) * max;
                float purity = Mathf.Clamp01((max - min) / (max + .001f));
                var radiance = Vector3.Scale((Vector3)colors[light], pure) * (1 - bounce.w) * settings.aprtIndirectIntensity;
                var directRadiance = (Vector3)colors[light] * direct.w;
                l0 += (directRadiance + radiance) * .5f + radiance * purity * .5f;
                var directional = (directRadiance - radiance) * .25f;
                x += directional * direction.x; y += directional * direction.y; z += directional * direction.z;
            }
            var visibility = new Vector4(axes.Average(a => a.w), (axes[0].w - axes[1].w) * .5f, (axes[2].w - axes[3].w) * .5f, (axes[4].w - axes[5].w) * .5f);
            float power = settings.aprtSkyVisibilityPower > 0 ? settings.aprtSkyVisibilityPower : 2;
            float skyGate = Mathf.Pow(Mathf.Max(Mathf.Clamp01(visibility.x), 1e-5f), Mathf.Max(power, .25f));
            float energy = Vector3.Dot(Vector3.Max(l0, Vector3.zero), new Vector3(.2126f, .7152f, .0722f));
            float t = Mathf.Clamp01((energy * Mathf.Max(settings.aprtIndirectContrast, 1) - .04f) / (.35f - .04f));
            float gate = skyGate * t * t * (3 - 2 * t);
            float giGate = Mathf.Lerp(Mathf.Clamp01(settings.aprtShadowGIVisibility), 1, gate);
            l0 = l0 * giGate + (Vector3)sky[0] * gate * settings.aprtReferenceSkyIntensity;
            x = x * giGate + (Vector3)sky[1] * visibility.y * gate * settings.aprtReferenceSkyIntensity;
            y = y * giGate + (Vector3)sky[2] * visibility.z * gate * settings.aprtReferenceSkyIntensity;
            z = z * giGate + (Vector3)sky[3] * visibility.w * gate * settings.aprtReferenceSkyIntensity;
            for (int channel = 0; channel < 3; ++channel)
            {
                float magnitude = Mathf.Sqrt(x[channel] * x[channel] + y[channel] * y[channel] + z[channel] * z[channel]);
                float scale = Mathf.Min(1, Mathf.Max(l0[channel] * .95f, 0) / Mathf.Max(magnitude, 1e-5f));
                x[channel] *= scale; y[channel] *= scale; z[channel] *= scale;
            }
            l0 *= settings.aprtIndirectIntensity; x *= settings.aprtIndirectIntensity; y *= settings.aprtIndirectIntensity; z *= settings.aprtIndirectIntensity;
            return new[] { new Vector4(l0.x, l0.y, l0.z, gate), new Vector4(x.x, x.y, x.z, visibility.y * gate),
                new Vector4(y.x, y.y, y.z, visibility.z * gate), new Vector4(z.x, z.y, z.z, visibility.w * gate) };
        }

        static Vector4 Sample(Vector4[] axes, Vector3 direction)
        {
            var positive = Vector3.Max(Vector3.one * .00001f, direction);
            var negative = Vector3.Max(Vector3.one * .00001f, -direction);
            float total = positive.x + positive.y + positive.z + negative.x + negative.y + negative.z;
            return (axes[0] * positive.x + axes[1] * negative.x + axes[2] * positive.y + axes[3] * negative.y + axes[4] * positive.z + axes[5] * negative.z) / total;
        }
    }
}
