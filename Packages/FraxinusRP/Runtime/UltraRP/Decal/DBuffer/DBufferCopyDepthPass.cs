using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    internal class DBufferCopyDepthPass
    {
        private readonly DepthCopyHelper _helper;
        private readonly ProfilingSampler _profilingSampler;

        public DBufferCopyDepthPass()
        {
            _profilingSampler = new ProfilingSampler("DBuffer Copy Depth");
            _helper = new DepthCopyHelper();
        }

        public void Dispose()
        {
            _helper.Dispose();
        }

        /// <summary>
        /// Renders the depth copy for DBuffer.
        /// Creates temporary DBuffer depth texture if needed based on rendering mode and depth priming.
        /// </summary>
        public TextureHandle Render(RenderGraph renderGraph, ContextContainer frameData)
        {
            RenderingData renderingData = frameData.Get<RenderingData>();
            CameraData cameraData = frameData.Get<CameraData>();
            ResourceData resourceData = frameData.Get<ResourceData>();

            bool isDeferred = renderingData.hasDeferred;
            bool useDepthPriming = cameraData.renderer.useDepthPriming && !cameraData.isSubContext;
            bool isMsaa = cameraData.cameraTargetDescriptor.msaaSamples > 1;

            // Check if we need to create a temporary depth buffer for DBuffer rendering
            // Deferred always has compatible depth
            // Forward only has compatible depth when depth priming is enabled without MSAA
            bool hasCompatibleDepth = isDeferred || (useDepthPriming && !isMsaa);

            if (!hasCompatibleDepth)
            {
                // Create temporary DBuffer depth texture
                var depthDesc = cameraData.cameraTargetDescriptor;
                depthDesc.graphicsFormat = GraphicsFormat.None; // Depth only rendering
                depthDesc.depthStencilFormat = cameraData.cameraTargetDescriptor.depthStencilFormat;
                depthDesc.msaaSamples = 1;

                TextureHandle dBufferDepth = UltraRenderer.CreateRenderGraphTexture(
                    renderGraph, depthDesc, DBufferRenderPass.s_DBufferDepthName, true);

                // Copy current depth to DBuffer depth
                _helper.Render(renderGraph, dBufferDepth, resourceData.cameraDepthTexture, resourceData, cameraData,
                    _profilingSampler, passName: "Copy DBuffer Depth");

                return dBufferDepth;
            }

            return TextureHandle.nullHandle;
        }

        public void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle destination, TextureHandle source, bool bindAsCameraDepth = false, string passName = "Copy Depth")
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();
            _helper.Render(renderGraph, destination, source, resourceData, cameraData, _profilingSampler, bindAsCameraDepth, passName);
        }
    }
}
