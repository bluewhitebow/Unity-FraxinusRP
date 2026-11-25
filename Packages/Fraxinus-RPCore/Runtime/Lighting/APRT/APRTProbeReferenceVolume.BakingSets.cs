using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering
{
    internal sealed partial class APRTProbeReferenceVolume
    {
        // The provider remains one backend. Each loaded set owns an independent
        // runtime, so local cell IDs, layouts and scenario names never alias.
        readonly Dictionary<ProbeVolumeBakingSet, APRTProbeReferenceVolume> m_SetRuntimes = new();
        readonly List<APRTProbeReferenceVolume> m_LoadedSetRuntimes = new();
        readonly List<APRTProbeReferenceVolume> m_RetiredSetRuntimes = new();
        readonly List<APRTProbeReferenceVolume> m_PinnedSetRuntimes = new();
        readonly List<APRTProbeReferenceVolume> m_RenderingSetRuntimes = new();
        APRTProbeReferenceVolume m_RuntimeOwner;
        ProbeVolumeSystemParameters m_SystemParameters;
        internal IReadOnlyList<APRTProbeReferenceVolume> loadedBakingSetRuntimes => m_LoadedSetRuntimes;
        APRTProbeReferenceVolume focusedRuntime => currentBakingSet != null &&
            m_SetRuntimes.TryGetValue(currentBakingSet, out var runtime) ? runtime : null;
        bool coordinatesBakingSets => m_SetRuntimes.Count != 0;

        internal APRTProbeReferenceVolume GetBakingSetRuntime(ProbeVolumeBakingSet set, bool create = false)
        {
            if (set == null) return null;
            if (m_RuntimeOwner != null) return currentBakingSet == set ? this : null;
            if (m_SetRuntimes.TryGetValue(set, out var runtime)) return runtime;
            if (!create) return !coordinatesBakingSets && currentBakingSet == set ? this : null;
            if (!coordinatesBakingSets) ReloadBakedData();
            // New sets are prepared as a whole when a camera next uses them.
            runtime = new APRTProbeReferenceVolume { m_RuntimeOwner = this };
            runtime.Initialize(m_SystemParameters);
            runtime.SetActiveBakingSet(set);
            runtime.m_APRTDebugMode = m_APRTDebugMode;
            m_SetRuntimes.Add(set, runtime);
            m_LoadedSetRuntimes.Add(runtime);
            return runtime;
        }

        void RegisterSceneBakingSet(string sceneGUID, ProbeVolumeBakingSet set)
        {
            var runtime = GetBakingSetRuntime(set, true);
            if (runtime.m_LoadedScenes.Add(sceneGUID)) runtime.m_LoadedCellsDirty = true;
            if (currentBakingSet == null) m_BakingSet.Set(set);
        }

        void RemoveSceneBakingSet(string sceneGUID)
        {
            for (int i = m_LoadedSetRuntimes.Count - 1; i >= 0; --i)
            {
                var runtime = m_LoadedSetRuntimes[i];
                if (!runtime.m_LoadedScenes.Remove(sceneGUID)) continue;
                runtime.m_LoadedCellsDirty = true;
                if (runtime.m_LoadedScenes.Count != 0) continue;
                var set = runtime.currentBakingSet;
                m_SetRuntimes.Remove(set);
                m_LoadedSetRuntimes.RemoveAt(i);
                if (m_ResourcesPinned || runtime.m_ResourcesPinned) m_RetiredSetRuntimes.Add(runtime);
                else runtime.Cleanup();
                if (currentBakingSet == set)
                    m_BakingSet.Set(m_LoadedSetRuntimes.Count > 0 ? m_LoadedSetRuntimes[0].currentBakingSet : null);
            }
            if (m_LoadedSetRuntimes.Count == 0 && !m_ResourcesPinned) ReleaseSamplingAtlas();
            m_RenderingSetRuntimes.Clear();
        }

        void ReleaseRetiredBakingSets()
        {
            foreach (var runtime in m_RetiredSetRuntimes)
            {
                runtime.m_ResourcesPinned = false;
                runtime.Cleanup();
            }
            m_RetiredSetRuntimes.Clear();
        }

        void ClearBakingSetRuntimes()
        {
            foreach (var runtime in m_LoadedSetRuntimes)
            {
                if (runtime.m_ResourcesPinned) m_RetiredSetRuntimes.Add(runtime);
                else runtime.Cleanup();
            }
            m_LoadedSetRuntimes.Clear();
            m_SetRuntimes.Clear();
            m_RenderingSetRuntimes.Clear();
            if (!m_ResourcesPinned) { ReleaseRetiredBakingSets(); ReleaseSamplingAtlas(); }
        }

        internal IReadOnlyList<APRTProbeReferenceVolume> GetRenderingBakingSetRuntimes(Camera camera)
        {
            if (m_ResourcesPinned)
            {
                // Keep retired buffers alive for recorded cameras, but exclude
                // their lighting from cameras recorded after scene removal.
                m_RenderingSetRuntimes.Clear();
                foreach (var runtime in m_PinnedSetRuntimes)
                    if (ReferenceEquals(runtime, this) ? currentBakingSet != null : m_LoadedSetRuntimes.Contains(runtime))
                        m_RenderingSetRuntimes.Add(runtime);
                return m_RenderingSetRuntimes;
            }
            if (coordinatesBakingSets) return m_LoadedSetRuntimes;
            m_RenderingSetRuntimes.Clear();
            if (currentBakingSet != null) m_RenderingSetRuntimes.Add(this);
            return m_RenderingSetRuntimes;
        }

        internal void PinBakingSetRuntimesForFrame(IReadOnlyList<APRTProbeReferenceVolume> contexts)
        {
            if (m_ResourcesPinned) return;
            m_ResourcesPinned = contexts.Count > 0;
            m_PinnedSetRuntimes.Clear();
            for (int i = 0; i < contexts.Count; ++i)
            {
                var context = contexts[i];
                m_PinnedSetRuntimes.Add(context);
                context.PinRuntimeResourcesForFrame();
            }
        }
    }
}
