using System;
using System.Collections.Generic;
using System.Reflection;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    // Feature-specific declarations must never turn a custom node into a known
    // forward/deferred implementation for the unrelated whole-pass rules.
    internal static class UltraRPShaderScreenShadowUsage
    {
        internal static bool DeclaresUnused(Type type)
            => type != null && type.GetCustomAttribute<ScreenSpaceShadowUsageAttribute>(false)?.usesScreenSpaceShadows == false;

        internal static bool IsReviewedNativeModule(Type type)
            => type == typeof(PRTRelightPass) || type == typeof(DeferredPass) || type == typeof(GBufferPass) ||
                type == typeof(InvokeOnRenderObjectCallbackPass) || type == typeof(ProbeVolumeDebugPass) ||
                type == typeof(MotionVectorRenderPass) || type == typeof(DrawScreenSpaceUIPass) ||
                type == typeof(GroundTruthAmbientOcclusionPass) || type == typeof(ContactShadowsPass) ||
                type == typeof(CapturePass) || type == typeof(AreaLightsPass) || type == typeof(APRTRelightPass) ||
                type == typeof(APRTProbeDebugPass) || type == typeof(DepthCopyPass) ||
                type == typeof(DepthNormalOnlyPass) || type == typeof(DepthOnlyPass) ||
                type == typeof(CustomPassWithDepthCopyAndMotion) || type == typeof(GBufferDepthCopyAndMotionPass);

        internal static IEnumerable<Type> DiscoveredModules()
        {
            // Match ScriptableRenderer.CachePassModuleTypes. Direct IRendererModule
            // implementations (for example light cores) are not auto-instantiated.
            foreach (Type type in TypeCache.GetTypesDerivedFrom<BuiltinPass>())
                if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
                    yield return type;
            foreach (Type type in TypeCache.GetTypesDerivedFrom<InjectPass>())
                if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
                    yield return type;
        }

        internal static bool AreModulesKnown(IEnumerable<Type> types, bool allowScreenDeclarations, out string reason)
        {
            reason = null;
            if (types == null)
                return Failed(out reason, "Missing renderer module types");
            foreach (Type type in types)
                if (!IsReviewedNativeModule(type) && !(allowScreenDeclarations && DeclaresUnused(type)))
                    return Failed(out reason, "Unknown registered renderer module: " + (type?.FullName ?? "<missing>"));
            return true;
        }

        internal static bool ArePostPassTypesKnown(IEnumerable<Type> types, out string reason)
        {
            reason = null;
            if (types == null)
                return Failed(out reason, "Missing postprocess pass types");
            foreach (Type type in types)
                if (!UltraRPPostProcessShaderStripper.IsKnownPostPassType(type) && !DeclaresUnused(type))
                    return Failed(out reason, "Unknown postprocess screen-shadow usage: " + (type?.FullName ?? "<missing>"));
            return true;
        }

        internal static bool IsKnownUberProcessor(Type type)
            => type == typeof(ChromaticAberrationProcessor) || type == typeof(FilmGrainProcessor) ||
                type == typeof(LensDistortionProcessor) || type == typeof(VignetteProcessor);

        internal static bool AreUberProcessorTypesKnown(IEnumerable<Type> types, out string reason)
        {
            reason = null;
            if (types == null)
                return Failed(out reason, "Missing Uber processor types");
            foreach (Type type in types)
                if (!IsKnownUberProcessor(type) && !DeclaresUnused(type))
                    return Failed(out reason, "Unknown Uber processor screen-shadow usage: " + (type?.FullName ?? "<missing>"));
            return true;
        }

        internal static bool TryProveUnused(IEnumerable<RenderPipelineAsset> assets, out string reason)
        {
            reason = null;
            if (assets == null)
                return Failed(out reason, "Pipeline assets are missing");
            if (!AreModulesKnown(DiscoveredModules(), true, out reason))
                return false;
            int graphCount = 0;
            foreach (var asset in assets)
            {
                if (asset == null || asset.GetType() != typeof(UltraRenderPipelineAsset))
                    return Failed(out reason, "Missing or unknown pipeline asset");
                var graphs = ((UltraRenderPipelineAsset)asset).rendererGraphList;
                if (graphs == null || graphs.Length == 0)
                    return Failed(out reason, "No renderer graphs in " + asset.name);
                foreach (var graph in graphs)
                {
                    ++graphCount;
                    if (graph == null || graph.GetType() != typeof(UltraRendererGraph) || graph.nodes == null || graph.nodes.Count == 0)
                        return Failed(out reason, "Missing, empty or custom renderer graph in " + asset.name);
                    if (UnityEditor.SerializationUtility.HasManagedReferencesWithMissingTypes(graph))
                        return Failed(out reason, "Missing node types in " + graph.name);
                    foreach (var node in graph.nodes)
                    {
                        Type type = node?.GetType();
                        bool knownNative = type != null && UltraRPPostProcessShaderStripper.ClassifyRendererGraph(
                            typeof(UltraRendererGraph), new[] { type }) != UltraRPPostProcessShaderStripper.RendererGraphUsage.Unknown;
                        if (!knownNative && !DeclaresUnused(type))
                            return Failed(out reason, "Unknown node screen-shadow usage in " + graph.name + ": " + (type?.FullName ?? "<missing>"));
                        if (type == typeof(TextureBlitNode))
                        {
                            var field = typeof(TextureBlitNode).GetField("_blitMaterial", BindingFlags.Instance | BindingFlags.NonPublic);
                            if (field == null || field.FieldType != typeof(Material) || (Material)field.GetValue(node) != null)
                                return Failed(out reason, "Custom TextureBlitNode material in " + graph.name);
                        }
                        if (node is PostProcessPass post && !PostProcessIsKnown(post, out reason))
                            return false;
                    }
                }
            }
            if (graphCount == 0)
                return Failed(out reason, "No renderer graphs were declared");
            reason = "All " + graphCount + " declared graphs and discovered extensions have reviewed or explicit screen-shadow usage";
            return true;
        }

        static bool PostProcessIsKnown(PostProcessPass post, out string reason)
        {
            reason = null;
            if (post.postPassEntries == null || post.uberEffectEntries == null)
                return Failed(out reason, "Missing postprocess entries");
            var passes = new List<Type>();
            foreach (var entry in post.postPassEntries)
                passes.Add(entry?.passType);
            foreach (Type type in TypeCache.GetTypesDerivedFrom<BasePostPass>())
                if (!type.IsAbstract && !type.IsInterface)
                    passes.Add(type);
            if (!ArePostPassTypesKnown(passes, out reason))
                return false;

            var processors = new List<Type>();
            foreach (Type type in TypeCache.GetTypesDerivedFrom<IUberEffectProcessor>())
                if (!type.IsAbstract && !type.IsInterface)
                    processors.Add(type);
            if (!AreUberProcessorTypesKnown(processors, out reason))
                return false;
            foreach (var effect in post.uberEffectEntries)
            {
                Type type = effect?.componentType;
                if (type != typeof(ChromaticAberration) && type != typeof(FilmGrain) &&
                    type != typeof(LensDistortion) && type != typeof(Vignette) && !DeclaresUnused(type))
                    return Failed(out reason, "Unknown Uber component screen-shadow usage: " + (type?.FullName ?? "<missing>"));
            }
            return true;
        }

        static bool Failed(out string reason, string detail)
        {
            reason = detail;
            return false;
        }
    }
}
