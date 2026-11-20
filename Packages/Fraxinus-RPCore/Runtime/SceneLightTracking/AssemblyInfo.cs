using System.Runtime.CompilerServices;

// Keep this bridge's assembly name: Unity grants
// Unity.RenderPipelines.Universal.2D.Internal access to ObjectDispatcher.
// It lives in Core so both pipelines can share scene tracking independently.
[assembly: InternalsVisibleTo("Unity.RenderPipelines.Universal.Runtime")]
[assembly: InternalsVisibleTo("Fraxinus.Runtime")]
[assembly: InternalsVisibleTo("Fraxinus.GPUBatcher.Runtime")]
