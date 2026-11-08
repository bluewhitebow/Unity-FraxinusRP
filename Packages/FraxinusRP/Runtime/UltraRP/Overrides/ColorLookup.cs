using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable, VolumeComponentMenu("Post-processing/Color Lookup")]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    public sealed class ColorLookup : VolumeComponent, IPostProcessComponent
    {
        public TextureParameter texture = new TextureParameter(null);
        public ClampedFloatParameter contribution = new ClampedFloatParameter(0f, 0f, 1f);

        public bool IsActive() => contribution.value > 0f && ValidateLUT();

        public bool ValidateLUT()
        {
            var asset = UltraRenderPipeline.asset;
            if (asset == null || texture.value == null)
                return false;
            int size = asset.colorGradingLutSize;
            if (texture.value.height != size || texture.value.width != size * size)
                return false;
            if (texture.value is Texture2D image)
                return !GraphicsFormatUtility.IsSRGBFormat(image.graphicsFormat);
            if (texture.value is RenderTexture target)
                return target.dimension == TextureDimension.Tex2D && !target.sRGB;
            return false;
        }
    }
}
