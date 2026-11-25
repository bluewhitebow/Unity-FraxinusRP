using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Shape of a spot light(HDRP Preset)
    /// </summary>
    public enum SpotLightShape
    {
        /// <summary>Cone shape. The default shape of the spot light.</summary>
        Cone,

        /// <summary>Pyramid shape.</summary>
        Pyramid,

        /// <summary>Box shape. Similar to a directional light but with bounds.</summary>
        Box
    }


    partial class UltraAdditionalLightData
    {
        /// <summary>
        /// Angular diameter of the emissive celestial body represented by the light as seen from the camera (in degrees).
        /// Used to render the sun/moon disk.
        /// </summary>
        [SerializeField] float _angularDiameter = 0.5f;

        public float angularDiameter
        {
            get => _angularDiameter;
            set { _angularDiameter = value; }
        }

        /// <summary>
        /// Only for Punctual/Sphere/Disc. Default shape radius is not 0 so that specular highlight is visible by default, it matches the previous default of 0.99 for MaxSmoothness.
        /// </summary>
        [SerializeField] float _shapeRadius = 0.025f;
        public float shapeRadius
        {
            get => _shapeRadius;
            set { _shapeRadius = value; }
        }

        // Light contributions
        /// <summary>
        /// Base Light contribution.
        /// </summary>
        [SerializeField] float _baseContribution = 1.0f;

        public float baseContribution
        {
            get => _baseContribution;
            set { _baseContribution = value; }
        }
    }
}
