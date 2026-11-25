using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// PRT bake cubemap resolution
    /// </summary>
    public enum PRTBakeResolution
    {
        [InspectorName("128 * 128")]
        _128 = 128,
        [InspectorName("256 * 256")]
        _256 = 256,
        [InspectorName("512 * 512")]
        _512 = 512
    }

    /// <summary>
    /// PRT bake interface (Runtime可用)
    /// </summary>
    internal interface IPRTBaker
    {
        /// <summary>
        /// Bake surfel data at the specified position.
        /// </summary>
        /// <param name="probePosition">探针位置</param>
        /// <returns>Surfel数组</returns>
        Surfel[] BakeSurfelData(Vector3 probePosition);
    }
}
