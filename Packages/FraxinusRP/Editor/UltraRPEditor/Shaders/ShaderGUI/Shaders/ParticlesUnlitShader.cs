using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FraxinusEditor.Rendering.Ultra
{
    internal class ParticlesUnlitShader : BaseShaderGUI
    {
        // Properties
        private BakedLitGUI.BakedLitProperties _shadingModelProperties;
        private ParticleGUI.ParticleProperties _particleProps;

        // List of renderers using this material in the scene, used for validating vertex streams
        private List<ParticleSystemRenderer> _renderersUsingThisMaterial = new List<ParticleSystemRenderer>();

        public override void FindProperties(MaterialProperty[] properties)
        {
            base.FindProperties(properties);
            _shadingModelProperties = new BakedLitGUI.BakedLitProperties(properties);
            _particleProps = new ParticleGUI.ParticleProperties(properties);
        }

        public override void ValidateMaterial(Material material)
        {
            SetMaterialKeywords(material, null, ParticleGUI.SetMaterialKeywords);
        }

        public override void DrawSurfaceOptions(Material material)
        {
            base.DrawSurfaceOptions(material);
            DoPopup(ParticleGUI.Styles.colorMode, _particleProps.colorMode, Enum.GetNames(typeof(ParticleGUI.ColorMode)));
        }

        public override void DrawSurfaceInputs(Material material)
        {
            base.DrawSurfaceInputs(material);
            BakedLitGUI.Inputs(_shadingModelProperties, materialEditor);
            DrawEmissionProperties(material, true);
        }

        public override void DrawAdvancedOptions(Material material)
        {
            materialEditor.ShaderProperty(_particleProps.flipbookMode, ParticleGUI.Styles.flipbookMode);
            ParticleGUI.FadingOptions(material, materialEditor, _particleProps);
            ParticleGUI.DoVertexStreamsArea(material, _renderersUsingThisMaterial);

            DrawQueueOffsetField();
        }

        public override void OnOpenGUI(Material material, MaterialEditor materialEditor)
        {
            CacheRenderersUsingThisMaterial(material);
            base.OnOpenGUI(material, materialEditor);
        }

        private void CacheRenderersUsingThisMaterial(Material material)
        {
            _renderersUsingThisMaterial.Clear();

            ParticleSystemRenderer[] renderers = UnityEngine.Object.FindObjectsByType<ParticleSystemRenderer>(FindObjectsSortMode.InstanceID);
            foreach (ParticleSystemRenderer renderer in renderers)
            {
                if (renderer.sharedMaterial == material)
                    _renderersUsingThisMaterial.Add(renderer);
            }
        }
    }
} // namespace FraxinusEditor.Rendering.Ultra
