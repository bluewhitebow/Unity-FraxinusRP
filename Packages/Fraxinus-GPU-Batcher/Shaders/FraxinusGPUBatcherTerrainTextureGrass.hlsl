#ifndef GPUBatcher_TERRAIN_GRASS_INCLUDED
#define GPUBatcher_TERRAIN_GRASS_INCLUDED

#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Shadows.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/DebugMipmapStreamingMacros.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _MainTex_ST;
    half4 _BaseColor;
    half _Cutoff;
    half _Shininess;
    half _GrassInvHeight;
    half _WindSpeed;
    half _WaveSize;
    half _WindStrength;
    half4 _WavingTint;
    half4 _HealthyColor;
    half4 _DryColor;
    half _NoiseSpread;
CBUFFER_END

TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);
float4 _MainTex_TexelSize;
UNITY_TEXTURE_STREAMING_DEBUG_VARS_FOR_TEX(_MainTex);

// set by the shadow caster pass
float3 _LightDirection;
float3 _LightPosition;

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    half3  vertexSH   : TEXCOORD1;
    float3 positionWS : TEXCOORD2;
    half3  normalWS   : TEXCOORD3;
    half3  viewDirWS  : TEXCOORD4;
    half4  fogFactorAndVertexLight : TEXCOORD5; // x: fog, yzw: vertex light
#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord : TEXCOORD6;
#endif
    half3  waveColor  : TEXCOORD7;
#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion : TEXCOORD8;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VaryingsDepth
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

half ClipGrassAlpha(float2 uv)
{
    half a = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).a * _BaseColor.a;
    clip(a - _Cutoff);
    return a;
}

// 4 fast sine-cosine pairs via taylor series
void FastSinCos(float4 val, out float4 s, out float4 c)
{
    val = val * 6.408849 - 3.1415927;
    float4 r5 = val * val;
    float4 r6 = r5 * r5;
    float4 r7 = r6 * r5;
    float4 r8 = r6 * r5;
    float4 r1 = r5 * val;
    float4 r2 = r1 * r5;
    float4 r3 = r2 * r5;
    float4 sin7 = { 1, -0.16161616, 0.0083333, -0.00019841 };
    float4 cos8 = { -0.5, 0.041666666, -0.0013888889, 0.000024801587 };
    s = val + r1 * sin7.y + r2 * sin7.z + r3 * sin7.w;
    c = 1 + r5 * cos8.x + r6 * cos8.y + r7 * cos8.z + r8 * cos8.w;
}

// 0 at base (os y=0), 1 at tip
half GrassWaveGradient(float3 positionOS)
{
    return saturate(positionOS.y * _GrassInvHeight);
}

// shadergraph gradient noise (perlin family), for per-blade healthy/dry
float2 GrassGradientDir(float2 p)
{
    p = fmod(p, 289.0);
    float x = fmod((34.0 * p.x + 1.0) * p.x, 289.0) + p.y;
    x = fmod((34.0 * x + 1.0) * x, 289.0);
    x = frac(x / 41.0) * 2.0 - 1.0;
    return normalize(float2(x - floor(x + 0.5), abs(x) - 0.5));
}

half GrassGradientNoise(float2 p)
{
    float2 ip = floor(p);
    float2 fp = frac(p);
    float d00 = dot(GrassGradientDir(ip), fp);
    float d01 = dot(GrassGradientDir(ip + float2(0, 1)), fp - float2(0, 1));
    float d10 = dot(GrassGradientDir(ip + float2(1, 0)), fp - float2(1, 0));
    float d11 = dot(GrassGradientDir(ip + float2(1, 1)), fp - float2(1, 1));
    fp = fp * fp * fp * (fp * (fp * 6.0 - 15.0) + 10.0);
    return lerp(lerp(d00, d10, fp.x), lerp(d01, d11, fp.x), fp.y) + 0.5;
}

// phase keyed off world xz so blades desync
void ApplyGrassWave(inout float3 positionWS, half gradient, out half3 waveColor)
{
    half waveAmount = gradient * _WindStrength;

    half4 waveXSize = half4(0.012, 0.02, 0.06, 0.024) * _WaveSize;
    half4 waveZSize = half4(0.006, 0.02, 0.02, 0.05) * _WaveSize;
    half4 waveSpeed = half4(1.2, 2.0, 1.6, 4.8);
    half4 waveXmove = half4(0.024, 0.04, -0.12, 0.096);
    half4 waveZmove = half4(0.006, 0.02, -0.02, 0.1);

    float4 waves = positionWS.x * waveXSize + positionWS.z * waveZSize;
    waves += (_Time.y * _WindSpeed) * waveSpeed;
    waves = frac(waves);

    float4 s, c;
    FastSinCos(waves, s, c);
    s = s * s;
    s = s * s;

    // wave-driven tint shimmer
    half lighting = dot(s, normalize(half4(1.0, 1.0, 0.4, 0.2))) * 0.7;
    waveColor = lerp(half3(0.5, 0.5, 0.5), _WavingTint.rgb, lighting);

    s = s * waveAmount;

    half3 waveMove;
    waveMove.x = dot(s, waveXmove);
    waveMove.y = 0.0;
    waveMove.z = dot(s, waveZmove);

    positionWS.xz -= waveMove.xz * _WindStrength;
}

// billboard expands the unit quad toward the camera around the world y axis
float3 GrassObjectToWorld(float3 positionOS)
{
#if defined(_BILLBOARD)
    float4x4 objectToWorld = UNITY_MATRIX_M;
    float3 originWS = objectToWorld._m03_m13_m23;
    float scaleX = length(objectToWorld._m00_m10_m20);
    float scaleY = length(objectToWorld._m01_m11_m21);
    float3 camRight = normalize(UNITY_MATRIX_V._m00_m01_m02);
    return originWS + camRight * (positionOS.x * scaleX) + float3(0.0, positionOS.y * scaleY, 0.0);
#else
    return TransformObjectToWorld(positionOS);
#endif
}

half3 GrassNormalWS(float3 normalOS, float3 positionWS)
{
    // grass is lit from the sky; a face/camera normal makes lighting flip with the view
    return half3(0.0, 1.0, 0.0);
}

// forward

void GrassInitializeInputData(Varyings IN, out InputData inputData)
{
    inputData = (InputData)0;

    inputData.positionWS = IN.positionWS;
    inputData.positionCS = IN.positionCS;
    inputData.normalWS = NormalizeNormalPerPixel(IN.normalWS);
    inputData.viewDirectionWS = SafeNormalize(IN.viewDirWS);

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    inputData.shadowCoord = IN.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

    inputData.fogCoord = IN.fogFactorAndVertexLight.x;
    inputData.vertexLighting = IN.fogFactorAndVertexLight.yzw;

    inputData.shadowMask = half4(1, 1, 1, 1);
#if defined(_SCREEN_SPACE_IRRADIANCE)
    inputData.bakedGI = SAMPLE_GI(_ScreenSpaceIrradiance, inputData.positionCS.xy);
#elif defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
    inputData.bakedGI = SAMPLE_GI(IN.vertexSH,
        GetAbsolutePositionWS(inputData.positionWS),
        inputData.normalWS,
        inputData.viewDirectionWS,
        inputData.positionCS.xy,
        IN.probeOcclusion,
        inputData.shadowMask);
#else
    inputData.bakedGI = SAMPLE_GI(0, IN.vertexSH, inputData.normalWS);
#endif

    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);

    #if defined(DEBUG_DISPLAY)
    inputData.vertexSH = IN.vertexSH;
    #if defined(USE_APV_PROBE_OCCLUSION)
    inputData.probeOcclusion = IN.probeOcclusion;
    #endif
    #endif
}

Varyings GrassForwardVert(Attributes IN)
{
    Varyings OUT = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(IN);
    UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

    float3 positionWS = GrassObjectToWorld(IN.positionOS.xyz);
    half3 waveColor;
    ApplyGrassWave(positionWS, GrassWaveGradient(IN.positionOS.xyz), waveColor);
    half3 normalWS = GrassNormalWS(IN.normalOS, positionWS);

    OUT.positionCS = TransformWorldToHClip(positionWS);
    OUT.positionWS = positionWS;
    OUT.normalWS = normalWS;
    OUT.viewDirWS = SafeNormalize(GetCameraPositionWS() - positionWS);
    OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);

    OUTPUT_SH4(positionWS, normalWS, OUT.viewDirWS, OUT.vertexSH, OUT.probeOcclusion);

    half3 vertexLight = VertexLighting(positionWS, normalWS);
    half fogFactor = ComputeFogFactor(OUT.positionCS.z);
    OUT.fogFactorAndVertexLight = half4(fogFactor, vertexLight);

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    OUT.shadowCoord = TransformWorldToShadowCoord(positionWS);
#endif

    // per-blade healthy/dry from the instance world origin
    float2 instanceXZ = float2(UNITY_MATRIX_M._m03, UNITY_MATRIX_M._m23);
    half noiseT = saturate(GrassGradientNoise(instanceXZ * _NoiseSpread + 23.71));
    half3 healthyDry = lerp(_DryColor.rgb, _HealthyColor.rgb, noiseT);
    OUT.waveColor = waveColor * healthyDry;
    return OUT;
}

half4 GrassForwardFrag(Varyings IN) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(IN);

    half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * _BaseColor;
    clip(tex.a - _Cutoff);

    // bottom-to-top brightness gradient, base = tip * 0.625 (measured from unity grass mesh vertex colors)
    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo = tex.rgb * 2.0 * IN.waveColor * lerp(0.625, 1.0, IN.uv.y);
    surfaceData.alpha = 1.0;
    surfaceData.specular = 0.0;
    surfaceData.occlusion = 1.0;
    surfaceData.normalTS = half3(0.0, 0.0, 0.0);

    InputData inputData;
    GrassInitializeInputData(IN, inputData);
    SETUP_DEBUG_TEXTURE_DATA_FOR_TEX(inputData, IN.uv, _MainTex);

    half4 color = UniversalFragmentBlinnPhong(inputData, surfaceData);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    return half4(color.rgb, 1.0);
}

// depth only

VaryingsDepth GrassDepthVert(Attributes IN)
{
    VaryingsDepth OUT = (VaryingsDepth)0;
    UNITY_SETUP_INSTANCE_ID(IN);
    UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

    float3 positionWS = GrassObjectToWorld(IN.positionOS.xyz);
    half3 waveColor;
    ApplyGrassWave(positionWS, GrassWaveGradient(IN.positionOS.xyz), waveColor);

    OUT.positionCS = TransformWorldToHClip(positionWS);
    OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
    return OUT;
}

half4 GrassDepthFrag(VaryingsDepth IN) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(IN);
    ClipGrassAlpha(IN.uv);
    return 0;
}

// shadow caster

VaryingsDepth GrassShadowVert(Attributes IN)
{
    VaryingsDepth OUT = (VaryingsDepth)0;
    UNITY_SETUP_INSTANCE_ID(IN);
    UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

    float3 positionWS = GrassObjectToWorld(IN.positionOS.xyz);
    half3 waveColor;
    ApplyGrassWave(positionWS, GrassWaveGradient(IN.positionOS.xyz), waveColor);
    float3 normalWS = GrassNormalWS(IN.normalOS, positionWS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
#if UNITY_REVERSED_Z
    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif
    OUT.positionCS = positionCS;
    OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
    return OUT;
}

// depth normals

struct VaryingsDepthNormals
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    half3  normalWS   : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

VaryingsDepthNormals GrassDepthNormalsVert(Attributes IN)
{
    VaryingsDepthNormals OUT = (VaryingsDepthNormals)0;
    UNITY_SETUP_INSTANCE_ID(IN);
    UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

    float3 positionWS = GrassObjectToWorld(IN.positionOS.xyz);
    half3 waveColor;
    ApplyGrassWave(positionWS, GrassWaveGradient(IN.positionOS.xyz), waveColor);

    OUT.positionCS = TransformWorldToHClip(positionWS);
    OUT.normalWS = GrassNormalWS(IN.normalOS, positionWS);
    OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
    return OUT;
}

half4 GrassDepthNormalsFrag(VaryingsDepthNormals IN) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(IN);
    ClipGrassAlpha(IN.uv);

    half3 normalWS = NormalizeNormalPerPixel(IN.normalWS);
#if defined(_GBUFFER_NORMALS_OCT)
    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
    float3 packedNormalWS = PackFloat2To888(saturate(octNormalWS * 0.5 + 0.5));
    return half4(packedNormalWS, 0.0);
#else
    return half4(normalWS, 0.0);
#endif
}

#endif
