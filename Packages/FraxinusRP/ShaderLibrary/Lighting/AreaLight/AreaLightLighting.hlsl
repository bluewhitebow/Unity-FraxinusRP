#ifndef FRAXINUS_AREA_LIGHT_LIGHTING_INCLUDED
#define FRAXINUS_AREA_LIGHT_LIGHTING_INCLUDED

// Texture arrays require shader model 3.5. Legacy materials keep their old lighting.
#if SHADER_TARGET >= 35 && !defined(SHADER_API_GLES)
// Hybrid Lit's FGD/IBL readiness is shared with this buffer even when the
// pipeline omits rectangle-light evaluation. Unused arrays compile away.
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Lighting/AreaLight/ShaderVariablesAreaLights.hlsl"
#if defined(_FRAXINUS_AREA_LIGHTS)
#define FRAXINUS_AREA_LIGHTS_SUPPORTED 1
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Lighting/AreaLight/AreaLightEvaluation.hlsl"
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/PreIntegratedFGD.hlsl"

half3 FraxinusEvaluateAreaLights(InputData inputData, BRDFData brdfData, BRDFData coatBRDF,
    half coatMask, half directOcclusion, uint meshLayers, bool specularHighlightsOff)
{
    if (_FraxinusAreaLightCount <= 0 || _FraxinusLightingLutsReady == 0)
        return 0.0;

    float NdotV = dot(inputData.normalWS, inputData.viewDirectionWS);
    // Half-precision normalized inputs can dot slightly above one; LTC uses sqrt(1 - NdotV).
    float clampedNdotV = ClampNdotV(saturate(NdotV));
    FraxinusAreaPreLightData preLight = (FraxinusAreaPreLightData)0;
    // GetLocalFrame handles both parallel and antiparallel view/normal singularities.
    preLight.basis = abs(NdotV) > 0.9999
        ? GetLocalFrame(inputData.normalWS)
        : GetOrthoBasisViewNormal(inputData.viewDirectionWS, inputData.normalWS, NdotV);
    float reflectivity;
    GetPreIntegratedFGDGGXAndDisneyDiffuse(clampedNdotV, brdfData.perceptualRoughness,
        brdfData.specular, preLight.specularFGD, preLight.diffuseFGD, reflectivity);
    preLight.specularTransform = SampleLtcMatrix(brdfData.perceptualRoughness,
        clampedNdotV, LTCLIGHTINGMODEL_GGX);
#if defined(FRAXINUS_HYBRID_LIT)
    preLight.diffuseTransform = SampleLtcMatrix(brdfData.perceptualRoughness,
        clampedNdotV, LTCLIGHTINGMODEL_DISNEY_DIFFUSE);
#else
    preLight.diffuseTransform = k_identity3x3;
    preLight.diffuseFGD = 1.0;
#endif
#if defined(_CLEARCOAT) || defined(_CLEARCOATMAP)
    preLight.coatTransform = SampleLtcMatrix(coatBRDF.perceptualRoughness,
        clampedNdotV, LTCLIGHTINGMODEL_GGX);
    preLight.coatWeight = coatMask * (kDielectricSpec.x + kDielectricSpec.a * Pow4(1.0 - saturate(NdotV)));
#endif

    float3 color = 0.0;
    uint lightCount = (uint)min(max(_FraxinusAreaLightCount, 0), FRAXINUS_MAX_AREA_LIGHTS);
    [loop] for (uint index = 0; index < lightCount; ++index)
    {
        float4 layers = _FraxinusAreaLightLayers[index];
        uint lightLayers = (uint)layers.x | ((uint)layers.y << 16);
#if defined(_LIGHT_LAYERS)
        if (!IsMatchingLightLayer(lightLayers, meshLayers))
            continue;
#endif
        FraxinusAreaLightData light;
        light.positionWS = _FraxinusAreaLightPositionRange[index].xyz;
        light.inverseRangeSquared = _FraxinusAreaLightPositionRange[index].w;
        light.right = _FraxinusAreaLightRightSize[index].xyz;
        light.halfWidth = _FraxinusAreaLightRightSize[index].w;
        light.up = _FraxinusAreaLightUpSize[index].xyz;
        light.halfHeight = _FraxinusAreaLightUpSize[index].w;
        light.color = _FraxinusAreaLightColor[index].rgb;
        light.diffuseDimmer = layers.z;
        light.specularDimmer = layers.w;
        FraxinusAreaLighting lighting = EvaluateFraxinusRectangle(inputData.positionWS,
            preLight, light, !specularHighlightsOff);
        color += (lighting.diffuse * brdfData.diffuse + lighting.specular) * directOcclusion;
    }
    return half3(min(color, HALF_MAX));
}
#endif

#endif

#endif
