using UnityEngine;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(ScreenSpaceLensFlare))]
    class ScreenSpaceLensFlareEditor : VolumeComponentEditor
    {
        protected SerializedDataParameter _intensity;
        protected SerializedDataParameter _tintColor;
        protected SerializedDataParameter _bloomMip;
        protected SerializedDataParameter _firstFlareIntensity;
        protected SerializedDataParameter _secondaryFlareIntensity;
        protected SerializedDataParameter _warpedFlareIntensity;
        protected SerializedDataParameter _warpedFlareScale;
        protected SerializedDataParameter _samples;
        protected SerializedDataParameter _sampleDimmer;
        protected SerializedDataParameter _vignetteEffect;
        protected SerializedDataParameter _startingPosition;
        protected SerializedDataParameter _scale;
        protected SerializedDataParameter _chromaticAbberationIntensity;
        protected SerializedDataParameter _streaksIntensity;
        protected SerializedDataParameter _streaksLength;
        protected SerializedDataParameter _streaksOrientation;
        protected SerializedDataParameter _streaksThreshold;
        protected SerializedDataParameter _resolution;

        static GUIContent s_intensity = EditorGUIUtility.TrTextContent("Intensity", "Sets the global intensity of the Screen Space Lens Flare effect. When set to 0, the pass is skipped. ");
        static GUIContent s_tintColor = EditorGUIUtility.TrTextContent("Tint Color", "Sets the color used to tint all the flares. ");
        static GUIContent s_bloomMip = EditorGUIUtility.TrTextContent("Bloom Mip Bias", "Controls the Bloom mip used as a source for the Lens Flare effect. A high value will result in a blurrier result for all the flares.");
        static GUIContent s_firstFlareIntensity = EditorGUIUtility.TrTextContent("Regular Multiplier", "Controls the intensity of the Regular Flare sample. Those flares are sampled using scaled screen coordinates.");
        static GUIContent s_secondaryFlareIntensity = EditorGUIUtility.TrTextContent("Reversed Multiplier", "Controls the intensity of the Reversed Flare sample. Those flares are sampled using scaled and flipped screen coordinates.");
        static GUIContent s_warpedFlareIntensity = EditorGUIUtility.TrTextContent("Warped Multiplier", "Controls the intensity of the Warped Flare sample. Those flares are sampled using polar screen coordinates.");
        static GUIContent s_warpedFlareScale = EditorGUIUtility.TrTextContent("Scale", "Sets the scale of the Warped Flare sample. A value of 1,1 will keep this flare circular. Set values below 1 can lead to stretching artifacts on the side of the screen.");
        static GUIContent s_samples = EditorGUIUtility.TrTextContent("Samples", "Controls the number of times the flare effect is repeated for each flare type (regular, reversed, warped). This parameter has a strong impact on performance.");
        static GUIContent s_sampleDimmer = EditorGUIUtility.TrTextContent("Sample Dimmer", "Controls the value by which each additionnal sample is multiplied. This parameter has an effect only after the first sample.");
        static GUIContent s_vignetteEffect = EditorGUIUtility.TrTextContent("Vignette Effect", "Controls the intensity of the vignette effect to occlude the Lens Flare effect at the center of the screen.");
        static GUIContent s_startingPosition = EditorGUIUtility.TrTextContent("Starting Position", "Controls the starting position of the flares in screen space relative to their source.");
        static GUIContent s_Scale = EditorGUIUtility.TrTextContent("Scale", "Controls the scale at which the flares are sampled. This parameter only impacts regular, reversed and warped flares.");
        static GUIContent s_chromaticAbberationIntensity = EditorGUIUtility.TrTextContent("Intensity", "Controls the strength of the Chromatic Aberration effect. The higher the value, the more light is dispersed on the sides of the screen.");
        static GUIContent s_streaksIntensity = EditorGUIUtility.TrTextContent("Multiplier", "Controls the intensity of streaks effect. This effect has an impact on performance when above zero. When this intensity is zero, this effect is not evaluated to save costs.");
        static GUIContent s_streaksOrientation = EditorGUIUtility.TrTextContent("Orientation", "Controls the orientation streaks effect in degrees. A value of 0 creates horizontal streaks.");
        static GUIContent s_streaksLength = EditorGUIUtility.TrTextContent("Length", "Controls the length of streaks effect. A value of one creates streaks about the width of the screen.");
        static GUIContent s_streaksThreshold = EditorGUIUtility.TrTextContent("Threshold", "Controls the threshold of streak effect. A high value makes the effect more localised on the high intensity areas of the screen. When using a non HDR Camera, a high threshold can remove streaks entirely.");
        static GUIContent s_resolution = EditorGUIUtility.TrTextContent("Resolution", "Specifies the resolution ratio at which the streaks effect is computed.");

        public override void OnEnable()
        {
            var o = new PropertyFetcher<ScreenSpaceLensFlare>(serializedObject);

            _intensity = Unpack(o.Find(x => x.intensity));
            _tintColor = Unpack(o.Find(x => x.tintColor));
            _bloomMip = Unpack(o.Find(x => x.bloomMip));
            _firstFlareIntensity = Unpack(o.Find(x => x.firstFlareIntensity));
            _secondaryFlareIntensity = Unpack(o.Find(x => x.secondaryFlareIntensity));
            _warpedFlareIntensity = Unpack(o.Find(x => x.warpedFlareIntensity));
            _warpedFlareScale = Unpack(o.Find(x => x.warpedFlareScale));
            _samples = Unpack(o.Find(x => x.samples));
            _sampleDimmer = Unpack(o.Find(x => x.sampleDimmer));
            _vignetteEffect = Unpack(o.Find(x => x.vignetteEffect));
            _startingPosition = Unpack(o.Find(x => x.startingPosition));
            _scale = Unpack(o.Find(x => x.scale));
            _chromaticAbberationIntensity = Unpack(o.Find(x => x.chromaticAbberationIntensity));
            _streaksIntensity = Unpack(o.Find(x => x.streaksIntensity));
            _streaksLength = Unpack(o.Find(x => x.streaksLength));
            _streaksOrientation = Unpack(o.Find(x => x.streaksOrientation));
            _streaksThreshold = Unpack(o.Find(x => x.streaksThreshold));
            _resolution = Unpack(o.Find(x => x.resolution));

        }

        public override void OnInspectorGUI()
        {
            PropertyField(_intensity, s_intensity);
            PropertyField(_tintColor, s_tintColor);
            PropertyField(_bloomMip, s_bloomMip);

            //Regular Flares
            PropertyField(_firstFlareIntensity, s_firstFlareIntensity);
            PropertyField(_secondaryFlareIntensity, s_secondaryFlareIntensity);
            PropertyField(_warpedFlareIntensity, s_warpedFlareIntensity);

            if(showAdditionalProperties)
            {
                using (new IndentLevelScope())
                {
                    PropertyField(_warpedFlareScale, s_warpedFlareScale);
                }
            }

            //Parameters for Regular Flares
            PropertyField(_samples, s_samples);
            if(showAdditionalProperties)
            {
                using (new IndentLevelScope())
                {
                    PropertyField(_sampleDimmer, s_sampleDimmer);
                }
            }
            PropertyField(_vignetteEffect, s_vignetteEffect);
            PropertyField(_startingPosition, s_startingPosition);
            PropertyField(_scale, s_Scale);

            //Streaks
            PropertyField(_streaksIntensity, s_streaksIntensity);
            using (new IndentLevelScope())
            {
                PropertyField(_streaksLength, s_streaksLength);
                PropertyField(_streaksOrientation, s_streaksOrientation);
                PropertyField(_streaksThreshold, s_streaksThreshold);
                PropertyField(_resolution, s_resolution);
            }

            //Chromatic Aberration
            PropertyField(_chromaticAbberationIntensity, s_chromaticAbberationIntensity);
        }

    }
}
