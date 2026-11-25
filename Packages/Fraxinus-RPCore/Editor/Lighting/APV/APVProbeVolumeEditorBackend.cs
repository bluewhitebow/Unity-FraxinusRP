using UnityEditor;

namespace UnityEngine.Rendering
{
    internal sealed partial class APVProbeVolumeEditorBackend : ProbeVolumeEditorBackend
    {
        internal override string id => APVProbeReferenceVolumeBackend.backendId;
        internal override ProbeVolumeBaking CreateBaking(TProbeReferenceVolumeBase volume, ProbeVolumeBakingSet bakingSet) => volume is APVProbeReferenceVolume apv ? new APVProbeVolumeBaking(apv, bakingSet) : null;
        internal override bool CanFreezePlacement(TProbeReferenceVolumeBase volume, ProbeVolumeBakingSet bakingSet) => volume is APVProbeReferenceVolume apv && APVProbeVolumeBaking.CanFreezePlacement(apv, bakingSet);

        [InitializeOnLoadMethod]
        static void Register() => ProbeVolumeBakingProvider.Register(new APVProbeVolumeEditorBackend());
    }
}
