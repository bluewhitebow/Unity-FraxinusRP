namespace UnityEngine.Rendering
{
    /// <summary>
    /// Spatial layout shared by all probe volume backends.
    /// </summary>
    internal readonly struct ProbeVolumeLayout
    {
        internal const int brickCellCount = 3;
        internal const int brickProbeCountPerDim = brickCellCount + 1;
        internal const int brickProbeCountTotal = brickProbeCountPerDim * brickProbeCountPerDim * brickProbeCountPerDim;

        internal readonly int simplificationLevels;
        internal readonly float minDistanceBetweenProbes;
        internal readonly Vector3 probeOffset;

        internal ProbeVolumeLayout(int simplificationLevels, float minDistanceBetweenProbes, Vector3 probeOffset)
        {
            this.simplificationLevels = simplificationLevels;
            this.minDistanceBetweenProbes = minDistanceBetweenProbes;
            this.probeOffset = probeOffset;
        }

        internal int maxSubdivision => GetMaxSubdivision(simplificationLevels);
        internal int cellSizeInBricks => CellSize(simplificationLevels);
        internal float minBrickSize => GetMinBrickSize(minDistanceBetweenProbes);
        internal float cellSizeInMeters => cellSizeInBricks * minBrickSize;

        internal float BrickSize(int subdivisionLevel) => minBrickSize * CellSize(subdivisionLevel);

        internal Vector3Int PositionToCell(Vector3 position) => Vector3Int.FloorToInt((position - probeOffset) / cellSizeInMeters);

        internal static int CellSize(int subdivisionLevel) => (int)Mathf.Pow(brickCellCount, subdivisionLevel);
        internal static int GetMaxSubdivision(int simplificationLevels) => simplificationLevels + 1;
        internal static float GetMinBrickSize(float minDistanceBetweenProbes) => Mathf.Max(0.01f, minDistanceBetweenProbes * brickCellCount);
    }
}
