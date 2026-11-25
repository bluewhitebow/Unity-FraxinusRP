using Unity.Mathematics;

namespace UnityEngine.Rendering
{
    internal partial class APVProbeReferenceVolume : TProbeReferenceVolume<
        APVProbeReferenceVolume,
        APVCellData, 
        APVCellData.PerScenarioData, 
        APVCell, 
        APVProbeBrickPool,
        APVProbeBrickBlendingPool,
        APVDataLocation>
    {
        internal static class ShaderIDs
        {
            // Adaptive Probe Volume
            public static readonly int _APVResIndex = Shader.PropertyToID("_APVResIndex");
            public static readonly int _APVResCellIndices = Shader.PropertyToID("_APVResCellIndices");
            public static readonly int _APVResL0_L1Rx = Shader.PropertyToID("_APVResL0_L1Rx");
            public static readonly int _APVResL1G_L1Ry = Shader.PropertyToID("_APVResL1G_L1Ry");
            public static readonly int _APVResL1B_L1Rz = Shader.PropertyToID("_APVResL1B_L1Rz");

            public static readonly int _APVResL2_0 = Shader.PropertyToID("_APVResL2_0");
            public static readonly int _APVResL2_1 = Shader.PropertyToID("_APVResL2_1");
            public static readonly int _APVResL2_2 = Shader.PropertyToID("_APVResL2_2");
            public static readonly int _APVResL2_3 = Shader.PropertyToID("_APVResL2_3");

            public static readonly int _APVProbeOcclusion = Shader.PropertyToID("_APVProbeOcclusion");

            public static readonly int _APVResValidity = Shader.PropertyToID("_APVResValidity");
            public static readonly int _SkyOcclusionTexL0L1 = Shader.PropertyToID("_SkyOcclusionTexL0L1");
            public static readonly int _SkyShadingDirectionIndicesTex = Shader.PropertyToID("_SkyShadingDirectionIndicesTex");
            public static readonly int _SkyPrecomputedDirections = Shader.PropertyToID("_SkyPrecomputedDirections");
            public static readonly int _AntiLeakData = Shader.PropertyToID("_AntiLeakData");
        }

        public override void BindPRVRuntimeResources(CommandBuffer cmdBuffer, bool isProbeVolumeEnabled)
        {
            bool needToBindNeutral = true;
            var refVolume = APVProbeReferenceVolume.instance;

            // Do this only if probe volume is enabled
            // if (isProbeVolumeEnabled && m_ProbeReferenceVolumeInit)
            if (isProbeVolumeEnabled)
            {
                APVRuntimeResource rr = refVolume.GetRuntimeResources<APVRuntimeResource>();

                bool validResources = rr.index != null && rr.L0_L1rx != null && rr.L1_G_ry != null && rr.L1_B_rz != null;
                validResources &= (refVolume.shBands == ProbeVolumeSHBands.SphericalHarmonicsL2 && rr.L2_0 != null) || refVolume.shBands == ProbeVolumeSHBands.SphericalHarmonicsL1;

                if (validResources)
                {
                    cmdBuffer.SetGlobalBuffer(ShaderIDs._APVResIndex, rr.index);
                    cmdBuffer.SetGlobalBuffer(ShaderIDs._APVResCellIndices, rr.cellIndices);

                    cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL0_L1Rx, rr.L0_L1rx);
                    cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL1G_L1Ry, rr.L1_G_ry);
                    cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL1B_L1Rz, rr.L1_B_rz);
                    cmdBuffer.SetGlobalTexture(ShaderIDs._APVResValidity, rr.Validity);

                    cmdBuffer.SetGlobalTexture(ShaderIDs._SkyOcclusionTexL0L1, rr.SkyOcclusionL0L1 ?? (RenderTargetIdentifier)CoreUtils.blackVolumeTexture);
                    cmdBuffer.SetGlobalTexture(ShaderIDs._SkyShadingDirectionIndicesTex, rr.SkyShadingDirectionIndices ?? (RenderTargetIdentifier)CoreUtils.blackVolumeTexture);
                    cmdBuffer.SetGlobalBuffer(ShaderIDs._SkyPrecomputedDirections, rr.SkyPrecomputedDirections);
                    cmdBuffer.SetGlobalBuffer(ShaderIDs._AntiLeakData, rr.QualityLeakReductionData);

                    if (refVolume.shBands == ProbeVolumeSHBands.SphericalHarmonicsL2)
                    {
                        cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL2_0, rr.L2_0);
                        cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL2_1, rr.L2_1);
                        cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL2_2, rr.L2_2);
                        cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL2_3, rr.L2_3);
                    }

                    cmdBuffer.SetGlobalTexture(ShaderIDs._APVProbeOcclusion, rr.ProbeOcclusion ?? (RenderTargetIdentifier)CoreUtils.whiteVolumeTexture);

                    needToBindNeutral = false;
                }
            }

            if (needToBindNeutral)
            {
                // Lazy init the empty buffer. We use sizeof(uint3) so that this buffer can be
                // used with uint and uint3 bindings without triggering validation errors.
                if (m_EmptyIndexBuffer == null)
                    m_EmptyIndexBuffer = new ComputeBuffer(1, sizeof(uint) * 3, ComputeBufferType.Structured);

                cmdBuffer.SetGlobalBuffer(ShaderIDs._APVResIndex, m_EmptyIndexBuffer);
                cmdBuffer.SetGlobalBuffer(ShaderIDs._APVResCellIndices, m_EmptyIndexBuffer);

                cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL0_L1Rx, CoreUtils.blackVolumeTexture);

                cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL1G_L1Ry, CoreUtils.blackVolumeTexture);
                cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL1B_L1Rz, CoreUtils.blackVolumeTexture);
                cmdBuffer.SetGlobalTexture(ShaderIDs._APVResValidity, CoreUtils.blackVolumeTexture);

                cmdBuffer.SetGlobalTexture(ShaderIDs._SkyOcclusionTexL0L1, CoreUtils.blackVolumeTexture);
                cmdBuffer.SetGlobalTexture(ShaderIDs._SkyShadingDirectionIndicesTex, CoreUtils.blackVolumeTexture);
                cmdBuffer.SetGlobalBuffer(ShaderIDs._SkyPrecomputedDirections, m_EmptyIndexBuffer);
                cmdBuffer.SetGlobalBuffer(ShaderIDs._AntiLeakData, m_EmptyIndexBuffer);

                if (refVolume.shBands == ProbeVolumeSHBands.SphericalHarmonicsL2)
                {
                    cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL2_0, CoreUtils.blackVolumeTexture);
                    cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL2_1, CoreUtils.blackVolumeTexture);
                    cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL2_2, CoreUtils.blackVolumeTexture);
                    cmdBuffer.SetGlobalTexture(ShaderIDs._APVResL2_3, CoreUtils.blackVolumeTexture);
                }

                cmdBuffer.SetGlobalTexture(ShaderIDs._APVProbeOcclusion, CoreUtils.whiteVolumeTexture);
            }
        }

        public override bool UpdateShaderVariablesProbeVolumes(CommandBuffer cmd, ProbeVolumesOptions probeVolumeOptions, int taaFrameIndex, bool supportRenderingLayers = false)
        {
            bool enableProbeVolumes = DataHasBeenLoaded();

            if (enableProbeVolumes)
            {
                ProbeVolumeShadingParameters parameters = default;
                // VolumeManager can temporarily have no stack (pipeline
                // construction, preview cameras, or a user-created render
                // entry point). Use the component's documented defaults
                // without allocating a transient ScriptableObject every frame.
                parameters.normalBias = probeVolumeOptions?.normalBias.value ?? 0.05f;
                parameters.viewBias = probeVolumeOptions?.viewBias.value ?? 0.1f;
                parameters.scaleBiasByMinDistanceBetweenProbes = probeVolumeOptions?.scaleBiasWithMinProbeDistance.value ?? false;
                parameters.samplingNoise = probeVolumeOptions?.samplingNoise.value ?? 0.1f;
                parameters.weight = probeVolumeOptions?.intensityMultiplier.value ?? 1.0f;
                parameters.leakReductionMode = probeVolumeOptions?.leakReductionMode.value ?? APVLeakReductionMode.Quality;
                parameters.frameIndexForNoise = taaFrameIndex * ((probeVolumeOptions?.animateSamplingNoise.value ?? true) ? 1 : 0);
                parameters.reflNormalizationLowerClamp = 0.005f;
                parameters.reflNormalizationUpperClamp = (probeVolumeOptions?.occlusionOnlyReflectionNormalization.value ?? true) ? 1.0f : 7.0f;

                parameters.skyOcclusionIntensity = skyOcclusion ? (probeVolumeOptions?.skyOcclusionIntensityMultiplier.value ?? 1.0f) : 0.0f;
                parameters.skyOcclusionShadingDirection = skyOcclusion && skyOcclusionShadingDirection;
                parameters.regionCount = m_CurrentBakingSet != null ? m_CurrentBakingSet.bakedMaskCount : 0;
                parameters.regionLayerMasks = (supportRenderingLayers && m_CurrentBakingSet != null) ? m_CurrentBakingSet.bakedLayerMasks : 0xFFFFFFFF;
                parameters.worldOffset = probeVolumeOptions?.worldOffset.value ?? Vector3.zero;
                UpdateConstantBufferForEditor(cmd, parameters);
            }

            return enableProbeVolumes;
        }

        internal void UpdateConstantBufferForEditor(CommandBuffer cmd, ProbeVolumeShadingParameters parameters)
        {
            float normalBias = parameters.normalBias;
            float viewBias = parameters.viewBias;
            var leakReductionMode = parameters.leakReductionMode;

            if (parameters.scaleBiasByMinDistanceBetweenProbes)
            {
                normalBias *= MinDistanceBetweenProbes();
                viewBias *= MinDistanceBetweenProbes();
            }

            var indexDim = m_CellIndices.GetGlobalIndirectionDimension();
            var poolDim = m_Pool.GetPoolDimensions();
            m_CellIndices.GetMinMaxEntry(out Vector3Int minEntry, out Vector3Int _);
            var entriesPerCell = m_CellIndices.entriesPerCellDimension;
            var skyDirectionWeight = parameters.skyOcclusionShadingDirection ? 1.0f : 0.0f;
            var probeOffset = ProbeOffset() + parameters.worldOffset;

            APVShaderVariablesProbeVolumes shaderVars;
            shaderVars._Offset_LayerCount = new Vector4(probeOffset.x, probeOffset.y, probeOffset.z, parameters.regionCount);
            shaderVars._MinLoadedCellInEntries_IndirectionEntryDim = new Vector4(_minLoadedCellPos.x * entriesPerCell, _minLoadedCellPos.y * entriesPerCell, _minLoadedCellPos.z * entriesPerCell, GetEntrySize());
            shaderVars._MaxLoadedCellInEntries_RcpIndirectionEntryDim = new Vector4((_maxLoadedCellPos.x + 1) * entriesPerCell - 1, (_maxLoadedCellPos.y + 1) * entriesPerCell - 1, (_maxLoadedCellPos.z + 1) * entriesPerCell - 1, 1.0f / GetEntrySize());
            shaderVars._PoolDim_MinBrickSize = new Vector4(poolDim.x, poolDim.y, poolDim.z, MinBrickSize());
            shaderVars._RcpPoolDim_XY = new Vector4(1.0f / poolDim.x, 1.0f / poolDim.y, 1.0f / poolDim.z, 1.0f / (poolDim.x * poolDim.y));
            shaderVars._MinEntryPos_Noise = new Vector4(minEntry.x, minEntry.y, minEntry.z, parameters.samplingNoise);
            shaderVars._EntryCount_X_XY_LeakReduction = new uint4((uint)indexDim.x, (uint)indexDim.x * (uint)indexDim.y, (uint)leakReductionMode, 0); // One slot available here
            shaderVars._Biases_NormalizationClamp = new Vector4(normalBias, viewBias, parameters.reflNormalizationLowerClamp, parameters.reflNormalizationUpperClamp);
            shaderVars._FrameIndex_Weights = new Vector4(parameters.frameIndexForNoise, parameters.weight, parameters.skyOcclusionIntensity, skyDirectionWeight);
            shaderVars._ProbeVolumeLayerMask = parameters.regionLayerMasks;

            ConstantBuffer.PushGlobal(cmd, shaderVars, shaderVariablesID);
        }
    }
}
