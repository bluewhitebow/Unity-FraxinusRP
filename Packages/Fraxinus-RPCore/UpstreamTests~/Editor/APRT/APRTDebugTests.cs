using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace UnityEngine.Rendering.Tests
{
    public class APRTDebugTests
    {
        static readonly FieldInfo s_DebugMode = typeof(APRTProbeReferenceVolume).GetField(
            "m_APRTDebugMode", BindingFlags.Instance | BindingFlags.NonPublic);

        [TestCase(0)]
        [TestCase(1)]
        public void SwitchingDiagnosticViewsNeverReplacesLightingOrItsTemporalHistory(int model)
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            using var cmd = new CommandBuffer();
            try
            {
                var data = APRTSkyIrradianceTests.SkyGridData(.23f);
                for (int i = 0; i < data.cells[0].axisData.Length; ++i) data.cells[0].axisData[i] = new Vector4(.7f, .12f, .04f, .23f);
                data.lightingModel = (APRTLightingModel)model;
                // Raw transport, visibility and validity must differ from the relit result.
                data.cells[0].probeValidity = new[] { 0f, .2f, .4f, .6f, .8f, 1f, .3f, .9f };
                set.CommitAPRTBakedData(data);
                set.aprtTemporalBlend = .25f;
                set.aprtSkyIntensity = set.aprtReferenceSkyIntensity = 1;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                volume.SetActiveBakingSet(set);

                Dispatch(volume, cmd);
                var lighting = ReadSH(volume);
                var sky = APRTSkyIrradianceTests.ReadSky(volume);
                Assert.That(sky.Any(value => value.x > .00001f), Is.True, "The fixture must exercise real sky irradiance history.");
                Assert.That(lighting.Any(value => value.x > .00001f), Is.True,
                    "The fixture must contain real relit lighting, rather than a zero buffer.");

                // The first dispatch initializes directly to the current lighting. Subsequent
                // dispatches exercise temporal blending with the same input, so any change
                // indicates that a diagnostic replaced the light buffer or its history.
                for (int mode = 1; mode <= 5; ++mode)
                {
                    SetMode(volume, mode);
                    Dispatch(volume, cmd);
                    AssertSameSH(lighting, ReadSH(volume), "Diagnostic mode " + mode);
                    AssertSameSH(sky, APRTSkyIrradianceTests.ReadSky(volume), "Sky history, diagnostic mode " + mode);
                    Assert.That(Shader.GetGlobalInt("_APRTDebugMode"), Is.EqualTo(mode));
                }
                SetMode(volume, 0);
                Dispatch(volume, cmd);
                AssertSameSH(lighting, ReadSH(volume), "Return to normal lighting");
                AssertSameSH(sky, APRTSkyIrradianceTests.ReadSky(volume), "Return to normal sky lighting");
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void DiagnosticsRemainBoundAtZeroGIWhileNormalLightingUsesItsFallback(int model)
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            using var cmd = new CommandBuffer();
            try
            {
                var data = APRTSkyIrradianceTests.SkyGridData(.23f);
                for (int i = 0; i < data.cells[0].axisData.Length; ++i) data.cells[0].axisData[i] = new Vector4(.7f, .12f, .04f, .23f);
                data.lightingModel = (APRTLightingModel)model;
                set.CommitAPRTBakedData(data);
                set.aprtGIIntensity = 0;
                set.aprtSkyIntensity = set.aprtReferenceSkyIntensity = 1;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                volume.SetActiveBakingSet(set);

                // Start without a previously uploaded bake. Diagnostics must be able to
                // initialize their own inputs even when the artistic GI gain is disabled.
                Vector4[] lighting = null;
                Vector4[] sky = null;
                for (int mode = 1; mode <= 5; ++mode)
                {
                    SetMode(volume, mode);
                    Assert.That(volume.PrepareRelight(null), Is.True, "Diagnostic mode " + mode);
                    Dispatch(volume, cmd);
                    Assert.That(volume.DataHasBeenLoaded(), Is.True);
                    Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.EqualTo(1));
                    Assert.That(Shader.GetGlobalFloat("_APRTGIIntensity"), Is.Zero,
                        "Raw diagnostics must not turn on the user's disabled GI gain.");
                    Assert.That(Shader.GetGlobalInt("_APRTProbeCount"), Is.EqualTo(8));
                    Assert.That(Shader.GetGlobalInt("_APRTDebugMode"), Is.EqualTo(mode));
                    if (lighting == null) lighting = ReadSH(volume);
                    else AssertSameSH(lighting, ReadSH(volume), "Zero-GI diagnostic mode " + mode);
                    if (sky == null) sky = APRTSkyIrradianceTests.ReadSky(volume);
                    else AssertSameSH(sky, APRTSkyIrradianceTests.ReadSky(volume), "Zero-GI sky diagnostic mode " + mode);
                }

                SetMode(volume, 0);
                Assert.That(volume.PrepareRelight(null), Is.False);
                cmd.Clear();
                volume.BindPRVRuntimeResources(cmd, true);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(Shader.GetGlobalInt("_APRTDebugMode"), Is.Zero);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.Zero);
                Assert.That(Shader.GetGlobalFloat("_APRTGIIntensity"), Is.Zero);
                Assert.That(Shader.GetGlobalInt("_APRTProbeCount"), Is.Zero);
                Assert.That(Shader.GetGlobalFloat("_APRTAddAmbientLighting"), Is.EqualTo(1),
                    "Normal lighting with zero GI must keep the standard ambient fallback.");

                set.aprtGIIntensity = 1;
                Dispatch(volume, cmd);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.EqualTo(1));
                AssertSameSH(lighting, ReadSH(volume), "Re-enable GI after diagnostic views");
                AssertSameSH(sky, APRTSkyIrradianceTests.ReadSky(volume), "Re-enable physical sky after diagnostic views");
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void HiddenProbeSpheresDoNotAllocateDebugDrawingResources()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var cameraObject = new GameObject("APRT hidden probe debug camera");
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                var data = APRTSkyIrradianceTests.SkyGridData(); set.CommitAPRTBakedData(data);
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                var settings = APRTRelightSettings.FromBakingSet(set, data.lightingModel);
                Assert.That(volume.PrepareRelight(camera, settings), Is.True);
                Assert.That(volume.showProbeDebug, Is.False);
                Assert.That(volume.TryGetProbeDebugDraw(camera, settings, out var draw), Is.False);
                Assert.That(draw, Is.Null);
                foreach (string name in new[] { "m_ProbeDebugMesh", "m_ProbeDebugMaterial" })
                {
                    var field = typeof(APRTProbeReferenceVolume).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.That(field, Is.Not.Null);
                    Assert.That(field.GetValue(volume), Is.Null, "Hidden probes must not allocate " + name);
                }
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); Object.DestroyImmediate(cameraObject); }
        }

        [Test]
        public void VisibleProbeSpheresAtZeroGIDoNotEnableReceiverLighting()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var cameraObject = new GameObject("APRT zero-GI probe debug camera");
            using var cmd = new CommandBuffer();
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                var data = APRTSkyIrradianceTests.SkyGridData(); set.CommitAPRTBakedData(data);
                set.aprtGIIntensity = 0;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                var settings = APRTRelightSettings.FromBakingSet(set, data.lightingModel);
                Assert.That(volume.PrepareRelight(camera, settings), Is.False);
                volume.probeVolumeDebug.drawProbes = true;
                Assert.That(volume.PrepareRelight(camera, settings), Is.True, "Probe locations must be viewable before normal lighting is enabled.");
                Dispatch(volume, cmd);
                var lighting = ReadSH(volume); var sky = APRTSkyIrradianceTests.ReadSky(volume);
                Assert.That(volume.TryGetProbeDebugDraw(camera, settings, out var draw), Is.True);
                Assert.That(draw.probeCount, Is.EqualTo(8));
                Assert.That(draw.mesh, Is.Not.Null); Assert.That(draw.material, Is.Not.Null);
                Assert.That(volume.IsProbeDebugDrawValid(draw), Is.True);
                AssertSameSH(lighting, ReadSH(volume), "Probe sphere preparation preserves directional SH");
                AssertSameSH(sky, APRTSkyIrradianceTests.ReadSky(volume), "Probe sphere preparation preserves sky SH");
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.Zero);
                Assert.That(Shader.GetGlobalFloat("_APRTGIIntensity"), Is.Zero);
                Assert.That(Shader.GetGlobalFloat("_APRTAddAmbientLighting"), Is.EqualTo(1));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); Object.DestroyImmediate(cameraObject); }
        }

        [Test]
        public void QueuedProbeDrawingRejectsHiddenAndReplacedBakeResources()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var firstSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var secondSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var cameraObject = new GameObject("APRT stale probe debug camera");
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                var data = APRTSkyIrradianceTests.SkyGridData(); firstSet.CommitAPRTBakedData(data);
                secondSet.CommitAPRTBakedData(APRTSkyIrradianceTests.SkyGridData(.5f));
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(firstSet);
                volume.probeVolumeDebug.drawProbes = true;
                var settings = APRTRelightSettings.FromBakingSet(firstSet, data.lightingModel);
                Assert.That(volume.PrepareRelight(camera, settings), Is.True);
                Assert.That(volume.TryGetProbeDebugDraw(camera, settings, out var oldDraw), Is.True);
                Assert.That(volume.IsProbeDebugDrawValid(oldDraw), Is.True);
                volume.probeVolumeDebug.drawProbes = false;
                Assert.That(volume.IsProbeDebugDrawValid(oldDraw), Is.False, "A queued graph draw must respect the user hiding probes.");
                volume.probeVolumeDebug.drawProbes = true;
                volume.SetActiveBakingSet(secondSet);
                Assert.That(volume.IsProbeDebugDrawValid(oldDraw), Is.False, "A queued graph draw must not use disposed buffers from another bake.");
                Assert.That(volume.PrepareRelight(camera, settings), Is.True);
                Assert.That(volume.TryGetProbeDebugDraw(camera, settings, out var newDraw), Is.True);
                Assert.That(volume.IsProbeDebugDrawValid(newDraw), Is.True);
                Assert.That(newDraw.buffers, Is.Not.SameAs(oldDraw.buffers));
            }
            finally
            {
                volume.Cleanup(); Object.DestroyImmediate(firstSet); Object.DestroyImmediate(secondSet); Object.DestroyImmediate(cameraObject);
            }
        }

        static void SetMode(APRTProbeReferenceVolume volume, int mode)
        {
            Assert.That(s_DebugMode, Is.Not.Null);
            s_DebugMode.SetValue(volume, Enum.ToObject(s_DebugMode.FieldType, mode));
        }

        static void Dispatch(APRTProbeReferenceVolume volume, CommandBuffer cmd)
        {
            cmd.Clear();
            volume.DispatchRelight(cmd,
                new[] { new Vector4(1.3f, .8f, .4f, 1), Vector4.zero, Vector4.zero, Vector4.zero },
                new[] { new Vector4(0, 1, 0, 0), Vector4.zero, Vector4.zero, Vector4.zero },
                new Vector4[7], APRTSkyIrradianceTests.ConstantRadiance(new Vector3(.3f, .5f, .7f)));
            Graphics.ExecuteCommandBuffer(cmd);
        }

        static Vector4[] ReadSH(APRTProbeReferenceVolume volume)
        {
            Assert.That(volume.runtimeSHBuffer, Is.Not.Null);
            var values = new Vector4[volume.runtimeSHBuffer.count];
            volume.runtimeSHBuffer.GetData(values);
            return values;
        }

        static void AssertSameSH(Vector4[] expected, Vector4[] actual, string context)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), context);
            for (int i = 0; i < actual.Length; ++i)
                Assert.That((actual[i] - expected[i]).sqrMagnitude, Is.LessThan(1e-12f),
                    context + ", probe " + i / 4 + ", coefficient " + i % 4);
        }

        static void RequireGPU()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45)
                Assert.Ignore("Requires a real shader model 4.5 compute device.");
        }
    }
}
