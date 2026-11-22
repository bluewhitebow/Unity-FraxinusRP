#if UNITY_EDITOR
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
    [RenderingCategories.CategoryInfo(Name = "R: Editor Assets", Order = 1000), HideInInspector]
    class UltraRenderPipelineEditorAssets : IRenderPipelineResources
    {
        public int version => 0;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => false;

        [SerializeField]
        [ResourcePath("Editor/UltraRPEditor/Volume/DefaultVolumeProfile.asset")]
        private VolumeProfile _defaultSettingsVolumeProfile;
        
        public VolumeProfile defaultVolumeProfile
        {
            get => _defaultSettingsVolumeProfile;
            set => this.SetValueAndNotify(ref _defaultSettingsVolumeProfile, value);
        }
    }
}
#endif
