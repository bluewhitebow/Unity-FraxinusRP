using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityEngine.Rendering
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class ProbeReferenceVolumeBackendAttribute : Attribute
    {
    }

    internal interface IProbeReferenceVolumeBackend
    {
        string id { get; }
        int priority { get; }
        TProbeReferenceVolumeBase volume { get; }
        IProbeVolumeBakingDataFormat bakingDataFormat { get; }

        bool CanUse(in ProbeVolumeSystemParameters parameters, out string reason);
        void Initialize(in ProbeVolumeSystemParameters parameters);
        void Cleanup();
    }

    internal sealed class ProbeReferenceVolumeRuntimeContext
    {
        internal IProbeReferenceVolumeBackend backend { get; }
        internal TProbeReferenceVolumeBase volume => backend.volume;
        internal ProbeVolumeSystemParameters parameters { get; }

        internal ProbeReferenceVolumeRuntimeContext(IProbeReferenceVolumeBackend backend, in ProbeVolumeSystemParameters parameters)
        {
            this.backend = backend;
            this.parameters = parameters;
        }
    }

    internal static class ProbeReferenceVolumeProvider
    {
        const string k_defaultBackendId = "APV";

        static readonly List<IProbeReferenceVolumeBackend> s_backends = new();
        // Registered implementations retain independent baked data, but only
        // the selected backend is initialized for rendering.
        static readonly List<IProbeReferenceVolumeBackend> s_initializedBackends = new();
        static ProbeReferenceVolumeRuntimeContext s_context;
        static bool s_backendsDiscovered;

        // A null context means no backend is rendering. Consumers must not use
        // activeBackend's editor fallback when claiming backend-owned state.
        internal static event Action runtimeBackendChanged;

        internal static ProbeReferenceVolumeRuntimeContext context => s_context;
        internal static IProbeReferenceVolumeBackend activeBackend
        {
            get
            {
                EnsureBackendsDiscovered();
                return s_context?.backend ?? FindBackend(k_defaultBackendId);
            }
        }

        internal static TProbeReferenceVolumeBase instance
        {
            get
            {
                EnsureBackendsDiscovered();
                return s_context?.volume ?? FindBackend(k_defaultBackendId)?.volume ?? APVProbeReferenceVolume.instance;
            }
        }
        internal static IReadOnlyList<IProbeReferenceVolumeBackend> backends
        {
            get
            {
                EnsureBackendsDiscovered();
                return s_backends;
            }
        }

        /// <summary>
        /// Enumerates the volumes exposed by all registered backends. The active
        /// volume remains available through <see cref="instance"/> for APV callers.
        /// </summary>
        internal static IEnumerable<TProbeReferenceVolumeBase> EnumerateVolumes()
        {
            EnsureBackendsDiscovered();

            var seen = new HashSet<TProbeReferenceVolumeBase>();
            foreach (var backend in s_backends)
            {
                if (backend?.volume != null && seen.Add(backend.volume))
                    yield return backend.volume;
            }
        }

        /// <summary>
        /// Enumerates only backends that were initialized by the provider. This
        /// deliberately does not trigger reflection discovery and is used by
        /// SRP shutdown/disabled paths where probing for optional backends would
        /// otherwise allocate or initialize them.
        /// </summary>
        internal static IEnumerable<TProbeReferenceVolumeBase> EnumerateInitializedVolumes()
        {
            var seen = new HashSet<TProbeReferenceVolumeBase>();
            foreach (var backend in s_initializedBackends)
            {
                if (backend?.volume != null && seen.Add(backend.volume))
                    yield return backend.volume;
            }
        }

        /// <summary>
        /// Invokes an action for every registered backend. Scene components use this
        /// instead of <see cref="instance"/> so secondary lighting backends receive
        /// the same lifecycle notifications as APV.
        /// </summary>
        internal static void ForEachBackend(Action<IProbeReferenceVolumeBackend> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            EnsureBackendsDiscovered();
            foreach (var backend in s_backends)
                action(backend);
        }

        internal static void RegisterBackend(IProbeReferenceVolumeBackend backend)
        {
            if (backend == null)
                throw new ArgumentNullException(nameof(backend));

            if (FindBackend(backend.id) != null)
                throw new InvalidOperationException($"Probe Reference Volume backend '{backend.id}' is already registered.");

            s_backends.Add(backend);
            s_backends.Sort((a, b) => b.priority.CompareTo(a.priority));
        }

        internal static ProbeReferenceVolumeRuntimeContext Initialize(in ProbeVolumeSystemParameters parameters, string preferredBackendId = null)
        {
            EnsureBackendsDiscovered();

            if (s_context != null || s_initializedBackends.Count != 0)
                Cleanup();

            var backend = SelectBackend(parameters, preferredBackendId);
            if (backend == null)
            {
                Debug.LogError($"No compatible Probe Reference Volume backend found.{BuildSelectionFailureMessage(parameters)}");
                return null;
            }

            var primaryContext = new ProbeReferenceVolumeRuntimeContext(backend, parameters);
            s_context = primaryContext;
            try
            {
                InitializeBackend(backend, parameters);

                s_context = primaryContext;
                runtimeBackendChanged?.Invoke();
                return primaryContext;
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        internal static void Cleanup()
        {
            // Keep backend state consistent across an APV -> legacy-pipeline
            // switch. The backend cleanup releases runtime
            // resources but does not reset m_EnabledBySRP itself.
            try
            {
                // Cleanup in reverse initialization order. Keep the context aligned
                // with the backend being cleaned because backend cleanup can consult
                // Provider.instance during teardown.
                if (s_initializedBackends.Count == 0 && s_context?.backend != null)
                {
                    // Preserve the original single-backend behavior for callers that
                    // provide a context directly (for example, editor tests).
                    var backend = s_context.backend;
                    backend.volume?.SetEnableStateFromSRP(false);
                    backend.Cleanup();
                }

                for (int i = s_initializedBackends.Count - 1; i >= 0; --i)
                {
                    var backend = s_initializedBackends[i];
                    s_context = new ProbeReferenceVolumeRuntimeContext(backend, s_context?.parameters ?? default);
                    backend.volume?.SetEnableStateFromSRP(false);
                    backend.Cleanup();
                }
            }
            finally
            {
                s_initializedBackends.Clear();
                s_context = null;
                runtimeBackendChanged?.Invoke();
            }
        }

        static void InitializeBackend(IProbeReferenceVolumeBackend backend, in ProbeVolumeSystemParameters parameters)
        {
            // Add before calling Initialize so a partially initialized backend is also
            // cleaned up if it throws, matching the existing single-backend behavior.
            s_initializedBackends.Add(backend);
            backend.Initialize(parameters);
        }

        internal static bool TryGetBackend(string id, out IProbeReferenceVolumeBackend backend)
        {
            EnsureBackendsDiscovered();
            backend = FindBackend(id);
            return backend != null;
        }

        static IProbeReferenceVolumeBackend SelectBackend(in ProbeVolumeSystemParameters parameters, string preferredBackendId)
        {
            if (!string.IsNullOrEmpty(preferredBackendId))
            {
                var preferred = FindBackend(preferredBackendId);
                if (preferred == null)
                {
                    Debug.LogError($"Probe Reference Volume backend '{preferredBackendId}' is not registered.");
                    return null;
                }

                if (preferred.CanUse(parameters, out var reason))
                    return preferred;

                Debug.LogError($"Probe Reference Volume backend '{preferredBackendId}' cannot be used. {reason}");
                return null;
            }

            foreach (var backend in s_backends)
            {
                if (backend.CanUse(parameters, out _))
                    return backend;
            }

            return null;
        }

        static string BuildSelectionFailureMessage(in ProbeVolumeSystemParameters parameters)
        {
            if (s_backends.Count == 0)
                return string.Empty;

            var message = string.Empty;
            foreach (var backend in s_backends)
            {
                if (!backend.CanUse(parameters, out var reason))
                    message += $"\n- {backend.id}: {reason}";
            }

            return message;
        }

        static IProbeReferenceVolumeBackend FindBackend(string id)
        {
            foreach (var backend in s_backends)
            {
                if (backend.id == id)
                    return backend;
            }

            return null;
        }

        static void EnsureBackendsDiscovered()
        {
            if (s_backendsDiscovered)
                return;

            s_backendsDiscovered = true;

            // The built-in backend must also be available in stripped players.
            // Reflection discovery remains available for additional backends.
            if (FindBackend(k_defaultBackendId) == null)
                RegisterBackend(new APVProbeReferenceVolumeBackend());
            if (FindBackend(APRTProbeReferenceVolumeBackend.backendId) == null)
                RegisterBackend(new APRTProbeReferenceVolumeBackend());

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                DiscoverBackends(assembly);
        }

        static void DiscoverBackends(Assembly assembly)
        {
            if (assembly.IsDynamic)
                return;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }

            foreach (var type in types)
            {
                if (type == null ||
                    type.IsAbstract ||
                    !typeof(IProbeReferenceVolumeBackend).IsAssignableFrom(type) ||
                    type.GetCustomAttribute<ProbeReferenceVolumeBackendAttribute>() == null)
                {
                    continue;
                }

                try
                {
                    var backend = (IProbeReferenceVolumeBackend)Activator.CreateInstance(type, nonPublic: true);
                    if (FindBackend(backend.id) == null)
                        RegisterBackend(backend);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to register Probe Reference Volume backend '{type.FullName}'. {e.Message}");
                }
            }
        }
    }
}
