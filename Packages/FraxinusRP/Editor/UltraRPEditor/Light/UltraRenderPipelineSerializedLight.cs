using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEditor.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    internal class UltraRenderPipelineSerializedLight : ISerializedLight
    {
        /// <summary>The base settings of the light</summary>
        public LightEditor.Settings settings { get; }
        /// <summary>The light serialized</summary>
        public SerializedObject serializedObject { get; }
        /// <summary>The additional light data serialized</summary>
        public SerializedObject serializedAdditionalDataObject { get; private set; }

        public UltraAdditionalLightData additionalLightData => lightsAdditionalData[0];
        public UltraAdditionalLightData[] lightsAdditionalData { get; private set; }

        // Common SRP's Lights properties
        public SerializedProperty intensity { get; }

        // UltraRP Light Properties
        public SerializedProperty useAdditionalDataProp { get; }                     // Does light use shadow bias settings defined in UltraRP asset file?
        public SerializedProperty additionalLightsShadowResolutionTierProp { get; }  // Index of the AdditionalLights ShadowResolution Tier
        public SerializedProperty softShadowQualityProp { get; }                     // Per light soft shadow filtering quality.
        public SerializedProperty lightCookieSizeProp { get; }                       // Multi dimensional light cookie size replacing `cookieSize` in legacy light.
        public SerializedProperty lightCookieOffsetProp { get; }                     // Multi dimensional light cookie offset.
        public SerializedProperty shapeRadius { get; }                               // Point light shape radius
        public SerializedProperty areaSize { get; }
        public SerializedProperty areaDiffuseDimmer { get; }
        public SerializedProperty areaSpecularDimmer { get; }

        // Light layers related
        public SerializedProperty renderingLayers { get; }
        public SerializedProperty customShadowLayers { get; }
        public SerializedProperty shadowRenderingLayers { get; }

        /// <summary>Method that updates the <see cref="SerializedObject"/> of the Light and the Additional Light Data</summary>
        public void Update()
        {
            foreach (var data in lightsAdditionalData)
                data.InitializeAreaSizeFromLight();
            serializedObject.Update();
            serializedAdditionalDataObject.Update();
            settings.Update();
        }

        /// <summary>Method that applies the modified properties the <see cref="SerializedObject"/> of the Light and the Light Camera Data</summary>
        public void Apply()
        {
            serializedObject.ApplyModifiedProperties();
            serializedAdditionalDataObject.ApplyModifiedProperties();
            settings.ApplyModifiedProperties();
            foreach (var data in lightsAdditionalData)
            {
                data.InitializeAreaSizeFromLight();
                data.SyncAreaSizeToLight();
            }
        }

        /// <summary>Constructor</summary>
        /// <param name="serializedObject"><see cref="SerializedObject"/> with the light</param>
        /// <param name="settings"><see cref="LightEditor.Settings"/>with the settings</param>
        public UltraRenderPipelineSerializedLight(SerializedObject serializedObject, LightEditor.Settings settings)
        {
            this.settings = settings;
            settings.OnEnable();

            this.serializedObject = serializedObject;

            lightsAdditionalData = CoreEditorUtils
                .GetAdditionalData<UltraAdditionalLightData>(serializedObject.targetObjects);
            serializedAdditionalDataObject = new SerializedObject(lightsAdditionalData);

            intensity = serializedObject.FindProperty("_intensity");

            useAdditionalDataProp = serializedAdditionalDataObject.FindProperty("_usePipelineSettings");
            additionalLightsShadowResolutionTierProp = serializedAdditionalDataObject.FindProperty("_additionalLightsShadowResolutionTier");
            softShadowQualityProp = serializedAdditionalDataObject.FindProperty("_softShadowQuality");
            lightCookieSizeProp = serializedAdditionalDataObject.FindProperty("_lightCookieSize");
            lightCookieOffsetProp = serializedAdditionalDataObject.FindProperty("_lightCookieOffset");

            renderingLayers = serializedAdditionalDataObject.FindProperty("_renderingLayers");
            customShadowLayers = serializedAdditionalDataObject.FindProperty("_customShadowLayers");
            shadowRenderingLayers = serializedAdditionalDataObject.FindProperty("_shadowRenderingLayers");

            shapeRadius = serializedAdditionalDataObject.FindProperty("_shapeRadius");
            areaSize = serializedAdditionalDataObject.FindProperty("_areaSize");
            areaDiffuseDimmer = serializedAdditionalDataObject.FindProperty("_areaDiffuseDimmer");
            areaSpecularDimmer = serializedAdditionalDataObject.FindProperty("_areaSpecularDimmer");

            settings.ApplyModifiedProperties();
        }
    }
}
