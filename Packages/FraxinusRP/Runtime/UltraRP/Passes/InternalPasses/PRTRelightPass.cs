using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Internal pass for PRT probe volume relight
    /// </summary>
    public class PRTRelightPass : BuiltinPass
    {
        private PRTProbeVolume _currentVolume;
        private ComputeBuffer _brickRadianceBuffer;
        private ComputeBuffer _factorBuffer;
        private ComputeBuffer _validityMaskBuffer;
        private ComputeBuffer _reflectionProbeBuffer;

        private const int k_brickRadianceStride = 28; // float3 * 2 + float = 28 bytes
        private const int k_reflectionProbeDataStride = 48;
        private const int k_maxReflectionProbes = 64;

        private readonly ProfilingSampler _relightSampler = new("PRT Relight");
        private readonly List<PRTProbe> _probesToUpdate = new();

        private static class ShaderIDs
        {
            public static readonly int _coefficientVoxelGridSize = Shader.PropertyToID("_coefficientVoxelGridSize");
            public static readonly int _coefficientVoxelSize = Shader.PropertyToID("_coefficientVoxelSize");
            public static readonly int _coefficientVoxelCorner = Shader.PropertyToID("_coefficientVoxelCorner");
            public static readonly int _coefficientVoxel3D = Shader.PropertyToID("_coefficientVoxel3D");
            public static readonly int _validityVoxel3D = Shader.PropertyToID("_validityVoxel3D");
            public static readonly int _boundingBoxMin = Shader.PropertyToID("_boundingBoxMin");
            public static readonly int _boundingBoxSize = Shader.PropertyToID("_boundingBoxSize");
            public static readonly int _originalBoundingBoxMin = Shader.PropertyToID("_originalBoundingBoxMin");
            public static readonly int _reflectionProbeNormalizationData = Shader.PropertyToID("_reflectionProbeNormalizationData");
        }

        public void Render(RenderGraph renderGraph, ContextContainer frameData, PRTProbeVolume volume)
        {
            if (volume == null || !volume.IsActivate())
                return;

            // Import external RenderTextures as TextureHandles
            // Convert RenderTexture to RTHandle for RenderGraph compatibility
            RTHandle coefficientVoxelRT = RTHandles.Alloc(volume.coefficientVoxel3D);
            RTHandle validityVoxelRT = RTHandles.Alloc(volume.validityVoxel3D);
            
            TextureHandle coefficientVoxelHandle = renderGraph.ImportTexture(coefficientVoxelRT);
            TextureHandle validityVoxelHandle = renderGraph.ImportTexture(validityVoxelRT);

            using var builder = renderGraph.AddRasterRenderPass<PassData>("PRT Relight", out var passData, _relightSampler);
            
            passData.volume = volume;
            passData.pass = this;
            passData.coefficientVoxel = coefficientVoxelHandle;
            passData.validityVoxel = validityVoxelHandle;

            // Declare texture dependencies
            builder.UseTexture(coefficientVoxelHandle, AccessFlags.ReadWrite);
            builder.UseTexture(validityVoxelHandle, AccessFlags.ReadWrite);

            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);

            builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
            {
                data.pass.ExecuteRelight(context.cmd, data.volume, data.coefficientVoxel, data.validityVoxel);
            });
        }

        private void ExecuteRelight(RasterCommandBuffer cmd, PRTProbeVolume volume, TextureHandle coefficientVoxel, TextureHandle validityVoxel)
        {
            if (_currentVolume != volume)
            {
                ReleaseVolumeBuffers();
                _currentVolume = volume;
            }

            // Get probes to update
            _probesToUpdate.Clear();
            volume.GetProbesToUpdate(_probesToUpdate);

            if (_probesToUpdate.Count == 0)
                return;

            // Setup global parameters
            SetupGlobalParameters(cmd, volume);

            // Initialize buffers
            InitializeBuffers(volume);

            // Execute relight (simplified - no compute shaders yet)
            // This will be completed when we add compute shaders
            
            // Set global textures for sampling
            cmd.SetGlobalTexture("_coefficientVoxel3D", coefficientVoxel);
            cmd.SetGlobalTexture("_validityVoxel3D", validityVoxel);
        }

        private void SetupGlobalParameters(RasterCommandBuffer cmd, PRTProbeVolume volume)
        {
            Vector3 corner = volume.GetVoxelMinCorner();
            Vector4 voxelCorner = new Vector4(corner.x, corner.y, corner.z, 0);
            Vector4 voxelSize = new Vector4(volume.probeSizeX, volume.probeSizeY, volume.probeSizeZ, 0);
            Vector4 boundingBoxMin = new Vector4(volume.boundingBoxMin.x, volume.boundingBoxMin.y, volume.boundingBoxMin.z, 0);
            Vector4 boundingBoxSize = new Vector4(volume.currentVoxelGrid.x, volume.currentVoxelGrid.y, volume.currentVoxelGrid.z, 0);
            Vector4 originalBoundingBoxMin = new Vector4(volume.originalBoxMin.x, volume.originalBoxMin.y, volume.originalBoxMin.z, 0);

            cmd.SetGlobalFloat(ShaderIDs._coefficientVoxelGridSize, volume.probeGridSize);
            cmd.SetGlobalVector(ShaderIDs._coefficientVoxelSize, voxelSize);
            cmd.SetGlobalVector(ShaderIDs._coefficientVoxelCorner, voxelCorner);
            cmd.SetGlobalVector(ShaderIDs._boundingBoxMin, boundingBoxMin);
            cmd.SetGlobalVector(ShaderIDs._boundingBoxSize, boundingBoxSize);
            cmd.SetGlobalVector(ShaderIDs._originalBoundingBoxMin, originalBoundingBoxMin);
        }

        private void InitializeBuffers(PRTProbeVolume volume)
        {
            // Initialize factor buffer
            var factors = volume.GetAllFactors();
            if (_factorBuffer == null || _factorBuffer.count != factors.Length)
            {
                _factorBuffer?.Release();
                _factorBuffer = new ComputeBuffer(factors.Length, BrickFactor.stride);
            }
            _factorBuffer.SetData(factors);

            // Initialize validity mask buffer
            int probeCount = volume.probeSizeX * volume.probeSizeY * volume.probeSizeZ;
            if (_validityMaskBuffer == null || _validityMaskBuffer.count != probeCount)
            {
                _validityMaskBuffer?.Release();
                _validityMaskBuffer = new ComputeBuffer(probeCount, 4);
            }

            // Initialize reflection probe buffer if needed
            if (_reflectionProbeBuffer == null)
            {
                _reflectionProbeBuffer = new ComputeBuffer(k_maxReflectionProbes, k_reflectionProbeDataStride);
            }
        }

        private void ReleaseVolumeBuffers()
        {
            _brickRadianceBuffer?.Release();
            _brickRadianceBuffer = null;
            _factorBuffer?.Release();
            _factorBuffer = null;
            _validityMaskBuffer?.Release();
            _validityMaskBuffer = null;
        }

        protected override void OnDispose()
        {
            ReleaseVolumeBuffers();
            _reflectionProbeBuffer?.Release();
            _reflectionProbeBuffer = null;
        }

        protected override void OnCameraCleanup(CommandBuffer cmd)
        {
            // No per-camera cleanup needed for PRT
        }

        private class PassData
        {
            public PRTProbeVolume volume;
            public PRTRelightPass pass;
            public TextureHandle coefficientVoxel;
            public TextureHandle validityVoxel;
        }
    }
}
