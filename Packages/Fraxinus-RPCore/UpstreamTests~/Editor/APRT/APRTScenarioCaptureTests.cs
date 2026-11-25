using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityEngine.Rendering.Tests
{
    public class APRTScenarioCaptureTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void DeletingTheActiveCaptureSelectsAnotherCaptureOrReturnsToLive(bool keepSecondCapture)
        {
            var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            var volume = new APRTProbeReferenceVolume();
            try
            {
                set.SetDefaults();
                set.TryAddScenario("Day");
                set.TryAddScenario("Night");
                var data = APRTSkyIrradianceTests.SkyGridData();
                data.lightingModel = APRTLightingModel.APRTV;
                data.SetBakedLightingScenario(APRTScenarioBakingTests.BakedScenario(data, "Day"));
                if (keepSecondCapture) data.SetBakedLightingScenario(APRTScenarioBakingTests.BakedScenario(data, "Night", 1));
                set.CommitAPRTBakedData(data);
                set.aprtUseLightingScenarios = true;
                volume.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true, supportScenarios = true, supportScenarioBlending = true, blendingMemoryBudget = ProbeVolumeBlendingTextureMemoryBudget.MemoryBudgetLow });
                set.SetActiveScenario("Day", false, volume);
                if (keepSecondCapture) set.BlendLightingScenario("Night", .5f, volume);

                APRTProbeVolumeBakingSettingsEditor.RemoveLightingScenario(set, "Day");

                Assert.That(set.aprtUseLightingScenarios, Is.EqualTo(keepSecondCapture));
                Assert.That(set.aprtOtherScenario, Is.Null);
                Assert.That(set.aprtScenarioBlendingFactor, Is.Zero);
                Assert.That(data.FindLightingScenario("Day"), Is.Null);
                if (keepSecondCapture) Assert.That(set.aprtLightingScenario, Is.EqualTo("Night"));
            }
            finally { volume.Cleanup(); Object.DestroyImmediate(set); }
        }

        [Test]
        public void CaptureIncludesLoadedSetScenesAndExcludesOtherScenesWithBlackMissingSky()
        {
            using var fixture = new CaptureFixture();
            var first = fixture.AddLight(fixture.first, new Color(.2f, .4f, .8f), 2);
            var second = fixture.AddLight(fixture.second, new Color(.7f, .1f, .3f), 3);
            fixture.AddLight(fixture.outside, Color.white, 50);
            var disabled = fixture.AddLight(fixture.first, Color.yellow, 100);
            disabled.enabled = false;

            Assert.That(APRTLightingScenarioCapture.TryCapture(fixture.set, "Day", out string error), Is.True, error);
            var scenario = fixture.set.aprtBakedData.FindLightingScenario("Day");
            Assert.That(scenario.IsValid(), Is.True);
            Assert.That(scenario.colors.Count(color => color.w == 1), Is.EqualTo(2));
            foreach (var light in new[] { first, second })
            {
                Vector4 expected = light.color.linear * light.intensity;
                expected.w = 1;
                Assert.That(scenario.colors, Does.Contain(expected));
            }
            Assert.That(scenario.skyRadiance, Is.All.EqualTo(Vector4.zero));
        }

        [Test]
        public void BakeInputCaptureNeedsNoPreviousTransportAndDoesNotPublishBeforeBakeCompletes()
        {
            using var fixture = new CaptureFixture();
            fixture.AddLight(fixture.first, Color.red, 2);
            fixture.set.aprtBakedData.ClearAssets();
            Assert.That(APRTLightingScenarioCapture.TryCaptureInputs(fixture.set, "Day", out var inputs, out string error), Is.True, error);
            Assert.That(inputs, Is.Not.Null);
            Assert.That(inputs.IsValid(), Is.True);
            Assert.That(inputs.colors.Count(color => color.w == 1), Is.EqualTo(1));
            Assert.That(fixture.set.aprtBakedData.FindLightingScenario("Day"), Is.Null);
            Assert.That(fixture.set.aprtBakedData.HasValidSharedData(), Is.False);
        }

        [Test]
        public void DisabledConfiguredSunStillControlsTheCapturedAtmosphereWithoutAddingDirectLight()
        {
            using var fixture = new CaptureFixture();
            var configured = fixture.AddLight(fixture.first, Color.red, 1);
            configured.enabled = false;
            configured.gameObject.SetActive(false);
            fixture.AddLight(fixture.second, Color.blue, 10);
            RenderSettings.sun = configured;
            var colors = new Vector4[4];
            var directions = new Vector4[4];
            APRTLightingScenarioCapture.CollectDirectionalLights(new HashSet<string>(fixture.set.sceneGUIDs),
                colors, directions, out var skySun, out int count);
            Assert.That(skySun, Is.SameAs(configured));
            Assert.That(count, Is.EqualTo(1));
            Assert.That(colors.Count(color => color.w == 1), Is.EqualTo(1));
        }

        [Test]
        public void TooManyDirectionalLightsRetainThePreviousCapture()
        {
            using var fixture = new CaptureFixture();
            Assert.That(APRTLightingScenarioCapture.TryCapture(fixture.set, "Day", out string error), Is.True, error);
            var previous = fixture.set.aprtBakedData.FindLightingScenario("Day");
            for (int i = 0; i < 5; ++i)
                fixture.AddLight(i % 2 == 0 ? fixture.first : fixture.second, Color.white, 1);
            Assert.That(APRTLightingScenarioCapture.TryCapture(fixture.set, "Day", out error), Is.False);
            Assert.That(error, Does.Contain("up to four"));
            Assert.That(fixture.set.aprtBakedData.FindLightingScenario("Day"), Is.SameAs(previous));
        }

        sealed class CaptureFixture : IDisposable
        {
            internal readonly ProbeVolumeBakingSet set;
            internal readonly Scene first, second, outside;
            readonly Scene previousScene = SceneManager.GetActiveScene();
            readonly string folder;

            internal CaptureFixture()
            {
                string name = "APRTScenarioCaptureTest_" + Guid.NewGuid().ToString("N");
                folder = "Assets/" + name;
                AssetDatabase.CreateFolder("Assets", name);
                first = CreateScene("First");
                second = CreateScene("Second");
                outside = CreateScene("Outside");
                SceneManager.SetActiveScene(first);
                RenderSettings.skybox = null;
                RenderSettings.sun = null;
                set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                set.SetDefaults();
                set.TryAddScenario("Day");
                set.AddScene(first.GetGUID());
                set.AddScene(second.GetGUID());
                var data = APRTSkyIrradianceTests.SkyGridData();
                data.lightingModel = APRTLightingModel.APRTV;
                set.CommitAPRTBakedData(data);
                AssetDatabase.CreateAsset(set, folder + "/Capture.asset");
            }

            Scene CreateScene(string name)
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.That(EditorSceneManager.SaveScene(scene, folder + "/" + name + ".unity"), Is.True);
                return scene;
            }

            internal Light AddLight(Scene scene, Color color, float intensity)
            {
                var go = new GameObject("Scenario Capture Test Light");
                SceneManager.MoveGameObjectToScene(go, scene);
                var light = go.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = color;
                light.intensity = intensity;
                return light;
            }

            public void Dispose()
            {
                if (set != null)
                {
                    set.RemoveScene(first.GetGUID());
                    set.RemoveScene(second.GetGUID());
                }
                foreach (var scene in new[] { first, second, outside })
                    if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                if (previousScene.IsValid() && previousScene.isLoaded) SceneManager.SetActiveScene(previousScene);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
