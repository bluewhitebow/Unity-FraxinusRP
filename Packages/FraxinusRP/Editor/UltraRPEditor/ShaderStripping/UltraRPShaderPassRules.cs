using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    // Composed into the existing Core stripper: a separate registered stripper
    // would be AND-combined by Core and could veto the existing feature rules.
    internal sealed class UltraRPShaderPassRules
    {
        // Independent author declarations. A custom shader may opt into one
        // semantic contract without authorizing any unrelated pass removal.
        internal const string TerrainHolesTag = "FraxinusTerrainHoles";
        internal const string ShadowRulesTag = "FraxinusShadowRules";
        internal const string PassRulesTag = "FraxinusPassRules";
        internal const string LightmapRulesTag = "FraxinusLightmapRules";

        [Flags]
        internal enum Capabilities
        {
            None = 0,
            TerrainHoles = 1 << 0,
            ShadowCombinations = 1 << 1,
            StandardPasses = 1 << 2,
            LightmapCombinations = 1 << 3,
            All = TerrainHoles | ShadowCombinations | StandardPasses | LightmapCombinations
        }

        [Flags]
        internal enum Keywords
        {
            None = 0,
            Main = 1 << 0,
            Cascade = 1 << 1,
            Screen = 1 << 2,
            Additional = 1 << 3,
            AdditionalShadows = 1 << 4,
            ForwardPlus = 1 << 5,
            Soft = 1 << 6,
            SoftLow = 1 << 7,
            SoftMedium = 1 << 8,
            SoftHigh = 1 << 9,
            PunctualCaster = 1 << 10,
            AlphaTest = 1 << 11,
            DirectionalLightmap = 1 << 12,
            Lightmap = 1 << 13,
            DynamicLightmap = 1 << 14
        }

        internal struct BuildPolicy
        {
            public bool enabled;
            public bool forwardOnly;
            public bool mainShadowsUnused;
            public bool additionalShadowsUnused;
            public bool terrainHolesUnused;
            public bool stripMeta;
        }

        internal enum RemovalReason
        {
            None,
            UnusedGBufferPass,
            UnusedShadowCasterPass,
            UnusedMetaPass,
            EditorOnlyPass,
            TerrainHoles,
            UnusedShadowCasterLightType,
            SoftShadowsWithoutShadows,
            AdditionalShadowsWithoutLights,
            DirectionalLightmapWithoutLightmap
        }

        internal RemovalReason lastRemovalReason { get; private set; }

        static readonly string[] s_keywordNames =
        {
            "_MAIN_LIGHT_SHADOWS", "_MAIN_LIGHT_SHADOWS_CASCADE", "_MAIN_LIGHT_SHADOWS_SCREEN",
            "_ADDITIONAL_LIGHTS", "_ADDITIONAL_LIGHT_SHADOWS", "_FORWARD_PLUS",
            "_SHADOWS_SOFT", "_SHADOWS_SOFT_LOW", "_SHADOWS_SOFT_MEDIUM", "_SHADOWS_SOFT_HIGH",
            "_CASTING_PUNCTUAL_LIGHT_SHADOW", "_ALPHATEST_ON",
            "DIRLIGHTMAP_COMBINED", "LIGHTMAP_ON", "DYNAMICLIGHTMAP_ON"
        };
        static readonly Dictionary<string, string> s_auditedShaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Ultra Render Pipeline/Lit", "Lit.shader" },
            { "Ultra Render Pipeline/Simple Lit", "SimpleLit.shader" },
            { "Ultra Render Pipeline/Particles/Lit", "Particles/ParticlesLit.shader" },
            { "Ultra Render Pipeline/Particles/Simple Lit", "Particles/ParticlesSimpleLit.shader" },
            { "Ultra Render Pipeline/Terrain/Lit", "Terrain/TerrainLit.shader" },
            { "Hidden/Ultra Render Pipeline/Terrain/Lit (Add Pass)", "Terrain/TerrainLitAdd.shader" },
            { "Hidden/Ultra Render Pipeline/Terrain/Lit (Base Pass)", "Terrain/TerrainLitBase.shader" }
        };
        static readonly ShaderTagId s_pipelineTag = new ShaderTagId("RenderPipeline");
        static readonly ShaderTagId s_lightModeTag = new ShaderTagId("LightMode");
        static readonly ShaderTagId s_terrainHolesTag = new ShaderTagId(TerrainHolesTag);
        static readonly ShaderTagId s_shadowRulesTag = new ShaderTagId(ShadowRulesTag);
        static readonly ShaderTagId s_passRulesTag = new ShaderTagId(PassRulesTag);
        static readonly ShaderTagId s_lightmapRulesTag = new ShaderTagId(LightmapRulesTag);
        const Keywords MainShadows = Keywords.Main | Keywords.Cascade | Keywords.Screen;
        const Keywords SoftShadows = Keywords.Soft | Keywords.SoftLow | Keywords.SoftMedium | Keywords.SoftHigh;

        Shader _shader;
        BuildPolicy _policy;
        LocalKeyword[] _keywords;
        readonly Dictionary<PassIdentifier, string> _passModes = new Dictionary<PassIdentifier, string>();
        readonly Dictionary<PassIdentifier, Capabilities> _passCapabilities = new Dictionary<PassIdentifier, Capabilities>();
        readonly Dictionary<(PassIdentifier, ShaderType, ShaderCompilerPlatform), Keywords> _declarations =
            new Dictionary<(PassIdentifier, ShaderType, ShaderCompilerPlatform), Keywords>();
        int _examined;
        int _removed;

        internal static BuildPolicy GatherPolicy(IEnumerable<RenderPipelineAsset> assets, bool stripUnused, bool knownForwardOnly)
        {
            if (!stripUnused || assets == null)
                return default;
            var policy = new BuildPolicy
            {
                enabled = true,
                forwardOnly = knownForwardOnly,
                mainShadowsUnused = true,
                additionalShadowsUnused = true,
                terrainHolesUnused = true
            };
            int count = 0;
            foreach (var pipeline in assets)
            {
                if (pipeline == null || pipeline.GetType() != typeof(UltraRenderPipelineAsset))
                    return default;
                ++count;
                var asset = (UltraRenderPipelineAsset)pipeline;
                policy.mainShadowsUnused &= asset.mainLightRenderingMode == LightRenderingMode.Disabled || !asset.supportsMainLightShadows;
                // Forward+ can create additional shadow data even when the
                // asset's ordinary additional-light mode is Disabled.
                policy.additionalShadowsUnused &= !asset.supportsAdditionalLightShadows;
                policy.terrainHolesUnused &= !asset.supportsTerrainHoles;
            }
            if (count == 0)
                return default;
            // Meta remains necessary for Enlighten precomputed realtime GI.
            policy.stripMeta = GraphicsSettings.currentRenderPipeline != null &&
                GraphicsSettings.currentRenderPipeline.GetType() == typeof(UltraRenderPipelineAsset) &&
                (!SupportedRenderingFeatures.active.enlighten ||
                 (SupportedRenderingFeatures.active.lightmapBakeTypes & LightmapBakeType.Realtime) == 0);
            return policy;
        }

        internal static bool ShouldRemovePass(BuildPolicy policy, bool audited, string lightMode)
            => ShouldRemovePass(policy, LegacyCapabilities(audited, false), lightMode);

        internal static bool ShouldRemovePass(BuildPolicy policy, Capabilities capabilities, string lightMode)
            => GetPassRemovalReason(policy, capabilities, lightMode) != RemovalReason.None;

        internal static RemovalReason GetPassRemovalReason(BuildPolicy policy, bool audited, string lightMode)
            => GetPassRemovalReason(policy, LegacyCapabilities(audited, false), lightMode);

        internal static RemovalReason GetPassRemovalReason(BuildPolicy policy, Capabilities capabilities, string lightMode)
        {
            if (!policy.enabled || (capabilities & Capabilities.StandardPasses) == 0)
                return RemovalReason.None;
            switch (lightMode)
            {
                case "UniversalGBuffer": return policy.forwardOnly ? RemovalReason.UnusedGBufferPass : RemovalReason.None;
                case "ShadowCaster": return policy.forwardOnly && policy.mainShadowsUnused && policy.additionalShadowsUnused ? RemovalReason.UnusedShadowCasterPass : RemovalReason.None;
                case "Meta": return policy.stripMeta ? RemovalReason.UnusedMetaPass : RemovalReason.None;
                case "SceneSelectionPass":
                case "Picking": return RemovalReason.EditorOnlyPass;
                default: return RemovalReason.None;
            }
        }

        internal static bool ShouldRemoveVariant(BuildPolicy policy, bool audited, bool terrain,
            string lightMode, ShaderType stage, Keywords declared, Keywords enabled)
            => ShouldRemoveVariant(policy, LegacyCapabilities(audited, terrain), lightMode, stage, declared, enabled);

        internal static bool ShouldRemoveVariant(BuildPolicy policy, Capabilities capabilities,
            string lightMode, ShaderType stage, Keywords declared, Keywords enabled)
            => GetVariantRemovalReason(policy, capabilities, lightMode, stage, declared, enabled) != RemovalReason.None;

        internal static bool IncludesFragmentProgram(ShaderType stage, ShaderCompilerPlatform platform)
            => stage == ShaderType.Fragment || stage == ShaderType.Vertex &&
                (platform == ShaderCompilerPlatform.Vulkan || platform == ShaderCompilerPlatform.GLES3x ||
                 platform == ShaderCompilerPlatform.OpenGLCore);

        internal static RemovalReason GetVariantRemovalReason(BuildPolicy policy, bool audited, bool terrain,
            string lightMode, ShaderType stage, Keywords declared, Keywords enabled)
            => GetVariantRemovalReason(policy, LegacyCapabilities(audited, terrain), lightMode, stage, declared, enabled);

        internal static RemovalReason GetVariantRemovalReason(BuildPolicy policy, Capabilities capabilities,
            string lightMode, ShaderType stage, Keywords declared, Keywords enabled)
        {
            if (!policy.enabled)
                return RemovalReason.None;
            // The caller queries fragment declarations separately for backends
            // whose Vertex callback contains the combined vertex/fragment program.
            // Intersect enabled with the exact pass/stage/platform declarations.
            enabled &= declared;
            if ((capabilities & Capabilities.TerrainHoles) != 0 && policy.terrainHolesUnused && stage == ShaderType.Fragment &&
                (enabled & Keywords.AlphaTest) != 0) return RemovalReason.TerrainHoles;
            // The Standard lightmap contract uses directionality only while a
            // static or dynamic lightmap is sampled. Require all three actual
            // declarations: a missing dynamic axis is not proof that it is OFF.
            const Keywords lightmapControls = Keywords.DirectionalLightmap | Keywords.Lightmap | Keywords.DynamicLightmap;
            if ((capabilities & Capabilities.LightmapCombinations) != 0 && stage == ShaderType.Fragment &&
                (declared & lightmapControls) == lightmapControls &&
                (enabled & lightmapControls) == Keywords.DirectionalLightmap)
                return RemovalReason.DirectionalLightmapWithoutLightmap;
            if ((capabilities & Capabilities.StandardPasses) != 0 && lightMode == "ShadowCaster" && stage == ShaderType.Vertex && policy.forwardOnly &&
                (declared & Keywords.PunctualCaster) != 0)
            {
                bool punctual = (enabled & Keywords.PunctualCaster) != 0;
                return (punctual ? policy.additionalShadowsUnused : policy.mainShadowsUnused) ? RemovalReason.UnusedShadowCasterLightType : RemovalReason.None;
            }
            if ((capabilities & Capabilities.ShadowCombinations) == 0 ||
                stage != ShaderType.Fragment || lightMode != "UltraForward") return RemovalReason.None;

            const Keywords allShadowControls = MainShadows | Keywords.AdditionalShadows;
            if ((declared & allShadowControls) == allShadowControls &&
                (enabled & SoftShadows) != 0 && (enabled & allShadowControls) == 0) return RemovalReason.SoftShadowsWithoutShadows;

            // In deferred rendering a forward-only object can observe the
            // deferred camera's lighting globals. Keep those combinations.
            const Keywords additionalControls = Keywords.Additional | Keywords.ForwardPlus | Keywords.AdditionalShadows;
            return policy.forwardOnly && (declared & additionalControls) == additionalControls &&
                (enabled & Keywords.AdditionalShadows) != 0 &&
                (enabled & (Keywords.Additional | Keywords.ForwardPlus)) == 0 ? RemovalReason.AdditionalShadowsWithoutLights : RemovalReason.None;
        }

        internal static Capabilities LegacyCapabilities(bool audited, bool terrain)
            // The old bool API cannot prove which shader's lightmap code was
            // audited. Preserve its original contracts when adding new flags.
            => !audited ? Capabilities.None : Capabilities.ShadowCombinations | Capabilities.StandardPasses |
                (terrain ? Capabilities.TerrainHoles : Capabilities.None);

        internal static Capabilities ResolveCapabilities(string pipelineTag, string terrainHolesTag,
            string shadowRulesTag, string passRulesTag, Capabilities legacy = Capabilities.None)
            => ResolveCapabilities(pipelineTag, terrainHolesTag, shadowRulesTag, passRulesTag, null, legacy);

        internal static Capabilities ResolveCapabilities(string pipelineTag, string terrainHolesTag,
            string shadowRulesTag, string passRulesTag, string lightmapRulesTag, Capabilities legacy = Capabilities.None)
        {
            if (!string.Equals(pipelineTag, "UltraPipeline", StringComparison.Ordinal))
                return Capabilities.None;
            // Tags belong to SubShader, matching native keyword prefilter scope.
            // A missing tag preserves the legacy audited contract. Any explicit
            // unrecognized value, including False/None, disables only that rule.
            Capabilities result = legacy & Capabilities.All;
            // Unity 2022 imports boolean tag values such as "True" as "true".
            // Keep that canonicalization local to the boolean contract; unknown
            // values and the named Standard contracts remain strict.
            ResolveCapability(ref result, Capabilities.TerrainHoles, terrainHolesTag?.ToLowerInvariant(), "true");
            ResolveCapability(ref result, Capabilities.ShadowCombinations, shadowRulesTag, "Standard");
            ResolveCapability(ref result, Capabilities.StandardPasses, passRulesTag, "Standard");
            ResolveCapability(ref result, Capabilities.LightmapCombinations, lightmapRulesTag, "Standard");
            return result;
        }

        static void ResolveCapability(ref Capabilities result, Capabilities capability, string value, string supported)
        {
            if (string.IsNullOrEmpty(value))
                return;
            result &= ~capability;
            if (string.Equals(value, supported, StringComparison.Ordinal))
                result |= capability;
        }

        internal static bool EquivalentRecoveredSource(string canonical, string recovered)
        {
            if (string.IsNullOrEmpty(canonical) || string.IsNullOrEmpty(recovered))
                return false;
            // Preserve every pragma, include, pass and HLSL line. Only the
            // recovered display name and newline convention may differ.
            const string declaration = "(?m)^Shader\\s+\"[^\"]+\"";
            return string.Equals(Regex.Replace(canonical.Replace("\r\n", "\n"), declaration, "Shader \"_\""),
                Regex.Replace(recovered.Replace("\r\n", "\n"), declaration, "Shader \"_\""), StringComparison.Ordinal);
        }

        internal static bool IsAuditedShader(Shader shader, out bool terrain)
            => IsAuditedShader(shader, out terrain, out _);

        internal static bool IsAuditedShader(Shader shader, out bool terrain, out bool lightmaps)
        {
            terrain = false;
            lightmaps = false;
            if (shader == null)
                return false;
            string canonicalName = shader.name.Replace("Universal Render Pipeline/", "Ultra Render Pipeline/");
            if (!s_auditedShaders.TryGetValue(canonicalName, out string relativePath))
                return false;
            string canonicalPath = UltraRenderPipelineAsset.packagePath + "/Shaders/" + relativePath;
            string path = AssetDatabase.GetAssetPath(shader).Replace('\\', '/');
            bool audited = path == canonicalPath;
            if (!audited && path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                var canonical = AssetDatabase.LoadAssetAtPath<Shader>(canonicalPath);
                if (canonical == null)
                    return false;
                string diskPath = canonicalPath;
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(canonicalPath);
                if (package != null)
                    diskPath = Path.Combine(package.resolvedPath, "Shaders", relativePath);
                try
                {
                    audited = EquivalentRecoveredSource(File.ReadAllText(diskPath), File.ReadAllText(path));
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    // Unreadable recovered sources cannot prove the stripping contract.
                    return false;
                }
            }
            terrain = audited && relativePath.StartsWith("Terrain/", StringComparison.Ordinal);
            // Particles and other legacy audited shaders do not inherit a new
            // lighting contract just because they share shadow/pass semantics.
            lightmaps = audited && (terrain || relativePath == "Lit.shader");
            return audited;
        }

        internal void BeforeShaderStripping(Shader shader, BuildPolicy policy)
        {
            Clear();
            if (!policy.enabled || shader == null)
                return;
            bool audited = IsAuditedShader(shader, out bool terrain, out bool lightmaps);
            Capabilities legacy = LegacyCapabilities(audited, terrain);
            if (lightmaps)
                legacy |= Capabilities.LightmapCombinations;
            var data = ShaderUtil.GetShaderData(shader);
            if (data == null)
                return;
            for (int subshaderIndex = 0; subshaderIndex < data.SerializedSubshaderCount; ++subshaderIndex)
            {
                var subshader = data.GetSerializedSubshader(subshaderIndex);
                if (subshader == null)
                    continue;
                Capabilities capabilities = ResolveCapabilities(subshader.FindTagValue(s_pipelineTag).name,
                    subshader.FindTagValue(s_terrainHolesTag).name, subshader.FindTagValue(s_shadowRulesTag).name,
                    subshader.FindTagValue(s_passRulesTag).name, subshader.FindTagValue(s_lightmapRulesTag).name, legacy);
                if (capabilities == Capabilities.None)
                    continue;
                for (int passIndex = 0; passIndex < subshader.PassCount; ++passIndex)
                {
                    var pass = subshader.GetPass(passIndex);
                    if (pass == null)
                        continue;
                    var identifier = new PassIdentifier((uint)subshaderIndex, (uint)passIndex);
                    _passModes[identifier] = pass.FindTagValue(s_lightModeTag).name;
                    _passCapabilities[identifier] = capabilities;
                }
            }
            if (_passCapabilities.Count == 0)
                return;
            _shader = shader;
            _policy = policy;
            _keywords = new LocalKeyword[s_keywordNames.Length];
            for (int i = 0; i < _keywords.Length; ++i)
                _keywords[i] = shader.keywordSpace.FindKeyword(s_keywordNames[i]);
        }

        internal Capabilities GetPassCapabilities(Shader shader, PassIdentifier pass)
            => shader != null && shader == _shader && _passCapabilities.TryGetValue(pass, out Capabilities capabilities)
                ? capabilities : Capabilities.None;

        internal Keywords GetDeclaredKeywords(Shader shader, PassIdentifier pass, ShaderType stage, ShaderCompilerPlatform platform)
        {
            if (shader == null || shader != _shader || !_passModes.ContainsKey(pass))
                return Keywords.None;
            var key = (pass, stage, platform);
            if (_declarations.TryGetValue(key, out Keywords declared))
                return declared;
            for (int i = 0; i < _keywords.Length; ++i)
            {
                // Terrain-hole contracts may use a local alpha-test keyword.
                var keyword = _keywords[i];
                if (keyword.isValid && (i == 11 || keyword.isOverridable) &&
                    ShaderUtil.PassHasKeyword(shader, pass, keyword, stage, platform)) declared |= (Keywords)(1 << i);
            }
            _declarations.Add(key, declared);
            return declared;
        }

        internal bool CanRemoveVariant(Shader shader, ShaderSnippetData snippet, ShaderCompilerData data)
        {
            lastRemovalReason = RemovalReason.None;
            if (shader == null || shader != _shader || !_passModes.TryGetValue(snippet.pass, out string lightMode))
                return false;
            Capabilities capabilities = GetPassCapabilities(shader, snippet.pass);
            ++_examined;
            lastRemovalReason = GetPassRemovalReason(_policy, capabilities, lightMode);
            if (lastRemovalReason == RemovalReason.None)
            {
                Keywords declared = GetDeclaredKeywords(shader, snippet.pass, snippet.shaderType, data.shaderCompilerPlatform);
                Keywords enabled = Keywords.None;
                for (int i = 0; i < _keywords.Length; ++i)
                    if ((declared & (Keywords)(1 << i)) != 0 && data.shaderKeywordSet.IsEnabled(_keywords[i]))
                        enabled |= (Keywords)(1 << i);
                lastRemovalReason = GetVariantRemovalReason(_policy, capabilities, lightMode, snippet.shaderType, declared, enabled);
                if (lastRemovalReason == RemovalReason.None && snippet.shaderType != ShaderType.Fragment &&
                    IncludesFragmentProgram(snippet.shaderType, data.shaderCompilerPlatform))
                {
                    // Vulkan/GL ignore stage-specific keyword suffixes and Unity
                    // sends a combined program as a Vertex snippet. Query the
                    // fragment declarations explicitly; separate D3D/Metal vertex
                    // snippets must never infer fragment keyword OFF states.
                    declared = GetDeclaredKeywords(shader, snippet.pass, ShaderType.Fragment, data.shaderCompilerPlatform);
                    enabled = Keywords.None;
                    for (int i = 0; i < _keywords.Length; ++i)
                        if ((declared & (Keywords)(1 << i)) != 0 && data.shaderKeywordSet.IsEnabled(_keywords[i]))
                            enabled |= (Keywords)(1 << i);
                    lastRemovalReason = GetVariantRemovalReason(_policy, capabilities, lightMode, ShaderType.Fragment, declared, enabled);
                }
            }
            bool remove = lastRemovalReason != RemovalReason.None;
            if (remove)
                ++_removed;
            return remove;
        }

        internal void AfterShaderStripping(Shader shader)
        {
            int removed = _removed;
            int examined = _examined;
            Clear();
            if (removed > 0)
                Debug.Log("UltraRP pass/combination stripping: " + shader.name + ", removed " +
                    removed + " / " + examined + " variants.");
        }

        void Clear()
        {
            _shader = null;
            _policy = default;
            _keywords = null;
            _passModes.Clear();
            _passCapabilities.Clear();
            _declarations.Clear();
            _examined = _removed = 0;
            lastRemovalReason = RemovalReason.None;
        }
    }
}
