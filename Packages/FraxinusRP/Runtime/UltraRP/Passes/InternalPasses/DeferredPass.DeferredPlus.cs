using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public partial class DeferredPass
    {
        sealed class DeferredPlusPassData
        {
            internal Material material;
            internal bool framebufferFetch;
            internal bool shadowMask;
            internal bool contactShadows;
            internal int mainLightIndex;
            internal DeferredLights.LightingDatas lighting;
            internal ForwardPlusFrameData forwardPlus;
            internal TextureHandle renderingLayers;
            internal TextureHandle shadowMaskTexture;
            internal TextureHandle contactShadowTexture;
        }

        static readonly ProfilingSampler s_deferredPlusSampler = new ProfilingSampler("Deferred+ Lighting");

        void RenderDeferredPlus(RenderGraph graph, ContextContainer frameData,
            TextureHandle color, TextureHandle depth, TextureHandle[] gbuffer,
            TextureHandle mainShadows, TextureHandle additionalShadows,
            DeferredLights deferred, bool contactShadows)
        {
            var camera = frameData.Get<CameraData>();
            var lights = frameData.Get<LightData>();
            var resources = frameData.Get<ResourceData>();
            if (frameData.Contains<DeferredClassifyDebugBuffersData>())
                frameData.Get<DeferredClassifyDebugBuffersData>().isValid = false;

            using (var builder = graph.AddRasterRenderPass<DeferredPlusPassData>(
                s_deferredPlusSampler.name, out var pass, s_deferredPlusSampler))
            {
                APRTRelightPass.UseLightingResources(builder, frameData);
                AreaLightsPass.UseLightingResources(builder, frameData);
                GroundTruthAmbientOcclusionPass.UseLightingResources(builder, frameData);
                pass.forwardPlus = ForwardLights.UseLightingResources(builder, frameData);
                pass.material = deferred.deferredPlusMaterial;
                pass.framebufferFetch = deferred.useFramebufferFetch;
                pass.mainLightIndex = lights.mainLightIndex;
                pass.shadowMask = deferred.useShadowMask;
                pass.contactShadows = contactShadows;
                pass.contactShadowTexture = contactShadows ? resources.contactShadowsTexture : graph.defaultResources.whiteTexture;
                // Decal layers may allocate a GBuffer slice even without light layers.
                // Input attachment indices follow the lighting shader's active keywords.
                bool lightLayers = deferred.useLightLayers && !CoreUtils.IsSceneLightingDisabled(camera.camera);
                pass.renderingLayers = lightLayers ? gbuffer[deferred.gBufferRenderingLayers] : graph.defaultResources.blackTexture;
                pass.shadowMaskTexture = pass.shadowMask ? gbuffer[deferred.gBufferShadowMask] : graph.defaultResources.whiteTexture;

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

                builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.Read);
                if (pass.framebufferFetch)
                {
                    for (int i = 0; i < 3; ++i)
                        builder.SetInputAttachment(gbuffer[i], i, AccessFlags.Read);
                    builder.SetInputAttachment(gbuffer[deferred.gbufferDepthIndex], 3, AccessFlags.Read);
                    if (lightLayers)
                        builder.SetInputAttachment(pass.renderingLayers, 4, AccessFlags.Read);
                    if (pass.shadowMask)
                        builder.SetInputAttachment(pass.shadowMaskTexture, lightLayers ? 5 : 4, AccessFlags.Read);
                }
                else
                {
                    for (int i = 0; i < 3; ++i)
                        builder.UseTexture(gbuffer[i], AccessFlags.Read);
                    builder.UseTexture(data.cameraDepth, AccessFlags.Read);
                    builder.UseTexture(pass.renderingLayers, AccessFlags.Read);
                    builder.UseTexture(pass.shadowMaskTexture, AccessFlags.Read);
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
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((DeferredPlusPassData p, RasterGraphContext ctx) => ExecuteDeferredPlus(ctx.cmd, p));
            }
        }

        static void ExecuteDeferredPlus(RasterCommandBuffer cmd, DeferredPlusPassData pass)
        {
            var data = pass.lighting;
            ForwardLights.BindLightLists(cmd, pass.forwardPlus);
            cmd.SetKeyword(ShaderGlobalKeywords.RenderPassEnabled, pass.framebufferFetch);
            cmd.SetKeyword(ShaderGlobalKeywords._DEFERRED_MIXED_LIGHTING, pass.shadowMask);
            cmd.SetGlobalInt("_DeferredPlusMainLightIndex", pass.mainLightIndex);
            cmd.SetGlobalInt("_FraxinusAreaLightCount", data.areaLightCount);
            cmd.SetGlobalInt("_FraxinusLightingLutsReady", data.areaLightingReady ? 1 : 0);
            cmd.SetGlobalInt("_FraxinusAmbientOcclusionEnabled", data.ambientOcclusionEnabled ? 1 : 0);
            cmd.SetGlobalFloat("_FraxinusAmbientOcclusionDirectStrength", data.ambientOcclusionDirectStrength);
            cmd.SetGlobalFloat(s_contactShadowsEnabledPropID, pass.contactShadows ? 1f : 0f);
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
                cmd.SetGlobalTexture("_DeferredPlusRenderingLayersTexture", pass.renderingLayers);
                cmd.SetGlobalTexture("_DeferredPlusShadowMaskTexture", pass.shadowMaskTexture);
            }
            for (int model = 0; model < (int)ShadingModels.CurModelsNum; ++model)
                cmd.DrawProcedural(Matrix4x4.identity, pass.material, model, MeshTopology.Triangles, 3, 1);
            cmd.SetKeyword(ShaderGlobalKeywords._DEFERRED_MIXED_LIGHTING, false);
        }
    }
}
