using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

using FraxinusEngine.Base;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    public class ForwardLights : LightCoreBase
    {
        static class LightConstantBuffer
        {
            public static int _MainLightPosition = Shader.PropertyToID("_MainLightPosition");
            public static int _MainLightColor = Shader.PropertyToID("_MainLightColor");
            public static int _MainLightOcclusionProbesChannel = Shader.PropertyToID("_MainLightOcclusionProbes");
            public static int _MainLightLayerMask = Shader.PropertyToID("_MainLightLayerMask");

            public static int _AdditionalLightsCount = Shader.PropertyToID("_AdditionalLightsCount");
            public static int _AdditionalLightsPosition = Shader.PropertyToID("_AdditionalLightsPosition");
            public static int _AdditionalLightsColor = Shader.PropertyToID("_AdditionalLightsColor");
            public static int _AdditionalLightsAttenuation = Shader.PropertyToID("_AdditionalLightsAttenuation");
            public static int _AdditionalLightsSpotDir = Shader.PropertyToID("_AdditionalLightsSpotDir");
            public static int _AdditionalLightOcclusionProbeChannel = Shader.PropertyToID("_AdditionalLightsOcclusionProbes");
            public static int _AdditionalLightsLayerMasks = Shader.PropertyToID("_AdditionalLightsLayerMasks");

            public static int _AdditionalLightsBuffer = Shader.PropertyToID("_AdditionalLightsBuffer");
            public static int _AdditionalLightsIndices = Shader.PropertyToID("_AdditionalLightsIndices");
        }

        internal struct InitParams
        {
            public LightCookieManager lightCookieManager;

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
                }

                return p;
            }
        }

        const string k_setupLightConstants = "Setup Light Constants";
        private static readonly ProfilingSampler _profilingSampler = new ProfilingSampler(k_setupLightConstants);
        private static readonly ProfilingSampler _profilingSamplerFPSetup = new ProfilingSampler("Forward+ Setup");
        private static readonly ProfilingSampler _profilingSamplerFPComplete = new ProfilingSampler("Forward+ Complete");
        private static readonly ProfilingSampler _profilingSamplerFPUpload = new ProfilingSampler("Forward+ Upload");
        MixedLightingSetup _mixedLightingSetup;

        Vector4[] _additionalLightPositions;
        Vector4[] _additionalLightColors;
        Vector4[] _additionalLightAttenuations;
        Vector4[] _additionalLightSpotDirections;
        Vector4[] _additionalLightOcclusionProbeChannels;
        float[] _additionalLightsLayerMasks;

        bool _useStructuredBuffer;
        bool _useForwardPlus;
        int _directionalLightCount;
        int _actualTileWidth;
        int2 _tileResolution;

        JobHandle _cullingHandle;
        NativeArray<uint> _zBins;
        GraphicsBuffer _zBinsBuffer;
        NativeArray<uint> _tileMasks;
        GraphicsBuffer _tileMasksBuffer;

        int _wordsPerTile;
        float _zBinScale;
        float _zBinOffset;
        int _lightCount;
        int _binCount;

        LightCookieManager _lightCookieManager;
        ReflectionProbeManager _reflectionProbeManager;

        internal LightCookieManager lightCookieManager => _lightCookieManager;
        internal ReflectionProbeManager reflectionProbeManager => _reflectionProbeManager;

        public bool useforwardLight
        {
            get => _useForwardPlus;
            set
            {
                if (value != _useForwardPlus)
                {
                    _useForwardPlus = value;
                    if (_useForwardPlus)
                        CreateForwardPlusBuffers();
                    else
                        ReleseForwardPlusBuffers();
                }
            }
        }

        public ForwardLights() : this(InitParams.Create()) { }

        internal ForwardLights(InitParams initParams)
        {
            _useStructuredBuffer = RenderingUtils.useStructuredBuffer;

            int maxLights = UltraRenderPipeline.maxVisibleAdditionalLights;
            _additionalLightPositions = new Vector4[maxLights];
            _additionalLightColors = new Vector4[maxLights];
            _additionalLightAttenuations = new Vector4[maxLights];
            _additionalLightSpotDirections = new Vector4[maxLights];
            _additionalLightOcclusionProbeChannels = new Vector4[maxLights];
            _additionalLightsLayerMasks = new float[maxLights];

            if (_useForwardPlus)
                CreateForwardPlusBuffers();

            _reflectionProbeManager = ReflectionProbeManager.Create();
            _lightCookieManager = initParams.lightCookieManager;
        }

        protected override void OnCameraCleanup(CommandBuffer cmd)
        {

        }

        protected override void OnDispose()
        {
            Cleanup();
        }

        static ProfilingSampler s_setupForwardLights = new ProfilingSampler("Setup Forward lights.");
        private class SetupLightPassData
        {
            internal RenderingData renderingData;
            internal CameraData cameraData;
            internal LightData lightData;
            internal ForwardLights forwardLights;
        };

        internal void SetupRenderGraphLights(RenderGraph renderGraph, RenderingData renderingData, CameraData cameraData, LightData lightData)
        {
            _lightCookieManager?.PrepareRenderGraphTextures(renderGraph, lightData);
            using (var builder = renderGraph.AddUnsafePass<SetupLightPassData>(s_setupForwardLights.name, out var passData, s_setupForwardLights))
            {
                passData.renderingData = renderingData;
                passData.cameraData = cameraData;
                passData.lightData = lightData;
                passData.forwardLights = this;

                if (_lightCookieManager != null)
                {
                    builder.UseTexture(_lightCookieManager.mainLightCookieTextureHandle, AccessFlags.Read);
                    var cookieAtlas = _lightCookieManager.additionalLightsCookieAtlasTextureHandle;
                    // A missing cookie uses the read-only default white texture.
                    builder.UseTexture(cookieAtlas, RenderGraphUtils.TextureHandlesEqual(cookieAtlas, renderGraph.defaultResources.whiteTexture)
                        ? AccessFlags.Read : AccessFlags.ReadWrite);
                }

                builder.AllowPassCulling(false);

                builder.SetRenderFunc((SetupLightPassData data, UnsafeGraphContext rgContext) =>
                {
                    data.forwardLights.SetupLights(rgContext.cmd, data.renderingData, data.cameraData, data.lightData);
                });
            }
        }

        private void Cleanup()
        {
            _cullingHandle.Complete();
            ReleseForwardPlusBuffers();

            _lightCookieManager?.Dispose();
            _lightCookieManager = null;
        }
        void CreateForwardPlusBuffers()
        {
            _zBins = new NativeArray<uint>(UltraRenderPipeline.maxZBinWords, Allocator.Persistent);
            _zBinsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Constant, UltraRenderPipeline.maxZBinWords / 4, UnsafeUtility.SizeOf<float4>());
            _zBinsBuffer.name = "UltraRP z-Bin Buffer";
            _tileMasks = new NativeArray<uint>(UltraRenderPipeline.maxTileWords, Allocator.Persistent);
            _tileMasksBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Constant, UltraRenderPipeline.maxTileWords / 4, UnsafeUtility.SizeOf<float4>());
            _tileMasksBuffer.name = "UltraRP Tile Buffer";
        }

        void ReleseForwardPlusBuffers()
        {
            if (_zBins != null) _zBins.Dispose();
            if (_zBinsBuffer != null) _zBinsBuffer.Dispose();
            if (_tileMasks != null) _tileMasks.Dispose();
            if (_tileMasksBuffer != null) _tileMasksBuffer.Dispose();
        }

        static int AlignByteCount(int count, int align) => align * ((count + align - 1) / align);

        // Calculate view planes and viewToViewportScaleBias. This handles projection center in case the projection is off-centered
        void GetViewParams(Camera camera, float4x4 viewToClip, out float viewPlaneBot, out float viewPlaneTop, out float4 viewToViewportScaleBias)
        {
            // We want to calculate `fovHalfHeight = tan(fov / 2)`
            // `projection[1][1]` contains `1 / tan(fov / 2)`
            var viewPlaneHalfSizeInv = math.float2(viewToClip[0][0], viewToClip[1][1]);
            var viewPlaneHalfSize = math.rcp(viewPlaneHalfSizeInv);
            var centerClipSpace = camera.orthographic ? -math.float2(viewToClip[3][0], viewToClip[3][1]) : math.float2(viewToClip[2][0], viewToClip[2][1]);

            viewPlaneBot = centerClipSpace.y * viewPlaneHalfSize.y - viewPlaneHalfSize.y;
            viewPlaneTop = centerClipSpace.y * viewPlaneHalfSize.y + viewPlaneHalfSize.y;
            viewToViewportScaleBias = math.float4(
                viewPlaneHalfSizeInv * 0.5f,
                -centerClipSpace * 0.5f + 0.5f
            );
        }

        public void PreSetup(RenderingData renderingData, CameraData cameraData, LightData lightData)
        {
            if (_useForwardPlus) 
            {
                using var _ = new ProfilingScope(_profilingSamplerFPSetup);

                if (!_cullingHandle.IsCompleted)
                    throw new InvalidOperationException("Forward+ jobs have not completed yet.");

                if (_tileMasks.Length != UltraRenderPipeline.maxTileWords)
                {
                    _zBins.Dispose();
                    _zBinsBuffer.Dispose();
                    _tileMasks.Dispose();
                    _tileMasksBuffer.Dispose();
                    CreateForwardPlusBuffers();
                }
                else
                {
                    unsafe
                    {
                        UnsafeUtility.MemClear(_zBins.GetUnsafePtr(), _zBins.Length * sizeof(uint));
                        UnsafeUtility.MemClear(_tileMasks.GetUnsafePtr(), _tileMasks.Length * sizeof(uint));
                    }
                }

                var camera = cameraData.camera;

                var screenResolution = math.int2(cameraData.pixelWidth, cameraData.pixelHeight);
                var viewCount = 1;

                _lightCount = lightData.visibleLights.Length;
                var lightOffset = 0;
                while (lightOffset < _lightCount && lightData.visibleLights[lightOffset].lightType == LightType.Directional)
                    lightOffset++;

                _lightCount -= lightOffset;
                _directionalLightCount = lightOffset;
                if (lightData.mainLightIndex != -1 && _directionalLightCount != 0)
                    _directionalLightCount -= 1;

                var visibleLights = lightData.visibleLights.GetSubArray(lightOffset, _lightCount);
                var reflectionProbes = renderingData.cullResults.visibleReflectionProbes;
                var reflectionProbeCount = math.min(reflectionProbes.Length, UltraRenderPipeline.maxVisibleReflectionProbes);
                var itemsPerTile = visibleLights.Length + reflectionProbeCount;
                _wordsPerTile = (itemsPerTile + 31) / 32;

                _actualTileWidth = 8 >> 1;
                do
                {
                    _actualTileWidth <<= 1;
                    _tileResolution = (screenResolution + _actualTileWidth - 1) / _actualTileWidth;
                }
                while ((_tileResolution.x * _tileResolution.y * _wordsPerTile * viewCount) > UltraRenderPipeline.maxTileWords);

                // 这里的常数 2，指的是后面 ZBinningJob 中的 HeaderLength
                if (!camera.orthographic)
                {
                    // Use to calculate binIndex = log2(z) * zBinScale + zBinOffset
                    _zBinScale = (UltraRenderPipeline.maxZBinWords / viewCount) / ((math.log2(camera.farClipPlane) - math.log2(camera.nearClipPlane)) * (2 + _wordsPerTile));
                    _zBinOffset = -math.log2(camera.nearClipPlane) * _zBinScale;
                    _binCount = (int)(math.log2(camera.farClipPlane) * _zBinScale + _zBinOffset);
                }
                else
                {
                    // Use to calculate binIndex = z * zBinScale + zBinOffset
                    _zBinScale = (UltraRenderPipeline.maxZBinWords / viewCount) / ((camera.farClipPlane - camera.nearClipPlane) * (2 + _wordsPerTile));
                    _zBinOffset = -camera.nearClipPlane * _zBinScale;
                    _binCount = (int)(camera.farClipPlane * _zBinScale + _zBinOffset);
                }

                _binCount = Math.Max(_binCount, 0);

                var worldToViews = new Fixed2<float4x4>(cameraData.GetViewMatrix(0), cameraData.GetViewMatrix(math.min(1, viewCount - 1)));
                var viewToClips = new Fixed2<float4x4>(cameraData.GetProjectionMatrix(0), cameraData.GetProjectionMatrix(math.min(1, viewCount - 1)));

                // Should probe come after otherProbe?
                static bool IsProbeGreater(VisibleReflectionProbe probe, VisibleReflectionProbe otherProbe)
                {
                    return probe.importance < otherProbe.importance ||
                        (probe.importance == otherProbe.importance && probe.bounds.extents.sqrMagnitude > otherProbe.bounds.extents.sqrMagnitude);
                }

                for (var i = 1; i < reflectionProbeCount; i++)
                {
                    var probe = reflectionProbes[i];
                    var j = i - 1;
                    while (j >= 0 && IsProbeGreater(reflectionProbes[j], probe))
                    {
                        reflectionProbes[j + 1] = reflectionProbes[j];
                        j--;
                    }

                    reflectionProbes[j + 1] = probe;
                }

                var minMaxZs = new NativeArray<float2>(itemsPerTile * viewCount, Allocator.TempJob);

                // 计算每个Light所影响的 z 范围，结果存储再 minMaxZ[index] 中
                var lightMinMaxZJob = new LightMinMaxZJob
                {
                    worldToViews = worldToViews,
                    lights = visibleLights,
                    minMaxZs = minMaxZs.GetSubArray(0, _lightCount * viewCount)
                };
                // Innerloop batch count of 32 is not special, just a handwavy amount to not have too much scheduling overhead nor too little parallelism.
                var lightMinMaxZHandle = lightMinMaxZJob.ScheduleParallel(_lightCount * viewCount, 32, new JobHandle());

                // 计算每个Reflection Probe所影响的 z 范围，结果存储再 minMaxZ[index] 中
                var reflectionProbeMinMaxZJob = new ReflectionProbeMinMaxZJob
                {
                    worldToViews = worldToViews,
                    reflectionProbes = reflectionProbes,
                    minMaxZs = minMaxZs.GetSubArray(_lightCount * viewCount, reflectionProbeCount * viewCount)
                };
                var reflectionProbeMinMaxZHandle = reflectionProbeMinMaxZJob.ScheduleParallel(reflectionProbeCount * viewCount, 32, lightMinMaxZHandle);
                
                // 计算有多少个batch，每128个bin是一个batch
                var zBinningBatchCount = (_binCount + ZBinningJob.batchSize - 1) / ZBinningJob.batchSize;
                var zBinningJob = new ZBinningJob
                {
                    bins = _zBins,
                    minMaxZs = minMaxZs,
                    zBinScale = _zBinScale,
                    zBinOffset = _zBinOffset,
                    binCount = _binCount,
                    wordsPerTile = _wordsPerTile,
                    lightCount = _lightCount,
                    reflectionProbeCount = reflectionProbeCount,
                    batchCount = zBinningBatchCount,
                    viewCount = viewCount,
                    isOrthographic = camera.orthographic
                };
                var zBinningHandle = zBinningJob.ScheduleParallel(zBinningBatchCount * viewCount, 1, reflectionProbeMinMaxZHandle);

                reflectionProbeMinMaxZHandle.Complete();

                GetViewParams(camera, viewToClips[0], out float viewPlaneBottom0, out float viewPlaneTop0, out float4 viewToViewportScaleBias0);
                GetViewParams(camera, viewToClips[1], out float viewPlaneBottom1, out float viewPlaneTop1, out float4 viewToViewportScaleBias1);

                // We want to calculate `fovHalfHeight = tan(fov / 2)`
                // `projection[1][1]` contains `1 / tan(fov / 2)`
                var fovHalfHeights = new Fixed2<float>(1.0f / viewToClips[0][1][1], 1.0f / viewToClips[1][1][1]);
                // Each light needs 1 range for Y, and a range per row. Align to 128-bytes to avoid false sharing.
                var rangesPerItem = AlignByteCount((1 + _tileResolution.y) * UnsafeUtility.SizeOf<InclusiveRange>(), 128) / UnsafeUtility.SizeOf<InclusiveRange>();
                var tileRanges = new NativeArray<InclusiveRange>(rangesPerItem * itemsPerTile * viewCount, Allocator.TempJob);
                var tilingJob = new TilingJob
                {
                    lights = visibleLights,
                    reflectionProbes = reflectionProbes,
                    tileRanges = tileRanges,
                    itemsPerTile = itemsPerTile,
                    rangesPerItem = rangesPerItem,
                    worldToViews = worldToViews,
                    centerOffset = float4.zero,
                    tileScale = (float2)screenResolution / _actualTileWidth,
                    tileScaleInv = _actualTileWidth / (float2)screenResolution,
                    viewPlaneHalfSizes = new Fixed2<float2>(fovHalfHeights[0] * math.float2(cameraData.aspectRatio, 1), fovHalfHeights[1] * math.float2(cameraData.aspectRatio, 1)),
                    viewPlaneHalfSizeInvs = new Fixed2<float2>(math.rcp(fovHalfHeights[0] * math.float2(cameraData.aspectRatio, 1)), math.rcp(fovHalfHeights[1] * math.float2(cameraData.aspectRatio, 1))),
                    tileCount = _tileResolution,
                    near = camera.nearClipPlane,
                    isOrthographic = camera.orthographic
                };

                var tileRangeHandle = tilingJob.ScheduleParallel(itemsPerTile * viewCount, 1, reflectionProbeMinMaxZHandle);

                var expansionJob = new TileRangeExpansionJob
                {
                    tileRanges = tileRanges,
                    tileMasks = _tileMasks,
                    rangesPerItem = rangesPerItem,
                    itemsPerTile = itemsPerTile,
                    wordsPerTile = _wordsPerTile,
                    tileResolution = _tileResolution,
                };

                var tilingHandle = expansionJob.ScheduleParallel(_tileResolution.y * viewCount, 1, tileRangeHandle);
                _cullingHandle = JobHandle.CombineDependencies(
                    minMaxZs.Dispose(zBinningHandle),
                    tileRanges.Dispose(tilingHandle));

                JobHandle.ScheduleBatchedJobs();
            }
        }

        internal void SetupLights(UnsafeCommandBuffer cmd, RenderingData renderingData, CameraData cameraData, LightData lightData)
        {
            int additionalLightsCount = lightData.additionalLightsCount;
            bool additionalLightsPerVertex = lightData.shadeAdditionalLightsPerVertex;

            using (new ProfilingScope(_profilingSampler))
            {
                if (_useForwardPlus)
                {
                    _reflectionProbeManager.UpdateGpuData(CommandBufferHelpers.GetNativeCommandBuffer(cmd), ref renderingData.cullResults);

                    using (new ProfilingScope(_profilingSamplerFPComplete))
                        _cullingHandle.Complete();

                    using (new ProfilingScope(_profilingSamplerFPUpload))
                    {
                        _zBinsBuffer.SetData(_zBins.Reinterpret<float4>(UnsafeUtility.SizeOf<uint>()));
                        _tileMasksBuffer.SetData(_tileMasks.Reinterpret<float4>(UnsafeUtility.SizeOf<uint>()));
                        cmd.SetGlobalConstantBuffer(_zBinsBuffer, "ultraRP_ZBinBuffer", 0, UltraRenderPipeline.maxZBinWords * 4);
                        cmd.SetGlobalConstantBuffer(_tileMasksBuffer, "ultraRP_TileBuffer", 0, UltraRenderPipeline.maxTileWords * 4);
                    }

                    cmd.SetGlobalVector("_FPParams0", math.float4(_zBinScale, _zBinOffset, _lightCount, _directionalLightCount));
                    cmd.SetGlobalVector("_FPParams1", math.float4(cameraData.pixelRect.size / _actualTileWidth, _tileResolution.x, _wordsPerTile));
                    cmd.SetGlobalVector("_FPParams2", math.float4(_binCount, _tileResolution.x * _tileResolution.y, 0, 0));
                }

                SetupShaderLightConstants(cmd, ref renderingData.cullResults, lightData);

                bool lightCountCheck = (cameraData.renderer.stripAdditionalLightOffVariants && lightData.supportsAdditionalLights) || additionalLightsCount > 0;
                cmd.SetKeyword(ShaderGlobalKeywords.AdditionalLightsVertex, lightCountCheck && additionalLightsPerVertex && !_useForwardPlus);
                cmd.SetKeyword(ShaderGlobalKeywords.AdditionalLightsPixel, lightCountCheck && !additionalLightsPerVertex && !_useForwardPlus);
                cmd.SetKeyword(ShaderGlobalKeywords.ForwardPlus, _useForwardPlus);

                bool isShadowMask = lightData.supportsMixedLighting && _mixedLightingSetup == MixedLightingSetup.ShadowMask;
                bool isShadowMaskAlways = isShadowMask && QualitySettings.shadowmaskMode == ShadowmaskMode.Shadowmask;
                bool isSubtractive = lightData.supportsMixedLighting && _mixedLightingSetup == MixedLightingSetup.Subtractive;
                cmd.SetKeyword(ShaderGlobalKeywords.LightmapShadowMixing, isSubtractive || isShadowMaskAlways);
                cmd.SetKeyword(ShaderGlobalKeywords.ShadowsShadowMask, isShadowMask);
                cmd.SetKeyword(ShaderGlobalKeywords.MixedLightingSubtractive, isSubtractive); // Backward compatibility

                cmd.SetKeyword(ShaderGlobalKeywords.ReflectionProbeBlending, lightData.reflectionProbeBlending);
                cmd.SetKeyword(ShaderGlobalKeywords.ReflectionProbeBoxProjection, lightData.reflectionProbeBoxProjection);

                var asset = UltraRenderPipeline.asset;
                bool apvIsEnabled = asset != null && asset.supportProbeVolume;
                bool aprtIsEnabled = asset != null && asset.supportAPRTV;
                ProbeVolumeSHBands probeVolumeSHBands = asset.probeVolumeSHBands;

                cmd.SetKeyword(ShaderGlobalKeywords.ProbeVolumeL1, apvIsEnabled && probeVolumeSHBands == ProbeVolumeSHBands.SphericalHarmonicsL1);
                cmd.SetKeyword(ShaderGlobalKeywords.ProbeVolumeL2, apvIsEnabled && probeVolumeSHBands == ProbeVolumeSHBands.SphericalHarmonicsL2);
                cmd.SetKeyword(ShaderGlobalKeywords.ProbeVolumeAPRT, aprtIsEnabled);

                // TODO: If we can robustly detect LIGHTMAP_ON, we can skip SH logic.
                var shMode = PlatformAutoDetect.ShAutoDetect(asset.shEvalMode);
                cmd.SetKeyword(ShaderGlobalKeywords.EVALUATE_SH_MIXED, shMode == ShEvalMode.Mixed);
                cmd.SetKeyword(ShaderGlobalKeywords.EVALUATE_SH_VERTEX, shMode == ShEvalMode.PerVertex);

                bool enableProbeVolumes = false;
                if (apvIsEnabled)
                {
                    var stack = VolumeManager.instance.stack;
                    enableProbeVolumes = ProbeReferenceVolumeProvider.instance.UpdateShaderVariablesProbeVolumes(
                        CommandBufferHelpers.GetNativeCommandBuffer(cmd),
                        stack.GetComponent<ProbeVolumesOptions>(),
                        cameraData.IsTemporalJitterEnabled() ? Time.frameCount : 0,
                        lightData.supportsLightLayers);
                }

                cmd.SetGlobalInt("_EnableProbeVolumes", enableProbeVolumes ? 1 : 0);
                cmd.SetKeyword(ShaderGlobalKeywords.LightLayers, lightData.supportsLightLayers && !CoreUtils.IsSceneLightingDisabled(cameraData.camera));

                if (_lightCookieManager != null)
                    _lightCookieManager.Setup(CommandBufferHelpers.GetNativeCommandBuffer(cmd), lightData);
                else
                    cmd.SetKeyword(ShaderGlobalKeywords.LightCookies, false);
            }
        }

        void SetupShaderLightConstants(UnsafeCommandBuffer cmd, ref CullingResults cullResults, LightData lightData)
        {
            _mixedLightingSetup = MixedLightingSetup.None;

            // Main light has an optimized shader path for main light. This will benefit games that only care about a single light.
            // UltraRP also supports only a single shadow light, if available it will be the main light.
            SetupMainLightConstants(cmd, lightData);
            SetupAdditionalLightConstants(cmd, ref cullResults, lightData);
        }

        void SetupMainLightConstants(UnsafeCommandBuffer cmd, LightData lightData)
        {
            Vector4 lightPos, lightColor, lightAttenuation, lightSpotDir, lightOcclusionChannel;
            bool supportsLightLayers = lightData.supportsLightLayers;
            uint lightLayerMask;
            bool isSubtractive;
            InitializeLightConstants(lightData.visibleLights, lightData.mainLightIndex, supportsLightLayers, out lightPos, out lightColor, out lightAttenuation, out lightSpotDir, out lightOcclusionChannel, out lightLayerMask, out isSubtractive);
            lightColor.w = isSubtractive ? 0f : 1f;

            cmd.SetGlobalVector(LightConstantBuffer._MainLightPosition, lightPos);
            cmd.SetGlobalVector(LightConstantBuffer._MainLightColor, lightColor);
            cmd.SetGlobalVector(LightConstantBuffer._MainLightOcclusionProbesChannel, lightOcclusionChannel);

            if (supportsLightLayers)
                cmd.SetGlobalInt(LightConstantBuffer._MainLightLayerMask, (int)lightLayerMask);
        }

        void SetupAdditionalLightConstants(UnsafeCommandBuffer cmd, ref CullingResults cullResults, LightData lightData)
        {
            bool supportsLightLayers = lightData.supportsLightLayers;
            var lights = lightData.visibleLights;
            int maxAdditionalLightsCount = UltraRenderPipeline.maxVisibleAdditionalLights;
            int additionalLightsCount = SetupPerObjectLightIndices(cullResults, lightData);
            if (additionalLightsCount > 0)
            {
                if (_useStructuredBuffer)
                {
                    NativeArray<ShaderInput.LightData> additionalLightsData = new NativeArray<ShaderInput.LightData>(additionalLightsCount, Allocator.Temp);
                    for (int i = 0, lightIter = 0; i < lights.Length && lightIter < maxAdditionalLightsCount; ++i)
                    {
                        if (lightData.mainLightIndex != i)
                        {
                            ShaderInput.LightData data;
                            InitializeLightConstants(lights, i, supportsLightLayers,
                                out data.position, out data.color, out data.attenuation,
                                out data.spotDirection, out data.occlusionProbeChannels,
                                out data.layerMask, out _);
                            additionalLightsData[lightIter] = data;
                            lightIter++;
                        }
                    }

                    var lightDataBuffer = ShaderData.instance.GetLightDataBuffer(additionalLightsCount);
                    lightDataBuffer.SetData(additionalLightsData);

                    int lightIndices = cullResults.lightAndReflectionProbeIndexCount;
                    var lightIndicesBuffer = ShaderData.instance.GetLightIndicesBuffer(lightIndices);

                    cmd.SetGlobalBuffer(LightConstantBuffer._AdditionalLightsBuffer, lightDataBuffer);
                    cmd.SetGlobalBuffer(LightConstantBuffer._AdditionalLightsIndices, lightIndicesBuffer);

                    additionalLightsData.Dispose();
                }
                else
                {
                    for (int i = 0, lightIter = 0; i < lights.Length && lightIter < maxAdditionalLightsCount; ++i)
                    {
                        if (lightData.mainLightIndex != i)
                        {
                            InitializeLightConstants(
                                lights,
                                i,
                                supportsLightLayers,
                                out _additionalLightPositions[lightIter],
                                out _additionalLightColors[lightIter],
                                out _additionalLightAttenuations[lightIter],
                                out _additionalLightSpotDirections[lightIter],
                                out _additionalLightOcclusionProbeChannels[lightIter],
                                out uint lightLayerMask,
                                out var isSubtractive);

                            if (supportsLightLayers)
                                _additionalLightsLayerMasks[lightIter] = math.asfloat(lightLayerMask);

                            _additionalLightColors[lightIter].w = isSubtractive ? 1f : 0f;
                            lightIter++;
                        }
                    }

                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsPosition,             _additionalLightPositions);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsColor,                _additionalLightColors);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsAttenuation,          _additionalLightAttenuations);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsSpotDir,              _additionalLightSpotDirections);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightOcclusionProbeChannel, _additionalLightOcclusionProbeChannels);

                    if (supportsLightLayers)
                        cmd.SetGlobalFloatArray(LightConstantBuffer._AdditionalLightsLayerMasks, _additionalLightsLayerMasks);
                }

                cmd.SetGlobalVector(LightConstantBuffer._AdditionalLightsCount, new Vector4(lightData.maxPerObjectAdditionalLightsCount, 0.0f, 0.0f, 0.0f));
            }
            else
                cmd.SetGlobalVector(LightConstantBuffer._AdditionalLightsCount, Vector4.zero);
        }

 
        void InitializeLightConstants(NativeArray<VisibleLight> lights, int lightIndex, bool supportsLightLayers, out Vector4 lightPos, out Vector4 lightColor, out Vector4 lightAttenuation, out Vector4 lightSpotDir, out Vector4 lightOcclusionProbeChannel, out uint lightLayerMask, out bool isSubtractive)
        {
            InitializeLightConstantsCommon(lights, lightIndex, out lightPos, out lightColor, out lightAttenuation, out lightSpotDir, out lightOcclusionProbeChannel);

            lightLayerMask = 0;
            isSubtractive = false;

            // When no lights are visible, main light will be set to -1.
            // In this case we initialize it to default values and return
            if (lightIndex < 0)
                return;

            ref VisibleLight lightData = ref lights.UnsafeElementAtMutable(lightIndex);
            Light light = lightData.light;
            var lightBakingOutput = light.bakingOutput;
            isSubtractive = lightBakingOutput.isBaked && lightBakingOutput.lightmapBakeType == LightmapBakeType.Mixed && lightBakingOutput.mixedLightingMode == MixedLightingMode.Subtractive;

            if (light == null)
                return;

            if (lightBakingOutput.lightmapBakeType == LightmapBakeType.Mixed &&
                lightData.light.shadows != LightShadows.None &&
                _mixedLightingSetup == MixedLightingSetup.None)
            {
                switch (lightBakingOutput.mixedLightingMode)
                {
                    case MixedLightingMode.Subtractive:
                        _mixedLightingSetup = MixedLightingSetup.Subtractive;
                        break;
                    case MixedLightingMode.Shadowmask:
                        _mixedLightingSetup = MixedLightingSetup.ShadowMask;
                        break;
                }
            }

            if (supportsLightLayers)
            {
                var additionalLightData = light.GetUltraAdditionalLightData();
                lightLayerMask = RenderingLayerUtils.ToValidRenderingLayers(additionalLightData.renderingLayers);
            }
        }

        int SetupPerObjectLightIndices(CullingResults cullResults, LightData lightData)
        {
            if (lightData.additionalLightsCount == 0 || _useForwardPlus)
                return lightData.additionalLightsCount;

            var perObjectLightIndexMap = cullResults.GetLightIndexMap(Allocator.Temp);
            int globalDirectionalLightsCount = 0;
            int additionalLightsCount = 0;

            // Disable all directional lights from the perobject light indices
            // Pipeline handles main light globally and there's no support for additional directional lights atm.
            int maxVisibleAdditionalLightsCount = UltraRenderPipeline.maxVisibleAdditionalLights;
            int len = lightData.visibleLights.Length;
            for (int i = 0; i < len; ++i)
            {
                if (additionalLightsCount >= maxVisibleAdditionalLightsCount)
                    break;

                if (i == lightData.mainLightIndex)
                {
                    perObjectLightIndexMap[i] = -1;
                    ++globalDirectionalLightsCount;
                }
                else
                {
                    if (lightData.visibleLights[i].lightType == LightType.Directional ||
                        lightData.visibleLights[i].lightType == LightType.Spot ||
                        lightData.visibleLights[i].lightType == LightType.Point)
                    {
                        // Light type is supported
                        perObjectLightIndexMap[i] -= globalDirectionalLightsCount;
                    }
                    else
                    {
                        // Light type is not supported. Skip the light.
                        perObjectLightIndexMap[i] = -1;
                    }

                    ++additionalLightsCount;
                }
            }

            // Disable all remaining lights we cannot fit into the global light buffer.
            for (int i = globalDirectionalLightsCount + additionalLightsCount; i < perObjectLightIndexMap.Length; ++i)
                perObjectLightIndexMap[i] = -1;

            cullResults.SetLightIndexMap(perObjectLightIndexMap);

            if (_useStructuredBuffer && additionalLightsCount > 0)
            {
                int lightAndReflectionProbeIndices = cullResults.lightAndReflectionProbeIndexCount;
                Debug.Assert(lightAndReflectionProbeIndices > 0, "Pipelines configures additional lights but per-object light and probe indices count is zero.");
                cullResults.FillLightAndReflectionProbeIndices(ShaderData.instance.GetLightIndicesBuffer(lightAndReflectionProbeIndices));
            }

            perObjectLightIndexMap.Dispose();
            return additionalLightsCount;
        }
    }
}
