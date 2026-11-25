
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    public class FinalBlitPass : IDisposable
    {
        enum BlitType
        {
            Core = 0, // Core blit
            HDR = 1, // Blit with HDR encoding and overlay UI compositing
            Count = 2
        }

        struct BlitMaterialData
        {
            public Material material;
            public int nearestSamplerPass;
            public int bilinearSamplerPass;
        }

        private class PassData
        {
            internal TextureHandle source;
            internal TextureHandle destination;
            internal int sourceID;
            internal Vector4 hdrOutputLuminanceParams;
            internal bool requireSrgbConversion;
            internal bool enableAlphaOutput;
            internal BlitMaterialData blitMaterialData;
            internal CameraData cameraData;
        }

        static class BlitPassNames
        {
            public const string nearestSampler = "NearestDebugDraw";
            public const string bilinearSampler = "BilinearDebugDraw";
        }

        string _passName;
        ProfilingSampler _profilingSampler;

        static readonly int s_cameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");

        BlitMaterialData[] _blitMaterialData;

        public FinalBlitPass()
        {
            _passName = this.GetType().Name;
            _profilingSampler = new ProfilingSampler(this.GetType().Name);
        }

        private void EnsureMaterial(BlitType blitType)
        {
            _blitMaterialData ??= new BlitMaterialData[(int)BlitType.Count];
            if (_blitMaterialData[(int)blitType].material != null)
                return;

            // Graph assets can be enabled while another pipeline is active, including during a domain reload.
            // Resolve the resource only when this pass is actually recorded by the UltraRP renderer.
            if (!RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>(
                    out var shadersResources))
                throw new InvalidOperationException("FinalBlitPass requires UltraRP runtime shader resources when rendering.");

            Shader shader = blitType == BlitType.Core ? shadersResources.coreBlitPS : shadersResources.blitHDROverlay;
            if (shader == null)
                throw new InvalidOperationException($"FinalBlitPass requires the {blitType} blit shader in UltraRP runtime shader resources.");

            Material material = CoreUtils.CreateEngineMaterial(shader);
            _blitMaterialData[(int)blitType] = new BlitMaterialData
            {
                material = material,
                nearestSamplerPass = material.FindPass(BlitPassNames.nearestSampler),
                bilinearSamplerPass = material.FindPass(BlitPassNames.bilinearSampler)
            };
        }

        public void Dispose()
        {
            if (_blitMaterialData != null)
            {
                foreach (var data in _blitMaterialData)
                    CoreUtils.Destroy(data.material);

                _blitMaterialData = null;
            }
        }


        public void Render(RenderGraph renderGraph, ContextContainer frameData, CameraData cameraData, in TextureHandle src, in TextureHandle dest, TextureHandle overlayUITexture)
        {
            BlitType blitType = cameraData.isHDROutputActive ? BlitType.HDR : BlitType.Core;
            EnsureMaterial(blitType);
            using (var builder = renderGraph.AddRasterRenderPass<PassData>(_passName, out var passData, _profilingSampler))
            {
                ResourceData resourceData = frameData.Get<ResourceData>();

                //if (cameraData.requiresDepthTexture)
                //    builder.UseGlobalTexture(s_cameraDepthTextureID);

                bool outputsToHDR = cameraData.isHDROutputActive;
                bool outputsAlpha = cameraData.isAlphaOutputEnabled;
                InitPassData(cameraData, ref passData, blitType, outputsAlpha);

                passData.sourceID = ShaderPropertyId.sourceTex;
                passData.source = src;
                builder.UseTexture(src, AccessFlags.Read);
                passData.destination = dest;
                builder.SetRenderAttachment(dest, 0, AccessFlags.Write);

                if (outputsToHDR && overlayUITexture.IsValid())
                {
                    VolumeStack stack = VolumeManager.instance.stack;
                    Tonemapping tonemapping = stack.GetComponent<Tonemapping>();
                    UltraRenderPipeline.GetHDROutputLuminanceParameters(passData.cameraData.hdrDisplayInformation, passData.cameraData.hdrDisplayColorGamut, tonemapping, out passData.hdrOutputLuminanceParams);

                    builder.UseTexture(overlayUITexture, AccessFlags.Read);
                }
                else
                    passData.hdrOutputLuminanceParams = new Vector4(-1.0f, -1.0f, -1.0f, -1.0f);

                passData.hdrOutputLuminanceParams = new Vector4(-1.0f, -1.0f, -1.0f, -1.0f);

                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    // Early out if material is not available
                    if (data.blitMaterialData.material == null)
                    {
                        Debug.LogError("FinalBlitPass: Blit material is null. Cannot perform final blit. Please check shader resources configuration.");
                        return;
                    }

                    data.blitMaterialData.material.enabledKeywords = null;

                    context.cmd.SetKeyword(ShaderGlobalKeywords.LinearToSRGBConversion, data.requireSrgbConversion);
                    data.blitMaterialData.material.SetTexture(data.sourceID, data.source);

                    DebugHandler debugHandler =  ScriptableRenderPassNode.GetActiveDebugHandler(data.cameraData);
                    bool resolveToDebugScreen = debugHandler != null && debugHandler.WriteToDebugScreenTexture(data.cameraData.resolveFinalTarget);

                    // TODO RENDERGRAPH: this should ideally be shared in ExecutePass to avoid code duplication
                    if (data.hdrOutputLuminanceParams.w >= 0)
                    {
                        HDROutputUtils.Operation hdrOperation = HDROutputUtils.Operation.None;
                        // If the HDRDebugView is on, we don't want the encoding
                        if (debugHandler == null || !debugHandler.HDRDebugViewIsActive(data.cameraData.resolveFinalTarget))
                            hdrOperation |= HDROutputUtils.Operation.ColorEncoding;

                        // Color conversion may have happened in the Uber post process through color grading, so we don't want to reapply it
                        if (!data.cameraData.postProcessEnabled)
                            hdrOperation |= HDROutputUtils.Operation.ColorConversion;

                        SetupHDROutput(data.cameraData.hdrDisplayColorGamut, data.blitMaterialData.material, hdrOperation, data.hdrOutputLuminanceParams, data.cameraData.rendersOverlayUI);
                    }

                    if (resolveToDebugScreen)
                    {
                        RTHandle sourceTex = data.source;
                        Vector2 viewportScale = sourceTex.useScaling ? new Vector2(sourceTex.rtHandleProperties.rtHandleScale.x, sourceTex.rtHandleProperties.rtHandleScale.y) : Vector2.one;

                        int shaderPassIndex = sourceTex.rt?.filterMode == FilterMode.Bilinear ? data.blitMaterialData.bilinearSamplerPass : data.blitMaterialData.nearestSamplerPass;
                        Blitter.BlitTexture(context.cmd, sourceTex, viewportScale, data.blitMaterialData.material, shaderPassIndex);
                    }
                    else
                        ExecutePass(context.cmd, data, data.source, data.destination, data.cameraData);
                });
            }
        }

        private void InitPassData(CameraData cameraData, ref PassData passData, BlitType blitType, bool enableAlphaOutput)
        {
            passData.cameraData = cameraData;
            passData.requireSrgbConversion = cameraData.requireSrgbConversion;
            passData.enableAlphaOutput = enableAlphaOutput;

            passData.blitMaterialData = _blitMaterialData[(int)blitType];
        }

        static void ExecutePass(RasterCommandBuffer cmd, PassData data, RTHandle source, RTHandle destination, CameraData cameraData)
        {
            bool isRenderToBackBufferTarget = !cameraData.isSceneViewCamera;
            Vector4 scaleBias = RenderingUtils.GetFinalBlitScaleBias(source, destination, cameraData);
            if (isRenderToBackBufferTarget)
                cmd.SetViewport(cameraData.pixelRect);

            // turn off any global wireframe & "scene view wireframe shader hijack" settings for doing blits:
            // we never want them to show up as wireframe
            cmd.SetWireframe(false);

            CoreUtils.SetKeyword(data.blitMaterialData.material, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, data.enableAlphaOutput);

            int shaderPassIndex = source.rt?.filterMode == FilterMode.Bilinear ? data.blitMaterialData.bilinearSamplerPass : data.blitMaterialData.nearestSamplerPass;
            Blitter.BlitTexture(cmd, source, scaleBias, data.blitMaterialData.material, shaderPassIndex);
        }

        static void SetupHDROutput(ColorGamut hdrDisplayColorGamut, Material material, HDROutputUtils.Operation hdrOperation, Vector4 hdrOutputParameters, bool rendersOverlayUI)
        {
            material.SetVector(ShaderPropertyId.hdrOutputLuminanceParams, hdrOutputParameters);
            HDROutputUtils.ConfigureHDROutput(material, hdrDisplayColorGamut, hdrOperation);
            CoreUtils.SetKeyword(material, ShaderKeywordStrings.HDROverlay, rendersOverlayUI);
        }
    }
}
