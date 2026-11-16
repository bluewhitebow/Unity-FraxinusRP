using System;
using System.Diagnostics;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

using Debug = UnityEngine.Debug;

namespace FraxinusEngine.Rendering.Ultra
{
    public static class RenderingUtils
    {
        public static string GetUltraRPPath() => "Assets";

        static Texture3D s_whiteVolumeTexture;
        /// <summary>
        /// 白色 3D 纹理 (1x1x1)，用于 fallback
        /// </summary>
        public static Texture3D whiteVolumeTexture
        {
            get
            {
                if (s_whiteVolumeTexture == null)
                {
                    s_whiteVolumeTexture = new Texture3D(1, 1, 1, TextureFormat.RGBA32, false);
                    s_whiteVolumeTexture.name = "White Volume Texture";
                    s_whiteVolumeTexture.hideFlags = HideFlags.HideAndDontSave;
                    s_whiteVolumeTexture.SetPixels(new Color[] { Color.white });
                    s_whiteVolumeTexture.Apply();
                }
                return s_whiteVolumeTexture;
            }
        }

        static List<ShaderTagId> s_legacyShaderPassNames = new List<ShaderTagId>()
        {
            new ShaderTagId("Always"),
            new ShaderTagId("ForwardBase"),
            new ShaderTagId("PrepassBase"),
            new ShaderTagId("Vertex"),
            new ShaderTagId("VertexLMRGBM"),
            new ShaderTagId("VertexLM"),
        };

        static AttachmentDescriptor s_emptyAttachment = new AttachmentDescriptor(GraphicsFormat.None);
        internal static AttachmentDescriptor emptyAttachment
        {
            get
            {
                return s_emptyAttachment;
            }
        }

        public enum FullScreenMesh
        {
            Triangle = 0,
            Quad = 1,
        }

        static Mesh s_fullscreenQuadMesh = null;
        public static Mesh fullscreenQuadMesh
        {
            get
            {
                if (s_fullscreenQuadMesh != null)
                    return s_fullscreenQuadMesh;

                float topV = 1.0f;
                float bottomV = 0.0f;

                s_fullscreenQuadMesh = new Mesh { name = "Fullscreen Quad" };
                s_fullscreenQuadMesh.SetVertices(new List<Vector3>
                {
                    new Vector3(-1.0f, -1.0f, 0.0f),
                    new Vector3(-1.0f,  1.0f, 0.0f),
                    new Vector3(1.0f, -1.0f, 0.0f),
                    new Vector3(1.0f,  1.0f, 0.0f)
                });

                s_fullscreenQuadMesh.SetUVs(0, new List<Vector2>
                {
                    new Vector2(0.0f, bottomV),
                    new Vector2(0.0f, topV),
                    new Vector2(1.0f, bottomV),
                    new Vector2(1.0f, topV)
                });

                s_fullscreenQuadMesh.SetIndices(new[] { 0, 1, 2, 2, 1, 3 }, MeshTopology.Triangles, 0, false);
                s_fullscreenQuadMesh.UploadMeshData(true);
                return s_fullscreenQuadMesh;
            }
        }

        static Mesh s_fullscreenTriangleMesh = null;
        public static Mesh fullScreenTriangleMesh
        {
            get
            {
                if (s_fullscreenTriangleMesh != null)
                    return s_fullscreenTriangleMesh;

                s_fullscreenTriangleMesh = new Mesh { name = "FullScreen Triangle" };
                // 支持low平台(GLES2/3, DX9 等)
                s_fullscreenTriangleMesh.SetVertices(new List<Vector3>
                {
                    new Vector3(-1f, -1f, 0f),
                    new Vector3(-1f,  3f, 0f),
                    new Vector3( 3f, -1f, 0f)
                });
                s_fullscreenTriangleMesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, false);
                s_fullscreenTriangleMesh.UploadMeshData(false);

                return s_fullscreenTriangleMesh;
            }
        }

        internal static bool useStructuredBuffer
        {
            // 在某些平台用StructuredBuffers有性能问题，所以保底还是用UBO
            get
            {
                // TODO: 知道弄清楚Vulkan的绑定问题之前都关掉SSBO
                // When enabling this also enable it in shader side in Input.hlsl
                return false;

                // 在D3D平台不用SSBO，因为在D3D10不通过增加变体就分不清
                // GraphicsDeviceType deviceType = SystemInfo.graphicsDeviceType;
                // return !Application.isMobilePlatform &&
                //    (deviceType == GraphicsDeviceType.Metal || deviceType == GraphicsDeviceType.Vulkan ||
                //     deviceType == GraphicsDeviceType.PlayStation4 || deviceType == GraphicsDeviceType.PlayStation5 || deviceType == GraphicsDeviceType.XboxOne);
            }
        }

        internal static bool SupportsLightLayers(GraphicsDeviceType type)
        {
            return true;
        }

        static Material s_errorMaterial;
        static Material errorMaterial
        {
            get
            {
                if (s_errorMaterial == null)
                {
                    // TODO: When importing project, AssetPreviewUpdater::CreatePreviewForAsset will be called multiple times.
                    // This might be in a point that some resources required for the pipeline are not finished importing yet.
                    // Proper fix is to add a fence on asset import.
                    Shader shader = Shader.Find("Hidden/Ultra Render Pipeline/FallbackError");
                    if (shader != null)
                        s_errorMaterial = new Material(shader);
                }

                return s_errorMaterial;
            }
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal static void CreateRendererParamsObjectsWithError(ref CullingResults cullResults, Camera camera, FilteringSettings filterSettings, SortingCriteria sortFlags, ref RendererListParams param)
        {
            SortingSettings sortingSettings = new SortingSettings(camera) { criteria = sortFlags };
            DrawingSettings errorSettings = new DrawingSettings(s_legacyShaderPassNames[0], sortingSettings)
            {
                perObjectData = PerObjectData.None,
                overrideMaterial = errorMaterial,
                overrideMaterialPassIndex = 0
            };
            for (int i = 1; i < s_legacyShaderPassNames.Count; ++i)
                errorSettings.SetShaderPassName(i, s_legacyShaderPassNames[i]);

            param = new RendererListParams(cullResults, errorSettings, filterSettings);
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal static void CreateRendererListObjectsWithError(ScriptableRenderContext context, ref CullingResults cullResults, Camera camera, FilteringSettings filterSettings, SortingCriteria sortFlags, ref RendererList rl)
        {
            // TODO: When importing project, AssetPreviewUpdater::CreatePreviewForAsset will be called multiple times.
            // This might be in a point that some resources required for the pipeline are not finished importing yet.
            // Proper fix is to add a fence on asset import.
            if (errorMaterial == null)
            {
                rl = RendererList.nullRendererList;
                return;
            }

            RendererListParams param = new RendererListParams();
            CreateRendererParamsObjectsWithError(ref cullResults, camera, filterSettings, sortFlags, ref param);
            rl = context.CreateRendererList(ref param);
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal static void CreateRendererListObjectsWithError(RenderGraph renderGraph, ref CullingResults cullResults, Camera camera, FilteringSettings filterSettings, SortingCriteria sortFlags, ref RendererListHandle rl)
        {
            // TODO: When importing project, AssetPreviewUpdater::CreatePreviewForAsset will be called multiple times.
            // This might be in a point that some resources required for the pipeline are not finished importing yet.
            // Proper fix is to add a fence on asset import.
            if (errorMaterial == null)
            {
                rl = new RendererListHandle();
                return;
            }

            RendererListParams param = new RendererListParams();
            CreateRendererParamsObjectsWithError(ref cullResults, camera, filterSettings, sortFlags, ref param);
            rl = renderGraph.CreateRendererList(param);
        }

        [Conditional("DEVELOPMENT_BUILD"), Conditional("UNITY_EDITOR")]
        internal static void DrawRendererListObjectsWithError(RasterCommandBuffer cmd, ref RendererList rl)
        {
            cmd.DrawRendererList(rl);
        }

        internal static void CreateRendererListWithRenderStateBlock(ScriptableRenderContext context, ref CullingResults cullResults, DrawingSettings ds, FilteringSettings fs, RenderStateBlock rsb, ref RendererList rl)
        {
            RendererListParams param = new RendererListParams();
            unsafe
            {
                RenderStateBlock* rsbPtr = &rsb;
                var stateBlocks = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<RenderStateBlock>(rsbPtr, 1, Allocator.None);

                var shaderTag = ShaderTagId.none;
                var tagValues = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<ShaderTagId>(&shaderTag, 1, Allocator.None);

                // Inside CreateRendererList (below), we pass the NativeArrays to C++ by calling GetUnsafeReadOnlyPtr
                // This will check read access but NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray does not set up the SafetyHandle (by design) so create/add it here
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                var safetyHandle = AtomicSafetyHandle.Create();

                AtomicSafetyHandle.SetAllowReadOrWriteAccess(safetyHandle, true);

                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref stateBlocks, safetyHandle);
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref tagValues, safetyHandle);
#endif

                // Create & schedule the RL
                param = new RendererListParams(cullResults, ds, fs)
                {
                    tagValues = tagValues,
                    stateBlocks = stateBlocks
                };

                rl = context.CreateRendererList(ref param);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.Release(safetyHandle);
#endif
            }
        }

        static ShaderTagId[] s_shaderTagValues = new ShaderTagId[1];
        static RenderStateBlock[] s_renderStateBlocks = new RenderStateBlock[1];

        internal static void CreateRendererListWithRenderStateBlock(RenderGraph renderGraph, ref CullingResults cullResults, DrawingSettings ds, FilteringSettings fs, RenderStateBlock rsb, ref RendererListHandle rl)
        {
            s_shaderTagValues[0] = ShaderTagId.none;
            s_renderStateBlocks[0] = rsb;
            NativeArray<ShaderTagId> tagValues = new NativeArray<ShaderTagId>(s_shaderTagValues, Allocator.Temp);
            NativeArray<RenderStateBlock> stateBlocks = new NativeArray<RenderStateBlock>(s_renderStateBlocks, Allocator.Temp);
            var param = new RendererListParams(cullResults, ds, fs)
            {
                tagValues = tagValues,
                stateBlocks = stateBlocks,
                isPassTagName = false
            };

            rl = renderGraph.CreateRendererList(param);
        }

        static Dictionary<RenderTextureFormat, bool> _renderTextureFormatSupport = new Dictionary<RenderTextureFormat, bool>();
        static Dictionary<GraphicsFormat, Dictionary<GraphicsFormatUsage, bool>> _graphicsFormatSupport = new Dictionary<GraphicsFormat, Dictionary<GraphicsFormatUsage, bool>>();
        internal static Mesh fullScreenQuadMesh;

        internal static void ClearSystemInfoCache()
        {
            _renderTextureFormatSupport.Clear();
            _graphicsFormatSupport.Clear();
        }

        public static bool SupportsRenderTextureFormat(RenderTextureFormat format)
        {
            if (!_renderTextureFormatSupport.TryGetValue(format, out var support))
            {
                support = SystemInfo.SupportsRenderTextureFormat(format);
                _renderTextureFormatSupport.Add(format, support);
            }

            return support;
        }

        /// <summary>
        /// Returns whether a format supports every requested Unity 6 usage
        /// flag. Unity 2022 exposes only single <see cref="FormatUsage"/>
        /// queries, so combined flags are evaluated one by one.
        /// </summary>
        private static bool QueryGraphicsFormatSupport(GraphicsFormat format, GraphicsFormatUsage usage)
        {
#if UNITY_6000_0_OR_NEWER
            return SystemInfo.IsFormatSupported(format, usage);
#else
            var remaining = usage;
            if (!IsLegacyUsageSupported(format, GraphicsFormatUsage.Sample, FormatUsage.Sample, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.Linear, FormatUsage.Linear, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.Sparse, FormatUsage.Sparse, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.Render, FormatUsage.Render, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.Blend, FormatUsage.Blend, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.GetPixels, FormatUsage.GetPixels, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.SetPixels, FormatUsage.SetPixels, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.SetPixels32, FormatUsage.SetPixels32, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.ReadPixels, FormatUsage.ReadPixels, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.LoadStore, FormatUsage.LoadStore, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.MSAA2x, FormatUsage.MSAA2x, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.MSAA4x, FormatUsage.MSAA4x, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.MSAA8x, FormatUsage.MSAA8x, ref remaining) ||
                !IsLegacyUsageSupported(format, GraphicsFormatUsage.StencilSampling, FormatUsage.StencilSampling, ref remaining))
                return false;

            // Reject unknown future bits instead of silently claiming support.
            return remaining == GraphicsFormatUsage.None;
#endif
        }

#if !UNITY_6000_0_OR_NEWER
        private static bool IsLegacyUsageSupported(
            GraphicsFormat format,
            GraphicsFormatUsage flag,
            FormatUsage legacyUsage,
            ref GraphicsFormatUsage remaining)
        {
            if ((remaining & flag) == 0)
                return true;

            remaining &= ~flag;
            return SystemInfo.IsFormatSupported(format, legacyUsage);
        }
#endif

        public static bool SupportsGraphicsFormat(GraphicsFormat format, GraphicsFormatUsage usage)
        {
            bool support = false;
            if (!_graphicsFormatSupport.TryGetValue(format, out var uses))
            {
                uses = new Dictionary<GraphicsFormatUsage, bool>();
                support = QueryGraphicsFormatSupport(format, usage);
                uses.Add(usage, support);
                _graphicsFormatSupport.Add(format, uses);
            }
            else
            {
                if (!uses.TryGetValue(usage, out support))
                {
                    support = QueryGraphicsFormatSupport(format, usage);
                    uses.Add(usage, support);
                }
            }

            return support;
        }

        internal static int GetLastValidColorBufferIndex(RenderTargetIdentifier[] colorBuffers)
        {
            int i = colorBuffers.Length - 1;
            for (; i >= 0; --i)
            {
                if (colorBuffers[i] != 0)
                    break;
            }
            return i;
        }

        internal static uint GetValidColorBufferCount(RenderTargetIdentifier[] colorBuffers)
        {
            uint nonNullColorBuffers = 0;
            if (colorBuffers != null)
            {
                foreach (var identifier in colorBuffers)
                {
                    if (identifier != 0)
                        ++nonNullColorBuffers;
                }
            }

            return nonNullColorBuffers;
        }

        internal static bool IsMRT(RenderTargetIdentifier[] colorBuffers)
        {
            return GetValidColorBufferCount(colorBuffers) > 1;
        }

        internal static bool Contains(RenderTargetIdentifier[] source, RenderTargetIdentifier value)
        {
            foreach (var identifier in source)
            {
                if (identifier == value)
                    return true;
            }

            return false;
        }

        internal static int IndexOf(RenderTargetIdentifier[] source, RenderTargetIdentifier value)
        {
            for (int i = 0; i < source.Length; ++i)
            {
                if (source[i] == value)
                    return i;
            }

            return -1;
        }

        internal static uint CountDistinct(RenderTargetIdentifier[] source, RenderTargetIdentifier value)
        {
            uint count = 0;
            for (int i = 0; i < source.Length; ++i)
            {
                if (source[i] != value && source[i] != 0)
                    ++count;
            }

            return count;
        }

        internal static int LastValid(RenderTargetIdentifier[] source)
        {
            for (int i = source.Length - 1; i >= 0; --i)
            {
                if (source[i] != 0)
                    return i;
            }

            return -1;
        }

        internal static bool Contains(ClearFlag a, ClearFlag b)
        {
            return (a & b) == b;
        }

        internal static bool SequenceEqual(RenderTargetIdentifier[] left, RenderTargetIdentifier[] right)
        {
            if (left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; ++i)
                if (left[i] != right[i])
                    return false;

            return true;
        }

        internal static bool MultisampleDepthResolveSupported()
        {
            // Temporarily disabling depth resolve a driver bug on OSX when using some AMD graphics cards. Temporarily disabling depth resolve on that platform
            // TODO: re-enable once the issue is investigated/fixed
            if (Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer)
                return false;

            // Should we also check if the format has stencil and check stencil resolve capability only in that case?
            return SystemInfo.supportsMultisampleResolveDepth && SystemInfo.supportsMultisampleResolveStencil;
        }

        /// <summary>
        /// Re-allocate fixed-size RTHandle if it is not allocated or doesn't match the descriptor
        /// </summary>
        /// <param name="handle">RTHandle to check (can be null)</param>
        /// <param name="descriptor">Descriptor for the RTHandle to match</param>
        /// <param name="filterMode">Filtering mode of the RTHandle.</param>
        /// <param name="wrapMode">Addressing mode of the RTHandle.</param>
        /// <param name="anisoLevel">Anisotropic filtering level.</param>
        /// <param name="mipMapBias">Bias applied to mipmaps during filtering.</param>
        /// <param name="name">Name of the RTHandle.</param>
        /// <returns>If an allocation was done.</returns>
        public static bool ReAllocateHandleIfNeeded(
            ref RTHandle handle,
            in RenderTextureDescriptor descriptor,
            FilterMode filterMode = FilterMode.Point,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat,
            int anisoLevel = 1,
            float mipMapBias = 0,
            string name = "")
        {
            Debug.Assert(descriptor.graphicsFormat == GraphicsFormat.None ^ descriptor.depthStencilFormat == GraphicsFormat.None);

            TextureDesc requestRTDesc = RTHandleResourcePool.CreateTextureDesc(descriptor, TextureSizeMode.Explicit, anisoLevel, 0, filterMode, wrapMode, name);
            if (RTHandleNeedsReAlloc(handle, requestRTDesc, false))
            {
                if (handle != null && handle.rt != null)
                {
                    TextureDesc currentRTDesc = RTHandleResourcePool.CreateTextureDesc(handle.rt.descriptor, TextureSizeMode.Explicit, handle.rt.anisoLevel, handle.rt.mipMapBias, handle.rt.filterMode, handle.rt.wrapMode, handle.name);
                    AddStaleResourceToPoolOrRelease(currentRTDesc, handle);
                }

                if (UltraRenderPipeline.rtHandlePool.TryGetResource(requestRTDesc, out handle))
                    return true;

                var actualFormat = descriptor.graphicsFormat != GraphicsFormat.None ? descriptor.graphicsFormat : descriptor.depthStencilFormat;

                RTHandleAllocInfo allocInfo = new RTHandleAllocInfo();
                allocInfo.slices = descriptor.volumeDepth;
                allocInfo.format = actualFormat;
                allocInfo.filterMode = filterMode;
                allocInfo.wrapModeU = wrapMode;
                allocInfo.wrapModeV = wrapMode;
                allocInfo.wrapModeW = wrapMode;
                allocInfo.dimension = descriptor.dimension;
                allocInfo.enableRandomWrite = descriptor.enableRandomWrite;
                allocInfo.useMipMap = descriptor.useMipMap;
                allocInfo.autoGenerateMips = descriptor.autoGenerateMips;
                allocInfo.anisoLevel = anisoLevel;
                allocInfo.mipMapBias = mipMapBias;
                allocInfo.msaaSamples = (MSAASamples)descriptor.msaaSamples;
                allocInfo.bindTextureMS = descriptor.bindMS;
                allocInfo.useDynamicScale = descriptor.useDynamicScale;
                allocInfo.memoryless = descriptor.memoryless;
                allocInfo.vrUsage = descriptor.vrUsage;
                allocInfo.name = name;

                handle = RTHandles.Alloc(descriptor.width, descriptor.height, allocInfo);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Return true if handle does not match descriptor
        /// </summary>
        /// <param name="handle">RTHandle to check (can be null)</param>
        /// <param name="descriptor">Descriptor for the RTHandle to match</param>
        /// <param name="filterMode">Filtering mode of the RTHandle.</param>
        /// <param name="wrapMode">Addressing mode of the RTHandle.</param>
        /// <param name="anisoLevel">Anisotropic filtering level.</param>
        /// <param name="mipMapBias">Bias applied to mipmaps during filtering.</param>
        /// <param name="name">Name of the RTHandle.</param>
        /// <param name="scaled">Check if the RTHandle has auto scaling enabled if not, check the widths and heights</param>
        /// <returns></returns>
        internal static bool RTHandleNeedsReAlloc(
            RTHandle handle,
            in TextureDesc descriptor,
            bool scaled)
        {
            if (handle == null || handle.rt == null)
                return true;
            if (handle.useScaling != scaled)
                return true;
            if (!scaled && (handle.rt.width != descriptor.width || handle.rt.height != descriptor.height))
                return true;

            var rtHandleFormat = (handle.rt.descriptor.depthStencilFormat != GraphicsFormat.None) ? handle.rt.descriptor.depthStencilFormat : handle.rt.descriptor.graphicsFormat;

            return
                rtHandleFormat != descriptor.format ||
                handle.rt.descriptor.dimension != descriptor.dimension ||
                handle.rt.descriptor.enableRandomWrite != descriptor.enableRandomWrite ||
                handle.rt.descriptor.useMipMap != descriptor.useMipMap ||
                handle.rt.descriptor.autoGenerateMips != descriptor.autoGenerateMips ||
                (MSAASamples)handle.rt.descriptor.msaaSamples != descriptor.msaaSamples ||
                handle.rt.descriptor.bindMS != descriptor.bindTextureMS ||
                handle.rt.descriptor.useDynamicScale != descriptor.useDynamicScale ||
                handle.rt.descriptor.memoryless != descriptor.memoryless ||
                handle.rt.filterMode != descriptor.filterMode ||
                handle.rt.wrapMode != descriptor.wrapMode ||
                handle.rt.anisoLevel != descriptor.anisoLevel ||
                handle.rt.mipMapBias != descriptor.mipMapBias ||
                handle.name != descriptor.name;
        }

        /// <summary>
        /// Re-allocate dynamically resized RTHandle if it is not allocated or doesn't match the descriptor
        /// </summary>
        /// <param name="handle">RTHandle to check (can be null)</param>
        /// <param name="scaleFactor">Constant scale for the RTHandle size computation.</param>
        /// <param name="descriptor">Descriptor for the RTHandle to match</param>
        /// <param name="filterMode">Filtering mode of the RTHandle.</param>
        /// <param name="wrapMode">Addressing mode of the RTHandle.</param>
        /// <param name="anisoLevel">Anisotropic filtering level.</param>
        /// <param name="mipMapBias">Bias applied to mipmaps during filtering.</param>
        /// <param name="name">Name of the RTHandle.</param>
        /// <returns>If an allocation was done.</returns>
        public static bool ReAllocateHandleIfNeeded(
            ref RTHandle handle,
            Vector2 scaleFactor,
            in RenderTextureDescriptor descriptor,
            FilterMode filterMode = FilterMode.Point,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat,
            int anisoLevel = 1,
            float mipMapBias = 0,
            string name = "")
        {
            var usingConstantScale = handle != null && handle.useScaling && handle.scaleFactor == scaleFactor;
            TextureDesc requestRTDesc = RTHandleResourcePool.CreateTextureDesc(descriptor, TextureSizeMode.Scale, anisoLevel, 0, filterMode, wrapMode);
            if (!usingConstantScale || RTHandleNeedsReAlloc(handle, requestRTDesc, true))
            {
                if (handle != null && handle.rt != null)
                {
                    TextureDesc currentRTDesc = RTHandleResourcePool.CreateTextureDesc(handle.rt.descriptor, TextureSizeMode.Scale, handle.rt.anisoLevel, handle.rt.mipMapBias, handle.rt.filterMode, handle.rt.wrapMode);
                    AddStaleResourceToPoolOrRelease(currentRTDesc, handle);
                }

                if (UltraRenderPipeline.rtHandlePool.TryGetResource(requestRTDesc, out handle))
                {
                    return true;
                }

                var actualFormat = descriptor.graphicsFormat != GraphicsFormat.None ? descriptor.graphicsFormat : descriptor.depthStencilFormat;

                RTHandleAllocInfo allocInfo = new RTHandleAllocInfo();
                allocInfo.slices = descriptor.volumeDepth;
                allocInfo.format = actualFormat;
                allocInfo.filterMode = filterMode;
                allocInfo.wrapModeU = wrapMode;
                allocInfo.wrapModeV = wrapMode;
                allocInfo.wrapModeW = wrapMode;
                allocInfo.dimension = descriptor.dimension;
                allocInfo.enableRandomWrite = descriptor.enableRandomWrite;
                allocInfo.useMipMap = descriptor.useMipMap;
                allocInfo.autoGenerateMips = descriptor.autoGenerateMips;
                allocInfo.anisoLevel = anisoLevel;
                allocInfo.mipMapBias = mipMapBias;
                allocInfo.msaaSamples = (MSAASamples)descriptor.msaaSamples;
                allocInfo.bindTextureMS = descriptor.bindMS;
                allocInfo.useDynamicScale = descriptor.useDynamicScale;
                allocInfo.memoryless = descriptor.memoryless;
                allocInfo.vrUsage = descriptor.vrUsage;
                allocInfo.name = name;

                handle = RTHandles.Alloc(scaleFactor, allocInfo);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Re-allocate dynamically resized RTHandle if it is not allocated or doesn't match the descriptor
        /// </summary>
        /// <param name="handle">RTHandle to check (can be null)</param>
        /// <param name="scaleFunc">Function used for the RTHandle size computation.</param>
        /// <param name="descriptor">Descriptor for the RTHandle to match</param>
        /// <param name="filterMode">Filtering mode of the RTHandle.</param>
        /// <param name="wrapMode">Addressing mode of the RTHandle.</param>
        /// <param name="anisoLevel">Anisotropic filtering level.</param>
        /// <param name="mipMapBias">Bias applied to mipmaps during filtering.</param>
        /// <param name="name">Name of the RTHandle.</param>
        /// <returns>If an allocation was done.</returns>
        public static bool ReAllocateHandleIfNeeded(
            ref RTHandle handle,
            ScaleFunc scaleFunc,
            in RenderTextureDescriptor descriptor,
            FilterMode filterMode = FilterMode.Point,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat,
            int anisoLevel = 1,
            float mipMapBias = 0,
            string name = "")
        {
            var usingScaleFunction = handle != null && handle.useScaling && handle.scaleFactor == Vector2.zero;
            TextureDesc requestRTDesc = RTHandleResourcePool.CreateTextureDesc(descriptor, TextureSizeMode.Functor, anisoLevel, 0, filterMode, wrapMode);
            if (!usingScaleFunction || RTHandleNeedsReAlloc(handle, requestRTDesc, true))
            {
                if (handle != null && handle.rt != null)
                {
                    TextureDesc currentRTDesc = RTHandleResourcePool.CreateTextureDesc(handle.rt.descriptor, TextureSizeMode.Functor, handle.rt.anisoLevel, handle.rt.mipMapBias, handle.rt.filterMode, handle.rt.wrapMode);
                    AddStaleResourceToPoolOrRelease(currentRTDesc, handle);
                }

                if (UltraRenderPipeline.rtHandlePool.TryGetResource(requestRTDesc, out handle))
                    return true;

                var actualFormat = descriptor.graphicsFormat != GraphicsFormat.None ? descriptor.graphicsFormat : descriptor.depthStencilFormat;

                RTHandleAllocInfo allocInfo = new RTHandleAllocInfo();
                allocInfo.slices = descriptor.volumeDepth;
                allocInfo.format = actualFormat;
                allocInfo.filterMode = filterMode;
                allocInfo.wrapModeU = wrapMode;
                allocInfo.wrapModeV = wrapMode;
                allocInfo.wrapModeW = wrapMode;
                allocInfo.dimension = descriptor.dimension;
                allocInfo.enableRandomWrite = descriptor.enableRandomWrite;
                allocInfo.useMipMap = descriptor.useMipMap;
                allocInfo.autoGenerateMips = descriptor.autoGenerateMips;
                allocInfo.anisoLevel = anisoLevel;
                allocInfo.mipMapBias = mipMapBias;
                allocInfo.msaaSamples = (MSAASamples)descriptor.msaaSamples;
                allocInfo.bindTextureMS = descriptor.bindMS;
                allocInfo.useDynamicScale = descriptor.useDynamicScale;
                allocInfo.memoryless = descriptor.memoryless;
                allocInfo.vrUsage = descriptor.vrUsage;
                allocInfo.name = name;

                handle = RTHandles.Alloc(scaleFunc, allocInfo);
                return true;
            }
            return false;
        }

        internal static void AddStaleResourceToPoolOrRelease(TextureDesc desc, RTHandle handle)
        {
            if (!UltraRenderPipeline.rtHandlePool.AddResourceToPool(desc, handle, Time.frameCount))
                RTHandles.Release(handle);
        }

        public static TextureHandle CreateRenderGraphTexture(RenderGraph renderGraph, RenderTextureDescriptor desc, string name, bool clear,
            FilterMode filterMode = FilterMode.Point, TextureWrapMode wrapMode = TextureWrapMode.Clamp)
        {
            TextureDesc rgDesc = new TextureDesc(desc.width, desc.height);
            rgDesc.dimension = desc.dimension;
            rgDesc.clearBuffer = clear;
            rgDesc.bindTextureMS = desc.bindMS;
            rgDesc.colorFormat = desc.graphicsFormat;
            rgDesc.depthBufferBits = (DepthBits)desc.depthBufferBits;
            rgDesc.slices = desc.volumeDepth;
            rgDesc.msaaSamples = (MSAASamples)desc.msaaSamples;
            rgDesc.name = name;
            rgDesc.enableRandomWrite = desc.enableRandomWrite;
            rgDesc.filterMode = filterMode;
            rgDesc.wrapMode = wrapMode;
            rgDesc.isShadowMap = desc.shadowSamplingMode != ShadowSamplingMode.None && desc.depthStencilFormat != GraphicsFormat.None;

            return renderGraph.CreateTexture(rgDesc);
        }

        internal static TextureHandle CreateRenderGraphTexture(RenderGraph renderGraph, RenderTextureDescriptor desc, string name, bool clear, Color color,
            FilterMode filterMode = FilterMode.Point, TextureWrapMode wrapMode = TextureWrapMode.Clamp)
        {
            TextureDesc rgDesc = new TextureDesc(desc.width, desc.height);
            rgDesc.dimension = desc.dimension;
            rgDesc.clearBuffer = clear;
            rgDesc.clearColor = color;
            rgDesc.bindTextureMS = desc.bindMS;
            rgDesc.colorFormat = desc.graphicsFormat;
            rgDesc.depthBufferBits = (DepthBits)desc.depthBufferBits;
            rgDesc.slices = desc.volumeDepth;
            rgDesc.msaaSamples = (MSAASamples)desc.msaaSamples;
            rgDesc.name = name;
            rgDesc.enableRandomWrite = desc.enableRandomWrite;
            rgDesc.filterMode = filterMode;
            rgDesc.wrapMode = wrapMode;

            return renderGraph.CreateTexture(rgDesc);
        }

        internal static void DisplayMessageNotification(string msg)
        {
            Debug.LogError(msg);

#if UNITY_EDITOR
            foreach (UnityEditor.SceneView sceneView in UnityEditor.SceneView.sceneViews)
                sceneView.ShowNotification(new GUIContent(msg));
#endif
        }

        internal static Vector4 GetFinalBlitScaleBias(RTHandle source, RTHandle destination, CameraData cameraData)
        {
            Vector2 viewportScale = source.useScaling ? new Vector2(source.rtHandleProperties.rtHandleScale.x, source.rtHandleProperties.rtHandleScale.y) : Vector2.one;
            var yflip = cameraData.IsRenderTargetProjectionMatrixFlipped(destination);
            Vector4 scaleBias = !yflip ? new Vector4(viewportScale.x, -viewportScale.y, 0, viewportScale.y) : new Vector4(viewportScale.x, viewportScale.y, 0, 0);

            return scaleBias;
        }

        public static DrawingSettings CreateDrawingSettings(ShaderTagId shaderTagId, RenderingData renderingData,
            CameraData cameraData, LightData lightData, SortingCriteria sortingCriteria)
        {
            Camera camera = cameraData.camera;
            SortingSettings sortingSettings = new SortingSettings(camera) { criteria = sortingCriteria };
            DrawingSettings settings = new DrawingSettings(shaderTagId, sortingSettings)
            {
                perObjectData = renderingData.perObjectData,
                mainLightIndex = lightData.mainLightIndex,
                enableDynamicBatching = renderingData.supportsDynamicBatching,

                // Disable instancing for preview cameras. This is consistent with the built-in forward renderer. Also fixes case 1127324.
                enableInstancing = camera.cameraType == CameraType.Preview ? false : true,
            };
            return settings;
        }

        public static DrawingSettings CreateDrawingSettings(List<ShaderTagId> shaderTagIdList,
            RenderingData renderingData, CameraData cameraData,
            LightData lightData, SortingCriteria sortingCriteria)
        {
            if (shaderTagIdList == null || shaderTagIdList.Count == 0)
            {
                Debug.LogWarning("ShaderTagId list is invalid. DrawingSettings is created with default pipeline ShaderTagId");
                return CreateDrawingSettings(new ShaderTagId("ScriptableRenderPipeline"), renderingData, cameraData, lightData, sortingCriteria);
            }
            
            DrawingSettings settings = CreateDrawingSettings(shaderTagIdList[0], renderingData, cameraData, lightData, sortingCriteria);
            for (int i = 1; i < shaderTagIdList.Count; ++i)
                settings.SetShaderPassName(i, shaderTagIdList[i]);
            
            return settings;
        }

        /// <summary>
        /// PackedMipChainInfo from HDRP HDUtils.cs
        /// </summary>
        internal struct PackedMipChainInfo
        {
            public Vector2Int textureSize;
            public int mipLevelCount;
            public Vector2Int[] mipLevelSizes;
            public Vector2Int[] mipLevelOffsets;

            private Vector2 _cachedTextureScale;
            private Vector2Int _cachedHardwareTextureSize;

            private bool _offsetBufferWillNeedUpdate;

            public void Allocate()
            {
                mipLevelOffsets = new Vector2Int[15];
                mipLevelSizes = new Vector2Int[15];
                _offsetBufferWillNeedUpdate = true;
            }

            // We pack all MIP levels into the top MIP level to avoid the Pow2 MIP chain restriction.
            // We compute the required size iteratively.
            // This function is NOT fast, but it is illustrative, and can be optimized later.
            public void ComputePackedMipChainInfo(Vector2Int viewportSize)
            {
                bool isHardwareDrsOn = DynamicResolutionHandler.instance.HardwareDynamicResIsEnabled();
                Vector2Int hardwareTextureSize = isHardwareDrsOn ? DynamicResolutionHandler.instance.ApplyScalesOnSize(viewportSize) : viewportSize;
                Vector2 textureScale = isHardwareDrsOn ? new Vector2((float)viewportSize.x / (float)hardwareTextureSize.x, (float)viewportSize.y / (float)hardwareTextureSize.y) : new Vector2(1.0f, 1.0f);

                // No work needed.
                if (_cachedHardwareTextureSize == hardwareTextureSize && _cachedTextureScale == textureScale)
                    return;

                _cachedHardwareTextureSize = hardwareTextureSize;
                _cachedTextureScale = textureScale;

                mipLevelSizes[0] = hardwareTextureSize;
                mipLevelOffsets[0] = Vector2Int.zero;

                int mipLevel = 0;
                Vector2Int mipSize = hardwareTextureSize;

                do
                {
                    mipLevel++;

                    // Round up.
                    mipSize.x = Math.Max(1, (mipSize.x + 1) >> 1);
                    mipSize.y = Math.Max(1, (mipSize.y + 1) >> 1);

                    mipLevelSizes[mipLevel] = mipSize;

                    Vector2Int prevMipBegin = mipLevelOffsets[mipLevel - 1];
                    Vector2Int prevMipEnd = prevMipBegin + mipLevelSizes[mipLevel - 1];

                    Vector2Int mipBegin = new Vector2Int();

                    if ((mipLevel & 1) != 0) // Odd
                    {
                        mipBegin.x = prevMipBegin.x;
                        mipBegin.y = prevMipEnd.y;
                    }
                    else // Even
                    {
                        mipBegin.x = prevMipEnd.x;
                        mipBegin.y = prevMipBegin.y;
                    }

                    mipLevelOffsets[mipLevel] = mipBegin;

                    hardwareTextureSize.x = Math.Max(hardwareTextureSize.x, mipBegin.x + mipSize.x);
                    hardwareTextureSize.y = Math.Max(hardwareTextureSize.y, mipBegin.y + mipSize.y);
                }
                while ((mipSize.x > 1) || (mipSize.y > 1));

                textureSize = new Vector2Int(
                    (int)Mathf.Ceil((float)hardwareTextureSize.x * textureScale.x), (int)Mathf.Ceil((float)hardwareTextureSize.y * textureScale.y));

                mipLevelCount = mipLevel + 1;
                _offsetBufferWillNeedUpdate = true;
            }

            public GraphicsBuffer GetOffsetBufferData(GraphicsBuffer mipLevelOffsetsBuffer)
            {
                if (_offsetBufferWillNeedUpdate)
                {
                    mipLevelOffsetsBuffer.SetData(mipLevelOffsets);
                    _offsetBufferWillNeedUpdate = false;
                }

                return mipLevelOffsetsBuffer;
            }
        }

        internal static float ComputeViewportScale(int viewportSize, int bufferSize)
        {
            float rcpBufferSize = 1.0f / bufferSize;

            // Scale by (vp_dim / buf_dim).
            return viewportSize * rcpBufferSize;
        }

        internal static float ComputeViewportLimit(int viewportSize, int bufferSize)
        {
            float rcpBufferSize = 1.0f / bufferSize;

            // Clamp to (vp_dim - 0.5) / buf_dim.
            return (viewportSize - 0.5f) * rcpBufferSize;
        }

        internal static Vector4 ComputeViewportScaleAndLimit(Vector2Int viewportSize, Vector2Int bufferSize)
        {
            return new Vector4(ComputeViewportScale(viewportSize.x, bufferSize.x),  // Scale(x)
                ComputeViewportScale(viewportSize.y, bufferSize.y),                 // Scale(y)
                ComputeViewportLimit(viewportSize.x, bufferSize.x),                 // Limit(x)
                ComputeViewportLimit(viewportSize.y, bufferSize.y));                // Limit(y)
        }

        internal static Matrix4x4 ComputePixelCoordToWorldSpaceViewDirectionMatrix(float verticalFoV, Vector2 lensShift, Vector4 screenSize, Matrix4x4 worldToViewMatrix, bool renderToCubemap, float aspectRatio = -1, bool isOrthographic = false)
        {
            Matrix4x4 viewSpaceRasterTransform;

            if (isOrthographic)
            {
                // For ortho cameras, project the skybox with no perspective
                // the same way as builtin does (case 1264647)
                viewSpaceRasterTransform = new Matrix4x4(
                    new Vector4(-2.0f * screenSize.z, 0.0f, 0.0f, 0.0f),
                    new Vector4(0.0f, -2.0f * screenSize.w, 0.0f, 0.0f),
                    new Vector4(1.0f, 1.0f, -1.0f, 0.0f),
                    new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
            }
            else
            {
                // Compose the view space version first.
                // V = -(X, Y, Z), s.t. Z = 1,
                // X = (2x / resX - 1) * tan(vFoV / 2) * ar = x * [(2 / resX) * tan(vFoV / 2) * ar] + [-tan(vFoV / 2) * ar] = x * [-m00] + [-m20]
                // Y = (2y / resY - 1) * tan(vFoV / 2)      = y * [(2 / resY) * tan(vFoV / 2)]      + [-tan(vFoV / 2)]      = y * [-m11] + [-m21]

                aspectRatio = aspectRatio < 0 ? screenSize.x * screenSize.w : aspectRatio;
                float tanHalfVertFoV = Mathf.Tan(0.5f * verticalFoV);

                // Compose the matrix.
                float m21 = (1.0f - 2.0f * lensShift.y) * tanHalfVertFoV;
                float m11 = -2.0f * screenSize.w * tanHalfVertFoV;

                float m20 = (1.0f - 2.0f * lensShift.x) * tanHalfVertFoV * aspectRatio;
                float m00 = -2.0f * screenSize.z * tanHalfVertFoV * aspectRatio;

                if (renderToCubemap)
                {
                    // Flip Y.
                    m11 = -m11;
                    m21 = -m21;
                }

                viewSpaceRasterTransform = new Matrix4x4(new Vector4(m00, 0.0f, 0.0f, 0.0f),
                    new Vector4(0.0f, m11, 0.0f, 0.0f),
                    new Vector4(m20, m21, -1.0f, 0.0f),
                    new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
            }

            // Remove the translation component.
            var homogeneousZero = new Vector4(0, 0, 0, 1);
            worldToViewMatrix.SetColumn(3, homogeneousZero);

            // Flip the Z to make the coordinate system left-handed.
            worldToViewMatrix.SetRow(2, -worldToViewMatrix.GetRow(2));

            // Transpose for HLSL.
            return Matrix4x4.Transpose(worldToViewMatrix.transpose * viewSpaceRasterTransform);
        }

        /// <summary>
        /// Compute the matrix from screen space (pixel) to world space direction (RHS).
        /// 
        /// You can use this matrix on the GPU to compute the direction to look in a cubemap for a specific
        /// screen pixel.
        /// </summary>
        /// <param name="camera"></param>
        /// <param name="viewMatrix"></param>
        /// <param name="gpuProjMatrix"></param>
        /// <param name="resolution"></param>
        /// <param name="aspect">
        /// The aspect ratio to use.
        ///
        /// if negative, then the aspect ratio of <paramref name="resolution"/> will be used.
        ///
        /// It is different from the aspect ratio of <paramref name="resolution"/> for anamorphic projections.
        /// </param>
        /// <returns></returns>
        internal static Matrix4x4 ComputePixelCoordToWorldSpaceViewDirectionMatrix(Camera camera, Matrix4x4 viewMatrix, Matrix4x4 gpuProjMatrix, Vector4 resolution, float aspect = -1)
        {
            //// In XR mode, or if explicitely required, use a more generic matrix to account for asymmetry in the projection
            //var useGenericMatrix = xr.enabled || frameSettings.IsEnabled(FrameSettingsField.AsymmetricProjection);

            // Asymmetry is also possible from a user-provided projection, so we must check for it too.
            // Note however, that in case of physical camera, the lens shift term is the only source of
            // asymmetry, and this is accounted for in the optimized path below. Additionally, Unity C++ will
            // automatically disable physical camera when the projection is overridden by user.
            //useGenericMatrix |= HDUtils.IsProjectionMatrixAsymmetric(viewConstants.projMatrix) && !camera.usePhysicalProperties;

            //if (useGenericMatrix)
            //{
            //    var viewSpaceRasterTransform = new Matrix4x4(
            //        new Vector4(2.0f * resolution.z, 0.0f, 0.0f, -1.0f),
            //        new Vector4(0.0f, -2.0f * resolution.w, 0.0f, 1.0f),
            //        new Vector4(0.0f, 0.0f, 1.0f, 0.0f),
            //        new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

            //    var transformT = viewConstants.invViewProjMatrix.transpose * Matrix4x4.Scale(new Vector3(-1.0f, -1.0f, -1.0f));
            //    return viewSpaceRasterTransform * transformT;
            //}

            float verticalFoV = camera.GetGateFittedFieldOfView() * Mathf.Deg2Rad;
            if (!camera.usePhysicalProperties)
            {
                verticalFoV = Mathf.Atan(-1.0f / gpuProjMatrix[1, 1]) * 2;
            }
            Vector2 lensShift = camera.GetGateFittedLensShift();

            return ComputePixelCoordToWorldSpaceViewDirectionMatrix(verticalFoV, lensShift, resolution, viewMatrix, false, aspect, camera.orthographic);
        }

        /// <summary>Get the aspect ratio of a projection matrix.</summary>
        /// <param name="matrix"></param>
        /// <returns></returns>
        internal static float ProjectionMatrixAspect(in Matrix4x4 matrix)
            => -matrix.m11 / matrix.m00;

        internal static float InterpolateOrientation(float fromValue, float toValue, float t)
        {
            // Compute the direct distance
            float directDistance = Mathf.Abs(toValue - fromValue);
            float outputValue = 0.0f;

            // Handle the two cases
            if (fromValue < toValue)
            {
                float upperRange = 360.0f - toValue;
                float lowerRange = fromValue;
                float alternativeDistance = upperRange + lowerRange;
                if (alternativeDistance < directDistance)
                {
                    float targetValue = toValue - 360.0f;
                    outputValue = fromValue + (targetValue - fromValue) * t;
                    if (outputValue < 0.0f)
                        outputValue += 360.0f;
                }
                else
                {
                    outputValue = fromValue + (toValue - fromValue) * t;
                }
            }
            else
            {
                float upperRange = 360.0f - fromValue;
                float lowerRange = toValue;
                float alternativeDistance = upperRange + lowerRange;
                if (alternativeDistance < directDistance)
                {
                    float targetValue = toValue + 360.0f;
                    outputValue = fromValue + (targetValue - fromValue) * t;
                    if (outputValue > 360.0f)
                        outputValue -= 360.0f;
                }
                else
                {
                    outputValue = fromValue + (toValue - fromValue) * t;
                }
            }

            return outputValue;
        }

        /// <summary>
        /// Compute kernel size.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        internal static int DivRoundUp(int x, int y) => (x + y - 1) / y;

        public static float AsFloat(uint val) { unsafe { return *((float*)&val); } }

        public static uint AsUInt(float val) { unsafe { return *((uint*)&val); } }
    }
}
