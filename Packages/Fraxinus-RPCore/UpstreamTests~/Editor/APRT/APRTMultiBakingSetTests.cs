using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace UnityEngine.Rendering.Tests
{
    public class APRTMultiBakingSetTests
    {
        const string FirstScene = "aprt-multi-set-first-scene";
        const string SecondScene = "aprt-multi-set-second-scene";

        [TestCase(false)]
        [TestCase(true)]
        public void AdditiveScenesKeepBothBakingSetsLoadedAndRemoveOnlyTheirOwner(bool removeFirstSet)
        {
            using var first = new SetFixture(FirstScene, .25f);
            using var second = new SetFixture(SecondScene, 4);
            var coordinator = Coordinator();
            try
            {
                Register(coordinator, first, second);
                var firstRuntime = coordinator.GetBakingSetRuntime(first.set);
                var secondRuntime = coordinator.GetBakingSetRuntime(second.set);
                Assert.That(firstRuntime, Is.Not.SameAs(secondRuntime));
                Assert.That(coordinator.loadedBakingSetRuntimes, Is.EquivalentTo(new[] { firstRuntime, secondRuntime }));
                SelectPair(firstRuntime, .25f); SelectPair(secondRuntime, .75f);
                Update(coordinator);
                Assert.That(firstRuntime.residentCellIndices, Is.EqualTo(new[] { 0 }));
                Assert.That(secondRuntime.residentCellIndices, Is.EqualTo(new[] { 0 }));
                AssertLighting(firstRuntime, first, .25f); AssertLighting(secondRuntime, second, .75f);

                var removed = removeFirstSet ? first : second;
                var survivor = removeFirstSet ? second : first;
                var survivorRuntime = removeFirstSet ? secondRuntime : firstRuntime;
                var removedRuntime = removeFirstSet ? firstRuntime : secondRuntime;
                var retainedBuffer = survivorRuntime.runtimeSHBuffer;
                var retainedLighting = APRTStreamingTests.ReadDirectional(survivorRuntime);
                coordinator.AddPendingSceneRemoval(removed.scene);
                Update(coordinator);

                Assert.That(coordinator.GetBakingSetRuntime(removed.set), Is.Null);
                Assert.That(coordinator.loadedBakingSetRuntimes, Is.EqualTo(new[] { survivorRuntime }));
                Assert.That(removedRuntime.pendingCellRequestCount, Is.Zero);
                Assert.That(removedRuntime.runtimeSHBuffer, Is.Null);
                Assert.That(coordinator.GetBakingSetRuntime(survivor.set), Is.SameAs(survivorRuntime));
                Assert.That(survivorRuntime.runtimeSHBuffer, Is.SameAs(retainedBuffer), "Removing another Set must preserve surviving allocations.");
                Assert.That(APRTStreamingTests.ReadDirectional(survivorRuntime), Is.EqualTo(retainedLighting));

                coordinator.AddPendingSceneRemoval(survivor.scene);
                Assert.That(coordinator.loadedBakingSetRuntimes, Is.Empty);
                Assert.That(coordinator.GetVideoMemoryCost(), Is.Zero);
                Assert.That(coordinator.DataHasBeenLoaded(), Is.False);
            }
            finally { coordinator.Cleanup(); }
        }

        [TestCase(.25f, .75f)]
        [TestCase(1, 0)]
        public void IdenticalCellIndicesAndScenarioNamesKeepIndependentGpuLightingAndBlendWeights(float firstFactor, float secondFactor)
        {
            using var first = new SetFixture(FirstScene, .5f);
            using var second = new SetFixture(SecondScene, 9);
            var coordinator = Coordinator();
            try
            {
                Register(coordinator, first, second);
                var firstRuntime = coordinator.GetBakingSetRuntime(first.set);
                var secondRuntime = coordinator.GetBakingSetRuntime(second.set);
                SelectPair(firstRuntime, firstFactor); SelectPair(secondRuntime, secondFactor);
                Update(coordinator);
                Assert.That(firstRuntime.runtimeSHBuffer, Is.Not.SameAs(secondRuntime.runtimeSHBuffer));
                AssertLighting(firstRuntime, first, firstFactor); AssertLighting(secondRuntime, second, secondFactor);
                var retainedSecond = APRTStreamingTests.ReadDirectional(secondRuntime);
                var retainedBuffer = secondRuntime.runtimeSHBuffer;

                firstRuntime.scenarioBlendingFactor = 1 - firstFactor;
                AssertLighting(firstRuntime, first, 1 - firstFactor);
                Assert.That(secondRuntime.scenarioBlendingFactor, Is.EqualTo(secondFactor));
                Assert.That(secondRuntime.runtimeSHBuffer, Is.SameAs(retainedBuffer));
                Assert.That(APRTStreamingTests.ReadDirectional(secondRuntime), Is.EqualTo(retainedSecond),
                    "Updating the first Set cannot overwrite SH belonging to the same cell index in the second Set.");
                Assert.That(first.set.aprtScenarioBlendingFactor, Is.EqualTo(1 - firstFactor));
                Assert.That(second.set.aprtScenarioBlendingFactor, Is.EqualTo(secondFactor));
            }
            finally { coordinator.Cleanup(); }
        }

        [Test]
        public void RepeatedSceneRegistrationDoesNotDuplicateTheRuntimeAndASharedSetSurvivesUntilItsLastSceneLeaves()
        {
            using var first = new SetFixture(FirstScene, .25f);
            using var second = new SetFixture(SecondScene, 4);
            const string sharedScene = "aprt-multi-set-first-additional-scene";
            first.set.SetPerSceneCellLists("APRT", new Dictionary<string, List<int>>
            {
                { first.scene, new List<int> { 0 } }, { sharedScene, new List<int> { 0 } }
            });
            var coordinator = Coordinator();
            try
            {
                Register(coordinator, first, second);
                var firstRuntime = coordinator.GetBakingSetRuntime(first.set);
                var secondRuntime = coordinator.GetBakingSetRuntime(second.set);
                SelectPair(firstRuntime, .5f); SelectPair(secondRuntime, .5f);
                coordinator.AddPendingSceneLoading(first.scene, first.set);
                coordinator.AddPendingSceneLoading(sharedScene, first.set);
                Update(coordinator);
                Assert.That(coordinator.loadedBakingSetRuntimes.Count, Is.EqualTo(2));
                Assert.That(firstRuntime.residentCellCount, Is.EqualTo(1));
                coordinator.AddPendingSceneRemoval(first.scene); Update(coordinator);
                Assert.That(coordinator.GetBakingSetRuntime(first.set), Is.SameAs(firstRuntime));
                Assert.That(firstRuntime.residentCellCount, Is.EqualTo(1), "The remaining scene still owns the Set's shared cell.");
                coordinator.AddPendingSceneRemoval(sharedScene); Update(coordinator);
                Assert.That(coordinator.GetBakingSetRuntime(first.set), Is.Null);
                Assert.That(coordinator.loadedBakingSetRuntimes, Is.EqualTo(new[] { secondRuntime }));
                AssertLighting(secondRuntime, second, .5f);
            }
            finally { coordinator.Cleanup(); }
        }

        [Test]
        public void ChangingTheFocusedSetPreservesBothRegisteredSetsAndTheirGpuResources()
        {
            using var first = new SetFixture(FirstScene, .25f);
            using var second = new SetFixture(SecondScene, 4);
            var coordinator = Coordinator();
            try
            {
                Register(coordinator, first, second);
                var firstRuntime = coordinator.GetBakingSetRuntime(first.set);
                var secondRuntime = coordinator.GetBakingSetRuntime(second.set);
                SelectPair(firstRuntime, .25f); SelectPair(secondRuntime, .75f);
                Update(coordinator);
                AssertLighting(firstRuntime, first, .25f); AssertLighting(secondRuntime, second, .75f);
                var firstBuffer = firstRuntime.runtimeSHBuffer;
                var secondBuffer = secondRuntime.runtimeSHBuffer;
                coordinator.SetActiveBakingSet(second.set);
                Assert.That(coordinator.currentBakingSet, Is.SameAs(second.set));
                Assert.That(coordinator.lightingScenario, Is.EqualTo("Scenario 0"));
                Assert.That(coordinator.scenarioBlendingFactor, Is.EqualTo(.75f));
                coordinator.SetActiveBakingSet(first.set);
                Assert.That(coordinator.currentBakingSet, Is.SameAs(first.set));
                Assert.That(coordinator.scenarioBlendingFactor, Is.EqualTo(.25f));
                Assert.That(coordinator.loadedBakingSetRuntimes, Is.EquivalentTo(new[] { firstRuntime, secondRuntime }));
                Assert.That(firstRuntime.runtimeSHBuffer, Is.SameAs(firstBuffer));
                Assert.That(secondRuntime.runtimeSHBuffer, Is.SameAs(secondBuffer));
                AssertLighting(firstRuntime, first, .25f); AssertLighting(secondRuntime, second, .75f);
            }
            finally { coordinator.Cleanup(); }
        }

        [Test]
        public void AllLoadedSetsKeepEveryCellAndReportSourceAndCombinedAtlasMemory()
        {
            using var first = new SetFixture(FirstScene, .25f, cellCount: 3);
            using var second = new SetFixture(SecondScene, 4, cellCount: 3);
            var coordinator = Coordinator();
            var owner = new GameObject("APRT multi set full loading camera");
            try
            {
                var camera = owner.AddComponent<Camera>();
                Register(coordinator, first, second);
                foreach (var runtime in coordinator.loadedBakingSetRuntimes)
                {
                    runtime.lightingScenario = "Scenario 0";
                    runtime.BlendLightingScenario(null, 0);
                }
                Update(coordinator, camera);
                Assert.That(ResidentCells(coordinator.loadedBakingSetRuntimes), Is.EqualTo(6));
                Assert.That(coordinator.PrepareSamplingAtlas(coordinator.loadedBakingSetRuntimes), Is.True);
                Assert.That(coordinator.samplingAtlasMemoryBytes, Is.GreaterThan(0));
                long sourceBytes = coordinator.loadedBakingSetRuntimes.Sum(runtime => (long)runtime.GetVideoMemoryCost());
                Assert.That(coordinator.GetVideoMemoryCost(), Is.EqualTo(sourceBytes + coordinator.samplingAtlasMemoryBytes));
                var sourceBuffers = coordinator.loadedBakingSetRuntimes.Select(runtime => runtime.runtimeSHBuffer).ToArray();
                var atlas = coordinator.samplingAtlasBuffers.ToArray();
                camera.transform.position = Vector3.one * 100000;
                Update(coordinator, camera);
                Assert.That(coordinator.PrepareSamplingAtlas(coordinator.loadedBakingSetRuntimes), Is.True);
                Assert.That(ResidentCells(coordinator.loadedBakingSetRuntimes), Is.EqualTo(6));
                Assert.That(coordinator.loadedBakingSetRuntimes.Select(runtime => runtime.runtimeSHBuffer), Is.EqualTo(sourceBuffers));
                Assert.That(coordinator.samplingAtlasBuffers, Is.EqualTo(atlas));
            }
            finally { coordinator.Cleanup(); Object.DestroyImmediate(owner); }
        }

        [Test]
        public void RemovingAllPinnedSetsRetainsRecordedResourcesUntilFrameEndAndThenReleasesEveryAllocation()
        {
            using var first = new SetFixture(FirstScene, .25f);
            using var second = new SetFixture(SecondScene, 4);
            var coordinator = Coordinator();
            try
            {
                Register(coordinator, first, second);
                var firstRuntime = coordinator.GetBakingSetRuntime(first.set);
                var secondRuntime = coordinator.GetBakingSetRuntime(second.set);
                SelectPair(firstRuntime, .25f); SelectPair(secondRuntime, .75f);
                Update(coordinator);
                Assert.That(coordinator.PrepareSamplingAtlas(coordinator.loadedBakingSetRuntimes), Is.True);
                coordinator.PinBakingSetRuntimesForFrame(coordinator.loadedBakingSetRuntimes);
                var firstSource = firstRuntime.runtimeSHBuffer;
                var secondSource = secondRuntime.runtimeSHBuffer;
                var recordedAtlas = coordinator.samplingAtlasBuffers.ToArray();
                int recordedBytes = coordinator.GetVideoMemoryCost();
                Assert.That(recordedAtlas, Is.Not.Empty);

                coordinator.AddPendingSceneRemoval(first.scene);
                coordinator.AddPendingSceneRemoval(second.scene);
                Assert.That(coordinator.loadedBakingSetRuntimes, Is.Empty);
                Assert.That(firstRuntime.runtimeSHBuffer, Is.SameAs(firstSource));
                Assert.That(secondRuntime.runtimeSHBuffer, Is.SameAs(secondSource));
                Assert.That(coordinator.samplingAtlasBuffers, Is.EqualTo(recordedAtlas),
                    "An already recorded camera still owns the imported atlas until its frame ends.");
                Assert.That(coordinator.GetVideoMemoryCost(), Is.EqualTo(recordedBytes));
                Assert.That(coordinator.GetRenderingBakingSetRuntimes(null), Is.Empty,
                    "Cameras recorded after unloading must not sample retired lighting.");

                EndFrame(coordinator);
                Assert.That(firstRuntime.runtimeSHBuffer, Is.Null);
                Assert.That(secondRuntime.runtimeSHBuffer, Is.Null);
                Assert.That(coordinator.samplingAtlasBuffers, Is.Empty,
                    "With no loaded Set, no later camera will rebuild the atlas or release it on our behalf.");
                Assert.That(coordinator.GetVideoMemoryCost(), Is.Zero);
                Assert.That(coordinator.DataHasBeenLoaded(), Is.False);
                Assert.That(coordinator.GetRenderingBakingSetRuntimes(null), Is.Empty);
            }
            finally { EndFrame(coordinator); coordinator.Cleanup(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PartialSceneRemovalReleasesStaleSamplingAtlasAtFrameEndWithoutAnotherRelight(bool pinned)
        {
            using var first = new SetFixture(FirstScene, .25f);
            using var second = new SetFixture(SecondScene, 4);
            var coordinator = Coordinator();
            APRTProbeReferenceVolume survivor = null;
            try
            {
                Register(coordinator, first, second);
                survivor = coordinator.GetBakingSetRuntime(first.set);
                var removed = coordinator.GetBakingSetRuntime(second.set);
                SelectPair(survivor, .25f); SelectPair(removed, .75f);
                Update(coordinator);
                Assert.That(coordinator.PrepareSamplingAtlas(coordinator.loadedBakingSetRuntimes), Is.True);
                Assert.That(coordinator.samplingAtlasMemoryBytes, Is.GreaterThan(0));
                var recordedAtlas = coordinator.samplingAtlasBuffers.ToArray();
                var survivorSource = survivor.runtimeSHBuffer;
                int survivorBytes = survivor.GetVideoMemoryCost();
                if (pinned) coordinator.PinBakingSetRuntimesForFrame(coordinator.preparedSamplingContexts);

                coordinator.AddPendingSceneRemoval(second.scene);
                Assert.That(coordinator.loadedBakingSetRuntimes, Is.EqualTo(new[] { survivor }));
                if (pinned)
                    Assert.That(coordinator.samplingAtlasBuffers, Is.EqualTo(recordedAtlas),
                        "The atlas imported by recorded cameras must survive until their frame ends.");
                // No further camera or PrepareSamplingAtlas call follows the
                // unload: all remaining cameras may have GI disabled.
                EndFrame(coordinator);
                EndFrame(survivor);
                Assert.That(removed.runtimeSHBuffer, Is.Null);
                Assert.That(coordinator.samplingAtlasBuffers, Is.Empty);
                Assert.That(coordinator.preparedSamplingContexts, Is.Empty);
                Assert.That(coordinator.samplingAtlasMemoryBytes, Is.Zero);
                Assert.That(survivor.runtimeSHBuffer, Is.SameAs(survivorSource));
                Assert.That(coordinator.GetVideoMemoryCost(), Is.EqualTo(survivorBytes),
                    "Only the remaining Set's source allocations remain after the frame.");
                AssertLighting(survivor, first, .25f);
            }
            finally
            {
                EndFrame(coordinator);
                if (survivor != null) EndFrame(survivor);
                coordinator.Cleanup();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BakingSetsRegisteredDuringAPinnedFrameLoadFullyAfterRecordedResourcesAreReleased(bool disk)
        {
            using var first = new SetFixture(FirstScene, .25f);
            using var second = new SetFixture(SecondScene, 4, disk: disk);
            var coordinator = Coordinator(disk: disk);
            APRTProbeReferenceVolume firstRuntime = null;
            try
            {
                Register(coordinator, first);
                firstRuntime = coordinator.GetBakingSetRuntime(first.set);
                SelectPair(firstRuntime, .25f);
                Update(coordinator);
                Assert.That(firstRuntime.residentCellCount, Is.EqualTo(1));
                Assert.That(coordinator.PrepareSamplingAtlas(coordinator.loadedBakingSetRuntimes), Is.True);
                coordinator.PinBakingSetRuntimesForFrame(coordinator.preparedSamplingContexts);
                var firstSource = firstRuntime.runtimeSHBuffer;

                Register(coordinator, second);
                var addedRuntime = coordinator.GetBakingSetRuntime(second.set);
                SelectPair(addedRuntime, .75f);
                // Both entry points run for later cameras, even though the
                // coordinator cannot change this frame's pinned topology.
                for (int camera = 0; camera < 3; ++camera)
                {
                    coordinator.PerformPendingOperations();
                    Update(coordinator);
                }
                Assert.That(addedRuntime.pendingCellRequestCount, Is.Zero);
                Assert.That(addedRuntime.residentCellCount, Is.Zero);
                Assert.That(addedRuntime.runtimeSHBuffer, Is.Null);
                Assert.That(firstRuntime.runtimeSHBuffer, Is.SameAs(firstSource));
                Assert.That(coordinator.GetRenderingBakingSetRuntimes(null), Is.EqualTo(new[] { firstRuntime }));

                EndFrame(coordinator);
                EndFrame(firstRuntime);
                Update(coordinator);
                Assert.That(addedRuntime.residentCellCount, Is.EqualTo(1));
                Assert.That(addedRuntime.pendingCellRequestCount, Is.Zero);
                AssertLighting(addedRuntime, second, .75f);
                Assert.That(coordinator.GetRenderingBakingSetRuntimes(null), Does.Contain(addedRuntime));
            }
            finally
            {
                EndFrame(coordinator);
                if (firstRuntime != null) EndFrame(firstRuntime);
                coordinator.Cleanup();
            }
        }

        [Test]
        public void EveryLoadedSetKeepsBothSelectedLightingEndpoints()
        {
            using var first = new SetFixture(FirstScene, .25f, cellCount: 3);
            using var second = new SetFixture(SecondScene, 4, cellCount: 3);
            var coordinator = Coordinator();
            try
            {
                Register(coordinator, first, second);
                var firstRuntime = coordinator.GetBakingSetRuntime(first.set);
                var secondRuntime = coordinator.GetBakingSetRuntime(second.set);
                SelectPair(firstRuntime, .25f); SelectPair(secondRuntime, .75f);
                Update(coordinator);
                Assert.That(ResidentCells(coordinator.loadedBakingSetRuntimes), Is.EqualTo(6));
                foreach (var runtime in coordinator.loadedBakingSetRuntimes)
                {
                    Assert.That(runtime.residentScenarioNames, Is.EquivalentTo(new[] { "Scenario 0", "Scenario 1" }));
                    Assert.That(runtime.residentScenarioBytes, Is.EqualTo(24 * 128 * 2));
                    AssertLighting(runtime, runtime == firstRuntime ? first : second, runtime == firstRuntime ? .25f : .75f);
                }
            }
            finally { coordinator.Cleanup(); }
        }

        [TestCase(1, 3)]
        [TestCase(3, 1)]
        [TestCase(2, 2)]
        public void LegacyLoadLimitsCannotDelayCellsFromAnyLoadedBakingSet(int cellLimit, int blendLimit)
        {
            using var first = new SetFixture(FirstScene, .25f, cellCount: 3);
            using var second = new SetFixture(SecondScene, 4, cellCount: 3);
            var coordinator = Coordinator();
            try
            {
                coordinator.EnableMaxCellStreaming(false);
                coordinator.SetNumberOfCellsLoadedPerFrame(cellLimit);
                coordinator.numberOfCellsBlendedPerFrame = blendLimit;
                Register(coordinator, first, second);
                SelectPair(coordinator.GetBakingSetRuntime(first.set), .25f);
                SelectPair(coordinator.GetBakingSetRuntime(second.set), .75f);
                Update(coordinator);
                Assert.That(ResidentCells(coordinator.loadedBakingSetRuntimes), Is.EqualTo(6));
                Update(coordinator);
                Assert.That(ResidentCells(coordinator.loadedBakingSetRuntimes), Is.EqualTo(6));
                AssertLighting(coordinator.GetBakingSetRuntime(first.set), first, .25f);
                AssertLighting(coordinator.GetBakingSetRuntime(second.set), second, .75f);
            }
            finally { coordinator.Cleanup(); }
        }

        [Test]
        public void RemovingOneExternalSetPreservesTheOtherSetAndReloadsSynchronously()
        {
            using var first = new SetFixture(FirstScene, .25f, disk: true);
            using var second = new SetFixture(SecondScene, 4, disk: true);
            var coordinator = Coordinator(disk: true);
            try
            {
                Register(coordinator, first, second);
                var removedRuntime = coordinator.GetBakingSetRuntime(first.set);
                var survivor = coordinator.GetBakingSetRuntime(second.set);
                SelectPair(removedRuntime, .25f); SelectPair(survivor, .75f);
                Update(coordinator);
                coordinator.AddPendingSceneRemoval(first.scene);
                Assert.That(removedRuntime.pendingCellRequestCount, Is.Zero);
                Assert.That(removedRuntime.runtimeSHBuffer, Is.Null);
                Assert.That(coordinator.GetBakingSetRuntime(second.set), Is.SameAs(survivor));
                Update(coordinator);
                Assert.That(survivor.residentCellCount, Is.EqualTo(1));
                AssertLighting(survivor, second, .75f);
                var survivorBuffer = survivor.runtimeSHBuffer;
                var survivorLighting = APRTStreamingTests.ReadDirectional(survivor);
                coordinator.AddPendingSceneLoading(first.scene, first.set);
                var reloaded = coordinator.GetBakingSetRuntime(first.set);
                SelectPair(reloaded, .5f);
                Update(coordinator);
                Assert.That(reloaded.residentCellCount, Is.EqualTo(1));
                AssertLighting(reloaded, first, .5f);
                Assert.That(coordinator.GetBakingSetRuntime(second.set), Is.SameAs(survivor));
                Assert.That(survivor.runtimeSHBuffer, Is.SameAs(survivorBuffer));
                Assert.That(APRTStreamingTests.ReadDirectional(survivor), Is.EqualTo(survivorLighting));
                Assert.That(removedRuntime.runtimeSHBuffer, Is.Null, "Reloading the scene creates a separate context.");
                Assert.That(first.set.aprtBakedData.cells[0].axisData, Is.Empty);
                Assert.That(second.set.aprtBakedData.cells[0].axisData, Is.Empty);
            }
            finally { coordinator.Cleanup(); }
        }

        // Each set deliberately reuses local cell indices and Scenario names.
        // Distinct payloads make accidental cross-set lookup visible in GPU readback.
        sealed class SetFixture : IDisposable
        {
            internal readonly ProbeVolumeBakingSet set;
            internal readonly APRTBakedData source;
            internal readonly string scene;
            readonly string assetFolder;

            internal SetFixture(string scene, float lightingOffset, int cellCount = 1, bool disk = false)
            {
                this.scene = scene;
                var data = APRTStreamingTests.CellsData(cellCount);
                data.SetBakedLightingScenario(APRTScenarioBakingTests.BakedScenario(data, "Scenario 0", lightingOffset));
                data.SetBakedLightingScenario(APRTScenarioBakingTests.BakedScenario(data, "Scenario 1", lightingOffset + 2));
                set = APRTStreamingTests.Set(data, scenarioCount: 0);
                set.name = scene;
                set.TryAddScenario("Scenario 0"); set.TryAddScenario("Scenario 1");
                set.aprtUseLightingScenarios = true;
                set.SetPerSceneCellLists("APRT", new Dictionary<string, List<int>>
                {
                    { scene, Enumerable.Range(0, cellCount).ToList() }
                });
                var copy = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                try
                {
                    EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(set), copy);
                    source = copy.aprtBakedData;
                }
                finally { Object.DestroyImmediate(copy); }
                if (!disk) return;

                string folderName = "APRTMultiBakingSetTest_" + Guid.NewGuid().ToString("N");
                assetFolder = "Assets/" + folderName;
                AssetDatabase.CreateFolder("Assets", folderName);
                AssetDatabase.CreateAsset(set, assetFolder + "/BakingSet.asset");
                using var transaction = APRTStreamingDataWriter.Prepare(set, set.aprtBakedData);
                transaction.Publish();
                EditorUtility.SetDirty(set); AssetDatabase.SaveAssetIfDirty(set);
            }

            internal Vector4[] Expected(float factor)
            {
                var first = source.FindLightingScenario("Scenario 0").bakedDirectionalSH;
                var second = source.FindLightingScenario("Scenario 1").bakedDirectionalSH;
                return first.Zip(second, (a, b) => Vector4.LerpUnclamped(a, b, factor)).ToArray();
            }

            public void Dispose()
            {
                if (assetFolder != null) AssetDatabase.DeleteAsset(assetFolder);
                else Object.DestroyImmediate(set);
            }
        }

        static APRTProbeReferenceVolume Coordinator(bool disk = false)
        {
            APRTStreamingTests.RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            volume.Initialize(APRTStreamingTests.Parameters(disk: disk));
            volume.EnableMaxCellStreaming(true);
            return volume;
        }

        static void Update(APRTProbeReferenceVolume coordinator, Camera camera = null)
        {
            using var command = new CommandBuffer();
            APRTStreamingTests.PrepareLoadedSets(coordinator, camera);
            Graphics.ExecuteCommandBuffer(command);
        }

        static void Register(APRTProbeReferenceVolume coordinator, params SetFixture[] fixtures)
        {
            foreach (var fixture in fixtures) coordinator.AddPendingSceneLoading(fixture.scene, fixture.set);
            foreach (var fixture in fixtures)
            {
                var runtime = coordinator.GetBakingSetRuntime(fixture.set);
                Assert.That(runtime, Is.Not.Null, "Registering a scene creates its Baking Set runtime.");
                Assert.That(runtime.currentBakingSet, Is.SameAs(fixture.set));
            }
        }

        static void SelectPair(APRTProbeReferenceVolume runtime, float factor)
        {
            runtime.lightingScenario = "Scenario 0";
            runtime.BlendLightingScenario("Scenario 1", factor);
            Assert.That(runtime.lightingScenario, Is.EqualTo("Scenario 0"));
            Assert.That(runtime.otherScenario, Is.EqualTo("Scenario 1"));
            Assert.That(runtime.scenarioBlendingFactor, Is.EqualTo(factor));
        }

        static void AssertLighting(APRTProbeReferenceVolume runtime, SetFixture fixture, float factor)
        {
            APRTStreamingTests.Dispatch(runtime, fixture.set);
            APRTStreamingTests.AssertSH(APRTStreamingTests.ReadDirectional(runtime), fixture.Expected(factor),
                fixture.scene + " owns its independent Scenario data and blend weight");
        }

        static int ResidentCells(IEnumerable<APRTProbeReferenceVolume> runtimes) => runtimes.Sum(runtime => runtime.residentCellCount);

        static void EndFrame(APRTProbeReferenceVolume coordinator) => APRTStreamingTests.EndFrame(coordinator);
    }
}
