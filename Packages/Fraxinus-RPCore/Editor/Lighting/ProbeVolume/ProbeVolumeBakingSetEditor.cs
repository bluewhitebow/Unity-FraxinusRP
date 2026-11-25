using System;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UnityEditor.Rendering
{
    [CustomPropertyDrawer(typeof(LogarithmicAttribute))]
    class LogarithmicDrawer : PropertyDrawer
    {
        // Draw the property inside the given rect
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // First get the attribute since it contains the range for the slider
            var range = attribute as LogarithmicAttribute;

            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            EditorGUI.BeginProperty(position, label, property);

            EditorGUI.BeginChangeCheck();
#if UNITY_6000_0_OR_NEWER
            int newValue = EditorGUI.LogarithmicIntSlider(position, label, property.intValue, range.min, range.max, 2, 1, 1 << 30);
#else
            // Unity 2022 has no logarithmic integer control. Keep the same
            // serialized range and power-of-two snapping with its supported
            // integer slider.
            int newValue = EditorGUI.IntSlider(position, label, property.intValue, range.min, range.max);
#endif
            if (EditorGUI.EndChangeCheck())
                property.intValue = Mathf.ClosestPowerOfTwo(newValue);

            EditorGUI.EndProperty();
            EditorGUI.showMixedValue = false;
        }
    }

    [CanEditMultipleObjects]
    [CustomEditor(typeof(ProbeVolumeBakingSet))]
    internal class ProbeVolumeBakingSetEditor : Editor
    {
        SerializedProperty m_MinDistanceBetweenProbes;
        SerializedProperty m_SimplificationLevels;
        SerializedProperty m_MinRendererVolumeSize;
        SerializedProperty m_RenderersLayerMask;
        SerializedProperty m_FreezePlacement;
        SerializedProperty m_ProbeOffset;
        SerializedProperty m_LightingScenarios;



        ProbeVolumeEditorBackend m_Backend;
        ProbeVolumeBakingSettingsEditor m_BackendSettings;

        ProbeVolumeBakingSet bakingSet => target as ProbeVolumeBakingSet;

        static class Styles
        {
            public static readonly GUIContent scenariosTitle = new GUIContent("Lighting Scenarios");
            public static readonly GUIContent placementTitle = new GUIContent("Probe Placement");

            public static readonly GUIContent keepSamePlacement = new GUIContent("Probe Positions", "If set to Don't Recalculate, probe positions are not recalculated when baking. Allows baking multiple Scenarios that include small differences in Scene geometry.");
            public static readonly string[] placementOptions = new string[] { "Recalculate", "Don't Recalculate" };

            // Scenario section
            public static readonly GUIContent emptyLabel = new GUIContent("", CoreEditorStyles.GetMessageTypeIcon(MessageType.Info), "This scenario doesn't have any baked data. Set it as active scenario and select Bake Probe Volumes to bake the lighting data.");
            public static readonly GUIContent aprtEmptyLabel = new GUIContent("", CoreEditorStyles.GetMessageTypeIcon(MessageType.Info), "This scenario has no compatible baked APRT lighting data. Set it as Active and select Bake APRT to bake its lighting data.");

            // Probe Placement section
            public static readonly string msgProbeFreeze = "Some scene(s) in this Baking Set are not currently loaded in the Hierarchy. Set Probe Positions to Don't Recalculate to not break compatibility with already baked scenarios.";
            public static readonly GUIContent probeOffset = new GUIContent("Probe Offset", "Offset on world origin used during baking. Can be used to have cells on positions that are not multiples of the probe spacing.");
            public static readonly GUIContent maxDistanceBetweenProbes = new GUIContent("Max Probe Spacing", "Maximum distance between probes, in meters. Determines the number of Bricks in a streamable unit.");
            public static readonly GUIContent minDistanceBetweenProbes = new GUIContent("Min Probe Spacing", "Minimum distance between probes, in meters.");
            public static readonly string simplificationLevelsHighWarning = " Using this many brick sizes will result in high memory usage and can cause instabilities.";
            public static readonly GUIContent minRendererVolumeSize = new GUIContent("Min Renderer Size", "The smallest Renderer size to consider when placing probes.");
            public static readonly GUIContent renderersLayerMask = new GUIContent("Layer Mask", "Specify Layers to use when generating probe positions.");
            public static readonly GUIContent rendererFilterSettings = new GUIContent("Renderer Filter Settings");


            // Probe Invalidity section
        }

        static readonly string s_RenameScenarioUndoName = "Rename Baking Set Scenario";

        void OnEnable()
        {
            m_MinDistanceBetweenProbes = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.minDistanceBetweenProbes));
            m_SimplificationLevels = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.simplificationLevels));
            m_MinRendererVolumeSize = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.minRendererVolumeSize));
            m_RenderersLayerMask = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.renderersLayerMask));
            m_FreezePlacement = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.freezePlacement));
            m_ProbeOffset = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.probeOffset));
            m_LightingScenarios = serializedObject.FindProperty(nameof(ProbeVolumeBakingSet.m_LightingScenarios));

            if (ProbeReferenceVolumeProvider.instance.supportScenarioBlending)
                InitializeScenarioList();

            Undo.undoRedoEvent += OnUndoRedo;
        }

        private void OnDisable()
        {
            Undo.undoRedoEvent -= OnUndoRedo;
        }

        void OnUndoRedo(in UndoRedoInfo info)
        {
            if (bakingSet != null && info.undoName == s_RenameScenarioUndoName)
                bakingSet.EnsureScenarioAssetNameConsistencyForUndo();
            // Blend previews are not serialized. Undoing a rename or bake
            // can invalidate their target even though the active name restores.
            var aprt = APRTProbeReferenceVolume.instance;
            if (bakingSet != null && aprt.currentBakingSet == bakingSet)
            {
                bakingSet.BlendLightingScenario(null, 0, aprt);
                aprt.ReloadBakedData();
                SceneView.RepaintAll();
            }
        }

        public override bool UseDefaultMargins()
        {
            return false;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            using (new EditorGUI.DisabledScope(ProbeVolumeBakingProvider.isRunning))
            {
                ProbePlacementGUI();
                LightingScenariosGUI();
            }
            var backend = ProbeVolumeBakingProvider.backend;
            if (m_Backend != backend)
            {
                m_Backend = backend;
                m_BackendSettings = backend?.CreateBakingSettings(serializedObject);
            }
            m_BackendSettings?.Draw();


            serializedObject.ApplyModifiedProperties();
        }

        void ProbePlacementGUI()
        {
            if (!ProbeVolumeBakingPanel.Foldout(Styles.placementTitle, ProbeVolumeBakingPanel.Expandable.Placement, true))
                return;

            EditorGUI.indentLevel++;
            bool canFreezePlacement = ProbeVolumeBakingProvider.CanFreezePlacement(bakingSet);
            if (ProbeReferenceVolumeProvider.instance.supportLightingScenarios || ProbeVolumeBakingWorkflow.aprtSelected)
            {
                bool incompatibleAPRTPlacement = ProbeVolumeBakingWorkflow.aprtSelected && m_FreezePlacement.boolValue && !canFreezePlacement;
                using (new EditorGUI.DisabledGroupScope(!canFreezePlacement && !incompatibleAPRTPlacement))
                {
                    EditorGUI.BeginChangeCheck();
                    bool freeze = (canFreezePlacement || incompatibleAPRTPlacement) && m_FreezePlacement.boolValue;
                    freeze = EditorGUILayout.Popup(Styles.keepSamePlacement, freeze ? 1 : 0, Styles.placementOptions) == 1;
                    if (EditorGUI.EndChangeCheck())
                        m_FreezePlacement.boolValue = freeze;
                }

                if (incompatibleAPRTPlacement && m_FreezePlacement.boolValue)
                    EditorGUILayout.HelpBox("The current APRT placement cannot reuse the previous bake. Load every Baking Set scene and restore the baked placement settings and volume bounds, or select Recalculate before baking.", MessageType.Warning);

                if (canFreezePlacement && !m_FreezePlacement.boolValue && m_LightingScenarios.arraySize > 1)
                {
                    foreach (var guid in bakingSet.sceneGUIDs)
                    {
                        Scene scene = SceneManager.GetSceneByPath(AssetDatabase.GUIDToAssetPath(guid));
                        if (scene.isLoaded) continue;

                        if (bakingSet.HasBeenBaked())
                        {
                            EditorGUILayout.HelpBox(Styles.msgProbeFreeze, MessageType.Warning);
                            EditorGUILayout.Space();
                        }
                        break;
                    }
                }
            }

            using (new EditorGUI.DisabledScope(ProbeVolumeBakingProvider.isRunning || (canFreezePlacement && m_FreezePlacement.boolValue)))
            {
                // Display vector3 ourselves otherwise display is messed up
                {
                    var rect = EditorGUILayout.GetControlRect();
                    EditorGUI.BeginProperty(rect, Styles.probeOffset, m_ProbeOffset);

                    rect = EditorGUI.PrefixLabel(rect, Styles.probeOffset);
                    rect.xMin -= 10 * EditorGUIUtility.pixelsPerPoint;

                    EditorGUI.BeginChangeCheck();
                    var value = EditorGUI.Vector3Field(rect, GUIContent.none, m_ProbeOffset.vector3Value);
                    if (EditorGUI.EndChangeCheck())
                        m_ProbeOffset.vector3Value = value;

                    EditorGUI.EndProperty();
                }

                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(m_MinDistanceBetweenProbes, Styles.minDistanceBetweenProbes);
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties();
                    ProbeVolumeBakingProvider.backend?.OnPlacementSettingsChanged(bakingSet);
                    serializedObject.Update();
                }

                SimplificationLevelsSlider();

                int levels = ProbeVolumeBakingSet.GetMaxSubdivision(m_SimplificationLevels.intValue);
                MessageType helpBoxType = MessageType.Info;
                string helpBoxText = $"Baked Probe Volume data will contain up-to {levels} different sizes of Brick.";
                if (levels == 6)
                {
                    helpBoxType = MessageType.Warning;
                    helpBoxText += Styles.simplificationLevelsHighWarning;
                }
                EditorGUILayout.HelpBox(helpBoxText, helpBoxType);

                EditorGUILayout.Space();
                EditorGUILayout.Space();

                EditorGUI.indentLevel++;
                if (ProbeVolumeBakingPanel.Foldout(Styles.rendererFilterSettings, ProbeVolumeBakingPanel.Expandable.PlacementFilters, false))
                {
                    EditorGUILayout.PropertyField(m_RenderersLayerMask, Styles.renderersLayerMask);
                    EditorGUILayout.PropertyField(m_MinRendererVolumeSize, Styles.minRendererVolumeSize);
                }
                EditorGUI.indentLevel--;
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space();
        }

        void LightingScenariosGUI()
        {
            if (!ProbeReferenceVolumeProvider.instance.supportLightingScenarios)
                return;

            if (!ProbeVolumeBakingPanel.Foldout(Styles.scenariosTitle, ProbeVolumeBakingPanel.Expandable.Scenarios, true))
                return;

            if (m_Scenarios == null)
                InitializeScenarioList();

            using (new EditorGUI.IndentLevelScope())
                ProbeVolumeBakingPanel.DrawListWithIndent(m_Scenarios);

            if (ProbeVolumeBakingWorkflow.aprtSelected)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.HelpBox("Set a Lighting Scenario as Active, adjust the scene lighting, then select Bake APRT to bake that scenario. Use Don't Recalculate to keep probe positions compatible between scenarios.", MessageType.Info);
                    using (new EditorGUI.DisabledScope(serializedObject.targetObjects.Length != 1))
                        APRTProbeVolumeBakingSettingsEditor.DrawScenarioBlendPreview(bakingSet);
                }
            }

            EditorGUILayout.Space();
        }



        bool RenameEvent(Rect rect, bool active, bool focused, int index, ref int renameIndex)
        {
            if (active)
            {
                if (Event.current.type == EventType.MouseDown && !rect.Contains(Event.current.mousePosition))
                    renameIndex = -1;
                if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2)
                {
                    if (rect.Contains(Event.current.mousePosition))
                        renameIndex = index;
                }
                if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                    renameIndex = -1;
            }
            else if (renameIndex == index && !focused)
                renameIndex = -1;

            return renameIndex == index;
        }



        #region Probe Placement

        static int s_SimplificationSliderID = "SimplificationLevelSlider".GetHashCode();

        void SimplificationLevelsSlider()
        {
            const int highestSimplification = 5;

            var rect = EditorGUILayout.GetControlRect();
            var label = Styles.maxDistanceBetweenProbes;
            EditorGUI.BeginProperty(rect, label, m_SimplificationLevels);

            int id = GUIUtility.GetControlID(s_SimplificationSliderID, FocusType.Keyboard, rect);
            rect = EditorGUI.PrefixLabel(rect, id, label);

            int value = m_SimplificationLevels.intValue;
            EditorGUI.BeginChangeCheck();
            value = Mathf.RoundToInt(GUI.Slider(rect, value, 0, 2, highestSimplification, GUI.skin.horizontalSlider, GUI.skin.horizontalSliderThumb, true, id, "horizontalsliderthumbextent"));
            if (GUIUtility.hotControl == id)
                GUIUtility.keyboardControl = id;
            if (EditorGUI.EndChangeCheck())
                m_SimplificationLevels.intValue = value;

            ProbeVolumeBakingPanel.DrawSimplificationLevelsMarkers(rect, bakingSet.minDistanceBetweenProbes, 2, highestSimplification, value, value);
            EditorGUI.EndProperty();
        }
        #endregion

        #region Lighting Scenarios

        ReorderableList m_Scenarios = null;

        int renamingScenario = -1;

        internal static bool HasScenarioData(ProbeVolumeBakingSet set, string scenario, TProbeReferenceVolumeBase volume) =>
            set != null && volume != null && (set.GetBakedData(volume.backendId)?.HasValidScenarioData(scenario, volume) ?? false);

        void SetActiveScenario(string scenario)
        {
            var volume = ProbeVolumeBakingWorkflow.aprtSelected
                ? APRTProbeReferenceVolume.instance
                : ProbeReferenceVolumeProvider.instance;
            if (ProbeVolumeBakingWorkflow.aprtSelected)
                APRTProbeReferenceVolume.instance.SetActiveBakingSet(bakingSet);
            if (scenario == bakingSet.GetLightingScenario(volume.backendId))
                return;

            serializedObject.ApplyModifiedProperties();
            Undo.RegisterCompleteObjectUndo(bakingSet, "Change active scenario");
            bakingSet.SetActiveScenario(scenario, false, volume);
            EditorUtility.SetDirty(bakingSet);
            serializedObject.Update();
            SceneView.RepaintAll();
        }

        void InitializeScenarioList()
        {
            m_Scenarios = new ReorderableList(serializedObject, m_LightingScenarios, true, true, true, true)
            {
                multiSelect = false,
                elementHeightCallback = _ => EditorGUIUtility.singleLineHeight,

                drawHeaderCallback = (rect) =>
                {
                    ProbeVolumeBakingPanel.SplitRectInThree(rect, out var left, out var middle, out var right, 70);

                    EditorGUI.LabelField(left, "Scenario");
                    EditorGUI.LabelField(middle, "Active");
                    EditorGUI.LabelField(right, "Status");
                },

                onReorderCallback = (ReorderableList list) =>
                {
                    serializedObject.ApplyModifiedProperties();
                }
            };

            m_Scenarios.drawElementCallback = (rect, index, active, focused) =>
            {
                ProbeVolumeBakingPanel.SplitRectInThree(rect, out var left, out var middle, out var right, 70);
                var scenarioName = bakingSet.m_LightingScenarios[index];

                // Status
                {
                    right.xMin += 8f;
                    right.width = right.height;

                    bool baked = HasScenarioData(bakingSet, scenarioName, ProbeReferenceVolumeProvider.instance);

                    using (new EditorGUI.DisabledScope(true))
                        GUI.Label(right, baked ? GUIContent.none : ProbeVolumeBakingWorkflow.aprtSelected ? Styles.aprtEmptyLabel : Styles.emptyLabel);
                }

                // Label for active scene
                middle.xMin += 10;
                middle.yMin += 2;
                middle.width = 19;
                EditorGUI.BeginChangeCheck();
                bool toggled = EditorGUI.Toggle(middle, bakingSet.GetLightingScenario(ProbeReferenceVolumeProvider.instance.backendId) == scenarioName, EditorStyles.radioButton);
                if (EditorGUI.EndChangeCheck() && toggled)
                    SetActiveScenario(scenarioName);

                // Name
                if (RenameEvent(left, active, focused, index, ref renamingScenario))
                {
                    // Renaming
                    EditorGUI.BeginChangeCheck();
                    var name = EditorGUI.DelayedTextField(left, scenarioName, EditorStyles.boldLabel);
                    if (EditorGUI.EndChangeCheck() && !string.IsNullOrWhiteSpace(name))
                    {
                        renamingScenario = -1;
                        try
                        {
                            AssetDatabase.StartAssetEditing();
                            Undo.RegisterCompleteObjectUndo(bakingSet, s_RenameScenarioUndoName);
                            name = bakingSet.RenameScenario(scenarioName, name);
                        }
                        finally
                        {
                            AssetDatabase.StopAssetEditing();
                            m_LightingScenarios.GetArrayElementAtIndex(index).stringValue = name;
                            serializedObject.Update();
                            serializedObject.ApplyModifiedProperties();

                            SetActiveScenario(name);
                        }
                    }
                }
                else
                    EditorGUI.LabelField(left, scenarioName);
            };

            m_Scenarios.onSelectCallback = (ReorderableList list) =>
            {
                SetActiveScenario(bakingSet.m_LightingScenarios[list.index]);
                SceneView.RepaintAll();
                Repaint();
            };

            m_Scenarios.onAddCallback = (list) =>
            {
                serializedObject.ApplyModifiedProperties();
                Undo.RegisterCompleteObjectUndo(bakingSet, "Added new lighting scenario");
                bakingSet.CreateScenario("New Lighting Scenario");
                serializedObject.Update();
            };

            m_Scenarios.onRemoveCallback = (list) =>
            {
                if (m_Scenarios.count == 1)
                {
                    EditorUtility.DisplayDialog("Can't delete scenario", "You can't delete the last scenario. You need to have at least one.", "OK");
                    return;
                }
                var scenario = bakingSet.m_LightingScenarios[list.index];
                if (!EditorUtility.DisplayDialog("Delete the selected scenario?", $"Deleting the scenario will also delete corresponding baked data on disk.\nDo you really want to delete the scenario '{scenario}'?\n\nYou cannot undo the delete assets action.", "Yes", "Cancel"))
                    return;
                serializedObject.ApplyModifiedProperties();
                try
                {
                    AssetDatabase.StartAssetEditing();
                    if (ProbeVolumeBakingWorkflow.aprtSelected)
                        APRTProbeVolumeBakingSettingsEditor.RemoveLightingScenario(bakingSet, scenario);
                    else if (!bakingSet.RemoveScenario(scenario))
                        return;
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                    serializedObject.Update();
                    SetActiveScenario(ProbeVolumeBakingWorkflow.aprtSelected
                        ? bakingSet.GetLightingScenario(APRTProbeReferenceVolumeBackend.backendId)
                        : bakingSet.m_LightingScenarios[0]);
                }
            };
        }
        #endregion
    }
}
