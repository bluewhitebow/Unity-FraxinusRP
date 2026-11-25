using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public abstract class DepthCopyAndMotionPassBase : InjectPass
    {
        protected bool _depthCopyValid = false;
        protected bool _renderMotionVectorValid = false;

        protected DepthCopyAndMotionPassBase() : base() { }

        public void ConfigExecuteValid(bool depthCopyValid, bool motionVectorValid)
        {
            _depthCopyValid = depthCopyValid;
            _renderMotionVectorValid = motionVectorValid;
        }

        protected override void OnCameraCleanup(CommandBuffer cmd) 
        {
            _depthCopyValid = false;
            _renderMotionVectorValid = false;
        }

        protected override void OnDispose() { }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();

            var renderer = cameraData.renderer;

            if (_depthCopyValid)
            {
                var copyDepthPass = renderer.GetModule<DepthCopyPass>();
                copyDepthPass.Render(renderGraph, frameData, resourceData.cameraDepthTexture, resourceData.activeDepthTexture, true);
            }

            if (_renderMotionVectorValid)
            {
                var motionVectorPass = renderer.GetModule<MotionVectorRenderPass>();
                motionVectorPass.Render(renderGraph, frameData, resourceData.cameraDepthTexture, resourceData.motionVectorColor, resourceData.motionVectorDepth);
            }
        }

        public abstract override bool IsEligibleToInject(ScriptableRenderer renderer, ScriptableRenderPassNode node);
    }
}

