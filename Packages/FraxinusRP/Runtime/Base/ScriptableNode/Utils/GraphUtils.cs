using System;
using System.Collections.Generic;
using System.Linq;

namespace FraxinusEngine.Base
{
    public static class GraphUtils
    {
        enum State
        { 
            White,
            Grey,
            Black
        }

        class TraversalNode
        {
            public BaseNode node;
            public List<TraversalNode> inputs = new List<TraversalNode>();
            public List<TraversalNode> outputs = new List<TraversalNode>();
            public State state = State.White;

            public TraversalNode(BaseNode node) 
            {
                this.node = node;
            }
        }

        class TraversalGraph
        {
            public List<TraversalNode> nodes = new List<TraversalNode>();
            public List<TraversalNode> outputs = new List<TraversalNode> ();
        }

        static TraversalGraph ConvertGraphToTraversalGraph(BaseGraph graph)
        {
            TraversalGraph g = new TraversalGraph();
            Dictionary<BaseNode, TraversalNode> nodeMap = new Dictionary<BaseNode, TraversalNode>();

            foreach (var node in graph.nodes)
            {
                var tn = new TraversalNode(node);
                g.nodes.Add(tn);
                nodeMap[node] = tn;

                if (graph.graphOutputs.Contains(node))
                    g.outputs.Add(tn);
            }

            foreach (var tn in g.nodes)
            {
                tn.inputs = tn.node.GetInputNodes().Where(n => nodeMap.ContainsKey(n)).Select(n => nodeMap[n]).ToList();
                tn.outputs = tn.node.GetOutputNodes().Where(n => nodeMap.ContainsKey(n)).Select(n => nodeMap[n]).ToList();
            }

            return g;
        }

        public static void FindCyclesInGraph(BaseGraph g, Action<BaseNode> cyclicNode)
        {
            var graph = ConvertGraphToTraversalGraph(g);
            List<TraversalNode> cyclicNodes = new List<TraversalNode>();

            foreach (var n in graph.nodes)
                DFS(n);

            void DFS(TraversalNode n)
            {
                if (n.state == State.Black)
                    return;

                n.state = State.Grey;

                foreach (var input in n.inputs)
                {
                    if (input.state == State.White)
                        DFS(input);
                    else if (input.state == State.Grey)
                        cyclicNodes.Add(n);
                }
                n.state = State.Black;
            }

            cyclicNodes.ForEach((tn) => cyclicNode?.Invoke(tn.node));
        }

        public static List<BaseNode> DepthFirstSort(BaseGraph g, BaseNode specifyNode = null)
        {
            var graph = ConvertGraphToTraversalGraph(g);
            List<BaseNode> depthFirstNodes = new List<BaseNode>();

            foreach (var n in graph.nodes)
            {
                if (specifyNode is not null && specifyNode != n.node)
                    continue;

                DFS(n);
            }

            void DFS(TraversalNode n)
            {
                if (n.state == State.Black)
                    return;

                n.state = State.Grey;

                if (n.node is not ParameterNode parameterNode)
                {
                    foreach (var input in n.inputs)
                    {
                        if (input.state == State.White)
                            DFS(input);
                    }
                }

                n.state = State.Black;

                // Only add the node when his children are completely visited
                depthFirstNodes.Add(n.node);
            }

            return depthFirstNodes;
        }
    }
}
