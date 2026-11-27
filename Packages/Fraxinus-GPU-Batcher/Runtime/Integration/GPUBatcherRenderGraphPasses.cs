#if UNITY_6000_0_OR_NEWER
// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using UnityEngine;
using UnityEngine.Experimental.Rendering;
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
            public bool sourceTextureArray;
        }

        private class HZBResolvePassData
        {
            public GPUBatcherRenderer batcher;
            public TextureHandle source, destination;
            public int width, height, kernel, slice;
            public bool textureArray;
        }

        private static readonly int s_sourceMSAA = Shader.PropertyToID("_HZBSourceMSAA");
        private static readonly int s_sourceMSAAArray = Shader.PropertyToID("_HZBSourceMSAAArray");
        private static readonly int s_resolvedDepth = Shader.PropertyToID("_HZBResolvedDepth");
        private static readonly int s_sourceSlice = Shader.PropertyToID("_HZBSourceSlice");

        // Explicit array loads do not depend on the pipeline's stereo keywords.
        internal static TextureHandle RecordHZBMSAAResolve(RenderGraph graph, GPUBatcherRenderer batcher,
            TextureHandle source, int width, int height, bool textureArray, int slice = 0)
        {
            int kernel = batcher.HZBResolveMSAAKernel(textureArray);
            if (kernel < 0) throw new System.InvalidOperationException("GPU Batcher HZB MSAA resolve kernel is missing.");
            var destination = graph.CreateTexture(new TextureDesc(width, height)
            {
                name = "FraxinusGPUBatcher.ResolvedOpaqueDepth", format = GraphicsFormat.R32_SFloat,
                filterMode = FilterMode.Point, enableRandomWrite = true
            });
            using (var builder = graph.AddUnsafePass<HZBResolvePassData>("FraxinusGPUBatcher.ResolveOpaqueDepth", out var data))
            {
                data.batcher = batcher; data.source = source; data.destination = destination;
                data.width = width; data.height = height; data.kernel = kernel;
                data.textureArray = textureArray; data.slice = slice;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(destination, AccessFlags.WriteAll);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (HZBResolvePassData pass, UnsafeGraphContext context) =>
                {
                    var shader = pass.batcher.occlusionShader;
                    context.cmd.SetComputeVectorParam(shader, GPUBatcherRenderer.idHzbDepthConvention, GPUBatcherRenderer.hzbDepthConvention);
                    context.cmd.SetComputeIntParam(shader, s_sourceSlice, pass.slice);
                    context.cmd.SetComputeTextureParam(shader, pass.kernel, pass.textureArray ? s_sourceMSAAArray : s_sourceMSAA, pass.source);
                    context.cmd.SetComputeTextureParam(shader, pass.kernel, s_resolvedDepth, pass.destination);
                    context.cmd.DispatchCompute(shader, pass.kernel, (pass.width + 7) / 8, (pass.height + 7) / 8, 1);
                });
            }
            return destination;
        }

        private static void UseCullingBuffers(IUnsafeRenderGraphBuilder builder, RenderGraph renderGraph,
            GPUBatcherRenderer.CullingBufferSet viewSet)
        {
            builder.UseBuffer(renderGraph.ImportBuffer(viewSet.drawBucketBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(viewSet.visibilityBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(viewSet.indirectArgsBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(viewSet.visibleChunksBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(viewSet.visibleChunkCountBuffer), AccessFlags.ReadWrite);
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
            ctx.cmd.SetComputeTextureParam(brg.cullingShaderRef, brg.cullChunksKernelRef,
                GPUBatcherRenderer.idHzbTextureRef, atlas);
            ctx.cmd.SetComputeTextureParam(brg.cullingShaderRef, brg.cullInstancesKernelRef,
                GPUBatcherRenderer.idHzbTextureRef, atlas);
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

        /// <summary>HZB build for the active viewport; the kernel queries the bound source texture's actual size.</summary>
        public static void RecordHZBBuildPass(RenderGraph renderGraph, GPUBatcherRenderer brg, string passName, int cameraId,
            TextureHandle depth, int depthWidth, int depthHeight,
            bool sourceTextureArray = false)
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
                data.sourceTextureArray = sourceTextureArray;

                builder.SetRenderFunc(static (HZBBuildPassData passData, UnsafeGraphContext ctx) =>
                {
                    if (!passData.depth.IsValid()) return;

                    ctx.cmd.SetComputeTextureParam(passData.brg.occlusionShader, passData.brg.hzbDownscaleKernel,
                        passData.brg.idHzbSource, passData.depth);

                    if (passData.hzbOutput.IsValid())
                    {
                        ctx.cmd.SetComputeTextureParam(passData.brg.occlusionShader, passData.brg.hzbDownscaleKernel,
                            GPUBatcherRenderer.idHzbMipOutRef, passData.hzbOutput);
                        ctx.cmd.SetComputeTextureParam(passData.brg.occlusionShader, passData.brg.hzbDownscaleTailKernel,
                            GPUBatcherRenderer.idHzbMipOutRef, passData.hzbOutput);
                    }

                    passData.brg.SetHZBSourceTexArray(passData.sourceTextureArray);
                    passData.brg.ExecuteHZBBuild(CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd),
                        passData.camera, passData.depthWidth, passData.depthHeight,
                        skipHZBOutputBinding: true);
                });
            }
        }
    }
}
#endif
