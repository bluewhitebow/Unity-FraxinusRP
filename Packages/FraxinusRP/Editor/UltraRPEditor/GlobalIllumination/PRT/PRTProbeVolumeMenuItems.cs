using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEngine;

namespace FraxinusEditor.Rendering.Ultra
{
    internal static class PRTProbeVolumeMenuItems
    {
        [MenuItem("GameObject/Light/PRT Probe Volume", priority = 50)]
        private static void CreateProbeVolumeGameObject(MenuCommand menuCommand)
        {
            var parent = menuCommand.context as GameObject;
            var probeVolume = new GameObject("PRT Probe Volume", typeof(PRTProbeVolume));
            if (parent != null)
            {
                probeVolume.transform.SetParent(parent.transform, false);
            }
            Undo.RegisterCreatedObjectUndo(probeVolume, "Create PRT Probe Volume");
            Selection.activeGameObject = probeVolume;
        }

        [MenuItem("GameObject/Light/PRT Probe Adjustment Volume", priority = 51)]
        private static void CreateProbeAdjustmentVolumeGameObject(MenuCommand menuCommand)
        {
            var parent = menuCommand.context as GameObject;
            var adjustmentVolume = new GameObject("PRT Probe Adjustment Volume", typeof(PRTProbeAdjustmentVolume));
            if (parent != null)
            {
                adjustmentVolume.transform.SetParent(parent.transform, false);
            }
            Undo.RegisterCreatedObjectUndo(adjustmentVolume, "Create PRT Probe Adjustment Volume");
            Selection.activeGameObject = adjustmentVolume;
        }
    }
}
