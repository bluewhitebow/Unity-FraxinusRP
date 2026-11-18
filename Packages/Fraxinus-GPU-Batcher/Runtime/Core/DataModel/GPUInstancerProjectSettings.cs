using System.Collections.Generic;
using UnityEngine;
using System;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Project-wide GPU Instancer settings.  This asset owns global shader/SRP
    /// bindings and the embedded runtime channel configuration.  Manager
    /// instances are intentionally not stored here; they are created from the
    /// profiles in <see cref="runtimeConfig"/> by the runtime Driver.
    /// </summary>
    public class GPUInstancerProjectSettings : ScriptableObject
    {
        // AssetDatabase.CreateAsset can synchronously invoke scene/editor callbacks.
        // Keep the object visible while its project-owned asset graph is being
        // created so a re-entrant settings request does not create the same file.
        private static GPUInstancerProjectSettings _settingsBeingInitialized;

        // In a Player build there may be no generated project copy (for
        // example when a host constructs its settings solely from package
        // defaults).  Cache the in-memory fallback so repeated calls do not
        // instantiate an unbounded stream of ScriptableObjects.  Unity's
        // overloaded null check below naturally invalidates this token after
        // the native object has been destroyed or a domain is reset.
        private static GPUInstancerProjectSettings _runtimeSettingsCache;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeCache()
        {
            // Enter Play Mode can skip the managed domain reload. Do not let
            // the previous play session's hidden fallback become the next
            // session's canonical settings instance. The object itself is
            // reclaimed by Unity's play-mode teardown; clearing the managed
            // token here is safe even when the cached value is a Resources
            // asset rather than an in-memory clone.
            _settingsBeingInitialized = null;
            _runtimeSettingsCache = null;
        }

        public GPUInstancerShaderBindings shaderBindings;
        public GPUInstancerBillboardAtlasBindings billboardAtlasBindings;
        public ShaderVariantCollection shaderVariantCollection;
        public int instancingBoundsSize = 10000;

        /// <summary>
        /// Embedded channel configuration.  Keeping this as a plain serializable
        /// object (rather than another asset) preserves the single project
        /// settings file and lets Unity serialize polymorphic profiles through
        /// SerializeReference.
        /// </summary>
        [SerializeField]
        public GPUInstancerRuntimeConfig runtimeConfig = new GPUInstancerRuntimeConfig();

        #region Core Settings
        public List<GPUIRenderingSettings> renderingSettingPresets;

        public bool hasCustomRenderingSettings;
        public GPUIRenderingSettings customRenderingSettings;
        #endregion Core Settings

        /// <summary>Returns the project-owned settings asset (or a runtime copy).</summary>
        public static GPUInstancerProjectSettings GetDefaultGPUInstancerProjectSettings()
        {
            if (_settingsBeingInitialized != null)
                return _settingsBeingInitialized;

            if (!Application.isPlaying)
                _runtimeSettingsCache = null;
            if (Application.isPlaying && _runtimeSettingsCache != null)
                return _runtimeSettingsCache;

            GPUInstancerProjectSettings projectSettings = null;

#if UNITY_EDITOR
            // Always prefer the project-owned settings asset.  The copy shipped
            // in Resources is a template and must never be edited in
            // place (registry packages are often read-only).
            string projectAssetPath = GPUInstancerConstants.GetProjectSettingsAssetPath(
                GPUInstancerConstants.PROJECT_SETTINGS_DEFAULT_NAME, ".asset");
            projectSettings = AssetDatabase.LoadAssetAtPath<GPUInstancerProjectSettings>(projectAssetPath);
            if (projectSettings == null && !Application.isPlaying)
            {
                GPUInstancerProjectSettings template = Resources.Load<GPUInstancerProjectSettings>(
                    GPUInstancerConstants.SETTINGS_PATH + GPUInstancerConstants.PROJECT_SETTINGS_DEFAULT_NAME);
                projectSettings = template != null ? Instantiate(template) : CreateInstance<GPUInstancerProjectSettings>();
                projectSettings.name = GPUInstancerConstants.PROJECT_SETTINGS_DEFAULT_NAME;
                _settingsBeingInitialized = projectSettings;
                try { CreateProjectSettingsAsset(projectSettings, ".asset"); }
                finally
                {
                    _settingsBeingInitialized = null;
                }
            }
#endif

            if (projectSettings == null)
            {
                // Player builds can load a generated project copy by its unique
                // Resources key.  If no copy was included, clone the package
                // template in memory rather than mutating the template object.
                projectSettings = Resources.Load<GPUInstancerProjectSettings>(
                    GPUInstancerConstants.GetProjectSettingsResourcePath(
                        GPUInstancerConstants.PROJECT_SETTINGS_DEFAULT_NAME));
                if (projectSettings == null)
                {
                    GPUInstancerProjectSettings template = Resources.Load<GPUInstancerProjectSettings>(
                        GPUInstancerConstants.SETTINGS_PATH + GPUInstancerConstants.PROJECT_SETTINGS_DEFAULT_NAME);
                    projectSettings = template != null ? Instantiate(template) : CreateInstance<GPUInstancerProjectSettings>();
                    projectSettings.name = GPUInstancerConstants.PROJECT_SETTINGS_DEFAULT_NAME;
                }
            }

            if (projectSettings.runtimeConfig == null)
                throw new InvalidOperationException(
                    "GPUInstancerProjectSettings requires a runtimeConfig. Recreate the project settings asset from the current package template.");

            _settingsBeingInitialized = projectSettings;
            try
            {
                // Package-owned nested assets are replaced with writable project
                // copies by these setters. Keeping the template references alive
                // until this point also makes re-entrant reads safe.
                projectSettings.SetDefaultBindings();
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    EditorUtility.SetDirty(projectSettings);
                    AssetDatabase.SaveAssetIfDirty(projectSettings);
                }
#endif
                // Keep only play-mode/runtime results here.  Edit-mode
                // settings are resolved through AssetDatabase on every call
                // so inspector edits and asset replacement remain immediately
                // visible. Assign after initialization succeeds so a failed
                // setup cannot poison the cache with a partial object.
                if (Application.isPlaying)
                    _runtimeSettingsCache = projectSettings;
                return projectSettings;
            }
            finally
            {
                _settingsBeingInitialized = null;
            }
        }

        public virtual void SetDefaultBindings()
        {
            SetDefaultGPUInstancerShaderBindings();
            SetDefaultGPUInstancerBillboardAtlasBindings();
            SetDefaultShaderVariantCollection();

            // This method is called from every manager lifecycle boundary,
            // including Driver-created transient managers in Play Mode. Do not
            // replace an authored preset list there: assigning a new list
            // mutates the persistent ProjectSettings object in memory (and can
            // be saved when domain reload is disabled), while also discarding
            // custom platform choices. Only initialize missing/null data.
            if (renderingSettingPresets == null)
            {
                renderingSettingPresets = new List<GPUIRenderingSettings>
                {
                    new GPUIRenderingSettings(){ platform = GPUIPlatform.Default, matrixHandlingType = GPUIMatrixHandlingType.Default, computeThreadCount = GPUIComputeThreadCount.x512 },
                    new GPUIRenderingSettings(){ platform = GPUIPlatform.OpenGLCore, matrixHandlingType = GPUIMatrixHandlingType.Default, computeThreadCount = GPUIComputeThreadCount.x256 },
                    new GPUIRenderingSettings(){ platform = GPUIPlatform.Metal, matrixHandlingType = GPUIMatrixHandlingType.Default, computeThreadCount = GPUIComputeThreadCount.x256 },
                    new GPUIRenderingSettings(){ platform = GPUIPlatform.GLES31, matrixHandlingType = GPUIMatrixHandlingType.CopyToTexture, computeThreadCount = GPUIComputeThreadCount.x128 },
                    new GPUIRenderingSettings(){ platform = GPUIPlatform.Vulkan, matrixHandlingType = GPUIMatrixHandlingType.CopyToTexture, computeThreadCount = GPUIComputeThreadCount.x128 },
                    new GPUIRenderingSettings(){ platform = GPUIPlatform.PS4, matrixHandlingType = GPUIMatrixHandlingType.Default, computeThreadCount = GPUIComputeThreadCount.x512 },
                    new GPUIRenderingSettings(){ platform = GPUIPlatform.XBoxOne, matrixHandlingType = GPUIMatrixHandlingType.Default, computeThreadCount = GPUIComputeThreadCount.x512 }
                };
            }

        }

        #region Shader Bindings
        public virtual void SetDefaultGPUInstancerShaderBindings()
        {
            if (shaderBindings == null || IsPackageAsset(shaderBindings))
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    Undo.RecordObject(this, "GPUInstancerShaderBindings instance generated");
#endif
                shaderBindings = GetDefaultGPUInstancerShaderBindings();
            }
        }

        public static GPUInstancerShaderBindings GetDefaultGPUInstancerShaderBindings()
        {
            GPUInstancerShaderBindings shaderBindings = null;
#if UNITY_EDITOR
            string projectAssetPath = GPUInstancerConstants.GetProjectSettingsAssetPath(
                GPUInstancerConstants.SHADER_BINDINGS_DEFAULT_NAME, ".asset");
            shaderBindings = AssetDatabase.LoadAssetAtPath<GPUInstancerShaderBindings>(projectAssetPath);
            if (shaderBindings == null && !Application.isPlaying)
            {
                GPUInstancerShaderBindings template = Resources.Load<GPUInstancerShaderBindings>(
                    GPUInstancerConstants.SETTINGS_PATH + GPUInstancerConstants.SHADER_BINDINGS_DEFAULT_NAME);
                shaderBindings = template != null ? Instantiate(template) : CreateInstance<GPUInstancerShaderBindings>();
                shaderBindings.name = GPUInstancerConstants.SHADER_BINDINGS_DEFAULT_NAME;
                CreateProjectSettingsAsset(shaderBindings, ".asset");
            }
#endif
            if (shaderBindings == null)
            {
                shaderBindings = Resources.Load<GPUInstancerShaderBindings>(
                    GPUInstancerConstants.GetProjectSettingsResourcePath(
                        GPUInstancerConstants.SHADER_BINDINGS_DEFAULT_NAME));
                if (shaderBindings == null)
                {
                    GPUInstancerShaderBindings template = Resources.Load<GPUInstancerShaderBindings>(
                        GPUInstancerConstants.SETTINGS_PATH + GPUInstancerConstants.SHADER_BINDINGS_DEFAULT_NAME);
                    shaderBindings = template != null ? Instantiate(template) : CreateInstance<GPUInstancerShaderBindings>();
                    shaderBindings.name = GPUInstancerConstants.SHADER_BINDINGS_DEFAULT_NAME;
                }
            }
            if (shaderBindings.shaderInstances == null)
                shaderBindings.ResetShaderInstances();
            return shaderBindings;
        }
        #endregion Shader Bindings

        #region Billboard Atlas Bindings
        public virtual void SetDefaultGPUInstancerBillboardAtlasBindings()
        {
            if (billboardAtlasBindings == null || IsPackageAsset(billboardAtlasBindings))
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    Undo.RecordObject(this, "GPUInstancerBillboardAtlasBindings instance generated");
#endif
                billboardAtlasBindings = GetDefaultGPUInstancerBillboardAtlasBindings();
            }
        }

        public static GPUInstancerBillboardAtlasBindings GetDefaultGPUInstancerBillboardAtlasBindings()
        {
            GPUInstancerBillboardAtlasBindings billboardAtlasBindings = null;
#if UNITY_EDITOR
            string projectAssetPath = GPUInstancerConstants.GetProjectSettingsAssetPath(
                GPUInstancerConstants.BILLBOARD_ATLAS_BINDINGS_DEFAULT_NAME, ".asset");
            billboardAtlasBindings = AssetDatabase.LoadAssetAtPath<GPUInstancerBillboardAtlasBindings>(projectAssetPath);
            if (billboardAtlasBindings == null && !Application.isPlaying)
            {
                GPUInstancerBillboardAtlasBindings template = Resources.Load<GPUInstancerBillboardAtlasBindings>(
                    GPUInstancerConstants.SETTINGS_PATH + GPUInstancerConstants.BILLBOARD_ATLAS_BINDINGS_DEFAULT_NAME);
                billboardAtlasBindings = template != null ? Instantiate(template) : CreateInstance<GPUInstancerBillboardAtlasBindings>();
                billboardAtlasBindings.name = GPUInstancerConstants.BILLBOARD_ATLAS_BINDINGS_DEFAULT_NAME;
                CreateProjectSettingsAsset(billboardAtlasBindings, ".asset");
            }
#endif
            if (billboardAtlasBindings == null)
            {
                billboardAtlasBindings = Resources.Load<GPUInstancerBillboardAtlasBindings>(
                    GPUInstancerConstants.GetProjectSettingsResourcePath(
                        GPUInstancerConstants.BILLBOARD_ATLAS_BINDINGS_DEFAULT_NAME));
                if (billboardAtlasBindings == null)
                {
                    GPUInstancerBillboardAtlasBindings template = Resources.Load<GPUInstancerBillboardAtlasBindings>(
                        GPUInstancerConstants.SETTINGS_PATH + GPUInstancerConstants.BILLBOARD_ATLAS_BINDINGS_DEFAULT_NAME);
                    billboardAtlasBindings = template != null ? Instantiate(template) : CreateInstance<GPUInstancerBillboardAtlasBindings>();
                    billboardAtlasBindings.name = GPUInstancerConstants.BILLBOARD_ATLAS_BINDINGS_DEFAULT_NAME;
                }
            }
            if (billboardAtlasBindings.billboardAtlasBindings == null)
                billboardAtlasBindings.ResetBillboardAtlases();
            return billboardAtlasBindings;
        }
        #endregion Billboard Atlas Bindings

        #region Shader Variant Collection
        public virtual void SetDefaultShaderVariantCollection()
        {
            if (shaderVariantCollection == null || IsPackageAsset(shaderVariantCollection))
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    Undo.RecordObject(this, "GPUI ShaderVariantCollection instance generated");
#endif
                shaderVariantCollection = GetDefaultShaderVariantCollection();
            }
        }

        public static ShaderVariantCollection GetDefaultShaderVariantCollection()
        {
            ShaderVariantCollection shaderVariantCollection = null;
#if UNITY_EDITOR
            string projectAssetPath = GPUInstancerConstants.GetProjectSettingsAssetPath(
                GPUInstancerConstants.SHADER_VARIANT_COLLECTION_DEFAULT_NAME, ".shadervariants");
            shaderVariantCollection = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(projectAssetPath);
            if (shaderVariantCollection == null && !Application.isPlaying)
            {
                ShaderVariantCollection template = Resources.Load<ShaderVariantCollection>(
                    GPUInstancerConstants.SETTINGS_PATH + GPUInstancerConstants.SHADER_VARIANT_COLLECTION_DEFAULT_NAME);
                if (template != null)
                {
                    // ShaderVariantCollection is a native asset type. Instantiating
                    // it during a domain reload can trigger Unity's PPtr remapping
                    // assertion, so copy the package template through AssetDatabase.
                    string directory = System.IO.Path.GetDirectoryName(projectAssetPath);
                    if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                        System.IO.Directory.CreateDirectory(directory);

                    string templatePath = AssetDatabase.GetAssetPath(template);
                    if (!string.IsNullOrEmpty(templatePath) && AssetDatabase.CopyAsset(templatePath, projectAssetPath))
                    {
                        AssetDatabase.ImportAsset(projectAssetPath, ImportAssetOptions.ForceUpdate);
                        shaderVariantCollection = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(projectAssetPath);
                    }
                }

                if (shaderVariantCollection == null)
                {
                    shaderVariantCollection = new ShaderVariantCollection
                    {
                        name = GPUInstancerConstants.SHADER_VARIANT_COLLECTION_DEFAULT_NAME
                    };
                    CreateProjectSettingsAsset(shaderVariantCollection, ".shadervariants");
                }
            }
#endif
            if (shaderVariantCollection == null)
            {
                shaderVariantCollection = Resources.Load<ShaderVariantCollection>(
                    GPUInstancerConstants.GetProjectSettingsResourcePath(
                        GPUInstancerConstants.SHADER_VARIANT_COLLECTION_DEFAULT_NAME));
                if (shaderVariantCollection == null)
                {
                    ShaderVariantCollection template = Resources.Load<ShaderVariantCollection>(
                        GPUInstancerConstants.SETTINGS_PATH + GPUInstancerConstants.SHADER_VARIANT_COLLECTION_DEFAULT_NAME);
                    shaderVariantCollection = template != null ? template : new ShaderVariantCollection
                    {
                        name = GPUInstancerConstants.SHADER_VARIANT_COLLECTION_DEFAULT_NAME
                    };
                }
            }
            return shaderVariantCollection;
        }

        public virtual void AddShaderVariantToCollection(string shaderName, string extensionCode = null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && shaderBindings != null && shaderVariantCollection != null && !string.IsNullOrEmpty(shaderName))
            {
                Shader instancedShader = shaderBindings.GetInstancedShader(shaderName, extensionCode);
                if (instancedShader != null)
                {
                    ShaderVariantCollection.ShaderVariant shaderVariant = new ShaderVariantCollection.ShaderVariant();
                    shaderVariant.shader = instancedShader;
                    //shaderVariant.passType = PassType.Normal;
                    shaderVariantCollection.Add(shaderVariant);
                    // To add only the shader without the passtype or keywords, remove the specific variant but the shader remains
                    shaderVariantCollection.Remove(shaderVariant);
                }
            }
#endif
        }

        public virtual void AddShaderVariantToCollection(Material material, string extensionCode = null)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && shaderBindings != null && shaderVariantCollection != null && material != null && !string.IsNullOrEmpty(material.shader.name))
            {
                Shader instancedShader = shaderBindings.GetInstancedShader(material.shader.name, extensionCode);
                if (instancedShader != null)
                {
                    ShaderVariantCollection.ShaderVariant shaderVariant = new ShaderVariantCollection.ShaderVariant();
                    shaderVariant.shader = instancedShader;
                    shaderVariant.keywords = material.shaderKeywords;
                    shaderVariantCollection.Add(shaderVariant);
                }
            }
#endif
        }
        #endregion Shader Variant Collection

        private static bool IsPackageAsset(UnityEngine.Object asset)
        {
#if UNITY_EDITOR
            if (asset == null)
                return false;
            return GPUInstancerConstants.IsPackageAssetPath(AssetDatabase.GetAssetPath(asset));
#else
            return false;
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// Creates an editor-owned copy below Assets/FraxinusGPUBatcherData.
        /// AssetDatabase paths are project-relative; this helper deliberately
        /// never accepts a package path.
        /// </summary>
        private static void CreateProjectSettingsAsset(UnityEngine.Object asset, string extension)
        {
            if (asset == null || Application.isPlaying)
                return;

            string assetPath = GPUInstancerConstants.GetProjectSettingsAssetPath(asset.name, extension);
            if (GPUInstancerConstants.IsPackageAssetPath(assetPath))
                throw new InvalidOperationException("Generated settings path resolved inside Packages/: " + assetPath);

            string directory = System.IO.Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                System.IO.Directory.CreateDirectory(directory);

            UnityEngine.Object existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(asset, assetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }
        }
#endif

        #region Rendering Settings
        public GPUIMatrixHandlingType GetMatrixHandlingType(GPUIPlatform platform)
        {
            if (hasCustomRenderingSettings && customRenderingSettings != null)
                return customRenderingSettings.matrixHandlingType;
            if (renderingSettingPresets == null)
                return GPUIMatrixHandlingType.Default;
            for (int i = 0; i < renderingSettingPresets.Count; i++)
            {
                GPUIRenderingSettings preset = renderingSettingPresets[i];
                if (preset != null && preset.platform == platform)
                    return renderingSettingPresets[i].matrixHandlingType;
            }
            return GPUIMatrixHandlingType.Default;
        }

        public GPUIComputeThreadCount GetComputeThreadCount(GPUIPlatform platform)
        {
            if (hasCustomRenderingSettings && customRenderingSettings != null)
                return customRenderingSettings.computeThreadCount;
            if (renderingSettingPresets == null)
                return GPUIComputeThreadCount.x1024;
            for (int i = 0; i < renderingSettingPresets.Count; i++)
            {
                GPUIRenderingSettings preset = renderingSettingPresets[i];
                if (preset != null && preset.platform == platform)
                    return renderingSettingPresets[i].computeThreadCount;
            }
            return GPUIComputeThreadCount.x1024;
        }

        [Serializable]
        public class GPUIRenderingSettings
        {
            public GPUIPlatform platform;
            public GPUIMatrixHandlingType matrixHandlingType;
            public GPUIComputeThreadCount computeThreadCount;
        }
        #endregion Rendering Settings

        public bool IsUseBothEyesVRCulling()
        {
#if GPUI_XR
            return UnityEngine.XR.XRSettings.enabled;
#else
            return false;
#endif
        }

        public static bool IsVREnabled()
        {
#if GPUI_XR
            return UnityEngine.XR.XRSettings.enabled;
#else
            return false;
#endif
        }

        public bool IsBillboardsSupported()
        {
            return Shader.Find(GPUInstancerConstants.SHADER_GPUI_BILLBOARD_2D_RENDERER_URP) != null;
        }
    }

    public enum GPUIPlatform
    {
        Default,
        OpenGLCore,
        Metal,
        GLES31,
        Vulkan,
        PS4,
        XBoxOne
    }

    public enum GPUIMatrixHandlingType
    {
        Default = 0,
        MatrixAppend = 1,
        CopyToTexture = 2
    }

    public enum GPUIComputeThreadCount
    {
        x64 = 0,
        x128 = 1,
        x256 = 2,
        x512 = 3,
        x1024 = 4
    }
}
