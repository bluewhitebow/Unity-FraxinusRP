using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    //-----------------------------------------------------------------------------
    // light extension
    //-----------------------------------------------------------------------------
    static class VisibleLightExtensionMethods
    {
        public struct VisibleLightAxisAndPosition
        {
            public Vector3 position;
            public Vector3 forward;
            public Vector3 up;
            public Vector3 right;
        }

        public static Vector3 GetPosition(this VisibleLight value)
        {
            return value.localToWorldMatrix.GetColumn(3);
        }

        public static Vector3 GetForward(this VisibleLight value)
        {
            return value.localToWorldMatrix.GetColumn(2);
        }

        public static Vector3 GetUp(this VisibleLight value)
        {
            return value.localToWorldMatrix.GetColumn(1);
        }

        public static Vector3 GetRight(this VisibleLight value)
        {
            return value.localToWorldMatrix.GetColumn(0);
        }

        public static VisibleLightAxisAndPosition GetAxisAndPosition(this VisibleLight value)
        {
            var matrix = value.localToWorldMatrix;
            VisibleLightAxisAndPosition output;
            output.position = matrix.GetColumn(3);
            output.forward = matrix.GetColumn(2);
            output.up = matrix.GetColumn(1);
            output.right = matrix.GetColumn(0);
            return output;
        }
    }

    //-----------------------------------------------------------------------------
    // structure definition
    //-----------------------------------------------------------------------------

    [GenerateHLSL]
    internal enum LightVolumeType
    {
        Cone,
        Sphere,
        Box,
        Count
    }

    [GenerateHLSL]
    internal enum LightCategory
    {
        Punctual,
        Area,
        Env,
        Decal,
        Count
    }

    [GenerateHLSL]
    internal enum LightFeatureFlags
    {
        // Light bit mask must match LightDefinitions.s_LightFeatureMaskFlags value
        Punctual = 1 << 12,
        Area = 1 << 13,
        Directional = 1 << 14,
        Env = 1 << 15,
        Sky = 1 << 16,
        SSRefraction = 1 << 17,
        SSReflection = 1 << 18,
        // If adding more light be sure to not overflow LightDefinitions.s_LightFeatureMaskFlags
    }

    // Caution: Order is important and is use for optimization in light loop
    [GenerateHLSL]
    internal enum GPULightType
    {
        Directional,
        Point,
        Spot,
        ProjectorPyramid,
        ProjectorBox,

        // AreaLight
        // Tube, // Keep Line lights before Rectangle. This is needed because of a compiler bug (see LightLoop.hlsl)
        // Rectangle,

        // Currently not supported in real time (just use for reference)
        // Disc,
        // Sphere,
    };

    //Do not change these numbers!!
    //Its not a full power of 2 because the last light slot is reserved.
    internal enum FPTLMaxLightSizes
    {
        Low = 31,
        High = 63
    }

    [GenerateHLSL]
    class LightDefinitions
    {
        public static int maxNrBigTileLightsPlusOne = 512;      // may be overkill but the footprint is 2 bits per pixel using uint16.
        public static float viewportScaleZ = 1.0f;
        public static int useLeftHandCameraSpace = 1;

        public static int tileSizeFptl = 16;
        public static int tileSizeClustered = 32;
        public static int tileSizeBigTile = 64;

        // Tile indexing constants for indirect dispatch deferred pass : [2 bits for eye index | 15 bits for tileX | 15 bits for tileY]
        public static int tileIndexMask = 0x7FFF;
        public static int tileIndexShiftX = 0;
        public static int tileIndexShiftY = 15;
        public static int tileIndexShiftEye = 30;

        // feature variants
        public static int numFeatureVariants = 29;

        // light list limits
        public static int lightListMaxCoarseEntries = 64;
        public static int lightClusterMaxCoarseEntries = 128;

        // We have room for ShaderConfig.FPTLMaxLightCount lights, plus 1 implicit value for length.
        // We allocate only 16 bits per light index & length, thus we divide by 2, and store in a word buffer.
        /// <summary>
        /// Maximum number of lights for a fine pruned light tile. This number can only be the prespecified possibilities in FPTLMaxLightSizes
        /// Lower count will mean some memory savings.
        /// Note: For any rendering bigger than 4k (in native) it is recommended to use Low count per tile, to avoid possible artifacts.
        /// </summary>
        public static int lightDwordPerFptlTile = (((int)FPTLMaxLightSizes.High + 1)) / 2;
        public static int lightClusterPackingCountBits = (int)Mathf.Ceil(Mathf.Log(Mathf.NextPowerOfTwo((int)FPTLMaxLightSizes.High), 2));
        public static int lightClusterPackingCountMask = (1 << lightClusterPackingCountBits) - 1;
        public static int lightClusterPackingOffsetBits = 32 - lightClusterPackingCountBits;
        public static int lightClusterPackingOffsetMask = (1 << lightClusterPackingOffsetBits) - 1;

        // Following define the maximum number of bits use in each feature category.
        public static uint lightFeatureMaskFlags = 0xFFF000;
        public static uint lightFeatureMaskFlagsOpaque = 0xFFF000 & ~((uint)LightFeatureFlags.SSRefraction); // Opaque don't support screen space refraction
        public static uint lightFeatureMaskFlagsTransparent = 0xFFF000 & ~((uint)LightFeatureFlags.SSReflection); // Transparent don't support screen space reflection
        public static uint materialFeatureMaskFlags = 0x000FFF;   // don't use all bits just to be safe from signed and/or float conversions :/

        // Screen space shadow flags
        public static uint rayTracedScreenSpaceShadowFlag = 0x1000;
        public static uint screenSpaceColorShadowFlag = 0x100;
        public static uint invalidScreenSpaceShadow = 0xff;
        public static uint screenSpaceShadowIndexMask = 0xff;

        //Contact shadow bit definitions
        public static int contactShadowFadeBits = 8;
        public static int contactShadowMaskBits = 32 - contactShadowFadeBits;
        public static int contactShadowFadeMask = (1 << contactShadowFadeBits) - 1;
        public static int contactShadowMaskMask = (1 << contactShadowMaskBits) - 1;

    }

    [GenerateHLSL]
    struct SFiniteLightBound
    {
        public Vector3 boxAxisX; // Scaled by the extents (half-size)
        public Vector3 boxAxisY; // Scaled by the extents (half-size)
        public Vector3 boxAxisZ; // Scaled by the extents (half-size)
        public Vector3 center;   // Center of the bounds (box) in camera space
        public float scaleXY;  // Scale applied to the top of the box to turn it into a truncated pyramid (X = Y)
        public float radius;     // Circumscribed sphere for the bounds (box)
    };

    [GenerateHLSL]
    struct LightVolumeData
    {
        public Vector3 lightPos;     // Of light's "origin"
        public uint lightVolume;     // Type index

        public Vector3 lightAxisX;   // Normalized
        public uint lightCategory;   // Category index

        public Vector3 lightAxisY;   // Normalized
        public float radiusSq;       // Cone and sphere: light range squared

        public Vector3 lightAxisZ;   // Normalized
        public float cotan;          // Cone: cotan of the aperture (half-angle)

        public Vector3 boxInnerDist; // Box: extents (half-size) of the inner box
        public uint featureFlags;

        public Vector3 boxInvRange; // Box: 1 / (OuterBoxExtents - InnerBoxExtents)
        public float unused2;
    };

    /// <summary>
    /// unsafe for array
    /// </summary>
    [GenerateHLSL(needAccessors = false, generateCBuffer = true)]
    //unsafe struct ShaderVariablesLightList
    struct ShaderVariablesLightList
    {
        public Matrix4x4 g_mInvScrProjectionArr;
        public Matrix4x4 g_mScrProjectionArr;
        public Matrix4x4 g_mInvProjectionArr;
        public Matrix4x4 g_mProjectionArr;

        public Vector4 g_screenSize;

        public Vector2Int g_viDimensions;
        public int g_iNrVisibLights;
        public uint g_isOrthographic;

        public uint g_BaseFeatureFlags;
        public int g_iNumSamplesMSAA;
        public uint _EnvLightIndexShift;
        public uint _DecalIndexShift;

        // From HDRP ShaderVariablesGlobal
        // Tile/Cluster
        public uint _NumTileFtplX;
        public uint _NumTileFtplY;
        public float g_fClustScale;
        public float g_fClustBase;

        public float g_fNearPlane;
        public float g_fFarPlane;
        public int g_iLog2NumClusters; // We need to always define these to keep constant buffer layouts compatible
        public uint g_isLogBaseBufferEnabled;

        public uint _NumTileClusteredX;
        public uint _NumTileClusteredY;
        public uint _DirectionalLightCount;
        public int _EnvSliceSize; // Unused

        //public uint _EnableDecalLayers;
    }

    [GenerateHLSL(PackingRules.Exact, false)]
    internal struct GPULightData
    {
        // Packing order depends on chronological access to avoid cache misses
        // Make sure to respect the 16-byte alignment
        public Vector3 lightPosWS;
        public uint lightLayerMask;

        public Vector3 lightColor;
        public int lightFlags;

        public Vector4 lightAttenuation;

        public Vector3 lightDirection;
        public int shadowLightIndex;

        public Vector4 lightOcclusionProbInfo;

        public int cookieLightIndex;
        public int shadowType;
        public float minRoughness;
        public float __unused1__;

        public float baseContribution;
        public float rimContribution;
        public float outlineContribution;
        public float __unused2__;
    };

    [GenerateHLSL(PackingRules.Exact, false)]
    struct DirectionalLightData
    {
        // Packing order depends on chronological access to avoid cache misses
        // Make sure to respect the 16-byte alignment
        public Vector3 lightPosWS;
        public uint lightLayerMask;

        public Vector3 lightColor;
        public int lightFlags;

        public Vector4 lightAttenuation;

        public Vector3 lightDirection;
        public int shadowlightIndex;

        public float minRoughness;
        public float lightDimmer;       //TODO: make it used
        public float diffuseDimmer;     //TODO: make it used
        public float specularDimmer;    //TODO: make it used
    };

    [GenerateHLSL(PackingRules.Exact, false)]
    struct EnvLightData
    {
        // EnvLightData is in ReflectionProbeManager, we just need this struct for index.
    }

    public class GPULights : IDisposable
    {
        static class ShaderConstants
        {
            public static readonly int g_LightBounds = Shader.PropertyToID("g_LightBounds");
            public static readonly int g_vBoundsBuffer = Shader.PropertyToID("g_vBoundsBuffer");
            public static readonly int g_LightVolumeData = Shader.PropertyToID("g_LightVolumeData");
            public static readonly int g_vLightList = Shader.PropertyToID("g_vLightList");
            public static readonly int ShaderVariablesLightList = Shader.PropertyToID("ShaderVariablesLightList");
            public static readonly int g_CoarseLightList = Shader.PropertyToID("g_CoarseLightList");
            public static readonly int g_vLayeredLightList = Shader.PropertyToID("g_vLayeredLightList");
            public static readonly int g_LayeredOffset = Shader.PropertyToID("g_LayeredOffset");
            public static readonly int g_LayeredSingleIdxBuffer = Shader.PropertyToID("g_LayeredSingleIdxBuffer");
            public static readonly int g_vLightListCluster = Shader.PropertyToID("g_vLightListCluster");
            public static readonly int g_vLayeredOffsetsBuffer = Shader.PropertyToID("g_vLayeredOffsetsBuffer");
            public static readonly int g_logBaseBuffer = Shader.PropertyToID("g_logBaseBuffer");
            public static readonly int g_GPULightDatas = Shader.PropertyToID("g_GPULightDatas");
            public static readonly int g_DirectionalLightDatas = Shader.PropertyToID("g_DirectionalLightDatas");
        }
        static class Profiling
        {
            public static ProfilingSampler gpuLightsGlobalAsync = new ProfilingSampler(nameof(gpuLightsGlobalAsync));
            public static ProfilingSampler gpuLights = new ProfilingSampler(nameof(gpuLights));
        }

        private class SetupLightPassData
        {
            internal RenderingData renderingData;
            internal CameraData cameraData;
            internal LightData lightData;
            internal GPULights gpuLights;
        };

        private class GPULightsPassData
        {
            internal LightData lightData;
            internal GPULightsDataBuildSystem gpuLightsDataBuildSystem;

            // Compute Shaders
            internal ComputeShader gpuLightsClearLists;
            internal ComputeShader gpuLightsCoarseCullingCS;
            internal ComputeShader gpuLightsFPTL;
            internal ComputeShader gpuLightsCluster;
            internal int clearKernel;
            internal int screenSpaceAABBKernel;
            internal int coarseCullingLightsKernel;
            internal int clusterCullingLightsKernel;

            internal int nrBigTilesX;
            internal int nrBigTilesY;

            internal int nrClustersX;
            internal int nrClustersY;

            // LightsData
            internal ShaderVariablesLightList lightListCB;
            internal BufferHandle lightBoundsBuffer;
            internal BufferHandle lightVolumeDataBuffer;

            // AABB, CoarseCull Buffer
            internal BufferHandle aabbBoundsBuffer;
            internal BufferHandle coarseLightList;

            // ClusterBuffer
            internal BufferHandle globalLightListAtomic;
            internal BufferHandle perVoxelOffset;
            internal BufferHandle perVoxelLightLists;
            internal BufferHandle perTileLogBaseTweak;
        }

        private class GlobalSyncPassData
        {
            internal GPULightsDataBuildSystem gpuLightsDataBuildSystem;
            internal GPULightsOutPassData outData;
        }

        private static LocalKeyword s_bigTileVolumetricLightListKeyword;

        // Profiling tag
        private static string s_passName = "GPU Lights";
        private static string _screenSpaceAABBTag = "ScreenSpaceAABB";
        private static string _coarseCullingTag = "CoarseCulling";
        private static string _clusterCullingTag = "ClusterCulling";
        private static string _clearLightListsTag = "ClearLightLists";
        private static ProfilingSampler s_screenSpaceAABBSampler = new ProfilingSampler(_screenSpaceAABBTag);
        private static ProfilingSampler s_coarseCullingSampler = new ProfilingSampler(_coarseCullingTag);
        private static ProfilingSampler s_clusterCullingSampler = new ProfilingSampler(_clusterCullingTag);
        private static ProfilingSampler s_clearLightListsSampler = new ProfilingSampler(_clearLightListsTag);

        // Public Variables
        internal ShaderVariablesLightList lightCBuffer;

        private int s_maxDirectionalLightsOnScreen = 16;
        private int s_maxPunctualLightsOnScreen = 512;
        // TODO: change to m_MaxDirectionalLightsOnScreen + m_MaxPunctualLightsOnScreen(512) + m_MaxAreaLightsOnScreen + m_MaxEnvLightsOnScreen
        private int s_maxLightOnScreen = 16 + 512;

        // Private Variables
        private ComputeShader _gpulightsCS_ClearLists;
        private ComputeShader _gpuLightsCS_CoarseCulling;
        private ComputeShader _gpuLightsCS_FPTL;
        private ComputeShader _gpuLightsCS_Cluster;
        private int _clearKernel;
        private int _screenSpaceAABBKernel;
        private int _coarseCullingLightsKernel;
        private int _clusterCullingLightsKernel;

        private LightCookieManager _lightCookieManager;
        private GPULightsDataBuildSystem _gpuLightsDataBuildSystem;
        private ReflectionProbeManager _reflectionProbeManager;

        // Constants
        private const int k_log2NumClusters = 6; // accepted range is from 0 to 6 (NR_THREADS is set to 64). NumClusters is 1<<g_iLog2NumClusters
        private const float k_clustLogBase = 1.02f;     // each slice 2% bigger than the previous

        // Statics
        // Left-handed to right-handed
        static readonly Matrix4x4 s_flipMatrixLHSRHS = Matrix4x4.Scale(new Vector3(1, 1, -1));

        internal struct InitParams
        {
            public LightCookieManager lightCookieManager;
            public UltraRenderPipelineRuntimeShaders runtimeShaders;

            static internal InitParams Create()
            {
                InitParams p;
                {
                    var settings = LightCookieManager.Settings.Create();
                    var asset = UltraRenderPipeline.asset;
                    if (asset)
                    {
                        settings.atlas.format = asset.additionalLightsCookieFormat;
                        settings.atlas.resolution = asset.additionalLightsCookieResolution;
                    }

                    p.lightCookieManager = new LightCookieManager(ref settings);

                    p.runtimeShaders = null;
                    if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>(
                        out var ultraRendererShaders))
                    {
                        p.runtimeShaders = ultraRendererShaders;
                    }
                }

                return p;
            }
        }

        public GPULights() : this(InitParams.Create()) { }

        internal GPULights(InitParams initParams)
        {
            _lightCookieManager = initParams.lightCookieManager;

            var shaderResources = initParams.runtimeShaders;

            _gpulightsCS_ClearLists = shaderResources.gpuLightsClearLists;
            _gpuLightsCS_CoarseCulling = shaderResources.gpuLightsCoarseCullingCS;
            _gpuLightsCS_FPTL = shaderResources.gpuLightsFPTL;
            _gpuLightsCS_Cluster = shaderResources.gpuLightsCluster;

            _clearKernel = _gpulightsCS_ClearLists.FindKernel("ClearList");
            _screenSpaceAABBKernel = _gpuLightsCS_CoarseCulling.FindKernel("ScreenSpaceAABB");
            _coarseCullingLightsKernel = _gpuLightsCS_CoarseCulling.FindKernel("CoarseCullingLights");
            _clusterCullingLightsKernel = _gpuLightsCS_Cluster.FindKernel("ClusterCullingLights");

            lightCBuffer = new ShaderVariablesLightList();

            _reflectionProbeManager = ReflectionProbeManager.Create();
            _gpuLightsDataBuildSystem = new GPULightsDataBuildSystem();
        }

        public void Dispose()
        {
            _gpuLightsDataBuildSystem.Dispose();
            _reflectionProbeManager.Dispose();
        }

        public void OnCameraCleanup(CommandBuffer cmd)
        {
            // Clean Keyword if need
            cmd.SetKeyword(ShaderGlobalKeywords.GPULightsCluster, false);
        }

        static ProfilingSampler s_setupClusterDeferredLights = new ProfilingSampler("Setup ClusterDeferred Lights");

        public void SetupLights(UnsafeCommandBuffer cmd, RenderingData renderingData,
            CameraData cameraData, LightData lightData)
        {
            _reflectionProbeManager.UpdateGpuData(CommandBufferHelpers.GetNativeCommandBuffer(cmd),
                ref renderingData.cullResults);
        }

        public void SetupRenderGraphLights(RenderGraph renderGraph, RenderingData renderingData, CameraData cameraData, LightData lightData)
        {
            // Forward+ already prepares and declares the common reflection-probe
            // atlas. Preserve that atlas and its matching constants when GPU
            // deferred lighting and Forward+ draws share a camera.
            if (renderingData.usesForwardPlusLightLists)
                return;

            using (var builder = renderGraph.AddUnsafePass<SetupLightPassData>(s_setupClusterDeferredLights.name,
                       out var passData,
                       s_setupClusterDeferredLights))
            {
                passData.renderingData = renderingData;
                passData.cameraData = cameraData;
                passData.lightData = lightData;
                passData.gpuLights = this;

                builder.AllowPassCulling(false);

                builder.SetRenderFunc((SetupLightPassData data, UnsafeGraphContext rgContext) =>
                {
                    data.gpuLights.SetupLights(rgContext.cmd, data.renderingData, data.cameraData, data.lightData);
                });
            }
        }

        public void NewFrame(int maxBoundsCount, DeferredLights deferredLights)
        {
            _gpuLightsDataBuildSystem.NewFrame(maxBoundsCount, deferredLights, _lightCookieManager);
        }

        public void BuildGPULightList(LightData lightData, CameraData cameraData)
        {
            _gpuLightsDataBuildSystem.BuildGPULightList(lightData, cameraData);
        }

        public void BuildEnvLightList(ref NativeArray<VisibleReflectionProbe> reflectionProbes, int reflectionProbeCount, CameraData cameraData)
        {
            _gpuLightsDataBuildSystem.BuildEnvLightList(ref reflectionProbes, reflectionProbeCount, cameraData);
        }

        internal void PreSetup(LightData lightData, CameraData cameraData)
        {
            var desc = cameraData.cameraTargetDescriptor;
            int width = desc.width;
            int height = desc.height;

            var temp = new Matrix4x4();
            temp.SetRow(0, new Vector4(0.5f * width, 0.0f, 0.0f, 0.5f * width));
            temp.SetRow(1, new Vector4(0.0f, 0.5f * height, 0.0f, 0.5f * height));
            temp.SetRow(2, new Vector4(0.0f, 0.0f, 0.5f, 0.5f));
            temp.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

            var temp2 = new Matrix4x4();
            temp2.SetRow(0, new Vector4(1.0f, 0.0f, 0.0f, 0.0f));
            temp2.SetRow(1, new Vector4(0.0f, 1.0f, 0.0f, 0.0f));
            temp2.SetRow(2, new Vector4(0.0f, 0.0f, 0.5f, 0.5f));
            temp2.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

            // camera to screen matrix (and it's inverse)
            {
                //Matrix4x4 gpuProjectionMatrix = cameraData.GetGPUProjectionMatrix(cameraData.IsCameraProjectionMatrixFlipped());
                Matrix4x4 projMatrix = cameraData.GetProjectionMatrix();

                projMatrix *= s_flipMatrixLHSRHS;

                lightCBuffer.g_mScrProjectionArr = temp * projMatrix;
                lightCBuffer.g_mInvScrProjectionArr = lightCBuffer.g_mScrProjectionArr.inverse;

                lightCBuffer.g_mProjectionArr = temp2 * projMatrix;
                lightCBuffer.g_mInvProjectionArr = lightCBuffer.g_mProjectionArr.inverse;
            }

            var scaledCameraWidth = (float)cameraData.cameraTargetDescriptor.width;
            var scaledCameraHeight = (float)cameraData.cameraTargetDescriptor.height;

            if (cameraData.camera.allowDynamicResolution && !cameraData.isSubContext)
            {
                scaledCameraWidth *= ScalableBufferManager.widthScaleFactor;
                scaledCameraHeight *= ScalableBufferManager.heightScaleFactor;
            }

            int envLightsCount = _gpuLightsDataBuildSystem.envLightsCount;
            // Bounds contain punctual lights followed by reflection probes. The
            // Forward additional-light count also includes non-main directionals,
            // which have no bounds and would make culling read unwritten entries.
            int punctualLightsCount = _gpuLightsDataBuildSystem.boundsCount - envLightsCount;
            lightCBuffer.g_iNrVisibLights = _gpuLightsDataBuildSystem.boundsCount;
            lightCBuffer._DirectionalLightCount = (uint)lightData.directionalLightsCount;

            /// <see cref="ScriptableRenderer"/> cmd.SetGlobalVector(ShaderPropertyId.screenSize...
            lightCBuffer.g_screenSize = new Vector4(scaledCameraWidth, scaledCameraHeight, 1.0f / scaledCameraWidth, 1.0f / scaledCameraHeight);
            lightCBuffer.g_viDimensions = new Vector2Int((int)scaledCameraWidth, (int)scaledCameraHeight);
            lightCBuffer.g_isOrthographic = cameraData.camera.orthographic ? 1u : 0u;
            //lightCBuffer.g_BaseFeatureFlags = 0; // Filled for each individual pass.
            //lightCBuffer.g_iNumSamplesMSAA = msaaSamples;
            lightCBuffer._EnvLightIndexShift = (uint)punctualLightsCount;
            lightCBuffer._DecalIndexShift = (uint)_gpuLightsDataBuildSystem.boundsCount;

            const float C = (float)(1 << k_log2NumClusters);
            var geomSeries = (1.0 - Mathf.Pow(k_clustLogBase, C)) / (1 - k_clustLogBase); // geometric series: sum_k=0^{C-1} base^k

            // Tile/Cluster
            lightCBuffer._NumTileFtplX = (uint)RenderingUtils.DivRoundUp(width, LightDefinitions.tileSizeFptl);
            lightCBuffer._NumTileFtplY = (uint)RenderingUtils.DivRoundUp(height, LightDefinitions.tileSizeFptl);
            lightCBuffer.g_fClustScale = (float)(geomSeries / (cameraData.camera.farClipPlane - cameraData.camera.nearClipPlane)); ;
            lightCBuffer.g_fClustBase = k_clustLogBase;
            lightCBuffer.g_fNearPlane = cameraData.camera.nearClipPlane;
            lightCBuffer.g_fFarPlane = cameraData.camera.farClipPlane;
            lightCBuffer.g_iLog2NumClusters = k_log2NumClusters;
            lightCBuffer.g_isLogBaseBufferEnabled = 1;// Need depth
            lightCBuffer._NumTileClusteredX = (uint)RenderingUtils.DivRoundUp(width, LightDefinitions.tileSizeClustered);
            lightCBuffer._NumTileClusteredY = (uint)RenderingUtils.DivRoundUp(height, LightDefinitions.tileSizeClustered);
        }

        private void InitResources(RenderGraph renderGraph, GPULightsPassData passData, LightData lightData, GPULightsOutPassData outData, CameraData cameraData)
        {
            // Copy the constant buffer into the parameter struct.
            passData.lightListCB = lightCBuffer;

            passData.lightData = lightData;
            passData.gpuLightsDataBuildSystem = _gpuLightsDataBuildSystem;

            // Compute Shaders
            passData.gpuLightsClearLists = _gpulightsCS_ClearLists;
            passData.gpuLightsCoarseCullingCS = _gpuLightsCS_CoarseCulling;
            passData.gpuLightsCluster = _gpuLightsCS_Cluster;
            passData.clearKernel = _clearKernel;
            passData.screenSpaceAABBKernel = _screenSpaceAABBKernel;
            passData.coarseCullingLightsKernel = _coarseCullingLightsKernel;
            passData.clusterCullingLightsKernel = _clusterCullingLightsKernel;

            var width = cameraData.cameraTargetDescriptor.width;
            var height = cameraData.cameraTargetDescriptor.height;
            passData.nrBigTilesX = RenderingUtils.DivRoundUp(width, 64);
            passData.nrBigTilesY = RenderingUtils.DivRoundUp(height, 64);

            int allLightsBufferSize = s_maxLightOnScreen;

            passData.lightBoundsBuffer = renderGraph.CreateBuffer(new BufferDesc(allLightsBufferSize, Marshal.SizeOf(typeof(SFiniteLightBound)), GraphicsBuffer.Target.Structured) { name = "LightBoundsBuffer" });
            passData.lightVolumeDataBuffer = renderGraph.CreateBuffer(new BufferDesc(allLightsBufferSize, Marshal.SizeOf(typeof(LightVolumeData)), GraphicsBuffer.Target.Structured) { name = "LightVolumeDataBuffer" });

            passData.aabbBoundsBuffer = renderGraph.CreateBuffer(new BufferDesc(allLightsBufferSize, Marshal.SizeOf(typeof(float4)), GraphicsBuffer.Target.Structured) { name = "aabbBoundsBuffer" });
            passData.coarseLightList = renderGraph.CreateBuffer(new BufferDesc(LightDefinitions.maxNrBigTileLightsPlusOne * passData.nrBigTilesX * passData.nrBigTilesY, sizeof(uint), GraphicsBuffer.Target.Structured) { name = "CoarseLightList" });

            // Cluster buffers
            passData.nrClustersX = (width + LightDefinitions.tileSizeClustered - 1) / LightDefinitions.tileSizeClustered;
            passData.nrClustersY = (height + LightDefinitions.tileSizeClustered - 1) / LightDefinitions.tileSizeClustered;
            var nrClusterTiles = passData.nrClustersX * passData.nrClustersY;

            passData.globalLightListAtomic = renderGraph.CreateBuffer(new BufferDesc(1, sizeof(uint), GraphicsBuffer.Target.Structured) { name = "GlobalLightListAtomic" });
            passData.perVoxelLightLists = renderGraph.CreateBuffer(new BufferDesc(32 * (1 << k_log2NumClusters) * nrClusterTiles, sizeof(uint), GraphicsBuffer.Target.Structured) { name = "PerVoxelLightLists" });
            passData.perVoxelOffset = renderGraph.CreateBuffer(new BufferDesc((int)LightCategory.Count * (1 << k_log2NumClusters) * nrClusterTiles, sizeof(uint), GraphicsBuffer.Target.Structured) { name = "PerVoxelOffset" });
            passData.perTileLogBaseTweak = renderGraph.CreateBuffer(new BufferDesc(nrClusterTiles, sizeof(float), GraphicsBuffer.Target.Structured) { name = "PerTileLogBaseTweak" });

            // Outdata
            outData.lightListCB = passData.lightListCB;
            outData.gpuLightsData = renderGraph.CreateBuffer(new BufferDesc(s_maxPunctualLightsOnScreen, Marshal.SizeOf(typeof(GPULightData)), GraphicsBuffer.Target.Structured) { name = "GPULightsData" });
            outData.directionalLightsData = renderGraph.CreateBuffer(new BufferDesc(s_maxDirectionalLightsOnScreen, Marshal.SizeOf(typeof(DirectionalLightData))){ name = "directionalLightsData" });

            outData.coarseLightList = passData.coarseLightList;

            outData.perVoxelOffset = passData.perVoxelOffset;
            outData.perVoxelLightLists = passData.perVoxelLightLists;
            outData.perTileLogBaseTweak = passData.perTileLogBaseTweak;
        }

        public void Render(RenderGraph renderGraph, ContextContainer frameData)
        {
            LightData lightData = frameData.Get<LightData>();
            CameraData cameraData = frameData.Get<CameraData>();
            ResourceData resourceData = frameData.Get<ResourceData>();

            // We need reBuild GPULightsData at main thread.
            _gpuLightsDataBuildSystem.ReBuildGPULightsDataBuffer(lightData);

            using (var builder = renderGraph.AddComputePass<GPULightsPassData>(s_passName, out var passData, ProfilingSampler.Get(UltraRPProfileId.GPULights)))
            {
                // Set passData
                GPULightsOutPassData outPassData = cameraData.isSubContext ? frameData.Get<GPULightsOutPassData>() : frameData.GetOrCreate<GPULightsOutPassData>();
                InitResources(renderGraph, passData, lightData, outPassData, cameraData);

                // Declare input/output
                builder.UseBuffer(passData.lightBoundsBuffer, AccessFlags.Write);
                builder.UseBuffer(passData.lightVolumeDataBuffer, AccessFlags.Write);

                builder.UseBuffer(passData.aabbBoundsBuffer, AccessFlags.Write);
                builder.UseBuffer(passData.coarseLightList, AccessFlags.Write);

                builder.UseBuffer(passData.globalLightListAtomic, AccessFlags.Write);
                builder.UseBuffer(passData.perVoxelOffset, AccessFlags.Write);
                builder.UseBuffer(passData.perVoxelLightLists, AccessFlags.Write);
                builder.UseBuffer(passData.perTileLogBaseTweak, AccessFlags.Write);

                // Setup builder state
#if ASYNC_COMPUTE
                builder.EnableAsyncCompute(true);
#endif
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((GPULightsPassData data, ComputeGraphContext context) =>
                {
                    ExecutePass(data, context);
                });
            }
        }

        static void ExecutePass(GPULightsPassData data, ComputeGraphContext context)
        {
            int totalLightCount = data.lightListCB.g_iNrVisibLights;
            if (totalLightCount == 0)
            {
                ClearAllLightLists(context.cmd, data);
                return;
            }

            ComputeCommandBuffer cmd = context.cmd;
            // Set lightsData here.
            cmd.SetBufferData(data.lightBoundsBuffer, data.gpuLightsDataBuildSystem.lightBounds, 0, 0,
                data.gpuLightsDataBuildSystem.boundsCount);
            cmd.SetBufferData(data.lightVolumeDataBuffer, data.gpuLightsDataBuildSystem.lightVolumes, 0, 0,
                data.gpuLightsDataBuildSystem.boundsCount);

            // Push Constant buffer
            ConstantBufferCompute.Push(cmd, data.lightListCB, data.gpuLightsCoarseCullingCS,
                ShaderConstants.ShaderVariablesLightList);

            // GenerateLightsScreenSpaceAABBs
            using (new ProfilingScope(cmd, s_screenSpaceAABBSampler))
            {
                cmd.SetComputeBufferParam(data.gpuLightsCoarseCullingCS, data.screenSpaceAABBKernel, ShaderConstants.g_LightBounds, data.lightBoundsBuffer);// in
                cmd.SetComputeBufferParam(data.gpuLightsCoarseCullingCS, data.screenSpaceAABBKernel, ShaderConstants.g_vBoundsBuffer, data.aabbBoundsBuffer);// out

                const int threadsPerLight = 4;  // Shader: THREADS_PER_LIGHT (4)
                const int threadsPerGroup = 64; // Shader: THREADS_PER_GROUP (64)

                int groupCount = RenderingUtils.DivRoundUp(totalLightCount * threadsPerLight, threadsPerGroup);
                cmd.DispatchCompute(data.gpuLightsCoarseCullingCS, data.screenSpaceAABBKernel, groupCount, 1, 1);
            }

            // CoarseCullingLights
            using (new ProfilingScope(cmd, s_coarseCullingSampler))
            {
                cmd.SetComputeBufferParam(data.gpuLightsCoarseCullingCS, data.coarseCullingLightsKernel, ShaderConstants.g_LightVolumeData, data.lightVolumeDataBuffer);// in
                cmd.SetComputeBufferParam(data.gpuLightsCoarseCullingCS, data.coarseCullingLightsKernel, ShaderConstants.g_LightBounds, data.lightBoundsBuffer);// in
                cmd.SetComputeBufferParam(data.gpuLightsCoarseCullingCS, data.coarseCullingLightsKernel, ShaderConstants.g_vBoundsBuffer, data.aabbBoundsBuffer);// in
                cmd.SetComputeBufferParam(data.gpuLightsCoarseCullingCS, data.coarseCullingLightsKernel, ShaderConstants.g_vLightList, data.coarseLightList);// out

                cmd.DispatchCompute(data.gpuLightsCoarseCullingCS, data.coarseCullingLightsKernel, data.nrBigTilesX, data.nrBigTilesY, 1);
            }

            // FPTLLights
            {
                // no implementation
            }

            // ClusterLights
            using (new ProfilingScope(cmd, s_clusterCullingSampler))
            {
                cmd.SetComputeBufferParam(data.gpuLightsCluster, data.clusterCullingLightsKernel, ShaderConstants.g_LightVolumeData, data.lightVolumeDataBuffer);// in
                cmd.SetComputeBufferParam(data.gpuLightsCluster, data.clusterCullingLightsKernel, ShaderConstants.g_LightBounds, data.lightBoundsBuffer);// in

                cmd.SetComputeBufferParam(data.gpuLightsCluster, data.clusterCullingLightsKernel, ShaderConstants.g_vBoundsBuffer, data.aabbBoundsBuffer);// in
                cmd.SetComputeBufferParam(data.gpuLightsCluster, data.clusterCullingLightsKernel, ShaderConstants.g_CoarseLightList, data.coarseLightList);// in

                cmd.SetComputeBufferParam(data.gpuLightsCluster, data.clusterCullingLightsKernel, ShaderConstants.g_vLayeredLightList, data.perVoxelLightLists);// out
                cmd.SetComputeBufferParam(data.gpuLightsCluster, data.clusterCullingLightsKernel, ShaderConstants.g_LayeredOffset, data.perVoxelOffset);// out
                cmd.SetComputeBufferParam(data.gpuLightsCluster, data.clusterCullingLightsKernel, ShaderConstants.g_LayeredSingleIdxBuffer, data.globalLightListAtomic);// used
                cmd.SetComputeBufferParam(data.gpuLightsCluster, data.clusterCullingLightsKernel, ShaderConstants.g_logBaseBuffer, data.perTileLogBaseTweak);// out
                ConstantBufferCompute.Push(cmd, data.lightListCB, data.gpuLightsCluster, ShaderConstants.ShaderVariablesLightList);

                cmd.DispatchCompute(data.gpuLightsCluster, data.clusterCullingLightsKernel, data.nrClustersX, data.nrClustersY, 1);
            }
        }

        static void ClearLightList(ComputeCommandBuffer cmd, GPULightsPassData data, GraphicsBuffer bufferToClear)
        {
            Vector2 countAndOffset = new Vector2Int(bufferToClear.count, 0);
            int totalNumberOfGroupsNeeded = RenderingUtils.DivRoundUp(bufferToClear.count, 64);
            const int maxAllowedGroups = 65535;// On higher resolutions we might end up with more than 65535 group which is not allowed, so we need to to have multiple dispatches.

            cmd.SetComputeBufferParam(data.gpuLightsClearLists, data.clearKernel, "_LightListToClear", bufferToClear);

            int i = 0;
            while (totalNumberOfGroupsNeeded > 0)
            {
                countAndOffset.y = maxAllowedGroups * i;
                cmd.SetComputeVectorParam(data.gpuLightsClearLists, "_LightListEntriesAndOffset", countAndOffset);

                int currGroupCount = Math.Min(maxAllowedGroups, totalNumberOfGroupsNeeded);

                cmd.DispatchCompute(data.gpuLightsClearLists, data.clearKernel, currGroupCount, 1, 1);

                totalNumberOfGroupsNeeded -= currGroupCount;
                i++;
            }
        }

        static void ClearAllLightLists(ComputeCommandBuffer cmd, GPULightsPassData data)
        {
            using (new ProfilingScope(cmd, s_clearLightListsSampler))
            {
                if (data.coarseLightList.IsValid())
                {
                    ClearLightList(cmd, data, data.coarseLightList);
                    ClearLightList(cmd, data, data.perVoxelOffset);
                }
            }
        }

        static void ResolveGPULightsData(ComputeCommandBuffer cmd, GlobalSyncPassData data)
        {
            var outData = data.outData;
            cmd.SetBufferData(outData.gpuLightsData, data.gpuLightsDataBuildSystem.gpuLightsData, 0, 0, data.gpuLightsDataBuildSystem.lightsCount);
            cmd.SetBufferData(outData.directionalLightsData, data.gpuLightsDataBuildSystem.directionalLightsData, 0, 0, data.gpuLightsDataBuildSystem.directionalLightCount);

            BindGlobals(cmd, outData);
        }

        private static void BindGlobals(ComputeCommandBuffer cmd, GPULightsOutPassData outData)
        {
            // The constants and grid buffers belong to this scope.
            ConstantBufferCompute.PushGlobal(cmd, outData.lightListCB, ShaderConstants.ShaderVariablesLightList);
            // Lights data ref
            cmd.SetGlobalBuffer(ShaderConstants.g_GPULightDatas, outData.gpuLightsData);
            cmd.SetGlobalBuffer(ShaderConstants.g_DirectionalLightDatas, outData.directionalLightsData);
            // Coarse cull result
            cmd.SetGlobalBuffer(ShaderConstants.g_CoarseLightList, outData.coarseLightList);
            // Cluster cull result
            cmd.SetGlobalBuffer(ShaderConstants.g_vLightListCluster, outData.perVoxelLightLists);
            cmd.SetGlobalBuffer(ShaderConstants.g_vLayeredOffsetsBuffer, outData.perVoxelOffset);
            cmd.SetGlobalBuffer(ShaderConstants.g_logBaseBuffer, outData.perTileLogBaseTweak);

            cmd.SetKeyword(ShaderGlobalKeywords.GPULightsCluster, true);

        }

        private class GlobalBindingsPassData
        {
            internal GPULightsOutPassData data;
        }

        internal static void RenderGlobalBindings(RenderGraph graph, GPULightsOutPassData data)
        {
            using var builder = graph.AddComputePass<GlobalBindingsPassData>("Restore Context GPU Light Grid", out var pass);
            pass.data = data;
            builder.UseBuffer(data.gpuLightsData, AccessFlags.Read);
            builder.UseBuffer(data.directionalLightsData, AccessFlags.Read);
            builder.UseBuffer(data.coarseLightList, AccessFlags.Read);
            builder.UseBuffer(data.perVoxelOffset, AccessFlags.Read);
            builder.UseBuffer(data.perVoxelLightLists, AccessFlags.Read);
            builder.UseBuffer(data.perTileLogBaseTweak, AccessFlags.Read);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((GlobalBindingsPassData p, ComputeGraphContext ctx) => BindGlobals(ctx.cmd, p.data));
        }

        internal void RenderSetGlobalSync(RenderGraph renderGraph, ContextContainer frameData)
        {
            using (var builder = renderGraph.AddComputePass<GlobalSyncPassData>("GPU Lights Global Sync",
                       out var passData, Profiling.gpuLightsGlobalAsync))
            {
                if (!frameData.Contains<GPULightsOutPassData>())
                    return;

                var outData = frameData.Get<GPULightsOutPassData>();
                passData.gpuLightsDataBuildSystem = _gpuLightsDataBuildSystem;
                passData.outData = outData;

                builder.UseBuffer(outData.gpuLightsData, AccessFlags.Write);
                builder.UseBuffer(outData.directionalLightsData, AccessFlags.Write);

                builder.UseBuffer(outData.coarseLightList);

                builder.UseBuffer(outData.perVoxelOffset);
                builder.UseBuffer(outData.perVoxelLightLists);
                builder.UseBuffer(outData.perTileLogBaseTweak);

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((GlobalSyncPassData data, ComputeGraphContext context) =>
                {
                    ResolveGPULightsData(context.cmd, data);
                });
            }
        }
    }
}
