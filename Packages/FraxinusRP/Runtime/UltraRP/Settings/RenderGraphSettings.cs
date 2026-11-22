using System;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_6000_0_OR_NEWER
using RenderingCategories = UnityEngine.Categorization;
#else
using RenderingCategories = UnityEngine.Rendering.Categorization;
#endif

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    [RenderingCategories.CategoryInfo(Name = "Render Graph", Order = 50)]
    [RenderingCategories.ElementInfo(Order = -10)]
    public class RenderGraphSettings : IRenderPipelineGraphicsSettings
    {
        public int version => 0;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => true;
    }

}