using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public class ChromaticAberrationProcessor : IUberEffectProcessor
    {
        static class ShaderConstants
        {
            public static readonly int _Chroma_Params = Shader.PropertyToID("_Chroma_Params");
        }

        public Type componentType => typeof(ChromaticAberration);
        public string displayName => "Chromatic Aberration";

        public bool Valid(bool enable)
        {
            return enable;
        }


        public void Setup(Material material, VolumeComponent component, PostProcessPass pass, CameraData cameraData)
        {
            var chromaticAberration = component as ChromaticAberration;
 
            material.SetFloat(ShaderConstants._Chroma_Params, chromaticAberration.intensity.value * 0.05f);
            material.EnableKeyword(ShaderKeywordStrings.ChromaticAberration);
        }
    }
}

