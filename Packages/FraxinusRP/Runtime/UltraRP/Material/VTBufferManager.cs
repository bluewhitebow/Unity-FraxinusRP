#if ENABLE_VIRTUALTEXTURES
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using VirtualTexturing = UnityEngine.Rendering.VirtualTexturing;

namespace FraxinusEngine.Rendering.Ultra
{
    internal sealed class VTBufferManager
    {
        const int kResolveScaleFactor = 16;
        const int kThreadGroupSize = 8;
        const GraphicsFormat kFeedbackFormat = GraphicsFormat.R16G16_UNorm;
        const GraphicsFormat kResolveFormat = GraphicsFormat.R8G8B8A8_UNorm;

        // ENABLE_VIRTUALTEXTURES gates the feature; device capabilities are fixed.
        static readonly bool s_HardwareSupported = SystemInfo.supportsComputeShaders &&
            SystemInfo.supportsAsyncGPUReadback && SystemInfo.supportedRenderTargetCount >= 3 &&
            SystemInfo.supportsSeparatedRenderTargetsBlend &&
            SystemInfo.IsFormatSupported(kFeedbackFormat, FormatUsage.Render) &&
            SystemInfo.IsFormatSupported(kFeedbackFormat, FormatUsage.Blend) &&
            SystemInfo.IsFormatSupported(kFeedbackFormat, FormatUsage.Sample) &&
            SystemInfo.IsFormatSupported(kResolveFormat, FormatUsage.LoadStore) &&
            SystemInfo.IsFormatSupported(kResolveFormat, FormatUsage.ReadPixels);

        static readonly int s_InputTexture = Shader.PropertyToID("_InputTexture");
        static readonly int s_InputTextureMSAA = Shader.PropertyToID("_InputTextureMSAA");
        static readonly int s_OutputTexture = Shader.PropertyToID("_OutputTexture");
        static readonly int s_Params = Shader.PropertyToID("_Params");
        static readonly int s_Params1 = Shader.PropertyToID("_Params1");
        static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler("Resolve VT Feedback");
        static readonly ProfilingSampler s_ClearProfilingSampler = new ProfilingSampler("Clear VT Feedback");

        // Native resolvers read these textures asynchronously. Never resize an existing
        // generation. Power-of-two growth bounds the retained generations' total area
        // below the current generation, and they are released with their camera owner.
        sealed class ResolverResources
        {
            internal readonly int width;
            internal readonly int height;
            internal readonly RTHandle lowres;
            internal readonly VirtualTexturing.Resolver resolver;
            internal readonly VirtualTexturing.Resolver resolverMSAA;

            internal ResolverResources(int width, int height, string cameraName)
            {
                this.width = width;
                this.height = height;
                lowres = RTHandles.Alloc(width, height, colorFormat: kResolveFormat,
                    filterMode: FilterMode.Point, enableRandomWrite: true,
                    useMipMap: false, autoGenerateMips: false,
                    name: "VT Feedback Lowres (" + cameraName + ")");
                resolver = new VirtualTexturing.Resolver();
                resolverMSAA = new VirtualTexturing.Resolver();
                resolver.UpdateSize(width, height);
                resolverMSAA.UpdateSize(width, height);
            }

            internal void Cleanup()
            {
                resolver.Dispose();
                resolverMSAA.Dispose();
                RTHandles.Release(lowres);
            }
        }

        readonly List<ResolverResources> _generations = new List<ResolverResources>();
        ResolverResources _resources;
        ComputeShader _downsample;
        int _kernel;
        int _kernelMSAA;
        int _resolveCount;
        bool _disposed;

        internal Camera camera { get; }
        internal double lastUsedTime { get; private set; }

        internal VTBufferManager(Camera camera)
        {
            this.camera = camera;
        }

        internal static bool IsSupported(CameraData cameraData)
        {
            if (!s_HardwareSupported || cameraData == null || cameraData.camera == null || cameraData.isSubContext ||
                cameraData.camera.stereoEnabled)
                return false;

            CameraType type = cameraData.cameraType;
            if (type != CameraType.Game && type != CameraType.SceneView && type != CameraType.Reflection)
                return false;

            RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
            if (descriptor.dimension != TextureDimension.Tex2D || descriptor.volumeDepth != 1 ||
                descriptor.width <= 0 || descriptor.height <= 0 ||
                descriptor.graphicsFormat == GraphicsFormat.None || descriptor.useDynamicScale)
                return false;

            if (descriptor.msaaSamples > 1)
            {
                var feedbackDescriptor = new RenderTextureDescriptor(descriptor.width, descriptor.height,
                    kFeedbackFormat, 0) { msaaSamples = descriptor.msaaSamples, bindMS = true };
                if (SystemInfo.GetRenderTextureSupportedMSAASampleCount(feedbackDescriptor) < descriptor.msaaSamples)
                    return false;
            }

            return RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>(
                out var shaders) && shaders.vtFeedbackDownsample != null;
        }

        sealed class ClearPassData { }

        internal static TextureHandle CreateVTFeedbackBuffer(RenderGraph renderGraph, RenderTextureDescriptor descriptor)
        {
            bool msaa = descriptor.msaaSamples > 1;
            var feedback = renderGraph.CreateTexture(new TextureDesc(descriptor.width, descriptor.height)
            {
                colorFormat = kFeedbackFormat,
                dimension = TextureDimension.Tex2D,
                slices = 1,
                msaaSamples = (MSAASamples)Mathf.Max(1, descriptor.msaaSamples),
                bindTextureMS = msaa,
                clearBuffer = true,
                clearColor = Color.white,
                filterMode = FilterMode.Point,
                name = msaa ? "VT Feedback MSAA" : "VT Feedback"
            });

            // A camera can have no DrawObjects writer (or all renderer lists can be
            // culled). A real first writer makes the white clear and allocation
            // happen even in that case, before the native resolver reads it.
            using (var builder = renderGraph.AddRasterRenderPass<ClearPassData>(
                "Clear VT Feedback", out _, s_ClearProfilingSampler))
            {
                builder.SetRenderAttachment(feedback, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (ClearPassData data, RasterGraphContext context) => { });
            }
            return feedback;
        }

        internal void BeginRender(RenderTextureDescriptor descriptor)
        {
            if (_disposed)
                return;

            lastUsedTime = Time.realtimeSinceStartupAsDouble;

            if (_downsample == null &&
                RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>(out var shaders))
            {
                _downsample = shaders.vtFeedbackDownsample;
                if (_downsample != null)
                {
                    _kernel = _downsample.FindKernel("KMain");
                    _kernelMSAA = _downsample.FindKernel("KMainMSAA");
                }
            }

            if (_downsample == null)
                return;

            int width = GetResolveDimension(descriptor.width);
            int height = GetResolveDimension(descriptor.height);
            if (_resources == null || width > _resources.width || height > _resources.height)
            {
                width = Mathf.NextPowerOfTwo(Mathf.Max(width, _resources?.width ?? 1));
                height = Mathf.NextPowerOfTwo(Mathf.Max(height, _resources?.height ?? 1));
                _resources = new ResolverResources(width, height, camera != null ? camera.name : "Camera");
                _generations.Add(_resources);
            }
        }

        sealed class ResolvePassData
        {
            internal VTBufferManager owner;
            internal VirtualTexturing.Resolver resolver;
            internal ComputeShader shader;
            internal int kernel;
            internal int inputID;
            internal int width, height, lowresWidth, lowresHeight;
            internal int sampleOffset;
            internal TextureHandle input;
            internal TextureHandle lowres;
        }

        internal void Resolve(RenderGraph renderGraph, TextureHandle input, RenderTextureDescriptor descriptor)
        {
            if (_disposed || !input.IsValid())
                return;

            BeginRender(descriptor);
            if (_resources == null || _downsample == null)
                return;

            using (var builder = renderGraph.AddUnsafePass<ResolvePassData>("Resolve VT Feedback", out var data, s_ProfilingSampler))
            {
                bool msaa = descriptor.msaaSamples > 1;
                data.owner = this;
                data.resolver = msaa ? _resources.resolverMSAA : _resources.resolver;
                data.shader = _downsample;
                data.kernel = msaa ? _kernelMSAA : _kernel;
                data.inputID = msaa ? s_InputTextureMSAA : s_InputTexture;
                data.width = descriptor.width;
                data.height = descriptor.height;
                data.lowresWidth = GetResolveDimension(descriptor.width);
                data.lowresHeight = GetResolveDimension(descriptor.height);
                data.sampleOffset = _resolveCount & (kResolveScaleFactor * kResolveScaleFactor - 1);
                data.input = input;
                data.lowres = renderGraph.ImportTexture(_resources.lowres);
                builder.UseTexture(data.input, AccessFlags.Read);
                builder.UseTexture(data.lowres, AccessFlags.ReadWrite);
                // Resolver.Process queues native readback; its consumer is outside the graph.
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (ResolvePassData pass, UnsafeGraphContext context) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    RTHandle inputTexture = pass.input;
                    RTHandle lowres = pass.lowres;
                    cmd.SetComputeTextureParam(pass.shader, pass.kernel, pass.inputID, inputTexture);
                    cmd.SetComputeTextureParam(pass.shader, pass.kernel, s_OutputTexture, lowres);
                    cmd.SetComputeVectorParam(pass.shader, s_Params, new Vector4(kResolveScaleFactor,
                        pass.sampleOffset % kResolveScaleFactor, pass.sampleOffset / kResolveScaleFactor, 0));
                    cmd.SetComputeVectorParam(pass.shader, s_Params1,
                        new Vector4(pass.width, pass.height, pass.lowresWidth, pass.lowresHeight));
                    cmd.DispatchCompute(pass.shader, pass.kernel,
                        (pass.lowresWidth + kThreadGroupSize - 1) / kThreadGroupSize,
                        (pass.lowresHeight + kThreadGroupSize - 1) / kThreadGroupSize, 1);
                    pass.resolver.Process(cmd, lowres, 0, pass.lowresWidth, 0, pass.lowresHeight, 0, 0);
                    VirtualTexturing.System.Update();
                    ++pass.owner._resolveCount;
                });
            }
        }

        internal void Cleanup()
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var generation in _generations)
                generation.Cleanup();
            _generations.Clear();
            _resources = null;
            _downsample = null;
        }

        static int GetResolveDimension(int dimension)
        {
            return Mathf.Max(1, (dimension + kResolveScaleFactor - 1) / kResolveScaleFactor);
        }
    }
}
#endif
