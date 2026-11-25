using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable, VolumeComponentMenu("Post-processing/Chromatic Aberration")]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    public sealed class ChromaticAberration : VolumeComponent, IPostProcessComponent
    {
        /// <summary>
        /// Controls the strength of the chromatic aberration effect.
        /// </summary>
        [Tooltip("Use the slider to set the strength of the Chromatic Aberration effect.")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f);

        /// <inheritdoc/>
        public bool IsActive() => intensity.value > 0f;
    }
}

