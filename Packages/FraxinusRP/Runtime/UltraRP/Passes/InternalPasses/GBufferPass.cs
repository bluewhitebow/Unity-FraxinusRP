using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class GBufferPass : BuiltinPass
    {
        private class PassData
        {
            internal TextureHandle[] gbuffer;
            internal TextureHandle depth;

            internal DeferredLights deferredLights;
            internal ForwardPlusFrameData forwardPlus;

            internal RendererListHandle rendererListHdl;
            internal RendererListHandle objectsWithErrorRendererListHdl;

            // Required for code sharing purpose between RG and non-RG.
            internal RendererList rendererList;
            internal RendererList objectsWithErrorRendererList;
        }

        // Statics
        private static readonly int s_cameraNormalsTextureID = Shader.PropertyToID("_CameraNormalsTexture");
        private static readonly int s_cameraRenderingLayersTextureID = Shader.PropertyToID("_CameraRenderingLayersTexture");
        private static readonly ShaderTagId s_shaderTagLit = new ShaderTagId("Lit");
        private static readonly ShaderTagId s_shaderTagSimpleLit = new ShaderTagId("SimpleLit");
        private static readonly ShaderTagId s_shaderTagUnlit = new ShaderTagId("Unlit");
        private static readonly ShaderTagId s_shaderTagComplexLit = new ShaderTagId("ComplexLit");
        private static readonly ShaderTagId s_shaderTagUltraGBuffer = new ShaderTagId("UniversalGBuffer");
        private static readonly ShaderTagId s_shaderTagUltraMaterialType = new ShaderTagId("UltraMaterialType");

        static ShaderTagId[] s_shaderTagValues;

        private FilteringSettings _filteringSettings;
        private RenderStateBlock[] _renderStateBlocks;

        private RenderStateBlock _renderStateBlock;

        private LayerMask _layerMask;
        public LayerMask layerMask
        {
            get => _layerMask;
            set
            {
                _layerMask = value;
                SetDirty();
            }
        }


        public bool gbufferDepthPriming;

        public bool setGlobalTextures;

        static StencilStateData s_defaultStencilState = new StencilStateData() { passOperation = StencilOp.Replace };

        public GBufferPass() : base()
        {
            _profilingSampler = new ProfilingSampler("Draw GBuffer");

            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, layerMask);

            StencilStateData stencilData = s_defaultStencilState;
            StencilState defaultStencilState = StencilState.defaultValue;
            defaultStencilState.enabled = stencilData.overrideStencilState;
            defaultStencilState.SetCompareFunction(stencilData.stencilCompareFunction);
            defaultStencilState.SetPassOperation(stencilData.passOperation);
            defaultStencilState.SetFailOperation(stencilData.failOperation);
            defaultStencilState.SetZFailOperation(stencilData.zFailOperation);


            _renderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);
            _renderStateBlock.stencilState = defaultStencilState;
            _renderStateBlock.stencilReference = stencilData.stencilReference;
            _renderStateBlock.mask = RenderStateMask.Stencil;

            if (s_shaderTagValues == null)
            {
                s_shaderTagValues = new ShaderTagId[5];
                s_shaderTagValues[0] = s_shaderTagLit;
                s_shaderTagValues[1] = s_shaderTagSimpleLit;
                s_shaderTagValues[2] = s_shaderTagUnlit;
                s_shaderTagValues[3] = s_shaderTagComplexLit;
                s_shaderTagValues[4] = new ShaderTagId();// Special catch all case for materials where UltraMaterialType is not defined or the tag value doesn't match anything we know.
            }
        }

        protected override void OnCameraCleanup(CommandBuffer cmd) 
        {
            gbufferDepthPriming = true;
            setGlobalTextures = false;
        }

        protected override void OnDispose() { }


        void SetDirty() => FlushRenderSettings();

        void FlushRenderSettings()
        {
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, _layerMask);
        }

        internal void ConfigureRendererListsSettings(RenderStateBlock[] renderStateBlocks, FilteringSettings filteringSettings)
        {
            _renderStateBlocks = renderStateBlocks;
            _filteringSettings = filteringSettings;
        }

        internal void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle cameraColor, TextureHandle cameraDepth)
        {
            ResourceData resourceData   = frameData.Get<ResourceData>();
            RenderingData renderingData = frameData.Get<RenderingData>();
            CameraData cameraData       = frameData.Get<CameraData>();
            LightData lightData         = frameData.Get<LightData>();

            var renderer = cameraData.renderer;
            var deferredLights = renderer.GetModule<DeferredLights>();

            TextureHandle[] gbuffer;

            bool useCameraRenderingLayersTexture = deferredLights.useRenderingLayers && !deferredLights.useLightLayers;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(_passName, out var passData, _profilingSampler))
            {
                APRTRelightPass.UseLightingResources(builder, frameData);
                passData.forwardPlus = ForwardLights.UseLightingResources(builder, frameData);
                GroundTruthAmbientOcclusionPass.UseLightingResources(builder, frameData);
                // Note: This code is pretty confusing as passData.gbuffer[i] and gbuffer[i] actually point to the same array but seem to be mixed in this code.
                if (passData.gbuffer == null || passData.gbuffer.Length != deferredLights.gBufferSliceCount)
                    passData.gbuffer = new TextureHandle[deferredLights.gBufferSliceCount];
                gbuffer = passData.gbuffer;
                for (int i = 0; i < deferredLights.gBufferSliceCount; i++)
                {
                    var gbufferSlice = cameraData.cameraTargetDescriptor;
                    gbufferSlice.depthStencilFormat = GraphicsFormat.None; // make sure no depth surface is actually created
                    gbufferSlice.stencilFormat = GraphicsFormat.None;

                    if (i == deferredLights.gBufferNormalSmoothnessIndex && deferredLights.hasNormalPrepass && !cameraData.isSubContext)
                        gbuffer[i] = resourceData.cameraNormalsTexture;
                    else if (i == deferredLights.gBufferRenderingLayers && useCameraRenderingLayersTexture)
                        gbuffer[i] = resourceData.renderingLayersTexture;
                    else if (i != deferredLights.gBufferLightingIndex)
                    {
                        gbufferSlice.graphicsFormat = deferredLights.GetGBufferFormat(i);
                        gbuffer[i] = UltraRenderer.CreateRenderGraphTexture(renderGraph, gbufferSlice, DeferredLights.gbufferNames[i], true);
                    }
                    else
                        gbuffer[i] = cameraColor;

                    // Note: We don't store the returned handle here it is a versioned handle.
                    // In general it should be fine to use unversioned handles anyway especially unversioned resources
                    // should be registered in the frame data
                    builder.SetRenderAttachment(gbuffer[i], i, AccessFlags.Write);
                }

                RenderGraphUtils.UseDBufferIfValid(builder, resourceData);
                resourceData.gBuffer = gbuffer;
                resourceData.cameraNormalsTexture = gbuffer[deferredLights.gBufferNormalSmoothnessIndex];

                passData.depth = cameraDepth;
                builder.SetRenderAttachmentDepth(cameraDepth, AccessFlags.Write);
                passData.deferredLights = deferredLights;

                InitRendererLists(ref passData, default(ScriptableRenderContext), renderGraph, renderingData, cameraData, lightData);
                builder.UseRendererList(passData.rendererListHdl);
                builder.UseRendererList(passData.objectsWithErrorRendererListHdl);

                if (setGlobalTextures || cameraData.isSubContext)
                {
                    builder.SetGlobalTextureAfterPass(resourceData.cameraNormalsTexture, s_cameraNormalsTextureID);
                    if (useCameraRenderingLayersTexture)
                        builder.SetGlobalTextureAfterPass(resourceData.renderingLayersTexture, s_cameraRenderingLayersTextureID);
                }

                SetGlobalGBufferTextures(builder, gbuffer, ref deferredLights);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    ForwardLights.BindLightLists(context.cmd, data.forwardPlus);
                    ExecutePass(context.cmd, data, data.rendererListHdl, data.objectsWithErrorRendererListHdl);
                });
            }
        }

        private void InitRendererLists(ref PassData passData, ScriptableRenderContext context, RenderGraph renderGraph, RenderingData renderingData, CameraData cameraData, LightData lightData)
        {
            ShaderTagId lightModeTag = s_shaderTagUltraGBuffer;
            var drawingSettings = CreateDrawingSettings(lightModeTag, renderingData, cameraData, lightData, cameraData.defaultOpaqueSortFlags);
            var filterSettings = _filteringSettings;

            if (gbufferDepthPriming && !cameraData.isSubContext && cameraData.renderer.useDepthPriming && (cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth))
            {
                _renderStateBlock.depthState = new DepthState(false, CompareFunction.Equal);
                _renderStateBlock.mask |= RenderStateMask.Depth;
            }
            else if (_renderStateBlock.depthState.compareFunction == CompareFunction.Equal)
            {
                _renderStateBlock.depthState = new DepthState(true, CompareFunction.LessEqual);
                _renderStateBlock.mask |= RenderStateMask.Depth;
            }

            if (_renderStateBlocks == null)
                _renderStateBlocks = new RenderStateBlock[5];

            _renderStateBlocks[0] = DeferredLights.OverwriteStencil(_renderStateBlock, (int)StencilUsage.MaterialMask, (int)StencilUsage.MaterialLit);
            _renderStateBlocks[1] = DeferredLights.OverwriteStencil(_renderStateBlock, (int)StencilUsage.MaterialMask, (int)StencilUsage.MaterialSimpleLit);
            _renderStateBlocks[2] = DeferredLights.OverwriteStencil(_renderStateBlock, (int)StencilUsage.MaterialMask, (int)StencilUsage.MaterialUnlit);
            _renderStateBlocks[3] = DeferredLights.OverwriteStencil(_renderStateBlock, (int)StencilUsage.MaterialMask, (int)StencilUsage.MaterialUnlit);  // Fill GBuffer, but skip lighting pass for ComplexLit
            _renderStateBlocks[4] = _renderStateBlocks[0];

            NativeArray<ShaderTagId> tagValues = new NativeArray<ShaderTagId>(s_shaderTagValues, Allocator.Temp);
            NativeArray<RenderStateBlock> stateBlocks = new NativeArray<RenderStateBlock>(_renderStateBlocks, Allocator.Temp);
            var param = new RendererListParams(renderingData.cullResults, drawingSettings, filterSettings)
            {
                tagValues = tagValues,
                stateBlocks = stateBlocks,
                tagName = s_shaderTagUltraMaterialType,
                isPassTagName = false
            };

            passData.rendererListHdl = renderGraph.CreateRendererList(param);

            tagValues.Dispose();
            stateBlocks.Dispose();

            RenderingUtils.CreateRendererListObjectsWithError(renderGraph, ref renderingData.cullResults, cameraData.camera, filterSettings, SortingCriteria.None, ref passData.objectsWithErrorRendererListHdl);
        }

        static void ExecutePass(RasterCommandBuffer cmd, PassData data, RendererList rendererList, RendererList errorRendererList)
        {
            bool usesRenderingLayers = data.deferredLights.useRenderingLayers && !data.deferredLights.hasRenderingLayerPrepass;
            if (usesRenderingLayers)
                cmd.SetKeyword(ShaderGlobalKeywords.WriteRenderingLayers, true);

            if (data.deferredLights.isOverlay)
                data.deferredLights.ClearStencilPartial(cmd);

            cmd.DrawRendererList(rendererList);

            // Render objects that did not match any shader pass with error shader
            RenderingUtils.DrawRendererListObjectsWithError(cmd, ref errorRendererList);

            // Clean up
            if (usesRenderingLayers)
                cmd.SetKeyword(ShaderGlobalKeywords.WriteRenderingLayers, false);
        }

        private DrawingSettings CreateDrawingSettings(ShaderTagId shaderTagId, RenderingData renderingData,
            CameraData cameraData, LightData lightData, SortingCriteria sortingCriteria)
        {
            return RenderingUtils.CreateDrawingSettings(shaderTagId, renderingData, cameraData, lightData, sortingCriteria);
        }

        private static void SetGlobalGBufferTextures(IRasterRenderGraphBuilder builder, TextureHandle[] gbuffer, ref DeferredLights deferredLights)
        {
            for (int i = 0; i < gbuffer.Length; i++)
            {
                if (i != deferredLights.gBufferLightingIndex && gbuffer[i].IsValid())
                    builder.SetGlobalTextureAfterPass(gbuffer[i], Shader.PropertyToID(DeferredLights.gbufferNames[i]));
            }

            // If any sub-system needs camera normal texture, make it available.
            // Input attachments will only be used when this is not needed so safe to skip in that case
            if (gbuffer[deferredLights.gBufferLightingIndex].IsValid())
                builder.SetGlobalTextureAfterPass(gbuffer[deferredLights.gBufferNormalSmoothnessIndex], s_cameraNormalsTextureID);

            // if (deferredLights.UseRenderingLayers && gbuffer[deferredLights.GBufferRenderingLayers].IsValid())
            // {
            //     builder.SetGlobalTextureAfterPass(gbuffer[deferredLights.GBufferRenderingLayers], Shader.PropertyToID(DeferredLights.k_GBufferNames[deferredLights.GBufferRenderingLayers]));
            //     builder.SetGlobalTextureAfterPass(gbuffer[deferredLights.GBufferRenderingLayers], s_CameraRenderingLayersTextureID);
            // }
        }
    }
}

