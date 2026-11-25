using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Tests
{
    public class APVIntegrationTests
    {
        const string k_Core = "Packages/com.unity.render-pipelines.core/";
        string m_Folder;
        ProbeVolumeBakingSet m_Set;

        [SetUp]
        public void SetUp()
        {
            m_Folder = "Assets/APVTest_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(m_Folder);
            m_Set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
        }

        [TearDown]
        public void TearDown()
        {
            m_Set.apvBakedData.CleanupStreamableAssets();
            UnityEngine.Object.DestroyImmediate(m_Set);
            AssetDatabase.DeleteAsset(m_Folder);
        }

        [Test]
        public void RuntimeConstantsReachSamplingShader()
        {
            if (!SystemInfo.supportsComputeShaders)
                Assert.Ignore("Compute shaders are required.");
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(k_Core + "Tests/Editor/APV/APVConstants.compute");
            var constants = new APVShaderVariablesProbeVolumes
            {
                _Offset_LayerCount = new Vector4(3, 5, 7, 4),
                _FrameIndex_Weights = new Vector4(11, 0.75f, 0.25f, 1)
            };
            using var result = new ComputeBuffer(2, 16);
            using var cmd = new CommandBuffer();
            ConstantBuffer.PushGlobal(cmd, constants, APVProbeReferenceVolume.shaderVariablesID);
            cmd.SetComputeBufferParam(shader, 0, "_Result", result);
            cmd.DispatchCompute(shader, 0, 1, 1, 1);
            Graphics.ExecuteCommandBuffer(cmd);
            var values = new Vector4[2];
            result.GetData(values);
            Assert.That(values[0], Is.EqualTo(new Vector4(3, 5, 7, 0.75f)));
            Assert.That(values[1], Is.EqualTo(new Vector4(0.25f, 1, 11, 4)));
        }

        // Exercise the actual asynchronous disk reader and upload shaders together.
        // Scenario 1 deliberately skips shared data while keeping L2/occlusion offsets.
        [TestCase(ProbeVolumeSHBands.SphericalHarmonicsL1, false, false)]
        [TestCase(ProbeVolumeSHBands.SphericalHarmonicsL1, true, true)]
        [TestCase(ProbeVolumeSHBands.SphericalHarmonicsL1, true, false)]
        [TestCase(ProbeVolumeSHBands.SphericalHarmonicsL2, false, true)]
        [TestCase(ProbeVolumeSHBands.SphericalHarmonicsL2, true, true)]
        [TestCase(ProbeVolumeSHBands.SphericalHarmonicsL2, true, false)]
        public void DiskStreamingUploadsCorrectChannels(ProbeVolumeSHBands bands, bool occlusion, bool shared)
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("Compute shaders and GPU readback are required.");
            int probes = ProbeBrickPoolBase.GetChunkSizeInProbeCount();
            m_Set.L0ChunkSize = probes * 8;
            m_Set.L1ChunkSize = probes * 4;
            m_Set.L2TextureChunkSize = probes * 4;
            m_Set.ProbeOcclusionChunkSize = probes * 4;
            m_Set.sharedValidityMaskChunkSize = probes;
            m_Set.sharedSkyOcclusionL0L1ChunkSize = probes * 8;
            m_Set.sharedSkyShadingDirectionIndicesChunkSize = probes;
            m_Set.sharedDataChunkSize = probes * 10;
            m_Set.bakedSkyOcclusion = true;
            m_Set.bakedSkyShadingDirection = true;
            m_Set.bakedProbeOcclusion = occlusion;
            m_Set.bakedMaskCount = 1;
            m_Set.maxSHChunkCount = 8;
            var shBytes = new List<byte>();
            AppendHalf(shBytes, probes * 4, 0x3c00); // 1.0
            Append(shBytes, probes * 4, 32);
            Append(shBytes, probes * 4, 64);
            var sharedBytes = new List<byte>();
            Append(sharedBytes, probes, 255);
            AppendHalf(sharedBytes, probes * 4, 0x3800); // 0.5
            Append(sharedBytes, probes, 127);
            var optionalBytes = new List<byte>();
            for (byte i = 1; i <= 4; i++) Append(optionalBytes, probes * 4, (byte)(i * 16));
            var occlusionBytes = new List<byte>();
            Append(occlusionBytes, probes * 4, 192);
            m_Set.cellSharedDataAsset = Asset("shared", sharedBytes.ToArray());
            m_Set.cellBricksDataAsset = Asset("bricks", new byte[16]);
            var scenario = new ProbeVolumeBakingSet.PerScenarioDataInfo
            {
                cellDataAsset = Asset("sh", shBytes.ToArray()),
                cellOptionalDataAsset = Asset("l2", optionalBytes.ToArray()),
                cellProbeOcclusionDataAsset = occlusion ? Asset("occlusion", occlusionBytes.ToArray()) : null
            };
            m_Set.scenarios["Default"] = scenario;
            var scratchPool = new APVProbeVolumeScratchBufferPool(m_Set, bands);
            var cell = new APVCell { desc = new CellDesc { index = 0, bricksCount = 1, shChunkCount = 1 }, data = new APVCellData() };
            cell.poolInfo.chunkList.Add(default);
            cell.blendingInfo.chunkList.Add(default);
            var volume = new StreamingVolume();
            volume.Configure(m_Set, scratchPool, bands);
            volume.cells.Add(0, cell);
            var request = new APVProbeReferenceVolume.CellStreamingRequest
            {
                cell = cell, scenarioData = scenario, poolIndex = shared ? 0 : 1, streamSharedData = shared
            };
            APVDataLocation textures = default;
            try
            {
                Assert.That(volume.Read(request), Is.True);
                request.WaitAll();
                request.UpdateState();
                Assert.That(request.state, Is.EqualTo(APVProbeReferenceVolume.CellStreamingRequest.State.Complete));
                var layout = request.GetScratchBufferLayout<APVProbeReferenceVolume.CellStreamingScratchBufferLayout>();
                int end = layout._ProbeOcclusionOffset + layout._ProbeOcclusionSize;
                Assert.That(end, Is.EqualTo(32 + scratchPool.chunkSize), "All channels must fit the allocated payload.");
                textures = APVProbeBrickPool.CreateDataLocation(probes, false, bands, "APVTest", true, true, false, true, true, occlusion, out _);
                using var buffer = new ComputeBuffer(request.scratchBuffer.stagingBuffer.Length / 4, 4, ComputeBufferType.Raw);
                buffer.SetData(request.scratchBuffer.stagingBuffer.Reinterpret<uint>(1));
                Upload(buffer, layout, textures, bands, occlusion, shared);
                AssertPixel(textures.TexL0_L1rx, 1.0f);
                AssertPixel(textures.TexL1_G_ry, 32 / 255f);
                AssertPixel(textures.TexL1_B_rz, 64 / 255f);
                if (shared)
                {
                    AssertPixel(textures.TexValidity, 1.0f);
                    AssertPixel(textures.TexSkyOcclusion, 0.5f);
                    AssertPixel(textures.TexSkyShadingDirectionIndices, 127 / 255f);
                }
                if (occlusion) AssertPixel(textures.TexProbeOcclusion, 192 / 255f);
                if (bands == ProbeVolumeSHBands.SphericalHarmonicsL2)
                {
                    AssertPixel(textures.TexL2_0, 16 / 255f);
                    AssertPixel(textures.TexL2_1, 32 / 255f);
                    AssertPixel(textures.TexL2_2, 48 / 255f);
                    AssertPixel(textures.TexL2_3, 64 / 255f);
                }
            }
            finally
            {
                request.Dispose();
                if (request.scratchBuffer != null) scratchPool.ReleaseScratchBuffer(request.scratchBuffer);
                scratchPool.Cleanup();
                cell.data.Cleanup(true);
                textures.Cleanup();
            }
        }

        [Test]
        public void MissingOptionalAssetsAreSafeToValidateAndRelease()
        {
            var scenario = new ProbeVolumeBakingSet.PerScenarioDataInfo();
            Assert.That(scenario.ComputeHasValidData(ProbeVolumeSHBands.SphericalHarmonicsL1), Is.False);
            scenario.cellDataAsset = Asset("l1only", new byte[16]);
            m_Set.scenarios["Default"] = scenario;
            Assert.That(scenario.ComputeHasValidData(ProbeVolumeSHBands.SphericalHarmonicsL1), Is.True);
            Assert.That(scenario.ComputeHasValidData(ProbeVolumeSHBands.SphericalHarmonicsL2), Is.False);
            scenario.cellDataAsset.OpenFile().JobHandle.Complete();
            m_Set.apvBakedData.CleanupStreamableAssets();
            Assert.That(scenario.cellDataAsset.IsOpen(), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SelectedBackendIsVisibleDuringInitialization(bool failInitialization)
        {
            var contextField = typeof(ProbeReferenceVolumeProvider).GetField("s_context", BindingFlags.Static | BindingFlags.NonPublic);
            var previousContext = contextField.GetValue(null);
            contextField.SetValue(null, null);
            var backend = new InitializationBackend { failInitialization = failInitialization };
            ProbeReferenceVolumeProvider.RegisterBackend(backend);
            try
            {
                if (failInitialization)
                    Assert.Throws<InvalidOperationException>(() => ProbeReferenceVolumeProvider.Initialize(default, backend.id));
                else
                    ProbeReferenceVolumeProvider.Initialize(default, backend.id);
                Assert.That(backend.wasSelectedDuringInitialize, Is.True);
                if (failInitialization)
                {
                    Assert.That(backend.cleanedUp, Is.True);
                    Assert.That(ProbeReferenceVolumeProvider.context, Is.Null);
                }
                else
                {
                    Assert.That(ProbeReferenceVolumeProvider.activeBackend, Is.SameAs(backend));
                    ProbeReferenceVolumeProvider.Cleanup();
                    Assert.That(backend.cleanedUp, Is.True);
                    Assert.That(ProbeReferenceVolumeProvider.context, Is.Null);
                }
            }
            finally
            {
                ProbeReferenceVolumeProvider.Cleanup();
                var backends = (List<IProbeReferenceVolumeBackend>)typeof(ProbeReferenceVolumeProvider)
                    .GetField("s_backends", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                backends.Remove(backend);
                contextField.SetValue(null, previousContext);
            }
        }

        [Test]
        public void PlacementQueriesDoNotCreateOrSwitchBakingSessions()
        {
            var active = ProbeVolumeBakingProvider.activeOrNull;
            var scenes = ProbeVolumeBakingProvider.partialBakeSceneList;
            var freeze = ProbeVolumeBakingProvider.isFreezingPlacement;
            for (int i = 0; i < 3; ++i)
            {
                Assert.That(ProbeVolumeBakingProvider.CanFreezePlacement(null), Is.False);
                Assert.That(ProbeVolumeBakingProvider.CanFreezePlacement(m_Set), Is.False);
            }
            Assert.That(ProbeVolumeBakingProvider.activeOrNull, Is.SameAs(active));
            Assert.That(ProbeVolumeBakingProvider.partialBakeSceneList, Is.SameAs(scenes));
            Assert.That(ProbeVolumeBakingProvider.isFreezingPlacement, Is.EqualTo(freeze));
        }

        [Test]
        public void FreezePlacementRequiresDataFromTheRequestedBakingSet()
        {
            var volume = new PlacementVolume();
            volume.SetEnableStateFromSRP(true);
            string sceneGUID = Guid.NewGuid().ToString("N");
            var otherSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var owner = new GameObject("APV placement query test");
            owner.SetActive(false);
            var sceneData = owner.AddComponent<ProbeVolumePerSceneData>();
            sceneData.sceneGUID = sceneGUID;
            sceneData.serializedBakingSet = m_Set;
            volume.perSceneDataList.Add(sceneData);
            m_Set.AddScene(sceneGUID);
            m_Set.perSceneCellLists[sceneGUID] = new List<int> { 0 };
            try
            {
                Assert.That(APVProbeVolumeBaking.CanFreezePlacement(volume, m_Set), Is.True);
                Assert.That(APVProbeVolumeBaking.CanFreezePlacement(volume, otherSet), Is.False);
                Assert.That(APVProbeVolumeBaking.CanFreezePlacement(volume, m_Set, new HashSet<string>()), Is.False);
                Assert.That(APVProbeVolumeBaking.CanFreezePlacement(volume, m_Set, new HashSet<string> { sceneGUID }), Is.True);

                sceneData.serializedBakingSet = otherSet;
                Assert.That(APVProbeVolumeBaking.CanFreezePlacement(volume, m_Set), Is.False);
                sceneData.serializedBakingSet = m_Set;
                m_Set.perSceneCellLists.Clear();
                Assert.That(APVProbeVolumeBaking.CanFreezePlacement(volume, m_Set), Is.False);
                m_Set.perSceneCellLists[sceneGUID] = new List<int> { 0 };
                volume.SetEnableStateFromSRP(false);
                Assert.That(APVProbeVolumeBaking.CanFreezePlacement(volume, m_Set), Is.False);
            }
            finally
            {
                m_Set.RemoveScene(sceneGUID);
                volume.perSceneDataList.Clear();
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(otherSet);
            }
        }

        sealed class PlacementVolume : APVProbeReferenceVolume
        {
            public override bool isInitialized => true;
            internal override bool supportLightingScenarios => true;
        }

        sealed class InitializationBackend : IProbeReferenceVolumeBackend
        {
            public string id { get; } = "APVTest_" + Guid.NewGuid().ToString("N");
            public int priority => int.MinValue;
            public TProbeReferenceVolumeBase volume { get; } = new APVProbeReferenceVolume();
            public IProbeVolumeBakingDataFormat bakingDataFormat { get; } = new APVProbeVolumeBakingDataFormat();
            internal bool failInitialization, wasSelectedDuringInitialize, cleanedUp;
            public bool CanUse(in ProbeVolumeSystemParameters parameters, out string reason) { reason = null; return true; }
            public void Initialize(in ProbeVolumeSystemParameters parameters)
            {
                wasSelectedDuringInitialize = ReferenceEquals(ProbeReferenceVolumeProvider.instance, volume) &&
                    ReferenceEquals(ProbeReferenceVolumeProvider.activeBackend, this);
                if (failInitialization) throw new InvalidOperationException("Test initialization failure");
            }
            public void Cleanup() { cleanedUp = true; }
        }

        ProbeVolumeStreamableAsset Asset(string name, byte[] bytes)
        {
            string path = m_Folder + "/" + name + ".bytes";
            File.WriteAllBytes(path, bytes);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var descs = new SerializedDictionary<int, ProbeVolumeStreamableAsset.StreamableCellDesc>();
            descs[0] = new ProbeVolumeStreamableAsset.StreamableCellDesc { offset = 0, elementCount = 1 };
            return new ProbeVolumeStreamableAsset("APV", descs, bytes.Length, "test", AssetDatabase.AssetPathToGUID(path));
        }

        static void Append(List<byte> bytes, int count, byte value)
        {
            for (int i = 0; i < count; i++) bytes.Add(value);
        }

        static void AppendHalf(List<byte> bytes, int count, ushort value)
        {
            for (int i = 0; i < count; i++) { bytes.Add((byte)value); bytes.Add((byte)(value >> 8)); }
        }

        static void AssertPixel(Texture texture, float expected)
        {
            var readback = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat);
            readback.WaitForCompletion();
            Assert.That(readback.hasError, Is.False);
            var pixels = readback.GetData<Color>();
            Assert.That(pixels[0].r, Is.EqualTo(expected).Within(0.002f), texture.name);
            Assert.That(pixels[pixels.Length - 1].r, Is.EqualTo(expected).Within(0.002f), texture.name + " last probe");
        }

        static void Upload(ComputeBuffer buffer, APVProbeReferenceVolume.CellStreamingScratchBufferLayout layout,
            APVDataLocation textures, ProbeVolumeSHBands bands, bool occlusion, bool shared)
        {
            var shader = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(k_Core + "Runtime/Lighting/ProbeVolume/ProbeVolumeUploadData.compute"));
            var l2 = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(k_Core + "Runtime/Lighting/ProbeVolume/ProbeVolumeUploadDataL2.compute"));
            try
            {
                if (shared)
                {
                    shader.EnableKeyword("PROBE_VOLUMES_SHARED_DATA");
                    shader.EnableKeyword("PROBE_VOLUMES_SKY_OCCLUSION");
                    shader.EnableKeyword("PROBE_VOLUMES_SKY_SHADING_DIRECTION");
                }
                if (occlusion) shader.EnableKeyword("PROBE_VOLUMES_PROBE_OCCLUSION");
                using var cmd = new CommandBuffer();
                ConstantBuffer.Push(cmd, layout, shader, Shader.PropertyToID("CellStreamingScratchBufferLayout"));
                cmd.SetComputeBufferParam(shader, 0, "_ScratchBuffer", buffer);
                cmd.SetComputeTextureParam(shader, 0, "_Out_L0_L1Rx", textures.TexL0_L1rx);
                cmd.SetComputeTextureParam(shader, 0, "_Out_L1G_L1Ry", textures.TexL1_G_ry);
                cmd.SetComputeTextureParam(shader, 0, "_Out_L1B_L1Rz", textures.TexL1_B_rz);
                if (shared)
                {
                    cmd.SetComputeTextureParam(shader, 0, "_Out_Shared", textures.TexValidity);
                    cmd.SetComputeTextureParam(shader, 0, "_Out_SkyOcclusionL0L1", textures.TexSkyOcclusion);
                    cmd.SetComputeTextureParam(shader, 0, "_Out_SkyShadingDirectionIndices", textures.TexSkyShadingDirectionIndices);
                }
                if (occlusion) cmd.SetComputeTextureParam(shader, 0, "_Out_ProbeOcclusion", textures.TexProbeOcclusion);
                int groups = ProbeBrickPoolBase.GetChunkSizeInProbeCount() / 256;
                cmd.DispatchCompute(shader, 0, groups, 1, 1);
                if (bands == ProbeVolumeSHBands.SphericalHarmonicsL2)
                {
                    ConstantBuffer.Push(cmd, layout, l2, Shader.PropertyToID("CellStreamingScratchBufferLayout"));
                    cmd.SetComputeBufferParam(l2, 0, "_ScratchBuffer", buffer);
                    cmd.SetComputeTextureParam(l2, 0, "_Out_L2_0", textures.TexL2_0);
                    cmd.SetComputeTextureParam(l2, 0, "_Out_L2_1", textures.TexL2_1);
                    cmd.SetComputeTextureParam(l2, 0, "_Out_L2_2", textures.TexL2_2);
                    cmd.SetComputeTextureParam(l2, 0, "_Out_L2_3", textures.TexL2_3);
                    cmd.DispatchCompute(l2, 0, groups, 1, 1);
                }
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(shader);
                UnityEngine.Object.DestroyImmediate(l2);
            }
        }

        sealed class StreamingVolume : APVProbeReferenceVolume
        {
            internal void Configure(ProbeVolumeBakingSet set, APVProbeVolumeScratchBufferPool pool, ProbeVolumeSHBands bands)
            {
                m_CurrentBakingSet = set;
                typeof(APVProbeReferenceVolume).BaseType.GetField("m_ScratchBufferPool", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, pool);
                typeof(APVProbeReferenceVolume).GetField("m_SHBands", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, bands);
            }

            internal bool Read(CellStreamingRequest request)
            {
                return (bool)typeof(APVProbeReferenceVolume).BaseType.GetMethod("ProcessDiskStreamingRequest", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(this, new object[] { request });
            }
        }
    }
}
