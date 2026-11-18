using UnityEngine;
using System.Collections.Generic;
using System;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fraxinus.GPUBatcher
{
    public abstract class GPUInstancerTerrainManager : GPUInstancerManager
    {
        private Terrain _terrain;
        public List<Terrain> additionalTerrains;
        protected List<Terrain> _terrains;
        public Terrain terrain
        {
            get
            {
                if (Application.isPlaying && _terrains != null && _terrains.Count > 0)
                    return _terrains[0];
                else
                    return _terrain;
            }
        }
        public GPUInstancerTerrainSettings terrainSettings;

        public override UnityEngine.Object diagnosticContext => terrain;

        // A profile stores a project-owned TerrainSettings asset. Terrain
        // managers, however, are runtime engines and play mode must never
        // mutate that asset through the public terrainSettings field. Keep the
        // transient copy separately so it can be released when the manager is
        // disposed (or when a terrain binding is replaced).
        private GPUInstancerTerrainSettings _ownedRuntimeTerrainSettings;
        private GPUInstancerTerrainSettings _profileTerrainSettingsSource;
        private GPUInstancerProfile _profileTerrainSettingsOwner;
        private bool _profileTerrainSettingsInitialized;

        // Terrain prototype generation normally creates ScriptableObjects via
        // GPUInstancerUtility. In the editor workflow those objects
        // are added to TerrainSettings as sub-assets, but a profile-backed
        // manager running in Play Mode has no asset to own them.  Keep an
        // explicit ownership list for that transient path so a force-new or
        // layout reset can release the old objects without ever touching the
        // persistent prototypes stored on the Profile.
        private List<GPUInstancerPrototype> _ownedRuntimeTerrainPrototypes;
        private List<GPUInstancerPrototype> _runtimeTerrainPrototypeGenerationBaseline;
        private Type _runtimeTerrainPrototypeType;
        private int _runtimeTerrainPrototypeLayoutCount = -1;

        protected bool replacingInstances;
        protected bool initalizingInstances;
        protected bool _requiresTerrainUpdate;

        private Queue<Terrain> _terrainsToAdd;
        private Queue<Terrain> _terrainsToRemove;
        private bool _runtimeInitializationRetryPending;

        protected override void ApplyProfile(GPUInstancerProfile value, bool copyCollections)
        {
            base.ApplyProfile(value, copyCollections);

            // Terrain settings are channel data, not scene bindings.  The
            // profile may reference the reusable TerrainSettings asset; the
            // actual Terrain and additional-terrain list stay runtime-only.
            GPUInstancerTerrainSettings profileTerrainSettings = null;
            GPUInstancerTreeProfile treeProfile = value as GPUInstancerTreeProfile;
            if (treeProfile != null)
                profileTerrainSettings = treeProfile.terrainSettings;
            GPUInstancerDetailProfile detailProfile = value as GPUInstancerDetailProfile;
            if (detailProfile != null)
                profileTerrainSettings = detailProfile.terrainSettings;

            // Unity's overloaded == treats a destroyed ScriptableObject as
            // null, but a direct reference comparison does not.  Normalize the
            // value before comparing the source token below so a destroyed
            // TerrainSettings asset cannot keep a stale runtime clone
            // alive forever (especially with domain reload disabled).
            if (profileTerrainSettings == null)
                profileTerrainSettings = null;

            bool isTerrainProfile = value is GPUInstancerTreeProfile || value is GPUInstancerDetailProfile;
            if (!isTerrainProfile)
                return;

            // Sync only when the source reference changes. SetupManagerWithTerrain
            // may generate a runtime-only settings object when the profile has
            // no asset assigned; assigning null again on every Tick would lose
            // that generated object immediately.
            if (_profileTerrainSettingsInitialized &&
                ReferenceEquals(_profileTerrainSettingsOwner, value) &&
                ReferenceEquals(_profileTerrainSettingsSource, profileTerrainSettings))
            {
                // Enter-Play-Mode options can keep managed objects alive while
                // toggling Application.isPlaying. Reconcile the source mode so
                // an edit-mode asset is not retained in play (and vice versa).
                bool runtimeCopyPresent = _ownedRuntimeTerrainSettings != null;
                if (runtimeCopyPresent == Application.isPlaying)
                {
                    if (Application.isPlaying)
                    {
                        if (ReferenceEquals(terrainSettings, _ownedRuntimeTerrainSettings))
                            return;
                    }
                    else if (profileTerrainSettings == null ||
                             ReferenceEquals(terrainSettings, profileTerrainSettings))
                    {
                        return;
                    }
                }
            }

            _profileTerrainSettingsInitialized = true;
            _profileTerrainSettingsSource = profileTerrainSettings;
            _profileTerrainSettingsOwner = value;

            ReleaseOwnedRuntimeTerrainSettings();
            if (profileTerrainSettings == null)
            {
                terrainSettings = null;
                return;
            }

            if (Application.isPlaying)
            {
                GPUInstancerTerrainSettings runtimeCopy = UnityEngine.Object.Instantiate(profileTerrainSettings);
                if (runtimeCopy == null)
                {
                    terrainSettings = null;
                    return;
                }
                runtimeCopy.name = profileTerrainSettings.name + " (Runtime)";
                SetOwnedRuntimeTerrainSettings(runtimeCopy);
            }
            else
            {
                // In edit mode this is configuration data. Keep the asset
                // reference so intentional inspector edits remain persistent.
                terrainSettings = profileTerrainSettings;
            }
        }

        /// <summary>
        /// Terrain generation owns prototype/settings objects in addition to
        /// the base manager buffers. Release those objects after the old
        /// terrain state has been restored, before a new Profile collection is
        /// copied in.
        /// </summary>
        protected override void OnProfileConfigurationReleased()
        {
            ReleaseOwnedRuntimeTerrainPrototypes();
            ReleaseOwnedRuntimeTerrainSettings();
            _profileTerrainSettingsInitialized = false;
            _profileTerrainSettingsSource = null;
            _profileTerrainSettingsOwner = null;
            base.OnProfileConfigurationReleased();
        }

        protected override void OnProfileConfigurationApplied(GPUInstancerProfile value)
        {
            base.OnProfileConfigurationApplied(value);

            // A terrain profile can be rebound while its proxy remains alive
            // (notably after a SerializeReference rehydration). If the new
            // profile has no persisted TerrainSettings asset, establish a
            // manager-local runtime copy immediately so the next Activate does
            // not leave the channel permanently dormant.
            if (value is GPUInstancerTreeProfile || value is GPUInstancerDetailProfile)
            {
                Terrain boundTerrain = terrain;
                if (boundTerrain != null && terrainSettings == null)
                    SetupManagerWithTerrain(boundTerrain);
            }
        }

        public override void Initialize()
        {
            base.Initialize();

            // Direct API callers can construct a profileless Manager and
            // assign a persistent TerrainSettings asset, so normalize it before runtime
            // code can mutate it. ApplyProfile already handled the usual path.
            EnsureRuntimeTerrainSettingsOwnership();

            // Rebuild caller-provided bindings when a profileless Manager is
            // initialized, while retaining the list populated by a Profile's
            // lazy SetupManagerWithTerrain call.
            if (_terrains == null || _terrains.Count == 0)
                RebuildRuntimeTerrainList();
            else
                PruneRuntimeTerrainList();

            if (Application.isPlaying && useFloatingOriginHandler && floatingOriginTransform == null && terrain != null)
            {
                floatingOriginTransform = terrain.transform;
            }
        }

        public override void Activate()
        {
            // An ordinary runtime Manager can be registered more than once,
            // or before a TerrainProxy is enabled. Ensure any caller-provided
            // TerrainSettings asset is detached before Activate reaches the
            // derived runtime-data builders.
            EnsureRuntimeTerrainSettingsOwnership();
            base.Activate();
        }

        public override void Tick()
        {
            EnsureRuntimeTerrainSettingsOwnership();
            base.Tick();

            EnsureRuntimeTerrainList();
            PruneRuntimeTerrainList();

            if (_terrainsToAdd != null)
            {
                while (_terrainsToAdd.Count > 0)
                {
                    Terrain terrain = _terrainsToAdd.Dequeue();
                    if (!_terrains.Contains(terrain))
                    {
                        _terrains.Add(terrain);
                        if (_terrain == null)
                            _terrain = terrain;
                        UpdateTerrains();
                    }
                }
            }
            if (_terrainsToRemove != null)
            {
                while (_terrainsToRemove.Count > 0)
                {
                    Terrain terrain = _terrainsToRemove.Dequeue();
                    if (_terrains.Contains(terrain))
                    {
                        _terrains.Remove(terrain);

                        // The primary terrain is kept in a separate serialized
                        // field. When it is
                        // removed at runtime, promote the next bound terrain
                        // (or clear the field) so the terrain getter cannot
                        // resurrect a scene object that has already unloaded.
                        if (ReferenceEquals(_terrain, terrain))
                            _terrain = _terrains.Count > 0 ? _terrains[0] : null;

                        UpdateTerrains();
                    }
                }

            }

            // A profile-owned manager can outlive its last proxy during an
            // additive scene unload. Release its per-terrain buffers now; the
            // next proxy bind will initialize them again after Setup.
            if (_terrains.Count == 0)
            {
                _runtimeInitializationRetryPending = false;
                if (isInitialized)
                    ClearInstancingData();
            }
            else
            {
                RetryRuntimeInitializationIfReady();
            }
        }

        public override void GeneratePrototypes(bool forceNew = false)
        {
            EnsureRuntimeTerrainSettingsOwnership();
            base.GeneratePrototypes(forceNew);
        }

        /// <summary>
        /// Starts ownership tracking for a Tree/Detail prototype generation
        /// pass.  The utility methods intentionally retain their historical
        /// public behaviour; the manager only observes the set of loaded
        /// prototype objects before and after the call.  This lets the
        /// corresponding <c>finally</c> block claim objects even when utility
        /// generation throws after creating a prototype but before adding it
        /// to <see cref="GPUInstancerManager.prototypeList"/>.
        /// </summary>
        protected bool BeginRuntimeTerrainPrototypeGeneration<TPrototype>(
            bool forceNew,
            int terrainPrototypeCount)
            where TPrototype : GPUInstancerPrototype
        {
            // Editor generation intentionally creates/updates persistent
            // TerrainSettings sub-assets. Restrict this ownership protocol to
            // Profile-backed Play Mode Managers; profileless API Managers and editor previews
            // retain their existing semantics.
            if (profile == null || !Application.isPlaying)
                return false;

            // A cleared manager list is the signal used by terrain rebinding
            // when TerrainData (and therefore the indexed layout) changes.
            // A changed source count is an additional guard for callers that
            // invoke GeneratePrototypes directly without clearing the list.
            bool layoutReset = forceNew ||
                HasOwnedRuntimeTerrainPrototypeOutsideCurrentList() ||
                (_runtimeTerrainPrototypeType != null &&
                 _runtimeTerrainPrototypeType != typeof(TPrototype)) ||
                (_runtimeTerrainPrototypeLayoutCount >= 0 &&
                 terrainPrototypeCount >= 0 &&
                 _runtimeTerrainPrototypeLayoutCount != terrainPrototypeCount);

            if (layoutReset)
                ReleaseOwnedRuntimeTerrainPrototypes();
            else
                ReleaseUnreferencedRuntimeTerrainPrototypes();

            // Capture all currently loaded objects, not only the manager list.
            // The latter may miss a prototype if an exception interrupts the
            // utility before its final Add() call.
            _runtimeTerrainPrototypeGenerationBaseline =
                CaptureRuntimeTerrainPrototypes<TPrototype>();
            _runtimeTerrainPrototypeType = typeof(TPrototype);
            _runtimeTerrainPrototypeLayoutCount = terrainPrototypeCount;
            return true;
        }

        /// <summary>Completes a generation pass and claims newly-created prototypes.</summary>
        protected void EndRuntimeTerrainPrototypeGeneration<TPrototype>()
            where TPrototype : GPUInstancerPrototype
        {
            List<GPUInstancerPrototype> baseline =
                _runtimeTerrainPrototypeGenerationBaseline;
            _runtimeTerrainPrototypeGenerationBaseline = null;
            if (baseline == null)
                return;

            try
            {
                // Observe the global object set as well as prototypeList. The
                // global pass is what covers an exception between CreateInstance
                // and list insertion; the list pass is a cheap fallback for
                // Unity versions that do not expose a newly-created hidden SO
                // through Resources until the next update.
                List<GPUInstancerPrototype> current =
                    CaptureRuntimeTerrainPrototypes<TPrototype>();
                for (int i = 0; i < current.Count; i++)
                {
                    GPUInstancerPrototype prototype = current[i];
                    if (!ContainsReference(baseline, prototype))
                        TrackRuntimeTerrainPrototype(prototype);
                }

                if (prototypeList != null)
                {
                    for (int i = 0; i < prototypeList.Count; i++)
                    {
                        GPUInstancerPrototype prototype = prototypeList[i];
                        if (prototype is TPrototype &&
                            !ContainsReference(baseline, prototype))
                            TrackRuntimeTerrainPrototype(prototype);
                    }
                }
            }
            catch (Exception exception)
            {
                // This method runs from a generation finally block. Never
                // replace the utility's original exception with an object
                // enumeration/cleanup failure; log the latter and let the
                // caller observe the original failure.
                Debug.LogException(exception, diagnosticContext);
            }
            finally
            {
                // Do not retain a potentially stale baseline if object
                // enumeration itself fails during scene teardown.
                _runtimeTerrainPrototypeGenerationBaseline = null;
            }
        }

        private static List<GPUInstancerPrototype> CaptureRuntimeTerrainPrototypes<TPrototype>()
            where TPrototype : GPUInstancerPrototype
        {
            List<GPUInstancerPrototype> result = new List<GPUInstancerPrototype>();
            TPrototype[] loaded = Resources.FindObjectsOfTypeAll<TPrototype>();
            if (loaded == null)
                return result;

            for (int i = 0; i < loaded.Length; i++)
            {
                TPrototype prototype = loaded[i];
                if (!ReferenceEquals(prototype, null) && !ContainsReference(result, prototype))
                    result.Add(prototype);
            }
            return result;
        }

        private bool HasOwnedRuntimeTerrainPrototypeOutsideCurrentList()
        {
            if (_ownedRuntimeTerrainPrototypes == null ||
                _ownedRuntimeTerrainPrototypes.Count == 0)
                return false;
            if (prototypeList == null)
                return true;

            for (int i = 0; i < _ownedRuntimeTerrainPrototypes.Count; i++)
            {
                GPUInstancerPrototype owned = _ownedRuntimeTerrainPrototypes[i];
                if (!ReferenceEquals(owned, null) &&
                    !ContainsReference(prototypeList, owned))
                    return true;
            }
            return false;
        }

        private void ReleaseUnreferencedRuntimeTerrainPrototypes()
        {
            if (_ownedRuntimeTerrainPrototypes == null ||
                _ownedRuntimeTerrainPrototypes.Count == 0)
                return;

            for (int i = _ownedRuntimeTerrainPrototypes.Count - 1; i >= 0; i--)
            {
                GPUInstancerPrototype owned = _ownedRuntimeTerrainPrototypes[i];
                if (ReferenceEquals(owned, null) ||
                    prototypeList == null ||
                    !ContainsReference(prototypeList, owned))
                {
                    _ownedRuntimeTerrainPrototypes.RemoveAt(i);
                    DestroyOwnedRuntimeTerrainPrototype(owned);
                }
            }
        }

        private void TrackRuntimeTerrainPrototype(GPUInstancerPrototype prototype)
        {
            if (ReferenceEquals(prototype, null) ||
                IsPersistentOrProfileTerrainPrototype(prototype))
                return;

            if (_ownedRuntimeTerrainPrototypes == null)
                _ownedRuntimeTerrainPrototypes = new List<GPUInstancerPrototype>();
            if (ContainsReference(_ownedRuntimeTerrainPrototypes, prototype))
                return;

            // Runtime-generated prototypes are not project data. Keep them
            // out of hierarchy/build saves immediately after discovery.
            prototype.hideFlags = HideFlags.HideAndDontSave;
            _ownedRuntimeTerrainPrototypes.Add(prototype);
        }

        /// <summary>
        /// Releases all transient prototypes owned by this profile-backed
        /// manager. Persistent Profile/TerrainSettings objects are explicitly
        /// skipped as a final safety net, even if a caller mutated a list
        /// between generation and teardown.
        /// </summary>
        protected void ReleaseOwnedRuntimeTerrainPrototypes()
        {
            if (_ownedRuntimeTerrainPrototypes == null ||
                _ownedRuntimeTerrainPrototypes.Count == 0)
            {
                _runtimeTerrainPrototypeGenerationBaseline = null;
                _runtimeTerrainPrototypeType = null;
                _runtimeTerrainPrototypeLayoutCount = -1;
                return;
            }

            List<GPUInstancerPrototype> owned = _ownedRuntimeTerrainPrototypes;
            _ownedRuntimeTerrainPrototypes = null;
            for (int i = 0; i < owned.Count; i++)
                DestroyOwnedRuntimeTerrainPrototype(owned[i]);

            _runtimeTerrainPrototypeGenerationBaseline = null;
            _runtimeTerrainPrototypeType = null;
            _runtimeTerrainPrototypeLayoutCount = -1;
        }

        private void DestroyOwnedRuntimeTerrainPrototype(GPUInstancerPrototype prototype)
        {
            if (ReferenceEquals(prototype, null) ||
                IsPersistentOrProfileTerrainPrototype(prototype))
                return;

            // Tracking marks every claimed object with DontSave. If a caller
            // replaced the wrapper in the meantime, retaining an unmarked
            // object is safer than risking destruction of a persistent asset
            // that happens to share a stale reference in a Player build.
            if ((prototype.hideFlags & HideFlags.DontSave) == 0)
                return;

            // Remove stale references from the manager-side copy before the
            // Unity destruction call. Profile.prototypeList is never edited.
            if (prototypeList != null)
            {
                for (int i = prototypeList.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(prototypeList[i], prototype))
                        prototypeList.RemoveAt(i);
                }
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEngine.Object.DestroyImmediate(prototype);
            else
                UnityEngine.Object.Destroy(prototype);
#else
            UnityEngine.Object.Destroy(prototype);
#endif
        }

        private bool IsPersistentOrProfileTerrainPrototype(GPUInstancerPrototype prototype)
        {
            if (ReferenceEquals(prototype, null) || prototype == null)
                return true;

            GPUInstancerProfile currentProfile = profile;
            if (currentProfile != null && currentProfile.prototypeList != null &&
                ContainsReference(currentProfile.prototypeList, prototype))
                return true;

#if UNITY_EDITOR
            if (EditorUtility.IsPersistent(prototype))
                return true;
#endif
            return false;
        }

        private static bool ContainsReference<T>(List<T> list, T value)
            where T : class
        {
            if (list == null)
                return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], value))
                    return true;
            }
            return false;
        }

        public override void InitializeRuntimeDataAndBuffers(bool forceNew = true)
        {
            // Each explicit attempt consumes the pending request. Derived
            // managers request another attempt only for a known transient
            // prerequisite (TerrainData/detail resolutions not ready yet).
            _runtimeInitializationRetryPending = false;
            EnsureRuntimeTerrainSettingsOwnership();
            base.InitializeRuntimeDataAndBuffers(forceNew);
        }

        public override void Dispose()
        {
            _runtimeInitializationRetryPending = false;
            if (isDisposed)
                return;

            // Keep transient terrain assets releasable even when a derived
            // Deactivate/ClearInstancingData implementation throws while a
            // scene is being torn down.
            try
            {
                base.Dispose();
            }
            finally
            {
                ReleaseOwnedRuntimeTerrainPrototypes();
                ReleaseOwnedRuntimeTerrainSettings();
                _profileTerrainSettingsInitialized = false;
                _profileTerrainSettingsSource = null;
                _profileTerrainSettingsOwner = null;
            }
        }

        public override void ResetConfiguration()
        {
            base.ResetConfiguration();

#if UNITY_EDITOR
            showRegisteredPrefabsBox = false;
#endif
        }

        // Remove comment-out status to see partitioning bound gizmos:
        //public void OnDrawGizmos()
        //{
        //    if (spData != null && spData.activeCellList != null)
        //    {
        //        Color oldColor = Gizmos.color;
        //        Gizmos.color = Color.blue;
        //        foreach (GPUInstancerCell cell in spData.activeCellList)
        //        {
        //            if (cell != null)
        //                Gizmos.DrawWireCube(cell.cellInnerBounds.center, cell.cellInnerBounds.size);
        //        }
        //        Gizmos.color = oldColor;
        //    }
        //}

#if UNITY_EDITOR
        public override void CheckPrototypeChanges()
        {
            if (terrain != null && terrain.terrainData != null && terrainSettings != null)
            {
                string guid = GPUInstancerUtility.GetAssetGUID(terrain.terrainData);
                if (!string.IsNullOrEmpty(guid) && guid != terrainSettings.terrainDataGUID)
                    terrainSettings.terrainDataGUID = guid;
            }

            base.CheckPrototypeChanges();
        }
#endif

        public virtual void SetupManagerWithTerrain(Terrain terrain)
        {
            if (terrain == null)
                _runtimeInitializationRetryPending = false;

#if UNITY_EDITOR
            if (!Application.isPlaying && profile == null)
            {
                // Only the profileless editor workflow may
                // own/remove this proxy component. Profile-backed Managers are
                // transient and must never destroy the scene proxy that stores
                // their stable Profile IDs while a preview is active.
                if (_terrain != null && _terrain.GetComponent<GPUInstancerTerrainProxy>() != null)
                {
                    Undo.RecordObject(_terrain.gameObject, "Removed GPUInstancerTerrainProxy component");
                    UnityEngine.Object.DestroyImmediate(_terrain.GetComponent<GPUInstancerTerrainProxy>());
                }
            }
#endif

            Terrain previousTerrain = _terrain;

            // Cancel operations queued by a proxy that was disabled/rebound
            // before the next Hub tick.  Without this, a direct setup of the
            // same terrain can be undone one frame later by a stale removal.
            RemoveQueuedTerrain(_terrainsToRemove, terrain);
            RemoveQueuedTerrain(_terrainsToAdd, terrain);

            // A profileless API Manager may arrive here before Hub.Register
            // invokes Initialize(). Ensure its persistent TerrainSettings
            // asset is copied before the same-GUID fast path below returns.
            EnsureRuntimeTerrainSettingsOwnership();

            // A profile-created manager is registered before a TerrainProxy
            // gets a chance to bind its Terrain.  Initialize() therefore sees
            // an empty terrain list.  Keep the runtime list synchronized here
            // so the first lazy binding is treated exactly like a caller-
            // prebound Manager. Preserve explicitly configured additional
            // Terrains while replacing the primary one.
            EnsureRuntimeTerrainList();

            // A profile-backed manager separates persistent channel
            // configuration from scene bindings. Unbinding its last/all
            // Terrains must release GPU state while the old list is still
            // available (derived managers use it to restore Unity's native
            // tree/detail distances), but it must not erase terrainSettings
            // or prototypeList: both belong to the Profile recipe and are
            // needed when a proxy binds again. Profileless API
            // Managers retain their historical Setup(null) behaviour below.
            bool preserveProfileConfiguration = terrain == null && profile != null;
            if (preserveProfileConfiguration)
                ClearInstancingData();

            // Setup(null) and a primary-terrain switch can remove a live
            // terrain from the runtime list before the normal queued removal
            // path gets a chance to run.  Tree/Detail managers temporarily
            // suppress Unity's native renderer while bound, so restore every
            // terrain that is about to disappear while the old list and
            // channel settings are still available.  The profile path has
            // already restored through ClearInstancingData above; repeating
            // the idempotent assignment is harmless and keeps this boundary
            // safe if a derived implementation changes its teardown order.
            if (_terrains != null)
            {
                if (terrain == null)
                {
                    for (int i = 0; i < _terrains.Count; i++)
                        RestoreTerrainSettingsAfterUnbind(_terrains[i]);
                }
                else if (previousTerrain != null && !ReferenceEquals(previousTerrain, terrain) &&
                    (additionalTerrains == null || !additionalTerrains.Contains(previousTerrain)))
                {
                    RestoreTerrainSettingsAfterUnbind(previousTerrain);
                }
            }

            _terrain = terrain;
            if (terrain == null)
            {
                _terrains.Clear();
                // Profile-created managers use this list purely as runtime
                // bookkeeping. Clearing it prevents a previous proxy's
                // Terrain from being resurrected by the next Setup call. A
                // profileless Manager's caller-provided list remains intact
                // and is still rebuilt on demand.
                if (profile != null && additionalTerrains != null)
                    additionalTerrains.Clear();
                if (_terrainsToAdd != null)
                    _terrainsToAdd.Clear();
                if (_terrainsToRemove != null)
                    _terrainsToRemove.Clear();
            }
            else
            {
                if (previousTerrain != null && !ReferenceEquals(previousTerrain, terrain) &&
                    (additionalTerrains == null || !additionalTerrains.Contains(previousTerrain)))
                    _terrains.Remove(previousTerrain);

                _terrains.Remove(terrain);
                _terrains.Insert(0, terrain);
                if (additionalTerrains != null)
                {
                    for (int i = 0; i < additionalTerrains.Count; i++)
                    {
                        Terrain additional = additionalTerrains[i];
                        if (additional != null && !_terrains.Contains(additional))
                            _terrains.Add(additional);
                    }
                }
            }

            if (terrain != null)
            {
                if (terrain.terrainData == null)
                {
                    // Unity can expose a Terrain component briefly before its
                    // TerrainData is assigned (scene/prefab import).  Keep the
                    // binding, but defer settings/prototype generation until a
                    // later bind instead of throwing from GenerateTerrainSettings.
                    if (!isInitialized)
                        RequestRuntimeInitializationRetry();
                    if (!Application.isPlaying)
                        AddProxyToTerrain();
                    return;
                }
                if (terrainSettings != null)
                {
                    string guid = GPUInstancerUtility.GetAssetGUID(terrain.terrainData);

#if UNITY_EDITOR
                    // A Profile may have been authored against a package
                    // default (or another read-only) TerrainSettings asset.
                    // The same-GUID fast path used to return that object
                    // directly, which meant the editor drawer could mutate a
                    // package asset through the transient manager. Normalize
                    // the reference before comparing the GUID so every
                    // profile-backed editor binding owns a writable project
                    // copy.  If copying fails, discard the immutable source
                    // and fall through to GenerateTerrainSettings, which
                    // creates a fresh project asset instead of touching it.
                    if (!Application.isPlaying && profile != null)
                    {
                        GPUInstancerTerrainSettings editableSettings =
                            EnsureEditableTerrainSettingsAsset(terrainSettings, terrain);
                        if (editableSettings == null)
                        {
                            prototypeList.Clear();
                            ReleaseOwnedRuntimeTerrainSettings();
                            terrainSettings = null;
                            SetProfileTerrainSettings(null);
                        }
                        else if (!ReferenceEquals(editableSettings, terrainSettings))
                        {
                            GPUInstancerTerrainSettings previousSettings = terrainSettings;
                            terrainSettings = editableSettings;
                            SetProfileTerrainSettings(editableSettings);
                            RemapProfileTerrainPrototypes(previousSettings, editableSettings);
                            guid = GPUInstancerUtility.GetAssetGUID(terrain.terrainData);
                        }
                    }
#endif

                    if (terrainSettings != null &&
                        !string.IsNullOrEmpty(guid) && guid == terrainSettings.terrainDataGUID)
                    {
                        // A hand-authored Profile can provide only a
                        // TerrainSettings reference and leave its prototype
                        // list empty. Treat the first bind as a layout build
                        // in that case; otherwise the same-GUID shortcut
                        // would leave a valid Terrain with no runtime data.
                        int terrainPrototypeCount = -1;
                        if (this is GPUInstancerDetailManager)
                        {
                            terrainPrototypeCount = terrain.terrainData.detailPrototypes == null
                                ? 0 : terrain.terrainData.detailPrototypes.Length;
                        }
                        else if (this is GPUInstancerTreeManager)
                        {
                            terrainPrototypeCount = terrain.terrainData.treePrototypes == null
                                ? 0 : terrain.terrainData.treePrototypes.Length;
                        }
                        if (profile != null && (prototypeList == null || prototypeList.Count == 0) &&
                            terrainPrototypeCount > 0)
                        {
                            GeneratePrototypes(false);
                        }
                        if (!Application.isPlaying)
                            AddProxyToTerrain();
                        return;
                    }
                    else
                    {
                        prototypeList.Clear();
                        //RemoveTerrainSettings(terrainSettings);
                        ReleaseOwnedRuntimeTerrainSettings();
                        terrainSettings = null;
                    }
                }
                GPUInstancerTerrainSettings generatedTerrainSettings = GenerateTerrainSettings(terrain);
                if (Application.isPlaying)
                    SetOwnedRuntimeTerrainSettings(generatedTerrainSettings);
                else
                    terrainSettings = generatedTerrainSettings;
                SetProfileTerrainSettings(terrainSettings);
                GeneratePrototypes(false);
                if (!Application.isPlaying)
                    AddProxyToTerrain();
            }
            else
            {
                if (!preserveProfileConfiguration)
                {
                    prototypeList.Clear();
                    //RemoveTerrainSettings(terrainSettings);
                    ReleaseOwnedRuntimeTerrainSettings();
                    terrainSettings = null;
                    SetProfileTerrainSettings(null);
                }
            }
        }

        /// <summary>
        /// Requests one deferred initialization attempt for a bound Terrain
        /// whose Unity data is still being populated.
        /// </summary>
        protected void RequestRuntimeInitializationRetry()
        {
            if (!isDisposed && terrain != null && !isInitialized)
                _runtimeInitializationRetryPending = true;
        }

        private void RetryRuntimeInitializationIfReady()
        {
            if (!_runtimeInitializationRetryPending || !Application.isPlaying ||
                !isActive || isInitialized)
                return;

            Terrain boundTerrain = terrain;
            TerrainData data = boundTerrain == null ? null : boundTerrain.terrainData;
            if (data == null)
                return;
            if (this is GPUInstancerDetailManager &&
                (data.detailResolution <= 0 || data.heightmapResolution <= 0))
                return;

            // Consume before entering setup/user-extensible initialization.
            // A real exception or permanent configuration/resource error must
            // surface once instead of being converted into a per-frame retry.
            _runtimeInitializationRetryPending = false;
            SetupManagerWithTerrain(boundTerrain);
            if (terrainSettings == null || isInitialized)
                return;
            InitializeRuntimeDataAndBuffers(true);
        }

        private void SetProfileTerrainSettings(GPUInstancerTerrainSettings value)
        {
            // Profiles live on the project settings asset. Runtime terrain
            // discovery must stay manager-local and must never write a
            // generated HideAndDontSave object back into that asset graph.
            if (Application.isPlaying || _ownedRuntimeTerrainSettings != null)
                return;

#if UNITY_EDITOR
            GPUInstancerProjectSettings settings = GPUInstancerConstants.projectSettings;
            if (settings != null && settings.runtimeConfig != null &&
                settings.runtimeConfig.profiles != null && settings.runtimeConfig.profiles.Contains(profile))
            {
                Undo.RecordObject(settings, "Update GPU Instancer terrain profile");
                EditorUtility.SetDirty(settings);
            }
#endif

            if (profile is GPUInstancerTreeProfile treeProfile)
                treeProfile.terrainSettings = value;
            else if (profile is GPUInstancerDetailProfile detailProfile)
                detailProfile.terrainSettings = value;
        }

        private void SetOwnedRuntimeTerrainSettings(GPUInstancerTerrainSettings value)
        {
            if (value == null)
            {
                ReleaseOwnedRuntimeTerrainSettings();
                terrainSettings = null;
                return;
            }

            if (ReferenceEquals(_ownedRuntimeTerrainSettings, value))
            {
                terrainSettings = value;
                return;
            }

            ReleaseOwnedRuntimeTerrainSettings();
            value.hideFlags = HideFlags.HideAndDontSave;
            _ownedRuntimeTerrainSettings = value;
            terrainSettings = value;
        }

        private void ReleaseOwnedRuntimeTerrainSettings()
        {
            GPUInstancerTerrainSettings owned = _ownedRuntimeTerrainSettings;
            _ownedRuntimeTerrainSettings = null;

            // Clear our public alias even when Unity has already destroyed the
            // native object and the managed wrapper only compares equal to
            // null through Unity's overloaded operator.
            if (ReferenceEquals(terrainSettings, owned))
                terrainSettings = null;

            if (owned == null)
                return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEngine.Object.DestroyImmediate(owned);
            else
                UnityEngine.Object.Destroy(owned);
#else
            UnityEngine.Object.Destroy(owned);
#endif
        }

        /// <summary>
        /// Makes a transient copy when a profileless Manager points directly
        /// at a persistent TerrainSettings asset. Profile-backed Managers,
        /// perform the same operation in ApplyProfile; this helper covers direct API
        /// registration without writing runtime state back to disk.
        /// </summary>
        private void EnsureRuntimeTerrainSettingsOwnership()
        {
            if (!Application.isPlaying)
                return;

            GPUInstancerTerrainSettings source = terrainSettings;
            if (source == null)
            {
                if (!ReferenceEquals(_ownedRuntimeTerrainSettings, null))
                    ReleaseOwnedRuntimeTerrainSettings();
                terrainSettings = null;
                return;
            }

            if (_ownedRuntimeTerrainSettings != null &&
                ReferenceEquals(source, _ownedRuntimeTerrainSettings))
                return;

            // A runtime object produced by our own setup path is already
            // transient. Claim it instead of cloning it a second time. This
            // also handles direct API callers that created a settings object
            // and marked it HideAndDontSave before assigning it here.
            if ((source.hideFlags & HideFlags.DontSave) != 0)
            {
                SetOwnedRuntimeTerrainSettings(source);
                return;
            }

#if UNITY_EDITOR
            // In the editor, non-persistent objects are scene/runtime data and
            // should be owned directly. Persistent assets (including package
            // defaults) must be cloned before Play Mode can mutate them.
            if (!EditorUtility.IsPersistent(source))
            {
                SetOwnedRuntimeTerrainSettings(source);
                return;
            }
#endif

            GPUInstancerTerrainSettings runtimeCopy = UnityEngine.Object.Instantiate(source);
            if (runtimeCopy == null)
                return;

            runtimeCopy.name = source.name + " (Runtime)";
            SetOwnedRuntimeTerrainSettings(runtimeCopy);
        }

        public GPUInstancerTerrainProxy AddProxyToTerrain()
        {
#if UNITY_EDITOR
            if (terrain != null)
            {
                GPUInstancerTerrainProxy terrainProxy = terrain.GetComponent<GPUInstancerTerrainProxy>();
                if (terrainProxy == null)
                {
                    Undo.RecordObject(terrain.gameObject, "Added GPUInstancerTerrainProxy component");
                    terrainProxy = terrain.gameObject.AddComponent<GPUInstancerTerrainProxy>();
                    if (terrainProxy != null)
                        Undo.RegisterCreatedObjectUndo(terrainProxy, "Added GPUInstancerTerrainProxy component");
                }
                if (terrainProxy == null)
                    return null;
                if (this is GPUInstancerDetailManager)
                {
                    if (profile != null)
                    {
                        // Profile-created managers are transient. Persist only
                        // the stable ID on the scene proxy.
                        Undo.RecordObject(terrainProxy, "Bind GPU Instancer Detail Profile");
                        terrainProxy.SetProfileId(profile, true);
                        terrainProxy.SetRuntimeManager(this, true);
                        EditorUtility.SetDirty(terrainProxy);
                    }
                    else
                    {
                        // Standalone managed Managers have no serializable
                        // identity. Keep only the process-local handle.
                        terrainProxy.SetRuntimeManager(this, true);
                    }
                }
                else if (this is GPUInstancerTreeManager)
                {
                    if (profile != null)
                    {
                        Undo.RecordObject(terrainProxy, "Bind GPU Instancer Tree Profile");
                        terrainProxy.SetProfileId(profile, false);
                        terrainProxy.SetRuntimeManager(this, false);
                        EditorUtility.SetDirty(terrainProxy);
                    }
                    else
                    {
                        terrainProxy.SetRuntimeManager(this, false);
                    }
                }

#if UNITY_2018_3_OR_NEWER
                PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(terrainProxy.gameObject);
                if (prefabType == PrefabAssetType.NotAPrefab)
                {
#endif
                    while (UnityEditorInternal.ComponentUtility.MoveComponentUp(terrainProxy)) ;
#if UNITY_2018_3_OR_NEWER
                }
#endif
                return terrainProxy;
            }
#endif
            return null;
        }

        private GPUInstancerTerrainSettings GenerateTerrainSettings(Terrain terrain)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                string[] guids = AssetDatabase.FindAssets("t:GPUInstancerTerrainSettings");
                string guid = GPUInstancerUtility.GetAssetGUID(terrain.terrainData);
                for (int i = 0; i < guids.Length; i++)
                {
                    GPUInstancerTerrainSettings ts = AssetDatabase.LoadAssetAtPath<GPUInstancerTerrainSettings>(AssetDatabase.GUIDToAssetPath(guids[i]));
                    if (ts != null && !string.IsNullOrEmpty(guid) && ts.terrainDataGUID == guid)
                    {
                        // Package assets (and project assets locked by source
                        // control) are immutable defaults. A Profile exposes
                        // TerrainSettings as editable project configuration,
                        // so never attach such an object directly: clone the
                        // complete asset file, including prototype subassets,
                        // into the writable generated-data tree first.
                        ts = EnsureEditableTerrainSettingsAsset(ts, terrain);
                        if (ts == null)
                            continue;

                        prototypeList.Clear();
                        if (this is GPUInstancerDetailManager)
                        {
                            List<GPUInstancerPrototype> detailPrototypeList = new List<GPUInstancerPrototype>();
                            GPUInstancerUtility.SetPrototypeListFromAssets(ts, detailPrototypeList, typeof(GPUInstancerDetailPrototype));
                            for (int p = 0; p < detailPrototypeList.Count; p++)
                            {
                                foreach (GPUInstancerDetailPrototype detailPrototype in detailPrototypeList)
                                {
                                    if (detailPrototype.prototypeIndex == p)
                                    {
                                        prototypeList.Add(detailPrototype);
                                        break;
                                    }
                                }
                            }
                        }
                        if (this is GPUInstancerTreeManager)
                        {
                            List<GPUInstancerPrototype> treePrototypeList = new List<GPUInstancerPrototype>();
                            GPUInstancerUtility.SetPrototypeListFromAssets(ts, treePrototypeList, typeof(GPUInstancerTreePrototype));
                            for (int p = 0; p < treePrototypeList.Count; p++)
                            {
                                foreach (GPUInstancerTreePrototype treePrototype in treePrototypeList)
                                {
                                    if (treePrototype.prototypeIndex == p)
                                    {
                                        prototypeList.Add(treePrototype);
                                        break;
                                    }
                                }
                            }
                        }
                        return ts;
                    }
                }
            }
#endif

            GPUInstancerTerrainSettings terrainSettings = ScriptableObject.CreateInstance<GPUInstancerTerrainSettings>();
            terrainSettings.name = (string.IsNullOrEmpty(terrain.terrainData.name) ? terrain.gameObject.name : terrain.terrainData.name) + "_" + terrain.terrainData.GetInstanceID();
            terrainSettings.terrainDataGUID = GPUInstancerUtility.GetAssetGUID(terrain.terrainData);
            terrainSettings.maxDetailDistance = terrain.detailObjectDistance;
            terrainSettings.maxTreeDistance = terrain.treeDistance;
            terrainSettings.detailDensity = terrain.detailObjectDensity;
            terrainSettings.healthyDryNoiseTexture = Resources.Load<Texture2D>(GPUInstancerConstants.NOISE_TEXTURES_PATH + GPUInstancerConstants.DEFAULT_HEALTHY_DRY_NOISE);
            terrainSettings.windWaveNormalTexture = Resources.Load<Texture2D>(GPUInstancerConstants.NOISE_TEXTURES_PATH + GPUInstancerConstants.DEFAULT_WIND_WAVE_NOISE);

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                string assetPath = GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.PROTOTYPES_TERRAIN_PATH + terrainSettings.name + ".asset";

                // If there is already a file with the same name, change file name
                int counter = 2;
                while (System.IO.File.Exists(assetPath))
                {
                    assetPath = GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.PROTOTYPES_TERRAIN_PATH + terrainSettings.name + "_" + counter + ".asset";
                    counter++;
                }

                if (!System.IO.Directory.Exists(GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.PROTOTYPES_TERRAIN_PATH))
                {
                    System.IO.Directory.CreateDirectory(GPUInstancerConstants.GetDefaultPath() + GPUInstancerConstants.PROTOTYPES_TERRAIN_PATH);
                }

                AssetDatabase.CreateAsset(terrainSettings, assetPath);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
#endif
            return terrainSettings;
        }

#if UNITY_EDITOR
        private static GPUInstancerTerrainSettings EnsureEditableTerrainSettingsAsset(
            GPUInstancerTerrainSettings source,
            Terrain terrain)
        {
            if (source == null)
                return null;

            string sourcePath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(sourcePath))
                return source;

            bool packageAsset = GPUInstancerConstants.IsPackageAssetPath(sourcePath);
            bool editable = AssetDatabase.IsOpenForEdit(sourcePath);
            if (!packageAsset && editable)
                return source;

            // Reuse an existing writable copy for this TerrainData. This is
            // important when a package default is encountered repeatedly
            // during domain reloads or proxy rebinds; blindly copying it on
            // every setup would create duplicate project assets.
            string[] existingGuids = AssetDatabase.FindAssets("t:GPUInstancerTerrainSettings");
            for (int i = 0; i < existingGuids.Length; i++)
            {
                string existingPath = AssetDatabase.GUIDToAssetPath(existingGuids[i]);
                if (string.IsNullOrEmpty(existingPath) ||
                    GPUInstancerConstants.IsPackageAssetPath(existingPath) ||
                    !AssetDatabase.IsOpenForEdit(existingPath))
                    continue;
                GPUInstancerTerrainSettings existing =
                    AssetDatabase.LoadAssetAtPath<GPUInstancerTerrainSettings>(existingPath);
                if (existing != null && existing != source &&
                    existing.terrainDataGUID == source.terrainDataGUID)
                    return existing;
            }

            string folder = GPUInstancerConstants.GetDefaultPath() +
                GPUInstancerConstants.PROTOTYPES_TERRAIN_PATH;
            EnsureAssetFolder(folder);

            string baseName = source.name;
            if (string.IsNullOrEmpty(baseName) && terrain != null && terrain.terrainData != null)
                baseName = terrain.terrainData.name;
            if (string.IsNullOrEmpty(baseName))
                baseName = "TerrainSettings";

            string destination = AssetDatabase.GenerateUniqueAssetPath(
                folder + baseName + ".asset");
            if (!AssetDatabase.CopyAsset(sourcePath, destination))
            {
                Debug.LogWarning(
                    "GPU Instancer could not copy immutable TerrainSettings asset '" +
                    sourcePath + "' to a writable project asset.");
                return null;
            }

            AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
            GPUInstancerTerrainSettings copy =
                AssetDatabase.LoadAssetAtPath<GPUInstancerTerrainSettings>(destination);
            if (copy == null)
            {
                Debug.LogWarning(
                    "GPU Instancer copied TerrainSettings asset but could not load the writable copy: " +
                    destination);
                return null;
            }

            return copy;
        }

        /// <summary>
        /// Repoints a profile's terrain prototypes at the sub-assets in a
        /// writable TerrainSettings copy.  AssetDatabase.CopyAsset duplicates
        /// the complete file, but managed references held by a Profile still
        /// point at the package/source sub-assets until they are explicitly
        /// remapped.  Only references belonging to <paramref name="source"/>
        /// are changed; custom prototypes from another asset remain intact.
        /// </summary>
        private void RemapProfileTerrainPrototypes(
            GPUInstancerTerrainSettings source,
            GPUInstancerTerrainSettings destination)
        {
            if (source == null || destination == null ||
                ReferenceEquals(source, destination) || profile == null ||
                prototypeList == null || prototypeList.Count == 0)
                return;

            string sourcePath = AssetDatabase.GetAssetPath(source);
            string destinationPath = AssetDatabase.GetAssetPath(destination);
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(destinationPath) ||
                string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                return;

            Type prototypeType;
            if (this is GPUInstancerDetailManager)
                prototypeType = typeof(GPUInstancerDetailPrototype);
            else if (this is GPUInstancerTreeManager)
                prototypeType = typeof(GPUInstancerTreePrototype);
            else
                return;
            Dictionary<int, GPUInstancerPrototype> destinationByIndex =
                new Dictionary<int, GPUInstancerPrototype>();
            UnityEngine.Object[] destinationAssets =
                AssetDatabase.LoadAllAssetsAtPath(destinationPath);
            for (int i = 0; i < destinationAssets.Length; i++)
            {
                UnityEngine.Object asset = destinationAssets[i];
                if (asset == null || asset.GetType() != prototypeType)
                    continue;

                GPUInstancerPrototype prototype = (GPUInstancerPrototype)asset;
                int index = prototype is GPUInstancerDetailPrototype detailPrototype
                    ? detailPrototype.prototypeIndex
                    : ((GPUInstancerTreePrototype)prototype).prototypeIndex;
                if (!destinationByIndex.ContainsKey(index))
                    destinationByIndex.Add(index, prototype);
            }

            bool changed = false;
            for (int i = 0; i < prototypeList.Count; i++)
            {
                GPUInstancerPrototype prototype = prototypeList[i];
                if (prototype == null ||
                    !string.Equals(AssetDatabase.GetAssetPath(prototype), sourcePath,
                        StringComparison.OrdinalIgnoreCase) ||
                    prototype.GetType() != prototypeType)
                    continue;

                int index = prototype is GPUInstancerDetailPrototype detailPrototype
                    ? detailPrototype.prototypeIndex
                    : ((GPUInstancerTreePrototype)prototype).prototypeIndex;
                if (index < 0 || !destinationByIndex.TryGetValue(index, out GPUInstancerPrototype replacement) ||
                    replacement == null || ReferenceEquals(replacement, prototype))
                    continue;

                prototypeList[i] = replacement;
                changed = true;
            }

            if (changed)
            {
                SyncProfilePrototypeList();
                #if UNITY_EDITOR
                GPUInstancerProjectSettings settings = GPUInstancerConstants.projectSettings;
                if (settings != null)
                    EditorUtility.SetDirty(settings);
                #endif
            }
        }

        private static void EnsureAssetFolder(string folder)
        {
            string normalized = folder.Replace('\\', '/').TrimEnd('/');
            string[] parts = normalized.Split('/');
            string current = parts.Length > 0 ? parts[0] : string.Empty;
            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                    continue;
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
#endif

        private static void RemoveTerrainSettings(GPUInstancerTerrainSettings terrainSettings)
        {
#if UNITY_EDITOR
            string assetPath = AssetDatabase.GetAssetPath(terrainSettings);
            if (GPUInstancerConstants.IsPackageAssetPath(assetPath))
            {
                Debug.LogWarning("Skipping deletion of package-owned terrain settings: " + assetPath);
                return;
            }
            if (!string.IsNullOrEmpty(assetPath))
                AssetDatabase.DeleteAsset(assetPath);
#endif
        }

        public virtual void UpdateTerrains()
        {
            _requiresTerrainUpdate = true;
        }

        public virtual void AddTerrain(Terrain newTerrain)
        {
            if (newTerrain == null)
                return;

            EnsureRuntimeTerrainList();
            // A proxy can be disabled and enabled again before the next Hub
            // tick. Cancel the pending removal in that case so the rebind is
            // not immediately undone by the queued operation.
            bool cancelledPendingRemoval = IsTerrainQueued(_terrainsToRemove, newTerrain);
            RemoveQueuedTerrain(_terrainsToRemove, newTerrain);
            if (_terrains.Contains(newTerrain))
            {
                // RemoveTerrain restores Unity's native renderer immediately,
                // before its queued list mutation is consumed. If the proxy is
                // re-enabled in that same frame, the terrain never leaves this
                // manager, so re-apply the channel's native-renderer takeover.
                // Without this step Unity and GPUI draw the same terrain data.
                if (cancelledPendingRemoval)
                    ReapplyTerrainSettingsAfterRebind(newTerrain);
                return;
            }
            if (IsTerrainQueued(_terrainsToAdd, newTerrain))
                return;

            if (_terrainsToAdd == null)
                _terrainsToAdd = new Queue<Terrain>();
            _terrainsToAdd.Enqueue(newTerrain);
        }

        public virtual void RemoveTerrain(Terrain terrainToRemove)
        {
            if (terrainToRemove == null)
                return;

            EnsureRuntimeTerrainList();
            if (!_terrains.Contains(terrainToRemove) &&
                !IsTerrainQueued(_terrainsToAdd, terrainToRemove))
                return;

            // Derived terrain managers temporarily set Unity's native
            // distance fields to zero while they own a terrain (this hides
            // the built-in renderer so GPUI can draw the instances).  The
            // queued removal below may be consumed before
            // ClearInstancingData() runs, which means the removed terrain is
            // no longer present in the manager's list and the old restore
            // code would never see it. Restore the field at the binding
            // boundary instead of relying on list iteration during teardown.
            // This is intentionally done only for a terrain that is already
            // live in the list; a not-yet-applied add has not been modified
            // by the manager and needs no restoration.
            if (_terrains.Contains(terrainToRemove))
                RestoreTerrainSettingsAfterUnbind(terrainToRemove);

            // Cancel a not-yet-applied add rather than allowing the terrain to
            // appear for one frame after a proxy is disabled.
            RemoveQueuedTerrain(_terrainsToAdd, terrainToRemove);
            if (_terrains.Contains(terrainToRemove) && !IsTerrainQueued(_terrainsToRemove, terrainToRemove))
            {
                if (_terrainsToRemove == null)
                    _terrainsToRemove = new Queue<Terrain>();
                _terrainsToRemove.Enqueue(terrainToRemove);
            }
        }

        /// <summary>
        /// Restores Unity terrain renderer settings when a terrain leaves this
        /// manager. Tree and detail channels override this with their
        /// respective distance field; the base implementation is a no-op so
        /// custom terrain managers remain unaffected.
        /// </summary>
        protected virtual void RestoreTerrainSettingsAfterUnbind(Terrain terrain)
        {
        }

        /// <summary>
        /// Re-applies the channel-specific Unity Terrain renderer override when
        /// a proxy cancels a queued removal before the manager's next Tick.
        /// </summary>
        protected virtual void ReapplyTerrainSettingsAfterRebind(Terrain terrain)
        {
        }

        public List<Terrain> GetTerrains()
        {
            EnsureRuntimeTerrainList();
            PruneRuntimeTerrainList();
            return _terrains;
        }

        private void EnsureRuntimeTerrainList()
        {
            if (_terrains != null)
                return;

            RebuildRuntimeTerrainList();
        }

        private void RebuildRuntimeTerrainList()
        {
            _terrains = new List<Terrain>();
            if (_terrain != null)
                _terrains.Add(_terrain);
            if (additionalTerrains != null)
            {
                for (int i = 0; i < additionalTerrains.Count; i++)
                {
                    Terrain additional = additionalTerrains[i];
                    if (additional != null && !_terrains.Contains(additional))
                        _terrains.Add(additional);
                }
            }
        }

        private void PruneRuntimeTerrainList()
        {
            if (_terrains == null)
                return;

            for (int i = _terrains.Count - 1; i >= 0; i--)
            {
                if (_terrains[i] == null)
                    _terrains.RemoveAt(i);
            }

            // Keep the runtime bookkeeping list free of destroyed scene
            // references. Profile managers use it as runtime bookkeeping;
            // cleaning both paths also prevents stale fake-null entries from
            // defeating queue de-duplication after an additive scene unload.
            if (profile != null && additionalTerrains != null)
            {
                for (int i = additionalTerrains.Count - 1; i >= 0; i--)
                {
                    Terrain additional = additionalTerrains[i];
                    if (additional == null)
                        additionalTerrains.RemoveAt(i);
                }
            }

            if (_terrain == null && _terrains.Count > 0)
                _terrain = _terrains[0];

            // The primary terrain should not also be listed as an additional
            // entry. This can happen when a previous primary is promoted after
            // a queued removal; removing the duplicate keeps rebuilds stable.
            if (profile != null && _terrain != null && additionalTerrains != null)
                additionalTerrains.Remove(_terrain);
        }

        private static bool IsTerrainQueued(Queue<Terrain> queue, Terrain terrain)
        {
            if (queue == null || terrain == null)
                return false;
            foreach (Terrain queued in queue)
            {
                if (ReferenceEquals(queued, terrain))
                    return true;
            }
            return false;
        }

        private static void RemoveQueuedTerrain(Queue<Terrain> queue, Terrain terrain)
        {
            if (queue == null || queue.Count == 0 || terrain == null)
                return;

            Queue<Terrain> kept = new Queue<Terrain>(queue.Count);
            while (queue.Count > 0)
            {
                Terrain queued = queue.Dequeue();
                if (!ReferenceEquals(queued, terrain))
                    kept.Enqueue(queued);
            }
            while (kept.Count > 0)
                queue.Enqueue(kept.Dequeue());
        }
    }
}
