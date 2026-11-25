using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Utility class for component checks.
    /// </summary>
    public static class ComponentUtility
    {
        /// <summary> Check if the provided camera is compatible with Ultra Render Pipeline </summary>
        /// <param name="camera">The Camera to check</param>
        /// <returns>True if it is compatible, false otherwise</returns>
        public static bool IsUltraCamera(Camera camera)
            => camera.GetComponent<UltraAdditionalCameraData>() != null;

        /// <summary> Check if the provided light is compatible with Ultra Render Pipeline </summary>
        /// <param name="light">The Light to check</param>
        /// <returns>True if it is compatible, false otherwise</returns>
        public static bool IsUltraLight(Light light)
            => light.GetComponent<UltraAdditionalLightData>() != null;
    }
}
