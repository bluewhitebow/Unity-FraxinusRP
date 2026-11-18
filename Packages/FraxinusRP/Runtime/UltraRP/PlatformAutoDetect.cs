using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    internal static class PlatformAutoDetect
    {
        /// <summary>
        /// Detect and cache runtime platform information. This function should only be called once when creating the UltraRP renderer.
        /// </summary>
        internal static void Initialize()
        {
            bool isRunningMobile = false;
#if ENABLE_VR && ENABLE_VR_MODULE
#if PLATFORM_WINRT || PLATFORM_ANDROID
            isRunningMobile = IsRunningXRMobile();
#endif
#endif

            isXRMobile = isRunningMobile;
            isShaderAPIMobileDefined = GraphicsSettings.HasShaderDefine(BuiltinShaderDefine.SHADER_API_MOBILE);
            isSwitch = Application.platform == RuntimePlatform.Switch;
        }

#if ENABLE_VR && ENABLE_VR_MODULE
#if PLATFORM_WINRT || PLATFORM_ANDROID
        // XR mobile platforms are not treated as dedicated mobile platforms in Core. Handle them specially here. (Quest and HL).
        private static List<UnityEngine.XR.XRDisplaySubsystem> displaySubsystemList = new List<UnityEngine.XR.XRDisplaySubsystem>();
        private static bool IsRunningXRMobile()
        {
            var platform = Application.platform;
            if (platform == RuntimePlatform.WSAPlayerX86 || platform == RuntimePlatform.WSAPlayerARM || platform == RuntimePlatform.WSAPlayerX64 || platform == RuntimePlatform.Android)
            {
                UnityEngine.XR.XRDisplaySubsystem display = null;
                SubsystemManager.GetSubsystems(displaySubsystemList);

                if (displaySubsystemList.Count > 0)
                    display = displaySubsystemList[0];

                if (display != null)
                    return true;
            }
            return false;
        }
#endif
#endif

        /// <summary>
        /// If true, the runtime platform is an XR mobile platform.
        /// </summary>
        internal static bool isXRMobile { get; private set; } = false;

        /// <summary>
        /// If true, then SHADER_API_MOBILE has been defined in UltraRP shaders.
        /// </summary>
        internal static bool isShaderAPIMobileDefined { get; private set; } = false;

        /// <summary>
        /// If true, then the runtime platform is set to Switch.
        /// </summary>
        internal static bool isSwitch { get; private set; } = false;

        /// <summary>
        /// Gives the SH evaluation mode when set to automatically detect.
        /// </summary>
        /// <param name="mode">The current SH evaluation mode.</param>
        /// <returns>Returns the SH evaluation mode to use.</returns>
        internal static ShEvalMode ShAutoDetect(ShEvalMode mode)
        {
            if (mode == ShEvalMode.Auto)
            {
                if (isXRMobile || isShaderAPIMobileDefined || isSwitch)
                    return ShEvalMode.PerVertex;
                else
                    return ShEvalMode.PerPixel;
            }

            return mode;
        }

        internal static bool isRunningOnPowerVRGPU = SystemInfo.graphicsDeviceName.Contains("PowerVR");
    }
}
