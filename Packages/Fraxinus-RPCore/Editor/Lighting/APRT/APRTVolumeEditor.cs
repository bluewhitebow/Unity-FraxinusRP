using UnityEditor.IMGUI.Controls;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering
{
    [CustomEditor(typeof(APRTVolume)), CanEditMultipleObjects]
    internal sealed class APRTVolumeEditor : Editor
    {
        static readonly Color s_Color = new Color(0.3f, 0.85f, 1.0f, 0.8f);
        readonly BoxBoundsHandle m_Box = new BoxBoundsHandle();
        bool m_EditBounds = true;
        bool m_NoFitContributors;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var mode = serializedObject.FindProperty(nameof(APRTVolume.mode));
            EditorGUILayout.PropertyField(mode);
            var layout = serializedObject.FindProperty(nameof(APRTVolume.layout));
            EditorGUILayout.PropertyField(layout);
            bool referenceGrid = !layout.hasMultipleDifferentValues && layout.enumValueIndex == (int)APRTVolume.Layout.ReferenceGrid;
            if (referenceGrid)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(APRTVolume.regularGridDimensions)), new GUIContent("Grid Dimensions"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(APRTVolume.regularGridSpacing)), new GUIContent("Grid Spacing"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(APRTVolume.regularGridOriginOffset)), new GUIContent("First Probe Offset"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(APRTVolume.regularGridSamplingSize)), new GUIContent("Sampling Size", "Zero uses the captured lattice extent. Override when the sampling box differs from the captured probe extent."));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(APRTVolume.regularGridSamplingOriginOffset)), new GUIContent("Sampling Origin Offset", "Offset from the first captured probe in the volume's rotated axes. Used when Sampling Size is nonzero."));
                EditorGUILayout.HelpBox("Reference Grid uses exact rotated probe positions. The first probe is Transform Position plus the rotated offset. Keep one Reference Grid volume per Baking Set.", MessageType.Info);
            }
            else
            {
                var overrideSubdivision = serializedObject.FindProperty(nameof(APRTVolume.overridesSubdivLevels));
                EditorGUILayout.PropertyField(overrideSubdivision);
                if (overrideSubdivision.hasMultipleDifferentValues || overrideSubdivision.boolValue)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(APRTVolume.lowestSubdivLevelOverride)), new GUIContent("Finest Subdivision"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(APRTVolume.highestSubdivLevelOverride)), new GUIContent("Coarsest Subdivision"));
                }
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(APRTVolume.fillEmptySpaces)));
            }
            var overrideFilters = serializedObject.FindProperty(nameof(APRTVolume.overrideRendererFilters));
            EditorGUILayout.PropertyField(overrideFilters);
            if (overrideFilters.hasMultipleDifferentValues || overrideFilters.boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(APRTVolume.objectLayerMask)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(APRTVolume.minRendererVolumeSize)));
            }
            bool local = !mode.hasMultipleDifferentValues && mode.enumValueIndex == (int)APRTVolume.Mode.Local;
            if (mode.hasMultipleDifferentValues || local)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(APRTVolume.size)),
                    new GUIContent("Size", "World-space dimensions of the rotated box. Transform scale is ignored."));
                m_EditBounds = EditorGUILayout.Toggle(new GUIContent("Edit Bounds", "Show box handles in the Scene view."), m_EditBounds);
            }
            serializedObject.ApplyModifiedProperties();

            if (local)
                DrawFitButtons();

            EditorGUILayout.Space();
            var volume = (APRTVolume)target;
            var bakingSet = ProbeVolumeBakingWorkflow.GetSceneBakingSetForUI(volume.gameObject.scene);
            if (bakingSet != null)
            {
                EditorGUILayout.ObjectField("Baking Set", bakingSet, typeof(ProbeVolumeBakingSet), false);
            }
            var data = bakingSet?.GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
            if (data?.HasValidSharedData() == true)
                EditorGUILayout.LabelField("Baked APRT Data", $"{(data.lightingModel == APRTLightingModel.APRTV ? "APRTV" : "Legacy")} / format {data.formatVersion} / {data.sampleCount} probes / {data.cells.Count} cells");
            else if (data != null && data.sampleCount > 0)
                EditorGUILayout.HelpBox("Legacy APRT data must be baked again before it can be sampled.", MessageType.Warning);
            bool available = ProbeVolumeBakingProvider.IsBackendAvailable(APRTProbeReferenceVolumeBackend.backendId);
            if (!available) EditorGUILayout.HelpBox(APRTProbeVolumeEditorBackend.unavailableMessage, MessageType.Info);
            using (new EditorGUI.DisabledScope(!available || ProbeVolumeBakingProvider.isRunning || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Bake APRT")) ProbeVolumeBakingWorkflow.BakeAPRT(bakingSet, volume.gameObject.scene);
            if (ProbeVolumeBakingProvider.isRunning && ProbeVolumeBakingProvider.activeOrNull?.backendId == APRTProbeReferenceVolumeBackend.backendId && GUILayout.Button("Cancel APRT Bake"))
                ProbeVolumeBakingProvider.Cancel();
        }

        void DrawFitButtons()
        {
            GIContributors.ContributorFilter? filter = null;
            if (GUILayout.Button(new GUIContent("Fit to All Scenes", "Fit to GI contributors in all loaded Scenes."), EditorStyles.miniButton))
                filter = GIContributors.ContributorFilter.All;
            if (GUILayout.Button(new GUIContent("Fit to Scene", "Fit to GI contributors in the same Scene as this volume."), EditorStyles.miniButton))
                filter = GIContributors.ContributorFilter.Scene;
            if (GUILayout.Button(new GUIContent("Fit to Selection", "Fit to selected GI contributors and their children. Lock the Inspector to make additional selections."), EditorStyles.miniButton))
                filter = GIContributors.ContributorFilter.Selection;

            if (filter.HasValue)
            {
                m_NoFitContributors = false;
                foreach (var selected in targets)
                    m_NoFitContributors |= !FitToContributors((APRTVolume)selected, filter.Value);
                serializedObject.Update();
                SceneView.RepaintAll();
            }

            if (m_NoFitContributors)
                EditorGUILayout.HelpBox("No GI contributors were found for one or more volumes. Enable Contribute GI on active Mesh Renderers or Terrains in the chosen scope. Those volumes were left unchanged.", MessageType.Info);
        }

        internal static bool FitToContributors(APRTVolume volume, GIContributors.ContributorFilter filter)
        {
            if (volume.mode != APRTVolume.Mode.Local)
                return false;

            var contributors = GIContributors.Find(filter, volume.gameObject.scene);
            if (contributors.Count == 0)
                return false;

            // Fit in the volume's rotated axes, ignoring Transform scale just like its handles.
            // A world AABB used directly as size can miss geometry when the volume is rotated.
            var toWorld = volume.boundsMatrix;
            var toLocal = toWorld.inverse;
            var bounds = new Bounds();
            bool hasBounds = false;
            void Encapsulate(Bounds worldBounds)
            {
                var min = worldBounds.min;
                var max = worldBounds.max;
                for (int corner = 0; corner < 8; ++corner)
                {
                    var point = toLocal.MultiplyPoint3x4(new Vector3(
                        (corner & 1) == 0 ? min.x : max.x,
                        (corner & 2) == 0 ? min.y : max.y,
                        (corner & 4) == 0 ? min.z : max.z));
                    if (hasBounds)
                        bounds.Encapsulate(point);
                    else
                    {
                        bounds = new Bounds(point, Vector3.zero);
                        hasBounds = true;
                    }
                }
            }

            foreach (var renderer in contributors.renderers)
                Encapsulate(renderer.component.bounds);
            foreach (var terrain in contributors.terrains)
                Encapsulate(terrain.boundsWithTrees);

            // Use the scene's spacing even before the first bake and without initializing a backend.
            var bakingSet = ProbeVolumeBakingWorkflow.GetSceneBakingSetForUI(volume.gameObject.scene);
            float minBrickSize = ProbeVolumeBakingSet.GetMinBrickSize(bakingSet != null ? bakingSet.minDistanceBetweenProbes : 1.0f);
            Undo.RecordObjects(new Object[] { volume, volume.transform }, "Fit APRT Volume");
            volume.transform.position = toWorld.MultiplyPoint3x4(bounds.center);
            volume.size = Vector3.Max(bounds.size + Vector3.one * minBrickSize, Vector3.one * 0.001f);
            PrefabUtility.RecordPrefabInstancePropertyModifications(volume.transform);
            PrefabUtility.RecordPrefabInstancePropertyModifications(volume);
            EditorUtility.SetDirty(volume);
            return true;
        }

        void OnSceneGUI()
        {
            var volume = (APRTVolume)target;
            if (!m_EditBounds || volume.mode != APRTVolume.Mode.Local)
                return;
            using (new Handles.DrawingScope(s_Color, volume.boundsMatrix))
            {
                m_Box.center = Vector3.zero;
                m_Box.size = volume.size;
                EditorGUI.BeginChangeCheck();
                m_Box.DrawHandle();
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObjects(new Object[] { volume, volume.transform }, "Resize APRT Volume");
                    volume.transform.position = volume.boundsMatrix.MultiplyPoint3x4(m_Box.center);
                    volume.size = Vector3.Max(Vector3.one * 0.001f, m_Box.size);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(volume.transform);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(volume);
                    EditorUtility.SetDirty(volume);
                }
            }
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable)]
        static void DrawVolumeGizmo(APRTVolume volume, GizmoType type)
        {
            if (!volume.isActiveAndEnabled)
                return;
            var color = s_Color;
            color.a = (type & GizmoType.Selected) != 0 ? 0.8f : 0.25f;
            using (new Handles.DrawingScope(color, volume.boundsMatrix))
            {
                if (volume.layout == APRTVolume.Layout.ReferenceGrid)
                {
                    var extent = (Vector3)(volume.regularGridDimensions - Vector3Int.one) * volume.regularGridSpacing;
                    var offset = volume.regularGridOriginOffset;
                    Handles.DrawWireCube(offset + extent * 0.5f, extent);
                    Handles.Label(offset, "APRT First Probe");
                }
                else if (volume.mode == APRTVolume.Mode.Local)
                    Handles.DrawWireCube(Vector3.zero, volume.size);
                else
                    Handles.Label(Vector3.zero, volume.mode == APRTVolume.Mode.Global ? "APRT Baking Set" : "APRT Scene");
            }
        }

        [MenuItem("GameObject/Light/Adaptive PRT Volume", false, 12)]
        internal static void CreateVolume(MenuCommand command)
        {
            var gameObject = new GameObject("Adaptive PRT Volume");
            GameObjectUtility.SetParentAndAlign(gameObject, command?.context as GameObject);
            if (gameObject.transform.parent == null && SceneView.lastActiveSceneView != null)
                gameObject.transform.position = SceneView.lastActiveSceneView.pivot;
            gameObject.AddComponent<APRTVolume>();
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Adaptive PRT Volume");
            StageUtility.PlaceGameObjectInCurrentStage(gameObject);
            Selection.activeGameObject = gameObject;
        }
    }
}
