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
    [RenderingCategories.CategoryInfo(Name = "R: Runtime Textures", Order = 1000), HideInInspector]
    public class UltraRenderPipelineRuntimeTextures : IRenderPipelineResources
    {
        [SerializeField][HideInInspector] private int _version = 1;

        public int version => _version;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => true;

        [SerializeField]
        [ResourcePath("Textures/BlueNoise64/L/LDR_LLL1_0.png")]
        private Texture2D _blueNoise64LTex;

        /// <summary>
        /// Pre-baked blue noise textures.
        /// </summary>
        public Texture2D blueNoise64LTex
        {
            get => _blueNoise64LTex;
            set => this.SetValueAndNotify(ref _blueNoise64LTex, value, nameof(_blueNoise64LTex));
        }

        [SerializeField]
        [ResourcePath("Textures/BayerMatrix.png")]
        private Texture2D _bayerMatrixTex;

        /// <summary>
        /// Bayer matrix texture.
        /// </summary>
        public Texture2D bayerMatrixTex
        {
            get => _bayerMatrixTex;
            set => this.SetValueAndNotify(ref _bayerMatrixTex, value, nameof(_bayerMatrixTex));
        }

        [SerializeField]
        [ResourcePath("Textures/DebugFont.tga")]
        private Texture2D _debugFontTex;

        /// <summary>
        /// Debug font texture.
        /// </summary>
        public Texture2D debugFontTexture
        {
            get => _debugFontTex;
            set => this.SetValueAndNotify(ref _debugFontTex, value, nameof(_debugFontTex));
        }

        [SerializeField]
        [ResourcePath("Textures/Sky/DefaultHDRISky.exr")]
        private Cubemap _defaultHDRISky;

        /// <summary>
        /// Default HDRI sky cubemap.
        /// </summary>
        public Cubemap defaultHDRISky
        {
            get => _defaultHDRISky;
            set => this.SetValueAndNotify(ref _defaultHDRISky, value, nameof(_defaultHDRISky));
        }

        [SerializeField]
        [ResourcePath("Textures/ShadowRamp/DirectionalShadowRamp.png")]
        private Texture2D _defaultDirShadowRampTex;

        /// <summary>
        /// Default directional shadow ramp texture.
        /// </summary>
        public Texture2D defaultDirShadowRampTex
        {
            get => _defaultDirShadowRampTex;
            set => this.SetValueAndNotify(ref _defaultDirShadowRampTex, value, nameof(_defaultDirShadowRampTex));
        }
    }
}
