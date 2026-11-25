using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class HDRDebugViewPass
    {
        private enum HDRDebugPassId
        {
            CIExyPrepass = 0,
            DebugViewPass = 1
        }

        internal class ShaderConstants
        {
            public static readonly int _DebugHDRModeId = Shader.PropertyToID("_DebugHDRMode");
            public static readonly int _HDRDebugParamsId = Shader.PropertyToID("_HDRDebugParams");
            public static readonly int _xyTextureId = Shader.PropertyToID("_xyBuffer");
            public static readonly int _SizeOfHDRXYMapping = 512;
            public static readonly int _CIExyUAVIndex = 1;
        }

        // Common to RenderGraph and non-RenderGraph paths
        private class PassDataCIExy
        {
            internal Material material;
            internal Vector4 luminanceParameters;
            internal TextureHandle srcColor;
            internal TextureHandle xyBuffer;
            internal TextureHandle passThrough;
        }

        private class PassDataDebugView
        {
            internal Material material;
            internal HDRDebugMode hdrDebugMode;
            internal CameraData cameraData;
            internal Vector4 luminanceParameters;
            internal TextureHandle overlayUITexture;
            internal TextureHandle xyBuffer;
            internal TextureHandle srcColor;
            internal TextureHandle dstColor;
        }

        PassDataCIExy _passDataCIExy;
        PassDataDebugView _passDataDebugView;
        RTHandle _ciexyTarget;     // xyBuffer;
        RTHandle _passthroughRT;
        Material _material;

        private ProfilingSampler _profilingSampler;

        public HDRDebugViewPass(Material mat)
        {
            _profilingSampler = new ProfilingSampler("Blit HDR Debug Data");
            _passDataCIExy = new PassDataCIExy() { material = mat };
            _passDataDebugView = new PassDataDebugView() { material = mat };
            _material = mat;
        }

        public void Dispose()
        {
            _ciexyTarget?.Release();
            _passthroughRT?.Release();
        }

        public static void ConfigureDescriptorForCIEPrepass(ref RenderTextureDescriptor descriptor)
        {
            descriptor.graphicsFormat = GraphicsFormat.R32_SFloat;
            descriptor.width = descriptor.height = ShaderConstants._SizeOfHDRXYMapping;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.useDynamicScale = true;
            descriptor.depthStencilFormat = GraphicsFormat.None;
            descriptor.enableRandomWrite = true;
            descriptor.msaaSamples = 1;
            descriptor.dimension = TextureDimension.Tex2D;
            descriptor.vrUsage = VRTextureUsage.None; // We only need one for both eyes in VR
        }

        internal static Vector4 GetLuminanceParameters(CameraData cameraData)
        {
            var luminanceParams = Vector4.zero;
            if (cameraData.isHDROutputActive)
            {
                Tonemapping tonemapping = VolumeManager.instance.stack.GetComponent<Tonemapping>();
                UltraRenderPipeline.GetHDROutputLuminanceParameters(cameraData.hdrDisplayInformation, cameraData.hdrDisplayColorGamut, tonemapping, out luminanceParams);
            }
            else
                luminanceParams.z = 1.0f;

            return luminanceParams;
        }

        public void Setup(CameraData cameraData, HDRDebugMode hdrdebugMode)
        {
            _passDataDebugView.hdrDebugMode = hdrdebugMode;

            RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
            DebugHandler.ConfigureColorDescriptorForDebugScreen(ref descriptor, cameraData.pixelWidth, cameraData.pixelHeight);
            RenderingUtils.ReAllocateHandleIfNeeded(ref _passthroughRT, descriptor, name: "_HDRDebugDummyRT");

            RenderTextureDescriptor descriptorCIE = cameraData.cameraTargetDescriptor;
            HDRDebugViewPass.ConfigureDescriptorForCIEPrepass(ref descriptorCIE);
            RenderingUtils.ReAllocateHandleIfNeeded(ref _ciexyTarget, descriptorCIE, name: "_xyBuffer");
        }

        // RenderGraph path
        internal void RenderHDRDebug(RenderGraph renderGraph, CameraData cameraData, TextureHandle srcColor, TextureHandle overlayUITexture, TextureHandle dstColor, HDRDebugMode hdrDebugMode)
        {
            bool requiresCIExyData = hdrDebugMode != HDRDebugMode.ValuesAbovePaperWhite;
            Vector4 luminanceParameters = GetLuminanceParameters(cameraData);

            TextureHandle intermediateRT = srcColor;
            TextureHandle xyBuffer = TextureHandle.nullHandle;

            if (requiresCIExyData)
            {
                RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
                DebugHandler.ConfigureColorDescriptorForDebugScreen(ref descriptor, cameraData.pixelWidth, cameraData.pixelHeight);
                intermediateRT = UltraRenderer.CreateRenderGraphTexture(renderGraph, descriptor, "_HDRDebugDummyRT", false);

                ConfigureDescriptorForCIEPrepass(ref descriptor);
                xyBuffer = UltraRenderer.CreateRenderGraphTexture(renderGraph, descriptor, "_xyBuffer", true);

                // Using low level pass because of random UAV support, and since this is a debug view, we don't care much about merging passes or optimizing for TBDR.
                // This could be a compute pass (like in HDRP) but doing it in pixel is compatible with devices that might support HDR output but not compute shaders.
                using (var builder = renderGraph.AddUnsafePass<PassDataCIExy>("Blit HDR DebugView CIExy", out var passData, _profilingSampler))
                {
                    passData.material = _material;
                    passData.luminanceParameters = luminanceParameters;
                    passData.srcColor = srcColor;
                    builder.UseTexture(srcColor);
                    passData.xyBuffer = xyBuffer;
                    builder.UseTexture(xyBuffer, AccessFlags.Write);
                    passData.passThrough = intermediateRT;
                    builder.UseTexture(intermediateRT, AccessFlags.Write);

                    builder.SetRenderFunc((PassDataCIExy data, UnsafeGraphContext context) =>
                    {
                        ExecuteCIExyPrepass(CommandBufferHelpers.GetNativeCommandBuffer(context.cmd), data, data.srcColor, data.xyBuffer, data.passThrough);
                    });
                }
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassDataDebugView>("Blit HDR DebugView", out var passData, _profilingSampler))
            {
                passData.material = _material;
                passData.hdrDebugMode = hdrDebugMode;
                passData.luminanceParameters = luminanceParameters;
                passData.cameraData = cameraData;

                if (requiresCIExyData)
                {
                    passData.xyBuffer = xyBuffer;
                    builder.UseTexture(xyBuffer);
                }

                passData.srcColor = srcColor;
                builder.UseTexture(srcColor);
                passData.dstColor = dstColor;
                builder.SetRenderAttachment(dstColor, 0, AccessFlags.WriteAll);

                if (overlayUITexture.IsValid())
                {
                    passData.overlayUITexture = overlayUITexture;
                    builder.UseTexture(overlayUITexture);
                }

                builder.SetRenderFunc((PassDataDebugView data, RasterGraphContext context) =>
                {
                    data.material.enabledKeywords = null;
                    ExecuteHDRDebugViewFinalPass(context.cmd, data, data.srcColor, data.dstColor, data.xyBuffer);
                });
            }
        }

        private static void ExecuteCIExyPrepass(CommandBuffer cmd, PassDataCIExy data, RTHandle sourceTexture, RTHandle xyTarget, RTHandle destTexture)
        {
            CoreUtils.SetRenderTarget(cmd, destTexture, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare, ClearFlag.None, Color.clear);

            Vector4 debugParameters = new Vector4(ShaderConstants._SizeOfHDRXYMapping, ShaderConstants._SizeOfHDRXYMapping, 0, 0);

            cmd.SetRandomWriteTarget(ShaderConstants._CIExyUAVIndex, xyTarget);
            data.material.SetVector(ShaderConstants._HDRDebugParamsId, debugParameters);
            data.material.SetVector(ShaderPropertyId.hdrOutputLuminanceParams, data.luminanceParameters);

            Vector2 viewportScale = sourceTexture.useScaling ? new Vector2(sourceTexture.rtHandleProperties.rtHandleScale.x, sourceTexture.rtHandleProperties.rtHandleScale.y) : Vector2.one;
            Blitter.BlitTexture(cmd, sourceTexture, viewportScale, data.material, 0);

            cmd.ClearRandomWriteTargets();
        }

        private static void ExecuteHDRDebugViewFinalPass(RasterCommandBuffer cmd, PassDataDebugView data, RTHandle sourceTexture, RTHandle destination, RTHandle xyTarget)
        {
            if (data.cameraData.isHDROutputActive)
            {
                HDROutputUtils.ConfigureHDROutput(data.material, data.cameraData.hdrDisplayColorGamut, HDROutputUtils.Operation.ColorEncoding);
                CoreUtils.SetKeyword(data.material, ShaderKeywordStrings.HDROverlay, data.cameraData.rendersOverlayUI);
            }

            data.material.SetTexture(ShaderConstants._xyTextureId, xyTarget);

            Vector4 debugParameters = new Vector4(ShaderConstants._SizeOfHDRXYMapping, ShaderConstants._SizeOfHDRXYMapping, 0, 0);
            data.material.SetVector(ShaderConstants._HDRDebugParamsId, debugParameters);
            data.material.SetVector(ShaderPropertyId.hdrOutputLuminanceParams, data.luminanceParameters);
            data.material.SetInteger(ShaderConstants._DebugHDRModeId, (int)data.hdrDebugMode);

            Vector4 scaleBias = RenderingUtils.GetFinalBlitScaleBias(sourceTexture, destination, data.cameraData);

            RenderTargetIdentifier cameraTarget = BuiltinRenderTextureType.CameraTarget;
#if FRAXINUSRP_ENABLE_XR && ENABLE_VR && ENABLE_XR_MODULE
                if (data.cameraData.xr.enabled)
                    cameraTarget = data.cameraData.xr.renderTarget;
#endif

            if (destination.nameID == cameraTarget || data.cameraData.targetTexture != null)
                cmd.SetViewport(data.cameraData.pixelRect);

            Blitter.BlitTexture(cmd, sourceTexture, scaleBias, data.material, 1);
        }
    }
}
