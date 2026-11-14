using System;
using System.Collections.Generic;
using System.Linq;
using FraxinusEngine.Base;
using FraxinusEngine.Rendering.Ultra;

namespace FraxinusEditor.Rendering.Ultra
{
    // Like URP's stripShadowsOffVariants, this relies on the shadow passes
    // rendering an empty map when distance, visible lights or casters are zero.
    // The graph must actually execute those passes before receiver draws.
    internal static class UltraRPShaderShadowUsage
    {
        internal static bool TryProveRequired(IEnumerable<ScriptableRendererGraph> graphs, out string reason)
        {
            var declared = graphs?.ToArray();
            if (!UltraRPShaderLightingUsage.TryProveShadowPreserved(declared, out reason))
                return false;
            foreach (var graph in declared)
            {
                if (!TryProveGraph(graph, out reason))
                {
                    reason = graph.name + ": " + reason;
                    return false;
                }
            }
            reason = "All declared graphs execute active main/additional shadow passes before receiver draws; " +
                "providers preserve their globals. Empty maps also cover offscreen-depth cameras.";
            return true;
        }

        internal static bool TryProveGraph(ScriptableRendererGraph graph, out string reason)
        {
            if (graph == null || graph.GetType() != typeof(UltraRendererGraph) || graph.nodes == null ||
                UnityEditor.SerializationUtility.HasManagedReferencesWithMissingTypes(graph))
                return Failed(out reason, "Missing, unresolved or custom renderer graph");

            // Match ScriptableRenderer.UpdateComputeOrder's dependency roots and
            // ordering without constructing a renderer or executing user callbacks.
            var pending = new Stack<BaseNode>();
            if (graph.postNode != null) pending.Push(graph.postNode);
            if (graph.drivenNode != null) pending.Push(graph.drivenNode);
            var pairedBegins = new HashSet<ContextBeginNode>();
            foreach (var node in graph.nodes)
            {
                if (!(node is ContextEndNode end) || node.computeOrder < 0) continue;
                var begin = FindPairedContextBegin(end);
                if (begin == null || begin.computeOrder < 0)
                    return Failed(out reason, "Unpaired context end has unproven execution dependencies");
                pairedBegins.Add(begin);
                pending.Push(end);
            }
            foreach (var node in graph.nodes)
                if (node is ContextBeginNode begin && node.computeOrder >= 0 && !pairedBegins.Contains(begin))
                    return Failed(out reason, "Unpaired context begin has unproven execution dependencies");
            var reachable = new HashSet<BaseNode>();
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                if (node == null || !graph.nodes.Contains(node))
                    return Failed(out reason, "Missing graph dependency");
                if (!reachable.Add(node)) continue;
                foreach (var input in node.GetInputNodes()) pending.Push(input);
            }
            return TryProveExecutionOrder(reachable.Where(node => node.computeOrder >= 0), out reason);
        }

        internal static bool TryProveExecutionOrder(IEnumerable<BaseNode> nodes, out string reason)
        {
            if (nodes == null)
                return Failed(out reason, "No executable graph nodes");
            var passes = new List<ScriptableRenderPassNode>();
            foreach (var node in nodes)
            {
                if (!(node is ScriptableRenderPassNode pass))
                    return Failed(out reason, "Unknown executable graph node");
                if (!pass.active || pass.computeOrder < 0) continue;
                passes.Add(pass);
            }
            var ordered = passes.OrderBy(RenderOrderKey).ThenBy(pass => pass.computeOrder).ToArray();
            var main = ordered.FirstOrDefault(pass => pass.GetType() == typeof(MainLightShadowCasterPass));
            var additional = ordered.FirstOrDefault(pass => pass.GetType() == typeof(AdditionalLightsShadowCasterPass));
            if (main == null || additional == null)
                return Failed(out reason, "An active reachable main/additional shadow pass is missing");
            foreach (var pass in ordered)
            {
                Type type = pass.GetType();
                if (type == typeof(MainLightShadowCasterPass) || type == typeof(AdditionalLightsShadowCasterPass) ||
                    type == typeof(ColorGradingLutPass)) continue;
                // Runtime gathers dependencies in a HashSet: equal sort keys do
                // not prove which pass executes first, regardless of input order.
                if (!StrictlyBefore(main, pass) || !StrictlyBefore(additional, pass))
                    return Failed(out reason, "Receiver or callback may execute before shadow setup: " + type.Name);
            }
            reason = "Active main/additional shadow passes precede receiver draws";
            return true;
        }

        static int RenderOrderKey(ScriptableRenderPassNode pass)
            => pass.renderOrder == RenderOrder.BeforeDrivenPass ? -2 : (int)pass.renderOrder;

        static bool StrictlyBefore(ScriptableRenderPassNode first, ScriptableRenderPassNode second)
            => RenderOrderKey(first) < RenderOrderKey(second) ||
                RenderOrderKey(first) == RenderOrderKey(second) && first.computeOrder < second.computeOrder;

        // Same nesting walk used by ScriptableRenderer.FindPairedContextBegin.
        static ContextBeginNode FindPairedContextBegin(ContextEndNode end)
        {
            var visited = new HashSet<BaseNode>();
            var queue = new Queue<(BaseNode node, int nesting)>();
            foreach (var input in end.GetInputNodes()) queue.Enqueue((input, 0));
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                if (item.node == null || !visited.Add(item.node)) continue;
                int nesting = item.nesting;
                if (item.node is ContextEndNode) ++nesting;
                if (item.node is ContextBeginNode begin)
                {
                    if (nesting == 0) return begin;
                    --nesting;
                }
                foreach (var input in item.node.GetInputNodes()) queue.Enqueue((input, nesting));
            }
            return null;
        }

        static bool Failed(out string reason, string detail)
        {
            reason = "Shadow OFF retained: " + detail + ".";
            return false;
        }
    }
}
