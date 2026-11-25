using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline.Profiling.Pipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    public class DepthOfFieldPass : BasePostPass
    {
        static class ShaderConstants
        {
            public static readonly int _FullCoCTexture = Shader.PropertyToID("_FullCoCTexture");
            public static readonly int _HalfCoCTexture = Shader.PropertyToID("_HalfCoCTexture");
            public static readonly int _DofTexture = Shader.PropertyToID("_DofTexture");
            public static readonly int _CoCParams = Shader.PropertyToID("_CoCParams");
            public static readonly int _BokehKernel = Shader.PropertyToID("_BokehKernel");
            public static readonly int _BokehConstants = Shader.PropertyToID("_BokehConstants");
            public static readonly int _PongTexture = Shader.PropertyToID("_PongTexture");
            public static readonly int _PingTexture = Shader.PropertyToID("_PingTexture");

            public static readonly int _ColorTexture = Shader.PropertyToID("_ColorTexture");
            public static readonly int _DownSampleScaleFactor = Shader.PropertyToID("_DownSampleScaleFactor");

            public static readonly int _CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
        }

        // Depth Of Field shader passes
        const int k_gaussianDoFPassComputeCoc = 0;
        const int k_gaussianDoFPassDownscalePrefilter = 1;
        const int k_gaussianDoFPassBlurH = 2;
        const int k_gaussianDoFPassBlurV = 3;
        const int k_gaussianDoFPassComposite = 4;

        const int k_bokehDoFPassComputeCoc = 0;
        const int k_bokehDoFPassDownscalePrefilter = 1;
        const int k_bokehDoFPassBlur = 2;
        const int k_bokehDoFPassPostFilter = 3;
        const int k_bokehDoFPassComposite = 4;

        DepthOfField _depthOfField;

        Vector4[] _bokehKernel;
        int _bokehHash;
        // Needed if the device changes its render target width/height (ex, Mobile platform allows change of orientation)
        float _bokehMaxRadius;
        float _bokehRCPAspect;

        private GraphicsFormat _gaussianCoCFormat;

        public DepthOfFieldPass() : base() 
        {
            // UUM-41070: We require `Linear | Render` but with the deprecated FormatUsage this was checking `Blend`
            // For now, we keep checking for `Blend` until the performance hit of doing the correct checks is evaluated
            if (RenderingUtils.SupportsGraphicsFormat(GraphicsFormat.R16_UNorm, GraphicsFormatUsage.Blend))
                _gaussianCoCFormat = GraphicsFormat.R16_UNorm;
            else if (RenderingUtils.SupportsGraphicsFormat(GraphicsFormat.R16_SFloat, GraphicsFormatUsage.Blend))
                _gaussianCoCFormat = GraphicsFormat.R16_SFloat;
            else // Expect CoC banding
                _gaussianCoCFormat = GraphicsFormat.R8_UNorm;
        }

        public override ScriptableRenderPassInput renderInput => ScriptableRenderPassInput.Depth;


        public override bool Setup(bool enable, VolumeStack stack)
        {
            _depthOfField = stack.GetComponent<DepthOfField>();
            return enable && _depthOfField.IsActive();
        }

        public override TextureHandle Render(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle activeCameraColor)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();

            bool isSceneViewCamera = cameraData.isSceneViewCamera;

            TextureHandle currentSource = activeCameraColor;
            if (!isSceneViewCamera)
            {
                RenderDoF(renderGraph, resourceData, cameraData, in currentSource, out var DoFTarget);
                currentSource = DoFTarget;
            }

            return currentSource;
        }

        protected override void OnDispose()
        {

        }

        private void RenderDoF(RenderGraph renderGraph, ResourceData resourceData, CameraData cameraData, in TextureHandle source, out TextureHandle destination)
        {
            var descriptor = _postProcessPass.descriptor;
            var graph = _postProcessPass.graph;

            var dofMaterial = _depthOfField.mode.value == DepthOfFieldMode.Gaussian ?
                               graph.materialLib.GetMaterial("gaussianDepthOfFieldPS") :
                               graph.materialLib.GetMaterial("bokehDepthOfFieldPS");

            var desc = PostProcessPass.GetCompatibleDescriptor(descriptor,
                descriptor.width,
                descriptor.height,
                descriptor.graphicsFormat,
                GraphicsFormat.None);
            destination = UltraRenderer.CreateRenderGraphTexture(renderGraph, desc, "_DoFTarget", true, FilterMode.Bilinear);

            CoreUtils.SetKeyword(dofMaterial, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, cameraData.isAlphaOutputEnabled);

            if (_depthOfField.mode.value == DepthOfFieldMode.Gaussian)
                RenderDoFGaussian(renderGraph, resourceData, cameraData, source, destination, ref dofMaterial);
            else if (_depthOfField.mode.value == DepthOfFieldMode.Bokeh)
                RenderDoFBokeh(renderGraph, resourceData, cameraData, source, destination, ref dofMaterial);
        }

        private class DoFGaussianPassData
        {
            // Setup
            internal int downsample;
            internal RenderingData renderingData;
            internal Vector3 cocParams;
            internal bool highQualitySamplingValue;
            // Inputs
            internal TextureHandle sourceTexture;
            internal TextureHandle depthTexture;
            internal Material material;
            internal Material materialCoC;
            // Pass textures
            internal TextureHandle halfCoCTexture;
            internal TextureHandle fullCoCTexture;
            internal TextureHandle pingTexture;
            internal TextureHandle pongTexture;
            internal RenderTargetIdentifier[] multipleRenderTargets = new RenderTargetIdentifier[2];
            // Output textures
            internal TextureHandle destination;
        };

        public void RenderDoFGaussian(RenderGraph renderGraph, ResourceData resourceData, CameraData cameraData, in TextureHandle source, TextureHandle destination, ref Material dofMaterial)
        {
            var descriptor = _postProcessPass.descriptor;
            var graph = _postProcessPass.graph;

            var material = dofMaterial;
            int downSample = 2;
            int wh = descriptor.width / downSample;
            int hh = descriptor.height / downSample;

            // Pass Textures
            var fullCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(descriptor, descriptor.width, descriptor.height, _gaussianCoCFormat);
            var fullCoCTexture = UltraRenderer.CreateRenderGraphTexture(renderGraph, fullCoCTextureDesc, "_FullCoCTexture", true, FilterMode.Bilinear);
            var halfCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(descriptor, wh, hh, _gaussianCoCFormat);
            var halfCoCTexture = UltraRenderer.CreateRenderGraphTexture(renderGraph, halfCoCTextureDesc, "_HalfCoCTexture", true, FilterMode.Bilinear);
            var pingTextureDesc = PostProcessPass.GetCompatibleDescriptor(descriptor, wh, hh, _postProcessPass.defaultColorFormat);
            var pingTexture = UltraRenderer.CreateRenderGraphTexture(renderGraph, pingTextureDesc, "_PingTexture", true, FilterMode.Bilinear);
            var pongTextureDesc = PostProcessPass.GetCompatibleDescriptor(descriptor, wh, hh, _postProcessPass.defaultColorFormat);
            var pongTexture = UltraRenderer.CreateRenderGraphTexture(renderGraph, pongTextureDesc, "_PongTexture", true, FilterMode.Bilinear);

            using (var builder = renderGraph.AddUnsafePass<DoFGaussianPassData>("Depth of Field - Gaussian", out var passData))
            {
                // Setup
                float farStart = _depthOfField.gaussianStart.value;
                float farEnd = Mathf.Max(farStart, _depthOfField.gaussianEnd.value);

                // Assumes a radius of 1 is 1 at 1080p
                // Past a certain radius our gaussian kernel will look very bad so we'll clamp it for
                // very high resolutions (4K+).
                float maxRadius = _depthOfField.gaussianMaxRadius.value * (wh / 1080f);
                maxRadius = Mathf.Min(maxRadius, 2f);

                passData.downsample = downSample;
                passData.cocParams = new Vector3(farStart, farEnd, maxRadius);
                passData.highQualitySamplingValue = _depthOfField.highQualitySampling.value;

                passData.material = material;
                passData.materialCoC = graph.materialLib.GetMaterial("gaussianDepthOfFieldPS");

                // Inputs
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);

                passData.depthTexture = resourceData.cameraDepthTexture;
                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);

                // Pass Textures
                passData.fullCoCTexture = fullCoCTexture;
                builder.UseTexture(fullCoCTexture, AccessFlags.ReadWrite);

                passData.halfCoCTexture = halfCoCTexture;
                builder.UseTexture(halfCoCTexture, AccessFlags.ReadWrite);

                passData.pingTexture = pingTexture;
                builder.UseTexture(pingTexture, AccessFlags.ReadWrite);

                passData.pongTexture = pongTexture;
                builder.UseTexture(pongTexture, AccessFlags.ReadWrite);

                // Outputs
                passData.destination = destination;
                builder.UseTexture(destination, AccessFlags.Write);

                builder.SetRenderFunc(static (DoFGaussianPassData data, UnsafeGraphContext context) =>
                {
                    var dofMat = data.material;
                    var dofMaterialCoC = data.materialCoC;
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

                    RTHandle sourceTextureHdl = data.sourceTexture;
                    RTHandle dstHdl = data.destination;

                    // Setup
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_SetupDoF)))
                    {
                        dofMat.SetVector(ShaderConstants._CoCParams, data.cocParams);
                        CoreUtils.SetKeyword(dofMat, ShaderKeywordStrings.HighQualitySampling,
                            data.highQualitySamplingValue);

                        dofMaterialCoC.SetVector(ShaderConstants._CoCParams, data.cocParams);
                        CoreUtils.SetKeyword(dofMaterialCoC, ShaderKeywordStrings.HighQualitySampling,
                            data.highQualitySamplingValue);

                        PostProcessUtils.SetSourceSize(cmd, data.sourceTexture);
                        dofMat.SetVector(ShaderConstants._DownSampleScaleFactor,
                            new Vector4(1.0f / data.downsample, 1.0f / data.downsample, data.downsample,
                                data.downsample));
                    }

                    // Compute CoC
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_DOFComputeCOC)))
                    {
                        dofMat.SetTexture(ShaderConstants._CameraDepthTexture, data.depthTexture);
                        Blitter.BlitCameraTexture(cmd, data.sourceTexture, data.fullCoCTexture, data.materialCoC, k_gaussianDoFPassComputeCoc);
                    }

                    // Downscale & prefilter color + CoC
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_DOFDownscalePrefilter)))
                    {
                        dofMat.SetTexture(ShaderConstants._FullCoCTexture, data.fullCoCTexture);

                        // Handle packed shader output
                        data.multipleRenderTargets[0] = data.halfCoCTexture;
                        data.multipleRenderTargets[1] = data.pingTexture;
                        CoreUtils.SetRenderTarget(cmd, data.multipleRenderTargets, data.halfCoCTexture);

                        Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                        Blitter.BlitTexture(cmd, data.sourceTexture, viewportScale, dofMat, k_gaussianDoFPassDownscalePrefilter);
                    }

                    // Blur H
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_DOFBlurH)))
                    {
                        dofMat.SetTexture(ShaderConstants._HalfCoCTexture, data.halfCoCTexture);
                        Blitter.BlitCameraTexture(cmd, data.pingTexture, data.pongTexture, dofMat, k_gaussianDoFPassBlurH);
                    }

                    // Blur V
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_DOFBlurV)))
                    {
                        Blitter.BlitCameraTexture(cmd, data.pongTexture, data.pingTexture, dofMat, k_gaussianDoFPassBlurV);
                    }

                    // Composite
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_DOFComposite)))
                    {
                        dofMat.SetTexture(ShaderConstants._ColorTexture, data.pingTexture);
                        dofMat.SetTexture(ShaderConstants._FullCoCTexture, data.fullCoCTexture);
                        Blitter.BlitCameraTexture(cmd, sourceTextureHdl, dstHdl, dofMat, k_gaussianDoFPassComposite);
                    }
                });
            }
        }

        private class DoFBokehPassData
        {
            // Setup
            internal Vector4[] bokehKernel;
            internal int downSample;
            internal float uvMargin;
            internal Vector4 cocParams;
            internal bool useFastSRGBLinearConversion;
            // Inputs
            internal TextureHandle sourceTexture;
            internal TextureHandle depthTexture;
            internal Material material;
            internal Material materialCoC;
            // Pass textures
            internal TextureHandle halfCoCTexture;
            internal TextureHandle fullCoCTexture;
            internal TextureHandle pingTexture;
            internal TextureHandle pongTexture;
            // Output texture
            internal TextureHandle destination;
        };

        public void RenderDoFBokeh(RenderGraph renderGraph, ResourceData resourceData, CameraData cameraData, in TextureHandle source, in TextureHandle destination, ref Material dofMaterial)
        {
            var descriptor = _postProcessPass.descriptor;
            var graph = _postProcessPass.graph;

            int downSample = 2;
            var material = dofMaterial;
            int wh = descriptor.width / downSample;
            int hh = descriptor.height / downSample;

            // Pass Textures
            var fullCoCTextureDesc = PostProcessPass.GetCompatibleDescriptor(descriptor, descriptor.width, descriptor.height, GraphicsFormat.R8_UNorm);
            var fullCoCTexture = UltraRenderer.CreateRenderGraphTexture(renderGraph, fullCoCTextureDesc, "_FullCoCTexture", true, FilterMode.Bilinear);
            var pingTextureDesc = PostProcessPass.GetCompatibleDescriptor(descriptor, wh, hh, GraphicsFormat.R16G16B16A16_SFloat);
            var pingTexture = UltraRenderer.CreateRenderGraphTexture(renderGraph, pingTextureDesc, "_PingTexture", true, FilterMode.Bilinear);
            var pongTextureDesc = PostProcessPass.GetCompatibleDescriptor(descriptor, wh, hh, GraphicsFormat.R16G16B16A16_SFloat);
            var pongTexture = UltraRenderer.CreateRenderGraphTexture(renderGraph, pongTextureDesc, "_PongTexture", true, FilterMode.Bilinear);

            using (var builder = renderGraph.AddUnsafePass<DoFBokehPassData>("Depth of Field - Bokeh", out var passData))
            {
                // Setup
                // "A Lens and Aperture Camera Model for Synthetic Image Generation" [Potmesil81]
                float F = _depthOfField.focalLength.value / 1000f;
                float A = _depthOfField.focalLength.value / _depthOfField.aperture.value;
                float P = _depthOfField.focusDistance.value;
                float maxCoC = (A * F) / (P - F);
                float maxRadius = GetMaxBokehRadiusInPixels(descriptor.height);
                float rcpAspect = 1f / (wh / (float)hh);

                // Prepare the bokeh kernel constant buffer
                int hash = _depthOfField.GetHashCode();
                if (hash != _bokehHash || maxRadius != _bokehMaxRadius || rcpAspect != _bokehRCPAspect)
                {
                    _bokehHash = hash;
                    _bokehMaxRadius = maxRadius;
                    _bokehRCPAspect = rcpAspect;
                    PrepareBokehKernel(maxRadius, rcpAspect);
                }
                float uvMargin = (1.0f / descriptor.height) * downSample;

                passData.bokehKernel = _bokehKernel;
                passData.downSample = downSample;
                passData.uvMargin = uvMargin;
                passData.cocParams = new Vector4(P, maxCoC, maxRadius, rcpAspect);
                passData.useFastSRGBLinearConversion = _postProcessPass.useFastSRGBLinearConversion;

                // Inputs
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);

                passData.depthTexture = resourceData.cameraDepthTexture;
                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);

                passData.material = material;
                passData.materialCoC = graph.materialLib.GetMaterial("bokehDepthOfFieldPS");

                // Pass Textures
                passData.fullCoCTexture = fullCoCTexture;
                builder.UseTexture(fullCoCTexture, AccessFlags.ReadWrite);
                passData.pingTexture = pingTexture;
                builder.UseTexture(pingTexture, AccessFlags.ReadWrite);
                passData.pongTexture = pongTexture;
                builder.UseTexture(pongTexture, AccessFlags.ReadWrite);

                // Outputs
                passData.destination = destination;
                builder.UseTexture(destination, AccessFlags.Write);

                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.SetRenderFunc(static (DoFBokehPassData data, UnsafeGraphContext context) =>
                {
                    var dofMat = data.material;
                    var dofMaterialCoC = data.materialCoC;
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    RTHandle sourceTextureHdl = data.sourceTexture;
                    RTHandle dst = data.destination;

                    // Setup
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_SetupDoF)))
                    {
                        CoreUtils.SetKeyword(dofMat, ShaderKeywordStrings.UseFastSRGBLinearConversion,
                            data.useFastSRGBLinearConversion);
                        CoreUtils.SetKeyword(dofMaterialCoC, ShaderKeywordStrings.UseFastSRGBLinearConversion,
                            data.useFastSRGBLinearConversion);

                        dofMat.SetVector(ShaderConstants._CoCParams, data.cocParams);
                        dofMat.SetVectorArray(ShaderConstants._BokehKernel, data.bokehKernel);
                        dofMat.SetVector(ShaderConstants._DownSampleScaleFactor,
                            new Vector4(1.0f / data.downSample, 1.0f / data.downSample, data.downSample,
                                data.downSample));
                        dofMat.SetVector(ShaderConstants._BokehConstants,
                            new Vector4(data.uvMargin, data.uvMargin * 2.0f));
                        PostProcessUtils.SetSourceSize(cmd, data.sourceTexture);
                    }

                    // Compute CoC
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_DOFComputeCOC)))
                    {
                        dofMat.SetTexture(ShaderConstants._CameraDepthTexture, data.depthTexture);
                        Blitter.BlitCameraTexture(cmd, sourceTextureHdl, data.fullCoCTexture, dofMat, k_bokehDoFPassComputeCoc);
                    }

                    // Downscale and Prefilter Color + CoC
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_DOFDownscalePrefilter)))
                    {
                        dofMat.SetTexture(ShaderConstants._FullCoCTexture, data.fullCoCTexture);
                        Blitter.BlitCameraTexture(cmd, sourceTextureHdl, data.pingTexture, dofMat, k_bokehDoFPassDownscalePrefilter);
                    }

                    // Blur
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_DOFBlurBokeh)))
                    {
                        Blitter.BlitCameraTexture(cmd, data.pingTexture, data.pongTexture, dofMat, k_bokehDoFPassBlur);
                    }

                    // Post Filtering
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_DOFPostFilter)))
                    {
                        Blitter.BlitCameraTexture(cmd, data.pongTexture, data.pingTexture, dofMat, k_bokehDoFPassPostFilter);
                    }

                    // Composite
                    using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_DOFComposite)))
                    {
                        dofMat.SetTexture(ShaderConstants._DofTexture, data.pingTexture);
                        Blitter.BlitCameraTexture(cmd, sourceTextureHdl, dst, dofMat, k_bokehDoFPassComposite);
                    }
                });
            }

            void PrepareBokehKernel(float maxRadius, float rcpAspect)
            {
                const int kRings = 4;
                const int kPointsPerRing = 7;

                // Check the existing array
                if (_bokehKernel == null)
                    _bokehKernel = new Vector4[42];

                // Fill in sample points (concentric circles transformed to rotated N-Gon)
                int idx = 0;
                float bladeCount = _depthOfField.bladeCount.value;
                float curvature = 1f - _depthOfField.bladeCurvature.value;
                float rotation = _depthOfField.bladeRotation.value * Mathf.Deg2Rad;
                const float PI = Mathf.PI;
                const float TWO_PI = Mathf.PI * 2f;

                for (int ring = 1; ring < kRings; ring++)
                {
                    float bias = 1f / kPointsPerRing;
                    float radius = (ring + bias) / (kRings - 1f + bias);
                    int points = ring * kPointsPerRing;

                    for (int point = 0; point < points; point++)
                    {
                        // Angle on ring
                        float phi = 2f * PI * point / points;

                        // Transform to rotated N-Gon
                        // Adapted from "CryEngine 3 Graphics Gems" [Sousa13]
                        float nt = Mathf.Cos(PI / bladeCount);
                        float dt = Mathf.Cos(phi - (TWO_PI / bladeCount) * Mathf.Floor((bladeCount * phi + Mathf.PI) / TWO_PI));
                        float r = radius * Mathf.Pow(nt / dt, curvature);
                        float u = r * Mathf.Cos(phi - rotation);
                        float v = r * Mathf.Sin(phi - rotation);

                        float uRadius = u * maxRadius;
                        float vRadius = v * maxRadius;
                        float uRadiusPowTwo = uRadius * uRadius;
                        float vRadiusPowTwo = vRadius * vRadius;
                        float kernelLength = Mathf.Sqrt((uRadiusPowTwo + vRadiusPowTwo));
                        float uRCP = uRadius * rcpAspect;

                        _bokehKernel[idx] = new Vector4(uRadius, vRadius, kernelLength, uRCP);
                        idx++;
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static float GetMaxBokehRadiusInPixels(float viewportHeight)
            {
                // Estimate the maximum radius of bokeh (empirically derived from the ring count)
                const float kRadiusInPixels = 14f;
                return Mathf.Min(0.05f, kRadiusInPixels / viewportHeight);
            }
        }
    }
}