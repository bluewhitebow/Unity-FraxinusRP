using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;

namespace UnityEngine.Rendering.Tests
{
    public class APRTOptionsTests
    {
        [TestCase(0)]
        [TestCase(1)]
        public void MissingOrUnoverriddenComponentPreservesLegacySettings(int model)
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var options = ScriptableObject.CreateInstance<APRTOptions>();
            try
            {
                SetDistinctLegacyValues(set);
                string before = EditorJsonUtility.ToJson(set);
                var expected = APRTRelightSettings.FromBakingSet(set, (APRTLightingModel)model);
                Assert.That(APRTRelightSettings.Resolve(set, (APRTLightingModel)model, null), Is.EqualTo(expected));
                Assert.That(APRTRelightSettings.Resolve(set, (APRTLightingModel)model, options), Is.EqualTo(expected));
                Assert.That(EditorJsonUtility.ToJson(set), Is.EqualTo(before), "Resolving camera settings must not rewrite the Baking Set.");
            }
            finally { Object.DestroyImmediate(options); Object.DestroyImmediate(set); }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void OnlyCheckedVolumeFieldsReplaceLegacyValues(int model)
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var options = ScriptableObject.CreateInstance<APRTOptions>();
            try
            {
                SetDistinctLegacyValues(set);
                options.giIntensity.Override(2.2f);
                options.skyIntensity.Override(.75f);
                options.normalBias.Override(.125f);
                options.leakReduction.Override(false);
                options.indirectIntensity.value = 8; // Deliberately unchecked.
                var actual = APRTRelightSettings.Resolve(set, (APRTLightingModel)model, options);
                Assert.That(actual.giIntensity, Is.EqualTo(2.2f));
                Assert.That(actual.skyIntensity, Is.EqualTo(.75f));
                Assert.That(actual.normalBias, Is.EqualTo(.125f));
                Assert.That(actual.leakReduction, Is.False);
                Assert.That(actual.indirectIntensity, Is.EqualTo(set.aprtIndirectIntensity));
                Assert.That(actual.visibilityBias, Is.EqualTo(set.aprtVisibilityBias));
                Assert.That(actual.floorBoost, Is.EqualTo(set.aprtFloorBoost));
                Assert.That(set.aprtGIIntensity, Is.EqualTo(4.25f));
            }
            finally { Object.DestroyImmediate(options); Object.DestroyImmediate(set); }
        }

        [Test]
        public void LegacyCubemapCannotOverrideSceneSkyOrRewriteTheBakingSet()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var legacy = new Cubemap(8, TextureFormat.RGBAFloat, false);
            var sceneCube = new Cubemap(8, TextureFormat.RGBAFloat, false);
            var previousSkybox = RenderSettings.skybox;
            var skybox = CreateSkyMaterial(sceneCube);
            try
            {
                FillCube(legacy, Color.red); FillCube(sceneCube, Color.blue);
                var data = APRTSkyIrradianceTests.SkyGridData(); data.lightingModel = (APRTLightingModel)1;
                set.CommitAPRTBakedData(data); APRTSkyIrradianceTests.PhysicalSkySettings(set);
                set.aprtSkyTexture = legacy;
                string before = EditorJsonUtility.ToJson(set);
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                Assert.That(volume.PrepareRelight(null), Is.True);
                RenderSettings.skybox = skybox;
                volume.UpdateSkyEnvironment(null);
                APRTSkyIrradianceTests.AssertRGB(volume.GetSkyRadianceCoefficients()[0], Vector3.forward, .002f, "Scene sky must win over the old serialized red cubemap");
                FillCube(sceneCube, Color.green);
                volume.UpdateSkyEnvironment(null);
                APRTSkyIrradianceTests.AssertRGB(volume.GetSkyRadianceCoefficients()[0], Vector3.up, .002f, "Changed scene sky");
                Assert.That(EditorJsonUtility.ToJson(set), Is.EqualTo(before));
            }
            finally
            {
                RenderSettings.skybox = previousSkybox; volume.Cleanup();
                Object.DestroyImmediate(skybox); Object.DestroyImmediate(sceneCube); Object.DestroyImmediate(legacy); Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void CopiedLegacySettingsRoundTripThroughAFullVolumeOverride()
        {
            var source = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var destination = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var options = ScriptableObject.CreateInstance<APRTOptions>();
            var cube = new Cubemap(1, TextureFormat.RGBAFloat, false);
            try
            {
                SetDistinctLegacyValues(source); source.aprtSkyTexture = cube;
                var expected = APRTRelightSettings.FromBakingSet(source, (APRTLightingModel)1);
                options.ApplySettings(expected); options.SetAllOverridesTo(true);
                Assert.That(APRTRelightSettings.Resolve(destination, (APRTLightingModel)1, options), Is.EqualTo(expected),
                    "Copying the old appearance to a Volume must include every exposed relighting and receiving control.");
                Assert.That(destination.aprtSkyTexture, Is.Null);
                Assert.That(destination.aprtGIIntensity, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(cube); Object.DestroyImmediate(options);
                Object.DestroyImmediate(source); Object.DestroyImmediate(destination);
            }
        }

        [Test]
        public void InactiveComponentCannotOverrideRelighting()
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var options = ScriptableObject.CreateInstance<APRTOptions>();
            try
            {
                SetDistinctLegacyValues(set);
                options.giIntensity.Override(0);
                options.skyIntensity.Override(0);
                options.active = false;
                Assert.That(APRTRelightSettings.Resolve(set, (APRTLightingModel)1, options),
                    Is.EqualTo(APRTRelightSettings.FromBakingSet(set, (APRTLightingModel)1)));
            }
            finally { Object.DestroyImmediate(options); Object.DestroyImmediate(set); }
        }

        [Test]
        public void LocalVolumeBlendsFromLegacyBaselineWithoutChangingProfile()
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var previousSet = APRTProbeReferenceVolume.instance.currentBakingSet;
            var manager = new VolumeManager { baseComponentTypeArray = new[] { typeof(APRTOptions) } };
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var source = profile.Add<APRTOptions>();
            var volumeObject = new GameObject("APRT local-volume test");
            var triggerObject = new GameObject("APRT volume trigger");
            Volume volume = null;
            try
            {
                SetDistinctLegacyValues(set);
                APRTProbeReferenceVolume.instance.SetActiveBakingSet(set);
                manager.InitializeInternal();
                source.giIntensity.Override(8.25f);
                source.floorBoost.Override(6.5f);
                source.skyIntensity.Override(3);
                string sourceBefore = EditorJsonUtility.ToJson(source);
                var collider = volumeObject.AddComponent<BoxCollider>();
                collider.size = Vector3.one * 4;
                volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = false; volume.weight = .5f; volume.blendDistance = 0;
                volume.sharedProfile = profile;
                manager.Register(volume);
                manager.Update(manager.stack, triggerObject.transform, ~0);
                var blended = APRTRelightSettings.Resolve(set, (APRTLightingModel)1, manager.stack.GetComponent<APRTOptions>());
                Assert.That(blended.giIntensity, Is.EqualTo(6.25f).Within(.00001f), "Half influence starts at the serialized 4.25 baseline.");
                Assert.That(blended.floorBoost, Is.EqualTo(4.5f).Within(.00001f));
                Assert.That(blended.skyIntensity, Is.EqualTo(1.7f).Within(.00001f));
                Assert.That(blended.normalBias, Is.EqualTo(set.aprtNormalBias));
                Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(sourceBefore));

                // Leaving a local volume resets all its checked fields to legacy fallbacks.
                triggerObject.transform.position = Vector3.one * 100;
                manager.Update(manager.stack, triggerObject.transform, ~0);
                Assert.That(APRTRelightSettings.Resolve(set, (APRTLightingModel)1, manager.stack.GetComponent<APRTOptions>()),
                    Is.EqualTo(APRTRelightSettings.FromBakingSet(set, (APRTLightingModel)1)));
            }
            finally
            {
                if (volume != null) manager.Unregister(volume);
                if (manager.isInitialized) manager.Deinitialize();
                APRTProbeReferenceVolume.instance.SetActiveBakingSet(previousSet);
                Object.DestroyImmediate(triggerObject); Object.DestroyImmediate(volumeObject);
                Object.DestroyImmediate(source);
                CoreUtils.Destroy(profile); Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void TwoRealVolumeStacksProduceIndependentImmutableSnapshots()
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var previousSet = APRTProbeReferenceVolume.instance.currentBakingSet;
            var profile = ScriptableObject.CreateInstance<APRTOptions>();
            using var first = new VolumeStack();
            using var second = new VolumeStack();
            try
            {
                SetDistinctLegacyValues(set);
                APRTProbeReferenceVolume.instance.SetActiveBakingSet(set);
                first.Reload(new[] { typeof(APRTOptions) }); second.Reload(new[] { typeof(APRTOptions) });
                profile.giIntensity.Override(2);
                profile.normalBias.Override(.2f);
                profile.Override(first.GetComponent<APRTOptions>(), 1);
                profile.giIntensity.Override(7);
                profile.normalBias.Override(.7f);
                profile.Override(second.GetComponent<APRTOptions>(), 1);
                var firstSnapshot = APRTRelightSettings.Resolve(set, (APRTLightingModel)1, first.GetComponent<APRTOptions>());
                var secondSnapshot = APRTRelightSettings.Resolve(set, (APRTLightingModel)1, second.GetComponent<APRTOptions>());
                second.GetComponent<APRTOptions>().giIntensity.value = 9;
                first.GetComponent<APRTOptions>().normalBias.value = .9f;
                set.aprtFloorBoost = 14;
                Assert.That(firstSnapshot.giIntensity, Is.EqualTo(2));
                Assert.That(firstSnapshot.normalBias, Is.EqualTo(.2f));
                Assert.That(secondSnapshot.giIntensity, Is.EqualTo(7));
                Assert.That(secondSnapshot.normalBias, Is.EqualTo(.7f));
                Assert.That(firstSnapshot.floorBoost, Is.EqualTo(2.5f));
                Assert.That(secondSnapshot.floorBoost, Is.EqualTo(2.5f));
            }
            finally
            {
                APRTProbeReferenceVolume.instance.SetActiveBakingSet(previousSet);
                Object.DestroyImmediate(profile); Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void DefaultProfileAndSceneVolumeRespectTheirOverrideOrder()
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var previousSet = APRTProbeReferenceVolume.instance.currentBakingSet;
            var singleton = VolumeManager.instance;
            bool wasInitialized = singleton.isInitialized;
            var previousGlobal = singleton.globalDefaultProfile;
            var previousQuality = singleton.qualityDefaultProfile;
            var previousCustom = singleton.customDefaultProfiles != null ? new List<VolumeProfile>(singleton.customDefaultProfiles) : null;
            var manager = new VolumeManager { baseComponentTypeArray = new[] { typeof(APRTOptions) } };
            var defaultProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            var defaults = defaultProfile.Add<APRTOptions>();
            var sceneProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            var sceneOptions = sceneProfile.Add<APRTOptions>();
            var volumeObject = new GameObject("APRT default-profile test volume");
            Volume volume = null;
            try
            {
                SetDistinctLegacyValues(set);
                APRTProbeReferenceVolume.instance.SetActiveBakingSet(set);
                defaults.giIntensity.Override(8.25f); defaults.floorBoost.Override(6.5f);
                defaults.skyIntensity.Override(3);
                string defaultBefore = EditorJsonUtility.ToJson(defaults);
                // Production Override obtains its baseline default flags from the singleton.
                // Mirror this fixture's defaults there while keeping its tested stack independent.
                if (!wasInitialized) singleton.Initialize();
                singleton.SetGlobalDefaultProfile(defaultProfile);
                singleton.SetQualityDefaultProfile(null);
                singleton.SetCustomDefaultProfiles(null);
                manager.InitializeInternal(defaultProfile);
                manager.Update(manager.stack, null, ~0);
                var stackOptions = manager.stack.GetComponent<APRTOptions>();
                var defaultOptions = manager.GetVolumeComponentDefaultState(typeof(APRTOptions)) as APRTOptions;
                Assert.That(stackOptions.giIntensity.overrideState, Is.False,
                    "The framework resets stack override flags even for values inherited from Default Profiles.");
                var fromDefaults = APRTRelightSettings.Resolve(set, (APRTLightingModel)1, stackOptions, defaultOptions);
                Assert.That(fromDefaults.giIntensity, Is.EqualTo(8.25f));
                Assert.That(fromDefaults.floorBoost, Is.EqualTo(6.5f));
                Assert.That(fromDefaults.skyIntensity, Is.EqualTo(3));
                Assert.That(fromDefaults.normalBias, Is.EqualTo(.075f));

                sceneOptions.giIntensity.Override(9.25f);
                volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true; volume.weight = 1; volume.sharedProfile = sceneProfile;
                manager.Register(volume);
                manager.Update(manager.stack, null, ~0);
                var fromScene = APRTRelightSettings.Resolve(set, (APRTLightingModel)1, stackOptions, defaultOptions);
                Assert.That(fromScene.giIntensity, Is.EqualTo(9.25f), "Scene overrides take priority over Default Profiles.");
                Assert.That(fromScene.floorBoost, Is.EqualTo(6.5f), "Unchecked scene fields retain Default Profile overrides.");
                Assert.That(fromScene.normalBias, Is.EqualTo(.075f), "Fields unchecked in both profiles retain the Baking Set baseline.");
                Assert.That(EditorJsonUtility.ToJson(defaults), Is.EqualTo(defaultBefore));
            }
            finally
            {
                if (volume != null) manager.Unregister(volume);
                if (manager.isInitialized) manager.Deinitialize();
                APRTProbeReferenceVolume.instance.SetActiveBakingSet(previousSet);
                if (wasInitialized)
                {
                    singleton.SetGlobalDefaultProfile(previousGlobal);
                    singleton.SetQualityDefaultProfile(previousQuality);
                    singleton.SetCustomDefaultProfiles(previousCustom);
                }
                else if (singleton.isInitialized) singleton.Deinitialize();
                Object.DestroyImmediate(volumeObject); Object.DestroyImmediate(defaults); Object.DestroyImmediate(sceneOptions);
                Object.DestroyImmediate(defaultProfile); Object.DestroyImmediate(sceneProfile); Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void ExportedOptionsProfilePersistsSettingsWithoutTheLegacyCubemap()
        {
            string folderName = "APRTOptionsExportTest_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            VolumeProfile profile = null;
            APRTOptions options = null;
            try
            {
                AssetDatabase.CreateFolder("Assets", folderName);
                var cube = new Cubemap(1, TextureFormat.RGBAFloat, false);
                AssetDatabase.CreateAsset(cube, folder + "/Sky.asset");
                SetDistinctLegacyValues(set); set.aprtSkyTexture = cube;
                var expected = APRTRelightSettings.FromBakingSet(set, (APRTLightingModel)1);
                string sourceBefore = EditorJsonUtility.ToJson(set);
                string path = folder + "/APRTV Options.asset";
                profile = APRTProbeVolumeBakingSettingsEditor.CreateOptionsProfile(set, path);
                Assert.That(profile, Is.Not.Null);
                Assert.That(profile.TryGet(out options), Is.True);
                Assert.That(options.parameters, Has.All.Matches<VolumeParameter>(parameter => parameter.overrideState));
                AssetDatabase.SaveAssetIfDirty(profile); AssetDatabase.SaveAssetIfDirty(options);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var loaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
                Assert.That(loaded.TryGet<APRTOptions>(out var loadedOptions), Is.True);
                AssertPersistentOptions(loaded, loadedOptions, path);
                var restored = APRTRelightSettings.Resolve(null, (APRTLightingModel)1, loadedOptions, null);
                Assert.That(AssetDatabase.GetDependencies(path), Does.Not.Contain(folder + "/Sky.asset"),
                    "Exporting runtime options must not carry the obsolete Baking Set cubemap into the Profile.");
                AssertScalarSettings(restored, expected);
                Assert.That(EditorJsonUtility.ToJson(set), Is.EqualTo(sourceBefore));
            }
            finally
            {
                if (options != null) Undo.ClearUndo(options);
                if (profile != null) Undo.ClearUndo(profile);
                AssetDatabase.DeleteAsset(folder); Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void ExportedOptionsParameterEditsCanBeUndoneRedoneAndSaved()
        {
            string folderName = "APRTOptionsUndoTest_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            VolumeProfile profile = null;
            APRTOptions options = null;
            try
            {
                AssetDatabase.CreateFolder("Assets", folderName);
                SetDistinctLegacyValues(set);
                string path = folder + "/APRTV Options.asset";
                profile = APRTProbeVolumeBakingSettingsEditor.CreateOptionsProfile(set, path);
                Assert.That(profile.TryGet(out options), Is.True);
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Edit APRTV GI intensity");
                Undo.RecordObject(options, "Edit APRTV GI intensity");
                options.giIntensity.value = 9.5f;
                EditorUtility.SetDirty(options);
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(undoGroup);
                Undo.PerformUndo();
                Assert.That(options.giIntensity.value, Is.EqualTo(4.25f));
                AssertPersistentOptions(profile, options, path);
                Undo.PerformRedo();
                Assert.That(options.giIntensity.value, Is.EqualTo(9.5f));
                AssertPersistentOptions(profile, options, path);
                EditorUtility.SetDirty(options);
                AssetDatabase.SaveAssetIfDirty(options);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
                Assert.That(profile, Is.Not.Null);
                Assert.That(profile.TryGet(out options), Is.True);
                Assert.That(options.parameters, Has.All.Matches<VolumeParameter>(parameter => parameter.overrideState));
                Assert.That(options.giIntensity.value, Is.EqualTo(9.5f));
                Assert.That(options.skyIntensity.value, Is.EqualTo(.4f));
                AssertPersistentOptions(profile, options, path);
            }
            finally
            {
                if (options != null) Undo.ClearUndo(options);
                if (profile != null) Undo.ClearUndo(profile);
                AssetDatabase.DeleteAsset(folder); Object.DestroyImmediate(set);
            }
        }

        [Test]
        public void ExportCannotOverwriteAnExistingAsset()
        {
            string folderName = "APRTOptionsExistingAssetTest_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            try
            {
                AssetDatabase.CreateFolder("Assets", folderName);
                string path = folder + "/Existing.asset";
                var existing = ScriptableObject.CreateInstance<VolumeProfile>();
                existing.name = "Existing volume profile";
                AssetDatabase.CreateAsset(existing, path);
                string previousGUID = AssetDatabase.AssetPathToGUID(path);
                string previousContent = System.IO.File.ReadAllText(path);
                Assert.Throws<ArgumentException>(() => APRTProbeVolumeBakingSettingsEditor.CreateOptionsProfile(set, path));
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(previousGUID));
                Assert.That(System.IO.File.ReadAllText(path), Is.EqualTo(previousContent));
                Assert.That(AssetDatabase.LoadAssetAtPath<VolumeProfile>(path), Is.SameAs(existing));
            }
            finally { AssetDatabase.DeleteAsset(folder); Object.DestroyImmediate(set); }
        }

        [Test]
        public void CameraChangesResetTemporalHistoryAndBindTheirOwnSettings()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var options = ScriptableObject.CreateInstance<APRTOptions>();
            var cameraObjectA = new GameObject("APRT test camera A");
            var cameraObjectB = new GameObject("APRT test camera B");
            try
            {
                var cameraA = cameraObjectA.AddComponent<Camera>();
                var cameraB = cameraObjectB.AddComponent<Camera>();
                var data = APRTSkyIrradianceTests.SkyGridData(); data.lightingModel = (APRTLightingModel)1;
                set.CommitAPRTBakedData(data); APRTSkyIrradianceTests.PhysicalSkySettings(set);
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                options.giIntensity.Override(2); options.skyIntensity.Override(1);
                options.temporalBlend.Override(.25f); options.normalBias.Override(.125f);
                var first = APRTRelightSettings.Resolve(set, data.lightingModel, options);
                options.giIntensity.Override(7); options.skyIntensity.Override(3); options.normalBias.Override(.375f);
                var second = APRTRelightSettings.Resolve(set, data.lightingModel, options);
                Assert.That(volume.PrepareRelight(cameraA, first), Is.True);
                DispatchAndAssertSky(volume, first, cameraA.GetInstanceID(), 1);
                Assert.That(Shader.GetGlobalFloat("_APRTGIIntensity"), Is.EqualTo(2));
                Assert.That(Shader.GetGlobalFloat("_APRTNormalBias"), Is.EqualTo(.125f));
                DispatchAndAssertSky(volume, second, cameraB.GetInstanceID(), 3);
                Assert.That(Shader.GetGlobalFloat("_APRTGIIntensity"), Is.EqualTo(7));
                Assert.That(Shader.GetGlobalFloat("_APRTNormalBias"), Is.EqualTo(.375f));

                options.skyIntensity.Override(5);
                var changedSecond = APRTRelightSettings.Resolve(set, data.lightingModel, options);
                DispatchAndAssertSky(volume, changedSecond, cameraB.GetInstanceID(), 3.5f);
                DispatchAndAssertSky(volume, first, cameraA.GetInstanceID(), 1);

                // Deferred receiver bindings retain the camera snapshot rather than rereading the mutable Baking Set.
                set.aprtGIIntensity = 42; set.aprtNormalBias = 10;
                using var cmd = new CommandBuffer();
                volume.BindPRVRuntimeResources(cmd, true); Graphics.ExecuteCommandBuffer(cmd);
                Assert.That(Shader.GetGlobalFloat("_APRTGIIntensity"), Is.EqualTo(2));
                Assert.That(Shader.GetGlobalFloat("_APRTNormalBias"), Is.EqualTo(.125f));
            }
            finally
            {
                volume.Cleanup(); Object.DestroyImmediate(options); Object.DestroyImmediate(set);
                Object.DestroyImmediate(cameraObjectA); Object.DestroyImmediate(cameraObjectB);
            }
        }

        [Test]
        public void CameraGIOverrideControlsEligibilityAndLegacyDispatchStillWorks()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var options = ScriptableObject.CreateInstance<APRTOptions>();
            try
            {
                var data = APRTSkyIrradianceTests.SkyGridData(); data.lightingModel = (APRTLightingModel)1;
                set.CommitAPRTBakedData(data); APRTSkyIrradianceTests.PhysicalSkySettings(set);
                set.aprtGIIntensity = 0;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                options.giIntensity.Override(1);
                Assert.That(volume.PrepareRelight(null), Is.False);
                Assert.That(volume.PrepareRelight(null, APRTRelightSettings.Resolve(set, data.lightingModel, options)), Is.True);
                options.giIntensity.Override(0);
                Assert.That(volume.PrepareRelight(null, APRTRelightSettings.Resolve(set, data.lightingModel, options)), Is.False);
                Assert.That(Shader.GetGlobalFloat("_EnableAPRT"), Is.EqualTo(0));
                set.aprtGIIntensity = 1; set.aprtReferenceSkyIntensity = .8f;
                using var cmd = new CommandBuffer();
                volume.DispatchRelight(cmd, new Vector4[4], new Vector4[4], null, APRTSkyIrradianceTests.ConstantRadiance(Vector3.one));
                Graphics.ExecuteCommandBuffer(cmd);
                var actual = APRTSkyIrradianceTests.ReadSky(volume);
                for (int probe = 0; probe < 8; ++probe)
                    APRTSkyIrradianceTests.AssertRGB(actual[probe * 4], Vector3.one * .8f, .00001f, "Legacy entry point");
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(options); Object.DestroyImmediate(set); }
        }

        [Test]
        public void SceneSkyChangesBetweenCamerasKeepIndependentRadianceSnapshots()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var options = ScriptableObject.CreateInstance<APRTOptions>();
            var cubeA = new Cubemap(16, TextureFormat.RGBAFloat, false);
            var cubeB = new Cubemap(16, TextureFormat.RGBAFloat, false);
            var skyA = CreateSkyMaterial(cubeA); var skyB = CreateSkyMaterial(cubeB);
            var previousSkybox = RenderSettings.skybox;
            var cameraObjectA = new GameObject("APRT sky camera A");
            var cameraObjectB = new GameObject("APRT sky camera B");
            try
            {
                var cameraA = cameraObjectA.AddComponent<Camera>(); var cameraB = cameraObjectB.AddComponent<Camera>();
                var data = APRTSkyIrradianceTests.SkyGridData(); data.lightingModel = (APRTLightingModel)1;
                set.CommitAPRTBakedData(data); APRTSkyIrradianceTests.PhysicalSkySettings(set);
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                FillCube(cubeA, new Color(.25f, .5f, 1, 1)); FillCube(cubeB, new Color(2, 1, .5f, 1));
                var first = APRTRelightSettings.Resolve(set, (APRTLightingModel)1, options);
                var second = APRTRelightSettings.Resolve(set, (APRTLightingModel)1, options);
                Assert.That(volume.PrepareRelight(cameraA, first), Is.True);
                RenderSettings.skybox = skyA;
                volume.UpdateSkyEnvironment(null, first, cameraA);
                var radianceA = (Vector4[])volume.GetSkyRadianceCoefficients(cameraA).Clone();
                RenderSettings.skybox = skyB;
                volume.UpdateSkyEnvironment(null, second, cameraB);
                APRTSkyIrradianceTests.AssertRGB(volume.GetSkyRadianceCoefficients(cameraA)[0], new Vector3(.25f, .5f, 1), .002f, "Scene sky captured for camera A");
                APRTSkyIrradianceTests.AssertRGB(volume.GetSkyRadianceCoefficients(cameraB)[0], new Vector3(2, 1, .5f), .002f, "Scene sky captured for camera B");
                Assert.That(volume.GetSkyRadianceCoefficients(cameraA), Is.EqualTo(radianceA), "Preparing camera B must retain camera A's full radiance SH.");

                FillCube(cubeB, new Color(3, .25f, 2, 1));
                volume.UpdateSkyEnvironment(null, second, cameraB);
                APRTSkyIrradianceTests.AssertRGB(volume.GetSkyRadianceCoefficients(cameraB)[0], new Vector3(3, .25f, 2), .002f, "Changed scene sky for B");
                Assert.That(volume.GetSkyRadianceCoefficients(cameraA), Is.EqualTo(radianceA));
            }
            finally
            {
                RenderSettings.skybox = previousSkybox;
                volume.Cleanup(); Object.DestroyImmediate(options); Object.DestroyImmediate(set);
                Object.DestroyImmediate(skyA); Object.DestroyImmediate(skyB);
                Object.DestroyImmediate(cubeA); Object.DestroyImmediate(cubeB);
                Object.DestroyImmediate(cameraObjectA); Object.DestroyImmediate(cameraObjectB);
            }
        }

        [Test]
        public void DisabledBaseLightingCannotBeInheritedByItsOverlay()
        {
            RequireGPU();
            var volume = new APRTProbeReferenceVolume();
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var options = ScriptableObject.CreateInstance<APRTOptions>();
            const int baseCamera = 271;
            try
            {
                var data = APRTSkyIrradianceTests.SkyGridData(); data.lightingModel = (APRTLightingModel)1;
                set.CommitAPRTBakedData(data); APRTSkyIrradianceTests.PhysicalSkySettings(set);
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true }); volume.SetActiveBakingSet(set);
                options.giIntensity.Override(1);
                var enabled = APRTRelightSettings.Resolve(set, data.lightingModel, options);
                DispatchAndAssertSky(volume, enabled, baseCamera, 1);
                Assert.That(volume.CanSampleForCamera(baseCamera), Is.True);
                Assert.That(volume.CanSampleForCamera(baseCamera + 1), Is.False, "Another camera stack cannot inherit the previous base's data.");
                using (var cmd = new CommandBuffer())
                {
                    volume.BindPRVRuntimeResources(cmd, false); Graphics.ExecuteCommandBuffer(cmd);
                }
                Assert.That(volume.CanSampleForCamera(baseCamera), Is.True, "Temporarily disabling reflection/preview sampling must preserve base ownership.");
                options.giIntensity.Override(0);
                Assert.That(volume.PrepareRelight(null, APRTRelightSettings.Resolve(set, data.lightingModel, options)), Is.False);
                Assert.That(volume.CanSampleForCamera(baseCamera), Is.False, "A skipped zero-GI base must not let an overlay revive stale GI.");
                DispatchAndAssertSky(volume, enabled, baseCamera, 1);
                Assert.That(volume.CanSampleForCamera(baseCamera), Is.True);
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(options); Object.DestroyImmediate(set); }
        }

        static void AssertPersistentOptions(VolumeProfile profile, APRTOptions options, string path)
        {
            Assert.That(EditorUtility.IsPersistent(profile), Is.True);
            Assert.That(EditorUtility.IsPersistent(options), Is.True);
            Assert.That(AssetDatabase.GetAssetPath(profile), Is.EqualTo(path));
            Assert.That(AssetDatabase.GetAssetPath(options), Is.EqualTo(path));
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(profile, out string profileGUID, out long profileID), Is.True);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(options, out string optionsGUID, out long optionsID), Is.True);
            Assert.That(profileGUID, Is.EqualTo(optionsGUID));
            Assert.That(profileGUID, Is.EqualTo(AssetDatabase.AssetPathToGUID(path)));
            Assert.That(profileID, Is.Not.EqualTo(0));
            Assert.That(optionsID, Is.Not.EqualTo(0));
            Assert.That(optionsID, Is.Not.EqualTo(profileID));
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(path), Does.Contain(profile));
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(path), Does.Contain(options));
        }

        static void AssertScalarSettings(APRTRelightSettings actual, APRTRelightSettings expected)
        {
            Assert.That(actual.giIntensity, Is.EqualTo(expected.giIntensity), "GI intensity");
            Assert.That(actual.indirectIntensity, Is.EqualTo(expected.indirectIntensity), "Indirect intensity");
            Assert.That(actual.indirectContrast, Is.EqualTo(expected.indirectContrast), "Indirect contrast");
            Assert.That(actual.skyIntensity, Is.EqualTo(expected.skyIntensity), "Sky intensity");
            Assert.That(actual.saturationBoost, Is.EqualTo(expected.saturationBoost), "Saturation boost");
            Assert.That(actual.shadowGIVisibility, Is.EqualTo(expected.shadowGIVisibility), "Shadow GI visibility");
            Assert.That(actual.skyVisibilityPower, Is.EqualTo(expected.skyVisibilityPower), "Sky visibility power");
            Assert.That(actual.floorBoost, Is.EqualTo(expected.floorBoost), "Floor boost");
            Assert.That(actual.floorPower, Is.EqualTo(expected.floorPower), "Floor power");
            Assert.That(actual.temporalBlend, Is.EqualTo(expected.temporalBlend), "Temporal blend");
            Assert.That(actual.probeInvalidityThreshold, Is.EqualTo(expected.probeInvalidityThreshold), "Probe invalidity threshold");
            Assert.That(actual.visibilityBias, Is.EqualTo(expected.visibilityBias), "Visibility bias");
            Assert.That(actual.normalBias, Is.EqualTo(expected.normalBias), "Normal bias");
            Assert.That(actual.bounceIntensity, Is.EqualTo(expected.bounceIntensity), "Legacy bounce intensity");
            Assert.That(actual.leakReduction, Is.EqualTo(expected.leakReduction), "Leak reduction");
            Assert.That(actual.addAmbientLighting, Is.EqualTo(expected.addAmbientLighting), "Legacy ambient lighting");
        }

        static void DispatchAndAssertSky(APRTProbeReferenceVolume volume, APRTRelightSettings settings, int cameraId, float gain)
        {
            using var cmd = new CommandBuffer();
            volume.DispatchRelight(cmd, new Vector4[4], new Vector4[4], null,
                APRTSkyIrradianceTests.ConstantRadiance(Vector3.one), settings, cameraId);
            Graphics.ExecuteCommandBuffer(cmd);
            var actual = APRTSkyIrradianceTests.ReadSky(volume);
            for (int probe = 0; probe < 8; ++probe)
                APRTSkyIrradianceTests.AssertRGB(actual[probe * 4], Vector3.one * gain, .00001f, "Camera-specific sky");
        }

        static void SetDistinctLegacyValues(ProbeVolumeBakingSet set)
        {
            set.aprtGIIntensity = 4.25f; set.aprtIndirectIntensity = 1.75f; set.aprtIndirectContrast = 3;
            set.aprtSkyIntensity = 1.3f; set.aprtReferenceSkyIntensity = .4f;
            set.aprtSaturationBoost = .8f; set.aprtShadowGIVisibility = .3f; set.aprtSkyVisibilityPower = 2;
            set.aprtFloorBoost = 2.5f; set.aprtFloorPower = 3; set.aprtTemporalBlend = .5f;
            set.aprtLeakReduction = true; set.aprtProbeInvalidityThreshold = .35f;
            set.aprtVisibilityBias = .025f; set.aprtNormalBias = .075f;
        }

        static void FillCube(Cubemap cube, Color color)
        {
            var pixels = new Color[cube.width * cube.width];
            for (int i = 0; i < pixels.Length; ++i) pixels[i] = color;
            for (int face = 0; face < 6; ++face) cube.SetPixels(pixels, (CubemapFace)face);
            cube.Apply(false, false);
        }

        static Material CreateSkyMaterial(Cubemap cube)
        {
            var material = new Material(Shader.Find("Skybox/Cubemap"));
            material.SetTexture("_Tex", cube); material.SetColor("_Tint", Color.white);
            // Skybox-Cubed.shader multiplies by unity_ColorSpaceDouble, then
            // its [Gamma] _Exposure. Supply gamma-encoded exposure so Unity's
            // linear conversion cancels that multiplier without fitting output.
            material.SetFloat("_Exposure", QualitySettings.activeColorSpace == ColorSpace.Linear
                ? Mathf.LinearToGammaSpace(1f / 4.59479380f) : .5f);
            return material;
        }

        static void RequireGPU()
        {
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("Requires a real graphics device.");
        }
    }
}
