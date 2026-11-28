#ifndef ULTRA_DOTS_PRAGMAS_INCLUDED
#define ULTRA_DOTS_PRAGMAS_INCLUDED

#ifndef HAVE_VFX_MODIFICATION
    #ifndef FRAXINUS_DOTS_ONLY
        #pragma multi_compile _ DOTS_INSTANCING_ON
    #else
        #pragma multi_compile DOTS_INSTANCING_ON
    #endif
    // Tuanjie's shader compiler reports 2022.3. Its C# backend selects the backported BRG ABI.
    #if UNITY_VERSION < 600000
        #pragma multi_compile _ FRAXINUS_TUANJIE_BRG FRAXINUS_TUANJIE_BRG_64K
    #endif
    // Android Vulkan uses raw DOTS buffers; the GLES uniform-buffer variant can use SM3.5.
    #if (UNITY_PLATFORM_ANDROID && !SHADER_API_VULKAN) || (UNITY_PLATFORM_WEBGL && !SHADER_API_WEBGPU) || UNITY_PLATFORM_UWP
        #pragma target 3.5 DOTS_INSTANCING_ON
    #else
        #pragma target 4.5 DOTS_INSTANCING_ON
    #endif
#endif // HAVE_VFX_MODIFICATION

#endif // ULTRA_DOTS_PRAGMAS_INCLUDED
