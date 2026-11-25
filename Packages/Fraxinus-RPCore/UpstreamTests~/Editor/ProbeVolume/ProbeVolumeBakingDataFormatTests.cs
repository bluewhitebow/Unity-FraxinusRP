using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace UnityEngine.Rendering.Tests
{
    public class ProbeVolumeBakingDataFormatTests
    {
        static readonly FieldInfo s_Context = typeof(ProbeReferenceVolumeProvider).GetField("s_context", BindingFlags.Static | BindingFlags.NonPublic);

        ProbeReferenceVolumeRuntimeContext m_PreviousContext;
        ProbeVolumeBakingSet m_BakingSet;
        TestBackend m_Backend;

        [SetUp]
        public void SetUp()
        {
            m_PreviousContext = (ProbeReferenceVolumeRuntimeContext)s_Context.GetValue(null);
            // This uninitialized volume supplies runtime flags without allocating GPU resources.
            m_Backend = new TestBackend("TestProbeFormat", new APVProbeReferenceVolume());
            SelectBackend(m_Backend);
            m_BakingSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
        }

        [TearDown]
        public void TearDown()
        {
            s_Context.SetValue(null, m_PreviousContext);
            if (m_BakingSet != null)
                UnityEngine.Object.DestroyImmediate(m_BakingSet);
        }

        [Test]
        public void SelectedFormatResolvesCellsWithoutAPVAssets()
        {
            var testData = m_BakingSet.bakedData;
            Assert.That(testData, Is.TypeOf<TestBakedData>());
            var indices = new List<int> { 17, 41 };

            Assert.That(m_BakingSet.ResolveSharedCellData(indices), Is.True);
            Assert.That(m_BakingSet.ResolvePerScenarioCellData(indices), Is.True);

            var format = m_Backend.format;
            Assert.That(format.sharedCalls, Is.EqualTo(1));
            Assert.That(format.scenarioCalls, Is.EqualTo(1));
            Assert.That(format.sharedContext.bakingSet, Is.SameAs(m_BakingSet));
            Assert.That(format.sharedContext.referenceVolume, Is.SameAs(m_Backend.volume));
            Assert.That(format.sharedContext.cellIndices, Is.SameAs(indices));
            Assert.That(format.scenarioContext.bakingSet, Is.SameAs(m_BakingSet));
            Assert.That(format.scenarioContext.referenceVolume, Is.SameAs(m_Backend.volume));
            Assert.That(format.scenarioContext.cellIndices, Is.SameAs(indices));
            Assert.That(m_BakingSet.cellDataMap.Keys, Is.EquivalentTo(indices));
            foreach (int index in indices)
                Assert.That(m_BakingSet.GetCellData<TestCellData>(index).scenarioResolved, Is.True);
            Assert.That(m_BakingSet.backendBakedData, Has.Count.EqualTo(1));
            Assert.That(m_BakingSet.backendBakedData, Is.All.TypeOf<TestBakedData>());
            Assert.That(format.createdBakedDataCount, Is.EqualTo(1));
        }

        [Test]
        public void ResolveReturnsTheSelectedFormatsFailure()
        {
            Assert.That(m_BakingSet.bakedData, Is.TypeOf<TestBakedData>());
            var indices = new List<int> { 17 };
            var format = m_Backend.format;
            format.sharedResult = false;

            Assert.That(m_BakingSet.ResolveSharedCellData(indices), Is.False);
            Assert.That(m_BakingSet.cellDataMap, Is.Empty);

            format.sharedResult = true;
            Assert.That(m_BakingSet.ResolveSharedCellData(indices), Is.True);
            format.scenarioResult = false;
            Assert.That(m_BakingSet.ResolvePerScenarioCellData(indices), Is.False);
            Assert.That(m_BakingSet.GetCellData<TestCellData>(17).scenarioResolved, Is.False);
            Assert.That(m_BakingSet.backendBakedData, Is.All.TypeOf<TestBakedData>());
        }

        [TestCase(true)]
        [TestCase(false)]
        public void InitializeTouchesOnlyActiveDataAndForwardsStreamingMode(bool useStreamingAsset)
        {
            var activeData = (TestBakedData)m_BakingSet.bakedData;
            var inactiveBackend = new TestBackend("InactiveProbeFormat", m_Backend.volume);
            SelectBackend(inactiveBackend);
            var inactiveData = (TestBakedData)m_BakingSet.bakedData;
            SelectBackend(m_Backend);
            m_BakingSet.GetCellDescs(m_Backend.id).Add(17, new CellDesc { index = 17 });

            m_BakingSet.Initialize(useStreamingAsset);

            Assert.That(activeData.initializeCalls, Is.EqualTo(1));
            Assert.That(activeData.initializedVolume, Is.SameAs(m_Backend.volume));
            Assert.That(inactiveData.initializeCalls, Is.Zero);
            Assert.That(inactiveBackend.format.sharedCalls, Is.Zero);
            Assert.That(inactiveBackend.format.scenarioCalls, Is.Zero);
            Assert.That(m_BakingSet.backendBakedData, Has.Count.EqualTo(2));
            Assert.That(m_BakingSet.backendBakedData, Is.All.TypeOf<TestBakedData>());
            Assert.That(m_Backend.format.createdBakedDataCount, Is.EqualTo(1));
            Assert.That(m_Backend.format.sharedCalls, Is.EqualTo(useStreamingAsset ? 0 : 1));
            Assert.That(m_Backend.format.scenarioCalls, Is.EqualTo(useStreamingAsset ? 0 : 1));

            if (useStreamingAsset)
            {
                var indices = new List<int> { 17 };
                Assert.That(m_BakingSet.ResolveSharedCellData(indices), Is.True);
                Assert.That(m_BakingSet.ResolvePerScenarioCellData(indices), Is.True);
            }

            Assert.That(m_Backend.format.sharedContext.useStreamingAsset, Is.EqualTo(useStreamingAsset));
            Assert.That(m_Backend.format.scenarioContext.useStreamingAsset, Is.EqualTo(useStreamingAsset));
            Assert.That(m_BakingSet.GetCellData<TestCellData>(17).scenarioResolved, Is.True);
        }

        static void SelectBackend(TestBackend backend)
        {
            s_Context.SetValue(null, new ProbeReferenceVolumeRuntimeContext(backend, default));
        }

        sealed class TestBackend : IProbeReferenceVolumeBackend
        {
            internal readonly TestFormat format;
            public string id => format.backendId;
            public int priority => 0;
            public TProbeReferenceVolumeBase volume { get; }
            public IProbeVolumeBakingDataFormat bakingDataFormat => format;

            internal TestBackend(string id, TProbeReferenceVolumeBase volume)
            {
                format = new TestFormat(id);
                this.volume = volume;
            }

            public bool CanUse(in ProbeVolumeSystemParameters parameters, out string reason) { reason = null; return true; }
            public void Initialize(in ProbeVolumeSystemParameters parameters) => Assert.Fail("Resolving baked data must not initialize a runtime backend.");
            public void Cleanup() => Assert.Fail("The test must not clean up a runtime backend.");
        }

        sealed class TestFormat : IProbeVolumeBakingDataFormat
        {
            public string backendId { get; }
            internal int createdBakedDataCount;
            internal int sharedCalls;
            internal int scenarioCalls;
            internal bool sharedResult = true;
            internal bool scenarioResult = true;
            internal ProbeVolumeBakingDataResolveContext sharedContext;
            internal ProbeVolumeBakingDataResolveContext scenarioContext;

            internal TestFormat(string backendId) => this.backendId = backendId;

            public ProbeVolumeBakedData CreateBakedData()
            {
                ++createdBakedDataCount;
                return new TestBakedData(backendId);
            }

            public ICellData CreateCellData(ProbeVolumeBakingSet bakingSet, int cellIndex, bool streamingOnly) => new TestCellData();

            public bool ResolveSharedCellData(in ProbeVolumeBakingDataResolveContext context)
            {
                ++sharedCalls;
                sharedContext = context;
                if (!sharedResult)
                    return false;
                foreach (int index in context.cellIndices)
                    context.bakingSet.cellDataMap[index] = CreateCellData(context.bakingSet, index, false);
                return true;
            }

            public bool ResolveScenarioCellData(in ProbeVolumeBakingDataResolveContext context)
            {
                ++scenarioCalls;
                scenarioContext = context;
                if (!scenarioResult)
                    return false;
                foreach (int index in context.cellIndices)
                    context.bakingSet.GetCellData<TestCellData>(index).scenarioResolved = true;
                return true;
            }

            public int GetChunkGPUMemory(ProbeVolumeBakingSet bakingSet, TProbeReferenceVolumeBase referenceVolume) => 0;
        }

        sealed class TestCellData : ICellData
        {
            internal bool scenarioResolved;
            public IEnumerable<KeyValuePair<string, IPerScenarioData>> EnumerateScenarioEntries() => Array.Empty<KeyValuePair<string, IPerScenarioData>>();
            public bool TryGetScenarioEntry(string name, out IPerScenarioData data) { data = null; return false; }
            public bool RemoveScenarioEntry(string name) => false;
            public bool RenameScenarioEntry(string oldName, string newName) => false;
            public void CleanupPerScenarioData(IPerScenarioData data) { }
            public void Cleanup(bool cleanScenarioList) { }
        }

        [Serializable]
        sealed class TestBakedData : ProbeVolumeBakedData
        {
            public override string backendId { get; }
            internal int initializeCalls;
            internal TProbeReferenceVolumeBase initializedVolume;
            internal override string streamingAssetsPath => "TestProbeData";
            internal override int scenarioCount => 0;

            internal TestBakedData(string backendId) => this.backendId = backendId;

            internal override void Initialize(TProbeReferenceVolumeBase referenceVolume)
            {
                ++initializeCalls;
                initializedVolume = referenceVolume;
            }

            internal override bool HasBakedData(string scenario, bool supportLightingScenarios) => true;
            internal override bool HasValidSharedData() => true;
            internal override bool HasValidScenarioData(string scenario, TProbeReferenceVolumeBase volume) => true;
            internal override bool HasSupportData() => false;
            internal override void CleanupStreamableAssets() { }
            internal override IEnumerable<ProbeVolumeBuildResource> GetBuildResources(IReadOnlyList<RenderPipelineAsset> renderPipelineAssets) => Array.Empty<ProbeVolumeBuildResource>();
            internal override bool TryGetScenarioHash(string scenario, out int sceneHash) { sceneHash = 0; return false; }
            internal override long GetDiskSizeOfSharedData() => 0;
            internal override long GetDiskSizeOfScenarioData(string scenario) => 0;
            internal override void ClearAssets() { }
            internal override void RemoveScenario(string scenario) { }
            internal override void RenameScenario(string scenario, string newName, string assetName) { }
            internal override void EnsureScenarioAssetNames(string assetName) { }
        }
    }
}
