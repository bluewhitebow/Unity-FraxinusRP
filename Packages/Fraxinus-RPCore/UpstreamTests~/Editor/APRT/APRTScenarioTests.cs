using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnityEngine.Rendering.Tests
{
    public class APRTScenarioTests
    {
        [Test]
        public void RepeatedAndAlternatingLiveLightingCaptureDoesNotAllocateAfterWarmup()
        {
            var volume = new APRTProbeReferenceVolume();
            var colors = new Vector4[4];
            var directions = new Vector4[4];
            var sky = new Vector4[9];
            colors[0] = Vector4.one;
            var first = volume.CaptureScenarioBlend(colors, directions, sky);
            colors[0] *= 2;
            var second = volume.CaptureScenarioBlend(colors, directions, sky);
            Assert.That(second, Is.Not.SameAs(first));
            for (int i = 0; i < 32; ++i)
            {
                colors[0] = (i & 1) == 0 ? Vector4.one : Vector4.one * 2;
                volume.CaptureScenarioBlend(colors, directions, sky);
            }
            using var recorder = new APRTAllocationRecorder();
            recorder.Begin();
            for (int i = 0; i < 128; ++i)
            {
                colors[0] = (i & 1) == 0 ? Vector4.one : Vector4.one * 2;
                volume.CaptureScenarioBlend(colors, directions, sky);
            }
            int allocated = recorder.End();
            Assert.That(allocated, Is.Zero);
            colors[0] = Vector4.one;
            Assert.That(volume.CaptureScenarioBlend(colors, directions, sky), Is.SameAs(first));
            sky[8].x = .125f;
            Assert.That(volume.CaptureScenarioBlend(colors, directions, sky), Is.Not.SameAs(first),
                "Changing a coefficient must capture a new immutable input for deferred rendering.");
        }

        static APRTLightingScenario Scenario(string name, bool night = false)
        {
            var scenario = new APRTLightingScenario { name = name };
            scenario.colors[0] = night ? new Vector4(2, .6f, .3f, 1) : new Vector4(.04f, .02f, .01f, 1);
            scenario.directions[0] = night ? new Vector4(1, 0, 0, 0) : new Vector4(0, 1, 0, 0);
            scenario.skyRadiance[0] = night ? new Vector4(.1f, .2f, .4f, 0) : new Vector4(2, 1, .5f, 0);
            scenario.skyRadiance[1] = new Vector4(.02f, -.03f, .04f, 0);
            scenario.skyRadiance[3] = night ? new Vector4(.01f, .03f, -.02f, 0) : new Vector4(.4f, -.1f, .2f, 0);
            return scenario;
        }

        static APRTBakedData ScenarioGrid(APRTLightingModel model = APRTLightingModel.APRTV)
        {
            var data = APRTSkyIrradianceTests.RGBSkyGridData(.4f);
            data.lightingModel = model;
            var axes = new[]
            {
                new Vector4(.9f, .03f, .1f, .25f), new Vector4(.08f, .8f, .06f, .06f),
                new Vector4(.15f, .07f, .3f, .85f), new Vector4(.45f, .4f, .3f, 0),
                new Vector4(.2f, .08f, .5f, .35f), new Vector4(.02f, .75f, .1f, .1f)
            };
            for (int probe = 0; probe < data.cells[0].probeCount; ++probe)
                Array.Copy(axes, 0, data.cells[0].axisData, probe * 6, 6);
            data.SetLightingScenario(Scenario("Day"));
            data.SetLightingScenario(Scenario("Night", true));
            return data;
        }

        static ProbeVolumeBakingSet CreateSet(APRTLightingModel model = APRTLightingModel.APRTV)
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            set.SetDefaults();
            set.TryAddScenario("Day");
            set.TryAddScenario("Night");
            set.CommitAPRTBakedData(ScenarioGrid(model));
            set.aprtGIIntensity = 1;
            set.aprtTemporalBlend = 1;
            set.aprtIndirectIntensity = .8f;
            set.aprtIndirectContrast = 1;
            set.aprtShadowGIVisibility = .08f;
            set.aprtSkyVisibilityPower = .5f;
            set.aprtSkyIntensity = set.aprtReferenceSkyIntensity = .7f;
            return set;
        }

        [Test]
        public void SavedScenariosOwnTheirInputsAndSurviveSerializationWithoutDuplicatingTransport()
        {
            var set = CreateSet();
            var copy = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var data = set.aprtBakedData;
                var geometry = data.cells[0];
                long sharedBytes = data.GetDiskSizeOfSharedData();
                var source = Scenario("Dusk");
                var originalColor = source.colors[0];
                data.SetLightingScenario(source);
                source.colors[0] = Vector4.zero;
                source.directions[0] = Vector4.zero;
                source.skyRadiance[0] = Vector4.zero;
                Assert.That(data.FindLightingScenario("Dusk").colors[0], Is.EqualTo(originalColor));
                Assert.That(data.FindLightingScenario("Dusk").IsValid(), Is.True);
                Assert.That(data.cells[0], Is.SameAs(geometry));
                Assert.That(data.GetDiskSizeOfSharedData(), Is.EqualTo(sharedBytes));
                Assert.That(data.GetDiskSizeOfScenarioData("Dusk"), Is.GreaterThan(0));
                set.aprtUseLightingScenarios = true;

                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(set), copy);
                Assert.That(copy.aprtUseLightingScenarios, Is.True);
                Assert.That(copy.aprtBakedData.lightingScenarios.Count, Is.EqualTo(3));
                Assert.That(copy.aprtBakedData.scenarioCount, Is.Zero, "Legacy input captures require a scenario bake before the status becomes baked.");
                Assert.That(copy.aprtBakedData.HasValidSharedData(), Is.True);
                foreach (var scenario in data.lightingScenarios)
                {
                    var restored = copy.aprtBakedData.FindLightingScenario(scenario.name);
                    Assert.That(restored, Is.Not.Null);
                    Assert.That(restored.colors, Is.EqualTo(scenario.colors));
                    Assert.That(restored.directions, Is.EqualTo(scenario.directions));
                    Assert.That(restored.skyRadiance, Is.EqualTo(scenario.skyRadiance));
                }
                Assert.That(copy.aprtBakedData.cells[0].axisData, Is.EqualTo(geometry.axisData));
                Assert.That(copy.aprtBakedData.cells[0].skyIrradianceTransferRGB, Is.EqualTo(geometry.skyIrradianceTransferRGB));
            }
            finally { Object.DestroyImmediate(set); Object.DestroyImmediate(copy); }
        }

        [TestCase("name")]
        [TestCase("light-count")]
        [TestCase("sky-count")]
        [TestCase("negative-light")]
        [TestCase("nan-sky")]
        [TestCase("infinite-direction")]
        [TestCase("zero-direction")]
        public void InvalidScenarioCannotReplaceThePreviousSnapshot(string fault)
        {
            var data = ScenarioGrid();
            var previous = data.FindLightingScenario("Day");
            var invalid = previous.Clone();
            switch (fault)
            {
                case "name": invalid.name = " "; break;
                case "light-count": invalid.colors = new Vector4[3]; break;
                case "sky-count": invalid.skyRadiance = new Vector4[7]; break;
                case "negative-light": invalid.colors[0].x = -1; break;
                case "nan-sky": invalid.skyRadiance[8].x = float.NaN; break;
                case "infinite-direction": invalid.directions[0].y = float.PositiveInfinity; break;
                case "zero-direction": invalid.directions[0] = Vector4.zero; break;
            }
            Assert.That(invalid.IsValid(), Is.False);
            Assert.Throws<ArgumentException>(() => data.SetLightingScenario(invalid));
            Assert.That(data.FindLightingScenario("Day"), Is.SameAs(previous));
            Assert.That(data.lightingScenarios.Count, Is.EqualTo(2));
        }

        [Test]
        public void RebakingTransportRetainsIndependentLightingCapturesAndExplicitClearRemovesThem()
        {
            var set = CreateSet();
            try
            {
                var previous = set.aprtBakedData;
                var originalDay = previous.FindLightingScenario("Day");
                var replacement = APRTSkyIrradianceTests.SkyGridData(.2f);
                replacement.lightingModel = APRTLightingModel.APRTV;
                set.CommitAPRTBakedData(replacement);
                Assert.That(set.aprtBakedData, Is.SameAs(replacement));
                Assert.That(replacement.FindLightingScenario("Day").colors, Is.EqualTo(originalDay.colors));
                Assert.That(replacement.FindLightingScenario("Day").colors, Is.Not.SameAs(originalDay.colors));
                Assert.That(replacement.FindLightingScenario("Night"), Is.Not.Null);
                Assert.That(replacement.TryGetScenarioHash("Day", out _), Is.False);
                Assert.That(replacement.TryGetScenarioHash("Night", out _), Is.False);
                Assert.That(replacement.HasBakedLightingScenario("Day"), Is.False, "Legacy inputs survive for migration but do not claim stored probe lighting.");
                set.Clear("APRT");
                Assert.That(set.aprtBakedData.lightingScenarios, Is.Empty);
                Assert.That(set.aprtBakedData.HasValidSharedData(), Is.False);
            }
            finally { Object.DestroyImmediate(set); }
        }

        [Test]
        public void RenameAndRemoveUpdateAPRTSelectionWithoutChangingAPVSelection()
        {
            var set = CreateSet();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                set.aprtUseLightingScenarios = true;
                set.TryAddScenario("APV Only");
                set.SetActiveScenario("APV Only", false, new APVProbeReferenceVolume());
                Initialize(volume, set);
                volume.lightingScenario = "Day";
                volume.BlendLightingScenario("Night", .35f);
                Assert.That(set.GetLightingScenario("APRT"), Is.EqualTo("Day"));
                Assert.That(set.GetOtherScenario("APRT"), Is.EqualTo("Night"));
                Assert.That(set.GetScenarioBlendingFactor("APRT"), Is.EqualTo(.35f));

                Assert.That(set.RenameScenario("Night", "Dusk"), Is.EqualTo("Dusk"));
                Assert.That(volume.otherScenario, Is.EqualTo("Dusk"));
                Assert.That(volume.scenarioBlendingFactor, Is.EqualTo(.35f));
                Assert.That(set.aprtBakedData.FindLightingScenario("Night"), Is.Null);
                Assert.That(set.aprtBakedData.FindLightingScenario("Dusk"), Is.Not.Null);
                Assert.That(set.RemoveScenario("Dusk"), Is.True);
                Assert.That(volume.otherScenario, Is.Null);
                Assert.That(volume.scenarioBlendingFactor, Is.Zero);

                Assert.That(set.RenameScenario("Day", "Morning"), Is.EqualTo("Morning"));
                Assert.That(volume.lightingScenario, Is.EqualTo("Morning"));
                Assert.That(set.GetLightingScenario("APV"), Is.EqualTo("APV Only"));
                Assert.That(set.GetOtherScenario("APV"), Is.Null);
                Assert.That(set.GetScenarioBlendingFactor("APV"), Is.Zero);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CaptureRejectsMissingGeometryOrUnloadedScenesWithoutReplacingStoredLighting(bool hasGeometry)
        {
            var set = CreateSet();
            try
            {
                var previous = set.aprtBakedData.FindLightingScenario("Day");
                if (hasGeometry)
                    set.AddScene("00000000000000000000000000000001");
                else
                    set.aprtBakedData.formatVersion = 0;
                Assert.That(APRTLightingScenarioCapture.TryCapture(set, "Day", out string error), Is.False);
                Assert.That(error, Is.Not.Null.And.Not.Empty);
                Assert.That(error, Does.Contain(hasGeometry ? "Load every scene" : "Bake APRTV geometry"));
                Assert.That(set.aprtBakedData.FindLightingScenario("Day"), Is.SameAs(previous));
            }
            finally
            {
                if (hasGeometry) set.RemoveScene("00000000000000000000000000000001");
                Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void MissingSavedEndpointCannotSilentlyUseCurrentLiveLighting()
        {
            var set = CreateSet();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                set.aprtUseLightingScenarios = true;
                Initialize(volume, set);
                set.TryAddScenario("Uncaptured");
                set.SetActiveScenario("Uncaptured", false, volume);
                var live = Scenario("Live");
                Assert.That(volume.CaptureScenarioBlend(live.colors, live.directions, live.skyRadiance), Is.Null);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void SelectingThirdScenarioCommitsTheNextPairAndNullTargetStopsBlending()
        {
            var set = CreateSet();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                set.aprtUseLightingScenarios = true;
                set.TryAddScenario("Overcast");
                set.aprtBakedData.SetLightingScenario(Scenario("Overcast"));
                Initialize(volume, set);
                volume.lightingScenario = "Day";
                volume.BlendLightingScenario("Night", 1);
                Assert.That(volume.lightingScenario, Is.EqualTo("Day"), "A factor of one does not implicitly change the active endpoint.");
                volume.lightingScenario = "Night";
                Assert.That(volume.otherScenario, Is.Null);
                Assert.That(volume.scenarioBlendingFactor, Is.Zero);
                volume.BlendLightingScenario("Overcast", .5f);
                volume.scenarioBlendingFactor = .75f;
                Assert.That(volume.lightingScenario, Is.EqualTo("Night"));
                Assert.That(volume.otherScenario, Is.EqualTo("Overcast"));
                Assert.That(volume.scenarioBlendingFactor, Is.EqualTo(.75f));
                volume.BlendLightingScenario(null, .9f);
                Assert.That(volume.otherScenario, Is.Null);
                Assert.That(volume.scenarioBlendingFactor, Is.Zero);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void InvalidScenarioSelectionOrNonfiniteFactorRetainsTheWorkingPair()
        {
            var set = CreateSet();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                Initialize(volume, set);
                volume.lightingScenario = "Day";
                volume.BlendLightingScenario("Night", .4f);
                volume.lightingScenario = "Missing";
                volume.BlendLightingScenario("Missing", .8f);
                volume.scenarioBlendingFactor = float.NaN;
                volume.scenarioBlendingFactor = float.PositiveInfinity;
                Assert.That(volume.lightingScenario, Is.EqualTo("Day"));
                Assert.That(volume.otherScenario, Is.EqualTo("Night"));
                Assert.That(volume.scenarioBlendingFactor, Is.EqualTo(.4f));
                set.aprtBakedData.FindLightingScenario("Night").skyRadiance[8].x = float.NaN;
                var live = Scenario("Live");
                Assert.That(volume.CaptureScenarioBlend(live.colors, live.directions, live.skyRadiance), Is.Null,
                    "A corrupt blend target must not silently fall back to current live lighting.");
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void SavedBlendMatchesRelitEndpointSHIncludingSkyAndNonlinearReferenceResponse(int model)
        {
            RequireGPU();
            var set = CreateSet((APRTLightingModel)model);
            var volume = new APRTProbeReferenceVolume();
            try
            {
                Initialize(volume, set);
                Assert.That(set.aprtUseLightingScenarios, Is.False, "Existing Baking Sets keep live relighting by default.");
                var day = Scenario("Day");
                var night = Scenario("Night", true);
                var a = Relight(volume, set, day);
                var b = Relight(volume, set, night);
                Assert.That(a.directional.Zip(b.directional, (x, y) => (x - y).magnitude).Max(), Is.GreaterThan(.001f));
                Assert.That(a.sky.Zip(b.sky, (x, y) => (x - y).magnitude).Max(), Is.GreaterThan(.01f));
                if (model == (int)APRTLightingModel.APRTV)
                {
                    var mixedInputs = day.Clone();
                    mixedInputs.colors = day.colors.Zip(night.colors, (x, y) => (x + y) * .5f).ToArray();
                    mixedInputs.directions = day.directions.Zip(night.directions, (x, y) => (x + y) * .5f).ToArray();
                    var relitMixedInputs = Relight(volume, set, mixedInputs);
                    Assert.That(relitMixedInputs.directional.Select((value, i) => (value - (a.directional[i] + b.directional[i]) * .5f).magnitude).Max(),
                        Is.GreaterThan(.001f), "The fixture must distinguish SH interpolation from nonlinear relighting of interpolated inputs.");
                }
                set.aprtUseLightingScenarios = true;
                volume.lightingScenario = "Day";
                foreach (float factor in new[] { -1f, 0, .5f, 1, 2 })
                {
                    volume.BlendLightingScenario("Night", factor);
                    var actual = Relight(volume, set, new APRTLightingScenario { name = "Black live input" });
                    AssertLighting(actual, Lerp(a, b, Mathf.Clamp01(factor)), "Saved factor " + factor);
                }
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void TemporalHistoryIsAppliedOnceAfterScenarioBlendAndResetsForAnotherCamera()
        {
            RequireGPU();
            var set = CreateSet();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                Initialize(volume, set);
                var a = Relight(volume, set, Scenario("Day"));
                var b = Relight(volume, set, Scenario("Night", true));
                set.aprtUseLightingScenarios = true;
                set.aprtTemporalBlend = .25f;
                volume.lightingScenario = "Day";
                volume.BlendLightingScenario("Night", .1f);
                var history = Relight(volume, set, Scenario("Unused"));
                AssertLighting(history, Lerp(a, b, .1f), "Entering saved scenarios resets live history");
                volume.BlendLightingScenario("Night", .7f);
                var target = Lerp(a, b, .7f);
                var actual = Relight(volume, set, Scenario("Unused"));
                AssertLighting(actual, Lerp(history, target, .25f), "Single temporal blend after combining endpoints");
                AssertLighting(Relight(volume, set, Scenario("Unused"), 72), target, "Another camera must not inherit a different camera's history");
                set.aprtUseLightingScenarios = false;
                AssertLighting(Relight(volume, set, Scenario("Day"), 72), a, "Returning to live lighting resets saved scenario history");
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void RecordedSnapshotRetainsLightingAndSelectionUntilItsDispatchExecutes()
        {
            RequireGPU();
            var set = CreateSet();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                Initialize(volume, set);
                var a = Relight(volume, set, Scenario("Day"));
                var b = Relight(volume, set, Scenario("Night", true));
                set.aprtUseLightingScenarios = true;
                volume.lightingScenario = "Day";
                volume.BlendLightingScenario("Night", .3f);
                var snapshot = volume.CaptureScenarioBlend(null, null, null);
                Assert.That(snapshot, Is.Not.Null);
                Assert.That(snapshot.useLightingScenarios, Is.True);
                Assert.That(snapshot.activeScenario, Is.EqualTo("Day"));
                Assert.That(snapshot.otherScenario, Is.EqualTo("Night"));
                Assert.That(snapshot.factor, Is.EqualTo(.3f));
                foreach (var scenario in set.aprtBakedData.lightingScenarios)
                {
                    Array.Clear(scenario.colors, 0, scenario.colors.Length);
                    Array.Clear(scenario.skyRadiance, 0, scenario.skyRadiance.Length);
                }
                volume.lightingScenario = "Night";
                using var cmd = new CommandBuffer();
                volume.DispatchRelight(cmd, snapshot, APRTRelightSettings.FromBakingSet(set, set.aprtBakedData.lightingModel), 71);
                Graphics.ExecuteCommandBuffer(cmd);
                AssertLighting(ReadLighting(volume), Lerp(a, b, .3f), "Snapshot owns both endpoint arrays and the selected factor");
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        static void Initialize(APRTProbeReferenceVolume volume, ProbeVolumeBakingSet set)
        {
            volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true, supportScenarios = true, supportScenarioBlending = true, blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow });
            volume.SetActiveBakingSet(set);
        }

        static (Vector4[] directional, Vector4[] sky) Relight(APRTProbeReferenceVolume volume, ProbeVolumeBakingSet set,
            APRTLightingScenario live, int cameraId = 71)
        {
            var snapshot = volume.CaptureScenarioBlend(live.colors, live.directions, live.skyRadiance);
            Assert.That(snapshot, Is.Not.Null);
            using var cmd = new CommandBuffer();
            volume.DispatchRelight(cmd, snapshot, APRTRelightSettings.FromBakingSet(set, set.aprtBakedData.lightingModel), cameraId);
            Graphics.ExecuteCommandBuffer(cmd);
            return ReadLighting(volume);
        }

        static (Vector4[] directional, Vector4[] sky) ReadLighting(APRTProbeReferenceVolume volume)
        {
            Assert.That(volume.runtimeSHBuffer, Is.Not.Null);
            var directional = new Vector4[volume.runtimeSHBuffer.count];
            volume.runtimeSHBuffer.GetData(directional);
            return (directional, APRTSkyIrradianceTests.ReadSky(volume));
        }

        static (Vector4[] directional, Vector4[] sky) Lerp((Vector4[] directional, Vector4[] sky) a,
            (Vector4[] directional, Vector4[] sky) b, float t) =>
            (a.directional.Zip(b.directional, (x, y) => Vector4.Lerp(x, y, t)).ToArray(),
                a.sky.Zip(b.sky, (x, y) => Vector4.Lerp(x, y, t)).ToArray());

        static void AssertLighting((Vector4[] directional, Vector4[] sky) actual,
            (Vector4[] directional, Vector4[] sky) expected, string reason)
        {
            for (int i = 0; i < expected.directional.Length; ++i)
            {
                Assert.That((actual.directional[i] - expected.directional[i]).magnitude, Is.LessThan(.00002f), reason + ", directional " + i);
                Assert.That((actual.sky[i] - expected.sky[i]).magnitude, Is.LessThan(.00002f), reason + ", sky " + i);
            }
        }

        static void RequireGPU()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45)
                Assert.Ignore("Requires a real shader model 4.5 compute device.");
        }
    }
}
