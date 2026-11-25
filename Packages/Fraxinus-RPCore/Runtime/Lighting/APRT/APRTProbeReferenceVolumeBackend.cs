namespace UnityEngine.Rendering
{
    /// <summary>Backend registration for Adaptive PRT Volumes.</summary>
    [ProbeReferenceVolumeBackend]
    internal sealed class APRTProbeReferenceVolumeBackend : IProbeReferenceVolumeBackend
    {
        internal const string backendId = "APRT";
        readonly APRTProbeVolumeBakingDataFormat m_BakingDataFormat = new();

        public string id => backendId;
        // Preserve APV's default priority for callers without an explicit id.
        // URP selects APRT by id when the Light Probe System enum requests it.
        public int priority => -100;
        public TProbeReferenceVolumeBase volume => APRTProbeReferenceVolume.instance;
        public IProbeVolumeBakingDataFormat bakingDataFormat => m_BakingDataFormat;

        public bool CanUse(in ProbeVolumeSystemParameters parameters, out string reason)
        {
            if (!parameters.enableAPRT)
            {
                reason = "APRT is disabled in ProbeVolumeSystemParameters.";
                return false;
            }
            reason = null;
            return true;
        }

        public void Initialize(in ProbeVolumeSystemParameters parameters)
        {
            if (!APRTProbeReferenceVolume.instance.isInitialized)
            {
                APRTProbeReferenceVolume.instance.Initialize(parameters);
            }
        }

        public void Cleanup()
        {
            if (APRTProbeReferenceVolume.instance.isInitialized)
                APRTProbeReferenceVolume.instance.Cleanup();
        }
    }
}
