using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static FraxinusEngine.Rendering.Ultra.DepthCopyHelper;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline.Profiling.Pipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    partial class DeferredLights
    {
        // Initial indirect dispatch arguments. Classification appends the X group count.
        static readonly uint[] s_clearDispatchIndirectBuffer =
        {
            0u, 1u, 1u, // model 0: threadGroupCountX/Y/Z
            0u, 1u, 1u, // model 1
            0u, 1u, 1u, // model 2
        };
        
        public struct LightingDatas
        {
            public int numTilesX;
            public int numTilesY;

            public TextureHandle lightingHandle;
            public TextureHandle stencilHandle;
            public TextureHandle cameraDepth;
            public TextureHandle[] gbuffer;

            // Compute Buffers
            public BufferHandle dispatchIndirectBuffer;
            public BufferHandle tileListBuffer;

            // Lighting lookup textures
            public TextureHandle fgdGGXAndDisneyDiffuse;
            public TextureHandle areaLightLtc;
            public int areaLightCount;
            public bool areaLightingReady;

            // Screen-space AO and light cookies consumed by the lighting kernels.
            public TextureHandle ambientOcclusionTexture;
            public bool ambientOcclusionEnabled;
            public float ambientOcclusionDirectStrength;
            public TextureHandle mainLightCookieTexture;
            public TextureHandle additionalLightsCookieAtlasTexture;
        }

        internal void GetCookieTextures(RenderGraph renderGraph, out TextureHandle mainCookie, out TextureHandle additionalCookieAtlas)
        {
            mainCookie = _lightCookieManager != null
                ? _lightCookieManager.mainLightCookieTextureHandle : renderGraph.defaultResources.whiteTexture;
            additionalCookieAtlas = _lightCookieManager != null
                ? _lightCookieManager.additionalLightsCookieAtlasTextureHandle : renderGraph.defaultResources.whiteTexture;
        }

        public void RenderClusterLights(RenderGraph renderGraph, ContextContainer frameData)
        {
            RenderingData renderingData = frameData.Get<RenderingData>();
            CameraData cameraData = frameData.Get<CameraData>();
            LightData lightData = frameData.Get<LightData>();
            // GPULights
            {
                var punctualLightCount = lightData.additionalLightsCount;
                var reflectionProbes = renderingData.cullResults.visibleReflectionProbes;
                var reflectionProbeCount = Mathf.Min(reflectionProbes.Length, UltraRenderPipeline.maxVisibleReflectionProbes);
                _gpuLights.NewFrame(punctualLightCount + reflectionProbeCount, this);
                _gpuLights.BuildGPULightList(lightData, cameraData);
                _gpuLights.BuildEnvLightList(ref reflectionProbes, reflectionProbeCount, cameraData);

                _gpuLights.PreSetup(lightData, cameraData);
            }

            _gpuLights.SetupRenderGraphLights(renderGraph, renderingData, cameraData, lightData);

            // GPULightList
            _gpuLights.Render(renderGraph, frameData);
            _gpuLights.RenderSetGlobalSync(renderGraph, frameData);
        }

        internal void RenderContextLightGrid(RenderGraph graph, ContextContainer frameData)
        {
            // A ContextBegin changes resolution only. Light bounds and light data
            // remain valid for the shared view; pixel-addressed grids do not.
            _gpuLights.PreSetup(frameData.Get<LightData>(), frameData.Get<CameraData>());
            _gpuLights.Render(graph, frameData);
            _gpuLights.RenderSetGlobalSync(graph, frameData);
        }

        public void ExecuteClusterDeferredPass(ComputeCommandBuffer cmd, CameraData cameraData, LightData lightData, ShadowData shadowData, LightingDatas data)
        {
            EnsureDeferredLightingKernels();
            cmd.SetComputeIntParam(_deferredLightingCS, "_ClusterMainLightIndex", lightData.mainLightIndex);
            // Stencil's PrecomputeLights skips all visible lights when both
            // pipeline lighting modes are disabled. The GPU list can still
            // contain directional lights, so pass the effective count explicitly.
            int directionalLightCount = lightData.additionalLightsCount != 0 || lightData.mainLightIndex >= 0
                ? lightData.directionalLightsCount : 0;
            cmd.SetComputeIntParam(_deferredLightingCS, "_ClusterDirectionalLightCount", directionalLightCount);
            cmd.SetComputeIntParam(_deferredLightingCS, "_FraxinusAreaLightCount", data.areaLightCount);
            cmd.SetComputeIntParam(_deferredLightingCS, "_FraxinusLightingLutsReady", data.areaLightingReady ? 1 : 0);
            cmd.SetComputeIntParam(_deferredLightingCS, "_FraxinusAmbientOcclusionEnabled", data.ambientOcclusionEnabled ? 1 : 0);
            cmd.SetComputeFloatParam(_deferredLightingCS, "_FraxinusAmbientOcclusionDirectStrength", data.ambientOcclusionDirectStrength);

            // BuildIndirect
            {
                cmd.SetBufferData(data.dispatchIndirectBuffer, s_clearDispatchIndirectBuffer, 0, 0, s_clearDispatchIndirectBuffer.Length);

                cmd.SetComputeTextureParam(_deferredLightingCS, _deferredClassifyTilesKernel, "_StencilTexture", data.stencilHandle, 0,
                    RenderTextureSubElement.Stencil);

                cmd.SetComputeBufferParam(_deferredLightingCS, _deferredClassifyTilesKernel, "g_DispatchIndirectBuffer", data.dispatchIndirectBuffer);
                cmd.SetComputeBufferParam(_deferredLightingCS, _deferredClassifyTilesKernel, "g_TileList", data.tileListBuffer);

                cmd.DispatchCompute(_deferredLightingCS, _deferredClassifyTilesKernel, data.numTilesX, data.numTilesY, 1);
            }

            // Lighting
            {
                // Bind PreIntegratedFGD before deferred shading.
                PreIntegratedFGD.instance.Bind(cmd, PreIntegratedFGD.FGDIndex.FGD_GGXAndDisneyDiffuse, data.fgdGGXAndDisneyDiffuse);

                int modelsNum = (int)ShadingModels.CurModelsNum;
                for (int modelIndex = 0; modelIndex < modelsNum; modelIndex++)
                {
                    var kernelIndex = _deferredLightingKernels[modelIndex];
                    cmd.SetComputeIntParam(_deferredLightingCS, "_ShadingModelIndex", modelIndex);
                    cmd.SetComputeIntParam(_deferredLightingCS, "_ShadingModelStencil", (int)GetShadingModels(modelIndex));
                    cmd.SetComputeVectorParam(_deferredLightingCS, "_TilesNum", new Vector2(data.numTilesX, data.numTilesY));

                    cmd.SetComputeBufferParam(_deferredLightingCS, kernelIndex, "g_TileList", data.tileListBuffer);
                    cmd.SetComputeTextureParam(_deferredLightingCS, kernelIndex, "_LightingTexture", data.lightingHandle);
                    cmd.SetComputeTextureParam(_deferredLightingCS, kernelIndex, "_StencilTexture", data.stencilHandle, 0, RenderTextureSubElement.Stencil);
                    cmd.SetComputeTextureParam(_deferredLightingCS, kernelIndex, "_CameraDepthTexture", data.cameraDepth);
                    cmd.SetComputeTextureParam(_deferredLightingCS, kernelIndex,
                        PreIntegratedFGD.ShaderConstants._PreIntegratedFGD_GGXDisneyDiffuse, data.fgdGGXAndDisneyDiffuse);
                    cmd.SetComputeTextureParam(_deferredLightingCS, kernelIndex, "_LtcData", data.areaLightLtc);
                    cmd.SetComputeTextureParam(_deferredLightingCS, kernelIndex, "_ScreenSpaceOcclusionTexture", data.ambientOcclusionTexture);
                    cmd.SetComputeTextureParam(_deferredLightingCS, kernelIndex, "_MainLightCookieTexture", data.mainLightCookieTexture, 0, RenderTextureSubElement.Color);
                    cmd.SetComputeTextureParam(_deferredLightingCS, kernelIndex, "_AdditionalLightsCookieAtlasTexture", data.additionalLightsCookieAtlasTexture);
                    // Parent GBuffer production can precede an entire child scope.
                    // Bind the captured inputs to each kernel rather than sampling
                    // the globals most recently published by another scope.
                    for (int i = 0; i < data.gbuffer.Length; ++i)
                        if (i != gBufferLightingIndex && data.gbuffer[i].IsValid())
                            cmd.SetComputeTextureParam(_deferredLightingCS, kernelIndex, gbufferShaderPropertyIDs[i], data.gbuffer[i]);

                    cmd.DispatchCompute(_deferredLightingCS, kernelIndex, data.dispatchIndirectBuffer, (uint)modelIndex * 3 * sizeof(uint));
                }
            }
        }

        public static ShadingModels GetShadingModels(int modelIndex)
        {
            switch (modelIndex)
            {
                case 0:
                    return ShadingModels.Lit;
                case 1:
                    return ShadingModels.SimpleLit;
                case 2:
                    return ShadingModels.Character;
                default:
                    return ShadingModels.Lit;
            }
        }
    }
}
