using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Serialization;
using FraxinusEngine.Base;
using static FraxinusEngine.Rendering.Ultra.DeferredLights;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;
using static UnityEngine.Rendering.HDROutputUtils;

namespace FraxinusEngine.Rendering.Ultra
{
    public enum TemporalAAQuality
    {
        // Quality options were tuned to give meaningful performance differences on mobile hardware.

        /// <summary>
        /// 5-tap RGB clamp. No motion dilation. Suitable for no/low motion scenes.
        /// </summary>
        VeryLow = 0,

        /// <summary>
        /// 5-tap RGB clamp. 5-tap motion dilation.
        /// </summary>
        Low,

        /// <summary>
        /// 9-tap YCoCg variance clamp. 9-tap motion dilation.
        /// </summary>
        Medium,

        /// <summary>
        /// 9-tap YCoCg variance clamp and bicubic history.
        /// </summary>
        High,

        // VeryHigh is a catch all option for enabling all the implemented features regardless of cost.
        // Currently, 9-tap YCoCg variance clip, bicubic history and center sample filtering.
        // In the future, VeryHigh mode could read additional buffers to improve the quality further.

        /// <summary>
        /// Best quality, everything enabled.
        /// </summary>
        VeryHigh
    }

    /// <summary>
    /// Temporal anti-aliasing settings.
    /// </summary>
    [Serializable]
    public struct TAASettings
    {
        [SerializeField]
        [FormerlySerializedAs("quality")]
        internal TemporalAAQuality quality;
        [SerializeField]
        [FormerlySerializedAs("frameInfluence")]
        internal float frameInfluence;
        [SerializeField]
        [FormerlySerializedAs("jitterScale")]
        internal float jitterScale;
        [SerializeField]
        [FormerlySerializedAs("mipBias")]
        internal float mipBias;
        [SerializeField]
        [FormerlySerializedAs("varianceClampScale")]
        internal float varianceClampScale;
        [SerializeField]
        [FormerlySerializedAs("contrastAdaptiveSharpening")]
        internal float contrastAdaptiveSharpening;

        // Internal API
        [NonSerialized] internal int resetHistoryFrames;      // Number of frames the history is reset. 0 no reset, 1 normal reset, 2 XR reset, -1 infinite (toggle on)
        [NonSerialized] internal int jitterFrameCountOffset;  // Jitter "seed" == Time.frameCount + jitterFrameCountOffset. Used for testing determinism.

        /// <summary>
        /// Creates a new instance of the settings with default values.
        /// </summary>
        /// <returns>Default settings.</returns>
        public static TAASettings Create()
        {
            TAASettings s;

            s.quality = TemporalAAQuality.High;
            s.frameInfluence = 0.1f;
            s.jitterScale = 1.0f;
            s.mipBias = 0.0f;
            s.varianceClampScale = 0.9f;
            s.contrastAdaptiveSharpening = 0.0f; // Disabled

            s.resetHistoryFrames = 0;
            s.jitterFrameCountOffset = 0;

            return s;
        }
    }

    internal static class TemporalAA
    {
        internal static class ShaderConstants
        {
            public static readonly int _TaaAccumulationTex = Shader.PropertyToID("_TaaAccumulationTex");
            public static readonly int _TaaMotionVectorTex = Shader.PropertyToID("_TaaMotionVectorTex");

            public static readonly int _TaaFilterWeights = Shader.PropertyToID("_TaaFilterWeights");

            public static readonly int _TaaFrameInfluence = Shader.PropertyToID("_TaaFrameInfluence");
            public static readonly int _TaaVarianceClampScale = Shader.PropertyToID("_TaaVarianceClampScale");

            public static readonly int _CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
        }

        internal static class ShaderKeywords
        {
            public static readonly string TAA_LOW_PRECISION_SOURCE = "TAA_LOW_PRECISION_SOURCE";
        }


        internal static int CalculateTaaFrameIndex(ref TAASettings settings)
        {
            // UltraRP supports adding an offset value to the TAA frame index for testing determinism.
            int taaFrameCountOffset = settings.jitterFrameCountOffset;
            return Time.frameCount + taaFrameCountOffset;
        }


        internal delegate void JitterFunc(int frameIndex, out Vector2 jitter, out bool allowScaling);

        internal static Matrix4x4 CalculateJitterMatrix(CameraData cameraData, JitterFunc jitterFunc)
        {
            Matrix4x4 jitterMat = Matrix4x4.identity;

            bool isJitter = cameraData.IsTemporalJitterEnabled();
            if (isJitter)
            {
                int taaFrameIndex = CalculateTaaFrameIndex(ref cameraData.taaSettings);
                jitterFunc(taaFrameIndex, out Vector2 jitter, out bool allowScaling);
                if (allowScaling)
                    jitter *= cameraData.taaSettings.jitterScale;

                float offsetX = jitter.x * (2.0f / cameraData.cameraTargetDescriptor.width);
                float offsetY = jitter.y * (2.0f / cameraData.cameraTargetDescriptor.height);
                jitterMat = Matrix4x4.Translate(new Vector3(offsetX, offsetY, 0.0f));
            }

            return jitterMat;
        }

        // Static allocation of JitterFunc delegate to avoid GC
        internal static JitterFunc jitterFunc = CalculateJitter;

        private static readonly Vector2[] s_taaFilterOffsets = new Vector2[]
{
            new Vector2(0.0f, 0.0f),

            new Vector2(0.0f, 1.0f),
            new Vector2(1.0f, 0.0f),
            new Vector2(-1.0f, 0.0f),
            new Vector2(0.0f, -1.0f),

            new Vector2(-1.0f, 1.0f),
            new Vector2(1.0f, -1.0f),
            new Vector2(1.0f, 1.0f),
            new Vector2(-1.0f, -1.0f)
        };

        private static readonly float[] s_taaFilterWeights = new float[s_taaFilterOffsets.Length + 1];

        static void CalculateJitter(int frameIndex, out Vector2 jitter, out bool allowScaling)
        {
            // The variance between 0 and the actual halton sequence values reveals noticeable
            // instability in Unity's shadow maps, so we avoid index 0.
            float jitterX = HaltonSequence.Get((frameIndex & 1023) + 1, 2) - 0.5f;
            float jitterY = HaltonSequence.Get((frameIndex & 1023) + 1, 3) - 0.5f;

            jitter = new Vector2(jitterX, jitterY);
            allowScaling = true;
        }

        internal static float[] CalculateFilterWeights(ref TAASettings settings)
        {
            int taaFrameIndex = CalculateTaaFrameIndex(ref settings);

            // Based on HDRP
            // Precompute weights used for the Blackman-Harris filter.
            float totalWeight = 0;
            for (int i = 0; i < 9; ++i)
            {
                // The internal jitter function used by TAA always allows scaling
                CalculateJitter(taaFrameIndex, out var jitter, out var _);
                jitter *= settings.jitterScale;

                // The rendered frame (pixel grid) is already jittered.
                // We sample 3x3 neighbors with int offsets, but weight the samples
                // relative to the distance to the non-jittered pixel center.
                // From the POV of offset[0] at (0,0), the original pixel center is at (-jitter.x, -jitter.y).
                float x = s_taaFilterOffsets[i].x - jitter.x;
                float y = s_taaFilterOffsets[i].y - jitter.y;
                float d2 = (x * x + y * y);

                s_taaFilterWeights[i] = Mathf.Exp((-0.5f / (0.22f)) * d2);
                totalWeight += s_taaFilterWeights[i];
            }

            // Normalize weights.
            for (int i = 0; i < 9; ++i)
                s_taaFilterWeights[i] /= totalWeight;

            return s_taaFilterWeights;
        }


        internal static GraphicsFormat[] accumulationFormatList = new GraphicsFormat[]
        {
            GraphicsFormat.R16G16B16A16_SFloat,
            GraphicsFormat.B10G11R11_UFloatPack32,
            GraphicsFormat.R8G8B8A8_UNorm,
            GraphicsFormat.B8G8R8A8_UNorm,
        };

        internal static RenderTextureDescriptor TemporalAADescFromCameraDesc(ref RenderTextureDescriptor cameraDesc)
        {
            RenderTextureDescriptor taaDesc = cameraDesc;

            // Explicitly set from cameraDesc.* for clarity.
            taaDesc.width = cameraDesc.width;
            taaDesc.height = cameraDesc.height;
            taaDesc.msaaSamples = 1;
            taaDesc.volumeDepth = cameraDesc.volumeDepth;
            taaDesc.mipCount = 0;
            taaDesc.graphicsFormat = cameraDesc.graphicsFormat;
            taaDesc.sRGB = false;
            taaDesc.depthStencilFormat = GraphicsFormat.None;
            taaDesc.dimension = cameraDesc.dimension;
            taaDesc.vrUsage = cameraDesc.vrUsage;
            taaDesc.memoryless = RenderTextureMemoryless.None;
            taaDesc.useMipMap = false;
            taaDesc.autoGenerateMips = false;
            taaDesc.enableRandomWrite = false;
            taaDesc.bindMS = false;
            taaDesc.useDynamicScale = false;

            if (!GraphicsFormatCompatibility.IsFormatSupported(taaDesc.graphicsFormat, GraphicsFormatUsage.Render))
            {
                taaDesc.graphicsFormat = GraphicsFormat.None;
                for (int i = 0; i < accumulationFormatList.Length; i++)
                    if (GraphicsFormatCompatibility.IsFormatSupported(accumulationFormatList[i], GraphicsFormatUsage.Render))
                    {
                        taaDesc.graphicsFormat = accumulationFormatList[i];
                        break;
                    }
            }

            return taaDesc;
        }

        static uint s_warnCounter = 0;

        internal static string ValidateAndWarn(CameraData cameraData, bool isSTPRequested = false)
        {
            string reasonWarning = null;

            if (reasonWarning == null && !cameraData.postProcessEnabled)
                reasonWarning = "because camera has post-processing disabled.";

            if (cameraData.taaHistory == null)
                reasonWarning = "due to invalid persistent data.";

            if (reasonWarning == null && cameraData.cameraTargetDescriptor.msaaSamples != 1)
                reasonWarning = "because MSAA is on. Turn MSAA off on the camera or current UltraRP Asset.";

            if (reasonWarning == null && cameraData.camera.TryGetComponent<UltraAdditionalCameraData>(out var additionalCameraData))
            {
                if (additionalCameraData.renderType == CameraRenderType.Overlay ||
                    additionalCameraData.cameraStack.Count > 0)
                {
                    reasonWarning = "because camera is stacked.";
                }
            }

            if (reasonWarning == null && cameraData.camera.allowDynamicResolution)
                reasonWarning = "because camera has dynamic resolution enabled. You can use a constant render scale instead.";

            if (reasonWarning == null && !cameraData.renderer.SupportsMotionVectors())
                reasonWarning = "because the renderer does not implement motion vectors. Motion vectors are required.";

            if (reasonWarning == null &&
                (!(cameraData.renderer is UltraRenderer ultraRenderer) || !ultraRenderer.SupportsTemporalAA()))
                reasonWarning = "because the renderer has no enabled TAA pass in its main post-processing path.";

            const int warningThrottleFrames = 60 * 1; // 60 FPS * 1 sec
            if (s_warnCounter % warningThrottleFrames == 0)
                Debug.LogWarning("Disabling TAA " + (isSTPRequested ? "and STP " : "") + reasonWarning);

            s_warnCounter++;

            return reasonWarning;
        }

        private class TaaPassData
        {
            internal TextureHandle dstTex;
            internal TextureHandle srcColorTex;
            internal TextureHandle srcDepthTex;
            internal TextureHandle srcMotionVectorTex;
            internal TextureHandle srcTaaAccumTex;

            internal Material material;
            internal int passIndex;

            internal float taaFrameInfluence;
            internal float taaVarianceClampScale;
            internal float[] taaFilterWeights;

            internal bool taaLowPrecisionSource;
            internal bool taaAlphaOutput;
        }

        internal static void Render(RenderGraph renderGraph, Material taaMaterial, CameraData cameraData, ref TextureHandle srcColor, ref TextureHandle srcDepth, ref TextureHandle srcMotionVectors, ref TextureHandle dstColor)
        {
            int multipassId = 0;

            ref var taa = ref cameraData.taaSettings;

            bool isNewFrame = cameraData.taaHistory.GetAccumulationVersion(multipassId) != Time.frameCount;
            float taaInfluence = taa.resetHistoryFrames == 0 ? taa.frameInfluence : 1.0f;

            RTHandle accumulationTexture = cameraData.taaHistory.GetAccumulationTexture(multipassId);
            TextureHandle srcAccumulation = renderGraph.ImportTexture(accumulationTexture);

            // On frame rerender or pause, stop all motion using a black motion texture.
            // This is done to avoid blurring the Taa resolve due to motion and Taa history mismatch.
            // The TAA history was updated for the next frame, as we did not know yet that we're going render this frame again.
            // We would need to keep the both the current and previous history (double buffering) in order to resolve
            // either this frame (again) or the next frame correctly, but it would cost more memory.
            TextureHandle activeMotionVectors = isNewFrame ? srcMotionVectors : renderGraph.defaultResources.blackTexture;
            using (var builder = renderGraph.AddRasterRenderPass<TaaPassData>("Temporal Anti-aliasing", out var passData, ProfilingSampler.Get(UltraRPProfileId.RG_TAA)))
            {
                passData.dstTex = dstColor;
                builder.SetRenderAttachment(dstColor, 0, AccessFlags.Write);
                passData.srcColorTex = srcColor;
                builder.UseTexture(srcColor, AccessFlags.Read);
                passData.srcDepthTex = srcDepth;
                builder.UseTexture(srcDepth, AccessFlags.Read);
                passData.srcMotionVectorTex = activeMotionVectors;
                builder.UseTexture(activeMotionVectors, AccessFlags.Read);
                passData.srcTaaAccumTex = srcAccumulation;
                builder.UseTexture(srcAccumulation, AccessFlags.Read);

                passData.material = taaMaterial;
                passData.passIndex = (int)taa.quality;

                passData.taaFrameInfluence = taaInfluence;
                passData.taaVarianceClampScale = taa.varianceClampScale;

                if (taa.quality == TemporalAAQuality.VeryHigh)
                    passData.taaFilterWeights = CalculateFilterWeights(ref taa);
                else
                    passData.taaFilterWeights = null;

                switch (accumulationTexture.rt.graphicsFormat)
                {
                    // Avoid precision issues with YCoCg and low bit color formats.
                    case GraphicsFormat.B10G11R11_UFloatPack32:
                    case GraphicsFormat.R8G8B8A8_UNorm:
                    case GraphicsFormat.B8G8R8A8_UNorm:
                        passData.taaLowPrecisionSource = true;
                        break;
                    default:
                        passData.taaLowPrecisionSource = false;
                        break;
                }

                passData.taaAlphaOutput = cameraData.isAlphaOutputEnabled;

                builder.SetRenderFunc(static (TaaPassData data, RasterGraphContext context) =>
                {
                    data.material.SetFloat(ShaderConstants._TaaFrameInfluence, data.taaFrameInfluence);
                    data.material.SetFloat(ShaderConstants._TaaVarianceClampScale, data.taaVarianceClampScale);
                    data.material.SetTexture(ShaderConstants._TaaAccumulationTex, data.srcTaaAccumTex);
                    data.material.SetTexture(ShaderConstants._TaaMotionVectorTex, data.srcMotionVectorTex);
                    data.material.SetTexture(ShaderConstants._CameraDepthTexture, data.srcDepthTex);
                    CoreUtils.SetKeyword(data.material, ShaderKeywords.TAA_LOW_PRECISION_SOURCE, data.taaLowPrecisionSource);
                    CoreUtils.SetKeyword(data.material, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, data.taaAlphaOutput);

                    if (data.taaFilterWeights != null)
                        data.material.SetFloatArray(ShaderConstants._TaaFilterWeights, data.taaFilterWeights);

                    Blitter.BlitTexture(context.cmd, data.srcColorTex, Vector2.one, data.material, data.passIndex);
                });
            }

            if (isNewFrame)
            {
                int kHistoryCopyPass = taaMaterial.shader.passCount - 1;
                using (var builder = renderGraph.AddRasterRenderPass<TaaPassData>("Temporal Anti-aliasing Copy History", out var passData, ProfilingSampler.Get(UltraRPProfileId.RG_TAACopyHistory)))
                {
                    passData.dstTex = srcAccumulation;
                    builder.SetRenderAttachment(srcAccumulation, 0, AccessFlags.Write);
                    passData.srcColorTex = dstColor;
                    builder.UseTexture(dstColor, AccessFlags.Read);   // Resolved color is the new history

                    passData.material = taaMaterial;
                    passData.passIndex = kHistoryCopyPass;

                    builder.SetRenderFunc((TaaPassData data, RasterGraphContext context) => { Blitter.BlitTexture(context.cmd, data.srcColorTex, Vector2.one, data.material, data.passIndex); });
                }

                cameraData.taaHistory.SetAccumulationVersion(multipassId, Time.frameCount);
            }
        }
    }

}
