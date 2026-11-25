using UnityEngine;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;

namespace FraxinusEditor.Rendering.Ultra
{
    partial class UltraRenderPipelineLightUI
    {
        private static class Styles
        {
            public static readonly GUIContent type = EditorGUIUtility.TrTextContent("Type", "Specifies the current type of light. Possible types are Directional, Spot, Point, and Area lights.");

            public static readonly GUIContent areaLightShapeContent = EditorGUIUtility.TrTextContent("Shape", "Specifies the shape of the Area light. Possible types are Rectangle and Disc.");
            public static readonly GUIContent[] lightTypeTitles = { EditorGUIUtility.TrTextContent("Spot"), EditorGUIUtility.TrTextContent("Directional"), EditorGUIUtility.TrTextContent("Point"), EditorGUIUtility.TrTextContent("Area") };
            public static readonly int[] lightTypeValues = { (int)LightType.Spot, (int)LightType.Directional, (int)LightType.Point, (int)LightType.Rectangle };

            public static readonly GUIContent[] areaLightShapeTitles = { EditorGUIUtility.TrTextContent("Rectangle"), EditorGUIUtility.TrTextContent("Disc") };
            public static readonly int[] areaLightShapeValues = { (int)LightType.Rectangle, (int)LightType.Disc };
            public static readonly GUIContent areaSize = EditorGUIUtility.TrTextContent("Size", "Rectangle width and height in world units. Transform scale does not affect the light size.");
            public static readonly GUIContent areaDiffuseDimmer = EditorGUIUtility.TrTextContent("Diffuse Contribution");
            public static readonly GUIContent areaSpecularDimmer = EditorGUIUtility.TrTextContent("Specular Contribution");
            public static readonly GUIContent areaLightMode = EditorGUIUtility.TrTextContent("Mode");
            public static readonly GUIContent[] areaLightModeTitles = { EditorGUIUtility.TrTextContent("Realtime"), EditorGUIUtility.TrTextContent("Baked") };
            public static readonly int[] areaLightModeValues = { (int)LightmapBakeType.Realtime, (int)LightmapBakeType.Baked };
            public static readonly GUIContent[] areaLightMixedModeTitles = { EditorGUIUtility.TrTextContent("Realtime"), EditorGUIUtility.TrTextContent("Baked"), EditorGUIUtility.TrTextContent("Mixed (unsupported)") };
            public static readonly int[] areaLightMixedModeValues = { (int)LightmapBakeType.Realtime, (int)LightmapBakeType.Baked, (int)LightmapBakeType.Mixed };

            public static readonly GUIContent spotAngle = EditorGUIUtility.TrTextContent("Spot Angle", "Controls the angle in degrees at the base of a Spot light's cone.");
            public static readonly GUIContent lightRadius = EditorGUIUtility.TrTextContent("Radius", "Controls the radius of the point light. Affects specular highlights and light contribution.");

            public static readonly GUIContent bakingWarning = EditorGUIUtility.TrTextContent("Light mode is currently overridden to Realtime mode. Enable Baked Global Illumination to use Mixed or Baked light modes.");
            public static readonly GUIContent disabledLightWarning = EditorGUIUtility.TrTextContent("Lighting has been disabled in at least one Scene view. Any changes applied to lights in the Scene will not be updated in these views until Lighting has been enabled again.");
            public static readonly GUIContent sunSourceWarning = EditorGUIUtility.TrTextContent("This light is set as the current Sun Source, which requires a directional light. Go to the Lighting Window's Environment settings to edit the Sun Source.");
            public static readonly GUIContent cullingMask = EditorGUIUtility.TrTextContent("Culling Mask", "Specifies which lights are culled per camera. To control exclude certain lights affecting certain objects, use Rendering Layers instead, which is supported across all rendering paths.");
            public static readonly GUIContent cullingMaskWarning = EditorGUIUtility.TrTextContent("Culling Mask should be used to control which lights are culled per camera. If you want to exclude certain lights from affecting certain objects, use Rendering Layers on the Light, and Rendering Layer Mask on the Mesh Renderer.");

            public static readonly GUIContent shadowRealtimeSettings = EditorGUIUtility.TrTextContent("Realtime Shadows", "Settings for realtime direct shadows.");
            public static readonly GUIContent shadowStrength = EditorGUIUtility.TrTextContent("Strength", "Controls how dark the shadows cast by the light will be.");
            public static readonly GUIContent shadowNearPlane = EditorGUIUtility.TrTextContent("Near Plane", "Controls the value for the near clip plane when rendering shadows. Currently clamped to 0.1 units or 1% of the lights range property, whichever is lower.");
            public static readonly GUIContent shadowNormalBias = EditorGUIUtility.TrTextContent("Normal", "Determines the bias this Light applies along the normal of surfaces it illuminates. This is ignored for point lights.");
            public static readonly GUIContent shadowDepthBias = EditorGUIUtility.TrTextContent("Depth", "Determines the bias at which shadows are pushed away from the shadow-casting Game Object along the line from the Light.");
            public static readonly GUIContent shadowInfo = EditorGUIUtility.TrTextContent("Unity might reduce the Light's shadow resolution to ensure that shadow maps fit in the shadow atlas. Consider this when selecting the the size of the shadow atlas, the shadow resolution of Lights, the number of Lights in your scene and whether you use soft shadows.");

            // Resolution (default or custom)
            public static readonly GUIContent shadowResolution = EditorGUIUtility.TrTextContent("Resolution", $"Sets the rendered resolution of the shadow maps. A higher resolution increases the fidelity of shadows at the cost of GPU performance and memory usage. Rounded to the next power of two, and clamped to be at least {UltraAdditionalLightData.additionalLightsShadowMinimumResolution}.");
            public static readonly int[] shadowResolutionDefaultValues =
            {
                UltraAdditionalLightData.additionalLightsShadowResolutionTierCustom,
                UltraAdditionalLightData.additionalLightsShadowResolutionTierLow,
                UltraAdditionalLightData.additionalLightsShadowResolutionTierMedium,
                UltraAdditionalLightData.additionalLightsShadowResolutionTierHigh
            };
            public static readonly GUIContent[] shadowResolutionDefaultOptions =
            {
                EditorGUIUtility.TrTextContent("Custom"),
                UltraRenderPipelineAssetUI.Styles.additionalLightsShadowResolutionTierNames[0],
                UltraRenderPipelineAssetUI.Styles.additionalLightsShadowResolutionTierNames[1],
                UltraRenderPipelineAssetUI.Styles.additionalLightsShadowResolutionTierNames[2],
            };

            public static GUIContent softShadowQuality = EditorGUIUtility.TrTextContent("Soft Shadows Quality", "Controls the filtering quality of soft shadows. Higher quality has lower performance.");

            // Bias (default or custom)
            public static GUIContent shadowBias = EditorGUIUtility.TrTextContent("Bias", "Select if the Bias should use the settings from the Pipeline Asset or Custom settings.");
            public static int[] optionDefaultValues = { 0, 1 };
            public static GUIContent[] displayedDefaultOptions =
            {
                EditorGUIUtility.TrTextContent("Custom"),
                EditorGUIUtility.TrTextContent("Use settings from Render Pipeline Asset")
            };

            public static readonly GUIContent customShadowLayers = EditorGUIUtility.TrTextContent("Custom Shadow Layers", "When enabled, you can use the Layer property below to specify the layers for shadows seperately to lighting. When disabled, the Light Layer property in the General section specifies the layers for both lighting and for shadows.");
            public static readonly GUIContent shadowLayer = EditorGUIUtility.TrTextContent("Layer", "Specifies the light layer to use for shadows.");

            public static readonly GUIContent lightCookieSize = EditorGUIUtility.TrTextContent("Cookie Size", "Controls the size of the cookie mask currently assigned to the light.");
            public static readonly GUIContent lightCookieOffset = EditorGUIUtility.TrTextContent("Cookie Offset", "Controls the offset of the cookie mask currently assigned to the light.");
            /// <summary>Title with "Rendering Layer"</summary>
            public static readonly GUIContent renderingLayers = EditorGUIUtility.TrTextContent("Rendering Layers", "Select the Rendering Layers that the Light affects. This Light affects objects where at least one Rendering Layer value matches.");
            public static readonly GUIContent renderingLayersDisabled = EditorGUIUtility.TrTextContent("Rendering Layers", "Rendering Layers are disabled because they have a small GPU performance cost. To enable this setting, go to the active UltraRP Asset, and enable Lighting -> Use Rendering Layers.");
        }
    }
}
