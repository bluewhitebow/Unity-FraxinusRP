using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// InjectPass过滤模式
    /// 控制子作用域如何过滤InjectPass的注入
    /// </summary>
    public enum InjectPassFilterMode
    {
        /// <summary>自动：使用InjectPass声明的scopeFilter</summary>
        Auto,
        
        /// <summary>禁用：子作用域不注入任何InjectPass</summary>
        Disabled
    }
    
    /// <summary>
    /// 分辨率模式
    /// 控制子作用域的分辨率计算方式
    /// </summary>
    public enum ResolutionMode
    {
        ScaleFactor
    }
    
    /// <summary>
    /// 子上下文初始化模式
    /// </summary>
    public enum SubContextInitMode
    {
        Clear,
        CopyFromMain
    }
    
    [Serializable, NodeMenuItem("Context/ContextBegin")]
    public class ContextBeginNode : ScriptableRenderPassNode
    {
        [Header("Resolution Settings")]
        [SerializeField, Range(0.1f, 1.0f)] private float _scaleFactor = 0.5f;
        
        [Header("Initialization")]
        [SerializeField] private SubContextInitMode _initMode = SubContextInitMode.Clear;
        
        [Header("InjectPass Control")]
        [SerializeField] private InjectPassFilterMode _injectPassMode = InjectPassFilterMode.Auto;
        
        [Input("From Main CameraTarget")]
        public CameraTargetBufferNodeParameter inCameraTarget;
        
        [Output("To Sub CameraTarget", allowMultiple = true)]
        public CameraTargetBufferNodeParameter outCameraTarget;
        
        
        public InjectPassFilterMode injectPassMode => _injectPassMode;
        
        // 缓存 ResourceData 和 CameraData 实例，避免每帧分配
        private ResourceData _cachedSubResourceData;
        private CameraData _cachedSubCameraData;
        private readonly RenderingData _subRenderingData = new();
        private readonly GPULightsOutPassData _subGPULights = new();
        private readonly DeferredClassifyDebugBuffersData _subClassifyDebug = new();
        private readonly ContextData _subContextData = new();
        
        public ContextBeginNode() : base()
        {
            profilingSampler = new ProfilingSampler(nameof(ContextBeginNode));
        }
        
        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            var state = frameData.GetOrCreate<ContextStateManager>();
            state.EnsureMainContext(frameData);
            var parent = state.GetCurrentContext();
            parent.textureBindings = ContextRenderState.CaptureTextures(renderGraph);
            var resourceData = frameData.Get<ResourceData>();
            var cameraData = frameData.Get<CameraData>();
            var renderingData = frameData.Get<RenderingData>();
            var parentTarget = inCameraTarget.value;
            if (!parentTarget.IsValid())
                parentTarget = new CameraTargetBuffer(resourceData.activeColorTexture, resourceData.activeDepthTexture);

            var parentDesc = renderGraph.GetTextureDesc(parentTarget.color);
            int width = Mathf.Max(1, (int)(parentDesc.width * Mathf.Clamp(_scaleFactor, 0.1f, 1f)));
            int height = Mathf.Max(1, (int)(parentDesc.height * Mathf.Clamp(_scaleFactor, 0.1f, 1f)));
            _cachedSubResourceData ??= new ResourceData();
            _cachedSubCameraData ??= new CameraData();
            UpdateSubResourceData(renderGraph, _cachedSubResourceData, resourceData, cameraData, width, height,
                renderingData.hasDeferredCluster && !cameraData.renderer.GetModule<DeferredLights>().useClusterRaster);
            UpdateCameraData(_cachedSubCameraData, cameraData, width, height);
            CopyRenderingData(_subRenderingData, renderingData);
            _subContextData.Clear();
            _subContextData.Set(_cachedSubResourceData);
            _subContextData.Set(_cachedSubCameraData);
            _subContextData.Set(_subRenderingData);
            _subGPULights.Reset();
            _subClassifyDebug.Reset();
            _subContextData.Set(_subGPULights);
            _subContextData.Set(_subClassifyDebug);
            state.PushContext(parent);
            state.EnterSubContext(new ContextInfo
            {
                scope = ContextScope.Sub,
                contextId = state.AllocateSubContextId(),
                contextName = "SubContext",
                data = _subContextData,
                injectPassMode = parent.injectPassMode == InjectPassFilterMode.Disabled ? InjectPassFilterMode.Disabled : _injectPassMode,
                startTime = Time.realtimeSinceStartup
            });

            if (_initMode == SubContextInitMode.CopyFromMain)
                CopyFromParentContext(renderGraph, parentTarget.color, _cachedSubResourceData);
            else
                ClearSubContextTextures(renderGraph, _cachedSubResourceData, cameraData);

            cameraData.renderer.SetupRenderGraphCameraProperties(renderGraph, false);
            RenderForwardPrepass(renderGraph, frameData, _subRenderingData, _cachedSubResourceData, cameraData.renderer);
            ContextRenderState.PublishTextures(renderGraph, _cachedSubResourceData);
            if (_subRenderingData.hasDeferredCluster)
                cameraData.renderer.GetModule<DeferredLights>().RenderContextLightGrid(renderGraph, frameData);
            ConfigurePortValue(outCameraTarget, new CameraTargetBuffer(_cachedSubResourceData.cameraColor, _cachedSubResourceData.cameraDepth));
        }

        private static void CopyRenderingData(RenderingData target, RenderingData source)
        {
            target.Reset();
            target.cullResults = source.cullResults;
            target.supportsDynamicBatching = source.supportsDynamicBatching;
            target.perObjectData = source.perObjectData;
            target.renderPassInputs = source.renderPassInputs;
            target.opaqueLayerMask = source.opaqueLayerMask;
            target.requiresDepthPrepass = source.requiresDepthPrepass;
            target.hasForwardPlus = source.hasForwardPlus;
            target.hasDeferred = source.hasDeferred;
            target.hasDeferredCluster = source.hasDeferredCluster;
            target.clusterLightingMode = source.clusterLightingMode;
            // Occlusion history belongs to the main camera's depth surface.
            target.needsOccluderUpdate = false;
        }

        private static void RenderForwardPrepass(RenderGraph graph, ContextContainer frame,
            RenderingData rendering, ResourceData resources, ScriptableRenderer renderer)
        {
            if (rendering.hasDeferred || !rendering.requiresDepthPrepass)
                return;

            // The main RenderDriven pass can satisfy depth through a prepass and
            // disable its later opaque depth copy. Its prepass did not render into
            // this child's targets, so produce the child's depth/normals here too.
            // Children do not use main-camera depth priming or occlusion history.
            var depth = resources.cameraDepthTexture;
            if (rendering.renderPassInputs.requiresNormalsTexture)
            {
                var prepass = renderer.GetModule<DepthNormalOnlyPass>();
                prepass.layerMask = rendering.opaqueLayerMask;
                prepass.Render(graph, frame, resources.cameraNormalsTexture, depth,
                    resources.renderingLayersTexture, uint.MaxValue, true, true);
            }
            else
            {
                var prepass = renderer.GetModule<DepthOnlyPass>();
                prepass.layerMask = rendering.opaqueLayerMask;
                prepass.Render(graph, frame, ref depth, uint.MaxValue, true);
            }
        }

        private class ClearPassData { }
        
        private class CopyPassData
        {
            internal TextureHandle srcColor;
            internal TextureHandle dstColor;
            internal TextureHandle dstDepth;
        }
        
        private void ClearSubContextTextures(RenderGraph renderGraph, ResourceData subResourceData, CameraData cameraData)
        {
            using (var builder = renderGraph.AddRasterRenderPass<ClearPassData>(
                "Clear SubContext Textures", out var passData, profilingSampler))
            {
                
                builder.SetRenderAttachment(subResourceData.cameraColor, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(subResourceData.cameraDepth, AccessFlags.Write);
                
                builder.AllowPassCulling(false);
                
                builder.SetRenderFunc((ClearPassData data, RasterGraphContext context) =>
                {
                    
                });
            }
        }
        
        private void CopyFromParentContext(RenderGraph renderGraph, TextureHandle parentColor, ResourceData subResourceData)
        {
            using (var builder = renderGraph.AddRasterRenderPass<CopyPassData>(
                "Copy MainContext Color to SubContext", out var passData, profilingSampler))
            {
                passData.srcColor = parentColor;
                passData.dstColor = subResourceData.cameraColor;
                passData.dstDepth = subResourceData.cameraDepth;
                
                builder.UseTexture(parentColor, AccessFlags.Read);
                builder.SetRenderAttachment(subResourceData.cameraColor, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(subResourceData.cameraDepth, AccessFlags.Write); // 清除深度
                
                builder.AllowPassCulling(false);
                
                builder.SetRenderFunc((CopyPassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.srcColor, new Vector4(1, 1, 0, 0), 0, false);
                });
            }
        }
        
        private static TextureHandle CreateScaledTexture(RenderGraph graph, TextureHandle parent, int width, int height, string name)
        {
            if (!parent.IsValid())
                return TextureHandle.nullHandle;
            var desc = graph.GetTextureDesc(parent);
            desc.width = width;
            desc.height = height;
            desc.sizeMode = TextureSizeMode.Explicit;
            desc.msaaSamples = MSAASamples.None;
            desc.bindTextureMS = false;
            desc.useDynamicScale = false;
            desc.clearBuffer = true;
            desc.clearColor = Color.clear;
            desc.name = name;
            var texture = graph.CreateTexture(desc);
            // A descriptor's clear flag only takes effect at the first write.
            // Publishing a never-written handle as a global does not create it.
            using (var builder = graph.AddRasterRenderPass<ClearPassData>("Initialize SubContext Texture", out _))
            {
                if (desc.depthBufferBits != DepthBits.None)
                    builder.SetRenderAttachmentDepth(texture, AccessFlags.Write);
                else
                    builder.SetRenderAttachment(texture, 0, AccessFlags.Write);
                builder.SetRenderFunc((ClearPassData data, RasterGraphContext context) => { });
            }
            return texture;
        }

        private void UpdateSubResourceData(RenderGraph graph, ResourceData sub, ResourceData parent, CameraData camera, int width, int height, bool randomWrite)
        {
            sub.Reset();
            sub.InitFrame();
            sub.activeColorID = ResourceBase.ActiveID.Camera;
            sub.activeDepthID = ResourceBase.ActiveID.Camera;
            var colorDesc = new TextureDesc(width, height)
            {
                colorFormat = camera.cameraTargetDescriptor.graphicsFormat,
                enableRandomWrite = randomWrite,
                clearBuffer = true,
                clearColor = camera.backgroundColor,
                name = "_SubContext_Color"
            };
            sub.cameraColor = graph.CreateTexture(colorDesc);
            var depthDescriptor = camera.cameraTargetDescriptor;
            depthDescriptor.width = width;
            depthDescriptor.height = height;
            depthDescriptor.msaaSamples = 1;
            depthDescriptor.bindMS = false;
            depthDescriptor.useDynamicScale = false;
            depthDescriptor.graphicsFormat = GraphicsFormat.None;
            depthDescriptor.depthStencilFormat = camera.renderer.cameraDepthAttachmentFormat;
            sub.cameraDepth = UltraRenderer.CreateRenderGraphTexture(graph, depthDescriptor, "_SubContext_Depth", true);
            sub.cameraDepthTexture = CreateScaledTexture(graph, parent.cameraDepthTexture, width, height, "_SubContext_DepthTexture");
            sub.cameraNormalsTexture = CreateScaledTexture(graph, parent.cameraNormalsTexture, width, height, "_SubContext_Normals");
            sub.renderingLayersTexture = CreateScaledTexture(graph, parent.renderingLayersTexture, width, height, "_SubContext_RenderingLayers");
            sub.motionVectorColor = CreateScaledTexture(graph, parent.motionVectorColor, width, height, "_SubContext_MotionVectors");
            sub.motionVectorDepth = CreateScaledTexture(graph, parent.motionVectorDepth, width, height, "_SubContext_MotionDepth");
            // Read-only sky lighting is shared; framebuffer resources stay local.
            sub.skyAmbientProbe = parent.skyAmbientProbe;
            sub.skyReflectionProbe = parent.skyReflectionProbe;
            var parentGBuffer = parent.gBuffer;
            var subGBuffer = sub.gBuffer;
            for (int i = 0; i < parentGBuffer.Length && i < subGBuffer.Length; i++)
            {
                var source = parentGBuffer[i];
                subGBuffer[i] = RenderGraphUtils.TextureHandlesEqual(source, parent.activeColorTexture)
                    ? sub.cameraColor
                    : CreateScaledTexture(graph, source, width, height, "_SubContext_GBuffer");
            }
        }

        private void UpdateCameraData(CameraData target, CameraData source, int newWidth, int newHeight)
        {
            target.Reset();
            target.isSubContext = true;
            
            // 基础Camera引用（不克隆Unity Camera对象本身）
            target.camera = source.camera;
            target.historyManager = null;
            
            // 渲染类型
            target.renderType = source.renderType;
            target.targetTexture = null;
            
            // 修改分辨率相关字段
            target.cameraTargetDescriptor = source.cameraTargetDescriptor;
            target.cameraTargetDescriptor.width = newWidth;
            target.cameraTargetDescriptor.height = newHeight;
            target.cameraTargetDescriptor.msaaSamples = 1;
            target.cameraTargetDescriptor.bindMS = false;
            target.cameraTargetDescriptor.useDynamicScale = false;
            target.pixelRect = new Rect(0, 0, newWidth, newHeight);
            target.pixelWidth = newWidth;
            target.pixelHeight = newHeight;
            target.aspectRatio = (float)newWidth / newHeight;
            
            // 屏幕坐标
            target.useScreenCoordOverride = source.useScreenCoordOverride;
            target.screenSizeOverride = source.screenSizeOverride;
            target.screenCoordScaleBias = source.screenCoordScaleBias;
            
            // 渲染缩放
            target.renderScale = source.renderScale;
            target.imageScalingMode = ImageScalingMode.None;
            target.upscalingFilter = source.upscalingFilter;
            target.fsrOverrideSharpness = source.fsrOverrideSharpness;
            target.fsrSharpness = source.fsrSharpness;
            target.hdrColorBufferPrecision = source.hdrColorBufferPrecision;
            
            // 清除和类型
            target.clearDepth = source.clearDepth;
            target.cameraType = source.cameraType;
            target.isDefaultViewport = source.isDefaultViewport;
            target.isHdrEnabled = source.isHdrEnabled;
            target.allowHDROutput = false;
            target.isAlphaOutputEnabled = source.isAlphaOutputEnabled;
            
            // 排序
            target.defaultOpaqueSortFlags = source.defaultOpaqueSortFlags;
            
            // 纹理需求
            target.requiresDepthTexture = source.requiresDepthTexture;
            target.requiresOpaqueTexture = source.requiresOpaqueTexture;
            target.useGPUOcclusionCulling = false;
            
            // 阴影和后处理
            target.maxShadowDistance = source.maxShadowDistance;
            target.postProcessEnabled = source.postProcessEnabled;
            target.stackAnyPostProcessingEnabled = source.stackAnyPostProcessingEnabled;
            target.captureActions = null;
            target.volumeLayerMask = source.volumeLayerMask;
            target.volumeTrigger = source.volumeTrigger;
            target.isStopNaNEnabled = source.isStopNaNEnabled;
            target.isDitheringEnabled = source.isDitheringEnabled;
            target.antialiasing = AntialiasingMode.None;
            target.antialiasingQuality = source.antialiasingQuality;
            
            // 最终目标
            target.resolveFinalTarget = false;
            
            // 世界空间位置和颜色
            target.worldSpaceCameraPos = source.worldSpaceCameraPos;
            target.backgroundColor = source.backgroundColor;
            
            // TAA历史（引用共享，不深度克隆）
            target.taaHistory = null;
            target.stpHistory = null;
            target.sgsr2History = null;
            target.sgsr2Enabled = false;
            target.taaSettings = source.taaSettings;
            
            // Renderer引用
            target.renderer = source.renderer;
            
            // 视图和投影矩阵
            target.SetViewProjectionAndJitterMatrix(
                source.GetViewMatrix(),
                source.GetProjectionMatrixNoJitter(),
                Matrix4x4.identity  // 子作用域暂不使用jitter
            );
            
            target.stackLastCameraOutputToHDR = false;
        }
    }
}
