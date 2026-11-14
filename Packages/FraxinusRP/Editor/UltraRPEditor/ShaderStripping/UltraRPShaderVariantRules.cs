using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using LightingKeywords = FraxinusEditor.Rendering.Ultra.UltraRPShaderBuildFeatures.LightingKeywords;

namespace FraxinusEditor.Rendering.Ultra
{
    // This is a helper of the existing registered stripper. Core combines independent
    // IShaderVariantStripper implementations with All(), so a second registered
    // ordinary-shader stripper would be vetoed by the postprocess stripper.
    internal sealed class UltraRPShaderVariantRules
    {
        internal enum RemovalReason { None, ForbiddenGlobal, RequiredGlobal, LightingCombination }

        static readonly ShaderTagId s_pipelineTag = new ShaderTagId("RenderPipeline");
        static readonly string[] s_lightingKeywordNames = { "_FORWARD_PLUS", "_ADDITIONAL_LIGHTS", "_ADDITIONAL_LIGHTS_VERTEX" };
        static readonly string[] s_mainShadowKeywordNames = { "_MAIN_LIGHT_SHADOWS", "_MAIN_LIGHT_SHADOWS_CASCADE", "_MAIN_LIGHT_SHADOWS_SCREEN" };
        Shader _shader;
        bool[] _ownedSubshaders = Array.Empty<bool>();
        LocalKeyword[] _forbiddenGlobals = Array.Empty<LocalKeyword>();
        LocalKeyword[] _requiredGlobals = Array.Empty<LocalKeyword>();
        readonly Dictionary<(PassIdentifier, ShaderType, ShaderCompilerPlatform), LocalKeyword[]> _requiredDeclarations =
            new Dictionary<(PassIdentifier, ShaderType, ShaderCompilerPlatform), LocalKeyword[]>();
        LocalKeyword[] _lightingGlobals = Array.Empty<LocalKeyword>();
        int _allowedLightingStates = UltraRPShaderBuildFeatures.AllLightingStates;
        bool _requireMainLightShadows;
        LocalKeyword[] _mainShadowGlobals = Array.Empty<LocalKeyword>();
        readonly Dictionary<(PassIdentifier, ShaderType, ShaderCompilerPlatform), int> _mainShadowDeclarations =
            new Dictionary<(PassIdentifier, ShaderType, ShaderCompilerPlatform), int>();
        readonly Dictionary<(PassIdentifier, ShaderType, ShaderCompilerPlatform), LightingKeywords> _lightingDeclarations =
            new Dictionary<(PassIdentifier, ShaderType, ShaderCompilerPlatform), LightingKeywords>();
        int _examined;
        int _removed;
        internal RemovalReason lastRemovalReason { get; private set; }
        internal bool lastRemovalWasRequired => lastRemovalReason == RemovalReason.RequiredGlobal;

        internal static bool IsOwnedPipelineTag(string renderPipelineTag)
            => string.Equals(renderPipelineTag, "UltraPipeline", StringComparison.Ordinal);

        // The same ownership/declaration policy is shared by the pure policy tests
        // and the cached shader path below. A matching local-only keyword is not a
        // declaration of a runtime-controlled global and must not be supplied here.
        internal static string[] SelectForbiddenGlobals(bool canStrip, string renderPipelineTag,
            IEnumerable<string> forbiddenGlobalKeywords, ISet<string> declaredGlobalKeywords)
        {
            if (!canStrip || !IsOwnedPipelineTag(renderPipelineTag) ||
                forbiddenGlobalKeywords == null || declaredGlobalKeywords == null)
                return Array.Empty<string>();

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in forbiddenGlobalKeywords)
                if (!string.IsNullOrEmpty(name) && declaredGlobalKeywords.Contains(name) && seen.Add(name))
                    result.Add(name);
            return result.ToArray();
        }

        internal static bool ShouldRemoveVariant(bool canStrip, string renderPipelineTag,
            IEnumerable<string> forbiddenGlobalKeywords, ISet<string> declaredGlobalKeywords,
            ISet<string> enabledKeywords)
        {
            if (enabledKeywords == null)
                return false;
            foreach (string name in SelectForbiddenGlobals(canStrip, renderPipelineTag,
                         forbiddenGlobalKeywords, declaredGlobalKeywords))
                if (enabledKeywords.Contains(name))
                    return true;
            return false;
        }

        // Here declarations must belong to the actual pass/stage/platform, not
        // merely the Shader keyword space. A stage without this axis is not OFF.
        internal static bool ShouldRemoveRequiredVariant(bool canStrip, string renderPipelineTag,
            IEnumerable<string> requiredGlobalKeywords, ISet<string> declaredStageGlobalKeywords,
            ISet<string> enabledKeywords)
        {
            if (enabledKeywords == null)
                return false;
            foreach (string name in SelectForbiddenGlobals(canStrip, renderPipelineTag,
                         requiredGlobalKeywords, declaredStageGlobalKeywords))
                if (!enabledKeywords.Contains(name))
                    return true;
            return false;
        }

        internal void BeforeShaderStripping(Shader shader, bool canStrip,
            IEnumerable<string> forbiddenGlobalKeywords, IEnumerable<string> requiredGlobalKeywords = null,
            int allowedLightingStates = UltraRPShaderBuildFeatures.AllLightingStates, bool requireMainLightShadows = false)
        {
            Clear();
            if (shader == null || !canStrip || forbiddenGlobalKeywords == null)
                return;

            var shaderData = ShaderUtil.GetShaderData(shader);
            if (shaderData == null)
                return;
            // Compiler pass identifiers address serialized source subshaders.
            // shader.subshaderCount also includes fallback subshaders, which
            // cannot be indexed through GetSerializedSubshader on this shader.
            var ownedSubshaders = new bool[shaderData.SerializedSubshaderCount];
            bool anyOwned = false;
            for (int i = 0; i < ownedSubshaders.Length; ++i)
            {
                var subshader = shaderData.GetSerializedSubshader(i);
                ownedSubshaders[i] = subshader != null && IsOwnedPipelineTag(subshader.FindTagValue(s_pipelineTag).name);
                anyOwned |= ownedSubshaders[i];
            }
            if (!anyOwned)
                return;

            var declaredGlobals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var keyword in shader.keywordSpace.keywords)
                if (keyword.isValid && keyword.isOverridable)
                    declaredGlobals.Add(keyword.name);
            string[] forbidden = SelectForbiddenGlobals(canStrip, "UltraPipeline",
                forbiddenGlobalKeywords, declaredGlobals);
            var keywords = new LocalKeyword[forbidden.Length];
            for (int i = 0; i < forbidden.Length; ++i)
                keywords[i] = shader.keywordSpace.FindKeyword(forbidden[i]);
            string[] required = SelectForbiddenGlobals(canStrip, "UltraPipeline",
                requiredGlobalKeywords, declaredGlobals);
            _requiredGlobals = new LocalKeyword[required.Length];
            for (int i = 0; i < required.Length; ++i)
                _requiredGlobals[i] = shader.keywordSpace.FindKeyword(required[i]);

            _allowedLightingStates = allowedLightingStates;
            _requireMainLightShadows = requireMainLightShadows;
            _mainShadowGlobals = new LocalKeyword[s_mainShadowKeywordNames.Length];
            for (int i = 0; i < _mainShadowGlobals.Length; ++i)
                _mainShadowGlobals[i] = shader.keywordSpace.FindKeyword(s_mainShadowKeywordNames[i]);
            _lightingGlobals = new LocalKeyword[s_lightingKeywordNames.Length];
            for (int i = 0; i < _lightingGlobals.Length; ++i)
                _lightingGlobals[i] = shader.keywordSpace.FindKeyword(s_lightingKeywordNames[i]);

            _shader = shader;
            _ownedSubshaders = ownedSubshaders;
            _forbiddenGlobals = keywords;
        }

        internal bool IsPreparedForSubshader(Shader shader, int subshaderIndex)
            => shader != null && shader == _shader && subshaderIndex >= 0 &&
               subshaderIndex < _ownedSubshaders.Length && _ownedSubshaders[subshaderIndex];

        internal bool CanRemoveVariant(Shader shader, ShaderSnippetData snippet, ShaderCompilerData data)
        {
            lastRemovalReason = RemovalReason.None;
            int subshaderIndex = (int)snippet.pass.SubshaderIndex;
            if (!IsPreparedForSubshader(shader, subshaderIndex))
                return false;

            ++_examined;
            // Forbidden states use positive enabled-keyword evidence. Required
            // states below additionally prove the actual pass/stage declaration;
            // a separate vertex program cannot imply fragment keyword OFF.
            foreach (var keyword in _forbiddenGlobals)
            {
                if (!data.shaderKeywordSet.IsEnabled(keyword))
                    continue;
                ++_removed;
                lastRemovalReason = RemovalReason.ForbiddenGlobal;
                return true;
            }
            foreach (var keyword in RequiredDeclarations(shader, snippet.pass, snippet.shaderType, data.shaderCompilerPlatform))
            {
                if (data.shaderKeywordSet.IsEnabled(keyword))
                    continue;
                ++_removed;
                lastRemovalReason = RemovalReason.RequiredGlobal;
                return true;
            }
            if (_requireMainLightShadows)
            {
                int declared = MainShadowDeclarations(shader, snippet.pass, snippet.shaderType, data.shaderCompilerPlatform);
                int enabled = 0;
                for (int i = 0; i < _mainShadowGlobals.Length; ++i)
                    if ((declared & (1 << i)) != 0 && data.shaderKeywordSet.IsEnabled(_mainShadowGlobals[i]))
                        enabled |= 1 << i;
                if (ShouldRemoveMainShadowOffVariant(true, "UltraPipeline", true, declared, enabled))
                {
                    ++_removed;
                    lastRemovalReason = RemovalReason.RequiredGlobal;
                    return true;
                }
            }
            if (_allowedLightingStates != UltraRPShaderBuildFeatures.AllLightingStates)
            {
                LightingKeywords declared = LightingDeclarations(shader, snippet.pass, snippet.shaderType, data.shaderCompilerPlatform);
                LightingKeywords enabled = LightingKeywords.None;
                for (int i = 0; i < _lightingGlobals.Length; ++i)
                    if ((declared & (LightingKeywords)(1 << i)) != 0 && data.shaderKeywordSet.IsEnabled(_lightingGlobals[i]))
                        enabled |= (LightingKeywords)(1 << i);
                if (ShouldRemoveLightingVariant(true, "UltraPipeline", _allowedLightingStates, declared, enabled))
                {
                    ++_removed;
                    lastRemovalReason = RemovalReason.LightingCombination;
                    return true;
                }
            }
            return false;
        }

        // Missing axes are projected out, never inferred to be OFF. In particular,
        // a GBuffer without an additional-light declaration remains untouched.
        internal static bool ShouldRemoveLightingVariant(bool canStrip, string renderPipelineTag,
            int allowedLightingStates, LightingKeywords declaredGlobals, LightingKeywords enabledKeywords)
        {
            if (!canStrip || !IsOwnedPipelineTag(renderPipelineTag) ||
                allowedLightingStates <= 0 || (allowedLightingStates & ~UltraRPShaderBuildFeatures.AllLightingStates) != 0 ||
                (declaredGlobals & LightingKeywords.ForwardPlus) == 0 ||
                (declaredGlobals & (LightingKeywords.AdditionalPixel | LightingKeywords.AdditionalVertex)) == 0)
                return false;
            int declared = (int)(declaredGlobals & LightingKeywords.All);
            int enabled = (int)enabledKeywords & declared;
            for (int state = 0; state < 8; ++state)
                if ((allowedLightingStates & (1 << state)) != 0 && (state & declared) == enabled)
                    return false;
            return true;
        }

        internal LightingKeywords LightingDeclarations(Shader shader, PassIdentifier pass, ShaderType stage,
            ShaderCompilerPlatform platform)
        {
            if (!IsPreparedForSubshader(shader, (int)pass.SubshaderIndex))
                return LightingKeywords.None;
            var key = (pass, stage, platform);
            if (_lightingDeclarations.TryGetValue(key, out var result))
                return result;
            bool combinedFragment = stage != ShaderType.Fragment &&
                UltraRPShaderPassRules.IncludesFragmentProgram(stage, platform);
            result = LightingKeywords.None;
            for (int i = 0; i < _lightingGlobals.Length; ++i)
            {
                var keyword = _lightingGlobals[i];
                if (keyword.isValid && keyword.isOverridable &&
                    (ShaderUtil.PassHasKeyword(shader, pass, keyword, stage, platform) ||
                     combinedFragment && ShaderUtil.PassHasKeyword(shader, pass, keyword, ShaderType.Fragment, platform)))
                    result |= (LightingKeywords)(1 << i);
            }
            _lightingDeclarations.Add(key, result);
            return result;
        }

        internal static bool ShouldRemoveMainShadowOffVariant(bool canStrip, string renderPipelineTag,
            bool requireMainLightShadows, int declaredGlobals, int enabledKeywords)
            // Main/cascade/screen form one multi_compile group. A missing axis
            // in this exact stage cannot be interpreted as an OFF declaration.
            => canStrip && IsOwnedPipelineTag(renderPipelineTag) && requireMainLightShadows &&
                declaredGlobals == 7 && (enabledKeywords & 7) == 0;

        internal int MainShadowDeclarations(Shader shader, PassIdentifier pass, ShaderType stage,
            ShaderCompilerPlatform platform)
        {
            if (!IsPreparedForSubshader(shader, (int)pass.SubshaderIndex)) return 0;
            var key = (pass, stage, platform);
            if (_mainShadowDeclarations.TryGetValue(key, out int result)) return result;
            bool combinedFragment = stage != ShaderType.Fragment &&
                UltraRPShaderPassRules.IncludesFragmentProgram(stage, platform);
            for (int i = 0; i < _mainShadowGlobals.Length; ++i)
            {
                var keyword = _mainShadowGlobals[i];
                if (keyword.isValid && keyword.isOverridable &&
                    (ShaderUtil.PassHasKeyword(shader, pass, keyword, stage, platform) ||
                     combinedFragment && ShaderUtil.PassHasKeyword(shader, pass, keyword, ShaderType.Fragment, platform)))
                    result |= 1 << i;
            }
            _mainShadowDeclarations.Add(key, result);
            return result;
        }

        internal LocalKeyword[] RequiredDeclarations(Shader shader, PassIdentifier pass, ShaderType stage,
            ShaderCompilerPlatform platform)
        {
            if (!IsPreparedForSubshader(shader, (int)pass.SubshaderIndex))
                return Array.Empty<LocalKeyword>();
            var key = (pass, stage, platform);
            if (_requiredDeclarations.TryGetValue(key, out var result))
                return result;
            var declared = new List<LocalKeyword>();
            bool combinedFragment = stage != ShaderType.Fragment &&
                UltraRPShaderPassRules.IncludesFragmentProgram(stage, platform);
            foreach (var keyword in _requiredGlobals)
                if (ShaderUtil.PassHasKeyword(shader, pass, keyword, stage, platform) ||
                    combinedFragment && ShaderUtil.PassHasKeyword(shader, pass, keyword, ShaderType.Fragment, platform))
                    declared.Add(keyword);
            result = declared.ToArray();
            _requiredDeclarations.Add(key, result);
            return result;
        }

        internal void AfterShaderStripping(Shader shader)
        {
            int removed = _removed;
            int examined = _examined;
            Clear();
            if (removed > 0)
                Debug.Log("UltraRP global feature stripping: " + shader.name + ", removed " +
                    removed + " / " + examined + " variants.");
        }

        void Clear()
        {
            _shader = null;
            _ownedSubshaders = Array.Empty<bool>();
            _forbiddenGlobals = Array.Empty<LocalKeyword>();
            _requiredGlobals = Array.Empty<LocalKeyword>();
            _requiredDeclarations.Clear();
            _lightingGlobals = Array.Empty<LocalKeyword>();
            _allowedLightingStates = UltraRPShaderBuildFeatures.AllLightingStates;
            _requireMainLightShadows = false;
            _mainShadowGlobals = Array.Empty<LocalKeyword>();
            _mainShadowDeclarations.Clear();
            _lightingDeclarations.Clear();
            lastRemovalReason = RemovalReason.None;
            _examined = _removed = 0;
        }
    }
}
