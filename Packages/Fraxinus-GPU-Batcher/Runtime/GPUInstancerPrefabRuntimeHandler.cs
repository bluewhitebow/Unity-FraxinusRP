using System.Collections.Generic;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    public class GPUInstancerPrefabRuntimeHandler : MonoBehaviour
    {
        [HideInInspector]
        public GPUInstancerPrefab gpuiPrefab;

        [System.NonSerialized]
        private GPUInstancerPrefabManager _prefabManager;
        private static Dictionary<GPUInstancerPrefabPrototype, GPUInstancerPrefabManager> _managerDictionary;

        /// <summary>
        /// Recreates the process-local lookup after a subsystem reset.  With
        /// domain reload disabled Unity can keep an already-enabled component
        /// alive while the static dictionary is cleared; the next OnEnable (or
        /// a direct lookup from user code) must therefore be able to rebuild it
        /// instead of dereferencing a null map.
        /// </summary>
        private static void EnsureManagerDictionary()
        {
            if (_managerDictionary != null)
                return;

            _managerDictionary = new Dictionary<GPUInstancerPrefabPrototype, GPUInstancerPrefabManager>();
            IReadOnlyList<GPUInstancerManager> prefabManagers = GPUInstancerManagerHub.activeManagers;
            if (prefabManagers == null)
                return;

            for (int i = 0; i < prefabManagers.Count; i++)
            {
                GPUInstancerPrefabManager manager = prefabManagers[i] as GPUInstancerPrefabManager;
                if (manager == null || manager.isDisposed || manager.prototypeList == null)
                    continue;
                for (int p = 0; p < manager.prototypeList.Count; p++)
                {
                    GPUInstancerPrefabPrototype prototype = manager.prototypeList[p] as GPUInstancerPrefabPrototype;
                    if (prototype != null && !_managerDictionary.ContainsKey(prototype))
                        _managerDictionary.Add(prototype, manager);
                }
            }
        }

        /// <summary>
        /// Clears the process-local prototype lookup at a Unity subsystem
        /// boundary.  Enter Play Mode can be configured with domain reload
        /// disabled, in which case the dictionary would otherwise retain
        /// prototype references and Manager instances from the previous session even after
        /// Driver has disposed those transient managers.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetManagerDictionary()
        {
            ClearManagerDictionary();
        }

        /// <summary>
        /// Explicit lifecycle hook used by the Driver when it shuts down
        /// without a managed-domain reset (for example an editor preview or
        /// a host that swaps runtime configurations in-place).
        /// </summary>
        internal static void ClearManagerDictionary()
        {
            if (_managerDictionary != null)
                _managerDictionary.Clear();
            _managerDictionary = null;
        }

        /// <summary>
        /// Removes every prototype alias for a Manager that is leaving the
        /// Hub. Managers are plain managed objects, so a static value would
        /// otherwise keep the complete runtime graph reachable until another
        /// handler queried the same prototype or Driver shut down.
        /// </summary>
        internal static void RemoveManager(GPUInstancerPrefabManager manager)
        {
            if (_managerDictionary == null || ReferenceEquals(manager, null))
                return;

            List<GPUInstancerPrefabPrototype> staleKeys = null;
            foreach (KeyValuePair<GPUInstancerPrefabPrototype, GPUInstancerPrefabManager> pair in _managerDictionary)
            {
                GPUInstancerPrefabManager mapped = pair.Value;
                if (!ReferenceEquals(mapped, manager) && mapped != null && !mapped.isDisposed)
                    continue;

                if (staleKeys == null)
                    staleKeys = new List<GPUInstancerPrefabPrototype>();
                staleKeys.Add(pair.Key);
            }

            if (staleKeys == null)
                return;
            for (int i = 0; i < staleKeys.Count; i++)
                _managerDictionary.Remove(staleKeys[i]);
        }

        private void Awake()
        {
            gpuiPrefab = GetComponent<GPUInstancerPrefab>();
            EnsureManagerDictionary();
        }

        private void OnEnable()
        {
            if (gpuiPrefab == null)
                gpuiPrefab = GetComponent<GPUInstancerPrefab>();
            if (gpuiPrefab == null)
                return;

            if (gpuiPrefab.state == PrefabInstancingState.None)
            {
                if (_prefabManager == null || _prefabManager.isDisposed)
                    _prefabManager = GetPrefabManager();
                if (_prefabManager != null && !_prefabManager.isDisposed)
                {
                    if (!_prefabManager.isInitialized)
                        _prefabManager.InitializeRuntimeDataAndBuffers();
                    _prefabManager.AddPrefabInstance(gpuiPrefab, true);
                }
            }
        }

        private void OnDisable()
        {
            if (gpuiPrefab == null)
                return;

            if (gpuiPrefab.state == PrefabInstancingState.Instanced)
            {
                if (_prefabManager == null || _prefabManager.isDisposed)
                    _prefabManager = GetPrefabManager();
                    if (_prefabManager != null && !_prefabManager.isDisposed)
                    _prefabManager.RemovePrefabInstance(gpuiPrefab, false);
            }
        }

        private GPUInstancerPrefabManager GetPrefabManager()
        {
            GPUInstancerPrefabManager prefabManager = null;
            if (gpuiPrefab == null)
                return null;

            EnsureManagerDictionary();
            IReadOnlyList<GPUInstancerManager> managers = GPUInstancerManagerHub.activeManagers;
            if (managers != null && managers.Count > 0)
            {
                if (!_managerDictionary.TryGetValue(gpuiPrefab.prefabPrototype, out prefabManager) ||
                    prefabManager == null || prefabManager.isDisposed ||
                    !prefabManager.CanResolvePrototype(gpuiPrefab.prefabPrototype))
                {
                    prefabManager = FindPrefabManager(managers);
                    if (prefabManager == null)
                    {
                        Debug.LogWarning("Can not find GPUI Prefab Manager for prototype: " + gpuiPrefab.prefabPrototype);
                        return null;
                    }
                    _managerDictionary[gpuiPrefab.prefabPrototype] = prefabManager;
                }
                if (prefabManager == null || prefabManager.isDisposed ||
                    !prefabManager.CanResolvePrototype(gpuiPrefab.prefabPrototype))
                {
                    prefabManager = FindPrefabManager(managers);
                    if (prefabManager == null)
                        return null;
                    _managerDictionary[gpuiPrefab.prefabPrototype] = prefabManager;
                }
            }
            return prefabManager;
        }

        private GPUInstancerPrefabManager FindPrefabManager(IReadOnlyList<GPUInstancerManager> managers)
        {
            for (int i = 0; i < managers.Count; i++)
            {
                GPUInstancerPrefabManager manager = managers[i] as GPUInstancerPrefabManager;
                if (manager != null && !manager.isDisposed &&
                    manager.CanResolvePrototype(gpuiPrefab.prefabPrototype))
                    return manager;
            }
            return null;
        }
    }
}
