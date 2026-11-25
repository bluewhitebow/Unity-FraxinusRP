using System;
using System.ComponentModel;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Base;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FraxinusEngine.Rendering.Ultra
{
    [DisplayInfo(name = "UltraRP Global Settings Asset", order = CoreUtils.Sections.section4 + 2)]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    [DisplayName("UltraRP")]
    public partial class UltraRenderPipelineGlobalSettings : RenderPipelineGlobalSettings<UltraRenderPipelineGlobalSettings, UltraRenderPipeline>
#if !UNITY_6000_0_OR_NEWER
        , ISerializationCallbackReceiver, IRenderingLayerNamesProvider
#endif
    {
        internal bool IsAtLastVersion() => true;

        [SerializeField] RenderPipelineGraphicsSettingsContainer m_Settings = new();
        protected override List<IRenderPipelineGraphicsSettings> settingsList => m_Settings?.settingsList;

#if !UNITY_6000_0_OR_NEWER
        string[] IRenderingLayerNamesProvider.renderingLayerMaskNames => renderingLayerNames;

        internal bool EnsureGraphicsSettings()
        {
            m_Settings ??= new RenderPipelineGraphicsSettingsContainer();
            var changed = RenderPipelineGraphicsSettingsCompatibility.PopulateRenderPipelineGraphicsSettings(
                this, typeof(UltraRenderPipelineAsset));
#if UNITY_EDITOR
            var defaults = GetSettings<UltraRenderPipelineDefaultVolumeProfileSettings>();
            if (defaults != null && defaults.volumeProfile == null)
            {
                defaults.volumeProfile = GetOrCreateDefaultVolumeProfile(null);
                changed = true;
            }
            if (changed)
                EditorUtility.SetDirty(this);
#endif
            return changed;
        }
#endif

#if !UNITY_6000_0_OR_NEWER
        public void OnBeforeSerialize()
        {
            m_Settings?.OnBeforeSerialize();
        }

        public void OnAfterDeserialize()
        {
#if UNITY_EDITOR
            EditorApplication.delayCall += () =>
            {
                if (this != null)
                    EnsureGraphicsSettings();
            };
#endif
        }
#endif

#if UNITY_EDITOR
        internal void PrepareGraphicsSettingsForPlayerBuild()
        {
#if !UNITY_6000_0_OR_NEWER
            EnsureGraphicsSettings();
            m_Settings.OnBeforeSerialize();
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssetIfDirty(this);
#endif
        }
#endif

        private static T GetOrCreateGraphicsSettings<T>(UltraRenderPipelineGlobalSettings data) where T : class, IRenderPipelineGraphicsSettings, new()
        {
            data.m_Settings ??= new RenderPipelineGraphicsSettingsContainer();
            T settings;

            if (data.TryGet(typeof(T), out var baseSettings))
                settings = baseSettings as T;
            else
            {
                settings = new T();
                data.Add(settings);
            }

            return settings;
        }


        public static readonly string defaultAssetName = "UltraRenderPipelineGlobalSettings";

#if UNITY_EDITOR
        internal static string defaultPath => $"Assets/{defaultAssetName}.asset";
        public static UltraRenderPipelineGlobalSettings Ensure(bool canCreateNewAsset = true)
        {
            UltraRenderPipelineGlobalSettings currentInstance = GraphicsSettings.
                GetSettingsForRenderPipeline<UltraRenderPipeline>() as UltraRenderPipelineGlobalSettings;

            if (RenderPipelineGlobalSettingsUtils.TryEnsure<UltraRenderPipelineGlobalSettings, UltraRenderPipeline>(ref currentInstance, defaultPath, canCreateNewAsset))
            {
#if !UNITY_6000_0_OR_NEWER
                bool settingsChanged = currentInstance.EnsureGraphicsSettings();
                if (GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset)
                    RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(currentInstance, typeof(UltraRenderPipeline));
                if (settingsChanged)
                    AssetDatabase.SaveAssetIfDirty(currentInstance);
#endif
                if (currentInstance != null && string.IsNullOrEmpty(currentInstance.name))
                {
                    currentInstance.name = defaultAssetName;
                    EditorUtility.SetDirty(currentInstance);
                    AssetDatabase.SaveAssetIfDirty(currentInstance);
                }
                
                return currentInstance;
            }

            return null;
        }

        public override void Initialize(RenderPipelineGlobalSettings source = null)
        {
            if (string.IsNullOrEmpty(name))
                name = defaultAssetName;

            if (source is UltraRenderPipelineGlobalSettings globalSettingsSource)
                renderingLayerNames = (string[])globalSettingsSource.renderingLayerNames.Clone();

            // Note: RenderPipelineGraphicsSettings are not populated yet when the global settings asset is being
            // initialized, so create the setting before using it
            var defaultVolumeProfileSettings = GetOrCreateGraphicsSettings<UltraRenderPipelineDefaultVolumeProfileSettings>(this);
            defaultVolumeProfileSettings.volumeProfile = GetOrCreateDefaultVolumeProfile(defaultVolumeProfileSettings.volumeProfile);
        }
#endif // #if UNITY_EDITOR


        public override void Reset()
        {
            base.Reset();
#if UNITY_EDITOR && !UNITY_6000_0_OR_NEWER
            EnsureGraphicsSettings();
            Initialize();
#endif
            DecalProjector.UpdateAllDecalProperties();
        }

        internal static VolumeProfile GetOrCreateDefaultVolumeProfile(VolumeProfile defaultVolumeProfile)
        {
#if UNITY_EDITOR
            if (defaultVolumeProfile == null || defaultVolumeProfile.Equals(null))
            {
                const string k_defaultVolumeProfileName = "UltraRPDefaultVolumeProfile";
                const string k_defaultVolumeProfilePath = "Assets/" + k_defaultVolumeProfileName + ".asset";

                defaultVolumeProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(k_defaultVolumeProfilePath);
                if (defaultVolumeProfile == null)
                {
                    defaultVolumeProfile = CreateInstance<VolumeProfile>();
                    defaultVolumeProfile.name = k_defaultVolumeProfileName;
                    AssetDatabase.CreateAsset(defaultVolumeProfile, k_defaultVolumeProfilePath);
                }

                AssetDatabase.SaveAssetIfDirty(defaultVolumeProfile);

                if (VolumeManager.instance.isInitialized && RenderPipelineManager.currentPipeline is UltraRenderPipeline)
                    VolumeManager.instance.SetGlobalDefaultProfile(defaultVolumeProfile);
            }
#endif
            return defaultVolumeProfile;
        }

        [SerializeField]
        internal string[] renderingLayerNames = new string[] { "Default" };

        [SerializeField] uint _validRenderingLayers;

        internal void ResetRenderingLayerNames()
        {
            renderingLayerNames = new string[] { "Default" };
        }
    }
}
