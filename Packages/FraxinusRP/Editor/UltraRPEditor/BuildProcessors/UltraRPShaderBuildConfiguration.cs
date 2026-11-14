using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    internal static class UltraRPShaderBuildConfiguration
    {
        // SBP's cached write operations must depend on stripping settings, not
        // only Shader source. Never delete ShaderCache to invalidate bundle data.
        internal static Hash128 GetHash()
        {
            if (!UltraRPShaderBuildSession.isBuildingForUltraRP)
                return default;
            var data = UltraRPShaderBuildSession.data;
            return GetHash(data.buildTarget, data.isDevelopmentBuild, data.allRenderPipelineAssets);
        }

        internal static Hash128 GetProjectHash()
        {
            var assets = new List<RenderPipelineAsset>();
            UnityEditor.Rendering.BuildTargetExtensions.TryGetRenderPipelineAssets(EditorUserBuildSettings.activeBuildTarget, assets);
            return GetHash(EditorUserBuildSettings.activeBuildTarget, EditorUserBuildSettings.development, assets);
        }

        static Hash128 GetHash(BuildTarget target, bool development, IEnumerable<RenderPipelineAsset> assets)
        {
            var declaredAssets = assets.ToArray();
            var text = new StringBuilder("FraxinusShaderBuildConfiguration:2\n");
            text.AppendLine(GetImplementationHash(GetImplementationTypes(declaredAssets)).ToString());
            text.AppendLine(target.ToString());
            text.AppendLine(development.ToString());
            text.AppendLine(PlayerSettings.allowHDRDisplaySupport.ToString());
            foreach (var api in PlayerSettings.GetGraphicsAPIs(target))
                text.AppendLine(api.ToString());
            foreach (var asset in declaredAssets.OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal))
            {
                AppendAsset(text, asset);
                if (asset is UltraRenderPipelineAsset ultra)
                    foreach (var graph in ultra.rendererGraphList ?? Array.Empty<ScriptableRendererGraph>())
                        AppendAsset(text, graph);
            }
            var global = GraphicsSettings.GetSettingsForRenderPipeline<UltraRenderPipeline>() as UltraRenderPipelineGlobalSettings;
            AppendAsset(text, global);
            var settings = global == null ? null : RenderPipelineGraphicsSettingsCompatibility
                .GetRenderPipelineSettings<UltraRenderPipelineShaderStrippingSetting>(global);
            if (settings != null && settings.stripUnusedPostProcessingVariants)
                foreach (string guid in AssetDatabase.FindAssets("t:VolumeProfile").OrderBy(value => value, StringComparer.Ordinal))
                {
                    var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(AssetDatabase.GUIDToAssetPath(guid));
                    AppendAsset(text, profile);
                    if (profile != null && profile.components != null)
                        foreach (var component in profile.components)
                            AppendAsset(text, component);
                }
            // These also affect Unity's built-in stripping (fog, lightmaps,
            // instancing) and the platform's included quality configurations.
            foreach (string path in new[] { "ProjectSettings/GraphicsSettings.asset", "ProjectSettings/QualitySettings.asset" })
                if (File.Exists(path))
                    text.AppendLine(File.ReadAllText(path));
            return Hash128.Compute(text.ToString());
        }

        internal static IEnumerable<Type> GetImplementationTypes(IEnumerable<RenderPipelineAsset> assets)
        {
            yield return typeof(UltraRPShaderBuildConfiguration);
            yield return typeof(UltraRenderPipeline);
            yield return typeof(CoreBuildData);
            foreach (var asset in assets)
            {
                if (asset == null)
                    continue;
                yield return asset.GetType();
                if (!(asset is UltraRenderPipelineAsset ultra))
                    continue;
                foreach (var graph in ultra.rendererGraphList ?? Array.Empty<ScriptableRendererGraph>())
                {
                    if (graph == null)
                        continue;
                    yield return graph.GetType();
                    if (graph.nodes == null)
                        continue;
                    foreach (var node in graph.nodes)
                    {
                        if (node == null)
                            continue;
                        yield return node.GetType();
                        if (!(node is PostProcessPass post))
                            continue;
                        if (post.postPassEntries != null)
                            foreach (var entry in post.postPassEntries)
                                yield return entry?.passType;
                        if (post.uberEffectEntries != null)
                            foreach (var entry in post.uberEffectEntries)
                                yield return entry?.componentType;
                    }
                }
            }
            // Unknown extensions and disabled entries matter too: changing a
            // declaration attribute can change stripping without asset changes.
            foreach (Type type in UltraRPShaderScreenShadowUsage.DiscoveredModules())
                yield return type;
            foreach (Type type in TypeCache.GetTypesDerivedFrom<BasePostPass>())
                yield return type;
            foreach (Type type in TypeCache.GetTypesDerivedFrom<IUberEffectProcessor>())
                yield return type;
        }

        internal static Hash128 GetImplementationHash(IEnumerable<Type> types)
        {
            var modules = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var implementation in types)
                // Inherited implementations may live in a different assembly.
                for (var type = implementation; type != null; type = type.BaseType)
                {
                    var assembly = type.Assembly;
                    modules.Add(assembly.FullName + ":" + assembly.ManifestModule.ModuleVersionId.ToString());
                }
            return Hash128.Compute(string.Join("\n", modules));
        }

        static void AppendAsset(StringBuilder text, UnityEngine.Object asset)
        {
            if (asset == null)
            {
                text.AppendLine("<missing>");
                return;
            }
            text.AppendLine(AssetDatabase.GetAssetPath(asset));
            text.AppendLine(Canonicalize(EditorJsonUtility.ToJson(asset)));
        }

        internal static string Canonicalize(string json)
        {
            // Derived native-filter fields change during preparation/recovery.
            // Their source settings are already serialized elsewhere in the asset.
            json = Regex.Replace(json, "\\\"_(?:pre|re)filter[^\\\"]*\\\"\\s*:\\s*(?:true|false|-?[0-9]+)\\s*,?", "");
            return Regex.Replace(json, "\\\"instanceID\\\"\\s*:\\s*(-?[0-9]+)", match =>
            {
                int id = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (id == 0)
                    return "\"assetRef\":\"null\"";
                var reference = EditorUtility.InstanceIDToObject(id);
                if (reference != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(reference, out string guid, out long localId))
                    return "\"assetRef\":\"" + guid + ":" + localId + "\"";
                // Unsaved custom objects cannot have a stable GUID. Including the
                // id may miss a cache hit after reload, but never reuses stale data.
                return match.Value;
            });
        }
    }
}
