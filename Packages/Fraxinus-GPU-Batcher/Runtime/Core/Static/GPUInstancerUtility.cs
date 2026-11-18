using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Events;
using Unity.Collections;
using UnityEngine.Jobs;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fraxinus.GPUBatcher
{
    public static class GPUInstancerUtility
    {
        private static readonly ProfilingSampler s_computeScope = new ProfilingSampler("FraxinusGPUBatcher");

        internal static void DispatchCompute(ComputeShader shader, int kernel, int groupsX, int groupsY, int groupsZ)
        {
            if (shader == null)
                return;

            CommandBuffer commandBuffer = CommandBufferPool.Get("FraxinusGPUBatcher");
            using (new ProfilingScope(commandBuffer, s_computeScope))
                commandBuffer.DispatchCompute(shader, kernel, groupsX, groupsY, groupsZ);
            Graphics.ExecuteCommandBuffer(commandBuffer);
            CommandBufferPool.Release(commandBuffer);
        }

#if UNITY_EDITOR
        /// <summary>Delete only project-owned generated assets.</summary>
        private static void DeleteProjectAsset(UnityEngine.Object asset)
        {
            if (asset == null)
                return;
            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (GPUInstancerConstants.IsPackageAssetPath(assetPath))
            {
                Debug.LogWarning("Skipping deletion of package-owned asset: " + assetPath);
                return;
            }
            if (!string.IsNullOrEmpty(assetPath))
                AssetDatabase.DeleteAsset(assetPath);
        }
#endif

        #region GPU Instancing

        public static Texture2D dummyHiZTex;
        public static GPUIMatrixHandlingType matrixHandlingType;

        /// <summary>
        /// Initializes GPU buffer related data for the instance prototypes. Instance transformation matrices must be generated before this.
        /// </summary>
        public static void InitializeGPUBuffers<T>(List<T> runtimeDataList) where T : GPUInstancerRuntimeData
        {
            if (runtimeDataList == null || runtimeDataList.Count == 0)
                return;

            for (int i = 0; i < runtimeDataList.Count; i++)
            {
                InitializeGPUBuffer(runtimeDataList[i]);
            }
        }

        public static void InitializeGPUBuffer<T>(T runtimeData) where T : GPUInstancerRuntimeData
        {
            if (runtimeData == null || runtimeData.bufferSize == 0)
                return;

            if (runtimeData.instanceLODs == null || runtimeData.instanceLODs.Count == 0)
            {
                Debug.LogError("instance prototype with an empty LOD list detected. There must be at least one LOD defined per instance prototype.");
                return;
            }

            if (dummyHiZTex == null)
                dummyHiZTex = new Texture2D(1, 1);

            #region Set Visibility Buffer
            // Setup the visibility compute buffer
            if (runtimeData.transformationMatrixVisibilityBuffer == null || runtimeData.transformationMatrixVisibilityBuffer.count != runtimeData.bufferSize)
            {
                if (runtimeData.transformationMatrixVisibilityBuffer != null)
                    runtimeData.transformationMatrixVisibilityBuffer.Release();
                runtimeData.transformationMatrixVisibilityBuffer = new ComputeBuffer(runtimeData.bufferSize, GPUInstancerConstants.STRIDE_SIZE_MATRIX4X4);
                if (runtimeData.instanceDataNativeArray.IsCreated)
                    runtimeData.transformationMatrixVisibilityBuffer.SetData(runtimeData.instanceDataNativeArray);
            }
            #endregion Set Visibility Buffer

            #region Set LOD Buffer
            // Setup the LOD buffer
            if (runtimeData.instanceLODDataBuffer == null || runtimeData.instanceLODDataBuffer.count != runtimeData.bufferSize)
            {
                if (runtimeData.instanceLODDataBuffer != null)
                    runtimeData.instanceLODDataBuffer.Release();

                runtimeData.instanceLODDataBuffer = new ComputeBuffer(runtimeData.bufferSize, GPUInstancerConstants.STRIDE_SIZE_FLOAT4);
            }
            #endregion Set LOD Buffer

            #region Set Args Buffer
            if (runtimeData.argsBuffer == null)
            {
                // Initialize indirect renderer buffer

                int totalSubMeshCount = 0;
                for (int i = 0; i < runtimeData.instanceLODs.Count; i++)
                {
                    for (int j = 0; j < runtimeData.instanceLODs[i].renderers.Count; j++)
                    {
                        totalSubMeshCount += runtimeData.instanceLODs[i].renderers[j].mesh.subMeshCount;
                    }
                }

                // Initialize indirect renderer buffer. First LOD's each renderer's all submeshes will be followed by second LOD's each renderer's submeshes and so on.
                runtimeData.args = new uint[5 * totalSubMeshCount];
                int argsLastIndex = 0;

                // Setup LOD Data:
                for (int lod = 0; lod < runtimeData.instanceLODs.Count; lod++)
                {
                    // setup LOD renderers:
                    for (int r = 0; r < runtimeData.instanceLODs[lod].renderers.Count; r++)
                    {
                        runtimeData.instanceLODs[lod].renderers[r].argsBufferOffset = argsLastIndex;
                        // Setup the indirect renderer buffer:
                        for (int j = 0; j < runtimeData.instanceLODs[lod].renderers[r].mesh.subMeshCount; j++)
                        {
                            runtimeData.args[argsLastIndex++] = runtimeData.instanceLODs[lod].renderers[r].mesh.GetIndexCount(j); // index count per instance
                            runtimeData.args[argsLastIndex++] = 0;// (uint)runtimeData.bufferSize;
                            runtimeData.args[argsLastIndex++] = runtimeData.instanceLODs[lod].renderers[r].mesh.GetIndexStart(j); // start index location
                            runtimeData.args[argsLastIndex++] = 0; // base vertex location
                            runtimeData.args[argsLastIndex++] = 0; // start instance location
                        }
                    }
                }

                if (runtimeData.args.Length > 0)
                {
                    runtimeData.argsBuffer = new ComputeBuffer(runtimeData.args.Length, sizeof(uint), ComputeBufferType.IndirectArguments);

                    runtimeData.argsBuffer.SetData(runtimeData.args);

                    if (runtimeData.hasShadowCasterBuffer)
                    {
                        runtimeData.shadowArgs = runtimeData.args.ToArray();

                        if (runtimeData.shadowArgsBuffer != null)
                            runtimeData.shadowArgsBuffer.Release();

                        runtimeData.shadowArgsBuffer = new ComputeBuffer(runtimeData.args.Length, sizeof(uint), ComputeBufferType.IndirectArguments);
                        runtimeData.shadowArgsBuffer.SetData(runtimeData.args);
                    }
                }
            }
            #endregion Set Args Buffer

            SetAppendBuffers(runtimeData);

            runtimeData.InitializeData();
        }

        #region Set Append Buffers Platform Dependent
        public static void SetAppendBuffers<T>(T runtimeData) where T : GPUInstancerRuntimeData
        {
            switch (matrixHandlingType)
            {
                case GPUIMatrixHandlingType.MatrixAppend:
                    SetAppendBuffersVulkan(runtimeData);
                    break;
                case GPUIMatrixHandlingType.CopyToTexture:
                    SetAppendBuffersGLES3(runtimeData);
                    break;
                default:
                    SetAppendBuffersDefault(runtimeData);
                    break;
            }
        }

        private static void SetAppendBuffersDefault<T>(T runtimeData) where T : GPUInstancerRuntimeData
        {
            int lod = 0;
            foreach (GPUInstancerPrototypeLOD gpuiLod in runtimeData.instanceLODs)
            {
                if (gpuiLod.transformationMatrixAppendBuffer == null || gpuiLod.transformationMatrixAppendBuffer.count != runtimeData.bufferSize)
                {
                    // Create the LOD append buffers. Each LOD has its own append buffer.
                    if (gpuiLod.transformationMatrixAppendBuffer != null)
                        gpuiLod.transformationMatrixAppendBuffer.Release();

                    gpuiLod.transformationMatrixAppendBuffer = new ComputeBuffer(runtimeData.bufferSize, GPUInstancerConstants.STRIDE_SIZE_INT, ComputeBufferType.Append);

                    if (runtimeData.hasShadowCasterBuffer)
                    {
                        if (gpuiLod.shadowAppendBuffer != null)
                            gpuiLod.shadowAppendBuffer.Release();
                        gpuiLod.shadowAppendBuffer = new ComputeBuffer(runtimeData.bufferSize, GPUInstancerConstants.STRIDE_SIZE_INT, ComputeBufferType.Append);
                    }
                }

                foreach (GPUInstancerRenderer renderer in gpuiLod.renderers)
                {
                    // Setup instance LOD renderer material property block shader buffers with the append buffer
                    renderer.mpb.SetBuffer(GPUInstancerConstants.VisibilityKernelPoperties.TRANSFORMATION_MATRIX_BUFFER, gpuiLod.transformationMatrixAppendBuffer);
                    renderer.mpb.SetBuffer(GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, runtimeData.transformationMatrixVisibilityBuffer);
                    renderer.mpb.SetBuffer(GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_LOD_BUFFER, runtimeData.instanceLODDataBuffer);
                    renderer.mpb.SetMatrix(GPUInstancerConstants.VisibilityKernelPoperties.RENDERER_TRANSFORM_OFFSET, renderer.transformOffset);
                    renderer.mpb.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_LOD_LEVEL, runtimeData.prototype.isLODCrossFade ? lod : -1);
                    if (runtimeData.prototype.isLODCrossFade)
                    {
                        if (runtimeData.prototype.isLODCrossFadeAnimate)
                            renderer.mpb.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_FADE_LEVEL_MULTIPLIER, 0.01f);
                        else
                            renderer.mpb.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_FADE_LEVEL_MULTIPLIER, 1);
                    }

                    if (runtimeData.hasShadowCasterBuffer && renderer.shadowMPB != null)
                    {
                        renderer.shadowMPB.SetBuffer(GPUInstancerConstants.VisibilityKernelPoperties.TRANSFORMATION_MATRIX_BUFFER, gpuiLod.shadowAppendBuffer);
                        renderer.shadowMPB.SetBuffer(GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, runtimeData.transformationMatrixVisibilityBuffer);
                        renderer.shadowMPB.SetBuffer(GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_LOD_BUFFER, runtimeData.instanceLODDataBuffer);
                        renderer.shadowMPB.SetMatrix(GPUInstancerConstants.VisibilityKernelPoperties.RENDERER_TRANSFORM_OFFSET, renderer.transformOffset);
                        renderer.shadowMPB.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_LOD_LEVEL, -1);
                    }

                    SetRenderingLayerMask(runtimeData, renderer);
                }
                lod++;
            }
        }

        private static void SetAppendBuffersVulkan<T>(T runtimeData) where T : GPUInstancerRuntimeData
        {
            foreach (GPUInstancerPrototypeLOD gpuiLod in runtimeData.instanceLODs)
            {
                if (gpuiLod.transformationMatrixAppendBuffer == null || gpuiLod.transformationMatrixAppendBuffer.count != runtimeData.bufferSize)
                {
                    // Create the LOD append buffers. Each LOD has its own append buffer.
                    if (gpuiLod.transformationMatrixAppendBuffer != null)
                        gpuiLod.transformationMatrixAppendBuffer.Release();

                    gpuiLod.transformationMatrixAppendBuffer = new ComputeBuffer(runtimeData.bufferSize, GPUInstancerConstants.STRIDE_SIZE_MATRIX4X4, ComputeBufferType.Append);

                    if (runtimeData.hasShadowCasterBuffer)
                    {
                        if (gpuiLod.shadowAppendBuffer != null)
                            gpuiLod.shadowAppendBuffer.Release();
                        gpuiLod.shadowAppendBuffer = new ComputeBuffer(runtimeData.bufferSize, GPUInstancerConstants.STRIDE_SIZE_MATRIX4X4, ComputeBufferType.Append);
                    }
                }

                foreach (GPUInstancerRenderer renderer in gpuiLod.renderers)
                {
                    // Setup instance LOD renderer material property block shader buffers with the append buffer
                    renderer.mpb.SetBuffer(GPUInstancerConstants.VisibilityKernelPoperties.TRANSFORMATION_MATRIX_BUFFER, gpuiLod.transformationMatrixAppendBuffer);
                    renderer.mpb.SetMatrix(GPUInstancerConstants.VisibilityKernelPoperties.RENDERER_TRANSFORM_OFFSET, renderer.transformOffset);

                    if (runtimeData.hasShadowCasterBuffer && renderer.shadowMPB != null)
                    {
                        renderer.shadowMPB.SetBuffer(GPUInstancerConstants.VisibilityKernelPoperties.TRANSFORMATION_MATRIX_BUFFER, gpuiLod.shadowAppendBuffer);
                        renderer.shadowMPB.SetMatrix(GPUInstancerConstants.VisibilityKernelPoperties.RENDERER_TRANSFORM_OFFSET, renderer.transformOffset);
                    }

                    SetRenderingLayerMask(runtimeData, renderer);
                }
            }
        }

        private static void SetAppendBuffersGLES3<T>(T runtimeData) where T : GPUInstancerRuntimeData
        {
            int rowCount = Mathf.CeilToInt(runtimeData.bufferSize / (float)GPUInstancerConstants.TEXTURE_MAX_SIZE);
            if (runtimeData.prototype.isLODCrossFade && (runtimeData.instanceLODDataTexture == null || runtimeData.instanceLODDataTexture.width != runtimeData.bufferSize))
            {
                DestroyObject(runtimeData.instanceLODDataTexture);
                runtimeData.instanceLODDataTexture = new RenderTexture(rowCount == 1 ? runtimeData.bufferSize : GPUInstancerConstants.TEXTURE_MAX_SIZE, rowCount, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                {
                    isPowerOfTwo = false,
                    enableRandomWrite = true,
                    filterMode = FilterMode.Point,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                runtimeData.instanceLODDataTexture.Create();
            }

            int lod = 0;
            foreach (GPUInstancerPrototypeLOD gpuiLod in runtimeData.instanceLODs)
            {
                if (gpuiLod.transformationMatrixAppendBuffer == null || gpuiLod.transformationMatrixAppendBuffer.count != runtimeData.bufferSize)
                {
                    // Create the LOD append buffers. Each LOD has its own append buffer.
                    if (gpuiLod.transformationMatrixAppendBuffer != null)
                        gpuiLod.transformationMatrixAppendBuffer.Release();

                    gpuiLod.transformationMatrixAppendBuffer = new ComputeBuffer(runtimeData.bufferSize, GPUInstancerConstants.STRIDE_SIZE_INT, ComputeBufferType.Append);
                }
                if (gpuiLod.transformationMatrixAppendTexture == null || gpuiLod.transformationMatrixAppendTexture.width != runtimeData.bufferSize)
                {
                    DestroyObject(gpuiLod.transformationMatrixAppendTexture);

                    gpuiLod.transformationMatrixAppendTexture = new RenderTexture(rowCount == 1 ? runtimeData.bufferSize : GPUInstancerConstants.TEXTURE_MAX_SIZE, 4 * rowCount, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                    {
                        isPowerOfTwo = false,
                        enableRandomWrite = true,
                        filterMode = FilterMode.Point,
                        useMipMap = false,
                        autoGenerateMips = false
                    };
                    gpuiLod.transformationMatrixAppendTexture.Create();
                }

                if (runtimeData.hasShadowCasterBuffer)
                {
                    if (gpuiLod.shadowAppendBuffer != null)
                        gpuiLod.shadowAppendBuffer.Release();
                    gpuiLod.shadowAppendBuffer = new ComputeBuffer(runtimeData.bufferSize, GPUInstancerConstants.STRIDE_SIZE_INT, ComputeBufferType.Append);

                    DestroyObject(gpuiLod.shadowAppendTexture);

                    gpuiLod.shadowAppendTexture = new RenderTexture(rowCount == 1 ? runtimeData.bufferSize : GPUInstancerConstants.TEXTURE_MAX_SIZE, 4 * rowCount, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                    {
                        isPowerOfTwo = false,
                        enableRandomWrite = true,
                        filterMode = FilterMode.Point,
                        useMipMap = false,
                        autoGenerateMips = false
                    };
                    gpuiLod.shadowAppendTexture.Create();
                }

                foreach (GPUInstancerRenderer renderer in gpuiLod.renderers)
                {
                    // Setup instance LOD renderer material property block shader buffers with the append buffer
                    renderer.mpb.SetTexture(GPUInstancerConstants.BufferToTextureKernelPoperties.TRANSFORMATION_MATRIX_TEXTURE, gpuiLod.transformationMatrixAppendTexture);
                    renderer.mpb.SetMatrix(GPUInstancerConstants.VisibilityKernelPoperties.RENDERER_TRANSFORM_OFFSET, renderer.transformOffset);
                    renderer.mpb.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, runtimeData.bufferSize);
                    renderer.mpb.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.MAX_TEXTURE_SIZE, GPUInstancerConstants.TEXTURE_MAX_SIZE);
                    renderer.mpb.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_LOD_LEVEL, runtimeData.prototype.isLODCrossFade ? lod : -1);
                    if (runtimeData.prototype.isLODCrossFade)
                    {
                        if (runtimeData.prototype.isLODCrossFadeAnimate)
                            renderer.mpb.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_FADE_LEVEL_MULTIPLIER, 0.01f);
                        else
                            renderer.mpb.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_FADE_LEVEL_MULTIPLIER, 1);
                        renderer.mpb.SetTexture(GPUInstancerConstants.BufferToTextureKernelPoperties.LOD_DATA_TEXTURE, runtimeData.instanceLODDataTexture);
                    }

                    if (runtimeData.hasShadowCasterBuffer && renderer.shadowMPB != null)
                    {
                        renderer.shadowMPB.SetTexture(GPUInstancerConstants.BufferToTextureKernelPoperties.TRANSFORMATION_MATRIX_TEXTURE, gpuiLod.shadowAppendTexture);
                        renderer.shadowMPB.SetMatrix(GPUInstancerConstants.VisibilityKernelPoperties.RENDERER_TRANSFORM_OFFSET, renderer.transformOffset);
                        renderer.shadowMPB.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, runtimeData.bufferSize);
                        renderer.shadowMPB.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.MAX_TEXTURE_SIZE, GPUInstancerConstants.TEXTURE_MAX_SIZE);
                        renderer.shadowMPB.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_LOD_LEVEL, -1);
                    }

                    SetRenderingLayerMask(runtimeData, renderer);
                }
                lod++;
            }
        }

        private static void SetRenderingLayerMask<T>(T runtimeData, GPUInstancerRenderer renderer) where T : GPUInstancerRuntimeData
        {
            // Set Rendering Layer Mask
            if (renderer.rendererRef != null)
            {
                Vector4 renderingLayer = new Vector4(BitConverter.ToSingle(BitConverter.GetBytes(renderer.rendererRef.renderingLayerMask), 0), 0, 0, 0);
                renderer.mpb.SetVector(GPUInstancerConstants.VisibilityKernelPoperties.UNITY_RENDERING_LAYER, renderingLayer);
                if (runtimeData.hasShadowCasterBuffer && renderer.shadowMPB != null)
                    renderer.shadowMPB.SetVector(GPUInstancerConstants.VisibilityKernelPoperties.UNITY_RENDERING_LAYER, renderingLayer);
            }
        }
        #endregion Set Append Buffers Platform Dependent

        /// <summary>
        /// Indirectly renders matrices for all prototypes. 
        /// Transform matrices are sent to a compute shader which does culling operations and appends them to the GPU (Unlimited buffer size).
        /// All GPU buffers must be already initialized.
        /// </summary>
        public static void UpdateGPUBuffers<T>(ComputeShader cameraComputeShader, int[] cameraComputeKernelIDs,
            ComputeShader visibilityComputeShader, int[] instanceVisibilityComputeKernelIDs, List<T> runtimeDataList,
            GPUInstancerCameraData cameraData, bool isManagerFrustumCulling, bool isManagerOcclusionCulling, bool showRenderedAmount, bool isInitial)
            where T : GPUInstancerRuntimeData
        {
            if (runtimeDataList == null)
                return;

            for (int i = 0; i < runtimeDataList.Count; i++)
            {
                UpdateGPUBuffer(cameraComputeShader, cameraComputeKernelIDs, visibilityComputeShader, instanceVisibilityComputeKernelIDs,
                    runtimeDataList[i], cameraData, isManagerFrustumCulling, isManagerOcclusionCulling, showRenderedAmount, isInitial);
            }
        }

        /// <summary>
        /// Indirectly renders matrices for all prototypes. 
        /// Transform matrices are sent to a compute shader which does culling operations and appends them to the GPU (Unlimited buffer size).
        /// All GPU buffers must be already initialized.
        /// </summary>
        public static void UpdateGPUBuffer<T>(ComputeShader cameraComputeShader, int[] cameraComputeKernelIDs,
            ComputeShader visibilityComputeShader, int[] instanceVisibilityComputeKernelIDs, T runtimeData,
            GPUInstancerCameraData cameraData, bool isManagerFrustumCulling, bool isManagerOcclusionCulling, bool showRenderedAmount, bool isInitial)
            where T : GPUInstancerRuntimeData
        {
            if (runtimeData == null)
                return;

            if (runtimeData.transformationMatrixVisibilityBuffer == null || runtimeData.bufferSize == 0 || runtimeData.instanceCount == 0)
            {
                if (showRenderedAmount && runtimeData.args != null)
                {
                    if (runtimeData.instanceLODs == null)
                        return;
                    for (int lod = 0; lod < runtimeData.instanceLODs.Count; lod++)
                    {
                        GPUInstancerPrototypeLOD lodData = runtimeData.instanceLODs[lod];
                        if (lodData == null || lodData.argsBufferOffset < 0 ||
                            lodData.argsBufferOffset + 1 >= runtimeData.args.Length)
                            continue;
                        runtimeData.args[lodData.argsBufferOffset + 1] = 0;
                        if (runtimeData.hasShadowCasterBuffer && runtimeData.shadowArgs != null &&
                            lodData.argsBufferOffset + 1 < runtimeData.shadowArgs.Length)
                            runtimeData.shadowArgs[lodData.argsBufferOffset + 1] = 0;
                    }
                }
                return;
            }

            DispatchCSInstancedCameraCalculation(cameraComputeShader, cameraComputeKernelIDs, runtimeData,
                cameraData, isManagerFrustumCulling, isManagerOcclusionCulling, isInitial);

            int lodCount = runtimeData.instanceLODs.Count;
            int instanceVisibilityComputeKernelId = instanceVisibilityComputeKernelIDs[
                lodCount > GPUInstancerConstants.COMPUTE_MAX_LOD_BUFFER ?
                    GPUInstancerConstants.COMPUTE_MAX_LOD_BUFFER - 1
                    : lodCount - 1];

            DispatchCSInstancedVisibilityCalculation(visibilityComputeShader, instanceVisibilityComputeKernelId, runtimeData, false, 0, 0);
            if (runtimeData.hasShadowCasterBuffer)
            {
                DispatchCSInstancedVisibilityCalculation(visibilityComputeShader, instanceVisibilityComputeKernelId, runtimeData, true, 0, 1);
            }
            if (!isInitial && runtimeData.prototype.isLODCrossFade)
            {
                DispatchCSInstancedVisibilityCalculation(visibilityComputeShader, instanceVisibilityComputeKernelId, runtimeData, false, 0, 2);
            }

            int lodShift = GPUInstancerConstants.COMPUTE_MAX_LOD_BUFFER;
            while (lodCount - lodShift > 0)
            {
                instanceVisibilityComputeKernelId = instanceVisibilityComputeKernelIDs[Math.Min(lodCount - lodShift, GPUInstancerConstants.COMPUTE_MAX_LOD_BUFFER) - 1];

                DispatchCSInstancedVisibilityCalculation(visibilityComputeShader, instanceVisibilityComputeKernelId, runtimeData, false,
                    lodShift, 0);
                if (runtimeData.hasShadowCasterBuffer)
                {
                    DispatchCSInstancedVisibilityCalculation(visibilityComputeShader, instanceVisibilityComputeKernelId, runtimeData, true,
                        lodShift, 1);
                }
                if (!isInitial && runtimeData.prototype.isLODCrossFade)
                {
                    DispatchCSInstancedVisibilityCalculation(visibilityComputeShader, instanceVisibilityComputeKernelId, runtimeData, false,
                        lodShift, 2);
                }
                lodShift += GPUInstancerConstants.COMPUTE_MAX_LOD_BUFFER;
            }

            GPUInstancerPrototypeLOD rdLOD;
            GPUInstancerRenderer rdRenderer;

            // Copy (overwrite) the modified instance count of the append buffer to each index of the indirect renderer buffer (argsBuffer)
            // that represents a submesh's instance count. The offset is calculated in parallel to the Graphics.DrawMeshInstancedIndirect call,
            // which expects args[1] to be the instance count for the first LOD's first renderer. Every 5 index offset of args represents the 
            // next submesh in the renderer, followed by the next renderer and it's submeshes. After all submeshes of all renderers for the 
            // first LOD, the other LODs follow in the same manner.
            // For reference, see: https://docs.unity3d.com/ScriptReference/ComputeBuffer.CopyCount.html

            for (int lod = 0; lod < lodCount; lod++)
            {
                rdLOD = runtimeData.instanceLODs[lod];
                for (int r = 0; r < rdLOD.renderers.Count; r++)
                {
                    rdRenderer = rdLOD.renderers[r];
                    for (int j = 0; j < rdRenderer.mesh.subMeshCount; j++)
                    {
                        // LOD renderer start location + LOD renderer material start location + 1 :
                        int offset = (rdRenderer.argsBufferOffset * GPUInstancerConstants.STRIDE_SIZE_INT) + (j * GPUInstancerConstants.STRIDE_SIZE_INT * 5) + GPUInstancerConstants.STRIDE_SIZE_INT;
                        ComputeBuffer.CopyCount(rdLOD.transformationMatrixAppendBuffer, runtimeData.argsBuffer, offset);

                        if (runtimeData.hasShadowCasterBuffer)
                        {
                            ComputeBuffer.CopyCount(rdLOD.shadowAppendBuffer, runtimeData.shadowArgsBuffer, offset);
                        }
                    }
                }
            }

            // WARNING: this will read back the instance matrices buffer after the compute shader operates on it. This will impact FPS greatly. Use only for debug.
            if (showRenderedAmount)
            {
                if (runtimeData.argsBuffer != null && runtimeData.args != null && runtimeData.args.Length > 0)
                {
                    runtimeData.argsBuffer.GetData(runtimeData.args);
                    if (runtimeData.hasShadowCasterBuffer)
                        runtimeData.shadowArgsBuffer.GetData(runtimeData.shadowArgs);
                }
            }
        }

        public static void DispatchCSInstancedCameraCalculation<T>(ComputeShader cameraComputeShader, int[] cameraComputeKernelIDs, T runtimeData,
            GPUInstancerCameraData cameraData, bool isManagerFrustumCulling, bool isManagerOcclusionCulling, bool isInitial)
            where T : GPUInstancerRuntimeData
        {
            int lodCount = runtimeData.instanceLODs.Count;

            int instanceVisibilityComputeKernelId = cameraComputeKernelIDs[!isInitial && runtimeData.prototype.isLODCrossFade ? 1 : 0];

            cameraComputeShader.SetBuffer(instanceVisibilityComputeKernelId, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_LOD_BUFFER, runtimeData.instanceLODDataBuffer);
            cameraComputeShader.SetBuffer(instanceVisibilityComputeKernelId, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, runtimeData.transformationMatrixVisibilityBuffer);

            cameraComputeShader.SetMatrix(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_MVP_MATRIX,
                cameraData.mvpMatrix);
            cameraComputeShader.SetVector(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BOUNDS_CENTER,
                runtimeData.instanceBounds.center);
            cameraComputeShader.SetVector(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BOUNDS_EXTENTS,
                runtimeData.instanceBounds.extents);
            cameraComputeShader.SetBool(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_FRUSTUM_CULL_SWITCH,
                isManagerFrustumCulling && runtimeData.prototype.isFrustumCulling);
            cameraComputeShader.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_MIN_VIEW_DISTANCE,
                runtimeData.prototype.minDistance);
            cameraComputeShader.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_MAX_VIEW_DISTANCE,
                runtimeData.prototype.maxDistance);
            cameraComputeShader.SetVector(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_CAMERA_POSITION,
                cameraData.cameraPosition);
            cameraComputeShader.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_FRUSTUM_OFFSET,
                runtimeData.prototype.frustumOffset);
            cameraComputeShader.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_OCCLUSION_OFFSET,
                runtimeData.prototype.occlusionOffset);
            cameraComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_OCCLUSION_ACCURACY,
                runtimeData.prototype.occlusionAccuracy);
            cameraComputeShader.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_MIN_CULLING_DISTANCE,
                runtimeData.prototype.minCullingDistance);
            cameraComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE,
                runtimeData.instanceCount);

            float shadowDistance = -1;
            if (runtimeData.hasShadowCasterBuffer)
            {
                if (runtimeData.prototype.useCustomShadowDistance)
                    shadowDistance = runtimeData.prototype.shadowDistance;
                else
                {
                    shadowDistance = QualitySettings.shadowDistance;
#if GPUI_FRAXINUS
                    if (GraphicsSettings.currentRenderPipeline is FraxinusEngine.Rendering.Ultra.UltraRenderPipelineAsset pipelineAsset)
                        shadowDistance = pipelineAsset.shadowDistance;
#endif
                }
                cameraComputeShader.SetFloats(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_SHADOW_LOD_MAP,
                    runtimeData.prototype.GetShadowLODMapOrDefault());
                cameraComputeShader.SetBool(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_CULL_SHADOW,
                    runtimeData.prototype.cullShadows);
            }
            cameraComputeShader.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_SHADOW_DISTANCE,
                shadowDistance);

            cameraComputeShader.SetFloats(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_LOD_SIZES,
                runtimeData.lodSizes);
            cameraComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_LOD_COUNT,
                lodCount);

            cameraComputeShader.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_HALF_ANGLE, cameraData.halfAngle);

            if (!isInitial && runtimeData.prototype.isLODCrossFade)
            {
                cameraComputeShader.SetBool(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_ANIMATE_CROSS_FADE, runtimeData.prototype.isLODCrossFadeAnimate);
                if (runtimeData.prototype.isLODCrossFadeAnimate)
                {
                    cameraComputeShader.SetFloat(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_DELTA_TIME,
                        GPUInstancerManager.timeSinceLastDrawCall);
                }
            }

            if (isManagerOcclusionCulling && cameraData.hasOcclusionDepth)
            {
                cameraComputeShader.SetBool(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_OCCLUSION_CULL_SWITCH,
                    runtimeData.prototype.isOcclusionCulling);
                if (GPUInstancerConstants.projectSettings.IsUseBothEyesVRCulling())
                {
                    cameraComputeShader.SetMatrix(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_MVP_MATRIX2,
                        cameraData.mvpMatrix2);
                }
                cameraComputeShader.SetTexture(instanceVisibilityComputeKernelId, GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_HIERARCHICAL_Z_TEXTURE_MAP,
                    cameraData.hiZDepthTexture);
                cameraComputeShader.SetVector(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_HIERARCHICAL_Z_TEXTURE_SIZE,
                    cameraData.hiZTextureSize);
            }
            else
            {
                cameraComputeShader.SetBool(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_OCCLUSION_CULL_SWITCH, false);
                // setting a dummy placeholder or the compute shader will throw errors.
                cameraComputeShader.SetTexture(instanceVisibilityComputeKernelId, GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_HIERARCHICAL_Z_TEXTURE_MAP,
                    dummyHiZTex);
            }

            // Dispatch the compute shader
            DispatchCompute(cameraComputeShader, instanceVisibilityComputeKernelId,
                Mathf.CeilToInt(runtimeData.instanceCount / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
        }

        public static void DispatchCSInstancedVisibilityCalculation<T>(ComputeShader visibilityComputeShader, int instanceVisibilityComputeKernelId, T runtimeData,
            bool isShadow, int lodShift, int lodAppendIndex) where T : GPUInstancerRuntimeData
        {
            GPUInstancerPrototypeLOD rdLOD;
            int lodCount = runtimeData.instanceLODs.Count;

            visibilityComputeShader.SetBuffer(instanceVisibilityComputeKernelId, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER,
                runtimeData.transformationMatrixVisibilityBuffer);
            visibilityComputeShader.SetBuffer(instanceVisibilityComputeKernelId, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_LOD_BUFFER,
                runtimeData.instanceLODDataBuffer);

            for (int lod = 0; lod < lodCount - lodShift && lod < GPUInstancerConstants.COMPUTE_MAX_LOD_BUFFER; lod++)
            {
                rdLOD = runtimeData.instanceLODs[lod + lodShift];
                if (isShadow)
                {
                    rdLOD.shadowAppendBuffer.SetCounterValue(0);
                    visibilityComputeShader.SetBuffer(instanceVisibilityComputeKernelId, GPUInstancerConstants.VisibilityKernelPoperties.TRANSFORMATION_MATRIX_APPEND_BUFFERS[lod],
                            rdLOD.shadowAppendBuffer);
                }
                else
                {
                    if (lodAppendIndex == 0)
                        rdLOD.transformationMatrixAppendBuffer.SetCounterValue(0);
                    visibilityComputeShader.SetBuffer(instanceVisibilityComputeKernelId, GPUInstancerConstants.VisibilityKernelPoperties.TRANSFORMATION_MATRIX_APPEND_BUFFERS[lod],
                            rdLOD.transformationMatrixAppendBuffer);
                }
            }

            visibilityComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, runtimeData.instanceCount);
            visibilityComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_LOD_SHIFT, lodShift);
            visibilityComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_LOD_APPEND_INDEX, lodAppendIndex);

            // Dispatch the compute shader
            DispatchCompute(visibilityComputeShader, instanceVisibilityComputeKernelId,
                Mathf.CeilToInt(runtimeData.instanceCount / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
        }

        public static void GPUIDrawMeshInstancedIndirect<T>(List<T> runtimeDataList, Bounds instancingBounds, GPUInstancerCameraData cameraData, int layerMask = ~0,
            bool lightProbeDisabled = false)
            where T : GPUInstancerRuntimeData
        {
            if (runtimeDataList == null)
                return;

            Camera rendereringCamera = cameraData.GetRenderingCamera();
            foreach (T runtimeData in runtimeDataList)
            {
                if (runtimeData == null || runtimeData.transformationMatrixVisibilityBuffer == null || runtimeData.bufferSize == 0 || runtimeData.instanceCount == 0)
                    continue;

                // Everything is ready; execute the instanced indirect rendering. We execute a drawcall for each submesh of each LOD.
                GPUInstancerPrototypeLOD rdLOD;
                GPUInstancerRenderer rdRenderer;
                Material rdMaterial;
                int offset = 0;
                int submeshIndex = 0;
                for (int lod = 0; lod < runtimeData.instanceLODs.Count; lod++)
                {
                    rdLOD = runtimeData.instanceLODs[lod];
                    bool isLODShadowCasting = runtimeData.IsLODShadowCasting(lod);
                    for (int r = 0; r < rdLOD.renderers.Count; r++)
                    {
                        rdRenderer = rdLOD.renderers[r];
                        if (!IsInLayer(layerMask, rdRenderer.layer))
                            continue;

                        for (int m = 0; m < rdRenderer.materials.Count; m++)
                        {
                            rdMaterial = rdRenderer.materials[m];

                            submeshIndex = Math.Min(m, rdRenderer.mesh.subMeshCount - 1);
                            offset = (rdRenderer.argsBufferOffset + 5 * submeshIndex) * GPUInstancerConstants.STRIDE_SIZE_INT;

                            if (rdRenderer.rendererRef == null || rdRenderer.rendererRef.shadowCastingMode != ShadowCastingMode.ShadowsOnly)
                            {
                                Graphics.DrawMeshInstancedIndirect(rdRenderer.mesh, submeshIndex,
                                    rdMaterial,
                                    instancingBounds,
                                    runtimeData.argsBuffer,
                                    offset,
                                    rdRenderer.mpb,
                                    runtimeData.prototype.isShadowCasting && !runtimeData.hasShadowCasterBuffer && isLODShadowCasting ? ShadowCastingMode.On : ShadowCastingMode.Off, rdRenderer.receiveShadows, rdRenderer.layer,
                                    rendereringCamera
#if UNITY_2018_1_OR_NEWER
                                , lightProbeDisabled ? LightProbeUsage.Off : LightProbeUsage.BlendProbes
#endif
                                );
                            }

                            if (runtimeData.hasShadowCasterBuffer && runtimeData.prototype.isShadowCasting && isLODShadowCasting && rdRenderer.castShadows)
                            {
                                Graphics.DrawMeshInstancedIndirect(rdRenderer.mesh, submeshIndex,
                                    rdMaterial,
                                    instancingBounds,
                                    runtimeData.shadowArgsBuffer,
                                    offset,
                                    rdRenderer.shadowMPB,
                                    ShadowCastingMode.ShadowsOnly, false, rdRenderer.layer, rendereringCamera
#if UNITY_2018_1_OR_NEWER
                                    , lightProbeDisabled ? LightProbeUsage.Off : LightProbeUsage.BlendProbes
#endif
                                    );
                            }
                        }
                    }
                }
            }
        }

        public static void DispatchBufferToTexture<T>(List<T> runtimeDataList, ComputeShader bufferToTextureComputeShader, int bufferToTextureComputeKernelID, int bufferToTextureCrossFadeKernelID = 1) where T : GPUInstancerRuntimeData
        {
            if (runtimeDataList == null)
                return;

            foreach (T runtimeData in runtimeDataList)
            {
                if (runtimeData == null || runtimeData.args == null || runtimeData.transformationMatrixVisibilityBuffer == null || runtimeData.bufferSize == 0)
                    continue;

                for (int lod = 0; lod < runtimeData.instanceLODs.Count; lod++)
                {
                    bufferToTextureComputeShader.SetBuffer(bufferToTextureComputeKernelID, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, runtimeData.transformationMatrixVisibilityBuffer);
                    bufferToTextureComputeShader.SetBuffer(bufferToTextureComputeKernelID, GPUInstancerConstants.VisibilityKernelPoperties.TRANSFORMATION_MATRIX_BUFFER, runtimeData.instanceLODs[lod].transformationMatrixAppendBuffer);
                    bufferToTextureComputeShader.SetTexture(bufferToTextureComputeKernelID, GPUInstancerConstants.BufferToTextureKernelPoperties.TRANSFORMATION_MATRIX_TEXTURE, runtimeData.instanceLODs[lod].transformationMatrixAppendTexture);
                    bufferToTextureComputeShader.SetBuffer(bufferToTextureComputeKernelID, GPUInstancerConstants.VisibilityKernelPoperties.ARGS_BUFFER, runtimeData.argsBuffer);
                    bufferToTextureComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.ARGS_BUFFER_INDEX, runtimeData.instanceLODs[lod].argsBufferOffset + 1);
                    bufferToTextureComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.MAX_TEXTURE_SIZE, GPUInstancerConstants.TEXTURE_MAX_SIZE);

                    DispatchCompute(bufferToTextureComputeShader, bufferToTextureComputeKernelID,
                        Mathf.CeilToInt(runtimeData.bufferSize / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);

                    if (runtimeData.hasShadowCasterBuffer)
                    {
                        bufferToTextureComputeShader.SetBuffer(bufferToTextureComputeKernelID, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, runtimeData.transformationMatrixVisibilityBuffer);
                        bufferToTextureComputeShader.SetBuffer(bufferToTextureComputeKernelID, GPUInstancerConstants.VisibilityKernelPoperties.TRANSFORMATION_MATRIX_BUFFER, runtimeData.instanceLODs[lod].shadowAppendBuffer);
                        bufferToTextureComputeShader.SetTexture(bufferToTextureComputeKernelID, GPUInstancerConstants.BufferToTextureKernelPoperties.TRANSFORMATION_MATRIX_TEXTURE, runtimeData.instanceLODs[lod].shadowAppendTexture);
                        bufferToTextureComputeShader.SetBuffer(bufferToTextureComputeKernelID, GPUInstancerConstants.VisibilityKernelPoperties.ARGS_BUFFER, runtimeData.shadowArgsBuffer);
                        bufferToTextureComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.ARGS_BUFFER_INDEX, runtimeData.instanceLODs[lod].argsBufferOffset + 1);
                        bufferToTextureComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.MAX_TEXTURE_SIZE, GPUInstancerConstants.TEXTURE_MAX_SIZE);

                        DispatchCompute(bufferToTextureComputeShader, bufferToTextureComputeKernelID,
                            Mathf.CeilToInt(runtimeData.bufferSize / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
                    }

                    if (runtimeData.prototype.isLODCrossFade)
                    {
                        bufferToTextureComputeShader.SetTexture(bufferToTextureCrossFadeKernelID, GPUInstancerConstants.BufferToTextureKernelPoperties.LOD_DATA_TEXTURE, runtimeData.instanceLODDataTexture);
                        bufferToTextureComputeShader.SetBuffer(bufferToTextureCrossFadeKernelID, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_LOD_BUFFER, runtimeData.instanceLODDataBuffer);
                        bufferToTextureComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.MAX_TEXTURE_SIZE, GPUInstancerConstants.TEXTURE_MAX_SIZE);
                        bufferToTextureComputeShader.SetInt(GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, runtimeData.bufferSize);

                        DispatchCompute(bufferToTextureComputeShader, bufferToTextureCrossFadeKernelID,
                            Mathf.CeilToInt(runtimeData.bufferSize / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
                    }
                }
            }
        }


        public static bool IsInLayer(int layerMask, int layer)
        {
            return layerMask == (layerMask | (1 << layer));
        }
        #endregion GPU Instancing

        #region Prototype Release

        public static void ReleaseInstanceBuffers<T>(List<T> runtimeDataList) where T : GPUInstancerRuntimeData
        {
            if (runtimeDataList == null)
                return;

            for (int i = 0; i < runtimeDataList.Count; i++)
            {
                ReleaseInstanceBuffers(runtimeDataList[i]);
            }
        }

        public static void ReleaseInstanceBuffers<T>(T runtimeData) where T : GPUInstancerRuntimeData
        {
            if (runtimeData == null)
                return;

            if (runtimeData.instanceLODs != null)
            {
                for (int lod = 0; lod < runtimeData.instanceLODs.Count; lod++)
                {
                    GPUInstancerPrototypeLOD lodData = runtimeData.instanceLODs[lod];
                    if (lodData == null)
                        continue;
                    if (lodData.transformationMatrixAppendBuffer != null)
                        lodData.transformationMatrixAppendBuffer.Release();
                    lodData.transformationMatrixAppendBuffer = null;

                    DestroyObject(lodData.transformationMatrixAppendTexture);
                    lodData.transformationMatrixAppendTexture = null;

                    DestroyObject(lodData.shadowAppendTexture);
                    lodData.shadowAppendTexture = null;

                    if (lodData.shadowAppendBuffer != null)
                        lodData.shadowAppendBuffer.Release();
                    lodData.shadowAppendBuffer = null;
                }
            }

            if (runtimeData.instanceLODDataBuffer != null)
                runtimeData.instanceLODDataBuffer.Release();
            runtimeData.instanceLODDataBuffer = null;

            if (runtimeData.transformationMatrixVisibilityBuffer != null)
                runtimeData.transformationMatrixVisibilityBuffer.Release();
            runtimeData.transformationMatrixVisibilityBuffer = null;

            if (runtimeData.argsBuffer != null)
                runtimeData.argsBuffer.Release();
            runtimeData.argsBuffer = null;

            if (runtimeData.shadowArgsBuffer != null)
                runtimeData.shadowArgsBuffer.Release();
            runtimeData.shadowArgsBuffer = null;

            runtimeData.ReleaseBuffers();
        }

        public static void ReleaseSPBuffers(GPUInstancerSpatialPartitioningData<GPUInstancerCell> spData)
        {
            if (spData == null || spData.activeCellList == null)
                return;

            for (int i = 0; i < spData.activeCellList.Count; i++)
            {
                ReleaseSPCell(spData.activeCellList[i]);
            }
        }

        public static void ReleaseSPCell(GPUInstancerCell spCell)
        {
            if (spCell != null && spCell is GPUInstancerDetailCell)
            {
                GPUInstancerDetailCell detailCell = (GPUInstancerDetailCell)spCell;
                if (detailCell.detailInstanceBuffers != null)
                {
                    foreach (ComputeBuffer cb in detailCell.detailInstanceBuffers.Values)
                    {
                        if (cb != null)
                        {
                            cb.Release();
                        }
                    }
                    detailCell.detailInstanceBuffers = null;
                }
            }
        }

        #endregion Prototype Release

        #region Create Prototypes

        #region Create Detail Prototypes

        /// <summary>
        /// Returns a list of GPU Instancer compatible prototypes given the DetailPrototypes from a Unity Terrain.
        /// </summary>
        /// <param name="detailPrototypes">Unity Terrain Detail prototypes</param>
        /// <returns></returns>
        public static void SetDetailInstancePrototypes(UnityEngine.Object context, List<GPUInstancerPrototype> detailInstancePrototypes, DetailPrototype[] detailPrototypes,
            int quadCount, GPUInstancerTerrainSettings terrainSettings, bool forceNew)
        {
            if (forceNew)
                RemoveAssetsOfType(terrainSettings, typeof(GPUInstancerDetailPrototype));

            for (int i = 0; i < detailPrototypes.Length; i++)
            {
                if (!forceNew && detailInstancePrototypes.Count > i)
                    continue;

                AddDetailInstancePrototypeFromTerrainPrototype(context, detailInstancePrototypes, detailPrototypes[i], i, quadCount, terrainSettings);
            }
            RemoveUnusedAssets(terrainSettings, detailInstancePrototypes, typeof(GPUInstancerDetailPrototype));
        }

        public static void AddDetailInstancePrototypeFromTerrainPrototype(UnityEngine.Object context, List<GPUInstancerPrototype> detailInstancePrototypes, DetailPrototype terrainDetailPrototype,
            int detailIndex, int quadCount, GPUInstancerTerrainSettings terrainSettings, GameObject replacementPrefab = null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && context != null)
                Undo.RecordObject(context, "Detail prototype changed " + detailIndex);
#endif

            if (replacementPrefab == null && terrainDetailPrototype.prototype != null)
            {
                replacementPrefab = terrainDetailPrototype.prototype;
                while (replacementPrefab.transform.parent != null)
                    replacementPrefab = replacementPrefab.transform.parent.gameObject;
            }

            GPUInstancerDetailPrototype detailPrototype = ScriptableObject.CreateInstance<GPUInstancerDetailPrototype>();
            detailPrototype.prototypeIndex = detailIndex;
            detailPrototype.detailRenderMode = terrainDetailPrototype.renderMode;
            detailPrototype.usePrototypeMesh = terrainDetailPrototype.usePrototypeMesh;
            detailPrototype.prefabObject = replacementPrefab;
            detailPrototype.prototypeTexture = terrainDetailPrototype.prototypeTexture;
            detailPrototype.useCrossQuads = quadCount > 1;
            detailPrototype.quadCount = quadCount;

            detailPrototype.billboardFaceCamPos = GPUInstancerProjectSettings.IsVREnabled();

            detailPrototype.detailHealthyColor = terrainDetailPrototype.healthyColor;
            detailPrototype.detailDryColor = terrainDetailPrototype.dryColor;
            detailPrototype.noiseSpread = terrainDetailPrototype.noiseSpread;
            detailPrototype.detailScale = new Vector4(terrainDetailPrototype.minWidth, terrainDetailPrototype.maxWidth, terrainDetailPrototype.minHeight, terrainDetailPrototype.maxHeight);
            detailPrototype.windWaveTintColor = Color.Lerp(detailPrototype.detailHealthyColor, detailPrototype.detailDryColor, 0.5f);
            detailPrototype.name = "Detail_" + detailIndex + "_" + (terrainDetailPrototype.prototype != null ? terrainDetailPrototype.prototype.name + "_" + terrainDetailPrototype.prototype.GetInstanceID() : terrainDetailPrototype.prototypeTexture.name + "_" + terrainDetailPrototype.prototypeTexture.GetInstanceID());
            detailPrototype.maxDistance = terrainSettings.maxDetailDistance;
            detailPrototype.detailDensity = terrainSettings.detailDensity;
            detailPrototype.isShadowCasting = terrainDetailPrototype.usePrototypeMesh;
            detailPrototype.isBillboardDisabled = !terrainDetailPrototype.usePrototypeMesh;

            // A bit redundant here, but makes it possible to add GOs with SpeedTree, Tree Creator and Soft Occlusion shaders as terrain details.
            if (replacementPrefab != null)
                DetermineTreePrototypeType(detailPrototype);
            detailPrototype.useOriginalShaderForShadow = true;
            //if (detailPrototype.treeType == GPUInstancerTreeType.SpeedTree)
            //    detailPrototype.lodBiasAdjustment = 0.5f;

            if (IsBillboardGeneratedByDefault(detailPrototype))
            {
                detailPrototype.isLODCrossFade = false;
                detailPrototype.useGeneratedBillboard = true;
                if (detailPrototype.billboard == null)
                    detailPrototype.billboard = new GPUInstancerBillboard();
                GeneratePrototypeBillboard(detailPrototype, GPUInstancerConstants.projectSettings);
            }

            AddObjectToAsset(terrainSettings, detailPrototype);

            detailInstancePrototypes.Add(detailPrototype);

            if (terrainDetailPrototype.usePrototypeMesh)
                GenerateInstancedShadersForGameObject(detailPrototype);
            else
            {
                if (Shader.Find(GPUInstancerConstants.SHADER_GPUI_FOLIAGE_URP) == null)
                    ImportFoliageSRPShader();
                else
                    GPUInstancerConstants.projectSettings.AddShaderVariantToCollection(GPUInstancerConstants.SHADER_GPUI_FOLIAGE_URP);
            }

        }

        public static void ImportFoliageSRPShader()
        {
#if UNITY_EDITOR
            EditorApplication.update -= ImportFoliageSRPShaderPopup;
            EditorApplication.update += ImportFoliageSRPShaderPopup;
#endif
        }

        public static void ImportFoliageSRPShaderPopup()
        {
#if UNITY_EDITOR
            if (Shader.Find(GPUInstancerConstants.SHADER_GPUI_FOLIAGE_URP) == null)
            {
                Debug.LogError("Fraxinus GPU Batcher could not find the Fraxinus foliage shader '" +
                    GPUInstancerConstants.SHADER_GPUI_FOLIAGE_URP + "'. The package ships Fraxinus support directly; " +
                    "reinstall the package if the shader asset is missing.");

            }
            EditorApplication.update -= ImportFoliageSRPShaderPopup;
#endif
        }

        public static void AddDetailInstanceRuntimeDataToList(List<GPUInstancerRuntimeData> runtimeDataList, List<GPUInstancerPrototype> detailPrototypes,
            GPUInstancerTerrainSettings terrainSettings, int detailLayer, GPUInstancerManager ownerManager = null)
        {
            if (runtimeDataList == null || detailPrototypes == null ||
                (ownerManager != null && ownerManager.isDisposed))
                return;

            // Detail maps are addressed by Terrain prototype index.  If a
            // channel-wide prerequisite is missing, keep one explicit hole
            // per source prototype instead of returning an empty/short list;
            // otherwise a later valid entry can be rendered with the map data
            // belonging to a different detail layer.
            if (terrainSettings == null || GPUInstancerConstants.projectSettings == null ||
                GPUInstancerConstants.projectSettings.shaderBindings == null)
            {
                for (int i = 0; i < detailPrototypes.Count; i++)
                    runtimeDataList.Add(null);
                return;
            }

            for (int i = 0; i < detailPrototypes.Count; i++)
            {
                GPUInstancerDetailPrototype detailPrototype = detailPrototypes[i] as GPUInstancerDetailPrototype;
                if (detailPrototype == null)
                {
                    // Terrain detail maps and all downstream cell dictionaries
                    // are keyed by prototype-list index. Keep an explicit hole
                    // for malformed managed-reference entries so later valid
                    // prototypes do not slide onto the wrong detail layer.
                    runtimeDataList.Add(null);
                    continue;
                }

                GPUInstancerRuntimeData runtimeData = new GPUInstancerRuntimeData(detailPrototype, ownerManager);

                if (detailPrototype.usePrototypeMesh)
                {
                    if (!runtimeData.CreateRenderersFromGameObject(detailPrototypes[i]) ||
                        runtimeData.instanceLODs == null || runtimeData.instanceLODs.Count == 0 ||
                        !runtimeData.instanceLODs.Any(lod => lod != null && lod.renderers != null && lod.renderers.Count > 0))
                    {
                        runtimeDataList.Add(null);
                        continue;
                    }

                    AddBillboardToRuntimeData(runtimeData);

                    if (detailPrototype.treeType == GPUInstancerTreeType.SpeedTree || detailPrototype.treeType == GPUInstancerTreeType.SpeedTree8 ||
                        detailPrototype.treeType == GPUInstancerTreeType.TreeCreatorTree)
                        GPUInstancerManager.AddTreeProxy(detailPrototype, runtimeData);

                    if (detailPrototype.prefabObject != null && detailPrototype.prefabObject.GetComponentsInChildren<MeshRenderer>()
                        .Any(r => r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null &&
                            r.sharedMaterial.shader.name == GPUInstancerConstants.SHADER_GPUI_FOLIAGE_URP))
                    {
                        if (runtimeData.instanceLODs == null)
                            continue;
                        for (int lod = 0; lod < runtimeData.instanceLODs.Count; lod++)
                        {
                            GPUInstancerPrototypeLOD lodData = runtimeData.instanceLODs[lod];
                            if (lodData == null || lodData.renderers == null)
                                continue;
                            for (int r = 0; r < lodData.renderers.Count; r++)
                            {
                                GPUInstancerRenderer renderer = lodData.renderers[r];
                                if (renderer == null || renderer.mpb == null)
                                    continue;
                                renderer.mpb.SetTexture("_HealthyDryNoiseTexture", terrainSettings.GetHealthyDryNoiseTexture(detailPrototype));
                                renderer.mpb.SetFloat("_NoiseSpread", detailPrototype.noiseSpread);
                                renderer.mpb.SetTexture("_WindWaveNormalTexture", terrainSettings.windWaveNormalTexture);
                                renderer.mpb.SetVector("_WindVector", terrainSettings.windVector);
                            }
                        }
                    }
                }
                else
                {
                    Material instanceMaterial;

                    if (detailPrototype.useCustomMaterialForTextureDetail && detailPrototype.textureDetailCustomMaterial != null)
                    {
                        instanceMaterial = GPUInstancerConstants.projectSettings.shaderBindings.GetInstancedMaterial(detailPrototype.textureDetailCustomMaterial);
                        if (instanceMaterial == null)
                        {
                            runtimeDataList.Add(null);
                            continue;
                        }
                        string customDetailName = detailPrototype.prototypeTexture == null ? "Detail" : detailPrototype.prototypeTexture.name;
                        instanceMaterial.name = "InstancedMaterial_" + customDetailName + "_GPUITemp";

                        // Note: Cross quad distance billboarding is disabled for custom materials since GPU Instancer handles billboarding in the GPUInstancer/Foliage shader.
                        runtimeData.AddLodAndRenderer(CreateCrossQuadsMeshForDetailGrass(1, 1, customDetailName,
                                detailPrototype.quadCount), new List<Material> { instanceMaterial }, new MaterialPropertyBlock(), true, 0f,
                                null, false, detailLayer);

                        runtimeDataList.Add(runtimeData);

                        continue;
                    }

                    string shaderName = GPUInstancerConstants.SHADER_GPUI_FOLIAGE_URP;

                    Shader instanceShader = Shader.Find(shaderName);
                    if (instanceShader == null)
                    {
                        Debug.LogError("Required foliage shader " + shaderName + " is not found. Please extract the shader from its respective package or use a custom material");
                        runtimeDataList.Add(null);
                        continue;
                    }

                    instanceMaterial = new Material(instanceShader);

                    instanceMaterial.SetTexture("_HealthyDryNoiseTexture", terrainSettings.GetHealthyDryNoiseTexture(detailPrototype));
                    instanceMaterial.SetTexture("_WindWaveNormalTexture", terrainSettings.windWaveNormalTexture);
                    instanceMaterial.SetVector("_WindVector", terrainSettings.windVector);

                    instanceMaterial.SetFloat("_IsBillboard", detailPrototype.useCrossQuads ? 0.0f : detailPrototype.isBillboard ? 1.0f : 0.0f);

                    if (detailPrototype.billboardFaceCamPos)
                        instanceMaterial.EnableKeyword("_BILLBOARDFACECAMPOS_ON");
                    else
                        instanceMaterial.DisableKeyword("_BILLBOARDFACECAMPOS_ON");

                    instanceMaterial.SetTexture("_MainTex", detailPrototype.prototypeTexture);
                    instanceMaterial.SetColor("_HealthyColor", detailPrototype.detailHealthyColor);
                    instanceMaterial.SetColor("_DryColor", detailPrototype.detailDryColor);
                    instanceMaterial.SetFloat("_NoiseSpread", detailPrototype.noiseSpread);
                    instanceMaterial.SetFloat("_AmbientOcclusion", detailPrototype.ambientOcclusion);
                    instanceMaterial.SetFloat("_GradientPower", detailPrototype.gradientPower);

                    instanceMaterial.SetColor("_WindWaveTintColor", detailPrototype.windWaveTintColor);
                    instanceMaterial.SetFloat("_WindIdleSway", detailPrototype.windIdleSway);
                    instanceMaterial.SetFloat("_WindWavesOn", detailPrototype.windWavesOn ? 1.0f : 0.0f);
                    instanceMaterial.SetFloat("_WindWaveSize", detailPrototype.windWaveSize);
                    instanceMaterial.SetFloat("_WindWaveTint", detailPrototype.windWaveTint);
                    instanceMaterial.SetFloat("_WindWaveSway", detailPrototype.windWaveSway);


                    string detailName = detailPrototype.prototypeTexture == null ? "Detail" : detailPrototype.prototypeTexture.name;
                    instanceMaterial.name = "InstancedMaterial_" + detailName + "_GPUITemp";

                    runtimeData.AddLodAndRenderer(CreateCrossQuadsMeshForDetailGrass(1, 1, detailName,
                                                                          detailPrototype.useCrossQuads ? detailPrototype.quadCount : 1), new List<Material> { instanceMaterial }, new MaterialPropertyBlock(), true,
                                                                          detailPrototype.useCrossQuads ? GetDistanceRelativeHeight(detailPrototype) : 0f,
                                                                          null, false, detailLayer);

                    // Add grass LOD if cross quadding.
                    if (detailPrototype.useCrossQuads)
                    {
                        Material lodMaterial = new Material(instanceMaterial);
                        lodMaterial.SetFloat("_IsBillboard", 1.0f);

                        if (detailPrototype.billboardFaceCamPos)
                            lodMaterial.EnableKeyword("_BILLBOARDFACECAMPOS_ON");
                        else
                            lodMaterial.DisableKeyword("_BILLBOARDFACECAMPOS_ON");

                        // LOD Debug:
                        if (detailPrototype.billboardDistanceDebug)
                        {
                            lodMaterial.SetColor("_HealthyColor", detailPrototype.billboardDistanceDebugColor);
                            lodMaterial.SetColor("_DryColor", detailPrototype.billboardDistanceDebugColor);
                        }

                        runtimeData.AddLodAndRenderer(CreateCrossQuadsMeshForDetailGrass(1, 1, detailName,
                            1), new List<Material> { lodMaterial }, new MaterialPropertyBlock(), true, 0f,
                            null, false, detailLayer);
                    }
                }

                runtimeDataList.Add(runtimeData);
            }
        }

        public static void UpdateDetailInstanceRuntimeDataList(List<GPUInstancerRuntimeData> runtimeDataList, GPUInstancerTerrainSettings terrainSettings, bool updateMeshes = false,
            int detailLayer = 0)
        {
            if (runtimeDataList == null || terrainSettings == null ||
                GPUInstancerConstants.projectSettings == null || GPUInstancerConstants.projectSettings.shaderBindings == null)
                return;
            for (int i = 0; i < runtimeDataList.Count; i++)
            {
                GPUInstancerRuntimeData runtimeData = runtimeDataList[i];
                if (runtimeData == null)
                    continue;
                GPUInstancerDetailPrototype detailPrototype = runtimeData.prototype as GPUInstancerDetailPrototype;
                if (detailPrototype == null || runtimeData.instanceLODs == null || runtimeData.instanceLODs.Count == 0)
                    continue;

                if (detailPrototype.usePrototypeMesh)
                {
                    if (detailPrototype.prefabObject != null && detailPrototype.prefabObject.GetComponentsInChildren<MeshRenderer>()
                        .Any(r => r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null &&
                            r.sharedMaterial.shader.name == GPUInstancerConstants.SHADER_GPUI_FOLIAGE_URP))
                    {
                        for (int lod = 0; lod < runtimeData.instanceLODs.Count; lod++)
                        {
                            GPUInstancerPrototypeLOD lodData = runtimeData.instanceLODs[lod];
                            if (lodData == null || lodData.renderers == null)
                                continue;
                            for (int r = 0; r < lodData.renderers.Count; r++)
                            {
                                GPUInstancerRenderer renderer = lodData.renderers[r];
                                if (renderer == null || renderer.mpb == null)
                                    continue;
                                renderer.mpb.SetTexture("_HealthyDryNoiseTexture", terrainSettings.GetHealthyDryNoiseTexture(detailPrototype));
                                renderer.mpb.SetFloat("_NoiseSpread", detailPrototype.noiseSpread);
                                renderer.mpb.SetTexture("_WindWaveNormalTexture", terrainSettings.windWaveNormalTexture);
                                renderer.mpb.SetVector("_WindVector", terrainSettings.windVector);
                            }
                        }
                    }
                }
                else
                {
                    if (!detailPrototype.useCustomMaterialForTextureDetail || (detailPrototype.useCustomMaterialForTextureDetail && detailPrototype.textureDetailCustomMaterial != null))
                    {
                        if (updateMeshes)
                        {
                            if (detailPrototype.useCrossQuads)
                            {
                                GPUInstancerPrototypeLOD lodBilboard = runtimeDataList[i].instanceLODs[runtimeDataList[i].instanceLODs.Count - 1];
                                if (lodBilboard == null || lodBilboard.renderers == null || lodBilboard.renderers.Count == 0 ||
                                    lodBilboard.renderers[0] == null || lodBilboard.renderers[0].materials == null ||
                                    lodBilboard.renderers[0].materials.Count == 0 || runtimeDataList[i].lodSizes == null ||
                                    runtimeDataList[i].lodSizes.Length < 2 || runtimeDataList[i].instanceLODs[0] == null)
                                    continue;
                                if (runtimeDataList[i].instanceLODs[0].transformationMatrixAppendBuffer != null)
                                    runtimeDataList[i].instanceLODs[0].transformationMatrixAppendBuffer.Release();
                                runtimeDataList[i].instanceLODs[0].transformationMatrixAppendBuffer = null;

                                runtimeDataList[i].instanceLODs.Clear();

                                string detailName = detailPrototype.prototypeTexture == null ? "Detail" : detailPrototype.prototypeTexture.name;
                                runtimeDataList[i].AddLodAndRenderer(CreateCrossQuadsMeshForDetailGrass(1, 1, detailName,
                                                                                      detailPrototype.quadCount), new List<Material> { lodBilboard.renderers[0].materials[0] }, new MaterialPropertyBlock(), true,
                                                                                      1, // not calling GetDistanceRelativeHeight since will be called below.
                                                                                      null, false, detailLayer);

                                runtimeDataList[i].instanceLODs.Add(lodBilboard);
                                runtimeDataList[i].lodSizes[1] = 0f;

                                if (runtimeDataList[i].argsBuffer != null)
                                    runtimeDataList[i].argsBuffer.Release();
                                runtimeDataList[i].argsBuffer = null;

                                InitializeGPUBuffer(runtimeDataList[i]);
                            }
                            else if (runtimeDataList[i].instanceLODs.Count == 2 && runtimeDataList[i].instanceLODs[0] != null &&
                                runtimeDataList[i].lodSizes != null && runtimeDataList[i].lodSizes.Length >= 2)
                            {
                                if (runtimeDataList[i].instanceLODs[0].transformationMatrixAppendBuffer != null)
                                    runtimeDataList[i].instanceLODs[0].transformationMatrixAppendBuffer.Release();
                                runtimeDataList[i].instanceLODs[0].transformationMatrixAppendBuffer = null;

                                runtimeDataList[i].instanceLODs.RemoveAt(0);

                                if (runtimeDataList[i].argsBuffer != null)
                                    runtimeDataList[i].argsBuffer.Release();
                                runtimeDataList[i].argsBuffer = null;

                                runtimeDataList[i].lodSizes[0] = 0;
                                runtimeDataList[i].lodSizes[1] = -1;

                                InitializeGPUBuffer(runtimeDataList[i]);
                            }
                        }

                        for (int lod = 0; lod < runtimeDataList[i].instanceLODs.Count; lod++)
                        {
                            GPUInstancerPrototypeLOD lodData = runtimeDataList[i].instanceLODs[lod];
                            if (lodData == null || lodData.renderers == null || lodData.renderers.Count == 0 ||
                                lodData.renderers[0] == null || lodData.renderers[0].mpb == null)
                                continue;
                            MaterialPropertyBlock instanceMaterial = lodData.renderers[0].mpb;

                            instanceMaterial.SetTexture("_HealthyDryNoiseTexture", terrainSettings.GetHealthyDryNoiseTexture(detailPrototype));
                            instanceMaterial.SetTexture("_WindWaveNormalTexture", terrainSettings.windWaveNormalTexture);
                            instanceMaterial.SetVector("_WindVector", terrainSettings.windVector);

                            instanceMaterial.SetColor("_HealthyColor", detailPrototype.detailHealthyColor);
                            instanceMaterial.SetColor("_DryColor", detailPrototype.detailDryColor);
                            instanceMaterial.SetFloat("_NoiseSpread", detailPrototype.noiseSpread);
                            instanceMaterial.SetFloat("_AmbientOcclusion", detailPrototype.ambientOcclusion);
                            instanceMaterial.SetFloat("_GradientPower", detailPrototype.gradientPower);

                            instanceMaterial.SetColor("_WindWaveTintColor", detailPrototype.windWaveTintColor);
                            instanceMaterial.SetFloat("_WindIdleSway", detailPrototype.windIdleSway);
                            instanceMaterial.SetFloat("_WindWavesOn", detailPrototype.windWavesOn ? 1.0f : 0.0f);
                            instanceMaterial.SetFloat("_WindWaveSize", detailPrototype.windWaveSize);
                            instanceMaterial.SetFloat("_WindWaveTint", detailPrototype.windWaveTint);
                            instanceMaterial.SetFloat("_WindWaveSway", detailPrototype.windWaveSway);

                            instanceMaterial.SetFloat("_IsBillboard", detailPrototype.useCrossQuads && lod == 0 ? 0.0f : detailPrototype.isBillboard || detailPrototype.useCrossQuads ? 1.0f : 0.0f);
                        }
                    }

                    if (detailPrototype.useCrossQuads)
                    {
                        if (runtimeDataList[i].lodSizes == null || runtimeDataList[i].lodSizes.Length == 0)
                            continue;
                        runtimeDataList[i].lodSizes[0] = GetDistanceRelativeHeight(detailPrototype);

                        if (detailPrototype.billboardDistanceDebug && runtimeDataList[i].instanceLODs.Count > 1 &&
                            runtimeDataList[i].instanceLODs[1] != null && runtimeDataList[i].instanceLODs[1].renderers != null &&
                            runtimeDataList[i].instanceLODs[1].renderers.Count > 0 && runtimeDataList[i].instanceLODs[1].renderers[0] != null &&
                            runtimeDataList[i].instanceLODs[1].renderers[0].mpb != null)
                        {
                            MaterialPropertyBlock instanceMaterial = runtimeDataList[i].instanceLODs[1].renderers[0].mpb;
                            instanceMaterial.SetColor("_HealthyColor", detailPrototype.billboardDistanceDebugColor);
                            instanceMaterial.SetColor("_DryColor", detailPrototype.billboardDistanceDebugColor);
                        }
                    }
                }


            }
        }

        public static float GetDistanceRelativeHeight(GPUInstancerDetailPrototype detailPrototype)
        {
            return (1 - detailPrototype.billboardDistance);
        }

        #endregion  Create Detail Prototypes

        #region Create Prefab Prototypes

        public static void SetPrefabInstancePrototypes(UnityEngine.Object context, List<GPUInstancerPrototype> prototypeList, List<GameObject> prefabList, bool forceNew)
        {
            if (prefabList == null)
                return;

#if UNITY_EDITOR
            if (!Application.isPlaying && context != null)
                Undo.RecordObject(context, "Prefab prototypes changed");

            bool changed = false;
            if (forceNew)
            {
                foreach (GPUInstancerPrefabPrototype prototype in prototypeList)
                {
                    DeleteProjectAsset(prototype);
                    changed = true;
                }
            }
            else
            {
                foreach (GPUInstancerPrefabPrototype prototype in prototypeList)
                {
                    if (!prefabList.Contains(prototype.prefabObject))
                    {
                        DeleteProjectAsset(prototype);
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
#endif

            foreach (GameObject go in prefabList)
            {
                if (!forceNew && prototypeList.Exists(p => p.prefabObject == go))
                    continue;

                prototypeList.Add(GeneratePrefabPrototype(go, forceNew));
            }

#if UNITY_EDITOR
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (!Application.isPlaying)
            {
                GPUInstancerPrefab[] prefabInstances = GameObject.FindObjectsOfType<GPUInstancerPrefab>();
                for (int i = 0; i < prefabInstances.Length; i++)
                {
#if UNITY_2018_2_OR_NEWER
                    UnityEngine.Object prefabRoot = PrefabUtility.GetCorrespondingObjectFromSource(prefabInstances[i].gameObject);
#else
                    UnityEngine.Object prefabRoot = PrefabUtility.GetPrefabParent(prefabInstances[i].gameObject);
#endif
                    if (prefabRoot != null && ((GameObject)prefabRoot).GetComponent<GPUInstancerPrefab>() != null && prefabInstances[i].prefabPrototype != ((GameObject)prefabRoot).GetComponent<GPUInstancerPrefab>().prefabPrototype)
                    {
                        Undo.RecordObject(prefabInstances[i], "Changed GPUInstancer Prefab Prototype " + prefabInstances[i].gameObject + i);
                        prefabInstances[i].prefabPrototype = ((GameObject)prefabRoot).GetComponent<GPUInstancerPrefab>().prefabPrototype;
                    }
                }
            }
#endif
        }

        public static GPUInstancerPrefabPrototype GeneratePrefabPrototype(GameObject go, bool forceNew, bool attachScript = true)
        {
            if (go == null)
                return null;

            GPUInstancerPrefab prefabScript = go.GetComponent<GPUInstancerPrefab>();
            // Never edit/save a persistent prefab asset from a Play Mode
            // callback merely to attach the helper component. Scene objects
            // and prefab instances are modified only in memory; a persistent
            // asset is allowed to produce a prototype without the component.
            bool attachmentSkippedForPersistentRuntime = false;
            if (attachScript && prefabScript == null)
#if UNITY_2018_3_OR_NEWER && UNITY_EDITOR
            {
                if (Application.isPlaying && EditorUtility.IsPersistent(go))
                {
                    attachmentSkippedForPersistentRuntime = true;
                    Debug.LogWarning(
                        "GPU Instancer did not attach GPUInstancerPrefab to the persistent prefab asset during Play Mode. " +
                        "The runtime prototype was created without modifying the asset.", go);
                }
                else if (EditorUtility.IsPersistent(go) && PrefabUtility.IsPartOfPrefabAsset(go))
                {
                    prefabScript = AddComponentToPrefab<GPUInstancerPrefab>(go);
                }
                else
                {
                    // Scene objects and prefab instances must not go through
                    // LoadPrefabContents/SaveAsPrefabAsset.
                    prefabScript = go.AddComponent<GPUInstancerPrefab>();
                }
            }
#else
            {
                prefabScript = go.AddComponent<GPUInstancerPrefab>();
            }
#endif
            if (attachScript && prefabScript == null && !attachmentSkippedForPersistentRuntime)
                return null;

            GPUInstancerPrefabPrototype prototype = null;
            if (prefabScript != null)
                prototype = prefabScript.prefabPrototype;
            if (prototype == null)
            {
                prototype = ScriptableObject.CreateInstance<GPUInstancerPrefabPrototype>();
                // A persistent prefab asset is immutable from a runtime API
                // perspective.  Assigning the newly-created prototype to its
                // GPUInstancerPrefab component here would mutate the asset in
                // memory (and can become a serialized change when domain
                // reload is disabled), even though no explicit Save call is
                // made. Scene objects and prefab instances remain writable.
                bool canWritePrefabComponent = prefabScript != null;
#if UNITY_EDITOR
                canWritePrefabComponent = canWritePrefabComponent &&
                    (!Application.isPlaying || !EditorUtility.IsPersistent(prefabScript));
#endif
                if (canWritePrefabComponent)
                    prefabScript.prefabPrototype = prototype;
                prototype.prefabObject = go;
                prototype.name = go.name + "_" + go.GetInstanceID();
                DetermineTreePrototypeType(prototype);
                prototype.useOriginalShaderForShadow = true;
                if (go.GetComponent<Rigidbody>() != null)
                {
                    prototype.enableRuntimeModifications = true;
                    prototype.autoUpdateTransformData = true;
                }

                if (IsBillboardGeneratedByDefault(prototype))
                {
                    prototype.isLODCrossFade = false;
                    prototype.useGeneratedBillboard = true;
                    if (prototype.billboard == null)
                        prototype.billboard = new GPUInstancerBillboard();
                    GeneratePrototypeBillboard(prototype);
                }

                GenerateInstancedShadersForGameObject(prototype);

#if UNITY_EDITOR
                if (!Application.isPlaying)
                    EditorUtility.SetDirty(go);
#endif
            }
#if UNITY_EDITOR
            if (!Application.isPlaying && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(prototype)))
            {
                string assetPath = GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.PROTOTYPES_PREFAB_PATH + prototype.name + ".asset";

                if (!System.IO.Directory.Exists(GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.PROTOTYPES_PREFAB_PATH))
                {
                    System.IO.Directory.CreateDirectory(GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.PROTOTYPES_PREFAB_PATH);
                }

                AssetDatabase.CreateAsset(prototype, assetPath);
            }

#if UNITY_2018_3_OR_NEWER
            if (!Application.isPlaying && prefabScript != null && prefabScript.prefabPrototype != prototype)
            {
                GameObject prefabContents = LoadPrefabContents(go);
                prefabContents.GetComponent<GPUInstancerPrefab>().prefabPrototype = prototype;
                UnloadPrefabContents(go, prefabContents);
            }
#endif
#endif
            return prototype;
        }

        #endregion

        #region Create Tree Prototypes
        public static void SetTreeInstancePrototypes(UnityEngine.Object context, List<GPUInstancerPrototype> treeIntancePrototypes, TreePrototype[] treePrototypes,
            GPUInstancerTerrainSettings terrainSettings, bool forceNew)
        {
            if (forceNew)
                RemoveAssetsOfType(terrainSettings, typeof(GPUInstancerTreePrototype));

            for (int i = 0; i < treePrototypes.Length; i++)
            {
                if (!forceNew && treeIntancePrototypes.Count > i)
                    continue;

                AddTreeInstancePrototypeFromTerrainPrototype(context, treeIntancePrototypes, treePrototypes[i], i, terrainSettings);

#if UNITY_EDITOR
                if (context != null)
                    EditorUtility.SetDirty(context);
#endif
            }
            RemoveUnusedAssets(terrainSettings, treeIntancePrototypes, typeof(GPUInstancerTreePrototype));
        }

        public static void AddTreeInstancePrototypeFromTerrainPrototype(UnityEngine.Object context, List<GPUInstancerPrototype> treeInstancePrototypes, TreePrototype terrainTreePrototype,
            int treeIndex, GPUInstancerTerrainSettings terrainSettings)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && context != null)
                Undo.RecordObject(context, "Tree prototype changed " + treeIndex);
#endif

            GPUInstancerTreePrototype treePrototype = ScriptableObject.CreateInstance<GPUInstancerTreePrototype>();
            treePrototype.prototypeIndex = treeIndex;
            treePrototype.prefabObject = terrainTreePrototype.prefab;
            treePrototype.name = "Tree_" + treeIndex + "_" + terrainTreePrototype.prefab.name;
            treePrototype.maxDistance = terrainSettings.maxTreeDistance;
            treePrototype.useOriginalShaderForShadow = true;
            DetermineTreePrototypeType(treePrototype);
            if (treePrototype.treeType == GPUInstancerTreeType.None)
                treePrototype.treeType = GPUInstancerTreeType.MeshTree;

            //if (treePrototypeType == GPUInstancerTreeType.SpeedTree) 
            //    treePrototype.lodBiasAdjustment = 0.5f;

            treePrototype.isLODCrossFade = false;

            if (treePrototype.treeType != GPUInstancerTreeType.SpeedTree8)
            {
                treePrototype.useGeneratedBillboard = true;
                if (treePrototype.billboard == null)
                    treePrototype.billboard = new GPUInstancerBillboard();
                GeneratePrototypeBillboard(treePrototype);
            }

            AddObjectToAsset(terrainSettings, treePrototype);

            treeInstancePrototypes.Add(treePrototype);

            GenerateInstancedShadersForGameObject(treePrototype);
        }

        public static void AddTreeInstanceRuntimeDataToList(List<GPUInstancerRuntimeData> runtimeDataList, List<GPUInstancerPrototype> treePrototypes,
            GPUInstancerTerrainSettings terrainSettings, GPUInstancerManager ownerManager = null)
        {
            if (runtimeDataList == null || treePrototypes == null ||
                (ownerManager != null && ownerManager.isDisposed))
                return;

            // TreeInstance.prototypeIndex addresses the source prototype
            // list.  Preserve its complete layout even when project settings
            // are temporarily unavailable during startup/import.
            if (GPUInstancerConstants.projectSettings == null)
            {
                for (int i = 0; i < treePrototypes.Count; i++)
                    runtimeDataList.Add(null);
                return;
            }
            for (int i = 0; i < treePrototypes.Count; i++)
            {
                GPUInstancerTreePrototype treePrototype = treePrototypes[i] as GPUInstancerTreePrototype;
                if (treePrototype == null)
                {
                    // TreeInstance.prototypeIndex addresses the source list;
                    // preserve that index even when an old/corrupt profile
                    // contains an entry of the wrong concrete type.
                    runtimeDataList.Add(null);
                    continue;
                }

                GPUInstancerRuntimeData runtimeData = new GPUInstancerRuntimeData(treePrototype, ownerManager);

                // LOD renderers will not be added for the SpeedTree billboards since they don't have MeshFilters
                if (!runtimeData.CreateRenderersFromGameObject(treePrototype) ||
                    runtimeData.instanceLODs == null || runtimeData.instanceLODs.Count == 0 ||
                    !runtimeData.instanceLODs.Any(lod => lod != null && lod.renderers != null && lod.renderers.Count > 0))
                {
                    runtimeDataList.Add(null);
                    continue;
                }
                // Account for tree billboards
                AddBillboardToRuntimeData(runtimeData);

                if (treePrototype.treeType == GPUInstancerTreeType.SpeedTree || treePrototype.treeType == GPUInstancerTreeType.SpeedTree8 ||
                    treePrototype.treeType == GPUInstancerTreeType.TreeCreatorTree)
                    GPUInstancerManager.AddTreeProxy(treePrototype, runtimeData);

                runtimeData.hasShadowCasterBuffer = treePrototype.isShadowCasting;

                runtimeDataList.Add(runtimeData);
            }
        }

        public static void DetermineTreePrototypeType(GPUInstancerPrototype prototype)
        {
            if (prototype.prefabObject != null)
            {
                MeshRenderer meshRenderer = prototype.prefabObject.GetComponent<MeshRenderer>();
                Shader shader = null;
                if (meshRenderer != null
                    && meshRenderer.sharedMaterials != null
                    && meshRenderer.sharedMaterials.Length > 0
                    && meshRenderer.sharedMaterials[0] != null
                    && meshRenderer.sharedMaterials[0].shader != null)
                {
                    shader = meshRenderer.sharedMaterials[0].shader;
                }
                else
                {
                    LODGroup lodGroup = prototype.prefabObject.GetComponent<LODGroup>();
                    if (lodGroup != null) // SpeedTree with LOD Group
                    {
                        LOD[] lods = lodGroup.GetLODs();

                        if (lods != null
                            && lods.Length > 0
                            && lods[0].renderers != null
                            && lods[0].renderers.Length > 0
                            && lods[0].renderers[0].sharedMaterials != null
                            && lods[0].renderers[0].sharedMaterials.Length > 0
                            && lods[0].renderers[0].sharedMaterials[0] != null
                            && lods[0].renderers[0].sharedMaterials[0].shader != null)
                        {
                            shader = lods[0].renderers[0].sharedMaterials[0].shader;
                        }
                    }
                }

                if (shader != null)
                {
                    // Tree Creator
                    if (shader.name.Contains("Tree Creator"))
                    {
                        prototype.treeType = GPUInstancerTreeType.TreeCreatorTree;
                        return;
                    }

                    // SpeedTree 7 with single renderer
                    if (shader.name == GPUInstancerConstants.SHADER_UNITY_SPEED_TREE
                        || shader.name == GPUInstancerConstants.SHADER_GPUI_SPEED_TREE
                        || shader.name == GPUInstancerConstants.SHADER_UNITY_SPEED_TREE_URP)
                    {
                        prototype.treeType = GPUInstancerTreeType.SpeedTree;
                        return;
                    }

                    // SpeedTree 8 with single renderer
                    if (shader.name == GPUInstancerConstants.SHADER_UNITY_SPEED_TREE_8 ||
                        shader.name == GPUInstancerConstants.SHADER_GPUI_SPEED_TREE_8 ||
                        shader.name == GPUInstancerConstants.SHADER_UNITY_SPEED_TREE_8_URP)
                    {
                        prototype.treeType = GPUInstancerTreeType.SpeedTree8;
                        return;
                    }
                }

                // Soft Occlusion:
                if (prototype.prefabObject.GetComponentsInChildren<MeshRenderer>()
                        .Any(mr => mr.sharedMaterials.Where(m => m.shader.name == GPUInstancerConstants.SHADER_UNITY_TREE_SOFT_OCCLUSION_BARK ||
                                                                 m.shader.name == GPUInstancerConstants.SHADER_GPUI_TREE_SOFT_OCCLUSION_BARK ||
                                                                 m.shader.name == GPUInstancerConstants.SHADER_UNITY_TREE_SOFT_OCCLUSION_LEAVES ||
                                                                 m.shader.name == GPUInstancerConstants.SHADER_GPUI_TREE_SOFT_OCCLUSION_LEAVES).FirstOrDefault()))
                {
                    prototype.treeType = GPUInstancerTreeType.SoftOcclusionTree;
                    return;
                }
            }

            prototype.treeType = GPUInstancerTreeType.None;
        }

        public static void ImportSpeedTree8Shader()
        {
#if UNITY_EDITOR
            EditorApplication.update -= ImportSpeedTree8ShaderPopup;
            EditorApplication.update += ImportSpeedTree8ShaderPopup;
#endif
        }

        public static void ImportSpeedTree8ShaderPopup()
        {
#if UNITY_EDITOR
            if (Shader.Find(GPUInstancerConstants.SHADER_GPUI_SPEED_TREE_8) == null)
            {
                // No compatible built-in archive is available for this package.
                Debug.LogWarning("Fraxinus GPU Batcher SpeedTree8 support is not installed. " +
                    "Add a Fraxinus-compatible SpeedTree8 shader asset to the project.");
            }
            EditorApplication.update -= ImportSpeedTree8ShaderPopup;
#endif
        }
        #endregion

        #endregion Create Prototypes

        #region Mesh Utility Methods

        public static Mesh CreateCrossQuadsMeshForDetailGrass(float width, float height, string name, int quality)
        {
            GameObject parent = new GameObject(name, typeof(MeshFilter));
            parent.transform.position = Vector3.zero;
            CombineInstance[] combinesInstances = new CombineInstance[quality];
            for (int i = 0; i < quality; i++)
            {
                GameObject child = new GameObject("quadToCombine_" + i, typeof(MeshFilter));

                Mesh mesh = GenerateQuadMesh(width, height, new Rect(0.0f, 0.0f, 1.0f, 1.0f), true);

                // modify normals fit for grass
                for (int j = 0; j < mesh.normals.Length; j++)
                    mesh.normals[i] = Vector3.up;

                child.GetComponent<MeshFilter>().sharedMesh = mesh;
                child.transform.parent = parent.transform;
                child.transform.localPosition = Vector3.zero;
                child.transform.localRotation = Quaternion.identity * Quaternion.AngleAxis((180.0f / quality) * i, Vector3.up);
                child.transform.localScale = Vector3.one;

                combinesInstances[i] = new CombineInstance
                {
                    mesh = child.GetComponent<MeshFilter>().sharedMesh,
                    transform = child.transform.localToWorldMatrix
                };
            }
            parent.GetComponent<MeshFilter>().sharedMesh = new Mesh();
            parent.GetComponent<MeshFilter>().sharedMesh.CombineMeshes(combinesInstances, true, true);
            Mesh result = parent.GetComponent<MeshFilter>().sharedMesh;
            result.name = name;

            GameObject.DestroyImmediate(parent);
            return result;
        }

        public static Mesh GenerateQuadMesh(float width, float height, Rect? uvRect = null, bool centerPivotAtBottom = false, float pivotOffsetX = 0f, float pivotOffsetY = 0f)
        {
            Mesh mesh = new Mesh();
            mesh.name = "QuadMesh";


            //mesh.vertices = new Vector3[] {
            //    new Vector3(centerPivotAtBottom ? -width/2 : 0, 0, 0),
            //    new Vector3(centerPivotAtBottom ? -width/2 : 0, height, 0),
            //    new Vector3(centerPivotAtBottom ? width/2 : width, height, 0),
            //    new Vector3(centerPivotAtBottom ? width/2 : width, 0, 0)
            //};

            mesh.vertices = new Vector3[]
            {
                new Vector3(centerPivotAtBottom ? -width/2-pivotOffsetX : -pivotOffsetX, -pivotOffsetY, 0), // bottom left
                new Vector3(centerPivotAtBottom ? -width/2-pivotOffsetX : -pivotOffsetX, height-pivotOffsetY, 0), // top left
                new Vector3(centerPivotAtBottom ? width/2-pivotOffsetX : width-pivotOffsetX, height-pivotOffsetY, 0), // top right
                new Vector3(centerPivotAtBottom ? width/2-pivotOffsetX : width-pivotOffsetX, -pivotOffsetY, 0) // bottom right
            };


            if (uvRect != null)
                mesh.uv = new Vector2[]
                {
                    new Vector2(uvRect.Value.x, uvRect.Value.y),
                    new Vector2(uvRect.Value.x, uvRect.Value.y + uvRect.Value.height),
                    new Vector2(uvRect.Value.x + uvRect.Value.width, uvRect.Value.y + uvRect.Value.height),
                    new Vector2(uvRect.Value.x + uvRect.Value.width, uvRect.Value.y)
                };

            //mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 }; // ori
            //mesh.triangles = new int[] { 1, 2, 0, 2, 3, 0 };
            mesh.triangles = new int[] { 0, 1, 3, 1, 2, 3 };

            Vector3 planeNormal = new Vector3(0, 0, -1);
            Vector4 planeTangent = new Vector4(1, 0, 0, -1);

            mesh.normals = new Vector3[4]
            {
                planeNormal,
                planeNormal,
                planeNormal,
                planeNormal
            };

            mesh.tangents = new Vector4[4]
            {
                planeTangent,
                planeTangent,
                planeTangent,
                planeTangent
            };

            Color[] colors = new Color[mesh.vertices.Length];

            for (int i = 0; i < mesh.vertices.Length; i++)
                colors[i] = Color.Lerp(Color.clear, Color.red, mesh.vertices[i].y);

            mesh.colors = colors;

            return mesh;
        }

        #endregion Mesh Utility Methods

        #region Terrain Utility Methods

        public static List<int[]> GetDetailMapsFromTerrain(Terrain terrain, List<GPUInstancerPrototype> detailPrototypeList)
        {
            List<int[]> result = new List<int[]>();
            for (int i = 0; i < detailPrototypeList.Count; i++)
            {
                int[,] map = terrain.terrainData.GetDetailLayer(0, 0, terrain.terrainData.detailResolution, terrain.terrainData.detailResolution, i);
                result.Add(new int[map.GetLength(0) * map.GetLength(1)]);
                for (int y = 0; y < map.GetLength(0); y++)
                {
                    for (int x = 0; x < map.GetLength(1); x++)
                    {
                        result[i][x + y * map.GetLength(0)] = map[y, x];
                    }
                }
            }
            return result;
        }

        public static Bounds GenerateBoundsFromTerrainPositionAndSize(Vector3 position, Vector3 size)
        {
            return new Bounds(new Vector3(position.x + size.x / 2, position.y + size.y / 2, position.z + size.z / 2), size);
        }

        /// get height for specified coordinates
        public static float SampleTerrainHeight(float px, float py, float leftBottomH, float leftTopH, float rightBottomH, float rightTopH)
        {
            return Mathf.Lerp(Mathf.Lerp(leftBottomH, rightBottomH, px), Mathf.Lerp(leftTopH, rightTopH, px), py);
        }

        public static Vector3 ComputeTerrainNormal(float leftBottomH, float leftTopH, float rightBottomH, float scale)
        {
            Vector3 P = new Vector3(0, leftBottomH * scale, 0);
            Vector3 Q = new Vector3(0, leftTopH * scale, 1);
            Vector3 R = new Vector3(1, rightBottomH * scale, 0);

            return Vector3.Cross(Q - R, R - P).normalized;
        }

        #endregion Terrain Utility Methods

        #region Math Utility Methods

        public static int GCD(int[] numbers)
        {
            return numbers.Aggregate(GCD);
        }

        public static int GCD(int a, int b)
        {
            return b == 0 ? a : GCD(b, a % b);
        }

        public static IEnumerable<int> GetDivisors(int n)
        {
            return from a in Enumerable.Range(2, n / 2)
                   where n % a == 0
                   select a;
        }

        #endregion Math Utility Methods

        #region Billboard Utility Methods
        public static void AssignBillboardBinding(GPUInstancerPrototype prototype)
        {
            if (prototype.billboard == null)
                prototype.billboard = new GPUInstancerBillboard();

            if (prototype.billboard.albedoAtlasTexture == null)
            {
                BillboardAtlasBinding billboardAtlasBinding = GPUInstancerConstants.projectSettings.billboardAtlasBindings.GetBillboardAtlasBinding(prototype.prefabObject, prototype.billboard.atlasResolution, prototype.billboard.frameCount);

                if (billboardAtlasBinding != null)
                {
                    prototype.billboard.albedoAtlasTexture = billboardAtlasBinding.albedoAtlasTexture;
                    prototype.billboard.normalAtlasTexture = billboardAtlasBinding.normalAtlasTexture;
                    prototype.billboard.quadSize = billboardAtlasBinding.quadSize;
                    prototype.billboard.yPivotOffset = billboardAtlasBinding.yPivotOffset;
                }
            }
        }

        public static void GeneratePrototypeBillboard(GPUInstancerPrototype prototype, bool forceRegenerate = false)
        {

            if (prototype.billboard == null)
                prototype.billboard = new GPUInstancerBillboard();

            if (prototype.billboard.useCustomBillboard)
                return;

            if (!GPUInstancerConstants.projectSettings.IsBillboardsSupported())
                return; // SRP version is not supported for generated billboards.
            DetermineTreePrototypeType(prototype);

            prototype.billboard.billboardFaceCamPos = GPUInstancerProjectSettings.IsVREnabled();

            BillboardAtlasBinding billboardAtlasBinding = GPUInstancerConstants.projectSettings.billboardAtlasBindings.GetBillboardAtlasBinding(prototype.prefabObject, prototype.billboard.atlasResolution, prototype.billboard.frameCount);
            if (billboardAtlasBinding != null)
            {
                if (forceRegenerate)
                {
                    GPUInstancerConstants.projectSettings.billboardAtlasBindings.RemoveBillboardAtlas(billboardAtlasBinding);
                    //#if UNITY_EDITOR
                    // commented out because saving over same file
                    //AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(billboardAtlasBinding.albedoAtlasTexture));
                    //AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(billboardAtlasBinding.normalAtlasTexture));
                    //#endif
                }
                else
                {
                    prototype.billboard.albedoAtlasTexture = billboardAtlasBinding.albedoAtlasTexture;
                    prototype.billboard.normalAtlasTexture = billboardAtlasBinding.normalAtlasTexture;
                    prototype.billboard.quadSize = billboardAtlasBinding.quadSize;
                    prototype.billboard.yPivotOffset = billboardAtlasBinding.yPivotOffset;
                    return;
                }
            }

            GameObject sample = null;
            GameObject billboardCameraPivot = null;
            RenderTexture currentRt = null;
            RenderPipelineAsset renderPipelineAsset = null;
            RenderPipelineAsset qualityPipelineAsset = null;
            bool renderPipelineOverridden = false;
#if UNITY_EDITOR
            string albedoAtlasPath = null;
            string normalAtlasPath = null;
#endif
#if UNITY_2022_2_OR_NEWER
            int cachedMasterTextureLimit = QualitySettings.globalTextureMipmapLimit;
            QualitySettings.globalTextureMipmapLimit = 0;
#else
            int cachedMasterTextureLimit = QualitySettings.masterTextureLimit;
            QualitySettings.masterTextureLimit = 0;
#endif

            try
            {
                //Debug.Log("Generating Billboard for " + prototype.name);

                // cache the current render texture
                currentRt = RenderTexture.active;

                // calculate frame resolution
                int frameResolution = prototype.billboard.atlasResolution / prototype.billboard.frameCount;

#if UNITY_EDITOR
                // use previous texture path if exists
                if (prototype.billboard.albedoAtlasTexture != null)
                    albedoAtlasPath = AssetDatabase.GetAssetPath(prototype.billboard.albedoAtlasTexture);
                if (prototype.billboard.normalAtlasTexture != null)
                    normalAtlasPath = AssetDatabase.GetAssetPath(prototype.billboard.normalAtlasTexture);
#endif

                // initialize the atlas textures
                prototype.billboard.albedoAtlasTexture = new Texture2D(prototype.billboard.atlasResolution, frameResolution);
                prototype.billboard.normalAtlasTexture = new Texture2D(prototype.billboard.atlasResolution, frameResolution);

                // create render target for atlas frames (both albedo and normal will share the same target)
                RenderTexture frameTarget = RenderTexture.GetTemporary(frameResolution, frameResolution, 32, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                frameTarget.enableRandomWrite = true;
                frameTarget.Create();

                // instantiate an instance of the prefab to sample
                sample = GameObject.Instantiate(prototype.prefabObject, Vector3.zero, Quaternion.identity); // TO-DO: apply a rotation offset?
                sample.transform.localScale = Vector3.one;
                sample.hideFlags = HideFlags.DontSave;

                // set all children of the sample to the sample layer and calculate their overall bounds
                int sampleLayer = 31;
                MeshRenderer[] sampleChildrenMRs = sample.GetComponentsInChildren<MeshRenderer>();

                if (sampleChildrenMRs == null || sampleChildrenMRs.Length == 0)
                {
                    Debug.LogError("Cannot create GPU Instancer billboard for " + prototype.name + " : no mesh renderers found in prototype prefab!");
                    GameObject.DestroyImmediate(sample);
                    prototype.useGeneratedBillboard = false;
                    return;
                }

                Bounds sampleBounds = new Bounds(Vector3.zero, Vector3.zero);

                for (int i = 0; i < sampleChildrenMRs.Length; i++)
                {
                    sampleChildrenMRs[i].gameObject.layer = sampleLayer;

                    // TO-DO: turn this into a generic, add logic to shader bindings:
                    for (int m = 0; m < sampleChildrenMRs[i].sharedMaterials.Length; m++)
                    {
                        if (sampleChildrenMRs[i].sharedMaterials[m].HasProperty("_MainTexture"))
                            sampleChildrenMRs[i].sharedMaterials[m].SetTexture("_MainTex", sampleChildrenMRs[i].sharedMaterials[m].GetTexture("_MainTexture"));
                    }

                    // check if mesh renderer is enabled for this child
                    if (!sampleChildrenMRs[i].enabled)
                        continue;

                    MeshFilter sampleChildMF = sampleChildrenMRs[i].GetComponent<MeshFilter>();
                    if (sampleChildMF == null || sampleChildMF.sharedMesh == null || sampleChildMF.sharedMesh.vertices == null)
                        continue;

                    // encapsulate vertices instead of mesh renderer bounds; the latter are sometimes larger than necessary
                    Vector3[] verts = sampleChildMF.sharedMesh.vertices;
                    for (var v = 0; v < verts.Length; v++)
                        sampleBounds.Encapsulate(sampleChildrenMRs[i].transform.localToWorldMatrix.MultiplyPoint3x4(verts[v]));
                }

                // calculate quad size
                float sampleBoundsMaxSize = Mathf.Max(sampleBounds.size.x, sampleBounds.size.z) * 2;
                sampleBoundsMaxSize = Mathf.Max(sampleBoundsMaxSize, sampleBounds.size.y); // if height is longer, adjust.

                Shader billboardAlbedoBakeShader = Shader.Find(GPUInstancerConstants.SHADER_GPUI_BILLBOARD_ALBEDO_BAKER);
                Shader billboardNormalBakeShader = Shader.Find(GPUInstancerConstants.SHADER_GPUI_BILLBOARD_NORMAL_BAKER);
                Shader.SetGlobalFloat("_GPUIBillboardBrightness", prototype.billboard.billboardBrightness);
                Shader.SetGlobalFloat("_GPUIBillboardCutoffOverride", prototype.billboard.cutoffOverride);
#if UNITY_EDITOR
                Shader.SetGlobalFloat("_IsLinearSpace", PlayerSettings.colorSpace == ColorSpace.Linear ? 1.0f : 0.0f);
#endif


                // create the billboard snapshot camera
                billboardCameraPivot = new GameObject("GPUI_BillboardCameraPivot");
                Camera billboardCamera = new GameObject().AddComponent<Camera>();
                billboardCamera.transform.SetParent(billboardCameraPivot.transform);

                billboardCamera.gameObject.hideFlags = HideFlags.DontSave;
                billboardCamera.cullingMask = 1 << sampleLayer;
                billboardCamera.clearFlags = CameraClearFlags.SolidColor;
                billboardCamera.backgroundColor = Color.clear;
                billboardCamera.orthographic = true;
                billboardCamera.nearClipPlane = 0f;
                billboardCamera.farClipPlane = sampleBoundsMaxSize;
                billboardCamera.orthographicSize = sampleBoundsMaxSize * 0.5f;
                billboardCamera.allowMSAA = false;
                billboardCamera.enabled = false;
                billboardCamera.renderingPath = RenderingPath.Forward;
                billboardCamera.targetTexture = frameTarget;
                billboardCamera.transform.localPosition = new Vector3(0, sampleBounds.center.y, -sampleBoundsMaxSize / 2);

                float rotateAngle = 360f / prototype.billboard.frameCount;

                renderPipelineAsset = GraphicsSettings.renderPipelineAsset;
                qualityPipelineAsset = QualitySettings.renderPipeline;
                GraphicsSettings.renderPipelineAsset = null;
                QualitySettings.renderPipeline = null;
                renderPipelineOverridden = true;

                // render the frames into the atlas textures
                RenderTexture.active = frameTarget;
                for (int f = 0; f < prototype.billboard.frameCount; f++)
                {
                    sample.transform.rotation = Quaternion.AngleAxis(rotateAngle * -f, Vector3.up);
                    //billboardCameraPivot.transform.rotation = Quaternion.AngleAxis(rotateAngle * f, Vector3.up);
                    billboardCamera.RenderWithShader(billboardAlbedoBakeShader, String.Empty);
                    prototype.billboard.albedoAtlasTexture.ReadPixels(new Rect(0, 0, frameResolution, frameResolution), f * frameResolution, 0);

                    billboardCamera.RenderWithShader(billboardNormalBakeShader, String.Empty);
                    prototype.billboard.normalAtlasTexture.ReadPixels(new Rect(0, 0, frameResolution, frameResolution), f * frameResolution, 0);
                }
                GraphicsSettings.renderPipelineAsset = renderPipelineAsset;
                QualitySettings.renderPipeline = qualityPipelineAsset;
                renderPipelineOverridden = false;

                // set the result billboard to the prototype
                prototype.billboard.albedoAtlasTexture.Apply();
                prototype.billboard.normalAtlasTexture.Apply();

                prototype.billboard.albedoAtlasTexture = DilateBillboardTexture(prototype.billboard.albedoAtlasTexture, prototype.billboard.frameCount, false);
                prototype.billboard.normalAtlasTexture = DilateBillboardTexture(prototype.billboard.normalAtlasTexture, prototype.billboard.frameCount, true);

                prototype.billboard.quadSize = sampleBoundsMaxSize;
                prototype.billboard.yPivotOffset = sample.transform.position.y + ((sampleBoundsMaxSize / 2) - (sampleBounds.extents.y) - sampleBounds.min.y);

#if UNITY_EDITOR
                // save the textures to the project
                if (!System.IO.Directory.Exists(GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.PROTOTYPES_BILLBOARD_TEXTURES_PATH))
                {
                    System.IO.Directory.CreateDirectory(GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.PROTOTYPES_BILLBOARD_TEXTURES_PATH);
                }

                if (string.IsNullOrEmpty(albedoAtlasPath))
                    albedoAtlasPath = GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.PROTOTYPES_BILLBOARD_TEXTURES_PATH + prototype.name + "_BillboardAlbedo.png";
                if (string.IsNullOrEmpty(normalAtlasPath))
                    normalAtlasPath = GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.PROTOTYPES_BILLBOARD_TEXTURES_PATH + prototype.name + "_BillboardNormal.png";
                var bytes = prototype.billboard.albedoAtlasTexture.EncodeToPNG();
                VersionControlCheckout(albedoAtlasPath);
                System.IO.File.WriteAllBytes(albedoAtlasPath, bytes);

                bytes = prototype.billboard.normalAtlasTexture.EncodeToPNG();
                VersionControlCheckout(normalAtlasPath);
                System.IO.File.WriteAllBytes(normalAtlasPath, bytes);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // set importer settings
                TextureImporter importer = (TextureImporter)TextureImporter.GetAtPath(albedoAtlasPath);
                importer.maxTextureSize = prototype.billboard.atlasResolution;
                AssetDatabase.ImportAsset(albedoAtlasPath);
                importer = (TextureImporter)TextureImporter.GetAtPath(normalAtlasPath);
                importer.maxTextureSize = prototype.billboard.atlasResolution;
                importer.textureType = TextureImporterType.NormalMap;
                AssetDatabase.ImportAsset(normalAtlasPath);

                // set the atlas references to the newly created asset files.
                prototype.billboard.albedoAtlasTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoAtlasPath);
                prototype.billboard.normalAtlasTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(normalAtlasPath);

                if (prototype)
                    EditorUtility.SetDirty(prototype);
#endif

                GPUInstancerConstants.projectSettings.billboardAtlasBindings.AddBillboardAtlas(prototype.prefabObject, prototype.billboard.atlasResolution, prototype.billboard.frameCount, prototype.billboard.albedoAtlasTexture,
                    prototype.billboard.normalAtlasTexture, prototype.billboard.quadSize, prototype.billboard.yPivotOffset);

                // restore the active render target
                RenderTexture.active = currentRt;

                // Debug quad:
                // ShowBillboardQuad(prototype, Vector3.zero);
            }
            finally
            {
                if (currentRt != null)
                    RenderTexture.active = currentRt;
                if (renderPipelineOverridden)
                {
                    GraphicsSettings.renderPipelineAsset = renderPipelineAsset;
                    QualitySettings.renderPipeline = qualityPipelineAsset;
                }
#if UNITY_2022_2_OR_NEWER
                QualitySettings.globalTextureMipmapLimit = cachedMasterTextureLimit;
#else
                QualitySettings.masterTextureLimit = cachedMasterTextureLimit;
#endif
                if (sample)
                    GameObject.DestroyImmediate(sample);
                if (billboardCameraPivot)
                    GameObject.DestroyImmediate(billboardCameraPivot);
            }
        }

        public static Texture2D DilateBillboardTexture(Texture2D billboardTexture, int frameCount, bool isNormal)
        {
            ComputeShader dilationCompute = (ComputeShader)Resources.Load(GPUInstancerConstants.COMPUTE_BILLBOARD_RESOURCE_PATH);
            int dilationKernel = dilationCompute.FindKernel(GPUInstancerConstants.COMPUTE_BILLBOARD_DILATION_KERNEL);

            RenderTexture resultTexture = RenderTexture.GetTemporary(billboardTexture.width, billboardTexture.height, 32, RenderTextureFormat.ARGB32);

            resultTexture.enableRandomWrite = true;
            resultTexture.Create();

            dilationCompute.SetTexture(dilationKernel, "result", resultTexture);
            dilationCompute.SetTexture(dilationKernel, "billboardSource", billboardTexture);
            dilationCompute.SetInts("billboardSize", new int[2] { billboardTexture.width, billboardTexture.height });
            dilationCompute.SetInt("frameCount", frameCount);
#if UNITY_EDITOR
            dilationCompute.SetBool("isLinearSpace", PlayerSettings.colorSpace == ColorSpace.Linear);
#endif
            dilationCompute.SetBool("isNormal", isNormal);
            DispatchCompute(dilationCompute, dilationKernel,
                Mathf.CeilToInt(billboardTexture.width / (GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT_2D * frameCount)),
                Mathf.CeilToInt(billboardTexture.height / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT_2D), frameCount);
            RenderTexture.active = resultTexture;

            Texture2D result = new Texture2D(billboardTexture.width, billboardTexture.height);
            result.ReadPixels(new Rect(0, 0, billboardTexture.width, billboardTexture.height), 0, 0);
            result.Apply();

            RenderTexture.active = null;
            resultTexture.Release();
            return result;
        }

        public static void AddBillboardToRuntimeData(GPUInstancerRuntimeData runtimeData)
        {
            if (runtimeData.prototype.useGeneratedBillboard && runtimeData.prototype.billboard != null)
            {
                // This is for the special case of importing a prototype with a billboard generated in the Standard Pipeline into an SRP project.
                if (!GPUInstancerConstants.projectSettings.IsBillboardsSupported() && runtimeData.prototype.useGeneratedBillboard && !runtimeData.prototype.billboard.useCustomBillboard)
                    return;

                Mesh billboardMesh;
                Material billboardMaterial;
                bool isShadowCasting = false;

                if (runtimeData.prototype.billboard.useCustomBillboard && runtimeData.prototype.billboard.customBillboardInLODGroup)
                    return;

                if (runtimeData.prototype.billboard.useCustomBillboard
                    && runtimeData.prototype.billboard.customBillboardMesh != null && runtimeData.prototype.billboard.customBillboardMaterial != null)
                {
                    billboardMesh = runtimeData.prototype.billboard.customBillboardMesh;
                    billboardMaterial = GPUInstancerConstants.projectSettings.shaderBindings.GetInstancedMaterial(runtimeData.prototype.billboard.customBillboardMaterial);
                    isShadowCasting = runtimeData.prototype.billboard.isBillboardShadowCasting;
                }
                else
                {
                    if (runtimeData.prototype.billboard.albedoAtlasTexture == null || runtimeData.prototype.billboard.normalAtlasTexture == null)
                        return;

                    billboardMesh = GenerateQuadMesh(runtimeData.prototype.billboard.quadSize, runtimeData.prototype.billboard.quadSize,
                                    new Rect(0.0f, 0.0f, 1.0f, 1.0f), true, 0, runtimeData.prototype.billboard.yPivotOffset);

                    billboardMaterial = GetBillboardMaterial(runtimeData.prototype);
                }

                if (runtimeData.prototype.isLODCrossFade)
                    runtimeData.EnableLODFade(billboardMaterial);

                if (runtimeData.prototype.treeType == GPUInstancerTreeType.SpeedTree || runtimeData.prototype.treeType == GPUInstancerTreeType.SpeedTree8)
                {

                    LODGroup lodGroup = runtimeData.prototype.prefabObject.GetComponent<LODGroup>();

                    if (lodGroup != null) // if the SpeedTree is not an LOD Group, it will be treated the same with mesh renderer non-SpeedTree objects below.
                    {
                        for (int lod = 0; lod < lodGroup.GetLODs().Length; lod++)
                        {
                            bool speedTreeBillboardFound = false;

                            if (runtimeData.prototype.treeType == GPUInstancerTreeType.SpeedTree)
                                speedTreeBillboardFound = lodGroup.GetLODs()[lod].renderers.Any(r => r.GetComponent<BillboardRenderer>() != null);

                            if (runtimeData.prototype.treeType == GPUInstancerTreeType.SpeedTree8)
                                speedTreeBillboardFound = lodGroup.GetLODs()[lod].renderers.Any(r => r.sharedMaterials[0].IsKeywordEnabled("EFFECT_BILLBOARD"));

                            if (speedTreeBillboardFound)
                            {
                                runtimeData.AddLodAndRenderer(billboardMesh, new List<Material> { billboardMaterial }, new MaterialPropertyBlock(), isShadowCasting,
                                    lodGroup.GetLODs()[lod].screenRelativeTransitionHeight, new MaterialPropertyBlock(), true, runtimeData.prototype.prefabObject.layer);
                                return;
                            }

                        }

                        // if the SpeedTree object didn't have a billboard LOD, add it as the final LOD anyway. (Handled below)
                    }
                }

                if (runtimeData.prototype.prefabObject.GetComponent<LODGroup>() != null && runtimeData.prototype.billboard.replaceLODCullWithBillboard)
                {
                    runtimeData.AddLodAndRenderer(billboardMesh, new List<Material> { billboardMaterial }, new MaterialPropertyBlock(), isShadowCasting,
                            0f, new MaterialPropertyBlock(), true, runtimeData.prototype.prefabObject.layer);
                }
                else
                {
                    //float halfAngle = Mathf.Tan(Mathf.Deg2Rad * GPUInstancerManager.activeManagerList.Find(m => m is GPUInstancerTreeManager).cameraData.mainCamera.fieldOfView * 0.5F);
                    //float lodSize = runtimeData.prototype.billboard.quadSize * 0.5F / (50f * halfAngle);
                    //lodSize /= QualitySettings.lodBias;

                    float lodSize = (1 - runtimeData.prototype.billboard.billboardDistance) / QualitySettings.lodBias;
                    int index = (runtimeData.instanceLODs.Count - 1) * 4;
                    if (runtimeData.instanceLODs.Count > 4)
                        index = (runtimeData.instanceLODs.Count - 5) * 4 + 1;

                    if (lodSize > runtimeData.lodSizes[index])
                    {
                        runtimeData.lodSizes[index] = lodSize;
                        if (runtimeData.prototype.isLODCrossFade && !runtimeData.prototype.isLODCrossFadeAnimate)
                            runtimeData.lodSizes[index + 2] = lodSize + (1 - lodSize) * runtimeData.prototype.lodFadeTransitionWidth;
                    }

                    runtimeData.AddLodAndRenderer(billboardMesh, new List<Material> { billboardMaterial }, new MaterialPropertyBlock(), isShadowCasting,
                            0f, new MaterialPropertyBlock(), true, runtimeData.prototype.prefabObject.layer);
                }
            }
        }

        public static Material GetBillboardMaterial(GPUInstancerPrototype prototype)
        {
            if (!GPUInstancerConstants.projectSettings.IsBillboardsSupported())
                return null;

            Material billboardMaterial = new Material(
                Shader.Find(GPUInstancerConstants.SHADER_GPUI_BILLBOARD_2D_RENDERER_URP));

            billboardMaterial.SetTexture("_AlbedoAtlas", prototype.billboard.albedoAtlasTexture);
            billboardMaterial.SetTexture("_NormalAtlas", prototype.billboard.normalAtlasTexture);
            billboardMaterial.SetFloat("_FrameCount_GPUI", prototype.billboard.frameCount);
            billboardMaterial.SetFloat("_CutOff_GPUI", 0.3f);
            billboardMaterial.SetFloat("_NormalStrength_GPUI", prototype.billboard.normalStrength);

            if (prototype.billboard.billboardFaceCamPos)
                billboardMaterial.EnableKeyword("_BILLBOARDFACECAMPOS_ON");
            else
                billboardMaterial.DisableKeyword("_BILLBOARDFACECAMPOS_ON");

            billboardMaterial.name = billboardMaterial.shader.name + "_GPUITemp";
            return billboardMaterial;
        }

        public static string GetBillboardShaderName(GPUInstancerPrototype prototype)
        {
            if (prototype.billboard == null)
                return null;

            if (prototype.billboard.useCustomBillboard && prototype.billboard.customBillboardMaterial != null && prototype.billboard.customBillboardMaterial.shader != null)
                return prototype.billboard.customBillboardMaterial.shader.name;

            return GPUInstancerConstants.SHADER_GPUI_BILLBOARD_2D_RENDERER_URP;
        }

        public static bool IsBillboardGeneratedByDefault(GPUInstancerPrototype prototype)
        {
            return (prototype.treeType == GPUInstancerTreeType.SpeedTree || prototype.treeType == GPUInstancerTreeType.TreeCreatorTree
                || (prototype.billboard != null && prototype.billboard.useCustomBillboard));
        }

        public static void ShowBillboardQuad(GPUInstancerPrototype prototype, Vector3 quadPos)
        {
            if (prototype.billboard.useCustomBillboard)
            {
                if (prototype.billboard.customBillboardMesh != null && prototype.billboard.customBillboardMaterial != null)
                {
                    GameObject quadGO = new GameObject();
                    quadGO.name = "GPUI Billboard (" + prototype.name + ")";
                    MeshFilter quadMF = quadGO.AddComponent<MeshFilter>();
                    MeshRenderer quadMR = quadGO.AddComponent<MeshRenderer>();
                    quadMR.shadowCastingMode = prototype.billboard.isBillboardShadowCasting ? ShadowCastingMode.On : ShadowCastingMode.Off;

                    quadMF.mesh = prototype.billboard.customBillboardMesh;
                    quadMR.sharedMaterial = prototype.billboard.customBillboardMaterial;

#if UNITY_EDITOR
                    Selection.activeGameObject = quadGO;
                    SceneView.lastActiveSceneView.FrameSelected();
#endif
                }
            }
            else
            {
                GameObject quadGO = new GameObject();
                quadGO.name = "GPUI Billboard (" + prototype.name + ")";
                MeshFilter quadMF = quadGO.AddComponent<MeshFilter>();
                MeshRenderer quadMR = quadGO.AddComponent<MeshRenderer>();
                quadMR.shadowCastingMode = ShadowCastingMode.Off;

                quadMF.mesh = GenerateQuadMesh(prototype.billboard.quadSize, prototype.billboard.quadSize, new Rect(0.0f, 0.0f, 1.0f, 1.0f), true, 0, prototype.billboard.yPivotOffset);
                quadMR.sharedMaterial = GetBillboardMaterial(prototype);

#if UNITY_EDITOR
                Selection.activeGameObject = quadGO;
                SceneView.lastActiveSceneView.FrameSelected();
#endif
            }
        }

#endregion Billboard Utility Methods

        #region ScriptableObject Utility Methods

        public static void RemoveAssetsOfType(UnityEngine.Object baseAsset, Type type)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                string assetPath = AssetDatabase.GetAssetPath(baseAsset);
                if (GPUInstancerConstants.IsPackageAssetPath(assetPath))
                {
                    Debug.LogWarning("Skipping mutation of package-owned asset: " + assetPath);
                    return;
                }
                UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                bool requireImport = false;
                foreach (UnityEngine.Object asset in assets)
                {
                    if (asset.GetType().Equals(type))
                    {
                        GameObject.DestroyImmediate(asset, true);
                        requireImport = true;
                    }
                }
                if (requireImport)
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                }
            }
#endif
        }

        public static void RemoveUnusedAssets<T>(UnityEngine.Object baseAsset, List<T> prototypeList, Type prototypeType) where T : GPUInstancerPrototype
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                string assetPath = AssetDatabase.GetAssetPath(baseAsset);
                if (GPUInstancerConstants.IsPackageAssetPath(assetPath))
                {
                    Debug.LogWarning("Skipping mutation of package-owned asset: " + assetPath);
                    return;
                }
                UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                bool requireImport = false;
                foreach (UnityEngine.Object asset in assets)
                {
                    if (asset.GetType() == prototypeType && !prototypeList.Contains((T)asset))
                    {
                        GameObject.DestroyImmediate(asset, true);
                        requireImport = true;
                    }
                }
                if (requireImport)
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                }
            }
#endif
        }

        public static void AddObjectToAsset(UnityEngine.Object baseAsset, UnityEngine.Object objectToAdd)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                string assetPath = AssetDatabase.GetAssetPath(baseAsset);
                if (GPUInstancerConstants.IsPackageAssetPath(assetPath))
                {
                    Debug.LogWarning("Skipping mutation of package-owned asset: " + assetPath);
                    return;
                }
                AssetDatabase.AddObjectToAsset(objectToAdd, assetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }
#endif
        }

        public static void SetPrototypeListFromAssets<T>(UnityEngine.Object baseAsset, List<T> prototypeList, Type prototypeType) where T : GPUInstancerPrototype
        {
#if UNITY_EDITOR
            string assetPath = AssetDatabase.GetAssetPath(baseAsset);
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            foreach (UnityEngine.Object asset in assets)
            {
                if (asset.GetType() == prototypeType)
                    prototypeList.Add((T)asset);
            }
#endif
        }

        public static string GetAssetGUID(UnityEngine.Object assetObject)
        {
#if UNITY_EDITOR
            string assetPath = AssetDatabase.GetAssetPath(assetObject);
            if (!string.IsNullOrEmpty(assetPath))
                return AssetDatabase.AssetPathToGUID(assetPath);
#endif
            return null;
        }

        #endregion ScriptableObject Utility Methods

        #region Spatial Partitioning

        public static void CalculateSpatialPartitioningValuesFromTerrain(GPUInstancerSpatialPartitioningData<GPUInstancerCell> spData,
            Terrain terrain, float maxDetailDistance, float preferedCellSize = 0)
        {
            if (preferedCellSize == 0)
                preferedCellSize = maxDetailDistance / 2;

            float maxTerrainSize = Mathf.Max(terrain.terrainData.size.x, terrain.terrainData.size.z);
            spData.cellRowAndCollumnCountPerTerrain = Mathf.FloorToInt(maxTerrainSize / preferedCellSize);

            if (spData.cellRowAndCollumnCountPerTerrain == 0)
            {
                spData.cellRowAndCollumnCountPerTerrain = 1;
            }
            else
            {
                // fix cellRowAndCollumnCountPerTerrain
                if (terrain.terrainData.detailResolution % spData.cellRowAndCollumnCountPerTerrain != 0
                    || (terrain.terrainData.heightmapResolution - 1) % spData.cellRowAndCollumnCountPerTerrain != 0)
                {
                    int gcd = GCD(terrain.terrainData.detailResolution, terrain.terrainData.heightmapResolution - 1);
                    List<int> divisors = GetDivisors(gcd).ToList();
                    divisors.Add(gcd);
                    divisors.RemoveAll(d => d > spData.cellRowAndCollumnCountPerTerrain);
                    spData.cellRowAndCollumnCountPerTerrain = divisors.Count == 0 ? 1 : divisors.Last();
                }
            }

            float innerCellSizeX = terrain.terrainData.size.x / spData.cellRowAndCollumnCountPerTerrain;
            float innerCellSizeY = terrain.terrainData.size.y;
            float innerCellSizeZ = terrain.terrainData.size.z / spData.cellRowAndCollumnCountPerTerrain;
            float boundsAddition = maxDetailDistance * 2.5f;

            for (int z = 0; z < spData.cellRowAndCollumnCountPerTerrain; z++)
            {
                for (int x = 0; x < spData.cellRowAndCollumnCountPerTerrain; x++)
                {
                    GPUInstancerDetailCell cell = new GPUInstancerDetailCell(x, z);
                    cell.cellBounds = new Bounds(
                        new Vector3(
                            terrain.transform.position.x + (x * innerCellSizeX) + innerCellSizeX / 2,
                            terrain.transform.position.y + innerCellSizeY / 2,
                            terrain.transform.position.z + (z * innerCellSizeZ) + innerCellSizeZ / 2
                        ),
                        new Vector3(innerCellSizeX + boundsAddition, innerCellSizeY + boundsAddition, innerCellSizeZ + boundsAddition));
                    cell.cellInnerBounds = new Bounds(
                        new Vector3(
                            terrain.transform.position.x + (x * innerCellSizeX) + innerCellSizeX / 2,
                            terrain.transform.position.y + innerCellSizeY / 2,
                            terrain.transform.position.z + (z * innerCellSizeZ) + innerCellSizeZ / 2
                        ),
                        new Vector3(innerCellSizeX, innerCellSizeY, innerCellSizeZ));

                    cell.instanceStartPosition = new Vector3(terrain.transform.position.x + x * innerCellSizeX, terrain.transform.position.y, terrain.transform.position.z + z * innerCellSizeZ);

                    spData.AddCell(cell);
                }
            }
        }

        #endregion Spatial Partitioning

        #region Shader Functions

        public static void GenerateInstancedShadersForGameObject(GPUInstancerPrototype prototype)
        {
            if (prototype.prefabObject == null)
                return;

            MeshRenderer[] meshRenderers = prototype.prefabObject.GetComponentsInChildren<MeshRenderer>();

#if UNITY_EDITOR
            string warnings = "";
            Shader warningShader = null;
#endif

            foreach (MeshRenderer mr in meshRenderers)
            {
                Material[] mats = mr.sharedMaterials;

                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || mats[i].shader == null)
                        continue;
                    if (GPUInstancerConstants.projectSettings.shaderBindings.IsShadersInstancedVersionExists(mats[i].shader.name))
                    {
                        GPUInstancerConstants.projectSettings.AddShaderVariantToCollection(mats[i]);
                        continue;
                    }

                    if (!Application.isPlaying)
                    {
                        if (IsShaderInstanced(mats[i].shader))
                        {
                            GPUInstancerConstants.projectSettings.shaderBindings.AddShaderInstance(mats[i].shader.name, mats[i].shader, true);
                            GPUInstancerConstants.projectSettings.AddShaderVariantToCollection(mats[i]);
                        }
                        else
                        {
                            Shader instancedShader = CreateInstancedShader(mats[i].shader);
                            if (instancedShader != null)
                            {
                                GPUInstancerConstants.projectSettings.shaderBindings.AddShaderInstance(mats[i].shader.name, instancedShader);
                                GPUInstancerConstants.projectSettings.AddShaderVariantToCollection(mats[i]);
                            }
#if UNITY_EDITOR
                            else
                            {
                                if (!warnings.Contains(mats[i].shader.name))
                                {
                                    warningShader = mats[i].shader;
                                    string originalAssetPath = AssetDatabase.GetAssetPath(mats[i].shader).ToLower();
                                    if (originalAssetPath.EndsWith(".shadergraph"))
                                        warnings += string.Format(GPUInstancerConstants.ERRORTEXT_shaderGraph, mats[i].shader.name);
                                    else if (originalAssetPath.EndsWith(".surfshader"))
                                    {
                                        warnings += "Better Shaders surface shader does not contain GPU Instancer setup: " + mats[i].shader.name + ". Please create a stacked shader and add Stackable_GPUInstancer to the Shader List.";
                                    }
                                    else if (originalAssetPath.EndsWith(".stackedshader"))
                                    {
                                        warnings += "Better Shaders stacked shader does not contain GPU Instancer setup: " + mats[i].shader.name + ". Please add Stackable_GPUInstancer to the Shader List.";
                                    }
                                    else
                                    {
                                        warnings += "Can not create instanced version for shader: " + mats[i].shader.name + ". If you are using a Unity built-in shader, please download the shader to your project from the Unity Archive.";
                                        warningShader = null;
                                    }
                                }
                            }
#endif
                        }
                    }
                }
            }

            if (prototype.useGeneratedBillboard && prototype.billboard != null)
            {
                Material m = GetBillboardMaterial(prototype);
                GPUInstancerConstants.projectSettings.AddShaderVariantToCollection(m);
                DestroyObject(m);
            }

#if UNITY_EDITOR
            if (string.IsNullOrEmpty(warnings))
            {
                if (prototype.warningText != null)
                {
                    prototype.warningText = null;
                    prototype.warningShader = null;
                }
            }
            else
            {
                if (prototype.warningText != warnings)
                {
                    prototype.warningText = warnings;
                    prototype.warningShader = warningShader;
                }
            }
#endif
        }

        public static bool IsShaderInstanced(Shader shader)
        {
#if UNITY_EDITOR
            if (shader == null || shader.name == GPUInstancerConstants.SHADER_UNITY_INTERNAL_ERROR)
            {
                Debug.LogError("Can not find shader! Please make sure that the material has a shader assigned.");
                return false;
            }
            string originalAssetPath = AssetDatabase.GetAssetPath(shader);
            string originalShaderText = "";
            originalShaderText = System.IO.File.ReadAllText(originalAssetPath);
            if (!string.IsNullOrEmpty(originalShaderText))
            {
                if (originalAssetPath.ToLower().EndsWith(".shadergraph"))
                {
                    return originalShaderText.Contains("GPUInstancerShaderGraphNode") || originalShaderText.Contains("GPU Instancer Setup");
                }
                else if (originalAssetPath.ToLower().EndsWith(".stackedshader"))
                {
                    return originalShaderText.Contains("3c86a33ed39d8494baf6556519cac089"); // guid for GPUI Stackable for Better Shaders
                }
                else
                {
                    return originalShaderText.Contains("GPUInstancerInclude.cginc");
                }
            }
#endif
            return false;
        }

        #region Auto Shader Conversion Helper Methods
#if UNITY_EDITOR
        private static string GetShaderIncludePath(string originalAssetPath, bool createInDefaultFolder, out string newAssetPath)
        {
            newAssetPath = originalAssetPath;
            if (createInDefaultFolder)
            {
                if (!System.IO.Directory.Exists(GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.SHADERS_PATH))
                    System.IO.Directory.CreateDirectory(GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.SHADERS_PATH);

                string[] oapSplit = originalAssetPath.Replace('\\', '/').Split('/');
                newAssetPath = GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.SHADERS_PATH + oapSplit[oapSplit.Length - 1];
            }

            // Generated shaders are project assets, while the instancing include
            // remains a package asset.  A package-absolute include is stable for
            // both embedded and registry installations and avoids fragile
            // relative paths when the source shader lives in another package.
            return GPUInstancerConstants.GetPackageAssetPath("Shaders/Include/GPUInstancerInclude.cginc");
        }

        private static string ReplaceShaderTextCGProgram(string includePath, string newShaderText, bool doubleEscape)
        {
            // For vertex/fragment and surface shaders
            #region CGPROGRAM
            int lastIndex = 0;
            string searchStart = "CGPROGRAM";
            string additionTextStart = "\n#include \"UnityCG.cginc\"\n#include \"" + includePath + "\"\n#pragma instancing_options procedural:setupGPUI\n#pragma multi_compile_instancing";
            if (doubleEscape)
                additionTextStart = additionTextStart.Replace("\n", "\\n").Replace("\"", "\\\"");
            string searchEnd = "ENDCG";
            string additionTextEnd = "";//"#include \"" + includePath + "\"\n";

            int foundIndex = -1;
            while (true)
            {
                foundIndex = newShaderText.IndexOf(searchStart, lastIndex);
                if (foundIndex == -1)
                    break;
                lastIndex = foundIndex + searchStart.Length + additionTextStart.Length + 1;

                newShaderText = newShaderText.Substring(0, foundIndex + searchStart.Length) + additionTextStart + newShaderText.Substring(foundIndex + searchStart.Length, newShaderText.Length - foundIndex - searchStart.Length);

                foundIndex = newShaderText.IndexOf(searchEnd, lastIndex);
                lastIndex = foundIndex + searchStart.Length + additionTextEnd.Length + 1;
                newShaderText = newShaderText.Substring(0, foundIndex) + additionTextEnd + newShaderText.Substring(foundIndex, newShaderText.Length - foundIndex);
            }
            #endregion CGPROGRAM

            return newShaderText;
        }

        private static string ReplaceShaderTextHLSLProgram(string includePath, string newShaderText, bool doubleEscape, bool forceAlphaTest = false, string startAddition = null)
        {
            // For SRP Shaders
            #region HLSLPROGRAM
            int lastIndex = 0;
            string searchStart = "HLSLPROGRAM";
            string additionTextStart = "\n#include \"" + includePath + "\"\n#pragma instancing_options procedural:setupGPUI\n#pragma multi_compile_instancing\n";
            additionTextStart = "\n#include \"Packages/com.bluewhitebow.fraxinus-rp/ShaderLibrary/Core.hlsl\"\n" + additionTextStart;
            if (forceAlphaTest)
                additionTextStart = "\n#define _ALPHATEST_ON" + additionTextStart;
            if (!string.IsNullOrEmpty(startAddition))
                additionTextStart = startAddition + additionTextStart;
            if (doubleEscape)
                additionTextStart = additionTextStart.Replace("\n", "\\n").Replace("\"", "\\\"");
            string searchEnd = "ENDHLSL";
            string additionTextEnd = "";
            if (doubleEscape)
                additionTextEnd = additionTextEnd.Replace("\n", "\\n").Replace("\"", "\\\"");
            int foundIndex = -1;
            while (true)
            {
                foundIndex = newShaderText.IndexOf(searchStart, lastIndex);
                if (foundIndex == -1)
                    break;
                lastIndex = foundIndex + searchStart.Length + additionTextStart.Length + 1;

                newShaderText = newShaderText.Substring(0, foundIndex + searchStart.Length) + additionTextStart + newShaderText.Substring(foundIndex + searchStart.Length, newShaderText.Length - foundIndex - searchStart.Length);

                foundIndex = newShaderText.IndexOf(searchEnd, lastIndex);
                lastIndex = foundIndex + searchStart.Length + additionTextEnd.Length + 1;
                newShaderText = newShaderText.Substring(0, foundIndex) + additionTextEnd + newShaderText.Substring(foundIndex, newShaderText.Length - foundIndex);
            }
            #endregion HLSLPROGRAM

            return newShaderText;
        }

        private static Shader SaveInstancedShader(string originalAssetPath, bool useOriginal, string extension, string newShaderText, Shader originalShader, string newShaderName)
        {
            string originalFileName = System.IO.Path.GetFileName(originalAssetPath);
            bool packageAsset = GPUInstancerConstants.IsPackageAssetPath(originalAssetPath);
            string newAssetPath;
            if (packageAsset)
            {
                // Never write into Packages/.  GetShaderIncludePath normally
                // redirects package sources already, but keep this guard here as
                // the final boundary for callers and future conversion paths.
                string generatedDirectory = GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.SHADERS_PATH;
                if (!System.IO.Directory.Exists(generatedDirectory))
                    System.IO.Directory.CreateDirectory(generatedDirectory);
                string generatedFileName = originalFileName.EndsWith("_GPUI" + extension, StringComparison.OrdinalIgnoreCase)
                    ? originalFileName
                    : originalFileName.Replace(extension, "_GPUI" + extension);
                newAssetPath = generatedDirectory + generatedFileName;
                useOriginal = false;
            }
            else
            {
                if (useOriginal)
                    newAssetPath = originalAssetPath;
                else
                {
                    string generatedFileName = originalFileName.EndsWith("_GPUI" + extension, StringComparison.OrdinalIgnoreCase)
                        ? originalFileName
                        : originalFileName.Replace(extension, "_GPUI" + extension);
                    newAssetPath = originalAssetPath.Replace(originalFileName, generatedFileName);
                }
            }

            if (GPUInstancerConstants.IsPackageAssetPath(newAssetPath))
                throw new InvalidOperationException("Refusing to write generated shader into package path: " + newAssetPath);

            string parentDirectory = System.IO.Path.GetDirectoryName(newAssetPath);
            if (!string.IsNullOrEmpty(parentDirectory) && !System.IO.Directory.Exists(parentDirectory))
                System.IO.Directory.CreateDirectory(parentDirectory);

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(newShaderText);
            VersionControlCheckout(newAssetPath);
            System.IO.FileStream fs = System.IO.File.Create(newAssetPath);
            fs.Write(bytes, 0, bytes.Length);
            fs.Close();
            //System.IO.File.WriteAllText(newAssetPath, newShaderText);
            EditorUtility.DisplayProgressBar("GPU Instancer Shader Conversion", "Importing instanced shader for " + originalShader.name, 0.8f);
            AssetDatabase.ImportAsset(newAssetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();

            Shader instancedShader = AssetDatabase.LoadAssetAtPath<Shader>(newAssetPath);
            if (instancedShader == null)
                instancedShader = Shader.Find(newShaderName);

            return instancedShader;
        }
#endif // UNITY_EDITOR
#endregion Auto Shader Conversion Helper Methods

        public static Shader CreateInstancedShader(Shader originalShader, bool useOriginal = false)
        {
#if UNITY_EDITOR
            try
            {
                if (originalShader == null || originalShader.name == GPUInstancerConstants.SHADER_UNITY_INTERNAL_ERROR)
                {
                    Debug.LogError("Can not find shader! Please make sure that the material has a shader assigned.");
                    return null;
                }
                Shader originalShaderRef = Shader.Find(originalShader.name);
                string originalAssetPath = AssetDatabase.GetAssetPath(originalShaderRef);
                string extension = System.IO.Path.GetExtension(originalAssetPath);

                // A package shader cannot be rewritten in place.  Treat an
                // in-place request as a normal generated copy so the resulting
                // asset receives a unique shader name and remains project-owned.
                if (GPUInstancerConstants.IsPackageAssetPath(originalAssetPath))
                    useOriginal = false;

                // can not work with ShaderGraph or other non shader code
                if (extension != ".shader")
                {
                    if (extension.EndsWith("pack"))
                        return CreateInstancedShaderPack(originalShader, originalAssetPath);
                    else
                        return null;
                }

                EditorUtility.DisplayProgressBar("GPU Instancer Shader Conversion", "Creating instanced shader for " + originalShader.name + ". Please wait...", 0.1f);

                #region Remove Existing procedural setup
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                using (System.IO.StreamReader sr = new System.IO.StreamReader(originalAssetPath))
                {
                    while (!sr.EndOfStream)
                    {
                        string line = sr.ReadLine();
                        if (!line.Contains("#pragma instancing_options")
                            && !line.Contains("GPUInstancerInclude.cginc")
                            //&& !line.Contains("#include \"UnityCG.cginc\"")
                            && !line.Contains("#pragma multi_compile_instancing"))
                            sb.Append(line + "\n");
                    }
                }
                string originalShaderText = sb.ToString();
                #endregion Remove Existing procedural setup

                // Keep every generated (non-original) shader in the project data
                // root.  Package sources must also be redirected there even when
                // a caller requests an in-place regeneration, because Packages/
                // is immutable.  Authored Assets/ shaders passed with
                // useOriginal=true retain their explicit in-place semantics.
                bool createInDefaultFolder = !useOriginal || originalAssetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);

                // Preserve relative includes when copying a Fraxinus package shader to Assets.
                bool hasPipelineRelativeIncludes = originalAssetPath.StartsWith(
                    "Packages/com.bluewhitebow.fraxinus-rp/", StringComparison.OrdinalIgnoreCase);
                string pipelineIncludeAddition = System.IO.Path.GetDirectoryName(originalAssetPath).Replace('\\', '/') + "/";

                EditorUtility.DisplayProgressBar("GPU Instancer Shader Conversion", "Creating instanced shader for " + originalShader.name + ".  Please wait...", 0.5f);

                string newShaderName = useOriginal ? "" : "GPUInstancer/" + originalShader.name;
                string newShaderText = useOriginal ? originalShaderText.Replace("\r\n", "\n") : originalShaderText.Replace("\r\n", "\n").Replace("\"" + originalShader.name + "\"", "\"" + newShaderName + "\"");

                string includePath = GetShaderIncludePath(originalAssetPath, createInDefaultFolder, out originalAssetPath);

                // For vertex/fragment and surface shaders
                newShaderText = ReplaceShaderTextCGProgram(includePath, newShaderText, false);

                if (hasPipelineRelativeIncludes && createInDefaultFolder)
                {
                    newShaderText = System.Text.RegularExpressions.Regex.Replace(newShaderText,
                        "(#include(?:_with_pragmas)?\\s+\")([^\"]+)(\")", match =>
                        {
                            string path = match.Groups[2].Value;
                            if (path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                                path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                                !System.IO.File.Exists(pipelineIncludeAddition + path))
                                return match.Value;
                            return match.Groups[1].Value + pipelineIncludeAddition + path + match.Groups[3].Value;
                        });
                }

                // For SRP Shaders
                newShaderText = ReplaceShaderTextHLSLProgram(includePath, newShaderText, false, originalShader.name == "Ultra Render Pipeline/Nature/SpeedTree8", originalShader.name == GPUInstancerConstants.SHADER_UNITY_SPEED_TREE_URP ?
                    "\n#if defined(GEOM_TYPE_FROND) || defined(GEOM_TYPE_LEAF) || defined(GEOM_TYPE_FACING_LEAF)\n#define _ALPHATEST_ON\n#endif" : "");

                Shader instancedShader = SaveInstancedShader(originalAssetPath, useOriginal, extension, newShaderText, originalShader, newShaderName);

                if (instancedShader != null)
                    Debug.Log("Generated GPUI support enabled version for shader: " + originalShader.name, instancedShader);
                return instancedShader;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
#endif
#if !UNITY_EDITOR
            return null;
#endif
        }

        public static Shader CreateInstancedShaderPack(Shader originalShader, string originalAssetPath)
        {
#if UNITY_EDITOR
            EditorUtility.DisplayProgressBar("GPU Instancer Shader Conversion", "Creating instanced shader for " + originalShader.name + ". Please wait...", 0.1f);

            string extension = System.IO.Path.GetExtension(originalAssetPath);

            #region Remove Existing procedural setup
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            using (System.IO.StreamReader sr = new System.IO.StreamReader(originalAssetPath))
            {
                while (!sr.EndOfStream)
                {
                    string line = sr.ReadLine();
                    string[] lines = line.Split(new string[] { "\\n" }, StringSplitOptions.None);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        line = lines[i];
                        if (!line.Contains("#pragma instancing_options")
                        && !line.Contains("GPUInstancerInclude.cginc")
                        //&& !line.Contains("#include \"UnityCG.cginc\"")
                        && !line.Contains("#pragma multi_compile_instancing"))
                        {
                            if (i == lines.Length - 1)
                                sb.Append(line);
                            else
                                sb.Append(line + "\\n");
                        }
                    }
                }
            }
            string originalShaderText = sb.ToString();
            #endregion Remove Existing procedural setup

            // Shader-pack sources are always copied to the project data root;
            // importing an archive or package asset must never create files under
            // Packages/.
            bool createInDefaultFolder = true;

            EditorUtility.DisplayProgressBar("GPU Instancer Shader Conversion", "Creating instanced shader for " + originalShader.name + ".  Please wait...", 0.5f);

            string newShaderName = "GPUInstancer/" + originalShader.name;
            string newShaderText = originalShaderText.Replace("\r\n", "\n").Replace(originalShader.name, newShaderName);

            string includePath = GetShaderIncludePath(originalAssetPath, createInDefaultFolder, out originalAssetPath);

            // For vertex/fragment and surface shaders
            newShaderText = ReplaceShaderTextCGProgram(includePath, newShaderText, true);

            // For SRP Shaders
            newShaderText = ReplaceShaderTextHLSLProgram(includePath, newShaderText, true);

            Shader instancedShader = SaveInstancedShader(originalAssetPath, false, extension, newShaderText, originalShader, newShaderName);

            if (instancedShader != null)
                Debug.Log("Generated GPUI support enabled version for shader: " + originalShader.name, instancedShader);
            EditorUtility.ClearProgressBar();

            return instancedShader;
#else
            return null;
#endif
        }
        #endregion Shader Functions

        #region Extensions

        public static T[] MirrorAndFlatten<T>(this T[,] array2D)
        {
            T[] resultArray1D = new T[array2D.GetLength(0) * array2D.GetLength(1)];

            for (int y = 0; y < array2D.GetLength(0); y++)
            {
                for (int x = 0; x < array2D.GetLength(1); x++)
                {
                    resultArray1D[x + y * array2D.GetLength(0)] = array2D[y, x];
                }
            }

            return resultArray1D;
        }

        public static T[] MirrorAndFlatten<T>(this T[,] array2D, int xBase, int yBase, int width, int height)
        {
            T[] resultArray1D = new T[width * height];

            for (int y = 0; y < width; y++)
            {
                for (int x = 0; x < height; x++)
                {
                    resultArray1D[x + y * width] = array2D[y + yBase, x + xBase];
                }
            }

            return resultArray1D;
        }

        /// <summary>
        ///   <para>Returns a random float number between and min [inclusive] and max [inclusive] (Read Only).</para>
        /// </summary>
        /// <param name="min"></param>
        /// <param name="max"></param>
        public static float Range(this System.Random prng, float min, float max)
        {
            return (float)(min + (prng.NextDouble() * (max - min)));
        }

        public static void Matrix4x4ToFloatArray(this Matrix4x4 matrix4x4, float[] floatArray)
        {
            floatArray[0] = matrix4x4[0, 0];
            floatArray[1] = matrix4x4[1, 0];
            floatArray[2] = matrix4x4[2, 0];
            floatArray[3] = matrix4x4[3, 0];
            floatArray[4] = matrix4x4[0, 1];
            floatArray[5] = matrix4x4[1, 1];
            floatArray[6] = matrix4x4[2, 1];
            floatArray[7] = matrix4x4[3, 1];
            floatArray[8] = matrix4x4[0, 2];
            floatArray[9] = matrix4x4[1, 2];
            floatArray[10] = matrix4x4[2, 2];
            floatArray[11] = matrix4x4[3, 2];
            floatArray[12] = matrix4x4[0, 3];
            floatArray[13] = matrix4x4[1, 3];
            floatArray[14] = matrix4x4[2, 3];
            floatArray[15] = matrix4x4[3, 3];
        }

        public static Matrix4x4 Matrix4x4FromString(string matrixStr)
        {
            Matrix4x4 matrix4x4 = new Matrix4x4();
            string[] floatStrArray = matrixStr.Split(';');
            for (int i = 0; i < floatStrArray.Length; i++)
            {
                matrix4x4[i / 4, i % 4] = float.Parse(floatStrArray[i], System.Globalization.CultureInfo.InvariantCulture);
            }
            return matrix4x4;
        }

        public static string Matrix4x4ToString(Matrix4x4 matrix4x4)
        {
            string matrix4X4String = matrix4x4.ToString().Replace("\n", ";").Replace("\t", ";");
            matrix4X4String = matrix4X4String.Substring(0, matrix4X4String.Length - 1);
            return matrix4X4String;
        }

        public static void SetMatrix4x4ToTransform(this Transform transform, Matrix4x4 matrix)
        {
            transform.position = matrix.GetColumn(3);
            transform.localScale = new Vector3(
                                matrix.GetColumn(0).magnitude,
                                matrix.GetColumn(1).magnitude,
                                matrix.GetColumn(2).magnitude
                                );
            transform.rotation = Quaternion.LookRotation(matrix.GetColumn(2), matrix.GetColumn(1));
        }

        public static float[] Matrix4x4ToFloatArray(this Matrix4x4 matrix4x4)
        {
            float[] floatArray = new float[16];

            Matrix4x4ToFloatArray(matrix4x4, floatArray);

            return floatArray;
        }

        #region Compute Buffer Management
        public static void SetDataSingle(this ComputeBuffer computeBuffer, Matrix4x4[] data, int managedBufferStartIndex, int computeBufferStartIndex)
        {
#if UNITY_2017_1_OR_NEWER && !UNITY_ANDROID
            computeBuffer.SetData(data, managedBufferStartIndex, computeBufferStartIndex, 1);
#else
            if (GPUInstancerConstants.computeBufferSetDataPartial != null)
            {
                GPUInstancerConstants.computeBufferSetDataPartial.SetBuffer(GPUInstancerConstants.computeBufferSetDataSingleKernelId, 
                    GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, computeBuffer);
                GPUInstancerConstants.computeBufferSetDataPartial.SetFloats(GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_DATA_TO_SET,
                    data[managedBufferStartIndex].m00,
                    data[managedBufferStartIndex].m10,
                    data[managedBufferStartIndex].m20,
                    data[managedBufferStartIndex].m30,
                    data[managedBufferStartIndex].m01,
                    data[managedBufferStartIndex].m11,
                    data[managedBufferStartIndex].m21,
                    data[managedBufferStartIndex].m31,
                    data[managedBufferStartIndex].m02,
                    data[managedBufferStartIndex].m12,
                    data[managedBufferStartIndex].m22,
                    data[managedBufferStartIndex].m32,
                    data[managedBufferStartIndex].m03,
                    data[managedBufferStartIndex].m13,
                    data[managedBufferStartIndex].m23,
                    data[managedBufferStartIndex].m33
                    );
                GPUInstancerConstants.computeBufferSetDataPartial.SetInt(GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_COMPUTE_BUFFER_START_INDEX, computeBufferStartIndex);
                DispatchCompute(GPUInstancerConstants.computeBufferSetDataPartial, GPUInstancerConstants.computeBufferSetDataSingleKernelId, 1, 1, 1);
            }
#endif
        }

        public static void SetDataPartial(this ComputeBuffer computeBuffer, Matrix4x4[] data, int managedBufferStartIndex, int computeBufferStartIndex, int count, ComputeBuffer managedBuffer = null, Matrix4x4[] managedData = null)
        {
            if (managedBufferStartIndex == 0 && computeBufferStartIndex == 0 && count == data.Length)
            {
                computeBuffer.SetData(data);
                return;
            }

#if UNITY_2017_1_OR_NEWER && !UNITY_ANDROID
            computeBuffer.SetData(data, managedBufferStartIndex, computeBufferStartIndex, count);
#else
            if (count == 1)
            {
                SetDataSingle(computeBuffer, data, managedBufferStartIndex, computeBufferStartIndex);
            }
            else
            {
                if (GPUInstancerConstants.computeBufferSetDataPartial != null)
                {
                    Array.Copy(data, managedBufferStartIndex, managedData, 0, count);
                    managedBuffer.SetData(managedData);

                    GPUInstancerConstants.computeBufferSetDataPartial.SetBuffer(GPUInstancerConstants.computeBufferSetDataPartialKernelId, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, computeBuffer);
                    GPUInstancerConstants.computeBufferSetDataPartial.SetBuffer(GPUInstancerConstants.computeBufferSetDataPartialKernelId, GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_MANAGED_BUFFER_DATA, managedBuffer);
                    GPUInstancerConstants.computeBufferSetDataPartial.SetInt(GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_COMPUTE_BUFFER_START_INDEX, computeBufferStartIndex);
                    GPUInstancerConstants.computeBufferSetDataPartial.SetInt(GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_COUNT, count);
                    DispatchCompute(GPUInstancerConstants.computeBufferSetDataPartial, GPUInstancerConstants.computeBufferSetDataPartialKernelId, Mathf.CeilToInt(count / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
                }
            }
#endif
        }

        public static void CopyComputeBuffer(this ComputeBuffer computeBuffer, int computeBufferStartIndex, int count, ComputeBuffer managedBuffer)
        {
            GPUInstancerConstants.computeBufferSetDataPartial.SetBuffer(GPUInstancerConstants.computeBufferSetDataPartialKernelId, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, computeBuffer);
            GPUInstancerConstants.computeBufferSetDataPartial.SetBuffer(GPUInstancerConstants.computeBufferSetDataPartialKernelId, GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_MANAGED_BUFFER_DATA, managedBuffer);
            GPUInstancerConstants.computeBufferSetDataPartial.SetInt(GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_COMPUTE_BUFFER_START_INDEX, computeBufferStartIndex);
            GPUInstancerConstants.computeBufferSetDataPartial.SetInt(GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_COUNT, count);
            DispatchCompute(GPUInstancerConstants.computeBufferSetDataPartial, GPUInstancerConstants.computeBufferSetDataPartialKernelId, Mathf.CeilToInt(count / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
        }

        public static ComputeBuffer MergeComputeBuffers(this ComputeBuffer computeBuffer, ComputeBuffer bufferToMerge, bool releaseMergedBuffers)
        {
            // create a new buffer with the combined size
            ComputeBuffer mergedBuffer = new ComputeBuffer(computeBuffer.count + bufferToMerge.count, computeBuffer.stride);

            // Set the first buffers's data to the new buffer
            GPUInstancerConstants.computeBufferSetDataPartial.SetBuffer(GPUInstancerConstants.computeBufferSetDataPartialKernelId, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, mergedBuffer);
            GPUInstancerConstants.computeBufferSetDataPartial.SetBuffer(GPUInstancerConstants.computeBufferSetDataPartialKernelId, GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_MANAGED_BUFFER_DATA, computeBuffer);
            GPUInstancerConstants.computeBufferSetDataPartial.SetInt(GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_COMPUTE_BUFFER_START_INDEX, 0);
            GPUInstancerConstants.computeBufferSetDataPartial.SetInt(GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_COUNT, computeBuffer.count);
            DispatchCompute(GPUInstancerConstants.computeBufferSetDataPartial, GPUInstancerConstants.computeBufferSetDataPartialKernelId, Mathf.CeilToInt(computeBuffer.count / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);

            // Set the second buffers's data to the new buffer
            GPUInstancerConstants.computeBufferSetDataPartial.SetBuffer(GPUInstancerConstants.computeBufferSetDataPartialKernelId, GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, mergedBuffer);
            GPUInstancerConstants.computeBufferSetDataPartial.SetBuffer(GPUInstancerConstants.computeBufferSetDataPartialKernelId, GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_MANAGED_BUFFER_DATA, bufferToMerge);
            GPUInstancerConstants.computeBufferSetDataPartial.SetInt(GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_COMPUTE_BUFFER_START_INDEX, computeBuffer.count);
            GPUInstancerConstants.computeBufferSetDataPartial.SetInt(GPUInstancerConstants.SetDataKernelProperties.BUFFER_PARAMETER_COUNT, bufferToMerge.count);
            DispatchCompute(GPUInstancerConstants.computeBufferSetDataPartial, GPUInstancerConstants.computeBufferSetDataPartialKernelId, Mathf.CeilToInt(bufferToMerge.count / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);

            if (releaseMergedBuffers)
            {
                computeBuffer.Release();
                bufferToMerge.Release();
            }
            return mergedBuffer;
        }
        #endregion Compute Buffer Management

        // Dispatch Compute Shader to update positions
        public static void SetGlobalPositionOffset(GPUInstancerManager manager, Vector3 offsetPosition)
        {
            if (manager == null || manager.isDisposed)
                return;
            if (manager.runtimeDataList != null)
            {
                manager.SetGlobalPositionOffset(offsetPosition);
                foreach (GPUInstancerRuntimeData runtimeData in manager.runtimeDataList)
                {

                    if (runtimeData == null)
                    {
                        Debug.LogWarning("SetGlobalPositionOffset called before manager initialization. Offset will not be applied.");
                        continue;
                    }

                    if (runtimeData.instanceCount == 0 || runtimeData.bufferSize == 0)
                        continue;

                    if (runtimeData.transformationMatrixVisibilityBuffer == null)
                    {
                        Debug.LogWarning("SetGlobalPositionOffset called before buffers are initialized. Offset will not be applied.");
                        continue;
                    }

                    GPUInstancerConstants.computeRuntimeModification.SetBuffer(GPUInstancerConstants.computeBufferTransformOffsetId,
                        GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, runtimeData.transformationMatrixVisibilityBuffer);
                    GPUInstancerConstants.computeRuntimeModification.SetInt(
                        GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, runtimeData.bufferSize);
                    GPUInstancerConstants.computeRuntimeModification.SetVector(
                        GPUInstancerConstants.RuntimeModificationKernelProperties.BUFFER_PARAMETER_POSITION_OFFSET, offsetPosition);

                    DispatchCompute(GPUInstancerConstants.computeRuntimeModification, GPUInstancerConstants.computeBufferTransformOffsetId,
                        Mathf.CeilToInt(runtimeData.bufferSize / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
                }
            }
        }

        public static void SetGlobalMatrixOffset(GPUInstancerManager manager, Matrix4x4 offsetMatrix)
        {
            if (manager == null || manager.isDisposed)
                return;
            if (manager.runtimeDataList != null)
            {
                manager.SetGlobalMatrixOffset(offsetMatrix);
                foreach (GPUInstancerRuntimeData runtimeData in manager.runtimeDataList)
                {

                    if (runtimeData == null)
                    {
                        Debug.LogWarning("SetGlobalMatrixOffset called before manager initialization. Offset will not be applied.");
                        continue;
                    }

                    if (runtimeData.instanceCount == 0 || runtimeData.bufferSize == 0)
                        continue;

                    if (runtimeData.transformationMatrixVisibilityBuffer == null)
                    {
                        Debug.LogWarning("SetGlobalMatrixOffset called before buffers are initialized. Offset will not be applied.");
                        continue;
                    }

                    GPUInstancerConstants.computeRuntimeModification.SetBuffer(GPUInstancerConstants.computeBufferMatrixOffsetId,
                        GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, runtimeData.transformationMatrixVisibilityBuffer);
                    GPUInstancerConstants.computeRuntimeModification.SetInt(
                        GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, runtimeData.bufferSize);
                    GPUInstancerConstants.computeRuntimeModification.SetMatrix(
                        GPUInstancerConstants.RuntimeModificationKernelProperties.BUFFER_PARAMETER_MATRIX_OFFSET, offsetMatrix);

                    DispatchCompute(GPUInstancerConstants.computeRuntimeModification, GPUInstancerConstants.computeBufferMatrixOffsetId,
                        Mathf.CeilToInt(runtimeData.bufferSize / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
                }
            }
        }

        #region RemoveInstancesInsideBounds
        // Dispatch Compute Shader to remove instances inside bounds
        public static void RemoveInstancesInsideBounds(ComputeBuffer instanceDataBuffer, Vector3 center, Vector3 extents, float offset)
        {
            if (instanceDataBuffer != null)
            {
                GPUInstancerConstants.computeRuntimeModification.SetBuffer(GPUInstancerConstants.computeRemoveInsideBoundsId,
                    GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, instanceDataBuffer);
                GPUInstancerConstants.computeRuntimeModification.SetInt(
                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, instanceDataBuffer.count);
                GPUInstancerConstants.computeRuntimeModification.SetVector(
                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BOUNDS_CENTER, center);
                GPUInstancerConstants.computeRuntimeModification.SetVector(
                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BOUNDS_EXTENTS, extents + Vector3.one * offset);

                DispatchCompute(GPUInstancerConstants.computeRuntimeModification, GPUInstancerConstants.computeRemoveInsideBoundsId,
                    Mathf.CeilToInt(instanceDataBuffer.count / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
            }
        }

        //Dispatch Compute Shader to remove instances inside box collider
        public static void RemoveInstancesInsideBoxCollider(ComputeBuffer instanceDataBuffer, BoxCollider boxCollider, float offset)
        {
            if (instanceDataBuffer != null)
            {
                GPUInstancerConstants.computeRuntimeModification.SetBuffer(GPUInstancerConstants.computeRemoveInsideBoxId,
                    GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, instanceDataBuffer);
                GPUInstancerConstants.computeRuntimeModification.SetInt(
                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, instanceDataBuffer.count);
                GPUInstancerConstants.computeRuntimeModification.SetVector(
                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BOUNDS_CENTER, boxCollider.center);
                GPUInstancerConstants.computeRuntimeModification.SetVector(
                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BOUNDS_EXTENTS, boxCollider.size / 2 + Vector3.one * offset);

#if UNITY_2017_3_OR_NEWER
                GPUInstancerConstants.computeRuntimeModification.SetMatrix(
                    GPUInstancerConstants.RuntimeModificationKernelProperties.BUFFER_PARAMETER_MODIFIER_TRANSFORM, boxCollider.transform.localToWorldMatrix);
#else
                GPUInstancerConstants.computeRuntimeModification.SetFloats(
                    GPUInstancerConstants.RuntimeModificationKernelProperties.BUFFER_PARAMETER_MODIFIER_TRANSFORM, boxCollider.transform.localToWorldMatrix.Matrix4x4ToFloatArray());
#endif
                DispatchCompute(GPUInstancerConstants.computeRuntimeModification, GPUInstancerConstants.computeRemoveInsideBoxId,
                    Mathf.CeilToInt(instanceDataBuffer.count / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
            }
        }

        // Dispatch Compute Shader to remove instances inside sphere collider
        public static void RemoveInstancesInsideSphereCollider(ComputeBuffer instanceDataBuffer, SphereCollider sphereCollider, float offset)
        {
            if (instanceDataBuffer != null)
            {
                GPUInstancerConstants.computeRuntimeModification.SetBuffer(GPUInstancerConstants.computeRemoveInsideSphereId,
                    GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, instanceDataBuffer);
                GPUInstancerConstants.computeRuntimeModification.SetInt(
                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, instanceDataBuffer.count);
                GPUInstancerConstants.computeRuntimeModification.SetVector(
                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BOUNDS_CENTER, sphereCollider.center + sphereCollider.transform.position);
                GPUInstancerConstants.computeRuntimeModification.SetFloat(
                    GPUInstancerConstants.RuntimeModificationKernelProperties.BUFFER_PARAMETER_MODIFIER_RADIUS,
                    sphereCollider.radius * Mathf.Max(Mathf.Max(sphereCollider.transform.localScale.x, sphereCollider.transform.localScale.y), sphereCollider.transform.localScale.z) + offset);

                DispatchCompute(GPUInstancerConstants.computeRuntimeModification, GPUInstancerConstants.computeRemoveInsideSphereId,
                    Mathf.CeilToInt(instanceDataBuffer.count / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
            }
        }

        // Dispatch Compute Shader to remove instances inside capsule collider
        public static void RemoveInstancesInsideCapsuleCollider(ComputeBuffer instanceDataBuffer, CapsuleCollider capsuleCollider, float offset)
        {
            if (instanceDataBuffer != null)
            {
                GPUInstancerConstants.computeRuntimeModification.SetBuffer(GPUInstancerConstants.computeRemoveInsideCapsuleId,
                    GPUInstancerConstants.VisibilityKernelPoperties.INSTANCE_DATA_BUFFER, instanceDataBuffer);
                GPUInstancerConstants.computeRuntimeModification.SetInt(
                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BUFFER_SIZE, instanceDataBuffer.count);
                GPUInstancerConstants.computeRuntimeModification.SetVector(
                    GPUInstancerConstants.VisibilityKernelPoperties.BUFFER_PARAMETER_BOUNDS_CENTER, capsuleCollider.center);
                GPUInstancerConstants.computeRuntimeModification.SetFloat(
                    GPUInstancerConstants.RuntimeModificationKernelProperties.BUFFER_PARAMETER_MODIFIER_RADIUS,
                    capsuleCollider.radius * Mathf.Max(Mathf.Max(
                        capsuleCollider.direction == 0 ? 0 : capsuleCollider.transform.localScale.x,
                        capsuleCollider.direction == 1 ? 0 : capsuleCollider.transform.localScale.y),
                        capsuleCollider.direction == 2 ? 0 : capsuleCollider.transform.localScale.z) + offset);
                GPUInstancerConstants.computeRuntimeModification.SetFloat(
                    GPUInstancerConstants.RuntimeModificationKernelProperties.BUFFER_PARAMETER_MODIFIER_HEIGHT,
                    capsuleCollider.height * (
                    capsuleCollider.direction == 0 ? capsuleCollider.transform.localScale.x : 0 +
                    capsuleCollider.direction == 1 ? capsuleCollider.transform.localScale.y : 0 +
                    capsuleCollider.direction == 2 ? capsuleCollider.transform.localScale.z : 0
                    ));

#if UNITY_2017_3_OR_NEWER
                GPUInstancerConstants.computeRuntimeModification.SetMatrix(
                    GPUInstancerConstants.RuntimeModificationKernelProperties.BUFFER_PARAMETER_MODIFIER_TRANSFORM, capsuleCollider.transform.localToWorldMatrix);
#else
                GPUInstancerConstants.computeRuntimeModification.SetFloats(
                    GPUInstancerConstants.RuntimeModificationKernelProperties.BUFFER_PARAMETER_MODIFIER_TRANSFORM, capsuleCollider.transform.localToWorldMatrix.Matrix4x4ToFloatArray());
#endif
                GPUInstancerConstants.computeRuntimeModification.SetVector(
                    GPUInstancerConstants.RuntimeModificationKernelProperties.BUFFER_PARAMETER_MODIFIER_AXIS,
                    capsuleCollider.direction == 0 ? Vector3.right : (capsuleCollider.direction == 1 ? Vector3.up : Vector3.forward));

                DispatchCompute(GPUInstancerConstants.computeRuntimeModification, GPUInstancerConstants.computeRemoveInsideCapsuleId,
                    Mathf.CeilToInt(instanceDataBuffer.count / GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT), 1, 1);
            }
        }
        #endregion RemoveInstancesInsideBounds

        #region NoGameObject methods
        public static void InitializeWithMatrix4x4Array(GPUInstancerPrefabManager prefabManager, GPUInstancerPrefabPrototype prototype, Matrix4x4[] matrix4x4Array)
        {
            if (prefabManager == null || prefabManager.isDisposed)
                return;
            prototype = prefabManager.ResolveRuntimePrototype(prototype) as GPUInstancerPrefabPrototype;
            if (prototype == null)
                return;
            prototype.enableRuntimeModifications = false;
            // initialize runtimeData
            prefabManager.InitializeRuntimeDataAndBuffers(false);
            // find runtimeData for prefab
            GPUInstancerRuntimeData runtimeData = prefabManager.GetRuntimeData(prototype);
            if (runtimeData == null)
            {
                runtimeData = prefabManager.InitializeRuntimeDataForPrefabPrototype(prototype);
                if (runtimeData == null)
                {
                    Debug.LogError("Can not find runtime data for prototype: " + prototype + ". Please check if the prototype was added to the Prefab Manager.");
                    return;
                }
            }
            // set counts to runtimeData
            runtimeData.bufferSize = matrix4x4Array.Length;
            runtimeData.instanceCount = matrix4x4Array.Length;
            // release previously initialized buffers
            ReleaseInstanceBuffers(runtimeData);
            // add tree proxy
            if (prototype.treeType == GPUInstancerTreeType.SpeedTree || prototype.treeType == GPUInstancerTreeType.SpeedTree8 ||
                prototype.treeType == GPUInstancerTreeType.TreeCreatorTree)
                GPUInstancerManager.AddTreeProxy(prototype, runtimeData);
            // initialize buffers
            InitializeGPUBuffer(runtimeData);
            // set data to initialized buffer
            runtimeData.transformationMatrixVisibilityBuffer.SetData(matrix4x4Array);
        }

        public static void InitializePrototype(GPUInstancerPrefabManager prefabManager, GPUInstancerPrefabPrototype prototype, int bufferSize, int instanceCount = 0)
        {
            if (prefabManager == null || prefabManager.isDisposed)
                return;
            prototype = prefabManager.ResolveRuntimePrototype(prototype) as GPUInstancerPrefabPrototype;
            if (prototype == null)
                return;
            prototype.enableRuntimeModifications = false;
            // initialize runtimeData
            prefabManager.InitializeRuntimeDataAndBuffers(false);
            // find runtimeData for prefab
            GPUInstancerRuntimeData runtimeData = prefabManager.GetRuntimeData(prototype);
            if (runtimeData == null)
            {
                runtimeData = prefabManager.InitializeRuntimeDataForPrefabPrototype(prototype);
                if (runtimeData == null)
                {
                    Debug.LogError("Can not find runtime data for prototype: " + prototype + ". Please check if the prototype was added to the Prefab Manager.");
                    return;
                }
            }
            // set counts to runtimeData
            runtimeData.bufferSize = bufferSize;
            runtimeData.instanceCount = instanceCount;
            // release previously initialized buffers
            ReleaseInstanceBuffers(runtimeData);
            // add tree proxy
            if (prototype.treeType == GPUInstancerTreeType.SpeedTree || prototype.treeType == GPUInstancerTreeType.SpeedTree8 ||
                prototype.treeType == GPUInstancerTreeType.TreeCreatorTree)
                GPUInstancerManager.AddTreeProxy(prototype, runtimeData);
            // initialize buffers
            InitializeGPUBuffer(runtimeData);
        }

        public static void UpdateVisibilityBufferWithMatrix4x4Array(GPUInstancerPrefabManager prefabManager, GPUInstancerPrefabPrototype prototype, Matrix4x4[] matrix4x4Array,
            int arrayStartIndex = 0, int bufferStartIndex = 0, int count = 0)
        {
            if (prefabManager == null || prefabManager.isDisposed)
                return;
            // find runtimeData for prefab
            GPUInstancerRuntimeData runtimeData = prefabManager.GetRuntimeData(prototype, true);
            if (runtimeData == null)
                return;
            if (runtimeData.bufferSize == 0)
            {
                Debug.LogError("Can not find runtime data for prototype: " + prototype + ". Please check if the prototype was added to the Prefab Manager and the initialize method was called before update.");
                return;
            }

            // set data to buffer
#if UNITY_2017_1_OR_NEWER
            if (count > 0)
                runtimeData.transformationMatrixVisibilityBuffer.SetData(matrix4x4Array, arrayStartIndex, bufferStartIndex, count);
            else
                runtimeData.transformationMatrixVisibilityBuffer.SetData(matrix4x4Array);
#else
            runtimeData.transformationMatrixVisibilityBuffer.SetData(matrix4x4Array);
#endif
        }

#if UNITY_2019_1_OR_NEWER
        public static void UpdateVisibilityBufferWithNativeArray<T>(GPUInstancerPrefabManager prefabManager, GPUInstancerPrefabPrototype prototype, NativeArray<T> float4x4Array,
            int arrayStartIndex = 0, int bufferStartIndex = 0, int count = 0) where T : struct
        {
            if (prefabManager == null || prefabManager.isDisposed)
                return;
            // find runtimeData for prefab
            GPUInstancerRuntimeData runtimeData = prefabManager.GetRuntimeData(prototype, true);
            if (runtimeData == null)
                return;
            if (runtimeData.bufferSize == 0)
            {
                Debug.LogError("Can not find runtime data for prototype: " + prototype + ". Please check if the prototype was added to the Prefab Manager and the initialize method was called before update.");
                return;
            }
            // set data to buffer

            if (count > 0)
                runtimeData.transformationMatrixVisibilityBuffer.SetData(float4x4Array, arrayStartIndex, bufferStartIndex, count);
            else
                runtimeData.transformationMatrixVisibilityBuffer.SetData(float4x4Array);
        }
#endif
#endregion NoGameObject methods

        public static NativeArray<T> ResizeNativeArray<T>(NativeArray<T> previousArray, int newSize, Allocator allocator) where T : struct
        {
            NativeArray<T> result = new NativeArray<T>(newSize, allocator);
            if (previousArray.IsCreated)
            {
                int count = Math.Min(previousArray.Length, newSize);
                for (int i = 0; i < count; i++)
                {
                    result[i] = previousArray[i];
                }
                previousArray.Dispose();
            }
            return result;
        }

        public static TransformAccessArray ResizeTransformAccessArray(TransformAccessArray previousArray, int newSize)
        {
            TransformAccessArray.Allocate(newSize, -1, out TransformAccessArray result);
            Transform[] transforms = new Transform[newSize];
            if (previousArray.isCreated)
            {
                int count = Math.Min(previousArray.length, newSize);
                for (int i = 0; i < count; i++)
                {
                    transforms[i] = previousArray[i];
                }
                previousArray.Dispose();
            }
            result.SetTransforms(transforms);
            return result;
        }

        public static void DestroyObject(UnityEngine.Object obj)
        {
            if (obj != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(obj);
                else
                    UnityEngine.Object.DestroyImmediate(obj);
            }
        }

        #endregion Extensions

        #region Event System

        private static Dictionary<GPUInstancerEventType, UnityEvent> _eventDictionary;

        public static void StartListening(GPUInstancerEventType eventType, UnityAction listener)
        {
            if (_eventDictionary == null)
                _eventDictionary = new Dictionary<GPUInstancerEventType, UnityEvent>();

            UnityEvent thisEvent = null;
            if (_eventDictionary.TryGetValue(eventType, out thisEvent))
            {
                thisEvent.RemoveListener(listener);
                thisEvent.AddListener(listener);
            }
            else
            {
                thisEvent = new UnityEvent();
                thisEvent.AddListener(listener);
                _eventDictionary.Add(eventType, thisEvent);
            }
        }

        public static void StopListening(GPUInstancerEventType eventType, UnityAction listener)
        {
            if (_eventDictionary == null)
                return;

            UnityEvent thisEvent = null;
            if (_eventDictionary.TryGetValue(eventType, out thisEvent))
            {
                thisEvent.RemoveListener(listener);
            }
        }

        public static void TriggerEvent(GPUInstancerEventType eventType)
        {
            if (_eventDictionary == null || !_eventDictionary.ContainsKey(eventType))
                return;

            UnityEvent thisEvent = null;
            if (_eventDictionary.TryGetValue(eventType, out thisEvent))
                thisEvent.Invoke();
        }

        #endregion Event System

        #region Prefab System
#if UNITY_2018_3_OR_NEWER && UNITY_EDITOR
        public static T AddComponentToPrefab<T>(GameObject prefabObject) where T : Component
        {
            if (prefabObject == null)
                return null;

            PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(prefabObject);

            // A scene instance can report Regular/Variant as its asset type,
            // but only a persistent prefab asset may be opened and saved here.
            // Keep runtime and scene-instance calls in memory so this helper
            // can never mutate a project asset as a side effect of gameplay.
            if (!Application.isPlaying &&
                EditorUtility.IsPersistent(prefabObject) &&
                PrefabUtility.IsPartOfPrefabAsset(prefabObject) &&
                (prefabType == PrefabAssetType.Regular || prefabType == PrefabAssetType.Variant))
            {
                string prefabPath = AssetDatabase.GetAssetPath(prefabObject);
                if (string.IsNullOrEmpty(prefabPath))
                    return null;
                GameObject prefabContents = PrefabUtility.LoadPrefabContents(prefabPath);
                if (prefabContents == null)
                    return null;

                prefabContents.AddComponent<T>();

                PrefabUtility.SaveAsPrefabAsset(prefabContents, prefabPath);
                PrefabUtility.UnloadPrefabContents(prefabContents);

                return prefabObject.GetComponent<T>();
            }

            return prefabObject.AddComponent<T>();
        }

        public static void RemoveComponentFromPrefab<T>(GameObject prefabObject) where T : Component
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefabObject);
            if (string.IsNullOrEmpty(prefabPath))
                return;
            GameObject prefabContents = PrefabUtility.LoadPrefabContents(prefabPath);

            T component = prefabContents.GetComponent<T>();
            if (component)
            {
                GameObject.DestroyImmediate(component, true);
            }

            PrefabUtility.SaveAsPrefabAsset(prefabContents, prefabPath);
            PrefabUtility.UnloadPrefabContents(prefabContents);
        }

        public static GameObject LoadPrefabContents(GameObject prefabObject)
        {
            string prefabPath = AssetDatabase.GetAssetPath(prefabObject);
            if (string.IsNullOrEmpty(prefabPath))
                return null;
            return PrefabUtility.LoadPrefabContents(prefabPath);
        }

        public static void UnloadPrefabContents(GameObject prefabObject, GameObject prefabContents, bool saveChanges = true)
        {
            if (!prefabContents)
                return;
            if (saveChanges)
            {
                string prefabPath = AssetDatabase.GetAssetPath(prefabObject);
                if (string.IsNullOrEmpty(prefabPath))
                    return;
                PrefabUtility.SaveAsPrefabAsset(prefabContents, prefabPath);
            }
            PrefabUtility.UnloadPrefabContents(prefabContents);
            if (prefabContents)
            {
                Debug.Log("Destroying prefab contents...");
                GameObject.DestroyImmediate(prefabContents);
            }
        }

        public static GameObject GetCorrespongingPrefabOfVariant(GameObject variant)
        {
            GameObject result = variant;
            PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(result);
            if (prefabType == PrefabAssetType.Variant)
            {
                if (PrefabUtility.IsPartOfNonAssetPrefabInstance(result))
                    result = GetOutermostPrefabAssetRoot(result);

                prefabType = PrefabUtility.GetPrefabAssetType(result);
                if (prefabType == PrefabAssetType.Variant)
                    result = GetOutermostPrefabAssetRoot(result);
            }
            return result;
        }

        public static GameObject GetOutermostPrefabAssetRoot(GameObject prefabInstance)
        {
            GameObject result = prefabInstance;
            GameObject newPrefabObject = PrefabUtility.GetCorrespondingObjectFromSource(result);
            if (newPrefabObject != null)
            {
                while (newPrefabObject.transform.parent != null)
                    newPrefabObject = newPrefabObject.transform.parent.gameObject;
                result = newPrefabObject;
            }
            return result;
        }

        public static List<GameObject> GetCorrespondingPrefabAssetsOfGameObjects(GameObject[] gameObjects)
        {
            List<GameObject> result = new List<GameObject>();
            PrefabAssetType prefabType;
            GameObject prefabRoot;
            foreach (GameObject go in gameObjects)
            {
                prefabRoot = null;
                if (go != PrefabUtility.GetOutermostPrefabInstanceRoot(go))
                    continue;
                prefabType = PrefabUtility.GetPrefabAssetType(go);
                if (prefabType == PrefabAssetType.Regular)
                    prefabRoot = PrefabUtility.GetCorrespondingObjectFromSource(go);
                else if (prefabType == PrefabAssetType.Variant)
                    prefabRoot = GetCorrespongingPrefabOfVariant(go);

                if (prefabRoot != null)
                    result.Add(prefabRoot);
            }

            return result;
        }
#endif
#endregion Prefab System

        #region Version Control
        public static void VersionControlCheckout(UnityEngine.Object assetObject)
        {
#if UNITY_EDITOR
            if (UnityEditor.VersionControl.Provider.enabled && UnityEditor.VersionControl.Provider.isActive)
            {
                VersionControlCheckout(AssetDatabase.GetAssetPath(assetObject));
            }
#endif
        }

        public static void VersionControlCheckout(string path)
        {
#if UNITY_EDITOR
            if (UnityEditor.VersionControl.Provider.enabled && UnityEditor.VersionControl.Provider.isActive)
            {
                UnityEditor.VersionControl.Asset asset = UnityEditor.VersionControl.Provider.GetAssetByPath(path);
                if (asset == null)
                    return;

                if (UnityEditor.VersionControl.Provider.hasCheckoutSupport)
                {
                    UnityEditor.VersionControl.Task checkOutTask = UnityEditor.VersionControl.Provider.Checkout(asset, UnityEditor.VersionControl.CheckoutMode.Both);
                    checkOutTask.Wait();
                }
            }
#endif
        }
        #endregion Version Control

        #region Platform Dependent

        public static void SetPlatformDependentVariables()
        {
            GPUIPlatform platform = DeterminePlatform();
            matrixHandlingType = GPUInstancerConstants.projectSettings.GetMatrixHandlingType(platform);

            GPUIComputeThreadCount computeThreadCount = GPUInstancerConstants.projectSettings.GetComputeThreadCount(platform);
            switch (computeThreadCount)
            {
                case GPUIComputeThreadCount.x64:
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT = 64;
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT_2D = 8;
                    break;
                case GPUIComputeThreadCount.x128:
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT = 128;
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT_2D = 8;
                    break;
                case GPUIComputeThreadCount.x256:
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT = 256;
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT_2D = 16;
                    break;
                case GPUIComputeThreadCount.x512:
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT = 512;
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT_2D = 16;
                    break;
                case GPUIComputeThreadCount.x1024:
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT = 1024;
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT_2D = 32;
                    break;
                default:
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT = 512;
                    GPUInstancerConstants.COMPUTE_SHADER_THREAD_COUNT_2D = 16;
                    break;
            }
        }

        public static GPUIPlatform DeterminePlatform()
        {
            switch (SystemInfo.graphicsDeviceType)
            {
                case GraphicsDeviceType.OpenGLCore:
                    return GPUIPlatform.OpenGLCore;
                case GraphicsDeviceType.Metal:
                    return GPUIPlatform.Metal;
                case GraphicsDeviceType.OpenGLES3:
                    return GPUIPlatform.GLES31;
                case GraphicsDeviceType.Vulkan:
                    return GPUIPlatform.Vulkan;
                case GraphicsDeviceType.PlayStation4:
                    return GPUIPlatform.PS4;
                case GraphicsDeviceType.XboxOne:
                    return GPUIPlatform.XBoxOne;
                default:
                    return GPUIPlatform.Default;
            }
        }

        public static void UpdatePlatformDependentFiles()
        {
#if UNITY_EDITOR
            SetPlatformDependentVariables();
            // The old implementation rewrote PlatformDefines.hlsl and
            // GPUIPlatformDependent.cginc in-place using GUIDs.  Those GUIDs now
            // resolve to Resources inside this package, which is
            // immutable for registry/git installs.  The package ships stable
            // platform defaults; custom rendering settings are consumed by the
            // managed dispatch code above and no package file is modified here.
            if (GPUInstancerConstants.projectSettings.hasCustomRenderingSettings)
                Debug.Log("Fraxinus GPU Batcher uses managed custom rendering settings; package shader includes remain immutable.");
#endif
        }

        #endregion Platform Dependent
    }
}
