using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    internal static class SgsrUpscaling
    {
        static readonly ProfilingSampler s_spatialSampler = new ProfilingSampler("SGSR1 Upscale");
        static readonly ProfilingSampler s_temporalSampler = new ProfilingSampler("SGSR2 Upscale");

        class BlitPassData
        {
            internal TextureHandle source;
            internal Material material;
        }

        class ConvertPassData
        {
            internal TextureHandle source;
            internal TextureHandle depth;
            internal TextureHandle motion;
            internal Material material;
            internal TAAU_SGSR2.Parameters parameters;
        }

        class UpscalePassData
        {
            internal TextureHandle source;
            internal TextureHandle conversion;
            internal TextureHandle history;
            internal Material material;
            internal TAAU_SGSR2.Parameters parameters;
        }

        internal static void RenderSpatial(RenderGraph renderGraph, Material material,
            TextureHandle source, TextureHandle destination)
        {
            using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>(
                "SGSR1 Upscale", out var passData, s_spatialSampler))
            {
                passData.source = source;
                passData.material = material;
                builder.UseTexture(source, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (BlitPassData data, RasterGraphContext context) =>
                {
                    RTHandle sourceHandle = data.source;
                    PostProcessUtils.SetSourceSize(context.cmd, sourceHandle);
                    Vector2 scale = sourceHandle.useScaling
                        ? new Vector2(sourceHandle.rtHandleProperties.rtHandleScale.x, sourceHandle.rtHandleProperties.rtHandleScale.y)
                        : Vector2.one;
                    Blitter.BlitTexture(context.cmd, sourceHandle, scale, data.material, 0);
                });
            }
        }

        internal static bool RenderTemporal(RenderGraph renderGraph, Material material,
            CameraData cameraData, ResourceData resources, TextureHandle source, TextureHandle destination)
        {
            var history = cameraData.sgsr2History;
            if (!cameraData.IsSGSR2Enabled() || !TAAU_SGSR2.IsSupported(material, history) ||
                !resources.cameraDepthTexture.IsValid() || !resources.motionVectorColor.IsValid())
                return false;

            int eyeIndex = TAAU_SGSR2.GetEyeIndex(cameraData);
            RTHandle currentHistoryHandle = history.GetCurrentTexture(eyeIndex);
            RTHandle previousHistoryHandle = history.GetPreviousTexture(eyeIndex);
            if (currentHistoryHandle == null || previousHistoryHandle == null)
                return false;

            var parameters = TAAU_SGSR2.BuildParameters(cameraData, history, eyeIndex, true);
            var conversionDesc = PostProcessPass.GetCompatibleDescriptor(cameraData.cameraTargetDescriptor,
                cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height,
                GraphicsFormat.R16G16B16A16_SFloat);
            // Signed motion vectors require a floating-point target; support is checked before camera jitter.
            var conversion = UltraRenderer.CreateRenderGraphTexture(renderGraph, conversionDesc,
                "_Sgsr2MotionDepthTexture", false, FilterMode.Point);

            using (var builder = renderGraph.AddRasterRenderPass<ConvertPassData>(
                "SGSR2 Convert", out var passData, s_temporalSampler))
            {
                passData.source = source;
                passData.depth = resources.cameraDepthTexture;
                passData.motion = resources.motionVectorColor;
                passData.material = material;
                passData.parameters = parameters;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(passData.depth, AccessFlags.Read);
                builder.UseTexture(passData.motion, AccessFlags.Read);
                builder.SetRenderAttachment(conversion, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (ConvertPassData data, RasterGraphContext context) =>
                {
                    TAAU_SGSR2.SetParameters(data.material, data.parameters);
                    data.material.SetTexture(TAAU_SGSR2.ShaderConstants.CameraDepthTexture, data.depth);
                    data.material.SetTexture(TAAU_SGSR2.ShaderConstants.MotionVectorTexture, data.motion);
                    Blitter.BlitTexture(context.cmd, data.source, Vector2.one, data.material, TAAU_SGSR2.ConvertPass);
                });
            }

            TextureHandle previousHistory = renderGraph.ImportTexture(previousHistoryHandle);
            using (var builder = renderGraph.AddRasterRenderPass<UpscalePassData>(
                "SGSR2 Upscale", out var passData, s_temporalSampler))
            {
                passData.source = source;
                passData.conversion = conversion;
                passData.history = previousHistory;
                passData.material = material;
                passData.parameters = parameters;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(conversion, AccessFlags.Read);
                builder.UseTexture(previousHistory, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (UpscalePassData data, RasterGraphContext context) =>
                {
                    TAAU_SGSR2.SetParameters(data.material, data.parameters);
                    data.material.SetTexture(TAAU_SGSR2.ShaderConstants.HistoryTexture, data.history);
                    data.material.SetTexture(TAAU_SGSR2.ShaderConstants.DepthClipTexture, data.conversion);
                    Blitter.BlitTexture(context.cmd, data.source, Vector2.one, data.material, TAAU_SGSR2.UpscalePass);
                });
            }

            TextureHandle currentHistory = renderGraph.ImportTexture(currentHistoryHandle);
            using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>(
                "SGSR2 Copy History", out var passData, s_temporalSampler))
            {
                passData.source = destination;
                passData.material = material;
                builder.UseTexture(destination, AccessFlags.Read);
                builder.SetRenderAttachment(currentHistory, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (BlitPassData data, RasterGraphContext context) =>
                    Blitter.BlitTexture(context.cmd, data.source, Vector2.one, data.material, TAAU_SGSR2.CopyHistoryPass));
            }

            history.SetAccumulationVersion(eyeIndex, Time.frameCount);
            history.SetPreviousViewProjection(eyeIndex, TAAU_SGSR2.GetNonJitteredViewProjection(cameraData, eyeIndex));
            return true;
        }
    }
}
