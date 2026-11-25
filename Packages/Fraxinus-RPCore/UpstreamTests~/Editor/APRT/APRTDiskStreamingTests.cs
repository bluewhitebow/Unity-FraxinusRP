using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using Object = UnityEngine.Object;

namespace UnityEngine.Rendering.Tests
{
    public class APRTDiskStreamingTests
    {
        [Test]
        public void PublishedStreamingBakeSerializesDescriptorsAndLoadsOnlyTheRequestedCellAndScenario()
        {
            using var fixture = new DiskFixture();
            var data = fixture.set.aprtBakedData;
            var source = fixture.original;
            var copy = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                Assert.That(data.HasValidSharedData(), Is.True);
                Assert.That(data.HasBakedLightingScenario("Scenario 0"), Is.True);
                Assert.That(data.cells.All(cell => cell.axisData.Length == 0 && cell.probeValidity.Length == 0 &&
                    cell.skyIrradianceTransfer.Length == 0 && cell.skyIrradianceTransferRGB.Length == 0), Is.True);
                Assert.That(data.lightingScenarios.All(scenario => scenario.bakedDirectionalSH.Length == 0 &&
                    scenario.bakedSkyIrradianceSH.Length == 0), Is.True);
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(fixture.set), copy);
                Assert.That(copy.aprtBakedData.HasValidSharedData(), Is.True);
                Assert.That(copy.aprtBakedData.HasBakedLightingScenario("Scenario 2"), Is.True);
                var cell = APRTDiskData.ReadSharedSync(copy.aprtBakedData.cells[1], data.GetLayoutHash(), true, true);
                var lighting = APRTDiskData.ReadScenarioSync(copy.aprtBakedData.FindLightingScenario("Scenario 2"), 1, 8, data.GetLayoutHash());
                Assert.That(cell.axisData, Is.EqualTo(source.cells[1].axisData));
                Assert.That(cell.skyIrradianceTransferRGB, Is.EqualTo(source.cells[1].skyIrradianceTransferRGB));
                Assert.That(lighting.directionalSH, Is.EqualTo(source.FindLightingScenario("Scenario 2").bakedDirectionalSH.Skip(32).Take(32).ToArray()));
                Assert.That(copy.aprtBakedData.cells[0].axisData, Is.Empty);
                Assert.That(copy.aprtBakedData.cells[1].axisData, Is.Empty, "Decoding returns a separate owned payload and keeps serialized metadata lazy.");
                Assert.That(copy.aprtBakedData.FindLightingScenario("Scenario 0").bakedDirectionalSH, Is.Empty);
            }
            finally { Object.DestroyImmediate(copy); }
        }

        [Test]
        public void WindowsLongPathsDecodeLegacySharedAndScenarioPayloadsSynchronously()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("Exercises Windows MAX_PATH handling in Unity Mono's real file reads.");
            using var fixture = new DiskFixture();
            string relativeRoot = "Library/APRTLongPathTest_" + Guid.NewGuid().ToString("N");
            string absoluteRoot = Path.GetFullPath(relativeRoot).Replace('/', '\\');
            string directory = relativeRoot + "/" + new string('a', 96) + "/" + new string('b', 96);
            string extendedRoot = @"\\?\" + absoluteRoot;
            try
            {
                // Create the test input through Windows' extended path syntax;
                // the production resolver must then make all four reads work.
                Directory.CreateDirectory(@"\\?\" + Path.GetFullPath(directory).Replace('/', '\\'));
                var data = fixture.set.aprtBakedData;
                var cell = data.cells[0];
                var scenario = data.FindLightingScenario("Scenario 1");
                var sharedDescriptor = cell.streamableAsset;
                var scenarioDescriptor = scenario.streamableAssets[0];
                foreach (var descriptor in new[] { sharedDescriptor, scenarioDescriptor })
                {
                    string previousPath = descriptor.ResolvePath();
                    string relativePath = directory + "/" + Path.GetFileName(descriptor.projectRelativePath);
                    string absolutePath = Path.GetFullPath(relativePath).Replace('/', '\\');
                    Assert.That(absolutePath.Length, Is.GreaterThan(260), "The payload must exceed the legacy Windows path limit.");
                    File.Copy(previousPath, @"\\?\" + absolutePath);
                    descriptor.projectRelativePath = relativePath;
                }
                int layout = data.GetLayoutHash();
                var expectedShared = fixture.original.cells[0];
                var expectedSH = fixture.original.FindLightingScenario("Scenario 1").bakedDirectionalSH.Take(32).ToArray();
                var sharedSync = APRTDiskData.ReadSharedSync(cell, layout, true, true);
                var scenarioSync = APRTDiskData.ReadScenarioSync(scenario, 0, 8, layout);
                Assert.That(sharedSync.axisData, Is.EqualTo(expectedShared.axisData));
                Assert.That(sharedSync.skyIrradianceTransferRGB, Is.EqualTo(expectedShared.skyIrradianceTransferRGB));
                Assert.That(scenarioSync.directionalSH, Is.EqualTo(expectedSH));
                Assert.That(cell.axisData, Is.Empty);
                Assert.That(scenario.bakedDirectionalSH, Is.Empty);
            }
            finally
            {
                if (Directory.Exists(extendedRoot)) Directory.Delete(extendedRoot, recursive: true);
            }
        }

        [Test]
        public void LegacyExternalBakeLoadsAllCellsAndTheSelectedPairSynchronously()
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new DiskFixture();
            var set = fixture.set;
            var volume = new APRTProbeReferenceVolume();
            try
            {
                volume.Initialize(APRTStreamingTests.Parameters(disk: true));
                volume.SetActiveBakingSet(set);
                volume.lightingScenario = "Scenario 0";
                volume.BlendLightingScenario("Scenario 1", .5f);
                volume.SetNumberOfCellsLoadedPerFrame(1);
                APRTStreamingTests.PrepareLoadedSets(volume);
                Assert.That(volume.residentCellIndices, Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(volume.pendingCellRequestCount, Is.Zero);
                Assert.That(volume.diskStreamingEnabled, Is.False);
                Assert.That(volume.residentScenarioNames, Is.EquivalentTo(new[] { "Scenario 0", "Scenario 1" }));
                APRTStreamingTests.Dispatch(volume, set);
                var a = fixture.original.FindLightingScenario("Scenario 0").bakedDirectionalSH;
                var b = fixture.original.FindLightingScenario("Scenario 1").bakedDirectionalSH;
                APRTStreamingTests.AssertSH(APRTStreamingTests.ReadDirectional(volume), a.Zip(b, (x, y) => Vector4.Lerp(x, y, .5f)).ToArray());
                Assert.That(set.aprtBakedData.cells.All(cell => cell.axisData.Length == 0), Is.True,
                    "Compatibility loading must keep serialized metadata separate from decoded payloads.");
            }
            finally { volume.Cleanup(); }
        }

        [Test]
        public void SwitchingBakingSetsReleasesThePreviouslyLoadedExternalPayload()
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new DiskFixture();
            var replacement = APRTStreamingTests.Set(APRTStreamingTests.CellsData(1));
            var volume = new APRTProbeReferenceVolume();
            try
            {
                volume.Initialize(APRTStreamingTests.Parameters(disk: true));
                volume.SetActiveBakingSet(fixture.set);
                APRTStreamingTests.PrepareLoadedSets(volume);
                Assert.That(volume.residentCellCount, Is.EqualTo(3));
                volume.SetActiveBakingSet(replacement);
                Assert.That(volume.pendingCellRequestCount, Is.Zero);
                Assert.That(volume.runtimeSHBuffer, Is.Null);
                APRTStreamingTests.PrepareLoadedSets(volume);
                Assert.That(volume.residentCellCount, Is.EqualTo(1));
                volume.lightingScenario = "Scenario 0";
                APRTStreamingTests.Dispatch(volume, replacement);
                Assert.That(APRTStreamingTests.ReadDirectional(volume), Is.EqualTo(replacement.aprtBakedData.FindLightingScenario("Scenario 0").bakedDirectionalSH));
                Assert.That(fixture.set.aprtBakedData.cells.All(cell => cell.axisData.Length == 0), Is.True);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(replacement); }
        }

        [Test]
        public void MovingTheCameraDoesNotReopenLegacyExternalFilesOrReplaceBuffers()
        {
            APRTStreamingTests.RequireGPU();
            using var fixture = new DiskFixture();
            var volume = new APRTProbeReferenceVolume();
            var owner = new GameObject("APRT legacy loading camera");
            try
            {
                var camera = owner.AddComponent<Camera>();
                volume.Initialize(APRTStreamingTests.Parameters(disk: true));
                volume.SetActiveBakingSet(fixture.set);
                volume.lightingScenario = "Scenario 0";
                volume.BlendLightingScenario("Scenario 1", .5f);
                APRTStreamingTests.PrepareLoadedSets(volume, camera);
                Assert.That(volume.residentCellCount, Is.EqualTo(3));
                var buffers = volume.gpuBuffers.ToArray();
                foreach (var cell in fixture.set.aprtBakedData.cells) File.Delete(cell.streamableAsset.ResolvePath());
                foreach (var scenario in fixture.set.aprtBakedData.lightingScenarios)
                    foreach (var descriptor in scenario.streamableAssets) File.Delete(descriptor.ResolvePath());
                foreach (float position in new[] { 60f, -100000f, 100000f })
                {
                    camera.transform.position = Vector3.right * position;
                    APRTStreamingTests.PrepareLoadedSets(volume, camera);
                    Assert.That(volume.residentCellIndices, Is.EqualTo(new[] { 0, 1, 2 }));
                    Assert.That(volume.gpuBuffers, Is.EqualTo(buffers));
                    Assert.That(volume.pendingCellRequestCount, Is.Zero);
                }
                APRTStreamingTests.Dispatch(volume, fixture.set);
                var a = fixture.original.FindLightingScenario("Scenario 0").bakedDirectionalSH;
                var b = fixture.original.FindLightingScenario("Scenario 1").bakedDirectionalSH;
                APRTStreamingTests.AssertSH(APRTStreamingTests.ReadDirectional(volume), a.Zip(b, (x, y) => Vector4.Lerp(x, y, .5f)).ToArray());
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(owner); }
        }

        [TestCase("missing")]
        [TestCase("truncated")]
        [TestCase("checksum")]
        [TestCase("header")]
        [TestCase("nan")]
        public void CorruptSharedDataFailsBeforeBecomingResident(string fault)
        {
            using var fixture = new DiskFixture();
            var data = fixture.set.aprtBakedData;
            var descriptor = data.cells[1].streamableAsset;
            Corrupt(descriptor, fault);
            if (fault == "missing")
                Assert.Throws<FileNotFoundException>(() => APRTDiskData.ReadSharedSync(data.cells[1], data.GetLayoutHash(), true, true));
            else
                Assert.Throws<InvalidDataException>(() => APRTDiskData.ReadSharedSync(data.cells[1], data.GetLayoutHash(), true, true));
            Assert.That(data.cells[1].axisData, Is.Empty);
            Assert.That(data.HasValidSharedData(), Is.True, "Descriptor validity is independent of reading payload bytes.");
        }

        [TestCase("checksum")]
        [TestCase("header")]
        [TestCase("nan")]
        public void CorruptScenarioDataCannotBecomeAFallbackToCurrentSceneLighting(string fault)
        {
            using var fixture = new DiskFixture();
            var data = fixture.set.aprtBakedData;
            var scenario = data.FindLightingScenario("Scenario 1");
            Corrupt(scenario.streamableAssets[1], fault);
            Assert.Throws<InvalidDataException>(() => APRTDiskData.ReadScenarioSync(scenario, 1, 8, data.GetLayoutHash()));
            Assert.That(scenario.bakedDirectionalSH, Is.Empty);
        }

        [Test]
        public void AnUnpublishedGenerationIsRemovedAndThePreviousBakeRemainsReadable()
        {
            using var fixture = new DiskFixture();
            var previous = fixture.set.aprtBakedData;
            var staged = APRTStreamingTests.CellsData();
            staged.SetBakedLightingScenario(APRTScenarioBakingTests.BakedScenario(staged, "Scenario 0", 5));
            string newPath;
            using (APRTStreamingDataWriter.Prepare(fixture.set, staged))
            {
                newPath = staged.cells[0].streamableAsset.ResolvePath();
                Assert.That(File.Exists(newPath), Is.True);
                Assert.That(fixture.set.aprtBakedData, Is.SameAs(previous));
            }
            Assert.That(File.Exists(newPath), Is.False);
            Assert.That(fixture.set.aprtBakedData, Is.SameAs(previous));
            Assert.That(APRTDiskData.ReadSharedSync(previous.cells[0], previous.GetLayoutHash(), true, true).axisData,
                Is.EqualTo(fixture.original.cells[0].axisData));
        }

        [Test]
        public void RecalculatingProbePositionsCannotInheritAnOldExternalScenarioWithTheSameProbeCount()
        {
            using var fixture = new DiskFixture();
            var replacement = APRTStreamingTests.CellsData();
            replacement.cells[0].probePositions[0] += Vector3.up * .125f;
            replacement.SetBakedLightingScenario(APRTScenarioBakingTests.BakedScenario(replacement, "Scenario 0", 5));
            using var transaction = APRTStreamingDataWriter.Prepare(fixture.set, replacement);
            fixture.set.CommitAPRTBakedData(replacement);
            transaction.Publish();
            Assert.That(replacement.HasBakedLightingScenario("Scenario 0"), Is.True);
            Assert.That(replacement.FindLightingScenario("Scenario 1"), Is.Null);
            Assert.That(replacement.FindLightingScenario("Scenario 2"), Is.Null);
            Assert.That(replacement.scenarioCount, Is.EqualTo(1));
        }

        [TestCase("null")]
        [TestCase("duplicate")]
        [TestCase("wrong-cell")]
        [TestCase("wrong-layout")]
        public void DamagedScenarioDescriptorsAreUnavailableWithoutThrowingDuringStatusQueries(string fault)
        {
            using var fixture = new DiskFixture();
            var data = fixture.set.aprtBakedData;
            var scenario = data.FindLightingScenario("Scenario 0");
            switch (fault)
            {
                case "null": scenario.streamableAssets[0] = null; break;
                case "duplicate": scenario.streamableAssets[1] = scenario.streamableAssets[0].Clone(); break;
                case "wrong-cell": scenario.streamableAssets[0].cellIndex = 77; break;
                case "wrong-layout": scenario.streamableAssets[0].layoutHash ^= 1; break;
            }
            Assert.That(data.HasBakedLightingScenario("Scenario 0"), Is.False);
            Assert.That(data.TryGetScenarioHash("Scenario 0", out _), Is.False);
        }

        [Test]
        public void BuildResourcesIncludeEverySharedCellAndIndependentScenarioFileWithoutTextAssets()
        {
            using var fixture = new DiskFixture();
            var descriptors = UnityEditor.Rendering.APRTStreamingBuildProcessor.GetStreamingResources(fixture.set.aprtBakedData).ToArray();
            Assert.That(descriptors.Length, Is.EqualTo(12));
            Assert.That(descriptors.Select(item => item.streamingRelativePath).Distinct().Count(), Is.EqualTo(12));
            foreach (var descriptor in descriptors)
            {
                Assert.That(descriptor.streamingRelativePath, Does.StartWith(APRTDiskData.streamingAssetsDirectory + "/"));
                APRTDiskData.ValidateFileSync(descriptor);
            }
            Assert.That(fixture.set.aprtBakedData.cells.Select(cell => cell.asset), Is.All.Null);
        }

        static void Corrupt(APRTStreamableAsset descriptor, string fault)
        {
            string path = descriptor.ResolvePath();
            if (fault == "missing") { File.Delete(path); return; }
            var bytes = File.ReadAllBytes(path);
            if (fault == "truncated") { File.WriteAllBytes(path, bytes.Take(bytes.Length - 1).ToArray()); return; }
            if (fault == "header") bytes[0] ^= 1;
            else if (fault == "nan") Array.Copy(BitConverter.GetBytes(float.NaN), 0, bytes, 24, 4);
            else bytes[bytes.Length - 1] ^= 1;
            File.WriteAllBytes(path, bytes);
            if (fault == "header" || fault == "nan") descriptor.checksum = APRTDiskData.ComputeChecksum(bytes);
        }

        internal sealed class DiskFixture : IDisposable
        {
            internal readonly ProbeVolumeBakingSet set;
            internal readonly APRTBakedData original;
            readonly string folder;

            internal DiskFixture()
            {
                string name = "APRTDiskStreamingTest_" + Guid.NewGuid().ToString("N");
                folder = "Assets/" + name;
                AssetDatabase.CreateFolder("Assets", name);
                set = APRTStreamingTests.Set();
                // A serialized clone matches a domain-reloaded original bake.
                var originalSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                try
                {
                    EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(set), originalSet);
                    original = originalSet.aprtBakedData;
                }
                finally { Object.DestroyImmediate(originalSet); }
                AssetDatabase.CreateAsset(set, folder + "/BakingSet.asset");
                using var transaction = APRTStreamingDataWriter.Prepare(set, set.aprtBakedData);
                transaction.Publish();
                EditorUtility.SetDirty(set);
                AssetDatabase.SaveAssetIfDirty(set);
            }

            public void Dispose() => AssetDatabase.DeleteAsset(folder);
        }
    }
}
