using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    public static class GPUInstancerDriver
    {
        private enum Lifecycle
        {
            Stopped,
            Running,
            ShuttingDown
        }

        private static readonly Dictionary<string, GPUInstancerManager> s_managers = new Dictionary<string, GPUInstancerManager>(StringComparer.Ordinal);

        private static Lifecycle s_lifecycle;
        private static GPUInstancerRuntimeConfig s_runtimeConfig;

        public static bool isBootstrapped => s_lifecycle == Lifecycle.Running;
        public static int ownedManagerCount => s_managers.Count;

        public static void Bootstrap()
        {
            GPUInstancerProjectSettings settings = GPUInstancerConstants.projectSettings;
            BootstrapInternal(settings == null ? null : settings.runtimeConfig);
        }

        public static void Bootstrap(GPUInstancerRuntimeConfig config)
        {
            if (config == null)
            {
                Shutdown();
                return;
            }

            BootstrapInternal(config);
        }

        private static void BootstrapInternal(GPUInstancerRuntimeConfig config)
        {
            if (s_lifecycle == Lifecycle.ShuttingDown)
                return;
            if (config == null)
            {
                Shutdown();
                return;
            }

            s_runtimeConfig = config;
            Reconcile(config);

            if (config.profiles != null)
            {
                for (int i = 0; i < config.profiles.Count; i++)
                {
                    GPUInstancerProfile profile = config.profiles[i];
                    if (profile == null || !profile.enabled || IsTerrainProfile(profile))
                        continue;
                    GetOrCreate(profile);
                }
            }

            s_lifecycle = Lifecycle.Running;
        }

        public static GPUInstancerManager GetOrCreate(GPUInstancerProfile profile)
        {
            if (profile == null || s_lifecycle == Lifecycle.ShuttingDown)
                return null;

            if (string.IsNullOrWhiteSpace(profile.id))
                throw new InvalidOperationException("GPU Instancer profile has no ID.");

            if (!profile.enabled)
            {
                Remove(profile);
                return null;
            }

            if (s_managers.TryGetValue(profile.id, out GPUInstancerManager manager))
            {
                if (!IsAlive(manager))
                    s_managers.Remove(profile.id);
                else
                {
                    if (!ReferenceEquals(manager.profile, profile))
                        manager.SetProfile(profile);

                    if (!GPUInstancerManagerHub.IsRegistered(manager))
                        GPUInstancerManagerHub.Register(manager);

                    return manager;
                }
            }

            manager = profile.CreateManager();
            if (!IsAlive(manager))
                throw new InvalidOperationException("Profile did not create a live Manager: " + profile.displayName);

            if (!ReferenceEquals(manager.profile, profile))
                manager.SetProfile(profile);

            GPUInstancerManagerHub.Register(manager);
            s_managers.Add(profile.id, manager);
            return manager;
        }

        public static TManager GetOrCreate<TManager>(GPUInstancerProfile profile) where TManager : GPUInstancerManager
        {
            return GetOrCreate(profile) as TManager;
        }

        public static GPUInstancerManager GetOrCreate(string profileId)
        {
            return GetOrCreate(FindProfile(profileId));
        }

        public static bool TryGet(GPUInstancerProfile profile, out GPUInstancerManager manager)
        {
            if (profile == null || !profile.enabled || string.IsNullOrEmpty(profile.id))
            {
                manager = null;
                return false;
            }

            if (!TryGet(profile.id, out manager))
                return false;

            if (!ReferenceEquals(manager.profile, profile))
                manager.SetProfile(profile);

            return true;
        }

        public static bool TryGet<TManager>(GPUInstancerProfile profile, out TManager manager) where TManager : GPUInstancerManager
        {
            bool found = TryGet(profile, out GPUInstancerManager raw);
            manager = raw as TManager;
            return found && manager != null;
        }

        public static bool TryGet(string profileId, out GPUInstancerManager manager)
        {
            if (string.IsNullOrEmpty(profileId) || !s_managers.TryGetValue(profileId, out manager))
            {
                manager = null;
                return false;
            }

            if (IsAlive(manager))
                return true;

            s_managers.Remove(profileId);
            manager = null;
            return false;
        }

        public static GPUInstancerProfile FindProfile(string profileId)
        {
            GPUInstancerRuntimeConfig config = ResolveConfig();
            return config == null ? null : config.FindProfile(profileId);
        }

        internal static bool IsProfilePrototypeSource(GPUInstancerPrototype prototype)
        {
            if (prototype == null)
                return false;

            GPUInstancerRuntimeConfig config = ResolveConfig();
            if (config == null || config.profiles == null)
                return false;

            for (int i = 0; i < config.profiles.Count; i++)
            {
                List<GPUInstancerPrototype> prototypes = config.profiles[i]?.prototypeList;
                if (prototypes != null && prototypes.Contains(prototype))
                    return true;
            }

            return false;
        }

        public static bool Remove(GPUInstancerProfile profile)
        {
            if (profile == null || string.IsNullOrEmpty(profile.id) || !s_managers.TryGetValue(profile.id, out GPUInstancerManager manager))
                return false;

            s_managers.Remove(profile.id);
            DisposeManager(manager);
            return true;
        }

        public static void Shutdown()
        {
            if (s_lifecycle == Lifecycle.ShuttingDown)
                return;

            s_lifecycle = Lifecycle.ShuttingDown;
            GPUInstancerManager[] managers = SnapshotManagers();
            s_managers.Clear();

            for (int i = 0; i < managers.Length; i++)
                DisposeManager(managers[i]);

            GPUInstancerPrefabRuntimeHandler.ClearManagerDictionary();
            ClearState();
        }

        internal static void ResetStatics()
        {
            Shutdown();
        }

        private static GPUInstancerRuntimeConfig ResolveConfig()
        {
            if (s_runtimeConfig != null)
                return s_runtimeConfig;

            GPUInstancerProjectSettings settings = GPUInstancerConstants.projectSettings;
            return settings == null ? null : settings.runtimeConfig;
        }

        private static void Reconcile(GPUInstancerRuntimeConfig config)
        {
            if (s_managers.Count == 0)
                return;

            string[] ids = new string[s_managers.Count];
            s_managers.Keys.CopyTo(ids, 0);
            for (int i = 0; i < ids.Length; i++)
            {
                GPUInstancerProfile profile = config.FindProfile(ids[i]);
                if (profile == null || !profile.enabled)
                {
                    GPUInstancerManager removed = s_managers[ids[i]];
                    s_managers.Remove(ids[i]);
                    DisposeManager(removed);
                    continue;
                }

                GPUInstancerManager manager = s_managers[ids[i]];
                if (!IsAlive(manager))
                {
                    s_managers.Remove(ids[i]);
                    continue;
                }

                if (!ReferenceEquals(manager.profile, profile))
                    manager.SetProfile(profile);
            }
        }

        private static GPUInstancerManager[] SnapshotManagers()
        {
            GPUInstancerManager[] managers = new GPUInstancerManager[s_managers.Count];
            s_managers.Values.CopyTo(managers, 0);
            return managers;
        }

        private static void DisposeManager(GPUInstancerManager manager)
        {
            if (manager == null)
                return;

            GPUInstancerLoopScheduler.CancelCoroutines(manager);
            try
            {
                GPUInstancerManagerHub.Unregister(manager);
            }
            finally
            {
                manager.Dispose();
            }
        }

        private static void ClearState()
        {
            s_managers.Clear();
            s_runtimeConfig = null;
            s_lifecycle = Lifecycle.Stopped;
        }

        private static bool IsTerrainProfile(GPUInstancerProfile profile)
        {
            return profile is GPUInstancerTreeProfile || profile is GPUInstancerDetailProfile;
        }

        private static bool IsAlive(GPUInstancerManager manager)
        {
            return manager != null && !manager.isDisposed;
        }
    }
}
