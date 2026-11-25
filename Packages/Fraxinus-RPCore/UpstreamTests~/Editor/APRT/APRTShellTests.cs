using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace UnityEngine.Rendering.Tests
{
    public class APRTShellTests
    {
        [Test]
        public void ProviderStillSelectsExplicitAPRTBackend()
        {
            Assert.That(ProbeReferenceVolumeProvider.TryGetBackend("APRT", out var backend), Is.True);
            Assert.That(backend.volume, Is.SameAs(APRTProbeReferenceVolume.instance));
            Assert.That(backend.CanUse(default, out _), Is.False);

            var parameters = new ProbeVolumeSystemParameters { enableAPRT = true };
            Assert.That(backend.CanUse(parameters, out var reason), Is.True);
            Assert.That(reason, Is.Null.Or.Empty);

            // Resolve the provider's choice without changing the running editor's backend.
            var selectBackend = typeof(ProbeReferenceVolumeProvider).GetMethod("SelectBackend", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(selectBackend, Is.Not.Null);
            Assert.That(selectBackend.Invoke(null, new object[] { parameters, "APRT" }), Is.SameAs(backend));
        }

        [Test]
        public void EditorBackendOffersAPRTBakingOnlyForItsVolumeAndSet()
        {
            var backend = new APRTProbeVolumeEditorBackend();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                Assert.That(backend.id, Is.EqualTo("APRT"));
                Assert.That(backend.supportsBaking, Is.True);
                var baking = backend.CreateBaking(new APRTProbeReferenceVolume(), set);
                Assert.That(baking, Is.TypeOf<APRTProbeVolumeBaking>());
                baking.Dispose();
                Assert.That(backend.CreateBaking(new APVProbeReferenceVolume(), set), Is.Null);
                Assert.That(backend.CreateBaking(new APRTProbeReferenceVolume(), null), Is.Null);
            }
            finally { Object.DestroyImmediate(set); }
        }

        [Test]
        public void RetainedRegionUsesOrientedWorldSizeIndependentOfTransformScale()
        {
            var owner = new GameObject("APRT shell region test") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var volume = owner.AddComponent<APRTVolume>();
                volume.mode = APRTVolume.Mode.Local;
                volume.size = new Vector3(2, 4, 6);
                owner.transform.position = new Vector3(8, 3, -5);
                owner.transform.rotation = Quaternion.Euler(0, 90, 0);
                owner.transform.localScale = new Vector3(50, 20, -3);

                Assert.That((volume.worldBounds.center - owner.transform.position).sqrMagnitude, Is.LessThan(0.000001f));
                Assert.That((volume.worldBounds.size - new Vector3(6, 4, 2)).sqrMagnitude, Is.LessThan(0.000001f));
                Assert.That(volume.ContainsPoint(owner.transform.position + owner.transform.rotation * new Vector3(.9f, 1.9f, 2.9f)), Is.True);
                Assert.That(volume.ContainsPoint(owner.transform.position + owner.transform.rotation * new Vector3(1.1f, 0, 0)), Is.False);
                var corner = volume.volumeMatrix.MultiplyPoint3x4(Vector3.one * .5f);
                var expected = owner.transform.position + owner.transform.rotation * new Vector3(1, 2, 3);
                Assert.That((corner - expected).sqrMagnitude, Is.LessThan(0.000001f));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [Test]
        public void LegacyDataLifecycleDoesNotLoadOrAllocateLighting()
        {
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var payload = new TextAsset("Legacy APRT payload must never be decoded by the empty backend.");
            using var cmd = new CommandBuffer();
            try
            {
                PopulateLegacyData(set.aprtBakedData, payload);
                set.GetCellDescs("APRT").Add(5, new CellDesc { index = 5, probeCount = 64 });
                set.SetPerSceneCellLists("APRT", new Dictionary<string, List<int>> { { "test-scene", new List<int> { 5 } } });
                volume.Initialize(new ProbeVolumeSystemParameters
                {
                    enableAPRT = true,
                    supportScenarios = true,
                    supportScenarioBlending = true,
                    blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow,
                    supportGPUStreaming = true,
                    supportDiskStreaming = true,
                });
                volume.SetEnableStateFromSRP(true);
                volume.SetActiveBakingSet(set);
                Assert.That(volume.isInitialized, Is.True);
                Assert.That(volume.currentBakingSet, Is.SameAs(set));

                volume.AddPendingSceneLoading("test-scene", set);
                volume.PerformPendingOperations();
                volume.UpdateCellStreaming(cmd, null);
                volume.UpdateCellStreaming(cmd, null, null);
                volume.RenderDebug(null, null, null);
                volume.BindPRVRuntimeResources(cmd, true);

                Assert.That(volume.DataHasBeenLoaded(), Is.False);
                Assert.That(volume.GetVideoMemoryCost(), Is.Zero);
                Assert.That(volume.diskStreamingEnabled, Is.False, "APRT loads baked scene data without disk streaming.");
                Assert.That(volume.supportLightingScenarios, Is.True);
                Assert.That(volume.supportScenarioBlending, Is.True);
                Assert.That(volume.UpdateShaderVariablesProbeVolumes(cmd, null, 0), Is.False);
                Assert.That(volume.GetProbeSamplingDebugResources(null, out var buffer, out _), Is.False);
                Assert.That(buffer, Is.Null);
                Assert.That(set.GetCellDataMap("APRT"), Is.Empty);
                Assert.That(set.aprtBakedData.cells[0].asset, Is.SameAs(payload),
                    "Reading a legacy set must leave its serialized references available for recovery.");

                volume.AddPendingSceneRemoval("test-scene");
                volume.UnloadAllCells();
                volume.Cleanup();
                volume.Cleanup();
                Assert.That(volume.isInitialized, Is.False);
                Assert.That(volume.currentBakingSet, Is.Null);
                Assert.That(volume.perSceneDataList, Is.Empty);
                Assert.That(volume.DataHasBeenLoaded(), Is.False);
                Assert.That(volume.GetVideoMemoryCost(), Is.Zero);
            }
            finally
            {
                volume.Cleanup();
                Object.DestroyImmediate(payload);
                Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void MissingDataDisablesShaderSamplingWithoutTransportResources()
        {
            var volume = new APRTProbeReferenceVolume();
            using var cmd = new CommandBuffer();

            volume.BindPRVRuntimeResources(cmd, true);
            volume.BindPRVRuntimeResources(cmd, false);
            volume.UpdateCellStreaming(cmd, null);
            volume.UpdateCellStreaming(cmd, null, null);

            Assert.That(volume.UpdateShaderVariablesProbeVolumes(cmd, null, 0), Is.False);
            Assert.That(volume.DataHasBeenLoaded(), Is.False);
            Assert.That(volume.GetVideoMemoryCost(), Is.Zero);
            volume.Cleanup();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EmptyAndLegacyBakedDataHaveNoUsableDataOrBuildResources(bool legacy)
        {
            var data = new APRTBakedData();
            var payload = new TextAsset("Inactive legacy payload");
            try
            {
                if (legacy) PopulateLegacyData(data, payload);
                Assert.That(data.backendId, Is.EqualTo("APRT"));
                Assert.That(data.HasBakedData("Default", true), Is.False);
                Assert.That(data.HasBakedData(null, false), Is.False);
                Assert.That(data.HasValidSharedData(), Is.False);
                Assert.That(data.HasValidScenarioData("Default", new APRTProbeReferenceVolume()), Is.False);
                Assert.That(data.HasSupportData(), Is.False);
                Assert.That(data.scenarioCount, Is.Zero);
                Assert.That(data.GetBuildResources(null), Is.Empty);
                Assert.That(data.GetDiskSizeOfSharedData(), Is.Zero);
                Assert.That(data.GetDiskSizeOfScenarioData("Default"), Is.Zero);
                Assert.That(data.TryGetScenarioHash("Default", out var hash), Is.False);
                Assert.That(hash, Is.Zero);
                Assert.DoesNotThrow(data.CleanupStreamableAssets);
                Assert.DoesNotThrow(data.ClearAssets);
            }
            finally { Object.DestroyImmediate(payload); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LegacyBakedCellsCannotBeResolvedOrDecoded(bool streamingOnly)
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var payload = new TextAsset("Deliberately not a valid transport file: the shell must never read this.");
            try
            {
                PopulateLegacyData(set.aprtBakedData, payload);
                set.GetCellDescs("APRT").Add(5, new CellDesc { index = 5, probeCount = 64 });
                var format = new APRTProbeVolumeBakingDataFormat();
                var volume = new APRTProbeReferenceVolume();
                var context = new ProbeVolumeBakingDataResolveContext(set, volume, new List<int> { 5 }, streamingOnly, "APRT");

                Assert.That(format.CreateBakedData(), Is.TypeOf<APRTBakedData>());
                Assert.That(format.CreateCellData(set, 5, streamingOnly), Is.Null);
                Assert.That(format.ResolveSharedCellData(context), Is.False);
                Assert.That(format.ResolveScenarioCellData(context), Is.False);
                Assert.That(format.GetChunkGPUMemory(set, volume), Is.Zero);
                Assert.That(set.GetCellDataMap("APRT"), Is.Empty);
            }
            finally { Object.DestroyImmediate(payload); Object.DestroyImmediate(set); }
        }

        [Test]
        public void FrameBindingAndCleanupDisableAPRTAndKeepAmbientLighting()
        {
            string[] names = { "_EnableAPRT", "_APRTGIIntensity", "_APRTAddAmbientLighting", "_APRTProbeCount" };
            var previous = new float[names.Length];
            for (int i = 0; i < names.Length; ++i) previous[i] = Shader.GetGlobalFloat(names[i]);
            var volume = new APRTProbeReferenceVolume();
            using var cmd = new CommandBuffer();
            try
            {
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                SeedLegacyGlobals();
                volume.BindPRVRuntimeResources(cmd, true);
                Graphics.ExecuteCommandBuffer(cmd);
                AssertInactiveGlobals();

                cmd.Clear();
                SeedLegacyGlobals();
                Assert.That(volume.UpdateShaderVariablesProbeVolumes(cmd, null, 0), Is.False);
                Graphics.ExecuteCommandBuffer(cmd);
                AssertInactiveGlobals();

                SeedLegacyGlobals();
                volume.SetEnableStateFromSRP(false);
                AssertInactiveGlobals();
                SeedLegacyGlobals();
                volume.Clear();
                AssertInactiveGlobals();
                SeedLegacyGlobals();
                volume.Cleanup();
                AssertInactiveGlobals();
            }
            finally
            {
                volume.Cleanup();
                for (int i = 0; i < names.Length; ++i) Shader.SetGlobalFloat(names[i], previous[i]);
            }
        }

        static void SeedLegacyGlobals()
        {
            Shader.SetGlobalFloat("_EnableAPRT", 1);
            Shader.SetGlobalFloat("_APRTGIIntensity", 5);
            Shader.SetGlobalFloat("_APRTAddAmbientLighting", 0);
            Shader.SetGlobalFloat("_APRTProbeCount", 64);
        }

        static void AssertInactiveGlobals()
        {
            Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.Zero);
            Assert.That(Shader.GetGlobalFloat("_APRTGIIntensity"), Is.Zero);
            Assert.That(Shader.GetGlobalFloat("_APRTAddAmbientLighting"), Is.EqualTo(1));
            Assert.That(Shader.GetGlobalFloat("_APRTProbeCount"), Is.Zero);
        }

        internal static void PopulateLegacyData(APRTBakedData data, TextAsset payload)
        {
            data.cells = new List<APRTBakedCell> { new APRTBakedCell { index = 5, probeCount = 64, asset = payload } };
            data.sampleCount = 32;
            data.hasLeakReductionData = true;
            data.regularGrid = true;
            data.regularGridOrigin = Vector3.zero;
            data.regularGridRotation = Quaternion.identity;
            data.regularGridDimensions = new Vector3Int(4, 4, 4);
            data.regularGridSpacing = 1;
            data.samplingDomains = new[] { Matrix4x4.identity };
        }

        [Test]
        public void ClearingAPRTShellPreservesAPVLayoutAndScenario()
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                set.SetDefaults();
                set.TryAddScenario("Night");
                set.SetActiveScenario("Night", false, new APVProbeReferenceVolume());
                var retained = new CellDesc { index = 7 };
                set.GetCellDescs("APV").Add(7, retained);
                set.SetPerSceneCellLists("APV", new Dictionary<string, List<int>> { { "scene", new List<int> { 7 } } });
                set.GetCellDescs("APRT").Add(5, new CellDesc { index = 5 });
                set.SetPerSceneCellLists("APRT", new Dictionary<string, List<int>> { { "scene", new List<int> { 5 } } });

                set.Clear("APRT");

                Assert.That(set.GetCellDescs("APRT"), Is.Empty);
                Assert.That(set.GetCellDescs("APV")[7], Is.SameAs(retained));
                Assert.That(set.GetPerSceneCellLists("APV")["scene"], Is.EqualTo(new[] { 7 }));
                Assert.That(set.GetLightingScenario("APV"), Is.EqualTo("Night"));
            }
            finally { Object.DestroyImmediate(set); }
        }
    }
}
