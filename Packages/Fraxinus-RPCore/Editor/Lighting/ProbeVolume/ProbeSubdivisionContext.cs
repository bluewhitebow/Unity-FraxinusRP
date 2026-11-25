using System.Collections.Generic;


namespace UnityEngine.Rendering
{
    class ProbeSubdivisionContext
    {
        public List<ProbeVolumePlacementDescriptor> probeVolumes = new ();
        public List<(Vector3Int position, Bounds bounds)> cells = new ();
        public ProbeVolumeBakingSet bakingSet;
        public ProbeVolumeLayout profile;

        public void Initialize(ProbeVolumeBakingSet bakingSet, ProbeVolumeLayout profileInfo, HashSet<string> sceneFilter = null)
        {
            var descriptors = new List<ProbeVolumePlacementDescriptor>();
            foreach (var volume in ProbeVolumePlacement.GetProbeVolumeList(sceneFilter))
                if (volume.isActiveAndEnabled)
                    descriptors.Add(new ProbeVolumePlacementDescriptor(volume));
            Initialize(bakingSet, profileInfo, descriptors);
        }

        public void Initialize(ProbeVolumeBakingSet bakingSet, ProbeVolumeLayout profileInfo,
            IEnumerable<ProbeVolumePlacementDescriptor> descriptors)
        {
            Profiling.Profiler.BeginSample("ProbeSubdivisionContext.Initialize");
            this.bakingSet = bakingSet;
            profile = profileInfo;
            probeVolumes.Clear();
            cells.Clear();
            probeVolumes.AddRange(descriptors);
            float cellSize = profileInfo.cellSizeInMeters;
            Vector3 cellDimensions = new Vector3(cellSize, cellSize, cellSize);

            // Generate all the unique cell positions from probe volumes:
            HashSet<Vector3Int> cellPositions = new HashSet<Vector3Int>();
            foreach (var pv in probeVolumes)
            {
                // This method generates many cells outside of the probe volumes but it's ok because next step will do obb collision tests between each cell and each probe volumes so we will eliminate them.
                var min = profileInfo.PositionToCell(pv.bounds.min);
                var max = profileInfo.PositionToCell(pv.bounds.max);

                for (int x = min.x; x <= max.x; x++)
                {
                    for (int y = min.y; y <= max.y; y++)
                    {
                        for (int z = min.z; z <= max.z; z++)
                        {
                            var cellPos = new Vector3Int(x, y, z);
                            if (cellPositions.Add(cellPos))
                            {
                                var center = profileInfo.probeOffset + new Vector3((cellPos.x + 0.5f) * cellSize, (cellPos.y + 0.5f) * cellSize, (cellPos.z + 0.5f) * cellSize);
                                cells.Add((cellPos, new Bounds(center, cellDimensions)));
                            }
                        }
                    }
                }
            }

            Profiling.Profiler.EndSample();
        }
    }
}
