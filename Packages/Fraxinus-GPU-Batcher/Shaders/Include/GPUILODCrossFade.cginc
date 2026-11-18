#ifndef GPU_INSTANCER_CROSSFADE_INCLUDED
#define GPU_INSTANCER_CROSSFADE_INCLUDED

#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(GPUI_MHT_MATRIX_APPEND)

#ifdef UNITY_APPLY_DITHER_CROSSFADE
#undef UNITY_APPLY_DITHER_CROSSFADE
#endif

#ifdef LOD_FADE_CROSSFADE
#define UNITY_APPLY_DITHER_CROSSFADE(vpos) UnityApplyDitherCrossFadeGPUI(vpos)
#else
#define UNITY_APPLY_DITHER_CROSSFADE(vpos)
#endif

uniform sampler2D _DitherMaskLOD2D;
void UnityApplyDitherCrossFadeGPUI(float2 vpos)
{
    if (LODLevel >= 0)
    {
#ifdef GPUI_MHT_COPY_TEXTURE
        uint textureWidth = min(bufferSize, maxTextureSize);
        float indexX = ((gpui_InstanceID % maxTextureSize) + 0.5f) / textureWidth;
        float rowCount = ceil(bufferSize / maxTextureSize);
        float rowIndex = floor(gpui_InstanceID / maxTextureSize) + 0.5f;

        uint4 lodData = uint4(tex2Dlod(gpuiInstanceLODDataTexture, float4(indexX, rowIndex / rowCount, 0.0f, 0.0f)));
#else
        uint4 lodData = gpuiInstanceLODData[gpui_InstanceID];
#endif

        if (lodData.w > 0)
        {
            float fadeLevel = floor(lodData.w * fadeLevelMultiplier);

            if (lodData.z == uint(LODLevel))
                fadeLevel = 15 - fadeLevel;

            if (fadeLevel > 0)
            {
                vpos /= 4; // the dither mask texture is 4x4
                vpos.y = (frac(vpos.y) + fadeLevel) * 0.0625 /* 1/16 */; // quantized lod fade by 16 levels
                clip(tex2D(_DitherMaskLOD2D, vpos).a - 0.5);
            }
        }
    }
}

#ifdef UNITY_RANDOM_INCLUDED
#ifdef LODDitheringTransition
#undef LODDitheringTransition
#endif

#define LODDitheringTransition(fadeMaskSeed, ditherFactor) LODDitheringTransitionGPUI(fadeMaskSeed, ditherFactor)

    void LODDitheringTransitionGPUI(uint2 fadeMaskSeed, float ditherFactor)
    {
	    if (LODLevel >= 0)
	    {
#ifdef GPUI_MHT_COPY_TEXTURE
            uint textureWidth = min(bufferSize, maxTextureSize);
            float indexX = ((gpui_InstanceID % maxTextureSize) + 0.5f) / textureWidth;
            float rowCount = ceil(bufferSize / maxTextureSize);
            float rowIndex = floor(gpui_InstanceID / maxTextureSize) + 0.5f;

            uint4 lodData = uint4(tex2Dlod(gpuiInstanceLODDataTexture, float4(indexX, rowIndex / rowCount, 0.0f, 0.0f)));
#else
            uint4 lodData = gpuiInstanceLODData[gpui_InstanceID];
#endif

		    if (lodData.w > 0)
		    {
			    float fadeLevel = floor(lodData.w * fadeLevelMultiplier);

			    if (fadeLevel > 0)
			    {
				    ditherFactor =  (frac(fadeMaskSeed.y) + fadeLevel) * 0.0625;
				    float p = GenerateHashedRandomFloat(fadeMaskSeed);
				    float f = ditherFactor - p;
				    if (lodData.z == uint(LODLevel))
					    f = -f;
				    clip(f);
			    }
		    }
	    }
    }
#endif // UNITY_RANDOM_INCLUDED

#if defined(ULTRA_PIPELINE_CORE_INCLUDED) && defined(LOD_FADE_CROSSFADE)
// Fraxinus passes use LODFadeCrossFade; their per-renderer unity_LODFade
// cannot represent the independent LOD transitions in an indirect draw.
#include "Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/LODCrossFade.hlsl"

void LODFadeCrossFadeGPUI(float4 positionCS)
{
    if (LODLevel < 0)
        return;

#ifdef GPUI_MHT_COPY_TEXTURE
    uint textureWidth = min(bufferSize, maxTextureSize);
    float indexX = ((gpui_InstanceID % maxTextureSize) + 0.5f) / textureWidth;
    float rowCount = ceil(bufferSize / maxTextureSize);
    float rowIndex = floor(gpui_InstanceID / maxTextureSize) + 0.5f;
    uint4 lodData = uint4(tex2Dlod(gpuiInstanceLODDataTexture, float4(indexX, rowIndex / rowCount, 0.0f, 0.0f)));
#else
    uint4 lodData = gpuiInstanceLODData[gpui_InstanceID];
#endif

    if (lodData.w > 0)
    {
        float fadeLevel = floor(lodData.w * fadeLevelMultiplier) * 0.0625;
        if (lodData.z == uint(LODLevel))
            fadeLevel = -fadeLevel;

        half dither = SAMPLE_TEXTURE2D(_DitheringTexture, sampler_PointRepeat, positionCS.xy * _DitheringTextureInvSize).a;
        clip(fadeLevel - CopySign(dither, fadeLevel));
    }
}

#define LODFadeCrossFade(positionCS) LODFadeCrossFadeGPUI(positionCS)
#endif

#endif  // UNITY_PROCEDURAL_INSTANCING_ENABLED

#endif // GPU_INSTANCER_CROSSFADE_INCLUDED
