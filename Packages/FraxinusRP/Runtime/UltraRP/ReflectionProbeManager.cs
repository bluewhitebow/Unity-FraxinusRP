using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    struct ReflectionProbeManager : IDisposable
    {
        unsafe struct CachedProbe
        {
            public uint updateCount;
            public Hash128 imageContentsHash;
            public int size;
            public int mipCount;
            // One for each mip.
            public fixed int dataIndices[k_maxMipCount];
            public fixed int levels[k_maxMipCount];
            public Texture texture;
            public int lastUsed;
            public Vector4 hdrData;
        }

        static class ShaderProperties
        {
            public static readonly int _BoxMin = Shader.PropertyToID("ultraRP_ReflProbes_BoxMin");
            public static readonly int _BoxMax = Shader.PropertyToID("ultraRP_ReflProbes_BoxMax");
            public static readonly int _ProbePosition = Shader.PropertyToID("ultraRP_ReflProbes_ProbePosition");
            public static readonly int _MipScaleOffset = Shader.PropertyToID("ultraRP_ReflProbes_MipScaleOffset");
            public static readonly int _Count = Shader.PropertyToID("ultraRP_ReflProbes_Count");
            public static readonly int _Atlas = Shader.PropertyToID("ultraRP_ReflProbes_Atlas");
        }

        // There is a global max of 7 mips in Unity.
        const int k_maxMipCount = 7;

        int2 _resolution;
        RenderTexture _atlasTexture0;
        RenderTexture _atlasTexture1;
        RTHandle _atlasTexture0Handle;
        BuddyAllocator _atlasAllocator;
        Dictionary<int, CachedProbe> _cache;
        Dictionary<int, int> _warningCache;
        List<int> _needsUpdate;
        List<int> _needsRemove;

        // Pre-allocated arrays for filling constant buffers
        Vector4[] _boxMax;
        Vector4[] _boxMin;
        Vector4[] _probePosition;
        Vector4[] _mipScaleOffset;
        int _preparedProbeCount;

        public RenderTexture atlasRT => _atlasTexture0;
        public RTHandle atlasRTHandle => _atlasTexture0Handle;

        public static ReflectionProbeManager Create()
        {
            var instance = new ReflectionProbeManager();
            instance.Init();
            return instance;
        }

        void Init()
        {
            var maxProbes = UltraRenderPipeline.maxVisibleReflectionProbes;

            // _resolution = math.min((int)reflectionProbeResolution, SystemInfo.maxTextureSize);
            _resolution = 1;
            var format = GraphicsFormat.B10G11R11_UFloatPack32;
            if (!GraphicsFormatCompatibility.IsFormatSupported(format, GraphicsFormatUsage.Render))
                format = GraphicsFormat.R16G16B16A16_SFloat;

            _atlasTexture0 = new RenderTexture(new RenderTextureDescriptor
            {
                width = _resolution.x,
                height = _resolution.y,
                volumeDepth = 1,
                dimension = TextureDimension.Tex2D,
                graphicsFormat = format,
                useMipMap = false,
                msaaSamples = 1
            });
            _atlasTexture0.name = "UltraRP Reflection Probe Atlas";
            _atlasTexture0.filterMode = FilterMode.Bilinear;
            _atlasTexture0.hideFlags = HideFlags.HideAndDontSave;
            _atlasTexture0.Create();
            _atlasTexture0Handle = RTHandles.Alloc(_atlasTexture0);

            _atlasTexture1 = new RenderTexture(_atlasTexture0.descriptor);
            _atlasTexture1.name = "UltraRP Reflection Probe Atlas";
            _atlasTexture1.filterMode = FilterMode.Bilinear;
            _atlasTexture1.hideFlags = HideFlags.HideAndDontSave;

            // The smallest allocatable resolution we want is 4x4. We calculate the number of levels as:
            // log2(max) - log2(4) = log2(max) - 2
            _atlasAllocator = new BuddyAllocator(math.floorlog2(SystemInfo.maxTextureSize) - 2, 2);
            _cache = new Dictionary<int, CachedProbe>(maxProbes);
            _warningCache = new Dictionary<int, int>(maxProbes);
            _needsUpdate = new List<int>(maxProbes);
            _needsRemove = new List<int>(maxProbes);

            _boxMax = new Vector4[maxProbes];
            _boxMin = new Vector4[maxProbes];
            _probePosition = new Vector4[maxProbes];
            _mipScaleOffset = new Vector4[maxProbes * 7];
        }

        public unsafe void UpdateGpuData(CommandBuffer cmd, ref CullingResults cullResults)
        {
            PrepareGpuData(ref cullResults);
            UploadGpuData(cmd);
        }

        // Allocate before RenderGraph imports the atlas so its descriptor and
        // handle refer to the texture that the lighting shaders will sample.
        public unsafe void PrepareGpuData(ref CullingResults cullResults)
        {
            var probes = cullResults.visibleReflectionProbes;
            var probeCount = math.min(probes.Length, UltraRenderPipeline.maxVisibleReflectionProbes);
            var frameIndex = Time.renderedFrameCount;

            // Populate list of probes we need to remove to avoid modifying dictionary while iterating.
            foreach (var (id, cachedProbe) in _cache)
            {
                // Evict probe if not used for more than 1 frame, if the texture no longer exists, or if the size changed.
                if (Math.Abs(cachedProbe.lastUsed - frameIndex) > 1 ||
                    !cachedProbe.texture ||
                    cachedProbe.size != cachedProbe.texture.width)
                {
                    _needsRemove.Add(id);
                    for (var i = 0; i < k_maxMipCount; i++)
                    {
                        if (cachedProbe.dataIndices[i] != -1) 
                            _atlasAllocator.Free(new BuddyAllocation(cachedProbe.levels[i], cachedProbe.dataIndices[i]));
                    }
                }
            }

            foreach (var probeIndex in _needsRemove)
                _cache.Remove(probeIndex);

            _needsRemove.Clear();

            foreach (var (id, lastUsed) in _warningCache)
            {
                if (Math.Abs(lastUsed - frameIndex) > 1)
                    _needsRemove.Add(id);
            }

            foreach (var probeIndex in _needsRemove)
                _warningCache.Remove(probeIndex);

            _needsRemove.Clear();

            var showFullWarning = false;
            var requiredAtlasSize = math.int2(0, 0);

            for (var probeIndex = 0; probeIndex < probeCount; probeIndex++)
            {
                var probe = probes[probeIndex];

                var texture = probe.texture;
                if (!probe.reflectionProbe || !texture)
                    continue;
                var id = probe.reflectionProbe.GetInstanceID();
                var wasCached = _cache.TryGetValue(id, out var cachedProbe);

                // A custom/baked probe can replace its cubemap without changing
                // the component ID or updateCount. Reallocate using the current
                // texture's size and upload it even when both counts are equal.
                if (wasCached && (cachedProbe.texture != texture || cachedProbe.size != texture.width))
                {
                    for (int mip = 0; mip < k_maxMipCount; ++mip)
                        if (cachedProbe.dataIndices[mip] != -1)
                            _atlasAllocator.Free(new BuddyAllocation(cachedProbe.levels[mip], cachedProbe.dataIndices[mip]));
                    _cache.Remove(id);
                    wasCached = false;
                    cachedProbe = default;
                }

                if (!wasCached)
                {
                    cachedProbe.size = texture.width;
                    var mipCount = math.ceillog2(cachedProbe.size * 4) + 1;
                    var level = _atlasAllocator.levelCount + 2 - mipCount;
                    cachedProbe.mipCount = math.min(mipCount, k_maxMipCount);
                    cachedProbe.texture = texture;

                    var mip = 0;
                    for (; mip < cachedProbe.mipCount; mip++)
                    {
                        // Clamp to maximum level. This is relevant for 64x64 and lower, which will have valid content
                        // in 1x1 mip. The octahedron size is double the face size, so that ends up at 2x2. Due to
                        // borders the final mip must be 4x4 as that leaves 2x2 texels for the octahedron.
                        var mipLevel = math.min(level + mip, _atlasAllocator.levelCount - 1);
                        if (!_atlasAllocator.TryAllocate(mipLevel, out var allocation))
                            break;
                        // We split up the allocation struct because C# cannot do struct fixed arrays :(
                        cachedProbe.levels[mip] = allocation.level;
                        cachedProbe.dataIndices[mip] = allocation.index;
                        var scaleOffset = (int4)(GetScaleOffset(mipLevel, allocation.index, true, false) * _resolution.xyxy);
                        requiredAtlasSize = math.max(requiredAtlasSize, scaleOffset.zw + scaleOffset.xy);
                    }

                    // Check if we ran out of space in the atlas.
                    if (mip < cachedProbe.mipCount)
                    {
                        if (!_warningCache.ContainsKey(id))
                            showFullWarning = true;

                        _warningCache[id] = frameIndex;
                        for (var i = 0; i < mip; i++) 
                            _atlasAllocator.Free(new BuddyAllocation(cachedProbe.levels[i], cachedProbe.dataIndices[i]));
                        for (var i = 0; i < k_maxMipCount; i++) 
                            cachedProbe.dataIndices[i] = -1;
                        continue;
                    }

                    for (; mip < k_maxMipCount; mip++)
                        cachedProbe.dataIndices[mip] = -1;
                }

                var needsUpdate = !wasCached || cachedProbe.updateCount != texture.updateCount;
#if UNITY_EDITOR
                needsUpdate |= cachedProbe.imageContentsHash != texture.imageContentsHash;
#endif
                needsUpdate |= cachedProbe.hdrData != probe.hdrData;    // The probe needs update if the runtime intensity multiplier changes

                if (needsUpdate)
                {
                    cachedProbe.updateCount = texture.updateCount;
#if UNITY_EDITOR
                    cachedProbe.imageContentsHash = texture.imageContentsHash;
#endif
                    _needsUpdate.Add(id);
                }

                // If the probe is set to be updated every frame, we assign the last used frame to -1 so it's evicted in next frame.
                if (probe.reflectionProbe.refreshMode == ReflectionProbeRefreshMode.EveryFrame)
                    cachedProbe.lastUsed = -1;
                else
                    cachedProbe.lastUsed = frameIndex;

                cachedProbe.hdrData = probe.hdrData;
                _cache[id] = cachedProbe;
            }

            // Grow the atlas if it's not big enough to contain the current allocations.
            if (math.any(_resolution < requiredAtlasSize))
            {
                requiredAtlasSize = math.max(_resolution, math.ceilpow2(requiredAtlasSize));
                var desc = _atlasTexture0.descriptor;
                desc.width = requiredAtlasSize.x;
                desc.height = requiredAtlasSize.y;
                _atlasTexture1.width = requiredAtlasSize.x;
                _atlasTexture1.height = requiredAtlasSize.y;
                _atlasTexture1.Create();

                if (_atlasTexture0.width != 1)
                {
                    if (SystemInfo.copyTextureSupport != CopyTextureSupport.None)
                        Graphics.CopyTexture(_atlasTexture0, 0, 0, 0, 0, _resolution.x, _resolution.y, _atlasTexture1, 0, 0, 0, 0);
                    else
                        Graphics.Blit(_atlasTexture0, _atlasTexture1, (float2)_resolution / requiredAtlasSize, Vector2.zero);
                }

                _atlasTexture0Handle.Release();
                _atlasTexture0.Release();
                (_atlasTexture0, _atlasTexture1) = (_atlasTexture1, _atlasTexture0);
                _atlasTexture0Handle = RTHandles.Alloc(_atlasTexture0);
                _resolution = requiredAtlasSize;
            }

            _preparedProbeCount = probeCount;
            Array.Clear(_probePosition, 0, _probePosition.Length);
            for (var probeIndex = 0; probeIndex < probeCount; probeIndex++)
            {
                var probe = probes[probeIndex];
                if (!probe.reflectionProbe)
                    continue;
                var id = probe.reflectionProbe.GetInstanceID();
                // Keep the same slot as the CPU cluster masks. A missing or
                // rejected probe has position.w == 0 and is skipped in HLSL.
                var dataIndex = probeIndex;
                if (!_cache.TryGetValue(id, out var cachedProbe) || !probe.texture)
                {
                    continue;
                }

                _boxMax[dataIndex] = new Vector4(probe.bounds.max.x, probe.bounds.max.y, probe.bounds.max.z, probe.blendDistance);
                _boxMin[dataIndex] = new Vector4(probe.bounds.min.x, probe.bounds.min.y, probe.bounds.min.z, probe.importance);
                _probePosition[dataIndex] = new Vector4(probe.localToWorldMatrix.m03, probe.localToWorldMatrix.m13, probe.localToWorldMatrix.m23, (probe.isBoxProjection ? 1 : -1) * (cachedProbe.mipCount));
                for (var i = 0; i < cachedProbe.mipCount; i++)
                    _mipScaleOffset[dataIndex * k_maxMipCount + i] = GetScaleOffset(cachedProbe.levels[i], cachedProbe.dataIndices[i], false, false);
            }

            if (showFullWarning)
                Debug.LogWarning("A number of reflection probes have been skipped due to the reflection probe atlas being full.\nTo fix this, you can decrease the number or resolution of probes.");

        }

        public unsafe void UploadGpuData(CommandBuffer cmd)
        {
            using (new ProfilingScope(cmd, ProfilingSampler.Get(UltraRPProfileId.UpdateReflectionProbeAtlas)))
            {
                cmd.SetRenderTarget(_atlasTexture0);

                foreach (var probeId in _needsUpdate)
                {
                    var cachedProbe = _cache[probeId];
                    for (var mip = 0; mip < cachedProbe.mipCount; mip++)
                    {
                        var level = cachedProbe.levels[mip];
                        var dataIndex = cachedProbe.dataIndices[mip];
                        // If we need to y-flip we will instead flip the atlas since that is updated less frequent and then the lookup should be correct.
                        // By doing this we won't have to y-flip the lookup in the shader code. 
                        var scaleBias = GetScaleOffset(level, dataIndex, true, !SystemInfo.graphicsUVStartsAtTop);
                        var sizeWithoutPadding = (1 << (_atlasAllocator.levelCount + 1 - level)) - 2;
                        Blitter.BlitCubeToOctahedral2DQuadWithPadding(cmd, cachedProbe.texture, new Vector2(sizeWithoutPadding, sizeWithoutPadding), scaleBias, mip, true, 2, cachedProbe.hdrData);
                    }
                }

                cmd.SetGlobalVectorArray(ShaderProperties._BoxMin, _boxMin);
                cmd.SetGlobalVectorArray(ShaderProperties._BoxMax, _boxMax);
                cmd.SetGlobalVectorArray(ShaderProperties._ProbePosition, _probePosition);
                cmd.SetGlobalVectorArray(ShaderProperties._MipScaleOffset, _mipScaleOffset);
                cmd.SetGlobalFloat(ShaderProperties._Count, _preparedProbeCount);
                cmd.SetGlobalTexture(ShaderProperties._Atlas, _atlasTexture0);
            }

            _needsUpdate.Clear();
        }

        float4 GetScaleOffset(int level, int dataIndex, bool includePadding, bool yflip)
        {
            // level = m_AtlasAllocator.levelCount + 2 - (log2(size) + 1) <=>
            // log2(size) + 1 = m_AtlasAllocator.levelCount + 2 - level <=>
            // log2(size) = m_AtlasAllocator.levelCount + 1 - level <=>
            // size = 2^(m_AtlasAllocator.levelCount + 1 - level)
            var size = (1 << (_atlasAllocator.levelCount + 1 - level));
            var coordinate = SpaceFillingCurves.DecodeMorton2D((uint)dataIndex);
            var scale = (size - (includePadding ? 0 : 2)) / ((float2)_resolution);
            var bias = ((float2)coordinate * size + (includePadding ? 0 : 1)) / (_resolution);
            if (yflip) bias.y = 1.0f - bias.y - scale.y;
            return math.float4(scale, bias);
        }

        public void Dispose()
        {
            if (_atlasTexture0)
            {
                _atlasTexture0.Release();
                _atlasTexture0Handle.Release();
            }
            _atlasAllocator.Dispose();

            UnityEngine.Object.DestroyImmediate(_atlasTexture0);
            UnityEngine.Object.DestroyImmediate(_atlasTexture1);
            
            this = default;
        }
    }
}
