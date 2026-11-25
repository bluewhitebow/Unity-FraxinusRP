using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.Tests
{
    public class APRTBackendIsolationTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void RemovingTheActiveSetSelectsAnAlreadyEnabledAdditiveSet(bool explicitlySwitched)
        {
            var volume = new APRTProbeReferenceVolume();
            var firstSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var secondSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var firstScene = EditorSceneManager.NewPreviewScene();
            var secondScene = EditorSceneManager.NewPreviewScene();
            try
            {
                var firstOwner = new GameObject("APRT additive first");
                SceneManager.MoveGameObjectToScene(firstOwner, firstScene);
                var first = firstOwner.AddComponent<ProbeVolumePerSceneData>();
                first.serializedBakingSet = firstSet;
                first.sceneGUID = "aprt-additive-first";
                var secondOwner = new GameObject("APRT additive second");
                SceneManager.MoveGameObjectToScene(secondOwner, secondScene);
                var second = secondOwner.AddComponent<ProbeVolumePerSceneData>();
                second.serializedBakingSet = secondSet;
                second.sceneGUID = "aprt-additive-second";
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                volume.RegisterPerSceneData(first);
                volume.RegisterPerSceneData(second);
                Assert.That(volume.currentBakingSet, Is.SameAs(firstSet));

                if (explicitlySwitched)
                {
                    volume.SetActiveBakingSet(secondSet);
                    volume.UnregisterPerSceneData(second);
                    Assert.That(volume.currentBakingSet, Is.SameAs(firstSet), "Switching sets must also switch the tracked scene ownership.");
                    volume.UnregisterPerSceneData(first);
                }
                else
                {
                    volume.UnregisterPerSceneData(first);
                    Assert.That(volume.currentBakingSet, Is.SameAs(secondSet), "The remaining scene is already enabled and cannot send another registration event.");
                    volume.UnregisterPerSceneData(second);
                }
                Assert.That(volume.currentBakingSet, Is.Null);
                Assert.That(volume.DataHasBeenLoaded(), Is.False);
                Assert.That(volume.GetVideoMemoryCost(), Is.Zero);
            }
            finally
            {
                volume.Cleanup();
                EditorSceneManager.ClosePreviewScene(firstScene);
                EditorSceneManager.ClosePreviewScene(secondScene);
                Object.DestroyImmediate(firstSet);
                Object.DestroyImmediate(secondSet);
            }
        }

        [Test]
        public void SetActiveSceneUsesRegisteredSceneDataWithoutEditorAssetLookup()
        {
            var volume = new APRTProbeReferenceVolume();
            var firstSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var secondSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var firstScene = EditorSceneManager.NewPreviewScene();
            var secondScene = EditorSceneManager.NewPreviewScene();
            try
            {
                // Unsaved scenes have no editor GUID-to-asset mapping. Scene registration
                // is the same source of baking-set references available in a player.
                var firstOwner = new GameObject("APRT first scene");
                firstOwner.SetActive(false);
                SceneManager.MoveGameObjectToScene(firstOwner, firstScene);
                var firstData = firstOwner.AddComponent<ProbeVolumePerSceneData>();
                firstData.serializedBakingSet = firstSet;
                firstData.sceneGUID = "aprt-first-scene";
                var secondOwner = new GameObject("APRT second scene");
                secondOwner.SetActive(false);
                SceneManager.MoveGameObjectToScene(secondOwner, secondScene);
                var secondData = secondOwner.AddComponent<ProbeVolumePerSceneData>();
                secondData.serializedBakingSet = secondSet;
                secondData.sceneGUID = "aprt-second-scene";

                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                volume.RegisterPerSceneData(firstData);
                volume.RegisterPerSceneData(secondData);
                volume.SetActiveBakingSet(firstSet);

                volume.SetActiveScene(secondScene);
                Assert.That(volume.currentBakingSet, Is.SameAs(secondSet));
                volume.SetActiveScene(firstScene);
                Assert.That(volume.currentBakingSet, Is.SameAs(firstSet));
                Assert.That(volume.GetVideoMemoryCost(), Is.Zero);
            }
            finally
            {
                volume.Cleanup();
                EditorSceneManager.ClosePreviewScene(firstScene);
                EditorSceneManager.ClosePreviewScene(secondScene);
                Object.DestroyImmediate(firstSet);
                Object.DestroyImmediate(secondSet);
            }
        }

        [TestCase("APRT", "APV")]
        public void ClearOnlyRemovesRequestedBackendLayout(string cleared, string retained)
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                set.GetCellDescs(cleared).Add(5, new CellDesc { index = 5 });
                var retainedDesc = new CellDesc { index = 7 };
                set.GetCellDescs(retained).Add(7, retainedDesc);
                set.SetPerSceneCellLists(cleared, new Dictionary<string, List<int>> { { "scene", new List<int> { 5 } } });
                set.SetPerSceneCellLists(retained, new Dictionary<string, List<int>> { { "scene", new List<int> { 7 } } });

                set.Clear(cleared);

                Assert.That(set.GetCellDescs(cleared), Is.Empty);
                Assert.That(set.GetCellDescs(retained)[7], Is.SameAs(retainedDesc));
                Assert.That(set.GetPerSceneCellLists(retained)["scene"], Is.EqualTo(new[] { 7 }));
            }
            finally { Object.DestroyImmediate(set); }
        }

        [Test]
        public void APRTLifecycleAndTogglesPreserveAPVState()
        {
            var aprt = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var apv = APVProbeReferenceVolume.instance;
            bool initialized = apv.isInitialized;
            bool enabled = apv.enabledBySRP;
            var activeSet = apv.currentBakingSet;
            var scenario = apv.lightingScenario;
            var sceneData = apv.perSceneDataList.ToArray();
            int memory = apv.GetVideoMemoryCost();
            var apvDebug = apv.probeVolumeDebug;
            bool drawsProbes = apvDebug.drawProbes;
            try
            {
                aprt.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
                aprt.SetActiveBakingSet(set);
                aprt.lightingScenario = "Night";
                aprt.probeVolumeDebug.drawProbes = !drawsProbes;
                aprt.SetEnableStateFromSRP(false);
                aprt.SetEnableStateFromSRP(true);
                aprt.UnloadAllCells();
                aprt.Clear();
                aprt.Cleanup();

                Assert.That(apv.isInitialized, Is.EqualTo(initialized));
                Assert.That(apv.enabledBySRP, Is.EqualTo(enabled));
                Assert.That(apv.currentBakingSet, Is.SameAs(activeSet));
                Assert.That(apv.lightingScenario, Is.EqualTo(scenario));
                Assert.That(apv.perSceneDataList, Is.EqualTo(sceneData));
                Assert.That(apv.GetVideoMemoryCost(), Is.EqualTo(memory));
                Assert.That(apv.probeVolumeDebug, Is.SameAs(apvDebug));
                Assert.That(apv.probeVolumeDebug.drawProbes, Is.EqualTo(drawsProbes));
            }
            finally { aprt.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void ClearingLegacyAPRTReferencesPreservesPayloadAssetsAndAPV()
        {
            string name = "APRTShellAssets_" + System.Guid.NewGuid().ToString("N");
            string folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);
            try
            {
                AssetDatabase.CreateFolder(folder, "Owner_APRT");
                string transportPath = folder + "/Owner_APRT/Cell_5.bytes";
                System.IO.File.WriteAllText(transportPath, "Opaque legacy transport retained for recovery");
                AssetDatabase.ImportAsset(transportPath, ImportAssetOptions.ForceSynchronousImport);
                var payload = AssetDatabase.LoadAssetAtPath<TextAsset>(transportPath);
                string skyPath = folder + "/Owner_APRT/SceneSky.asset";
                var sky = new Cubemap(4, TextureFormat.RGBA32, false);
                AssetDatabase.CreateAsset(sky, skyPath);
                string payloadGuid = AssetDatabase.AssetPathToGUID(transportPath);
                string skyGuid = AssetDatabase.AssetPathToGUID(skyPath);
                var original = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                AssetDatabase.CreateAsset(original, folder + "/Owner.asset");
                original.SetDefaults();
                APRTShellTests.PopulateLegacyData(original.aprtBakedData, payload);
                original.aprtBakedData.sceneSkyTexture = sky;
                original.aprtSkyTexture = sky;
                original.GetCellDescs("APRT").Add(5, new CellDesc { index = 5, probeCount = 64 });
                original.GetCellDescs("APV").Add(7, new CellDesc { index = 7 });
                original.SetPerSceneCellLists("APV", new Dictionary<string, List<int>> { { "scene", new List<int> { 7 } } });
                EditorUtility.SetDirty(original);
                AssetDatabase.SaveAssetIfDirty(original);
                Assert.That(AssetDatabase.CopyAsset(folder + "/Owner.asset", folder + "/Copy.asset"), Is.True);
                var copy = AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(folder + "/Copy.asset");
                Assert.That(copy.aprtBakedData.cells[0].asset, Is.EqualTo(payload));
                Assert.That(copy.aprtBakedData.HasValidSharedData(), Is.False);
                var retainedAPV = copy.GetCellDescs("APV")[7];

                copy.Clear("APRT");

                Assert.That(copy.aprtBakedData.cells, Is.Empty);
                Assert.That(copy.aprtBakedData.sceneSkyTexture, Is.Null);
                Assert.That(copy.aprtSkyTexture, Is.EqualTo(sky));
                Assert.That(copy.GetCellDescs("APRT"), Is.Empty);
                Assert.That(copy.GetCellDescs("APV")[7], Is.SameAs(retainedAPV));
                Assert.That(copy.GetSceneCellIndexList("APV", "scene"), Is.EqualTo(new[] { 7 }));
                Assert.That(original.aprtBakedData.cells[0].asset, Is.EqualTo(payload));
                Assert.That(original.aprtBakedData.sceneSkyTexture, Is.EqualTo(sky));

                original.Clear("APRT");

                Assert.That(AssetDatabase.AssetPathToGUID(transportPath), Is.EqualTo(payloadGuid));
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(transportPath), Is.Not.Null);
                Assert.That(AssetDatabase.AssetPathToGUID(skyPath), Is.EqualTo(skyGuid));
                Assert.That(AssetDatabase.LoadAssetAtPath<Cubemap>(skyPath), Is.Not.Null);
                Assert.That(original.GetCellDescs("APV").ContainsKey(7), Is.True);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void APRTScenarioSelectionSurvivesSerializationWithoutChangingAPV()
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var copy = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                set.SetDefaults();
                set.TryAddScenario("Night");
                set.SetActiveScenario("Night", false, new APRTProbeReferenceVolume());
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(set), copy);
                Assert.That(copy.GetLightingScenario("APRT"), Is.EqualTo("Night"));
                Assert.That(copy.GetLightingScenario("APV"), Is.EqualTo(TProbeReferenceVolumeBase.defaultLightingScenario));
            }
            finally
            {
                Object.DestroyImmediate(set);
                Object.DestroyImmediate(copy);
            }
        }

        [Test]
        public void ClearingAPRTSceneBackendPreservesSharedBakingSetReference()
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var owner = new GameObject("APRT scene isolation test");
            owner.SetActive(false);
            try
            {
                var data = owner.AddComponent<ProbeVolumePerSceneData>();
                data.serializedBakingSet = set;
                data.Clear("APRT");
                Assert.That(data.serializedBakingSet, Is.SameAs(set));
            }
            finally
            {
                Object.DestroyImmediate(owner);
                Object.DestroyImmediate(set);
            }
        }

    }
}
