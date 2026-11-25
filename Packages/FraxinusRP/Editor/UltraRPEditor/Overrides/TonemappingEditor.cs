using UnityEngine;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(Tonemapping))]
    sealed class TonemappingEditor : VolumeComponentEditor
    {
        SerializedDataParameter _mode;

        // HDR Mode.
        SerializedDataParameter _neutralHDRRangeReductionMode;
        SerializedDataParameter _hueShiftAmount;
        SerializedDataParameter _hdrDetectPaperWhite;
        SerializedDataParameter _hdrPaperwhite;
        SerializedDataParameter _hdrDetectNitLimits;
        SerializedDataParameter _hdrMinNits;
        SerializedDataParameter _hdrMaxNits;
        SerializedDataParameter _hdrAcesPreset;

        public override bool hasAdditionalProperties => true;

        public override void OnEnable()
        {
            var o = new PropertyFetcher<Tonemapping>(serializedObject);

            _mode = Unpack(o.Find(x => x.mode));
            _neutralHDRRangeReductionMode = Unpack(o.Find(x => x.neutralHDRRangeReductionMode));
            _hueShiftAmount = Unpack(o.Find(x => x.hueShiftAmount));
            _hdrDetectPaperWhite = Unpack(o.Find(x => x.detectPaperWhite));
            _hdrPaperwhite = Unpack(o.Find(x => x.paperWhite));
            _hdrDetectNitLimits = Unpack(o.Find(x => x.detectBrightnessLimits));
            _hdrMinNits = Unpack(o.Find(x => x.minNits));
            _hdrMaxNits = Unpack(o.Find(x => x.maxNits));
            _hdrAcesPreset = Unpack(o.Find(x => x.acesPreset));
        }

        public override void OnInspectorGUI()
        {
            PropertyField(_mode);

            // Display a warning if the user is trying to use a tonemap while rendering in LDR
            var asset = UltraRenderPipeline.asset;
            int hdrTonemapMode = _mode.value.intValue;
            if (asset != null && !asset.supportsHDR && hdrTonemapMode != (int)TonemappingMode.None)
            {
                EditorGUILayout.HelpBox("Tonemapping should only be used when working with High Dynamic Range (HDR). Please enable HDR through the active Render Pipeline Asset.", MessageType.Warning);
                return;
            }

            if (PlayerSettings.allowHDRDisplaySupport && hdrTonemapMode != (int)TonemappingMode.None)
            {
                EditorGUILayout.LabelField("HDR Output");

                if (hdrTonemapMode == (int)TonemappingMode.Neutral)
                {
                    PropertyField(_neutralHDRRangeReductionMode);
                    PropertyField(_hueShiftAmount);

                    PropertyField(_hdrDetectPaperWhite);
                    EditorGUI.indentLevel++;
                    using (new EditorGUI.DisabledScope(_hdrDetectPaperWhite.value.boolValue))
                    {
                        PropertyField(_hdrPaperwhite);
                    }
                    EditorGUI.indentLevel--;

                    PropertyField(_hdrDetectNitLimits);
                    EditorGUI.indentLevel++;
                    using (new EditorGUI.DisabledScope(_hdrDetectNitLimits.value.boolValue))
                    {
                        PropertyField(_hdrMinNits);
                        PropertyField(_hdrMaxNits);
                    }
                    EditorGUI.indentLevel--;
                }
                if (hdrTonemapMode == (int)TonemappingMode.ACES)
                {
                    PropertyField(_hdrAcesPreset);

                    PropertyField(_hdrDetectPaperWhite);
                    EditorGUI.indentLevel++;
                    using (new EditorGUI.DisabledScope(_hdrDetectPaperWhite.value.boolValue))
                    {
                        PropertyField(_hdrPaperwhite);
                    }
                    EditorGUI.indentLevel--;
                }
            }
        }
    }
}
