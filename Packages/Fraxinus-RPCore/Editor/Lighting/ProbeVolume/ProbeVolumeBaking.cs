using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace UnityEngine.Rendering
{
    internal abstract class ProbeVolumeBaking
    {
        internal abstract string backendId { get; }
        internal abstract TProbeReferenceVolumeBase referenceVolume { get; }
        internal abstract ProbeVolumeBakingSet activeBakingSet { get; }
        // Starts the APV-only baker without entering Unity's Lightmapping pipeline.
        // Backends which do not provide a standalone implementation keep the default.
        internal virtual bool BakeStandaloneAsync() => false;
        public abstract bool Cancel();
        public abstract bool isRunning { get; }
        internal abstract void Clear();
        internal abstract void Dispose();
        internal virtual bool CanFreezePlacement() => false;
        internal virtual HashSet<string> partialBakeSceneList { get; set; }
        internal virtual bool isFreezingPlacement { get; set; }
    }

    internal abstract partial class ProbeVolumeBaking<TVolume> : ProbeVolumeBaking
        where TVolume : TProbeReferenceVolumeBase
    {
        readonly ProbeVolumeBakingSetWeakReference m_BakingSet;
        bool m_BakePrepared;
        protected readonly TVolume volume;
        protected ProbeVolumeBakingSet bakingSet => m_BakingSet.Get();
        internal override TProbeReferenceVolumeBase referenceVolume => volume;
        internal override ProbeVolumeBakingSet activeBakingSet => bakingSet;

        internal bool PrepareBaking()
        {
            if (isRunning)
                return false;
            if (m_BakePrepared)
                return true;
            if (!InitializeBake() || !PrepareBakeData())
                return false;
            m_BakePrepared = true;
            return true;
        }
        protected abstract bool PrepareBakeData();
        protected virtual void PrepareBackend() { }
        protected virtual void ClearAlgorithmData() { }

        protected void CompleteBake()
        {
            m_BakePrepared = false;
            ProbeVolumeBakingProvider.partialBakeSceneList = null;
            ProbeVolumeBakingProvider.Complete(this);
            volume.checksDuringBakeAction = null;
        }

        protected ProbeVolumeBaking(TVolume volume, ProbeVolumeBakingSet bakingSet)
        {
            this.volume = volume;
            m_BakingSet = new ProbeVolumeBakingSetWeakReference(bakingSet);
        }
    }

    internal static class ProbeVolumeBakingProvider
    {
        static readonly Dictionary<string, ProbeVolumeEditorBackend> s_Registrations = new();
        static ProbeVolumeBaking s_Active;
        static ProbeVolumeBaking s_Running;
        static ProbeVolumeBaking s_RestartPending;
        static int s_RestartSceneHandle;
        static int[] s_RestartSceneHandles;
        static HashSet<string> s_PartialBakeSceneList;
        static bool s_IsFreezingPlacement;
        // A component can explicitly route startup to the selected registered
        // backend. The resulting session owns the request after async startup.
        static string s_RequestedBackendId;
        static ProbeVolumeBakingSet s_RequestedBakingSet;
        internal static ProbeVolumeBakingSet requestedBakingSet => s_RequestedBakingSet;

        static IProbeReferenceVolumeBackend RuntimeBackend
        {
            get
            {
                if (s_Running != null && ProbeReferenceVolumeProvider.TryGetBackend(s_Running.backendId, out var running))
                    return running;
                if (!string.IsNullOrEmpty(s_RequestedBackendId) &&
                    ProbeReferenceVolumeProvider.TryGetBackend(s_RequestedBackendId, out var requested))
                    return requested;
                return ProbeReferenceVolumeProvider.activeBackend;
            }
        }

        internal static ProbeVolumeBaking active => Get();
        internal static ProbeVolumeEditorBackend backend
        {
            get
            {
                var runtime = RuntimeBackend;
                return runtime != null && s_Registrations.TryGetValue(runtime.id, out var editor) ? editor : null;
            }
        }
        internal static ProbeVolumeBaking activeOrNull => s_Running ?? s_Active;
        internal static bool isRunning => s_RestartPending != null || (s_Running?.isRunning ?? false);

        // Only the backend enabled and initialized by the active pipeline is
        // available. Registered implementations alone do not enable baking.
        internal static bool IsBackendAvailable(string backendId)
        {
            if (string.IsNullOrEmpty(backendId) ||
                !ProbeReferenceVolumeProvider.TryGetBackend(backendId, out var runtimeBackend) ||
                !s_Registrations.TryGetValue(backendId, out var registration) || !registration.supportsBaking)
                return false;

            var context = ProbeReferenceVolumeProvider.context;
            if (context == null)
                return false;

            return runtimeBackend.volume?.isInitialized == true &&
                runtimeBackend.volume.enabledBySRP &&
                runtimeBackend.CanUse(context.parameters, out _);
        }
        internal static HashSet<string> partialBakeSceneList
        {
            get => s_Running?.partialBakeSceneList ?? s_Active?.partialBakeSceneList ?? s_PartialBakeSceneList;
            set
            {
                s_PartialBakeSceneList = value;
                if (s_Active != null) s_Active.partialBakeSceneList = value;
                if (s_Running != null) s_Running.partialBakeSceneList = value;
            }
        }
        internal static bool isFreezingPlacement
        {
            get => s_Running?.isFreezingPlacement ?? s_Active?.isFreezingPlacement ?? s_IsFreezingPlacement;
            set
            {
                s_IsFreezingPlacement = value;
                if (s_Active != null) s_Active.isFreezingPlacement = value;
                if (s_Running != null) s_Running.isFreezingPlacement = value;
            }
        }

        static ProbeVolumeBakingProvider()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseForDomainReload;
            EditorApplication.quitting += ReleaseForDomainReload;
        }

        internal static void Register(ProbeVolumeEditorBackend backend)
        {
            if (backend == null || string.IsNullOrEmpty(backend.id))
                throw new ArgumentException("A probe-volume editor backend requires an id.", nameof(backend));
            if (s_Registrations.ContainsKey(backend.id))
                throw new InvalidOperationException($"Probe Volume editor backend '{backend.id}' is already registered.");
            s_Registrations.Add(backend.id, backend);
        }

        internal static ProbeVolumeBaking Get(ProbeVolumeBakingSet bakingSet = null, bool logError = true)
        {
            if (s_Running != null)
                return s_Running;

            var backend = RuntimeBackend;
            if (backend == null)
            {
                if (logError) Debug.LogError("No active Probe Volume backend is available for baking.");
                return null;
            }

            if (!s_Registrations.TryGetValue(backend.id, out var registration))
            {
                if (logError) Debug.LogError($"Probe Volume backend '{backend.id}' has no editor baking implementation.");
                return null;
            }

            if (!registration.supportsBaking)
                return null;

            bakingSet ??= s_RequestedBakingSet ?? ProbeVolumeBakingSet.GetBakingSetForScene(SceneManager.GetActiveScene()) ?? backend.volume?.currentBakingSet;
            if (bakingSet == null)
            {
                if (logError) Debug.LogError($"Probe Volume backend '{backend.id}' has no Baking Set.");
                return null;
            }

            if (s_Active != null && (s_Active.referenceVolume != backend.volume || s_Active.backendId != backend.id || s_Active.activeBakingSet != bakingSet))
            {
                ClearPendingRestart();
                var partialBakeSceneList = s_PartialBakeSceneList;
                s_Active.Dispose();
                s_Active = null;
                s_PartialBakeSceneList = partialBakeSceneList;
            }

            if (s_Active == null)
            {
                s_Active = registration.CreateBaking(backend.volume, bakingSet);
                if (s_Active == null)
                {
                    Debug.LogError($"Probe Volume backend '{backend.id}' could not create its editor baking implementation.");
                    return null;
                }
                s_Active.partialBakeSceneList = s_PartialBakeSceneList;
                s_Active.isFreezingPlacement = s_IsFreezingPlacement;
            }

            return s_Active;
        }

        static ProbeVolumeBaking PrepareBake()
        {
            var backend = RuntimeBackend;
            if (backend == null || !s_Registrations.TryGetValue(backend.id, out var registration))
            {
                Debug.LogError(backend == null ? "No active Probe Volume backend is available for baking." : $"Probe Volume backend '{backend.id}' has no editor baking implementation.");
                return null;
            }

            if (!registration.supportsBaking)
                return null;

            if (!registration.PrepareBaking())
                return null;

            var baking = Get();
            if (baking != null)
                baking.isFreezingPlacement = baking.activeBakingSet.freezePlacement;
            return baking;
        }

        internal static bool Cancel()
        {
            bool hadPendingRestart = s_RestartPending != null;
            ClearPendingRestart();
            var baking = s_Running;
            if (baking != null && baking.Cancel())
                return true;
            return hadPendingRestart;
        }

        internal static bool BakeStandaloneAsync()
        {
            // UI callers enter through this provider directly, so keep the
            // public completion state in sync with both UI and API starts.
            AdaptiveProbeVolumes.ResetLastBakeResult();
            ClearPendingRestart();
            if (s_Running != null)
                return false;

            var baking = PrepareBake();
            if (baking == null)
                return false;

            s_Running = baking;
            try
            {
                if (baking.BakeStandaloneAsync())
                    return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                CancelRunning();
            }

            s_Running = null;
            return false;
        }

        /// <summary>
        /// Starts a standalone bake for the specified enabled backend. The
        /// routing override is scoped to startup; the session keeps ownership
        /// until the asynchronous bake completes or is cancelled.
        /// </summary>
        internal static bool BakeStandaloneAsync(string backendId)
        {
            if (string.IsNullOrEmpty(backendId))
                return BakeStandaloneAsync();

            if (!IsBackendAvailable(backendId))
            {
                Debug.LogWarning($"Probe Volume backend '{backendId}' is not enabled or has no editor baking implementation.");
                return false;
            }

            var previous = s_RequestedBackendId;
            s_RequestedBackendId = backendId;
            try
            {
                return BakeStandaloneAsync();
            }
            finally
            {
                s_RequestedBackendId = previous;
            }
        }

        // Component inspectors may target a different scene than the active
        // scene or Lighting window. Keep their explicit set scoped to startup;
        // the created baking session retains it throughout the async bake.
        internal static bool BakeStandaloneAsync(string backendId, ProbeVolumeBakingSet bakingSet)
        {
            if (bakingSet == null || isRunning)
                return false;
            var previous = s_RequestedBakingSet;
            s_RequestedBakingSet = bakingSet;
            try { return BakeStandaloneAsync(backendId); }
            finally { s_RequestedBakingSet = previous; }
        }

        // Clearing assets must work even when no baker has been created since
        // the last domain reload. The UI passes its selected set explicitly.
        internal static bool ClearBakedData(ProbeVolumeBakingSet bakingSet, string backendId)
        {
            if (isRunning)
                return false;
            var previous = s_RequestedBackendId;
            s_RequestedBackendId = backendId;
            try { return ClearBakedData(bakingSet); }
            finally { s_RequestedBackendId = previous; }
        }

        internal static bool ClearBakedData(ProbeVolumeBakingSet bakingSet = null)
        {
            ClearPendingRestart();
            if (s_Running != null && s_Running.isRunning && !s_Running.Cancel())
                return false;

            var baking = Get(bakingSet, false);
            if (baking == null)
                return false;

            baking.Clear();
            AssetDatabase.SaveAssetIfDirty(baking.activeBakingSet);
            return true;
        }

        internal static void RestartAfterSceneChanges(ProbeVolumeBaking baking)
        {
            if (!ReferenceEquals(s_Running, baking) || !Cancel())
                return;

            // Keep the restart owned by this backend and scene setup until the
            // standalone cancellation cleanup has completed.
            s_RestartPending = baking;
            s_RestartSceneHandle = SceneManager.GetActiveScene().handle;
            s_RestartSceneHandles = new int[SceneManager.sceneCount];
            for (int i = 0; i < s_RestartSceneHandles.Length; ++i)
                s_RestartSceneHandles[i] = SceneManager.GetSceneAt(i).handle;
            EditorApplication.update += RestartWhenStandaloneBakeStops;
        }

        static void ClearPendingRestart()
        {
            EditorApplication.update -= RestartWhenStandaloneBakeStops;
            s_RestartPending = null;
            s_RestartSceneHandles = null;
        }

        static void RestartWhenStandaloneBakeStops()
        {
            var baking = s_RestartPending;
            var backend = ProbeReferenceVolumeProvider.activeBackend;
            bool sceneSetupMatches = s_RestartSceneHandles != null &&
                s_RestartSceneHandles.Length == SceneManager.sceneCount &&
                s_RestartSceneHandle == SceneManager.GetActiveScene().handle;
            for (int i = 0; sceneSetupMatches && i < s_RestartSceneHandles.Length; ++i)
                sceneSetupMatches = s_RestartSceneHandles[i] == SceneManager.GetSceneAt(i).handle;

            if (baking == null || !ReferenceEquals(s_Active, baking) ||
                backend?.volume != baking.referenceVolume || baking.activeBakingSet == null ||
                !sceneSetupMatches || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                ClearPendingRestart();
                return;
            }

            if (baking.isRunning)
                return;

            ClearPendingRestart();
            BakeStandaloneAsync();
        }

        internal static void Complete(ProbeVolumeBaking baking)
        {
            if (ReferenceEquals(s_Running, baking))
                s_Running = null;
        }

        internal static bool CanFreezePlacement(ProbeVolumeBakingSet bakingSet)
        {
            var backend = ProbeReferenceVolumeProvider.activeBackend;
            if (bakingSet == null || backend?.volume?.isInitialized != true)
                return false;

            return s_Registrations.TryGetValue(backend.id, out var registration) && registration.CanFreezePlacement(backend.volume, bakingSet);
        }

        internal static void DisposeActive()
        {
            ClearPendingRestart();
            if (s_Running != null)
            {
                CancelRunning();
                return;
            }
            s_Active?.Dispose();
            s_Active = null;
        }

        static void CancelRunning()
        {
            var baking = s_Running;
            s_Running = null;
            if (baking == null)
                return;
            try { baking.Dispose(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        static void ReleaseForDomainReload()
        {
            ClearPendingRestart();
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseForDomainReload;
            EditorApplication.quitting -= ReleaseForDomainReload;
            if (s_Running != null) CancelRunning();
            s_Active?.Dispose();
            s_Active = null;
        }
    }

}
