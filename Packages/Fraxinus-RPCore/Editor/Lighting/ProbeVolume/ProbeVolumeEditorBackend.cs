using UnityEditor;

namespace UnityEngine.Rendering
{
    /// <summary>Editor behavior supplied by the active probe lighting algorithm.</summary>
    internal abstract class ProbeVolumeEditorBackend
    {
        internal abstract string id { get; }
        internal virtual string displayName => "Probe Volumes";
        internal virtual bool supportsBaking => true;
        internal abstract ProbeVolumeBaking CreateBaking(TProbeReferenceVolumeBase volume, ProbeVolumeBakingSet bakingSet);
        internal virtual bool PrepareBaking() => ProbeVolumeBakingWorkflow.PrepareBake();
        internal virtual bool CanFreezePlacement(TProbeReferenceVolumeBase volume, ProbeVolumeBakingSet bakingSet) => false;
        internal virtual void OnPlacementSettingsChanged(ProbeVolumeBakingSet bakingSet) { }
        internal virtual ProbeVolumeBakingSettingsEditor CreateBakingSettings(SerializedObject serializedObject) => null;
        internal virtual void DrawDisabledHelpBox() => EditorGUILayout.HelpBox($"The current render pipeline does not support {displayName}.", MessageType.Warning);
        internal virtual bool DrawVolumeWarnings() => true;
        internal virtual void OpenDebugPanel() { }
    }

    /// <summary>Algorithm settings scoped to one Baking Set inspector.</summary>
    internal abstract class ProbeVolumeBakingSettingsEditor
    {
        internal abstract void Draw();
    }
}
