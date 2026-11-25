using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace UnityEngine.Rendering.Tests
{
    public class APVFrozenPlacementStreamingTests
    {
        const int k_CellIndex = 7;
        string m_Folder, m_SceneA, m_SceneB;
        ProbeVolumeBakingSet m_Set;
        GameObject m_OwnerA, m_OwnerB;

        [SetUp]
        public void SetUp()
        {
            string folderName = "APVFrozenPlacement_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folderName);
            m_Folder = "Assets/" + folderName;
            m_Set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            m_Set.SetDefaults();
            m_Set.minDistanceBetweenProbes = 1;
            m_Set.simplificationLevels = 1;
            m_SceneA = Guid.NewGuid().ToString("N");
            m_SceneB = Guid.NewGuid().ToString("N");
            m_Set.AddScene(m_SceneA);
            m_Set.AddScene(m_SceneB);
        }

        [TearDown]
        public void TearDown()
        {
            m_Set.Cleanup("APV");
            m_Set.RemoveScene(m_SceneA);
            m_Set.RemoveScene(m_SceneB);
            UnityEngine.Object.DestroyImmediate(m_OwnerA);
            UnityEngine.Object.DestroyImmediate(m_OwnerB);
            UnityEngine.Object.DestroyImmediate(m_Set);
            AssetDatabase.DeleteAsset(m_Folder);
        }

        [TestCase(false, false, true)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        [TestCase(false, false, false)]
        public void FrozenPlacementReadsSavedBricksWithoutResidencyAndRetainsBothScenes(
            bool streamingMetadataExists, bool oversizedReadScratch, bool useStreamingAsset)
        {
            var volume = new UnresidentStreamingVolume { diskStreaming = useStreamingAsset };
            var sceneA = SceneData(ref m_OwnerA, m_SceneA);
            var sceneB = SceneData(ref m_OwnerB, m_SceneB);
            volume.perSceneDataList.Add(sceneA);
            volume.perSceneDataList.Add(sceneB);
            var cellPosition = new Vector3Int(1, 0, -1);
            var expectedBricks = new[]
            {
                new ProbeBrickIndex.Brick(new Vector3Int(3, 0, -3), 0),
                new ProbeBrickIndex.Brick(new Vector3Int(4, 0, -3), 1)
            };
            m_Set.cellDescs[k_CellIndex] = new CellDesc
            {
                index = k_CellIndex, position = cellPosition,
                bricksCount = expectedBricks.Length, probeCount = expectedBricks.Length * 64
            };
            m_Set.perSceneCellLists[m_SceneA] = new List<int> { k_CellIndex };
            m_Set.perSceneCellLists[m_SceneB] = new List<int> { k_CellIndex };

            // Put unrelated bricks on either side of the selected cell. The
            // frozen path must read the descriptor's byte offset and count.
            byte[] payload;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    WriteBrick(writer, new ProbeBrickIndex.Brick(new Vector3Int(99, 99, 99), 6));
                    foreach (var brick in expectedBricks) WriteBrick(writer, brick);
                    WriteBrick(writer, new ProbeBrickIndex.Brick(new Vector3Int(-99, -99, -99), 6));
                }
                payload = stream.ToArray();
            }
            m_Set.apvBakedData.cellBricksDataAsset = Asset("bricks", payload, k_CellIndex, 16, expectedBricks.Length, 16);
            // Select the asset-read path without initializing GPU pools or eagerly
            // decoding the cells. Without streaming this returns the entire
            // TextAsset, so the frozen reader must honor the selected cell offset.
            typeof(ProbeVolumeBakingSet).GetField("m_UseStreamingAsset", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(m_Set, useStreamingAsset);
            Assert.That(m_Set.useStreamingAsset, Is.EqualTo(useStreamingAsset));
            APVCellData metadata = null;
            if (streamingMetadataExists)
            {
                metadata = (APVCellData)new APVProbeVolumeBakingDataFormat().CreateCellData(m_Set, k_CellIndex, true);
                m_Set.cellDataMap.Add(k_CellIndex, metadata);
                Assert.That(metadata.bricks.IsCreated, Is.False);
            }

            if (oversizedReadScratch)
            {
                // Synchronous reads reuse larger buffers. The returned NativeArray
                // can therefore exceed this cell's two saved bricks.
                var seed = Asset("large-scratch", new byte[512], 0, 0, 512, 1);
                var scratch = m_Set.LoadStreamableAssetData<byte>(seed, new List<int> { 0 });
                try { Assert.That(scratch.Length, Is.GreaterThan(expectedBricks.Length * 16)); }
                finally { m_Set.ReleaseStreamableAssetData(scratch); seed.CloseFile(); }
            }

            var baker = new APVProbeVolumeBaking(volume, m_Set);
            typeof(ProbeVolumeBaking<APVProbeReferenceVolume>)
                .GetField("m_Layout", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(baker, new ProbeVolumeLayout(1, 1, Vector3.zero));
            var collection = typeof(APVProbeVolumeBaking).GetMethod("GetBricksFromLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
            var result = (ProbeSubdivisionResult)collection.Invoke(baker, null);

            Assert.That(result.cells.Count, Is.EqualTo(1), "A shared cell must be collected only once.");
            Assert.That(result.cells[0].position, Is.EqualTo(cellPosition));
            Assert.That(result.cells[0].bricks, Is.EqualTo(expectedBricks), "Saved brick order and subdivision must survive streaming.");
            Assert.That(result.scenesPerCells[cellPosition], Is.EquivalentTo(new[] { m_SceneA, m_SceneB }));
            Assert.That(volume.cells.Count, Is.Zero, "Frozen collection must not require GPU residency.");
            Assert.That(volume.diskStreamingEnabled, Is.EqualTo(useStreamingAsset), "The reader must preserve streaming configuration.");
            Assert.That(m_Set.useStreamingAsset, Is.EqualTo(useStreamingAsset), "The reader must preserve the asset-read path.");
            Assert.That(m_Set.cellDataMap.Count, Is.EqualTo(streamingMetadataExists ? 1 : 0));
            if (streamingMetadataExists)
            {
                Assert.That(m_Set.cellDataMap[k_CellIndex], Is.SameAs(metadata));
                Assert.That(metadata.bricks.IsCreated, Is.False, "The reader must preserve live streaming metadata.");
            }
            Assert.That(m_Set.apvBakedData.cellBricksDataAsset.IsOpen(), Is.False, "Synchronous brick reads must close the saved file.");
        }

        ProbeVolumePerSceneData SceneData(ref GameObject owner, string guid)
        {
            owner = new GameObject("Frozen placement scene data");
            owner.SetActive(false);
            var data = owner.AddComponent<ProbeVolumePerSceneData>();
            data.sceneGUID = guid;
            data.serializedBakingSet = m_Set;
            return data;
        }

        ProbeVolumeStreamableAsset Asset(string name, byte[] bytes, int cell, int offset, int count, int elementSize)
        {
            string path = m_Folder + "/" + name + ".bytes";
            File.WriteAllBytes(path, bytes);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var descriptors = new SerializedDictionary<int, ProbeVolumeStreamableAsset.StreamableCellDesc>();
            descriptors[cell] = new ProbeVolumeStreamableAsset.StreamableCellDesc { offset = offset, elementCount = count };
            return new ProbeVolumeStreamableAsset("APV", descriptors, elementSize, "test", AssetDatabase.AssetPathToGUID(path));
        }

        static void WriteBrick(BinaryWriter writer, ProbeBrickIndex.Brick brick)
        {
            writer.Write(brick.position.x);
            writer.Write(brick.position.y);
            writer.Write(brick.position.z);
            writer.Write(brick.subdivisionLevel);
        }

        sealed class UnresidentStreamingVolume : APVProbeReferenceVolume
        {
            internal bool diskStreaming;
            public override bool isInitialized => true;
            internal override bool diskStreamingEnabled => diskStreaming;
            internal override bool supportLightingScenarios => true;
        }
    }
}
