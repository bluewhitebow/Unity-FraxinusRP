using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering
{
    [CustomEditor(typeof(APRTOptions))]
    sealed class APRTOptionsEditor : VolumeComponentEditor
    {
        SerializedDataParameter m_GIIntensity, m_IndirectIntensity, m_IndirectContrast, m_SkyIntensity;
        SerializedDataParameter m_SaturationBoost, m_ShadowGIVisibility, m_SkyVisibilityPower, m_FloorBoost, m_FloorPower;
        SerializedDataParameter m_TemporalBlend, m_LeakReduction, m_ProbeInvalidityThreshold, m_VisibilityBias, m_NormalBias;

        public override void OnEnable()
        {
            var o = new PropertyFetcher<APRTOptions>(serializedObject);
            m_GIIntensity = Unpack(o.Find(x => x.giIntensity));
            m_IndirectIntensity = Unpack(o.Find(x => x.indirectIntensity));
            m_IndirectContrast = Unpack(o.Find(x => x.indirectContrast));
            m_SkyIntensity = Unpack(o.Find(x => x.skyIntensity));
            m_SaturationBoost = Unpack(o.Find(x => x.saturationBoost));
            m_ShadowGIVisibility = Unpack(o.Find(x => x.shadowGIVisibility));
            m_SkyVisibilityPower = Unpack(o.Find(x => x.skyVisibilityPower));
            m_FloorBoost = Unpack(o.Find(x => x.floorBoost));
            m_FloorPower = Unpack(o.Find(x => x.floorPower));
            m_TemporalBlend = Unpack(o.Find(x => x.temporalBlend));
            m_LeakReduction = Unpack(o.Find(x => x.leakReduction));
            m_ProbeInvalidityThreshold = Unpack(o.Find(x => x.probeInvalidityThreshold));
            m_VisibilityBias = Unpack(o.Find(x => x.visibilityBias));
            m_NormalBias = Unpack(o.Find(x => x.normalBias));
            base.OnEnable();
        }

        public override void OnInspectorGUI()
        {
            EditorGUILayout.LabelField("Relighting", EditorStyles.boldLabel);
            PropertyField(m_GIIntensity, new GUIContent("GI Intensity"));
            PropertyField(m_SkyIntensity, new GUIContent("Sky Intensity"));
            PropertyField(m_IndirectIntensity, new GUIContent("Indirect Intensity"));
            PropertyField(m_IndirectContrast, new GUIContent("Indirect Contrast"));
            PropertyField(m_SaturationBoost, new GUIContent("Saturation Boost"));
            PropertyField(m_ShadowGIVisibility, new GUIContent("Shadow GI Visibility"));
            PropertyField(m_SkyVisibilityPower, new GUIContent("Sky Visibility Power"));
            PropertyField(m_FloorBoost, new GUIContent("Floor Boost"));
            PropertyField(m_FloorPower, new GUIContent("Floor Power"));
            PropertyField(m_TemporalBlend, new GUIContent("Temporal Blend"));
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Receiving", EditorStyles.boldLabel);
            PropertyField(m_LeakReduction, new GUIContent("Visibility Leak Reduction"));
            PropertyField(m_ProbeInvalidityThreshold, new GUIContent("Probe Invalidity Threshold"));
            PropertyField(m_VisibilityBias, new GUIContent("Visibility Bias"));
            PropertyField(m_NormalBias, new GUIContent("Normal Bias"));
            EditorGUILayout.HelpBox("Unchecked parameters inherit the default Volume Profile or existing Baking Set values. Relighting changes apply without rebaking; geometry and material changes require a new bake.", MessageType.Info);
        }
    }
}
