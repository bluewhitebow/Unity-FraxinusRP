using UnityEngine;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(DepthOfField))]
    sealed class DepthOfFieldEditor : VolumeComponentEditor
    {
        SerializedDataParameter _mode;

        SerializedDataParameter _gaussianStart;
        SerializedDataParameter _gaussianEnd;
        SerializedDataParameter _gaussianMaxRadius;
        SerializedDataParameter _highQualitySampling;

        SerializedDataParameter _focusDistance;
        SerializedDataParameter _focalLength;
        SerializedDataParameter _aperture;
        SerializedDataParameter _bladeCount;
        SerializedDataParameter _bladeCurvature;
        SerializedDataParameter _bladeRotation;

        public override void OnEnable()
        {
            var o = new PropertyFetcher<DepthOfField>(serializedObject);

            _mode = Unpack(o.Find(x => x.mode));
            _gaussianStart = Unpack(o.Find(x => x.gaussianStart));
            _gaussianEnd = Unpack(o.Find(x => x.gaussianEnd));
            _gaussianMaxRadius = Unpack(o.Find(x => x.gaussianMaxRadius));
            _highQualitySampling = Unpack(o.Find(x => x.highQualitySampling));

            _focusDistance = Unpack(o.Find(x => x.focusDistance));
            _focalLength = Unpack(o.Find(x => x.focalLength));
            _aperture = Unpack(o.Find(x => x.aperture));
            _bladeCount = Unpack(o.Find(x => x.bladeCount));
            _bladeCurvature = Unpack(o.Find(x => x.bladeCurvature));
            _bladeRotation = Unpack(o.Find(x => x.bladeRotation));
        }

        public override void OnInspectorGUI()
        {
            PropertyField(_mode);

            if (_mode.value.intValue == (int)DepthOfFieldMode.Gaussian)
            {
                PropertyField(_gaussianStart, EditorGUIUtility.TrTextContent("Start"));
                PropertyField(_gaussianEnd, EditorGUIUtility.TrTextContent("End"));
                PropertyField(_gaussianMaxRadius, EditorGUIUtility.TrTextContent("Max Radius"));
                PropertyField(_highQualitySampling);
            }
            else if (_mode.value.intValue == (int)DepthOfFieldMode.Bokeh)
            {
                PropertyField(_focusDistance);
                PropertyField(_focalLength);
                PropertyField(_aperture);
                PropertyField(_bladeCount);
                PropertyField(_bladeCurvature);
                PropertyField(_bladeRotation);
            }
        }
    }
}
