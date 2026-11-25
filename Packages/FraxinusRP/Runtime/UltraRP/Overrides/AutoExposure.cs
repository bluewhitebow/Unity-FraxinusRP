using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>Histogram metering and temporal exposure adaptation before Bloom and color grading.</summary>
    [Serializable, VolumeComponentMenu("Post-processing/Auto Exposure")]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    public sealed class AutoExposure : VolumeComponent, IPostProcessComponent
    {
        [Tooltip("Enable histogram auto exposure before Bloom and color grading.")]
        public BoolParameter enabled = new BoolParameter(false);

        [Tooltip("Ignore luminance samples below this percentile. Raise this to exclude dark areas from metering.")]
        public ClampedFloatParameter lowPercent = new ClampedFloatParameter(10f, 0f, 99f);

        [Tooltip("Ignore luminance samples above this percentile. Lower this to exclude small bright highlights.")]
        public ClampedFloatParameter highPercent = new ClampedFloatParameter(90f, 1f, 100f);

        [Tooltip("Minimum metered EV100 (ISO 100, calibration constant 12.5). Limits brightening in darkness.")]
        public ClampedFloatParameter minEV = new ClampedFloatParameter(-8f, -16f, 24f);

        [Tooltip("Maximum metered EV100. Limits darkening in bright scenes.")]
        public ClampedFloatParameter maxEV = new ClampedFloatParameter(16f, -16f, 24f);

        [Tooltip("Exposure compensation in stops. Positive values brighten the image. Metering targets 18% middle gray.")]
        public ClampedFloatParameter compensation = new ClampedFloatParameter(0f, -16f, 16f);

        [Tooltip("Adaptation rate when moving from a dark scene into a bright scene, in inverse seconds.")]
        public MinFloatParameter speedDarkToLight = new MinFloatParameter(3f, 0.001f);

        [Tooltip("Adaptation rate when moving from a bright scene into a dark scene, in inverse seconds.")]
        public MinFloatParameter speedLightToDark = new MinFloatParameter(1f, 0.001f);

        public bool IsActive() => enabled.value;

        [Obsolete("Unused #from(2023.1)", false)]
        public bool IsTileCompatible() => false;
    }
}
