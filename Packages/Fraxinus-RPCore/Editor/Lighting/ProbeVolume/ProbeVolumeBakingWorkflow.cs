using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering
{
    internal sealed class ProbeVolumeBakingWorkflow
    {
        const string k_APRTBackendId = "APRT";
        internal static bool aprtSelected => ProbeReferenceVolumeProvider.activeBackend?.id == k_APRTBackendId;
        internal static ProbeVolumeBakingWorkflow current;
        internal static ProbeVolumeBakingSet displayedBakingSet => current?.activeSet;
        internal static Scene displayedScene => current?.uiScene ?? SceneManager.GetActiveScene();

        internal bool singleSceneMode = true;
        internal Scene inspectorScene;
        internal Scene uiScene => inspectorScene.IsValid() && inspectorScene.isLoaded ? inspectorScene : SceneManager.GetActiveScene();
        internal bool isTemporaryBakingSet = false;

        ProbeVolumeBakingSetWeakReference m_ActiveSet = new();
        internal ProbeVolumeBakingSet activeSet
        {
            get => m_ActiveSet.Get();
            set
            {
                if (ReferenceEquals(m_ActiveSet.Get(), value)) return;
                inspectorScene = default;
                if (isTemporaryBakingSet) Object.DestroyImmediate(m_ActiveSet.Get());
                m_ActiveSet.Set(value);
                isTemporaryBakingSet = false;
                if (m_ActiveSet.Get() == null) return;
                singleSceneMode = m_ActiveSet.Get().singleSceneMode;
            }
        }

        internal static void NotifyBakingSetSaved(ProbeVolumeBakingSet bakingSet)
        {
            if (current != null && current.activeSet == bakingSet)
                current.isTemporaryBakingSet = false;
        }

        internal void SetInspectorContext(ProbeVolumeBakingSet bakingSet, Scene scene)
        {
            activeSet = bakingSet;
            inspectorScene = scene.IsValid() && scene.isLoaded && bakingSet != null && bakingSet.sceneGUIDs.Contains(scene.GetGUID())
                ? scene : default;
        }

        internal bool FindActiveSet()
        {
            if (isTemporaryBakingSet)
            {
                var registeredSet = ProbeVolumeBakingSet.GetBakingSetForScene(uiScene);
                if (registeredSet != null && EditorUtility.IsPersistent(registeredSet))
                    activeSet = registeredSet;
            }

            if (m_ActiveSet.Get() == null)
            {
                activeSet = ProbeVolumeBakingSet.GetBakingSetForScene(SceneManager.GetActiveScene());
                for (int i = 0; activeSet == null && i < SceneManager.sceneCount; i++)
                    activeSet = ProbeVolumeBakingSet.GetBakingSetForScene(SceneManager.GetSceneAt(i));
            }

            return m_ActiveSet.Get() != null;
        }

        internal bool ClearBakedData()
        {
            if (activeSet != null && ProbeVolumeBakingProvider.ClearBakedData(activeSet))
                return true;

            Debug.LogWarning($"Could not clear {ProbeVolumeBakingProvider.backend?.displayName ?? "probe volume"} data for the selected Baking Set.");
            return false;
        }

        internal void UseTemporaryBakingSet(string sceneGUID, ProbeVolumeBakingSet set = null)
        {
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                set.SetDefaults();

                ProbeReferenceVolumeProvider.instance.AddPendingSceneRemoval(sceneGUID);
            }

            EditorUtility.SetDirty(set);
            string scenePath = AssetDatabase.GUIDToAssetPath(sceneGUID);
            set.name = (string.IsNullOrEmpty(scenePath) ? "Untitled" : Path.GetFileNameWithoutExtension(scenePath)) + " Baking Set";
            set.singleSceneMode = true;
            set.AddScene(sceneGUID);
            activeSet = set;
            isTemporaryBakingSet = true;
        }

        internal void SaveTempBakingSetIfNeeded()
        {
            var scene = uiScene;
            if (!isTemporaryBakingSet || !scene.IsValid()) return;
            string path = string.IsNullOrEmpty(scene.path) ?
                ProbeVolumeBakingSet.GetDirectory("Assets/", "Untitled") :
                ProbeVolumeBakingSet.GetDirectory(scene.path, scene.name);
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            path = Path.Combine(path, activeSet.name + ".asset");
            path = AssetDatabase.GenerateUniqueAssetPath(path);

            AssetDatabase.CreateAsset(activeSet, path.Replace('\\', '/'));
            AssetDatabase.SaveAssetIfDirty(activeSet);
            // The scene registration already points to this same object;
            // rescanning would discard other unsaved Baking Set registrations.
            isTemporaryBakingSet = false;
        }

        internal void ConvertTempBakingSet()
        {
            singleSceneMode = activeSet.singleSceneMode = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                TryAddSceneToSet(SceneManager.GetSceneAt(i));
            SaveTempBakingSetIfNeeded();
        }

        void CreateProbeVolume()
        {
            var probeVolume = CoreEditorUtils.CreateGameObject(null, "Probe Volume");
            var pv = probeVolume.AddComponent<ProbeVolume>();
            pv.mode = ProbeVolume.Mode.Scene;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        internal bool TryMoveSceneToActiveSet(string sceneName, string sceneGUID, ProbeVolumeBakingSet oldSet, int index = -1)
        {
            if (!EditorUtility.DisplayDialog("Move Scene to baking set", $"The scene '{sceneName}' was already added in the baking set '{oldSet.name}'. Do you want to move it to the current set?", "Yes", "Cancel"))
                return false;

            Undo.RegisterCompleteObjectUndo(new Object[] { activeSet, oldSet }, "Moved scene to baking set");
            activeSet.MoveSceneToBakingSet(sceneGUID, index);

            return true;
        }

        internal void TryAddSceneToSet(Scene scene) => TryAddSceneToSet(scene.name, scene.GetGUID());

        internal void TryAddSceneToSet(string sceneName, string sceneGUID)
        {
            // Don't allow the same scene in two different sets
            var sceneSet = ProbeVolumeBakingSet.GetBakingSetForScene(sceneGUID);
            if (sceneSet == activeSet)
                return;
            if (sceneSet != null)
            {
                if (!TryMoveSceneToActiveSet(sceneName, sceneGUID, sceneSet))
                    return;
            }
            else
            {
                Undo.RegisterCompleteObjectUndo(new Object[] { activeSet }, "Added scene in baking set");
                activeSet.AddScene(sceneGUID);
            }
        }

        internal void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            if (scene == SceneManager.GetActiveScene())
            {
                inspectorScene = default;
                var prv = ProbeReferenceVolumeProvider.instance;
                // Find the set in which the new active scene belongs
                var set = ProbeVolumeBakingSet.GetBakingSetForScene(scene);

                activeSet = set;

                if (set == null)
                {
                    singleSceneMode = true;
                }
                else
                {
                    // Validate this set's selection for the selected backend.
                    if (set.m_LightingScenarios.Count > 0 && !set.m_LightingScenarios.Contains(set.GetLightingScenario(prv.backendId)))
                        set.SetActiveScenario(set.m_LightingScenarios[0], false, prv);
                }
            }
        }

        bool NoSceneHasProbeVolume() => ProbeVolume.instances.Count == 0;
        bool ActiveSceneHasProbeVolume() => ProbeVolume.instances.Any(d => d.gameObject.scene == SceneManager.GetActiveScene());
        internal ProbeVolume GetFirstProbeVolumeInNonActiveScene() => ProbeVolume.instances.FirstOrDefault(d => d.gameObject.scene != SceneManager.GetActiveScene());

        internal static ProbeVolumeBakingSet GetSceneBakingSetForUI(Scene scene)
        {
            // If the set is available, return it
            var bakingSet = ProbeVolumeBakingSet.GetBakingSetForScene(scene);
            if (bakingSet != null)
                return bakingSet;

            // Otherwise, a baking set might be created in the UI but not registered yet in the system
            if (current == null || current.activeSet == null)
                return null;
            if (!current.singleSceneMode || !current.activeSet.singleSceneMode)
                return null;
            if (!current.activeSet.sceneGUIDs.Contains(scene.GetGUID()))
                return null;

            return current.activeSet;
        }
        internal static bool BakeAPRT(ProbeVolumeBakingSet bakingSet, Scene scene)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || ProbeVolumeBakingProvider.isRunning ||
                !ProbeVolumeBakingProvider.IsBackendAvailable(k_APRTBackendId))
                return false;
            if (!PrepareAPRTBakingSet(ref bakingSet, scene))
                return false;
            if (current != null)
                current.SetInspectorContext(bakingSet, scene);
            ProbeVolumeBakingProvider.partialBakeSceneList = null;
            return ProbeVolumeBakingProvider.BakeStandaloneAsync(k_APRTBackendId, bakingSet);
        }

        static bool PrepareAPRTBakingSet(ref ProbeVolumeBakingSet bakingSet, Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
            {
                Debug.LogWarning("Save the scene before baking APRT.");
                return false;
            }
            bakingSet ??= GetSceneBakingSetForUI(scene);
            if (bakingSet == null)
            {
                bakingSet = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
                bakingSet.SetDefaults();
                bakingSet.name = scene.name + " Baking Set";
                bakingSet.singleSceneMode = true;
                bakingSet.AddScene(scene.GetGUID());
            }
            if (bakingSet.sceneGUIDs.Count == 0)
                bakingSet.AddScene(scene.GetGUID());
            foreach (var guid in bakingSet.sceneGUIDs)
            {
                var source = SceneManager.GetSceneByPath(AssetDatabase.GUIDToAssetPath(guid));
                if (!source.IsValid() || !source.isLoaded || string.IsNullOrEmpty(source.path))
                {
                    Debug.LogWarning("Load and save every scene in the APRT Baking Set before baking.");
                    return false;
                }
            }
            if (!EditorUtility.IsPersistent(bakingSet))
            {
                string directory = ProbeVolumeBakingSet.GetDirectory(scene.path, scene.name);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                string path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(directory, bakingSet.name + ".asset").Replace('\\', '/'));
                AssetDatabase.CreateAsset(bakingSet, path);
                AssetDatabase.SaveAssetIfDirty(bakingSet);
                NotifyBakingSetSaved(bakingSet);
            }
            return true;
        }

        static MethodInfo k_GetLightingSettingsOrDefaultsFallback = typeof(Lightmapping).GetMethod("GetLightingSettingsOrDefaultsFallback", BindingFlags.Static | BindingFlags.NonPublic);
        internal static LightingSettings GetLightingSettings()
        {
            return k_GetLightingSettingsOrDefaultsFallback.Invoke(null, null) as LightingSettings;
        }

        internal static bool PrepareBake()
        {
            if (current != null)
                return current.PrepareActiveBake();

            var workflow = new ProbeVolumeBakingWorkflow();
            try
            {
                return workflow.PrepareActiveBake();
            }
            finally
            {
                workflow.ReleaseTemporaryBakingSet();
            }
        }

        bool PrepareActiveBake()
        {
            var prv = ProbeReferenceVolumeProvider.instance;
            if (!prv.isInitialized || !prv.enabledBySRP)
                return false;

            if (aprtSelected)
            {
                var set = ProbeVolumeBakingProvider.requestedBakingSet ?? activeSet ??
                    ProbeVolumeBakingSet.GetBakingSetForScene(uiScene);
                if (!PrepareAPRTBakingSet(ref set, uiScene))
                    return false;
                activeSet = set;
                var data = set.GetBakedData(k_APRTBackendId) as APRTBakedData;
                if (!set.freezePlacement && data?.scenarioCount > 1 && !EditorUtility.DisplayDialog(
                    "Recalculate Probe Positions?",
                    "Recalculating probe positions may invalidate other baked Lighting Scenarios. Use Don't Recalculate to keep a compatible layout.",
                    "Recalculate", "Cancel"))
                    return false;
                ProbeVolumeBakingProvider.partialBakeSceneList = null;
                return true;
            }

            // In case UI was never opened we have to setup some stuff
            FindActiveSet();

            if (activeSet == null)
            {
                // Probe volumes were never setup by the user, try to do it for him by creating a default baking set
                var activeScene = SceneManager.GetActiveScene();
                var activeSceneGUID = activeScene.GetGUID();
                UseTemporaryBakingSet(activeSceneGUID);
            }

            bool createPV = singleSceneMode ? !ActiveSceneHasProbeVolume() : NoSceneHasProbeVolume();
            if (createPV)
            {
                if(!activeSet.DialogNoProbeVolumeInSetShown())
                {
                    if(EditorUtility.DisplayDialog("No Probe Volume in Scene", "Probe Volumes are enabled for this Project, but none exist in the Scene.\n\n" +
                        "Do you wish to add an Probe Volume to the Active Scene?", "Yes", "No"))
                        CreateProbeVolume();
                    activeSet.SetDialogNoProbeVolumeInSetShown(true);
                }
            }
            if (singleSceneMode)
            {
                if (GetFirstProbeVolumeInNonActiveScene() != null)
                {
                    int res = EditorUtility.DisplayDialogComplex("Create Baking Set?", "You are using the Single Scene Baking Mode and have more than one Scene loaded. It is not possible to generate lighting.\n\n" +
                        "Do you want to create a Baking Set instead?", "Yes", "Cancel", "Bake anyway");
                    if (res == 0)
                        ConvertTempBakingSet();
                    if (res == 1)
                        return false;
                }
            }

            SaveTempBakingSetIfNeeded();

            if (!FindActiveSet())
                return false;

            // Exclude scenes unchecked from the UI and scenes from other baking sets
            ProbeVolumeBakingProvider.partialBakeSceneList = new();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                var guid = scene.GetGUID();
                var sceneBakingSet = ProbeVolumeBakingSet.GetBakingSetForScene(guid);
                if (!scene.isLoaded || sceneBakingSet != activeSet) continue;
                var sceneBakeData = sceneBakingSet.GetSceneBakeData(guid);
                if (!sceneBakeData.bakeScene && sceneBakeData.hasProbeVolume) continue;

                ProbeVolumeBakingProvider.partialBakeSceneList.Add(guid);
            }

            if (ProbeVolumeBakingProvider.partialBakeSceneList.Count == activeSet.sceneGUIDs.Count)
                ProbeVolumeBakingProvider.partialBakeSceneList = null;

            if (ProbeReferenceVolumeProvider.instance.supportLightingScenarios && !activeSet.m_LightingScenarios.Contains(activeSet.lightingScenario) && activeSet.m_LightingScenarios.Count > 0)
                activeSet.SetActiveScenario(activeSet.m_LightingScenarios[0], false);

            // Layout has changed and is incompatible.
            if (activeSet.HasValidSharedData() && !activeSet.freezePlacement && !activeSet.CheckCompatibleCellLayout())
            {
                if (ProbeVolumeBakingProvider.partialBakeSceneList != null)
                {
                    if (EditorUtility.DisplayDialog("Incompatible Layout", "You are partially baking the set with an incompatible cell layout. Proceeding will invalidate all previously bake data.\n\n" + "Do you wish to continue?", "Yes", "No"))
                        return ClearBakedData();
                    else
                        return false;
                }
                else if (ProbeReferenceVolumeProvider.instance.supportLightingScenarios && activeSet.bakedData.scenarioCount != (activeSet.bakedData.HasBakedData(activeSet.lightingScenario, true) ? 1 : 0))
                {
                    if (EditorUtility.DisplayDialog("Incompatible Layout", "You are baking scenarios with incompatible cell layouts. Proceeding will invalidate all previously bake data.\n\n" + "Do you wish to continue?", "Yes", "No"))
                        return ClearBakedData();
                    else
                        return false;
                }
            }

            return true;
        }

        internal void ReleaseTemporaryBakingSet()
        {
            if (isTemporaryBakingSet)
                Object.DestroyImmediate(activeSet);
            m_ActiveSet.Set(null);
            isTemporaryBakingSet = false;
        }
    }
}
