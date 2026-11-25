using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEditor.Rendering.Ultra
{
    public static class RendererGraphTextureUtils
    {
        public static int GetSliceCount(Texture tex)
        {
            if (tex == null)
                return 0;

            switch (tex)
            {
                case Texture2D _:
                    return 1;
                case Texture2DArray t:
                    return t.depth;
                case Texture3D t:
                    return t.depth;
                case CubemapArray t:
                    return t.cubemapCount;
                case Cubemap _:
                    return 1;
                case RenderTexture rt:
                    if (rt.dimension == TextureDimension.Tex2D || rt.dimension == TextureDimension.Cube)
                        return 1;
                    else if (rt.dimension == TextureDimension.Tex3D || rt.dimension == TextureDimension.Tex2DArray || rt.dimension == TextureDimension.CubeArray)
                        return rt.volumeDepth;
                    else
                        return 0;
                default:
                    return 0;
            }
        }
    }
}
