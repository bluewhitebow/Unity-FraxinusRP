namespace UnityEngine.Rendering
{
    /// <summary>Options for the experimental standalone APV lighting baker.</summary>
    public struct StandaloneAPVBakeConfiguration
    {
        public int samples;
        public int directSamples;
        public int environmentSamples;
        public int bounces;
        public int minBounces;
        public int probesPerBatch;
        public float environmentIntensity;

        public static StandaloneAPVBakeConfiguration Default => new StandaloneAPVBakeConfiguration
        {
            // Match the Unity 6 LightTransport budget used by the reference
            // APV bake: 256 indirect/environment samples multiplied by the
            // Sponza light-probe multiplier (4), and 32 direct samples times
            // the same multiplier.
            samples = 1024,
            directSamples = 128,
            environmentSamples = 1024,
            bounces = 2,
            minBounces = 2,
            // Keep the UI bake and the parity runner on the same dispatch
            // shape. The sample sequence is global-probe indexed, but using a
            // different batch size changes the GPU dispatch/readback schedule
            // and can produce a different finite-sample bake on this backend.
            probesPerBatch = 256,
            environmentIntensity = 1.0f
        };

        internal bool IsValid(out string error)
        {
            if (samples < 1 || samples > 4096)
            {
                error = "samples must be in the range [1, 4096].";
                return false;
            }
            if (directSamples < 1 || directSamples > 4096)
            {
                error = "directSamples must be in the range [1, 4096].";
                return false;
            }
            if (environmentSamples < 0 || environmentSamples > 4096)
            {
                error = "environmentSamples must be in the range [0, 4096].";
                return false;
            }
            if (bounces < 0 || bounces > 4)
            {
                error = "bounces must be in the range [0, 4].";
                return false;
            }
            if (minBounces < 0 || minBounces > bounces)
            {
                error = "minBounces must be in the range [0, bounces].";
                return false;
            }
            if (probesPerBatch < 1 || probesPerBatch > 65535)
            {
                error = "probesPerBatch must be in the range [1, 65535].";
                return false;
            }
            if (float.IsNaN(environmentIntensity) || float.IsInfinity(environmentIntensity) || environmentIntensity < 0.0f || environmentIntensity > 1000.0f)
            {
                error = "environmentIntensity must be a finite value in the range [0, 1000].";
                return false;
            }
            error = null;
            return true;
        }

        internal StandaloneAPVBakeSettings ToInternalSettings()
        {
            return new StandaloneAPVBakeSettings
            {
                samples = samples,
                directSamples = directSamples,
                environmentSamples = environmentSamples > 0 ? environmentSamples : samples,
                bounces = bounces,
                minBounces = minBounces,
                probesPerBatch = probesPerBatch,
                environmentIntensity = environmentIntensity
            };
        }
    }

    public partial class AdaptiveProbeVolumes
    {
        static VirtualOffsetBaker s_VirtualOffsetBakerOverride;
        static SkyOcclusionBaker s_SkyOcclusionBakerOverride;
        static StandaloneAPVBakeConfiguration s_StandaloneBakeConfiguration = StandaloneAPVBakeConfiguration.Default;
        static bool s_LastBakeSucceeded;
        internal static APVProbeVolumeBaking baking => ProbeVolumeBakingProvider.Get(null, false) as APVProbeVolumeBaking;
        public static bool isRunning => ProbeVolumeBakingProvider.activeOrNull is APVProbeVolumeBaking current && current.isRunning;
        /// <summary>
        /// True only after the most recent standalone bake reached FinalizeBake
        /// and completed publication. Starting a bake, failing preparation,
        /// cancellation, or a worker/publication error resets this value.
        /// </summary>
        public static bool lastBakeSucceeded => s_LastBakeSucceeded;

        internal static void ResetLastBakeResult() => s_LastBakeSucceeded = false;
        internal static void MarkLastBakeSucceeded() => s_LastBakeSucceeded = true;
        /// <summary>
        /// Starts the standalone APV baker. This path owns probe lighting and
        /// never enters Unity's Lightmapping bake pipeline.
        /// </summary>
        public static bool BakeStandaloneAsync()
        {
            ResetLastBakeResult();
            if (!s_StandaloneBakeConfiguration.IsValid(out var error))
            {
                Debug.LogError($"Standalone APV bake configuration is invalid: {error}");
                return false;
            }
            if (!(ProbeVolumeBakingProvider.backend is APVProbeVolumeEditorBackend))
                return false;

            return ProbeVolumeBakingProvider.BakeStandaloneAsync();
        }

        // The Probe Volumes window is the public entry point for a standalone bake.
        // Keep its implicit configuration tied to the active LightingSettings
        // so a previous editor session (or a parity runner) cannot leave a
        // stale sample budget behind. Unity 6 multiplies the lighting sample
        // counts by lightProbeSampleCountMultiplier for APV probes; mirror
        // that rule here before the provider creates the baker.
        internal static void SyncStandaloneBakeConfigurationWithLightingSettings()
        {
            var lightingSettings = ProbeVolumeBakingWorkflow.GetLightingSettings();
            if (lightingSettings == null)
                return;

            int multiplier = Mathf.Max(1, Mathf.RoundToInt(lightingSettings.lightProbeSampleCountMultiplier));
            int directSamples = Mathf.Max(1, lightingSettings.directSampleCount) * multiplier;
            int environmentBase = Mathf.Max(0, lightingSettings.environmentSampleCount);
            int indirectBase = Mathf.Max(1, Mathf.Max(lightingSettings.indirectSampleCount, environmentBase));
            int indirectSamples = indirectBase * multiplier;
            int environmentSamples = environmentBase > 0 ? environmentBase * multiplier : indirectSamples;
            int bounces = Mathf.Clamp(lightingSettings.maxBounces, 0, 4);
            int minBounces = Mathf.Clamp(lightingSettings.minBounces, 0, bounces);

            var configuration = s_StandaloneBakeConfiguration;
            configuration.samples = indirectSamples;
            configuration.directSamples = directSamples;
            configuration.environmentSamples = environmentSamples;
            configuration.bounces = bounces;
            configuration.minBounces = minBounces;
            configuration.probesPerBatch = 256;
            configuration.environmentIntensity = 1.0f;
            s_StandaloneBakeConfiguration = configuration;

            Debug.Log($"Standalone APV settings: samples={indirectSamples}, directSamples={directSamples}, " +
                $"environmentSamples={environmentSamples}, bounces={bounces}, minBounces={minBounces}, " +
                $"probesPerBatch={configuration.probesPerBatch}, probeSampleMultiplier={multiplier}");
        }
        /// <summary>Gets or sets the configuration used by the next standalone bake.</summary>
        public static StandaloneAPVBakeConfiguration standaloneBakeConfiguration
        {
            get => s_StandaloneBakeConfiguration;
            set => s_StandaloneBakeConfiguration = value;
        }

        /// <summary>Starts a standalone bake with the supplied validated configuration.</summary>
        public static bool BakeStandaloneAsync(StandaloneAPVBakeConfiguration configuration)
        {
            ResetLastBakeResult();
            if (!configuration.IsValid(out var error))
            {
                Debug.LogError($"Standalone APV bake configuration is invalid: {error}");
                return false;
            }

            s_StandaloneBakeConfiguration = configuration;
            return BakeStandaloneAsync();
        }
        public static bool Cancel()
        {
            if (!isRunning)
                return false;

            bool canceled = ProbeVolumeBakingProvider.Cancel();
            if (canceled)
                ResetLastBakeResult();
            return canceled;
        }

        public static void SetVirtualOffsetBakerOverride(VirtualOffsetBaker baker)
        {
            s_VirtualOffsetBakerOverride = baker;
            (ProbeVolumeBakingProvider.activeOrNull as APVProbeVolumeBaking)?.SetVirtualOffsetBakerOverride(baker);
        }
        public static void SetSkyOcclusionBakerOverride(SkyOcclusionBaker baker)
        {
            s_SkyOcclusionBakerOverride = baker;
            (ProbeVolumeBakingProvider.activeOrNull as APVProbeVolumeBaking)?.SetSkyOcclusionBakerOverride(baker);
        }
        public static VirtualOffsetBaker GetVirtualOffsetBakerOverride() => s_VirtualOffsetBakerOverride;
        public static SkyOcclusionBaker GetSkyOcclusionBakerOverride() => s_SkyOcclusionBakerOverride;
        internal static StandaloneAPVBakeSettings GetStandaloneBakeSettings() => s_StandaloneBakeConfiguration.ToInternalSettings();
    }

    internal sealed partial class APVProbeVolumeBaking
    {
        internal void SetVirtualOffsetBakerOverride(AdaptiveProbeVolumes.VirtualOffsetBaker baker) => virtualOffsetOverride = baker;
        internal void SetSkyOcclusionBakerOverride(AdaptiveProbeVolumes.SkyOcclusionBaker baker) => skyOcclusionOverride = baker;
    }
}
