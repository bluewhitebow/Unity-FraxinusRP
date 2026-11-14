using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using Data = FraxinusEngine.Rendering.Ultra.UltraRenderPipelineAsset.ShaderPrefilteringData;
using Mode = FraxinusEngine.Rendering.Ultra.UltraRenderPipelineAsset.PrefilteringMode;
using MainMode = FraxinusEngine.Rendering.Ultra.UltraRenderPipelineAsset.PrefilteringModeMainLightShadows;
using AdditionalMode = FraxinusEngine.Rendering.Ultra.UltraRenderPipelineAsset.PrefilteringModeAdditionalLights;

namespace FraxinusEditor.Rendering.Ultra
{
    // Derived fields are prepared before native shader enumeration, then restored.
    // The journal survives a failed build or terminated Editor and records only
    // these fields; recovery never replaces the rest of a pipeline asset.
    internal static class UltraRPShaderPrefiltering
    {
        const string JournalPath = "Library/FraxinusRP/ShaderPrefilteringRecovery.json";
        static bool s_prepared;
        static bool s_buildObserved;
        static readonly int s_process = System.Diagnostics.Process.GetCurrentProcess().Id;
        static readonly FieldInfo[] s_fields = typeof(UltraRenderPipelineAsset)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.Name.StartsWith("_prefilter", StringComparison.Ordinal) ||
                            field.Name.StartsWith("_refilter", StringComparison.Ordinal)).ToArray();

        [Serializable]
        sealed class Journal
        {
            public int process;
            public List<Snapshot> assets = new List<Snapshot>();
        }

        [Serializable]
        sealed class Snapshot
        {
            public string guid;
            public string path;
            public bool dirty;
            public List<FieldState> fields = new List<FieldState>();
        }
        [Serializable]
        sealed class FieldState
        {
            public string name;
            public int value;
            public string diskLine;
        }

        [InitializeOnLoadMethod]
        static void Initialize()
        {
            // A domain reload can replace this class while a build is in progress.
            s_buildObserved |= BuildPipeline.isBuildingPlayer;
            EditorApplication.update -= RestoreAfterBuild;
            EditorApplication.update += RestoreAfterBuild;
            EditorApplication.delayCall += () =>
            {
                if (!BuildPipeline.isBuildingPlayer && !UltraRPShaderBuildSession.isActive && !s_prepared && File.Exists(JournalPath))
                    RestorePending();
            };
        }

        static void RestoreAfterBuild()
        {
            if (BuildPipeline.isBuildingPlayer)
                s_buildObserved = true;
            else if (!UltraRPShaderBuildSession.isActive && s_buildObserved && File.Exists(JournalPath))
                RestorePending();
        }

        internal static Data CreateData(UltraRenderPipelineAsset asset, bool stripUnused,
            bool stripDebug, bool stripScreen, bool allowHDRDisplay, bool knownForward, bool knownBuiltIn = false,
            UltraRPShaderBuildFeatures.Snapshot features = null)
        {
            Data data = Data.GetDefault();
            if (!stripUnused || asset == null || asset.GetType() != typeof(UltraRenderPipelineAsset))
                return data;
            features = features ?? UltraRPShaderBuildFeatures.Gather(new RenderPipelineAsset[] { asset }, true);

            bool stripMain = features.CanStripKeyword(ShaderKeywordStrings.MainLightShadows) &&
                features.CanStripKeyword(ShaderKeywordStrings.MainLightShadowCascades) &&
                features.CanStripKeyword(ShaderKeywordStrings.MainLightShadowScreen);
            data.mainLightShadowsPrefilteringMode = stripMain ? MainMode.Remove :
                features.CanStripKeyword(ShaderKeywordStrings.MainLightShadowScreen) ?
                MainMode.SelectMainLightCascadesAndOff : MainMode.SelectAll;
            // Native SelectIf cannot prove that this pass declares the complete
            // main/cascade/screen group. Remove OFF in the managed pass rule;
            // a pass declaring only main still needs OFF when cascades are active.
            bool stripPixel = features.CanStripKeyword(ShaderKeywordStrings.AdditionalLightsPixel);
            bool stripVertex = features.CanStripKeyword(ShaderKeywordStrings.AdditionalLightsVertex);
            data.additionalLightsPrefilteringMode = features.CanRequireKeyword(ShaderKeywordStrings.AdditionalLightsPixel) ? AdditionalMode.SelectPixel :
                features.CanRequireKeyword(ShaderKeywordStrings.AdditionalLightsVertex) ? AdditionalMode.SelectVertex :
                stripPixel && stripVertex ? AdditionalMode.Remove :
                stripPixel ? AdditionalMode.SelectVertexAndOff :
                stripVertex ? AdditionalMode.SelectPixelAndOff : AdditionalMode.SelectAll;
            if (features.CanStripKeyword(ShaderKeywordStrings.AdditionalLightShadows))
                data.additionalLightsShadowsPrefilteringMode = Mode.Remove;
            else if (features.CanRequireKeyword(ShaderKeywordStrings.AdditionalLightShadows))
                data.additionalLightsShadowsPrefilteringMode = Mode.SelectOnly;

            data.stripDebugDisplay = stripDebug;
            data.stripScreenCoordOverride = stripScreen;
            data.stripHDRKeywords = !allowHDRDisplay;
            data.stripAlphaOutputKeywords = features.CanStripKeyword(ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT);
            data.stripTerrainHoles = features.TerrainHolesUnused;
            data.stripWriteRenderingLayers = features.CanStripKeyword(ShaderKeywordStrings.WriteRenderingLayers);
            // The same build-wide intersection drives the ordinary shader stripper.
            // supportsSoftShadows is a runtime quality option; retain OFF and ON.
            data.stripSoftShadows = false;
            data.stripSoftShadowsQualityLow = features.CanStripKeyword(ShaderKeywordStrings.SoftShadowsLow);
            data.stripSoftShadowsQualityMedium = features.CanStripKeyword(ShaderKeywordStrings.SoftShadowsMedium);
            data.stripSoftShadowsQualityHigh = features.CanStripKeyword(ShaderKeywordStrings.SoftShadowsHigh);
            data.stripSHMixed = features.CanStripKeyword(ShaderKeywordStrings.EVALUATE_SH_MIXED);
            data.stripSHVertex = features.CanStripKeyword(ShaderKeywordStrings.EVALUATE_SH_VERTEX);
            data.stripReflectionProbeBlending = features.CanStripKeyword(ShaderKeywordStrings.ReflectionProbeBlending);
            data.requireReflectionProbeBlending = features.CanRequireKeyword(ShaderKeywordStrings.ReflectionProbeBlending);
            data.stripReflectionProbeBoxProjection = features.CanStripKeyword(ShaderKeywordStrings.ReflectionProbeBoxProjection);
            data.stripLODCrossFade = features.CanStripKeyword(ShaderKeywordStrings.LOD_FADE_CROSSFADE);
            data.stripProbeVolumes = features.CanStripKeyword(ShaderKeywordStrings.ProbeVolumeL1) && features.CanStripKeyword(ShaderKeywordStrings.ProbeVolumeL2);
            data.stripAPRTVolumes = features.CanStripKeyword(ShaderKeywordStrings.ProbeVolumeAPRT);
            data.stripLightLayers = features.CanStripKeyword(ShaderKeywordStrings.LightLayers);
            data.stripLightCookies = features.CanStripKeyword(ShaderKeywordStrings.LightCookies);
            data.areaLightsPrefilteringMode = features.CanStripKeyword(ShaderKeywordStrings.AreaLights) ? Mode.Remove :
                features.CanRequireKeyword(ShaderKeywordStrings.AreaLights) ? Mode.SelectOnly : Mode.Select;
            data.stripMixedLighting = features.CanStripKeyword(ShaderKeywordStrings.LightmapShadowMixing) &&
                features.CanStripKeyword(ShaderKeywordStrings.ShadowsShadowMask) &&
                features.CanStripKeyword(ShaderKeywordStrings.MixedLightingSubtractive);
            if (features.CanStripKeyword(ShaderKeywordStrings.ForwardPlus))
                data.forwardPlusPrefilteringMode = Mode.Remove;

            // Independent decal-provider requirements, merged across all assets.
            // Only named MRT modes are removed; DBuffer OFF remains available.
            data.stripDBufferMRT1 = features.CanStripKeyword(ShaderKeywordStrings.DBufferMRT1);
            data.stripDBufferMRT2 = features.CanStripKeyword(ShaderKeywordStrings.DBufferMRT2);
            data.stripDBufferMRT3 = features.CanStripKeyword(ShaderKeywordStrings.DBufferMRT3);
            if (knownForward)
                data.deferredPrefilteringMode = Mode.Remove;
            // AO is injected dynamically from the camera's Volume stack.
            data.screenSpaceOcclusionPrefilteringMode = Mode.Select;
            return data;
        }

        internal static void PrepareForBuild(bool isDevelopmentBuild, BuildTarget? buildTarget = null,
            IEnumerable<RenderPipelineAsset> includedAssets = null, bool enableNativeFiltering = true)
        {
            // A failed synchronous build can be followed by another build before
            // EditorApplication.update runs. Recompute from the original state
            // for every preparation, including the normal preprocess callback.
            if (File.Exists(JournalPath))
                RestorePending();
            var allAssets = new List<RenderPipelineAsset>();
            if (includedAssets != null)
                allAssets.AddRange(includedAssets);
            else if (!UnityEditor.Rendering.BuildTargetExtensions.TryGetRenderPipelineAssets(
                         buildTarget ?? EditorUserBuildSettings.activeBuildTarget, allAssets))
                return;
            var assets = allAssets.OfType<UltraRenderPipelineAsset>().Distinct().ToList();
            if (assets.Count == 0)
                return;

            var settings = GraphicsSettings.GetSettingsForRenderPipeline<UltraRenderPipeline>() as UltraRenderPipelineGlobalSettings;
            var ultra = settings == null ? null : RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<UltraRenderPipelineShaderStrippingSetting>(settings);
            var core = settings == null ? null : RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ShaderStrippingSetting>(settings);
            bool stripUnused = ultra != null && ultra.stripUnusedVariants;
            var features = UltraRPShaderBuildFeatures.WithBuildSettings(
                UltraRPShaderBuildFeatures.Gather(allAssets, stripUnused),
                core != null && core.stripRuntimeDebugShaders, ultra != null && ultra.stripScreenCoordOverrideVariants);
            UltraRPShaderBuildReport.RecordFeatures(features);
            var journal = new Journal { process = s_process };
            foreach (var asset in assets)
                journal.assets.Add(Capture(asset));
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath));
            File.WriteAllText(JournalPath + ".tmp", JsonUtility.ToJson(journal, true));
            File.Move(JournalPath + ".tmp", JournalPath);
            s_prepared = true;
            s_buildObserved = BuildPipeline.isBuildingPlayer;
            try
            {
                bool knownForward = stripUnused && UltraRPPostProcessShaderStripper.TryProveForwardOnlyBuild(
                    allAssets, out _, out _);
                foreach (var asset in assets)
                {
                    // Plain native AssetBundle callbacks have no reliable begin/end
                    // boundary and Unity may reuse them across synchronous builds.
                    // Keep their native fields neutral; current settings still drive
                    // the managed stripper and its report. Explicit/SBP/Player
                    // sessions have a real boundary and use early native filtering.
                    Data data = enableNativeFiltering ? CreateData(asset, stripUnused, core != null && core.stripRuntimeDebugShaders,
                        ultra != null && ultra.stripScreenCoordOverrideVariants, PlayerSettings.allowHDRDisplaySupport, knownForward, false, features) : Data.GetDefault();
                    asset.UpdateShaderKeywordPrefiltering(ref data);
                    EditorUtility.SetDirty(asset);
                    Debug.Log("UltraRP shader prefiltering: " + asset.name + ", native filtering=" + enableNativeFiltering + ", opt-in=" + stripUnused +
                        ", known forward=" + knownForward + ", main=" + data.mainLightShadowsPrefilteringMode +
                        ", additional=" + data.additionalLightsPrefilteringMode + ", strip debug=" + data.stripDebugDisplay +
                        ", strip screen=" + data.stripScreenCoordOverride + ", strip HDR output=" + data.stripHDRKeywords +
                        ", shared removed globals=" + string.Join(",", features.ForbiddenGlobalKeywords) +
                        ", shared required globals=" + string.Join(",", features.RequiredGlobalKeywords) + ".");
                }
            }
            catch
            {
                // Preparation owns these temporary mutations, including direct callers.
                RestorePending();
                throw;
            }
        }

        static string FieldPattern(string name) => @"(?m)^  " + Regex.Escape(name) + @": [^\r\n]*(?:\r?\n|$)";

        static string DiskPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || File.Exists(assetPath))
                return assetPath;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            return package == null ? assetPath : Path.Combine(package.resolvedPath,
                assetPath.Substring(package.assetPath.Length).TrimStart('/', '\\'));
        }

        static Snapshot Capture(UltraRenderPipelineAsset asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            string diskPath = DiskPath(path);
            if (string.IsNullOrEmpty(path) || !File.Exists(diskPath))
                throw new BuildFailedException("UltraRP prefiltering requires a saved pipeline asset: " + asset.name);
            if (!AssetDatabase.IsMainAsset(asset))
                throw new BuildFailedException("UltraRP prefilter recovery requires a standalone pipeline asset: " + path);
            string disk = File.ReadAllText(diskPath);
            if (!disk.StartsWith("%YAML", StringComparison.Ordinal))
                throw new BuildFailedException("UltraRP prefilter recovery requires text serialization for pipeline asset: " + path);
            RequireSingleObject(disk, path);
            var snapshot = new Snapshot { guid = AssetDatabase.AssetPathToGUID(path), path = path, dirty = EditorUtility.IsDirty(asset) };
            foreach (var field in s_fields)
            {
                if (field.FieldType != typeof(bool) && !field.FieldType.IsEnum)
                    throw new BuildFailedException("Unsupported prefilter field type: " + field.Name);
                Match line = Regex.Match(disk, FieldPattern(field.Name));
                snapshot.fields.Add(new FieldState { name = field.Name, value = Convert.ToInt32(field.GetValue(asset)),
                    diskLine = line.Success ? line.Value : null });
            }
            return snapshot;
        }

        internal static string RestoreSerializedFields(string currentText, IEnumerable<KeyValuePair<string, string>> fields)
        {
            foreach (var field in fields)
            {
                string pattern = FieldPattern(field.Key);
                if (Regex.IsMatch(currentText, pattern))
                    currentText = Regex.Replace(currentText, pattern, _ => field.Value ?? "");
                else if (!string.IsNullOrEmpty(field.Value))
                    currentText += field.Value;
            }
            return currentText;
        }

        static void RequireSingleObject(string yaml, string path)
        {
            if (Regex.Matches(yaml, @"(?m)^--- !u!").Count != 1)
                throw new BuildFailedException("UltraRP prefilter recovery requires one serialized object per pipeline asset file: " + path);
        }

        internal static void RestorePending()
        {
            if (!File.Exists(JournalPath))
            {
                s_prepared = s_buildObserved = false;
                return;
            }
            var journal = JsonUtility.FromJson<Journal>(File.ReadAllText(JournalPath));
            foreach (var snapshot in journal.assets)
            {
                string path = AssetDatabase.GUIDToAssetPath(snapshot.guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.AssetPathToGUID(path) != snapshot.guid)
                    throw new BuildFailedException("Cannot recover UltraRP prefilter fields: original asset GUID is missing: " + snapshot.path);
                string diskPath = DiskPath(path);
                var asset = AssetDatabase.LoadAssetAtPath<UltraRenderPipelineAsset>(path);
                if (asset == null || !AssetDatabase.IsMainAsset(asset) || !File.Exists(diskPath))
                    throw new BuildFailedException("Cannot recover UltraRP prefilter fields: " + path);
                string current = File.ReadAllText(diskPath);
                RequireSingleObject(current, path);
                foreach (var state in snapshot.fields)
                {
                    var field = s_fields.FirstOrDefault(item => item.Name == state.name);
                    if (field == null)
                        throw new BuildFailedException("Missing prefilter recovery field: " + state.name);
                    field.SetValue(asset, field.FieldType == typeof(bool) ? (object)(state.value != 0) : Enum.ToObject(field.FieldType, state.value));
                }
                EditorUtility.SetDirty(asset);
                // Build pipelines may save dirty assets. Restore only their original
                // derived YAML lines, preserving unrelated edits, line endings and BOM.
                byte[] bytes = File.ReadAllBytes(diskPath);
                bool bom = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
                string restored = RestoreSerializedFields(current, snapshot.fields.Select(state =>
                    new KeyValuePair<string, string>(state.name, state.diskLine)));
                if (!string.Equals(current, restored, StringComparison.Ordinal))
                    File.WriteAllText(diskPath, restored, new UTF8Encoding(bom));
                if (!snapshot.dirty)
                    EditorUtility.ClearDirty(asset);
            }
            File.Delete(JournalPath);
            s_prepared = s_buildObserved = false;
            Debug.Log("FRAXINUS_PREFILTER_RESTORED: original derived fields and asset dirty states.");
        }

    }
}
