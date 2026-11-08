using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public partial class DeferredPass
    {
        sealed class ClusterRasterPassData
        {
            internal Material material;
            internal bool framebufferFetch;
            internal bool contactShadows;
            internal int mainLightIndex;
            internal int directionalLightCount;
            internal DeferredLights.LightingDatas lighting;
            internal TextureHandle renderingLayers;
            internal TextureHandle contactShadowTexture;
            internal BufferHandle lightData, directionalData, coarseList, offsets, lightList, logBase;
        }

        static readonly ProfilingSampler s_clusterRasterSampler = new ProfilingSampler("Cluster Deferred Raster");

        void RenderClusterRaster(RenderGraph graph, ContextContainer frameData,
            TextureHandle color, TextureHandle depth, TextureHandle[] gbuffer,
            TextureHandle mainShadows, TextureHandle additionalShadows,
            DeferredLights deferred, bool contactShadows)
        {
            var camera = frameData.Get<CameraData>();
            var lights = frameData.Get<LightData>();
            var resources = frameData.Get<ResourceData>();
            var gpu = frameData.Get<GPULightsOutPassData>();
            if (frameData.Contains<DeferredClassifyDebugBuffersData>())
                frameData.Get<DeferredClassifyDebugBuffersData>().isValid = false;

            using (var builder = graph.AddRasterRenderPass<ClusterRasterPassData>(
                "Cluster Deferred Raster", out var pass, s_clusterRasterSampler))
            {
                APRTRelightPass.UseLightingResources(builder, frameData);
                AreaLightsPass.UseLightingResources(builder, frameData);
                GroundTruthAmbientOcclusionPass.UseLightingResources(builder, frameData);
                pass.material = deferred.clusterDeferredMaterial;
                pass.framebufferFetch = deferred.useFramebufferFetch;
                pass.mainLightIndex = lights.mainLightIndex;
                pass.directionalLightCount = lights.additionalLightsCount != 0 || lights.mainLightIndex >= 0
                    ? lights.directionalLightsCount : 0;
                pass.contactShadows = contactShadows;
                pass.contactShadowTexture = contactShadows ? resources.contactShadowsTexture : graph.defaultResources.whiteTexture;
                pass.renderingLayers = deferred.useRenderingLayers
                    ? gbuffer[deferred.gBufferRenderingLayers] : graph.defaultResources.blackTexture;

                var data = new DeferredLights.LightingDatas
                {
                    lightingHandle = color,
                    stencilHandle = depth,
                    cameraDepth = resources.cameraDepthTexture,
                    gbuffer = gbuffer
                };
                var areaLights = frameData.Contains<AreaLightFrameData>() ? frameData.Get<AreaLightFrameData>() : null;
                data.fgdGGXAndDisneyDiffuse = areaLights != null && areaLights.fgd.IsValid()
                    ? areaLights.fgd : PreIntegratedFGD.instance.ImportToRenderGraph(graph, PreIntegratedFGD.FGDIndex.FGD_GGXAndDisneyDiffuse);
                data.areaLightingReady = areaLights != null && areaLights.ltc.IsValid() && areaLights.fgd.IsValid();
                data.areaLightCount = data.areaLightingReady ? areaLights.lightCount : 0;
                data.areaLightLtc = data.areaLightingReady ? areaLights.ltc : graph.defaultResources.blackTextureArrayXR;
                var ao = frameData.Contains<AmbientOcclusionData>() ? frameData.Get<AmbientOcclusionData>() : null;
                data.ambientOcclusionEnabled = !camera.isSubContext && ao != null && ao.active && ao.output.IsValid() &&
                    RenderGraphUtils.TextureHandlesEqual(color, resources.activeColorTexture) &&
                    RenderGraphUtils.TextureHandlesEqual(depth, resources.activeDepthTexture);
                data.ambientOcclusionTexture = data.ambientOcclusionEnabled ? ao.output : graph.defaultResources.whiteTexture;
                data.ambientOcclusionDirectStrength = data.ambientOcclusionEnabled ? ao.directLightingStrength : 0f;
                deferred.GetCookieTextures(graph, out data.mainLightCookieTexture, out data.additionalLightsCookieAtlasTexture);
                pass.lighting = data;

                // The blend adds direct lighting and optionally corrects existing
                // GI. Fixed-function stencil rejects other shading models without
                // sampling depth/stencil or dispatching a classifier after GBuffer.
                builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.Read);
                if (pass.framebufferFetch)
                {
                    for (int i = 0; i < 3; ++i)
                        builder.SetInputAttachment(gbuffer[i], i, AccessFlags.Read);
                    builder.SetInputAttachment(gbuffer[deferred.gbufferDepthIndex], 3, AccessFlags.Read);
                    if (deferred.useRenderingLayers)
                        builder.SetInputAttachment(pass.renderingLayers, 4, AccessFlags.Read);
                }
                else
                {
                    for (int i = 0; i < 3; ++i)
                        builder.UseTexture(gbuffer[i], AccessFlags.Read);
                    builder.UseTexture(data.cameraDepth, AccessFlags.Read);
                    builder.UseTexture(pass.renderingLayers, AccessFlags.Read);
                }
                builder.UseTexture(data.fgdGGXAndDisneyDiffuse, AccessFlags.Read);
                builder.UseTexture(data.areaLightLtc, AccessFlags.Read);
                builder.UseTexture(data.ambientOcclusionTexture, AccessFlags.Read);
                builder.UseTexture(data.mainLightCookieTexture, AccessFlags.Read);
                builder.UseTexture(data.additionalLightsCookieAtlasTexture, AccessFlags.Read);
                builder.UseTexture(pass.contactShadowTexture, AccessFlags.Read);
                if (mainShadows.IsValid())
                    builder.UseTexture(mainShadows, AccessFlags.Read);
                if (additionalShadows.IsValid())
                    builder.UseTexture(additionalShadows, AccessFlags.Read);

                // Capture this scope's grid rather than relying on whichever
                // globals a nested context most recently published.
                pass.lightData = gpu.gpuLightsData;
                pass.directionalData = gpu.directionalLightsData;
                pass.coarseList = gpu.coarseLightList;
                pass.offsets = gpu.perVoxelOffset;
                pass.lightList = gpu.perVoxelLightLists;
                pass.logBase = gpu.perTileLogBaseTweak;
                builder.UseBuffer(pass.lightData, AccessFlags.Read);
                builder.UseBuffer(pass.directionalData, AccessFlags.Read);
                builder.UseBuffer(pass.coarseList, AccessFlags.Read);
                builder.UseBuffer(pass.offsets, AccessFlags.Read);
                builder.UseBuffer(pass.lightList, AccessFlags.Read);
                builder.UseBuffer(pass.logBase, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((ClusterRasterPassData p, RasterGraphContext ctx) => ExecuteClusterRaster(ctx.cmd, p));
            }
        }

        static void ExecuteClusterRaster(RasterCommandBuffer cmd, ClusterRasterPassData pass)
        {
            var data = pass.lighting;
            cmd.SetKeyword(ShaderGlobalKeywords.RenderPassEnabled, pass.framebufferFetch);
            cmd.SetGlobalInt("_ClusterMainLightIndex", pass.mainLightIndex);
            cmd.SetGlobalInt("_ClusterDirectionalLightCount", pass.directionalLightCount);
            cmd.SetGlobalInt("_FraxinusAreaLightCount", data.areaLightCount);
            cmd.SetGlobalInt("_FraxinusLightingLutsReady", data.areaLightingReady ? 1 : 0);
            cmd.SetGlobalInt("_FraxinusAmbientOcclusionEnabled", data.ambientOcclusionEnabled ? 1 : 0);
            cmd.SetGlobalFloat("_FraxinusAmbientOcclusionDirectStrength", data.ambientOcclusionDirectStrength);
            cmd.SetGlobalFloat(s_contactShadowsEnabledPropID, pass.contactShadows ? 1f : 0f);
            // Inactive child scopes must not replace the parent's visibility
            // texture: later forward draws enable it again on the same camera.
            if (pass.contactShadows)
                cmd.SetGlobalTexture("_ContactShadowTexture", pass.contactShadowTexture);
            cmd.SetGlobalTexture(PreIntegratedFGD.ShaderConstants._PreIntegratedFGD_GGXDisneyDiffuse, data.fgdGGXAndDisneyDiffuse);
            cmd.SetGlobalTexture("_LtcData", data.areaLightLtc);
            cmd.SetGlobalTexture("_ScreenSpaceOcclusionTexture", data.ambientOcclusionTexture);
            cmd.SetGlobalTexture("_MainLightCookieTexture", data.mainLightCookieTexture, RenderTextureSubElement.Color);
            cmd.SetGlobalTexture("_AdditionalLightsCookieAtlasTexture", data.additionalLightsCookieAtlasTexture);
            if (!pass.framebufferFetch)
            {
                for (int i = 0; i < 3; ++i)
                    cmd.SetGlobalTexture(DeferredLights.gbufferShaderPropertyIDs[i], data.gbuffer[i]);
                cmd.SetGlobalTexture("_CameraDepthTexture", data.cameraDepth);
                cmd.SetGlobalTexture("_ClusterRenderingLayersTexture", pass.renderingLayers);
            }
            // GPU Lights Global Sync (or ContextEnd's grid restore) publishes the
            // matching light-list constant buffer before these raster passes.
            cmd.SetGlobalBuffer("g_GPULightDatas", pass.lightData);
            cmd.SetGlobalBuffer("g_DirectionalLightDatas", pass.directionalData);
            cmd.SetGlobalBuffer("g_CoarseLightList", pass.coarseList);
            cmd.SetGlobalBuffer("g_vLayeredOffsetsBuffer", pass.offsets);
            cmd.SetGlobalBuffer("g_vLightListCluster", pass.lightList);
            cmd.SetGlobalBuffer("g_logBaseBuffer", pass.logBase);
            for (int model = 0; model < (int)ShadingModels.CurModelsNum; ++model)
                cmd.DrawProcedural(Matrix4x4.identity, pass.material, model, MeshTopology.Triangles, 3, 1);
        }
    }
}
