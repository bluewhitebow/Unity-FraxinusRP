using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class DepthCopyPass : BuiltinPass
    {
        private readonly DepthCopyHelper _helper;

        public DepthCopyPass() : base()
        {
            _helper = new DepthCopyHelper();
        }

        protected override void OnCameraCleanup(CommandBuffer cmd)
        {
        }

        protected override void OnDispose()
        {
            _helper.Dispose();
        }


        public void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle destination, TextureHandle source, bool bindAsCameraDepth = false, string passName = "Copy Depth", TextureHandle colorAttachment = default)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();
            _helper.Render(renderGraph, destination, source, resourceData, cameraData, _profilingSampler,
                bindAsCameraDepth, passName, colorAttachment);
        }

        internal void RenderToCameraTarget(RenderGraph renderGraph, ContextContainer frameData, TextureHandle source)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();

            // Final depth belongs to the output color surface, including after upscaling.
            // Keep the pair bound for native backbuffer passes and preserve pixels outside
            // this camera's viewport, whether it outputs to the screen or a RenderTexture.
            _helper.Render(renderGraph, resourceData.backBufferDepth, source, resourceData, cameraData, _profilingSampler,
                passName: "Final Depth Copy", colorAttachment: resourceData.backBufferColor, viewport: cameraData.pixelRect);
        }
    }
}

