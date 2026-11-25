#if UNITY_6000_0_OR_NEWER || HAS_FRAXINUS_LIGHT_TRANSPORT
using UnityEngine.Rendering.Sampling;
using UnityEngine.Rendering.UnifiedRayTracing;

namespace UnityEngine.Rendering
{
    internal partial class APVProbeVolumeBaking
    {
        // Helper struct to manage tracing backend

        struct APVRTContext
        {
            RayTracingContext m_Context;
            RayTracingBackend m_Backend;
            SamplingResources m_SamplingResources;
            RayTracingResources m_RayTracingResources;

            IRayTracingShader m_ShaderVO;
            IRayTracingShader m_ShaderSO;
            IRayTracingShader m_ShaderRL;

            const string k_PackageLightTransport = "Packages/com.fraxinus.rendering.light-transport";

            internal AccelStructAdapter CreateAccelerationStructure()
            {
                var c = context;
                return new AccelStructAdapter(c.CreateAccelerationStructure(new AccelerationStructureOptions
                {
                    // Use PreferFastBuild to avoid bug triggered with big meshes (UUM-52552));
                    buildFlags = BuildFlags.PreferFastBuild
                }),
                m_RayTracingResources
                );
            }

            public RayTracingContext context
            {
                get
                {
                    if (m_Context == null)
                    {
                        m_RayTracingResources = new RayTracingResources();
                        m_RayTracingResources.Load();

                        m_Backend = RayTracingContext.IsBackendSupported(RayTracingBackend.Hardware) ? RayTracingBackend.Hardware : RayTracingBackend.Compute;

                        m_Context = new RayTracingContext(m_Backend, m_RayTracingResources);
                    }

                    return m_Context;
                }
            }

            public IRayTracingShader shaderVO
            {
                get
                {
                    if (m_ShaderVO == null)
                    {
                        var bakingResources = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ProbeVolumeBakingResources>();
                        m_ShaderVO = m_Context.CreateRayTracingShader(m_Backend switch
                        {
                            RayTracingBackend.Hardware => bakingResources.traceVirtualOffsetRT,
                            RayTracingBackend.Compute => bakingResources.traceVirtualOffsetCS,
                            _ => null
                        });
                    }

                    return m_ShaderVO;
                }
            }

            public IRayTracingShader shaderSO
            {
                get
                {
                    if (m_ShaderSO == null)
                    {
                        var bakingResources = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ProbeVolumeBakingResources>();
                        m_ShaderSO = m_Context.CreateRayTracingShader(m_Backend switch
                        {
                            RayTracingBackend.Hardware => bakingResources.skyOcclusionRT,
                            RayTracingBackend.Compute => bakingResources.skyOcclusionCS,
                            _ => null
                        });
                    }

                    return m_ShaderSO;
                }
            }

            public IRayTracingShader shaderRL
            {
                get
                {
                    if (m_ShaderRL == null)
                    {
                        var bakingResources = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ProbeVolumeBakingResources>();
                        m_ShaderRL = m_Context.CreateRayTracingShader(m_Backend switch
                        {
                            RayTracingBackend.Hardware => bakingResources.renderingLayerRT,
                            RayTracingBackend.Compute => bakingResources.renderingLayerCS,
                            _ => null
                        });
                    }

                    return m_ShaderRL;
                }
            }

            public void BindSamplingTextures(CommandBuffer cmd)
            {
                if (m_SamplingResources == null)
                {
                    m_SamplingResources = new SamplingResources();
                    m_SamplingResources.Load();
                }

                SamplingResources.BindSobolBlueNoiseTextures(cmd, m_SamplingResources);
            }

            public bool TryGetMeshForAccelerationStructure(Renderer renderer, out Mesh mesh)
            {
                mesh = null;
                if (renderer.isPartOfStaticBatch)
                {
                    Debug.LogError("Static batching is not supported when baking APV.");
                    return false;
                }

                mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                if (mesh == null)
                    return false;

                // This would error out later in LoadIndexBuffer in LightTransport package
                if ((mesh.indexBufferTarget & GraphicsBuffer.Target.Raw) == 0 && (mesh.GetIndices(0) == null || mesh.GetIndices(0).Length == 0))
                    return false;

                return true;
            }

            public void Dispose()
            {
                if (m_Context != null)
                {
                    m_Context.Dispose();
                    m_Context = null;

                    // The lifetime of these shaders are bound to the lifetime of the context.
                    m_ShaderRL = null;
                    m_ShaderSO = null;
                    m_ShaderVO = null;
                }

                m_SamplingResources?.Dispose();
                m_SamplingResources = null;
            }
        }


    }
}
#endif
