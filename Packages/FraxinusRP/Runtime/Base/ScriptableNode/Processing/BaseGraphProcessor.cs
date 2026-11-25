using System;
using System.Collections.Generic;

namespace FraxinusEngine.Base
{
    public abstract  class BaseGraphProcessor : IDisposable
    {
        protected BaseGraph _graph;

        public BaseGraphProcessor(BaseGraph graph)
        {
            _graph = graph;

            UpdateComputeOrder(graph.graphOutputs);
            UpdateConnectivity();

            _graph.afterComputedOrder += UpdateComputeOrder;
            _graph.onConnectivityChanged += UpdateConnectivity;
        }

        ~BaseGraphProcessor() => Dispose();

        public void Dispose()
        {
            _graph.onConnectivityChanged -= UpdateConnectivity;
            _graph.afterComputedOrder -= UpdateComputeOrder;

            Dispose(true);
        }

        public abstract void UpdateComputeOrder(IEnumerable<BaseNode> computedNodes);

        public abstract void UpdateConnectivity();

        public abstract void Run();

        protected virtual void Dispose(bool dispose) { }
    }
}
