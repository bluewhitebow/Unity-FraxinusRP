using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace UnityEditor.Rendering
{
    /// <summary>
    /// Base implementation for drawing Default Volume Profile UI in Graphics Settings.
    /// </summary>
    public abstract partial class DefaultVolumeProfileSettingsPropertyDrawer : PropertyDrawer
    {
        // UUM-77758: Due to how PropertyDrawers are created and cached, there is no way to retrieve them reliably
        // later. We know that only one DefaultVolumeProfile exists at any given time, so we can access it through
        // static variables.
        static SerializedProperty s_DefaultVolumeProfileSerializedProperty;
        static DefaultVolumeProfileEditor s_DefaultVolumeProfileEditor;

        VisualElement m_Root;

        /// <summary>SerializedObject representing the settings object</summary>
        protected SerializedObject m_SettingsSerializedObject;
        /// <summary>SerializedProperty representing the Default Volume Profile</summary>
        protected SerializedProperty m_VolumeProfileSerializedProperty;
        /// <summary>Foldout state</summary>
        protected EditorPrefBool m_DefaultVolumeProfileFoldoutExpanded;
        /// <summary>VisualElement containing the DefaultVolumeProfileEditor</summary>
        protected VisualElement m_EditorContainer;
        /// <summary>Default Volume Profile label width</summary>
        protected const int k_DefaultVolumeLabelWidth = 260;
        /// <summary>Info box message</summary>
        protected abstract GUIContent volumeInfoBoxLabel { get; }

        // Unity 2022 renders Project Settings inspectors through IMGUI. The
        // Unity 6 drawer originally exposed only CreatePropertyGUI, which
        // leaves a managed-reference element as "No GUI Implemented" when it
        // is reached through an IMGUI PropertyField. Keep a compact IMGUI
        // fallback for the asset reference; Unity 6 continues to use the
        // richer UI Toolkit editor below.
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var volumeProfile = property.FindPropertyRelative("m_VolumeProfile");
            if (volumeProfile != null)
                EditorGUI.PropertyField(position, volumeProfile, EditorGUIUtility.TrTextContent("Default Profile"), true);
            else
                EditorGUI.LabelField(position, label, EditorGUIUtility.TrTextContent("Default profile is unavailable."));
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var volumeProfile = property.FindPropertyRelative("m_VolumeProfile");
            return volumeProfile != null
                ? EditorGUI.GetPropertyHeight(volumeProfile, EditorGUIUtility.TrTextContent("Default Profile"), true)
                : EditorGUIUtility.singleLineHeight;
        }

        /// <summary>
        /// CreatePropertyGUI implementation.
        /// </summary>
        /// <param name="property">Property to create UI for</param>
        /// <returns>VisualElement containing the created UI</returns>
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            m_Root = new VisualElement();

            var header = CreateHeader();
            if (header != null)
                m_Root.Add(header);

            m_SettingsSerializedObject = property.serializedObject;
            m_VolumeProfileSerializedProperty = property.FindPropertyRelative("m_VolumeProfile");
            m_DefaultVolumeProfileFoldoutExpanded = new EditorPrefBool($"{GetType()}.DefaultVolumeProfileFoldoutExpanded", true);

            m_EditorContainer = new VisualElement();
            if (!RenderPipelineManager.pipelineSwitchCompleted)
                // Defer creation of the UI until the render pipeline is created and VolumeManager is initialized
                RenderPipelineManager.activeRenderPipelineCreated += CreateDefaultVolumeProfileEditor;
            else
                CreateDefaultVolumeProfileEditor();

            m_Root.Add(CreateAssetFieldUI());
            m_Root.Add(m_EditorContainer);

            return m_Root;
        }

        /// <summary>
        /// Creates the header for the Volume Profile editor.
        /// </summary>
        /// <returns>VisualElement containing the header. Null for no header.</returns>
        protected virtual VisualElement CreateHeader() => null;

        /// <summary>
        /// Creates the Default Volume Profile editor.
        /// </summary>
        protected void CreateDefaultVolumeProfileEditor()
        {
            RenderPipelineManager.activeRenderPipelineCreated -= CreateDefaultVolumeProfileEditor;

            VolumeProfile profile = m_VolumeProfileSerializedProperty.objectReferenceValue as VolumeProfile;
            if (profile == null)
                return;

            if (profile == VolumeManager.instance.globalDefaultProfile)
                VolumeProfileUtils.EnsureAllOverridesForDefaultProfile(profile);

            if (s_DefaultVolumeProfileSerializedProperty != m_VolumeProfileSerializedProperty)
            {
                s_DefaultVolumeProfileSerializedProperty = m_VolumeProfileSerializedProperty;
                s_DefaultVolumeProfileEditor = new DefaultVolumeProfileEditor(profile, m_SettingsSerializedObject);
            }
            m_EditorContainer.Add(s_DefaultVolumeProfileEditor.Create());
            m_EditorContainer.Q<HelpBox>("volume-override-info-box").text = volumeInfoBoxLabel.text;

            if (m_DefaultVolumeProfileFoldoutExpanded.value)
                m_EditorContainer.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// Destroys the Default Volume Profile editor.
        /// </summary>
        protected void DestroyDefaultVolumeProfileEditor()
        {
            m_EditorContainer.style.display = DisplayStyle.None;
            m_EditorContainer?.Clear();

            if (s_DefaultVolumeProfileEditor != null)
                s_DefaultVolumeProfileEditor.Destroy();
            s_DefaultVolumeProfileEditor = null;
            s_DefaultVolumeProfileSerializedProperty = null;
        }

        /// <summary>
        /// Implementation of the Default Volume Profile asset field.
        /// </summary>
        /// <returns>VisualElement containing the UI</returns>
        protected abstract VisualElement CreateAssetFieldUI();

        // Unity 6 introduced the graphics-settings context-menu contracts and
        // the corresponding interface-wide settings lookup. RPCore supplies
        // the same contracts on Unity 2022, while the lookup below maps to the
        // registered global-settings container.
        /// <summary>
        /// Context menu implementation for Default Volume Profile.
        /// </summary>
        /// <typeparam name="TSetting">Default Volume Profile Settings type</typeparam>
        /// <typeparam name="TRenderPipeline">Render Pipeline type</typeparam>
        public abstract class DefaultVolumeProfileSettingsContextMenu2<TSetting, TRenderPipeline> : IRenderPipelineGraphicsSettingsContextMenu2<TSetting>
            where TSetting : class, IDefaultVolumeProfileSettings
            where TRenderPipeline : RenderPipeline
        {
            /// <summary>
            /// Path where new Default Volume Profile will be created.
            /// </summary>
            protected abstract string defaultVolumeProfilePath { get; }

            void IRenderPipelineGraphicsSettingsContextMenu2<TSetting>.PopulateContextMenu(TSetting setting, SerializedProperty _, ref GenericMenu menu)
            {
                bool canCreateNewAsset = RenderPipelineManager.currentPipeline is TRenderPipeline;
                VolumeProfileUtils.AddVolumeProfileContextMenuItems(ref menu,
                    setting.volumeProfile,
                    s_DefaultVolumeProfileEditor.allEditors,
                    overrideStateOnReset: true,
                    defaultVolumeProfilePath: defaultVolumeProfilePath,
                    onNewVolumeProfileCreated: createdProfile =>
                    {
                        s_DefaultVolumeProfileSerializedProperty.objectReferenceValue = createdProfile;
                        s_DefaultVolumeProfileSerializedProperty.serializedObject.ApplyModifiedProperties();

                        VolumeProfile initialAsset = null;

#if UNITY_6000_0_OR_NEWER
                        var initialAssetSettings = EditorGraphicsSettings.GetRenderPipelineSettingsFromInterface<IDefaultVolumeProfileAsset>();
                        if (initialAssetSettings.Length > 0)
                        {
                            if (initialAssetSettings.Length > 1)
                                throw new InvalidOperationException("Found multiple settings implementing IDefaultVolumeProfileAsset, expected only one");
                            initialAsset = initialAssetSettings[0].defaultVolumeProfile;
                        }
#else
                        // Unity 2022 has no interface-wide graphics settings
                        // query. The compatibility facade searches the active
                        // pipeline's registered global-settings container.
                        initialAsset = RenderPipelineGraphicsSettingsCompatibility
                            .GetRenderPipelineSettings<IDefaultVolumeProfileAsset>()
                            ?.defaultVolumeProfile;
#endif
                        VolumeProfileUtils.UpdateGlobalDefaultVolumeProfile<TRenderPipeline>(createdProfile, initialAsset);
                    },
                    onComponentEditorsExpandedCollapsed: s_DefaultVolumeProfileEditor.RebuildListViews,
                    canCreateNewAsset);
            }
        }
    }
}
