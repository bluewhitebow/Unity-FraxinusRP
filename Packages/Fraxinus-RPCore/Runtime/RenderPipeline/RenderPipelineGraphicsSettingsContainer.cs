using System;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityEngine.Rendering
{
    /// <summary>
    /// Contains a double list of <see cref="IRenderPipelineGraphicsSettings"/> one is used for editor
    /// and the other for standalone release, the standalone release will be stripped by <see cref="IRenderPipelineGraphicsSettingsStripper{T}"/>
    /// </summary>
    [Serializable]
    public class RenderPipelineGraphicsSettingsContainer : ISerializationCallbackReceiver
    {
#if UNITY_EDITOR
        [SerializeField] private RenderPipelineGraphicsSettingsCollection m_SettingsList = new();
#endif

        [SerializeField, HideInInspector] private RenderPipelineGraphicsSettingsCollection m_RuntimeSettings = new();

        /// <summary>
        /// Returns one list for editor and another for runtime
        /// </summary>
        public List<IRenderPipelineGraphicsSettings> settingsList
        {
#if UNITY_EDITOR
            get => m_SettingsList.settingsList;
#else
            get => m_RuntimeSettings.settingsList;
#endif
        }

        /// <summary>
        /// On Before Serialize callback where the stripping is performed
        /// </summary>
        public void OnBeforeSerialize()
        {
#if UNITY_EDITOR
            // Older global-settings assets can deserialize either collection
            // as null.  Initialize both before touching their managed
            // reference lists; otherwise the build-time serialization pass
            // aborts before the runtime snapshot is written.
            if (m_SettingsList == null)
                m_SettingsList = new RenderPipelineGraphicsSettingsCollection();
            if (m_RuntimeSettings == null)
                m_RuntimeSettings = new RenderPipelineGraphicsSettingsCollection();
            m_RuntimeSettings.settingsList.Clear();
            if (BuildPipeline.isBuildingPlayer) // Same behaviour as transfer.IsSerializingForGameRelease
                RenderPipelineGraphicsSettingsStripper.PerformStripping(m_SettingsList.settingsList, m_RuntimeSettings.settingsList);
            else
            {
                // Global-settings assets are also serialized while saving and
                // while Unity creates the first Player-data snapshot.  Keep a
                // conservative player-compatible snapshot outside an active
                // build; otherwise the hidden runtime list remains empty and
                // Player builds lose settings required at runtime.
                for (int i = 0; i < m_SettingsList.settingsList.Count; ++i)
                {
                    var setting = m_SettingsList.settingsList[i];
                    if (setting != null && setting.isAvailableInPlayerBuild)
                        m_RuntimeSettings.settingsList.Add(setting);
                }
            }
#endif
        }

        /// <summary>
        /// On After Deserialize callback, nothing is implemented
        /// </summary>
        public void OnAfterDeserialize()
        {
#if UNITY_EDITOR
            // Do not erase the hidden player snapshot during a second import
            // or deserialize. Unity 2022 may deserialize the global asset
            // between the build-preparation and player-data serialization
            // callbacks; clearing here would leave the player with no
            // graphics settings. The editor-facing settingsList still points
            // at m_SettingsList, so retaining this hidden copy is safe.
            if (m_SettingsList == null)
                m_SettingsList = new RenderPipelineGraphicsSettingsCollection();
            if (m_RuntimeSettings == null)
                m_RuntimeSettings = new RenderPipelineGraphicsSettingsCollection();
#endif
        }
    }
}
