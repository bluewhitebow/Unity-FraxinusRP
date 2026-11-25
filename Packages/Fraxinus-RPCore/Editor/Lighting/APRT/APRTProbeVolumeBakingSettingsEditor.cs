using System.Collections.Generic;
using UnityEditor;

namespace UnityEngine.Rendering
{
    /// <summary>Capture settings independent of APV's lightmapper settings.</summary>
    internal sealed class APRTProbeVolumeBakingSettingsEditor : ProbeVolumeBakingSettingsEditor
    {
        readonly SerializedObject m_Object;
        internal APRTProbeVolumeBakingSettingsEditor(SerializedObject serializedObject) { m_Object = serializedObject; }

        internal override void Draw()
        {
            using (new EditorGUI.DisabledScope(ProbeVolumeBakingProvider.isRunning))
            {
                EditorGUILayout.LabelField("Geometry Capture", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(m_Object.FindProperty("aprtCaptureResolution"),
                    new GUIContent("Capture Resolution", "Pixels per side of each of the six directions. Higher values improve small features and increase bake time."));
                EditorGUILayout.PropertyField(m_Object.FindProperty("aprtCaptureDistance"),
                    new GUIContent("Capture Distance", "Maximum geometry capture distance in world units. Misses are treated as sky."));
                EditorGUILayout.PropertyField(m_Object.FindProperty("aprtBakeSaturationPriority"), new GUIContent("Bake Saturation Priority", "Increase vivid surfaces' contributions during capture."));
                EditorGUILayout.PropertyField(m_Object.FindProperty("aprtBakeCenterWeight"), new GUIContent("Bake Center Weight", "Give capture pixels near the face center greater influence."));
                EditorGUILayout.Space();
                var skyBounce = m_Object.FindProperty("aprtBakeSkyBounce");
                EditorGUILayout.PropertyField(skyBounce, new GUIContent("Bake Sky One Bounce", "Include one diffuse reflection of the sky from static mesh surfaces. Changing geometry or materials requires rebaking; changing sky radiance does not."));
                if (skyBounce.boolValue || skyBounce.hasMultipleDifferentValues)
                {
                    EditorGUILayout.PropertyField(m_Object.FindProperty("aprtSkyBouncePrimarySamples"), new GUIContent("Surface Rays per Probe", "Directions traced from each probe to find reflecting surfaces. More rays reduce spatial noise and increase bake time."));
                    EditorGUILayout.PropertyField(m_Object.FindProperty("aprtSkyBounceSkySamples"), new GUIContent("Sky Rays per Surface", "Directions traced from each surface hit to the sky. More rays improve visibility and increase bake time."));
                    EditorGUILayout.HelpBox("Sky one bounce is baked from static mesh geometry and diffuse base color. It increases bake time and transport storage. Start with a small volume.", MessageType.Info);
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Relighting", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Add Lighting > APRTV Options to a Volume Profile to adjust relighting and receiving per camera. Unchecked parameters inherit the default Volume Profile or this Baking Set's existing values.", MessageType.Info);
            var set = m_Object.targetObject as ProbeVolumeBakingSet;
            DrawLightingSource(set);
            using (new EditorGUI.DisabledScope(set == null || m_Object.targetObjects.Length != 1))
            {
                if (GUILayout.Button(new GUIContent("Export Existing Relighting to Volume Profile", "Create a profile with all APRTV Options overrides set to the existing Baking Set values. Assign the profile to a Volume to preserve the current appearance. This does not modify the scene.")))
                {
                    m_Object.ApplyModifiedProperties();
                    string path = EditorUtility.SaveFilePanelInProject("Export APRTV Options", set.name + " APRTV Options", "asset", "Choose a location for the APRTV Volume Profile.");
                    if (!string.IsNullOrEmpty(path))
                    {
                        var profile = CreateOptionsProfile(set, AssetDatabase.GenerateUniqueAssetPath(path));
                        Selection.activeObject = profile;
                        EditorGUIUtility.PingObject(profile);
                    }
                }
            }

            var data = set?.GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
            bool validBake = data != null && data.HasValidSharedData();
            if (validBake)
            {
                string modelName = data.lightingModel == APRTLightingModel.APRTV ? "APRTV" : "Legacy";
                EditorGUILayout.LabelField("Baked APRTV Data", $"{modelName} / format {data.formatVersion} / {data.sampleCount} probes / {data.cells.Count} cells");
                EditorGUILayout.LabelField("Sky Transport", data.hasSkyBounceTransfer ? "Direct sky + one static diffuse bounce" : data.hasSkyIrradianceTransfer ? "Direct sky" : "Not captured");
                if (data.lightingModel != APRTLightingModel.APRTV)
                    EditorGUILayout.HelpBox("This Baking Set contains a legacy lighting bake. Bake APRTV again to upgrade it to the current lighting response.", MessageType.Info);
                var bounce = m_Object.FindProperty("aprtBakeSkyBounce");
                if (!bounce.hasMultipleDifferentValues && bounce.boolValue != data.hasSkyBounceTransfer)
                    EditorGUILayout.HelpBox("Bake APRTV again to apply the Sky One Bounce setting. Runtime uses the last complete bake.", MessageType.Info);
                if (!data.hasSkyIrradianceTransfer)
                    EditorGUILayout.HelpBox("Bake APRTV again to capture directional sky transfer. The existing bake still provides its directional-light response.", MessageType.Info);
            }
            else if (data != null && data.sampleCount > 0)
                EditorGUILayout.HelpBox("This APRTV data uses an incompatible format. Bake APRTV again to generate current transport data.", MessageType.Warning);
            EditorGUILayout.HelpBox(APRTProbeVolumeEditorBackend.implementationUnavailableMessage, MessageType.Info);
        }

        void DrawLightingSource(ProbeVolumeBakingSet set)
        {
            bool supportScenarios = APRTProbeReferenceVolume.instance.supportLightingScenarios;
            using (new EditorGUI.DisabledScope(set == null || m_Object.targetObjects.Length != 1 || ProbeVolumeBakingProvider.isRunning || !supportScenarios))
            {
                var useScenarios = m_Object.FindProperty(nameof(ProbeVolumeBakingSet.aprtUseLightingScenarios));
                EditorGUI.BeginChangeCheck();
                int mode = EditorGUILayout.Popup(new GUIContent("Lighting Source", "Use the active baked Lighting Scenario, or relight the geometry transport from the scene's current lighting."),
                    supportScenarios && useScenarios.boolValue ? 1 : 0, new[] { "Live Scene Lighting", "Baked Lighting Scenarios" });
                if (EditorGUI.EndChangeCheck())
                {
                    useScenarios.boolValue = mode == 1;
                    m_Object.ApplyModifiedProperties();
                    if (set != null && APRTProbeReferenceVolume.instance.currentBakingSet == set)
                        APRTProbeReferenceVolume.instance.ScenarioBlendingChanged(true);
                    SceneView.RepaintAll();
                }
            }
            if (!supportScenarios)
                EditorGUILayout.HelpBox("Enable Lighting Scenarios in the render pipeline asset to use baked scenarios. APRT currently uses live scene lighting.", MessageType.Info);
        }

        internal static void DrawScenarioBlendPreview(ProbeVolumeBakingSet set)
        {
            if (set == null) return;
            var volume = APRTProbeReferenceVolume.instance;
            var data = set.GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
            string current = set.GetLightingScenario(APRTProbeReferenceVolumeBackend.backendId);
            bool baked = data?.HasValidScenarioData(current, volume) == true;
            var targets = new List<string> { "None" };
            var targetNames = new List<string> { null };
            foreach (string name in set.lightingScenarios)
                if (name != current && data?.HasValidScenarioData(name, volume) == true)
                {
                    targets.Add(name);
                    targetNames.Add(name);
                }
            int targetIndex = Mathf.Max(0, targetNames.IndexOf(set.GetOtherScenario(APRTProbeReferenceVolumeBackend.backendId)));
            float factor = set.GetScenarioBlendingFactor(APRTProbeReferenceVolumeBackend.backendId);
            using (new EditorGUI.DisabledScope(!volume.supportScenarioBlending || !set.aprtUseLightingScenarios || !baked || ProbeVolumeBakingProvider.isRunning))
            {
                EditorGUI.BeginChangeCheck();
                int target = EditorGUILayout.Popup(new GUIContent("Blend Target", "Preview the blend between the active scenario and another baked scenario."), targetIndex, targets.ToArray());
                using (new EditorGUI.DisabledScope(target == 0))
                    factor = EditorGUILayout.Slider(new GUIContent("Blend Factor", "0 uses the active scenario; 1 uses the target scenario. Blend previews are temporary and are not saved."), factor, 0, 1);
                if (EditorGUI.EndChangeCheck())
                {
                    volume.SetActiveBakingSet(set);
                    set.BlendLightingScenario(targetNames[target], factor, volume);
                    SceneView.RepaintAll();
                }
            }
            if (!volume.supportScenarioBlending)
                EditorGUILayout.HelpBox("Enable Lighting Scenario Blending in the render pipeline asset to blend scenarios.", MessageType.Info);
        }

        internal static void RemoveLightingScenario(ProbeVolumeBakingSet set, string name)
        {
            bool activeRemoved = set.GetLightingScenario(APRTProbeReferenceVolumeBackend.backendId) == name;
            if (!set.RemoveScenario(name) || !activeRemoved) return;
            var data = set.GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
            // Use a scoped backend adapter without switching the scene's active
            // Baking Set merely because another asset is being edited.
            var volume = new APRTProbeReferenceVolume();
            string replacement = null;
            foreach (var candidate in set.lightingScenarios)
                if (data?.HasValidScenarioData(candidate, volume) == true)
                {
                    replacement = candidate;
                    break;
                }
            set.BlendLightingScenario(null, 0, volume);
            if (replacement != null) set.SetActiveScenario(replacement, false, volume);
            else set.aprtUseLightingScenarios = false;
            if (APRTProbeReferenceVolume.instance.currentBakingSet == set)
                APRTProbeReferenceVolume.instance.ScenarioBlendingChanged(true);
        }

        internal static VolumeProfile CreateOptionsProfile(ProbeVolumeBakingSet set, string assetPath)
        {
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null || System.IO.File.Exists(assetPath))
                throw new System.ArgumentException("An asset already exists at the selected path.", nameof(assetPath));
            var data = set?.GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
            var model = data != null && data.sampleCount > 0 ? data.lightingModel : APRTLightingModel.APRTV;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            AssetDatabase.CreateAsset(profile, assetPath);
            var options = profile.Add<APRTOptions>(true);
            options.ApplySettings(APRTRelightSettings.FromBakingSet(set, model));
            AssetDatabase.AddObjectToAsset(options, profile);
            AssetDatabase.SetMainObject(profile, assetPath);
            EditorUtility.SetDirty(profile);
            EditorUtility.SetDirty(options);
            AssetDatabase.SaveAssetIfDirty(profile);
            AssetDatabase.SaveAssetIfDirty(options);
            return profile;
        }
    }
}
