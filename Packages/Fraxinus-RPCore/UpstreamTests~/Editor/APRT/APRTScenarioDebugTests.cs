using System.Reflection;
using NUnit.Framework;

namespace UnityEngine.Rendering.Tests
{
    public class APRTScenarioDebugTests
    {
        static string DebugValue(APRTProbeReferenceVolume volume, string method) =>
            (string)typeof(APRTProbeReferenceVolume).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(volume, null);

        [TestCase(false)]
        [TestCase(true)]
        public void BakedSkyDiagnosticsUseStoredIrradianceEvenWhenLiveSkyIntensityIsZero(bool hasEnergy)
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45)
                Assert.Ignore("Requires a real shader model 4.5 compute device.");
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var previousSkybox = RenderSettings.skybox;
            using var cmd = new CommandBuffer();
            try
            {
                RenderSettings.skybox = null;
                set.SetDefaults();
                set.TryAddScenario("Day");
                set.TryAddScenario("Night");
                var data = APRTSkyIrradianceTests.SkyGridData();
                var day = new APRTLightingScenario
                {
                    name = "Day", bakedDirectionalSH = new Vector4[32], bakedSkyIrradianceSH = new Vector4[32]
                };
                var night = day.Clone();
                night.name = "Night";
                day.bakedSkyIrradianceSH[0] = hasEnergy ? new Vector4(4, 2, 1, 0) : Vector4.zero;
                data.SetBakedLightingScenario(day);
                data.SetBakedLightingScenario(night);
                set.CommitAPRTBakedData(data);
                set.aprtGIIntensity = 1;
                set.aprtSkyIntensity = set.aprtReferenceSkyIntensity = 0;
                set.aprtTemporalBlend = 1;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true, supportScenarios = true, supportScenarioBlending = true, blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow });
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Day";
                volume.BlendLightingScenario("Night", .25f);
                Assert.That(DebugValue(volume, "DebugSkyStatus"), Does.Contain("Waiting for a camera"));
                Assert.That(DebugValue(volume, "DebugSkySource"), Does.Contain("Baked scenarios"));
                var snapshot = volume.CaptureScenarioBlend(null, null, null);
                Assert.That(snapshot, Is.Not.Null);
                volume.PinRuntimeResourcesForFrame();
                volume.lightingScenario = "Night";
                set.aprtUseLightingScenarios = false;
                data.FindLightingScenario("Day").bakedSkyIrradianceSH[0] = new Vector4(100, 100, 100, 0);
                volume.DispatchRelight(cmd, snapshot, APRTRelightSettings.FromBakingSet(set, data.lightingModel), 93);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(DebugValue(volume, "DebugSkySource"), Is.EqualTo("Day -> Night (Baked Lighting Scenarios, blend 0.25)"));
                Assert.That(DebugValue(volume, "DebugSkyStatus"), Is.EqualTo(hasEnergy
                    ? "Ready - baked scenario sky irradiance" : "No sky energy - baked scenario irradiance is zero"));
                Assert.That(volume.GetCachedSkyRadianceCoefficientMagnitude(), Is.EqualTo(hasEnergy ? 3 : 0).Within(.00001f));
                Assert.That(typeof(APRTProbeReferenceVolume).GetField("m_SkyEnvironment", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(volume), Is.Null);
            }
            finally
            {
                RenderSettings.skybox = previousSkybox;
                volume.Cleanup();
                Object.DestroyImmediate(set);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SavedSkyDiagnosticsUseTheRenderedSnapshotWithoutALiveSkybox(bool hasEnergy)
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45)
                Assert.Ignore("Requires a real shader model 4.5 compute device.");
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var previousSkybox = RenderSettings.skybox;
            using var cmd = new CommandBuffer();
            try
            {
                RenderSettings.skybox = null;
                set.SetDefaults();
                set.TryAddScenario("Day");
                set.TryAddScenario("Night");
                var data = APRTSkyIrradianceTests.SkyGridData();
                var day = new APRTLightingScenario { name = "Day" };
                var night = new APRTLightingScenario { name = "Night" };
                day.skyRadiance[0] = hasEnergy ? new Vector4(4, 2, 1, 0) : Vector4.zero;
                data.SetLightingScenario(day);
                data.SetLightingScenario(night);
                set.CommitAPRTBakedData(data);
                set.aprtGIIntensity = set.aprtSkyIntensity = set.aprtReferenceSkyIntensity = 1;
                set.aprtTemporalBlend = 1;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true, supportScenarios = true, supportScenarioBlending = true, blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow });
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Day";
                volume.BlendLightingScenario("Night", .25f);

                Assert.That(DebugValue(volume, "DebugSkyStatus"), Does.Contain("Waiting for a camera"));
                Assert.That(DebugValue(volume, "DebugSkySource"), Does.Contain("Captured scenarios"));
                var snapshot = volume.CaptureScenarioBlend(null, null, null);
                Assert.That(snapshot, Is.Not.Null);
                // Changes after graph recording must not change what the panel
                // reports for the captured inputs used by this camera.
                volume.lightingScenario = "Night";
                set.aprtUseLightingScenarios = false;
                data.FindLightingScenario("Day").skyRadiance[0] = new Vector4(100, 100, 100, 0);
                volume.DispatchRelight(cmd, snapshot, APRTRelightSettings.FromBakingSet(set, data.lightingModel), 91);
                Graphics.ExecuteCommandBuffer(cmd);

                Assert.That(DebugValue(volume, "DebugSkySource"), Is.EqualTo("Day -> Night (Captured Lighting Scenarios, blend 0.25)"));
                Assert.That(DebugValue(volume, "DebugSkyStatus"), Is.EqualTo(hasEnergy
                    ? "Ready - captured scenario sky irradiance transfer" : "No sky energy - captured scenario radiance is zero"));
                Assert.That(volume.GetCachedSkyRadianceCoefficientMagnitude(), Is.EqualTo(hasEnergy ? 3 : 0).Within(.00001f));
                Assert.That(typeof(APRTProbeReferenceVolume).GetField("m_SkyEnvironment", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(volume), Is.Null);

                cmd.Clear();
                volume.DispatchRelight(cmd, volume.CaptureScenarioBlend(null, null, null),
                    APRTRelightSettings.FromBakingSet(set, data.lightingModel), 92);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(DebugValue(volume, "DebugSkySource"), Is.EqualTo("None"));
                Assert.That(DebugValue(volume, "DebugSkyStatus"), Is.EqualTo("No sky source - assign a scene skybox"));
            }
            finally
            {
                RenderSettings.skybox = previousSkybox;
                volume.Cleanup();
                Object.DestroyImmediate(set);
            }
        }
    }
}
