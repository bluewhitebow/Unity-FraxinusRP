using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace FraxinusEditor.Rendering.Ultra
{
    using static UnityEditor.Rendering.CameraUI;
    using CED = CoreEditorDrawer<UltraRenderPipelineSerializedCamera>;

    static partial class UltraRenderPipelineCameraUI
    {
        public enum Expandable
        {
            /// <summary> Projection</summary>
            Projection = 1 << 0,
            /// <summary> Physical</summary>
            Physical = 1 << 1,
            /// <summary> Output</summary>
            Output = 1 << 2,
            /// <summary> Orthographic</summary>
            Orthographic = 1 << 3,
            /// <summary> RenderLoop</summary>
            RenderLoop = 1 << 4,
            /// <summary> Rendering</summary>
            Rendering = 1 << 5,
            /// <summary> Environment</summary>
            Environment = 1 << 6,
            /// <summary> Stack</summary>
            Stack = 1 << 7,
        }

        public enum ExpandableAdditional
        {
            Rendering = 1 << 0,
        }

        static readonly ExpandedState<Expandable, Camera> s_expandedState = new(Expandable.Projection, "UltraRP");
        static readonly AdditionalPropertiesState<ExpandableAdditional, Camera> s_expandedAdditionalState = new(0, "Ultra");

        public static readonly CED.IDrawer sectionStackSettings =
            CED.Conditional(
                (serialized, editor) => (CameraRenderType)serialized.cameraType.intValue == CameraRenderType.Base,
                CED.FoldoutGroup(Styles.stackSettingsText, Expandable.Stack, s_expandedState, FoldoutOption.Indent, CED.Group(DrawerStackCameras)));

        public static readonly CED.IDrawer sectionProjectionSettings = CED.FoldoutGroup(
            CameraUI.Styles.projectionSettingsHeaderContent,
            Expandable.Projection,
            s_expandedState,
            FoldoutOption.Indent,
            CED.Group(DrawerProjection),
            PhysicalCamera.drawer
        );

        public static readonly CED.IDrawer[] inspector =
{
            CED.Group(
                DrawerCameraType
                ),
            sectionProjectionSettings,
            Rendering.drawer,
            sectionStackSettings,
            Environment.drawer,
            Output.drawer
        };

        static void DrawerProjection(UltraRenderPipelineSerializedCamera p, Editor owner)
        {
            var camera = p.serializedObject.targetObject as Camera;
            //bool pixelPerfectEnabled = camera.TryGetComponent<PixelPerfectCamera>(out var pixelPerfectCamera) && pixelPerfectCamera.enabled;
            //if (pixelPerfectEnabled)
            //    EditorGUILayout.HelpBox(Styles.pixelPerfectInfo, MessageType.Info);

            //using (new EditorGUI.DisabledGroupScope(pixelPerfectEnabled))
            CameraUI.Drawer_Projection(p, owner);
        }


        static void DrawerCameraType(UltraRenderPipelineSerializedCamera p, Editor owner)
        {
            int selectedRenderer = p.renderer.intValue;
            ScriptableRenderer scriptableRenderer = UltraRenderPipeline.asset.GetRenderer(selectedRenderer);

            EditorGUI.BeginChangeCheck();

            CameraRenderType originalCamType = (CameraRenderType)p.cameraType.intValue;
            CameraRenderType camType = scriptableRenderer.SupportsCameraStackingType(CameraRenderType.Overlay) ? originalCamType : CameraRenderType.Base;
            EditorGUI.BeginDisabledGroup(scriptableRenderer.SupportedCameraStackingTypes() == 0);
            camType = (CameraRenderType)EditorGUILayout.EnumPopup(
                Styles.cameraType,
                camType,
                e => scriptableRenderer.SupportsCameraStackingType((CameraRenderType)e),
                false
            );
            EditorGUI.EndDisabledGroup();

            if (EditorGUI.EndChangeCheck() || camType != originalCamType)
            {
                p.cameraType.intValue = (int)camType;
                if (camType == CameraRenderType.Overlay)
                    p.baseCameraSettings.clearFlags.intValue = (int)CameraClearFlags.Nothing;
            }

            EditorGUILayout.Space();
        }

        static void DrawerStackCameras(UltraRenderPipelineSerializedCamera p, Editor owner)
        {
            if (owner is UltraRenderPipelineCameraEditor cameraEditor)
            {
                cameraEditor.DrawStackSettings();
            }
        }
    }
}
