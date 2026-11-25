// Snapdragon Game Super Resolution 2 - motion/depth conversion stage.
// SPDX-License-Identifier: BSD-3-Clause

CBUFFER_START(Sgsr2ConvertData)
    float4x4 _ClipToPrevClip;
    float2 _RenderSize;
    float2 _RenderSizeRcp;
    float2 _JitterOffset;
    float _CameraFovAngleHor;
    int _Sgsr2UseMotionVectors;
CBUFFER_END

TEXTURE2D_X(_Sgsr2MotionVectorTexture);
SAMPLER(sampler_Sgsr2MotionVectorTexture);

#if UNITY_REVERSED_Z
    #define SGSR2_NEAREST_DEPTH(a, b) max((a), (b))
    // Unity's reversed depth stores near as 1 and far as 0.  The reference
    // SGSR2 implementation first inverts this value before doing its
    // nearest-depth/min comparisons, so the separation term must use the
    // same non-reversed distance convention.
    #define SGSR2_DEPTH_SEPARATION_BASE(depth) (1.0 - (depth))
    #define SGSR2_HAS_VALID_DEPTH(depth) ((depth) > 1.0e-05)
#else
    #define SGSR2_NEAREST_DEPTH(a, b) min((a), (b))
    #define SGSR2_DEPTH_SEPARATION_BASE(depth) (1.0 - (depth))
    #define SGSR2_HAS_VALID_DEPTH(depth) ((depth) < (1.0 - 1.0e-05))
#endif

// Returns backwards motion in screen-UV units. URP's motion-vector pass
// stores the forward displacement, so the sign is inverted here for history
// lookup (currentUV + backwardsMotion).
half2 Sgsr2GetBackwardsMotion(float2 uv, float nearestRawDepth)
{
    // Keep the disabled-motion-vector path free of a texture read.  Initializing
    // the result before the branch satisfies the stricter Unity 2022 D3D11
    // definite-assignment analysis while preserving the original contract.
    float2 motionVector = 0.0;
    if (_Sgsr2UseMotionVectors != 0)
    {
        float4 motionSample = SAMPLE_TEXTURE2D_X(_Sgsr2MotionVectorTexture,
                                                 sampler_Sgsr2MotionVectorTexture, uv);
        motionVector = -motionSample.xy;
    }

    // Fallback for renderers that cannot produce motion vectors. Reproject the
    // nearest depth from the same dilated footprint used by SGSR2's depth-clip
    // calculation. Unity stores raw depth in [0, 1] on every API, while an
    // OpenGL GPU projection expects clip Z in [-1, 1].
    float clipDepth = nearestRawDepth;
#if !UNITY_REVERSED_Z
    clipDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, clipDepth);
#endif

    // The sampled depth belongs to the jittered raster, but _ClipToPrevClip is
    // built from non-jittered view-projection matrices. Undo the current
    // render-pixel jitter so the clip position and matrix use the same space.
    float2 currentUvNoJitter = uv - _JitterOffset * _RenderSizeRcp;

    // ComputeClipSpacePosition applies Unity's render-texture Y convention,
    // matching the GPU projection matrices used to build _ClipToPrevClip.
    float4 currentClip = ComputeClipSpacePosition(currentUvNoJitter, clipDepth);
    float4 previousClip = mul(_ClipToPrevClip, currentClip);
    float previousW = previousClip.w;
    float safePreviousW = abs(previousW) > 1.0e-06
        ? previousW
        : (previousW < 0.0 ? -1.0e-06 : 1.0e-06);
    float2 currentNdc = currentClip.xy / currentClip.w;
    float2 previousNdc = previousClip.xy / safePreviousW;
    float2 fallbackMotion = (previousNdc - currentNdc) * 0.5;
#if UNITY_UV_STARTS_AT_TOP
    fallbackMotion.y = -fallbackMotion.y;
#endif

    float useMotionVectors = (_Sgsr2UseMotionVectors != 0) ? 1.0 : 0.0;
    return half2(lerp(fallbackMotion, motionVector, useMotionVectors));
}

half Sgsr2DepthClip(float2 texCoord, out float nearestRawDepth)
{
    // Gather a 4x4 neighborhood and estimate whether the pixel lies on a
    // depth discontinuity. The estimate is intentionally conservative: a
    // discontinuity reduces temporal history contribution in the upscale
    // stage and prevents foreground/background bleeding.
    float2 gatherCoord = texCoord - 0.5 * _RenderSizeRcp;
    float4 btmLeft = GATHER_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture, gatherCoord);
    float4 btmRight = GATHER_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture,
                                          gatherCoord + float2(2.0 * _RenderSizeRcp.x, 0.0));
    float4 topLeft = GATHER_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture,
                                         gatherCoord + float2(0.0, 2.0 * _RenderSizeRcp.y));
    float4 topRight = GATHER_TEXTURE2D_X(_CameraDepthTexture, sampler_CameraDepthTexture,
                                          gatherCoord + 2.0 * _RenderSizeRcp);

    float centerDepth = SGSR2_NEAREST_DEPTH(
        SGSR2_NEAREST_DEPTH(SGSR2_NEAREST_DEPTH(btmLeft.z, btmRight.w), topLeft.y), topRight.x);
    float btmLeft4 = SGSR2_NEAREST_DEPTH(SGSR2_NEAREST_DEPTH(SGSR2_NEAREST_DEPTH(btmLeft.y, btmLeft.x), btmLeft.z), btmLeft.w);
    float btmLeftMax9 = SGSR2_NEAREST_DEPTH(topLeft.x,
        SGSR2_NEAREST_DEPTH(SGSR2_NEAREST_DEPTH(centerDepth, btmLeft4), btmRight.x));
    nearestRawDepth = btmLeftMax9;

    if (!SGSR2_HAS_VALID_DEPTH(centerDepth))
        return 0.0h;

    float btmRight4 = SGSR2_NEAREST_DEPTH(SGSR2_NEAREST_DEPTH(SGSR2_NEAREST_DEPTH(btmRight.y, btmRight.x), btmRight.z), btmRight.w);
    float topLeft4 = SGSR2_NEAREST_DEPTH(SGSR2_NEAREST_DEPTH(SGSR2_NEAREST_DEPTH(topLeft.y, topLeft.x), topLeft.z), topLeft.w);
    float topRight4 = SGSR2_NEAREST_DEPTH(SGSR2_NEAREST_DEPTH(SGSR2_NEAREST_DEPTH(topRight.y, topRight.x), topRight.z), topRight.w);

    float diagonalLength = length(_RenderSize);
    float depthSeparation = 1.37e-05 * _CameraFovAngleHor * diagonalLength *
                            SGSR2_DEPTH_SEPARATION_BASE(centerDepth);
    const float epsilon = 1.19e-07;
    float weight = 0.0;
    weight += saturate(depthSeparation / (abs(centerDepth - btmLeft4) + epsilon));
    weight += saturate(depthSeparation / (abs(centerDepth - btmRight4) + epsilon));
    weight += saturate(depthSeparation / (abs(centerDepth - topLeft4) + epsilon));
    weight += saturate(depthSeparation / (abs(centerDepth - topRight4) + epsilon));

    // Keep the same gather footprint as the reference implementation. The
    // additional minimum is useful on some drivers when all four quadrants
    // have equal depth and avoids returning a NaN from an uninitialised path.
    weight = max(weight, 0.0 + btmLeftMax9 * 0.0);
    return saturate(1.0 - weight * 0.25);
}

half4 SnapdragonGameSuperResolutionConvertPass(float2 texCoord)
{
    // Initialize the out-argument at the call site as well. Older Unity D3D11
    // shader analysis does not reliably propagate definite assignment through
    // an out parameter returned from a helper with texture gathers.
    float nearestRawDepth = 1.0;
    half depthClip = Sgsr2DepthClip(texCoord, nearestRawDepth);
    half2 backwardsMotion = Sgsr2GetBackwardsMotion(texCoord, nearestRawDepth);
    return half4(backwardsMotion, depthClip, 1.0h);
}
