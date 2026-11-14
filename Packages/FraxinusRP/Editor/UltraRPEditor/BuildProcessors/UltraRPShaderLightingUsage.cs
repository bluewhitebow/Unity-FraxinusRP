using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;

namespace FraxinusEditor.Rendering.Ultra
{
    // The camera owns these globals. Extensions, render callbacks and nested
    // providers must preserve this policy while drawing ordinary FRP materials.
    // Like the rendering-layer contract, arbitrary writes to reserved keywords
    // are not an additional supported lighting configuration.
    internal static class UltraRPShaderLightingUsage
    {
        static readonly HashSet<Type> s_reviewed = new HashSet<Type>
        {
            typeof(TextureBlitNode), typeof(RenderDrivenPass), typeof(PRTRelightPassNode),
            typeof(PostProcessPass), typeof(MainLightShadowCasterPass),
            typeof(AdditionalLightsShadowCasterPass), typeof(GBufferPassNode),
            typeof(DrawSkyboxPass), typeof(DrawObjectsPass), typeof(DeferredObjectPass),
            typeof(DecalRenderPassNode), typeof(ContextEndNode), typeof(ContextBridgeNode),
            typeof(ContextBeginNode), typeof(ColorGradingLutPass),
            typeof(PRTRelightPass), typeof(ProbeVolumeDebugPass), typeof(MotionVectorRenderPass),
            typeof(GroundTruthAmbientOcclusionPass), typeof(GBufferPass),
            typeof(GBufferDepthCopyAndMotionPass), typeof(DrawScreenSpaceUIPass),
            typeof(DepthOnlyPass), typeof(DepthNormalOnlyPass), typeof(DepthCopyPass),
            typeof(DeferredPass), typeof(CustomPassWithDepthCopyAndMotion),
            typeof(ContactShadowsPass), typeof(CapturePass), typeof(AreaLightsPass),
            typeof(APRTRelightPass), typeof(APRTProbeDebugPass),
            // The callback dispatcher does not alter the policy. Its callbacks
            // obey the same reserved-keyword contract as other external code.
            typeof(InvokeOnRenderObjectCallbackPass),
            typeof(TAAPass), typeof(SMAAPass), typeof(PaniniProjectionPass),
            typeof(MotionBlurPass), typeof(LensFlare), typeof(DepthOfFieldPass),
            typeof(BloomPass), typeof(AutoExposurePass),
            typeof(ChromaticAberrationProcessor), typeof(LensDistortionProcessor),
            typeof(FilmGrainProcessor), typeof(VignetteProcessor),
            typeof(ChromaticAberration), typeof(LensDistortion), typeof(FilmGrain), typeof(Vignette)
        };

        internal static bool PreservesPolicy(Type type)
            => type != null && (s_reviewed.Contains(type) ||
                type.GetCustomAttribute<PreservesCameraLightingKeywordsAttribute>(false) != null);

        internal static bool PreservesShadowPolicy(Type type)
            => type != null && (s_reviewed.Contains(type) ||
                type.GetCustomAttribute<PreservesCameraLightingKeywordsAttribute>(false)?.preservesShadowKeywords == true);

        static IEnumerable<Type> AutomaticProviders()
        {
            // Match runtime discovery; inspect metadata without constructing passes.
            foreach (Type type in UltraRPShaderRenderingLayerUsage.DiscoveredModules())
                yield return type;
            foreach (Type type in TypeCache.GetTypesDerivedFrom<BasePostPass>())
                if (!type.IsAbstract)
                    yield return type;
            foreach (Type type in TypeCache.GetTypesDerivedFrom<IUberEffectProcessor>())
                if (!type.IsAbstract && !type.IsInterface)
                    yield return type;
        }

        internal static bool TryProvePreserved(IEnumerable<ScriptableRendererGraph> graphs, out string reason)
            => TryProvePreserved(graphs, false, out reason);

        internal static bool TryProveShadowPreserved(IEnumerable<ScriptableRendererGraph> graphs, out string reason)
            => TryProvePreserved(graphs, true, out reason);

        static bool TryProvePreserved(IEnumerable<ScriptableRendererGraph> graphs, bool shadows, out string reason)
        {
            if (graphs == null)
                return Failed(out reason, "No renderer graphs declared");
            Type[] automatic = AutomaticProviders().Distinct().ToArray();
            int count = 0;
            foreach (var graph in graphs)
            {
                ++count;
                if (graph == null || graph.GetType() != typeof(UltraRendererGraph) ||
                    SerializationUtility.HasManagedReferencesWithMissingTypes(graph))
                    return Failed(out reason, "Missing, unresolved or custom renderer graph");
                if (!TryProvePreservedCore(graph.nodes, automatic, shadows, out reason))
                {
                    reason = graph.name + ": " + reason;
                    return false;
                }
            }
            if (count == 0)
                return Failed(out reason, "No renderer graphs declared");
            reason = "All " + count + " graphs and applicable providers preserve camera lighting keywords";
            return true;
        }

        // Include disabled/disconnected nodes: applications can enable them later.
        // Explicit inputs keep tests independent of runtime TypeCache discovery.
        internal static bool TryProvePreserved(IEnumerable<object> nodes, IEnumerable<Type> automaticProviders,
            out string reason)
            => TryProvePreservedCore(nodes, automaticProviders, false, out reason);

        static bool TryProvePreservedCore(IEnumerable<object> nodes, IEnumerable<Type> automaticProviders,
            bool shadows, out string reason)
        {
            Func<Type, bool> preserves = shadows ? PreservesShadowPolicy : PreservesPolicy;
            if (nodes == null || automaticProviders == null)
                return Failed(out reason, "Missing graph or provider inventory");
            int count = 0;
            bool hasPostProcess = false;
            foreach (object node in nodes)
            {
                ++count;
                if (!(node is ScriptableRenderPassNode) || !preserves(node.GetType()))
                    return Failed(out reason, "Undeclared graph lighting provider: " + node?.GetType().FullName);
                if (!(node is PostProcessPass post))
                    continue;
                hasPostProcess = true;
                if (post.postPassEntries == null || post.uberEffectEntries == null)
                    return Failed(out reason, "Missing nested postprocess entries");
                foreach (var entry in post.postPassEntries)
                    if (entry == null || !preserves(entry.passType) ||
                        (entry.passInstance != null && !preserves(entry.passInstance.GetType())))
                        return Failed(out reason, "Undeclared nested postprocess lighting provider");
                foreach (var entry in post.uberEffectEntries)
                    if (entry == null || !preserves(entry.componentType))
                        return Failed(out reason, "Undeclared nested Uber component");
            }
            if (count == 0)
                return Failed(out reason, "Empty renderer graph");
            foreach (Type type in automaticProviders)
            {
                if (!hasPostProcess && type != null &&
                    (typeof(BasePostPass).IsAssignableFrom(type) || typeof(IUberEffectProcessor).IsAssignableFrom(type)))
                    continue;
                if (!preserves(type))
                    return Failed(out reason, "Undeclared automatic lighting provider: " + type?.FullName);
            }
            reason = "All graph and applicable automatic providers preserve camera lighting keywords";
            return true;
        }

        static bool Failed(out string reason, string detail)
        {
            reason = "Lighting OFF retained: " + detail + ".";
            return false;
        }
    }
}
