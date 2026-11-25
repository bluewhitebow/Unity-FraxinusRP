using UnityEngine;
using UnityEngine.Serialization;

namespace FraxinusEngine.Rendering.Ultra
{
    partial class UltraAdditionalLightData
    {
        [Range(0.0f, 16.0f), SerializeField] float _volumetricDimmer = 1.0f;

        [SerializeField] bool _affectsVolumetric = true;

        // Not used for directional lights.
        [SerializeField] float _volumetricFadeDistance = 10000.0f;

        /// <summary>
        /// Get/Set the light dimmer / multiplier on volumetric effects, between 0 and 16.
        /// </summary>
        public float volumetricDimmer
        {
            get => _affectsVolumetric ? _volumetricDimmer : 0.0f;
            set => _volumetricDimmer = Mathf.Clamp(value, 0.0f, 16.0f);
        }


        public bool affectsVolumetric
        {
            get => _affectsVolumetric;
            set => _affectsVolumetric = value;
        }


        /// <summary>
        /// Get/Set the light fade distance for volumetrics.
        /// </summary>
        public float volumetricFadeDistance
        {
            get => _volumetricFadeDistance;
            set => _volumetricFadeDistance = Mathf.Clamp(value, 0, float.MaxValue);
        }
    }
}