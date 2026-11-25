using System;

namespace UnityEngine.Rendering
{
    /// <summary>Per-camera relighting and receiving options for Adaptive PRT Volumes.</summary>
    [Serializable, VolumeComponentMenu("Lighting/APRTV Options"), SupportedOnRenderPipeline]
    public sealed class APRTOptions : VolumeComponent
    {
        APRTOptions() { displayName = "APRTV Options"; }

        /// <summary>Multiplier for the final indirect lighting.</summary>
        [Tooltip("Multiplier for the final APRTV indirect lighting.")]
        public ClampedFloatParameter giIntensity = new ClampedFloatParameter(1, 0, 50);

        /// <summary>Multiplier for the directional-light contribution.</summary>
        [Tooltip("Multiplier for the directional-light contribution. Sky lighting has its own intensity.")]
        public ClampedFloatParameter indirectIntensity = new ClampedFloatParameter(.56f, 0, 10);

        /// <summary>Contrast of the directional-light response.</summary>
        public ClampedFloatParameter indirectContrast = new ClampedFloatParameter(15, 1, 15);

        /// <summary>Multiplier for scene-skybox irradiance transmitted through the baked geometry.</summary>
        [Tooltip("Multiplier for sky irradiance from the scene skybox transmitted through the baked geometry, including a static diffuse bounce when baked. Works independently of directional lights.")]
        public MinFloatParameter skyIntensity = new MinFloatParameter(0, 0);

        /// <summary>Saturation of the directional-light response.</summary>
        public ClampedFloatParameter saturationBoost = new ClampedFloatParameter(1.49f, 0, 5);

        /// <summary>Residual directional-light contribution in occluded regions.</summary>
        public ClampedFloatParameter shadowGIVisibility = new ClampedFloatParameter(.009f, 0, 1);

        /// <summary>Power applied to the directional visibility response.</summary>
        public ClampedFloatParameter skyVisibilityPower = new ClampedFloatParameter(.32f, .25f, 8);

        /// <summary>Strength of the upward-facing surface response.</summary>
        public ClampedFloatParameter floorBoost = new ClampedFloatParameter(2.5f, 0, 15);

        /// <summary>Power of the upward-facing surface response.</summary>
        public ClampedFloatParameter floorPower = new ClampedFloatParameter(2, 1, 10);

        /// <summary>Weight of new lighting in each relighting update.</summary>
        [Tooltip("Weight of new lighting in each relighting update. One applies changes immediately.")]
        public ClampedFloatParameter temporalBlend = new ClampedFloatParameter(1, .01f, 1);

        /// <summary>Use baked visibility to reduce receiving leaks.</summary>
        [Tooltip("Use baked visibility to reduce receiving leaks.")]
        public BoolParameter leakReduction = new BoolParameter(false);

        /// <summary>Maximum tolerated backface fraction for sampled probes.</summary>
        [Tooltip("Maximum tolerated backface fraction for sampled probes.")]
        public ClampedFloatParameter probeInvalidityThreshold = new ClampedFloatParameter(.25f, 0, 1);

        /// <summary>Bias used for baked visibility comparisons, in world units.</summary>
        public MinFloatParameter visibilityBias = new MinFloatParameter(.01f, 0);

        /// <summary>Normal offset of the receiving position, in world units.</summary>
        public MinFloatParameter normalBias = new MinFloatParameter(0, 0);

        /// <inheritdoc/>
        public override void Override(VolumeComponent state, float interpFactor)
        {
            if (state is APRTOptions target)
            {
                var set = APRTProbeReferenceVolume.instance.currentBakingSet;
                var data = set?.GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
                // Avoid validating all probe payloads during Volume blending. The
                // runtime validates the bake before it can be rendered.
                var model = data != null && data.sampleCount > 0 ? data.lightingModel : APRTLightingModel.APRTV;
                target.SeedUnmodifiedParameters(APRTRelightSettings.FromBakingSet(set, model), GetDefaultOptions());
            }
            base.Override(state, interpFactor);
        }

        internal static APRTOptions GetDefaultOptions()
        {
            var manager = VolumeManager.instance;
            return manager.isInitialized ? manager.GetVolumeComponentDefaultState(typeof(APRTOptions)) as APRTOptions : null;
        }

        void SeedUnmodifiedParameters(APRTRelightSettings settings, APRTOptions defaults)
        {
            // Stack state is reset each update. Seed only untouched parameters so
            // local Volume weights blend from the existing Baking Set appearance
            // and higher-priority Volumes blend from lower-priority overrides.
            Seed(giIntensity, settings.giIntensity, defaults?.giIntensity);
            Seed(indirectIntensity, settings.indirectIntensity, defaults?.indirectIntensity);
            Seed(indirectContrast, settings.indirectContrast, defaults?.indirectContrast);
            Seed(skyIntensity, settings.skyIntensity, defaults?.skyIntensity);
            Seed(saturationBoost, settings.saturationBoost, defaults?.saturationBoost);
            Seed(shadowGIVisibility, settings.shadowGIVisibility, defaults?.shadowGIVisibility);
            Seed(skyVisibilityPower, settings.skyVisibilityPower, defaults?.skyVisibilityPower);
            Seed(floorBoost, settings.floorBoost, defaults?.floorBoost);
            Seed(floorPower, settings.floorPower, defaults?.floorPower);
            Seed(temporalBlend, settings.temporalBlend, defaults?.temporalBlend);
            Seed(leakReduction, settings.leakReduction, defaults?.leakReduction);
            Seed(probeInvalidityThreshold, settings.probeInvalidityThreshold, defaults?.probeInvalidityThreshold);
            Seed(visibilityBias, settings.visibilityBias, defaults?.visibilityBias);
            Seed(normalBias, settings.normalBias, defaults?.normalBias);
        }

        static void Seed<T>(VolumeParameter<T> parameter, T value, VolumeParameter<T> defaultParameter)
        {
            if (!parameter.overrideState && defaultParameter?.overrideState != true) parameter.value = value;
        }

        internal void ApplySettings(APRTRelightSettings settings)
        {
            giIntensity.value = settings.giIntensity;
            indirectIntensity.value = settings.indirectIntensity;
            indirectContrast.value = settings.indirectContrast;
            skyIntensity.value = settings.skyIntensity;
            saturationBoost.value = settings.saturationBoost;
            shadowGIVisibility.value = settings.shadowGIVisibility;
            skyVisibilityPower.value = settings.skyVisibilityPower;
            floorBoost.value = settings.floorBoost;
            floorPower.value = settings.floorPower;
            temporalBlend.value = settings.temporalBlend;
            leakReduction.value = settings.leakReduction;
            probeInvalidityThreshold.value = settings.probeInvalidityThreshold;
            visibilityBias.value = settings.visibilityBias;
            normalBias.value = settings.normalBias;
        }
    }

    // Immutable camera snapshot shared by render passes, with old serialized
    // Baking Set values used only for parameters not overridden by a Volume.
    internal readonly struct APRTRelightSettings
    {
        internal readonly float giIntensity, indirectIntensity, indirectContrast, skyIntensity;
        internal readonly float saturationBoost, shadowGIVisibility, skyVisibilityPower, floorBoost, floorPower;
        internal readonly float temporalBlend, probeInvalidityThreshold, visibilityBias, normalBias;
        internal readonly bool leakReduction;
        internal readonly float bounceIntensity;
        internal readonly bool addAmbientLighting;

        APRTRelightSettings(ProbeVolumeBakingSet set, APRTLightingModel model)
        {
            giIntensity = set != null ? set.aprtGIIntensity : 1;
            indirectIntensity = set != null ? set.aprtIndirectIntensity : .56f;
            indirectContrast = set != null ? set.aprtIndirectContrast : 15;
            skyIntensity = set != null ? (model == APRTLightingModel.APRTV ? set.aprtReferenceSkyIntensity : set.aprtSkyIntensity) : 0;
            saturationBoost = set != null ? set.aprtSaturationBoost : 1.49f;
            shadowGIVisibility = set != null ? set.aprtShadowGIVisibility : .009f;
            skyVisibilityPower = set != null ? set.aprtSkyVisibilityPower : .32f;
            floorBoost = set != null ? set.aprtFloorBoost : 2.5f;
            floorPower = set != null ? set.aprtFloorPower : 2;
            temporalBlend = set != null ? set.aprtTemporalBlend : 1;
            leakReduction = set != null && set.aprtLeakReduction;
            probeInvalidityThreshold = set != null ? set.aprtProbeInvalidityThreshold : .25f;
            visibilityBias = set != null ? set.aprtVisibilityBias : .01f;
            normalBias = set != null ? set.aprtNormalBias : 0;
            bounceIntensity = set != null ? set.aprtBounceIntensity : 1;
            addAmbientLighting = set != null && set.aprtAddAmbientLighting;
        }

        APRTRelightSettings(APRTRelightSettings fallback, APRTOptions options, APRTOptions defaults)
        {
            giIntensity = Value(options.giIntensity, fallback.giIntensity, defaults?.giIntensity);
            indirectIntensity = Value(options.indirectIntensity, fallback.indirectIntensity, defaults?.indirectIntensity);
            indirectContrast = Value(options.indirectContrast, fallback.indirectContrast, defaults?.indirectContrast);
            skyIntensity = Value(options.skyIntensity, fallback.skyIntensity, defaults?.skyIntensity);
            saturationBoost = Value(options.saturationBoost, fallback.saturationBoost, defaults?.saturationBoost);
            shadowGIVisibility = Value(options.shadowGIVisibility, fallback.shadowGIVisibility, defaults?.shadowGIVisibility);
            skyVisibilityPower = Value(options.skyVisibilityPower, fallback.skyVisibilityPower, defaults?.skyVisibilityPower);
            floorBoost = Value(options.floorBoost, fallback.floorBoost, defaults?.floorBoost);
            floorPower = Value(options.floorPower, fallback.floorPower, defaults?.floorPower);
            temporalBlend = Value(options.temporalBlend, fallback.temporalBlend, defaults?.temporalBlend);
            leakReduction = Value(options.leakReduction, fallback.leakReduction, defaults?.leakReduction);
            probeInvalidityThreshold = Value(options.probeInvalidityThreshold, fallback.probeInvalidityThreshold, defaults?.probeInvalidityThreshold);
            visibilityBias = Value(options.visibilityBias, fallback.visibilityBias, defaults?.visibilityBias);
            normalBias = Value(options.normalBias, fallback.normalBias, defaults?.normalBias);
            bounceIntensity = fallback.bounceIntensity;
            addAmbientLighting = fallback.addAmbientLighting;
        }

        static T Value<T>(VolumeParameter<T> parameter, T fallback, VolumeParameter<T> defaultParameter) =>
            parameter.overrideState || defaultParameter?.overrideState == true ? parameter.value : fallback;

        internal static APRTRelightSettings FromBakingSet(ProbeVolumeBakingSet set, APRTLightingModel model) => new APRTRelightSettings(set, model);

        internal static APRTRelightSettings Resolve(ProbeVolumeBakingSet set, APRTLightingModel model, APRTOptions options) =>
            Resolve(set, model, options, APRTOptions.GetDefaultOptions());

        internal static APRTRelightSettings Resolve(ProbeVolumeBakingSet set, APRTLightingModel model, APRTOptions options, APRTOptions defaultOptions)
        {
            var fallback = FromBakingSet(set, model);
            return options != null && options.active ? new APRTRelightSettings(fallback, options, defaultOptions) : fallback;
        }
    }
}
