using UnityEditor;

namespace UnityEngine.Rendering
{
    /// <summary>Standalone APRT capture and placement integration.</summary>
    internal sealed class APRTProbeVolumeEditorBackend : ProbeVolumeEditorBackend
    {
        internal const string unavailableMessage = "Select Adaptive PRT Volumes in the render pipeline asset to use APRT.";
        internal const string implementationUnavailableMessage = "APRT captures geometry transport and bakes the active Lighting Scenario from directional lights and the sky. Load all scenes in this Baking Set before baking. Use Don't Recalculate to preserve probe positions when baking additional scenarios.";

        internal override string id => APRTProbeReferenceVolumeBackend.backendId;
        internal override string displayName => "Adaptive Precompute Radiance Transfer";
        internal override bool supportsBaking => true;
        internal override ProbeVolumeBaking CreateBaking(TProbeReferenceVolumeBase volume, ProbeVolumeBakingSet bakingSet) =>
            bakingSet != null && volume is APRTProbeReferenceVolume aprt ? new APRTProbeVolumeBaking(aprt, bakingSet) : null;
        internal override bool PrepareBaking() => ProbeVolumeBakingWorkflow.PrepareBake();
        internal override bool CanFreezePlacement(TProbeReferenceVolumeBase volume, ProbeVolumeBakingSet bakingSet) =>
            volume is APRTProbeReferenceVolume aprt && APRTProbeVolumeBaking.CanFreezePlacement(aprt, bakingSet);
        internal override ProbeVolumeBakingSettingsEditor CreateBakingSettings(SerializedObject serializedObject) =>
            new APRTProbeVolumeBakingSettingsEditor(serializedObject);
        internal override void DrawDisabledHelpBox() => EditorGUILayout.HelpBox(unavailableMessage, MessageType.Info);

        [InitializeOnLoadMethod]
        static void Register() => ProbeVolumeBakingProvider.Register(new APRTProbeVolumeEditorBackend());
    }
}
