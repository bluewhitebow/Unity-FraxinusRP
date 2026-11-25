using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    public enum TextureSourceMode
    {
        Property,
        InputPort
    }

    [Serializable, NodeMenuItem("UltraRP/TextureBlitNode")]
    public class TextureBlitNode : ScriptableRenderPassNode
    {
        [Input("Camera Target")]
        public CameraTargetBufferNodeParameter inCameraTarget;

        [Input("Source Texture"), VisibleIf(nameof(_sourceMode), TextureSourceMode.InputPort)]
        public TextureHandleNodeParameter inSourceTexture;

        [Output("Camera Target", allowMultiple = true)]
        public CameraTargetBufferNodeParameter outCameraTarget;

        [SerializeField]
        private TextureSourceMode _sourceMode = TextureSourceMode.Property;

        [SerializeField]
        private Texture2D _sourceTexture;

        [SerializeField]
        private Material _blitMaterial;

        private Material _defBlitMaterial;

        public TextureBlitNode() : base()
        {
            profilingSampler = new ProfilingSampler(nameof(TextureBlitNode));
        }

        private void EnsureDefaultMaterial()
        {
            if (_defBlitMaterial == null)
            {
                if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>(
                        out var shadersResources))
                {
                    if (shadersResources.coreBlitPS != null)
                        _defBlitMaterial = CoreUtils.CreateEngineMaterial(shadersResources.coreBlitPS);
                }
            }
        }

        protected override void Disable()
        {
            base.Disable();

            if (_defBlitMaterial != null)
            {
                CoreUtils.Destroy(_defBlitMaterial);
                _defBlitMaterial = null;
            }
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
            {
                AppendDebugInfo("⚠️ CameraTarget is invalid");
                return;
            }

            // 根据模式选择纹理源
            bool useInputPort = _sourceMode == TextureSourceMode.InputPort;
            TextureHandle sourceHandle = useInputPort ? inSourceTexture.value : default;
            Texture2D propertyTexture = useInputPort ? null : _sourceTexture;
            if (useInputPort && !sourceHandle.IsValid())
            {
                AppendDebugInfo("⚠️ Input texture is invalid");
                return;
            }

            if (!useInputPort && propertyTexture == null)
            {
                AppendDebugInfo("⚠️ Source texture is null");
                return;
            }

            if (_blitMaterial == null)
            {
                EnsureDefaultMaterial();
                if (_defBlitMaterial == null)
                    throw new InvalidOperationException("UltraRP's core blit shader is missing from the pipeline resources.");
            }

            TextureHandle colorTarget = buffer.color;
            TextureHandle depthTarget = buffer.depth;
            
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Texture Blit", out var passData, profilingSampler))
            {
                passData.useInputPort = useInputPort;
                passData.propertyTexture = propertyTexture;
                passData.sourceTextureHandle = sourceHandle;
                passData.material = _blitMaterial != null ? _blitMaterial : _defBlitMaterial;

                if (useInputPort)
                    builder.UseTexture(sourceHandle, AccessFlags.Read);

                builder.SetRenderAttachment(colorTarget, 0, AccessFlags.Write);
                if (depthTarget.IsValid())
                    builder.SetRenderAttachmentDepth(depthTarget, AccessFlags.Write);

                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    ExecuteBlit(context.cmd, data);
                });
            }
        }

        private static void ExecuteBlit(RasterCommandBuffer cmd, PassData data)
        {
            Material material = data.material;
            Texture sourceTexture = data.useInputPort ? data.sourceTextureHandle : data.propertyTexture;

            Vector4 scaleBias = new Vector4(1, 1, 0, 0);
            Blitter.BlitTexture(cmd, sourceTexture, scaleBias, material, 0);
        }

        private class PassData
        {
            internal bool useInputPort;
            internal TextureHandle sourceTextureHandle;
            internal Texture2D propertyTexture;
            internal Material material;
        }
    }
}
