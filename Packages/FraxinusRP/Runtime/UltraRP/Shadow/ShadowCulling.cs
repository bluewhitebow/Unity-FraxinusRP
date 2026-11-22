using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    internal struct UltraRPLightShadowCullingInfos
    {
        public NativeArray<ShadowSliceData> slices;
        public uint slicesValidMask;

        public readonly bool IsSliceValid(int i)
        {
            return i >= 0 && i < 32 &&
                (slicesValidMask & (1u << i)) != 0;
        }
    }

    /// <summary>
    /// Extracts per-light shadow slices. Unity 6 submits them to native shadow
    /// culling (including BRG light callbacks); Unity 2022 uses camera culling.
    /// </summary>
    internal static class ShadowCulling
    {
        public static NativeArray<UltraRPLightShadowCullingInfos> CullShadowCasters(
            ref ScriptableRenderContext context,
            ShadowData shadowData,
            ref AdditionalLightsShadowAtlasLayout shadowAtlasLayout,
            ref CullingResults cullResults)
        {
            int mainLightIndex = FindMainLightIndex(cullResults.visibleLights);
            return CullShadowCasters(
                ref context,
                shadowData,
                ref shadowAtlasLayout,
                ref cullResults,
                mainLightIndex);
        }

        public static NativeArray<UltraRPLightShadowCullingInfos> CullShadowCasters(
            ref ScriptableRenderContext context,
            ShadowData shadowData,
            ref AdditionalLightsShadowAtlasLayout shadowAtlasLayout,
            ref CullingResults cullResults,
            int mainLightIndex = -1)
        {
            NativeArray<VisibleLight> visibleLights = cullResults.visibleLights;
            int lightCount = visibleLights.IsCreated ? visibleLights.Length : 0;
            var result = new NativeArray<UltraRPLightShadowCullingInfos>(
                lightCount, Allocator.Temp);
            if (lightCount == 0 || shadowData == null)
                return result;

            for (int lightIndex = 0; lightIndex < lightCount; ++lightIndex)
            {
                ref VisibleLight visibleLight =
                    ref visibleLights.UnsafeElementAt(lightIndex);
                Light light = visibleLight.light;
                if (light == null)
                    continue;

                int sliceCount;
                switch (visibleLight.lightType)
                {
                    case LightType.Directional:
                        if (!shadowData.supportsMainLightShadows ||
                            lightIndex != mainLightIndex)
                            continue;
                        sliceCount = Mathf.Clamp(
                            shadowData.mainLightShadowCascadesCount, 1, 32);
                        break;

                    case LightType.Point:
                    case LightType.Spot:
                        if (!shadowData.supportsAdditionalLightShadows ||
                            !shadowAtlasLayout.HasSpaceForLight(lightIndex))
                            continue;
                        sliceCount = ShadowUtils.GetPunctualLightShadowSlicesCount(
                            visibleLight.lightType);
                        break;

                    default:
                        continue;
                }

                var slices = new NativeArray<ShadowSliceData>(
                    sliceCount, Allocator.Temp);
                uint validMask = 0;

                for (int sliceIndex = 0; sliceIndex < sliceCount; ++sliceIndex)
                {
                    ShadowSliceData slice = default;
                    bool valid;

                    if (visibleLight.lightType == LightType.Directional)
                    {
                        int width = Mathf.Max(1,
                            shadowData.mainLightRenderTargetWidth > 0
                                ? shadowData.mainLightRenderTargetWidth
                                : shadowData.mainLightShadowmapWidth);
                        int height = Mathf.Max(1,
                            shadowData.mainLightRenderTargetHeight > 0
                                ? shadowData.mainLightRenderTargetHeight
                                : shadowData.mainLightShadowmapHeight);
                        int resolution = Mathf.Max(1,
                            shadowData.mainLightShadowResolution);

                        valid = ShadowUtils.ExtractDirectionalLightMatrix(
                            ref cullResults,
                            shadowData,
                            lightIndex,
                            sliceIndex,
                            width,
                            height,
                            resolution,
                            light.shadowNearPlane,
                            out _,
                            out slice);
                    }
                    else if (visibleLight.lightType == LightType.Point)
                    {
                        AdditionalLightsShadowAtlasLayout.ShadowResolutionRequest request =
                            shadowAtlasLayout.GetSliceShadowResolutionRequest(
                                lightIndex, sliceIndex);
                        int resolution = Mathf.Max(
                            1, request.allocatedResolution);
                        float fovBias = AdditionalLightsShadowCasterPass
                            .GetPointLightShadowFrustumFovBiasInDegrees(
                                resolution,
                                light.shadows == LightShadows.Soft);

                        valid = ShadowUtils.ExtractPointLightMatrix(
                            ref cullResults,
                            shadowData,
                            lightIndex,
                            (CubemapFace)sliceIndex,
                            fovBias,
                            out slice.shadowTransform,
                            out slice.viewMatrix,
                            out slice.projectionMatrix,
                            out slice.splitData);
                    }
                    else
                    {
                        valid = ShadowUtils.ExtractSpotLightMatrix(
                            ref cullResults,
                            shadowData,
                            lightIndex,
                            out slice.shadowTransform,
                            out slice.viewMatrix,
                            out slice.projectionMatrix,
                            out slice.splitData);
                    }

                    slices[sliceIndex] = slice;
                    if (valid)
                        validMask |= 1u << sliceIndex;
                }

                if (visibleLight.lightType == LightType.Point)
                {
                    const int pointLightShadowSliceCount = 6;
                    const uint allPointLightFacesMask =
                        (1u << pointLightShadowSliceCount) - 1u;
                    if (sliceCount != pointLightShadowSliceCount ||
                        (validMask & allPointLightFacesMask) != allPointLightFacesMask)
                    {
                        // A point-light shadow is one cubemap transaction. Never advertise a
                        // subset of its faces to the atlas publisher.
                        validMask = 0;
                    }
                }

                result[lightIndex] = new UltraRPLightShadowCullingInfos
                {
                    slices = slices,
                    slicesValidMask = validMask,
                };
            }

#if UNITY_6000_0_OR_NEWER
            int splitCount = 0;
            for (int lightIndex = 0; lightIndex < lightCount; ++lightIndex)
                if (result[lightIndex].slices.IsCreated)
                    splitCount += result[lightIndex].slices.Length;
            var splitBuffer = new NativeArray<ShadowSplitData>(splitCount, Allocator.Temp);
            var perLightInfos = new NativeArray<LightShadowCasterCullingInfo>(lightCount, Allocator.Temp);
            int splitOffset = 0;
            for (int lightIndex = 0; lightIndex < lightCount; ++lightIndex)
            {
                var slices = result[lightIndex].slices;
                if (!slices.IsCreated) continue;
                for (int sliceIndex = 0; sliceIndex < slices.Length; ++sliceIndex)
                    splitBuffer[splitOffset + sliceIndex] = slices[sliceIndex].splitData;
                perLightInfos[lightIndex] = new LightShadowCasterCullingInfo
                {
                    splitRange = new RangeInt(splitOffset, slices.Length),
                    projectionType = visibleLights[lightIndex].lightType == LightType.Directional
                        ? BatchCullingProjectionType.Orthographic : BatchCullingProjectionType.Perspective,
                };
                splitOffset += slices.Length;
            }
            context.CullShadowCasters(cullResults, new ShadowCastersCullingInfos
            {
                splitBuffer = splitBuffer,
                perLightInfos = perLightInfos,
            });
#endif
            return result;
        }

        static int FindMainLightIndex(NativeArray<VisibleLight> visibleLights)
        {
            if (!visibleLights.IsCreated)
                return -1;

            Light sun = RenderSettings.sun;
            int brightestDirectionalLightIndex = -1;
            float brightestLightIntensity = 0.0f;
            for (int lightIndex = 0; lightIndex < visibleLights.Length; ++lightIndex)
            {
                ref VisibleLight visibleLight =
                    ref visibleLights.UnsafeElementAt(lightIndex);
                Light light = visibleLight.light;
                // A null entry is not a terminator. Native culling can leave holes in
                // visibleLights; Unity 6 continues scanning the remaining lights.
                if (light == null)
                    continue;

                if (visibleLight.lightType != LightType.Directional)
                    continue;

                if (light == sun)
                    return lightIndex;

                if (light.intensity > brightestLightIntensity)
                {
                    brightestLightIntensity = light.intensity;
                    brightestDirectionalLightIndex = lightIndex;
                }
            }

            return brightestDirectionalLightIndex;
        }
    }
}
