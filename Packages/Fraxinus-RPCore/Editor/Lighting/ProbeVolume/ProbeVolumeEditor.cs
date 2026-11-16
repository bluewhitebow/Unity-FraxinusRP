using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditorInternal;

using Object = UnityEngine.Object;

[assembly: InternalsVisibleTo("SRPSmoke.Editor.Tests")]

namespace UnityEditor.Rendering
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(ProbeVolume))]
    internal class ProbeVolumeEditor : Editor
    {
        SerializedProbeVolume m_SerializedProbeVolume;
        internal const EditMode.SceneViewEditMode k_EditShape = EditMode.SceneViewEditMode.ReflectionProbeBox;

        static HierarchicalBox _ShapeBox;
        static HierarchicalBox s_ShapeBox
        {
            get
            {
                if (_ShapeBox == null)
                    _ShapeBox = new HierarchicalBox(ProbeVolumeUI.Styles.k_GizmoColorBase, ProbeVolumeUI.Styles.k_BaseHandlesColor);
                return _ShapeBox;
            }
        }

        protected void OnEnable()
        {
            m_SerializedProbeVolume = new SerializedProbeVolume(serializedObject);
        }

        public override void OnInspectorGUI()
        {
            ProbeVolume probeVolume = target as ProbeVolume;

            bool hasChanges = false;
            if (probeVolume.cachedTransform != probeVolume.gameObject.transform.worldToLocalMatrix)
            {
                hasChanges = true;
            }

            if (probeVolume.cachedHashCode != probeVolume.GetHashCode())
            {
                hasChanges = true;
            }

            probeVolume.mightNeedRebaking = hasChanges;

            // This component belongs to APV even when APRT is selected. Never
            // borrow APRT's warning text for an APV-only capability check.
            var apvBackend = new APVProbeVolumeEditorBackend();
            bool drawInspector = apvBackend.DrawVolumeWarnings();

            if (ProbeVolumeBakingProvider.backend?.id == APRTProbeReferenceVolumeBackend.backendId)
                EditorGUILayout.HelpBox("This Adaptive Probe Volume controls APV. Use an Adaptive PRT Volume to configure APRT geometry and bounds.", MessageType.Info);

            if (!ProbeVolumeGlobalSettingsStripper.ProbeVolumeSupportedForBuild())
            {
                apvBackend.DrawDisabledHelpBox();
                drawInspector = false;
            }

            if (drawInspector)
            {
                serializedObject.Update();
                ProbeVolumeUI.Inspector.Draw(m_SerializedProbeVolume, this);
                m_SerializedProbeVolume.Apply();
            }
        }

        [DrawGizmo(GizmoType.InSelectionHierarchy)]
        static void DrawGizmosSelected(ProbeVolume probeVolume, GizmoType gizmoType)
        {
            using (new Handles.DrawingScope(Matrix4x4.TRS(probeVolume.transform.position, probeVolume.transform.rotation, Vector3.one)))
            {
                // Bounding box.
                s_ShapeBox.center = Vector3.zero;
                s_ShapeBox.size = probeVolume.size;
                s_ShapeBox.DrawHull(EditMode.editMode == k_EditShape);
            }
        }

        protected void OnSceneGUI()
        {
            ProbeVolume probeVolume = target as ProbeVolume;
            if (probeVolume.mode != ProbeVolume.Mode.Local)
                return;

            //important: if the origin of the handle's space move along the handle,
            //handles displacement will appears as moving two time faster.
            using (new Handles.DrawingScope(Matrix4x4.TRS(Vector3.zero, probeVolume.transform.rotation, Vector3.one)))
            {
                //contained must be initialized in all case
                s_ShapeBox.center = Quaternion.Inverse(probeVolume.transform.rotation) * probeVolume.transform.position;
                s_ShapeBox.size = probeVolume.size;

                s_ShapeBox.monoHandle = false;
                EditorGUI.BeginChangeCheck();
                s_ShapeBox.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObjects(new Object[] { probeVolume, probeVolume.transform }, "Change Probe Volume Bounding Box");

                    probeVolume.size = s_ShapeBox.size;
                    Vector3 delta = probeVolume.transform.rotation * s_ShapeBox.center - probeVolume.transform.position;
                    probeVolume.transform.position += delta; ;
                }
            }
        }

        [MenuItem("CONTEXT/ProbeVolume/Rendering Debugger...")]
        internal static void AddProbeVolumeContextMenu()
        {
            ProbeVolumeBakingPanel.OpenProbeDebugPanel(null, null, 0);
        }
    }
}
