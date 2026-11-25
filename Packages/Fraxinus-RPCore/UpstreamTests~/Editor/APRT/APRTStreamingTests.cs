using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Object = UnityEngine.Object;

namespace UnityEngine.Rendering.Tests
{
    public class APRTStreamingTests
    {
        internal static ProbeVolumeSystemParameters Parameters(bool gpu = true, bool disk = false,
            bool scenarios = true, bool blend = true) => new ProbeVolumeSystemParameters
        {
            enableAPRT = true,
            supportScenarios = scenarios,
            supportScenarioBlending = blend,
            supportGPUStreaming = gpu,
            supportDiskStreaming = disk,
            memoryBudget = ProbeVolumeTextureMemoryBudget.MemoryBudgetLow,
            blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow
        };

        internal static APRTBakedData CellsData(int cellCount = 3, bool regularGrid = false)
        {
            var data = APRTSkyIrradianceTests.RGBSkyGridData(.4f);
            data.lightingModel = APRTLightingModel.APRTV;
            data.regularGrid = regularGrid;
            data.cells.Clear();
            for (int cellIndex = 0; cellIndex < cellCount; ++cellIndex)
            {
                var cell = APRTSkyIrradianceTests.RGBSkyGridData(.4f).cells[0];
                cell.index = cellIndex;
                cell.position = new Vector3Int(cellIndex * 10, 0, 0);
                for (int probe = 0; probe < cell.probeCount; ++probe) cell.probePositions[probe] += Vector3.right * (30 * cellIndex);
                if (!regularGrid)
                {
                    cell.brickPositions = new[] { new Vector3Int(cellIndex * 10, 0, 0) };
                    cell.brickSubdivisions = new[] { 0 };
                    cell.brickProbeIndices = Enumerable.Range(0, 64).Select(i => i % 8).ToArray();
                }
                data.cells.Add(cell);
            }
            data.sampleCount = cellCount * 8;
            data.globalBounds = new Bounds(new Vector3((cellCount - 1) * 15 + .5f, .5f, .5f),
                new Vector3((cellCount - 1) * 30 + 1, 1, 1));
            return data;
        }

        internal static ProbeVolumeBakingSet Set(APRTBakedData data = null, int scenarioCount = 3)
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            set.SetDefaults();
            data ??= CellsData();
            for (int i = 0; i < scenarioCount; ++i)
            {
                string name = "Scenario " + i;
                set.TryAddScenario(name);
                data.SetBakedLightingScenario(APRTScenarioBakingTests.BakedScenario(data, name, i + 1));
            }
            set.CommitAPRTBakedData(data);
            set.aprtGIIntensity = 1;
            set.aprtTemporalBlend = 1;
            return set;
        }

        [TestCase(false, false, false, false)]
        [TestCase(true, false, false, false)]
        [TestCase(false, true, true, false)]
        [TestCase(true, true, true, true)]
        public void InitializationHonorsScenariosAndDisablesAPRTStreaming(bool scenarios, bool blend, bool gpu, bool disk)
        {
            var volume = new APRTProbeReferenceVolume();
            try
            {
                volume.Initialize(Parameters(gpu, disk, scenarios, blend));
                Assert.That(volume.supportLightingScenarios, Is.EqualTo(scenarios));
                Assert.That(volume.supportScenarioBlending, Is.EqualTo(scenarios && blend));
                Assert.That(volume.diskStreamingEnabled, Is.False);
                Assert.That(volume.pendingCellRequestCount, Is.Zero);
            }
            finally { volume.Cleanup(); }
        }

        [Test]
        public void ReinitializationReleasesLoadedResourcesAndAppliesScenarioCapabilities()
        {
            RequireGPU();
            var set = Set();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                volume.Initialize(Parameters(disk: true));
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Scenario 0";
                volume.BlendLightingScenario("Scenario 1", .5f);
                Dispatch(volume, set);
                Assert.That(volume.residentScenarioNames, Is.EquivalentTo(new[] { "Scenario 0", "Scenario 1" }));
                Assert.That(volume.GetVideoMemoryCost(), Is.GreaterThan(0));
                volume.Initialize(Parameters(gpu: false, disk: true, blend: false));
                Assert.That(volume.currentBakingSet, Is.SameAs(set));
                Assert.That(volume.runtimeSHBuffer, Is.Null);
                Assert.That(volume.residentCellIndices, Is.Empty);
                Assert.That(volume.residentScenarioNames, Is.Empty);
                Assert.That(volume.pendingCellRequestCount, Is.Zero);
                Assert.That(volume.GetVideoMemoryCost(), Is.Zero);
                Assert.That(volume.diskStreamingEnabled, Is.False);
                Assert.That(volume.supportScenarioBlending, Is.False);
                PrepareLoadedSets(volume);
                Dispatch(volume, set);
                Assert.That(volume.residentCellCount, Is.EqualTo(3));
                Assert.That(volume.residentScenarioNames, Is.EqualTo(new[] { "Scenario 0" }));
                Assert.That(ReadDirectional(volume), Is.EqualTo(set.aprtBakedData.FindLightingScenario("Scenario 0").bakedDirectionalSH));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CameraMovementPreservesEveryCellAndGpuAllocation(bool legacyStreamingEnabled)
        {
            RequireGPU();
            var set = Set();
            var volume = new APRTProbeReferenceVolume();
            var owner = new GameObject("APRT full loading camera");
            try
            {
                var camera = owner.AddComponent<Camera>();
                volume.Initialize(Parameters(gpu: legacyStreamingEnabled, disk: legacyStreamingEnabled));
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Scenario 0";
                volume.SetNumberOfCellsLoadedPerFrame(1);
                PrepareLoadedSets(volume, camera);
                Assert.That(volume.residentCellIndices, Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(volume.probeCount, Is.EqualTo(24));
                var buffers = volume.gpuBuffers.ToArray();
                foreach (var position in new[] { Vector3.right * 60, Vector3.one * 100000, Vector3.left * 100000 })
                {
                    camera.transform.position = position;
                    PrepareLoadedSets(volume, camera);
                    Assert.That(volume.residentCellIndices, Is.EqualTo(new[] { 0, 1, 2 }));
                    Assert.That(volume.gpuBuffers, Is.EqualTo(buffers), "Camera movement cannot rebuild or evict APRT data.");
                }
                Dispatch(volume, set);
                Assert.That(ReadDirectional(volume), Is.EqualTo(set.aprtBakedData.FindLightingScenario("Scenario 0").bakedDirectionalSH));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(owner); Object.DestroyImmediate(set); }
        }

        [Test]
        public void FullRegularGridKeepsEveryProbeIndexWhenTheCameraMoves()
        {
            RequireGPU();
            var data = CellsData(2, true);
            data.regularGridDimensions = new Vector3Int(4, 2, 2);
            data.regularGridProbeIndices = Enumerable.Range(0, 16).ToArray();
            var set = Set(data);
            var volume = new APRTProbeReferenceVolume();
            var owner = new GameObject("APRT full grid camera");
            try
            {
                var camera = owner.AddComponent<Camera>();
                volume.Initialize(Parameters());
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Scenario 0";
                PrepareLoadedSets(volume, camera);
                var field = typeof(APRTProbeReferenceVolume).GetField("m_GridIndexBuffer", BindingFlags.NonPublic | BindingFlags.Instance);
                var buffer = (GraphicsBuffer)field.GetValue(volume);
                var indices = new int[buffer.count];
                buffer.GetData(indices);
                Assert.That(indices, Is.EqualTo(Enumerable.Range(0, 16)));
                camera.transform.position = Vector3.right * 30;
                PrepareLoadedSets(volume, camera);
                Assert.That(field.GetValue(volume), Is.SameAs(buffer));
                buffer.GetData(indices);
                Assert.That(indices, Is.EqualTo(Enumerable.Range(0, 16)));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(owner); Object.DestroyImmediate(set); }
        }

        [Test]
        public void APVCellStreamingCallbacksDoNotLoadOrReplaceAPRTBuffers()
        {
            RequireGPU();
            var set = Set();
            var volume = new APRTProbeReferenceVolume();
            using var cmd = new CommandBuffer();
            try
            {
                volume.Initialize(Parameters(disk: true));
                volume.SetActiveBakingSet(set);
                volume.UpdateCellStreaming(cmd, null);
                volume.UpdateCellStreaming(cmd, null, null);
                Assert.That(volume.residentCellIndices, Is.Empty);
                Assert.That(volume.runtimeSHBuffer, Is.Null);
                volume.PerformPendingOperations();
                PrepareLoadedSets(volume);
                var buffers = volume.gpuBuffers.ToArray();
                volume.PerformPendingOperations();
                volume.UpdateCellStreaming(cmd, null);
                Assert.That(volume.residentCellIndices, Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(volume.gpuBuffers, Is.EqualTo(buffers));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void LegacyCellAndBlendLimitsDoNotStageFullSceneLoading()
        {
            RequireGPU();
            var set = Set();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                var parameters = Parameters();
                parameters.blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.None;
                volume.Initialize(parameters);
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Scenario 0";
                volume.BlendLightingScenario("Scenario 1", .5f);
                volume.EnableMaxCellStreaming(false);
                volume.SetNumberOfCellsLoadedPerFrame(1);
                volume.numberOfCellsBlendedPerFrame = 1;
                PrepareLoadedSets(volume);
                Assert.That(volume.residentCellCount, Is.EqualTo(3));
                Assert.That(volume.probeCount, Is.EqualTo(24));
                Assert.That(volume.pendingCellRequestCount, Is.Zero);
                Assert.That(volume.residentScenarioNames, Is.EquivalentTo(new[] { "Scenario 0", "Scenario 1" }));
                Dispatch(volume, set);
                var first = set.aprtBakedData.FindLightingScenario("Scenario 0").bakedDirectionalSH;
                var second = set.aprtBakedData.FindLightingScenario("Scenario 1").bakedDirectionalSH;
                AssertSH(ReadDirectional(volume), first.Zip(second, (a, b) => Vector4.Lerp(a, b, .5f)).ToArray());
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void DisablingScenarioBlendingKeepsTheActiveEndpointAndIgnoresBlendRequests()
        {
            RequireGPU();
            var set = Set(CellsData(1));
            var volume = new APRTProbeReferenceVolume();
            try
            {
                volume.Initialize(Parameters(blend: false));
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Scenario 0";
                volume.BlendLightingScenario("Scenario 1", .5f);
                Assert.That(volume.otherScenario, Is.Null);
                Assert.That(volume.scenarioBlendingFactor, Is.Zero);
                Dispatch(volume, set);
                Assert.That(ReadDirectional(volume), Is.EqualTo(set.aprtBakedData.FindLightingScenario("Scenario 0").bakedDirectionalSH));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void SavedScenarioCountDoesNotIncreaseResidentGPUAtlasMemory()
        {
            RequireGPU();
            var few = Set(CellsData(1), 2);
            var many = Set(CellsData(1), 20);
            var first = new APRTProbeReferenceVolume();
            var second = new APRTProbeReferenceVolume();
            try
            {
                first.Initialize(Parameters());
                first.SetActiveBakingSet(few);
                first.lightingScenario = "Scenario 0";
                first.BlendLightingScenario("Scenario 1", .5f);
                Dispatch(first, few);
                second.Initialize(Parameters());
                second.SetActiveBakingSet(many);
                second.lightingScenario = "Scenario 0";
                second.BlendLightingScenario("Scenario 1", .5f);
                Dispatch(second, many);
                Assert.That(second.GetVideoMemoryCost(), Is.EqualTo(first.GetVideoMemoryCost()), "Only the active pair needs resident endpoint buffers.");
                second.BlendLightingScenario("Scenario 19", .5f);
                Dispatch(second, many);
                Assert.That(second.GetVideoMemoryCost(), Is.EqualTo(first.GetVideoMemoryCost()), "Switching targets reuses bounded endpoint capacity.");
                var a = many.aprtBakedData.FindLightingScenario("Scenario 0").bakedDirectionalSH;
                var b = many.aprtBakedData.FindLightingScenario("Scenario 19").bakedDirectionalSH;
                AssertSH(ReadDirectional(second), a.Zip(b, (x, y) => Vector4.Lerp(x, y, .5f)).ToArray());
            }
            finally { first.Cleanup(); second.Cleanup(); Object.DestroyImmediate(few); Object.DestroyImmediate(many); }
        }

        [Test]
        public void RecordedPairRetainsItsGPUResourcesWhileAnotherCameraRequestsDifferentEndpoints()
        {
            RequireGPU();
            var set = Set(CellsData(1));
            var volume = new APRTProbeReferenceVolume();
            try
            {
                volume.Initialize(Parameters());
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Scenario 0";
                volume.BlendLightingScenario("Scenario 1", .25f);
                var recorded = volume.CaptureScenarioBlend(null, null, null);
                Assert.That(recorded, Is.Not.Null);
                var buffer = volume.runtimeSHBuffer;
                volume.PinRuntimeResourcesForFrame();
                volume.lightingScenario = "Scenario 1";
                volume.BlendLightingScenario("Scenario 2", .75f);
                PrepareLoadedSets(volume);
                Assert.That(volume.runtimeSHBuffer, Is.SameAs(buffer));
                Assert.That(volume.residentScenarioNames, Is.EquivalentTo(new[] { "Scenario 0", "Scenario 1" }),
                    "A later camera cannot replace imported endpoint buffers before the recorded graph executes.");
                using var cmd = new CommandBuffer();
                volume.DispatchRelight(cmd, recorded, APRTRelightSettings.FromBakingSet(set, set.aprtBakedData.lightingModel), 93);
                Graphics.ExecuteCommandBuffer(cmd);
                var a = set.aprtBakedData.FindLightingScenario("Scenario 0").bakedDirectionalSH;
                var b = set.aprtBakedData.FindLightingScenario("Scenario 1").bakedDirectionalSH;
                AssertSH(ReadDirectional(volume), a.Zip(b, (x, y) => Vector4.Lerp(x, y, .25f)).ToArray(),
                    "Recorded camera execution keeps its original pair while the frame resources are pinned.");
                EndFrame(volume);
                PrepareLoadedSets(volume);
                Assert.That(volume.residentScenarioNames, Is.EquivalentTo(new[] { "Scenario 1", "Scenario 2" }));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void AnUnpinnedSnapshotFromAnEvictedPairCannotBindDisposedGPUResources()
        {
            RequireGPU();
            var set = Set(CellsData(1));
            var volume = new APRTProbeReferenceVolume();
            try
            {
                volume.Initialize(Parameters());
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Scenario 0";
                volume.BlendLightingScenario("Scenario 1", .25f);
                var obsolete = volume.CaptureScenarioBlend(null, null, null);
                Assert.That(obsolete, Is.Not.Null);
                volume.lightingScenario = "Scenario 1";
                volume.BlendLightingScenario("Scenario 2", .5f);
                Dispatch(volume, set, 94);
                using var cmd = new CommandBuffer();
                volume.DispatchRelight(cmd, obsolete, APRTRelightSettings.FromBakingSet(set, set.aprtBakedData.lightingModel), 93);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.Zero);
                Assert.That(volume.CanSampleForCamera(93), Is.False);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void LoadedSceneMembershipFiltersCellsAndSharedCellsSurviveUntilBothScenesLeave()
        {
            RequireGPU();
            var set = Set();
            var volume = new APRTProbeReferenceVolume();
            using var cmd = new CommandBuffer();
            try
            {
                set.SetPerSceneCellLists("APRT", new Dictionary<string, List<int>>
                {
                    { "First", new List<int> { 0, 1 } }, { "Second", new List<int> { 1, 2 } }
                });
                volume.Initialize(Parameters());
                volume.SetActiveBakingSet(set);
                volume.AddPendingSceneLoading("First", set);
                PrepareLoadedSets(volume);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(volume.residentCellIndices, Is.EquivalentTo(new[] { 0, 1 }));
                volume.AddPendingSceneLoading("Second", set);
                cmd.Clear();
                PrepareLoadedSets(volume);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(volume.residentCellIndices, Is.EquivalentTo(new[] { 0, 1, 2 }));
                volume.AddPendingSceneRemoval("First");
                cmd.Clear();
                PrepareLoadedSets(volume);
                Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(volume.residentCellIndices, Is.EquivalentTo(new[] { 1, 2 }), "Cell 1 belongs to both scenes.");
                volume.AddPendingSceneRemoval("Second");
                Assert.That(volume.residentCellIndices, Is.Empty);
                Assert.That(volume.GetVideoMemoryCost(), Is.Zero);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingOrFutureBakeReleasesPreviouslyLoadedResources(bool futureVersion)
        {
            RequireGPU();
            var set = Set();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                volume.Initialize(Parameters());
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Scenario 0";
                Dispatch(volume, set);
                Assert.That(volume.DataHasBeenLoaded(), Is.True);
                var buffer = volume.runtimeSHBuffer;
                var previous = set.aprtBakedData;
                var data = (List<ProbeVolumeBakedData>)set.backendBakedData;
                if (futureVersion) data[data.IndexOf(previous)] = new APRTBakedData { formatVersion = APRTBakedData.k_CurrentVersion + 1 };
                else data.Remove(previous);
                Assert.That(volume.PrepareRelight(null), Is.False);
                Assert.That(volume.DataHasBeenLoaded(), Is.False);
                Assert.That(volume.gpuBuffers, Is.Empty);
                Assert.That(volume.residentCellCount, Is.Zero);
                Assert.That(volume.GetVideoMemoryCost(), Is.Zero);
                Assert.That(buffer.IsValid(), Is.False);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ClearingPinnedDataReleasesItAtFrameEndWithoutAnotherCamera(bool unloadOnly)
        {
            RequireGPU();
            var set = Set();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                volume.Initialize(Parameters());
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Scenario 0";
                Dispatch(volume, set);
                var buffer = volume.runtimeSHBuffer;
                volume.PinRuntimeResourcesForFrame();
                if (unloadOnly) volume.UnloadAllCells();
                else volume.Clear();
                Assert.That(buffer.IsValid(), Is.True, "Recorded commands retain their imported buffer until frame end.");
                EndFrame(volume);
                Assert.That(buffer.IsValid(), Is.False);
                Assert.That(volume.gpuBuffers, Is.Empty);
                Assert.That(volume.residentCellCount, Is.Zero);
                Assert.That(volume.GetVideoMemoryCost(), Is.Zero);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        internal static void EndFrame(APRTProbeReferenceVolume volume) => typeof(APRTProbeReferenceVolume)
            .GetMethod("OnFrameEnd", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(volume, new object[] { default(ScriptableRenderContext), new List<Camera>() });

        internal static void PrepareLoadedSets(APRTProbeReferenceVolume volume, Camera camera = null)
        {
            var contexts = volume.GetRenderingBakingSetRuntimes(camera);
            for (int i = 0; i < contexts.Count; ++i) contexts[i].PrepareRelight(camera);
        }

        internal static void Dispatch(APRTProbeReferenceVolume volume, ProbeVolumeBakingSet set, int cameraId = 93)
        {
            var snapshot = volume.CaptureScenarioBlend(null, null, null);
            Assert.That(snapshot, Is.Not.Null);
            using var cmd = new CommandBuffer();
            volume.DispatchRelight(cmd, snapshot, APRTRelightSettings.FromBakingSet(set, set.aprtBakedData.lightingModel), cameraId);
            Graphics.ExecuteCommandBuffer(cmd);
        }

        internal static Vector4[] ReadDirectional(APRTProbeReferenceVolume volume)
        {
            Assert.That(volume.runtimeSHBuffer, Is.Not.Null);
            var result = new Vector4[volume.runtimeSHBuffer.count];
            volume.runtimeSHBuffer.GetData(result);
            return result;
        }

        internal static void AssertSH(Vector4[] actual, Vector4[] expected, string reason = "Stored SH interpolation")
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; ++i)
                Assert.That((actual[i] - expected[i]).magnitude, Is.LessThan(.00002f), reason + ", coefficient " + i);
        }

        internal static void RequireGPU()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsShaderLevel < 45)
                Assert.Ignore("Requires a real shader model 4.5 compute device.");
        }
    }
}
