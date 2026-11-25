using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace UnityEngine.Rendering.Tests
{
    public class APRTScenarioBakingTests
    {
        internal static APRTLightingScenario BakedScenario(APRTBakedData data, string name, float offset = 0)
        {
            int count = data.cells.Sum(cell => cell.probeCount) * 4;
            return new APRTLightingScenario
            {
                name = name,
                bakedDirectionalSH = Enumerable.Range(0, count).Select(i =>
                    new Vector4(offset + .1f + i * .005f, .2f + i * .003f, -.03f + i * .001f, 0)).ToArray(),
                bakedSkyIrradianceSH = Enumerable.Range(0, count).Select(i =>
                    new Vector4(.3f + i * .002f, offset + .1f + i * .004f, -.05f + i * .001f, 0)).ToArray()
            };
        }

        static ProbeVolumeBakingSet CreateSet()
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            set.SetDefaults();
            set.TryAddScenario("Day");
            set.TryAddScenario("Night");
            var data = APRTSkyIrradianceTests.RGBSkyGridData();
            data.lightingModel = APRTLightingModel.APRTV;
            data.SetBakedLightingScenario(BakedScenario(data, "Day"));
            data.SetBakedLightingScenario(BakedScenario(data, "Night", 1));
            set.CommitAPRTBakedData(data);
            set.aprtGIIntensity = 1;
            set.aprtTemporalBlend = 1;
            return set;
        }

        [Test]
        public void StoredProbeLightingOwnsItsArraysAndSurvivesSerializationWithSharedPlacement()
        {
            var set = CreateSet();
            var copy = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                var data = set.aprtBakedData;
                var source = BakedScenario(data, "Dusk", 2);
                var expectedDirectional = (Vector4[])source.bakedDirectionalSH.Clone();
                var expectedSky = (Vector4[])source.bakedSkyIrradianceSH.Clone();
                long sharedBytes = data.GetDiskSizeOfSharedData();
                data.SetBakedLightingScenario(source);
                Array.Clear(source.bakedDirectionalSH, 0, source.bakedDirectionalSH.Length);
                Array.Clear(source.bakedSkyIrradianceSH, 0, source.bakedSkyIrradianceSH.Length);
                Assert.That(data.FindLightingScenario("Dusk").bakedDirectionalSH, Is.EqualTo(expectedDirectional));
                Assert.That(data.FindLightingScenario("Dusk").bakedSkyIrradianceSH, Is.EqualTo(expectedSky));
                Assert.That(data.GetDiskSizeOfSharedData(), Is.EqualTo(sharedBytes));
                Assert.That(data.GetDiskSizeOfScenarioData("Dusk"), Is.GreaterThanOrEqualTo((long)expectedDirectional.Length * 2 * 16));
                Assert.That(data.scenarioCount, Is.EqualTo(3));

                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(set), copy);
                Assert.That(copy.aprtBakedData.HasBakedLightingScenario("Dusk"), Is.True);
                Assert.That(copy.aprtBakedData.FindLightingScenario("Dusk").bakedDirectionalSH, Is.EqualTo(expectedDirectional));
                Assert.That(copy.aprtBakedData.FindLightingScenario("Dusk").bakedSkyIrradianceSH, Is.EqualTo(expectedSky));
                Assert.That(copy.aprtBakedData.regularGridProbeIndices, Is.EqualTo(data.regularGridProbeIndices));
                Assert.That(copy.aprtBakedData.cells[0].probePositions, Is.EqualTo(data.cells[0].probePositions));
                Assert.That(copy.aprtBakedData.TryGetScenarioHash("Day", out int day), Is.True);
                Assert.That(copy.aprtBakedData.TryGetScenarioHash("Night", out int night), Is.True);
                Assert.That(day, Is.EqualTo(night), "Both scenarios use exactly the same probe layout.");
            }
            finally { Object.DestroyImmediate(set); Object.DestroyImmediate(copy); }
        }

        [Test]
        public void LegacyLightingInputCaptureIsNotReportedAsABakedScenario()
        {
            var data = APRTSkyIrradianceTests.SkyGridData();
            data.SetLightingScenario(new APRTLightingScenario { name = "Day" });
            Assert.That(data.FindLightingScenario("Day"), Is.Not.Null, "Retain legacy inputs for migration.");
            Assert.That(data.HasBakedLightingScenario("Day"), Is.False);
            Assert.That(data.HasBakedData("Day", true), Is.False);
            Assert.That(data.HasValidScenarioData("Day", new APRTProbeReferenceVolume()), Is.False);
            Assert.That(data.TryGetScenarioHash("Day", out _), Is.False);
            Assert.That(data.scenarioCount, Is.Zero);
        }

        [TestCase("missing-directional")]
        [TestCase("missing-sky")]
        [TestCase("short-directional")]
        [TestCase("short-sky")]
        [TestCase("nan-directional")]
        [TestCase("infinite-sky")]
        public void InvalidBakedSHCannotReplaceTheLastCompleteScenario(string fault)
        {
            var set = CreateSet();
            try
            {
                var data = set.aprtBakedData;
                var previous = data.FindLightingScenario("Day");
                var invalid = BakedScenario(data, "Day");
                switch (fault)
                {
                    case "missing-directional": invalid.bakedDirectionalSH = null; break;
                    case "missing-sky": invalid.bakedSkyIrradianceSH = null; break;
                    case "short-directional": invalid.bakedDirectionalSH = new Vector4[4]; break;
                    case "short-sky": invalid.bakedSkyIrradianceSH = new Vector4[4]; break;
                    case "nan-directional": invalid.bakedDirectionalSH[0].x = float.NaN; break;
                    case "infinite-sky": invalid.bakedSkyIrradianceSH[0].z = float.PositiveInfinity; break;
                }
                Assert.Throws<ArgumentException>(() => data.SetBakedLightingScenario(invalid));
                Assert.That(data.FindLightingScenario("Day"), Is.SameAs(previous));
                Assert.That(data.HasBakedLightingScenario("Day"), Is.True);
                Assert.That(data.scenarioCount, Is.EqualTo(2));
            }
            finally { Object.DestroyImmediate(set); }
        }

        [TestCase("probe-position")]
        [TestCase("grid-index")]
        [TestCase("sampling-origin")]
        public void EqualProbeCountCannotHideAnIncompatibleScenarioLayout(string fault)
        {
            var set = CreateSet();
            try
            {
                var data = set.aprtBakedData;
                switch (fault)
                {
                    case "probe-position": data.cells[0].probePositions[0] += Vector3.right * .125f; break;
                    case "grid-index":
                        (data.regularGridProbeIndices[0], data.regularGridProbeIndices[1]) =
                            (data.regularGridProbeIndices[1], data.regularGridProbeIndices[0]);
                        break;
                    case "sampling-origin": data.regularGridSamplingOrigin += Vector3.up; break;
                }
                Assert.That(data.HasValidSharedData(), Is.True);
                Assert.That(data.HasBakedLightingScenario("Day"), Is.False);
                Assert.That(data.HasBakedData("Day", true), Is.False);
                Assert.That(data.TryGetScenarioHash("Day", out _), Is.False);
            }
            finally { Object.DestroyImmediate(set); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void RebakingActiveScenarioRetainsOtherProbeLightingOnlyForIdenticalPlacement(bool identicalLayout)
        {
            var set = CreateSet();
            try
            {
                var oldNight = set.aprtBakedData.FindLightingScenario("Night");
                var replacement = APRTSkyIrradianceTests.RGBSkyGridData(.2f);
                replacement.lightingModel = APRTLightingModel.APRTV;
                if (!identicalLayout) replacement.cells[0].probePositions[0] += Vector3.right * .125f;
                var newDay = BakedScenario(replacement, "Day", 5);
                replacement.SetBakedLightingScenario(newDay);
                set.CommitAPRTBakedData(replacement);
                Assert.That(replacement.FindLightingScenario("Day").bakedDirectionalSH, Is.EqualTo(newDay.bakedDirectionalSH));
                Assert.That(replacement.HasBakedLightingScenario("Night"), Is.EqualTo(identicalLayout));
                if (identicalLayout)
                {
                    Assert.That(replacement.FindLightingScenario("Night").bakedDirectionalSH, Is.EqualTo(oldNight.bakedDirectionalSH));
                    Assert.That(replacement.FindLightingScenario("Night").bakedDirectionalSH, Is.Not.SameAs(oldNight.bakedDirectionalSH));
                    Assert.That(replacement.FindLightingScenario("Night").bakedSkyIrradianceSH, Is.EqualTo(oldNight.bakedSkyIrradianceSH));
                }
            }
            finally { Object.DestroyImmediate(set); }
        }

        [Test]
        public void FrozenPlacementPreservesProbeOrderAndIndicesWithIndependentCaptureArrays()
        {
            var original = APRTSkyIrradianceTests.RGBSkyGridData(.4f);
            original.lightingModel = APRTLightingModel.APRTV;
            original.samplingDomains = new[] { Matrix4x4.TRS(Vector3.one * .5f, Quaternion.identity, Vector3.one) };
            original.cells[0].index = 7;
            original.SetBakedLightingScenario(BakedScenario(original, "Day"));
            var frozen = APRTProbeVolumeBaking.CreateFrozenLayout(original, true);
            Assert.That(frozen.HasSameProbeLayout(original), Is.True);
            Assert.That(frozen.GetLayoutHash(), Is.EqualTo(original.GetLayoutHash()));
            Assert.That(frozen.regularGridProbeIndices, Is.EqualTo(original.regularGridProbeIndices));
            Assert.That(frozen.regularGridProbeIndices, Is.Not.SameAs(original.regularGridProbeIndices));
            Assert.That(frozen.samplingDomains, Is.EqualTo(original.samplingDomains));
            Assert.That(frozen.samplingDomains, Is.Not.SameAs(original.samplingDomains));
            Assert.That(frozen.cells.Count, Is.EqualTo(original.cells.Count));
            var source = original.cells[0];
            var destination = frozen.cells[0];
            Assert.That(destination.index, Is.EqualTo(source.index));
            Assert.That(destination.position, Is.EqualTo(source.position));
            Assert.That(destination.probePositions, Is.EqualTo(source.probePositions));
            Assert.That(destination.probePositions, Is.Not.SameAs(source.probePositions));
            Assert.That(destination.brickPositions, Is.EqualTo(source.brickPositions));
            Assert.That(destination.brickSubdivisions, Is.EqualTo(source.brickSubdivisions));
            Assert.That(destination.brickProbeIndices, Is.EqualTo(source.brickProbeIndices));
            Assert.That(destination.axisData, Is.Not.SameAs(source.axisData));
            Assert.That(destination.skyIrradianceTransfer, Is.Not.SameAs(source.skyIrradianceTransfer));
            Assert.That(destination.skyIrradianceTransferRGB, Is.Not.SameAs(source.skyIrradianceTransferRGB));
            Assert.That(destination.axisData, Is.All.EqualTo(Vector4.zero));
            Assert.That(destination.skyIrradianceTransfer, Is.All.EqualTo(0));
            Assert.That(destination.skyIrradianceTransferRGB, Is.All.EqualTo(Vector3.zero));
            Assert.That(frozen.FindLightingScenario("Day"), Is.Null, "Only commit inherits scenarios after the complete capture validates.");
            destination.axisData[0] = Vector4.one;
            Assert.That(source.axisData[0], Is.Not.EqualTo(destination.axisData[0]));
        }

        [Test]
        public void GPUBlendUsesStoredProbeLightingWithLiveInputsAndArtistSettingsChanged()
        {
            RequireGPU();
            var set = CreateSet();
            var volume = new APRTProbeReferenceVolume();
            var options = ScriptableObject.CreateInstance<APRTOptions>();
            try
            {
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true, supportScenarios = true, supportScenarioBlending = true, blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow });
                volume.SetActiveBakingSet(set);
                var day = set.aprtBakedData.FindLightingScenario("Day");
                var night = set.aprtBakedData.FindLightingScenario("Night");
                volume.lightingScenario = "Day";
                var live = new APRTLightingScenario { name = "Unused live input" };
                live.colors[0] = new Vector4(500, 200, 100, 1);
                live.directions[0] = new Vector4(0, 1, 0, 0);
                live.skyRadiance[0] = new Vector4(100, 200, 300, 0);
                set.aprtIndirectIntensity = 0;
                set.aprtIndirectContrast = 15;
                set.aprtSaturationBoost = 5;
                set.aprtFloorBoost = 15;
                set.aprtSkyIntensity = set.aprtReferenceSkyIntensity = 0;
                options.indirectIntensity.Override(10);
                options.indirectContrast.Override(15);
                options.saturationBoost.Override(0);
                options.floorBoost.Override(0);
                options.skyIntensity.Override(0);
                foreach (float weight in new[] { 0f, .5f, 1f })
                {
                    volume.BlendLightingScenario("Night", weight);
                    using var cmd = new CommandBuffer();
                    volume.DispatchRelight(cmd, volume.CaptureScenarioBlend(live.colors, live.directions, live.skyRadiance),
                        APRTRelightSettings.Resolve(set, set.aprtBakedData.lightingModel, options), 73);
                    Graphics.ExecuteCommandBuffer(cmd);
                    AssertBuffer(volume.runtimeSHBuffer, day.bakedDirectionalSH, night.bakedDirectionalSH, weight);
                    AssertBuffer(volume.runtimeSkyIrradianceBuffer, day.bakedSkyIrradianceSH, night.bakedSkyIrradianceSH, weight);
                }
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); Object.DestroyImmediate(options); }
        }

        [Test]
        public void OfflineScenarioEvaluationStoresTheSameDirectionalAndSkySHAsLiveRelighting()
        {
            RequireGPU();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                set.SetDefaults();
                var data = APRTSkyIrradianceTests.RGBSkyGridData(.4f);
                data.lightingModel = APRTLightingModel.APRTV;
                set.CommitAPRTBakedData(data);
                set.aprtGIIntensity = 1;
                set.aprtTemporalBlend = 1;
                set.aprtIndirectIntensity = .8f;
                set.aprtIndirectContrast = 2;
                set.aprtReferenceSkyIntensity = .7f;
                var inputs = new APRTLightingScenario { name = "Day" };
                inputs.colors[0] = new Vector4(2, .5f, .2f, 1);
                inputs.directions[0] = new Vector4(0, 1, 0, 0);
                inputs.skyRadiance[0] = new Vector4(1, 2, 3, 0);
                inputs.skyRadiance[3] = new Vector4(.2f, -.1f, .4f, 0);
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true, supportScenarios = true, supportScenarioBlending = true, blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow });
                volume.SetActiveBakingSet(set);
                var settings = APRTRelightSettings.FromBakingSet(set, data.lightingModel);
                using var cmd = new CommandBuffer();
                volume.DispatchRelight(cmd, volume.CaptureScenarioBlend(inputs.colors, inputs.directions, inputs.skyRadiance), settings, 75);
                Graphics.ExecuteCommandBuffer(cmd);
                APRTProbeVolumeBaking.EvaluateBakedLighting(data, inputs, settings);
                AssertBuffer(volume.runtimeSHBuffer, inputs.bakedDirectionalSH, inputs.bakedDirectionalSH, 0);
                AssertBuffer(volume.runtimeSkyIrradianceBuffer, inputs.bakedSkyIrradianceSH, inputs.bakedSkyIrradianceSH, 0);
                Assert.That(inputs.bakedDirectionalSH.Max(coefficient => coefficient.magnitude), Is.GreaterThan(.001f));
                Assert.That(inputs.bakedSkyIrradianceSH.Max(coefficient => coefficient.magnitude), Is.GreaterThan(.01f));
                Assert.That(data.FindLightingScenario("Day"), Is.Null, "Evaluation alone must not publish a scenario into the asset.");
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase("sky-count")]
        [TestCase("nonfinite-directional")]
        [TestCase("layout-hash")]
        public void BrokenBakedBlendEndpointCannotSilentlyFallBackToItsLegacyInputs(string fault)
        {
            var set = CreateSet();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true, supportScenarios = true, supportScenarioBlending = true, blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow });
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Day";
                volume.BlendLightingScenario("Night", .4f);
                var night = set.aprtBakedData.FindLightingScenario("Night");
                switch (fault)
                {
                    case "sky-count": night.bakedSkyIrradianceSH = new Vector4[4]; break;
                    case "nonfinite-directional": night.bakedDirectionalSH[0].x = float.NaN; break;
                    case "layout-hash": night.bakedLayoutHash ^= 1; break;
                }
                Assert.That(set.aprtBakedData.HasBakedLightingScenario("Night"), Is.False);
                Assert.That(volume.CaptureScenarioBlend(new Vector4[4], new Vector4[4], new Vector4[9]), Is.Null,
                    "A failed stored probe endpoint cannot switch to relighting current or captured input values.");
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void RecordedBakedBlendOwnsBothOutputArraysAndCannotObserveLaterRebakes()
        {
            RequireGPU();
            var set = CreateSet();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true, supportScenarios = true, supportScenarioBlending = true, blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow });
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Day";
                volume.BlendLightingScenario("Night", .5f);
                var day = set.aprtBakedData.FindLightingScenario("Day").Clone();
                var night = set.aprtBakedData.FindLightingScenario("Night").Clone();
                var snapshot = volume.CaptureScenarioBlend(null, null, null);
                Assert.That(snapshot, Is.Not.Null);
                volume.PinRuntimeResourcesForFrame();
                set.aprtBakedData.SetBakedLightingScenario(BakedScenario(set.aprtBakedData, "Day", 100));
                set.aprtBakedData.SetBakedLightingScenario(BakedScenario(set.aprtBakedData, "Night", 200));
                volume.lightingScenario = "Night";
                using var cmd = new CommandBuffer();
                volume.DispatchRelight(cmd, snapshot, APRTRelightSettings.FromBakingSet(set, set.aprtBakedData.lightingModel), 74);
                Graphics.ExecuteCommandBuffer(cmd);
                AssertBuffer(volume.runtimeSHBuffer, day.bakedDirectionalSH, night.bakedDirectionalSH, .5f);
                AssertBuffer(volume.runtimeSkyIrradianceBuffer, day.bakedSkyIrradianceSH, night.bakedSkyIrradianceSH, .5f);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void UndoingAStoredScenarioBakeReloadsTheAtlasWithoutAnOpenInspector()
        {
            RequireGPU();
            var set = CreateSet();
            var volume = APRTProbeReferenceVolume.instance;
            var previousSet = volume.currentBakingSet;
            bool wasInitialized = volume.isInitialized;
            bool wasEnabled = volume.enabledBySRP;
            try
            {
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true, supportScenarios = true, supportScenarioBlending = true, blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow });
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Day";
                var original = set.aprtBakedData.FindLightingScenario("Day").Clone();
                using var cmd = new CommandBuffer();
                volume.DispatchRelight(cmd, volume.CaptureScenarioBlend(null, null, null),
                    APRTRelightSettings.FromBakingSet(set, set.aprtBakedData.lightingModel), 76);
                Graphics.ExecuteCommandBuffer(cmd);
                AssertBuffer(volume.runtimeSHBuffer, original.bakedDirectionalSH, original.bakedDirectionalSH, 0);

                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Bake APRT Scenario Test");
                Undo.RegisterCompleteObjectUndo(set, "Bake APRT Scenario Test");
                var replacement = BakedScenario(set.aprtBakedData, "Day", 100);
                set.aprtBakedData.SetBakedLightingScenario(replacement);
                EditorUtility.SetDirty(set);
                Undo.FlushUndoRecordObjects();
                cmd.Clear();
                volume.DispatchRelight(cmd, volume.CaptureScenarioBlend(null, null, null),
                    APRTRelightSettings.FromBakingSet(set, set.aprtBakedData.lightingModel), 76);
                Graphics.ExecuteCommandBuffer(cmd);
                AssertBuffer(volume.runtimeSHBuffer, replacement.bakedDirectionalSH, replacement.bakedDirectionalSH, 0);

                Undo.IncrementCurrentGroup();
                Undo.PerformUndo();
                Assert.That(set.aprtBakedData.FindLightingScenario("Day").bakedDirectionalSH, Is.EqualTo(original.bakedDirectionalSH));
                Assert.That(volume.DataHasBeenLoaded(), Is.False, "The runtime Undo callback invalidates the atlas without an Inspector repaint.");
                cmd.Clear();
                volume.DispatchRelight(cmd, volume.CaptureScenarioBlend(null, null, null),
                    APRTRelightSettings.FromBakingSet(set, set.aprtBakedData.lightingModel), 76);
                Graphics.ExecuteCommandBuffer(cmd);
                AssertBuffer(volume.runtimeSHBuffer, original.bakedDirectionalSH, original.bakedDirectionalSH, 0);
                AssertBuffer(volume.runtimeSkyIrradianceBuffer, original.bakedSkyIrradianceSH, original.bakedSkyIrradianceSH, 0);
            }
            finally
            {
                Undo.ClearUndo(set);
                if (!wasInitialized) volume.Cleanup();
                volume.SetActiveBakingSet(previousSet);
                if (wasInitialized) volume.SetEnableStateFromSRP(wasEnabled);
                Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void SelectingAnUnbakedScenarioDisablesPreviouslyRenderedLightingAndLaterResourceBinding()
        {
            RequireGPU();
            var set = CreateSet();
            var volume = new APRTProbeReferenceVolume();
            using var cmd = new CommandBuffer();
            try
            {
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true, supportScenarios = true, supportScenarioBlending = true, blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow });
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Day";
                var settings = APRTRelightSettings.FromBakingSet(set, set.aprtBakedData.lightingModel);
                volume.DispatchRelight(cmd, volume.CaptureScenarioBlend(null, null, null), settings, 77);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.EqualTo(1));
                Assert.That(volume.DataHasBeenLoaded(), Is.True);
                Assert.That(volume.CanSampleForCamera(77), Is.True);

                set.TryAddScenario("Unbaked");
                set.SetActiveScenario("Unbaked", false, volume);
                var unavailable = volume.CaptureScenarioBlend(null, null, null);
                Assert.That(unavailable, Is.Null);
                cmd.Clear();
                volume.DispatchRelight(cmd, unavailable, settings, 77);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.Zero);
                Assert.That(volume.DataHasBeenLoaded(), Is.False);
                Assert.That(volume.CanSampleForCamera(77), Is.False);

                cmd.Clear();
                volume.BindPRVRuntimeResources(cmd, true);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.Zero, "Later resource binding must not reactivate Day's stale output.");
                Assert.That(Shader.GetGlobalFloat("_APRTGIIntensity"), Is.Zero);
                Assert.That(Shader.GetGlobalInt("_APRTProbeCount"), Is.Zero);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancelOrChangingActiveScenarioDuringAsyncBakeRetainsThePreviousCompleteBake(bool changeSelection)
        {
            using var fixture = new BakeFixture();
            var baker = new APRTProbeVolumeBaking(fixture.volume, fixture.set);
            try
            {
                var previous = fixture.set.aprtBakedData;
                var previousDay = previous.FindLightingScenario("Day");
                Assert.That(baker.BakeStandaloneAsync(), Is.True);
                Assert.That(fixture.set.aprtBakedData, Is.SameAs(previous));
                if (changeSelection)
                {
                    fixture.set.SetActiveScenario("Night", false, fixture.volume);
                    LogAssert.Expect(LogType.Error, "APRT lighting scenario selection changed during baking. The previous bake was retained; bake again after editing.");
                    typeof(APRTProbeVolumeBaking).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(baker, null);
                }
                else Assert.That(baker.Cancel(), Is.True);
                Assert.That(baker.isRunning, Is.False);
                Assert.That(baker.committed, Is.False);
                Assert.That(fixture.set.aprtBakedData, Is.SameAs(previous));
                Assert.That(previous.FindLightingScenario("Day"), Is.SameAs(previousDay));
                Assert.That(previous.HasBakedLightingScenario("Night"), Is.True);
            }
            finally { baker.Dispose(); }
        }

        [Test]
        public void CompleteMultisceneScenarioBakeFreezesPlacementAndPreservesPreviousLightingAcrossMaterialAndLightChanges()
        {
            RequireGPU();
            using var fixture = new BakeFixture();
            var set = fixture.set;
            set.aprtBakedData.ClearAssets();
            set.aprtCaptureResolution = 8;
            set.aprtCaptureDistance = 20;
            set.aprtBakeSaturationPriority = set.aprtBakeCenterWeight = 0;
            set.aprtIndirectIntensity = 1;
            set.aprtIndirectContrast = 1;
            set.aprtSaturationBoost = 1;
            set.aprtShadowGIVisibility = .1f;
            set.aprtSkyVisibilityPower = 1;
            var second = fixture.AddScene("Second");
            var outside = fixture.AddScene("Outside", false);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.That(shader, Is.Not.Null);
            var surface = new Material(shader);
            try
            {
                surface.SetColor("_BaseColor", new Color(.8f, .04f, .02f));
                var ground = fixture.AddCube(fixture.scene, new Vector3(.5f, -1, .5f), new Vector3(20, .2f, 20), surface);
                var wall = fixture.AddCube(second, new Vector3(2.5f, .5f, .5f), new Vector3(.2f, 8, 8), surface);
                var firstSun = fixture.AddLight(fixture.scene, new Color(1, .2f, .05f), 2);
                var secondSun = fixture.AddLight(second, new Color(.1f, .2f, 1), 1);
                fixture.AddLight(outside, Color.white, 100);
                fixture.RunBakeToCompletion();
                var first = set.aprtBakedData;
                Assert.That(first.totalProbeCount, Is.EqualTo(8));
                Assert.That(first.HasBakedLightingScenario("Day"), Is.True);
                Assert.That(first.HasBakedLightingScenario("Night"), Is.False, "A bake stores only the selected Scenario.");
                Assert.That(set.aprtUseLightingScenarios, Is.True);
                var savedDay = ReadBakedScenario(first, "Day");
                Assert.That(first.cells[0].streamableAsset, Is.Null, "New bakes embed their probe payload in the Baking Set.");
                Assert.That(first.FindLightingScenario("Day").streamableAssets, Is.Null.Or.Empty);
                var firstTransport = first.cells[0].axisData;
                Assert.That(savedDay.colors.Count(color => color.w == 1), Is.EqualTo(2), "Both loaded Baking Set scenes contribute lights; the outside scene is excluded.");
                Assert.That(savedDay.bakedDirectionalSH.Max(coefficient => coefficient.magnitude), Is.GreaterThan(.001f));
                var sceneLists = set.GetPerSceneCellLists("APRT");
                Assert.That(sceneLists.Keys, Is.EquivalentTo(set.sceneGUIDs));
                foreach (string guid in set.sceneGUIDs)
                    Assert.That(sceneLists[guid], Is.EquivalentTo(first.cells.Select(cell => cell.index)));
                Assert.That(APRTProbeVolumeBaking.CanFreezePlacement(fixture.volume, set), Is.True);

                firstSun.color = new Color(.05f, .1f, 1);
                firstSun.intensity = .15f;
                secondSun.intensity = 0;
                surface.SetColor("_BaseColor", new Color(.02f, .05f, .8f));
                wall.transform.position += Vector3.right;
                EditorUtility.SetDirty(surface);
                set.freezePlacement = true;
                set.SetActiveScenario("Night", false, fixture.volume);
                Assert.That(APRTProbeVolumeBaking.CanFreezePlacement(fixture.volume, set), Is.True, "Material and geometry changes may be recaptured at the frozen probe positions.");
                fixture.RunBakeToCompletion();
                var last = set.aprtBakedData;
                Assert.That(last.HasSameProbeLayout(first), Is.True);
                Assert.That(last.HasBakedLightingScenario("Day"), Is.True);
                Assert.That(last.HasBakedLightingScenario("Night"), Is.True);
                Assert.That(last.scenarioCount, Is.EqualTo(2));
                var restoredDay = ReadBakedScenario(last, "Day");
                var savedNight = ReadBakedScenario(last, "Night");
                Assert.That(restoredDay.bakedDirectionalSH, Is.EqualTo(savedDay.bakedDirectionalSH), "Rebaking Night must retain Day's evaluated output.");
                Assert.That(restoredDay.bakedSkyIrradianceSH, Is.EqualTo(savedDay.bakedSkyIrradianceSH));
                Assert.That(savedNight.bakedDirectionalSH.Zip(savedDay.bakedDirectionalSH,
                    (a, b) => (a - b).magnitude).Max(), Is.GreaterThan(.001f));
                Assert.That(last.cells[0].streamableAsset, Is.Null);
                Assert.That(last.FindLightingScenario("Night").streamableAssets, Is.Null.Or.Empty);
                var lastTransport = last.cells[0].axisData;
                Assert.That(lastTransport.Zip(firstTransport, (a, b) => (a - b).magnitude).Max(), Is.GreaterThan(.001f), "Night actually recaptures changed surface transport.");
                Assert.That(last.TryGetScenarioHash("Day", out int dayHash), Is.True);
                Assert.That(last.TryGetScenarioHash("Night", out int nightHash), Is.True);
                Assert.That(dayHash, Is.EqualTo(nightHash));
                Assert.That(ground.GetComponent<Renderer>().sharedMaterial, Is.SameAs(surface), "Baking preserves the scene material references.");
                fixture.region.fillEmptySpaces = !fixture.region.fillEmptySpaces;
                Assert.That(APRTProbeVolumeBaking.CanFreezePlacement(fixture.volume, set), Is.False, "Changing placement settings requires recalculating the layout.");
            }
            finally { Object.DestroyImmediate(surface); }
        }

        static APRTLightingScenario ReadBakedScenario(APRTBakedData data, string name)
        {
            var scenario = data.FindLightingScenario(name).Clone();
            if (scenario.streamableAssets == null || scenario.streamableAssets.Count == 0) return scenario;
            var cells = data.cells.Select(cell => APRTDiskData.ReadScenarioSync(scenario, cell.index, cell.probeCount, data.GetLayoutHash())).ToArray();
            scenario.bakedDirectionalSH = cells.SelectMany(cell => cell.directionalSH).ToArray();
            scenario.bakedSkyIrradianceSH = cells.SelectMany(cell => cell.skyIrradianceSH).ToArray();
            return scenario;
        }

        static void AssertBuffer(GraphicsBuffer buffer, Vector4[] first, Vector4[] second, float weight)
        {
            Assert.That(buffer, Is.Not.Null);
            Assert.That(buffer.count, Is.EqualTo(first.Length));
            var actual = new Vector4[buffer.count];
            buffer.GetData(actual);
            for (int i = 0; i < actual.Length; ++i)
                Assert.That((actual[i] - Vector4.Lerp(first[i], second[i], weight)).magnitude, Is.LessThan(.00002f), "SH coefficient " + i);
        }

        static void RequireGPU()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45)
                Assert.Ignore("Requires a real shader model 4.5 compute device.");
        }

        sealed class BakeFixture : IDisposable
        {
            internal readonly ProbeVolumeBakingSet set;
            internal readonly APRTProbeReferenceVolume volume = new();
            internal readonly APRTVolume region;
            readonly Scene previousScene = SceneManager.GetActiveScene();
            internal readonly Scene scene;
            readonly string folder;
            readonly System.Collections.Generic.List<Scene> additionalScenes = new();

            internal BakeFixture()
            {
                string name = "APRTScenarioBakeTest_" + Guid.NewGuid().ToString("N");
                folder = "Assets/" + name;
                AssetDatabase.CreateFolder("Assets", name);
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.That(EditorSceneManager.SaveScene(scene, folder + "/Bake.unity"), Is.True);
                SceneManager.SetActiveScene(scene);
                RenderSettings.skybox = null;
                RenderSettings.sun = null;
                set = CreateSet();
                set.AddScene(scene.GetGUID());
                set.freezePlacement = false;
                AssetDatabase.CreateAsset(set, folder + "/Bake.asset");
                var go = new GameObject("APRT Scenario Test Grid");
                SceneManager.MoveGameObjectToScene(go, scene);
                region = go.AddComponent<APRTVolume>();
                region.layout = APRTVolume.Layout.ReferenceGrid;
                region.regularGridDimensions = new Vector3Int(2, 2, 2);
                region.regularGridSpacing = 1;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true, supportScenarios = true, supportScenarioBlending = true, blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow });
                volume.SetActiveBakingSet(set);
                set.SetActiveScenario("Day", false, volume);
            }

            internal Scene AddScene(string name, bool inSet = true)
            {
                var added = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.That(EditorSceneManager.SaveScene(added, folder + "/" + name + ".unity"), Is.True);
                additionalScenes.Add(added);
                if (inSet) set.AddScene(added.GetGUID());
                SceneManager.SetActiveScene(scene);
                return added;
            }

            internal Light AddLight(Scene target, Color color, float intensity)
            {
                var go = new GameObject("Scenario Bake Test Sun");
                SceneManager.MoveGameObjectToScene(go, target);
                var light = go.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = color;
                light.intensity = intensity;
                light.transform.rotation = Quaternion.Euler(45, 25, 0);
                return light;
            }

            internal GameObject AddCube(Scene target, Vector3 position, Vector3 scale, Material material)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Scenario Bake Test Surface";
                SceneManager.MoveGameObjectToScene(go, target);
                go.transform.position = position;
                go.transform.localScale = scale;
                go.GetComponent<Renderer>().sharedMaterial = material;
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI);
                return go;
            }

            internal void RunBakeToCompletion()
            {
                var baker = new APRTProbeVolumeBaking(volume, set);
                try
                {
                    Assert.That(baker.BakeStandaloneAsync(), Is.True);
                    var tick = typeof(APRTProbeVolumeBaking).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
                    for (int i = 0; i < 20 && baker.isRunning; ++i) tick.Invoke(baker, null);
                    Assert.That(baker.isRunning, Is.False, "The eight-probe bake must finish within twenty editor ticks.");
                    Assert.That(baker.committed, Is.True);
                }
                finally { baker.Dispose(); }
            }

            public void Dispose()
            {
                volume.Cleanup();
                if (set != null) set.RemoveScene(scene.GetGUID());
                foreach (var added in additionalScenes)
                {
                    if (set != null && set.sceneGUIDs.Contains(added.GetGUID())) set.RemoveScene(added.GetGUID());
                    if (added.IsValid() && added.isLoaded) EditorSceneManager.CloseScene(added, true);
                }
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                if (previousScene.IsValid() && previousScene.isLoaded) SceneManager.SetActiveScene(previousScene);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
