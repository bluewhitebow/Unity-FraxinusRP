using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>Short screen-space shadows for the main directional light.</summary>
    [Serializable, VolumeComponentMenu("Lighting/Contact Shadows")]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    public sealed class ContactShadows : VolumeComponent, IPostProcessComponent
    {
        public BoolParameter enable = new BoolParameter(false);

        [Tooltip("Maximum ray length in world units. Only geometry in the camera depth buffer can cast contact shadows.")]
        public ClampedFloatParameter length = new ClampedFloatParameter(0.15f, 0f, 5f);

        public ClampedFloatParameter opacity = new ClampedFloatParameter(1f, 0f, 1f);

        [Tooltip("Blend between a fixed world-space ray length and a ray length proportional to view depth.")]
        public ClampedFloatParameter distanceScale = new ClampedFloatParameter(0f, 0f, 1f);

        [Tooltip("Maximum view depth at which contact shadows are evaluated, in world units.")]
        public MinFloatParameter maxDistance = new MinFloatParameter(50f, 0f);

        [Tooltip("Distance over which contact shadows fade out before Max Distance.")]
        public MinFloatParameter fadeDistance = new MinFloatParameter(10f, 0f);

        [Tooltip("Move the ray origin toward the camera by this many world units to reduce self-shadowing.")]
        public ClampedFloatParameter bias = new ClampedFloatParameter(0.002f, 0f, 0.1f);

        [Tooltip("Maximum depth difference accepted as an occluder, in world units. Larger values can create halos.")]
        public ClampedFloatParameter thickness = new ClampedFloatParameter(0.02f, 0.001f, 1f);

        public ClampedIntParameter sampleCount = new ClampedIntParameter(16, 4, 64);

        public bool IsActive() => enable.value && length.value > 0f && opacity.value > 0f && maxDistance.value > 0f;
    }
}
