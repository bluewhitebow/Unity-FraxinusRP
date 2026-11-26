#ifndef FRAXINUS_GRAPH_LIT_PRAGMAS_INCLUDED
#define FRAXINUS_GRAPH_LIT_PRAGMAS_INCLUDED

// Include directly from the Lit passes that accept indirect instance data.
#if UNITY_VERSION < 600000 && (defined(SHADER_API_D3D11) || defined(SHADER_API_VULKAN)) && defined(SHADER_API_DESKTOP)
    #pragma target 4.5 PROCEDURAL_INSTANCING_ON
#endif
#if UNITY_VERSION < 600000 && defined(SHADER_API_VULKAN) && defined(SHADER_API_DESKTOP)
    #pragma multi_compile_local _ FRAXINUS_VULKAN_GRAPH_DRAW_V1
    #pragma target 4.5 FRAXINUS_VULKAN_GRAPH_DRAW_V1
    // Select the procedural input layout for Vulkan indirect draws in the Player.
    #if defined(FRAXINUS_VULKAN_GRAPH_DRAW_V1)
        #ifndef PROCEDURAL_INSTANCING_ON
            #define PROCEDURAL_INSTANCING_ON 1
        #endif
        #ifndef UNITY_INSTANCING_PROCEDURAL_FUNC
            #define UNITY_INSTANCING_PROCEDURAL_FUNC FraxinusSetupGraphInstance
        #endif
    #endif
#endif

#endif
