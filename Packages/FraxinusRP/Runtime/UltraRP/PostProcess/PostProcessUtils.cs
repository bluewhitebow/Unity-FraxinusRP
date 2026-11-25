using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public static class PostProcessUtils
    {
        static class ShaderConstants
        {
            public static readonly int _Grain_Texture = Shader.PropertyToID("_Grain_Texture");
            public static readonly int _Grain_Params = Shader.PropertyToID("_Grain_Params");
            public static readonly int _Grain_TilingParams = Shader.PropertyToID("_Grain_TilingParams");

            public static readonly int _BlueNoise_Texture = Shader.PropertyToID("_BlueNoise_Texture");
            public static readonly int _Dithering_Params = Shader.PropertyToID("_Dithering_Params");

            public static readonly int _SourceSize = Shader.PropertyToID("_SourceSize");
        }

        public static void ConfigureFilmGrain(PostProcessData data, FilmGrain settings, int cameraPixelWidth, int cameraPixelHeight, Material material)
        {
            var texture = settings.texture.value;

            if (settings.type.value != FilmGrainLookup.Custom)
                texture = data.textures.filmGrainTex[(int)settings.type.value];

#if LWRP_DEBUG_STATIC_POSTFX
            float rndOffsetX = 0f;
            float rndOffsetY = 0f;
#else
            var oldState = Random.state;
            Random.InitState(Time.frameCount);
            float rndOffsetX = Random.value;
            float rndOffsetY = Random.value;
            Random.state = oldState;
#endif

            var tilingParams = texture == null
                ? Vector4.zero
                : new Vector4(cameraPixelWidth / (float)texture.width, cameraPixelHeight / (float)texture.height, rndOffsetX, rndOffsetY);

            material.SetTexture(ShaderConstants._Grain_Texture, texture);
            material.SetVector(ShaderConstants._Grain_Params, new Vector2(settings.intensity.value * 4f, settings.response.value));
            material.SetVector(ShaderConstants._Grain_TilingParams, tilingParams);
        }

        internal static void SetSourceSize(CommandBuffer cmd, RTHandle source)
        {
            SetSourceSize(CommandBufferHelpers.GetRasterCommandBuffer(cmd), source);
        }

        internal static void SetSourceSize(RasterCommandBuffer cmd, RTHandle source)
        {
            float width = source.rt.width;
            float height = source.rt.height;
            if (source.rt.useDynamicScale)
            {
                width *= ScalableBufferManager.widthScaleFactor;
                height *= ScalableBufferManager.heightScaleFactor;
            }
            cmd.SetGlobalVector(ShaderConstants._SourceSize, new Vector4(width, height, 1.0f / width, 1.0f / height));
        }
    }
}
