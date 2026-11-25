using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering.Tests
{
    public class APRTEditorInitializationTests
    {
        const BindingFlags k_InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo s_Context = typeof(ProbeReferenceVolumeProvider).GetField("s_context", BindingFlags.Static | BindingFlags.NonPublic);

        [Test]
        public void APRTInitializationRebuildsPersistedSceneRegistryWithoutInitializingAPV()
        {
            RequireValidationProject();
            using var fixture = new RegistryFixture();
            string sceneGUID = Guid.NewGuid().ToString("N");
            ProbeVolumeBakingSet set = fixture.CreatePersistentSet(sceneGUID);
            ProbeVolumeBakingSet.sceneToBakingSet.Clear();
            Assert.That(ProbeVolumeBakingSet.GetBakingSetForScene(sceneGUID), Is.Null,
                "The fixture must reproduce the empty static registry after a domain reload.");
            bool apvInitialized = APVProbeReferenceVolume.instance.isInitialized;

            APRTProbeReferenceVolume.instance.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });

            Assert.That(ProbeVolumeBakingSet.GetBakingSetForScene(sceneGUID), Is.SameAs(set),
                "An APRT-only initialization must recover the saved scene-to-set registration.");
            Assert.That(APVProbeReferenceVolume.instance.isInitialized, Is.EqualTo(apvInitialized),
                "Recovering APRT's editor registry must not initialize or clean up APV.");
            Assert.That(APRTProbeReferenceVolume.instance.DataHasBeenLoaded(), Is.False,
                "Recovering editor registrations must not allocate transport for an empty set.");
        }

        [Test]
        public void ExistingTemporaryBakingContextResolvesRegisteredPersistentSetAfterAPRTInitialization()
        {
            RequireValidationProject();
            RequireSavedBaseline();
            using var fixture = new RegistryFixture();
            Scene scene = fixture.CreateScene();
            string sceneGUID = scene.GetGUID();
            ProbeVolumeBakingSet persistent = fixture.CreatePersistentSet(sceneGUID);
            ProbeVolumeBakingSet temporary = fixture.CreateTemporarySet(sceneGUID);
            var workflow = new ProbeVolumeBakingWorkflow();
            SetField(workflow, "m_ActiveSet", new ProbeVolumeBakingSetWeakReference(temporary));
            SetField(workflow, "isTemporaryBakingSet", true);
            SetField(workflow, "inspectorScene", scene);
            ProbeVolumeBakingSet.sceneToBakingSet.Clear();

            APRTProbeReferenceVolume.instance.Initialize(new ProbeVolumeSystemParameters { enableAPRT = true });
            var find = typeof(ProbeVolumeBakingWorkflow).GetMethod("FindActiveSet", k_InstancePrivate);
            Assert.That(find, Is.Not.Null);
            Assert.That((bool)find.Invoke(workflow, null), Is.True);

            var active = (ProbeVolumeBakingSetWeakReference)GetField(workflow, "m_ActiveSet");
            Assert.That(active.Get(), Is.SameAs(persistent),
                "A context that already owns a temporary set must adopt the recovered persistent registration.");
            Assert.That((bool)GetField(workflow, "isTemporaryBakingSet"), Is.False);
            Assert.That(temporary == null, Is.True, "The context must dispose of the superseded temporary set.");
            Assert.That(EditorUtility.IsPersistent(persistent), Is.True);
            Assert.That(ProbeVolumeBakingSet.GetBakingSetForScene(sceneGUID), Is.SameAs(persistent));

            APRTProbeReferenceVolume.instance.SetActiveScene(scene);
            Assert.That(APRTProbeReferenceVolume.instance.currentBakingSet, Is.SameAs(persistent),
                "The runtime and the baking context must resolve the same persisted Baking Set.");
            Assert.That((bool)find.Invoke(workflow, null), Is.True);
            Assert.That(active.Get(), Is.SameAs(persistent), "Repeated discovery must preserve the persistent set.");
        }

        [Test]
        public void ReleasingBakingContextDestroysOnlyTemporarySets()
        {
            RequireValidationProject();
            using var fixture = new RegistryFixture();
            string sceneGUID = Guid.NewGuid().ToString("N");
            var temporary = fixture.CreateTemporarySet(sceneGUID);
            var workflow = new ProbeVolumeBakingWorkflow();
            SetField(workflow, "m_ActiveSet", new ProbeVolumeBakingSetWeakReference(temporary));
            workflow.isTemporaryBakingSet = true;

            workflow.ReleaseTemporaryBakingSet();

            Assert.That(temporary == null, Is.True);
            Assert.That(workflow.activeSet, Is.Null);

            var persistent = fixture.CreatePersistentSet(sceneGUID);
            workflow.activeSet = persistent;

            workflow.ReleaseTemporaryBakingSet();

            Assert.That(persistent != null, Is.True);
            Assert.That(EditorUtility.IsPersistent(persistent), Is.True);
            Assert.That(workflow.activeSet, Is.Null);
        }

        static void RequireValidationProject()
        {
            string path = Application.dataPath.Replace('\\', '/');
            string projectName = new DirectoryInfo(Path.GetDirectoryName(path)).Name;
            if (!Application.isBatchMode || !projectName.StartsWith("APRTValidationProject", StringComparison.Ordinal))
                Assert.Ignore("These singleton and registry tests run only in the isolated APRT validation project.");
        }

        static void RequireSavedBaseline()
        {
            for (int index = 0; index < SceneManager.sceneCount; ++index)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene) && string.IsNullOrEmpty(scene.path))
                    Assert.Ignore("The additive registry fixture requires a saved baseline scene.");
            }
        }

        static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, k_InstancePrivate);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }

        static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, k_InstancePrivate);
            Assert.That(field, Is.Not.Null);
            return field.GetValue(target);
        }

        sealed class RegistryFixture : IDisposable
        {
            readonly Dictionary<string, ProbeVolumeBakingSetWeakReference> m_PreviousRegistry = ProbeVolumeBakingSet.sceneToBakingSet;
            readonly ProbeReferenceVolumeRuntimeContext m_PreviousContext = ProbeReferenceVolumeProvider.context;
            readonly APRTProbeReferenceVolume m_Volume = APRTProbeReferenceVolume.instance;
            readonly bool m_PreviousInitialized = APRTProbeReferenceVolume.instance.isInitialized;
            readonly bool m_PreviousEnabled = APRTProbeReferenceVolume.instance.enabledBySRP;
            readonly ProbeVolumeBakingSet m_PreviousSet = APRTProbeReferenceVolume.instance.currentBakingSet;
            readonly string m_PreviousScenario = APRTProbeReferenceVolume.instance.lightingScenario;
            readonly List<ProbeVolumePerSceneData> m_PreviousSceneData = new(APRTProbeReferenceVolume.instance.perSceneDataList);
            readonly Scene m_PreviousScene = SceneManager.GetActiveScene();
            readonly string m_Folder;
            readonly List<ProbeVolumeBakingSet> m_TemporarySets = new();
            Scene m_Scene;

            internal RegistryFixture()
            {
                m_Volume.Cleanup();
                ProbeVolumeBakingSet.sceneToBakingSet = new Dictionary<string, ProbeVolumeBakingSetWeakReference>();
                Assert.That(ProbeReferenceVolumeProvider.TryGetBackend("APRT", out var backend), Is.True);
                s_Context.SetValue(null, new ProbeReferenceVolumeRuntimeContext(backend,
                    new ProbeVolumeSystemParameters { enableAPRT = true }));
                string name = "APRTRegistryTest_" + Guid.NewGuid().ToString("N");
                m_Folder = "Assets/" + name;
                AssetDatabase.CreateFolder("Assets", name);
            }

            internal ProbeVolumeBakingSet CreatePersistentSet(string sceneGUID)
            {
                var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                set.SetDefaults();
                AssetDatabase.CreateAsset(set, m_Folder + "/Registered.asset");
                set.AddScene(sceneGUID);
                AssetDatabase.SaveAssetIfDirty(set);
                return set;
            }

            internal ProbeVolumeBakingSet CreateTemporarySet(string sceneGUID)
            {
                var set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                m_TemporarySets.Add(set);
                set.SetDefaults();
                set.AddScene(sceneGUID);
                return set;
            }

            internal Scene CreateScene()
            {
                m_Scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.That(EditorSceneManager.SaveScene(m_Scene, m_Folder + "/Fixture.unity"), Is.True);
                Assert.That(m_Scene.IsValid() && m_Scene.isLoaded, Is.True);
                return m_Scene;
            }

            public void Dispose()
            {
                m_Volume.Cleanup();
                if (m_Scene.IsValid() && m_Scene.isLoaded) EditorSceneManager.CloseScene(m_Scene, true);
                if (m_PreviousScene.IsValid() && m_PreviousScene.isLoaded) SceneManager.SetActiveScene(m_PreviousScene);
                foreach (var set in m_TemporarySets) if (set != null) Object.DestroyImmediate(set);
                AssetDatabase.DeleteAsset(m_Folder);
                s_Context.SetValue(null, m_PreviousContext);
                if (m_PreviousInitialized)
                    m_Volume.Initialize(m_PreviousContext?.backend.id == "APRT" ? m_PreviousContext.parameters :
                        new ProbeVolumeSystemParameters { enableAPRT = m_PreviousEnabled });
                m_Volume.SetActiveBakingSet(m_PreviousSet);
                m_Volume.lightingScenario = m_PreviousScenario;
                m_Volume.SetEnableStateFromSRP(m_PreviousEnabled);
                foreach (var data in m_PreviousSceneData) if (data != null) m_Volume.RegisterPerSceneData(data);
                ProbeVolumeBakingSet.sceneToBakingSet = m_PreviousRegistry;
            }
        }
    }
}
