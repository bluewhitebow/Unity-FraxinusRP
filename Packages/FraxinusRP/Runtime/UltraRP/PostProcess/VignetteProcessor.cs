using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public class VignetteProcessor : IUberEffectProcessor
    {
        static class ShaderConstants
        {
            public static readonly int _Vignette_Params1 = Shader.PropertyToID("_Vignette_Params1");
            public static readonly int _Vignette_Params2 = Shader.PropertyToID("_Vignette_Params2");
        }

        public Type componentType => typeof(Vignette);
        public string displayName => "Vignette";

        private bool _enbale;


        public bool Valid(bool enable)
        {
            _enbale = enable;
            return true;
        }

        public void Setup(Material material, VolumeComponent component, PostProcessPass pass, CameraData cameraData)
        {
            var vignette = component as Vignette;
   
            var color = vignette.color.value;
            var center = vignette.center.value;
            var descriptor = pass.descriptor;
            var aspectRatio = descriptor.width / (float)descriptor.height;

            var v1 = new Vector4(
                color.r, color.g, color.b,
                vignette.rounded.value ? aspectRatio : 1f
            );
            var v2 = new Vector4(
                center.x, center.y,
                vignette.intensity.value * 3f * (_enbale ? 1f : 0f),
                vignette.smoothness.value * 5f
            );

            material.SetVector(ShaderConstants._Vignette_Params1, v1);
            material.SetVector(ShaderConstants._Vignette_Params2, v2);
        }
    }
}

