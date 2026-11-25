using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEditor.Rendering
{
    /// <summary>
    /// <see cref="ProjectWindowCallback.EndNameEditAction"/> for <see cref="RenderPipelineGlobalSettings"/>
    /// </summary>
    public class RenderPipelineGlobalSettingsEndNameEditAction : ProjectWindowCallback.EndNameEditAction
    {
        private Type renderPipelineType { get; set; }
        private Type renderPipelineGlobalSettingsType { get; set; }
        private RenderPipelineGlobalSettings source { get; set; }

        /// <inheritdoc/>
        public override void Action(int instanceId, string pathName, string resourceFile)
        {
            RenderPipelineGlobalSettings assetCreated = RenderPipelineGlobalSettingsUtils.Create(renderPipelineGlobalSettingsType, pathName, source) as RenderPipelineGlobalSettings;

            if (renderPipelineType != null)
            {
#if UNITY_6000_0_OR_NEWER
                EditorGraphicsSettings.SetRenderPipelineGlobalSettingsAsset(renderPipelineType, assetCreated);
#else
                // Unity 2022 exposes only a generic registration API. Resolve
                // that generic method with the concrete pipeline type retained
                // by this action so the asset is associated with the intended
                // render pipeline rather than the abstract RenderPipeline base.
                RegisterRenderPipelineSettings(renderPipelineType, assetCreated);
#endif
            }

            ProjectWindowUtil.ShowCreatedAsset(assetCreated);
        }

#if !UNITY_6000_0_OR_NEWER
        static void RegisterRenderPipelineSettings(Type pipelineType, RenderPipelineGlobalSettings settings)
        {
            var methods = typeof(GraphicsSettings).GetMethods(BindingFlags.Public | BindingFlags.Static);
            foreach (var method in methods)
            {
                if (method.Name != "RegisterRenderPipelineSettings" || !method.IsGenericMethodDefinition || method.GetGenericArguments().Length != 1)
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(RenderPipelineGlobalSettings))
                    continue;

                method.MakeGenericMethod(pipelineType).Invoke(null, new object[] { settings });
                RenderPipelineGraphicsSettingsCompatibility.RegisterGlobalSettings(
                    settings,
                    pipelineType);
                return;
            }

            throw new MissingMethodException(typeof(GraphicsSettings).FullName, "RegisterRenderPipelineSettings<T>");
        }
#endif

        private static string s_DefaultAssetName = "{0}.asset";

        internal static RenderPipelineGlobalSettingsEndNameEditAction CreateEndNameEditAction(
            Type renderPipelineType, Type renderPipelineGlobalSettingsType, bool updateAssetOnGraphicsSettings, RenderPipelineGlobalSettings source = null)
        {
            var action = ScriptableObject.CreateInstance<RenderPipelineGlobalSettingsEndNameEditAction>();
            action.renderPipelineGlobalSettingsType = renderPipelineGlobalSettingsType;

            if (updateAssetOnGraphicsSettings)
                action.renderPipelineType = renderPipelineType;

            if (source != null)
                action.source = source;

            return action;
        }

        internal static void StartEndNameEditAction(RenderPipelineGlobalSettingsEndNameEditAction action, string pathName)
        {
            ProjectWindowUtil.StartNameEditingIfProjectWindowExists(
                action.GetInstanceID(),
                action,
                pathName,
                CoreEditorStyles.globalSettingsIcon,
                null);
        }

        /// <summary>
        /// Creates a new <see cref="RenderPipelineGlobalSettings"/> of the given type for the given pipeline
        /// </summary>
        /// <typeparam name="TRenderPipeline">The type of the render pipeline the new settings asset is intended for.</typeparam>
        /// <typeparam name="TGlobalSettings">The type of the global settings asset to create.</typeparam>
        /// <param name="usePathFromCurrentSettings">If true, the new asset will be created at a unique path based on the current settings' path.</param>
        /// <param name="updateAssetOnGraphicsSettings">If true, GraphicsSettings will be updated with the new asset.</param>
        public static void CreateNew<TRenderPipeline, TGlobalSettings>(bool usePathFromCurrentSettings = false, bool updateAssetOnGraphicsSettings = false)
            where TRenderPipeline : RenderPipeline
            where TGlobalSettings : RenderPipelineGlobalSettings
        {
            string pathName = string.Format(s_DefaultAssetName, typeof(TGlobalSettings).Name);

            if (usePathFromCurrentSettings)
            {
#if UNITY_6000_0_OR_NEWER
                var currentSettings = GraphicsSettings.GetSettingsForRenderPipeline(typeof(TRenderPipeline));
#else
                var currentSettings = GraphicsSettings.GetSettingsForRenderPipeline<TRenderPipeline>();
#endif
                if (currentSettings != null)
                    pathName = AssetDatabase.GenerateUniqueAssetPath(AssetDatabase.GetAssetPath(currentSettings));
            }

            StartEndNameEditAction(CreateEndNameEditAction(typeof(TRenderPipeline), typeof(TGlobalSettings), updateAssetOnGraphicsSettings), pathName);
        }

        internal static void CloneFrom<TRenderPipeline, TGlobalSettings>(RenderPipelineGlobalSettings source, bool updateAssetOnGraphicsSettings = false)
            where TRenderPipeline : RenderPipeline
            where TGlobalSettings : RenderPipelineGlobalSettings
        {
            var pathName = AssetDatabase.GenerateUniqueAssetPath(AssetDatabase.GetAssetPath(source));
            StartEndNameEditAction(CreateEndNameEditAction(typeof(TRenderPipeline), typeof(TGlobalSettings), updateAssetOnGraphicsSettings, source), pathName);
        }
    }
}
