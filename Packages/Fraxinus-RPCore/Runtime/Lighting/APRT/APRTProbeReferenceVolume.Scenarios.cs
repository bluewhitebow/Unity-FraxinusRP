namespace UnityEngine.Rendering
{
    // Own all input arrays so a recorded graph cannot observe a later change to
    // a saved scenario, live light array, or the global scenario selection.
    internal sealed class APRTScenarioBlendSnapshot
    {
        static readonly Vector4[] s_ZeroLights = new Vector4[4], s_ZeroSky = new Vector4[9];
        readonly Vector4[] m_Colors, m_Directions, m_SkyRadiance;
        readonly Vector4[] m_OtherColors, m_OtherDirections, m_OtherSkyRadiance;
        internal string activeScenario { get; }
        internal string otherScenario { get; }
        internal float factor { get; }
        internal bool useLightingScenarios { get; }
        internal bool useBakedLighting { get; }
        internal int atlasGeneration { get; }
        readonly int m_ActiveOffset, m_OtherOffset;
        readonly float m_BakedSkyMagnitude;

        internal APRTScenarioBlendSnapshot(Vector4[] colors, Vector4[] directions, Vector4[] skyRadiance,
            APRTLightingScenario active = null, APRTLightingScenario other = null, float factor = 0,
            bool useBakedLighting = false, int activeOffset = 0, int otherOffset = 0, int atlasGeneration = 0,
            float bakedSkyMagnitude = 0)
        {
            useLightingScenarios = active != null;
            activeScenario = active?.name;
            otherScenario = other?.name;
            this.factor = other != null ? Mathf.Clamp01(factor) : 0;
            this.useBakedLighting = useBakedLighting;
            this.atlasGeneration = atlasGeneration;
            m_ActiveOffset = activeOffset;
            m_OtherOffset = other != null ? otherOffset : activeOffset;
            m_BakedSkyMagnitude = bakedSkyMagnitude;
            m_Colors = Copy(active?.colors ?? colors, 4);
            m_Directions = Copy(active?.directions ?? directions, 4);
            m_SkyRadiance = Copy(active?.skyRadiance ?? skyRadiance, 9);
            m_OtherColors = Copy(other?.colors, 4);
            m_OtherDirections = Copy(other?.directions, 4);
            m_OtherSkyRadiance = Copy(other?.skyRadiance, 9);
        }

        static Vector4[] Copy(Vector4[] source, int count) =>
            source != null && source.Length == count ? (Vector4[])source.Clone() : count == 4 ? s_ZeroLights : s_ZeroSky;

        internal bool Matches(Vector4[] colors, Vector4[] directions, Vector4[] skyRadiance,
            APRTLightingScenario active, APRTLightingScenario other, float blend,
            bool baked, int activeOffset, int otherOffset, int generation, float bakedSkyMagnitude)
        {
            return useLightingScenarios == (active != null) && activeScenario == active?.name &&
                otherScenario == other?.name && factor.Equals(other != null ? Mathf.Clamp01(blend) : 0) &&
                useBakedLighting == baked && atlasGeneration == generation && m_ActiveOffset == activeOffset &&
                m_OtherOffset == (other != null ? otherOffset : activeOffset) && m_BakedSkyMagnitude.Equals(bakedSkyMagnitude) &&
                SameInput(m_Colors, active?.colors ?? colors) && SameInput(m_Directions, active?.directions ?? directions) &&
                SameInput(m_SkyRadiance, active?.skyRadiance ?? skyRadiance) && SameInput(m_OtherColors, other?.colors) &&
                SameInput(m_OtherDirections, other?.directions) && SameInput(m_OtherSkyRadiance, other?.skyRadiance);
        }

        static bool SameInput(Vector4[] captured, Vector4[] input)
        {
            if (input == null || input.Length != captured.Length)
                input = captured.Length == 4 ? s_ZeroLights : s_ZeroSky;
            return SameValues(captured, input);
        }

        internal bool HasSameEndpoints(APRTScenarioBlendSnapshot previous) => previous != null &&
            useLightingScenarios == previous.useLightingScenarios && activeScenario == previous.activeScenario &&
            otherScenario == previous.otherScenario && useBakedLighting == previous.useBakedLighting &&
            atlasGeneration == previous.atlasGeneration && (!useLightingScenarios || useBakedLighting ||
                (SameValues(m_Colors, previous.m_Colors) && SameValues(m_Directions, previous.m_Directions) &&
                 SameValues(m_SkyRadiance, previous.m_SkyRadiance) && SameValues(m_OtherColors, previous.m_OtherColors) &&
                 SameValues(m_OtherDirections, previous.m_OtherDirections) && SameValues(m_OtherSkyRadiance, previous.m_OtherSkyRadiance)));

        static bool SameValues(Vector4[] first, Vector4[] second)
        {
            for (int i = 0; i < first.Length; ++i)
                if (!first[i].Equals(second[i])) return false;
            return true;
        }

        internal void Bind(CommandBuffer cmd, ComputeShader shader)
        {
            cmd.SetComputeVectorArrayParam(shader, "_APRTLightColors", m_Colors);
            cmd.SetComputeVectorArrayParam(shader, "_APRTLightDirections", m_Directions);
            cmd.SetComputeVectorArrayParam(shader, "_APRTSkyRadianceSH", m_SkyRadiance);
            cmd.SetComputeVectorArrayParam(shader, "_APRTScenarioLightColors", m_OtherColors);
            cmd.SetComputeVectorArrayParam(shader, "_APRTScenarioLightDirections", m_OtherDirections);
            cmd.SetComputeVectorArrayParam(shader, "_APRTScenarioSkyRadianceSH", m_OtherSkyRadiance);
            cmd.SetComputeFloatParam(shader, "_APRTScenarioBlend", factor);
            cmd.SetComputeIntParam(shader, "_APRTUseBakedScenarios", useBakedLighting ? 1 : 0);
            cmd.SetComputeIntParam(shader, "_APRTActiveScenarioOffset", m_ActiveOffset);
            cmd.SetComputeIntParam(shader, "_APRTOtherScenarioOffset", m_OtherOffset);
        }

        internal float SkyRadianceMagnitude()
        {
            if (useBakedLighting) return m_BakedSkyMagnitude;
            float magnitude = 0;
            for (int i = 0; i < m_SkyRadiance.Length; ++i)
            {
                var value = Vector4.Lerp(m_SkyRadiance[i], m_OtherSkyRadiance[i], factor);
                for (int channel = 0; channel < 3; ++channel)
                    if (!float.IsNaN(value[channel]) && !float.IsInfinity(value[channel]))
                        magnitude = Mathf.Max(magnitude, Mathf.Abs(value[channel]));
            }
            return magnitude;
        }
    }

    internal sealed partial class APRTProbeReferenceVolume
    {
        APRTScenarioBlendSnapshot m_LastRelightScenario;
        // Keep recorded snapshots immutable. A bounded cache also serves cameras
        // with different light inputs without allocating again as they alternate.
        readonly APRTScenarioBlendSnapshot[] m_ScenarioSnapshots = new APRTScenarioBlendSnapshot[8];
        int m_NextScenarioSnapshot;

        APRTScenarioBlendSnapshot Snapshot(Vector4[] colors, Vector4[] directions, Vector4[] skyRadiance,
            APRTLightingScenario active = null, APRTLightingScenario other = null, float factor = 0,
            bool baked = false, int activeOffset = 0, int otherOffset = 0, int generation = 0, float bakedSkyMagnitude = 0)
        {
            foreach (var snapshot in m_ScenarioSnapshots)
                if (snapshot != null && snapshot.Matches(colors, directions, skyRadiance,
                    active, other, factor, baked, activeOffset, otherOffset, generation, bakedSkyMagnitude)) return snapshot;
            var captured = new APRTScenarioBlendSnapshot(colors, directions, skyRadiance,
                active, other, factor, baked, activeOffset, otherOffset, generation, bakedSkyMagnitude);
            m_ScenarioSnapshots[m_NextScenarioSnapshot] = captured;
            m_NextScenarioSnapshot = (m_NextScenarioSnapshot + 1) % m_ScenarioSnapshots.Length;
            return captured;
        }

        bool HasLightingScenario(string name)
        {
            var set = currentBakingSet;
            if (set == null || string.IsNullOrEmpty(name) || !set.m_LightingScenarios.Contains(name)) return false;
            var data = set.GetBakedData(k_BackendId) as APRTBakedData;
            var scenario = data?.FindLightingScenario(name);
            if (scenario?.IsValid() != true) return false;
            if (!HasBakedLightingMarkers(scenario)) return true;
            if (ReferenceEquals(data, m_RuntimeData) && m_ValidatedRevision == data.lightingScenarioRevision)
                return scenario.bakedLayoutHash == m_LoadedLayoutHash && m_ValidBakedScenarios.Contains(name);
            return data.HasBakedLightingScenario(name);
        }

        static bool HasBakedLightingMarkers(APRTLightingScenario scenario) => scenario != null &&
            ((scenario.bakedDirectionalSH?.Length ?? 0) != 0 || (scenario.bakedSkyIrradianceSH?.Length ?? 0) != 0 ||
             scenario.bakedLayoutHash != 0 || (scenario.streamableAssets?.Count ?? 0) != 0);

        void SetLightingScenario(string name)
        {
            var set = currentBakingSet;
            if (!supportLightingScenarios || !HasLightingScenario(name)) return;
            set.SetActiveScenario(name, false, this);
            set.aprtUseLightingScenarios = true;
        }

        internal APRTScenarioBlendSnapshot CaptureScenarioBlend(Vector4[] colors, Vector4[] directions, Vector4[] skyRadiance)
        {
            if (focusedRuntime is { } runtime) return runtime.CaptureScenarioBlend(colors, directions, skyRadiance);
            var set = currentBakingSet;
            if (set == null || !usesLightingScenarios)
                return Snapshot(colors, directions, skyRadiance);
            var data = set.GetBakedData(k_BackendId) as APRTBakedData;
            if (data == null || !ValidIntensity(scenarioBlendingFactor)) return null;
            if (ReferenceEquals(data, m_RuntimeData) && m_LoadedScenarioRevision != data.lightingScenarioRevision)
                ReloadBakedData();
            if (!EnsureRuntimeData(set)) return null;
            var active = m_ResourcesPinned && m_ResidentActiveScenario != null ? m_ResidentActiveScenario : data.FindLightingScenario(lightingScenario);
            var other = supportScenarioBlending ?
                (m_ResourcesPinned && m_ResidentActiveScenario != null ? m_ResidentOtherScenario : data.FindLightingScenario(otherScenario)) : null;
            if (other?.name == active?.name) other = null;
            bool wantsOther = supportScenarioBlending && !string.IsNullOrEmpty(otherScenario) &&
                otherScenario != lightingScenario && !(m_ResourcesPinned && m_ResidentActiveScenario != null);
            if (active?.IsValid() != true || (wantsOther && other == null) || other?.IsValid() == false || !set.m_LightingScenarios.Contains(active.name) ||
                (other != null && !set.m_LightingScenarios.Contains(other.name))) return null;
            bool baked = HasBakedLightingMarkers(active);
            if (other != null && baked != HasBakedLightingMarkers(other)) return null;
            int activeOffset = 0, otherOffset = 0;
            if (baked && (!TryGetBakedScenarioOffset(active, out activeOffset) ||
                (other != null && !TryGetBakedScenarioOffset(other, out otherOffset)))) return null;
            float factor = active.name == lightingScenario && other?.name == otherScenario ? scenarioBlendingFactor : m_ResidentBlendFactor;
            if (active.name == lightingScenario && other?.name == otherScenario) m_ResidentBlendFactor = factor;
            return Snapshot(colors, directions, skyRadiance,
                active, other, factor, baked, activeOffset, otherOffset, m_ScenarioAtlasGeneration,
                baked ? Mathf.Lerp(m_BakedScenarioSkyMagnitude[active.name],
                    m_BakedScenarioSkyMagnitude[other?.name ?? active.name], other != null ? factor : 0) : 0);
        }

        bool TryGetBakedScenarioOffset(APRTLightingScenario scenario, out int offset)
        {
            offset = 0;
            // Complete SH validation happens during atlas upload. Per frame,
            // check selection and layout metadata without scanning every probe.
            return scenario.bakedLayoutHash == m_LoadedLayoutHash &&
                m_BakedScenarioOffsets.TryGetValue(scenario.name, out offset);
        }
    }
}
