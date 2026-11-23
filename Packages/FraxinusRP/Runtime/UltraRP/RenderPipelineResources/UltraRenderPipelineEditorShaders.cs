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
    [RenderingCategories.CategoryInfo(Name = "R: Editor Shaders", Order = 1000), HideInInspector]
    class UltraRenderPipelineEditorShaders : IRenderPipelineResources
    {
        public int version => 0;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => false;

        private const string k_brgPickingShaderPath = "Shaders/BRGPicking.shader";

        [SerializeField, ResourcePath(k_brgPickingShaderPath)]
        private Shader _brgPickingShader;

        public Shader brgPickingShader
        {
            get => _brgPickingShader;
            set => this.SetValueAndNotify(ref _brgPickingShader, value);
        }

        internal static Shader ResolveBRGPickingShader(UltraRenderPipelineEditorShaders resources)
        {
            if (resources?.brgPickingShader != null)
                return resources.brgPickingShader;

            return UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(UltraRenderPipelineAsset.packagePath + "/" + k_brgPickingShaderPath);
        }

        #region Autodesk
        [Header("Autodesk")]
        [SerializeField]
        // [ResourcePath("Shaders/AutodeskInteractive/AutodeskInteractive.shadergraph")]
        private Shader _autodeskInteractive;

        public Shader autodeskInteractiveShader
        {
            get => _autodeskInteractive;
            set => this.SetValueAndNotify(ref _autodeskInteractive, value);
        }

        [SerializeField]
        // [ResourcePath("Shaders/AutodeskInteractive/AutodeskInteractiveTransparent.shadergraph")]
        private Shader _autodeskInteractiveTransparent;

        public Shader autodeskInteractiveTransparentShader
        {
            get => _autodeskInteractiveTransparent;
            set => this.SetValueAndNotify(ref _autodeskInteractiveTransparent, value);
        }

        [SerializeField]
        // [ResourcePath("Shaders/AutodeskInteractive/AutodeskInteractiveMasked.shadergraph")]
        private Shader _autodeskInteractiveMasked;

        public Shader autodeskInteractiveMaskedShader
        {
            get => _autodeskInteractiveMasked;
            set => this.SetValueAndNotify(ref _autodeskInteractiveMasked, value);
        }
        #endregion

        #region Terrain
        [Header("Terrain")]
        [SerializeField]
        [ResourcePath("Shaders/Terrain/TerrainDetailLit.shader")]
        private Shader _terrainDetailLit;

        public Shader terrainDetailLitShader
        {
            get => _terrainDetailLit;
            set => this.SetValueAndNotify(ref _terrainDetailLit, value);
        }

        [SerializeField]
        [ResourcePath("Shaders/Terrain/WavingGrassBillboard.shader")]
        private Shader _terrainDetailGrassBillboard;

        public Shader terrainDetailGrassBillboardShader
        {
            get => _terrainDetailGrassBillboard;
            set => this.SetValueAndNotify(ref _terrainDetailGrassBillboard, value);
        }

        [SerializeField]
        [ResourcePath("Shaders/Terrain/WavingGrass.shader")]
        private Shader _terrainDetailGrass;

        public Shader terrainDetailGrassShader
        {
            get => _terrainDetailGrass;
            set => this.SetValueAndNotify(ref _terrainDetailGrass, value);
        }
        #endregion

        #region SpeedTree
        [Header("SpeedTree")]
        [SerializeField]
        [ResourcePath("Shaders/Nature/SpeedTree7.shader")]
        private Shader _defaultSpeedTree7Shader;

        public Shader defaultSpeedTree7Shader
        {
            get => _defaultSpeedTree7Shader;
            set => this.SetValueAndNotify(ref _defaultSpeedTree7Shader, value);
        }

        [SerializeField]
        [ResourcePath("Shaders/Nature/SpeedTree8.shader")]
        private Shader _defaultSpeedTree8Shader;

        public Shader defaultSpeedTree8Shader
        {
            get => _defaultSpeedTree8Shader;
            set => this.SetValueAndNotify(ref _defaultSpeedTree8Shader, value);
        }

        [SerializeField]
        // [ResourcePath("Shaders/Nature/SpeedTree9_URP.shadergraph")]
        private Shader _defaultSpeedTree9Shader;

        public Shader defaultSpeedTree9Shader
        {
            get => _defaultSpeedTree9Shader;
            set => this.SetValueAndNotify(ref _defaultSpeedTree9Shader, value);
        }
        #endregion
    }
}
#endif
