using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace UnityEngine.Rendering
{
    internal sealed partial class APVProbeVolumeEditorBackend
    {
        internal override string displayName => "Adaptive Probe Volumes";
        internal override ProbeVolumeBakingSettingsEditor CreateBakingSettings(SerializedObject serializedObject) => new APVProbeVolumeBakingSettingsEditor(serializedObject);
        internal override void OnPlacementSettingsChanged(ProbeVolumeBakingSet bakingSet) => bakingSet.settings.dilationSettings.dilationDistance = Mathf.Max(bakingSet.minDistanceBetweenProbes, bakingSet.settings.dilationSettings.dilationDistance);
        internal override void DrawDisabledHelpBox() => APVProbeVolumeEditorUtility.APVDisabledHelpBox();

        internal override bool DrawVolumeWarnings()
        {
            if (ProbeVolumeBakingWorkflow.GetLightingSettings().realtimeGI)
            {
                EditorGUILayout.HelpBox("Adaptive Probe Volumes are not supported when using Realtime Global Illumination (Enlighten).", MessageType.Warning, wide: true);
                return false;
            }
            APVProbeVolumeEditorUtility.FrameSettingDisabledHelpBox();
            return true;
        }

        internal override void OpenDebugPanel()
        {
            var debugPanel = EditorWindow.GetWindow<DebugWindow>();
            debugPanel.titleContent = DebugWindow.Styles.windowTitle;
            debugPanel.Show();
            var index = DebugManager.instance.FindPanelIndex(APVProbeReferenceVolume.instance.debugPanelName);
            if (index != -1)
                DebugManager.instance.RequestEditorWindowPanelIndex(index);
        }
    }
}
