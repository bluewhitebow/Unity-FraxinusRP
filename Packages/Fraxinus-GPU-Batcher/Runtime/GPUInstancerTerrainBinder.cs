using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    /// <summary>Connects scene Terrain proxies to profile-owned runtime managers.</summary>
    public static class GPUInstancerTerrainBinder
    {
        public static void BindLoadedProxies()
        {
            if (!Application.isPlaying)
                return;

            GPUInstancerTerrainProxy[] proxies = Resources.FindObjectsOfTypeAll<GPUInstancerTerrainProxy>();
            for (int i = 0; i < proxies.Length; i++)
            {
                GPUInstancerTerrainProxy proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled ||
                    proxy.gameObject == null ||
                    !proxy.gameObject.scene.IsValid() ||
                    !proxy.gameObject.scene.isLoaded)
                    continue;

                Bind(proxy);
            }
        }

        public static void Bind(GPUInstancerTerrainProxy proxy)
        {
            if (proxy == null)
                return;

            Terrain terrain = proxy.GetComponent<Terrain>();
            if (terrain == null)
                return;

            BindProfile(proxy, terrain,
                GPUInstancerDriver.FindProfile(proxy.detailProfileId) as GPUInstancerDetailProfile, true);
            BindProfile(proxy, terrain,
                GPUInstancerDriver.FindProfile(proxy.treeProfileId) as GPUInstancerTreeProfile, false);
        }

        public static void Unbind(GPUInstancerTerrainProxy proxy)
        {
            Unbind(proxy, true);
            Unbind(proxy, false);
        }

        public static void Unbind(GPUInstancerTerrainProxy proxy, bool detail)
        {
            if (proxy == null)
                return;

            Terrain terrain = proxy.GetComponent<Terrain>();
            if (terrain == null)
                return;

            GPUInstancerTerrainManager manager = detail
                ? (GPUInstancerTerrainManager)proxy.runtimeDetailManager
                : proxy.runtimeTreeManager;

            if (manager == null)
            {
                string profileId = detail ? proxy.detailProfileId : proxy.treeProfileId;
                if (GPUInstancerDriver.TryGet(profileId, out GPUInstancerManager resolved))
                    manager = resolved as GPUInstancerTerrainManager;
            }

            UnbindManager(manager, terrain);
            proxy.SetRuntimeManager(null, detail);
        }

        private static void BindProfile(
            GPUInstancerTerrainProxy proxy,
            Terrain terrain,
            GPUInstancerProfile profile,
            bool detail)
        {
            GPUInstancerTerrainManager current = detail
                ? (GPUInstancerTerrainManager)proxy.runtimeDetailManager
                : proxy.runtimeTreeManager;

            if (profile == null || !profile.enabled)
            {
                UnbindManager(current, terrain);
                proxy.SetRuntimeManager(null, detail);
                return;
            }

            if (!HasMatchingPrototypes(profile, terrain, detail))
            {
                Debug.LogError(
                    "Terrain '" + terrain.name + "' does not match Profile '" + profile.displayName + "'.",
                    terrain);
                return;
            }

            GPUInstancerTerrainManager manager =
                GPUInstancerDriver.GetOrCreate(profile) as GPUInstancerTerrainManager;
            if (manager == null)
                return;

            if (current != null && !ReferenceEquals(current, manager))
                UnbindManager(current, terrain);

            BindManager(manager, terrain);
            proxy.SetRuntimeManager(manager, detail);

            if (Application.isPlaying && manager.isActive &&
                !manager.isInitialized && manager.terrain != null)
                manager.InitializeRuntimeDataAndBuffers(true);
        }

        private static void BindManager(GPUInstancerTerrainManager manager, Terrain terrain)
        {
            if (!IsAlive(manager) || terrain == null)
                return;

            List<Terrain> terrains = manager.GetTerrains();
            if (terrains == null || terrains.Count == 0)
                manager.SetupManagerWithTerrain(terrain);
            else
                manager.AddTerrain(terrain);

            if (manager.additionalTerrains == null)
                manager.additionalTerrains = new List<Terrain>();
            if (manager.terrain != terrain && !manager.additionalTerrains.Contains(terrain))
                manager.additionalTerrains.Add(terrain);
        }

        private static void UnbindManager(GPUInstancerTerrainManager manager, Terrain terrain)
        {
            if (!IsAlive(manager) || terrain == null)
                return;

            manager.RemoveTerrain(terrain);
            manager.additionalTerrains?.Remove(terrain);
        }

        private static bool HasMatchingPrototypes(
            GPUInstancerProfile profile,
            Terrain terrain,
            bool detail)
        {
            if (terrain.terrainData == null)
                return false;
            if (profile.prototypeList == null || profile.prototypeList.Count == 0)
                return true;

            if (!detail)
            {
                TreePrototype[] source = terrain.terrainData.treePrototypes;
                if (source == null || source.Length != profile.prototypeList.Count)
                    return false;

                for (int i = 0; i < source.Length; i++)
                {
                    GPUInstancerTreePrototype prototype =
                        profile.prototypeList[i] as GPUInstancerTreePrototype;
                    if (prototype == null || prototype.prefabObject != source[i].prefab)
                        return false;
                }
                return true;
            }

            DetailPrototype[] details = terrain.terrainData.detailPrototypes;
            if (details == null || details.Length != profile.prototypeList.Count)
                return false;

            for (int i = 0; i < details.Length; i++)
            {
                GPUInstancerDetailPrototype prototype =
                    profile.prototypeList[i] as GPUInstancerDetailPrototype;
                if (prototype == null ||
                    prototype.prefabObject != details[i].prototype ||
                    prototype.prototypeTexture != details[i].prototypeTexture)
                    return false;
            }
            return true;
        }

        private static bool IsAlive(GPUInstancerManager manager)
        {
            return manager != null && !manager.isDisposed;
        }
    }
}
