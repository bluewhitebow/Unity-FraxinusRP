#if !UNITY_6000_0_OR_NEWER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Scripting.APIUpdating;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// Supplies rendering-layer names to the Unity 2022 compatibility facade
    /// without making RPCore depend on a concrete render pipeline assembly.
    /// </summary>
    internal interface IRenderingLayerNamesProvider
    {
        string[] renderingLayerMaskNames { get; }
    }

    /// <summary>
    /// Unity 6-compatible strongly typed render-pipeline asset base for Unity
    /// 2022.3. Unity 6 moved pipeline type metadata into this generic engine
    /// type; 2022 only exposes the non-generic <see cref="RenderPipelineAsset"/>
    /// base. The shim keeps the same metadata while delegating pipeline
    /// creation to the asset's existing <see cref="RenderPipelineAsset.CreatePipeline"/>
    /// override.
    /// </summary>
    /// <typeparam name="TRenderPipeline">The pipeline instance produced by this asset.</typeparam>
    public abstract class RenderPipelineAsset<TRenderPipeline> : RenderPipelineAsset
        where TRenderPipeline : RenderPipeline
    {
        /// <summary>Returns the strongly typed render-pipeline instance type.</summary>
        // The 2022 non-generic base has no pipelineType member. Hide it with
        // the same fixed value that Unity 6's generic base exposes as a sealed
        // override, so a typed asset can never report a different pipeline.
        public Type pipelineType => typeof(TRenderPipeline);

        /// <summary>Returns the default shader tag for the strongly typed pipeline.</summary>
        public override string renderPipelineShaderTag => typeof(TRenderPipeline).Name;

        // Unity 6 exposes this as a protected-internal property used by SRP
        // internals. Unity 2022 has no corresponding engine member, so expose
        // the same metadata as a hidden compatibility property.
        protected internal Type renderPipelineType => typeof(TRenderPipeline);

        // Unity 6's non-public base helper is used by editor/native code when
        // it needs a stable serialized pipeline type name.  Unity 2022 does
        // not declare the helper, but exposing the same internal member keeps
        // reflection-based callers and package code version-neutral.
        internal string pipelineTypeFullName => pipelineType?.FullName ?? string.Empty;

        /// <summary>
        /// Returns the default SpeedTree 9 shader used by this render pipeline.
        /// </summary>
        /// <remarks>
        /// Unity added this virtual member to the non-generic
        /// <see cref="RenderPipelineAsset"/> base in Unity 6. Unity 2022 cannot
        /// be extended in-place, so the generic compatibility base carries the
        /// member and lets Unity 6 pipeline assets retain their original
        /// <c>override</c> declaration.
        /// </remarks>
        public virtual Shader defaultSpeedTree9Shader => null;

        /// <summary>
        /// Ensures that global settings required by this render pipeline are
        /// available before the pipeline instance is created.
        /// </summary>
        /// <remarks>
        /// Unity 6 calls this hook from the engine's pipeline creation path.
        /// Unity 2022 has no corresponding engine callback, so compatibility
        /// assets call it explicitly before <see cref="RenderPipelineAsset.CreatePipeline"/>.
        /// </remarks>
        protected virtual void EnsureGlobalSettings()
        {
        }
    }

}

// Unity 6 exposes RenderingLayerMask in the UnityEngine namespace (rather
// than UnityEngine.Rendering).  Keep the type in the canonical namespace so
// assemblies compiled against Unity 6 retain the same metadata and so nested
// UnityEngine.Rendering.* namespaces resolve it through normal parent lookup.
namespace UnityEngine
{
    /// <summary>
    /// Unity 6-compatible rendering-layer mask for Unity 2022.3.
    ///
    /// Unity 2022 does not expose the engine's managed RenderingLayerMask
    /// type.  The registry-backed methods below bridge to a pipeline asset or
    /// global-settings asset when one is available, and retain Unity's
    /// conventional single "Default" layer when no SRP has registered names.
    /// </summary>
    [Serializable]
    [MovedFrom(true, "UnityEngine.Rendering", "Unity.RenderPipelines.Core.Runtime")]
    public struct RenderingLayerMask : IEquatable<RenderingLayerMask>
    {
        // Keep Unity 6's serialized field name.  FormerlySerializedAs allows
        // assets written by the earlier local shim (which used m_Mask) to
        // migrate without losing the bit pattern.
        [SerializeField]
        [FormerlySerializedAs("m_Mask")]
        uint m_Bits;

        /// <summary>Maximum number of rendering-layer bits supported.</summary>
        internal const int maxRenderingLayerSize = 32;

        // Preserve the constructor that was present in the first 2022 shim;
        // Unity 6 callers normally use the implicit uint conversion instead.
        public RenderingLayerMask(uint mask) => m_Bits = mask;

        // Unity 6 initializes this value once from the engine's tag manager.
        // Use an auto-property so the managed metadata has the same backing
        // field (`<defaultRenderingLayerMask>k__BackingField`) as Unity 6,
        // while retaining a safe managed fallback on Unity 2022.
        /// <summary>The project's default rendering-layer mask.</summary>
        public static RenderingLayerMask defaultRenderingLayerMask { get; }
            = new RenderingLayerMask(Internal_GetDefaultRenderingLayerValue());

        /// <summary>Gets or sets the raw 32-bit mask value.</summary>
        public uint value
        {
            get => m_Bits;
            set => m_Bits = value;
        }

        public static implicit operator uint(RenderingLayerMask value) => value.m_Bits;
        public static implicit operator RenderingLayerMask(uint value) => new RenderingLayerMask(value);
        public static implicit operator int(RenderingLayerMask value) => unchecked((int)value.m_Bits);
        public static implicit operator RenderingLayerMask(int value) => new RenderingLayerMask(unchecked((uint)value));

        public bool Equals(RenderingLayerMask other) => m_Bits == other.m_Bits;
        public override bool Equals(object obj) => obj is RenderingLayerMask other && Equals(other);
        public override int GetHashCode() => unchecked((int)m_Bits);
        public static bool operator ==(RenderingLayerMask a, RenderingLayerMask b) => a.m_Bits == b.m_Bits;
        public static bool operator !=(RenderingLayerMask a, RenderingLayerMask b) => a.m_Bits != b.m_Bits;

        /// <summary>
        /// Registers the active pipeline's direct rendering-layer provider.
        /// Custom pipelines which do not register one continue to use the
        /// reflection fallback below.
        /// </summary>
        internal static void SetNamesProvider(UnityEngine.Rendering.IRenderingLayerNamesProvider provider)
            => RenderingLayerRegistry.SetProvider(provider);

        /// <summary>Returns the display name for a rendering-layer index.</summary>
        public static string RenderingLayerToName(int index)
        {
            var names = RenderingLayerRegistry.GetNames();
            return index >= 0 && index < Math.Min(names.Length, maxRenderingLayerSize)
                ? names[index] ?? string.Empty
                : string.Empty;
        }

        /// <summary>Returns the number of currently defined layer names.</summary>
        public static int GetDefinedRenderingLayerCount()
        {
            var names = RenderingLayerRegistry.GetNames();
            return GetDefinedRenderingLayerCount(names);
        }

        /// <summary>Returns the OR-combined mask of all defined layers.</summary>
        public static uint GetDefinedRenderingLayersCombinedMaskValue()
        {
            var names = RenderingLayerRegistry.GetNames();
            uint mask = 0u;
            int count = Math.Min(names.Length, maxRenderingLayerSize);
            for (int i = 0; i < count; ++i)
            {
                if (!string.IsNullOrEmpty(names[i]))
                    mask |= 1u << i;
            }

            return mask;
        }

        /// <summary>Returns all non-empty rendering-layer names in index order.</summary>
        public static string[] GetDefinedRenderingLayerNames()
        {
            var names = RenderingLayerRegistry.GetNames();
            int count = GetDefinedRenderingLayerCount(names);
            var result = new string[count];
            int outputIndex = 0;
            int limit = Math.Min(names.Length, maxRenderingLayerSize);
            for (int i = 0; i < limit; ++i)
            {
                if (!string.IsNullOrEmpty(names[i]))
                    result[outputIndex++] = names[i];
            }

            return result;
        }

        /// <summary>Returns the bit value for each defined rendering-layer name.</summary>
        public static int[] GetDefinedRenderingLayerValues()
        {
            var names = RenderingLayerRegistry.GetNames();
            int count = GetDefinedRenderingLayerCount(names);
            var result = new int[count];
            int outputIndex = 0;
            int limit = Math.Min(names.Length, maxRenderingLayerSize);
            for (int i = 0; i < limit; ++i)
            {
                if (!string.IsNullOrEmpty(names[i]))
                    result[outputIndex++] = unchecked((int)(1u << i));
            }

            // Names beyond the 32-bit mask cannot occur in a valid registry,
            // but keep the returned array internally consistent if a malformed
            // custom settings asset supplies one.
            if (outputIndex != result.Length)
                Array.Resize(ref result, outputIndex);
            return result;
        }

        /// <summary>Returns the number of configured rendering-layer slots.</summary>
        public static int GetRenderingLayerCount()
            => Math.Min(RenderingLayerRegistry.GetNames().Length, maxRenderingLayerSize);

        static int GetDefinedRenderingLayerCount(string[] names)
        {
            int count = 0;
            int limit = Math.Min(names.Length, maxRenderingLayerSize);
            for (int i = 0; i < limit; ++i)
            {
                if (!string.IsNullOrEmpty(names[i]))
                    ++count;
            }

            return count;
        }

        // Unity 6 keeps this helper private and native-backed.  The method is
        // private in the 2022 facade, matching the Unity 6 managed surface;
        // reflection-based compatibility checks can still resolve the same
        // member without exposing another public API surface.
        static uint Internal_GetDefaultRenderingLayerValue()
        {
            try
            {
                return UnityEngine.Rendering.GraphicsSettings.defaultRenderingLayerMask;
            }
            catch
            {
                // Unity 2022 always has the property, but retain a safe value
                // for stripped/headless test harnesses.
                return 1u;
            }
        }

        /// <summary>
        /// Reflection-only bridge to SRP-owned rendering-layer names.  RPCore
        /// cannot reference UltraRP's assembly directly (the dependency points the
        /// other way), so this intentionally avoids a compile-time cycle.
        /// </summary>
        static class RenderingLayerRegistry
        {
            const BindingFlags k_StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags k_InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            static readonly string[] k_FallbackNames = { "Default" };

            // Reflection is unavoidable here because RPCore must not take a
            // compile-time dependency on Fraxinus.  Keep the expensive
            // metadata lookups out of the rendering hot path, and retain a
            // copy of the resolved names so callers do not allocate an array
            // on every mask/count query.  The source values are compared when
            // read, which also notices editor-side in-place edits to the
            // serialized names array (the common Unity 2022 compatibility
            // path).
            static readonly Dictionary<Type, PropertyInfo> s_NamesProperties =
                new Dictionary<Type, PropertyInfo>();
            static UnityEngine.Rendering.IRenderingLayerNamesProvider s_NamesProvider;
            static bool s_UltraTypeResolved;
            static Type s_UltraGlobalSettingsType;
            static PropertyInfo s_UltraInstanceProperty;
            static object s_CachedNamesOwner;
            static PropertyInfo s_CachedNamesProperty;
            static string[] s_CachedNames;

            [ThreadStatic]
            static bool s_Resolving;

            public static void SetProvider(UnityEngine.Rendering.IRenderingLayerNamesProvider provider)
            {
                // UnityEngine.Object overloads null after destruction. Do not
                // retain a dead ScriptableObject behind its interface type.
                if (provider is UnityEngine.Object unityObject && unityObject == null)
                    provider = null;

                s_NamesProvider = provider;
            }

            static bool TryGetProviderNames(out string[] names)
            {
                names = null;
                var provider = s_NamesProvider;
                if (provider == null)
                    return false;

                if (provider is UnityEngine.Object unityObject && unityObject == null)
                {
                    s_NamesProvider = null;
                    return false;
                }

                try
                {
                    names = provider.renderingLayerMaskNames;
                    if (names == null || names.Length == 0)
                        names = k_FallbackNames;
                    return true;
                }
                catch
                {
                    // A custom provider can be temporarily unavailable while
                    // its asset is migrating. Fall through to the legacy
                    // reflection path without allowing it to break rendering.
                    names = null;
                    return false;
                }
            }

            public static string[] GetNames()
            {
                // Rendering-layer queries can occur while a pipeline asset is
                // itself being initialized.  Avoid re-entering an asset's
                // settings getter and return the stable fallback for that
                // narrow window instead.
                if (s_Resolving)
                    return k_FallbackNames;

                s_Resolving = true;
                string[] names;
                try
                {
                    // UltraRP and other cooperating pipelines register this direct
                    // provider through the graphics-settings bridge. Returning
                    // its serialized array is allocation-free and observes
                    // in-place editor changes immediately. Every public mask
                    // operation clamps the usable range to 32 entries.
                    if (TryGetProviderNames(out names))
                        return names;

                    // UltraRP 2022 stores the names on an internal global-settings
                    // type; reflection keeps Core independent from
                    // Fraxinus.  Prefer this direct source so querying
                    // the mask does not force pipeline creation.
                    if (TryGetUltraGlobalSettings(out var settings) && TryGetNames(settings, out names))
                        return names;

                    // The asset override is also available while a pipeline
                    // is active or while the default asset is inspected in the
                    // editor.  Try explicit/default assets before the current
                    // pipeline because the latter may lazily create a SRP.
                    if (TryGetNames(UnityEngine.Rendering.GraphicsSettings.renderPipelineAsset, out names))
                        return names;
                    if (TryGetNames(UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline, out names))
                        return names;
                    if (TryGetNames(UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline, out names))
                        return names;

                    // The fallback array is private to this registry and is
                    // never exposed directly by the public helpers below, so
                    // avoid creating a short-lived clone on every query.
                    return k_FallbackNames;
                }
                finally
                {
                    s_Resolving = false;
                }
            }

            static bool TryGetNames(object owner, out string[] names)
            {
                names = null;
                if (owner == null || (owner is UnityEngine.Object unityObject && unityObject == null))
                    return false;

                try
                {
                    var ownerType = owner.GetType();
                    if (!s_NamesProperties.TryGetValue(ownerType, out var property))
                    {
                        property = ownerType.GetProperty("renderingLayerMaskNames", k_InstanceFlags);
                        if (property != null && property.GetIndexParameters().Length != 0)
                            property = null;
                        // Dictionary entries with a null value deliberately
                        // remember that this owner type has no compatible
                        // property, avoiding repeated GetProperty calls for
                        // custom SRPs which do not implement the legacy API.
                        s_NamesProperties.Add(ownerType, property);
                    }

                    if (property == null || property.GetIndexParameters().Length != 0)
                        return false;

                    var value = property.GetValue(owner, null);
                    if (value is string[] stringArray && stringArray.Length > 0)
                    {
                        int length = Math.Min(stringArray.Length, maxRenderingLayerSize);
                        if (MatchesCachedNames(owner, property, stringArray, length))
                        {
                            names = s_CachedNames;
                            return true;
                        }

                        names = new string[length];
                        Array.Copy(stringArray, names, length);
                        CacheNames(owner, property, names);
                        return true;
                    }

                    // A few custom SRPs expose an IList<string> instead of an
                    // array.  Handle that shape without taking a generic
                    // collection dependency in the public API.
                    if (value is IList list && list.Count > 0)
                    {
                        int length = Math.Min(list.Count, maxRenderingLayerSize);
                        if (MatchesCachedNames(owner, property, list, length))
                        {
                            names = s_CachedNames;
                            return true;
                        }

                        names = new string[length];
                        for (int i = 0; i < length; ++i)
                            names[i] = list[i] as string;
                        CacheNames(owner, property, names);
                        return true;
                    }
                }
                catch
                {
                    // A custom asset may throw while its settings are being
                    // initialized.  Falling back keeps static API queries
                    // side-effect free and mirrors Unity's empty-name behavior.
                }

                return false;
            }

            static bool MatchesCachedNames(object owner, PropertyInfo property, IList source, int length)
            {
                if (!ReferenceEquals(owner, s_CachedNamesOwner) ||
                    !ReferenceEquals(property, s_CachedNamesProperty) ||
                    s_CachedNames == null ||
                    s_CachedNames.Length != length)
                    return false;

                // Do not rely solely on the source array identity.  The
                // Unity 2022 compatibility asset edits its names in place,
                // so an identity-only cache would display stale layer names
                // after a rename.  There are at most 32 entries, making this
                // bounded comparison substantially cheaper than reflection,
                // allocation, and Array.Copy on every call.
                for (int i = 0; i < length; ++i)
                {
                    var sourceName = source[i] as string;
                    if (!string.Equals(s_CachedNames[i], sourceName, StringComparison.Ordinal))
                        return false;
                }

                return true;
            }

            static void CacheNames(object owner, PropertyInfo property, string[] names)
            {
                s_CachedNamesOwner = owner;
                s_CachedNamesProperty = property;
                s_CachedNames = names;
            }

            static bool TryGetUltraGlobalSettings(out object settings)
            {
                settings = null;
                try
                {
                    // Resolve the UltraRP type/property once.  The old code
                    // searched every loaded assembly and called GetProperty
                    // for every rendering-layer query, which is particularly
                    // expensive in the editor.  If UltraRP has not loaded yet,
                    // leave the flag clear so a later domain/package load can
                    // still be discovered.
                    if (!s_UltraTypeResolved)
                    {
                        var type = Type.GetType(
                            "FraxinusEngine.Rendering.Ultra.UltraRenderPipelineGlobalSettings, Fraxinus.Runtime");
                        if (type == null)
                        {
                            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                            for (int i = 0; i < assemblies.Length && type == null; ++i)
                                type = assemblies[i].GetType("FraxinusEngine.Rendering.Ultra.UltraRenderPipelineGlobalSettings", false);
                        }

                        if (type != null)
                        {
                            s_UltraGlobalSettingsType = type;
                            s_UltraInstanceProperty = type.GetProperty("instance", k_StaticFlags);
                            s_UltraTypeResolved = true;
                        }
                    }

                    if (!s_UltraTypeResolved || s_UltraGlobalSettingsType == null ||
                        s_UltraInstanceProperty == null)
                        return false;

                    settings = s_UltraInstanceProperty.GetValue(null, null);
                    return settings != null;
                }
                catch
                {
                    settings = null;
                    return false;
                }
            }
        }
    }
}

namespace UnityEngine.Experimental.Rendering
{
    /// <summary>
    /// Describes one or more capabilities required from a graphics format.
    /// Unity 6 replaced the single-value <see cref="FormatUsage"/> query with
    /// this flags-based contract; the values intentionally match Unity 6's
    /// engine metadata.
    /// </summary>
    [Flags]
    public enum GraphicsFormatUsage
    {
        None = 0,
        Sample = 1 << 0,
        Linear = 1 << 1,
        Sparse = 1 << 2,
        Render = 1 << 4,
        Blend = 1 << 5,
        GetPixels = 1 << 6,
        SetPixels = 1 << 7,
        SetPixels32 = 1 << 8,
        ReadPixels = 1 << 9,
        LoadStore = 1 << 10,
        MSAA2x = 1 << 11,
        MSAA4x = 1 << 12,
        MSAA8x = 1 << 13,
        StencilSampling = 1 << 16,
    }
}
#endif
