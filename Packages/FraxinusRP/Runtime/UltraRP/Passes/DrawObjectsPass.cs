using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable, NodeMenuItem("UltraRP/DrawObjectsPass")]
    public class DrawObjectsPass : ScriptableRenderPassNode
    {
        public enum RenderQueueType
        { 
            Opaque,
            Transparent,
            All
        }

        public enum OverrideMaterialMode
        {
            None,
            Material,
            Shader
        }

        class RenderingLayersPassData
        {
            internal PassData basePassData;
            internal RenderingLayerUtils.MaskSize maskSize;

            public RenderingLayersPassData()
            {
                basePassData = new PassData();
            }
        }

        class PassData
        {
            internal TextureHandle albedoHdl;
            internal TextureHandle depthHdl;

            internal CameraData cameraData;
            internal bool isOpaque;
            internal bool shouldTransparentsReceiveShadows;
            internal uint batchLayerMask;
            internal bool isActiveTargetBackBuffer;
            internal bool contactShadowsEnabled;
            internal ForwardPlusFrameData forwardPlus;

            internal RendererListHandle rendererListHdl;
            internal RendererListHandle objectsWithErrorRendererListHdl;
            internal DebugRendererLists debugRendererLists;
        }

        [Input("Camera Target")]
        public CameraTargetBufferNodeParameter inCameraTarget;

        [Input("Main ShadowMap")]
        public TextureHandleNodeParameter mainShadowMap;

        [Input("Additional ShadowMap")]
        public TextureHandleNodeParameter additionalShadowMap;

        [Output("Camera Target")]
        internal CameraTargetBufferNodeParameter outCameraTarget;

        [SerializeField] private List<string> _shaderTagIdStrings = new List<string>();
        [SerializeField] private LayerMask _layerMask;
        [SerializeField] private bool _isForwardPlus;
        [SerializeField] private bool _isOpaque;
        [SerializeField] private bool _forwardOnlyInDeferred;
        [SerializeField] private RenderQueueType _renderQueueType;
        [SerializeField] private OverrideMaterialMode _overrideMode = OverrideMaterialMode.None;
        [SerializeField] [VisibleIf(nameof(_overrideMode), OverrideMaterialMode.Material), ShowAsObjectDrawer] private Material _overrideMaterial = null;
        [SerializeField] [VisibleIf(nameof(_overrideMode), OverrideMaterialMode.Material)] private int _overrideMaterialPassIndex = 0;
        [SerializeField] [VisibleIf(nameof(_overrideMode), OverrideMaterialMode.Shader), ShowAsObjectDrawer] private Shader _overrideShader = null;
        [SerializeField] [VisibleIf(nameof(_overrideMode), OverrideMaterialMode.Shader)] private int _overrideShaderPassIndex = 0;
        [SerializeField] private bool _overrideDepthState = false;
        [SerializeField] [VisibleIf(nameof(_overrideDepthState), true)] private bool _enableWrite = true;
        [SerializeField] [VisibleIf(nameof(_overrideDepthState), true)] private CompareFunction _depthCompareFunction = CompareFunction.LessEqual;
        [SerializeField] private StencilStateData _stencilSettings = new StencilStateData();
        [SerializeField] private bool _useSortingLayerFilter = false;
        [SerializeField, VisibleIf(nameof(_useSortingLayerFilter), true)] private string _sortingLayerName = "Default";

        static readonly int s_drawObjectPassDataPropID = Shader.PropertyToID("_DrawObjectPassData");
        static readonly int s_contactShadowsEnabledPropID = Shader.PropertyToID("_ContactShadowsEnabled");

        public List<string> shaderTagIdStrings
        {
            get => _shaderTagIdStrings;
            set
            {
                _shaderTagIdStrings = value;
                SetDirty();
            }
        }

        public LayerMask layerMask
        {
            get => _layerMask;
            set
            {
                _layerMask = value;
                SetDirty();
            }
        }

        public bool isForwardPlus
        {
            get => _isForwardPlus;
            set
            {
                _isForwardPlus = value;
            }
        }

        public bool isOpaque
        {
            get => _isOpaque;
            set
            {
                _isOpaque = value;
                SetDirty();
            }
        }

        // A deferred graph still needs its complete forward path for overlay
        // cameras, where the renderer disables deferred lighting for the frame.
        public bool forwardOnlyInDeferred
        {
            get => _forwardOnlyInDeferred;
            set => _forwardOnlyInDeferred = value;
        }

        public RenderQueueType renderQueueType
        {
            get => _renderQueueType;
            set
            {
                _renderQueueType = value;
                SetDirty();
            }
        }

        public Material overrideMaterial
        {
            get => _overrideMaterial;
            set => _overrideMaterial = value;
        }

        public Shader overrideShader
        {
            get => _overrideShader;
            set => _overrideShader = value;
        }

        public bool enableWrite
        {
            get => _enableWrite;
            set
            {
                _enableWrite = value;
                SetDirty();
            }
        }

        public CompareFunction depthCompareFunction
        {
            get => _depthCompareFunction;
            set
            {
                _depthCompareFunction = value;
                SetDirty();
            }
        }

        public StencilStateData stencilStateData
        {
            get => _stencilSettings;
            set
            {
                _stencilSettings = value;
                SetDirty();
            }
        }

        public bool useSortingLayerFilter
        {
            get => _useSortingLayerFilter;
            set
            {
                _useSortingLayerFilter = value;
                SetDirty();
            }
        }

        public string sortingLayerName
        {
            get => _sortingLayerName;
            set
            {
                _sortingLayerName = value;
                SetDirty();
            }
        }

        FilteringSettings _filteringSettings;
        RenderStateBlock _renderStateBlock;
        List<ShaderTagId> _shaderTagIdList = new List<ShaderTagId>();
        readonly List<ShaderTagId> _deferredForwardOnlyTagIds = new List<ShaderTagId>();

        protected override void SetDirty() => FlushRenderSettings();


        public override RenderingMode RequestRenderingMode()
        {
            return _isForwardPlus ? RenderingMode.ForwardPlus : RenderingMode.Forward;
        }

        public DrawObjectsPass() : base()
        {
            base.profilingSampler = new ProfilingSampler(nameof(DrawObjectsPass));
        }

        protected override void Enable()
        {
            base.Enable();

            FlushRenderSettings();
        }

        public override LayerMask GetOpaqueLayerMask()
        {
            return isOpaque ? layerMask : 0;
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

            TextureHandle mainShadowsTexture = mainShadowMap.value;
            TextureHandle additionalShadowsTexture = additionalShadowMap.value;

            ResourceData resourceData   = frameData.Get<ResourceData>();
            RenderingData renderingData = frameData.Get<RenderingData>();
            CameraData cameraData       = frameData.Get<CameraData>();

            var renderer = cameraData.renderer;
            var renderinglayerSetting = renderer.renderinglayerSetting;

            bool needsOccluderUpdate = renderingData.needsOccluderUpdate;
            
            if (isOpaque)
            {
                var passCount = needsOccluderUpdate ? 2 : 1;
                for (int passIndex = 0; passIndex < passCount; ++passIndex)
                {
                    uint batchLayerMask = uint.MaxValue;
                    if (needsOccluderUpdate)
                    {
                        // first pass: test everything against previous frame final depth pyramid
                        // second pass: re-test culled against current frame intermediate depth pyramid
                        OcclusionTest occlusionTest = (passIndex) == 0 ? OcclusionTest.TestAll : OcclusionTest.TestCulled;
                        UltraRenderer.InstanceOcclusionTest(renderGraph, cameraData, occlusionTest);
#if UNITY_6000_0_OR_NEWER
                        batchLayerMask = occlusionTest.GetBatchLayerMask();
#endif
                    }

                    if (renderinglayerSetting.renderingLayerProvidesRenderObjectPass)
                    {
                        RenderWithRenderingLayers(renderGraph,
                            frameData,
                            colorTarget,
                            resourceData.renderingLayersTexture,
                            depthTarget,
                            mainShadowsTexture,
                            additionalShadowsTexture,
                            renderinglayerSetting.renderingLayersMaskSize,
                            batchLayerMask);
                        renderer.SetRenderingLayersGlobalTextures(renderGraph);
                    }
                    else
                        Render(renderGraph, context, frameData, colorTarget, depthTarget, mainShadowsTexture, additionalShadowsTexture, batchLayerMask);

                    if (needsOccluderUpdate)
                    {
                        // first pass: make current frame intermediate depth pyramid
                        // second pass: make current frame final depth pyramid, set occlusion test results for later passes
                        UltraRenderer.UpdateInstanceOccluders(renderGraph, cameraData, resourceData.activeDepthTexture);
                        if (passIndex != 0)
                            UltraRenderer.InstanceOcclusionTest(renderGraph, cameraData, OcclusionTest.TestAll);
                    }
                }

                needsOccluderUpdate = false;

                renderingData.needsOccluderUpdate = needsOccluderUpdate;
            }
            else
                Render(renderGraph, context, frameData, colorTarget, depthTarget, mainShadowsTexture, additionalShadowsTexture);
        }

        private void Render(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData, 
            in TextureHandle colorTarget, in TextureHandle depthTarget,
            in TextureHandle mainShadowsTexture, in TextureHandle additionalShadowsTexture,
            uint batchLayerMask = uint.MaxValue)
        {
            ResourceData resourceData   = frameData.Get<ResourceData>();
            RenderingData renderingData = frameData.Get<RenderingData>();
            CameraData cameraData       = frameData.Get<CameraData>();
            LightData lightData         = frameData.Get<LightData>();

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Draw Objects Pass", out var passData, profilingSampler))
            {
                builder.UseAllGlobalTextures(true);
                APRTRelightPass.UseLightingResources(builder, frameData);
                AreaLightsPass.UseLightingResources(builder, frameData);
                passData.forwardPlus = ForwardLights.UseLightingResources(builder, frameData);
                GroundTruthAmbientOcclusionPass.UseLightingResources(builder, frameData);

                InitPassData(cameraData, ref passData, batchLayerMask, resourceData.isActiveTargetBackBuffer);
                passData.contactShadowsEnabled = CanUseContactShadows(cameraData, resourceData, colorTarget, depthTarget);
                if (passData.contactShadowsEnabled)
                    builder.UseTexture(resourceData.contactShadowsTexture, AccessFlags.Read);

                passData.albedoHdl = colorTarget;
                builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);

#if ENABLE_VIRTUALTEXTURES
                BindVirtualTextureFeedback(builder, resourceData, cameraData, colorTarget, depthTarget, 1);
#endif

                passData.depthHdl = depthTarget;
                builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Write);

                if (mainShadowsTexture.IsValid())
                    builder.UseTexture(mainShadowsTexture, AccessFlags.Read);

                if (additionalShadowsTexture.IsValid())
                    builder.UseTexture(additionalShadowsTexture, AccessFlags.Read);

                InitRendererLists(renderingData, cameraData, lightData, ref passData, default(ScriptableRenderContext), renderGraph);
                var activeDebugHandler = GetActiveDebugHandler(cameraData);
                if (activeDebugHandler != null)
                    passData.debugRendererLists.PrepareRendererListForRasterPass(builder);
                else
                {
                    builder.UseRendererList(passData.rendererListHdl);
                    builder.UseRendererList(passData.objectsWithErrorRendererListHdl);
                }

                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    ForwardLights.BindLightLists(context.cmd, data.forwardPlus);
                    bool yFlip = data.cameraData.IsRenderTargetProjectionMatrixFlipped(data.albedoHdl);
                    ExecutePass(context.cmd, data, data.rendererListHdl, data.objectsWithErrorRendererListHdl, yFlip);
                });
            }
        }

        private void RenderWithRenderingLayers(RenderGraph renderGraph, ContextContainer frameData, 
            TextureHandle colorTarget, TextureHandle renderingLayersTexture, TextureHandle depthTarget, 
            TextureHandle mainShadowsTexture, TextureHandle additionalShadowsTexture, 
            RenderingLayerUtils.MaskSize maskSize, uint batchLayerMask = uint.MaxValue)
        {
            using (var builder = renderGraph.AddRasterRenderPass<RenderingLayersPassData>(passName, out var passData, profilingSampler))
            {
                APRTRelightPass.UseLightingResources(builder, frameData);
                AreaLightsPass.UseLightingResources(builder, frameData);
                passData.basePassData.forwardPlus = ForwardLights.UseLightingResources(builder, frameData);
                GroundTruthAmbientOcclusionPass.UseLightingResources(builder, frameData);
                ResourceData resourceData   = frameData.Get<ResourceData>();
                RenderingData renderingData = frameData.Get<RenderingData>();
                CameraData cameraData       = frameData.Get<CameraData>();
                LightData lightData         = frameData.Get<LightData>();

                InitPassData(cameraData, ref passData.basePassData, batchLayerMask);
                passData.basePassData.contactShadowsEnabled = CanUseContactShadows(cameraData, resourceData, colorTarget, depthTarget);
                if (passData.basePassData.contactShadowsEnabled)
                    builder.UseTexture(resourceData.contactShadowsTexture, AccessFlags.Read);
                passData.maskSize = maskSize;

                passData.basePassData.albedoHdl = colorTarget;
                builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);
                builder.SetRenderAttachment(renderingLayersTexture, 1, AccessFlags.Write);
#if ENABLE_VIRTUALTEXTURES
                BindVirtualTextureFeedback(builder, resourceData, cameraData, colorTarget, depthTarget, 2);
#endif
                passData.basePassData.depthHdl = depthTarget;
                builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Write);
                if (mainShadowsTexture.IsValid())
                    builder.UseTexture(mainShadowsTexture, AccessFlags.Read);
                if (additionalShadowsTexture.IsValid())
                    builder.UseTexture(additionalShadowsTexture, AccessFlags.Read);

                InitRendererLists(renderingData, cameraData, lightData, ref passData.basePassData, default(ScriptableRenderContext), renderGraph);
                var activeDebugHandler = GetActiveDebugHandler(cameraData);
                if (activeDebugHandler != null)
                    passData.basePassData.debugRendererLists.PrepareRendererListForRasterPass(builder);
                else
                {
                    builder.UseRendererList(passData.basePassData.rendererListHdl);
                    builder.UseRendererList(passData.basePassData.objectsWithErrorRendererListHdl);
                }

                // Required here because of RenderingLayerUtils.SetupProperties
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((RenderingLayersPassData data, RasterGraphContext context) =>
                {
                    ForwardLights.BindLightLists(context.cmd, data.basePassData.forwardPlus);
                    // Enable Rendering Layers
                    context.cmd.SetKeyword(ShaderGlobalKeywords.WriteRenderingLayers, true);

                    RenderingLayerUtils.SetupProperties(context.cmd, data.maskSize);

                    bool yFlip = data.basePassData.cameraData.IsRenderTargetProjectionMatrixFlipped(data.basePassData.albedoHdl, data.basePassData.depthHdl);

                    // Execute
                    ExecutePass(context.cmd, data.basePassData, data.basePassData.rendererListHdl, data.basePassData.objectsWithErrorRendererListHdl, yFlip);

                    // Clean up
                    context.cmd.SetKeyword(ShaderGlobalKeywords.WriteRenderingLayers, false);
                });
            }
        }

        bool CanUseContactShadows(CameraData cameraData, ResourceData resources, TextureHandle color, TextureHandle depth)
        {
            return isOpaque && !cameraData.isSubContext && resources.contactShadowsTexture.IsValid() &&
                RenderGraphUtils.TextureHandlesEqual(color, resources.activeColorTexture) &&
                RenderGraphUtils.TextureHandlesEqual(depth, resources.activeDepthTexture);
        }

        void FlushRenderSettings()
        {
            _deferredForwardOnlyTagIds.Clear();
            _deferredForwardOnlyTagIds.Add(new ShaderTagId("UltraForwardOnly"));
            _deferredForwardOnlyTagIds.Add(new ShaderTagId("SRPDefaultUnlit"));
            _shaderTagIdList.Clear();
            _shaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
            if (_shaderTagIdStrings != null && _shaderTagIdStrings.Count > 0)
            {
                foreach (var shaderTagString in _shaderTagIdStrings)
                    _shaderTagIdList.Add(new ShaderTagId(shaderTagString));
            }

            switch (renderQueueType)
            {
                case RenderQueueType.Opaque:
                    _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, layerMask);
                    break;
                case RenderQueueType.Transparent:
                    _filteringSettings = new FilteringSettings(RenderQueueRange.transparent, layerMask);
                    break;
                case RenderQueueType.All:
                    _filteringSettings = new FilteringSettings(RenderQueueRange.all, layerMask);
                    break;
            }

            // Sorting Layer 过滤
            if (_useSortingLayerFilter)
            {
                if (!string.IsNullOrEmpty(_sortingLayerName) && SortingLayer.IsValid(SortingLayer.NameToID(_sortingLayerName)))
                {
                    int layerId = SortingLayer.NameToID(_sortingLayerName);
                    int layerValue = SortingLayer.GetLayerValueFromID(layerId);
                    _filteringSettings.sortingLayerRange = new SortingLayerRange((short)layerValue, (short)layerValue);
                }
                else
                    _filteringSettings.sortingLayerRange = new SortingLayerRange(short.MinValue, short.MinValue);
            }

            _renderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);

            if (_overrideDepthState)
                SetDepthState(_enableWrite, _depthCompareFunction);

            if (_stencilSettings.overrideStencilState)
                SetStencilState(_stencilSettings.stencilReference, _stencilSettings.stencilCompareFunction, _stencilSettings.passOperation,
                    _stencilSettings.failOperation, _stencilSettings.zFailOperation);
        }

        void InitPassData(CameraData cameraData, ref PassData passData, uint batchLayerMask, bool isActiveTargetBackBuffer = false)
        {
            passData.cameraData = cameraData;
            passData.isOpaque = _isOpaque;
            passData.shouldTransparentsReceiveShadows = false;
            passData.batchLayerMask = batchLayerMask;
            passData.isActiveTargetBackBuffer = isActiveTargetBackBuffer;
        }

#if ENABLE_VIRTUALTEXTURES
        static void BindVirtualTextureFeedback(IRasterRenderGraphBuilder builder, ResourceData resources,
            CameraData cameraData, TextureHandle color, TextureHandle depth, int attachment)
        {
            // Child contexts and custom node targets have their own dimensions and lifetime.
            // Never mix those requests into the main camera's feedback buffer.
            if (!cameraData.isSubContext && resources.virtualTextureFeedback.IsValid() &&
                RenderGraphUtils.TextureHandlesEqual(color, resources.cameraColor) &&
                RenderGraphUtils.TextureHandlesEqual(depth, resources.cameraDepth))
                builder.SetRenderAttachment(resources.virtualTextureFeedback, attachment, AccessFlags.ReadWrite);
        }
#endif

        void InitRendererLists(RenderingData renderingData, CameraData cameraData, LightData lightData, ref PassData passData, ScriptableRenderContext context, RenderGraph renderGraph)
        {
            ref Camera camera = ref cameraData.camera;
            var sortFlags = (_isOpaque) ? cameraData.defaultOpaqueSortFlags : SortingCriteria.CommonTransparent;
            if (!cameraData.isSubContext && cameraData.renderer.useDepthPriming && _isOpaque && (cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth))
                sortFlags = SortingCriteria.SortingLayer | SortingCriteria.RenderQueue | SortingCriteria.OptimizeStateChanges | SortingCriteria.CanvasOrder;

            var filterSettings = _filteringSettings;
#if UNITY_6000_0_OR_NEWER
            filterSettings.batchLayerMask = passData.batchLayerMask;
#endif

#if UNITY_EDITOR
            // When rendering the preview camera, we want the layer mask to be forced to Everything
            if (cameraData.isPreviewCamera)
                filterSettings.layerMask = -1;
#endif

            var shaderTags = _isOpaque && _forwardOnlyInDeferred && renderingData.hasDeferred
                ? _deferredForwardOnlyTagIds : _shaderTagIdList;
            DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(shaderTags, renderingData, cameraData, lightData, sortFlags);
            if (!cameraData.isSubContext && cameraData.renderer.useDepthPriming && _isOpaque && (cameraData.renderType == CameraRenderType.Base || cameraData.clearDepth))
            {
                _renderStateBlock.depthState = new DepthState(false, CompareFunction.Equal);
                _renderStateBlock.mask |= RenderStateMask.Depth;
            }
            else if (_renderStateBlock.depthState.compareFunction == CompareFunction.Equal)
            {
                _renderStateBlock.depthState = new DepthState(true, CompareFunction.LessEqual);
                _renderStateBlock.mask |= RenderStateMask.Depth;
            }

            Material material = null;
            Shader shader = null;
            switch (_overrideMode)
            {
                case OverrideMaterialMode.None:
                    break;
                case OverrideMaterialMode.Material:
                    material = overrideMaterial;
                    break;
                case OverrideMaterialMode.Shader:
                    shader = overrideShader;
                    break;
            }

            drawingSettings.overrideMaterial = material;
            drawingSettings.overrideMaterialPassIndex = _overrideMaterialPassIndex;
            drawingSettings.overrideShader = shader;
            drawingSettings.overrideShaderPassIndex = _overrideShaderPassIndex;
            var activeDebugHandler = GetActiveDebugHandler(cameraData);
            if (activeDebugHandler != null)
                passData.debugRendererLists = activeDebugHandler.CreateRendererListsWithDebugRenderState(renderGraph, ref renderingData.cullResults, ref drawingSettings, ref filterSettings, ref _renderStateBlock);
            else
            {
                RenderingUtils.CreateRendererListWithRenderStateBlock(renderGraph, ref renderingData.cullResults, drawingSettings, filterSettings, _renderStateBlock, ref passData.rendererListHdl);
                RenderingUtils.CreateRendererListObjectsWithError(renderGraph, ref renderingData.cullResults, camera, filterSettings, sortFlags, ref passData.objectsWithErrorRendererListHdl);
            }
        }

        void SetDepthState(bool writeEnabled, CompareFunction function = CompareFunction.Less)
        {
            _renderStateBlock.mask |= RenderStateMask.Depth;
            _renderStateBlock.depthState = new DepthState(writeEnabled, function);
        }

        void SetStencilState(int reference, CompareFunction compareFunction, StencilOp passOp, StencilOp failOp, StencilOp zFailOp)
        {
            StencilState stencilState = StencilState.defaultValue;
            stencilState.enabled = true;
            stencilState.SetCompareFunction(compareFunction);
            stencilState.SetPassOperation(passOp);
            stencilState.SetFailOperation(failOp);
            stencilState.SetZFailOperation(zFailOp);

            _renderStateBlock.mask |= RenderStateMask.Stencil;
            _renderStateBlock.stencilReference = reference;
            _renderStateBlock.stencilState = stencilState;
        }

        static void ExecutePass(RasterCommandBuffer cmd, PassData data, RendererList rendererList, RendererList objectsWithErrorRendererList, bool yFlip)
        {
            // A child context or a transparent draw may follow an enabled main draw.
            // Capture the value while recording and restore it for every consumer.
            cmd.SetGlobalFloat(s_contactShadowsEnabledPropID, data.contactShadowsEnabled ? 1.0f : 0.0f);
            Vector4 drawObjectPassData = new Vector4(0.0f, 0.0f, 0.0f, (data.isOpaque) ? 1.0f : 0.0f);
            cmd.SetGlobalVector(s_drawObjectPassDataPropID, drawObjectPassData);

            // scaleBias.x = flipSign
            // scaleBias.y = scale
            // scaleBias.z = bias
            // scaleBias.w = unused
            float flipSign = yFlip ? -1.0f : 1.0f;
            Vector4 scaleBias = (flipSign < 0.0f)
                ? new Vector4(flipSign, 1.0f, -1.0f, 1.0f)
                : new Vector4(flipSign, 0.0f, 1.0f, 1.0f);
            cmd.SetGlobalVector(ShaderPropertyId.scaleBiasRt, scaleBias);

            // Set a value that can be used by shaders to identify when AlphaToMask functionality may be active
            // The material shader alpha clipping logic requires this value in order to function correctly in all cases.
            float alphaToMaskAvailable = ((data.cameraData.cameraTargetDescriptor.msaaSamples > 1) && data.isOpaque) ? 1.0f : 0.0f;
            cmd.SetGlobalFloat(ShaderPropertyId.alphaToMaskAvailable, alphaToMaskAvailable);

            var activeDebugHandler = GetActiveDebugHandler(data.cameraData);
            if (activeDebugHandler != null)
                data.debugRendererLists.DrawWithRendererList(cmd);
            else
            {
                cmd.DrawRendererList(rendererList);
                RenderingUtils.DrawRendererListObjectsWithError(cmd, ref objectsWithErrorRendererList);
            }
        }
    }
}
