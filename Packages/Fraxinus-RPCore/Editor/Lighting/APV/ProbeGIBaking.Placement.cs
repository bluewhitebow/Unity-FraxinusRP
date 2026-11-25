using System.Linq;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine.SceneManagement;
using UnityEditor;

using Brick = UnityEngine.Rendering.ProbeBrickIndex.Brick;

namespace UnityEngine.Rendering
{
    internal sealed partial class APVProbeVolumeBaking
    {
        int PosToIndex(Vector3Int pos)
        {
            Vector3Int normalizedPos = pos - minCellPosition;
            return normalizedPos.z * (cellCount.x * cellCount.y) + normalizedPos.y * cellCount.x + normalizedPos.x;
        }

        internal override bool CanFreezePlacement() => CanFreezePlacement(volume, bakingSet, partialBakeSceneList);

        internal static bool CanFreezePlacement(APVProbeReferenceVolume volume, ProbeVolumeBakingSet bakingSet, HashSet<string> sceneFilter = null)
        {
            if (bakingSet == null || !volume.isInitialized || !volume.enabledBySRP || !volume.supportLightingScenarios)
                return false;

            bool hasSceneData = false;
            foreach (var sceneData in volume.perSceneDataList)
            {
                if (!bakingSet.sceneGUIDs.Contains(sceneData.sceneGUID) || (sceneFilter != null && !sceneFilter.Contains(sceneData.sceneGUID)))
                    continue;

                if (sceneData.serializedBakingSet != bakingSet || bakingSet.GetSceneCellIndexList(sceneData.sceneGUID) == null)
                    return false;
                hasSceneData = true;
            }

            return hasSceneData;
        }

        NativeList<Vector3> RunPlacement(ref bool canceledByUser)
        {
            float previousBrickSize = volume.MinBrickSize();
            int previousSubdivision = volume.GetMaxSubdivision();
            Vector3 previousOffset = volume.ProbeOffset();
            volume.SetSubdivisionDimensions(m_Layout.minBrickSize, m_Layout.maxSubdivision, m_Layout.probeOffset);
            try
            {
                m_BakingBatch = new BakingBatch(cellCount, volume);
                ProbeSubdivisionResult result;
                using (new BakingSetupProfiling(BakingSetupProfiling.Stages.BakeBricks))
                    result = GetWorldSubdivision(ref canceledByUser);

                if (canceledByUser)
                    return new NativeList<Vector3>(Allocator.Temp);

                using (new BakingSetupProfiling(BakingSetupProfiling.Stages.ApplySubdivisionResults))
                    return ApplySubdivisionResults(result, ref canceledByUser);
            }
            finally
            {
                volume.SetSubdivisionDimensions(previousBrickSize, previousSubdivision, previousOffset);
            }
        }

        ProbeSubdivisionResult GetWorldSubdivision(ref bool canceledByUser)
        {
            if (isFreezingPlacement)
                return GetBricksFromLoaded();

            var ctx = PrepareProbeSubdivisionContext();
            return BakeBricks(ctx, m_BakingBatch.contributors, ref canceledByUser);
        }

        NativeList<Vector3> ApplySubdivisionResults(ProbeSubdivisionResult results, ref bool canceledByUser)
        {
            int cellIdx = 0, freq = 10;
            BakingSetupProfiling.GetProgressRange(out float progress0, out float progress1);

            var positions = new NativeList<Vector3>(Allocator.Persistent);
            foreach ((var position, var bounds, var bricks) in results.cells)
            {
                if (cellIdx++ % freq == 0) // Don't refresh progress bar at every iteration because it's slow
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Baking Probe Volumes", $"Subdividing cell {cellIdx} out of {results.cells.Count}", Mathf.Lerp(progress0, progress1, cellIdx / (float)results.cells.Count)))
                    {
                        canceledByUser = true;
                        return positions;
                    }
                }

                ConvertBricksToPositions(bricks, out var probePositions, out var brickSubdivLevels);
                DeduplicateProbePositions(in probePositions, in brickSubdivLevels, m_BakingBatch, positions, out var probeIndices);

                BakingCell cell = new BakingCell()
                {
                    index = PosToIndex(position),
                    position = position,
                    bounds = bounds,
                    bricks = bricks,
                    probePositions = probePositions,
                    probeIndices = probeIndices,
                };

                m_BakingBatch.cells.Add(cell);
                m_BakingBatch.cellIndex2SceneReferences[cell.index] = new HashSet<string>(results.scenesPerCells[cell.position]);
            }

            return positions;
        }

        static void DeduplicateProbePositions(in Vector3[] probePositions, in int[] brickSubdivLevel, BakingBatch batch, NativeList<Vector3> uniquePositions, out int[] indices)
        {
            indices = new int[probePositions.Length];
            int uniqueIndex = batch.positionToIndex.Count;

            for (int i = 0; i < probePositions.Length; i++)
            {
                var pos = probePositions[i];
                var brickSubdiv = brickSubdivLevel[i];
                int probeHash = batch.GetProbePositionHash(pos);

                if (batch.positionToIndex.TryGetValue(probeHash, out var index))
                {
                    indices[i] = index;
                    int oldBrickLevel = batch.uniqueBrickSubdiv[probeHash];
                    if (brickSubdiv < oldBrickLevel)
                        batch.uniqueBrickSubdiv[probeHash] = brickSubdiv;
                }
                else
                {
                    batch.positionToIndex[probeHash] = uniqueIndex;
                    indices[i] = uniqueIndex;
                    batch.uniqueBrickSubdiv[probeHash] = brickSubdiv;
                    uniquePositions.Add(pos);
                    uniqueIndex++;
                }
            }
        }

        ProbeSubdivisionResult GetBricksFromLoaded()
        {
            var dataList = GetPerSceneDataList();
            var result = new ProbeSubdivisionResult();

            foreach (var data in dataList)
            {
                var cellSize = m_Layout.minDistanceBetweenProbes * 3.0f * m_Layout.cellSizeInBricks;
                Vector3 cellDimensions = new Vector3(cellSize, cellSize, cellSize);

                // Loop through cells in asset, we need to be careful as there'll be duplicates.
                // As we go through the cells we fill ProbeSubdivisionResult as we go.
                var cells = m_BakingSet.GetSceneCellIndexList(data.sceneGUID);
                foreach (var cellIndex in cells)
                {
                    var cellDesc = m_BakingSet.GetCellDesc(cellIndex);
                    var cellData = m_BakingSet.GetCellData<APVCellData>(cellIndex);
                    var cellPos = cellDesc.position;

                    if (!result.scenesPerCells.ContainsKey(cellPos))
                    {
                        result.scenesPerCells[cellPos] = new HashSet<string>();

                        var center = new Vector3((cellPos.x + 0.5f) * cellSize, (cellPos.y + 0.5f) * cellSize, (cellPos.z + 0.5f) * cellSize);
                        Brick[] bricks;
                        if (cellData != null && cellData.bricks.IsCreated)
                            bricks = cellData.bricks.ToArray();
                        else
                        {
                            // Disk streaming can leave a cell's CPU data empty. Frozen
                            // placement needs the saved bricks regardless of residency.
                            var asset = m_BakingSet.apvBakedData.cellBricksDataAsset;
                            var savedBricks = m_BakingSet.LoadStreamableAssetData<Brick>(asset, new List<int> { cellIndex });
                            try
                            {
                                int start = m_BakingSet.useStreamingAsset ? 0 : asset.streamableCellDescs[cellIndex].offset / asset.elementSize;
                                if (!savedBricks.IsCreated || start < 0 || savedBricks.Length - start < cellDesc.bricksCount)
                                    throw new System.InvalidOperationException($"Cannot load frozen probe placement for cell {cellIndex}.");
                                bricks = savedBricks.GetSubArray(start, cellDesc.bricksCount).ToArray();
                            }
                            finally { m_BakingSet.ReleaseStreamableAssetData(savedBricks); }
                        }
                        result.cells.Add((cellPos, new Bounds(center, cellDimensions), bricks));
                    }
                    result.scenesPerCells[cellPos].Add(data.sceneGUID);
                }
            }

            return result;
        }

        internal ProbeSubdivisionContext PrepareProbeSubdivisionContext(bool liveContext = false)
        {
            var context = new ProbeSubdivisionContext();
            var profile = liveContext || m_Layout.minDistanceBetweenProbes <= 0 ? bakingSet.layout : m_Layout;
            context.Initialize(bakingSet, profile, partialBakeSceneList);
            return context;
        }

        internal static ProbeSubdivisionResult BakeBricks(ProbeSubdivisionContext context, in GIContributors contributors, ref bool canceledByUser)
        {
            BakingSetupProfiling.GetProgressRange(out float progress0, out float progress1);
            return ProbeVolumePlacement.BakeBricks(context, contributors, ref canceledByUser, progress0, progress1);
        }

        // Converts brick information into positional data at kBrickProbeCountPerDim * kBrickProbeCountPerDim * kBrickProbeCountPerDim resolution
        internal void ConvertBricksToPositions(Brick[] bricks, out Vector3[] positions, out int[] subdivisionLevels) => ProbeVolumePlacement.ConvertBricksToPositions(m_Layout, bricks, out positions, out subdivisionLevels);
    }
}
