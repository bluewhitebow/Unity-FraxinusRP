using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public class LensDistortionProcessor : IUberEffectProcessor
    {
        static class ShaderConstants
        {
            public static readonly int _Distortion_Params1 = Shader.PropertyToID("_Distortion_Params1");
            public static readonly int _Distortion_Params2 = Shader.PropertyToID("_Distortion_Params2");
        }

        public Type componentType => typeof(LensDistortion);
        public string displayName => "Lens Distortion";

        public bool Valid(bool enable)
        {
            return enable;
        }


        public void Setup(Material material, VolumeComponent component, PostProcessPass pass, CameraData cameraData)
        {
            if (cameraData.isSceneViewCamera)
                return;

            var lensDistortion = component as LensDistortion;

            float amount = 1.6f * Mathf.Max(Mathf.Abs(lensDistortion.intensity.value * 100f), 1f);
            float theta = Mathf.Deg2Rad * Mathf.Min(160f, amount);
            float sigma = 2f * Mathf.Tan(theta * 0.5f);
            var center = lensDistortion.center.value * 2f - Vector2.one;
            
            var p1 = new Vector4(
                center.x,
                center.y,
                Mathf.Max(lensDistortion.xMultiplier.value, 1e-4f),
                Mathf.Max(lensDistortion.yMultiplier.value, 1e-4f)
            );
            var p2 = new Vector4(
                lensDistortion.intensity.value >= 0f ? theta : 1f / theta,
                sigma,
                1f / lensDistortion.scale.value,
                lensDistortion.intensity.value * 100f
            );

            material.SetVector(ShaderConstants._Distortion_Params1, p1);
            material.SetVector(ShaderConstants._Distortion_Params2, p2);
            material.EnableKeyword(ShaderKeywordStrings.Distortion);
        }
    }
}

