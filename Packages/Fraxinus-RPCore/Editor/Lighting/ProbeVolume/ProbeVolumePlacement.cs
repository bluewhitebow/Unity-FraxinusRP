using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;

using Brick = UnityEngine.Rendering.ProbeBrickIndex.Brick;

namespace UnityEngine.Rendering
{
    // Shared geometry placement used by each probe volume baking backend.
    internal static class ProbeVolumePlacement
    {
        internal static List<ProbeVolume> GetProbeVolumeList(HashSet<string> sceneFilter = null)
        {
            var volumes = GameObject.FindObjectsByType<ProbeVolume>(FindObjectsSortMode.InstanceID);
            if (sceneFilter == null)
                return new List<ProbeVolume>(volumes);

            return volumes.Where(volume => volume.isActiveAndEnabled && sceneFilter.Contains(volume.gameObject.scene.GetGUID())).ToList();
        }

        internal static ProbeSubdivisionResult BakeBricks(ProbeSubdivisionContext context, in GIContributors contributors, ref bool canceledByUser, float progress0 = 0.0f, float progress1 = 1.0f)
        {
            var result = new ProbeSubdivisionResult();
            if (context.probeVolumes.Count == 0)
                return result;

            int cellIndex = 0;
            const int refreshFrequency = 100;
            using (var gpuResources = ProbePlacement.AllocateGPUResources(context.probeVolumes.Count, context.profile))
            {
                foreach (var cell in context.cells)
                {
                    if (cellIndex++ % refreshFrequency == 0 && EditorUtility.DisplayCancelableProgressBar("Generating Probe Volume Bricks", $"Processing cell {cellIndex} out of {context.cells.Count}", Mathf.Lerp(progress0, progress1, cellIndex / (float)context.cells.Count)))
                    {
                        canceledByUser = true;
                        return new ProbeSubdivisionResult();
                    }

                    BakeCell(context, contributors, gpuResources, cell.position, cell.bounds, result);
                }
            }

            return result;
        }

        internal static void BakeCell(ProbeSubdivisionContext context, in GIContributors contributors,
            ProbePlacement.GPUSubdivisionContext gpuResources, Vector3Int position, Bounds bounds, ProbeSubdivisionResult result)
        {
            var scenesInCell = new HashSet<string>();
            var overlappingVolumes = new List<ProbeVolumePlacementDescriptor>();
            foreach (var probeVolume in context.probeVolumes)
            {
                if (!ProbeVolumePositioning.OBBAABBIntersect(probeVolume.volume, bounds, probeVolume.bounds))
                    continue;
                overlappingVolumes.Add(probeVolume);
                scenesInCell.Add(probeVolume.component.gameObject.scene.GetGUID());
            }

            if (overlappingVolumes.Count == 0)
                return;
            var filteredContributors = contributors.Filter(context.bakingSet, bounds, overlappingVolumes);
            if (filteredContributors.Count == 0 && !overlappingVolumes.Any(volume => volume.fillEmptySpaces))
                return;
            var bricks = ProbePlacement.SubdivideCell(position, bounds, context, gpuResources, filteredContributors, overlappingVolumes);
            if (bricks.Length == 0)
                return;
            foreach (var renderer in filteredContributors.renderers)
                scenesInCell.Add(renderer.component.gameObject.scene.GetGUID());
            foreach (var terrain in filteredContributors.terrains)
                scenesInCell.Add(terrain.component.gameObject.scene.GetGUID());
            result.cells.Add((position, bounds, bricks));
            result.scenesPerCells[position] = scenesInCell;
        }

        // Probe positions are part of the shared Probe/Brick layout. Backends only
        // provide the profile that defines the grid origin and minimum brick size.
        internal static void ConvertBricksToPositions(ProbeVolumeLayout profile, Brick[] bricks, out Vector3[] positions, out int[] subdivisionLevels)
        {
            int positionIndex = 0;
            float scale = profile.minBrickSize / ProbeVolumeLayout.brickCellCount;
            positions = new Vector3[bricks.Length * ProbeVolumeLayout.brickProbeCountTotal];
            subdivisionLevels = new int[positions.Length];

            foreach (var brick in bricks)
            {
                int brickSize = ProbeVolumeLayout.CellSize(brick.subdivisionLevel);
                Vector3Int brickOffset = brick.position * ProbeVolumeLayout.brickCellCount;
                for (int z = 0; z < ProbeVolumeLayout.brickProbeCountPerDim; z++)
                    for (int y = 0; y < ProbeVolumeLayout.brickProbeCountPerDim; y++)
                        for (int x = 0; x < ProbeVolumeLayout.brickProbeCountPerDim; x++)
                        {
                            positions[positionIndex] = profile.probeOffset + (Vector3)(brickOffset + new Vector3Int(x, y, z) * brickSize) * scale;
                            subdivisionLevels[positionIndex++] = brick.subdivisionLevel;
                        }
            }
        }
    }
}
