using System;
using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class CapturePass : BuiltinPass
    {
        private class UnsafePassData
        {
            internal TextureHandle source;
            public IEnumerator<Action<RenderTargetIdentifier, CommandBuffer>> captureActions;
        }

        protected override void OnCameraCleanup(CommandBuffer cmd) { }

        protected override void OnDispose() { }

        public void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();

            using (var builder = renderGraph.AddUnsafePass<UnsafePassData>(_passName, out var passData, _profilingSampler))
            {
                // Setup up the pass data with cameraColor, which has the correct orientation and position in a built player
                passData.source = resourceData.cameraColor;
                passData.captureActions = cameraData.captureActions;

                // Setup up the builder
                builder.AllowPassCulling(false);
                builder.UseTexture(resourceData.cameraColor);
                builder.SetRenderFunc((UnsafePassData data, UnsafeGraphContext unsafeContext) =>
                {
                    var nativeCommandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(unsafeContext.cmd);
                    var captureActions = data.captureActions;
                    for (data.captureActions.Reset(); data.captureActions.MoveNext();)
                        captureActions.Current(data.source, nativeCommandBuffer);
                });
            }
        }
    }
}
