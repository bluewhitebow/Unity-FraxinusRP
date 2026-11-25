using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable]
    [NodeMenuItem("UltraRP/DrawSkyboxPass")]
    public class DrawSkyboxPass : ScriptableRenderPassNode
    {
        private class PassData
        {
            internal TextureHandle colorTarget;
            internal TextureHandle depthTarget;
            internal RendererListHandle skyRendererListHandle;
            internal Material material;
            internal CameraData cameraData;
            internal LightData lightData;
        }

        [Input("Camera Target")]
        public CameraTargetBufferNodeParameter inCameraTarget;

        [Output("Camera Target")]
        internal CameraTargetBufferNodeParameter outCameraTarget;

        [SerializeField] private bool _visualSky;

        public DrawSkyboxPass() : base() 
        {
            profilingSampler = new ProfilingSampler(nameof(DrawSkyboxPass));
        }

        public override void SetupPass()
        {
            CameraTargetBuffer buffer = inCameraTarget.value;

            ConfigurePortValue(outCameraTarget, buffer);
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            CameraTargetBuffer buffer = inCameraTarget.value;
            
            if (!buffer.IsValid())
                return;

            TextureHandle colorTarget = buffer.color;
            TextureHandle depthTarget = buffer.depth;

            CameraData cameraData = frameData.Get<CameraData>();

            if (cameraData.camera.clearFlags == CameraClearFlags.Skybox && cameraData.renderType != CameraRenderType.Overlay)
            {
                var activeDebugHandler = GetActiveDebugHandler(cameraData);
                if (activeDebugHandler != null && activeDebugHandler.IsScreenClearNeeded)
                    return;

                if (_visualSky)
                {
                    LightData lightData = frameData.Get<LightData>();
                    
                    using (var builder = renderGraph.AddUnsafePass<PassData>(passName, out var passData, profilingSampler))
                    {
                        passData.colorTarget = colorTarget;
                        passData.depthTarget = depthTarget;
                        passData.cameraData = cameraData;
                        passData.lightData = lightData;
                        
                        builder.UseTexture(colorTarget, AccessFlags.Write);
                        builder.UseTexture(depthTarget, AccessFlags.Write);

                        builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                        {
                            var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                            cmd.SetRenderTarget(passData.colorTarget, passData.depthTarget);
                            // SkySystem.instance.RenderSky(cmd, data.cameraData, data.lightData);
                        });
                    }
                }
                else
                {
                    cameraData.camera.TryGetComponent(out Skybox cameraSkybox);
                    Material skyboxMaterial = cameraSkybox != null ? cameraSkybox.material : RenderSettings.skybox;

                    using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
                    {
                        var skyRendererListHandle = CreateSkyBoxRendererList(renderGraph, cameraData);
                        passData.skyRendererListHandle = skyRendererListHandle;
                        passData.material = skyboxMaterial;
                        
                        builder.UseRendererList(skyRendererListHandle);
                        builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);
                        builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Write);

                        builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                        {
                            context.cmd.DrawRendererList(data.skyRendererListHandle);
                        });
                    }
                }
            }
        }

        private RendererListHandle CreateSkyBoxRendererList(RenderGraph renderGraph, CameraData cameraData)
        {
            return renderGraph.CreateSkyboxRendererList(cameraData.camera);
        }
    }
}
