using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    // Use Core's interface explicitly; the source package also retains unrelated
    // interfaces with the same short names in this namespace.
    internal sealed class UltraRPDeferredComputeShaderStripper :
        UnityEditor.Rendering.IComputeShaderVariantStripper,
        UnityEditor.Rendering.IComputeShaderVariantStripperScope
    {
        UltraRPBuildData _buildData;
        bool _stripUnusedVariants;
        bool _clusteredUnused;
        bool _canStripShader;
        bool _areaRequired;
        bool _areaForbidden;
        bool _canFilterArea;
        LocalKeyword _areaKeyword;
        int _removed;

        // Core discovers public parameterless constructors, including on internal types.
        public UltraRPDeferredComputeShaderStripper() { }

        // Core caches discovered implementations for its callback instance. Keep
        // this participant registered across consecutive OFF/ON bundle builds;
        // PrepareBuild evaluates the current settings for each session identity.
        public bool active => UltraRPShaderBuildSession.isBuildingForUltraRP;

        static bool IsEnabledForCurrentBuild()
        {
            if (!UltraRPShaderBuildSession.isBuildingForUltraRP)
                return false;
            var globalSettings = GraphicsSettings.GetSettingsForRenderPipeline<UltraRenderPipeline>()
                as UltraRenderPipelineGlobalSettings;
            if (globalSettings == null)
                return false;
            var settings = RenderPipelineGraphicsSettingsCompatibility
                .GetRenderPipelineSettings<UltraRenderPipelineShaderStrippingSetting>(globalSettings);
            return settings != null && settings.stripUnusedVariants;
        }

        void PrepareBuild()
        {
            var buildData = UltraRPBuildData.instance;
            if (ReferenceEquals(_buildData, buildData))
                return;
            _buildData = buildData;
            _stripUnusedVariants = IsEnabledForCurrentBuild();
            _clusteredUnused = false;
            _areaRequired = false;
            _areaForbidden = false;
            if (!_stripUnusedVariants)
                return;
            var features = UltraRPShaderBuildFeatures.Gather(buildData.allRenderPipelineAssets, true);
            _areaRequired = features.CanRequireKeyword(ShaderKeywordStrings.AreaLights);
            _areaForbidden = features.CanStripKeyword(ShaderKeywordStrings.AreaLights);
            bool known = UltraRPPostProcessShaderStripper.TryAnalyzeDeclaredBuild(
                buildData.allRenderPipelineAssets, out var usage, out int graphCount, out string reason);
            _clusteredUnused = known && UltraRPPostProcessShaderStripper.ShouldStripClusteredCompute(true, new[] { usage });
            Debug.Log("UltraRP DeferredLit compute stripping: " + (_clusteredUnused ? "remove" : "retain") +
                ", declared renderer graphs examined: " + graphCount + ". " + reason +
                ". Undeclared runtime graphs or lighting modes require stripUnusedVariants=false.");
        }

        internal static bool ShouldStripComputeShader(bool stripUnusedVariants, bool clusteredUnused, string path, string name)
        {
            return stripUnusedVariants && clusteredUnused && name == "DeferredLit" &&
                string.Equals(path?.Replace('\\', '/'),
                    UltraRenderPipelineAsset.packagePath + "/Shaders/Lighting/DeferredLit.compute", StringComparison.Ordinal);
        }

        public void BeforeShaderStripping(ComputeShader shader)
        {
            PrepareBuild();
            _removed = 0;
            _canStripShader = ShouldStripComputeShader(_stripUnusedVariants, _clusteredUnused,
                AssetDatabase.GetAssetPath(shader), shader.name);
            _areaKeyword = new LocalKeyword(shader, ShaderKeywordStrings.AreaLights);
            string path = AssetDatabase.GetAssetPath(shader);
            _canFilterArea = _stripUnusedVariants && !string.IsNullOrEmpty(path) && _areaKeyword.isValid && _areaKeyword.isOverridable &&
                IsAreaLightComputeShader(path, AssetDatabase.GetDependencies(path));
        }

        internal static bool IsAreaLightComputeShader(string path, IEnumerable<string> dependencies)
        {
            string library = UltraRenderPipelineAsset.packagePath + "/Shaders/Lighting/DeferredLit.compute.hlsl";
            if (string.Equals(path?.Replace('\\', '/'),
                UltraRenderPipelineAsset.packagePath + "/Shaders/Lighting/DeferredLit.compute", StringComparison.Ordinal) ||
                dependencies != null && dependencies.Any(dependency =>
                    string.Equals(dependency?.Replace('\\', '/'), library, StringComparison.Ordinal)))
                return true;
            // Unity 2022's AssetDatabase dependency list omits shader include
            // files. Only a direct literal include proves the shared contract;
            // unknown macros, wrappers and missing sources keep both variants.
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path) &&
                    HasAreaLightComputeInclude(File.ReadAllText(path));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static bool HasAreaLightComputeInclude(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            string uncommented = Regex.Replace(source, @"//[^\r\n]*|/\*[\s\S]*?\*/", " ");
            string library = Regex.Escape(UltraRenderPipelineAsset.packagePath + "/Shaders/Lighting/DeferredLit.compute.hlsl");
            return Regex.IsMatch(uncommented, "^[ \\t]*#[ \\t]*include_with_pragmas[ \\t]+\\\"" + library + "\\\"[ \\t]*\\r?$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
        }

        internal static bool ShouldStripAreaLightVariant(bool canFilter, bool required, bool forbidden, bool enabled)
            => canFilter && required != forbidden && (forbidden ? enabled : !enabled);

        public bool CanRemoveVariant(ComputeShader shader, string kernelName, ShaderCompilerData data)
        {
            // Unknown shaders must return false. Using a skipper here could make
            // Core's All() evaluate true when every registered stripper skips.
            bool stripArea = ShouldStripAreaLightVariant(_canFilterArea, _areaRequired, _areaForbidden,
                _canFilterArea && data.shaderKeywordSet.IsEnabled(_areaKeyword));
            bool removed = _canStripShader || stripArea;
            if (removed)
                ++_removed;
            UltraRPShaderBuildReport.RecordDecision(_canStripShader
                ? UltraRPShaderBuildReport.Decision.DeferredPath : stripArea
                    ? (_areaForbidden ? UltraRPShaderBuildReport.Decision.ForbiddenGlobal : UltraRPShaderBuildReport.Decision.RequiredGlobal)
                    : UltraRPShaderBuildReport.Decision.Retained);
            return removed;
        }

        public void AfterShaderStripping(ComputeShader shader)
        {
            if (_removed > 0)
                Debug.Log("UltraRP compute stripping: " + shader.name + ", removed " + _removed + " variants.");
        }
    }
}
