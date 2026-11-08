using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static FraxinusEngine.Rendering.Ultra.DeferredLights;
using static FraxinusEngine.Rendering.Ultra.DepthCopyHelper;

namespace FraxinusEngine.Rendering.Ultra
{
    public partial class DeferredPass : BuiltinPass
    {
        private class RasterPassData
        {
            internal CameraData cameraData;
            internal LightData lightData;
            internal ShadowData shadowData;

            internal TextureHandle color;
            internal TextureHandle depth;
            internal TextureHandle[] gbuffer;
            internal DeferredLights deferredLights;
            internal bool contactShadowsEnabled;
        }

        private class ComputePassData
        {
            internal CameraData cameraData;
            internal LightData lightData;
            internal ShadowData shadowData;

            internal DeferredLights deferredLights;

            internal TextureHandle lightingHandle;
            internal TextureHandle stencilHandle;
            internal TextureHandle cameraDepth;
            internal TextureHandle[] gbuffer;

            internal int numTilesX;
            internal int numTilesY;

            // Compute Buffers
            internal BufferHandle dispatchIndirectBuffer;
            internal BufferHandle tileListBuffer;

            // Lighting lookup textures
            internal TextureHandle fgdGGXAndDisneyDiffuse;
            internal TextureHandle areaLightLtc;
            internal int areaLightCount;
            internal bool areaLightingReady;

            // Screen-space AO and light cookies consumed by the lighting kernels.
            internal TextureHandle ambientOcclusionTexture;
            internal bool ambientOcclusionEnabled;
            internal float ambientOcclusionDirectStrength;
            internal TextureHandle mainLightCookieTexture;
            internal TextureHandle additionalLightsCookieAtlasTexture;
            internal bool contactShadowsEnabled;
        }

        private const int k_deferredLightingTileSize = 16;
        private static readonly int s_contactShadowsEnabledPropID = Shader.PropertyToID("_ContactShadowsEnabled");

        public DeferredPass() : base()
        {
            _profilingSampler = new ProfilingSampler("Render Deferred Lighting");
        }

        protected override void OnCameraCleanup(CommandBuffer cmd) { }

        protected override void OnDispose() { }

        internal void Render(RenderGraph renderGraph, ContextContainer frameData, 
            TextureHandle color, TextureHandle depth, TextureHandle[] gbuffer,
            TextureHandle mainShadowsTexture, TextureHandle additionalShadowsTexture, bool isCluster)
        {
            ResourceData resourceData   = frameData.Get<ResourceData>();
            CameraData cameraData       = frameData.Get<CameraData>();
            LightData lightData         = frameData.Get<LightData>();
            ShadowData shadowData       = frameData.Get<ShadowData>();

            var renderer = cameraData.renderer;
            var deferredLights = renderer.GetModule<DeferredLights>();
            bool contactShadowsEnabled = !cameraData.isSubContext && resourceData.contactShadowsTexture.IsValid() &&
                RenderGraphUtils.TextureHandlesEqual(color, resourceData.activeColorTexture) &&
                RenderGraphUtils.TextureHandlesEqual(depth, resourceData.activeDepthTexture);

            bool clusterLighting = isCluster;
            if (clusterLighting && deferredLights.useClusterRaster)
            {
                RenderClusterRaster(renderGraph, frameData, color, depth, gbuffer,
                    mainShadowsTexture, additionalShadowsTexture, deferredLights, contactShadowsEnabled);
                return;
            }
            if (clusterLighting)
            {
                var deferredClassifyDebug = cameraData.isSubContext ? frameData.Get<DeferredClassifyDebugBuffersData>() : frameData.GetOrCreate<DeferredClassifyDebugBuffersData>();

                using (var builder = renderGraph.AddComputePass<ComputePassData>(_passName, out var passData, _profilingSampler))
                {
                    APRTRelightPass.UseLightingResources(builder, frameData);
                    AreaLightsPass.UseLightingResources(builder, frameData);
                    GroundTruthAmbientOcclusionPass.UseLightingResources(builder, frameData);
                    // Setup passData
                    passData.cameraData = cameraData;
                    passData.lightData = lightData;
                    passData.shadowData = shadowData;
                    passData.gbuffer = gbuffer;

                    passData.lightingHandle = color;
                    passData.stencilHandle = depth;
                    passData.cameraDepth = resourceData.cameraDepthTexture;
                    passData.deferredLights = deferredLights;
                    passData.contactShadowsEnabled = contactShadowsEnabled;
                    if (contactShadowsEnabled)
                        builder.UseTexture(resourceData.contactShadowsTexture, AccessFlags.Read);

                    var width = cameraData.cameraTargetDescriptor.width;
                    var height = cameraData.cameraTargetDescriptor.height;
                    passData.numTilesX = RenderingUtils.DivRoundUp(width, k_deferredLightingTileSize);
                    passData.numTilesY = RenderingUtils.DivRoundUp(height, k_deferredLightingTileSize);

                    passData.dispatchIndirectBuffer = renderGraph.CreateBuffer(new BufferDesc((int)ShadingModels.CurModelsNum * 3, sizeof(uint), GraphicsBuffer.Target.IndirectArguments) { name = "DispatchIndirectBuffer" });
                    passData.tileListBuffer = renderGraph.CreateBuffer(new BufferDesc((int)ShadingModels.CurModelsNum * passData.numTilesX * passData.numTilesY, sizeof(uint)) { name = "TileListBuffer" });

                    deferredClassifyDebug.dispatchIndirectBuffer = passData.dispatchIndirectBuffer;
                    deferredClassifyDebug.tileListBuffer = passData.tileListBuffer;
                    deferredClassifyDebug.numTilesX = passData.numTilesX;
                    deferredClassifyDebug.numTilesY = passData.numTilesY;
                    deferredClassifyDebug.isValid = true;

                    var areaLights = frameData.Contains<AreaLightFrameData>() ? frameData.Get<AreaLightFrameData>() : null;
                    passData.fgdGGXAndDisneyDiffuse = areaLights != null && areaLights.fgd.IsValid()
                        ? areaLights.fgd
                        : PreIntegratedFGD.instance.ImportToRenderGraph(renderGraph, PreIntegratedFGD.FGDIndex.FGD_GGXAndDisneyDiffuse);
                    passData.areaLightingReady = areaLights != null && areaLights.ltc.IsValid() && areaLights.fgd.IsValid();
                    passData.areaLightCount = passData.areaLightingReady ? areaLights.lightCount : 0;
                    // Compute kernels require every referenced texture to be bound,
                    // including frames where no rectangle lights can be evaluated.
                    passData.areaLightLtc = passData.areaLightingReady
                        ? areaLights.ltc : renderGraph.defaultResources.blackTextureArrayXR;

                    var ambientOcclusion = frameData.Contains<AmbientOcclusionData>()
                        ? frameData.Get<AmbientOcclusionData>() : null;
                    passData.ambientOcclusionEnabled = !cameraData.isSubContext &&
                        ambientOcclusion != null && ambientOcclusion.active && ambientOcclusion.output.IsValid() &&
                        RenderGraphUtils.TextureHandlesEqual(color, resourceData.activeColorTexture) &&
                        RenderGraphUtils.TextureHandlesEqual(depth, resourceData.activeDepthTexture);
                    passData.ambientOcclusionTexture = passData.ambientOcclusionEnabled
                        ? ambientOcclusion.output : renderGraph.defaultResources.whiteTexture;
                    passData.ambientOcclusionDirectStrength = passData.ambientOcclusionEnabled
                        ? ambientOcclusion.directLightingStrength : 0f;
                    deferredLights.GetCookieTextures(renderGraph,
                        out passData.mainLightCookieTexture, out passData.additionalLightsCookieAtlasTexture);

                    // Declare input/output
                    builder.UseTexture(passData.lightingHandle, AccessFlags.ReadWrite);
                    builder.UseTexture(passData.stencilHandle, AccessFlags.Read);
                    builder.UseBuffer(passData.dispatchIndirectBuffer, AccessFlags.ReadWrite);
                    builder.UseBuffer(passData.tileListBuffer, AccessFlags.ReadWrite);
                    builder.UseTexture(passData.fgdGGXAndDisneyDiffuse, AccessFlags.Read);
                    builder.UseTexture(passData.areaLightLtc, AccessFlags.Read);
                    builder.UseTexture(passData.ambientOcclusionTexture, AccessFlags.Read);
                    builder.UseTexture(passData.mainLightCookieTexture, AccessFlags.Read);
                    builder.UseTexture(passData.additionalLightsCookieAtlasTexture, AccessFlags.Read);
                    builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                    if (mainShadowsTexture.IsValid())
                        builder.UseTexture(mainShadowsTexture, AccessFlags.Read);
                    if (additionalShadowsTexture.IsValid())
                        builder.UseTexture(additionalShadowsTexture, AccessFlags.Read);

                    // Global bindings do not extend a transient RenderGraph resource's lifetime.
                    // Keep the culled lists and light data alive through their actual consumer.
                    var gpuLights = frameData.Get<GPULightsOutPassData>();
                    builder.UseBuffer(gpuLights.gpuLightsData, AccessFlags.Read);
                    builder.UseBuffer(gpuLights.directionalLightsData, AccessFlags.Read);
                    builder.UseBuffer(gpuLights.coarseLightList, AccessFlags.Read);
                    builder.UseBuffer(gpuLights.perVoxelOffset, AccessFlags.Read);
                    builder.UseBuffer(gpuLights.perVoxelLightLists, AccessFlags.Read);
                    builder.UseBuffer(gpuLights.perTileLogBaseTweak, AccessFlags.Read);

                    for (int i = 0; i < gbuffer.Length; ++i)
                    {
                        if (i != deferredLights.gBufferLightingIndex)
                            builder.UseTexture(gbuffer[i], AccessFlags.Read);
                    }

                    // Setup builder state
                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetGlobalFloat(s_contactShadowsEnabledPropID, data.contactShadowsEnabled ? 1.0f : 0.0f);
                        var lightDatas = new DeferredLights.LightingDatas()
                        {
                            numTilesX = data.numTilesX,
                            numTilesY = data.numTilesY,

                            lightingHandle = data.lightingHandle,
                            stencilHandle = data.stencilHandle,
                            cameraDepth = data.cameraDepth,
                            gbuffer = data.gbuffer,

                            dispatchIndirectBuffer = data.dispatchIndirectBuffer,
                            tileListBuffer = data.tileListBuffer,

                            fgdGGXAndDisneyDiffuse = data.fgdGGXAndDisneyDiffuse,
                            areaLightLtc = data.areaLightLtc,
                            areaLightCount = data.areaLightCount,
                            areaLightingReady = data.areaLightingReady,

                            ambientOcclusionTexture = data.ambientOcclusionTexture,
                            ambientOcclusionEnabled = data.ambientOcclusionEnabled,
                            ambientOcclusionDirectStrength = data.ambientOcclusionDirectStrength,
                            mainLightCookieTexture = data.mainLightCookieTexture,
                            additionalLightsCookieAtlasTexture = data.additionalLightsCookieAtlasTexture,
                        };

                        data.deferredLights.ExecuteClusterDeferredPass(context.cmd, data.cameraData, data.lightData, data.shadowData, lightDatas);
                    });
                }
            }
            else
            {
                if (frameData.Contains<DeferredClassifyDebugBuffersData>())
                    frameData.Get<DeferredClassifyDebugBuffersData>().isValid = false;

                using (var builder = renderGraph.AddRasterRenderPass<RasterPassData>(_passName, out var passData, _profilingSampler))
                {
                    APRTRelightPass.UseLightingResources(builder, frameData);
                    GroundTruthAmbientOcclusionPass.UseLightingResources(builder, frameData);
                    passData.cameraData = cameraData;
                    passData.lightData = lightData;
                    passData.shadowData = shadowData;

                    passData.color = color;
                    builder.SetRenderAttachment(color, 0, AccessFlags.Write);
                    passData.depth = depth;
                    builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
                    passData.deferredLights = deferredLights;
                    passData.contactShadowsEnabled = contactShadowsEnabled;
                    if (contactShadowsEnabled)
                        builder.UseTexture(resourceData.contactShadowsTexture, AccessFlags.Read);

                    passData.gbuffer = gbuffer;

                    if (!deferredLights.useFramebufferFetch)
                    {
                        // Keep the GBuffer depth copy alive until the lighting shader has sampled it.
                        builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                        for (int i = 0; i < gbuffer.Length; ++i)
                        {
                            if (i != deferredLights.gBufferLightingIndex)
                                builder.UseTexture(gbuffer[i], AccessFlags.Read);
                        }
                    }
                    else
                    {
                        var idx = 0;
                        for (int i = 0; i < gbuffer.Length; ++i)
                        {
                            if (i != deferredLights.gBufferLightingIndex)
                            {
                                builder.SetInputAttachment(gbuffer[i], idx, AccessFlags.Read);
                                idx++;
                            }
                        }
                    }

                    if (mainShadowsTexture.IsValid())
                        builder.UseTexture(mainShadowsTexture, AccessFlags.Read);

                    if (additionalShadowsTexture.IsValid())
                        builder.UseTexture(additionalShadowsTexture, AccessFlags.Read);

                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc((RasterPassData data, RasterGraphContext context) =>
                    {
                        context.cmd.SetGlobalFloat(s_contactShadowsEnabledPropID, data.contactShadowsEnabled ? 1.0f : 0.0f);
                        data.deferredLights.ExecuteDeferredPass(context.cmd, data.cameraData, data.lightData, data.shadowData, data.gbuffer);
                    });
                }
            }
        }
    }
}
