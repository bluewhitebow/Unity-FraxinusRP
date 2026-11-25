namespace UnityEngine.Rendering
{
    [ProbeReferenceVolumeBackend]
    internal sealed class APVProbeReferenceVolumeBackend : IProbeReferenceVolumeBackend
    {
        internal const string backendId = "APV";
        readonly APVProbeVolumeBakingDataFormat m_BakingDataFormat = new APVProbeVolumeBakingDataFormat();

        public string id => backendId;
        public int priority => 0;
        public TProbeReferenceVolumeBase volume => APVProbeReferenceVolume.instance;
        public IProbeVolumeBakingDataFormat bakingDataFormat => m_BakingDataFormat;

        public bool CanUse(in ProbeVolumeSystemParameters parameters, out string reason)
        {
            // Preserve the legacy direct-provider behavior when APRT is not part
            // of the request: old callers never populated enableAPV.
            if (parameters.enableAPRT && !parameters.enableAPV)
            {
                reason = "APV is disabled in ProbeVolumeSystemParameters.";
                return false;
            }
            reason = null;
            return true;
        }

        public void Initialize(in ProbeVolumeSystemParameters parameters)
        {
            if (!APVProbeReferenceVolume.instance.isInitialized)
                APVProbeReferenceVolume.instance.Initialize(parameters);
        }

        public void Cleanup()
        {
            if (APVProbeReferenceVolume.instance.isInitialized)
                APVProbeReferenceVolume.instance.Cleanup();
        }
    }
}
