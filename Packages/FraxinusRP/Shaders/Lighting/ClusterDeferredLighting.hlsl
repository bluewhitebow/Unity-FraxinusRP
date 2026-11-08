#ifndef FRAXINUS_CLUSTER_DEFERRED_LIGHTING_INCLUDED
#define FRAXINUS_CLUSTER_DEFERRED_LIGHTING_INCLUDED

// Shared BRDF, light-list traversal and AO behavior for compute and raster deferred.
// Entry points provide their own GBuffer loads and output composition.
#ifndef _GPU_LIGHTS_CLUSTER
#define _GPU_LIGHTS_CLUSTER
#endif
#include "Packages/com.bluewhitebow.fraxinus-rp/Runtime/UltraRP/LightLoop/Cluster/GPULights.cs.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/Runtime/UltraRP/LightLoop/Cluster/ShadingModels.cs.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Shadows.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/ContactShadows.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/PreIntegratedFGD.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/GPUCulledLights.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/UnityGBuffer.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Lighting/AreaLight/AreaLightLighting.hlsl"

// Projects may supply a main-light filter without introducing
// project asset dependencies into the render pipeline package.
#ifndef FRAXINUS_CLUSTER_MAIN_LIGHT_SHADOW
#define FRAXINUS_CLUSTER_MAIN_LIGHT_SHADOW(shadowCoord, receiver, normalWS, shadowMask, occlusionProbeChannels) \
    MainLightShadow(shadowCoord, receiver.positionWS, shadowMask, occlusionProbeChannels)
#endif


int _ClusterMainLightIndex;
int _ClusterDirectionalLightCount;
int _FraxinusAmbientOcclusionEnabled;
float _FraxinusAmbientOcclusionDirectStrength;

// Geometry and material data shared by every light affecting this pixel.
struct ShadingData
{
    float3 normalWS;
    float3 viewDirectionWS;
    float occlusion;
    uint materialFlags;
#ifdef _LIGHT_LAYERS
    uint meshRenderingLayers;
#endif

#if defined(CLUSTER_SIMPLE_LIT)
    float3 albedo;
    float3 specular;
    float specularExponent;
#else
    // The same decode supports Stencil-compatible direct light and area lights.
    BRDFData brdfData;
#if !defined(FRAXINUS_CLUSTER_STENCIL_LIGHTING)
    float3 nativeDiffuseColor;
    float3 nativeFresnel0;
    float nativePerceptualRoughness;
    float nativeRoughness;
    float nativeNdotV;
    float nativeClampedNdotV;
    float3 nativeSpecularCompensation;
#endif
#endif
};

ShadingData DecodeShadingDataFromGBuffer(PositionInputs posInput,
    float4 gbuffer0, float4 gbuffer1, float4 gbuffer2, float renderingLayers)
{
    ShadingData data = (ShadingData)0;

    data.normalWS = normalize(UnpackNormal(gbuffer2.xyz));
    data.viewDirectionWS = GetWorldSpaceNormalizeViewDir(posInput.positionWS);
    data.occlusion = gbuffer1.a;
    data.materialFlags = UnpackMaterialFlags(gbuffer0.a);
#ifdef _LIGHT_LAYERS
    data.meshRenderingLayers = DecodeMeshRenderingLayer(renderingLayers);
#endif

#if defined(CLUSTER_SIMPLE_LIT)
    data.albedo = gbuffer0.rgb;
    data.specular = gbuffer1.rgb;
    data.specularExponent = exp2(10 * gbuffer2.a + 1);
#else
    data.brdfData = BRDFDataFromGbuffer(gbuffer0, gbuffer1, gbuffer2);
#if !defined(FRAXINUS_CLUSTER_STENCIL_LIGHTING)
    // Preserve the package's Disney metallic model. Specular-workflow GBuffer1
    // stores RGB Fresnel reflectance, so its red channel is not metallic.
    if ((data.materialFlags & kMaterialFlagSpecularSetup) != 0)
    {
        data.nativeDiffuseColor = data.brdfData.diffuse;
        data.nativeFresnel0 = data.brdfData.specular;
    }
    else
    {
        float metallic = MetallicFromReflectivity(gbuffer1.r);
        data.nativeDiffuseColor = ComputeDiffuseColor(gbuffer0.rgb, metallic);
        data.nativeFresnel0 = ComputeFresnel0(gbuffer0.rgb, metallic, DEFAULT_SPECULAR_VALUE);
    }
    data.nativePerceptualRoughness = PerceptualSmoothnessToPerceptualRoughness(gbuffer2.a);
    data.nativeRoughness = PerceptualRoughnessToRoughness(data.nativePerceptualRoughness);
    data.nativeNdotV = dot(data.normalWS, data.viewDirectionWS);
    data.nativeClampedNdotV = ClampNdotV(data.nativeNdotV);

    if ((data.materialFlags & kMaterialFlagSpecularHighlightsOff) == 0)
    {
        float3 specularFGD;
        float diffuseFGD, reflectivity;
        GetPreIntegratedFGDGGXAndDisneyDiffuse(data.nativeClampedNdotV,
            data.nativePerceptualRoughness, data.nativeFresnel0,
            specularFGD, diffuseFGD, reflectivity);
        data.nativeSpecularCompensation = 1.0 + data.nativeFresnel0 * (rcp(reflectivity) - 1.0);
    }
#endif
#endif
    return data;
}

bool ShouldEvaluateLight(ShadingData data, int lightFlags, uint lightLayerMask)
{
#if defined(FRAXINUS_CLUSTER_STENCIL_LIGHTING)
    // Only lightmapped subtractive writers set the material bit. Their mixed
    // light is already present in the GBuffer's baked lighting, as in Stencil.
    if ((lightFlags & data.materialFlags) == kMaterialFlagSubtractiveMixedLighting)
        return false;
#endif
#ifdef _LIGHT_LAYERS
    if (!IsMatchingLightLayer(lightLayerMask, data.meshRenderingLayers))
        return false;
#endif
    return true;
}

float3 EvaluateDirectLight(ShadingData data, Light light, float lightMinRoughness)
{
#if defined(CLUSTER_SIMPLE_LIT)
    half3 attenuatedColor = light.color * (light.distanceAttenuation * light.shadowAttenuation);
    return LightingLambert(attenuatedColor, light.direction, data.normalWS) * data.albedo
        + LightingSpecular(attenuatedColor, light.direction, data.normalWS,
            data.viewDirectionWS, half4(data.specular, 1), data.specularExponent);
#elif defined(FRAXINUS_CLUSTER_STENCIL_LIGHTING)
    bool specularHighlightsOff = (data.materialFlags & kMaterialFlagSpecularHighlightsOff) != 0;
    return LightingPhysicallyBased(data.brdfData, light, data.normalWS,
        data.viewDirectionWS, specularHighlightsOff);
#else
    float NdotL = dot(data.normalWS, light.direction);
    float LdotV, NdotH, LdotH, invLenLV;
    GetBSDFAngle(data.viewDirectionWS, light.direction, NdotL, data.nativeNdotV,
        LdotV, NdotH, LdotH, invLenLV);
    float diffuseTerm = DisneyDiffuse(data.nativeClampedNdotV, abs(NdotL),
        LdotV, data.nativePerceptualRoughness);
    float3 lighting = data.nativeDiffuseColor * diffuseTerm;

    if ((data.materialFlags & kMaterialFlagSpecularHighlightsOff) == 0)
    {
        float roughness = max(data.nativeRoughness, lightMinRoughness);
        float3 fresnel = F_Schlick(data.nativeFresnel0, LdotH);
        float visibility = DV_SmithJointGGX(NdotH, abs(NdotL), data.nativeClampedNdotV, roughness);
        lighting += fresnel * visibility * data.nativeSpecularCompensation;
    }
    return lighting * light.color * (light.distanceAttenuation * light.shadowAttenuation * saturate(NdotL));
#endif
}

float GetDirectAmbientOcclusion(uint2 positionSS)
{
    if (_FraxinusAmbientOcclusionEnabled == 0)
        return 1.0;
    float occlusion = saturate(LOAD_TEXTURE2D_X(_ScreenSpaceOcclusionTexture, positionSS).r);
    return lerp(1.0, occlusion, saturate(_FraxinusAmbientOcclusionDirectStrength));
}

float3 EvaluateClusterLighting(PositionInputs receiver, ShadingData data, uint shadingModel)
{
    float3 lighting = 0;
    bool receiveShadows = (data.materialFlags & kMaterialFlagReceiveShadowsOff) == 0;

#if defined(FRAXINUS_CLUSTER_RASTER_LIGHTING)
    // Fragment derivatives (Umbra and the main cookie's implicit LOD) cannot
    // live inside a runtime light loop: FXC otherwise tries to fully unroll it.
    // The main light is unique, so evaluate its filter once before traversal.
    float mainLightShadow = 1.0;
    float3 mainLightCookie = 1.0;
    if (_ClusterMainLightIndex >= 0 && _ClusterMainLightIndex < _ClusterDirectionalLightCount)
    {
#ifdef _LIGHT_COOKIES
        mainLightCookie = SampleMainLightCookie(receiver.positionWS);
#endif
        if (receiveShadows)
        {
            float4 shadowCoord = TransformWorldToShadowCoord(receiver.positionWS);
            mainLightShadow = FRAXINUS_CLUSTER_MAIN_LIGHT_SHADOW(shadowCoord,
                receiver, data.normalWS, half4(1, 1, 1, 1), _MainLightOcclusionProbes);
            mainLightShadow = min(mainLightShadow, LoadContactShadow(receiver.positionSS));
        }
    }
    [loop]
#endif
    for (int lightIndex = 0; lightIndex < _ClusterDirectionalLightCount; ++lightIndex)
    {
        DirectionalLightData directional = g_DirectionalLightDatas[lightIndex];
        if (!ShouldEvaluateLight(data, directional.lightFlags, directional.lightLayerMask))
            continue;

        Light light = (Light)0;
        light.color = directional.lightColor;
        light.direction = directional.lightDirection;
        light.distanceAttenuation = 1.0;
        light.shadowAttenuation = 1.0;
#ifdef _LIGHT_COOKIES
        if (lightIndex == _ClusterMainLightIndex)
#if defined(FRAXINUS_CLUSTER_RASTER_LIGHTING)
            light.color *= mainLightCookie;
#else
            light.color *= SampleMainLightCookie(receiver.positionWS);
#endif
        else
        {
            // Visible directional lights come first; cookie indices omit main.
            int cookieIndex = lightIndex;
            if (_ClusterMainLightIndex >= 0 && cookieIndex > _ClusterMainLightIndex)
                --cookieIndex;
            if (cookieIndex < MAX_VISIBLE_LIGHTS)
                light.color *= SampleAdditionalLightCookie(cookieIndex, receiver.positionWS);
        }
#endif
        if (receiveShadows && lightIndex == _ClusterMainLightIndex)
        {
#if defined(FRAXINUS_CLUSTER_RASTER_LIGHTING)
            light.shadowAttenuation = mainLightShadow;
#else
            float4 shadowCoord = TransformWorldToShadowCoord(receiver.positionWS);
            light.shadowAttenuation = FRAXINUS_CLUSTER_MAIN_LIGHT_SHADOW(shadowCoord,
                receiver, data.normalWS, half4(1, 1, 1, 1), _MainLightOcclusionProbes);
            light.shadowAttenuation = min(light.shadowAttenuation, LoadContactShadow(receiver.positionSS));
#endif
        }
        lighting += EvaluateDirectLight(data, light, directional.minRoughness);
    }

    uint lightStart, lightCount;
    GetCountAndStart(receiver, LIGHTCATEGORY_PUNCTUAL, lightStart, lightCount);
    for (uint offset = 0; offset < lightCount; ++offset)
    {
        uint lightIndex = FetchIndex(lightStart, offset);
        if (lightIndex == (uint)-1)
            break;
        GPULightData punctual = FetchLight(lightIndex);
        if (!ShouldEvaluateLight(data, punctual.lightFlags, punctual.lightLayerMask))
            continue;

        float3 lightVector = punctual.lightPosWS - receiver.positionWS;
#if defined(FRAXINUS_CLUSTER_STENCIL_LIGHTING)
        float distanceSqr = max(dot(lightVector, lightVector), HALF_MIN);
#else
        float distanceSqr = max(dot(lightVector, lightVector), FLT_MIN);
#endif
        Light light = (Light)0;
        light.color = punctual.lightColor;
        light.direction = lightVector * rsqrt(distanceSqr);
        light.distanceAttenuation = DistanceAttenuation(distanceSqr, punctual.lightAttenuation.xy)
            * AngleAttenuation(punctual.lightDirection, light.direction, punctual.lightAttenuation.zw);
        light.shadowAttenuation = receiveShadows && punctual.shadowType != 0
            ? AdditionalLightShadow(punctual.shadowLightIndex, receiver.positionWS,
                light.direction, half4(1, 1, 1, 1), punctual.lightOcclusionProbInfo)
            : 1.0;
#ifdef _LIGHT_COOKIES
        if (punctual.cookieLightIndex >= 0)
            light.color *= SampleAdditionalLightCookie(punctual.cookieLightIndex, receiver.positionWS);
#endif
#if !defined(FRAXINUS_CLUSTER_STENCIL_LIGHTING)
        light.distanceAttenuation *= punctual.baseContribution;
#endif
        lighting += EvaluateDirectLight(data, light, punctual.minRoughness);
    }

    float directOcclusion = GetDirectAmbientOcclusion(receiver.positionSS);
    lighting *= directOcclusion;
#if !defined(CLUSTER_SIMPLE_LIT) && defined(FRAXINUS_AREA_LIGHTS_SUPPORTED)
    if (shadingModel == SHADINGMODELS_LIT)
    {
        InputData areaInput = (InputData)0;
        areaInput.positionWS = receiver.positionWS;
        areaInput.normalWS = data.normalWS;
        areaInput.viewDirectionWS = data.viewDirectionWS;
        uint meshLayers = 0xffffffffu;
#ifdef _LIGHT_LAYERS
        meshLayers = data.meshRenderingLayers;
#endif
        bool specularHighlightsOff = (data.materialFlags & kMaterialFlagSpecularHighlightsOff) != 0;
        // LTC includes its own FGD; keep it outside the native punctual compensation.
        lighting += FraxinusEvaluateAreaLights(areaInput, data.brdfData,
            (BRDFData)0, 0.0, directOcclusion, meshLayers, specularHighlightsOff);
    }
#endif
    return lighting;
}

float GetLegacyIndirectOcclusion(ShadingData data, uint2 positionSS)
{
    float indirectOcclusion = 1.0;
#if defined(FRAXINUS_CLUSTER_STENCIL_LIGHTING)
    if (_FraxinusAmbientOcclusionEnabled != 0 &&
        (data.materialFlags & kMaterialFlagScreenSpaceOcclusionApplied) == 0 &&
        _ClusterDirectionalLightCount > 0)
    {
        // Stencil's first directional draw alone corrects the existing GBuffer3.
        // Its mixed-light and layer rejection occur before that correction.
        // The current Stencil renderer does not dispatch its SSAOOnly pass.
        DirectionalLightData firstLight = g_DirectionalLightDatas[0];
        if (ShouldEvaluateLight(data, firstLight.lightFlags, firstLight.lightLayerMask))
        {
            float screenOcclusion = saturate(LOAD_TEXTURE2D_X(_ScreenSpaceOcclusionTexture, positionSS).r);
            indirectOcclusion = screenOcclusion < data.occlusion ? screenOcclusion * rcp(data.occlusion) : 1.0;
        }
    }
#endif
    return indirectOcclusion;
}

#endif
