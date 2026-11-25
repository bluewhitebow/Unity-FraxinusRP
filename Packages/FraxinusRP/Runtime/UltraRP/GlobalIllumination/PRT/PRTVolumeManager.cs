using System.Collections.Generic;
using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Manages all active PRT probe volumes in the scene
    /// </summary>
    public class PRTVolumeManager
    {
        private static readonly List<PRTProbeVolume> s_activeVolumes = new();
        private static readonly List<PRTProbeAdjustmentVolume> s_adjustmentVolumes = new();
        private static readonly Dictionary<ReflectionProbe, ReflectionProbeAdditionalData> s_reflectionProbeData = new();

        public static bool isBaking { get; set; }

        public static void RegisterProbeVolume(PRTProbeVolume volume)
        {
            if (!s_activeVolumes.Contains(volume))
            {
                s_activeVolumes.Add(volume);
            }
        }

        public static void UnregisterProbeVolume(PRTProbeVolume volume)
        {
            s_activeVolumes.Remove(volume);
        }

        public static void RegisterAdjustmentVolume(PRTProbeAdjustmentVolume volume)
        {
            if (volume && !s_adjustmentVolumes.Contains(volume))
            {
                s_adjustmentVolumes.Add(volume);
            }
        }

        public static void UnregisterAdjustmentVolume(PRTProbeAdjustmentVolume volume)
        {
            if (volume)
            {
                s_adjustmentVolumes.Remove(volume);
            }
        }

        public static void RegisterReflectionProbeAdditionalData(ReflectionProbe probe, ReflectionProbeAdditionalData data)
        {
            if (probe)
            {
                s_reflectionProbeData[probe] = data;
            }
        }

        public static void UnregisterReflectionProbeAdditionalData(ReflectionProbe probe, ReflectionProbeAdditionalData data)
        {
            if (probe && s_reflectionProbeData.TryGetValue(probe, out var existingData) && existingData == data)
            {
                s_reflectionProbeData.Remove(probe);
            }
        }

        public static bool TryGetReflectionProbeAdditionalData(ReflectionProbe probe, out ReflectionProbeAdditionalData data)
        {
            return s_reflectionProbeData.TryGetValue(probe, out data);
        }

        public static PRTProbeVolume probeVolume
        {
            get
            {
                if (s_activeVolumes.Count == 0)
                    return null;
                return s_activeVolumes[0];
            }
        }

        public static IReadOnlyList<PRTProbeVolume> activeVolumes => s_activeVolumes;
        public static IReadOnlyList<PRTProbeAdjustmentVolume> adjustmentVolumes => s_adjustmentVolumes;
        public static IReadOnlyCollection<ReflectionProbeAdditionalData> reflectionProbeAdditionalData => s_reflectionProbeData.Values;
    }
}
