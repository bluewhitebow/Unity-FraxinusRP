using UnityEngine;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    using CED = CoreEditorDrawer<UltraRenderPipelineSerializedLight>;

    partial class UltraRenderPipelineLightUI
    {
        static readonly ExpandedState<Expandable, Light> s_expandedStatePreset = new(0, "UltraRP-preset");

        public static readonly CED.IDrawer presetInspector = CED.Group(
            CED.Group((serialized, owner) =>
                EditorGUILayout.HelpBox(LightUI.Styles.unsupportedPresetPropertiesMessage, MessageType.Info)),
            CED.Group((serialized, owner) => EditorGUILayout.Space()),
            CED.FoldoutGroup(LightUI.Styles.generalHeader.text,
                Expandable.General,
                s_expandedStatePreset,
                DrawGeneralContentPreset),
            CED.FoldoutGroup(LightUI.Styles.emissionHeader.text,
                Expandable.Emission,
                s_expandedStatePreset,
                CED.Group(
                    LightUI.DrawColor,
                    DrawEmissionContent))
        );
    }
}
