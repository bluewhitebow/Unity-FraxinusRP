// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

#ifndef GPUBatcher_HZB_INCLUDED
#define GPUBatcher_HZB_INCLUDED

// matches Unity's OcclusionCullingCommon.hlsl from SRP Core Runtime/GPUDriven
//
// camera-relative world space for all projections, VP matrix has translation removed
// depth pyramid is a single flat 2D texture, l-shaped atlas layout
// occlusion test uses GatherRed to sample 2x2 depth footprint at the chosen mip
// The atlas uses canonical reversed depth (near=1, far=0) on every graphics API.

// resources

Texture2D<float> _HZBTexture;
SamplerState sampler_HZBTexture;

// atlas layout
float4   _OccluderDepthPyramidSize;    // (atlasWidth, atlasHeight, 1/atlasWidth, 1/atlasHeight)
float2   _DepthSizeInOccluderPixels;   // top mip dimensions
int      _HZBMipCount;

// per-mip bounds in the atlas: int4(offsetX, offsetY, sizeX, sizeY)
#define HZB_MAX_MIPS 8
int4     _OccluderMipBounds[HZB_MAX_MIPS];

// per-view data
float4x4 _ViewProjMatrix;             // gpuProjMatrix * viewMatrixNoTranslation (camera-relative)
float4   _FacingDirWorldSpace;         // invViewMatrix column 2 normalized (toward viewer)
float4   _RadialDirWorldSpace;         // (X + Y) axes of camera frame, normalized (diagonal)
float    _HZBPixelExpansion;           // static pixel expansion in HZB texels, 0 when pass 1 retest active
float    _HZBTranslationScale;        // |cameraDelta| * focalLengthPixels, 0 when pass 1 active or no delta

// camera position for HZB projection, pass 0 uses previous frame's position
float4   _HZBCameraPosition;

// x: device depth is reversed, y: texture UV origin is at the top,
// zw: clip Z/W -> device depth scale/bias (OpenGL uses [-1,1] clip Z).
float4 _HZBDepthConvention;

float HZBCanonicalDepth(float deviceDepth)
{
    return _HZBDepthConvention.x > 0.5 ? deviceDepth : 1.0 - deviceDepth;
}

float4 HZBCanonicalDepth(float4 deviceDepth)
{
    return _HZBDepthConvention.x > 0.5 ? deviceDepth : 1.0 - deviceDepth;
}

float HZBProjectedDepth(float clipDepth)
{
    return HZBCanonicalDepth(clipDepth * _HZBDepthConvention.z + _HZBDepthConvention.w);
}

float HZBTextureY(float clipY)
{
    return _HZBDepthConvention.y > 0.5 ? -clipY : clipY;
}


// depth comparison helpers (reversed-Z: near=1, far=0)

float HZBFarthestDepth(float4 depths)
{
    return min(min(depths.x, depths.y), min(depths.z, depths.w));
}

bool HZBIsVisibleAfterOcclusion(float occluderDepth, float queryClosestDepth)
{
    return queryClosestDepth > occluderDepth;
}


// NDC projection helpers (camera-relative world space)

float3 HZBComputeNDCWithZ(float3 positionRWS)
{
    float4 cs = mul(_ViewProjMatrix, float4(positionRWS, 1.0));

    cs.y = HZBTextureY(cs.y);

    cs.xyz *= rcp(cs.w);
    cs.xy = cs.xy * 0.5 + 0.5;
    cs.z = HZBProjectedDepth(cs.z);
    return cs.xyz;
}

float2 HZBComputeNDC(float3 positionRWS)
{
    return HZBComputeNDCWithZ(positionRWS).xy;
}

// mip selection: 2^mip >= radiusInPixels; frexp out must be float (DXC bug on vulkan)
int ComputeHZBMipLevel(float radiusInPixels)
{
    float mipExp = 0;
    frexp(radiusInPixels, mipExp);
    return max((int)mipExp, 0);
}


// instance-level HZB occlusion test for a bounding sphere

bool IsHZBVisible(float3 worldCenter, float worldRadius)
{
    // camera-relative world space - uses _HZBCameraPosition so Pass 0 projects
    // with the previous frame's camera position to match the previous HZB
    float3 centerPosRWS = worldCenter - _HZBCameraPosition.xyz;

    float3 radialVec = worldRadius * _RadialDirWorldSpace.xyz;
    float3 facingVec = worldRadius * _FacingDirWorldSpace.xyz;

    float2 centerPosNDC = HZBComputeNDC(centerPosRWS);

    // skip if center projects off-screen
    if (any(centerPosNDC < 0.0) || any(centerPosNDC > 1.0))
        return true;

    float2 radialPosNDC = HZBComputeNDC(centerPosRWS + radialVec);
    float3 frontCenterPosRWS = centerPosRWS + facingVec;

    float queryClosestDepth = HZBComputeNDCWithZ(frontCenterPosRWS).z;
    bool isBehindCamera = dot(frontCenterPosRWS, _FacingDirWorldSpace.xyz) >= 0.0;

    float2 centerCoordInTopMip = centerPosNDC * _DepthSizeInOccluderPixels;
    float radiusInPixels = length((radialPosNDC - centerPosNDC) * _DepthSizeInOccluderPixels);

    // expand rect by pixel expansion factor for camera motion compensation
    float depth = abs(dot(centerPosRWS, _FacingDirWorldSpace.xyz));
    float translationPixels = depth > 0.01 ? _HZBTranslationScale / depth : 0.0;
    radiusInPixels += _HZBPixelExpansion + translationPixels;

    int mipLevel = ComputeHZBMipLevel(radiusInPixels);

    if (mipLevel < _HZBMipCount && !isBehindCamera)
    {
        float2 centerCoordInChosenMip = ldexp(centerCoordInTopMip, -mipLevel);
        int4 mipBounds = _OccluderMipBounds[mipLevel];

        // clamp to keep GatherRed footprint within mip's atlas region
        float2 gatherUv = (float2(mipBounds.xy) + clamp(centerCoordInChosenMip, 0.5, float2(mipBounds.zw) - 1.0)) * _OccluderDepthPyramidSize.zw;
        float4 gatherDepths = _HZBTexture.GatherRed(sampler_HZBTexture, gatherUv);
        float occluderDepth = HZBFarthestDepth(gatherDepths);
        return HZBIsVisibleAfterOcclusion(occluderDepth, queryClosestDepth);
    }

    return true;
}


// chunk-level HZB test using projected AABB corners
// projects all 8 corners for tight screen rect + closest depth

bool IsChunkHZBVisible(float3 aabbMin, float3 aabbMax)
{
    float3 aabbCenter = (aabbMin + aabbMax) * 0.5;
    float3 centerRWS = aabbCenter - _HZBCameraPosition.xyz;

    // project all 8 AABB corners
    float2 screenMin = 2.0;
    float2 screenMax = -1.0;
    float closestDepth = 0.0; // reversed-Z: 0 = far, 1 = near
    bool anyBehind = false;

    [unroll]
    for (uint i = 0; i < 8; i++)
    {
        float3 corner = float3(
            (i & 1) ? aabbMax.x : aabbMin.x,
            (i & 2) ? aabbMax.y : aabbMin.y,
            (i & 4) ? aabbMax.z : aabbMin.z);

        float3 cornerRWS = corner - _HZBCameraPosition.xyz;
        float4 cs = mul(_ViewProjMatrix, float4(cornerRWS, 1.0));

        if (cs.w <= 0.0)
        {
            anyBehind = true;
            continue;
        }

        cs.y = HZBTextureY(cs.y);
        float3 ndc = cs.xyz * rcp(cs.w);
        float2 uv = ndc.xy * 0.5 + 0.5;

        screenMin = min(screenMin, uv);
        screenMax = max(screenMax, uv);
        closestDepth = max(closestDepth, HZBProjectedDepth(ndc.z));
    }

    // if any corner is behind camera, AABB straddles near plane - skip test
    if (anyBehind) return true;

    // if entire AABB projects off-screen, skip
    if (any(screenMax < 0.0) || any(screenMin > 1.0))
        return true;

    // clamp screen rect to viewport
    screenMin = saturate(screenMin);
    screenMax = saturate(screenMax);

    // compute rect size in top-mip pixels and select mip
    float2 rectSizePixels = (screenMax - screenMin) * _DepthSizeInOccluderPixels;
    float maxExtent = max(rectSizePixels.x, rectSizePixels.y);

    // pixel expansion for temporal mismatch
    float centerDepth = abs(dot(centerRWS, _FacingDirWorldSpace.xyz));
    float chunkTranslationPixels = centerDepth > 0.01 ? _HZBTranslationScale / centerDepth : 0.0;
    maxExtent += (_HZBPixelExpansion + chunkTranslationPixels) * 2.0; // diameter, not radius

    int mipLevel = ComputeHZBMipLevel(maxExtent * 0.5);  // half-extent for GatherRed 2x2 coverage

    if (mipLevel < _HZBMipCount)
    {
        float2 centerNDC = (screenMin + screenMax) * 0.5;
        float2 centerCoordInTopMip = centerNDC * _DepthSizeInOccluderPixels;
        float2 centerCoordInChosenMip = ldexp(centerCoordInTopMip, -mipLevel);
        int4 mipBounds = _OccluderMipBounds[mipLevel];

        float2 gatherUv = (float2(mipBounds.xy) + clamp(centerCoordInChosenMip, 0.5, float2(mipBounds.zw) - 1.0)) * _OccluderDepthPyramidSize.zw;
        float4 gatherDepths = _HZBTexture.GatherRed(sampler_HZBTexture, gatherUv);
        float occluderDepth = HZBFarthestDepth(gatherDepths);
        return HZBIsVisibleAfterOcclusion(occluderDepth, closestDepth);
    }

    return true;
}

// shadow caster occlusion test for a capsule (caster sphere swept along the light direction)
// walks the capsule as up to 8 sub-capsules so long shadows land on fine mips and compare
// against their own segment depth; cull requires every segment occluded, every early-out
// keeps the caster

bool IsShadowCapsuleHZBVisible(float3 p0, float3 p1, float radius)
{
    float3 rws0 = p0 - _HZBCameraPosition.xyz;
    float3 rws1 = p1 - _HZBCameraPosition.xyz;

    // capsule closest depth comes from the front points; behind-camera is untestable
    float3 front0 = rws0 + radius * _FacingDirWorldSpace.xyz;
    float3 front1 = rws1 + radius * _FacingDirWorldSpace.xyz;
    if (dot(front0, _FacingDirWorldSpace.xyz) >= 0.0) return true;
    if (dot(front1, _FacingDirWorldSpace.xyz) >= 0.0) return true;

    // clip space is affine in world position - project the endpoints once, lerp per segment
    float4 cs0 = mul(_ViewProjMatrix, float4(rws0, 1.0));
    float4 cs1 = mul(_ViewProjMatrix, float4(rws1, 1.0));
    if (cs0.w <= 0.0) return true;
    if (cs1.w <= 0.0) return true;
    cs0.y = HZBTextureY(cs0.y);
    cs1.y = HZBTextureY(cs1.y);

    float4 csF0 = mul(_ViewProjMatrix, float4(front0, 1.0));
    float4 csF1 = mul(_ViewProjMatrix, float4(front1, 1.0));
    float3 radial = radius * _RadialDirWorldSpace.xyz;
    float4 csR0 = mul(_ViewProjMatrix, float4(rws0 + radial, 1.0));
    float4 csR1 = mul(_ViewProjMatrix, float4(rws1 + radial, 1.0));
    csR0.y = HZBTextureY(csR0.y);
    csR1.y = HZBTextureY(csR1.y);

    float2 uv0 = cs0.xy * rcp(cs0.w) * 0.5 + 0.5;
    float2 uv1 = cs1.xy * rcp(cs1.w) * 0.5 + 0.5;
    float2 c0 = uv0 * _DepthSizeInOccluderPixels;
    float2 c1 = uv1 * _DepthSizeInOccluderPixels;
    float rPix0 = length((csR0.xy * rcp(csR0.w) * 0.5 + 0.5 - uv0) * _DepthSizeInOccluderPixels);
    float rPix1 = length((csR1.xy * rcp(csR1.w) * 0.5 + 0.5 - uv1) * _DepthSizeInOccluderPixels);

    // view depths for temporal expansion, linear along the axis
    float d0 = abs(dot(rws0, _FacingDirWorldSpace.xyz));
    float d1 = abs(dot(rws1, _FacingDirWorldSpace.xyz));
    float expandFull = _HZBPixelExpansion + _HZBTranslationScale / max(min(d0, d1), 0.01);

    // off-screen shadows are handled by the receiver plane tests
    float2 rectMin = min(c0 - rPix0, c1 - rPix1) - expandFull;
    float2 rectMax = max(c0 + rPix0, c1 + rPix1) + expandFull;
    if (any(rectMax < 0.0) || any(rectMin > _DepthSizeInOccluderPixels))
        return true;

    // segment count sized to the endpoint footprint so each segment lands on a fine mip
    float maxExtent = max(rectMax.x - rectMin.x, rectMax.y - rectMin.y);
    float targetExtent = max(2.0 * max(rPix0, rPix1) + 2.0 * expandFull, 8.0);
    int segCount = clamp((int)ceil(maxExtent / targetExtent), 1, 8);

    float2 cA = c0;
    float rPixA = rPix0;
    float dA = d0;
    float qA = HZBProjectedDepth(csF0.z * rcp(csF0.w));
    float qEnd = HZBProjectedDepth(csF1.z * rcp(csF1.w));

    [loop]
    for (int s = 0; s < segCount; s++)
    {
        float t = (s + 1) / (float)segCount;
        float4 csB = lerp(cs0, cs1, t);
        float4 csRB = lerp(csR0, csR1, t);
        float4 csFB = lerp(csF0, csF1, t);

        float2 uvB = csB.xy * rcp(csB.w) * 0.5 + 0.5;
        float2 cB = uvB * _DepthSizeInOccluderPixels;
        float rPixB = length((csRB.xy * rcp(csRB.w) * 0.5 + 0.5 - uvB) * _DepthSizeInOccluderPixels);
        float dB = lerp(d0, d1, t);
        float qB = (s == segCount - 1) ? qEnd : HZBProjectedDepth(csFB.z * rcp(csFB.w));

        // segment rect = union of its two endpoint circles
        float expand = _HZBPixelExpansion + _HZBTranslationScale / max(min(dA, dB), 0.01);
        float2 sMin = min(cA - rPixA, cB - rPixB) - expand;
        float2 sMax = max(cA + rPixA, cB + rPixB) + expand;
        if (any(sMax < 0.0) || any(sMin > _DepthSizeInOccluderPixels))
            return true;
        sMin = max(sMin, 0.0);
        sMax = min(sMax, _DepthSizeInOccluderPixels);

        float ext = max(sMax.x - sMin.x, sMax.y - sMin.y);
        int mip = ComputeHZBMipLevel(ext * 0.5);
        if (mip >= _HZBMipCount) return true;

        float2 coordInMip = ldexp((sMin + sMax) * 0.5, -mip);
        int4 mipBounds = _OccluderMipBounds[mip];
        float2 gatherUv = (float2(mipBounds.xy) + clamp(coordInMip, 0.5, float2(mipBounds.zw) - 1.0)) * _OccluderDepthPyramidSize.zw;
        float4 gatherDepths = _HZBTexture.GatherRed(sampler_HZBTexture, gatherUv);
        if (HZBIsVisibleAfterOcclusion(HZBFarthestDepth(gatherDepths), max(qA, qB)))
            return true;

        cA = cB;
        rPixA = rPixB;
        dA = dB;
        qA = qB;
    }
    return false;
}

#endif // GPUBatcher_HZB_INCLUDED
