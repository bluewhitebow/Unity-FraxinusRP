using System.Linq;
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    class UltraRenderPipelineGlobalSettingsPostprocessor : AssetPostprocessor
    {
        const string k_graphicsSettingsPath = "ProjectSettings/GraphicsSettings.asset";
        static void OnPostprocessAllAssets(string[] importedAssets , string[] __, string[] ___, string[] ____, bool didDomainReload)
        {
            if (GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset &&
                (didDomainReload || importedAssets.Contains(k_graphicsSettingsPath)))
                UltraRenderPipelineGlobalSettings.Ensure();
        }
    }
}
