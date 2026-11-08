using System;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable, VolumeComponentMenu("Post-processing/Panini Projection")]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    public sealed class PaniniProjection : VolumeComponent, IPostProcessComponent
    {
        public ClampedFloatParameter distance = new ClampedFloatParameter(0f, 0f, 1f);
        public ClampedFloatParameter cropToFit = new ClampedFloatParameter(1f, 0f, 1f);
        public bool IsActive() => distance.value > 0f;
    }
}
