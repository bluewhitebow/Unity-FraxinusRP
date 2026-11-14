#ifndef FRAXINUS_CLUSTER_DEFERRED_LIGHTING_INCLUDED
#define FRAXINUS_CLUSTER_DEFERRED_LIGHTING_INCLUDED

// Shared BRDF, light-list traversal and AO behavior for compute and raster deferred.
// Entry points provide their own GBuffer loads and output composition.
#ifndef _GPU_LIGHTS_CLUSTER
#define _GPU_LIGHTS_CLUSTER
#endif
#include "Packages/com.bluewhitebow.fraxinus-rp/Runtime/UltraRP/LightLoop/Cluster/GPULights.cs.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/Shaders/Lighting/DeferredLightingCommon.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/GPUCulledLights.hlsl"

// Projects may supply a main-light filter without introducing
// project asset dependencies into the render pipeline package.
#ifndef FRAXINUS_CLUSTER_MAIN_LIGHT_SHADOW
#define FRAXINUS_CLUSTER_MAIN_LIGHT_SHADOW(shadowCoord, receiver, normalWS, shadowMask, occlusionProbeChannels) \
    MainLightShadow(shadowCoord, receiver.positionWS, shadowMask, occlusionProbeChannels)
#endif


int _ClusterMainLightIndex;
int _ClusterDirectionalLightCount;
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
    lighting += EvaluateDeferredAreaLighting(receiver, data, shadingModel, directOcclusion);
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
