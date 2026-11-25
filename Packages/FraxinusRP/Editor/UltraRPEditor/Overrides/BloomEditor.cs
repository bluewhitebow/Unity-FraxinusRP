using UnityEngine;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(Bloom))]
    sealed class BloomEditor : VolumeComponentEditor
    {
        SerializedDataParameter _threshold;
        SerializedDataParameter _intensity;
        SerializedDataParameter _scatter;
        SerializedDataParameter _clamp;
        SerializedDataParameter _tint;
        SerializedDataParameter _highQualityFiltering;
        SerializedDataParameter _downsample;
        SerializedDataParameter _maxIterations;
        SerializedDataParameter _dirtTexture;
        SerializedDataParameter _dirtIntensity;

        public override void OnEnable()
        {
            var o = new PropertyFetcher<Bloom>(serializedObject);

            _threshold = Unpack(o.Find(x => x.threshold));
            _intensity = Unpack(o.Find(x => x.intensity));
            _scatter = Unpack(o.Find(x => x.scatter));
            _clamp = Unpack(o.Find(x => x.clamp));
            _tint = Unpack(o.Find(x => x.tint));
            _highQualityFiltering = Unpack(o.Find(x => x.highQualityFiltering));
            _downsample = Unpack(o.Find(x => x.downscale));
            _maxIterations = Unpack(o.Find(x => x.maxIterations));
            _dirtTexture = Unpack(o.Find(x => x.dirtTexture));
            _dirtIntensity = Unpack(o.Find(x => x.dirtIntensity));
        }

        public override void OnInspectorGUI()
        {
            PropertyField(_threshold);
            PropertyField(_intensity);
            PropertyField(_scatter);
            PropertyField(_tint);
            PropertyField(_clamp);
            PropertyField(_highQualityFiltering);

            PropertyField(_downsample);
            PropertyField(_maxIterations);

            PropertyField(_dirtTexture);
            PropertyField(_dirtIntensity);
        }
    }
}
