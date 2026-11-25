using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Volume component for reflection normalization parameters.
    /// </summary>
    [VolumeComponentMenu("UltraRP/Reflection Normalization")]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    public sealed class ReflectionNormalization : VolumeComponent, IPostProcessComponent
    {
        #region Public Attributes

        [Tooltip("Enable reflection normalization to prevent unnatural reflection probe brightness.")]
        public BoolParameter enable = new(false, BoolParameter.DisplayType.EnumPopup);
        
        [Tooltip("Minimum normalization factor to prevent over-dark reflections.")]
        public ClampedFloatParameter minNormalizationFactor = new(0.1f, 0.01f, 1.0f);

        [Tooltip("Maximum normalization factor to prevent over-bright reflections.")]
        public ClampedFloatParameter maxNormalizationFactor = new(1.0f, 1.0f, 10.0f);

        [Tooltip("Weight for blending between normalized and original reflection values.")]
        public ClampedFloatParameter probeVolumeWeight = new(1.0f, 0.0f, 1.0f);

        #endregion

        #region Initialization Methods

        public ReflectionNormalization()
        {
            displayName = "Reflection Normalization";
        }

        #endregion

        #region Volume Component Methods

        private void OnValidate()
        {
            // Ensure min is less than max
            if (minNormalizationFactor.value >= maxNormalizationFactor.value)
            {
                maxNormalizationFactor.value = minNormalizationFactor.value + 0.1f;
            }
        }

        #endregion

        #region IPostProcessComponent Methods

        /// <summary>
        /// <inheritdoc/>
        /// </summary>
        /// <returns></returns>
        public bool IsActive()
        {
            return enable.value && probeVolumeWeight.value > 0.0f;
        }

        #endregion
    }
}
