using System;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Rendering;
#endif

#if UNITY_6000_0_OR_NEWER
using RenderingCategories = UnityEngine.Categorization;
#else
using RenderingCategories = UnityEngine.Rendering.Categorization;
#endif

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    [RenderingCategories.CategoryInfo(Name = "Volume", Order = 0)]
    public class UltraRenderPipelineDefaultVolumeProfileSettings : IDefaultVolumeProfileSettings
    {
        #region Version
        internal enum Version : int
        {
            Initial = 0,
        }

        [SerializeField]
        [HideInInspector]
        Version _version;

        /// <summary>Current version.</summary>
        public int version => (int)_version;
        #endregion

        [SerializeField]
        VolumeProfile m_VolumeProfile;

        /// <summary>
        /// The default volume profile asset.
        /// </summary>
        public VolumeProfile volumeProfile
        {
            get => m_VolumeProfile;
            set => this.SetValueAndNotify(ref m_VolumeProfile, value);
        }
    }

#if UNITY_EDITOR && UNITY_6000_0_OR_NEWER
    //Overriding "Reset" in menu that is not called at UltraRPDefaultVolumeProfileSettings creation such Reset()
    struct ResetImplementation : IRenderPipelineGraphicsSettingsContextMenu2<UltraRenderPipelineDefaultVolumeProfileSettings>
    {
        public void PopulateContextMenu(UltraRenderPipelineDefaultVolumeProfileSettings setting, SerializedProperty _, ref GenericMenu menu)
        {
            void Reset()
            {
                static VolumeProfile CopyVolumeProfileFromResourcesToAssets(VolumeProfile profileInResourcesFolder)
                {
                    if (profileInResourcesFolder == null)
                        return null;

                    const string k_defaultVolumeProfileName = "UltraRPDefaultVolumeProfile";
                    const string k_defaultVolumeProfilePath = "Assets/" + k_defaultVolumeProfileName + ".asset";

                    var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(k_defaultVolumeProfilePath);
                    if (profile == null)
                    {
                        CoreUtils.EnsureFolderTreeInAssetFilePath(k_defaultVolumeProfilePath);
                        AssetDatabase.CopyAsset(UnityEditor.AssetDatabase.GetAssetPath(profileInResourcesFolder), k_defaultVolumeProfilePath);
                        profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(k_defaultVolumeProfilePath);
                    }
                    else
                    {
                        profile.components.Clear();
                        foreach (var resourceComponent in profileInResourcesFolder.components)
                        {
                            var c = profile.Add(resourceComponent.GetType());
                            for (int i = 0; i < c.parameters.Count; i++)
                            {
                                c.parameters[i].SetValue(resourceComponent.parameters[i]);
                            }
                        }

                    }

                    return profile;
                }

#if UNITY_6000_0_OR_NEWER
                if (EditorGraphicsSettings.TryGetRenderPipelineSettingsForPipeline<UltraRenderPipelineEditorAssets, UltraRenderPipeline>(out var rpgs))
                {
                    RenderPipelineGraphicsSettingsEditorUtility.Rebind(
                        new UltraRenderPipelineDefaultVolumeProfileSettings() { volumeProfile = CopyVolumeProfileFromResourcesToAssets(rpgs.defaultVolumeProfile) },
                        typeof(UltraRenderPipeline)
                    );
                }
#else
                var globalSettings = UltraRenderPipelineGlobalSettings.Ensure();
                var rpgs = globalSettings == null ? null : globalSettings.GetSettings<UltraRenderPipelineEditorAssets>();
                if (rpgs != null)
                {
                    setting.volumeProfile = CopyVolumeProfileFromResourcesToAssets(rpgs.defaultVolumeProfile);
                    EditorUtility.SetDirty(globalSettings);
                    AssetDatabase.SaveAssetIfDirty(globalSettings);
                }
#endif
            }

            menu.AddItem(new GUIContent("Reset"), false, Reset);
        }
    }
#endif
}

