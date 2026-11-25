using UnityEngine;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(MotionBlur))]
    sealed class MotionBlurEditor : VolumeComponentEditor
    {
        SerializedDataParameter _mode;
        SerializedDataParameter _quality;
        SerializedDataParameter _intensity;
        SerializedDataParameter _clamp;

        public override void OnEnable()
        {
            var o = new PropertyFetcher<MotionBlur>(serializedObject);

            _mode = Unpack(o.Find(x => x.mode));
            _quality = Unpack(o.Find(x => x.quality));
            _intensity = Unpack(o.Find(x => x.intensity));
            _clamp = Unpack(o.Find(x => x.clamp));
        }

        public override void OnInspectorGUI()
        {
            //PropertyField(m_Mode);

            //if (m_Mode.value.intValue == (int)MotionBlurMode.CameraOnly)
            //{
            PropertyField(_mode);
            PropertyField(_quality);
            PropertyField(_intensity);
            PropertyField(_clamp);
            //}
            //else
            //{
            //    EditorGUILayout.HelpBox("Object motion blur is not supported on the Universal Render Pipeline yet.", MessageType.Info);
            //}
        }
    }
}
