using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Class that stores the shader stripping settings that are specific for <see cref="UltraRenderPipeline"/>
    /// </summary>
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    [UnityEngine.Rendering.Categorization.CategoryInfo(Name = "Additional Shader Stripping Settings", Order = 40)]
    [UnityEngine.Rendering.Categorization.ElementInfo(Order = 10)]
    public class UltraRenderPipelineShaderStrippingSetting : IRenderPipelineGraphicsSettings
    {
        #region Version
        internal enum Version : int
        {
            Initial = 0,
        }

        [SerializeField][HideInInspector]
        private Version _version;

        /// <summary>Current version.</summary>
        public int version => (int)_version;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => false;
        #endregion

        #region SerializeFields
        [SerializeField]
        [Tooltip("Opt in to post processing variant stripping based on all VolumeProfile assets, regardless of scene usage. Runtime-created effects must also be declared by a VolumeProfile asset; otherwise leave this disabled. Internal HDR grading and all declared Bloom modes are retained. Explicit screen-coordinate/debug stripping and Player HDR display settings apply to output variants.")]
        bool _stripUnusedPostProcessingVariants = false;

        [SerializeField]
        [Tooltip("Prefilters unused shader keywords across included pipeline assets and renderer graphs, and removes unused StencilDeferred and Clustered DeferredLit paths when proven unnecessary. Explicit debug, screen-coordinate and HDR display settings apply independently of VolumeProfile effect stripping. Declare every runtime-selected rendering configuration, or disable this for undeclared runtime feature changes.")]
        bool _stripUnusedVariants = true;

        [SerializeField]
        [Tooltip("Controls whether Screen Coordinates Override shader variants are automatically stripped.")]
        bool _stripScreenCoordOverrideVariants = true;
        #endregion

        #region Data Accessors

        /// <summary>
        /// Controls whether to automatically strip post processing shader variants based on <see cref="VolumeProfile"/> components.
        /// Stripping is done based on VolumeProfiles in project, their usage in scenes is not considered.
        /// Runtime-created effects must also be represented by a VolumeProfile asset, or this setting must remain disabled.
        /// </summary>
        public bool stripUnusedPostProcessingVariants
        {
            get => _stripUnusedPostProcessingVariants;
            set => this.SetValueAndNotify(ref _stripUnusedPostProcessingVariants, value);
        }

        /// <summary>
        /// Filters unused feature keywords and lighting paths across declared pipeline assets and renderer graphs.
        /// Applies explicit build settings independently of VolumeProfile effect stripping.
        /// Disable this for undeclared runtime pipeline assets, renderer graphs or feature mode changes.
        /// </summary>
        public bool stripUnusedVariants
        {
            get => _stripUnusedVariants;
            set => this.SetValueAndNotify(ref _stripUnusedVariants, value);
        }

        /// <summary>
        /// Controls whether Screen Coordinates Override shader variants are automatically stripped.
        /// </summary>
        public bool stripScreenCoordOverrideVariants
        {
            get => _stripScreenCoordOverrideVariants;
            set => this.SetValueAndNotify(ref _stripScreenCoordOverrideVariants, value);
        }
        #endregion
    }
}
