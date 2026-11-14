using System;
using System.Collections.Generic;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    // Opt-in contract: runtime-created Volume components must also be represented
    // by a VolumeProfile asset, or postprocess effect stripping must remain disabled.
    // Use Core's interfaces explicitly: the source Engine also defines unrelated
    // interfaces with the same short names in this namespace.
    internal sealed class UltraRPPostProcessShaderStripper :
        UnityEditor.Rendering.IShaderVariantStripper,
        UnityEditor.Rendering.IShaderVariantStripperScope
    {
        [Flags]
        internal enum Features
        {
            None = 0,
            LensDistortion = 1 << 0,
            ChromaticAberration = 1 << 1,
            Bloom = 1 << 2,
            Tonemapping = 1 << 3,
            FilmGrain = 1 << 4,
            DepthOfField = 1 << 5,
            MotionBlur = 1 << 6,
            All = (1 << 7) - 1
        }

        internal enum PostShader
        {
            Unowned,
            UberPost,
            Bloom,
            BokehDepthOfField,
            GaussianDepthOfField,
            CameraMotionBlur,
            FinalPost,
            ScalingSetup
        }

        static readonly string[] s_effectKeywords =
        {
            "_DISTORTION", "_CHROMATIC_ABERRATION",
            "_BLOOM_LQ", "_BLOOM_HQ", "_BLOOM_LQ_DIRT", "_BLOOM_HQ_DIRT",
            "_TONEMAP_ACES", "_TONEMAP_NEUTRAL", "_FILM_GRAIN"
        };

        static readonly string[] s_buildGlobalKeywordNames = { "DEBUG_DISPLAY", "SCREEN_COORD_OVERRIDE" };
        static readonly string[] s_buildLocalKeywordNames =
        {
            "HDR_INPUT", "HDR_ENCODING", "HDR_COLORSPACE_CONVERSION", "HDR_COLORSPACE_CONVERSION_AND_ENCODING",
            "_EASU_RCAS_AND_HDR_INPUT", "_GAMMA_20_AND_HDR_INPUT", "_ENABLE_ALPHA_OUTPUT"
        };

        UltraRPBuildData _buildData;
        UltraRPShaderBuildFeatures.Snapshot _shaderFeatures;
        readonly UltraRPShaderVariantRules _ordinaryShaderRules = new UltraRPShaderVariantRules();
        readonly UltraRPShaderPassRules _passRules = new UltraRPShaderPassRules();
        UltraRPShaderPassRules.BuildPolicy _passPolicy;
        Features _supported = Features.All;
        bool _canStrip;
        bool _canStripPostProcessing;
        bool _canStripDeferred;
        bool _isOwnedDeferredShader;
        bool _canStripDeferredPlus;
        bool _isOwnedDeferredPlusShader;
        bool _ownsOutputRules;
        PostShader _shaderKind;
        LocalKeyword[] _keywords;
        ShaderKeyword[] _buildGlobalKeywords;
        bool[] _buildGlobalKeywordPresent;
        LocalKeyword[] _buildLocalKeywords;
        readonly Dictionary<(PassIdentifier, ShaderCompilerPlatform), int> _outputDeclarations = new();
        bool _stripScreenCoordOverride;
        bool _stripRuntimeDebug;
        bool _allowHDROutput;
        bool _allowAlphaOutput = true;
        int _examined;
        int _removed;

        // Core discovers public parameterless constructors, including on internal types.
        public UltraRPPostProcessShaderStripper() { }

        // Core can cache its discovered stripper list across consecutive bundle
        // writes. Register for this pipeline even when the switches are OFF;
        // PrepareBuild rechecks them for each build-session identity.
        public bool active => UltraRPShaderBuildSession.isBuildingForUltraRP;

        static UltraRenderPipelineShaderStrippingSetting GetBuildSettings()
        {
            if (!UltraRPShaderBuildSession.isBuildingForUltraRP)
                return null;

            var globalSettings = GraphicsSettings.GetSettingsForRenderPipeline<UltraRenderPipeline>()
                as UltraRenderPipelineGlobalSettings;
            if (globalSettings == null)
                return null;

            return RenderPipelineGraphicsSettingsCompatibility
                .GetRenderPipelineSettings<UltraRenderPipelineShaderStrippingSetting>(globalSettings);
        }

        void PrepareBuild()
        {
            var buildData = UltraRPBuildData.instance;
            if (ReferenceEquals(_buildData, buildData))
                return;

            _buildData = buildData;
            _shaderFeatures = null;
            _passPolicy = default;
            _supported = Features.All;
            _canStrip = false;
            _canStripPostProcessing = false;
            _canStripDeferred = false;
            _canStripDeferredPlus = false;
            var ultraSettings = GetBuildSettings();
            if (ultraSettings == null)
                return;

            // Build-wide feature proof is independent of postprocess opt-in and
            // is shared with the earlier native keyword prefiltering layer.
            _shaderFeatures = UltraRPShaderBuildFeatures.Gather(
                buildData.allRenderPipelineAssets, ultraSettings.stripUnusedVariants);

            var globalSettings = GraphicsSettings.GetSettingsForRenderPipeline<UltraRenderPipeline>()
                as UltraRenderPipelineGlobalSettings;
            var coreSettings = RenderPipelineGraphicsSettingsCompatibility
                .GetRenderPipelineSettings<ShaderStrippingSetting>(globalSettings);
            // Explicit build settings do not depend on discovering VolumeProfiles.
            // With unused-variant stripping enabled, retain this fallback even if
            // effect stripping is disabled or profile discovery cannot prove usage.
            _stripScreenCoordOverride = ultraSettings.stripScreenCoordOverrideVariants;
            _stripRuntimeDebug = coreSettings != null && coreSettings.stripRuntimeDebugShaders;
            _allowHDROutput = PlayerSettings.allowHDRDisplaySupport;
            _canStrip = ultraSettings.stripUnusedVariants;
            _shaderFeatures = UltraRPShaderBuildFeatures.WithBuildSettings(
                _shaderFeatures, _stripRuntimeDebug, _stripScreenCoordOverride);
            _allowAlphaOutput = !_shaderFeatures.CanStripKeyword(ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT);

            // This decision is independent of VolumeProfiles and postprocess opt-in.
            if (ultraSettings.stripUnusedVariants)
            {
                bool known = TryAnalyzeDeclaredBuild(buildData.allRenderPipelineAssets,
                    out RendererGraphUsage usage, out int graphCount, out string graphReason);
                _canStripDeferred = known && ShouldStripDeferredShader(true, new[] { usage });
                _canStripDeferredPlus = known && ShouldStripDeferredPlusShader(true, new[] { usage });
                _passPolicy = UltraRPShaderPassRules.GatherPolicy(buildData.allRenderPipelineAssets,
                    true, known && usage == RendererGraphUsage.ForwardOnly);
                Debug.Log("UltraRP StencilDeferred stripping: " + (_canStripDeferred ? "remove" : "retain") +
                    ", declared renderer graphs examined: " + graphCount +
                    ". " + graphReason + ". Undeclared runtime graphs or lighting modes require stripUnusedVariants=false.");
            }
            if (!ultraSettings.stripUnusedPostProcessingVariants)
                return;

            if (!TryCollectProjectFeatures(out _supported, out int profileCount, out string reason))
            {
                Debug.LogWarning("UltraRP postprocess stripping skipped: " + reason);
                return;
            }

            // Preserve the postprocess-only opt-in when general stripping is off.
            _canStrip = true;
            _canStripPostProcessing = true;
            Debug.Log("UltraRP postprocess stripping: " + profileCount + " VolumeProfiles, retained effects: " + _supported +
                ". Runtime-only effects must be declared by a VolumeProfile asset or this opt-in setting must be disabled. " +
                "Internal HDR grading, off variants, and all declared Bloom modes remain available. " +
                "Build settings: strip screen coordinates=" + _stripScreenCoordOverride +
                ", strip debug=" + _stripRuntimeDebug + ", allow HDR display=" + _allowHDROutput + ".");
        }

        [Flags]
        internal enum RendererGraphUsage
        {
            ForwardOnly = 0,
            Stencil = 1 << 0,
            Clustered = 1 << 1,
            Unknown = 1 << 2,
            DeferredPlus = 1 << 3,
            Deferred = Stencil | Clustered | DeferredPlus,
        }

        internal static RendererGraphUsage ClassifyRendererGraph(Type graphType, IEnumerable<Type> nodeTypes)
        {
            if (nodeTypes == null)
                return RendererGraphUsage.Unknown;
            var nodes = new List<(Type type, DeferredLightingMode? mode)>();
            foreach (Type type in nodeTypes)
                nodes.Add((type, null));
            return ClassifyRendererGraph(graphType, nodes);
        }

        // Keep disabled and disconnected nodes: scripts may enable or reconnect them.
        // A runtime change to lighting mode must be represented by another declared graph,
        // or unused-variant stripping must remain disabled.
        internal static RendererGraphUsage ClassifyRendererGraphNodes(Type graphType, IEnumerable<FraxinusEngine.Base.BaseNode> nodes)
        {
            if (nodes == null)
                return RendererGraphUsage.Unknown;
            var settings = new List<(Type type, DeferredLightingMode? mode)>();
            foreach (var node in nodes)
                settings.Add((node?.GetType(), node?.GetType() == typeof(DeferredObjectPass)
                    ? (DeferredLightingMode?)((DeferredObjectPass)node).lightingMode : null));
            return ClassifyRendererGraph(graphType, settings);
        }

        static RendererGraphUsage ClassifyRendererGraph(Type graphType, IEnumerable<(Type type, DeferredLightingMode? mode)> nodes)
        {
            if (graphType != typeof(UltraRendererGraph))
                return RendererGraphUsage.Unknown;

            bool anyNode = false;
            bool gbuffer = false;
            var usage = RendererGraphUsage.ForwardOnly;
            foreach (var node in nodes)
            {
                Type type = node.type;
                anyNode = true;
                if (type == typeof(GBufferPassNode))
                {
                    gbuffer = true;
                    continue;
                }
                if (type == typeof(DeferredObjectPass))
                {
                    switch (node.mode)
                    {
                        case DeferredLightingMode.Stencil:
                            usage |= RendererGraphUsage.Stencil;
                            break;
                        case DeferredLightingMode.DeferredPlus:
                            usage |= RendererGraphUsage.DeferredPlus;
                            break;
                        case DeferredLightingMode.GPUCluster:
                            usage |= RendererGraphUsage.Clustered;
                            break;
                        case null:
                            usage |= RendererGraphUsage.Deferred;
                            break;
                        default:
                            return RendererGraphUsage.Unknown;
                    }
                    continue;
                }
                // Exact types only: derived, new built-in, subgraph and custom callback nodes
                // must be reviewed before this built-in pass allowlist is expanded.
                if (type != typeof(RenderDrivenPass) && type != typeof(ContextBeginNode) &&
                    type != typeof(DrawObjectsPass) && type != typeof(ContextEndNode) &&
                    type != typeof(TextureBlitNode) && type != typeof(PostProcessPass) &&
                    type != typeof(MainLightShadowCasterPass) && type != typeof(AdditionalLightsShadowCasterPass) &&
                    type != typeof(ColorGradingLutPass) && type != typeof(DecalRenderPassNode) &&
                    type != typeof(DrawSkyboxPass))
                    return RendererGraphUsage.Unknown;
            }
            // GBuffer without a known lighting consumer is not proof that any
            // deferred implementation is unused.
            return !anyNode ? RendererGraphUsage.Unknown
                : gbuffer && usage == RendererGraphUsage.ForwardOnly ? RendererGraphUsage.Deferred : usage;
        }

        internal static bool ShouldStripDeferredShader(bool stripUnusedVariants, IEnumerable<RendererGraphUsage> graphUsages)
            => CanStripLightingPath(stripUnusedVariants, graphUsages, RendererGraphUsage.Stencil);

        internal static bool ShouldStripClusteredCompute(bool stripUnusedVariants, IEnumerable<RendererGraphUsage> graphUsages)
            => CanStripLightingPath(stripUnusedVariants, graphUsages, RendererGraphUsage.Clustered);

        internal static bool ShouldStripDeferredPlusShader(bool stripUnusedVariants, IEnumerable<RendererGraphUsage> graphUsages)
            => CanStripLightingPath(stripUnusedVariants, graphUsages, RendererGraphUsage.DeferredPlus);

        static bool CanStripLightingPath(bool stripUnusedVariants, IEnumerable<RendererGraphUsage> graphUsages, RendererGraphUsage required)
        {
            if (!stripUnusedVariants || graphUsages == null)
                return false;
            bool anyGraph = false;
            foreach (var usage in graphUsages)
            {
                anyGraph = true;
                if ((usage & ~RendererGraphUsage.Deferred) != 0 || (usage & required) != 0)
                    return false;
            }
            return anyGraph;
        }

        internal static bool AreKnownForwardDrawSettings(DrawObjectsPass.OverrideMaterialMode overrideMode, IEnumerable<string> shaderTags)
        {
            if (overrideMode != DrawObjectsPass.OverrideMaterialMode.None || shaderTags == null)
                return false;
            foreach (string tag in shaderTags)
                if (string.IsNullOrEmpty(tag) || tag.IndexOf("Deferred", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tag.IndexOf("GBuffer", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            return true;
        }

        internal static bool TryProveForwardOnlyBuild(IEnumerable<RenderPipelineAsset> assets, out int graphCount, out string reason)
        {
            return TryProveBuiltInBuild(assets, false, out graphCount, out reason);
        }

        internal static bool TryProveBuiltInBuild(IEnumerable<RenderPipelineAsset> assets, bool allowDeferred, out int graphCount, out string reason)
        {
            if (!TryAnalyzeDeclaredBuild(assets, out RendererGraphUsage usage, out graphCount, out reason))
                return false;
            if (!allowDeferred && usage != RendererGraphUsage.ForwardOnly)
                return ForwardProofFailed(out reason, "Declared renderer graphs require " + usage);
            return true;
        }

        // The entire included pipeline set is the proof boundary, including every
        // non-default renderer graph used by runtime renderer switching.
        internal static bool TryAnalyzeDeclaredBuild(IEnumerable<RenderPipelineAsset> assets,
            out RendererGraphUsage usage, out int graphCount, out string reason)
        {
            usage = RendererGraphUsage.Unknown;
            graphCount = 0;
            reason = null;
            if (assets == null)
                return ForwardProofFailed(out reason, "Pipeline asset collection is missing");
            if (!UltraRPShaderScreenShadowUsage.AreModulesKnown(
                UltraRPShaderScreenShadowUsage.DiscoveredModules(), false, out reason))
                return false;
            var usages = new List<RendererGraphUsage>();
            int unityNullBlitOverrides = 0;
            foreach (var asset in assets)
            {
                if (asset == null)
                    return ForwardProofFailed(out reason, "Pipeline asset is missing");
                if (asset.GetType() != typeof(UltraRenderPipelineAsset))
                    return ForwardProofFailed(out reason, "Unknown pipeline asset type: " + asset.GetType().FullName);
                var graphs = ((UltraRenderPipelineAsset)asset).rendererGraphList;
                if (graphs == null || graphs.Length == 0)
                    return ForwardProofFailed(out reason, "Pipeline has no declared renderer graphs: " + asset.name);
                foreach (var graph in graphs)
                {
                    ++graphCount;
                    if (graph == null)
                        return ForwardProofFailed(out reason, "Missing renderer graph in pipeline: " + asset.name);
                    if (graph.GetType() != typeof(UltraRendererGraph))
                        return ForwardProofFailed(out reason, "Unknown renderer graph type: " + graph.GetType().FullName);
                    if (graph.nodes == null)
                        return ForwardProofFailed(out reason, "Missing node list in graph: " + graph.name);
                    if (UnityEditor.SerializationUtility.HasManagedReferencesWithMissingTypes(graph))
                        return ForwardProofFailed(out reason, "Graph has managed references with missing types: " + graph.name);
                    var nodeTypes = new List<Type>();
                    foreach (var node in graph.nodes)
                    {
                        nodeTypes.Add(node?.GetType());
                        if (node?.GetType() == typeof(DrawObjectsPass))
                        {
                            var field = typeof(DrawObjectsPass).GetField("_overrideMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (field == null || field.FieldType != typeof(DrawObjectsPass.OverrideMaterialMode) ||
                                !AreKnownForwardDrawSettings((DrawObjectsPass.OverrideMaterialMode)field.GetValue(node), ((DrawObjectsPass)node).shaderTagIdStrings))
                                return ForwardProofFailed(out reason, "DrawObjectsPass has an override, deferred/GBuffer tag, or unknown draw settings in graph: " + graph.name);
                        }
                        // Custom blit materials and post-pass extensions may invoke arbitrary shaders.
                        if (node?.GetType() == typeof(TextureBlitNode))
                        {
                            // _blitMaterial is the serialized user override. _defBlitMaterial is
                            // a runtime cache of CoreBlit and must not invalidate forward proof.
                            var field = typeof(TextureBlitNode).GetField("_blitMaterial", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (field == null || field.FieldType != typeof(Material))
                                return ForwardProofFailed(out reason, "TextureBlitNode material field is missing or has an unknown type");
                            object serializedOverride = field.GetValue(node);
                            // Reflection returns object: cast before comparing so a Unity-null
                            // serialized reference is treated as the default material path.
                            Material materialOverride = (Material)serializedOverride;
                            if (materialOverride != null)
                                return ForwardProofFailed(out reason, "Custom TextureBlitNode material in graph: " + graph.name);
                            if (serializedOverride != null)
                                ++unityNullBlitOverrides;
                        }
                        if (node?.GetType() == typeof(PostProcessPass))
                        {
                            var entries = ((PostProcessPass)node).postPassEntries;
                            if (entries == null)
                                return ForwardProofFailed(out reason, "Missing PostProcessPass entry list in graph: " + graph.name);
                            foreach (var entry in entries)
                            {
                                Type type = entry?.passType;
                                if (!IsKnownPostPassType(type))
                                    return ForwardProofFailed(out reason, "Unknown PostProcessPass entry in graph " + graph.name + ": " + (type?.FullName ?? "<missing>"));
                            }
                            // SetupPostPasses also instantiates discovered pass types that were
                            // not serialized in the graph. Their Init/Setup hooks are extensions
                            // even when their newly registered entry starts disabled.
                            foreach (var type in TypeCache.GetTypesDerivedFrom<BasePostPass>())
                                if (!type.IsAbstract && !type.IsInterface && !IsKnownPostPassType(type))
                                    return ForwardProofFailed(out reason, "Unknown registered postprocess pass: " + type.FullName);
                            var effects = ((PostProcessPass)node).uberEffectEntries;
                            if (effects == null)
                                return ForwardProofFailed(out reason, "Missing Uber effect entries in graph: " + graph.name);
                            foreach (var effect in effects)
                            {
                                Type type = effect?.componentType;
                                if (type != typeof(ChromaticAberration) && type != typeof(FilmGrain) &&
                                    type != typeof(LensDistortion) && type != typeof(Vignette))
                                    return ForwardProofFailed(out reason, "Unknown Uber effect in graph " + graph.name + ": " + (type?.FullName ?? "<missing>"));
                            }
                            // A custom processor may replace the handler of a built-in component.
                            foreach (var type in TypeCache.GetTypesDerivedFrom<IUberEffectProcessor>())
                                if (!type.IsAbstract && !type.IsInterface && type != typeof(ChromaticAberrationProcessor) &&
                                    type != typeof(FilmGrainProcessor) && type != typeof(LensDistortionProcessor) && type != typeof(VignetteProcessor))
                                    return ForwardProofFailed(out reason, "Unknown registered Uber effect processor: " + type.FullName);
                        }
                    }
                    var graphUsage = ClassifyRendererGraphNodes(graph.GetType(), graph.nodes);
                    if (graphUsage == RendererGraphUsage.Unknown)
                        return ForwardProofFailed(out reason, "Graph classification is " + graphUsage + " for " + graph.name + ": " +
                            string.Join(", ", nodeTypes.ConvertAll(type => type?.FullName ?? "<missing>")));
                    usages.Add(graphUsage);
                }
            }
            if (usages.Count == 0)
                return ForwardProofFailed(out reason, "No known renderer graphs were declared");
            usage = RendererGraphUsage.ForwardOnly;
            foreach (var graphUsage in usages)
                usage |= graphUsage;
            reason = "All declared graphs use reviewed built-in passes; lighting paths: " + usage
                + "; Unity-null blit overrides: " + unityNullBlitOverrides;
            return true;
        }

        static bool ForwardProofFailed(out string reason, string detail)
        {
            reason = detail;
            return false;
        }

        internal static bool IsKnownPostPassType(Type type)
            => type == typeof(BloomPass) || type == typeof(DepthOfFieldPass) ||
                type == typeof(MotionBlurPass) || type == typeof(AutoExposurePass) ||
                type == typeof(SMAAPass) || type == typeof(TAAPass) ||
                type == typeof(FraxinusEngine.Rendering.Ultra.LensFlare) || type == typeof(PaniniProjectionPass);

        internal static bool IsOwnedDeferredShader(string path, string shaderName)
        {
            return shaderName == "Hidden/Ultra Render Pipeline/StencilDeferred" &&
                string.Equals(path?.Replace('\\', '/'), UltraRenderPipelineAsset.packagePath + "/Shaders/Utils/StencilDeferred.shader", StringComparison.Ordinal);
        }

        internal static bool IsOwnedDeferredPlusShader(string path, string shaderName)
        {
            return shaderName == "Hidden/Ultra Render Pipeline/DeferredPlus" &&
                string.Equals(path?.Replace('\\', '/'), UltraRenderPipelineAsset.packagePath + "/Shaders/Lighting/DeferredPlus.shader", StringComparison.Ordinal);
        }

        internal static bool TryCollectProjectFeatures(out Features supported, out int profileCount, out string reason)
        {
            supported = Features.All;
            profileCount = 0;
            reason = null;
            var profiles = new List<VolumeProfile>();
            foreach (string guid in AssetDatabase.FindAssets("t:VolumeProfile"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
                if (profile == null || profile.components == null || profile.components.Exists(component => component == null))
                {
                    reason = "VolumeProfile could not be fully read: " + path;
                    return false;
                }
                profiles.Add(profile);
            }

            if (profiles.Count == 0)
            {
                reason = "no VolumeProfile assets were found.";
                return false;
            }

            profileCount = profiles.Count;
            supported = CollectFeatures(profiles);
            return true;
        }

        internal static Features CollectFeatures(IEnumerable<VolumeProfile> profiles)
        {
            Features supported = Features.None;
            foreach (var profile in profiles)
            {
                // Like URP, presence is sufficient. Do not remove a component's
                // variants merely because it is disabled or currently has zero intensity.
                if (profile.Has<LensDistortion>()) supported |= Features.LensDistortion;
                if (profile.Has<ChromaticAberration>()) supported |= Features.ChromaticAberration;
                // Screen-space flares use the Bloom bright filter even with Bloom disabled.
                if (profile.Has<Bloom>() || profile.Has<ScreenSpaceLensFlare>()) supported |= Features.Bloom;
                if (profile.Has<Tonemapping>()) supported |= Features.Tonemapping;
                if (profile.Has<FilmGrain>()) supported |= Features.FilmGrain;
                if (profile.Has<DepthOfField>()) supported |= Features.DepthOfField;
                if (profile.Has<MotionBlur>()) supported |= Features.MotionBlur;
            }
            return supported;
        }

        internal static PostShader IdentifyShader(string path, string shaderName)
        {
            const string prefix = "Hidden/Ultra Render Pipeline/";
            if (string.IsNullOrEmpty(shaderName) || !shaderName.StartsWith(prefix, StringComparison.Ordinal))
                return PostShader.Unowned;

            string name = shaderName.Substring(prefix.Length);
            // ScalingSetup's shader display name contains a space; its filename does not.
            if (name == "Scaling Setup") name = nameof(PostShader.ScalingSetup);
            if (!Enum.TryParse(name, out PostShader kind) || kind == PostShader.Unowned || name != kind.ToString())
                return PostShader.Unowned;

            string expected = UltraRenderPipelineAsset.packagePath + "/Shaders/PostProcessing/" + name + ".shader";
            return string.Equals(path?.Replace('\\', '/'), expected, StringComparison.Ordinal) ? kind : PostShader.Unowned;
        }

        internal static bool IsOwnedOutputShader(string path, string shaderName)
        {
            if (IdentifyShader(path, shaderName) != PostShader.Unowned) return true;
            string relative;
            switch (shaderName)
            {
                case "Hidden/Ultra Render Pipeline/LutBuilderHdr": relative = "PostProcessing/LutBuilderHdr.shader"; break;
                case "Hidden/Fraxinus/BlitHDROverlay": relative = "Utils/BlitHDROverlay.shader"; break;
                case "Hidden/Fraxinus/HDRDebugView": relative = "Debug/HDRDebugView.shader"; break;
                case "Hidden/Ultra Render Pipeline/XR/XRMirrorView": relative = "XR/XRMirrorView.shader"; break;
                default: return false;
            }
            return string.Equals(path?.Replace('\\', '/'), UltraRenderPipelineAsset.packagePath + "/Shaders/" + relative,
                StringComparison.Ordinal);
        }

        internal static bool DeclaresOutputKeyword(Shader shader, PassIdentifier pass, ShaderType stage,
            ShaderCompilerPlatform platform, string name)
        {
            if (shader == null || !UltraRPShaderPassRules.IncludesFragmentProgram(stage, platform)) return false;
            var keyword = shader.keywordSpace.FindKeyword(name);
            return keyword.isValid && ShaderUtil.PassHasKeyword(shader, pass, keyword, ShaderType.Fragment, platform);
        }

        int GetOutputDeclarations(Shader shader, ShaderSnippetData snippet, ShaderCompilerPlatform platform)
        {
            var key = (snippet.pass, platform);
            if (_outputDeclarations.TryGetValue(key, out int mask)) return mask;
            for (int i = 0; i < s_buildGlobalKeywordNames.Length; ++i)
                if (DeclaresOutputKeyword(shader, snippet.pass, ShaderType.Fragment, platform, s_buildGlobalKeywordNames[i])) mask |= 1 << i;
            for (int i = 0; i < s_buildLocalKeywordNames.Length; ++i)
                if (DeclaresOutputKeyword(shader, snippet.pass, ShaderType.Fragment, platform, s_buildLocalKeywordNames[i]))
                    mask |= 1 << (i + s_buildGlobalKeywordNames.Length);
            _outputDeclarations.Add(key, mask);
            return mask;
        }

        internal static bool ShouldRemoveKeyword(Features supported, string keyword)
        {
            Features required;
            switch (keyword)
            {
                case "_DISTORTION": required = Features.LensDistortion; break;
                case "_CHROMATIC_ABERRATION": required = Features.ChromaticAberration; break;
                case "_BLOOM_LQ":
                case "_BLOOM_HQ":
                case "_BLOOM_LQ_DIRT":
                case "_BLOOM_HQ_DIRT": required = Features.Bloom; break;
                case "_TONEMAP_ACES":
                case "_TONEMAP_NEUTRAL": required = Features.Tonemapping; break;
                case "_FILM_GRAIN": required = Features.FilmGrain; break;
                default: return false;
            }
            return (supported & required) == 0;
        }

        internal static bool ShouldRemoveBuildKeyword(string keyword, bool stripScreenCoordOverride,
            bool stripRuntimeDebug, bool allowHDROutput, bool allowAlphaOutput = true)
        {
            switch (keyword)
            {
                case "SCREEN_COORD_OVERRIDE": return stripScreenCoordOverride;
                case "DEBUG_DISPLAY": return stripRuntimeDebug;
                case "_ENABLE_ALPHA_OUTPUT": return !allowAlphaOutput;
                case "HDR_INPUT":
                case "HDR_ENCODING":
                case "HDR_COLORSPACE_CONVERSION":
                case "HDR_COLORSPACE_CONVERSION_AND_ENCODING":
                case "_EASU_RCAS_AND_HDR_INPUT":
                case "_GAMMA_20_AND_HDR_INPUT": return !allowHDROutput;
                default: return false;
            }
        }

        internal static bool ShouldRemoveWholeShader(Features supported, PostShader shader)
        {
            switch (shader)
            {
                case PostShader.Bloom: return (supported & Features.Bloom) == 0;
                case PostShader.BokehDepthOfField:
                case PostShader.GaussianDepthOfField: return (supported & Features.DepthOfField) == 0;
                case PostShader.CameraMotionBlur: return (supported & Features.MotionBlur) == 0;
                default: return false;
            }
        }

        public void BeforeShaderStripping(Shader shader)
        {
            PrepareBuild();
            _ordinaryShaderRules.BeforeShaderStripping(shader,
                UltraRPShaderBuildSession.isBuildingForUltraRP && _shaderFeatures != null && _shaderFeatures.CanStrip,
                _shaderFeatures?.ForbiddenGlobalKeywords, _shaderFeatures?.RequiredGlobalKeywords,
                _shaderFeatures?.AllowedLightingStates ?? UltraRPShaderBuildFeatures.AllLightingStates,
                _shaderFeatures?.RequireMainLightShadows ?? false);
            _passRules.BeforeShaderStripping(shader, _passPolicy);
            _examined = 0;
            _removed = 0;
            string path = AssetDatabase.GetAssetPath(shader);
            _shaderKind = IdentifyShader(path, shader.name);
            _ownsOutputRules = IsOwnedOutputShader(path, shader.name);
            _isOwnedDeferredShader = IsOwnedDeferredShader(path, shader.name);
            _isOwnedDeferredPlusShader = IsOwnedDeferredPlusShader(path, shader.name);
            _keywords = null;
            _buildGlobalKeywords = null;
            _buildGlobalKeywordPresent = null;
            _buildLocalKeywords = null;
            _outputDeclarations.Clear();
            if (_canStrip && _ownsOutputRules)
            {
                _buildGlobalKeywords = new ShaderKeyword[s_buildGlobalKeywordNames.Length];
                _buildGlobalKeywordPresent = new bool[s_buildGlobalKeywordNames.Length];
                for (int i = 0; i < _buildGlobalKeywords.Length; ++i)
                {
                    string name = s_buildGlobalKeywordNames[i];
                    _buildGlobalKeywords[i] = new ShaderKeyword(name);
                    _buildGlobalKeywordPresent[i] = shader.keywordSpace.FindKeyword(name).isValid;
                }
                _buildLocalKeywords = new LocalKeyword[s_buildLocalKeywordNames.Length];
                for (int i = 0; i < _buildLocalKeywords.Length; ++i)
                    _buildLocalKeywords[i] = shader.keywordSpace.FindKeyword(s_buildLocalKeywordNames[i]);
            }
            if (_canStripPostProcessing && _shaderKind == PostShader.UberPost)
            {
                _keywords = new LocalKeyword[s_effectKeywords.Length];
                for (int i = 0; i < _keywords.Length; ++i)
                    _keywords[i] = shader.keywordSpace.FindKeyword(s_effectKeywords[i]);
            }
        }

        public bool CanRemoveVariant(Shader shader, ShaderSnippetData snippet, ShaderCompilerData data)
        {
            // Core uses All() across registered strippers; ordinary and postprocess
            // rules are OR-composed here inside the same registered implementation.
            if (_ordinaryShaderRules.CanRemoveVariant(shader, snippet, data))
                return RecordDecision(true, _ordinaryShaderRules.lastRemovalReason == UltraRPShaderVariantRules.RemovalReason.LightingCombination ?
                    UltraRPShaderBuildReport.Decision.LightingCombination : _ordinaryShaderRules.lastRemovalWasRequired ?
                    UltraRPShaderBuildReport.Decision.RequiredGlobal : UltraRPShaderBuildReport.Decision.ForbiddenGlobal);
            if (_passRules.CanRemoveVariant(shader, snippet, data))
            {
                var reason = _passRules.lastRemovalReason;
                bool wholePass = reason == UltraRPShaderPassRules.RemovalReason.UnusedGBufferPass ||
                    reason == UltraRPShaderPassRules.RemovalReason.UnusedShadowCasterPass ||
                    reason == UltraRPShaderPassRules.RemovalReason.UnusedMetaPass ||
                    reason == UltraRPShaderPassRules.RemovalReason.EditorOnlyPass;
                return RecordDecision(true, wholePass ? UltraRPShaderBuildReport.Decision.UnusedPass :
                    UltraRPShaderBuildReport.Decision.InvalidCombination);
            }
            if (_isOwnedDeferredShader)
            {
                ++_examined;
                if (_canStripDeferred) ++_removed;
                return RecordDecision(_canStripDeferred, UltraRPShaderBuildReport.Decision.DeferredPath);
            }

            if (_isOwnedDeferredPlusShader)
            {
                ++_examined;
                if (_canStripDeferredPlus) ++_removed;
                return RecordDecision(_canStripDeferredPlus, UltraRPShaderBuildReport.Decision.DeferredPath);
            }
            if (!_canStrip || !_ownsOutputRules)
                return RecordDecision(false, UltraRPShaderBuildReport.Decision.Retained);

            ++_examined;
            bool remove = _canStripPostProcessing && ShouldRemoveWholeShader(_supported, _shaderKind);
            var decision = UltraRPShaderBuildReport.Decision.PostEffect;
            bool includesFragment = UltraRPShaderPassRules.IncludesFragmentProgram(snippet.shaderType, data.shaderCompilerPlatform);
            if (!remove && includesFragment)
            {
                int declarations = GetOutputDeclarations(shader, snippet, data.shaderCompilerPlatform);
                // DEBUG_DISPLAY/SCREEN_COORD_OVERRIDE are global multi_compile keywords.
                // HDR output keywords are declared multi_compile_local in these shaders.
                for (int i = 0; i < _buildGlobalKeywords.Length && !remove; ++i)
                    remove = (declarations & (1 << i)) != 0 && _buildGlobalKeywordPresent[i] && data.shaderKeywordSet.IsEnabled(_buildGlobalKeywords[i]) &&
                        ShouldRemoveBuildKeyword(s_buildGlobalKeywordNames[i], _stripScreenCoordOverride, _stripRuntimeDebug, _allowHDROutput);
                for (int i = 0; i < _buildLocalKeywords.Length && !remove; ++i)
                    remove = (declarations & (1 << (i + s_buildGlobalKeywordNames.Length))) != 0 && _buildLocalKeywords[i].isValid && data.shaderKeywordSet.IsEnabled(_buildLocalKeywords[i]) &&
                        ShouldRemoveBuildKeyword(s_buildLocalKeywordNames[i], _stripScreenCoordOverride, _stripRuntimeDebug, _allowHDROutput, _allowAlphaOutput);
                if (remove) decision = UltraRPShaderBuildReport.Decision.OutputSettings;
            }
            if (!remove && _canStripPostProcessing && _shaderKind == PostShader.UberPost && includesFragment)
            {
                for (int i = 0; i < _keywords.Length; ++i)
                {
                    if (_keywords[i].isValid && data.shaderKeywordSet.IsEnabled(_keywords[i]) &&
                        ShouldRemoveKeyword(_supported, s_effectKeywords[i]))
                    {
                        remove = true;
                        break;
                    }
                }
            }
            if (remove) ++_removed;
            return RecordDecision(remove, decision);
        }

        static bool RecordDecision(bool removed, UltraRPShaderBuildReport.Decision reason)
        {
            UltraRPShaderBuildReport.RecordDecision(removed ? reason : UltraRPShaderBuildReport.Decision.Retained);
            return removed;
        }

        public void AfterShaderStripping(Shader shader)
        {
            _ordinaryShaderRules.AfterShaderStripping(shader);
            _passRules.AfterShaderStripping(shader);
            if (_removed > 0)
                Debug.Log("UltraRP shader stripping: " + shader.name + ", removed " + _removed + " / " + _examined + " variants.");
        }
    }
}
