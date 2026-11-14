using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    // Shared by native keyword prefiltering and scriptable variant stripping.
    // A build includes the union of every declared asset/renderer configuration:
    // a keyword can be removed only when every configuration proves it unused.
    internal static class UltraRPShaderBuildFeatures
    {
        [Flags]
        internal enum DisabledCapabilities
        {
            None = 0,
            LODCrossFade = 1 << 0,
            ProbeVolumes = 1 << 1,
            APRTVolumes = 1 << 2,
            LightLayers = 1 << 3,
            LightCookies = 1 << 4,
            MixedLighting = 1 << 5,
            ReflectionProbeBlending = 1 << 6,
            ReflectionProbeBoxProjection = 1 << 7,
            AlphaOutput = 1 << 8,
            ScreenSpaceShadows = 1 << 9,
            RenderingLayerOutput = 1 << 10,
        }

        // Named DBuffer states only. OFF is always retained independently.
        [Flags]
        internal enum DBufferModes
        {
            None = 0,
            MRT1 = 1 << 0,
            MRT2 = 1 << 1,
            MRT3 = 1 << 2,
            All = MRT1 | MRT2 | MRT3,
        }

        [Flags]
        internal enum LightingKeywords
        {
            None = 0,
            ForwardPlus = 1 << 0,
            AdditionalPixel = 1 << 1,
            AdditionalVertex = 1 << 2,
            All = ForwardPlus | AdditionalPixel | AdditionalVertex
        }

        // Bit N permits the exact LightingKeywords tuple N. Unknown configurations
        // contribute all tuples, so merging cannot narrow another asset's needs.
        internal const int AllLightingStates = 0xff;

        internal sealed class AssetFeatures
        {
            readonly HashSet<string> _forbidden;
            readonly HashSet<string> _required;
            public string Source { get; }
            public IReadOnlyList<string> ForbiddenGlobalKeywords { get; }
            public IReadOnlyList<string> RequiredGlobalKeywords { get; }
            public IReadOnlyList<string> Reasons { get; }
            // Alpha test is also used by ordinary materials. This capability is
            // deliberately separate from the forbidden global keyword set.
            public bool TerrainHolesUnused { get; }
            public int AllowedLightingStates { get; }
            public bool RequireMainLightShadows { get; }

            internal AssetFeatures(string source, IEnumerable<string> forbidden, IEnumerable<string> reasons,
                bool terrainHolesUnused = false, IEnumerable<string> required = null,
                int allowedLightingStates = AllLightingStates, bool requireMainLightShadows = false)
            {
                Source = source ?? "<unknown>";
                _forbidden = new HashSet<string>(forbidden ?? Array.Empty<string>(), StringComparer.Ordinal);
                ForbiddenGlobalKeywords = Array.AsReadOnly(_forbidden.OrderBy(name => name, StringComparer.Ordinal).ToArray());
                _required = new HashSet<string>(required ?? Array.Empty<string>(), StringComparer.Ordinal);
                _required.ExceptWith(_forbidden);
                RequiredGlobalKeywords = Array.AsReadOnly(_required.OrderBy(name => name, StringComparer.Ordinal).ToArray());
                Reasons = Array.AsReadOnly((reasons ?? Array.Empty<string>()).ToArray());
                TerrainHolesUnused = terrainHolesUnused;
                AllowedLightingStates = allowedLightingStates;
                RequireMainLightShadows = requireMainLightShadows;
            }

            public bool CanStripKeyword(string keyword) => keyword != null && _forbidden.Contains(keyword);
            public bool CanRequireKeyword(string keyword) => keyword != null && _required.Contains(keyword);
        }

        internal sealed class Snapshot
        {
            readonly HashSet<string> _forbidden;
            readonly HashSet<string> _required;
            public bool CanStrip { get; }
            public IReadOnlyList<string> ForbiddenGlobalKeywords { get; }
            public IReadOnlyList<string> RequiredGlobalKeywords { get; }
            public IReadOnlyList<AssetFeatures> Assets { get; }
            public IReadOnlyList<string> Reasons { get; }
            public bool TerrainHolesUnused { get; }
            public int AllowedLightingStates { get; }
            public bool RequireMainLightShadows { get; }

            internal Snapshot(bool canStrip, AssetFeatures[] assets, IEnumerable<string> forbidden, IEnumerable<string> reasons, IEnumerable<string> required = null)
            {
                CanStrip = canStrip;
                Assets = Array.AsReadOnly(assets);
                _forbidden = new HashSet<string>(forbidden, StringComparer.Ordinal);
                ForbiddenGlobalKeywords = Array.AsReadOnly(_forbidden.OrderBy(name => name, StringComparer.Ordinal).ToArray());
                _required = new HashSet<string>(required ?? Array.Empty<string>(), StringComparer.Ordinal);
                _required.ExceptWith(_forbidden);
                RequiredGlobalKeywords = Array.AsReadOnly(_required.OrderBy(name => name, StringComparer.Ordinal).ToArray());
                Reasons = Array.AsReadOnly(reasons.ToArray());
                TerrainHolesUnused = canStrip && assets.Length > 0 &&
                    assets.All(asset => asset != null && asset.TerrainHolesUnused);
                AllowedLightingStates = !canStrip || assets.Length == 0 ? AllLightingStates :
                    assets.Aggregate(0, (allowed, asset) => allowed | (asset?.AllowedLightingStates ?? AllLightingStates));
                RequireMainLightShadows = canStrip && assets.Length > 0 &&
                    assets.All(asset => asset != null && asset.RequireMainLightShadows);
            }

            public bool CanStripKeyword(string keyword) => CanStrip && keyword != null && _forbidden.Contains(keyword);
            public bool CanRequireKeyword(string keyword) => CanStrip && keyword != null && _required.Contains(keyword);
        }

        // Pure entry point: null means unproven, not disabled. Explicit SH modes
        // retain the OFF state. Required lighting states need the independent provider contract.
        internal static AssetFeatures BuildAssetFeatures(ShEvalMode? effectiveShMode,
            bool? perLightSoftShadowQuality, bool? forwardPlus, string source,
            DisabledCapabilities disabledCapabilities = DisabledCapabilities.None,
            DBufferModes? dbufferModes = null, bool? reflectionProbeBlending = null,
            LightRenderingMode? additionalLightsMode = null, bool lightingProvidersPreserve = false)
        {
            var forbidden = new HashSet<string>(StringComparer.Ordinal);
            var reasons = new List<string>();
            if (perLightSoftShadowQuality == true)
            {
                forbidden.UnionWith(new[] { ShaderKeywordStrings.SoftShadowsLow,
                    ShaderKeywordStrings.SoftShadowsMedium, ShaderKeywordStrings.SoftShadowsHigh });
                reasons.Add("Per-light soft-shadow quality uses _SHADOWS_SOFT; static LOW/MEDIUM/HIGH are disabled.");
            }
            else
                reasons.Add("Static soft-shadow quality variants retained: per-light quality is unavailable or unproven.");

            switch (effectiveShMode)
            {
                case ShEvalMode.PerPixel:
                    forbidden.Add(ShaderKeywordStrings.EVALUATE_SH_MIXED);
                    forbidden.Add(ShaderKeywordStrings.EVALUATE_SH_VERTEX);
                    reasons.Add("Effective SH mode is PerPixel.");
                    break;
                case ShEvalMode.Mixed:
                    forbidden.Add(ShaderKeywordStrings.EVALUATE_SH_VERTEX);
                    reasons.Add("Effective SH mode is Mixed; retain MIXED and OFF.");
                    break;
                case ShEvalMode.PerVertex:
                    forbidden.Add(ShaderKeywordStrings.EVALUATE_SH_MIXED);
                    reasons.Add("Effective SH mode is PerVertex; retain VERTEX and OFF.");
                    break;
                default:
                    reasons.Add("SH variants retained: effective mode is unproven.");
                    break;
            }

            if (forwardPlus == false)
            {
                forbidden.Add(ShaderKeywordStrings.ForwardPlus);
                reasons.Add("Every declared graph has a known rendering-mode request without Forward+ or Deferred+ light lists.");
            }
            else
                reasons.Add("Forward+ light-list variants retained: a graph requires Forward+ or Deferred+, or its requirements are unproven.");

            AddDisabled(forbidden, disabledCapabilities, DisabledCapabilities.LODCrossFade, ShaderKeywordStrings.LOD_FADE_CROSSFADE);
            AddDisabled(forbidden, disabledCapabilities, DisabledCapabilities.ProbeVolumes, ShaderKeywordStrings.ProbeVolumeL1, ShaderKeywordStrings.ProbeVolumeL2);
            AddDisabled(forbidden, disabledCapabilities, DisabledCapabilities.APRTVolumes, ShaderKeywordStrings.ProbeVolumeAPRT);
            AddDisabled(forbidden, disabledCapabilities, DisabledCapabilities.LightLayers, ShaderKeywordStrings.LightLayers);
            AddDisabled(forbidden, disabledCapabilities, DisabledCapabilities.LightCookies, ShaderKeywordStrings.LightCookies);
            AddDisabled(forbidden, disabledCapabilities, DisabledCapabilities.MixedLighting, ShaderKeywordStrings.LightmapShadowMixing, ShaderKeywordStrings.ShadowsShadowMask, ShaderKeywordStrings.MixedLightingSubtractive);
            AddDisabled(forbidden, disabledCapabilities, DisabledCapabilities.ReflectionProbeBlending, ShaderKeywordStrings.ReflectionProbeBlending);
            AddDisabled(forbidden, disabledCapabilities, DisabledCapabilities.ReflectionProbeBoxProjection, ShaderKeywordStrings.ReflectionProbeBoxProjection);
            AddDisabled(forbidden, disabledCapabilities, DisabledCapabilities.AlphaOutput, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT);
            AddDisabled(forbidden, disabledCapabilities, DisabledCapabilities.ScreenSpaceShadows, ShaderKeywordStrings.MainLightShadowScreen);
            AddDisabled(forbidden, disabledCapabilities, DisabledCapabilities.RenderingLayerOutput, ShaderKeywordStrings.WriteRenderingLayers);
            if (disabledCapabilities != DisabledCapabilities.None)
                reasons.Add("Disabled asset capabilities: " + disabledCapabilities);
            if (dbufferModes.HasValue && (dbufferModes.Value & ~DBufferModes.All) == 0)
            {
                DBufferModes modes = dbufferModes.Value;
                if ((modes & DBufferModes.MRT1) == 0)
                    forbidden.Add(ShaderKeywordStrings.DBufferMRT1);
                if ((modes & DBufferModes.MRT2) == 0)
                    forbidden.Add(ShaderKeywordStrings.DBufferMRT2);
                if ((modes & DBufferModes.MRT3) == 0)
                    forbidden.Add(ShaderKeywordStrings.DBufferMRT3);
                reasons.Add("Declared DBuffer modes: " + modes + "; OFF retained.");
            }
            else
                reasons.Add("DBuffer modes retained: a provider or its configuration is unproven.");
            // supportsSoftShadows is intentionally absent: runtime quality options
            // can enable it after startup. Keep both ordinary _SHADOWS_SOFT states.
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (lightingProvidersPreserve)
            {
                if (reflectionProbeBlending == true)
                    required.Add(ShaderKeywordStrings.ReflectionProbeBlending);
                // Forward+ and Deferred+ disable both ordinary additional-light keywords.
                // Stencil and GPU Cluster deferred keep ordinary Forward's keyword policy.
                if (forwardPlus == false)
                {
                    if (additionalLightsMode == LightRenderingMode.PerPixel)
                        required.Add(ShaderKeywordStrings.AdditionalLightsPixel);
                    else if (additionalLightsMode == LightRenderingMode.PerVertex)
                        required.Add(ShaderKeywordStrings.AdditionalLightsVertex);
                }
                reasons.Add("Required camera lighting globals: " + string.Join(",", required) + ".");
            }
            else
                reasons.Add("Lighting OFF retained: the renderer/provider contract is unproven.");
            int lightingStates = GetAllowedLightingStates(forwardPlus, additionalLightsMode, lightingProvidersPreserve);
            reasons.Add("Allowed camera lighting tuples (ForwardPlus, AdditionalPixel, AdditionalVertex): " +
                DescribeLightingStates(lightingStates) + ".");
            return new AssetFeatures(source, forbidden, reasons, required: required, allowedLightingStates: lightingStates);
        }

        internal static int GetAllowedLightingStates(bool? forwardPlus, LightRenderingMode? additionalMode,
            bool lightingProvidersPreserve)
        {
            if (!lightingProvidersPreserve || !forwardPlus.HasValue)
                return AllLightingStates;
            LightingKeywords ordinary;
            switch (additionalMode)
            {
                case LightRenderingMode.Disabled: ordinary = LightingKeywords.None; break;
                case LightRenderingMode.PerPixel: ordinary = LightingKeywords.AdditionalPixel; break;
                case LightRenderingMode.PerVertex: ordinary = LightingKeywords.AdditionalVertex; break;
                default: return AllLightingStates;
            }
            // UltraRenderer always strips the additional-light OFF state and
            // ForwardLights enables ordinary additional lighting independently
            // of visible light count. Forward+/Deferred+ instead disable both
            // ordinary keywords. A declared plus node may be disabled at runtime,
            // so retain the ordinary tuple even when every graph declares plus.
            return (1 << (int)ordinary) | (forwardPlus.Value ? 1 << (int)LightingKeywords.ForwardPlus : 0);
        }

        internal static string DescribeLightingStates(int allowed)
            => string.Join(", ", Enumerable.Range(0, 8).Where(state => (allowed & (1 << state)) != 0)
                .Select(state => "(" + ((state & 1) != 0 ? "1" : "0") + "," +
                    ((state & 2) != 0 ? "1" : "0") + "," + ((state & 4) != 0 ? "1" : "0") + ")"));

        static void AddDisabled(HashSet<string> result, DisabledCapabilities disabled, DisabledCapabilities capability, params string[] keywords)
        {
            if ((disabled & capability) != 0)
                result.UnionWith(keywords);
        }

        // Asset light modes are build capabilities. Light count, shadow distance,
        // cascade count and ordinary soft-shadow quality remain runtime options.
        // Never infer a required ON state from a light count. Fixed camera policy is analyzed separately.
        internal static AssetFeatures BuildLightingFeatures(LightRenderingMode? mainMode, bool? mainShadows,
            LightRenderingMode? additionalMode, bool? additionalShadows, string source, bool knownForwardOnly = false)
        {
            var forbidden = new HashSet<string>(StringComparer.Ordinal);
            if (mainMode == LightRenderingMode.Disabled || mainShadows == false)
                forbidden.UnionWith(new[] { ShaderKeywordStrings.MainLightShadows,
                    ShaderKeywordStrings.MainLightShadowCascades, ShaderKeywordStrings.MainLightShadowScreen });
            switch (additionalMode)
            {
                case LightRenderingMode.Disabled:
                    forbidden.Add(ShaderKeywordStrings.AdditionalLightsVertex);
                    forbidden.Add(ShaderKeywordStrings.AdditionalLightsPixel);
                    break;
                case LightRenderingMode.PerVertex:
                    forbidden.Add(ShaderKeywordStrings.AdditionalLightsPixel);
                    break;
                case LightRenderingMode.PerPixel:
                    forbidden.Add(ShaderKeywordStrings.AdditionalLightsVertex);
                    break;
            }
            // Stencil Deferred restores the shadow keyword from ShadowData even
            // for the empty-map baked-shadow path. Forward Plus can enable it
            // independently of the additional light mode. Asset flags alone are
            // therefore insufficient evidence across these renderer paths.
            if (knownForwardOnly && additionalShadows == false)
                forbidden.Add(ShaderKeywordStrings.AdditionalLightShadows);
            return new AssetFeatures(source, forbidden, new[] {
                "Declared lighting: main=" + mainMode + ", main shadows=" + mainShadows +
                ", additional=" + additionalMode + ", additional shadows=" + additionalShadows +
                ", proven ordinary Forward-only=" + knownForwardOnly +
                "; light counts, cascade choices and soft-shadow ON/OFF remain dynamic."
            });
        }

        internal static AssetFeatures BuildShadowFeatures(LightRenderingMode? mainMode, bool? mainShadows,
            LightRenderingMode? additionalMode, bool? additionalShadows, bool graphProvesShadowPasses, string source)
        {
            bool main = graphProvesShadowPasses && mainMode == LightRenderingMode.PerPixel && mainShadows == true;
            // An ordinary PerVertex/Disabled camera can leave additional shadows
            // OFF even if another graph uses Forward+/Deferred+ light lists.
            bool additional = graphProvesShadowPasses && additionalMode == LightRenderingMode.PerPixel && additionalShadows == true;
            return new AssetFeatures(source, Array.Empty<string>(), new[] {
                "Required shadow receivers: main group=" + main + ", additional=" + additional +
                "; enabled capabilities use empty shadow maps at zero distance/lights/casters."
            }, required: additional ? new[] { ShaderKeywordStrings.AdditionalLightShadows } : Array.Empty<string>(),
                requireMainLightShadows: main);
        }

        internal static AssetFeatures BuildAreaLightFeatures(bool? supportsAreaLights, string source)
        {
            // This is a build capability, not the current visible-light count.
            // The runtime leaves the asset's keyword ON even when the count is zero.
            const string keyword = "_FRAXINUS_AREA_LIGHTS";
            return new AssetFeatures(source,
                supportsAreaLights == false ? new[] { keyword } : Array.Empty<string>(),
                new[] { "Area-light capability=" + supportsAreaLights + "; " +
                    (supportsAreaLights.HasValue ? "one declared keyword state retained." : "both states retained (unproven).") },
                required: supportsAreaLights == true ? new[] { keyword } : Array.Empty<string>());
        }

        // Pure merge: an unknown/missing configuration contributes an empty set.
        internal static Snapshot Merge(IEnumerable<AssetFeatures> features, bool stripUnused)
        {
            AssetFeatures[] assets = (features ?? Array.Empty<AssetFeatures>()).ToArray();
            bool enabled = stripUnused && assets.Length > 0;
            var forbidden = new HashSet<string>(StringComparer.Ordinal);
            var reasons = new List<string>();
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (enabled)
            {
                forbidden.UnionWith(assets[0]?.ForbiddenGlobalKeywords ?? Array.Empty<string>());
                required.UnionWith(assets[0]?.RequiredGlobalKeywords ?? Array.Empty<string>());
                foreach (var asset in assets.Skip(1))
                {
                    forbidden.IntersectWith(asset?.ForbiddenGlobalKeywords ?? Array.Empty<string>());
                    required.IntersectWith(asset?.RequiredGlobalKeywords ?? Array.Empty<string>());
                }
            }
            else
                reasons.Add(stripUnused ? "No build configurations were available; all variants retained." : "Unused variant stripping is disabled.");
            foreach (var asset in assets)
            {
                if (asset == null)
                    reasons.Add("Missing asset capability record; all variants retained.");
                else reasons.AddRange(asset.Reasons.Select(reason => asset.Source + ": " + reason));
            }
            return new Snapshot(enabled, assets, forbidden, reasons, required);
        }

        internal static Snapshot Gather(IEnumerable<RenderPipelineAsset> assets, bool stripUnused)
        {
            if (!stripUnused)
                return Merge(Array.Empty<AssetFeatures>(), false);
            return Merge((assets ?? Array.Empty<RenderPipelineAsset>()).Distinct().Select(AnalyzeAsset), true);
        }

        internal static Snapshot WithBuildSettings(Snapshot features, bool stripDebug, bool stripScreen)
        {
            if (features == null || !features.CanStrip)
                return features;
            var forbidden = new HashSet<string>(features.ForbiddenGlobalKeywords, StringComparer.Ordinal);
            if (stripDebug)
                forbidden.Add(ShaderKeywordStrings.DEBUG_DISPLAY);
            if (stripScreen)
                forbidden.Add(ShaderKeywordStrings.SCREEN_COORD_OVERRIDE);
            return new Snapshot(true, features.Assets.ToArray(), forbidden, features.Reasons.Concat(new[] {
                "Explicit build settings: strip debug=" + stripDebug + ", strip screen coordinates=" + stripScreen + "."
            }), features.RequiredGlobalKeywords);
        }

        internal static Snapshot GatherForCurrentBuild()
        {
            var assets = new List<RenderPipelineAsset>();
            if (!UnityEditor.Rendering.BuildTargetExtensions.TryGetRenderPipelineAssets(EditorUserBuildSettings.activeBuildTarget, assets))
                return Merge(Array.Empty<AssetFeatures>(), true);
            var settings = GraphicsSettings.GetSettingsForRenderPipeline<UltraRenderPipeline>() as UltraRenderPipelineGlobalSettings;
            var stripping = settings == null ? null : RenderPipelineGraphicsSettingsCompatibility
                .GetRenderPipelineSettings<UltraRenderPipelineShaderStrippingSetting>(settings);
            var core = settings == null ? null : RenderPipelineGraphicsSettingsCompatibility
                .GetRenderPipelineSettings<ShaderStrippingSetting>(settings);
            return WithBuildSettings(Gather(assets, stripping != null && stripping.stripUnusedVariants),
                core != null && core.stripRuntimeDebugShaders, stripping != null && stripping.stripScreenCoordOverrideVariants);
        }

        static AssetFeatures AnalyzeAsset(RenderPipelineAsset pipeline)
        {
            string source = pipeline == null ? "<missing pipeline>" : pipeline.name;
            if (pipeline == null || pipeline.GetType() != typeof(UltraRenderPipelineAsset))
                return new AssetFeatures(source, Array.Empty<string>(), new[] { "Unknown pipeline implementation; all capabilities retained." });
            var asset = (UltraRenderPipelineAsset)pipeline;
            var disabled = DisabledCapabilities.None;
            if (!asset.enableLODCrossFade)
                disabled |= DisabledCapabilities.LODCrossFade;
            if (!asset.supportProbeVolume)
                disabled |= DisabledCapabilities.ProbeVolumes;
            if (!asset.supportAPRTV)
                disabled |= DisabledCapabilities.APRTVolumes;
            if (!asset.useRenderingLayers)
                disabled |= DisabledCapabilities.LightLayers;
            if (!asset.supportsLightCookies)
                disabled |= DisabledCapabilities.LightCookies;
            if (!asset.supportsMixedLighting)
                disabled |= DisabledCapabilities.MixedLighting;
            if (!asset.reflectionProbeBlending)
                disabled |= DisabledCapabilities.ReflectionProbeBlending;
            if (!asset.reflectionProbeBoxProjection)
                disabled |= DisabledCapabilities.ReflectionProbeBoxProjection;
            if (!asset.allowPostProcessAlphaOutput)
                disabled |= DisabledCapabilities.AlphaOutput;
            // Reviewed built-in passes never produce screen-space main shadows.
            // Unknown node/processor implementations retain that provider state.
            bool screenShadowsUnused = UltraRPShaderScreenShadowUsage.TryProveUnused(
                new RenderPipelineAsset[] { asset }, out string screenShadowReason);
            if (screenShadowsUnused)
                disabled |= DisabledCapabilities.ScreenSpaceShadows;
            bool renderingLayersUnused = UltraRPShaderRenderingLayerUsage.TryProveUnused(
                new RenderPipelineAsset[] { asset }, out string renderingLayerReason);
            if (renderingLayersUnused)
                disabled |= DisabledCapabilities.RenderingLayerOutput;

            // Use Fraxinus runtime helpers, not URP's different mobile SH policy.
            ShEvalMode mode = PlatformAutoDetect.ShAutoDetect(asset.shEvalMode);
            bool quality = ShadowUtils.SupportsPerLightSoftShadowQuality();
            bool? forwardPlus = GetForwardPlusRequirement(asset, out string graphReason);
            DBufferModes? dbufferModes = GetDBufferRequirements(asset.rendererGraphList, out string decalReason);
            bool lightingPreserved = UltraRPShaderLightingUsage.TryProvePreserved(asset.rendererGraphList, out string lightingReason);
            var result = BuildAssetFeatures(mode, quality, forwardPlus, source, disabled, dbufferModes,
                asset.reflectionProbeBlending, asset.additionalLightsRenderingMode, lightingPreserved);
            bool knownForwardOnly = forwardPlus == false && UltraRPPostProcessShaderStripper.TryProveForwardOnlyBuild(
                new RenderPipelineAsset[] { asset }, out _, out _);
            var lighting = BuildLightingFeatures(asset.mainLightRenderingMode, asset.supportsMainLightShadows,
                asset.additionalLightsRenderingMode, asset.supportsAdditionalLightShadows, source, knownForwardOnly);
            bool shadowPasses = UltraRPShaderShadowUsage.TryProveRequired(asset.rendererGraphList, out string shadowReason);
            var shadows = BuildShadowFeatures(asset.mainLightRenderingMode, asset.supportsMainLightShadows,
                asset.additionalLightsRenderingMode, asset.supportsAdditionalLightShadows, shadowPasses, source);
            // UltraRenderer prepares this global for every camera, including
            // depth targets. A custom renderer need not execute that setup.
            bool knownAreaSetup = asset.rendererGraphList != null && asset.rendererGraphList.Any() &&
                asset.rendererGraphList.All(graph => graph != null && graph.GetType() == typeof(UltraRendererGraph) &&
                    !SerializationUtility.HasManagedReferencesWithMissingTypes(graph));
            var area = BuildAreaLightFeatures(knownAreaSetup ? asset.supportsAreaLights : (bool?)null, source);
            return new AssetFeatures(source, result.ForbiddenGlobalKeywords.Concat(lighting.ForbiddenGlobalKeywords).Concat(area.ForbiddenGlobalKeywords),
                result.Reasons.Concat(lighting.Reasons).Concat(shadows.Reasons).Concat(area.Reasons).Concat(new[] { graphReason, decalReason, lightingReason, shadowReason,
                    "Screen-space shadows: " + (screenShadowsUnused ? "unused; " : "retained; ") + screenShadowReason,
                    "Rendering-layer output: " + (renderingLayersUnused ? "unused; " : "retained; ") + renderingLayerReason,
                    "Terrain holes: " + (asset.supportsTerrainHoles ? "retained." : "unused; removal requires the Shader terrain-holes declaration.") }),
                !asset.supportsTerrainHoles, result.RequiredGlobalKeywords.Concat(shadows.RequiredGlobalKeywords).Concat(area.RequiredGlobalKeywords),
                result.AllowedLightingStates, shadows.RequireMainLightShadows);
        }

        internal static DBufferModes? GetDBufferRequirements(IEnumerable<ScriptableRendererGraph> graphs, out string reason)
        {
            if (graphs == null)
            {
                reason = "DBuffer: no declared renderer graphs.";
                return null;
            }
            DBufferModes required = DBufferModes.None;
            var graphReasons = new List<string>();
            foreach (var graph in graphs)
            {
                if (graph == null || graph.GetType() != typeof(UltraRendererGraph) ||
                    UnityEditor.SerializationUtility.HasManagedReferencesWithMissingTypes(graph))
                {
                    reason = "DBuffer: missing, derived, or unresolved renderer graph.";
                    return null;
                }
                DBufferModes? modes = GetDBufferRequirements(graph.nodes, out string nodeReason);
                graphReasons.Add(graph.name + ": " + nodeReason);
                if (!modes.HasValue)
                {
                    reason = string.Join("; ", graphReasons);
                    return null;
                }
                required |= modes.Value;
            }
            if (graphReasons.Count == 0)
            {
                reason = "DBuffer: no declared renderer graphs.";
                return null;
            }
            reason = string.Join("; ", graphReasons);
            return required;
        }

        // Inspect decal providers independently of unrelated pass types/rendering
        // modes. Disabled/disconnected providers still count. Like the other build
        // capabilities, runtime-created providers and custom code that writes
        // DBuffer resources/keywords must declare those configurations for the build.
        // No virtual rendering callback or platform-dependent runtime helper runs.
        internal static DBufferModes? GetDBufferRequirements(IEnumerable<object> nodes, out string reason)
        {
            if (nodes == null)
            {
                reason = "DBuffer: missing node list.";
                return null;
            }
            DBufferModes required = DBufferModes.None;
            int nodeCount = 0;
            int providerCount = 0;
            foreach (object node in nodes)
            {
                ++nodeCount;
                if (!(node is ScriptableRenderPassNode))
                {
                    reason = "DBuffer: unknown or missing render pass node.";
                    return null;
                }
                if (!(node is DecalRenderPassNode decal))
                    continue;
                ++providerCount;
                if (decal.GetType() != typeof(DecalRenderPassNode))
                {
                    reason = "DBuffer: derived decal provider " + decal.GetType().FullName;
                    return null;
                }
                var settings = decal.settings;
                if (settings == null)
                {
                    reason = "DBuffer: missing decal settings.";
                    return null;
                }
                switch (settings.technique)
                {
                    case DecalTechniqueOption.Automatic:
                        // GetDBufferSettings creates AlbedoNormalMAOS for Automatic,
                        // ignoring the serialized surfaceData. GetTechnique may
                        // instead choose ScreenSpace/GBuffer, which only adds OFF.
                        required |= DBufferModes.MRT3;
                        break;
                    case DecalTechniqueOption.ScreenSpace:
                        // Deferred changes this to GBuffer, never to DBuffer.
                        break;
                    case DecalTechniqueOption.DBuffer:
                        if (settings.dBufferSettings == null)
                        {
                            reason = "DBuffer: missing explicit DBuffer settings.";
                            return null;
                        }
                        switch (settings.dBufferSettings.surfaceData)
                        {
                            case DecalSurfaceData.Albedo: required |= DBufferModes.MRT1; break;
                            case DecalSurfaceData.AlbedoNormal: required |= DBufferModes.MRT2; break;
                            case DecalSurfaceData.AlbedoNormalMAOS: required |= DBufferModes.MRT3; break;
                            default:
                                reason = "DBuffer: invalid surface-data mode.";
                                return null;
                        }
                        break;
                    default:
                        reason = "DBuffer: invalid decal technique.";
                        return null;
                }
            }
            if (nodeCount == 0)
            {
                reason = "DBuffer: empty graph does not prove provider requirements.";
                return null;
            }
            reason = "DBuffer: audited " + providerCount + " declared decal providers among " +
                nodeCount + " nodes; possible named modes=" + required + "; OFF retained.";
            return required;
        }

        static bool? GetForwardPlusRequirement(UltraRenderPipelineAsset asset, out string reason)
        {
            var graphs = asset.rendererGraphList;
            if (graphs == null || graphs.Length == 0)
            {
                reason = "Forward Plus: no declared renderer graphs.";
                return null;
            }
            bool required = false;
            var graphReasons = new List<string>();
            foreach (var graph in graphs)
            {
                if (graph == null || graph.GetType() != typeof(UltraRendererGraph) ||
                    UnityEditor.SerializationUtility.HasManagedReferencesWithMissingTypes(graph))
                {
                    reason = "Forward Plus: missing, derived, or unresolved renderer graph.";
                    return null;
                }
                bool? uses = GetForwardPlusRequirement(graph.nodes, out string nodeReason);
                graphReasons.Add(graph.name + ": " + nodeReason);
                if (!uses.HasValue)
                {
                    reason = string.Join("; ", graphReasons);
                    return null;
                }
                required |= uses.Value;
            }
            reason = string.Join("; ", graphReasons);
            return required;
        }

        // Inspect the virtual slot used by UltraRenderer.StartRenderGraphRendering,
        // without executing user code. Existing disabled/disconnected nodes count:
        // they can be enabled/reconnected at runtime. Feature-changing mutations
        // that add configurations after the build require those configurations to
        // be declared for the build, as with the other pipeline capability filters.
        internal static bool? GetForwardPlusRequirement(IEnumerable<object> nodes, out string reason)
        {
            if (nodes == null)
            {
                reason = "Missing node list.";
                return null;
            }
            MethodInfo baseSlot = typeof(ScriptableRenderPassNode).GetMethod(nameof(ScriptableRenderPassNode.RequestRenderingMode));
            bool required = false;
            int count = 0;
            foreach (object node in nodes)
            {
                ++count;
                if (!(node is ScriptableRenderPassNode pass))
                {
                    reason = "Unknown or missing render pass node.";
                    return null;
                }
                MethodInfo method = pass.GetType().GetMethod(nameof(ScriptableRenderPassNode.RequestRenderingMode),
                    BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (method == null || !method.IsVirtual || !method.GetBaseDefinition().Equals(baseSlot))
                {
                    reason = "Unrecognized rendering-mode virtual slot: " + pass.GetType().FullName;
                    return null;
                }
                if (method.DeclaringType == typeof(DrawObjectsPass))
                    required |= ((DrawObjectsPass)pass).isForwardPlus;
                else if (method.DeclaringType == typeof(DeferredObjectPass))
                {
                    switch (((DeferredObjectPass)pass).lightingMode)
                    {
                        case DeferredLightingMode.DeferredPlus:
                            required = true;
                            break;
                        case DeferredLightingMode.Stencil:
                        case DeferredLightingMode.GPUCluster:
                            break;
                        default:
                            reason = "Unrecognized deferred lighting mode: " + pass.GetType().FullName;
                            return null;
                    }
                }
                else if (method.DeclaringType != typeof(ScriptableRenderPassNode))
                {
                    reason = "Custom rendering-mode override: " + pass.GetType().FullName;
                    return null;
                }
            }
            if (count == 0)
            {
                reason = "Empty graph does not prove rendering requirements.";
                return null;
            }
            reason = "Audited " + count + " declared RequestRenderingMode slots (base None / DrawObjects / DeferredObject); ForwardPlus=" + required;
            return required;
        }
    }
}
