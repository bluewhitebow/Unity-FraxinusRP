using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor.Rendering;
#endif

namespace UnityEngine.Rendering
{
#if !UNITY_6000_0_OR_NEWER
    // Unity 2022's non-generic RenderPipelineGlobalSettings base cannot expose
    // Unity 6's virtual Initialize method. This internal contract lets common
    // Core editor code dispatch to the virtual member on the generic shim
    // without reflection or pipeline-specific type checks.
    internal interface IRenderPipelineGlobalSettingsInitializer
    {
        void InitializeForCompatibility(RenderPipelineGlobalSettings source);
    }
#endif

    /// <summary>
    /// A <see cref="ScriptableObject"/> to associate with a <see cref="RenderPipeline"/> and store project-wide settings for that pipeline.
    /// You can register a single <see cref="RenderPipelineGlobalSettings"/> instance to the <see cref="GraphicsSettings"/> by using <see cref="Rendering.GraphicsSettings.RegisterRenderPipelineSettings"/>. You can use this to save `RenderPipeline` settings that appear in `GraphicsSettings`.
    /// </summary>
    /// <typeparam name="TGlobalRenderPipelineSettings"><see cref="RenderPipelineGlobalSettings"/></typeparam>
    /// <typeparam name="TRenderPipeline"><see cref="RenderPipeline"/></typeparam>
    public abstract class RenderPipelineGlobalSettings<TGlobalRenderPipelineSettings, TRenderPipeline> : RenderPipelineGlobalSettings
#if !UNITY_6000_0_OR_NEWER
        , IRenderPipelineGlobalSettingsInitializer
#endif
        where TRenderPipeline : RenderPipeline
        where TGlobalRenderPipelineSettings : RenderPipelineGlobalSettings
    {
#if !UNITY_6000_0_OR_NEWER
        // Unity 6 exposes the graphics-settings collection and its basic
        // accessors on RenderPipelineGlobalSettings itself. Unity 2022 has no
        // equivalent API, so keep the same derived-type contract here while
        // storing the collection in the pipeline-specific asset.
        protected abstract List<IRenderPipelineGraphicsSettings> settingsList { get; }

        /// <summary>Gets a graphics setting with the requested concrete type.</summary>
        public bool TryGet(Type type, out IRenderPipelineGraphicsSettings settings)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var list = settingsList;
            if (list != null)
            {
                for (int i = 0; i < list.Count; ++i)
                {
                    var candidate = list[i];
                    if (candidate != null && candidate.GetType() == type)
                    {
                        settings = candidate;
                        return true;
                    }
                }
            }

            settings = null;
            return false;
        }

        /// <summary>Adds a graphics setting to this global-settings asset.</summary>
        public void Add(IRenderPipelineGraphicsSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            settingsList?.Add(settings);
        }

        /// <summary>Initializes a newly created global-settings asset.</summary>
        public virtual void Initialize(RenderPipelineGlobalSettings source = null)
        {
        }

        void IRenderPipelineGlobalSettingsInitializer.InitializeForCompatibility(RenderPipelineGlobalSettings source)
        {
            Initialize(source);
        }
#endif

        public T GetSettings<T>() where T : class, IRenderPipelineGraphicsSettings
        {
            return TryGet(typeof(T), out var settings) ? settings as T : null;
        }

        /// <summary>
        /// Active Global Settings asset. If the value is `null` then no `TGlobalRenderPipelineSettings` is registered to the Graphics Settings with the `TRenderPipeline`.
        /// </summary>
#if UNITY_EDITOR
#if UNITY_6000_0_OR_NEWER
        public static TGlobalRenderPipelineSettings instance =>
            EditorGraphicsSettings.GetRenderPipelineGlobalSettingsAsset<TRenderPipeline>() as TGlobalRenderPipelineSettings;
#else
        public static TGlobalRenderPipelineSettings instance =>
            GraphicsSettings.GetSettingsForRenderPipeline<TRenderPipeline>() as TGlobalRenderPipelineSettings;
#endif
#else
        public static TGlobalRenderPipelineSettings instance => s_Instance.Value;
        private static Lazy<TGlobalRenderPipelineSettings> s_Instance = new (() => GraphicsSettings.GetSettingsForRenderPipeline<TRenderPipeline>() as TGlobalRenderPipelineSettings);
#endif

        /// <summary>
        /// Called when settings asset is reset in the editor.
        /// </summary>
        public virtual void Reset()
        {
#if UNITY_EDITOR
#if UNITY_6000_0_OR_NEWER
            EditorGraphicsSettings.PopulateRenderPipelineGraphicsSettings(this);
            Initialize();
#endif
#endif
        }
    }
}
