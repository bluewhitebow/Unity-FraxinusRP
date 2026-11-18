using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    public class GPUInstancerPrefabListRuntimeHandler : MonoBehaviour
    {
        [NonSerialized]
        public GPUInstancerPrefabManager prefabManager;
        private IEnumerable<GPUInstancerPrefab> _gpuiPrefabs;
        private bool _isIntancesAdded;
        private bool _pendingManagerResolution;

        private void OnEnable()
        {
            _pendingManagerResolution = true;
            TryRegisterInstances();
        }

        private void Update()
        {
            if (_pendingManagerResolution)
                TryRegisterInstances();
        }

        private void TryRegisterInstances()
        {
            if (!TryResolveManager())
                return;
            _pendingManagerResolution = false;

            if (prefabManager.prototypeList != null &&
                !prefabManager.prototypeList.All(p =>
                    p is GPUInstancerPrefabPrototype prefabPrototype && prefabPrototype.meshRenderersDisabled))
            {
                Debug.LogWarning("GPUInstancerPrefabListRuntimeHandler can not run in Threads while Mesh Renderers are enabled on the prefabs. Disabling threading...");
            }

            _gpuiPrefabs = gameObject.GetComponentsInChildren<GPUInstancerPrefab>(true);

            if (_gpuiPrefabs != null && _gpuiPrefabs.Count() > 0)
            {
                _isIntancesAdded = true;
                prefabManager.AddPrefabInstances(_gpuiPrefabs);
            }
        }

        private bool TryResolveManager()
        {
            if (prefabManager != null && !prefabManager.isDisposed)
                return true;

            prefabManager = null;
            IReadOnlyList<GPUInstancerManager> managers = GPUInstancerManagerHub.activeManagers;
            for (int i = 0; i < managers.Count; i++)
            {
                GPUInstancerPrefabManager candidate = managers[i] as GPUInstancerPrefabManager;
                if (candidate == null || candidate.isDisposed)
                    continue;
                prefabManager = candidate;
                return true;
            }
            return false;
        }

        private void OnDisable()
        {
            _isIntancesAdded = false;
            _pendingManagerResolution = false;

            if (prefabManager != null && !prefabManager.isDisposed &&
                _gpuiPrefabs != null && _gpuiPrefabs.Count() > 0)
            {
                prefabManager.RemovePrefabInstances(_gpuiPrefabs);
            }
            _gpuiPrefabs = null;
        }

        public void SetManager(GPUInstancerPrefabManager prefabManager)
        {
            if (_isIntancesAdded)
                OnDisable();
            this.prefabManager = prefabManager != null && !prefabManager.isDisposed
                ? prefabManager
                : null;
            if (isActiveAndEnabled)
                OnEnable();
        }
    }
}
