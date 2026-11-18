using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    /// <summary>Retries Tree binding while a runtime Terrain is being loaded.</summary>
    public class GPUInstancerTerrainRuntimeHandler : MonoBehaviour
    {
        [HideInInspector]
        public Terrain terrain;

        private const float ResolveRetryInterval = 0.1f;
        private bool _resolveScheduled;

        private void Awake()
        {
            terrain = GetComponent<Terrain>();
        }

        private void Reset()
        {
            if (GetComponent<Terrain>() != null)
                return;

            Debug.LogError("GPUInstancerTerrainRuntimeHandler can only be added to a Terrain!");
            DestroyImmediate(this);
        }

        private void OnEnable()
        {
            terrain = GetComponent<Terrain>();
            CancelResolve();
            if (Application.isPlaying)
                StartResolve();
        }

        private void Update()
        {
            if (Application.isPlaying && !_resolveScheduled)
                StartResolve();
        }

        private void OnDisable()
        {
            Unbind();
        }

        private void OnDestroy()
        {
            Unbind();
        }

        private void StartResolve()
        {
            if (_resolveScheduled || !isActiveAndEnabled)
                return;

            _resolveScheduled = true;
            InvokeRepeating(nameof(ResolveAndBind), 0f, ResolveRetryInterval);
        }

        private void CancelResolve()
        {
            if (_resolveScheduled)
                CancelInvoke(nameof(ResolveAndBind));
            _resolveScheduled = false;
        }

        private void ResolveAndBind()
        {
            if (!isActiveAndEnabled)
            {
                CancelResolve();
                return;
            }

            terrain = GetComponent<Terrain>();
            if (terrain == null)
            {
                CancelResolve();
                return;
            }
            if (terrain.terrainData == null)
                return;

            GPUInstancerTerrainProxy proxy = GetComponent<GPUInstancerTerrainProxy>();
            if (proxy == null || string.IsNullOrEmpty(proxy.treeProfileId))
            {
                CancelResolve();
                return;
            }

            GPUInstancerProfile profile = GPUInstancerDriver.FindProfile(proxy.treeProfileId);
            if (profile == null)
            {
                if (GPUInstancerDriver.isBootstrapped)
                    CancelResolve();
                return;
            }
            if (!(profile is GPUInstancerTreeProfile) || !profile.enabled)
            {
                CancelResolve();
                return;
            }

            GPUInstancerTerrainBinder.Bind(proxy);
            if (proxy.runtimeTreeManager != null)
                CancelResolve();
        }

        private void Unbind()
        {
            CancelResolve();
            GPUInstancerTerrainProxy proxy = GetComponent<GPUInstancerTerrainProxy>();
            if (proxy != null)
                GPUInstancerTerrainBinder.Unbind(proxy, false);
        }
    }
}
