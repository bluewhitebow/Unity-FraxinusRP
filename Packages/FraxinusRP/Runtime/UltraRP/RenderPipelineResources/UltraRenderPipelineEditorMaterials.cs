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
    [RenderingCategories.CategoryInfo(Name = "R: Editor Materials", Order = 1000), HideInInspector]
    class UltraRenderPipelineEditorMaterials : IRenderPipelineResources
    {
        public int version => 0;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => false;

        [SerializeField]
        [ResourcePath("Runtime/Materials/Lit.mat")]
        private Material _defaultMaterial;

        public virtual Material defaultMaterial
        {
            get => _defaultMaterial;
            set => this.SetValueAndNotify(ref _defaultMaterial, value);
        }

        [SerializeField]
        [ResourcePath("Runtime/Materials/ParticlesUnlit.mat")]
        private Material _defaultParticleMaterial;

        public virtual Material defaultParticleUnlitMaterial
        {
            get => _defaultParticleMaterial;
            set => this.SetValueAndNotify(ref _defaultParticleMaterial, value);
        }

        [SerializeField]
        [ResourcePath("Runtime/Materials/ParticlesUnlit.mat")]
        private Material _defaultLineMaterial;

        public virtual Material defaultLineMaterial
        {
            get => _defaultLineMaterial;
            set => this.SetValueAndNotify(ref _defaultLineMaterial, value);
        }

        [SerializeField]
        [ResourcePath("Runtime/Materials/TerrainLit.mat")]
        private Material _defaultTerrainMaterial;

        public virtual Material defaultTerrainLitMaterial
        {
            get => _defaultTerrainMaterial;
            set => this.SetValueAndNotify(ref _defaultTerrainMaterial, value);
        }

        [SerializeField]
        // A default decal material is not provided until the decal Shader Graph target is ported.
        private Material _defaultDecalMaterial;

        public virtual Material defaultDecalMaterial
        {
            get => _defaultDecalMaterial;
            set => this.SetValueAndNotify(ref _defaultDecalMaterial, value);
        }

        [SerializeField]
        [ResourcePath("Runtime/Materials/Sprite-Unlit-Default.mat")]
        private Material _defaultSpriteMaterial;

        public virtual Material defaultSpriteMaterial
        {
            get => _defaultSpriteMaterial;
            set => this.SetValueAndNotify(ref _defaultSpriteMaterial, value);
        }

        [SerializeField, ResourcePath("Runtime/Materials/Sprite-Mask.mat")]
        private Material _defaultSpriteMaskMaterial;

        public virtual Material defaultSpriteMaskMaterial
        {
            get => _defaultSpriteMaskMaterial;
            set => this.SetValueAndNotify(ref _defaultSpriteMaskMaterial, value);
        }
    }
}
#endif
