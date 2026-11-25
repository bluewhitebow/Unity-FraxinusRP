//============================================================================================================
//
//
//                  Copyright (c) 2024, Qualcomm Innovation Center, Inc. All rights reserved.
//                              SPDX-License-Identifier: BSD-3-Clause
//
//============================================================================================================

half FastLanczos(half base)
{
	half y = base - 1.0;
	half y2 = y * y;
	half y_temp = 0.75 * y + y2;
	return y_temp * y2;
}

TEXTURE2D_X(_DepthClipTexture);
TEXTURE2D_X(_Sgsr2HistoryTexture);
SAMPLER(sampler_Sgsr2HistoryTexture);

CBUFFER_START(Sgsr2UpscaleData)
    float2 _RenderSize;
    float2 _OutputSize;
    float2 _RenderSizeRcp;
    float2 _OutputSizeRcp;
    float2 _JitterOffset;
    float2 _ScaleRatio;
    float  _CameraFovAngleHor;
    float  _MinLerpContribution;
    float  _Reset;
    int    _SameCameraFrmNum;
CBUFFER_END

// Keep the algorithm below readable while using the explicit SGSR2 shader
// property names shared with the CPU setup code.
#define renderSize _RenderSize
#define outputSize _OutputSize
#define renderSizeRcp _RenderSizeRcp
#define outputSizeRcp _OutputSizeRcp
#define jitterOffset _JitterOffset
#define scaleRatio _ScaleRatio
#define cameraFovAngleHor _CameraFovAngleHor
#define minLerpContribution _MinLerpContribution
#define reset _Reset
#define bSameCamera _SameCameraFrmNum

half3 Sgsr2LoadColor(int2 pixel)
{
    int2 maxPixel = max(int2(renderSize) - 1, int2(0, 0));
    return LOAD_TEXTURE2D_X(_BlitTexture, clamp(pixel, int2(0, 0), maxPixel)).xyz;
}

half4 SnapdragonGameSuperResolutionUpscalePass(float2 texCoord)
{
    half Biasmax_viewportXScale = scaleRatio.x;
    half scalefactor = scaleRatio.y;

    float2 Hruv = texCoord;

    float2 Jitteruv;
    Jitteruv.x = clamp(Hruv.x + (jitterOffset.x * renderSizeRcp.x), 0.0, 1.0);
    Jitteruv.y = clamp(Hruv.y + (jitterOffset.y * renderSizeRcp.y), 0.0, 1.0);

    int2 InputPos = clamp(int2(Jitteruv * renderSize), int2(0, 0), int2(renderSize) - 1);

    half4 motionDepth = SAMPLE_TEXTURE2D_X_LOD(_DepthClipTexture, sampler_LinearClamp, Jitteruv, 0.0);
    half2 Motion = motionDepth.xy;
    half depthfactor = motionDepth.z;

    float2 previousUv = Hruv + Motion;
    bool historyInBounds = all(previousUv >= 0.0) && all(previousUv <= 1.0);
    float2 PrevUV = saturate(previousUv);
    // Never sample uninitialised history on reset frames: NaN * 0 remains NaN.
    half3 HistoryColor = 0.0;
    if (reset == 0.0 && historyInBounds)
        HistoryColor = SAMPLE_TEXTURE2D_X_LOD(_Sgsr2HistoryTexture, sampler_Sgsr2HistoryTexture, PrevUV, 0.0).xyz;

    /////upsample and compute box
    half4 Upsampledcw = 0.0;
    half biasmax = Biasmax_viewportXScale ;
    half biasmin = max(1.0, 0.3 + 0.3 * biasmax);
    // A reset frame must use the conservative reconstruction kernel while
    // history is being seeded.  Without the reset term the first frame uses
    // the temporal kernel even though its history contribution is disabled,
    // which produces an avoidable sharpness/edge discontinuity on camera
    // cuts and history reallocations.
    half biasfactor = max(0.25 * depthfactor, reset);
    half kernelbias = lerp(biasmax, biasmin, biasfactor);
    // The conversion texture stores backwards motion directly in UV units so
    // history lookup stays native to URP. Qualcomm's SGSR2 tuning, however,
    // measures velocity from its 2x NDC-style encoding. Restore that scale
    // for the temporal thresholds without changing the correct history UV.
    half motion_viewport_len = length((Motion * 2.0h) * outputSize);
    half curvebias = lerp(-2.0, -3.0, clamp(motion_viewport_len * 0.02, 0.0, 1.0));

    // HDR output can exceed 255 nits; keep squared moments in full precision.
    float3 rectboxcenter = 0.0;
    float3 rectboxvar = 0.0;
    half rectboxweight = 0.0;
    float2 srcpos = InputPos + 0.5 - jitterOffset;

    kernelbias *= 0.5;
    half kernelbias2 = kernelbias * kernelbias;
    half2 srcpos_srcOutputPos = srcpos - Hruv * renderSize;  //srcOutputPos = Hruv * renderSize;
    half3 rectboxmin;
    half3 rectboxmax;
    half3 topMid = Sgsr2LoadColor(InputPos + int2(0, 1));
    {

        half3 samplecolor = topMid;
        half2 baseoffset = srcpos_srcOutputPos + half2(0.0, 1.0);
        half baseoffset_dot = dot(baseoffset, baseoffset);
        half base = clamp(baseoffset_dot * kernelbias2, 0.0, 1.0);
        half weight = FastLanczos(base);
        Upsampledcw += half4(samplecolor * weight, weight);
        half boxweight = exp(baseoffset_dot * curvebias);
        rectboxmin = samplecolor;
        rectboxmax = samplecolor;
        float3 wsample = float3(samplecolor) * boxweight;
        rectboxcenter += wsample;
        rectboxvar += (samplecolor * wsample);
        rectboxweight += boxweight;
    }
    half3 rightMid = Sgsr2LoadColor(InputPos + int2(1, 0));
    {

        half3 samplecolor = rightMid;
        half2 baseoffset = srcpos_srcOutputPos + half2(1.0, 0.0);
        half baseoffset_dot = dot(baseoffset, baseoffset);
        half base = clamp(baseoffset_dot * kernelbias2, 0.0, 1.0);
        half weight = FastLanczos(base);
        Upsampledcw += half4(samplecolor * weight, weight);
        half boxweight = exp(baseoffset_dot * curvebias);
        rectboxmin = min(rectboxmin, samplecolor);
        rectboxmax = max(rectboxmax, samplecolor);
        float3 wsample = float3(samplecolor) * boxweight;
        rectboxcenter += wsample;
        rectboxvar += (samplecolor * wsample);
        rectboxweight += boxweight;
    }
    half3 leftMid = Sgsr2LoadColor(InputPos + int2(-1, 0));
    {

        half3 samplecolor = leftMid;
        half2 baseoffset = srcpos_srcOutputPos + half2(-1.0, 0.0);
        half baseoffset_dot = dot(baseoffset, baseoffset);
        half base = clamp(baseoffset_dot * kernelbias2, 0.0, 1.0);
        half weight = FastLanczos(base);
        Upsampledcw += half4(samplecolor * weight, weight);
        half boxweight = exp(baseoffset_dot * curvebias);
        rectboxmin = min(rectboxmin, samplecolor);
        rectboxmax = max(rectboxmax, samplecolor);
        float3 wsample = float3(samplecolor) * boxweight;
        rectboxcenter += wsample;
        rectboxvar += (samplecolor * wsample);
        rectboxweight += boxweight;
    }
    half3 centerMid = Sgsr2LoadColor(InputPos);
    {

        half3 samplecolor = centerMid;
        half2 baseoffset = srcpos_srcOutputPos;
        half baseoffset_dot = dot(baseoffset, baseoffset);
        half base = clamp(baseoffset_dot * kernelbias2, 0.0, 1.0);
        half weight = FastLanczos(base);
        Upsampledcw += half4(samplecolor * weight, weight);
        half boxweight = exp(baseoffset_dot * curvebias);
        rectboxmin = min(rectboxmin, samplecolor);
        rectboxmax = max(rectboxmax, samplecolor);
        float3 wsample = float3(samplecolor) * boxweight;
        rectboxcenter += wsample;
        rectboxvar += (samplecolor * wsample);
        rectboxweight += boxweight;
    }
    half3 btmMid = Sgsr2LoadColor(InputPos + int2(0, -1));
    {

        half3 samplecolor = btmMid;
        half2 baseoffset = srcpos_srcOutputPos + half2(0.0, -1.0);
        half baseoffset_dot = dot(baseoffset, baseoffset);
        half base = clamp(baseoffset_dot * kernelbias2, 0.0, 1.0);
        half weight = FastLanczos(base);
        Upsampledcw += half4(samplecolor * weight, weight);
        half boxweight = exp(baseoffset_dot * curvebias);
        rectboxmin = min(rectboxmin, samplecolor);
        rectboxmax = max(rectboxmax, samplecolor);
        float3 wsample = float3(samplecolor) * boxweight;
        rectboxcenter += wsample;
        rectboxvar += (samplecolor * wsample);
        rectboxweight += boxweight;
    }

    // Add the diagonal taps when a valid previous camera state exists. On a
    // reset frame the cross is preferable because it avoids sampling stale
    // edge colours while the history is being seeded.
    if (bSameCamera != 0)
    {
        {
            half3 topRight = Sgsr2LoadColor(InputPos + int2(1, 1));
            half3 samplecolor = topRight;
            half2 baseoffset = srcpos_srcOutputPos + half2(1.0, 1.0);
            half baseoffset_dot = dot(baseoffset, baseoffset);
            half base = clamp(baseoffset_dot * kernelbias2, 0.0, 1.0);
            half weight = FastLanczos(base);
            Upsampledcw += half4(samplecolor * weight, weight);
            half boxweight = exp(baseoffset_dot * curvebias);
            rectboxmin = min(rectboxmin, samplecolor);
            rectboxmax = max(rectboxmax, samplecolor);
            float3 wsample = float3(samplecolor) * boxweight;
            rectboxcenter += wsample;
            rectboxvar += (samplecolor * wsample);
            rectboxweight += boxweight;
        }
        {
            half3 topLeft = Sgsr2LoadColor(InputPos + int2(-1, 1));
            half3 samplecolor = topLeft;
            half2 baseoffset = srcpos_srcOutputPos + half2(-1.0, 1.0);
            half baseoffset_dot = dot(baseoffset, baseoffset);
            half base = clamp(baseoffset_dot * kernelbias2, 0.0, 1.0);
            half weight = FastLanczos(base);
            Upsampledcw += half4(samplecolor * weight, weight);
            half boxweight = exp(baseoffset_dot * curvebias);
            rectboxmin = min(rectboxmin, samplecolor);
            rectboxmax = max(rectboxmax, samplecolor);
            float3 wsample = float3(samplecolor) * boxweight;
            rectboxcenter += wsample;
            rectboxvar += (samplecolor * wsample);
            rectboxweight += boxweight;
        }
        {
            half3 btmRight = Sgsr2LoadColor(InputPos + int2(1, -1));
            half3 samplecolor = btmRight;
            half2 baseoffset = srcpos_srcOutputPos + half2(1.0, -1.0);
            half baseoffset_dot = dot(baseoffset, baseoffset);
            half base = clamp(baseoffset_dot * kernelbias2, 0.0, 1.0);
            half weight = FastLanczos(base);
            Upsampledcw += half4(samplecolor * weight, weight);
            half boxweight = exp(baseoffset_dot * curvebias);
            rectboxmin = min(rectboxmin, samplecolor);
            rectboxmax = max(rectboxmax, samplecolor);
            float3 wsample = float3(samplecolor) * boxweight;
            rectboxcenter += wsample;
            rectboxvar += (samplecolor * wsample);
            rectboxweight += boxweight;
        }

        {
            half3 btmLeft = Sgsr2LoadColor(InputPos + int2(-1, -1));
            half3 samplecolor = btmLeft;
            half2 baseoffset = srcpos_srcOutputPos + half2(-1.0, -1.0);
            half baseoffset_dot = dot(baseoffset, baseoffset);
            half base = clamp(baseoffset_dot * kernelbias2, 0.0, 1.0);
            half weight = FastLanczos(base);
            Upsampledcw += half4(samplecolor * weight, weight);
            half boxweight = exp(baseoffset_dot * curvebias);
            rectboxmin = min(rectboxmin, samplecolor);
            rectboxmax = max(rectboxmax, samplecolor);
            float3 wsample = float3(samplecolor) * boxweight;
            rectboxcenter += wsample;
            rectboxvar += (samplecolor * wsample);
            rectboxweight += boxweight;
        }
    }

    rectboxweight = 1.0 / rectboxweight;
    rectboxcenter *= rectboxweight;
    rectboxvar *= rectboxweight;
    rectboxvar = sqrt(abs(rectboxvar - rectboxcenter * rectboxcenter));

    half safeWeight = max(Upsampledcw.w, 1.0e-04);
    Upsampledcw.xyz =  clamp(Upsampledcw.xyz / safeWeight, rectboxmin - 0.075, rectboxmax + 0.075);
    Upsampledcw.w = safeWeight * (1.0 / 3.0) ;

    half baseupdate = 1.0 - depthfactor;
    baseupdate = min(baseupdate, lerp(baseupdate, Upsampledcw.w * 10.0, clamp(10.0 * motion_viewport_len, 0.0, 1.0)));
    baseupdate = min(baseupdate, lerp(baseupdate, Upsampledcw.w, clamp(motion_viewport_len * 0.05, 0.0, 1.0)));
    half basealpha = baseupdate;

    const float EPSILON = 1.192e-07;
    half boxscale = max(depthfactor, clamp(motion_viewport_len * 0.05, 0.0, 1.0));
    half boxsize = lerp(scalefactor, 1.0, boxscale);
    float3 sboxvar = rectboxvar * boxsize;
    float3 boxmin = rectboxcenter - sboxvar;
    float3 boxmax = rectboxcenter + sboxvar;
    rectboxmax = min(rectboxmax, boxmax);
    rectboxmin = max(rectboxmin, boxmin);

    half3 clampedcolor = clamp(HistoryColor, rectboxmin, rectboxmax);
    half startLerpValue = minLerpContribution;
    if ((abs(Motion.x) + abs(Motion.y)) > 0.000001) startLerpValue = 0.0;
    half lerpcontribution = (any(rectboxmin > HistoryColor) || any(HistoryColor > rectboxmax)) ? startLerpValue : 1.0;

    HistoryColor = lerp(clampedcolor, HistoryColor, clamp(lerpcontribution, 0.0, 1.0));
    half basemin = min(basealpha, 0.1);
    basealpha = lerp(basemin, basealpha, clamp(lerpcontribution, 0.0, 1.0));

    ////blend color
    half alphasum = max(EPSILON, basealpha + Upsampledcw.w);
    half alpha = historyInBounds ? clamp(Upsampledcw.w / alphasum + reset, 0.0, 1.0) : 1.0;

    Upsampledcw.xyz = lerp(HistoryColor, Upsampledcw.xyz, alpha);


    half sourceAlpha = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, Jitteruv, 0.0).a;
    return half4(max(Upsampledcw.xyz, 0.0), sourceAlpha);
}
