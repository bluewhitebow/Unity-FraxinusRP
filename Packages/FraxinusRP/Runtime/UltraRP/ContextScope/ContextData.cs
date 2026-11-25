using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    internal struct ContextTextureBindings
    {
        internal TextureHandle depth, normals, layers, motion, motionDepth;
        internal TextureHandle dbuffer0, dbuffer1, dbuffer2;
        internal TextureHandle ambientOcclusion;
    }

    internal static class ContextRenderState
    {
        private static readonly int s_Depth = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int s_Normals = Shader.PropertyToID("_CameraNormalsTexture");
        private static readonly int s_Layers = Shader.PropertyToID("_CameraRenderingLayersTexture");
        private static readonly int s_Motion = Shader.PropertyToID("_MotionVectorTexture");
        private static readonly int s_MotionDepth = Shader.PropertyToID("_MotionVectorDepthTexture");
        private static readonly int[] s_DBuffer = { Shader.PropertyToID("_DBufferTexture0"), Shader.PropertyToID("_DBufferTexture1"), Shader.PropertyToID("_DBufferTexture2") };
        private class KeywordsPassData { internal bool mrt1, mrt2, mrt3, decalLayers; }

        internal static ContextTextureBindings CaptureTextures(RenderGraph graph)
        {
            // Only already-published handles are safe to restore. ResourceData can
            // contain targets whose producer occurs after the current child scope.
            return new ContextTextureBindings
            {
                depth = graph.GetGlobal(s_Depth),
                normals = graph.GetGlobal(s_Normals),
                layers = graph.GetGlobal(s_Layers),
                motion = graph.GetGlobal(s_Motion),
                motionDepth = graph.GetGlobal(s_MotionDepth),
                dbuffer0 = graph.GetGlobal(s_DBuffer[0]),
                dbuffer1 = graph.GetGlobal(s_DBuffer[1]),
                dbuffer2 = graph.GetGlobal(s_DBuffer[2]),
                ambientOcclusion = graph.GetGlobal(GroundTruthAmbientOcclusionPass.textureId)
            };
        }

        internal static void RestoreTextures(RenderGraph graph, ResourceData resources, ContextTextureBindings textures)
        {
            Publish(graph, s_Depth, textures.depth);
            Publish(graph, s_Normals, textures.normals);
            Publish(graph, s_Layers, textures.layers);
            Publish(graph, s_Motion, textures.motion);
            Publish(graph, s_MotionDepth, textures.motionDepth);
            Publish(graph, s_DBuffer[0], textures.dbuffer0);
            Publish(graph, s_DBuffer[1], textures.dbuffer1);
            Publish(graph, s_DBuffer[2], textures.dbuffer2);
            PublishDecalKeywords(graph, resources);
            bool hasAO = resources.ambientOcclusionTexture.IsValid() && textures.ambientOcclusion.IsValid() &&
                RenderGraphUtils.TextureHandlesEqual(resources.ambientOcclusionTexture, textures.ambientOcclusion);
            GroundTruthAmbientOcclusionPass.Publish(graph, textures.ambientOcclusion, hasAO,
                resources.ambientOcclusionDirectLightingStrength);
        }

        internal static void PublishTextures(RenderGraph graph, ResourceData resources)
        {
            Publish(graph, s_Depth, resources.cameraDepthTexture);
            Publish(graph, s_Normals, resources.cameraNormalsTexture);
            Publish(graph, s_Layers, resources.renderingLayersTexture);
            Publish(graph, s_Motion, resources.motionVectorColor);
            Publish(graph, s_MotionDepth, resources.motionVectorDepth);
            var dbuffer = resources.dBuffer;
            for (int i = 0; i < s_DBuffer.Length; i++)
                Publish(graph, s_DBuffer[i], dbuffer[i]);
            PublishDecalKeywords(graph, resources);
            // Child resources have no AO producer. Do not inherit visibility from
            // the parent's screen or its camera resolution.
            GroundTruthAmbientOcclusionPass.Publish(graph, resources.ambientOcclusionTexture,
                resources.ambientOcclusionTexture.IsValid(), resources.ambientOcclusionDirectLightingStrength);
        }

        private static void PublishDecalKeywords(RenderGraph graph, ResourceData resources)
        {
            var dbuffer = resources.dBuffer;
            using var builder = graph.AddRasterRenderPass<KeywordsPassData>("Context Restore Decal Keywords", out var data);
            data.mrt3 = dbuffer[2].IsValid();
            data.mrt2 = !data.mrt3 && dbuffer[1].IsValid();
            data.mrt1 = !data.mrt3 && !data.mrt2 && dbuffer[0].IsValid();
            data.decalLayers = resources.decalLayers;
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((KeywordsPassData p, RasterGraphContext ctx) =>
            {
                ctx.cmd.SetKeyword(ShaderGlobalKeywords.DBufferMRT1, p.mrt1);
                ctx.cmd.SetKeyword(ShaderGlobalKeywords.DBufferMRT2, p.mrt2);
                ctx.cmd.SetKeyword(ShaderGlobalKeywords.DBufferMRT3, p.mrt3);
                ctx.cmd.SetKeyword(ShaderGlobalKeywords.DecalLayers, p.decalLayers);
            });
        }

        private static void Publish(RenderGraph graph, int name, TextureHandle texture)
        {
            RenderGraphUtils.SetGlobalTexture(graph, name, texture.IsValid() ? texture : graph.defaultResources.blackTexture, "Context Restore Textures");
        }
    }

    /// <summary>按类型存储作用域借用的数据引用，不负责创建或重置数据对象。</summary>
    public sealed class ContextData : ContextContainer.ScopeData
    {
    }
}
