using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable, NodeMenuItem("UltraRP/GBufferPassNode")]
    public class GBufferPassNode : ScriptableRenderPassNode
    {
        [Input("Camera Target")]
        public CameraTargetBufferNodeParameter inCameraTarget;

        [Output("Camera Target", allowMultiple = true)]
        public CameraTargetBufferNodeParameter outCameraTarget;

        [Output("Out GBuffer")]
        internal TextureHandlesNodeParameter outGBuffer;

        public GBufferPassNode() : base()
        {
            profilingSampler = new ProfilingSampler(nameof(GBufferPassNode));
        }

        public override void SetupPass()
        {
            // Port values will be configured after GBufferPass.Render() execution
            ConfigurePortValue(outGBuffer, null);
            ConfigurePortValue(outCameraTarget, inCameraTarget.value);
        }

        public override bool SupportedInCurrentFrame(ContextContainer frameData)
        {
            return frameData.Get<RenderingData>().hasDeferred;
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            CameraTargetBuffer buffer = inCameraTarget.value;
            
            if (!buffer.IsValid())
                return;

            TextureHandle colorTarget = buffer.color;
            TextureHandle depthTarget = buffer.depth;

            ResourceData resourceData = frameData.Get<ResourceData>();
            RenderingData renderingData = frameData.Get<RenderingData>();
            CameraData cameraData = frameData.Get<CameraData>();
            LightData lightData = frameData.Get<LightData>();

            var renderer = cameraData.renderer;
            var gBufferPass = renderer.GetModule<GBufferPass>();

            gBufferPass.Render(renderGraph, frameData, colorTarget, depthTarget);
            if (cameraData.isSubContext && !renderer.GetModule<DeferredLights>().useFramebufferFetch)
            {
                // The main camera may have copied its complete prepass depth before
                // entering this scope. This child has its own depth attachment and
                // must publish the GBuffer depth regardless of the main schedule or
                // whether optional InjectPass execution is disabled in this scope.
                renderer.GetModule<DepthCopyPass>().Render(renderGraph, frameData,
                    resourceData.cameraDepthTexture, depthTarget, true);
            }
            ConfigurePortValue(outGBuffer, resourceData.gBuffer);
        }
    }
}

