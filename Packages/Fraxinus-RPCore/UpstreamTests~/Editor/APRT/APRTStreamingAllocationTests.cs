using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using Object = UnityEngine.Object;

namespace UnityEngine.Rendering.Tests
{
    public class APRTStreamingAllocationTests
    {
        const string Scene = "aprt-streaming-allocation-test";

        [Test]
        public void RegisteredContextEndDoesNotAllocateAndReleasesDeferredResources()
        {
            APRTStreamingTests.RequireGPU();
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var manager = typeof(RenderPipelineManager);
            var legacyEvent = manager.GetField("endFrameRendering", flags);
            var contextEvent = manager.GetField("endContextRendering", flags);
            var endContext = (Action<ScriptableRenderContext, List<Camera>>)Delegate.CreateDelegate(
                typeof(Action<ScriptableRenderContext, List<Camera>>), manager.GetMethod("EndContextRendering", flags));
            var previousLegacy = legacyEvent.GetValue(null);
            var previousContext = contextEvent.GetValue(null);
            var set = APRTStreamingTests.Set(scenarioCount: 0);
            var volume = new APRTProbeReferenceVolume();
            var owner = new GameObject("APRT context event allocation test camera");
            bool listenersRemoved = false;
            try
            {
                // Isolate the real manager dispatch from unrelated Editor
                // listeners, and restore both event lists after this test.
                legacyEvent.SetValue(null, null);
                contextEvent.SetValue(null, null);
                var cameras = new List<Camera> { owner.AddComponent<Camera>() };
                volume.Initialize(APRTStreamingTests.Parameters(scenarios: false));
                volume.SetActiveBakingSet(set);
                Assert.That(volume.PrepareRelight(cameras[0]), Is.True);
                var buffer = volume.runtimeSHBuffer;
                for (int i = 0; i < 4; ++i)
                {
                    volume.PinRuntimeResourcesForFrame();
                    endContext(default, cameras);
                }

                using var recorder = new APRTAllocationRecorder();
                recorder.Begin();
                for (int i = 0; i < 16; ++i)
                {
                    volume.PinRuntimeResourcesForFrame();
                    endContext(default, cameras);
                }
                int allocated = recorder.End();
                Assert.That(allocated, Is.Zero, "The manager must not allocate a Camera[] to dispatch APRT's end callback.");
                Assert.That(volume.runtimeSHBuffer, Is.SameAs(buffer));

                volume.PinRuntimeResourcesForFrame();
                volume.ReloadBakedData();
                Assert.That(buffer.IsValid(), Is.True, "Recorded cameras retain resources until context execution ends.");
                endContext(default, cameras);
                Assert.That(buffer.IsValid(), Is.False, "The registered context callback must release a deferred reload.");
                volume.Cleanup();
                listenersRemoved = legacyEvent.GetValue(null) == null && contextEvent.GetValue(null) == null;
            }
            finally
            {
                try { volume.Cleanup(); Object.DestroyImmediate(owner); Object.DestroyImmediate(set); }
                finally
                {
                    legacyEvent.SetValue(null, previousLegacy);
                    contextEvent.SetValue(null, previousContext);
                }
            }
            Assert.That(listenersRemoved, Is.True, "Cleanup must unsubscribe APRT's context callback.");
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void PreparedScenesDoNotAllocateOrRebuildWhenTheCameraMoves(bool coordinated, bool gpuStreaming)
        {
            APRTStreamingTests.RequireGPU();
            var set = APRTStreamingTests.Set();
            var volume = new APRTProbeReferenceVolume();
            var owner = new GameObject("APRT allocation test camera");
            using var cmd = new CommandBuffer();
            try
            {
                var camera = owner.AddComponent<Camera>();
                volume.Initialize(APRTStreamingTests.Parameters(gpu: gpuStreaming));
                volume.SetActiveBakingSet(set);
                if (coordinated)
                {
                    set.SetPerSceneCellLists("APRT", new Dictionary<string, List<int>> { { Scene, new List<int> { 0, 1, 2 } } });
                    volume.AddPendingSceneLoading(Scene, set);
                }
                volume.EnableMaxCellStreaming(true);
                volume.lightingScenario = "Scenario 0";
                volume.BlendLightingScenario("Scenario 1", .5f);
                for (int i = 0; i < 4; ++i)
                {
                    // Exercise the same camera movement as the measured path:
                    // Unity creates the Transform managed wrapper on first access.
                    camera.transform.position = Vector3.right * (i % 3 * 30);
                    volume.PerformPendingOperations();
                    volume.UpdateCellStreaming(cmd, camera);
                    APRTStreamingTests.PrepareLoadedSets(volume, camera);
                }
                Assert.That(volume.residentCellCount, Is.EqualTo(3));
                var buffer = volume.runtimeSHBuffer;
                long pendingTicks = 0, prepareTicks = 0;
                Stopwatch.GetTimestamp();
                using var recorder = new APRTAllocationRecorder();
                recorder.Begin();
                for (int i = 0; i < 16; ++i)
                {
                    camera.transform.position = Vector3.right * (i % 3 * 30);
                    long start = Stopwatch.GetTimestamp();
                    volume.PerformPendingOperations();
                    pendingTicks += Stopwatch.GetTimestamp() - start;
                    start = Stopwatch.GetTimestamp();
                    volume.UpdateCellStreaming(cmd, camera);
                    APRTStreamingTests.PrepareLoadedSets(volume, camera);
                    prepareTicks += Stopwatch.GetTimestamp() - start;
                }
                int allocated = recorder.End();
                TestContext.WriteLine($"Full loading steady state: {allocated} GC allocations / 16 iterations; " +
                    $"PerformPendingOperations {pendingTicks * 1000.0 / Stopwatch.Frequency / 16:F4} ms/call; " +
                    $"PrepareRelight {prepareTicks * 1000.0 / Stopwatch.Frequency / 16:F4} ms/call.");
                Assert.That(allocated, Is.Zero, "Preparing an unchanged scene must not allocate or schedule cell work.");
                Assert.That(volume.runtimeSHBuffer, Is.SameAs(buffer));
                Assert.That(volume.residentCellIndices, Is.EqualTo(new[] { 0, 1, 2 }));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(owner); Object.DestroyImmediate(set); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ChangedLayoutReloadsAllProbePositionsOnRevisionOrExplicitReload(bool explicitReload)
        {
            APRTStreamingTests.RequireGPU();
            var set = APRTStreamingTests.Set(scenarioCount: 0);
            var volume = new APRTProbeReferenceVolume();
            var owner = new GameObject("APRT metadata test camera");
            using var cmd = new CommandBuffer();
            try
            {
                var camera = owner.AddComponent<Camera>();
                volume.Initialize(APRTStreamingTests.Parameters(scenarios: false));
                set.SetPerSceneCellLists("APRT", new Dictionary<string, List<int>> { { Scene, new List<int> { 0, 1, 2 } } });
                volume.AddPendingSceneLoading(Scene, set);
                APRTStreamingTests.PrepareLoadedSets(volume, camera);
                Assert.That(volume.residentCellIndices, Is.EqualTo(new[] { 0, 1, 2 }));
                var previousBuffer = volume.gpuBuffers[2];

                var data = set.aprtBakedData;
                for (int i = 0; i < data.cells[0].probeCount; ++i) data.cells[0].probePositions[i] += Vector3.right * 90;
                for (int i = 0; i < data.cells[2].probeCount; ++i) data.cells[2].probePositions[i] -= Vector3.right * 60;
                if (explicitReload) volume.ReloadBakedData();
                else ++data.lightingScenarioRevision;
                APRTStreamingTests.PrepareLoadedSets(volume, camera);
                Assert.That(volume.residentCellIndices, Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(volume.gpuBuffers[2], Is.Not.SameAs(previousBuffer));
                var positions = new Vector3[24];
                volume.gpuBuffers[2].GetData(positions);
                Assert.That(positions[0], Is.EqualTo(data.cells[0].probePositions[0]));
                Assert.That(positions[16], Is.EqualTo(data.cells[2].probePositions[0]));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(owner); Object.DestroyImmediate(set); }
        }
    }
}
