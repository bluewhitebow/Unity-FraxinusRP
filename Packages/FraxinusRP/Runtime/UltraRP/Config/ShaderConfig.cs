using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    [GenerateHLSL]
    public static class ShaderOptions
    {
        public const int maxVisibleLightCountLowEndMobile = 16;
        public const int maxVisibleLightCountMobile = 32;
        public const int maxVisibleLightCountDesktop = 256;
    };
}
