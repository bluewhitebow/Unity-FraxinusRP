using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;

namespace UnityEngine.Rendering.Tests
{
    public class ProbeVolumeBuildBoundaryTests
    {
        string m_Folder;
        string m_Output;
        readonly List<ScriptableObject> m_Objects = new();

        [SetUp]
        public void SetUp()
        {
            var id = Guid.NewGuid().ToString("N");
            m_Folder = "Assets/ProbeVolumeBuildTest_" + id;
            m_Output = Path.Combine("Library", "ProbeVolumeBuildTest_" + id);
            Directory.CreateDirectory(m_Folder);
        }

        [TearDown]
        public void TearDown()
        {
            ProbeVolumeBuildProcessor.RestoreAssetReferences();
            foreach (var obj in m_Objects)
                Object.DestroyImmediate(obj);
            m_Objects.Clear();
            AssetDatabase.DeleteAsset(m_Folder);
            if (Directory.Exists(m_Output))
                Directory.Delete(m_Output, true);
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public void APVBuildPolicySelectsOnlyRequiredResources(bool enabled, bool l2, bool occlusion)
        {
            var shared = Asset("shared");
            var bricks = Asset("bricks");
            var support = Asset("support");
            var data = Asset("scenario");
            var optional = Asset("optional");
            var probeOcclusion = Asset("occlusion");
            var bakedData = new APVProbeVolumeBakedData
            {
                cellSharedDataAsset = shared,
                cellBricksDataAsset = bricks,
                cellSupportDataAsset = support,
                bakedProbeOcclusion = occlusion
            };
            bakedData.scenarios.Add("Day", new ProbeVolumeBakingSet.PerScenarioDataInfo
            {
                cellDataAsset = data,
                cellOptionalDataAsset = optional,
                cellProbeOcclusionDataAsset = probeOcclusion
            });

            var resources = bakedData.GetBuildResources(new[] { Pipeline(enabled, l2) }).ToDictionary(resource => resource.asset, resource => resource.includeInBuild);
            Assert.That(resources.Count, Is.EqualTo(6));
            Assert.That(resources[shared], Is.EqualTo(enabled));
            Assert.That(resources[bricks], Is.EqualTo(enabled));
            Assert.That(resources[support], Is.False);
            Assert.That(resources[data], Is.EqualTo(enabled));
            Assert.That(resources[optional], Is.EqualTo(enabled && l2));
            Assert.That(resources[probeOcclusion], Is.EqualTo(enabled && occlusion));
            Assert.That(bakedData.streamingAssetsPath, Is.EqualTo("APVStreamingAssets"));
        }

        [Test]
        public void DisabledPipelineDoesNotForceL2IntoAnotherPipelineBuild()
        {
            var optional = Asset("optional");
            var bakedData = new APVProbeVolumeBakedData();
            bakedData.scenarios.Add("Day", new ProbeVolumeBakingSet.PerScenarioDataInfo { cellOptionalDataAsset = optional });
            var assets = new[] { Pipeline(true, false), Pipeline(false, true) };
            var resource = bakedData.GetBuildResources(assets).Single(item => item.asset == optional);
            Assert.That(resource.includeInBuild, Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void BuildProcessorHandlesAnotherFormatAndRestoresReferences(bool streaming)
        {
            var included = Asset("included");
            var stripped = Asset("stripped");
            var bakedData = new TestBakedData(included, stripped);

            bool copied = ProbeVolumeBuildProcessor.PrepareBakedData(bakedData, Array.Empty<RenderPipelineAsset>(), m_Output, streaming);
            Assert.That(copied, Is.EqualTo(streaming));
            Assert.That(included.asset == null, Is.EqualTo(streaming));
            Assert.That(stripped.asset, Is.Null);
            Assert.That(File.Exists(Path.Combine(m_Output, included.assetGUID + ".bytes")), Is.EqualTo(streaming));
            Assert.That(File.Exists(Path.Combine(m_Output, stripped.assetGUID + ".bytes")), Is.False);

            ProbeVolumeBuildProcessor.RestoreAssetReferences();
            Assert.That(included.asset, Is.Not.Null);
            Assert.That(stripped.asset, Is.Not.Null);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void MissingIncludedResourceFailsAndRestoresReferences(bool streaming)
        {
            var missingPath = m_Folder + "/missing.bytes";
            var missing = new ProbeVolumeStreamableAsset("TestProbeData", new SerializedDictionary<int, ProbeVolumeStreamableAsset.StreamableCellDesc>(), 1, "set", Guid.NewGuid().ToString("N"));
            var valid = Asset("valid");
            var bakedData = new TestBakedData(valid, null, missing);

            Assert.Throws<BuildFailedException>(() => ProbeVolumeBuildProcessor.PrepareBakedData(bakedData, Array.Empty<RenderPipelineAsset>(), m_Output, streaming));
            Assert.That(valid.asset, Is.Not.Null, "A failed preparation must restore references cleared before the failure.");
            Assert.That(missing.asset, Is.Null, "An invalid reference must remain absent after the failed preparation.");
        }

        [Test]
        public void ScenarioAssetsRenameAndDeleteWithMissingOptionalData()
        {
            var scenario = new ProbeVolumeBakingSet.PerScenarioDataInfo { cellDataAsset = Asset("data"), sceneHash = 17 };
            var bakedData = new APVProbeVolumeBakedData();
            bakedData.scenarios.Add("Day", scenario);
            bakedData.RenameScenario("Day", "Night", "Set");
            Assert.That(bakedData.HasBakedData("Day", true), Is.False);
            Assert.That(bakedData.HasBakedData("Night", true), Is.True);
            Assert.That(Path.GetFileName(scenario.cellDataAsset.GetAssetPath()), Is.EqualTo("Set-Night.CellData.bytes"));
            Assert.That(bakedData.GetDiskSizeOfScenarioData("Night"), Is.EqualTo(4));
            Assert.That(bakedData.TryGetScenarioHash("Night", out int hash), Is.True);
            Assert.That(hash, Is.EqualTo(17));

            bakedData.scenarios.Remove("Night");
            bakedData.scenarios.Add("Day", scenario);
            bakedData.EnsureScenarioAssetNames("Set");
            Assert.That(Path.GetFileName(scenario.cellDataAsset.GetAssetPath()), Is.EqualTo("Set-Day.CellData.bytes"));
            string path = scenario.cellDataAsset.GetAssetPath();
            bakedData.RemoveScenario("Day");
            Assert.That(File.Exists(path), Is.False);
            Assert.That(bakedData.scenarioCount, Is.Zero);
        }

        [Test]
        public void SharedLayoutFieldsKeepTheirSerializedNames()
        {
            var data = new APVProbeVolumeBakedData
            {
                maxCellPosition = new Vector3Int(1, 2, 3),
                minCellPosition = new Vector3Int(-1, -2, -3),
                globalBounds = new Bounds(new Vector3(2, 3, 4), Vector3.one * 8),
                bakedSimplificationLevels = 4,
                bakedMinDistanceBetweenProbes = 0.5f,
                bakedProbeOffset = new Vector3(5, 6, 7)
            };
            var restored = JsonUtility.FromJson<APVProbeVolumeBakedData>(JsonUtility.ToJson(data));
            Assert.That(restored.maxCellPosition, Is.EqualTo(data.maxCellPosition));
            Assert.That(restored.minCellPosition, Is.EqualTo(data.minCellPosition));
            Assert.That(restored.globalBounds, Is.EqualTo(data.globalBounds));
            Assert.That(restored.bakedSimplificationLevels, Is.EqualTo(4));
            Assert.That(restored.bakedMinDistanceBetweenProbes, Is.EqualTo(0.5f));
            Assert.That(restored.bakedProbeOffset, Is.EqualTo(data.bakedProbeOffset));
        }

        [Test]
        public void WeakReferenceUnloadsBackendAssetsWithoutCreatingAPVData()
        {
            var backendAsset = Asset("backend");
            var backendData = new TestBakedData(backendAsset, null);
            var bakingSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            m_Objects.Add(bakingSet);

            var backendDataList = new List<ProbeVolumeBakedData> { backendData };
            typeof(ProbeVolumeBakingSet).GetField("m_BackendBakedData", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(bakingSet, backendDataList);

            string bakingSetPath = m_Folder + "/BakingSet.asset";
            AssetDatabase.CreateAsset(bakingSet, bakingSetPath);
            AssetDatabase.SaveAssets();
            var persistedBakingSet = AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(bakingSetPath);
            Assert.That(persistedBakingSet, Is.Not.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(backendAsset.GetAssetPath()), Is.Not.Null);

            var persistedData = GetBackendData(persistedBakingSet);
            Assert.That(persistedData, Has.Count.EqualTo(1));
            Assert.That(persistedData[0], Is.TypeOf<TestBakedData>());
            Assert.That(persistedData.Any(data => data is APVProbeVolumeBakedData), Is.False);

            new ProbeVolumeBakingSetWeakReference(persistedBakingSet).Unload();

            Assert.That(((TestBakedData)persistedData[0]).unloadAssetsCalled, Is.True);
            Assert.That(persistedData, Has.Count.EqualTo(1));
            Assert.That(persistedData.Any(data => data is APVProbeVolumeBakedData), Is.False);
        }

        ProbeVolumeStreamableAsset Asset(string name)
        {
            string path = m_Folder + "/" + name + ".bytes";
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return new ProbeVolumeStreamableAsset("TestProbeData", new SerializedDictionary<int, ProbeVolumeStreamableAsset.StreamableCellDesc>(), 1, "set", AssetDatabase.AssetPathToGUID(path));
        }

        RenderPipelineAsset Pipeline(bool enabled, bool l2)
        {
            var asset = ScriptableObject.CreateInstance<TestPipelineAsset>();
            asset.enabled = enabled;
            asset.l2 = l2;
            m_Objects.Add(asset);
            return asset;
        }

        static List<ProbeVolumeBakedData> GetBackendData(ProbeVolumeBakingSet bakingSet)
        {
            return (List<ProbeVolumeBakedData>)typeof(ProbeVolumeBakingSet).GetField("m_BackendBakedData", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(bakingSet);
        }

        class TestPipelineAsset : RenderPipelineAsset, IProbeVolumeEnabledRenderPipeline
        {
            internal bool enabled;
            internal bool l2;
            public bool supportProbeVolume => enabled;
            public ProbeVolumeSHBands maxSHBands => l2 ? ProbeVolumeSHBands.SphericalHarmonicsL2 : ProbeVolumeSHBands.SphericalHarmonicsL1;
            protected override RenderPipeline CreatePipeline() => null;
        }

        [Serializable]
        sealed class TestBakedData : ProbeVolumeBakedData
        {
            [SerializeField] ProbeVolumeStreamableAsset m_Included;
            readonly ProbeVolumeStreamableAsset m_Stripped;
            readonly ProbeVolumeStreamableAsset m_Failed;
            internal bool unloadAssetsCalled { get; private set; }
            public override string backendId => "TestBackend";
            internal override string streamingAssetsPath => "TestProbeData";
            internal override int scenarioCount => 0;

            internal TestBakedData(ProbeVolumeStreamableAsset included, ProbeVolumeStreamableAsset stripped, ProbeVolumeStreamableAsset failed = null)
            {
                m_Included = included;
                m_Stripped = stripped;
                m_Failed = failed;
            }

            internal override IEnumerable<ProbeVolumeBuildResource> GetBuildResources(IReadOnlyList<RenderPipelineAsset> renderPipelineAssets)
            {
                yield return new ProbeVolumeBuildResource(m_Included, true);
                if (m_Failed != null)
                    yield return new ProbeVolumeBuildResource(m_Failed, true);
                yield return new ProbeVolumeBuildResource(m_Stripped, false);
            }

            internal override bool HasBakedData(string scenario, bool supportLightingScenarios) => false;
            internal override bool HasValidSharedData() => true;
            internal override bool HasSupportData() => false;
            internal override void CleanupStreamableAssets() { }
            internal override bool HasValidScenarioData(string scenario, TProbeReferenceVolumeBase volume) => false;
            internal override bool TryGetScenarioHash(string scenario, out int sceneHash) { sceneHash = 0; return false; }
            internal override long GetDiskSizeOfSharedData() => 0;
            internal override long GetDiskSizeOfScenarioData(string scenario) => 0;
            internal override void ClearAssets() { }
            internal override void RemoveScenario(string scenario) { }
            internal override void RenameScenario(string scenario, string newName, string assetName) { }
            internal override void EnsureScenarioAssetNames(string assetName) { }

            internal override void UnloadAssetsForWeakReference()
            {
                Resources.UnloadAsset(m_Included?.asset);
                unloadAssetsCalled = true;
            }
        }
    }
}
