using UnityEngine;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(LiftGammaGain))]
    sealed class LiftGammaGainEditor : VolumeComponentEditor
    {
        private static class Styles
        {
            public static readonly GUIContent liftLabel = EditorGUIUtility.TrTextContent("Lift", "Use this to control and apply a hue to the dark tones. This has a more exaggerated effect on shadows.");
            public static readonly GUIContent gammaLabel = EditorGUIUtility.TrTextContent("Gamma", "Use this to control and apply a hue to the mid-range tones with a power function.");
            public static readonly GUIContent gainLabel = EditorGUIUtility.TrTextContent("Gain", "Use this to increase and apply a hue to the signal and make highlights brighter.");
        }

        SerializedDataParameter _lift;
        SerializedDataParameter _gamma;
        SerializedDataParameter _gain;

        readonly TrackballUIDrawer _trackballUIDrawer = new TrackballUIDrawer();

        public override void OnEnable()
        {
            var o = new PropertyFetcher<LiftGammaGain>(serializedObject);

            _lift = Unpack(o.Find(x => x.lift));
            _gamma = Unpack(o.Find(x => x.gamma));
            _gain = Unpack(o.Find(x => x.gain));
        }

        public override void OnInspectorGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _trackballUIDrawer.OnGUI(_lift.value, enableOverrides ? _lift.overrideState : null, Styles.liftLabel, GetLiftValue);
                GUILayout.Space(4f);
                _trackballUIDrawer.OnGUI(_gamma.value, enableOverrides ? _gamma.overrideState : null, Styles.gammaLabel, GetLiftValue);
                GUILayout.Space(4f);
                _trackballUIDrawer.OnGUI(_gain.value, enableOverrides ? _gain.overrideState : null, Styles.gainLabel, GetLiftValue);
            }
        }

        static Vector3 GetLiftValue(Vector4 x) => new Vector3(x.x + x.w, x.y + x.w, x.z + x.w);
    }
}
