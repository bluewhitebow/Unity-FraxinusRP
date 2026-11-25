using System.Linq;
using System.Collections.Generic;

namespace FraxinusEngine.Base
{
    public class ProcessGraphProcessor : BaseGraphProcessor
    {
        List<BaseNode> _processList;

        public ProcessGraphProcessor(BaseGraph graph) : base(graph) { }

        public override void UpdateComputeOrder(IEnumerable<BaseNode> computedNodes)
        {
            // BaseGraph publishes its terminal nodes; execution still needs every dependency.
            _processList = _graph.nodes.Where(n => n.computeOrder >= 0).OrderBy(n => n.computeOrder).ToList();
        }
        public override void UpdateConnectivity() { }

        public override void Run()
        {
            int count = _processList.Count;
            for (int i = 0; i < count; i++)
                _processList[i].OnProcess();
        }

    }
}
