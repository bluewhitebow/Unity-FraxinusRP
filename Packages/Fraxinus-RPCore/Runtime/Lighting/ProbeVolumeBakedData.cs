using System;
#if UNITY_EDITOR
using System.Collections.Generic;
#endif

namespace UnityEngine.Rendering
{
    [Serializable]
    internal abstract class ProbeVolumeBakedData
    {
        public abstract string backendId { get; }

        [SerializeField] internal Vector3Int maxCellPosition;
        [SerializeField] internal Vector3Int minCellPosition;
        [SerializeField] internal Bounds globalBounds;
        [SerializeField] internal int bakedSimplificationLevels = -1;
        [SerializeField] internal float bakedMinDistanceBetweenProbes = -1.0f;
        [SerializeField] internal Vector3 bakedProbeOffset = Vector3.zero;

        internal abstract bool HasBakedData(string scenario, bool supportLightingScenarios);
        internal abstract bool HasValidSharedData();
        internal abstract bool HasValidScenarioData(string scenario, TProbeReferenceVolumeBase volume);
        internal abstract bool HasSupportData();
        internal abstract void CleanupStreamableAssets();

        internal virtual bool CheckCompatibleCellLayout(ProbeVolumeBakingSet bakingSet)
        {
            return bakingSet.simplificationLevels == bakedSimplificationLevels &&
                bakingSet.minDistanceBetweenProbes == bakedMinDistanceBetweenProbes &&
                bakingSet.probeOffset == bakedProbeOffset;
        }

        internal virtual void OnAfterDeserialize(ProbeVolumeBakingSet bakingSet)
        {
            if (bakedSimplificationLevels == -1)
            {
                bakedSimplificationLevels = bakingSet.simplificationLevels;
                bakedMinDistanceBetweenProbes = bakingSet.minDistanceBetweenProbes;
                bakedProbeOffset = bakingSet.probeOffset;
            }
        }

        internal virtual void Initialize(TProbeReferenceVolumeBase referenceVolume) { }

#if UNITY_EDITOR
        internal abstract string streamingAssetsPath { get; }
        internal abstract int scenarioCount { get; }
        internal abstract IEnumerable<ProbeVolumeBuildResource> GetBuildResources(IReadOnlyList<RenderPipelineAsset> renderPipelineAssets);
        internal abstract bool TryGetScenarioHash(string scenario, out int sceneHash);
        internal abstract long GetDiskSizeOfSharedData();
        internal abstract long GetDiskSizeOfScenarioData(string scenario);
        internal abstract void ClearAssets();
        internal abstract void RemoveScenario(string scenario);
        internal abstract void RenameScenario(string scenario, string newName, string assetName);
        internal abstract void EnsureScenarioAssetNames(string assetName);

        // Backends own the assets referenced by a baking set. This hook lets the
        // weak reference release those assets without exposing backend fields to
        // the common runtime layer.
        internal virtual void UnloadAssetsForWeakReference() { }
#endif
    }

#if UNITY_EDITOR
    internal readonly struct ProbeVolumeBuildResource
    {
        internal readonly ProbeVolumeStreamableAsset asset;
        internal readonly bool includeInBuild;

        internal ProbeVolumeBuildResource(ProbeVolumeStreamableAsset asset, bool includeInBuild)
        {
            this.asset = asset;
            this.includeInBuild = includeInBuild;
        }
    }
#endif
}
