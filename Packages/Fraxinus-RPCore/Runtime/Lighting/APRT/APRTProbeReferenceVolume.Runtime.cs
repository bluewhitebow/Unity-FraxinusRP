using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UnityEngine.Rendering
{
    internal sealed partial class APRTProbeReferenceVolume
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BrickInfo { public int x, y, z, subdivision; }

        APRTBakedData m_RuntimeData;
        GraphicsBuffer m_AxisBuffer, m_MomentBuffer, m_PositionBuffer, m_ValidityBuffer;
        GraphicsBuffer m_BrickBuffer, m_BrickProbeBuffer, m_BrickHashBuffer, m_GridIndexBuffer, m_DomainBuffer;
        GraphicsBuffer m_SkyTransferBuffer, m_SkyTransferRGBBuffer;
        GraphicsBuffer m_BakedDirectionalBuffer, m_BakedSkyBuffer;
        readonly Dictionary<string, int> m_BakedScenarioOffsets = new();
        readonly Dictionary<string, float> m_BakedScenarioSkyMagnitude = new();
        int m_ScenarioAtlasGeneration, m_LoadedScenarioRevision, m_LoadedLayoutHash;
        APRTSkyEnvironment m_SkyEnvironment;
        APRTSkyEnvironment m_LegacySkyEnvironment;
        Material m_LegacySceneSkybox;
        sealed class CameraSkyEnvironment
        {
            internal Camera camera;
            internal APRTSkyEnvironment environment;
            internal Material sceneSkybox;
        }
        readonly Dictionary<int, CameraSkyEnvironment> m_CameraSkyEnvironments = new();
        readonly List<int> m_DestroyedSkyCameras = new();
        GraphicsBuffer m_RuntimeSHBuffer, m_RuntimeSkyIrradianceBuffer;
        GraphicsBuffer[] m_GPUBufferAllocations = Array.Empty<GraphicsBuffer>();
        int m_ProbeCount;
        internal GraphicsBuffer runtimeSHBuffer { get => focusedRuntime?.runtimeSHBuffer ?? m_RuntimeSHBuffer; private set => m_RuntimeSHBuffer = value; }
        internal GraphicsBuffer runtimeSkyIrradianceBuffer { get => focusedRuntime?.runtimeSkyIrradianceBuffer ?? m_RuntimeSkyIrradianceBuffer; private set => m_RuntimeSkyIrradianceBuffer = value; }
        internal GraphicsBuffer[] gpuBuffers { get => focusedRuntime?.gpuBuffers ?? m_GPUBufferAllocations; private set => m_GPUBufferAllocations = value; }
        internal ComputeShader relightCompute { get; private set; }
        internal int probeCount { get => focusedRuntime?.probeCount ?? m_ProbeCount; private set => m_ProbeCount = value; }
        internal APRTLightingModel lightingModel => m_RuntimeData?.lightingModel ?? APRTLightingModel.DiffuseTransfer;
        int m_RelightKernel, m_HashMask, m_HashMaxSteps, m_BrickCount;
        bool m_RuntimeInitialized;
        bool m_SamplingAllowed;
        bool m_HasEffectiveSettings;
        bool m_LastRelightUsedLegacySettings;
        APRTRelightSettings m_EffectiveSettings;
        int m_LastRelightCameraId;
        float m_EffectiveSkyRadianceMagnitude;
        bool m_EffectiveSkyCacheInitialized, m_EffectiveSkyHasValidSource;
        Material m_EffectiveSceneSkybox;
        readonly HashSet<string> m_LoadedScenes = new();
        readonly Vector4[] m_EmptySkyRadiance = new Vector4[9];
        internal bool hasSkyIrradianceTransfer => m_RuntimeData?.hasSkyIrradianceTransfer == true;
        internal bool hasSkyBounceTransfer => m_RuntimeData?.hasSkyBounceTransfer == true;
        internal bool CanSampleForCamera(int baseCameraId)
        {
            if (coordinatesBakingSets)
            {
                foreach (var runtime in m_LoadedSetRuntimes) if (runtime.CanSampleForCamera(baseCameraId)) return true;
                return false;
            }
            return m_SamplingAllowed && DataHasBeenLoaded() && m_LastRelightCameraId == baseCameraId;
        }

        internal void InvalidateCameraSampling()
        {
            foreach (var runtime in m_LoadedSetRuntimes) runtime.InvalidateCameraSampling();
            m_SamplingAllowed = false;
            m_RuntimeInitialized = false;
            m_HasEffectiveSettings = false;
            m_LastRelightScenario = null;
        }

        APRTRelightSettings LegacyRelightSettings() => APRTRelightSettings.FromBakingSet(currentBakingSet,
            (currentBakingSet?.GetBakedData(k_BackendId) as APRTBakedData)?.lightingModel ?? lightingModel);

        APRTRelightSettings EffectiveRelightSettings() => m_HasEffectiveSettings && !m_LastRelightUsedLegacySettings
            ? m_EffectiveSettings : LegacyRelightSettings();

        internal void UpdateSkyEnvironment(Light sun) => UpdateSkyEnvironment(sun, LegacyRelightSettings(), null);

        internal void UpdateSkyEnvironment(Light sun, APRTRelightSettings settings) => UpdateSkyEnvironment(sun, settings, null);

        internal void UpdateSkyEnvironment(Light sun, APRTRelightSettings settings, Camera camera)
        {
            // Camera callbacks can select different scene skyboxes. Keeping their
            // captures separately prevents repeated readback when they alternate.
            m_DestroyedSkyCameras.Clear();
            foreach (var entry in m_CameraSkyEnvironments)
                if (entry.Value.camera == null) m_DestroyedSkyCameras.Add(entry.Key);
            foreach (int id in m_DestroyedSkyCameras)
            {
                var environment = m_CameraSkyEnvironments[id].environment;
                if (ReferenceEquals(m_SkyEnvironment, environment)) m_SkyEnvironment = null;
                environment.Dispose();
                m_CameraSkyEnvironments.Remove(id);
            }
            if (!hasSkyIrradianceTransfer || currentBakingSet == null) return;
            if (Intensity(settings.skyIntensity) <= 0) return;
            if (camera == null)
            {
                m_LegacySkyEnvironment ??= new APRTSkyEnvironment();
                m_LegacySceneSkybox = RenderSettings.skybox;
                m_SkyEnvironment = m_LegacySkyEnvironment;
            }
            else
            {
                int id = camera.GetInstanceID();
                if (!m_CameraSkyEnvironments.TryGetValue(id, out var entry))
                {
                    entry = new CameraSkyEnvironment { camera = camera, environment = new APRTSkyEnvironment() };
                    m_CameraSkyEnvironments.Add(id, entry);
                }
                entry.sceneSkybox = RenderSettings.skybox;
                m_SkyEnvironment = entry.environment;
            }
            m_SkyEnvironment.Update(null, RenderSettings.skybox, sun);
        }

        internal Vector4[] GetSkyRadianceCoefficients() => GetSkyRadianceCoefficients(null);

        internal Vector4[] GetSkyRadianceCoefficients(Camera camera)
        {
            // RenderGraph records a stable snapshot. GPU capture and synchronous
            // readback must finish before any camera context commands are recorded.
            var environment = camera == null ? m_LegacySkyEnvironment :
                m_CameraSkyEnvironments.TryGetValue(camera.GetInstanceID(), out var entry) ? entry.environment : null;
            return hasSkyIrradianceTransfer && environment?.hasValidSource == true
                ? (Vector4[])environment.radianceCoefficients.Clone() : m_EmptySkyRadiance;
        }

        internal void CopySkyRadianceCoefficients(Camera camera, Vector4[] destination)
        {
            if (destination == null || destination.Length != 9)
                throw new ArgumentException("Sky radiance requires nine coefficients.", nameof(destination));
            var environment = camera == null ? m_LegacySkyEnvironment :
                m_CameraSkyEnvironments.TryGetValue(camera.GetInstanceID(), out var entry) ? entry.environment : null;
            Array.Copy(hasSkyIrradianceTransfer && environment?.hasValidSource == true
                ? environment.radianceCoefficients : m_EmptySkyRadiance, destination, 9);
        }

        internal void ReloadBakedData()
        {
            foreach (var runtime in m_LoadedSetRuntimes) runtime.ReloadBakedData();
            if (m_ResourcesPinned) { m_DeferredReload = true; InvalidateCameraSampling(); return; }
            m_DeferredReload = false;
            ReleaseRuntimeResources();
            m_RuntimeData = null;
            m_ValidatedRevision = -1;
            m_ResidentCells.Clear(); m_ResidentPayloads.Clear(); m_ResidentScenarioNames.Clear();
            m_ResidentActiveScenario = m_ResidentOtherScenario = null;
            m_ValidBakedScenarios.Clear();
            m_LoadError = null;
            m_LoadedCellsDirty = true;
            m_SharedDataValid = false;
            m_RequestedActive = m_RequestedOther = null;
            m_CellProbeStarts = null;
        }

        void ReleaseRuntimeResources()
        {
            InvalidateGlobalBinding();
            foreach (var buffer in m_GPUBufferAllocations) buffer?.Dispose();
            gpuBuffers = Array.Empty<GraphicsBuffer>();
            runtimeSHBuffer = m_AxisBuffer = m_MomentBuffer = m_PositionBuffer = m_ValidityBuffer = null;
            runtimeSkyIrradianceBuffer = m_SkyTransferBuffer = m_SkyTransferRGBBuffer = null;
            m_BakedDirectionalBuffer = m_BakedSkyBuffer = null;
            m_BakedScenarioOffsets.Clear();
            m_BakedScenarioSkyMagnitude.Clear();
            ++m_ScenarioAtlasGeneration;
            m_LegacySkyEnvironment?.Dispose(); m_LegacySkyEnvironment = null;
            m_LegacySceneSkybox = null;
            foreach (var entry in m_CameraSkyEnvironments.Values) entry.environment.Dispose();
            m_CameraSkyEnvironments.Clear();
            m_DestroyedSkyCameras.Clear();
            m_SkyEnvironment = null;
            m_BrickBuffer = m_BrickProbeBuffer = m_BrickHashBuffer = m_GridIndexBuffer = m_DomainBuffer = null;
            m_RuntimeInitialized = false;
            m_SamplingAllowed = false;
            m_HasEffectiveSettings = false;
            m_LastRelightUsedLegacySettings = false;
            m_EffectiveSettings = default;
            m_LastRelightCameraId = 0;
            m_LastRelightScenario = null;
            m_EffectiveSkyRadianceMagnitude = 0;
            m_EffectiveSkyCacheInitialized = m_EffectiveSkyHasValidSource = false;
            m_EffectiveSceneSkybox = null;
            probeCount = 0;
        }

        static GraphicsBuffer Upload<T>(T[] values, int stride) where T : struct
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, values.Length), stride);
            if (values.Length > 0) buffer.SetData(values);
            return buffer;
        }

        // Shared by CPU construction and the HLSL lookup. Signed brick coordinates are bit-preserved.
        static uint BrickHash(int x, int y, int z, int level)
        {
            unchecked
            {
                uint hash = (uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)z * 83492791u ^ (uint)level * 2654435761u;
                hash ^= hash >> 16;
                hash *= 2246822519u;
                return hash ^ (hash >> 13);
            }
        }

        bool LoadRuntimeData(APRTBakedData data, List<APRTBakedCell> residentCells, List<int> sourceCells,
            int[] sourceStarts, Vector4[] bakedDirectional, Vector4[] bakedSky)
        {
            if (data == null || residentCells.Count == 0) return false;
            var positions = new List<Vector3>();
            var axis = new List<Vector4>();
            var moments = new List<Vector4>();
            var validity = new List<float>();
            var skyTransfer = new List<float>();
            var skyTransferRGB = new List<Vector3>();
            var bricks = new List<BrickInfo>();
            var brickProbes = new List<int>();
            var remap = new int[data.totalProbeCount];
            Array.Fill(remap, -1);
            for (int residentIndex = 0; residentIndex < residentCells.Count; ++residentIndex)
            {
                var cell = residentCells[residentIndex];
                int probeBase = positions.Count;
                for (int p = 0; p < cell.probeCount; ++p) remap[sourceStarts[sourceCells[residentIndex]] + p] = probeBase + p;
                positions.AddRange(cell.probePositions);
                axis.AddRange(cell.axisData);
                moments.AddRange(cell.visibilityMoments);
                validity.AddRange(cell.probeValidity);
                if (data.hasSkyBounceTransfer) skyTransferRGB.AddRange(cell.skyIrradianceTransferRGB);
                else if (data.hasSkyIrradianceTransfer) skyTransfer.AddRange(cell.skyIrradianceTransfer);
                for (int i = 0; i < cell.brickPositions.Length; ++i)
                {
                    var p = cell.brickPositions[i];
                    bricks.Add(new BrickInfo { x = p.x, y = p.y, z = p.z, subdivision = cell.brickSubdivisions[i] });
                    for (int j = 0; j < 64; ++j) brickProbes.Add(probeBase + cell.brickProbeIndices[i * 64 + j]);
                }
            }
            int hashSize = Mathf.NextPowerOfTwo(Math.Max(4, bricks.Count * 2));
            var hash = new int[hashSize];
            m_HashMask = hashSize - 1;
            m_HashMaxSteps = 1;
            for (int i = 0; i < bricks.Count; ++i)
            {
                var brick = bricks[i];
                int slot = (int)(BrickHash(brick.x, brick.y, brick.z, brick.subdivision) & (uint)m_HashMask);
                int steps = 1;
                while (hash[slot] != 0) { slot = (slot + 1) & m_HashMask; ++steps; }
                hash[slot] = i + 1;
                m_HashMaxSteps = Math.Max(m_HashMaxSteps, steps);
            }
            var domains = new Matrix4x4[data.samplingDomains.Length];
            for (int i = 0; i < domains.Length; ++i) domains[i] = data.samplingDomains[i].inverse;
            var gridIndices = new int[data.regularGridProbeIndices.Length];
            for (int i = 0; i < gridIndices.Length; ++i) gridIndices[i] = remap[data.regularGridProbeIndices[i]];
            m_LoadedScenarioRevision = data.lightingScenarioRevision;
            // Include every allocation in gpuBuffers immediately, so partial upload failure can be released.
            var owned = new List<GraphicsBuffer>();
            try
            {
                m_AxisBuffer = Upload(axis.ToArray(), 16); owned.Add(m_AxisBuffer);
                m_MomentBuffer = Upload(moments.ToArray(), 16); owned.Add(m_MomentBuffer);
                m_PositionBuffer = Upload(positions.ToArray(), 12); owned.Add(m_PositionBuffer);
                m_ValidityBuffer = Upload(validity.ToArray(), 4); owned.Add(m_ValidityBuffer);
                m_BrickBuffer = Upload(bricks.ToArray(), 16); owned.Add(m_BrickBuffer);
                m_BrickProbeBuffer = Upload(brickProbes.ToArray(), 4); owned.Add(m_BrickProbeBuffer);
                m_BrickHashBuffer = Upload(hash, 4); owned.Add(m_BrickHashBuffer);
                m_GridIndexBuffer = Upload(gridIndices, 4); owned.Add(m_GridIndexBuffer);
                m_DomainBuffer = Upload(domains, 64); owned.Add(m_DomainBuffer);
                runtimeSHBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, positions.Count * 4, 16);
                owned.Add(runtimeSHBuffer);
                // Defined zero output until the first complete relight has been recorded.
                runtimeSHBuffer.SetData(new Vector4[positions.Count * 4]);
                m_SkyTransferBuffer = Upload(skyTransfer.ToArray(), 4); owned.Add(m_SkyTransferBuffer);
                // Bind a one-element unused buffer for the other uniform branch.
                // Legacy bakes keep their scalar upload and arithmetic unchanged.
                m_SkyTransferRGBBuffer = Upload(skyTransferRGB.ToArray(), 12); owned.Add(m_SkyTransferRGBBuffer);
                runtimeSkyIrradianceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, positions.Count * 4, 16);
                owned.Add(runtimeSkyIrradianceBuffer);
                runtimeSkyIrradianceBuffer.SetData(new Vector4[positions.Count * 4]);
                m_BakedDirectionalBuffer = Upload(bakedDirectional, 16); owned.Add(m_BakedDirectionalBuffer);
                m_BakedSkyBuffer = Upload(bakedSky, 16); owned.Add(m_BakedSkyBuffer);
                gpuBuffers = owned.ToArray();
            }
            catch (Exception exception)
            {
                gpuBuffers = owned.ToArray();
                ReleaseRuntimeResources();
                Debug.LogException(exception);
                return false;
            }
            probeCount = positions.Count;
            m_BrickCount = bricks.Count;
            globalBounds = data.globalBounds;
            SetSubdivisionDimensions(data.bakedMinDistanceBetweenProbes * 3, data.bakedSimplificationLevels + 1, data.bakedProbeOffset);
            return true;
        }

        internal bool PrepareRelight(Camera camera) => PrepareRelight(camera, LegacyRelightSettings());

        internal bool PrepareRelight(Camera camera, APRTRelightSettings settings)
        {
            if (focusedRuntime is { } runtime)
            {
                return runtime.PrepareRelight(camera, settings);
            }
            var set = currentBakingSet;
            if (!m_Initialized || !m_Enabled || set == null || !SystemInfo.supportsComputeShaders ||
                SystemInfo.graphicsShaderLevel < 45 ||
                (m_APRTDebugMode == APRTDebugMode.Lighting && !showProbeDebug &&
                    (!ValidIntensity(settings.giIntensity) || settings.giIntensity <= 0)))
            {
                m_SamplingAllowed = false;
                InvalidateGlobalBinding();
                return false;
            }
            return EnsureRuntimeData(set);
        }

        bool EnsureRuntimeData(ProbeVolumeBakingSet set)
        {
            if (!UpdateLoadedCells(set)) { m_SamplingAllowed = false; return false; }
            if (runtimeSHBuffer == null) { m_SamplingAllowed = false; return false; }
            if (relightCompute == null)
            {
                var source = Resources.Load<ComputeShader>("APRTRelight");
                if (source == null) { m_SamplingAllowed = false; InvalidateGlobalBinding(); return false; }
                // Compute parameters belong to this runtime. Reusing the resource
                // asset can retain another dispatch's native state on Unity 2022.
                relightCompute = Object.Instantiate(source);
                relightCompute.hideFlags = HideFlags.HideAndDontSave;
                m_RelightKernel = relightCompute.FindKernel("Relight");
            }
            return true;
        }

        static bool ValidIntensity(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static float Intensity(float value) => ValidIntensity(value) ? Mathf.Max(0, value) : 0;

        internal void DispatchRelight(CommandBuffer cmd, Vector4[] colors, Vector4[] dirs, Vector4[] skySH, Vector4[] skyRadianceCoefficients = null)
            => DispatchRelight(cmd, CaptureScenarioBlend(colors, dirs, skyRadianceCoefficients), LegacyRelightSettings(), 0, true);

        internal void DispatchRelight(CommandBuffer cmd, Vector4[] colors, Vector4[] dirs, Vector4[] skySH,
            Vector4[] skyRadianceCoefficients, APRTRelightSettings settings, int cameraId)
            => DispatchRelight(cmd, CaptureScenarioBlend(colors, dirs, skyRadianceCoefficients), settings, cameraId, false);

        internal void DispatchRelight(CommandBuffer cmd, APRTScenarioBlendSnapshot lighting, APRTRelightSettings settings, int cameraId)
            => DispatchRelight(cmd, lighting, settings, cameraId, false);

        void DispatchRelight(CommandBuffer cmd, APRTScenarioBlendSnapshot lighting,
            APRTRelightSettings settings, int cameraId, bool useLegacySettings)
        {
            if (focusedRuntime is { } runtime)
            {
                runtime.DispatchRelight(cmd, lighting, settings, cameraId, useLegacySettings);
                return;
            }
            // skySH is retained for legacy internal callers, but ordinary ambient
            // SH is no longer an input. Physical sky requires raw L2 radiance.
            if (cmd == null) return;
            if (lighting == null)
            {
                InvalidateCameraSampling();
                InvalidateGlobalBinding();
                return;
            }
            if (!PrepareRelight(null, settings)) return;
            if (lighting.useBakedLighting && lighting.atlasGeneration != m_ScenarioAtlasGeneration)
            {
                InvalidateCameraSampling();
                InvalidateGlobalBinding();
                return;
            }
            // Every dispatch initializes the complete buffer. Multiple cameras can observe changed
            // lights in the same frame; no frame-number cache may hide those changes.
            cmd.SetComputeIntParam(relightCompute, "_APRTProbeCount", probeCount);
            cmd.SetComputeFloatParam(relightCompute, "_APRTBounceIntensity", Intensity(settings.bounceIntensity));
            // The SH output is shared. A different camera must not blend with
            // another Volume stack's lighting history.
            cmd.SetComputeFloatParam(relightCompute, "_APRTTemporalBlend", m_RuntimeInitialized &&
                cameraId == m_LastRelightCameraId && lighting.HasSameEndpoints(m_LastRelightScenario) &&
                ValidIntensity(settings.temporalBlend) ? Mathf.Clamp01(settings.temporalBlend) : 1);
            cmd.SetComputeIntParam(relightCompute, "_APRTLightingModel", (int)lightingModel);
            cmd.SetComputeFloatParam(relightCompute, "_APRTIndirectIntensity", Intensity(settings.indirectIntensity));
            cmd.SetComputeFloatParam(relightCompute, "_APRTIndirectContrast", Intensity(settings.indirectContrast));
            cmd.SetComputeFloatParam(relightCompute, "_APRTSaturationBoost", Intensity(settings.saturationBoost));
            cmd.SetComputeFloatParam(relightCompute, "_APRTShadowGIVisibility", Mathf.Clamp01(Intensity(settings.shadowGIVisibility)));
            cmd.SetComputeFloatParam(relightCompute, "_APRTSkyVisibilityPower", Intensity(settings.skyVisibilityPower));
            cmd.SetComputeIntParam(relightCompute, "_APRTHasSkyIrradianceTransfer", hasSkyIrradianceTransfer ? 1 : 0);
            cmd.SetComputeIntParam(relightCompute, "_APRTHasSkyBounceTransfer", hasSkyBounceTransfer ? 1 : 0);
            cmd.SetComputeFloatParam(relightCompute, "_APRTSkyIrradianceIntensity", Intensity(settings.skyIntensity));
            lighting.Bind(cmd, relightCompute);
            cmd.SetComputeBufferParam(relightCompute, m_RelightKernel, "_APRTBakedAxis", m_AxisBuffer);
            cmd.SetComputeBufferParam(relightCompute, m_RelightKernel, "_APRTRuntimeSH", runtimeSHBuffer);
            cmd.SetComputeBufferParam(relightCompute, m_RelightKernel, "_APRTSkyTransfer", m_SkyTransferBuffer);
            cmd.SetComputeBufferParam(relightCompute, m_RelightKernel, "_APRTSkyTransferRGB", m_SkyTransferRGBBuffer);
            cmd.SetComputeBufferParam(relightCompute, m_RelightKernel, "_APRTSkyIrradianceSH", runtimeSkyIrradianceBuffer);
            cmd.SetComputeBufferParam(relightCompute, m_RelightKernel, "_APRTBakedDirectionalSH", m_BakedDirectionalBuffer);
            cmd.SetComputeBufferParam(relightCompute, m_RelightKernel, "_APRTBakedSkyIrradianceSH", m_BakedSkyBuffer);
            cmd.DispatchCompute(relightCompute, m_RelightKernel, (probeCount + 63) / 64, 1, 1);
            m_RuntimeInitialized = true;
            m_SamplingAllowed = true;
            m_EffectiveSettings = settings;
            m_HasEffectiveSettings = true;
            m_LastRelightUsedLegacySettings = useLegacySettings;
            m_LastRelightCameraId = cameraId;
            m_LastRelightScenario = lighting;
            m_EffectiveSkyRadianceMagnitude = lighting.SkyRadianceMagnitude();
            if (cameraId == 0)
            {
                m_SkyEnvironment = m_LegacySkyEnvironment;
                m_EffectiveSceneSkybox = m_LegacySceneSkybox;
            }
            else if (m_CameraSkyEnvironments.TryGetValue(cameraId, out var entry))
            {
                m_SkyEnvironment = entry.environment;
                m_EffectiveSceneSkybox = entry.sceneSkybox;
            }
            else
            {
                m_SkyEnvironment = null;
                m_EffectiveSceneSkybox = RenderSettings.skybox;
            }
            m_EffectiveSkyCacheInitialized = m_SkyEnvironment != null;
            m_EffectiveSkyHasValidSource = m_SkyEnvironment?.hasValidSource == true;
            BindPRVRuntimeResources(cmd, true);
        }

        void BindRuntimeResources(CommandBuffer cmd, bool isProbeVolumeEnabled)
        {
            cmd.SetGlobalInt("_APRTSamplingSetCount", 0);
            if (focusedRuntime is { } runtime) { runtime.BindRuntimeResources(cmd, isProbeVolumeEnabled); return; }
            var set = currentBakingSet;
            var settings = EffectiveRelightSettings();
            bool ready = isProbeVolumeEnabled && m_Initialized && m_Enabled && m_RuntimeInitialized && m_SamplingAllowed &&
                runtimeSHBuffer != null && set != null && m_RuntimeData != null &&
                m_RuntimeData.formatVersion == APRTBakedData.k_CurrentVersion &&
                ReferenceEquals(set.GetBakedData(k_BackendId), m_RuntimeData) &&
                (Intensity(settings.giIntensity) > 0 || m_APRTDebugMode != APRTDebugMode.Lighting);
            cmd.SetGlobalInt("_APRTDebugMode", (int)m_APRTDebugMode);
            cmd.SetGlobalFloat("_EnableAPRT", ready ? 1 : 0);
            cmd.SetGlobalFloat("_APRTGIIntensity", ready ? Intensity(settings.giIntensity) : 0);
            cmd.SetGlobalFloat("_APRTAddAmbientLighting", !ready || settings.addAmbientLighting ? 1 : 0);
            cmd.SetGlobalInt("_APRTProbeCount", ready ? probeCount : 0);
            cmd.SetGlobalInt("_APRTLightingModel", ready ? (int)lightingModel : 0);
            if (!ready) return;
            cmd.SetGlobalBuffer("_APRTRuntimeSH", runtimeSHBuffer);
            cmd.SetGlobalBuffer("_APRTSkyIrradianceSH", runtimeSkyIrradianceBuffer);
            cmd.SetGlobalBuffer("_APRTBakedAxis", m_AxisBuffer);
            cmd.SetGlobalBuffer("_APRTProbePositions", m_PositionBuffer);
            cmd.SetGlobalBuffer("_APRTProbeValidity", m_ValidityBuffer);
            cmd.SetGlobalBuffer("_APRTVisibilityMoments", m_MomentBuffer);
            cmd.SetGlobalBuffer("_APRTBricks", m_BrickBuffer);
            cmd.SetGlobalBuffer("_APRTBrickProbeIndices", m_BrickProbeBuffer);
            cmd.SetGlobalBuffer("_APRTBrickHash", m_BrickHashBuffer);
            cmd.SetGlobalBuffer("_APRTGridProbeIndices", m_GridIndexBuffer);
            cmd.SetGlobalBuffer("_APRTWorldToDomains", m_DomainBuffer);
            cmd.SetGlobalInt("_APRTDomainCount", m_RuntimeData.samplingDomains.Length);
            cmd.SetGlobalInt("_APRTBrickCount", m_BrickCount);
            cmd.SetGlobalInt("_APRTBrickHashMask", m_HashMask);
            cmd.SetGlobalInt("_APRTBrickHashMaxSteps", m_HashMaxSteps);
            cmd.SetGlobalInt("_APRTMaxSubdivision", m_RuntimeData.bakedSimplificationLevels);
            cmd.SetGlobalVector("_APRTGridDimensions", new Vector4(m_RuntimeData.regularGridDimensions.x, m_RuntimeData.regularGridDimensions.y, m_RuntimeData.regularGridDimensions.z, 0));
            cmd.SetGlobalMatrix("_APRTWorldToGrid", m_RuntimeData.regularGrid
                ? Matrix4x4.TRS(m_RuntimeData.regularGridOrigin, m_RuntimeData.regularGridRotation.normalized, Vector3.one * m_RuntimeData.regularGridSpacing).inverse
                : Matrix4x4.identity);
            Vector3 samplingSpacing = Vector3.one * m_RuntimeData.regularGridSpacing;
            Vector3 samplingOrigin = m_RuntimeData.regularGridOrigin;
            if (m_RuntimeData.regularGridSamplingSize != Vector3.zero)
            {
                samplingOrigin = m_RuntimeData.regularGridSamplingOrigin;
                var last = m_RuntimeData.regularGridDimensions - Vector3Int.one;
                samplingSpacing = new Vector3(
                    m_RuntimeData.regularGridSamplingSize.x / Mathf.Max(1, last.x),
                    m_RuntimeData.regularGridSamplingSize.y / Mathf.Max(1, last.y),
                    m_RuntimeData.regularGridSamplingSize.z / Mathf.Max(1, last.z));
            }
            var samplingGridToWorld = m_RuntimeData.regularGrid
                ? Matrix4x4.TRS(samplingOrigin, m_RuntimeData.regularGridRotation.normalized, samplingSpacing)
                : Matrix4x4.identity;
            cmd.SetGlobalMatrix("_APRTSamplingGridToWorld", samplingGridToWorld);
            cmd.SetGlobalMatrix("_APRTWorldToSamplingGrid", samplingGridToWorld.inverse);
            cmd.SetGlobalVector("_APRTGridSamplingSpacing", samplingSpacing);
            cmd.SetGlobalVector("_APRTProbeOffset", m_RuntimeData.bakedProbeOffset);
            cmd.SetGlobalFloat("_APRTMinBrickSize", m_MinBrickSize);
            cmd.SetGlobalInt("_APRTRegularGrid", m_RuntimeData.regularGrid ? 1 : 0);
            cmd.SetGlobalFloat("_APRTFloorBoost", Intensity(settings.floorBoost));
            cmd.SetGlobalFloat("_APRTFloorPower", Mathf.Max(1, Intensity(settings.floorPower)));
            cmd.SetGlobalFloat("_APRTNormalBias", Intensity(settings.normalBias));
            cmd.SetGlobalFloat("_APRTVisibilityBias", Intensity(settings.visibilityBias));
            cmd.SetGlobalFloat("_APRTValidityThreshold", 1 - Mathf.Clamp01(settings.probeInvalidityThreshold));
            cmd.SetGlobalFloat("_APRTLeakReduction", settings.leakReduction ? 1 : 0);
        }

        int RuntimeMemoryCost()
        {
            long bytes = 0;
            foreach (var buffer in m_GPUBufferAllocations) if (buffer != null) bytes += (long)buffer.count * buffer.stride;
            return (int)Math.Min(int.MaxValue, bytes);
        }
    }
}
