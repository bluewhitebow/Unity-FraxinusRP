// force Burst cache invalidation on version change.
[assembly: System.Reflection.AssemblyVersion(Fraxinus.GPUBatcher.GPUBatcherVersion.Version + ".0")]

namespace Fraxinus.GPUBatcher
{
    public static class GPUBatcherVersion
    {
        public const string Version = "0.1.0";
    }
}
