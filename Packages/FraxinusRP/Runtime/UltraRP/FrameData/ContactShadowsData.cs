using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    // Snapshot prepared once per camera before resource allocation. Injected execution
    // reads this data without consulting mutable Volume or renderer module state.
    public sealed class ContactShadowsData : ContextItem
    {
        internal bool active;
        internal ComputeShader shader;
        internal int kernel, width, height;
        internal Matrix4x4 projection;
        internal Vector4 lightDirection, ray, fade, sampling;

        public override void Reset()
        {
            active = false;
            shader = null;
            kernel = width = height = 0;
            projection = default;
            lightDirection = ray = fade = sampling = default;
        }
    }
}
