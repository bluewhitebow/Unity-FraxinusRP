namespace FraxinusEngine.Base
{
    /// <summary>
    /// Compatibility processor for graphs that previously selected the Jobs processor.
    /// Nodes expose managed Process/port callbacks, not a Unity Jobs scheduling contract.
    /// Execute synchronously in dependency order; Run returns after outputs are produced.
    /// </summary>
    public class JobGraphProcessor : ProcessGraphProcessor
    {
        public bool usesSynchronousFallback => true;

        public JobGraphProcessor(BaseGraph graph) : base(graph) { }
    }
}
