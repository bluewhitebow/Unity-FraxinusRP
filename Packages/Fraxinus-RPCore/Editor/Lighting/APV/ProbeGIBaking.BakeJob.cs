namespace UnityEngine.Rendering
{
    internal partial class APVProbeVolumeBaking
    {
        // Contiguous probe ranges share the sky-occlusion settings of an
        // adjustment volume, or the baking set when no volume applies.
        struct BakeJob
        {
            Bounds m_Aabb;
            TProbeReferenceVolumeBase.Volume m_Obb;
            ProbeAdjustmentVolume m_Touchup;

            public int startOffset;
            public int probeCount;
            public int skyOcclusionBakingSamples;
            public int skyOcclusionBakingBounces;

            public void Create(ProbeVolumeBakingSet bakingSet)
            {
                skyOcclusionBakingSamples = bakingSet.skyOcclusionBakingSamples;
                skyOcclusionBakingBounces = bakingSet.skyOcclusionBakingBounces;
            }

            public void Create((TProbeReferenceVolumeBase.Volume obb, Bounds aabb, ProbeAdjustmentVolume touchup) volume)
            {
                m_Obb = volume.obb;
                m_Aabb = volume.aabb;
                m_Touchup = volume.touchup;
                skyOcclusionBakingSamples = m_Touchup.skyOcclusionSampleCount;
                skyOcclusionBakingBounces = m_Touchup.skyOcclusionMaxBounces;
            }

            public bool Contains(Vector3 point) => m_Touchup == null || m_Touchup.ContainsPoint(m_Obb, m_Aabb.center, point);
        }
    }
}
