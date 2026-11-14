using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
#if UNITY_EDITOR
    [InjectPass(typeof(DrawObjectsPass), InjectionPoint.After, true)]
    [InjectPass(typeof(DeferredObjectPass), InjectionPoint.After, true)]
    public class ProbeVolumeDebugPass : InjectPass
    {
        class WriteApvData
        {
            public ComputeShader computeShader;
            public BufferHandle resultBuffer;
            public Vector2 clickCoordinates;
            public TextureHandle depthBuffer;
            public TextureHandle normalBuffer;
        }

        private ComputeShader _computeShader;

        sealed class PreparedSampling : ContextItem
        {
            internal GraphicsBuffer resultBuffer;
            internal Vector2 coordinates;
            internal bool active;

            public override void Reset()
            {
                resultBuffer = null;
                coordinates = default;
                active = false;
            }
        }

        public ProbeVolumeDebugPass(): base()
        {
            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraPipelineDebugShaders>(out var debugShaders))
                _computeShader = debugShaders.probeVolumeSamplingDebugComputeShader;
        }

        protected override void OnCameraCleanup(CommandBuffer cmd){ }

        protected override void OnDispose() { }

        public override void ResetFrame(RenderGraph renderGraph, ContextContainer frameData)
        {
            frameData.GetOrCreate<PreparedSampling>().Reset();
        }

        public override bool PrepareFrame(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (UltraRenderPipeline.asset == null || !UltraRenderPipeline.asset.supportProbeVolume || _computeShader == null)
                return false;
            var volume = ProbeReferenceVolumeProvider.instance;
            if (!volume.isInitialized)
                return false;

            var prepared = frameData.GetOrCreate<PreparedSampling>();
            // Query once: the provider consumes single-update debug requests.
            // Execution uses this snapshot after its required textures are ready.
            prepared.active = volume.GetProbeSamplingDebugResources(frameData.Get<CameraData>().camera,
                out prepared.resultBuffer, out prepared.coordinates);
            if (prepared.active)
            {
                var rendering = frameData.Get<RenderingData>();
                rendering.renderPassInputs.requiresDepthTexture = true;
                rendering.renderPassInputs.requiresNormalsTexture = true;
                rendering.renderPassInputs.requiresDepthPrepass = true;
            }
            return false;
        }

        public override bool IsEligibleToInject(ScriptableRenderer renderer, ScriptableRenderPassNode node)
        {
            var nodes = renderer.sortedNodes;
            var lastOpaqueDrawPass = nodes.OfType<DrawObjectsPass>().LastOrDefault(n => n.isOpaque && n.active && renderer.IsSameContext(n, node));
            var lastDeferredPass = nodes.OfType<DeferredObjectPass>().LastOrDefault(p => p.active && renderer.IsSameContext(p, node));
            int drawPassIndex = lastOpaqueDrawPass != null ? nodes.IndexOf(lastOpaqueDrawPass) : -1;
            int deferredPassIndex = lastDeferredPass != null ? nodes.IndexOf(lastDeferredPass) : -1;

            ScriptableRenderPassNode targetNode = null;
            if (drawPassIndex > deferredPassIndex)
                targetNode = lastOpaqueDrawPass;
            else if (deferredPassIndex >= 0)
                targetNode = lastDeferredPass;

            return node == targetNode;
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            if (UltraRenderPipeline.asset == null || !UltraRenderPipeline.asset.supportProbeVolume)
                return;

            ResourceData resourceData   = frameData.Get<ResourceData>();

            TextureHandle cameraDepthTexture = resourceData.cameraDepthTexture;
            TextureHandle cameraNormalsTexture = resourceData.cameraNormalsTexture;

            var prepared = frameData.GetOrCreate<PreparedSampling>();
            if (!prepared.active || !cameraDepthTexture.IsValid() || !cameraNormalsTexture.IsValid())
                return;
            prepared.active = false;

            using (var builder = renderGraph.AddComputePass<WriteApvData>(_passName, out var passData, _profilingSampler))
            {
                passData.clickCoordinates = prepared.coordinates;
                passData.computeShader = _computeShader;

                passData.resultBuffer = renderGraph.ImportBuffer(prepared.resultBuffer);
                passData.depthBuffer = cameraDepthTexture;
                passData.normalBuffer = cameraNormalsTexture;

                builder.UseBuffer(passData.resultBuffer, AccessFlags.Write);
                builder.UseTexture(passData.depthBuffer, AccessFlags.Read);
                builder.UseTexture(passData.normalBuffer, AccessFlags.Read);

                builder.SetRenderFunc((WriteApvData data, ComputeGraphContext ctx) =>
                {
                    int kernel = data.computeShader.FindKernel("ComputePositionNormal");

                    ctx.cmd.SetComputeTextureParam(data.computeShader, kernel, "_CameraDepthTexture", data.depthBuffer);
                    ctx.cmd.SetComputeTextureParam(data.computeShader, kernel, "_NormalBufferTexture", data.normalBuffer);
                    ctx.cmd.SetComputeVectorParam(data.computeShader, "_positionSS", new Vector4(data.clickCoordinates.x, data.clickCoordinates.y, 0.0f, 0.0f));
                    ctx.cmd.SetComputeBufferParam(data.computeShader, kernel, "_ResultBuffer", data.resultBuffer);
                    ctx.cmd.DispatchCompute(data.computeShader, kernel, 1, 1, 1);
                });
            }
        }
    }
#endif
}
