using System;
using UnityEngine;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    using CED = CoreEditorDrawer<UltraRenderPipelineSerializedCamera>;

    static partial class UltraRenderPipelineCameraUI
    {
        public partial class Output
        {
            public static readonly CED.IDrawer drawer;
            static Output()
            {
                drawer = CED.Conditional(
                    (serialized, owner) => (CameraRenderType)serialized.cameraType.intValue == CameraRenderType.Base,
                    CED.FoldoutGroup(
                        CameraUI.Output.Styles.header,
                        Expandable.Output,
                        s_expandedState,
                        FoldoutOption.Indent,
                        CED.Group(
                            DrawerOutputTargetTexture
                            ),
                        CED.Conditional(
                            (serialized, owner) => serialized.serializedObject.targetObject is Camera camera && camera.targetTexture == null,
                            CED.Group(
                                DrawerOutputMultiDisplay
                            )
                            ),
                        CED.Group(
                            DrawerOutputNormalizedViewPort
                            ),
                        CED.Conditional(
                            (serialized, owner) => serialized.serializedObject.targetObject is Camera camera && camera.targetTexture == null,
                            CED.Group(
                                CED.Group(DrawerOutputHDR),
                                CED.Conditional(
                                    (serialized, owner) => PlayerSettings.allowHDRDisplaySupport,
                                    CED.Group(DrawerOutputHDROutput)
                                    ),
                                CED.Group(DrawerOutputAllowDynamicResolution)
                            )
                        )
                    )
                );
            }

            static void DrawerOutputMultiDisplay(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                using (var checkScope = new EditorGUI.ChangeCheckScope())
                {
                    p.baseCameraSettings.DrawMultiDisplay();
                    if (checkScope.changed)
                    {
                        UpdateStackCamerasOutput(p, camera =>
                        {
                            bool isChanged = false;
                            // Force same target display
                            int targetDisplay = p.baseCameraSettings.targetDisplay.intValue;
                            if (camera.targetDisplay != targetDisplay)
                            {
                                camera.targetDisplay = targetDisplay;
                                isChanged = true;
                            }

                            // Force same target display
                            StereoTargetEyeMask stereoTargetEye = (StereoTargetEyeMask)p.baseCameraSettings.targetEye.intValue;
                            if (camera.stereoTargetEye != stereoTargetEye)
                            {
                                camera.stereoTargetEye = stereoTargetEye;
                                isChanged = true;
                            }

                            return isChanged;
                        });
                    }
                }
            }

            static void DrawerOutputAllowDynamicResolution(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                using (var checkScope = new EditorGUI.ChangeCheckScope())
                {
                    CameraUI.Output.Drawer_Output_AllowDynamicResolution(p, owner, Styles.allowDynamicResolution);
                    if (checkScope.changed)
                    {
                        UpdateStackCamerasOutput(p, camera =>
                        {
                            bool allowDynamicResolution = p.allowDynamicResolution.boolValue;

                            if (camera.allowDynamicResolution == p.allowDynamicResolution.boolValue)
                                return false;

                            EditorUtility.SetDirty(camera);

                            camera.allowDynamicResolution = allowDynamicResolution;
                            return true;
                        });
                    }
                }
            }

            static void DrawerOutputNormalizedViewPort(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                using (var checkScope = new EditorGUI.ChangeCheckScope())
                {
                    CameraUI.Output.Drawer_Output_NormalizedViewPort(p, owner);
                    if (checkScope.changed)
                    {
                        UpdateStackCamerasOutput(p, camera =>
                        {
                            Rect rect = p.baseCameraSettings.normalizedViewPortRect.rectValue;
                            if (camera.rect != rect)
                            {
                                camera.rect = p.baseCameraSettings.normalizedViewPortRect.rectValue;
                                return true;
                            }

                            return false;
                        });
                    }
                }
            }

            static void UpdateStackCamerasOutput(UltraRenderPipelineSerializedCamera p, Func<Camera, bool> updateOutputProperty)
            {
                int cameraCount = p.cameras.arraySize;
                for (int i = 0; i < cameraCount; ++i)
                {
                    SerializedProperty cameraProperty = p.cameras.GetArrayElementAtIndex(i);
                    Camera overlayCamera = cameraProperty.objectReferenceValue as Camera;
                    if (overlayCamera != null)
                    {
                        Undo.RecordObject(overlayCamera, Styles.inspectorOverlayCameraText);
                        if (updateOutputProperty(overlayCamera))
                            EditorUtility.SetDirty(overlayCamera);
                    }
                }
            }

            static void DrawerOutputTargetTexture(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                using (var checkScope = new EditorGUI.ChangeCheckScope())
                {
                    EditorGUILayout.PropertyField(p.baseCameraSettings.targetTexture, Styles.targetTextureLabel);

                    var texture = p.baseCameraSettings.targetTexture.objectReferenceValue as RenderTexture;
                    if (!p.baseCameraSettings.targetTexture.hasMultipleDifferentValues && texture &&
                        (texture.antiAliasing > 1 || texture.bindTextureMS))
                        EditorGUILayout.HelpBox(Styles.unsupportedTargetTexture, MessageType.Error, true);

                    if (checkScope.changed)
                    {
                        UpdateStackCamerasOutput(p, camera =>
                        {
                            if (camera.targetTexture == texture)
                                return false;

                            camera.targetTexture = texture;
                            return true;
                        });
                    }
                }
            }
            static void DrawerOutputHDR(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                Rect controlRect = EditorGUILayout.GetControlRect(true);
                EditorGUI.BeginProperty(controlRect, Styles.allowHDR, p.baseCameraSettings.HDR);
                {
                    using (var checkScope = new EditorGUI.ChangeCheckScope())
                    {
                        int selectedValue = !p.baseCameraSettings.HDR.boolValue ? 0 : 1;
                        var allowHDR = EditorGUI.IntPopup(controlRect, Styles.allowHDR, selectedValue, Styles.displayedCameraOptions, Styles.cameraOptions) == 1;
                        if (checkScope.changed)
                        {
                            p.baseCameraSettings.HDR.boolValue = allowHDR;
                            UpdateStackCamerasOutput(p, camera =>
                            {
                                if (camera.allowHDR == allowHDR)
                                    return false;

                                camera.allowHDR = allowHDR;
                                return true;
                            });
                        }
                    }
                }
                EditorGUI.EndProperty();
            }
            
            static void DrawerOutputHDROutput(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                Rect controlRect = EditorGUILayout.GetControlRect(true);
                EditorGUI.BeginProperty(controlRect, Styles.allowHDROutput, p.allowHDROutput);
                {
                    using (var checkScope = new EditorGUI.ChangeCheckScope())
                    {
                        int selectedValue = !p.allowHDROutput.boolValue ? 0 : 1;
                        var allowHDROutput = EditorGUI.IntPopup(controlRect, Styles.allowHDROutput, selectedValue, Styles.hdrOuputOptions, Styles.hdrOuputValues) == 1;

                        var rpAsset = UltraRenderPipeline.asset;
                        bool perCameraHDRDisabled = !p.baseCameraSettings.HDR.hasMultipleDifferentValues &&
                            !p.baseCameraSettings.HDR.boolValue && rpAsset != null && rpAsset.supportsHDR;
                        
                        if (!p.allowHDROutput.hasMultipleDifferentValues && allowHDROutput &&
                            PlayerSettings.allowHDRDisplaySupport && perCameraHDRDisabled)
                        {
                            EditorGUILayout.HelpBox(Styles.disabledHDRRenderingWithHDROutput, MessageType.Warning);
                        }

                        if (checkScope.changed)
                            p.allowHDROutput.boolValue = allowHDROutput;
                    }
                }
                EditorGUI.EndProperty();
            }

        }
    }
}
