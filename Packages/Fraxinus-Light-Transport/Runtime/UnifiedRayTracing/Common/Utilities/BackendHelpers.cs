using System;

namespace UnityEngine.Rendering.UnifiedRayTracing
{
    internal static class BackendHelpers
    {
        internal static string GetFileNameOfShader(RayTracingBackend backend, string fileName)
        {
            string postFix = backend switch
            {
#if UNITY_6000_0_OR_NEWER
                RayTracingBackend.Hardware => "raytrace",
#endif
                RayTracingBackend.Compute => "compute",
                _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
            };
            return $"{fileName}.{postFix}";
        }

        internal static Type GetTypeOfShader(RayTracingBackend backend)
        {
            Type shaderType = backend switch
            {
#if UNITY_6000_0_OR_NEWER
                RayTracingBackend.Hardware => typeof(RayTracingShader),
#endif
                RayTracingBackend.Compute => typeof(ComputeShader),
                _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
            };
            return shaderType;
        }
    }
}
