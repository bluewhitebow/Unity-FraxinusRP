using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public class FilmGrainProcessor : IUberEffectProcessor
    {
        public Type componentType => typeof(FilmGrain);
        public string displayName => "Film Grain";

        public bool Valid(bool enable)
        {
            return enable;
        }


        public void Setup(Material material, VolumeComponent component, PostProcessPass pass, CameraData cameraData)
        {
            var filmGrain = component as FilmGrain;

            // Film Grain is only applied in Uber pass if there's no final pass
            // This check is handled in PostProcessPass, but we still configure it here
            material.EnableKeyword(ShaderKeywordStrings.FilmGrain);
            PostProcessUtils.ConfigureFilmGrain(
                pass.data,
                filmGrain,
                cameraData.pixelWidth,
                cameraData.pixelHeight,
                material
            );
        }
    }
}

