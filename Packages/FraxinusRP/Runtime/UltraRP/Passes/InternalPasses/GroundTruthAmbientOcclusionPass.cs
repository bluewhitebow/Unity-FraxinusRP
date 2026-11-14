using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    [InjectPass(typeof(RenderDrivenPass), InjectionPoint.After, scopeFilter = ContextScopeFilter.Main)]
    public sealed class GroundTruthAmbientOcclusionPass : InjectPass
    {
        static readonly ProfilingSampler s_Sampler = new ProfilingSampler("Ground Truth Ambient Occlusion");
        internal static readonly int textureId = Shader.PropertyToID("_ScreenSpaceOcclusionTexture");
        static readonly int s_AmbientParams = Shader.PropertyToID("_AmbientOcclusionParam");
        static readonly int s_Enabled = Shader.PropertyToID("_FraxinusAmbientOcclusionEnabled");
        static readonly int s_DirectStrength = Shader.PropertyToID("_FraxinusAmbientOcclusionDirectStrength");
        static readonly int s_Depth = Shader.PropertyToID("_GTAODepthTexture");
        static readonly int s_DepthHalf = Shader.PropertyToID("_GTAODepthHalf");
        static readonly int s_DepthQuarter = Shader.PropertyToID("_GTAODepthQuarter");
        static readonly int s_Normals = Shader.PropertyToID("_GTAONormalsTexture");
        static readonly int s_PackedInput = Shader.PropertyToID("_GTAOPackedData");
        static readonly int s_PackedOutput = Shader.PropertyToID("_RW_GTAOPackedData");
        static readonly int s_Output = Shader.PropertyToID("_GTAOOutput");
        static readonly int s_DepthInput = Shader.PropertyToID("_GTAODepthInput");
        static readonly int s_DepthOutput = Shader.PropertyToID("_GTAODepthOutput");
        static readonly int s_DownsampleSize = Shader.PropertyToID("_GTAODownsampleSize");
        static readonly int s_FullSize = Shader.PropertyToID("_GTAOFullSize");
        static readonly int s_TraceSize = Shader.PropertyToID("_GTAOTraceSize");
        static readonly int s_Params = Shader.PropertyToID("_GTAOParams");
        static readonly int s_Sampling = Shader.PropertyToID("_GTAOSampling");
        static readonly int s_Projection = Shader.PropertyToID("_GTAOProjection");
        static readonly int s_InverseProjection = Shader.PropertyToID("_GTAOInvProjection");
        static readonly int s_View = Shader.PropertyToID("_GTAOView");
        static readonly int s_Orthographic = Shader.PropertyToID("_GTAOOrthographic");
        static readonly int s_NormalsOct = Shader.PropertyToID("_GTAONormalsOct");

        sealed class PublishData { internal bool enabled; internal float directStrength; }
        sealed class DownsampleData
        {
            internal ComputeShader shader;
            internal int kernel, width, height;
            internal Vector4 size;
            internal TextureHandle input, output;
        }
        sealed class PassData
        {
            internal ComputeShader shader;
            internal int kernel, width, height, orthographic, normalsOct;
            internal Matrix4x4 projection, inverseProjection, view;
            internal Vector4 fullSize, traceSize, parameters, sampling;
            internal TextureHandle depth, depthHalf, depthQuarter, normals, packed, output;
        }

        protected override void OnCameraCleanup(CommandBuffer cmd) { }
        protected override void OnDispose() { }

        public override void ResetFrame(RenderGraph graph, ContextContainer frame)
        {
            var data = frame.GetOrCreate<AmbientOcclusionData>();
            data.Reset();
            var resources = frame.Get<ResourceData>();
            resources.ambientOcclusionTexture = TextureHandle.nullHandle;
            resources.ambientOcclusionDirectLightingStrength = 0f;
            Publish(graph, TextureHandle.nullHandle, false, 0f);
        }

        public override bool PrepareFrame(RenderGraph graph, ContextContainer frame)
        {
            var data = frame.GetOrCreate<AmbientOcclusionData>();
            var camera = frame.Get<CameraData>();
            if (!IsCameraSupported(camera) || !HasActiveMainInjection(camera.renderer, frame))
                return false;
            var settings = VolumeManager.instance.stack.GetComponent<GroundTruthAmbientOcclusion>();
            if (settings == null || !settings.IsActive() ||
                !RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>(out var shaders) ||
                shaders.groundTruthAmbientOcclusionCS == null)
                return false;
            var shader = shaders.groundTruthAmbientOcclusionCS;
            string denoise = settings.downSample.value ? "BlurUpsample" : "SpatialDenoise";
            if (!shader.HasKernel("DownsampleDepth") || !shader.HasKernel("GTAOMain") || !shader.HasKernel(denoise))
                return false;

            data.shader = shader;
            data.downsampleKernel = shader.FindKernel("DownsampleDepth");
            data.traceKernel = shader.FindKernel("GTAOMain");
            data.denoiseKernel = shader.FindKernel(denoise);
            if (!shader.IsSupported(data.downsampleKernel) || !shader.IsSupported(data.traceKernel) ||
                !shader.IsSupported(data.denoiseKernel))
                return false;
            data.width = camera.cameraTargetDescriptor.width;
            data.height = camera.cameraTargetDescriptor.height;
            data.downSample = settings.downSample.value;
            int divider = data.downSample ? 2 : 1;
            data.traceWidth = (data.width + divider - 1) / divider;
            data.traceHeight = (data.height + divider - 1) / divider;
            data.projection = camera.GetGPUProjectionMatrix(true);
            data.inverseProjection = data.projection.inverse;
            data.view = camera.GetViewMatrix();
            data.orthographic = camera.camera.orthographic ? 1 : 0;
            data.normalsOct = frame.Get<RenderingData>().hasDeferred && camera.renderer.accurateGbufferNormals ? 1 : 0;
            // Keep the same screen-space search extent when tracing quality changes.
            // The authored cap is measured at half-resolution 1080p.
            const float referencePixels = 960f * 540f;
            float pixelRadius = settings.maximumRadiusInPixels.value *
                Mathf.Sqrt(data.traceWidth * (float)data.traceHeight / referencePixels);
            data.parameters = new Vector4(settings.radius.value, settings.intensity.value,
                settings.thickness.value, Mathf.Max(1f, pixelRadius));
            data.sampling = new Vector4(settings.stepCount.value, settings.directionCount.value, divider, settings.blurSharpness.value);
            data.directLightingStrength = settings.directLightingStrength.value;
            data.outputFormat = SupportsFormat(GraphicsFormat.R8_UNorm) ? GraphicsFormat.R8_UNorm : GraphicsFormat.R16_SFloat;
            data.active = true;

            // All opaque geometry must be available before both Forward and GBuffer
            // lighting. A partial Deferred prepass contains only ForwardOnly objects.
            var rendering = frame.Get<RenderingData>();
            rendering.renderPassInputs.requiresDepthTexture = true;
            rendering.renderPassInputs.requiresNormalsTexture = true;
            rendering.renderPassInputs.requiresDepthPrepass = true;
            rendering.renderPassInputs.requiresFullDepthPrepass = true;
            rendering.renderPassInputs.requiresIntermediateTexture = true;
            rendering.renderPassInputs.disableFramebufferFetch = true;
            return true;
        }

        static bool SupportsFormat(GraphicsFormat format) =>
            SystemInfo.IsFormatSupported(format, FormatUsage.LoadStore) && SystemInfo.IsFormatSupported(format, FormatUsage.Sample);

        static bool IsCameraSupported(CameraData camera) =>
            camera != null && camera.camera != null && !camera.isSubContext &&
            camera.renderType == CameraRenderType.Base && !UltraRenderer.IsOffscreenDepthTexture(camera) &&
            (camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView) &&
            camera.cameraTargetDescriptor.dimension == TextureDimension.Tex2D &&
            camera.cameraTargetDescriptor.width > 0 && camera.cameraTargetDescriptor.height > 0 &&
            !camera.cameraTargetDescriptor.useDynamicScale && !camera.camera.stereoEnabled && SystemInfo.supportsComputeShaders &&
            SupportsFormat(GraphicsFormat.R32_SFloat) && SupportsFormat(GraphicsFormat.R32G32_SFloat) &&
            (SupportsFormat(GraphicsFormat.R8_UNorm) || SupportsFormat(GraphicsFormat.R16_SFloat));

        bool HasActiveMainInjection(ScriptableRenderer renderer, ContextContainer frame)
        {
            if (renderer == null) return false;
            int depth = 0;
            foreach (var node in renderer.sortedNodes)
            {
                if (node is ContextBeginNode) { ++depth; continue; }
                if (node is ContextEndNode) { depth = Mathf.Max(0, depth - 1); continue; }
                if (depth != 0 || !(node is RenderDrivenPass driven) || !driven.active ||
                    driven.computeOrder < 0 || !driven.canProcess || !driven.SupportedInCurrentFrame(frame)) continue;
                foreach (var pass in driven.afterRenderingPasses)
                    if (ReferenceEquals(pass, this)) return true;
            }
            return false;
        }

        public override void Execute(RenderGraph graph, ScriptableRenderContext context, ContextContainer frame)
        {
            if (frame.Get<CameraData>().isSubContext || !frame.Contains<AmbientOcclusionData>()) return;
            var prepared = frame.Get<AmbientOcclusionData>();
            var resources = frame.Get<ResourceData>();
            var rendering = frame.Get<RenderingData>();
            if (!prepared.active || prepared.output.IsValid() || !rendering.requiresDepthPrepass ||
                !rendering.renderPassInputs.requiresFullDepthPrepass ||
                !resources.cameraDepthTexture.IsValid() || !resources.cameraNormalsTexture.IsValid()) return;

            int halfWidth = (prepared.width + 1) / 2, halfHeight = (prepared.height + 1) / 2;
            int quarterWidth = (halfWidth + 1) / 2, quarterHeight = (halfHeight + 1) / 2;
            var halfDepth = CreateTexture(graph, halfWidth, halfHeight, GraphicsFormat.R32_SFloat, "GTAO Half Depth");
            var quarterDepth = CreateTexture(graph, quarterWidth, quarterHeight, GraphicsFormat.R32_SFloat, "GTAO Quarter Depth");
            Downsample(graph, prepared, resources.cameraDepthTexture, halfDepth, prepared.width, prepared.height, halfWidth, halfHeight);
            Downsample(graph, prepared, halfDepth, quarterDepth, halfWidth, halfHeight, quarterWidth, quarterHeight);
            var packed = CreateTexture(graph, prepared.traceWidth, prepared.traceHeight, GraphicsFormat.R32G32_SFloat, "GTAO Visibility and Depth");
            var output = CreateTexture(graph, prepared.width, prepared.height, prepared.outputFormat, "Ground Truth Ambient Occlusion");

            using (var builder = graph.AddComputePass<PassData>("GTAO Horizon Search", out var pass, s_Sampler))
            {
                Initialize(pass, prepared, prepared.traceKernel, prepared.traceWidth, prepared.traceHeight);
                pass.depth = resources.cameraDepthTexture; pass.normals = resources.cameraNormalsTexture;
                pass.depthHalf = halfDepth; pass.depthQuarter = quarterDepth; pass.packed = packed;
                builder.UseTexture(pass.depth, AccessFlags.Read);
                builder.UseTexture(pass.normals, AccessFlags.Read);
                builder.UseTexture(halfDepth, AccessFlags.Read);
                builder.UseTexture(quarterDepth, AccessFlags.Read);
                builder.UseTexture(packed, AccessFlags.WriteAll);
                builder.SetRenderFunc((PassData data, ComputeGraphContext ctx) =>
                {
                    BindParameters(ctx.cmd, data);
                    ctx.cmd.SetComputeTextureParam(data.shader, data.kernel, s_Depth, data.depth);
                    ctx.cmd.SetComputeTextureParam(data.shader, data.kernel, s_Normals, data.normals);
                    ctx.cmd.SetComputeTextureParam(data.shader, data.kernel, s_DepthHalf, data.depthHalf);
                    ctx.cmd.SetComputeTextureParam(data.shader, data.kernel, s_DepthQuarter, data.depthQuarter);
                    ctx.cmd.SetComputeTextureParam(data.shader, data.kernel, s_PackedOutput, data.packed);
                    ctx.cmd.DispatchCompute(data.shader, data.kernel, (data.width + 7) / 8, (data.height + 7) / 8, 1);
                });
            }

            using (var builder = graph.AddComputePass<PassData>(prepared.downSample ? "GTAO Bilateral Upsample" : "GTAO Spatial Denoise", out var pass, s_Sampler))
            {
                Initialize(pass, prepared, prepared.denoiseKernel, prepared.width, prepared.height);
                pass.depth = resources.cameraDepthTexture; pass.packed = packed; pass.output = output;
                builder.UseTexture(pass.depth, AccessFlags.Read);
                builder.UseTexture(packed, AccessFlags.Read);
                builder.UseTexture(output, AccessFlags.WriteAll);
                builder.SetRenderFunc((PassData data, ComputeGraphContext ctx) =>
                {
                    BindParameters(ctx.cmd, data);
                    ctx.cmd.SetComputeTextureParam(data.shader, data.kernel, s_Depth, data.depth);
                    ctx.cmd.SetComputeTextureParam(data.shader, data.kernel, s_PackedInput, data.packed);
                    ctx.cmd.SetComputeTextureParam(data.shader, data.kernel, s_Output, data.output);
                    ctx.cmd.DispatchCompute(data.shader, data.kernel, (data.width + 7) / 8, (data.height + 7) / 8, 1);
                });
            }
            prepared.output = resources.ambientOcclusionTexture = output;
            resources.ambientOcclusionDirectLightingStrength = prepared.directLightingStrength;
            Publish(graph, output, true, prepared.directLightingStrength);
        }

        static TextureHandle CreateTexture(RenderGraph graph, int width, int height, GraphicsFormat format, string name) =>
            graph.CreateTexture(new TextureDesc(width, height)
            { name = name, colorFormat = format, enableRandomWrite = true, filterMode = FilterMode.Point, clearBuffer = false });

        static void Downsample(RenderGraph graph, AmbientOcclusionData prepared, TextureHandle input, TextureHandle output,
            int inputWidth, int inputHeight, int width, int height)
        {
            using var builder = graph.AddComputePass<DownsampleData>("GTAO Depth Reduction", out var data, s_Sampler);
            data.shader = prepared.shader; data.kernel = prepared.downsampleKernel;
            data.input = input; data.output = output; data.width = width; data.height = height;
            data.size = new Vector4(width, height, inputWidth, inputHeight);
            builder.UseTexture(input, AccessFlags.Read);
            builder.UseTexture(output, AccessFlags.WriteAll);
            builder.SetRenderFunc((DownsampleData pass, ComputeGraphContext ctx) =>
            {
                ctx.cmd.SetComputeVectorParam(pass.shader, s_DownsampleSize, pass.size);
                ctx.cmd.SetComputeTextureParam(pass.shader, pass.kernel, s_DepthInput, pass.input);
                ctx.cmd.SetComputeTextureParam(pass.shader, pass.kernel, s_DepthOutput, pass.output);
                ctx.cmd.DispatchCompute(pass.shader, pass.kernel, (pass.width + 7) / 8, (pass.height + 7) / 8, 1);
            });
        }

        static void Initialize(PassData pass, AmbientOcclusionData prepared, int kernel, int width, int height)
        {
            pass.shader = prepared.shader; pass.kernel = kernel; pass.width = width; pass.height = height;
            pass.projection = prepared.projection; pass.inverseProjection = prepared.inverseProjection; pass.view = prepared.view;
            pass.parameters = prepared.parameters; pass.sampling = prepared.sampling;
            pass.orthographic = prepared.orthographic; pass.normalsOct = prepared.normalsOct;
            pass.fullSize = new Vector4(prepared.width, prepared.height, 1f / prepared.width, 1f / prepared.height);
            pass.traceSize = new Vector4(prepared.traceWidth, prepared.traceHeight, 1f / prepared.traceWidth, 1f / prepared.traceHeight);
        }

        static void BindParameters(ComputeCommandBuffer cmd, PassData pass)
        {
            cmd.SetComputeVectorParam(pass.shader, s_FullSize, pass.fullSize);
            cmd.SetComputeVectorParam(pass.shader, s_TraceSize, pass.traceSize);
            cmd.SetComputeVectorParam(pass.shader, s_Params, pass.parameters);
            cmd.SetComputeVectorParam(pass.shader, s_Sampling, pass.sampling);
            cmd.SetComputeMatrixParam(pass.shader, s_Projection, pass.projection);
            cmd.SetComputeMatrixParam(pass.shader, s_InverseProjection, pass.inverseProjection);
            cmd.SetComputeMatrixParam(pass.shader, s_View, pass.view);
            cmd.SetComputeIntParam(pass.shader, s_Orthographic, pass.orthographic);
            cmd.SetComputeIntParam(pass.shader, s_NormalsOct, pass.normalsOct);
        }

        // Also used at context boundaries. Publish neutral visibility before any
        // unsupported camera or child draw, and restore the parent's state afterwards.
        internal static void Publish(RenderGraph graph, TextureHandle texture, bool enabled, float directStrength)
        {
            using var builder = graph.AddRasterRenderPass<PublishData>(enabled ? "Publish Ambient Occlusion" : "Reset Ambient Occlusion", out var data);
            data.enabled = enabled && texture.IsValid();
            data.directStrength = data.enabled ? directStrength : 0f;
            var published = data.enabled ? texture : graph.defaultResources.whiteTexture;
            builder.UseTexture(published, AccessFlags.Read);
            builder.SetGlobalTextureAfterPass(published, textureId);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PublishData pass, RasterGraphContext ctx) =>
            {
                ctx.cmd.SetKeyword(ShaderGlobalKeywords.ScreenSpaceOcclusion, pass.enabled);
                ctx.cmd.SetGlobalVector(s_AmbientParams, new Vector4(1f, 0f, 0f, pass.directStrength));
                ctx.cmd.SetGlobalInt(s_Enabled, pass.enabled ? 1 : 0);
                ctx.cmd.SetGlobalFloat(s_DirectStrength, pass.directStrength);
            });
        }

        internal static void UseLightingResources(IBaseRenderGraphBuilder builder, ContextContainer frame)
        {
            if (frame.Get<CameraData>().isSubContext) return;
            var resources = frame.Get<ResourceData>();
            if (resources.ambientOcclusionTexture.IsValid()) builder.UseTexture(resources.ambientOcclusionTexture, AccessFlags.Read);
        }
    }
}
