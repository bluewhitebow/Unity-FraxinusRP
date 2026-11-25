using System.Collections.Generic;
using UnityEngine.Experimental.Rendering;
#if !UNITY_6000_0_OR_NEWER
using GraphicsFormatUsage = UnityEngine.Experimental.Rendering.FormatUsage;
#endif
using CellStreamingScratchBuffer =  UnityEngine.Rendering.TProbeReferenceVolumeBase.CellStreamingScratchBuffer;
using CellStreamingScratchBufferLayout = UnityEngine.Rendering.APVProbeReferenceVolume.CellStreamingScratchBufferLayout;

namespace UnityEngine.Rendering
{
    public class APVProbeBrickPool : TProbeBrickPool<APVDataLocation>
    {
        static APVProbeBrickPool()
        {
            // Keep the built-in handler reachable by IL2CPP and managed stripping.
            ProbeRuntimeResourcesTypeAdapter.RegisterHandler<APVRuntimeResource, APVDataLocation>(HandlePoolResource);
        }

        internal static readonly int _Out_L0_L1Rx = Shader.PropertyToID("_Out_L0_L1Rx");
        internal static readonly int _Out_L1G_L1Ry = Shader.PropertyToID("_Out_L1G_L1Ry");
        internal static readonly int _Out_L1B_L1Rz = Shader.PropertyToID("_Out_L1B_L1Rz");
        internal static readonly int _Out_Shared = Shader.PropertyToID("_Out_Shared");
        internal static readonly int _Out_ProbeOcclusion = Shader.PropertyToID("_Out_ProbeOcclusion");
        internal static readonly int _Out_SkyOcclusionL0L1 = Shader.PropertyToID("_Out_SkyOcclusionL0L1");
        internal static readonly int _Out_SkyShadingDirectionIndices = Shader.PropertyToID("_Out_SkyShadingDirectionIndices");
        internal static readonly int _Out_L2_0 = Shader.PropertyToID("_Out_L2_0");
        internal static readonly int _Out_L2_1 = Shader.PropertyToID("_Out_L2_1");
        internal static readonly int _Out_L2_2 = Shader.PropertyToID("_Out_L2_2");
        internal static readonly int _Out_L2_3 = Shader.PropertyToID("_Out_L2_3");
        internal static readonly int _ProbeVolumeScratchBufferLayout = Shader.PropertyToID(nameof(APVProbeReferenceVolume.CellStreamingScratchBufferLayout));
        internal static readonly int _ProbeVolumeScratchBuffer = Shader.PropertyToID("_ScratchBuffer");

        ProbeVolumeSHBands m_SHBands;
        bool m_ContainsValidity;
        bool m_ContainsProbeOcclusion;
        bool m_ContainsRenderingLayers;
        bool m_ContainsSkyOcclusion;
        bool m_ContainsSkyShadingDirection;

        ComputeShader m_DataUploadCS;
        int m_DataUploadKernel;
        ComputeShader m_DataUploadL2CS;
        int m_DataUploadL2Kernel;
        LocalKeyword m_DataUpload_Shared;
        LocalKeyword m_DataUpload_ProbeOcclusion;
        LocalKeyword m_DataUpload_SkyOcclusion;
        LocalKeyword m_DataUpload_SkyShadingDirection;


        public APVProbeBrickPool(ProbeVolumeTextureMemoryBudget memoryBudget, ProbeVolumeSHBands shBands, 
            bool allocateValidityData = false, bool allocateRenderingLayerData = false, 
            bool allocateSkyOcclusion = false, bool allocateSkyShadingData = false, 
            bool allocateProbeOcclusionData = false) : base(memoryBudget)
        {
            m_SHBands = shBands;
            m_ContainsValidity = allocateValidityData;
            m_ContainsProbeOcclusion = allocateProbeOcclusionData;
            m_ContainsRenderingLayers = allocateRenderingLayerData;
            m_ContainsSkyOcclusion = allocateSkyOcclusion;
            m_ContainsSkyShadingDirection = allocateSkyShadingData;
        }

        internal Texture GetValidityTexture()
        {
            return m_Pool.TexValidity;
        }

        internal Texture GetSkyOcclusionTexture()
        {
            return m_Pool.TexSkyOcclusion;
        }

        internal Texture GetSkyShadingDirectionIndicesTexture()
        {
            return m_Pool.TexSkyShadingDirectionIndices;
        }

        internal Texture GetProbeOcclusionTexture()
        {
            return m_Pool.TexProbeOcclusion;
        }

        internal override void Initialize()
        {
            base.Initialize();

            m_DataUploadCS = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ProbeVolumeRuntimeResources>()?.probeVolumeUploadDataCS;
            m_DataUploadL2CS = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ProbeVolumeRuntimeResources>()?.probeVolumeUploadDataL2CS;

            if (m_DataUploadCS != null)
            {
                m_DataUploadKernel = m_DataUploadCS ? m_DataUploadCS.FindKernel("UploadData") : -1;
                m_DataUpload_Shared = new LocalKeyword(m_DataUploadCS, "PROBE_VOLUMES_SHARED_DATA");
                m_DataUpload_ProbeOcclusion = new LocalKeyword(m_DataUploadCS, "PROBE_VOLUMES_PROBE_OCCLUSION");
                m_DataUpload_SkyOcclusion = new LocalKeyword(m_DataUploadCS, "PROBE_VOLUMES_SKY_OCCLUSION");
                m_DataUpload_SkyShadingDirection = new LocalKeyword(m_DataUploadCS, "PROBE_VOLUMES_SKY_SHADING_DIRECTION");
            }

            if (m_DataUploadL2CS != null)
            {
                m_DataUploadL2Kernel = m_DataUploadL2CS ? m_DataUploadL2CS.FindKernel("UploadDataL2") : -1;
            }
        }

        internal override void AllocatePool(int width, int height, int depth)
        {
            m_Pool = CreateDataLocation(width * height * depth, false, m_SHBands, "APV", true,
                m_ContainsValidity, m_ContainsRenderingLayers, m_ContainsSkyOcclusion, m_ContainsSkyShadingDirection, m_ContainsProbeOcclusion, out int estimatedCost);
            estimatedVMemCost = estimatedCost;
        }

        internal override void Update(APVDataLocation source, List<BrickChunkAlloc> srcLocations, List<BrickChunkAlloc> dstLocations, int destStartIndex)
        {
            for (int i = 0; i < srcLocations.Count; i++)
            {
                BrickChunkAlloc src = srcLocations[i];
                BrickChunkAlloc dst = dstLocations[destStartIndex + i];

                for (int j = 0; j < brickProbeCountPerDim; j++)
                {
                    int width = Mathf.Min(chunkSizeInBricks * brickProbeCountPerDim, source.width - src.x);
                    Graphics.CopyTexture(source.TexL0_L1rx, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexL0_L1rx, dst.z + j, 0, dst.x, dst.y);

                    Graphics.CopyTexture(source.TexL1_G_ry, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexL1_G_ry, dst.z + j, 0, dst.x, dst.y);
                    Graphics.CopyTexture(source.TexL1_B_rz, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexL1_B_rz, dst.z + j, 0, dst.x, dst.y);

                    if (m_ContainsValidity)
                        Graphics.CopyTexture(source.TexValidity, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexValidity, dst.z + j, 0, dst.x, dst.y);

                    if (m_ContainsSkyOcclusion)
                    {
                        Graphics.CopyTexture(source.TexSkyOcclusion, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexSkyOcclusion, dst.z + j, 0, dst.x, dst.y);
                        if (m_ContainsSkyShadingDirection)
                        {
                            Graphics.CopyTexture(source.TexSkyShadingDirectionIndices, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexSkyShadingDirectionIndices, dst.z + j, 0, dst.x, dst.y);
                        }
                    }

                    if (m_SHBands == ProbeVolumeSHBands.SphericalHarmonicsL2)
                    {
                        Graphics.CopyTexture(source.TexL2_0, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexL2_0, dst.z + j, 0, dst.x, dst.y);
                        Graphics.CopyTexture(source.TexL2_1, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexL2_1, dst.z + j, 0, dst.x, dst.y);
                        Graphics.CopyTexture(source.TexL2_2, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexL2_2, dst.z + j, 0, dst.x, dst.y);
                        Graphics.CopyTexture(source.TexL2_3, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexL2_3, dst.z + j, 0, dst.x, dst.y);
                    }

                    if (m_ContainsProbeOcclusion)
                    {
                        Graphics.CopyTexture(source.TexProbeOcclusion, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexProbeOcclusion, dst.z + j, 0, dst.x, dst.y);
                    }
                }
            }
        }

        internal override void UpdateValidity(APVDataLocation source, List<BrickChunkAlloc> srcLocations, List<BrickChunkAlloc> dstLocations, int destStartIndex)
        {
            Debug.Assert(m_ContainsValidity);

            for (int i = 0; i < srcLocations.Count; i++)
            {
                BrickChunkAlloc src = srcLocations[i];
                BrickChunkAlloc dst = dstLocations[destStartIndex + i];

                for (int j = 0; j < brickProbeCountPerDim; j++)
                {
                    int width = Mathf.Min(chunkSizeInBricks * brickProbeCountPerDim, source.width - src.x);
                    Graphics.CopyTexture(source.TexValidity, src.z + j, 0, src.x, src.y, width, brickProbeCountPerDim, m_Pool.TexValidity, dst.z + j, 0, dst.x, dst.y);
                }
            }
        }

        internal bool EnsureTextureValidity(bool renderingLayers, bool skyOcclusion, bool skyDirection, bool probeOcclusion)
        {
            if (m_ContainsRenderingLayers != renderingLayers || m_ContainsSkyOcclusion != skyOcclusion || m_ContainsSkyShadingDirection != skyDirection || m_ContainsProbeOcclusion != probeOcclusion)
            {
                m_Pool.Cleanup();

                m_ContainsRenderingLayers = renderingLayers;
                m_ContainsSkyOcclusion = skyOcclusion;
                m_ContainsSkyShadingDirection = skyDirection;
                m_ContainsProbeOcclusion = probeOcclusion;
                AllocatePool(m_Pool.width, m_Pool.height, m_Pool.depth);
                return false;
            }
            return true;
        }

        internal void Update(CommandBuffer cmd, CellStreamingScratchBuffer dataBuffer, CellStreamingScratchBufferLayout layout,
           List<BrickChunkAlloc> dstLocations, bool updateSharedData, Texture validityTexture, ProbeVolumeSHBands bands,
           bool skyOcclusion, Texture skyOcclusionTexture, bool skyShadingDirections, Texture skyShadingDirectionsTexture, bool probeOcclusion)
        {
            if (dstLocations.Count == 0)
                return;

            bool uploadL2 = bands == ProbeVolumeSHBands.SphericalHarmonicsL2;

            using (new ProfilingScope(cmd, ProfilingSampler.Get(CoreProfileId.APVDiskStreamingUpdatePool)))
            {
                int chunkCount = dstLocations.Count;

                cmd.SetComputeTextureParam(m_DataUploadCS, m_DataUploadKernel, _Out_L0_L1Rx, m_Pool.TexL0_L1rx);
                cmd.SetComputeTextureParam(m_DataUploadCS, m_DataUploadKernel, _Out_L1G_L1Ry, m_Pool.TexL1_G_ry);
                cmd.SetComputeTextureParam(m_DataUploadCS, m_DataUploadKernel, _Out_L1B_L1Rz, m_Pool.TexL1_B_rz);

                if (updateSharedData)
                {
                    cmd.EnableKeyword(m_DataUploadCS, m_DataUpload_Shared);
                    cmd.SetComputeTextureParam(m_DataUploadCS, m_DataUploadKernel, _Out_Shared, validityTexture);

                    if (skyOcclusion)
                    {
                        cmd.EnableKeyword(m_DataUploadCS, m_DataUpload_SkyOcclusion);
                        cmd.SetComputeTextureParam(m_DataUploadCS, m_DataUploadKernel, _Out_SkyOcclusionL0L1, skyOcclusionTexture);
                        if (skyShadingDirections)
                        {
                            cmd.SetComputeTextureParam(m_DataUploadCS, m_DataUploadKernel, _Out_SkyShadingDirectionIndices, skyShadingDirectionsTexture);
                            cmd.EnableKeyword(m_DataUploadCS, m_DataUpload_SkyShadingDirection);
                        }
                        else
                            cmd.DisableKeyword(m_DataUploadCS, m_DataUpload_SkyShadingDirection);
                    }
                    else
                    {
                        cmd.DisableKeyword(m_DataUploadCS, m_DataUpload_SkyOcclusion);
                        cmd.DisableKeyword(m_DataUploadCS, m_DataUpload_SkyShadingDirection);
                    }
                }
                else
                {
                    cmd.DisableKeyword(m_DataUploadCS, m_DataUpload_Shared);
                    cmd.DisableKeyword(m_DataUploadCS, m_DataUpload_SkyOcclusion);
                    cmd.DisableKeyword(m_DataUploadCS, m_DataUpload_SkyShadingDirection);
                }

                if (uploadL2)
                {
                    cmd.SetComputeTextureParam(m_DataUploadL2CS, m_DataUploadL2Kernel, _Out_L2_0, m_Pool.TexL2_0);
                    cmd.SetComputeTextureParam(m_DataUploadL2CS, m_DataUploadL2Kernel, _Out_L2_1, m_Pool.TexL2_1);
                    cmd.SetComputeTextureParam(m_DataUploadL2CS, m_DataUploadL2Kernel, _Out_L2_2, m_Pool.TexL2_2);
                    cmd.SetComputeTextureParam(m_DataUploadL2CS, m_DataUploadL2Kernel, _Out_L2_3, m_Pool.TexL2_3);
                }

                if (probeOcclusion)
                {
                    cmd.EnableKeyword(m_DataUploadCS, m_DataUpload_ProbeOcclusion);
                    cmd.SetComputeTextureParam(m_DataUploadCS, m_DataUploadKernel, _Out_ProbeOcclusion, m_Pool.TexProbeOcclusion);
                }
                else
                {
                    cmd.DisableKeyword(m_DataUploadCS, m_DataUpload_ProbeOcclusion);
                }

                const int numthreads = 64;
                const int probePerThread = 4; // We can upload 4 probes per thread in the current shader.
                int threadX = DivRoundUp(chunkSizeInBricks * brickProbeCountTotal / probePerThread, numthreads);

                ConstantBuffer.Push(cmd, layout, m_DataUploadCS, _ProbeVolumeScratchBufferLayout);
                cmd.SetComputeBufferParam(m_DataUploadCS, m_DataUploadKernel, _ProbeVolumeScratchBuffer, dataBuffer.buffer);
                cmd.DispatchCompute(m_DataUploadCS, m_DataUploadKernel, threadX, 1, chunkCount);

                if (uploadL2)
                {
                    ConstantBuffer.Push(cmd, layout, m_DataUploadL2CS, _ProbeVolumeScratchBufferLayout);
                    cmd.SetComputeBufferParam(m_DataUploadL2CS, m_DataUploadL2Kernel, _ProbeVolumeScratchBuffer, dataBuffer.buffer);
                    cmd.DispatchCompute(m_DataUploadL2CS, m_DataUploadL2Kernel, threadX, 1, chunkCount);
                }
            }
        }

        public static APVDataLocation CreateDataLocation(int numProbes, bool compressed, ProbeVolumeSHBands bands, string name, bool allocateRendertexture,
           bool allocateValidityData, bool allocateRenderingLayers, bool allocateSkyOcclusionData, bool allocateSkyShadingDirectionData, bool allocateProbeOcclusionData, out int allocatedBytes)
        {
            Vector3Int locSize = ProbeCountToDataLocSize(numProbes);
            int width = locSize.x;
            int height = locSize.y;
            int depth = locSize.z;

            APVDataLocation loc = default;
            var L0Format = GraphicsFormat.R16G16B16A16_SFloat;
            var L1L2Format = compressed ? GraphicsFormat.RGBA_BC7_UNorm : GraphicsFormat.R8G8B8A8_UNorm;

            var ValidityFormat = allocateRenderingLayers ?
                // for 32 bits we use a float format but it's an uint
                GraphicsFormat.R32_SFloat :
                // NOTE: Platforms that do not support Sample nor LoadStore for R8_UNorm need to fallback to RGBA8_UNorm since that format should be supported for both (e.g. GLES3.x)
                SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, GraphicsFormatUsage.Sample | GraphicsFormatUsage.LoadStore) ? GraphicsFormat.R8_UNorm : GraphicsFormat.R8G8B8A8_UNorm;

            allocatedBytes = 0;
            try
            {
                loc.TexL0_L1rx = CreateDataTexture(width, height, depth, L0Format, $"{name}_TexL0_L1rx", allocateRendertexture, ref allocatedBytes);
                loc.TexL1_G_ry = CreateDataTexture(width, height, depth, L1L2Format, $"{name}_TexL1_G_ry", allocateRendertexture, ref allocatedBytes);
                loc.TexL1_B_rz = CreateDataTexture(width, height, depth, L1L2Format, $"{name}_TexL1_B_rz", allocateRendertexture, ref allocatedBytes);

                if (allocateValidityData)
                    loc.TexValidity = CreateDataTexture(width, height, depth, ValidityFormat, $"{name}_Validity", allocateRendertexture, ref allocatedBytes);

                if (allocateSkyOcclusionData)
                    loc.TexSkyOcclusion = CreateDataTexture(width, height, depth, GraphicsFormat.R16G16B16A16_SFloat, $"{name}_SkyOcclusion", allocateRendertexture, ref allocatedBytes);

                if (allocateSkyShadingDirectionData)
                    loc.TexSkyShadingDirectionIndices = CreateDataTexture(width, height, depth, GraphicsFormat.R8_UNorm, $"{name}_SkyShadingDirectionIndices", allocateRendertexture, ref allocatedBytes);

                if (allocateProbeOcclusionData)
                    loc.TexProbeOcclusion = CreateDataTexture(width, height, depth, GraphicsFormat.R8G8B8A8_UNorm, $"{name}_ProbeOcclusion", allocateRendertexture, ref allocatedBytes);

                if (bands == ProbeVolumeSHBands.SphericalHarmonicsL2)
                {
                    loc.TexL2_0 = CreateDataTexture(width, height, depth, L1L2Format, $"{name}_TexL2_0", allocateRendertexture, ref allocatedBytes);
                    loc.TexL2_1 = CreateDataTexture(width, height, depth, L1L2Format, $"{name}_TexL2_1", allocateRendertexture, ref allocatedBytes);
                    loc.TexL2_2 = CreateDataTexture(width, height, depth, L1L2Format, $"{name}_TexL2_2", allocateRendertexture, ref allocatedBytes);
                    loc.TexL2_3 = CreateDataTexture(width, height, depth, L1L2Format, $"{name}_TexL2_3", allocateRendertexture, ref allocatedBytes);
                }

                loc.width = width;
                loc.height = height;
                loc.depth = depth;

                return loc;
            }
            catch
            {
                allocatedBytes = 0;
                loc.Cleanup();
                throw;
            }
        }

        [ProbeRuntimeResourceHandler]
        private static void HandlePoolResource(APVDataLocation pool, ref APVRuntimeResource resource)
        {
            resource.L0_L1rx = pool.TexL0_L1rx as RenderTexture;

            resource.L1_G_ry = pool.TexL1_G_ry as RenderTexture;
            resource.L1_B_rz = pool.TexL1_B_rz as RenderTexture;

            resource.L2_0 = pool.TexL2_0 as RenderTexture;
            resource.L2_1 = pool.TexL2_1 as RenderTexture;
            resource.L2_2 = pool.TexL2_2 as RenderTexture;
            resource.L2_3 = pool.TexL2_3 as RenderTexture;

            resource.ProbeOcclusion = pool.TexProbeOcclusion as RenderTexture;

            resource.Validity = pool.TexValidity as RenderTexture;
            resource.SkyOcclusionL0L1 = pool.TexSkyOcclusion as RenderTexture;
            resource.SkyShadingDirectionIndices = pool.TexSkyShadingDirectionIndices as RenderTexture;
        }
    }

    public class APVProbeBrickBlendingPool : TProbeBrickBlendingPool<APVProbeBrickPool, APVDataLocation>
    {
        static readonly int _PoolDim_LerpFactor = Shader.PropertyToID("_PoolDim_LerpFactor");
        static readonly int _ChunkList = Shader.PropertyToID("_ChunkList");

        static readonly int _State0_L0_L1Rx = Shader.PropertyToID("_State0_L0_L1Rx");
        static readonly int _State0_L1G_L1Ry = Shader.PropertyToID("_State0_L1G_L1Ry");
        static readonly int _State0_L1B_L1Rz = Shader.PropertyToID("_State0_L1B_L1Rz");
        static readonly int _State0_L2_0 = Shader.PropertyToID("_State0_L2_0");
        static readonly int _State0_L2_1 = Shader.PropertyToID("_State0_L2_1");
        static readonly int _State0_L2_2 = Shader.PropertyToID("_State0_L2_2");
        static readonly int _State0_L2_3 = Shader.PropertyToID("_State0_L2_3");
        static readonly int _State0_ProbeOcclusion = Shader.PropertyToID("_State0_ProbeOcclusion");

        static readonly int _State1_L0_L1Rx = Shader.PropertyToID("_State1_L0_L1Rx");
        static readonly int _State1_L1G_L1Ry = Shader.PropertyToID("_State1_L1G_L1Ry");
        static readonly int _State1_L1B_L1Rz = Shader.PropertyToID("_State1_L1B_L1Rz");
        static readonly int _State1_L2_0 = Shader.PropertyToID("_State1_L2_0");
        static readonly int _State1_L2_1 = Shader.PropertyToID("_State1_L2_1");
        static readonly int _State1_L2_2 = Shader.PropertyToID("_State1_L2_2");
        static readonly int _State1_L2_3 = Shader.PropertyToID("_State1_L2_3");
        static readonly int _State1_ProbeOcclusion = Shader.PropertyToID("_State1_ProbeOcclusion");

        ComputeShader m_StateBlendShader;
        int m_ScenarioBlendingKernel = -1;

        Vector4[] m_ChunkList;
        int m_MappedChunks;

        ProbeVolumeSHBands m_ShBands;
        bool m_ProbeOcclusion;

        public APVProbeBrickBlendingPool(ProbeVolumeBlendingTextureMemoryBudget memoryBudget, bool supportBlending, ProbeVolumeSHBands shBands, bool probeOcclusion) : base(memoryBudget, supportBlending)
        {            
            // Casting to other memory budget struct works cause it's casted to int in the end anyway
            m_ShBands = shBands;
            m_ProbeOcclusion = probeOcclusion;
        }


        internal override void Initialize()
        {
            base.Initialize();

            m_StateBlendShader = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<ProbeVolumeRuntimeResources>()?.probeVolumeBlendStatesCS;
            m_ScenarioBlendingKernel = m_StateBlendShader ? m_StateBlendShader.FindKernel("BlendScenarios") : -1;
        }

        internal override void AllocateResourcesIfNeeded()
        {
            if (isAllocated)
                return;

            m_State0 = new APVProbeBrickPool(m_MemoryBudget, m_ShBands, allocateProbeOcclusionData: m_ProbeOcclusion);
            m_State1 = new APVProbeBrickPool(m_MemoryBudget, m_ShBands, allocateProbeOcclusionData: m_ProbeOcclusion);
            m_State0.Initialize();
            m_State1.Initialize();

            int maxAvailablebrickCount = (GetPoolWidth() / ProbeBrickPoolBase.chunkProbeCountPerDim)
                                       * (GetPoolHeight() / ProbeBrickPoolBase.brickProbeCountPerDim)
                                       * (GetPoolDepth() / ProbeBrickPoolBase.brickProbeCountPerDim);

            m_ChunkList = new Vector4[maxAvailablebrickCount];
            m_MappedChunks = 0;
        }


        internal override void PerformBlending(CommandBuffer cmd, float factor, APVProbeBrickPool dstPool)
        {
            if (m_MappedChunks == 0)
                return;

            cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State0_L0_L1Rx, m_State0.m_Pool.TexL0_L1rx);
            cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State0_L1G_L1Ry, m_State0.m_Pool.TexL1_G_ry);
            cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State0_L1B_L1Rz, m_State0.m_Pool.TexL1_B_rz);

            cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State1_L0_L1Rx, m_State1.m_Pool.TexL0_L1rx);
            cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State1_L1G_L1Ry, m_State1.m_Pool.TexL1_G_ry);
            cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State1_L1B_L1Rz, m_State1.m_Pool.TexL1_B_rz);

            cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, APVProbeBrickPool._Out_L0_L1Rx, dstPool.m_Pool.TexL0_L1rx);
            cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, APVProbeBrickPool._Out_L1G_L1Ry, dstPool.m_Pool.TexL1_G_ry);
            cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, APVProbeBrickPool._Out_L1B_L1Rz, dstPool.m_Pool.TexL1_B_rz);

            if (m_ShBands == ProbeVolumeSHBands.SphericalHarmonicsL2)
            {
                m_StateBlendShader.EnableKeyword("PROBE_VOLUMES_L2");

                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State0_L2_0, m_State0.m_Pool.TexL2_0);
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State0_L2_1, m_State0.m_Pool.TexL2_1);
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State0_L2_2, m_State0.m_Pool.TexL2_2);
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State0_L2_3, m_State0.m_Pool.TexL2_3);

                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State1_L2_0, m_State1.m_Pool.TexL2_0);
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State1_L2_1, m_State1.m_Pool.TexL2_1);
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State1_L2_2, m_State1.m_Pool.TexL2_2);
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State1_L2_3, m_State1.m_Pool.TexL2_3);

                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, APVProbeBrickPool._Out_L2_0, dstPool.m_Pool.TexL2_0);
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, APVProbeBrickPool._Out_L2_1, dstPool.m_Pool.TexL2_1);
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, APVProbeBrickPool._Out_L2_2, dstPool.m_Pool.TexL2_2);
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, APVProbeBrickPool._Out_L2_3, dstPool.m_Pool.TexL2_3);
            }
            else
                m_StateBlendShader.DisableKeyword("PROBE_VOLUMES_L2");

            if (m_ProbeOcclusion)
            {
                m_StateBlendShader.EnableKeyword("USE_APV_PROBE_OCCLUSION");
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State0_ProbeOcclusion, m_State0.m_Pool.TexProbeOcclusion);
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, _State1_ProbeOcclusion, m_State1.m_Pool.TexProbeOcclusion);
                cmd.SetComputeTextureParam(m_StateBlendShader, m_ScenarioBlendingKernel, APVProbeBrickPool._Out_ProbeOcclusion, dstPool.m_Pool.TexProbeOcclusion);
            }
            else
                m_StateBlendShader.DisableKeyword("USE_APV_PROBE_OCCLUSION");

            var poolDim_LerpFactor = new Vector4(dstPool.GetPoolWidth(), dstPool.GetPoolHeight(), factor, 0.0f);

            const int numthreads = 4;
            int threadX = ProbeBrickPoolBase.DivRoundUp(ProbeBrickPoolBase.chunkProbeCountPerDim, numthreads);
            int threadY = ProbeBrickPoolBase.DivRoundUp(ProbeBrickPoolBase.brickProbeCountPerDim, numthreads);
            int threadZ = ProbeBrickPoolBase.DivRoundUp(ProbeBrickPoolBase.brickProbeCountPerDim, numthreads);

            cmd.SetComputeVectorArrayParam(m_StateBlendShader, _ChunkList, m_ChunkList);
            cmd.SetComputeVectorParam(m_StateBlendShader, _PoolDim_LerpFactor, poolDim_LerpFactor);
            cmd.DispatchCompute(m_StateBlendShader, m_ScenarioBlendingKernel, threadX, threadY, threadZ * m_MappedChunks);
            m_MappedChunks = 0;
        }


        internal override void BlendChunks(BaseCell cell, APVProbeBrickPool dstPool)
        {
            for (int c = 0; c < cell.blendingInfo.chunkList.Count; c++)
            {
                var chunk = cell.blendingInfo.chunkList[c];
                int dst = cell.poolInfo.chunkList[c].flattenIndex(dstPool.GetPoolWidth(), dstPool.GetPoolHeight());

                m_ChunkList[m_MappedChunks++] = new Vector4(chunk.x, chunk.y, chunk.z, dst);
            }
        }


        internal override void Update(APVDataLocation source, List<BrickChunkAlloc> srcLocations, List<BrickChunkAlloc> dstLocations, int destStartIndex, int state)
        {
            (state == 0 ? m_State0 : m_State1).Update(source, srcLocations, dstLocations, destStartIndex);
        }

        internal void Update(CommandBuffer cmd, CellStreamingScratchBuffer dataBuffer, CellStreamingScratchBufferLayout layout,
            List<BrickChunkAlloc> dstLocations, ProbeVolumeSHBands bands, int state, Texture validityTexture,
            bool skyOcclusion, Texture skyOcclusionTexture, bool skyShadingDirections, Texture skyShadingDirectionsTexture, bool probeOcclusion)
        {
            bool updateShared = state == 0 ? true : false;

            (state == 0 ? m_State0 : m_State1).Update(cmd, dataBuffer, layout, dstLocations,
                updateShared, validityTexture, bands, updateShared && skyOcclusion, skyOcclusionTexture,
                updateShared && skyShadingDirections, skyShadingDirectionsTexture, probeOcclusion);
        }

    }

}


