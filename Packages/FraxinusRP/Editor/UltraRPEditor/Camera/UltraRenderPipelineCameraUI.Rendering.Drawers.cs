using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    using CED = CoreEditorDrawer<UltraRenderPipelineSerializedCamera>;

    static partial class UltraRenderPipelineCameraUI
    {
        public partial class Rendering
        {
            private static readonly CED.IDrawer postProcessingWarningDrawer = CED.Conditional(
                (serialized, owner) => !serialized.renderPostProcessing.hasMultipleDifferentValues &&
                    serialized.renderPostProcessing.boolValue && IsPostProcessDataMissing(serialized, UltraRenderPipeline.asset),
                (serialized, owner) => EditorGUILayout.HelpBox(Styles.missingPostProcessData, MessageType.Warning));

            public static readonly CED.IDrawer drawer;

            public static readonly CED.IDrawer drawerPreset;

            static Rendering()
            {
                drawer = CED.AdditionalPropertiesFoldoutGroup(
                    CameraUI.Rendering.Styles.header,
                    Expandable.Rendering,
                    s_expandedState,
                    ExpandableAdditional.Rendering,
                    s_expandedAdditionalState,
                    CED.Group(
                        CED.Group(
                            DrawerRenderingRenderer
                        ),
                        baseCameraRenderTypeDrawer,
                        overlayCameraRenderTypeDrawer,
                        CED.Group(
                            CameraUI.Rendering.Drawer_Rendering_CullingMask,
                            CameraUI.Rendering.Drawer_Rendering_OcclusionCulling
                        )
                    ),
                    CED.noop,
                    FoldoutOption.Indent
                );

                drawerPreset = CED.FoldoutGroup(
                    CameraUI.Rendering.Styles.header,
                    Expandable.Rendering,
                    s_expandedState,
                    FoldoutOption.Indent,
                    CED.Group(
                        CameraUI.Rendering.Drawer_Rendering_CullingMask,
                        CameraUI.Rendering.Drawer_Rendering_OcclusionCulling
                    )
                );
            }

            static readonly CED.IDrawer baseCameraRenderTypeDrawer = CED.Conditional(
                   (serialized, owner) => (CameraRenderType)serialized.cameraType.intValue == CameraRenderType.Base,
                CED.Group(
                    DrawerRenderingRenderPostProcessing
                ),
                postProcessingWarningDrawer,
                CED.Group(
                    DrawerRenderingAntialiasing
                ),
                CED.Conditional(
                        (serialized, owner) => !serialized.antialiasing.hasMultipleDifferentValues && !IsSGSR2Selected(serialized),
                        CED.Group(
                            GroupOption.Indent,
                            new[]{
                                CED.Conditional(
                                    (serialized, owner) => (AntialiasingMode)serialized.antialiasing.intValue ==
                                    AntialiasingMode.SubpixelMorphologicalAntiAliasing,
                                    CED.Group(
                                        DrawerRenderingSMAAQuality
                                    )),
                                CED.Conditional(
                                    (serialized, owner) => (AntialiasingMode)serialized.antialiasing.intValue ==
                                    AntialiasingMode.TemporalAntiAliasing,
                                    CED.Group(
                                        DrawerRenderingTAAQuality
                                    ))
                            }
                    )
                ),
                CED.Group(
                    CameraUI.Rendering.Drawer_Rendering_StopNaNs
                    ),
                CED.Group(
                    CameraUI.Rendering.Drawer_Rendering_Dithering
                    ),
                CED.Group(
                    DrawerRenderingRenderShadows,
                    DrawerRenderingPriority
                )
            );

            static readonly CED.IDrawer overlayCameraRenderTypeDrawer = CED.Conditional(
                (serialized, owner) => (CameraRenderType)serialized.cameraType.intValue == CameraRenderType.Overlay,
                CED.Group(
                    DrawerRenderingRenderPostProcessing
                    ),
                postProcessingWarningDrawer,
                CED.Group(
                    DrawerRenderingClearDepth,
                    DrawerRenderingRenderShadows
                )
            );

            static void DrawerRenderingRenderer(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                var rpAsset = UltraRenderPipeline.asset;

                int selectedRendererOption = p.renderer.intValue;
                EditorGUI.BeginChangeCheck();

                Rect controlRect = EditorGUILayout.GetControlRect(true);
                EditorGUI.BeginProperty(controlRect, Styles.rendererType, p.renderer);

                EditorGUI.showMixedValue = p.renderer.hasMultipleDifferentValues;
                int selectedRenderer = EditorGUI.IntPopup(controlRect, Styles.rendererType, selectedRendererOption, rpAsset.rendererDisplayList, rpAsset.rendererIndexList);
                EditorGUI.EndProperty();

                if (!rpAsset.ValidateRendererGraphList())
                    EditorGUILayout.HelpBox(Styles.noRendererError, MessageType.Error);
                else if (!rpAsset.ValidateRendererGraph(selectedRendererOption))
                {
                    EditorGUILayout.HelpBox(Styles.missingRendererWarning, MessageType.Warning);
                    var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect());
                    if (GUI.Button(rect, Styles.selectRenderPipelineAsset))
                        Selection.activeObject = AssetDatabase.LoadAssetAtPath<UltraRenderPipelineAsset>(AssetDatabase.GetAssetPath(UltraRenderPipeline.asset));

                    GUILayout.Space(5);
                }

                if (EditorGUI.EndChangeCheck())
                    p.renderer.intValue = selectedRenderer;
            }

            static void DrawerRenderingAntialiasing(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                if (IsSGSR2Selected(p))
                {
                    EditorGUILayout.HelpBox("SGSR2 overrides this camera's anti-aliasing selection. It requires Post Processing, a renderer graph that supports temporal upscaling, and a non-stereo camera with a 2D color target, no camera stack or dynamic resolution. If unavailable, rendering falls back to Bilinear without temporal jitter or additional camera anti-aliasing. The camera's saved selection is used with other upscaling filters.", MessageType.Info);
                    return;
                }

                Rect antiAliasingRect = EditorGUILayout.GetControlRect();
                EditorGUI.BeginProperty(antiAliasingRect, Styles.antialiasing, p.antialiasing);
                {
                    EditorGUI.BeginChangeCheck();
                    int selectedValue = (int)(AntialiasingMode)EditorGUI.EnumPopup(antiAliasingRect, Styles.antialiasing, (AntialiasingMode)p.antialiasing.intValue);
                    if (EditorGUI.EndChangeCheck())
                        p.antialiasing.intValue = selectedValue;
                }
                EditorGUI.EndProperty();
            }

            static bool IsSGSR2Selected(UltraRenderPipelineSerializedCamera p)
            {
                var pipeline = UltraRenderPipeline.asset;
                if (pipeline == null || pipeline.upscalingFilter != UpscalingFilterSelection.SGSR2)
                    return false;

                // Camera initialization snaps render scales within 0.05 of one to one.
                if (pipeline.renderScale > 1.0f && Mathf.Abs(1.0f - pipeline.renderScale) >= 0.05f)
                    return false;

                foreach (var target in p.serializedObject.targetObjects)
                {
                    if (target is not Camera camera || camera.cameraType == CameraType.SceneView ||
                        camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection)
                        return false;
                }
                return true;
            }

            static void DrawerRenderingSMAAQuality(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                EditorGUILayout.PropertyField(p.antialiasingQuality, Styles.antialiasingQuality);
            }

            static void DrawerRenderingTAAQuality(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                EditorGUILayout.PropertyField(p.taaQuality, Styles.antialiasingQuality);

                {
                    // FSR overrides TAA CAS settings. Disable this setting when FSR is enabled.
                    bool disableSharpnessControl = UltraRenderPipeline.asset != null ?
                        (UltraRenderPipeline.asset.upscalingFilter == UpscalingFilterSelection.FSR) : false;
                    using var disable = new EditorGUI.DisabledScope(disableSharpnessControl);

                    EditorGUILayout.Slider(p.taaContrastAdaptiveSharpening, 0.0f, 1.0f, Styles.taaContrastAdaptiveSharpening);
                }

                bool additionalPropertiesVisible = s_expandedState[Expandable.Rendering] && s_expandedAdditionalState[ExpandableAdditional.Rendering];
                if (additionalPropertiesVisible)
                {
                    p.taaFrameInfluence.floatValue = 1.0f - EditorGUILayout.Slider(Styles.taaBaseBlendFactor, 1.0f - p.taaFrameInfluence.floatValue, 0.6f, 0.98f);
                    EditorGUILayout.Slider(p.taaJitterScale, 0.0f, 1.0f, Styles.taaJitterScale);
                    EditorGUILayout.Slider(p.taaMipBias, -1.0f, 0.0f, Styles.taaMipBias);

                    if (p.taaQuality.intValue >= (int)TemporalAAQuality.Medium)
                        EditorGUILayout.Slider(p.taaVarianceClampScale, 0.6f, 1.2f, Styles.taaVarianceClampScale);

                    bool isEditorInDeveloperMode = EditorPrefs.GetBool("DeveloperMode");
                    if (isEditorInDeveloperMode)
                    {
                        UltraRenderPipelineCameraEditor ultraRPCamEditor = owner as UltraRenderPipelineCameraEditor;
                        if (ultraRPCamEditor != null && ultraRPCamEditor.camera != null &&
                            ultraRPCamEditor.camera.TryGetComponent<UltraAdditionalCameraData>(out var urpAddCamData))
                        {
                            ref var taa = ref urpAddCamData.taaSettings;
                            var rect = GUILayoutUtility.GetRect(Styles.taaResetHistory, GUIStyle.none);
                            rect.x = rect.x + 32;
                            rect.width = rect.width - 32;
                            rect.height += 4; // avoid clipping the label.
                            if (GUI.Button(rect, Styles.taaResetHistory))
                                taa.resetHistoryFrames += 2; // XR both eyes
                        }
                    }
                }
            }

            static void DrawerRenderingClearDepth(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                EditorGUILayout.PropertyField(p.clearDepth, Styles.clearDepth);
            }

            static void DrawerRenderingRenderShadows(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                EditorGUILayout.PropertyField(p.renderShadows, Styles.renderingShadows);
            }

            static void DrawerRenderingRenderPostProcessing(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                EditorGUILayout.PropertyField(p.renderPostProcessing, Styles.renderPostProcessing);
            }

            static void DrawerRenderingPriority(UltraRenderPipelineSerializedCamera p, Editor owner)
            {
                EditorGUILayout.PropertyField(p.baseCameraSettings.depth, Styles.priority);
            }

            static bool IsPostProcessDataMissing(UltraRenderPipelineSerializedCamera p, UltraRenderPipelineAsset rpAsset)
            {
                if (rpAsset == null || rpAsset.rendererGraphList == null || p.renderer.hasMultipleDifferentValues)
                    return false;

                int selectedRendererOption = p.renderer.intValue;
                if (selectedRendererOption < 0 || selectedRendererOption >= rpAsset.rendererGraphList.Length ||
                    rpAsset.rendererGraphList[selectedRendererOption] == null)
                    selectedRendererOption = rpAsset.defaultRendererIndex;
                if (selectedRendererOption < 0 || selectedRendererOption >= rpAsset.rendererGraphList.Length)
                    return false;

                return rpAsset.rendererGraphList[selectedRendererOption] is UltraRendererGraph rendererGraph &&
                    rendererGraph.postProcessData == null;
            }
        }
    }
}
