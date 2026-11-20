// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace Fraxinus.GPUBatcher
{
    /// <summary>Render graph compute stages bound by the Fraxinus injection modules.</summary>
    internal static class GPUBatcherRenderGraphPasses
    {
        private class CullPassData
        {
            public GPUBatcherRenderer brg;
            public int cameraId;
            public TextureHandle hzbAtlas;
            public bool bindHZBAtlas;
        }

        private class HZBBuildPassData
        {
            public GPUBatcherRenderer brg;
            public GPUBatcherRenderer.HZBBuildContext camera;
            public TextureHandle depth;
            public TextureHandle hzbOutput;
            public int depthWidth;
            public int depthHeight;
            public int sourceTexWidth;
            public int sourceTexHeight;
            public bool sourceTextureArray;
        }

        private static void UseCullingBuffers(IUnsafeRenderGraphBuilder builder, RenderGraph renderGraph,
            GPUBatcherRenderer.CullingBufferSet viewSet)
        {
            builder.UseBuffer(renderGraph.ImportBuffer(viewSet.DrawBucketBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(viewSet.VisibilityBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(viewSet.IndirectArgsBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(viewSet.VisibleChunksBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(viewSet.VisibleChunkCountBuffer), AccessFlags.ReadWrite);
        }

        private static TextureHandle ImportHZBAtlas(IUnsafeRenderGraphBuilder builder, RenderGraph renderGraph,
            GPUBatcherRenderer brg, int cameraId, AccessFlags access)
        {
            var hzbRTH = brg.GetCameraHZBRTHandle(cameraId);
            if (hzbRTH == null) return default;
            var handle = renderGraph.ImportTexture(hzbRTH);
            builder.UseTexture(handle, access);
            return handle;
        }

        private static void BindHZBAtlasOnCullKernels(UnsafeGraphContext ctx, GPUBatcherRenderer brg, TextureHandle atlas)
        {
            ctx.cmd.SetComputeTextureParam(brg.CullingShaderRef, brg.CullChunksKernelRef,
                GPUBatcherRenderer.ID_HZBTextureRef, atlas);
            ctx.cmd.SetComputeTextureParam(brg.CullingShaderRef, brg.CullInstancesKernelRef,
                GPUBatcherRenderer.ID_HZBTextureRef, atlas);
        }

        /// <summary>Deferred culling pass. bindHZBAtlas imports last frame's HZB atlas and binds it via the graph (pass 0).</summary>
        public static void RecordDeferredCullingPass(RenderGraph renderGraph, GPUBatcherRenderer brg,
            string passName, int cameraId, bool bindHZBAtlas)
        {
            var viewSet = brg.GetPendingCullingBufferSet(cameraId);
            if (viewSet == null) return;

            using (var builder = renderGraph.AddUnsafePass<CullPassData>(passName, out var data))
            {
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                for (int split = 0; split < brg.GetPendingCullingSplitCount(cameraId); ++split)
                    UseCullingBuffers(builder, renderGraph, brg.GetPendingCullingBufferSet(cameraId, split));
                // pass data is pooled, always assign
                data.brg = brg;
                data.cameraId = cameraId;
                data.bindHZBAtlas = bindHZBAtlas;
                data.hzbAtlas = bindHZBAtlas ? ImportHZBAtlas(builder, renderGraph, brg, cameraId, AccessFlags.Read) : default;

                builder.SetRenderFunc(static (CullPassData passData, UnsafeGraphContext ctx) =>
                {
                    if (passData.hzbAtlas.IsValid())
                        BindHZBAtlasOnCullKernels(ctx, passData.brg, passData.hzbAtlas);
                    passData.brg.ExecuteGraphCulling(CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd),
                        passData.cameraId, skipHZBTextureBinding: passData.bindHZBAtlas);
                });
            }
        }

        /// <summary>Phase 1: re-cull against the current frame's HZB atlas.</summary>
        public static void RecordHZBCullPass(RenderGraph renderGraph, GPUBatcherRenderer brg, string passName, int cameraId)
        {
            var viewSet = brg.GetPendingCullingBufferSet(cameraId);
            if (viewSet == null) return;

            using (var builder = renderGraph.AddUnsafePass<CullPassData>(passName, out var data))
            {
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                UseCullingBuffers(builder, renderGraph, viewSet);
                data.brg = brg;
                data.cameraId = cameraId;
                data.hzbAtlas = ImportHZBAtlas(builder, renderGraph, brg, cameraId, AccessFlags.Read);

                builder.SetRenderFunc(static (CullPassData passData, UnsafeGraphContext ctx) =>
                {
                    if (passData.hzbAtlas.IsValid())
                        BindHZBAtlasOnCullKernels(ctx, passData.brg, passData.hzbAtlas);
                    passData.brg.ExecuteGraphHZBRetest(CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd),
                        passData.cameraId, skipHZBTextureBinding: true);
                });
            }
        }

        /// <summary>HZB build from the given depth source. sourceTexWidth/Height 0 means the depth texture matches the viewport.</summary>
        public static void RecordHZBBuildPass(RenderGraph renderGraph, GPUBatcherRenderer brg, string passName, int cameraId,
            TextureHandle depth, int depthWidth, int depthHeight,
            int sourceTexWidth = 0, int sourceTexHeight = 0, bool sourceTextureArray = false)
        {
            using (var builder = renderGraph.AddUnsafePass<HZBBuildPassData>(passName, out var data))
            {
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);

                data.brg = brg;
                data.camera = brg.CaptureHZBBuildContext(cameraId);
                data.depth = depth;
                if (depth.IsValid())
                    builder.UseTexture(depth, AccessFlags.Read);

                data.hzbOutput = ImportHZBAtlas(builder, renderGraph, brg, cameraId, AccessFlags.ReadWrite);
                data.depthWidth = depthWidth;
                data.depthHeight = depthHeight;
                data.sourceTexWidth = sourceTexWidth;
                data.sourceTexHeight = sourceTexHeight;
                data.sourceTextureArray = sourceTextureArray;

                builder.SetRenderFunc(static (HZBBuildPassData passData, UnsafeGraphContext ctx) =>
                {
                    if (!passData.depth.IsValid()) return;

                    ctx.cmd.SetComputeTextureParam(passData.brg.OcclusionShader, passData.brg.HZBDownscaleKernel,
                        passData.brg.ID_HZBSource, passData.depth);

                    if (passData.hzbOutput.IsValid())
                    {
                        ctx.cmd.SetComputeTextureParam(passData.brg.OcclusionShader, passData.brg.HZBDownscaleKernel,
                            GPUBatcherRenderer.ID_HZBMipOutRef, passData.hzbOutput);
                        ctx.cmd.SetComputeTextureParam(passData.brg.OcclusionShader, passData.brg.HZBDownscaleTailKernel,
                            GPUBatcherRenderer.ID_HZBMipOutRef, passData.hzbOutput);
                    }

                    passData.brg.SetHZBSourceTexArray(passData.sourceTextureArray);
                    passData.brg.ExecuteHZBBuild(CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd),
                        passData.camera, passData.depthWidth, passData.depthHeight,
                        skipHZBOutputBinding: true,
                        sourceTextureWidth: passData.sourceTexWidth, sourceTextureHeight: passData.sourceTexHeight);
                });
            }
        }
    }
}
