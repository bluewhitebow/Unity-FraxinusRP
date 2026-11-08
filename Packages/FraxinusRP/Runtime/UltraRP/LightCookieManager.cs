using System;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    internal class LightCookieManager : IDisposable
    {
        static class ShaderProperty
        {
            public static readonly int _MainLightCookieTexture = Shader.PropertyToID("_MainLightCookieTexture");
            public static readonly int _MainLightWorldToLight = Shader.PropertyToID("_MainLightWorldToLight");
            public static readonly int _MainLightCookieTextureFormat = Shader.PropertyToID("_MainLightCookieTextureFormat");

            public static readonly int _AdditionalLightsCookieAtlasTexture = Shader.PropertyToID("_AdditionalLightsCookieAtlasTexture");
            public static readonly int _AdditionalLightsCookieAtlasTextureFormat = Shader.PropertyToID("_AdditionalLightsCookieAtlasTextureFormat");

            public static readonly int _AdditionalLightsCookieEnableBits = Shader.PropertyToID("_AdditionalLightsCookieEnableBits");

            public static readonly int _AdditionalLightsCookieAtlasUVRectBuffer = Shader.PropertyToID("_AdditionalLightsCookieAtlasUVRectBuffer");
            public static readonly int _AdditionalLightsCookieAtlasUVRects = Shader.PropertyToID("_AdditionalLightsCookieAtlasUVRects");

            // TODO: these should be generic light property
            public static readonly int _AdditionalLightsWorldToLightBuffer = Shader.PropertyToID("_AdditionalLightsWorldToLightBuffer");
            public static readonly int _AdditionalLightsLightTypeBuffer = Shader.PropertyToID("_AdditionalLightsLightTypeBuffer");

            public static readonly int _AdditionalLightsWorldToLights = Shader.PropertyToID("_AdditionalLightsWorldToLights");
            public static readonly int _AdditionalLightsLightTypes = Shader.PropertyToID("_AdditionalLightsLightTypes");
        }

        private enum LightCookieShaderFormat
        {
            None = -1,

            RGB = 0,
            Alpha = 1,
            Red = 2
        }

        public struct Settings
        {
            public struct AtlasSettings
            {
                public Vector2Int resolution;
                public GraphicsFormat format;

                public bool isPow2 => Mathf.IsPowerOfTwo(resolution.x) && Mathf.IsPowerOfTwo(resolution.y);
                public bool isSquare => resolution.x == resolution.y;
            }

            public AtlasSettings atlas;
            public int maxAdditionalLights;        // UltraRenderPipeline.maxVisibleAdditionalLights;
            public float cubeOctahedralSizeScale;  // Cube octahedral projection size scale.
            public bool useStructuredBuffer;       // RenderingUtils.useStructuredBuffer

            public static Settings Create()
            {
                Settings s;
                s.atlas.resolution = new Vector2Int(1024, 1024);
                s.atlas.format = GraphicsFormat.R8G8B8A8_SRGB;
                s.maxAdditionalLights = UltraRenderPipeline.maxVisibleAdditionalLights;

                // (Scale * W * Scale * H) / (6 * WH) == (Scale^2 / 6)
                // 1: 1/6 = 16%, 2: 4/6 = 66%, 4: 16/6 == 266% of cube pixels
                // 100% cube pixels == sqrt(6) ~= 2.45f --> 2.5;
                s.cubeOctahedralSizeScale = 2.5f;
                s.useStructuredBuffer = RenderingUtils.useStructuredBuffer;
                return s;
            }
        }

        private struct LightCookieMapping
        {
            public ushort visibleLightIndex;    // Index into visible light (src)
            public ushort lightBufferIndex;     // Index into light shader data buffer(s) (dst) (matches ForwardLights.SetupAdditionalLightConstants())
            public Light light;                 // Cached built-in light for the visibleLightIndex. Avoids multiple copies on all the gets from native array.

            public static Func<LightCookieMapping, LightCookieMapping, int> compareByCookieSize = (LightCookieMapping a, LightCookieMapping b) =>
            {
                var alc = a.light.cookie;
                var blc = b.light.cookie;
                int a2 = alc.width * alc.height;
                int b2 = blc.width * blc.height;
                int d = b2 - a2;
                if (d == 0)
                {
                    // Sort by texture ID if "undecided" to batch fetches to the same cookie texture.
                    int ai = alc.GetInstanceID();
                    int bi = blc.GetInstanceID();
                    return ai - bi;
                }
                return d;
            };

            public static Func<LightCookieMapping, LightCookieMapping, int> compareByBufferIndex = (LightCookieMapping a, LightCookieMapping b) =>
            {
                return a.lightBufferIndex - b.lightBufferIndex;
            };
        }

        private readonly struct WorkSlice<T>
        {
            private readonly T[] _data;
            private readonly int _start;
            private readonly int _length;

            public WorkSlice(T[] src, int srcLen = -1) : this(src, 0, srcLen) { }

            public WorkSlice(T[] src, int srcStart, int srcLen = -1)
            {
                _data = src;
                _start = srcStart;
                _length = (srcLen < 0) ? src.Length : Math.Min(srcLen, src.Length);
                Debug.Assert(_start + _length <= capacity);
            }

            public T this[int index]
            {
                get => _data[_start + index];
                set => _data[_start + index] = value;
            }

            public int length => _length;
            public int capacity => _data.Length;

            public void Sort(Func<T, T, int> compare)
            {
                if (_length > 1)
                    Sorting.QuickSort(_data, _start, _start + _length - 1, compare);
            }
        }

        private class WorkMemory
        {
            public LightCookieMapping[] lightMappings;
            public Vector4[] uvRects;

            public void Resize(int size)
            {
                if (size <= lightMappings?.Length)
                    return;

                // Avoid allocs on every tiny size change.
                size = Math.Max(size, ((size + 15) / 16) * 16);

                lightMappings = new LightCookieMapping[size];
                uvRects = new Vector4[size];
            }
        }

        /// Must match light data layout.
        private class LightCookieShaderData : IDisposable
        {
            int _size = 0;
            bool _useStructuredBuffer;

            // Shader data CPU arrays, used to upload the data to GPU
            Matrix4x4[] _worldToLightCpuData;
            Vector4[] _atlasUVRectCpuData;
            float[] _lightTypeCpuData;
            ShaderBitArray _cookieEnableBitsCpuData;

            // Compute buffer counterparts for the CPU data
            ComputeBuffer _worldToLightBuffer;    // TODO: WorldToLight matrices should be general property of lights!!
            ComputeBuffer _atlasUVRectBuffer;
            ComputeBuffer _lightTypeBuffer;

            public Matrix4x4[] worldToLights => _worldToLightCpuData;
            public ShaderBitArray cookieEnableBits => _cookieEnableBitsCpuData;
            public Vector4[] atlasUVRects => _atlasUVRectCpuData;
            public float[] lightTypes => _lightTypeCpuData;

            public bool isUploaded { get; set; }

            public LightCookieShaderData(int size, bool useStructuredBuffer)
            {
                _useStructuredBuffer = useStructuredBuffer;
                Resize(size);
            }

            public void Dispose()
            {
                if (_useStructuredBuffer)
                {
                    _worldToLightBuffer?.Dispose();
                    _atlasUVRectBuffer?.Dispose();
                    _lightTypeBuffer?.Dispose();
                }
            }

            public void Resize(int size)
            {
                if (size <= _size)
                    return;

                if (_size > 0)
                    Dispose();

                _worldToLightCpuData = new Matrix4x4[size];
                _atlasUVRectCpuData = new Vector4[size];
                _lightTypeCpuData = new float[size];
                _cookieEnableBitsCpuData.Resize(size);

                if (_useStructuredBuffer)
                {
                    _worldToLightBuffer = new ComputeBuffer(size, Marshal.SizeOf<Matrix4x4>());
                    _atlasUVRectBuffer = new ComputeBuffer(size, Marshal.SizeOf<Vector4>());
                    _lightTypeBuffer = new ComputeBuffer(size, Marshal.SizeOf<float>());
                }

                _size = size;
            }

            public void Upload(CommandBuffer cmd)
            {
                if (_useStructuredBuffer)
                {
                    _worldToLightBuffer.SetData(_worldToLightCpuData);
                    _atlasUVRectBuffer.SetData(_atlasUVRectCpuData);
                    _lightTypeBuffer.SetData(_lightTypeCpuData);

                    cmd.SetGlobalBuffer(ShaderProperty._AdditionalLightsWorldToLightBuffer, _worldToLightBuffer);
                    cmd.SetGlobalBuffer(ShaderProperty._AdditionalLightsCookieAtlasUVRectBuffer, _atlasUVRectBuffer);
                    cmd.SetGlobalBuffer(ShaderProperty._AdditionalLightsLightTypeBuffer, _lightTypeBuffer);
                }
                else
                {
                    cmd.SetGlobalMatrixArray(ShaderProperty._AdditionalLightsWorldToLights, _worldToLightCpuData);
                    cmd.SetGlobalVectorArray(ShaderProperty._AdditionalLightsCookieAtlasUVRects, _atlasUVRectCpuData);
                    cmd.SetGlobalFloatArray(ShaderProperty._AdditionalLightsLightTypes, _lightTypeCpuData);
                }

                cmd.SetGlobalFloatArray(ShaderProperty._AdditionalLightsCookieEnableBits, _cookieEnableBitsCpuData.data);
                isUploaded = true;
            }

            public void Clear(CommandBuffer cmd)
            {
                if (isUploaded)
                {
                    // Set all lights to disabled/invalid state
                    _cookieEnableBitsCpuData.Clear();
                    cmd.SetGlobalFloatArray(ShaderProperty._AdditionalLightsCookieEnableBits, _cookieEnableBitsCpuData.data);
                    isUploaded = false;
                }
            }
        }

        // Unity defines directional light UVs over a unit box centered at light.
        // i.e. (0, 1) uv == (-0.5, 0.5) world area instead of the (0,1) world area.
        static readonly Matrix4x4 s_dirLightProj = Matrix4x4.Ortho(-0.5f, 0.5f, -0.5f, 0.5f, -0.5f, 0.5f);

        Texture2DAtlas _additionalLightsCookieAtlas;
        LightCookieShaderData _additionalLightsCookieShaderData;
        Texture _mainLightCookieSource;
        RTHandle _mainLightCookieHandle;

        readonly Settings _settings;
        WorkMemory _workMem;

        int[] _visibleLightIndexToShaderDataIndex;

        const int k_maxCookieSizeDivisor = 16;
        int _cookieSizeDivisor = 1;
        uint _prevCookieRequestPixelCount = 0xFFFFFFFF;

        int _prevWarnFrame = -1;

        internal bool isKeywordLightCookieEnabled { get; private set; }

        internal RTHandle additionalLightsCookieAtlasTexture => _additionalLightsCookieAtlas?.AtlasTexture;

        // Prepared during camera recording and copied into each consumer's pass data.
        internal TextureHandle mainLightCookieTextureHandle { get; private set; }
        internal TextureHandle additionalLightsCookieAtlasTextureHandle { get; private set; }

        internal void PrepareRenderGraphTextures(RenderGraph renderGraph, LightData lightData)
        {
            mainLightCookieTextureHandle = renderGraph.defaultResources.whiteTexture;
            additionalLightsCookieAtlasTextureHandle = renderGraph.defaultResources.whiteTexture;

            Texture mainCookie = lightData.mainLightIndex >= 0
                ? lightData.visibleLights[lightData.mainLightIndex].light.cookie : null;
            // Each camera's graph executes before the next camera is recorded.
            // Release only the wrapper when its source changes, so cookies from
            // unloaded scenes are not retained for the renderer's lifetime.
            if (!ReferenceEquals(mainCookie, _mainLightCookieSource))
            {
                _mainLightCookieHandle?.Release();
                _mainLightCookieHandle = null;
                _mainLightCookieSource = mainCookie;
            }
            if (mainCookie != null)
            {
                if (_mainLightCookieHandle == null)
                {
                    _mainLightCookieHandle = mainCookie is RenderTexture target
                        ? RTHandles.Alloc(new RenderTargetIdentifier(target), target.name)
                        : RTHandles.Alloc(mainCookie);
                }
                if (mainCookie is RenderTexture renderTexture)
                {
                    // A cookie can have both color and depth attachments. Import
                    // only its sampled color view; the source RT remains owned
                    // and configured by its producer.
                    var colorInfo = new RenderTargetInfo
                    {
                        width = renderTexture.width,
                        height = renderTexture.height,
                        volumeDepth = renderTexture.volumeDepth,
                        format = renderTexture.graphicsFormat,
                        msaaSamples = renderTexture.antiAliasing,
                        bindMS = renderTexture.bindTextureMS,
                    };
                    mainLightCookieTextureHandle = renderGraph.ImportTexture(_mainLightCookieHandle, colorInfo);
                }
                else
                {
                    mainLightCookieTextureHandle = renderGraph.ImportTexture(_mainLightCookieHandle);
                }
            }

            if (lightData.additionalLightsCount <= 0)
                return;

            _workMem.Resize(Math.Min(_settings.maxAdditionalLights, lightData.visibleLights.Length));
            int validLightCount = FilterAndValidateAdditionalLights(lightData, _workMem.lightMappings);
            if (validLightCount <= 0)
                return;

            // Setup uploads atlas contents when the graph executes. Allocate its
            // backing texture now so the producer and consumers share a handle
            // even on the first frame that a cookie becomes visible.
            if (!IsInitialized())
                InitAdditionalLights(validLightCount);
            additionalLightsCookieAtlasTextureHandle = renderGraph.ImportTexture(_additionalLightsCookieAtlas.AtlasTexture);
        }

        public LightCookieManager(ref Settings settings)
        {
            _settings = settings;
            _workMem = new WorkMemory();
        }

        void InitAdditionalLights(int size)
        {
            // No mip padding support.
            _additionalLightsCookieAtlas = new Texture2DAtlas(
                _settings.atlas.resolution.x,
                _settings.atlas.resolution.y,
                _settings.atlas.format,
                FilterMode.Bilinear,
                false,
                "UltraRP Light Cookie Atlas",
                false); // to support mips, use Pow2Atlas


            _additionalLightsCookieShaderData = new LightCookieShaderData(size, _settings.useStructuredBuffer);
            const int mainLightCount = 1;
            _visibleLightIndexToShaderDataIndex = new int[_settings.maxAdditionalLights + mainLightCount];

            _cookieSizeDivisor = 1;
            _prevCookieRequestPixelCount = 0xFFFFFFFF;
        }

        public bool IsInitialized() => _additionalLightsCookieAtlas != null && _additionalLightsCookieShaderData != null;

        public void Dispose()
        {
            _mainLightCookieHandle?.Release();
            _mainLightCookieHandle = null;
            _mainLightCookieSource = null;
            _additionalLightsCookieAtlas?.Release();
            _additionalLightsCookieShaderData?.Dispose();
        }

        public int GetLightCookieShaderDataIndex(int visibleLightIndex)
        {
            if (!IsInitialized())
                return -1;

            return _visibleLightIndexToShaderDataIndex[visibleLightIndex];
        }

        public void Setup(CommandBuffer cmd, LightData lightData)
        {
            using var profScope = new ProfilingScope(cmd, ProfilingSampler.Get(UltraRPProfileId.LightCookies));

            // Main light, 1 directional, bound directly
            bool isMainLightAvailable = lightData.mainLightIndex >= 0;
            if (isMainLightAvailable)
            {
                var mainLight = lightData.visibleLights[lightData.mainLightIndex];
                isMainLightAvailable = SetupMainLight(cmd, ref mainLight);
            }

            // Additional lights, N spot and point lights in atlas
            bool isAdditionalLightsAvailable = lightData.additionalLightsCount > 0;
            if (isAdditionalLightsAvailable)
                isAdditionalLightsAvailable = SetupAdditionalLights(cmd, lightData);

            // Ensure cookies are disabled if no cookies are available.
            if (!isAdditionalLightsAvailable)
            {
                // ..on the CPU (for deferred)
                if (_visibleLightIndexToShaderDataIndex != null &&
                    _additionalLightsCookieShaderData.isUploaded)
                {
                    int len = _visibleLightIndexToShaderDataIndex.Length;
                    for (int i = 0; i < len; i++)
                        _visibleLightIndexToShaderDataIndex[i] = -1;
                }

                // ..on the GPU
                _additionalLightsCookieShaderData?.Clear(cmd);
            }

            // Main and additional lights are merged into one keyword to reduce variants.
            isKeywordLightCookieEnabled = isMainLightAvailable || isAdditionalLightsAvailable;
            cmd.SetKeyword(ShaderGlobalKeywords.LightCookies, isKeywordLightCookieEnabled);
        }

        bool SetupMainLight(CommandBuffer cmd, ref VisibleLight visibleMainLight)
        {
            var mainLight = visibleMainLight.light;
            var cookieTexture = mainLight.cookie;
            bool isMainLightCookieEnabled = cookieTexture != null;

            if (isMainLightCookieEnabled)
            {
                Matrix4x4 cookieUVTransform = Matrix4x4.identity;
                float cookieFormat = (float)GetLightCookieShaderFormat(cookieTexture.graphicsFormat);

                if (mainLight.TryGetComponent(out UltraAdditionalLightData additionalLightData))
                    GetLightUVScaleOffset(ref additionalLightData, ref cookieUVTransform);

                Matrix4x4 cookieMatrix = s_dirLightProj * cookieUVTransform *
                    visibleMainLight.localToWorldMatrix.inverse;

                cmd.SetGlobalTexture(ShaderProperty._MainLightCookieTexture, cookieTexture);
                cmd.SetGlobalMatrix(ShaderProperty._MainLightWorldToLight, cookieMatrix);
                cmd.SetGlobalFloat(ShaderProperty._MainLightCookieTextureFormat, cookieFormat);
            }
            else
            {
                // Make sure we erase stale data in case the main light is disabled but cookie system is enabled (for additional lights).
                cmd.SetGlobalTexture(ShaderProperty._MainLightCookieTexture, Texture2D.whiteTexture);
                cmd.SetGlobalMatrix(ShaderProperty._MainLightWorldToLight, Matrix4x4.identity);
                cmd.SetGlobalFloat(ShaderProperty._MainLightCookieTextureFormat, (float)LightCookieShaderFormat.None);
            }

            return isMainLightCookieEnabled;
        }

        private LightCookieShaderFormat GetLightCookieShaderFormat(GraphicsFormat cookieFormat)
        {
            // TODO: convert this to use GraphicsFormatUtility
            switch (cookieFormat)
            {
                default:
                    return LightCookieShaderFormat.RGB;
                // A8, A16 GraphicsFormat does not expose yet.
                case (GraphicsFormat)54:
                case (GraphicsFormat)55:
                    return LightCookieShaderFormat.Alpha;
                case GraphicsFormat.R8_SRGB:
                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8_UInt:
                case GraphicsFormat.R8_SNorm:
                case GraphicsFormat.R8_SInt:
                case GraphicsFormat.R16_UNorm:
                case GraphicsFormat.R16_UInt:
                case GraphicsFormat.R16_SNorm:
                case GraphicsFormat.R16_SInt:
                case GraphicsFormat.R16_SFloat:
                case GraphicsFormat.R32_UInt:
                case GraphicsFormat.R32_SInt:
                case GraphicsFormat.R32_SFloat:
                case GraphicsFormat.R_BC4_SNorm:
                case GraphicsFormat.R_BC4_UNorm:
                case GraphicsFormat.R_EAC_SNorm:
                case GraphicsFormat.R_EAC_UNorm:
                    return LightCookieShaderFormat.Red;
            }
        }

        private void GetLightUVScaleOffset(ref UltraAdditionalLightData additionalLightData, ref Matrix4x4 uvTransform)
        {
            Vector2 uvScale = Vector2.one / additionalLightData.lightCookieSize;
            Vector2 uvOffset = additionalLightData.lightCookieOffset;

            if (Mathf.Abs(uvScale.x) < half.MinValue)
                uvScale.x = Mathf.Sign(uvScale.x) * half.MinValue;
            if (Mathf.Abs(uvScale.y) < half.MinValue)
                uvScale.y = Mathf.Sign(uvScale.y) * half.MinValue;

            uvTransform = Matrix4x4.Scale(new Vector3(uvScale.x, uvScale.y, 1));
            uvTransform.SetColumn(3, new Vector4(-uvOffset.x * uvScale.x, -uvOffset.y * uvScale.y, 0, 1));
        }


        bool SetupAdditionalLights(CommandBuffer cmd, LightData lightData)
        {
            int maxLightCount = Math.Min(_settings.maxAdditionalLights, lightData.visibleLights.Length);
            _workMem.Resize(maxLightCount);

            int validLightCount = FilterAndValidateAdditionalLights(lightData, _workMem.lightMappings);

            // Early exit if no valid cookie lights
            if (validLightCount <= 0)
                return false;

            // Lazy init GPU resources
            if (!IsInitialized())
                InitAdditionalLights(validLightCount);

            // Update Atlas
            var validLights = new WorkSlice<LightCookieMapping>(_workMem.lightMappings, validLightCount);
            int validUVRectCount = UpdateAdditionalLightsAtlas(cmd, ref validLights, _workMem.uvRects);

            // Upload shader data
            var validUvRects = new WorkSlice<Vector4>(_workMem.uvRects, validUVRectCount);
            UploadAdditionalLights(cmd, lightData, ref validLights, ref validUvRects);

            bool isAdditionalLightsEnabled = validUvRects.length > 0;
            return isAdditionalLightsEnabled;
        }

        int FilterAndValidateAdditionalLights(LightData lightData, LightCookieMapping[] validLightMappings)
        {
            int skipMainLightIndex = lightData.mainLightIndex;
            int lightBufferOffset = 0;
            int validLightCount = 0;

            int visibleLightCount = lightData.visibleLights.Length;
            for (int i = 0; i < visibleLightCount; i++)
            {
                // Drop main light from additional lights buffer.
                if (i == skipMainLightIndex)
                {
                    lightBufferOffset -= 1;
                    continue;
                }

                ref var visLight = ref lightData.visibleLights.UnsafeElementAtMutable(i);
                Light light = visLight.light;

                // Skip lights without a cookie texture
                if (light.cookie == null)
                    continue;

                // Only spot, point and directional lights are supported.
                // Warn on dropped lights
                var lightType = visLight.lightType;
                if (!(lightType == LightType.Spot ||
                      lightType == LightType.Point ||
                      lightType == LightType.Directional))
                {
                    Debug.LogWarning($"Additional {lightType.ToString()} light called '{light.name}' has a light cookie which will not be visible.", light);
                    continue;
                }

                Debug.Assert(i < ushort.MaxValue);

                LightCookieMapping lp;
                lp.visibleLightIndex = (ushort)i;
                lp.lightBufferIndex = (ushort)(i + lightBufferOffset);  // Matching FowardLights.SetupAdditionalLightConstants
                lp.light = light;

                // Drop lights if we have too many lights or too many cookies to fit ForwardLight data.
                if (lp.lightBufferIndex >= validLightMappings.Length || validLightCount + 1 >= validLightMappings.Length)
                {
                    // TODO: Better error system
                    if (visibleLightCount > _settings.maxAdditionalLights &&
                        Time.frameCount - _prevWarnFrame > 60 * 60) // warn throttling: ~60 FPS * 60 secs ~= 1 min
                    {
                        _prevWarnFrame = Time.frameCount;
                        Debug.LogWarning($"Max light cookies ({validLightMappings.Length.ToString()}) reached. Some visible lights ({(visibleLightCount - i - 1).ToString()}) might skip light cookie rendering.");
                    }

                    // Always break, buffer full.
                    break;
                }

                validLightMappings[validLightCount++] = lp;
            }

            return validLightCount;
        }

        int UpdateAdditionalLightsAtlas(CommandBuffer cmd, ref WorkSlice<LightCookieMapping> validLightMappings, Vector4[] textureAtlasUVRects)
        {
            // Sort in-place by cookie size for better atlas allocation efficiency (and deduplication)
            validLightMappings.Sort(LightCookieMapping.compareByCookieSize);

            uint cookieRequestPixelCount = ComputeCookieRequestPixelCount(ref validLightMappings);
            var atlasSize = _additionalLightsCookieAtlas.AtlasTexture.referenceSize;
            float requestAtlasRatio = cookieRequestPixelCount / (float)(atlasSize.x * atlasSize.y);
            int cookieSizeDivisorApprox = ApproximateCookieSizeDivisor(requestAtlasRatio);

            // Try to recover resolution and scale the cookies back up.
            // If the cookies "should fit" and
            // If we have less requested pixels than the last time we found the correct divisor (a guard against retrying every frame).
            if (cookieSizeDivisorApprox < _cookieSizeDivisor &&
                cookieRequestPixelCount < _prevCookieRequestPixelCount)
            {
                _additionalLightsCookieAtlas.ResetAllocator();
                _cookieSizeDivisor = cookieSizeDivisorApprox;
            }


            // Get cached atlas uv rectangles.
            // If there's new cookies, first try to add at current scaling level.
            // (This can result in suboptimal packing & scaling (additions aren't sorted), but reduces rebuilds.)
            // If it doesn't fit, scale down and rebuild the atlas until it fits.
            int uvRectCount = 0;
            while (uvRectCount <= 0)
            {
                uvRectCount = FetchUVRects(cmd, ref validLightMappings, textureAtlasUVRects, _cookieSizeDivisor);

                if (uvRectCount <= 0)
                {
                    // Uv rect fetching failed, reset and try again.
                    _additionalLightsCookieAtlas.ResetAllocator();

                    // Reduce cookie size to approximate value try to rebuild the atlas.
                    _cookieSizeDivisor = Mathf.Max(_cookieSizeDivisor + 1, cookieSizeDivisorApprox);
                    _prevCookieRequestPixelCount = cookieRequestPixelCount;
                }
            }

            return uvRectCount;
        }

        int FetchUVRects(CommandBuffer cmd, ref WorkSlice<LightCookieMapping> validLightMappings, Vector4[] textureAtlasUVRects, int cookieSizeDivisor)
        {
            int uvRectCount = 0;
            for (int i = 0; i < validLightMappings.length; i++)
            {
                var lcm = validLightMappings[i];

                Light light = lcm.light;
                Texture cookie = light.cookie;

                // NOTE: Currently we blit directly on addition (on atlas fetch cache miss).
                //   This can be costly if there are many resize rebuilds (in case "out-of-space", which shouldn't be a common case).
                //   If rebuilds become a problem, we could try to just allocate and blit only when we have a fully valid allocation.
                //   It would also make sense to do atlas operations only for unique textures and then reuse the results for similar cookies.
                Vector4 uvScaleOffset = Vector4.zero;
                if (cookie.dimension == TextureDimension.Cube)
                {
                    Debug.Assert(light.type == LightType.Point);
                    uvScaleOffset = FetchCube(cmd, cookie, cookieSizeDivisor);
                }
                else
                {
                    Debug.Assert(light.type == LightType.Spot || light.type == LightType.Directional, "Light type needs 2D texture!");
                    uvScaleOffset = Fetch2D(cmd, cookie, cookieSizeDivisor);
                }

                bool isCached = uvScaleOffset != Vector4.zero;
                if (!isCached)
                {
                    if (cookieSizeDivisor > k_maxCookieSizeDivisor)
                    {
                        Debug.LogWarning($"Light cookies atlas is extremely full! Some of the light cookies were discarded. Increase light cookie atlas space or reduce the amount of unique light cookies.");
                        // Complete fail, return what we have.
                        return uvRectCount;
                    }

                    // Failed to get uv rect for each cookie, fail and try again.
                    return 0;
                }

                // Adjust atlas UVs for OpenGL
                if (!SystemInfo.graphicsUVStartsAtTop)
                    uvScaleOffset.w = 1.0f - uvScaleOffset.w - uvScaleOffset.y;

                textureAtlasUVRects[uvRectCount++] = uvScaleOffset;
            }

            return uvRectCount;
        }

        uint ComputeCookieRequestPixelCount(ref WorkSlice<LightCookieMapping> validLightMappings)
        {
            uint requestPixelCount = 0;
            int prevCookieID = 0;
            for (int i = 0; i < validLightMappings.length; i++)
            {
                var lcm = validLightMappings[i];
                Texture cookie = lcm.light.cookie;
                int cookieID = cookie.GetInstanceID();

                // Consider only unique textures as atlas request pixels
                // NOTE: relies on same cookies being sorted together
                // (we need sorting for good atlas packing anyway)
                if (cookieID == prevCookieID)
                {
                    continue;
                }
                prevCookieID = cookieID;

                int pixelCookieCount = cookie.width * cookie.height;
                requestPixelCount += (uint)pixelCookieCount;
            }

            return requestPixelCount;
        }

        int ApproximateCookieSizeDivisor(float requestAtlasRatio)
        {
            // (Edge / N)^2 == 1/N^2 of area.
            // Ratio/N^2 == 1, sqrt(Ratio) == N, for "1:1" ratio.
            return (int)Mathf.Max(Mathf.Ceil(Mathf.Sqrt(requestAtlasRatio)), 1);
        }

        Vector4 Fetch2D(CommandBuffer cmd, Texture cookie, int cookieSizeDivisor = 1)
        {
            Debug.Assert(cookie != null);
            Debug.Assert(cookie.dimension == TextureDimension.Tex2D);

            Vector4 uvScaleOffset = Vector4.zero;

            var scaledWidth = Mathf.Max(cookie.width / cookieSizeDivisor, 4);
            var scaledHeight = Mathf.Max(cookie.height / cookieSizeDivisor, 4);
            Vector2 scaledCookieSize = new Vector2(scaledWidth, scaledHeight);

            bool isCached = _additionalLightsCookieAtlas.IsCached(out uvScaleOffset, cookie);
            if (isCached)
            {
                // Update contents IF required
                _additionalLightsCookieAtlas.UpdateTexture(cmd, cookie, ref uvScaleOffset);
            }
            else
            {
                _additionalLightsCookieAtlas.AllocateTexture(cmd, ref uvScaleOffset, cookie, scaledWidth, scaledHeight);
            }

            AdjustUVRect(ref uvScaleOffset, cookie, ref scaledCookieSize);
            return uvScaleOffset;
        }

        Vector4 FetchCube(CommandBuffer cmd, Texture cookie, int cookieSizeDivisor = 1)
        {
            Debug.Assert(cookie != null);
            Debug.Assert(cookie.dimension == TextureDimension.Cube);

            Vector4 uvScaleOffset = Vector4.zero;

            // Scale octahedral projection, so that cube -> oct2D pixel count match better.
            int scaledOctCookieSize = Mathf.Max(ComputeOctahedralCookieSize(cookie) / cookieSizeDivisor, 4);

            bool isCached = _additionalLightsCookieAtlas.IsCached(out uvScaleOffset, cookie);
            if (isCached)
            {
                // Update contents IF required
                _additionalLightsCookieAtlas.UpdateTexture(cmd, cookie, ref uvScaleOffset);
            }
            else
            {
                _additionalLightsCookieAtlas.AllocateTexture(cmd, ref uvScaleOffset, cookie, scaledOctCookieSize, scaledOctCookieSize);
            }

            // Cookie size in the atlas might not match CookieTexture size.
            // UVRect adjustment must be done with size in atlas.
            var scaledCookieSize = Vector2.one * scaledOctCookieSize;
            AdjustUVRect(ref uvScaleOffset, cookie, ref scaledCookieSize);
            return uvScaleOffset;
        }

        int ComputeOctahedralCookieSize(Texture cookie)
        {
            // Map 6*WxH pixels into 2W*2H pixels, so 4/6 ratio or 66% of cube pixels.
            int octCookieSize = Math.Max(cookie.width, cookie.height);
            if (_settings.atlas.isPow2)
                octCookieSize = octCookieSize * Mathf.NextPowerOfTwo((int)_settings.cubeOctahedralSizeScale);
            else
                octCookieSize = (int)(octCookieSize * _settings.cubeOctahedralSizeScale + 0.5f);
            return octCookieSize;
        }

        private void AdjustUVRect(ref Vector4 uvScaleOffset, Texture cookie, ref Vector2 cookieSize)
        {
            if (uvScaleOffset != Vector4.zero)
            {
                // Shrink by 0.5px to clamp the bilinear sampling to exclude atlas neighbors (no padding)
                ShrinkUVRect(ref uvScaleOffset, 0.5f, ref cookieSize);
            }
        }

        private void ShrinkUVRect(ref Vector4 uvScaleOffset, float amountPixels, ref Vector2 cookieSize)
        {
            var shrinkOffset = Vector2.one * amountPixels / cookieSize;
            var shrinkScale = (cookieSize - Vector2.one * (amountPixels * 2)) / cookieSize;
            uvScaleOffset.z += uvScaleOffset.x * shrinkOffset.x;
            uvScaleOffset.w += uvScaleOffset.y * shrinkOffset.y;
            uvScaleOffset.x *= shrinkScale.x;
            uvScaleOffset.y *= shrinkScale.y;
        }

        void UploadAdditionalLights(CommandBuffer cmd, LightData lightData, ref WorkSlice<LightCookieMapping> validLightMappings, ref WorkSlice<Vector4> validUvRects)
        {
            Debug.Assert(_additionalLightsCookieAtlas != null);
            Debug.Assert(_additionalLightsCookieShaderData != null);

            cmd.SetGlobalTexture(ShaderProperty._AdditionalLightsCookieAtlasTexture, _additionalLightsCookieAtlas.AtlasTexture);
            cmd.SetGlobalFloat(ShaderProperty._AdditionalLightsCookieAtlasTextureFormat, (float)GetLightCookieShaderFormat(_additionalLightsCookieAtlas.AtlasTexture.rt.graphicsFormat));

            // Resize and clear visible light to shader data mapping
            if (_visibleLightIndexToShaderDataIndex.Length < lightData.visibleLights.Length)
                _visibleLightIndexToShaderDataIndex = new int[lightData.visibleLights.Length];

            // Clear
            int len = Math.Min(_visibleLightIndexToShaderDataIndex.Length, lightData.visibleLights.Length);
            for (int i = 0; i < len; i++)
                _visibleLightIndexToShaderDataIndex[i] = -1;

            // Resize or init shader data.
            _additionalLightsCookieShaderData.Resize(_settings.maxAdditionalLights);

            var worldToLights = _additionalLightsCookieShaderData.worldToLights;
            var cookieEnableBits = _additionalLightsCookieShaderData.cookieEnableBits;
            var atlasUVRects = _additionalLightsCookieShaderData.atlasUVRects;
            var lightTypes = _additionalLightsCookieShaderData.lightTypes;

            // Set all rects to "Invalid" zero area (Vector4.zero), just in case they're accessed.
            Array.Clear(atlasUVRects, 0, atlasUVRects.Length);
            // Set all cookies disabled
            cookieEnableBits.Clear();

            // NOTE: technically, we don't need to upload constants again if we knew the lights, atlas (rects) or visible order haven't changed.
            // But detecting that, might be as time consuming as just doing the work.

            // Fill shader data. Layout should match primary light data for additional lights.
            // Currently it's the same as visible lights, but main light(s) dropped.
            for (int i = 0; i < validUvRects.length; i++)
            {
                int visIndex = validLightMappings[i].visibleLightIndex;
                int bufIndex = validLightMappings[i].lightBufferIndex;

                // Update the mapping
                _visibleLightIndexToShaderDataIndex[visIndex] = bufIndex;

                ref var visLight = ref lightData.visibleLights.UnsafeElementAtMutable(visIndex);

                // Update the (cpu) data
                lightTypes[bufIndex] = (int)visLight.lightType;
                worldToLights[bufIndex] = visLight.localToWorldMatrix.inverse;
                atlasUVRects[bufIndex] = validUvRects[i];
                cookieEnableBits[bufIndex] = true;

                // Spot projection
                if (visLight.lightType == LightType.Spot)
                {
                    // VisibleLight.localToWorldMatrix only contains position & rotation.
                    // Multiply projection for spot light.
                    float spotAngle = visLight.spotAngle;
                    float spotRange = visLight.range;
                    var perp = Matrix4x4.Perspective(spotAngle, 1, 0.001f, spotRange);

                    // Cancel embedded camera view axis flip (https://docs.unity3d.com/2021.1/Documentation/ScriptReference/Matrix4x4.Perspective.html)
                    perp.SetColumn(2, perp.GetColumn(2) * -1);

                    // world -> light local -> light perspective
                    worldToLights[bufIndex] = perp * worldToLights[bufIndex];
                }

                // Directional projection
                else if (visLight.lightType == LightType.Directional)
                {
                    Light light = visLight.light;
                    light.TryGetComponent<UltraAdditionalLightData>(out var additionalLightData);
                    {
                        Matrix4x4 cookieUVTransform = Matrix4x4.identity;
                        GetLightUVScaleOffset(ref additionalLightData, ref cookieUVTransform);

                        Matrix4x4 cookieMatrix = s_dirLightProj * cookieUVTransform *
                                                 visLight.localToWorldMatrix.inverse;

                        worldToLights[bufIndex] = cookieMatrix;
                    }
                }
            }

            // Apply changes and upload to GPU
            _additionalLightsCookieShaderData.Upload(cmd);
        }
    }
}
