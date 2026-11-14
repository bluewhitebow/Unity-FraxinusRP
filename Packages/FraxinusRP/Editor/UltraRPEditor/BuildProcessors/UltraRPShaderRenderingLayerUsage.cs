using System;
using System.Collections.Generic;
using System.Reflection;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    // This is independent of light-layer and screen-shadow proofs. Decals and
    // extensions can request the writer even when the asset disables light layers.
    internal static class UltraRPShaderRenderingLayerUsage
    {
        const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        // FRP currently has no RenderingLayers input bit. Do not infer that a new
        // bit is harmless when the enum grows: only these reviewed inputs are safe.
        const ScriptableRenderPassInput ReviewedInputs = ScriptableRenderPassInput.Depth |
            ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Motion;

        internal static bool DeclaresUnused(Type type)
            => type != null && type.GetCustomAttribute<RenderingLayerUsageAttribute>(false)?.usesRenderingLayers == false;

        internal static bool IsReviewedNativeModule(Type type)
            // GBuffer/DepthNormal writers are controlled by the asset and graph
            // requirements proved below, not by today's RequireRenderingLayers stub.
            // InvokeOnRenderObjectCallbackPass itself requests no writer. Arbitrary
            // scripts changing reserved keywords are covered by the extension
            // contract; no build-time callback execution or IL scan is performed.
            => type == typeof(PRTRelightPass) || type == typeof(DeferredPass) || type == typeof(GBufferPass) ||
                type == typeof(InvokeOnRenderObjectCallbackPass) || type == typeof(ProbeVolumeDebugPass) ||
                type == typeof(MotionVectorRenderPass) || type == typeof(DrawScreenSpaceUIPass) ||
                type == typeof(GroundTruthAmbientOcclusionPass) || type == typeof(ContactShadowsPass) ||
                type == typeof(CapturePass) || type == typeof(AreaLightsPass) || type == typeof(APRTRelightPass) ||
                type == typeof(APRTProbeDebugPass) || type == typeof(DepthCopyPass) ||
                type == typeof(DepthNormalOnlyPass) || type == typeof(DepthOnlyPass) ||
                type == typeof(CustomPassWithDepthCopyAndMotion) || type == typeof(GBufferDepthCopyAndMotionPass);

        static bool IsReviewedNode(Type type)
            // DrawObjects, RenderDriven and ContextBegin only use the built-in
            // writer selected from the same pipeline/decal settings. Exact types
            // prevent an overridden input getter or custom draw escaping review.
            => type == typeof(GBufferPassNode) || type == typeof(DeferredObjectPass) ||
                type == typeof(RenderDrivenPass) || type == typeof(ContextBeginNode) ||
                type == typeof(DrawObjectsPass) || type == typeof(ContextEndNode) ||
                type == typeof(TextureBlitNode) || type == typeof(PostProcessPass) ||
                type == typeof(MainLightShadowCasterPass) || type == typeof(AdditionalLightsShadowCasterPass) ||
                type == typeof(ColorGradingLutPass) || type == typeof(DecalRenderPassNode) || type == typeof(DrawSkyboxPass);

        static bool IsReviewedPostPass(Type type)
            // These read color/depth/motion; their own material keyword changes
            // are limited to post effects, not rendering-layer writer selection.
            => type == typeof(BloomPass) || type == typeof(DepthOfFieldPass) ||
                type == typeof(MotionBlurPass) || type == typeof(AutoExposurePass) ||
                type == typeof(SMAAPass) || type == typeof(TAAPass) ||
                type == typeof(FraxinusEngine.Rendering.Ultra.LensFlare) || type == typeof(PaniniProjectionPass);

        static bool IsReviewedUberProcessor(Type type)
            => type == typeof(ChromaticAberrationProcessor) || type == typeof(FilmGrainProcessor) ||
                type == typeof(LensDistortionProcessor) || type == typeof(VignetteProcessor);

        internal static bool AreModulesKnown(IEnumerable<Type> types, out string reason)
            => AreTypesKnown(types, IsReviewedNativeModule, "renderer module", out reason);

        internal static bool ArePostPassTypesKnown(IEnumerable<Type> types, out string reason)
            => AreTypesKnown(types, IsReviewedPostPass, "postprocess pass", out reason);

        internal static bool AreUberProcessorTypesKnown(IEnumerable<Type> types, out string reason)
            => AreTypesKnown(types, IsReviewedUberProcessor, "Uber processor", out reason);

        static bool AreTypesKnown(IEnumerable<Type> types, Func<Type, bool> reviewed, string kind, out string reason)
        {
            reason = null;
            if (types == null)
                return Failed(out reason, "Missing " + kind + " types");
            foreach (Type type in types)
            {
                if (reviewed(type))
                    continue;
                var declaration = type?.GetCustomAttribute<RenderingLayerUsageAttribute>(false);
                // Auto-created modules/entries have no serialized instance here.
                // A field-dependent declaration cannot prove every instance safe.
                if (declaration?.usesRenderingLayers != false || declaration.inputFieldName != null)
                    return Failed(out reason, "Unknown rendering-layer usage in " + kind + ": " + (type?.FullName ?? "<missing>"));
            }
            return true;
        }

        internal static IEnumerable<Type> DiscoveredModules()
        {
            // Match ScriptableRenderer.CachePassModuleTypes; do not instantiate.
            foreach (Type type in TypeCache.GetTypesDerivedFrom<BuiltinPass>())
                if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
                    yield return type;
            foreach (Type type in TypeCache.GetTypesDerivedFrom<InjectPass>())
                if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
                    yield return type;
        }

        static IEnumerable<Type> DiscoveredConcreteTypes<T>()
        {
            foreach (Type type in TypeCache.GetTypesDerivedFrom<T>())
                if (!type.IsAbstract && !type.IsInterface)
                    yield return type;
        }

        internal static bool TryProveUnused(IEnumerable<RenderPipelineAsset> assets, out string reason)
            => TryProveUnused(assets, DiscoveredModules(), DiscoveredConcreteTypes<BasePostPass>(),
                DiscoveredConcreteTypes<IUberEffectProcessor>(), out reason);

        // Explicit discovery inputs keep boundary tests independent of project
        // extensions. Unknown fixture types must not poison global TypeCache.
        internal static bool TryProveUnused(IEnumerable<RenderPipelineAsset> assets, IEnumerable<Type> modules,
            IEnumerable<Type> postPassTypes, IEnumerable<Type> uberProcessorTypes, out string reason)
        {
            reason = null;
            if (assets == null)
                return Failed(out reason, "Pipeline assets are missing");
            if (!AreModulesKnown(modules, out reason) || !ArePostPassTypesKnown(postPassTypes, out reason) ||
                !AreUberProcessorTypesKnown(uberProcessorTypes, out reason)) return false;
            int graphCount = 0;
            foreach (var asset in assets)
            {
                if (asset == null || asset.GetType() != typeof(UltraRenderPipelineAsset))
                    return Failed(out reason, "Missing or unknown pipeline asset");
                var ultra = (UltraRenderPipelineAsset)asset;
                if (ultra.useRenderingLayers)
                    return Failed(out reason, "Pipeline enables rendering layers: " + asset.name);
                if (!TryProveGraphsUnused(ultra.rendererGraphList, out int count, out reason))
                    return false;
                graphCount += count;
            }
            if (graphCount == 0)
                return Failed(out reason, "No renderer graphs were declared");
            reason = "All " + graphCount + " declared graphs, light/decal settings and discovered extensions prove rendering-layer output unused";
            return true;
        }

        internal static bool TryProveGraphsUnused(IEnumerable<ScriptableRendererGraph> graphs, out int graphCount, out string reason)
        {
            graphCount = 0;
            reason = null;
            if (graphs == null)
                return Failed(out reason, "Renderer graphs are missing");
            foreach (var graph in graphs)
            {
                ++graphCount;
                if (graph == null || graph.GetType() != typeof(UltraRendererGraph) || graph.nodes == null || graph.nodes.Count == 0)
                    return Failed(out reason, "Missing, empty or custom renderer graph");
                if (UnityEditor.SerializationUtility.HasManagedReferencesWithMissingTypes(graph))
                    return Failed(out reason, "Missing node types in " + graph.name);
                // Include inactive/disconnected nodes: they can be enabled later.
                foreach (var node in graph.nodes)
                    if (!NodeProvesUnused(node, out reason))
                        return false;
            }
            return graphCount > 0 || Failed(out reason, "No renderer graphs were declared");
        }

        internal static bool NodeProvesUnused(object node, out string reason)
        {
            reason = null;
            Type type = node?.GetType();
            if (type == null)
                return Failed(out reason, "Missing renderer node");
            if (node is DecalRenderPassNode decal)
            {
                // Do not let an extension declaration hide inherited decal settings.
                if (type != typeof(DecalRenderPassNode) || decal.settings == null ||
                    decal.settings.GetType() != typeof(DecalSettings) || decal.settings.decalLayers)
                    return Failed(out reason, "Missing, custom or enabled decal-layer settings: " + type.FullName);
            }
            if (!IsReviewedNode(type) && !DeclaresUnused(type))
                return Failed(out reason, "Unknown node rendering-layer usage: " + type.FullName);
            if (!InputsProveUnused(node, out reason))
                return false;
            if (type == typeof(TextureBlitNode))
            {
                var field = typeof(TextureBlitNode).GetField("_blitMaterial", InstanceFields);
                if (field == null || field.FieldType != typeof(Material) || (Material)field.GetValue(node) != null)
                    return Failed(out reason, "Custom TextureBlitNode material");
            }
            return !(node is PostProcessPass post) || PostProcessProvesUnused(post, out reason);
        }

        static bool InputsProveUnused(object node, out string reason)
        {
            reason = null;
            var type = node.GetType();
            var declaration = type.GetCustomAttribute<RenderingLayerUsageAttribute>(false);
            string namedField = declaration?.inputFieldName;
            bool found = namedField == null;
            for (Type current = type; current != null; current = current.BaseType)
            {
                foreach (var field in current.GetFields(InstanceFields | BindingFlags.DeclaredOnly))
                {
                    bool named = field.Name == namedField;
                    bool serialized = !field.IsNotSerialized && (field.IsPublic || field.IsDefined(typeof(SerializeField), false));
                    if (named)
                    {
                        if (found || !serialized || field.FieldType != typeof(ScriptableRenderPassInput))
                            return Failed(out reason, "Invalid declared input field: " + type.FullName + "." + namedField);
                        found = true;
                    }
                    if (!serialized || field.FieldType != typeof(ScriptableRenderPassInput))
                        continue;
                    var input = (ScriptableRenderPassInput)field.GetValue(node);
                    if ((input & ~ReviewedInputs) != 0)
                        return Failed(out reason, "Rendering-layer or unreviewed input request: " + type.FullName + "." + field.Name);
                }
            }
            return found || Failed(out reason, "Missing declared input field: " + type.FullName + "." + namedField);
        }

        static bool PostProcessProvesUnused(PostProcessPass post, out string reason)
        {
            reason = null;
            if (post.postPassEntries == null || post.uberEffectEntries == null)
                return Failed(out reason, "Missing postprocess entries");
            var types = new List<Type>();
            foreach (var entry in post.postPassEntries)
                types.Add(entry?.passType);
            if (!ArePostPassTypesKnown(types, out reason))
                return false;
            foreach (var effect in post.uberEffectEntries)
            {
                Type type = effect?.componentType;
                if (type != typeof(ChromaticAberration) && type != typeof(FilmGrain) &&
                    type != typeof(LensDistortion) && type != typeof(Vignette) && !DeclaresUnused(type))
                    return Failed(out reason, "Unknown Uber component rendering-layer usage: " + (type?.FullName ?? "<missing>"));
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
