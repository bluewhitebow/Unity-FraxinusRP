using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Helper class for depth copy operations. Contains shared logic for depth copying.
    /// </summary>
    internal class DepthCopyHelper
    {
        internal static class ShaderConstants
        {
            public static readonly int _CameraDepthAttachment = Shader.PropertyToID("_CameraDepthAttachment");
            public static readonly int _CameraDepthAttachmentTexelSize = Shader.PropertyToID("_CameraDepthAttachment_TexelSize");
            public static readonly int _CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
            public static readonly int _ZWriteShaderHandle = Shader.PropertyToID("_ZWrite");
        }

        internal class PassData
        {
            internal TextureHandle source;
            internal bool destinationYFlipped;
            internal CameraType cameraType;
            internal Rect? viewport;
            internal Material copyDepthMaterial;
            internal Vector4 sourceTexelSize;
            internal int msaaSamples;
            internal bool copyToDepth;
        }

        private readonly Material _copyToColorMaterial;
        private readonly Material _copyToDepthMaterial;

        public DepthCopyHelper()
        {
            Shader copyDepthPS = null;
            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRendererResources>(
                out var ultraRendererShaders))
            {
                copyDepthPS = ultraRendererShaders.copyDepthPS;
            }

            if (copyDepthPS == null)
                return;

            // Draw commands retain their material until submission. Keep render state
            // immutable so recording a later copy cannot change an earlier draw's ZWrite.
            _copyToColorMaterial = CoreUtils.CreateEngineMaterial(copyDepthPS);
            _copyToColorMaterial.SetFloat(ShaderConstants._ZWriteShaderHandle, 0.0f);
            _copyToDepthMaterial = CoreUtils.CreateEngineMaterial(copyDepthPS);
            _copyToDepthMaterial.SetFloat(ShaderConstants._ZWriteShaderHandle, 1.0f);
        }

        public void Dispose()
        {
            CoreUtils.Destroy(_copyToColorMaterial);
            CoreUtils.Destroy(_copyToDepthMaterial);
        }

        public void Render(RenderGraph renderGraph, TextureHandle destination, TextureHandle source, 
            ResourceData resourceData, CameraData cameraData, ProfilingSampler profilingSampler,
            bool bindAsCameraDepth = false, string passName = "Copy Depth", TextureHandle colorAttachment = default,
            Rect? viewport = null)
        {
            // RenderTargetInfo also supports imported backbuffers, which have no full TextureDesc.
            var sourceInfo = renderGraph.GetRenderTargetInfo(source);
            var destinationInfo = renderGraph.GetRenderTargetInfo(destination);
            bool copyToDepth = GraphicsFormatUtility.IsDepthStencilFormat(destinationInfo.format);
            bool writesWholeTarget = !viewport.HasValue ||
                viewport.Value == new Rect(0, 0, destinationInfo.width, destinationInfo.height);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out var passData, profilingSampler))
            {
                passData.copyDepthMaterial = copyToDepth ? _copyToDepthMaterial : _copyToColorMaterial;
                passData.sourceTexelSize = new Vector4(1.0f / sourceInfo.width, 1.0f / sourceInfo.height,
                    sourceInfo.width, sourceInfo.height);
                // A resolved source is sampled as Texture2D even when its attachment has MSAA.
                passData.msaaSamples = sourceInfo.bindMS && SystemInfo.supportsMultisampledTextures != 0
                    ? sourceInfo.msaaSamples : 1;
                // Native render passes may keep an attachment memoryless, without
                // allocating an RTHandle. Determine its orientation while recording
                // instead of resolving that attachment in the execution callback.
                bool destinationIsBackbuffer = cameraData.targetTexture == null &&
                    (RenderGraphUtils.TextureHandlesEqual(destination, resourceData.backBufferColor) ||
                     RenderGraphUtils.TextureHandlesEqual(destination, resourceData.backBufferDepth));
                passData.destinationYFlipped = !SystemInfo.graphicsUVStartsAtTop ||
                    cameraData.isSceneViewCamera || cameraData.isPreviewCamera || !destinationIsBackbuffer;
                passData.cameraType = cameraData.cameraType;
                passData.viewport = viewport;
                passData.copyToDepth = copyToDepth;

                if (copyToDepth)
                {
                    builder.SetRenderAttachmentDepth(destination, writesWholeTarget ? AccessFlags.WriteAll : AccessFlags.Write);
#if UNITY_EDITOR
                    // Scene/Preview depth-only draws need a color attachment (UUM-47698, UUM-55171).
                    if (!colorAttachment.IsValid() && (cameraData.isSceneViewCamera || cameraData.isPreviewCamera))
                        colorAttachment = resourceData.activeColorTexture;
#endif
                    // A supplied color/depth pair describes one output surface. The shader only
                    // writes SV_Depth, so preserve the color produced before this copy.
                    if (colorAttachment.IsValid())
                        builder.SetRenderAttachment(colorAttachment, 0, AccessFlags.ReadWrite);
                }
                else
                    // Writes depth as "grayscale color" output
                    builder.SetRenderAttachment(destination, 0, writesWholeTarget ? AccessFlags.WriteAll : AccessFlags.Write);

                passData.source = source;
                builder.UseTexture(source, AccessFlags.Read);

                if (bindAsCameraDepth && destination.IsValid())
                    builder.SetGlobalTextureAfterPass(destination, ShaderConstants._CameraDepthTexture);

                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    ExecutePass(context.cmd, data, data.source);
                });
            }
        }

        private static void ExecutePass(RasterCommandBuffer cmd, PassData passData, RTHandle source)
        {
            var copyDepthMaterial = passData.copyDepthMaterial;
            var msaaSamples = passData.msaaSamples;
            var copyToDepth = passData.copyToDepth;

            if (copyDepthMaterial == null)
            {
                Debug.LogError("Copy Depth shader is missing from the renderer resources.");
                return;
            }

            using (new ProfilingScope(cmd, ProfilingSampler.Get(UltraRPProfileId.CopyDepth)))
            {
                switch (msaaSamples)
                {
                    case 8:
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa2, false);
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa4, false);
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa8, true);
                        break;

                    case 4:
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa2, false);
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa4, true);
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa8, false);
                        break;

                    case 2:
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa2, true);
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa4, false);
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa8, false);
                        break;

                    // MSAA disabled, auto resolve supported or ms textures not supported
                    default:
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa2, false);
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa4, false);
                        cmd.SetKeyword(ShaderGlobalKeywords.DepthMsaa8, false);
                        break;
                }

                cmd.SetKeyword(ShaderGlobalKeywords._OUTPUT_DEPTH, copyToDepth);

                bool yflip = CameraData.IsHandleYFlipped(source, passData.cameraType) !=
                    passData.destinationYFlipped;

                Vector2 viewportScale = source.useScaling ? new Vector2(source.rtHandleProperties.rtHandleScale.x, source.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                Vector4 scaleBias = yflip ? new Vector4(viewportScale.x, -viewportScale.y, 0, viewportScale.y) : new Vector4(viewportScale.x, viewportScale.y, 0, 0);

                if (passData.viewport.HasValue)
                    cmd.SetViewport(passData.viewport.Value);

                // Record the binding in command order; Material.SetTexture would make
                // every queued draw sample the source assigned by the last copy.
                cmd.SetGlobalTexture(ShaderConstants._CameraDepthAttachment, passData.source);
                cmd.SetGlobalVector(ShaderConstants._CameraDepthAttachmentTexelSize, passData.sourceTexelSize);
                Blitter.BlitTexture(cmd, source, scaleBias, copyDepthMaterial, 0);
            }
        }
    }
}

