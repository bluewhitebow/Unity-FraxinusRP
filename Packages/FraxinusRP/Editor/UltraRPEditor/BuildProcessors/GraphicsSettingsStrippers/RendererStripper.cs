using FraxinusEditor.Rendering.Ultra;
using FraxinusEngine.Rendering.Ultra;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering
{
    abstract class RendererStripper<T, S> : IRenderPipelineGraphicsSettingsStripper<T>
        where T : IRenderPipelineGraphicsSettings
        where S : ScriptableRendererGraph
    {
        public bool active => UltraRPBuildData.instance.buildingPlayerForUltraRenderPipeline;

        public bool CanRemoveSettings(T settings)
        {
            foreach (var ultraAssetForBuild in UltraRPBuildData.instance.renderPipelineAssets)
            {
                foreach (var rendererGraph in ultraAssetForBuild.rendererGraphList)
                    if (rendererGraph is S)
                        return false;
            }

            return true;
        }
    }

    class UltraRendererResourcesStripper : RendererStripper<UltraRendererResources, UltraRendererGraph> { }
}
