using UnityEngine;
using UnityEditor;
using UnityEditor.Rendering;
using FraxinusEngine.Rendering.Ultra;

namespace FraxinusEditor.Rendering.Ultra
{
    [CustomEditor(typeof(PRTProbeVolume))]
    internal class PRTProbeVolumeEditor : PropertyFetchEditor<PRTProbeVolume>
    {
        private static readonly Color s_probeHandleColor = new(0.2f, 0.8f, 0.1f, 0.125f);

        // Grid Settings
        private SerializedProperty _probeSizeX;
        private SerializedProperty _probeSizeY;
        private SerializedProperty _probeSizeZ;
        private SerializedProperty _probeGridSize;

        // Probe Placement
        private SerializedProperty _enableBakePreprocess;
        private SerializedProperty _virtualOffset;
        private SerializedProperty _geometryBias;
        private SerializedProperty _rayOriginBias;

        // Relight Settings
        private SerializedProperty _multiFrameRelight;
        private SerializedProperty _enableRelightShadow;
        private SerializedProperty _probesPerFrameUpdate;
        private SerializedProperty _localProbeCount;

        // Voxel Settings
        private SerializedProperty _voxelProbeSize;

        // Asset
        private SerializedProperty _asset;

        // Bake Settings
        private SerializedProperty _bakeResolution;
        private SerializedProperty _bakeLayers;

        // Debug Settings
        private SerializedProperty _debugMode;
        private SerializedProperty _probeHandleSize;

        protected override void OnEnable()
        {
            base.OnEnable();

            // Grid Settings
            _probeSizeX = _properties.Find(volume => volume.probeSizeX);
            _probeSizeY = _properties.Find(volume => volume.probeSizeY);
            _probeSizeZ = _properties.Find(volume => volume.probeSizeZ);
            _probeGridSize = _properties.Find(volume => volume.probeGridSize);

            // Probe Placement
            _virtualOffset = _properties.Find(volume => volume.virtualOffset);
            _geometryBias = _properties.Find(volume => volume.geometryBias);
            _rayOriginBias = _properties.Find(volume => volume.rayOriginBias);
            _enableBakePreprocess = _properties.Find(volume => volume.enableBakePreprocess);

            // Relight Settings
            _multiFrameRelight = _properties.Find(volume => volume.multiFrameRelight);
            _enableRelightShadow = _properties.Find(volume => volume.enableRelightShadow);
            _probesPerFrameUpdate = _properties.Find(volume => volume.probesPerFrameUpdate);
            _localProbeCount = _properties.Find(volume => volume.localProbeCount);

            // Voxel Settings
            _voxelProbeSize = _properties.Find(volume => volume.voxelProbeSize);

            // Asset
            _asset = _properties.Find(volume => volume.asset);

            // Bake Settings
            _bakeResolution = _properties.Find(volume => volume.bakeResolution);
            _bakeLayers = _properties.Find(volume => volume.bakeLayers);

            // Debug Settings
            _debugMode = _properties.Find(volume => volume.debugMode);
            _probeHandleSize = _properties.Find(volume => volume.probeHandleSize);
        }

        public override void OnInspectorGUI()
        {
            if (!PRTProbeVolume.isFeatureEnabled)
            {
                EditorGUILayout.HelpBox("Precomputed Radiance Transfer Global Illumination is not activated in Renderer.",
                    MessageType.Info);
                return;
            }

            serializedObject.Update();

            using (new EditorGUI.DisabledScope(PRTVolumeManager.isBaking))
            {
                // Basic ProbeVolume settings
                DrawGridSettings();
                DrawProbePlacementSettings();
                DrawRelightSettings();
                DrawVoxelSettings();

                // Probe Selection & Debug section
                DrawDebugSettingsSection();

                // Bake settings section
                DrawBakeSettingsSection();
            }

            // Action buttons
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                DrawActionButtons();
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawGridSettings()
        {
            if (Foldout("Grid Settings", true))
            {
                EditorGUILayout.PropertyField(_probeSizeX, Styles.ProbeSizeXLabel);
                EditorGUILayout.PropertyField(_probeSizeY, Styles.ProbeSizeYLabel);
                EditorGUILayout.PropertyField(_probeSizeZ, Styles.ProbeSizeZLabel);
                EditorGUILayout.PropertyField(_probeGridSize, Styles.ProbeGridSizeLabel);
            }

            EditorGUILayout.Space();
        }

        private void DrawProbePlacementSettings()
        {
            if (Foldout("Probe Placement", true))
            {
                EditorGUILayout.PropertyField(_enableBakePreprocess, Styles.EnableBakePreprocessLabel);

                if (_enableBakePreprocess.boolValue)
                {
                    EditorGUILayout.PropertyField(_virtualOffset, Styles.VirtualOffsetLabel);
                    EditorGUILayout.PropertyField(_geometryBias, Styles.GeometryBiasLabel);
                    EditorGUILayout.PropertyField(_rayOriginBias, Styles.RayOriginBiasLabel);
                }
            }

            EditorGUILayout.Space();
        }

        private void DrawRelightSettings()
        {
            if (Foldout("Relight Settings", true))
            {
                EditorGUILayout.PropertyField(_multiFrameRelight, Styles.MultiFrameRelightLabel);

                if (_multiFrameRelight.boolValue)
                {
                    EditorGUILayout.PropertyField(_probesPerFrameUpdate, Styles.ProbesPerFrameUpdateLabel);
                    EditorGUILayout.PropertyField(_localProbeCount, Styles.LocalProbeCountLabel);
                }

                EditorGUILayout.PropertyField(_enableRelightShadow, Styles.EnableRelightShadowLabel);
            }

            EditorGUILayout.Space();
        }

        private void DrawVoxelSettings()
        {
            if (Foldout("Voxel Settings", true))
            {
                EditorGUILayout.PropertyField(_voxelProbeSize, Styles.VoxelProbeSizeLabel);
            }

            EditorGUILayout.Space();
        }

        private void DrawDebugSettingsSection()
        {
            if (Foldout("Debug Settings", true))
            {
                // Probe selection
                if (_target.probes != null && _target.probes.Length > 0)
                {
                    // Volume debug mode
                    EditorGUILayout.PropertyField(_debugMode, Styles.VolumeDebugModeLabel);

                    if (_debugMode.enumValueIndex == (int)ProbeVolumeDebugMode.ProbeRadiance)
                    {
                        // Debug mode for selected probe
                        var newDebugMode = (ProbeDebugMode)EditorGUILayout.EnumPopup("Probe Debug Mode",
                            _target.selectedProbeDebugMode);
                        _target.selectedProbeDebugMode = newDebugMode;
                    }

                    if (_debugMode.enumValueIndex == (int)ProbeVolumeDebugMode.ProbeGridWithVirtualOffset)
                    {
                        using (new EditorGUI.DisabledScope(Application.isPlaying))
                        {
                            if (GUILayout.Button("Bake Virtual Offset"))
                            {
                                _target.BakeProbeVirtualOffset();
                            }
                        }
                    }

                    if (_debugMode.enumValueIndex != (int)ProbeVolumeDebugMode.None)
                    {
                        EditorGUILayout.PropertyField(_probeHandleSize, Styles.ProbeHandleSizeLabel);
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("No probes found. probes will be created at runtime.", MessageType.Info);
                }
            }

            EditorGUILayout.Space();
        }

        private void DrawBakeSettingsSection()
        {
            if (Foldout("Bake Settings", true))
            {
                EditorGUILayout.PropertyField(_asset, Styles.ProbeVolumeAssetLabel);

                // Bake resolution
                EditorGUILayout.PropertyField(_bakeResolution, Styles.BakeResolutionLabel);

                // Bake layers
                EditorGUILayout.PropertyField(_bakeLayers, Styles.BakeLayersLabel);
            }

            if (_target.asset && !_target.asset.HasValidData)
            {
                EditorGUILayout.HelpBox(
                    "This prt probe volume does not have valid data for relighting.",
                    MessageType.Warning);
            }
        }

        private static void DrawActionButtons()
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginVertical();

            if (PRTVolumeManager.isBaking)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Cancel Baking"))
                {
                    StopBaking();
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                if (ButtonWithDropdownList(Styles.GenerateLightingLabel,
                        Styles.DetailActionLabels,
                        OnActionDropDown))
                {
                    PRTBakeManager.GenerateLighting();
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        private static void OnActionDropDown(object data)
        {
            int mode = (int)data;
            switch (mode)
            {
                case 0:
                    PRTBakeManager.BakeAllReflectionProbes();
                    break;
                case 1:
                    ClearData();
                    break;
            }
        }

        private static void ClearData()
        {
            if (EditorUtility.DisplayDialog("Clear Baked Data",
                    "Are you sure you want to clear baked data? This action cannot be undone.",
                    "Clear", "Cancel"))
            {
                PRTBakeManager.ClearBakedData();
            }
        }

        private static void StopBaking()
        {
            PRTBakeManager.StopBaking();
            PRTBakeManager.ClearBakedData();
        }

        private void OnSceneGUI()
        {
            if (_target.probes == null || _target.debugMode != ProbeVolumeDebugMode.ProbeRadiance)
            {
                return;
            }

            for (int i = 0; i < _target.probes.Length; i++)
            {
                Vector3 probePos = _target.probes[i].position;

                using (new Handles.DrawingScope(s_probeHandleColor))
                {
                    // Draw selectable handles
                    if (Handles.Button(probePos, Quaternion.identity, _target.probeHandleSize * 0.2f,
                            _target.probeHandleSize * 0.2f, Handles.SphereHandleCap))
                    {
                        _target.selectedProbeIndex = i;
                        Repaint();
                    }
                }
            }
        }

        private static class Styles
        {
            // Grid Settings
            public static readonly GUIContent ProbeSizeXLabel = new("Probe Size x", "Number of probes along x axis");
            public static readonly GUIContent ProbeSizeYLabel = new("Probe Size y", "Number of probes along y axis");
            public static readonly GUIContent ProbeSizeZLabel = new("Probe Size z", "Number of probes along z axis");
            public static readonly GUIContent ProbeGridSizeLabel = new("Probe Grid Size", "Distance between probes");

            // Probe Placement
            public static readonly GUIContent VirtualOffsetLabel = new("Virtual Offset", "Set volume offset when sampling surfels at bake time");
            public static readonly GUIContent GeometryBiasLabel = new("Geometry Bias", "How far to push a probe's capture point out of geometry");
            public static readonly GUIContent RayOriginBiasLabel = new("Ray Origin Bias", "Distance between a probe's center and the point used for sampling ray origin");
            public static readonly GUIContent EnableBakePreprocessLabel = new("Enable Bake Preprocess", "Enable bake preprocess for per-probe place adjustment");

            // Relight Settings
            public static readonly GUIContent MultiFrameRelightLabel = new("Multi Frame Relight", "Enable multi frame relight to improve performance");
            public static readonly GUIContent EnableRelightShadowLabel = new("Enable Relight Shadow", "Enable shadow calculation in PRT relight");
            public static readonly GUIContent ProbesPerFrameUpdateLabel = new("Probes Per Frame Update", "Number of probes to update per frame");
            public static readonly GUIContent LocalProbeCountLabel = new("Local Probe Count", "Number of camera nearby probes to relight");

            // Voxel Settings
            public static readonly GUIContent VoxelProbeSizeLabel = new("Voxel Probe Size", "Voxel texture const probe size");

            // Bake Settings
            public static readonly GUIContent BakeResolutionLabel =
                new("Bake Resolution", "Resolution for cubemap baking");

            public static readonly GUIContent BakeLayersLabel =
                new("Bake Layers", "Specify which layers of objects participate in PRT baking");

            // Debug Settings
            public static readonly GUIContent ProbeHandleSizeLabel = new("Probe Handle Size", "Size of Probe Handle.");

            public static readonly GUIContent VolumeDebugModeLabel =
                new("Volume Debug Mode", "Debug mode of Probe Volume.");

            public static readonly GUIContent ProbeVolumeAssetLabel =
                new("Probe Volume Asset", "Configure baked probe volume asset.");

            // Actions
            public static readonly GUIContent GenerateLightingLabel = EditorGUIUtility.TrTextContent("Generate Lighting", "Generates the probe volume and additional reflection probe data.");

            public static readonly string[] DetailActionLabels =
            {
                "Bake Reflection Probes Normalization Data",
                "Clear Baked Data"
            };
        }

        // Helper method for dropdown button
        private static bool ButtonWithDropdownList(GUIContent content, string[] options, GenericMenu.MenuFunction2 callback)
        {
            Rect rect = GUILayoutUtility.GetRect(content, EditorStyles.miniButton);
            Rect buttonRect = rect;
            Rect dropdownRect = rect;

            buttonRect.xMax = rect.xMax - 20;
            dropdownRect.xMin = rect.xMax - 20;

            bool buttonClicked = GUI.Button(buttonRect, content, EditorStyles.miniButton);

            if (GUI.Button(dropdownRect, GUIContent.none, EditorStyles.miniButton))
            {
                GenericMenu menu = new GenericMenu();
                for (int i = 0; i < options.Length; i++)
                {
                    menu.AddItem(new GUIContent(options[i]), false, callback, i);
                }
                menu.DropDown(dropdownRect);
            }

            return buttonClicked;
        }
    }
}
