using UnityEngine.Rendering.RenderGraphModule.NativeRenderPassCompiler;

namespace UnityEngine.Rendering.RenderGraphModule
{
    public partial class RenderGraph
    {
        // TODO(ddebaets) move old compile func/members over.
        NativePassCompiler nativeCompiler;

        internal NativePassCompiler CompileNativeRenderGraph(int graphHash)
        {
            using (new ProfilingScope(m_RenderGraphContext.cmd, ProfilingSampler.Get(RenderGraphProfileId.CompileRenderGraph)))
            {
                if (nativeCompiler == null)
                    nativeCompiler = new NativePassCompiler(m_CompilationCache);

                bool compilationIsCached = nativeCompiler.Initialize(
                    m_Resources,
                    m_RenderPasses,
                    m_DebugParameters.disablePassCulling,
                    name,
                    m_EnableCompilationCaching,
                    graphHash,
                    m_ExecutionCount);
                if (!compilationIsCached)
                    nativeCompiler.Compile(m_Resources);

                var passData = nativeCompiler.contextData.passData;
                int numPasses = passData.Length;
                for (int i = 0; i < numPasses; ++i)
                {
                    if (passData.ElementAt(i).culled)
                        continue;
                    var renderPass = m_RenderPasses[i];
                    m_RendererLists.AddRange(renderPass.usedRendererListList);
                }

                m_Resources.CreateRendererLists(
                    m_RendererLists,
                    m_RenderGraphContext.renderContext,
                    m_RendererListCulling);
                return nativeCompiler;
            }
        }

        void ExecuteNativeRenderGraph()
        {
            using (new ProfilingScope(m_RenderGraphContext.cmd, ProfilingSampler.Get(RenderGraphProfileId.ExecuteRenderGraph)))
            {
                nativeCompiler.ExecuteGraph(m_RenderGraphContext, m_Resources, m_RenderPasses);

                // Skip the managed/native submission when pass culling left no commands.
                if (!m_RenderGraphContext.contextlessTesting &&
                    m_RenderGraphContext.cmd.sizeInBytes > 0)
                    m_RenderGraphContext.renderContext.ExecuteCommandBuffer(m_RenderGraphContext.cmd);

                m_RenderGraphContext.cmd.Clear();
            }
        }
    }
}
