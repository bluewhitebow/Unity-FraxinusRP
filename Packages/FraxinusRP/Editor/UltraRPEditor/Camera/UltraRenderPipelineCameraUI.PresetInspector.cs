using UnityEngine;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    using CED = CoreEditorDrawer<UltraRenderPipelineSerializedCamera>;

    static partial class UltraRenderPipelineCameraUI
    {
        static readonly ExpandedState<Expandable, Camera> k_ExpandedStatePreset = new(0, "UltraRP-preset");

        public static readonly CED.IDrawer presetInspector = CED.Group(
            CED.Group((serialized, owner) =>
                EditorGUILayout.HelpBox(CameraUI.Styles.unsupportedPresetPropertiesMessage, MessageType.Info)),
            CED.Group((serialized, owner) => EditorGUILayout.Space()),
            CED.FoldoutGroup(
                CameraUI.Styles.projectionSettingsHeaderContent,
                Expandable.Projection,
                k_ExpandedStatePreset,
                FoldoutOption.Indent,
                CED.Group(
                    CameraUI.Drawer_Projection),
                PhysicalCamera.drawer),
            Rendering.drawerPreset
        );
    }
}
