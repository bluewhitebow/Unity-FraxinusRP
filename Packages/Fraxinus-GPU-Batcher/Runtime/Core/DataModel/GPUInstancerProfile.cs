using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    public interface IGPUInstancerProfile
    {
        bool enabled { get; }
        string displayName { get; }
        GPUInstancerManager CreateManager();
    }

    public interface IGPUInstancerProfile<TMgr> : IGPUInstancerProfile  where TMgr : GPUInstancerManager
    {
        new TMgr CreateManager();
    }

    public interface IGPUInstancerProfileHost
    {
        GPUInstancerProfile profile { get; }

        void SetProfile(GPUInstancerProfile profile);
    }

    [Serializable]
    public abstract class GPUInstancerProfile : IGPUInstancerProfile
    {
        [SerializeField]
        public string id;

        [SerializeField]
        private bool _enabled = true;

        [SerializeField]
        private string _displayName;

        [SerializeField]
        public List<GPUInstancerPrototype> prototypeList = new List<GPUInstancerPrototype>();

        [SerializeField]
        public bool autoSelectCamera = true;

        [SerializeField]
        public bool isFrustumCulling = true;

        [SerializeField]
        public bool isOcclusionCulling = true;

        [SerializeField]
        public float minCullingDistance = 0f;

        [SerializeField]
        public bool useFloatingOriginHandler = false;

        [SerializeField]
        public bool applyFloatingOriginRotationAndScale = false;

        [SerializeField]
        public LayerMask layerMask = ~0;

        [SerializeField]
        public bool lightProbeDisabled = false;

        protected GPUInstancerProfile()
        {
            InitializeAuthoringData();
        }

        public bool enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public virtual string displayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_displayName))
                    return _displayName;

                return GetDefaultDisplayName();
            }
            set => _displayName = value;
        }

        protected virtual string GetDefaultDisplayName()
        {
            string typeName = GetType().Name;
            const string suffix = "Profile";
            if (typeName.EndsWith(suffix, StringComparison.Ordinal))
                typeName = typeName.Substring(0, typeName.Length - suffix.Length);

            return typeName;
        }

        public void InitializeAuthoringData()
        {
            if (string.IsNullOrWhiteSpace(id))
                id = Guid.NewGuid().ToString("N");

            if (prototypeList == null)
                prototypeList = new List<GPUInstancerPrototype>();
        }

        public virtual GPUInstancerManager CreateManager()
        {
            return CreateManagerCore();
        }

        protected virtual GPUInstancerManager CreateManagerCore()
        {
            throw new NotSupportedException("Profile type does not provide a runtime manager factory: " + GetType().FullName);
        }

    }

    [Serializable]
    public class GPUInstancerRuntimeConfig
    {
        [SerializeReference]
        public List<GPUInstancerProfile> profiles = new List<GPUInstancerProfile>();

        public GPUInstancerRuntimeConfig()
        {
            profiles = new List<GPUInstancerProfile>();
        }

        public T AddProfile<T>() where T : GPUInstancerProfile, new()
        {
            T profile = new T();
            AddProfile(profile);
            return profile;
        }

        public void AddProfile(GPUInstancerProfile profile)
        {
            if (profile == null)
                return;

            if (profiles == null)
                profiles = new List<GPUInstancerProfile>();

            profile.InitializeAuthoringData();
            for (int i = profiles.Count - 1; i >= 0; i--)
            {
                GPUInstancerProfile existing = profiles[i];
                if (existing == null)
                {
                    profiles.RemoveAt(i);
                    continue;
                }

                if (ReferenceEquals(existing, profile) || (!string.IsNullOrEmpty(existing.id) && existing.id == profile.id))
                    return;
            }
            profiles.Add(profile);
        }

        public bool RemoveProfile(GPUInstancerProfile profile)
        {
            if (profile == null || profiles == null)
                return false;

            return profiles.Remove(profile);
        }

        public GPUInstancerProfile FindProfile(string profileId)
        {
            if (string.IsNullOrEmpty(profileId) || profiles == null)
                return null;

            for (int i = 0; i < profiles.Count; i++)
            {
                GPUInstancerProfile profile = profiles[i];
                if (profile != null && profile.id == profileId)
                    return profile;
            }
            return null;
        }

        public T FindProfile<T>(string profileId) where T : GPUInstancerProfile
        {
            return FindProfile(profileId) as T;
        }

    }
    [Serializable]
    public class GPUInstancerPrefabProfile : GPUInstancerProfile, IGPUInstancerProfile<GPUInstancerPrefabManager>
    {
        [SerializeField]
        public List<RegisteredPrefabsData> registeredPrefabs = new List<RegisteredPrefabsData>();

        [SerializeField]
        public List<GameObject> prefabList = new List<GameObject>();

        [SerializeField]
        public bool enableMROnManagerDisable = true;

        [SerializeField]
        public bool enableMROnRemoveInstance = true;

        public new virtual GPUInstancerPrefabManager CreateManager()
        {
            return GPUInstancerProfileFactory.CreateManager(this, () => new GPUInstancerPrefabManager());
        }

        protected override GPUInstancerManager CreateManagerCore()
        {
            return CreateManager();
        }

        protected override string GetDefaultDisplayName() => "Prefab";
    }

    /// <summary>Terrain tree-instancing channel configuration.</summary>
    [Serializable]
    public class GPUInstancerTreeProfile : GPUInstancerProfile, IGPUInstancerProfile<GPUInstancerTreeManager>
    {
        [SerializeField]
        public bool initializeWithCoroutine = true;

        // Terrain settings are project data; the scene Terrain itself remains
        // a Proxy-owned binding and is intentionally not stored here.
        [SerializeField]
        public GPUInstancerTerrainSettings terrainSettings;

        public new virtual GPUInstancerTreeManager CreateManager()
        {
            return GPUInstancerProfileFactory.CreateManager(this, () => new GPUInstancerTreeManager());
        }

        protected override GPUInstancerManager CreateManagerCore()
        {
            return CreateManager();
        }

        protected override string GetDefaultDisplayName() => "Tree";
    }

    /// <summary>Terrain detail-instancing channel configuration.</summary>
    [Serializable]
    public class GPUInstancerDetailProfile : GPUInstancerProfile, IGPUInstancerProfile<GPUInstancerDetailManager>
    {
        [SerializeField]
        public int detailLayer;

        [SerializeField]
        public bool runInThreads = false;

        [SerializeField]
        public GPUInstancerTerrainSettings terrainSettings;

        public new virtual GPUInstancerDetailManager CreateManager()
        {
            return GPUInstancerProfileFactory.CreateManager(this, () => new GPUInstancerDetailManager());
        }

        protected override GPUInstancerManager CreateManagerCore()
        {
            return CreateManager();
        }

        protected override string GetDefaultDisplayName() => "Detail";
    }

    /// <summary>Compile-time profile factory; no runtime interface reflection is used.</summary>
    internal static class GPUInstancerProfileFactory
    {
        public static TManager CreateManager<TManager>(GPUInstancerProfile profile, Func<TManager> factory) where TManager : GPUInstancerManager
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            TManager manager = null;
            try
            {
                manager = factory();
                if (manager == null)
                    throw new InvalidOperationException("GPU Instancer profile factory returned null.");
                // A profile is configuration data; the factory creates a plain
                // managed runtime engine and immediately attaches the recipe.
                if (manager is IGPUInstancerProfileHost host)
                    host.SetProfile(profile);
                return manager;
            }
            catch
            {
                // SetProfile is virtual and may throw after allocating runtime
                // state. The Driver never receives this object, so release it
                // here while preserving the original exception for the caller.
                if (!ReferenceEquals(manager, null))
                {
                    try
                    {
                        manager.Dispose();
                    }
                    catch (Exception cleanupException)
                    {
                        Debug.LogException(cleanupException);
                    }
                }
                throw;
            }
        }
    }
}
