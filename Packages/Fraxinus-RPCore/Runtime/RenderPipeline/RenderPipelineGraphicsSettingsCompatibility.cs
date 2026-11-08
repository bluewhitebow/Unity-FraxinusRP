using System;
using System.Collections.Generic;
using System.Reflection;

#if UNITY_EDITOR
using UnityEditor;
#endif

// Unity 6 moved graphics settings into a new element/registration system.
// These small contracts keep EngineRPCore-authored settings source-compatible
// on Unity 2022 without pretending that the editor's Unity 6 registrar exists.
#if !UNITY_6000_0_OR_NEWER
namespace UnityEngine.Rendering
{
    /// <summary>
    /// Centralizes engine-version differences in the graphics-settings API.
    /// Unity 2022 stores one <see cref="RenderPipelineGlobalSettings"/> asset
    /// per RenderPipeline type and does not expose Unity 6's element lookup
    /// API, so callers fall back to that registered asset and its local
    /// settings collection.
    /// </summary>
    public static class RenderPipelineGraphicsSettingsCompatibility
    {
        // Unity 2022 has no public API for enumerating the global settings
        // registered for a pipeline.  Keep the last asset explicitly
        // registered by a pipeline package so settings can still be resolved
        // while the render pipeline is being created (before
        // RenderPipelineManager.currentPipeline is non-null).
        static RenderPipelineGlobalSettings s_RegisteredGlobalSettings;
        static Type s_RegisteredRenderPipelineType;
        static RenderPipelineGlobalSettings s_SettingsCacheOwner;
        static readonly Dictionary<Type, object> s_SettingsCache = new Dictionary<Type, object>();

        // Fallback lookup is needed only for custom SRPs which do not call
        // RegisterGlobalSettings.  The old implementation rebuilt the method
        // list, closed the generic method, and invoked reflection on *every*
        // settings query.  Several Core helpers are queried from camera/render
        // hot paths, so that fallback turned into recurring managed work (and
        // reflection temporary allocations) when a custom pipeline was active.
        // Cache the method metadata per pipeline type and the resolved result
        // per pipeline instance.  A package that owns the registration clears
        // this cache below whenever its global-settings asset changes.
        static MethodInfo s_GetSettingsForRenderPipelineMethod;
        static bool s_GetSettingsForRenderPipelineMethodResolved;
        static readonly Dictionary<Type, MethodInfo> s_ClosedGetSettingsMethods =
            new Dictionary<Type, MethodInfo>();
        static RenderPipeline s_FallbackPipeline;
        static RenderPipelineGlobalSettings s_FallbackSettings;
        static bool s_FallbackSettingsResolved;

        // Unity 2022 does not expose a change notification when an editor
        // tool replaces the global-settings asset through
        // GraphicsSettings.RegisterRenderPipelineSettings<T>().  Pipeline
        // packages can use this notification to invalidate their own typed
        // instance caches without polling GraphicsSettings from a render
        // hot path.  The event is intentionally internal: it is a bridge
        // implementation detail, not another public graphics-settings API.
        internal static event Action<Type, RenderPipelineGlobalSettings> globalSettingsChanged;

#if UNITY_EDITOR
        // Discovering graphics-setting element types requires walking every
        // loaded assembly and calling GetTypes()/GetCustomAttributes().  The
        // global-settings asset can be ensured several times during one domain
        // reload (asset postprocessors, delayed deserialization, and pipeline
        // bootstrap), so keep the discovery result for the lifetime of this
        // managed domain.  A small assembly-count guard also handles editor
        // package loads that add an assembly without a full domain reload.
        readonly struct GraphicsSettingTypeCacheKey : IEquatable<GraphicsSettingTypeCacheKey>
        {
            readonly Type globalSettingsType;
            readonly Type renderPipelineAssetType;

            public GraphicsSettingTypeCacheKey(Type globalSettingsType, Type renderPipelineAssetType)
            {
                this.globalSettingsType = globalSettingsType;
                this.renderPipelineAssetType = renderPipelineAssetType;
            }

            public bool Equals(GraphicsSettingTypeCacheKey other) =>
                globalSettingsType == other.globalSettingsType &&
                renderPipelineAssetType == other.renderPipelineAssetType;

            public override bool Equals(object obj) =>
                obj is GraphicsSettingTypeCacheKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((globalSettingsType != null ? globalSettingsType.GetHashCode() : 0) * 397) ^
                        (renderPipelineAssetType != null ? renderPipelineAssetType.GetHashCode() : 0);
                }
            }
        }

        static readonly Dictionary<GraphicsSettingTypeCacheKey, List<Type>> s_GraphicsSettingTypesCache =
            new Dictionary<GraphicsSettingTypeCacheKey, List<Type>>();
        static int s_GraphicsSettingAssemblyCount = -1;
#endif

        public static Type currentRenderPipelineAssetType
        {
            get
            {
                // GraphicsSettings.currentRenderPipeline crosses the managed /
                // native boundary on Unity 2022. Read it once per query.
                var asset = GraphicsSettings.currentRenderPipeline;
                return asset != null ? asset.GetType() : null;
            }
        }

        /// <summary>
        /// Records the global settings asset currently owned by a pipeline.
        /// Unity 6 performs this bookkeeping in <c>GraphicsSettings</c>; the
        /// 2022 API needs a small package-side bridge.
        /// </summary>
        public static void RegisterGlobalSettings(
            RenderPipelineGlobalSettings settings,
            Type renderPipelineType = null)
        {
            if (settings != null &&
                (renderPipelineType == null || !typeof(RenderPipeline).IsAssignableFrom(renderPipelineType)))
                throw new ArgumentException(
                    $"{renderPipelineType?.FullName ?? "null"} must derive from {typeof(RenderPipeline).FullName}.",
                    nameof(renderPipelineType));

            if (ReferenceEquals(s_RegisteredGlobalSettings, settings) &&
                s_RegisteredRenderPipelineType == renderPipelineType)
                return;

            s_RegisteredGlobalSettings = settings;
            s_RegisteredRenderPipelineType = settings != null ? renderPipelineType : null;
            RenderingLayerMask.SetNamesProvider(settings as IRenderingLayerNamesProvider);
            s_SettingsCacheOwner = null;
            s_SettingsCache.Clear();
            s_FallbackPipeline = null;
            s_FallbackSettings = null;
            s_FallbackSettingsResolved = false;

            // Notify after updating the bridge state so listeners that need
            // to refresh a typed cache can read the new registration.  Keep
            // the callback isolated from the registration itself: a package
            // listener must never make GraphicsSettings registration fail.
            var changed = globalSettingsChanged;
            if (changed != null)
            {
                try
                {
                    changed(renderPipelineType, settings);
                }
                catch
                {
                    // Cache invalidation is best effort.  The next explicit
                    // lifecycle notification (or editor projectChanged
                    // event) will invalidate the listener again.
                }
            }
        }

        public static T GetRenderPipelineSettings<T>() where T : class
        {
            var globalSettings = GetCurrentRenderPipelineGlobalSettings();
            if (globalSettings == null)
                return null;

            return GetRenderPipelineSettings<T>(globalSettings);
        }

        /// <summary>
        /// Resolves a graphics setting from a specific global settings asset.
        /// This overload is useful during asset migration, when the asset is
        /// not registered yet but its serialized container is available.
        /// </summary>
        public static T GetRenderPipelineSettings<T>(RenderPipelineGlobalSettings globalSettings) where T : class
        {
            if (globalSettings == null)
                return null;

            if (globalSettings is T directSettings)
                return directSettings;

            // Cache lookups by setting type and owning asset. Settings are
            // managed objects whose identity remains stable for the lifetime
            // of a global-settings asset. This also covers an unregistered
            // custom SRP resolved through the reflection fallback: method
            // metadata alone is not enough if every typed lookup has to walk
            // the asset fields and properties again.
            if (!ReferenceEquals(s_SettingsCacheOwner, globalSettings))
            {
                s_SettingsCacheOwner = globalSettings;
                s_SettingsCache.Clear();
            }

            if (s_SettingsCache.TryGetValue(typeof(T), out var cached))
                return cached as T;

            T result = null;
            if (TryGetSettingsList(globalSettings, out var settingsList))
            {
                for (int i = 0; i < settingsList.Count; ++i)
                {
                    if (settingsList[i] is T typedSettings)
                    {
                        result = typedSettings;
                        break;
                    }
                }
            }
            s_SettingsCache[typeof(T)] = result;
            return result;
        }

        public static bool TryGetRenderPipelineSettings<T>(out T settings) where T : class
        {
            settings = GetRenderPipelineSettings<T>();
            return settings != null;
        }

        /// <summary>Tries to resolve a setting from a supplied global asset.</summary>
        public static bool TryGetRenderPipelineSettings<T>(
            RenderPipelineGlobalSettings globalSettings,
            out T settings) where T : class
        {
            settings = GetRenderPipelineSettings<T>(globalSettings);
            return settings != null;
        }

        /// <summary>
        /// Returns the concrete settings list held by a global settings
        /// container.  Unity 2022 cannot expose Unity 6's internal
        /// collection type, so this method accepts both the backported
        /// container and compatible custom containers through reflection.
        /// </summary>
        public static bool TryGetSettingsList(
            RenderPipelineGlobalSettings globalSettings,
            out List<IRenderPipelineGraphicsSettings> settingsList)
        {
            settingsList = FindSettingsList(globalSettings);
            return settingsList != null;
        }

        /// <summary>
        /// Populates a 2022 global settings container with every serializable
        /// graphics setting type visible to the current AppDomain.  This is
        /// the package-side equivalent of Unity 6's
        /// <c>EditorGraphicsSettings.PopulateRenderPipelineGraphicsSettings</c>.
        /// </summary>
        /// <param name="globalSettings">Asset that owns the settings.</param>
        /// <param name="renderPipelineAssetType">Optional asset type used by
        /// <see cref="SupportedOnRenderPipelineAttribute"/> filtering.</param>
        /// <returns>True when a setting or resource reference was added.</returns>
        public static bool PopulateRenderPipelineGraphicsSettings(
            RenderPipelineGlobalSettings globalSettings,
            Type renderPipelineAssetType = null)
        {
#if UNITY_EDITOR
            if (!TryGetSettingsList(globalSettings, out var settingsList))
                return false;

            var changed = false;
            var settingTypes = GetGraphicsSettingTypes(globalSettings, renderPipelineAssetType);
            for (int i = 0; i < settingTypes.Count; ++i)
            {
                var settingType = settingTypes[i];
                var alreadyPresent = false;
                for (int j = 0; j < settingsList.Count; ++j)
                {
                    var existing = settingsList[j];
                    if (existing != null && existing.GetType() == settingType)
                    {
                        alreadyPresent = true;
                        break;
                    }
                }

                if (alreadyPresent)
                    continue;

                var instance = CreateSettingInstance(settingType);
                if (instance is IRenderPipelineGraphicsSettings graphicsSettings)
                {
                    settingsList.Add(graphicsSettings);
                    changed = true;
                }
            }

            // ResourcePath is the Unity 6 graphics-settings resource marker.
            // Populate the managed settings with package assets so a newly
            // created 2022 global settings asset behaves like the Unity 6
            // asset after domain reload.
            for (int i = 0; i < settingsList.Count; ++i)
                changed |= TryReloadResourceReferences(settingsList[i]);

            if (changed && ReferenceEquals(globalSettings, s_SettingsCacheOwner))
                s_SettingsCache.Clear();
            return changed;
#else
            // Player data is generated by
            // RenderPipelineGraphicsSettingsContainer.OnBeforeSerialize and
            // must not be repopulated with editor-only settings at runtime.
            return false;
#endif
        }

        static List<IRenderPipelineGraphicsSettings> FindSettingsList(object value)
        {
            if (value == null)
                return null;

            if (value is RenderPipelineGraphicsSettingsContainer container)
                return container.settingsList;

            if (value is RenderPipelineGraphicsSettingsCollection collection)
                return collection.settingsList;

            if (value is List<IRenderPipelineGraphicsSettings> list)
                return list;

            // Do not recursively inspect arbitrary object graphs here.  Apart
            // from being expensive on every settings lookup, boxed value
            // types can expose self-similar implementation fields and cause
            // unbounded recursion.  A compatible global settings asset only
            // needs to expose the container/list as a direct field or
            // property, which mirrors the Unity 6 layout.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var currentType = value.GetType(); currentType != null; currentType = currentType.BaseType)
            {
                var fields = currentType.GetFields(flags | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; ++i)
                {
                    var field = fields[i];
                    if (field.IsStatic || !CanHoldSettingsList(field.FieldType))
                        continue;
                    var result = GetDirectSettingsList(field.GetValue(value));
                    if (result != null)
                        return result;
                }

                var properties = currentType.GetProperties(flags | BindingFlags.DeclaredOnly);
                for (int i = 0; i < properties.Length; ++i)
                {
                    var property = properties[i];
                    if (property.GetIndexParameters().Length != 0 || property.GetMethod == null)
                        continue;
                    if (!CanHoldSettingsList(property.PropertyType))
                        continue;
                    var result = GetDirectSettingsList(property.GetValue(value, null));
                    if (result != null)
                        return result;
                }
            }

            return null;
        }

        static bool CanHoldSettingsList(Type type)
        {
            return type == typeof(RenderPipelineGraphicsSettingsContainer) ||
                type == typeof(RenderPipelineGraphicsSettingsCollection) ||
                typeof(List<IRenderPipelineGraphicsSettings>).IsAssignableFrom(type);
        }

        static List<IRenderPipelineGraphicsSettings> GetDirectSettingsList(object value)
        {
            if (value is RenderPipelineGraphicsSettingsContainer container)
                return container.settingsList;
            if (value is RenderPipelineGraphicsSettingsCollection collection)
                return collection.settingsList;
            return value as List<IRenderPipelineGraphicsSettings>;
        }

#if UNITY_EDITOR
        static List<Type> GetGraphicsSettingTypes(
            RenderPipelineGlobalSettings globalSettings,
            Type renderPipelineAssetType)
        {
            // AppDomain statics are reset on a normal Unity domain reload. The
            // count check covers the less common case where a package assembly
            // is loaded into the existing editor domain after the first query.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (s_GraphicsSettingAssemblyCount != assemblies.Length)
            {
                s_GraphicsSettingTypesCache.Clear();
                s_GraphicsSettingAssemblyCount = assemblies.Length;
            }

            var cacheKey = new GraphicsSettingTypeCacheKey(
                globalSettings != null ? globalSettings.GetType() : null,
                renderPipelineAssetType);
            if (s_GraphicsSettingTypesCache.TryGetValue(cacheKey, out var cachedTypes))
                return cachedTypes;

            var result = new List<Type>();
            var targetPipelineTypes = new List<Type>();
            if (renderPipelineAssetType != null)
            {
                targetPipelineTypes.Add(renderPipelineAssetType);
            }
            else if (globalSettings != null)
            {
                // EditorGraphicsSettings derives this association from the
                // global-settings asset type. Do the same when callers use the
                // generic Populate overload, otherwise settings belonging to
                // every installed SRP would be added to a newly created asset.
                var attributes = Attribute.GetCustomAttributes(
                    globalSettings.GetType(),
                    typeof(SupportedOnRenderPipelineAttribute),
                    true);
                for (int attributeIndex = 0; attributeIndex < attributes.Length; ++attributeIndex)
                {
                    var pipelineTypes = ((SupportedOnRenderPipelineAttribute)attributes[attributeIndex])
                        .renderPipelineTypes;
                    for (int pipelineIndex = 0; pipelineIndex < pipelineTypes.Length; ++pipelineIndex)
                    {
                        var pipelineType = pipelineTypes[pipelineIndex];
                        if (pipelineType != null && !targetPipelineTypes.Contains(pipelineType))
                            targetPipelineTypes.Add(pipelineType);
                    }
                }
            }

            var settingsInterface = typeof(IRenderPipelineGraphicsSettings);
            for (int i = 0; i < assemblies.Length; ++i)
            {
                Type[] types;
                try
                {
                    types = assemblies[i].GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null)
                    continue;
                for (int j = 0; j < types.Length; ++j)
                {
                    var type = types[j];
                    if (type == null || !type.IsClass || type.IsAbstract || type.ContainsGenericParameters ||
                        !settingsInterface.IsAssignableFrom(type) ||
                        type.GetCustomAttributes(typeof(SerializableAttribute), true).Length == 0)
                        continue;

                    var supportedAttributes = type.GetCustomAttributes(typeof(SupportedOnRenderPipelineAttribute), true);
                    if (supportedAttributes.Length == 0)
                        continue;

                    if (targetPipelineTypes.Count != 0)
                    {
                        var supported = false;
                        for (int pipelineIndex = 0; pipelineIndex < targetPipelineTypes.Count; ++pipelineIndex)
                        {
                            if (SupportedOnRenderPipelineAttribute.IsTypeSupportedOnRenderPipeline(
                                type, targetPipelineTypes[pipelineIndex]))
                            {
                                supported = true;
                                break;
                            }
                        }

                        if (!supported)
                            continue;
                    }

                    if (!result.Contains(type))
                        result.Add(type);
                }
            }

            result.Sort((left, right) => string.CompareOrdinal(left.FullName, right.FullName));
            s_GraphicsSettingTypesCache[cacheKey] = result;
            return result;
        }

        static object CreateSettingInstance(Type type)
        {
            try
            {
                return Activator.CreateInstance(
                    type,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: null,
                    culture: null);
            }
            catch
            {
                // A third-party setting may expose no default constructor;
                // leave it for the package/editor to register explicitly.
                return null;
            }
        }

        static bool TryReloadResourceReferences(IRenderPipelineGraphicsSettings settings)
        {
            if (settings == null)
                return false;

            // ResourcePath is relative to the package which declares the setting.
            // Different SRPs commonly use the same relative shader/material names.
            var ownerPackage = UnityEditor.PackageManager.PackageInfo.FindForAssembly(settings.GetType().Assembly);
            var roots = ownerPackage != null
                ? new[] { ownerPackage.assetPath, "Packages/" + ownerPackage.name }
                : new[]
                {
                    "Packages/Fraxinus-RPCore",
                    "Packages/com.unity.render-pipelines.core",
                    "Packages/FraxinusRP",
                    "Packages/com.bluewhitebow.fraxinus-rp",
                    "Packages/Fraxinus-URPExtensions",
                    "Packages/com.unity.render-pipelines.universal",
                };
            var changed = false;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var currentType = settings.GetType(); currentType != null; currentType = currentType.BaseType)
            {
                var fields = currentType.GetFields(flags | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; ++i)
                {
                    var field = fields[i];
                    var pathsMarker = field.GetCustomAttribute<ResourcePathsAttribute>(true);
                    if (pathsMarker != null && field.FieldType.IsArray &&
                        typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType.GetElementType()))
                    {
                        var elementType = field.FieldType.GetElementType();
                        var paths = pathsMarker.paths;
                        var values = field.GetValue(settings) as Array;
                        if (values == null || values.Length != paths.Length)
                        {
                            var resized = Array.CreateInstance(elementType, paths.Length);
                            if (values != null)
                                Array.Copy(values, resized, Math.Min(values.Length, resized.Length));
                            values = resized;
                            field.SetValue(settings, values);
                            changed = true;
                        }
                        for (int index = 0; index < paths.Length; ++index)
                        {
                            if (values.GetValue(index) is UnityEngine.Object existing && existing != null)
                                continue;
                            var resource = LoadSettingsResource(roots, paths[index], elementType);
                            if (resource == null)
                                continue;
                            values.SetValue(resource, index);
                            changed = true;
                        }
                        continue;
                    }
                    var marker = field.GetCustomAttribute<ResourcePathAttribute>(true);
                    if (marker == null || !typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                        continue;
                    var currentValue = field.GetValue(settings) as UnityEngine.Object;
                    if (currentValue != null && !currentValue.Equals(null))
                        continue;

                    var loaded = LoadSettingsResource(roots, marker.path, field.FieldType);
                    if (loaded != null)
                    {
                        field.SetValue(settings, loaded);
                        changed = true;
                    }
                }
            }

            return changed;
        }

        static UnityEngine.Object LoadSettingsResource(string[] roots, string relativePath, Type type)
        {
            if (relativePath.StartsWith("Packages/", StringComparison.Ordinal) ||
                relativePath.StartsWith("Assets/", StringComparison.Ordinal))
                return AssetDatabase.LoadAssetAtPath(relativePath, type);

            for (int i = 0; i < roots.Length; ++i)
            {
                var path = roots[i].TrimEnd('/') + "/" + relativePath.TrimStart('/');
                var loaded = AssetDatabase.LoadAssetAtPath(path, type);
                if (loaded != null)
                    return loaded;
            }
            return null;
        }
#endif

        static RenderPipelineGlobalSettings GetCurrentRenderPipelineGlobalSettings()
        {
            var pipeline = RenderPipelineManager.currentPipeline;

            // Pipeline packages register their active global-settings asset
            // through RegisterGlobalSettings.  That registration is the
            // Unity-2022 equivalent of Unity 6's native graphics-settings
            // registry, so use it as the normal lookup path.  In particular,
            // graphics settings are queried from camera/render hot paths; do
            // not enumerate GraphicsSettings methods and invoke reflection on
            // every lookup when the matching asset is already known.
            if (s_RegisteredGlobalSettings != null &&
                (pipeline == null ||
                 s_RegisteredRenderPipelineType == null ||
                 pipeline.GetType() == s_RegisteredRenderPipelineType))
                return s_RegisteredGlobalSettings;

            // The 2022 API exposes this as a generic method only. Resolve the
            // concrete pipeline type at runtime as a fallback for custom SRPs
            // that have not registered through the compatibility bridge.
            if (pipeline != null)
            {
                // A pipeline instance is recreated when Unity switches SRPs.
                // Keep the null result cached as well: an unregistered custom
                // SRP is a valid state and should not trigger reflection on
                // every call merely because no settings asset exists.
                if (s_FallbackSettingsResolved &&
                    ReferenceEquals(s_FallbackPipeline, pipeline))
                    return s_FallbackSettings;

                var pipelineType = pipeline.GetType();
                RenderPipelineGlobalSettings result = null;
                if (TryGetSettingsForPipelineType(pipelineType, out var method))
                {
                    try
                    {
                        result = method.Invoke(null, null) as RenderPipelineGlobalSettings;
                    }
                    catch (ArgumentException)
                    {
                        // A malformed/custom pipeline type can fail the
                        // generic constraint; retain a null result for this
                        // pipeline instance and keep the package registration
                        // fallback below intact.
                    }
                    catch (TargetInvocationException)
                    {
                        // Unity may reject an unregistered pipeline while it
                        // is being torn down.  The cached registration below
                        // remains valid for this frame.
                    }
                }

                s_FallbackPipeline = pipeline;
                s_FallbackSettings = result;
                s_FallbackSettingsResolved = true;
                if (result != null)
                    return result;
            }

            if (pipeline == null ||
                s_RegisteredRenderPipelineType == null ||
                pipeline.GetType() == s_RegisteredRenderPipelineType)
                return s_RegisteredGlobalSettings;

            // A different SRP is active.  Do not leak the last package-side
            // registration across a pipeline switch when that SRP has no
            // registered global settings asset of its own.
            return null;
        }

        static bool TryGetSettingsForPipelineType(Type pipelineType, out MethodInfo closedMethod)
        {
            closedMethod = null;
            if (pipelineType == null)
                return false;

            if (s_ClosedGetSettingsMethods.TryGetValue(pipelineType, out closedMethod))
                return closedMethod != null;

            if (!s_GetSettingsForRenderPipelineMethodResolved)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                var methods = typeof(GraphicsSettings).GetMethods(flags);
                for (int i = 0; i < methods.Length; ++i)
                {
                    var method = methods[i];
                    if (method.Name == "GetSettingsForRenderPipeline" &&
                        method.IsGenericMethodDefinition &&
                        method.GetGenericArguments().Length == 1 &&
                        method.GetParameters().Length == 0)
                    {
                        s_GetSettingsForRenderPipelineMethod = method;
                        break;
                    }
                }

                s_GetSettingsForRenderPipelineMethodResolved = true;
            }

            if (s_GetSettingsForRenderPipelineMethod != null)
            {
                try
                {
                    closedMethod = s_GetSettingsForRenderPipelineMethod.MakeGenericMethod(pipelineType);
                }
                catch (ArgumentException)
                {
                    // The type did not satisfy Unity's generic constraint.
                    // Remember the miss so it is not retried for every query.
                    closedMethod = null;
                }
            }

            s_ClosedGetSettingsMethods[pipelineType] = closedMethod;
            return closedMethod != null;
        }
    }

    public interface IRenderPipelineGraphicsSettings
    {
        bool isAvailableInPlayerBuild { get; }
        int version { get; }
    }

    public interface IRenderPipelineResources : IRenderPipelineGraphicsSettings
    {
    }

    /// <summary>
    /// Unity 6 uses this attribute to associate a settings/volume type with
    /// one or more render-pipeline asset types.  Unity 2022 does not expose
    /// the attribute, so this implementation keeps the same reflection-facing
    /// contract and performs the type test locally.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = true)]
    public sealed class SupportedOnRenderPipelineAttribute : Attribute
    {
        readonly Type[] m_RenderPipelineTypes;

        public enum SupportedMode
        {
            Unsupported = 0,
            Supported = 1,
            SupportedByBaseClass = 2,
        }

        public SupportedOnRenderPipelineAttribute(Type renderPipeline)
            : this(new[] { renderPipeline })
        {
        }

        public SupportedOnRenderPipelineAttribute(params Type[] renderPipeline)
        {
            if (renderPipeline == null || renderPipeline.Length == 0)
                renderPipeline = new[] { typeof(RenderPipelineAsset) };

            for (int i = 0; i < renderPipeline.Length; ++i)
            {
                var pipelineType = renderPipeline[i];
                if (pipelineType == null || !typeof(RenderPipelineAsset).IsAssignableFrom(pipelineType))
                    throw new ArgumentException(
                        $"{pipelineType?.FullName ?? "null"} must derive from {typeof(RenderPipelineAsset).FullName}.",
                        nameof(renderPipeline));
            }

            m_RenderPipelineTypes = renderPipeline;
        }

        /// <summary>Render-pipeline asset types explicitly listed by the attribute.</summary>
        public Type[] renderPipelineTypes => m_RenderPipelineTypes;

        /// <summary>
        /// Whether the active render-pipeline asset supports this attribute.
        /// An attribute with no explicit types is intentionally considered
        /// applicable to every pipeline, matching Unity's default usage.
        /// </summary>
        public bool isSupportedOnCurrentPipeline
        {
            get
            {
                return GetSupportedMode(RenderPipelineGraphicsSettingsCompatibility.currentRenderPipelineAssetType) !=
                    SupportedMode.Unsupported;
            }
        }

        /// <summary>
        /// Returns whether the supplied asset type is directly supported,
        /// supported through a base asset type, or unsupported.
        /// </summary>
        public SupportedMode GetSupportedMode(Type renderPipelineAssetType)
        {
            if (renderPipelineAssetType == null)
                return SupportedMode.Unsupported;

            for (int i = 0; i < m_RenderPipelineTypes.Length; ++i)
            {
                var supportedType = m_RenderPipelineTypes[i];
                if (supportedType == null)
                    continue;

                if (renderPipelineAssetType == supportedType)
                    return SupportedMode.Supported;

                if (supportedType.IsAssignableFrom(renderPipelineAssetType))
                    return SupportedMode.SupportedByBaseClass;
            }

            return SupportedMode.Unsupported;
        }

        /// <summary>
        /// Tests a type's <see cref="SupportedOnRenderPipelineAttribute"/>
        /// against a render-pipeline asset type.  Types without the attribute
        /// remain available, as they did before the Unity 6 filtering API.
        /// </summary>
        public static bool IsTypeSupportedOnRenderPipeline(Type type, Type renderPipelineAssetType)
        {
            if (type == null)
                return false;

            var attributes = Attribute.GetCustomAttributes(
                type,
                typeof(SupportedOnRenderPipelineAttribute),
                true);

            if (attributes.Length == 0)
                return true;

            for (int i = 0; i < attributes.Length; ++i)
            {
                if (((SupportedOnRenderPipelineAttribute)attributes[i])
                    .GetSupportedMode(renderPipelineAssetType) != SupportedMode.Unsupported)
                    return true;
            }

            return false;
        }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class RecreatePipelineOnChangeAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class ResourcePathAttribute : Attribute
    {
        public readonly string path;
        public ResourcePathAttribute(string path) => this.path = path;
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class ResourcePathsAttribute : Attribute
    {
        public readonly string[] paths;
        public ResourcePathsAttribute(string[] paths) => this.paths = paths;
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class ResourceFormattedPathsAttribute : ResourcePathsAttribute
    {
        public ResourceFormattedPathsAttribute(string format, int start, int end)
            : base(CreatePaths(format, start, end)) { }

        static string[] CreatePaths(string format, int start, int end)
        {
            if (end < start)
                throw new ArgumentOutOfRangeException(nameof(end));
            var paths = new string[end - start];
            for (int i = 0; i < paths.Length; ++i)
                paths[i] = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, start + i);
            return paths;
        }
    }

    public static class RenderPipelineGraphicsSettingsExtensions
    {
        public static void SetValueAndNotify<T>(this object owner, ref T field, T value, string propertyName = null)
        {
            field = value;
        }
    }

    [Serializable]
    public sealed class RenderPipelineGraphicsSettingsCollection
    {
        // Unity 2022's regular serializer cannot serialize an interface list,
        // but SerializeReference can persist the concrete settings objects in
        // exactly the same asset that Unity 6 stores in its native collection.
        // Keep the field private so the existing Core property drawer still
        // owns the presentation of the collection.
        [SerializeReference]
        List<IRenderPipelineGraphicsSettings> m_Settings =
            new List<IRenderPipelineGraphicsSettings>();

        public List<IRenderPipelineGraphicsSettings> settingsList
        {
            get
            {
                if (m_Settings == null)
                    m_Settings = new List<IRenderPipelineGraphicsSettings>();
                return m_Settings;
            }
        }
    }
}

namespace UnityEngine.Rendering.Categorization
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class CategoryInfoAttribute : Attribute
    {
        public string Name { get; set; }
        public int Order { get; set; }
        public CategoryInfoAttribute() { }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class ElementInfoAttribute : Attribute
    {
        public int Order { get; set; }
        public ElementInfoAttribute() { }
    }
}
#else
// Keep the call sites version-neutral when the package is opened by Unity 6.
// On that editor the engine owns the full graphics-settings element API, so
// this facade simply forwards to it.
namespace UnityEngine.Rendering
{
    public static class RenderPipelineGraphicsSettingsCompatibility
    {
        public static Type currentRenderPipelineAssetType => GraphicsSettings.currentRenderPipelineAssetType;

        public static T GetRenderPipelineSettings<T>() where T : class
            => GraphicsSettings.GetRenderPipelineSettings<T>();

        public static bool TryGetRenderPipelineSettings<T>(out T settings) where T : class
        {
            settings = GetRenderPipelineSettings<T>();
            return settings != null;
        }
    }
}
#endif
