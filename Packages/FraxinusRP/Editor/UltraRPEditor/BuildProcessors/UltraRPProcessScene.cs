using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;

namespace FraxinusEditor.Rendering.Ultra
{
    class UltraRPProcessScene : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, BuildReport report)
        {
            bool usesUltraRP = false;
            if (GraphicsSettings.defaultRenderPipeline as UltraRenderPipelineAsset != null)
            {
                // ^ The global pipeline is set to UltraRP
                usesUltraRP = true;
            }
            else
            {
                // ^ The global pipeline isn't set to UltraRP, but a quality setting could still use it
                for (int i = 0; i < QualitySettings.count; i++)
                {
                    if (QualitySettings.GetRenderPipelineAssetAt(i) as UltraRenderPipelineAsset != null)
                    {
                        // ^ This quality setting uses UltraRP
                        usesUltraRP = true;
                        break;
                    }
                }
            }

            if (usesUltraRP)
            {
                GameObject[] roots = scene.GetRootGameObjects();

                foreach (GameObject root in roots)
                {
                    Light[] lights = root.GetComponentsInChildren<Light>(true);
                    foreach (Light light in lights)
                    {
                        if (light.type != LightType.Directional &&
                            light.type != LightType.Point &&
                            light.type != LightType.Spot &&
                            light.type != LightType.Rectangle &&
                            light.type != LightType.Disc)
                        {
                            Debug.LogWarning(
                                $"The {light.type} light type on the GameObject '{light.gameObject.name}' is unsupported by UltraRP, and will not be rendered."
                            );
                        }
                        else if (light.type == LightType.Rectangle && light.lightmapBakeType == LightmapBakeType.Mixed)
                        {
                            Debug.LogWarning(
                                $"The rectangle light '{light.gameObject.name}' uses Mixed mode, which UltraRP does not support. Choose Realtime or Baked."
                            );
                        }
                        else if (light.type == LightType.Rectangle && light.lightmapBakeType == LightmapBakeType.Realtime)
                        {
                            // Scene processing runs before player serialization. Creating
                            // the existing data component here does not modify the source scene.
                            if (!light.TryGetComponent<UltraAdditionalLightData>(out var data))
                            {
                                data = light.gameObject.AddComponent<UltraAdditionalLightData>();
                            }
                            data.InitializeAreaSizeFromLight();
                        }
                        else if (light.type == LightType.Disc && light.lightmapBakeType != LightmapBakeType.Baked)
                        {
                            Debug.LogWarning($"The disc light '{light.gameObject.name}' only supports Baked mode in UltraRP.");
                        }

                        if (light.type == LightType.Rectangle && light.TryGetComponent<UltraAdditionalLightData>(out var areaData))
                            areaData.InitializeAreaSizeFromLight();
                    }
                }
            }
        }
    }
}
