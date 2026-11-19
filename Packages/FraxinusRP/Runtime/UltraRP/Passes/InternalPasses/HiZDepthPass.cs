using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>Produces requested main-camera depth pyramids for FraxinusRP consumers.</summary>
    [InjectPass(typeof(RenderDrivenPass), InjectionPoint.After, scopeFilter = ContextScopeFilter.Main)]
    public sealed class HiZDepthPass : InjectPass
    {
        static readonly ProfilingSampler s_sampler = new ProfilingSampler("Hi-Z Depth");
        static readonly int _Source = Shader.PropertyToID("_HiZSource");
        static readonly int _Output = Shader.PropertyToID("_HiZOutput");
        static readonly int _SourceWidth = Shader.PropertyToID("_HiZSourceWidth");
        static readonly int _SourceHeight = Shader.PropertyToID("_HiZSourceHeight");
        static readonly int _OutputWidth = Shader.PropertyToID("_HiZOutputWidth");
        static readonly int _OutputHeight = Shader.PropertyToID("_HiZOutputHeight");
        static readonly int _InvertDepth = Shader.PropertyToID("_HiZInvertDepth");

        ComputeShader _shader;
        int _copyKernel = -1;
        int _reduceKernel = -1;

        sealed class FrameData : ContextItem
        {
            internal bool active, recorded;
            internal ComputeShader shader;
            internal int copyKernel, reduceKernel;
            public override void Reset()
            {
                active = recorded = false;
                shader = null;
                copyKernel = reduceKernel = 0;
            }
        }

        sealed class PassData
        {
            internal ComputeShader shader;
            internal int kernel, sourceWidth, sourceHeight, width, height, mip;
            internal bool invertDepth;
            internal TextureHandle source, output;
            internal HiZDepthHistory completedHistory;
            internal Camera camera;
            internal ScriptableRenderer renderer;
        }

        public override void ResetFrame(RenderGraph graph, ContextContainer frame) => frame.GetOrCreate<FrameData>().Reset();

        public override RenderPassInputSummary PrepareFrameResources(RenderGraph graph, ContextContainer frame)
        {
            var camera = frame.Get<CameraData>();
            if (!IsCameraSupported(camera))
                return default;

            // beginCameraRendering can request history on a camera which did not
            // have additional data when the pipeline first inspected it. Pick up
            // that newly attached owner before UpdateCameraHistory runs.
            if (camera.historyManager == null && camera.camera.TryGetComponent(out UltraAdditionalCameraData additional))
                camera.historyManager = additional.history;
            var history = camera.historyManager?.GetHistoryForRead<HiZDepthHistory>();
            if (history == null || !history.requestedThisFrame ||
                !HasActiveMainInjection(camera.renderer, frame) ||
                !RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>(out var shaders))
                return default;

            // Resolve kernels on first use and when the resource is replaced.
            if (!ReferenceEquals(_shader, shaders.hiZDepthCS))
            {
                _shader = shaders.hiZDepthCS;
                _copyKernel = _shader != null && _shader.HasKernel("CopyDepth") ? _shader.FindKernel("CopyDepth") : -1;
                _reduceKernel = _shader != null && _shader.HasKernel("ReduceDepth") ? _shader.FindKernel("ReduceDepth") : -1;
            }
            if (_shader == null || _copyKernel < 0 || _reduceKernel < 0)
                return default;

            var prepared = frame.GetOrCreate<FrameData>();
            prepared.shader = _shader;
            prepared.copyKernel = _copyKernel;
            prepared.reduceKernel = _reduceKernel;
            prepared.active = true;

            return new RenderPassInputSummary
            {
                requiresDepthTexture = true,
                requiresDepthPrepass = true,
                requiresFullDepthPrepass = true,
                disableFramebufferFetch = true,
            };
        }

        internal static bool HasActiveMainInjection(ScriptableRenderer renderer, ContextContainer frame = null)
        {
            if (renderer == null || renderer.graph == null || !renderer.graph.IsInjectPassEnabled(typeof(HiZDepthPass)))
                return false;
            int contextDepth = 0;
            foreach (var node in renderer.sortedNodes)
            {
                if (node is ContextBeginNode) { ++contextDepth; continue; }
                if (node is ContextEndNode) { contextDepth = Mathf.Max(0, contextDepth - 1); continue; }
                if (contextDepth != 0 || !(node is RenderDrivenPass driven) || !driven.active ||
                    driven.computeOrder < 0 || !driven.canProcess ||
                    (frame != null && !driven.SupportedInCurrentFrame(frame)))
                    continue;
                foreach (var injected in driven.afterRenderingPasses)
                    if (injected is HiZDepthPass)
                        return true;
            }
            return false;
        }

        static bool IsCameraSupported(CameraData camera) => camera != null &&
            HiZDepthHistory.IsCameraSupported(camera.camera) && !camera.isSubContext &&
            camera.renderType == CameraRenderType.Base && !UltraRenderer.IsOffscreenDepthTexture(camera) &&
            camera.cameraTargetDescriptor.dimension == TextureDimension.Tex2D &&
            camera.cameraTargetDescriptor.width > 0 && camera.cameraTargetDescriptor.height > 0 &&
            !camera.cameraTargetDescriptor.useDynamicScale;

        public override void Execute(RenderGraph graph, ScriptableRenderContext context, ContextContainer frame)
        {
            var camera = frame.Get<CameraData>();
            if (camera.isSubContext || !frame.Contains<FrameData>())
                return;

            var prepared = frame.Get<FrameData>();
            var rendering = frame.Get<RenderingData>();
            var depth = frame.Get<ResourceData>().cameraDepthTexture;
            if (!prepared.active || prepared.recorded || !rendering.requiresDepthPrepass ||
                !rendering.renderPassInputs.requiresFullDepthPrepass || !depth.IsValid())
                return;

            // UpdateCameraHistory has now advanced and swapped camera history.
            // Acquire and allocate the output only after that point.
            var history = camera.historyManager?.GetHistoryForWrite<HiZDepthHistory>();
            if (history == null || !history.requestedThisFrame)
                return;
            prepared.recorded = true;
            RecordDepthPyramid(graph, depth, camera.cameraTargetDescriptor, history,
                prepared.shader, prepared.copyKernel, prepared.reduceKernel, camera.camera, camera.renderer);
        }

        internal static void RecordDepthPyramid(RenderGraph graph, TextureHandle depth, RenderTextureDescriptor descriptor,
            HiZDepthHistory history, ComputeShader shader, int copyKernel, int reduceKernel, Camera camera, ScriptableRenderer renderer)
        {
            var handle = history.Update(descriptor.width, descriptor.height);
            if (handle?.rt == null || !handle.rt.IsCreated())
                return;
            history.Invalidate();
            var pyramid = graph.ImportTexture(handle);
            int width = descriptor.width;
            int height = descriptor.height;
            int mipCount = handle.rt.mipmapCount;
            RecordDispatch(graph, shader, copyKernel, depth, pyramid, width, height, width, height, 0,
                !SystemInfo.usesReversedZBuffer, mipCount == 1 ? history : null, camera, renderer);

            var source = pyramid;
            for (int mip = 1; mip < mipCount; ++mip)
            {
                int mipWidth = Mathf.Max(1, width >> 1);
                int mipHeight = Mathf.Max(1, height >> 1);
                // Separate subresources avoid simultaneous SRV/UAV bindings of
                // the persistent pyramid, including on D3D11.
                var reduced = graph.CreateTexture(new TextureDesc(mipWidth, mipHeight)
                {
                    name = "Hi-Z Reduced Depth",
                    colorFormat = GraphicsFormat.R32_SFloat,
                    enableRandomWrite = true,
                    filterMode = FilterMode.Point,
                    clearBuffer = false
                });
                RecordDispatch(graph, shader, reduceKernel, source, reduced, width, height, mipWidth, mipHeight, 0,
                    false, null, camera, renderer);
                RecordDispatch(graph, shader, copyKernel, reduced, pyramid, mipWidth, mipHeight, mipWidth, mipHeight, mip,
                    false, mip == mipCount - 1 ? history : null, camera, renderer);
                source = reduced;
                width = mipWidth;
                height = mipHeight;
            }
        }

        static void RecordDispatch(RenderGraph graph, ComputeShader shader, int kernel, TextureHandle source, TextureHandle output,
            int sourceWidth, int sourceHeight, int width, int height, int mip, bool invertDepth,
            HiZDepthHistory completedHistory, Camera camera, ScriptableRenderer renderer)
        {
            using (var builder = graph.AddComputePass<PassData>("Hi-Z Depth", out var data, s_sampler))
            {
                data.shader = shader;
                data.kernel = kernel;
                data.source = source;
                data.output = output;
                data.sourceWidth = sourceWidth;
                data.sourceHeight = sourceHeight;
                data.width = width;
                data.height = height;
                data.mip = mip;
                data.invertDepth = invertDepth;
                data.completedHistory = completedHistory;
                data.camera = camera;
                data.renderer = renderer;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(output, mip == 0 ? AccessFlags.Write : AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc((PassData pass, ComputeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    cmd.SetComputeTextureParam(pass.shader, pass.kernel, _Source, pass.source);
                    cmd.SetComputeTextureParam(pass.shader, pass.kernel, _Output, pass.output, pass.mip);
                    cmd.SetComputeIntParam(pass.shader, _SourceWidth, pass.sourceWidth);
                    cmd.SetComputeIntParam(pass.shader, _SourceHeight, pass.sourceHeight);
                    cmd.SetComputeIntParam(pass.shader, _OutputWidth, pass.width);
                    cmd.SetComputeIntParam(pass.shader, _OutputHeight, pass.height);
                    cmd.SetComputeIntParam(pass.shader, _InvertDepth, pass.invertDepth ? 1 : 0);
                    cmd.DispatchCompute(pass.shader, pass.kernel, (pass.width + 7) / 8, (pass.height + 7) / 8, 1);
                    pass.completedHistory?.MarkGenerated(pass.camera, pass.renderer);
                });
            }
        }

        protected override void OnCameraCleanup(CommandBuffer cmd) { }
        protected override void OnDispose() { }
    }
}
