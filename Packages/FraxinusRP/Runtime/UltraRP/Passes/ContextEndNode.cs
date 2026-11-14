using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable, NodeMenuItem("Context/ContextEnd")]
    public class ContextEndNode : ScriptableRenderPassNode
    {
        static readonly string s_globalTextureName = "_SubContextColor";
        
        [Input("From Sub CameraTarget")]
        public CameraTargetBufferNodeParameter inCameraTarget;
        
        [Output("Color Texture")]
        public TextureHandleNodeParameter outColorTexture;

        [Output("Parent CameraTarget", allowMultiple = true)]
        public CameraTargetBufferNodeParameter outCameraTarget;

        public ContextEndNode() : base()
        {
            profilingSampler = new ProfilingSampler(nameof(ContextEndNode));
        }
        
        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            if (!frameData.Contains<ContextStateManager>())
                throw new InvalidOperationException("ContextEnd requires an active ContextBegin.");
            var state = frameData.Get<ContextStateManager>();
            if (!state.IsInSubContext() || state.depth == 0)
                throw new InvalidOperationException("ContextEnd requires an active ContextBegin.");
            var target = inCameraTarget.value;
            var subColor = target.IsValid() ? target.color : state.Get<ResourceData>().activeColorTexture;
            RenderGraphUtils.SetGlobalTexture(renderGraph, Shader.PropertyToID(s_globalTextureName), subColor, "Publish SubContext Color");
            var parent = state.PopContext();
            var parentResources = frameData.Get<ResourceData>();
            frameData.Get<CameraData>().renderer.SetupRenderGraphCameraProperties(renderGraph, parentResources.isActiveTargetBackBuffer);
            ContextRenderState.RestoreTextures(renderGraph, parentResources, parent.textureBindings);
            if (frameData.Get<RenderingData>().usesForwardPlusLightLists)
                ForwardLights.RestoreContextLightLists(renderGraph, frameData);
            if (frameData.Get<RenderingData>().hasDeferredCluster)
                GPULights.RenderGlobalBindings(renderGraph, frameData.Get<GPULightsOutPassData>());
            ConfigurePortValue(outColorTexture, subColor);
            ConfigurePortValue(outCameraTarget, new CameraTargetBuffer(parentResources.activeColorTexture, parentResources.activeDepthTexture));
        }
    }
}
