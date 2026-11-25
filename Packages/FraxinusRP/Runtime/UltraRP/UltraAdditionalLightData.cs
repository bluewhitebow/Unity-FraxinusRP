using System;
using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    [Flags]
    public enum LightLayerEnum
    {
        /// <summary>The light will no affect any object.</summary>
        Nothing = 0,
        /// <summary>Light Layer 0.</summary>
        LightLayerDefault = 1 << 0,
        /// <summary>Light Layer 1.</summary>
        LightLayer1 = 1 << 1,
        /// <summary>Light Layer 2.</summary>
        LightLayer2 = 1 << 2,
        /// <summary>Light Layer 3.</summary>
        LightLayer3 = 1 << 3,
        /// <summary>Light Layer 4.</summary>
        LightLayer4 = 1 << 4,
        /// <summary>Light Layer 5.</summary>
        LightLayer5 = 1 << 5,
        /// <summary>Light Layer 6.</summary>
        LightLayer6 = 1 << 6,
        /// <summary>Light Layer 7.</summary>
        LightLayer7 = 1 << 7,
        /// <summary>Everything.</summary>
        Everything = 0xFF, // Custom name for "Everything" option
    }

    public static class LightExtensions
    {
        public static UltraAdditionalLightData GetUltraAdditionalLightData(this Light light)
        {
            var lightData = light.GetExistingAdditionalLightData();
            return lightData != null ? lightData : light.gameObject.AddComponent<UltraAdditionalLightData>();
        }

        internal static UltraAdditionalLightData GetExistingAdditionalLightData(this Light light)
        {
            if (light.TryGetComponent<UltraAdditionalLightData>(out var lightData))
                return lightData;

            return null;
        }
    }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    public partial class UltraAdditionalLightData : MonoBehaviour
    {
        [SerializeField] bool _usePipelineSettings = true;
        public bool usePipelineSettings
        {
            get { return _usePipelineSettings; }
            set { _usePipelineSettings = value; }
        }

        /// <summary>
        /// Value used to indicate custom shadow resolution tier for additional lights.
        /// </summary>
        public static readonly int additionalLightsShadowResolutionTierCustom = -1;

        /// <summary>
        /// Value used to indicate low shadow resolution tier for additional lights.
        /// </summary>
        public static readonly int additionalLightsShadowResolutionTierLow = 0;

        /// <summary>
        /// Value used to indicate medium shadow resolution tier for additional lights.
        /// </summary>
        public static readonly int additionalLightsShadowResolutionTierMedium = 1;

        /// <summary>
        /// Value used to indicate high shadow resolution tier for additional lights.
        /// </summary>
        public static readonly int additionalLightsShadowResolutionTierHigh = 2;

        /// <summary>
        /// The default shadow resolution tier for additional lights.
        /// </summary>
        public static readonly int additionalLightsShadowDefaultResolutionTier = additionalLightsShadowResolutionTierHigh;

        /// <summary>
        /// The default custom shadow resolution for additional lights.
        /// </summary>
        public static readonly int additionalLightsShadowDefaultCustomResolution = 128;

        [NonSerialized] private Light _light;

        /// <summary>
        /// Returns the cached light component associated with the game object that owns this light data.
        /// </summary>
#if UNITY_EDITOR
        internal new Light light
#else
        internal Light light
#endif
        {
            get
            {
                if (!_light)
                    TryGetComponent(out _light);

                return _light;
            }
        }

        /// <summary>
        /// The minimum shadow resolution for additional lights.
        /// </summary>
        public static readonly int additionalLightsShadowMinimumResolution = 128;

        [Tooltip("Controls if light shadow resolution uses pipeline settings.")]
        [SerializeField] int _additionalLightsShadowResolutionTier = additionalLightsShadowDefaultResolutionTier;

        /// <summary>
        /// Returns the selected shadow resolution tier.
        /// </summary>
        public int additionalLightsShadowResolutionTier
        {
            get { return _additionalLightsShadowResolutionTier; }
        }

        [SerializeField] uint _renderingLayers = 1;
        public uint renderingLayers
        {
            get
            {
                return _renderingLayers;
            }
            set
            {
                if (_renderingLayers != value)
                {
                    _renderingLayers = value;
                    SyncLightAndShadowLayers();
                }
            }
        }

        [SerializeField] bool _customShadowLayers = false;
        public bool customShadowLayers
        {
            get
            {
                return _customShadowLayers;
            }
            set
            {
                if (_customShadowLayers != value)
                {
                    _customShadowLayers = value;
                    SyncLightAndShadowLayers();
                }
            }
        }

        [SerializeField] uint _shadowRenderingLayers = 1;
        public uint shadowRenderingLayers
        {
            get
            {
                return _shadowRenderingLayers;
            }
            set
            {
                if (value != _shadowRenderingLayers)
                {
                    _shadowRenderingLayers = value;
                    SyncLightAndShadowLayers();
                }
            }
        }

        [Tooltip("Controls the size of the cookie mask currently assigned to the light.")]
        public Vector2 lightCookieSize
        {
            get => _lightCookieSize;
            set => _lightCookieSize = value;
        }
        [SerializeField] Vector2 _lightCookieSize = Vector2.one;

        [Tooltip("Controls the offset of the cookie mask currently assigned to the light.")]
        public Vector2 lightCookieOffset
        {
            get => _lightCookieOffset;
            set => _lightCookieOffset = value;
        }
        [SerializeField] Vector2 _lightCookieOffset = Vector2.zero;

        [Tooltip("Controls the filtering quality of soft shadows. Higher quality has lower performance.")]
        public SoftShadowQuality softShadowQuality
        {
            get => _softShadowQuality;
            set => _softShadowQuality = value;
        }
        [SerializeField] private SoftShadowQuality _softShadowQuality = SoftShadowQuality.UsePipelineSettings;


        /// <summary>
        /// Rim Light contribution.
        /// </summary>
        [SerializeField] float _rimContribution = 1.0f;
        public float rimContribution
        {
            get => _rimContribution;
            set { _rimContribution = value; }
        }

        /// <summary>
        /// Outline Light contribution.
        /// </summary>
        [SerializeField] float _outlineContribution = 1.0f;
        public float outlineContribution
        {
            get => _outlineContribution;
            set { _outlineContribution = value; }
        }

        private void SyncLightAndShadowLayers()
        {
            if (light)
                light.renderingLayerMask = _customShadowLayers ? (int)_shadowRenderingLayers : (int)_renderingLayers;
        }

    }
}
