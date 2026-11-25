using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UnityEngine.Rendering
{
    internal sealed partial class APRTProbeReferenceVolume
    {
        // Mirrors APRTSamplingContext in APRTSampling.hlsl. Local lookup indices
        // remain unchanged; offsets namespace each Baking Set inside this atlas.
        [StructLayout(LayoutKind.Sequential)]
        struct SamplingAtlasInt4
        {
            internal int x, y, z, w;
            internal SamplingAtlasInt4(int x, int y, int z, int w)
            { this.x = x; this.y = y; this.z = z; this.w = w; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SamplingAtlasDescriptor
        {
            internal SamplingAtlasInt4 probes, lookup, layout, flags;
            internal Vector4 gridDimensions, samplingSpacing, probeOffset;
            internal Vector4 receiver, response, boundsMin, boundsMax;
            internal Matrix4x4 worldToGrid, worldToSamplingGrid, samplingGridToWorld;
        }

        sealed class SamplingAtlasEntry
        {
            internal APRTProbeReferenceVolume context;
            internal int generation;
            internal GraphicsBuffer[] sources;
            internal SamplingAtlasDescriptor descriptor;
        }

        readonly List<SamplingAtlasEntry> m_SamplingAtlasEntries = new();
        readonly List<APRTProbeReferenceVolume> m_PreparedSamplingContexts = new();
        readonly List<APRTProbeReferenceVolume> m_ResidentSamplingScratch = new();
        SamplingAtlasDescriptor[] m_SamplingDescriptors = Array.Empty<SamplingAtlasDescriptor>();
        static readonly int[] s_AtlasSourceIds = PropertyIds("_SourceSH", "_SourceSkySH", "_SourceAxis", "_SourcePositions", "_SourceValidity", "_SourceMoments",
            "_SourceBricks", "_SourceBrickProbes", "_SourceHash", "_SourceGrid", "_SourceDomains");
        static readonly int[] s_AtlasTargetIds = PropertyIds("_TargetSH", "_TargetSkySH", "_TargetAxis", "_TargetPositions", "_TargetValidity", "_TargetMoments",
            "_TargetBricks", "_TargetBrickProbes", "_TargetHash", "_TargetGrid", "_TargetDomains");
        static readonly int[] s_AtlasGlobalIds = PropertyIds("_APRTRuntimeSH", "_APRTSkyIrradianceSH", "_APRTBakedAxis", "_APRTProbePositions", "_APRTProbeValidity", "_APRTVisibilityMoments",
            "_APRTBricks", "_APRTBrickProbeIndices", "_APRTBrickHash", "_APRTGridProbeIndices", "_APRTWorldToDomains", "_APRTSamplingContexts");
        static readonly int[] s_TopologyCountIds = PropertyIds("_CopyTopologyCount0", "_CopyTopologyCount1", "_CopyTopologyCount2", "_CopyTopologyCount3", "_CopyTopologyCount4");
        static readonly int[] s_TopologyOffsetIds = PropertyIds("_CopyTopologyOffset0", "_CopyTopologyOffset1", "_CopyTopologyOffset2", "_CopyTopologyOffset3", "_CopyTopologyOffset4");

        static int[] PropertyIds(params string[] names)
        {
            var ids = new int[names.Length];
            for (int i = 0; i < ids.Length; ++i) ids[i] = Shader.PropertyToID(names[i]);
            return ids;
        }
        GraphicsBuffer[] m_SamplingAtlasBuffers = Array.Empty<GraphicsBuffer>();
        ComputeShader m_SamplingAtlasCopy;
        int m_CopySamplingProbesKernel, m_CopySamplingTopologyKernel;

        internal GraphicsBuffer[] samplingAtlasBuffers => m_SamplingAtlasBuffers;
        internal IReadOnlyList<APRTProbeReferenceVolume> preparedSamplingContexts => m_PreparedSamplingContexts;

        bool SamplingAtlasNeedsReleaseAtFrameEnd()
        {
            foreach (var context in m_PreparedSamplingContexts)
                if (ReferenceEquals(context.m_RuntimeOwner, this) && !m_LoadedSetRuntimes.Contains(context))
                    return true;
            return m_SamplingAtlasBuffers.Length != 0 && !SamplingAtlasMatches(m_PreparedSamplingContexts);
        }

        internal long samplingAtlasMemoryBytes
        {
            get
            {
                long bytes = 0;
                foreach (var buffer in m_SamplingAtlasBuffers)
                    if (buffer != null) bytes += (long)buffer.count * buffer.stride;
                return bytes;
            }
        }

        static GraphicsBuffer[] SamplingAtlasSources(APRTProbeReferenceVolume context) => new[]
        {
            context.runtimeSHBuffer, context.runtimeSkyIrradianceBuffer, context.m_AxisBuffer,
            context.m_PositionBuffer, context.m_ValidityBuffer, context.m_MomentBuffer,
            context.m_BrickBuffer, context.m_BrickProbeBuffer, context.m_BrickHashBuffer,
            context.m_GridIndexBuffer, context.m_DomainBuffer
        };

        static GraphicsBuffer SamplingAtlasSource(APRTProbeReferenceVolume context, int index) => index switch
        {
            0 => context.runtimeSHBuffer, 1 => context.runtimeSkyIrradianceBuffer, 2 => context.m_AxisBuffer,
            3 => context.m_PositionBuffer, 4 => context.m_ValidityBuffer, 5 => context.m_MomentBuffer,
            6 => context.m_BrickBuffer, 7 => context.m_BrickProbeBuffer, 8 => context.m_BrickHashBuffer,
            9 => context.m_GridIndexBuffer, _ => context.m_DomainBuffer
        };

        internal static long EstimateSamplingAtlasContextBytes(APRTProbeReferenceVolume context)
        {
            if (context == null) return 0;
            long bytes = Marshal.SizeOf<SamplingAtlasDescriptor>();
            for (int i = 0; i < 11; ++i)
            {
                var buffer = SamplingAtlasSource(context, i);
                if (buffer != null) bytes += (long)buffer.count * buffer.stride;
            }
            return bytes;
        }

        bool SamplingAtlasMatches(IReadOnlyList<APRTProbeReferenceVolume> contexts)
        {
            if (contexts.Count != m_SamplingAtlasEntries.Count) return false;
            for (int i = 0; i < contexts.Count; ++i)
            {
                var context = contexts[i];
                var cached = m_SamplingAtlasEntries[i];
                if (!ReferenceEquals(context, cached.context) || context.m_ScenarioAtlasGeneration != cached.generation)
                    return false;
                for (int j = 0; j < cached.sources.Length; ++j)
                    if (!ReferenceEquals(SamplingAtlasSource(context, j), cached.sources[j])) return false;
            }
            return true;
        }

        // Pass the complete loaded source universe, even when this camera has
        // disabled one set. Resource topology is shared by every camera in the
        // frame; CopyAndBindSamplingAtlas selects the active subset separately.
        // The caller imports and pins preparedSamplingContexts through execution.
        internal bool PrepareSamplingAtlas(IReadOnlyList<APRTProbeReferenceVolume> contexts)
        {
            if (m_ResourcesPinned)
                return m_PreparedSamplingContexts.Count > 0 &&
                    (m_PreparedSamplingContexts.Count == 1 || SamplingAtlasMatches(m_PreparedSamplingContexts));
            var resident = m_ResidentSamplingScratch;
            resident.Clear();
            if (contexts != null)
                for (int i = 0; i < contexts.Count; ++i)
                {
                    var context = contexts[i];
                    // Intensity and scenario validity are camera settings. They
                    // must not remove a resident source needed by another camera.
                    // Async/unavailable cells may join the topology next frame.
                    if (context == null || resident.Contains(context) || context.currentBakingSet == null ||
                        !context.m_Initialized || !context.m_Enabled || !context.EnsureRuntimeData(context.currentBakingSet) ||
                        context.m_RuntimeData == null || context.probeCount <= 0) continue;
                    bool complete = true;
                    for (int j = 0; j < 11; ++j) complete &= SamplingAtlasSource(context, j) != null;
                    if (complete) resident.Add(context);
                }
            if (resident.Count == 0) { ReleaseSamplingAtlas(); return false; }
            if (resident.Count == 1)
            {
                ReleaseSamplingAtlas();
                m_PreparedSamplingContexts.Add(resident[0]);
                return true;
            }
            if (SamplingAtlasMatches(resident) && m_SamplingAtlasBuffers.Length == 12) return true;
            m_SamplingAtlasCopy ??= Resources.Load<ComputeShader>("APRTSamplingAtlas");
            if (m_SamplingAtlasCopy == null || !SystemInfo.supportsComputeShaders) return false;
            m_CopySamplingProbesKernel = m_SamplingAtlasCopy.FindKernel("CopyProbes");
            m_CopySamplingTopologyKernel = m_SamplingAtlasCopy.FindKernel("CopyTopology");
            ReleaseSamplingAtlas();
            var totals = new int[11];
            var owned = new List<GraphicsBuffer>();
            try
            {
                foreach (var context in resident)
                {
                    if (context == null || context.m_RuntimeData == null || context.probeCount <= 0)
                        throw new InvalidOperationException("APRT sampling atlas requires resident source contexts.");
                    var sources = SamplingAtlasSources(context);
                    foreach (var buffer in sources)
                        if (buffer == null) throw new InvalidOperationException("APRT sampling atlas source buffer is missing.");
                    var descriptor = new SamplingAtlasDescriptor
                    {
                        probes = new SamplingAtlasInt4(totals[3], context.probeCount, totals[6], context.m_BrickCount),
                        lookup = new SamplingAtlasInt4(totals[7], totals[8], context.m_HashMask, context.m_HashMaxSteps),
                        layout = new SamplingAtlasInt4(totals[9], totals[10], context.m_RuntimeData.samplingDomains.Length,
                            context.m_RuntimeData.bakedSimplificationLevels)
                    };
                    m_SamplingAtlasEntries.Add(new SamplingAtlasEntry
                    {
                        context = context, generation = context.m_ScenarioAtlasGeneration,
                        sources = sources, descriptor = descriptor
                    });
                    for (int i = 0; i < totals.Length; ++i) totals[i] = checked(totals[i] + sources[i].count);
                }
                var strides = new[] { 16, 16, 16, 12, 4, 16, 16, 4, 4, 4, 64 };
                for (int i = 0; i < totals.Length; ++i)
                    owned.Add(new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, totals[i]), strides[i]));
                owned.Add(new GraphicsBuffer(GraphicsBuffer.Target.Structured, resident.Count, Marshal.SizeOf<SamplingAtlasDescriptor>()));
                m_SamplingAtlasBuffers = owned.ToArray();
                m_SamplingDescriptors = new SamplingAtlasDescriptor[resident.Count];
                m_PreparedSamplingContexts.AddRange(resident);
                return true;
            }
            catch (Exception error)
            {
                foreach (var buffer in owned) buffer.Dispose();
                m_SamplingAtlasEntries.Clear();
                m_PreparedSamplingContexts.Clear();
                Debug.LogException(error);
                return false;
            }
        }

        SamplingAtlasDescriptor UpdateSamplingAtlasDescriptor(SamplingAtlasEntry entry)
        {
            var context = entry.context;
            var data = context.m_RuntimeData;
            var settings = context.EffectiveRelightSettings();
            var descriptor = entry.descriptor;
            descriptor.flags = new SamplingAtlasInt4(data.regularGrid ? 1 : 0, (int)data.lightingModel,
                context.m_RuntimeInitialized && context.m_SamplingAllowed ? 1 : 0, 1);
            descriptor.gridDimensions = new Vector4(data.regularGridDimensions.x, data.regularGridDimensions.y, data.regularGridDimensions.z, 0);
            Vector3 spacing = Vector3.one * data.regularGridSpacing;
            Vector3 origin = data.regularGridOrigin;
            if (data.regularGridSamplingSize != Vector3.zero)
            {
                origin = data.regularGridSamplingOrigin;
                var last = data.regularGridDimensions - Vector3Int.one;
                spacing = new Vector3(data.regularGridSamplingSize.x / Mathf.Max(1, last.x),
                    data.regularGridSamplingSize.y / Mathf.Max(1, last.y), data.regularGridSamplingSize.z / Mathf.Max(1, last.z));
            }
            descriptor.worldToGrid = data.regularGrid
                ? Matrix4x4.TRS(data.regularGridOrigin, data.regularGridRotation.normalized, Vector3.one * data.regularGridSpacing).inverse
                : Matrix4x4.identity;
            descriptor.samplingGridToWorld = data.regularGrid
                ? Matrix4x4.TRS(origin, data.regularGridRotation.normalized, spacing) : Matrix4x4.identity;
            descriptor.worldToSamplingGrid = descriptor.samplingGridToWorld.inverse;
            descriptor.samplingSpacing = spacing;
            descriptor.probeOffset = new Vector4(data.bakedProbeOffset.x, data.bakedProbeOffset.y,
                data.bakedProbeOffset.z, context.m_MinBrickSize);
            descriptor.receiver = new Vector4(Intensity(settings.giIntensity), settings.addAmbientLighting ? 1 : 0,
                Intensity(settings.normalBias), Intensity(settings.visibilityBias));
            descriptor.response = new Vector4(1 - Mathf.Clamp01(settings.probeInvalidityThreshold), settings.leakReduction ? 1 : 0,
                Intensity(settings.floorBoost), Mathf.Max(1, Intensity(settings.floorPower)));
            // Bounds are a cheap rejection before the exact oriented grid/domain
            // test. Half a grid step preserves receiving floor/wall surfaces at
            // the edge without extending a grid across the whole loaded world.
            var bounds = data.globalBounds;
            if (data.regularGrid)
            {
                var last = (Vector3)(data.regularGridDimensions - Vector3Int.one);
                var matrix = descriptor.samplingGridToWorld;
                bounds = new Bounds(matrix.MultiplyPoint3x4(-Vector3.one * .5f), Vector3.zero);
                for (int corner = 0; corner < 8; ++corner)
                    bounds.Encapsulate(matrix.MultiplyPoint3x4(new Vector3(
                        (corner & 1) == 0 ? -.5f : last.x + .5f,
                        (corner & 2) == 0 ? -.5f : last.y + .5f,
                        (corner & 4) == 0 ? -.5f : last.z + .5f)));
            }
            descriptor.boundsMin = bounds.min;
            descriptor.boundsMax = bounds.max;
            return descriptor;
        }

        // Record copies after the active contexts' relight. The prepared source
        // universe stays fixed; absent/disabled contexts get an inactive camera
        // descriptor. No GPU readback or topology reallocation occurs here.
        internal void CopyAndBindSamplingAtlas(CommandBuffer cmd, IReadOnlyList<APRTProbeReferenceVolume> contexts)
        {
            if (cmd == null) return;
            if (contexts == null || contexts.Count == 0)
            { cmd.SetGlobalInt("_APRTSamplingSetCount", 0); cmd.SetGlobalFloat("_EnableAPRT", 0); return; }
            if (m_PreparedSamplingContexts.Count == 1)
            {
                var source = m_PreparedSamplingContexts[0];
                bool active = false;
                for (int i = 0; i < contexts.Count; ++i) active |= ReferenceEquals(source, contexts[i]);
                source.BindPRVRuntimeResources(cmd, active);
                cmd.SetGlobalInt("_APRTSamplingSetCount", 0);
                return;
            }
            if (m_SamplingAtlasBuffers.Length != 12 || !SamplingAtlasMatches(m_PreparedSamplingContexts))
            { cmd.SetGlobalInt("_APRTSamplingSetCount", 0); cmd.SetGlobalFloat("_EnableAPRT", 0); return; }
            var descriptors = m_SamplingDescriptors;
            bool anyActive = false;
            for (int i = 0; i < descriptors.Length; ++i)
            {
                var entry = m_SamplingAtlasEntries[i];
                descriptors[i] = UpdateSamplingAtlasDescriptor(entry);
                bool active = false;
                for (int j = 0; j < contexts.Count; ++j) active |= ReferenceEquals(entry.context, contexts[j]);
                if (!active) descriptors[i].flags.z = 0;
                anyActive |= descriptors[i].flags.z != 0;
                var descriptor = descriptors[i];
                cmd.SetComputeIntParam(m_SamplingAtlasCopy, "_CopyProbeCount", descriptor.probes.y);
                cmd.SetComputeIntParam(m_SamplingAtlasCopy, "_CopyProbeOffset", descriptor.probes.x);
                for (int buffer = 0; buffer < 6; ++buffer)
                {
                    cmd.SetComputeBufferParam(m_SamplingAtlasCopy, m_CopySamplingProbesKernel, s_AtlasSourceIds[buffer], entry.sources[buffer]);
                    cmd.SetComputeBufferParam(m_SamplingAtlasCopy, m_CopySamplingProbesKernel, s_AtlasTargetIds[buffer], m_SamplingAtlasBuffers[buffer]);
                }
                cmd.DispatchCompute(m_SamplingAtlasCopy, m_CopySamplingProbesKernel, (descriptor.probes.y + 63) / 64, 1, 1);
                int maxCount = 1;
                for (int buffer = 0; buffer < 5; ++buffer)
                {
                    var source = entry.sources[buffer + 6];
                    int offset = buffer switch { 0 => descriptor.probes.z, 1 => descriptor.lookup.x, 2 => descriptor.lookup.y,
                        3 => descriptor.layout.x, _ => descriptor.layout.y };
                    cmd.SetComputeIntParam(m_SamplingAtlasCopy, s_TopologyCountIds[buffer], source.count);
                    cmd.SetComputeIntParam(m_SamplingAtlasCopy, s_TopologyOffsetIds[buffer], offset);
                    cmd.SetComputeBufferParam(m_SamplingAtlasCopy, m_CopySamplingTopologyKernel, s_AtlasSourceIds[buffer + 6], source);
                    cmd.SetComputeBufferParam(m_SamplingAtlasCopy, m_CopySamplingTopologyKernel, s_AtlasTargetIds[buffer + 6], m_SamplingAtlasBuffers[buffer + 6]);
                    maxCount = Math.Max(maxCount, source.count);
                }
                cmd.DispatchCompute(m_SamplingAtlasCopy, m_CopySamplingTopologyKernel, (maxCount + 63) / 64, 1, 1);
            }
            cmd.SetBufferData(m_SamplingAtlasBuffers[11], descriptors);
            for (int i = 0; i < s_AtlasGlobalIds.Length; ++i) cmd.SetGlobalBuffer(s_AtlasGlobalIds[i], m_SamplingAtlasBuffers[i]);
            cmd.SetGlobalInt("_APRTSamplingSetCount", descriptors.Length);
            cmd.SetGlobalInt("_APRTProbeCount", m_SamplingAtlasBuffers[3].count);
            cmd.SetGlobalInt("_APRTDebugMode", (int)m_APRTDebugMode);
            cmd.SetGlobalFloat("_APRTGIIntensity", 1);
            cmd.SetGlobalFloat("_EnableAPRT", anyActive ? 1 : 0);
        }

        internal void ReleaseSamplingAtlas()
        {
            foreach (var buffer in m_SamplingAtlasBuffers) buffer?.Dispose();
            m_SamplingAtlasBuffers = Array.Empty<GraphicsBuffer>();
            m_SamplingDescriptors = Array.Empty<SamplingAtlasDescriptor>();
            m_SamplingAtlasEntries.Clear();
            m_PreparedSamplingContexts.Clear();
        }
    }
}
