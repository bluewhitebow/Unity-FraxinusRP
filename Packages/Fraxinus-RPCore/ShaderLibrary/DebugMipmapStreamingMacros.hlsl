#ifndef UNITY_DEBUG_MIPMAP_STREAMING_MACROS_INCLUDED
#define UNITY_DEBUG_MIPMAP_STREAMING_MACROS_INCLUDED

#if UNITY_VERSION >= 600000

// Beware that this macro is used in constant buffers, so this should not change in size based on conditionals
#define UNITY_TEXTURE_STREAMING_DEBUG_VARS        \
    float4 unity_MipmapStreaming_DebugTex_ST;        \
    float4 unity_MipmapStreaming_DebugTex_TexelSize; \
    float4 unity_MipmapStreaming_DebugTex_MipInfo;   \
    float4 unity_MipmapStreaming_DebugTex_StreamInfo;

// Beware that this macro is used in constant buffers, so this should not change in size based on conditionals
#define UNITY_TEXTURE_STREAMING_DEBUG_VARS_FOR_TEX(tex) \
    float4 tex##_MipInfo;                                  \
    float4 tex##_StreamInfo;

#else

// Unity 2022 has no mipmap streaming debug mode, so its engine does not know these names. Both macros expand inside
// UnityPerMaterial, and the SRP Batcher requires every UnityPerMaterial member to have a matching material property
// in the shader property section. Declaring them here would therefore make every shader that includes this file SRP
// Batcher incompatible. Debugging3D.hlsl feeds neutral values to the debug data setup instead.
#define UNITY_TEXTURE_STREAMING_DEBUG_VARS
#define UNITY_TEXTURE_STREAMING_DEBUG_VARS_FOR_TEX(tex)

#endif

#endif // UNITY_DEBUG_MIPMAP_STREAMING_MACROS_INCLUDED
