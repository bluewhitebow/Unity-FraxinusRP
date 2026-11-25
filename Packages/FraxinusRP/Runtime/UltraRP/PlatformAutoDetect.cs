using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    internal static class PlatformAutoDetect
    {
        internal static bool isXRMobile { get; private set; } = false;

        internal static bool isShaderAPIMobileDefined { get; private set; } = false;

        internal static void Initialize()
        {
            isShaderAPIMobileDefined = GraphicsSettings.HasShaderDefine(BuiltinShaderDefine.SHADER_API_MOBILE);
        }

        internal static ShEvalMode ShAutoDetect(ShEvalMode mode)
        {
            if (mode == ShEvalMode.Auto)
            {
                if (PlatformAutoDetect.isXRMobile)
                    return ShEvalMode.PerVertex;
                else
                    return ShEvalMode.PerPixel;
            }

            return mode;
        }
    }
}
