using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering
{
    internal static class APRTLightingScenarioCapture
    {
        // Retained for migration tools. This records lighting inputs only;
        // scenario baking publishes the evaluated probe coefficients separately.
        internal static bool TryCapture(ProbeVolumeBakingSet set, string scenarioName, out string error)
        {
            error = null;
            try
            {
                if (set == null || !set.m_LightingScenarios.Contains(scenarioName))
                    throw new InvalidOperationException("Select a Lighting Scenario in the Baking Set before capturing.");
                if (ProbeVolumeBakingProvider.isRunning || EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("Capture Lighting Scenario is available in Edit Mode when no bake is running.");
                var data = set.GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
                if (data == null || !data.HasValidSharedData() || data.lightingModel != APRTLightingModel.APRTV)
                    throw new InvalidOperationException("Bake APRTV geometry before capturing lighting scenarios.");
                if (!TryCaptureInputs(set, scenarioName, out var snapshot, out error)) return false;

                Undo.RegisterCompleteObjectUndo(set, "Capture APRT Lighting Inputs");
                data.SetLightingScenario(snapshot);
                EditorUtility.SetDirty(set);
                AssetDatabase.SaveAssetIfDirty(set);
                var volume = APRTProbeReferenceVolume.instance;
                if (volume.currentBakingSet == set) volume.ScenarioBlendingChanged(true);
                SceneView.RepaintAll();
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        internal static bool TryCaptureInputs(ProbeVolumeBakingSet set, string scenarioName,
            out APRTLightingScenario snapshot, out string error)
        {
            snapshot = null;
            error = null;
            try
            {
                if (set == null || !set.m_LightingScenarios.Contains(scenarioName))
                    throw new InvalidOperationException("Select a Lighting Scenario in the Baking Set before baking.");
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    throw new InvalidOperationException("APRT lighting can only be baked in Edit Mode.");
                var scenes = new HashSet<string>(set.sceneGUIDs);
                if (scenes.Count == 0)
                    throw new InvalidOperationException("The APRT Baking Set does not contain a saved scene.");
                foreach (string guid in scenes)
                {
                    var scene = SceneManager.GetSceneByPath(AssetDatabase.GUIDToAssetPath(guid));
                    if (!scene.IsValid() || !scene.isLoaded)
                        throw new InvalidOperationException("Load every scene in this Baking Set before baking lighting.");
                }
                if (!scenes.Contains(SceneManager.GetActiveScene().GetGUID()))
                    throw new InvalidOperationException("Make a scene from this Baking Set active before capturing its sky and lighting.");

                int sourceRevision = GetSourceRevision(set);
                snapshot = new APRTLightingScenario { name = scenarioName };
                CollectDirectionalLights(scenes, snapshot.colors, snapshot.directions, out var sun, out int lightCount);
                if (lightCount > snapshot.colors.Length)
                    throw new InvalidOperationException("APRT lighting scenarios support up to four active Directional Lights. Disable extra lights before capturing.");
                using (var environment = new APRTSkyEnvironment())
                {
                    bool hasSky = RenderSettings.skybox != null;
                    bool captured = environment.Update(null, RenderSettings.skybox, sun);
                    if (hasSky && !captured)
                        throw new InvalidOperationException("The sky could not be captured. The previous lighting scenario was retained.");
                    snapshot.skyRadiance = (Vector4[])environment.radianceCoefficients.Clone();
                }
                if (!snapshot.IsValid())
                    throw new InvalidOperationException("Lighting contains invalid colors, directions or sky coefficients. The previous capture was retained.");
                if (sourceRevision != GetSourceRevision(set))
                    throw new InvalidOperationException("APRT lights or sky changed during capture. The previous bake was retained.");
                return true;
            }
            catch (Exception exception)
            {
                snapshot = null;
                error = exception.Message;
                return false;
            }
        }

        internal static int GetSourceRevision(ProbeVolumeBakingSet set)
        {
            unchecked
            {
                int hash = 17;
                void Add(int value) => hash = hash * 23 + value;
                void ObjectRevision(UnityEngine.Object value)
                {
                    Add(value != null ? value.GetInstanceID() : 0);
                    if (value != null) Add(EditorUtility.GetDirtyCount(value));
                }
                var scenes = new HashSet<string>(set.sceneGUIDs);
                Add(SceneManager.GetActiveScene().handle);
                foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
                {
                    if (light.type != LightType.Directional || !scenes.Contains(light.gameObject.scene.GetGUID())) continue;
                    ObjectRevision(light);
                    Add(light.isActiveAndEnabled ? 1 : 0);
                    Add(light.transform.forward.GetHashCode()); Add(light.color.GetHashCode()); Add(light.intensity.GetHashCode());
                }
                ObjectRevision(RenderSettings.sun);
                var skybox = RenderSettings.skybox;
                ObjectRevision(skybox);
                if (skybox != null)
                {
                    Add((int)skybox.ComputeCRC()); ObjectRevision(skybox.shader);
                    foreach (var property in skybox.GetTexturePropertyNames())
                    {
                        var texture = skybox.GetTexture(property);
                        ObjectRevision(texture);
                        if (texture != null) Add((int)texture.updateCount);
                    }
                }
                return hash;
            }
        }

        internal static void CollectDirectionalLights(HashSet<string> scenes, Vector4[] colors, Vector4[] directions,
            out Light sun, out int lightCount)
        {
            Array.Clear(colors, 0, colors.Length);
            Array.Clear(directions, 0, directions.Length);
            lightCount = 0;
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.InstanceID))
            {
                if (!light.isActiveAndEnabled || light.type != LightType.Directional || !scenes.Contains(light.gameObject.scene.GetGUID()))
                    continue;
                if (lightCount < colors.Length)
                {
                    // Match APRTRelightPass.CollectReferenceDirectionalLights.
                    colors[lightCount] = light.color.linear * light.intensity;
                    colors[lightCount].w = 1;
                    Vector3 direction = -light.transform.forward;
                    directions[lightCount] = new Vector4(direction.x, direction.y, direction.z, 0);
                }
                ++lightCount;
            }
            sun = FindSkySun(scenes);
        }

        static Light FindSkySun(HashSet<string> scenes)
        {
            // The procedural atmosphere still needs its sun when users disable
            // that Light's direct contribution. Match the runtime sky path.
            if (RenderSettings.sun != null && scenes.Contains(RenderSettings.sun.gameObject.scene.GetGUID()))
                return RenderSettings.sun;
            Light brightest = null, activeSceneSun = null;
            int activeSceneHandle = SceneManager.GetActiveScene().handle;
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (light.type != LightType.Directional || !scenes.Contains(light.gameObject.scene.GetGUID())) continue;
                if (brightest == null || light.intensity > brightest.intensity) brightest = light;
                if (light.gameObject.scene.handle == activeSceneHandle && (activeSceneSun == null || light.intensity > activeSceneSun.intensity))
                    activeSceneSun = light;
            }
            return activeSceneSun != null ? activeSceneSun : brightest;
        }
    }
}
