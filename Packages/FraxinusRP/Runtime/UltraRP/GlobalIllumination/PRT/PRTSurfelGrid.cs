using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Surfel structure: contains position, normal, albedo, and sky mask
    /// </summary>
    [Serializable]
    public struct Surfel
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector3 albedo;
        public float skyMask;

        public const int stride = 40;   // float3 * 3 + float = 40 bytes
    }

    /// <summary>
    /// Represents the main normal direction of a Surfel
    /// </summary>
    public enum SurfelDirection
    {
        PosX, NegX, PosY, NegY, PosZ, NegZ
    }

    /// <summary>
    /// Represents the indices of a Surfel
    /// </summary>
    [Serializable]
    public struct SurfelIndices
    {
        public int start;
        public int count;
        public const int stride = 8;    // int * 2 = 8 bytes
    }

    /// <summary>
    /// Factor structure: contains Brick index and the contribution weight of that Brick to the Probe
    /// </summary>
    [Serializable]
    public struct BrickFactor
    {
        public int brickIndex;
        public float weight;
        public const int stride = 8;    // int + float = 8 bytes
    }

    /// <summary>
    /// Factor range: each Probe stores the range of Factors it uses
    /// </summary>
    [Serializable]
    public struct FactorIndices
    {
        public int start;
        public int end;
    }

    /// <summary>
    /// Represents a 4x4x4 brick containing merged Surfels
    /// </summary>
    public class SurfelBrick
    {
        public const float brickSize = 4.0f;
        public readonly List<int> surfelIndices = new();
        public readonly HashSet<PRTProbe> referencedProbes = new();
        public int index { get; }

        public SurfelBrick(int index)
        {
            this.index = index;
        }
    }

    [Serializable]
    public class CellDataObsolute
    {
        public Surfel[] surfels;
        public SurfelIndices[] bricks;
        public BrickFactor[] factors;
        public FactorIndices[] probes;
        public float[] validityMasks;

        private CellDataObsolute() { }

        public CellDataObsolute(Surfel[] inSurfels, SurfelIndices[] surfelIndices, BrickFactor[] brickFactors, FactorIndices[] factorIndices, float[] validity)
        {
            surfels = inSurfels;
            bricks = surfelIndices;
            factors = brickFactors;
            probes = factorIndices;
            validityMasks = validity;
        }

        public static CellDataObsolute GetDefault()
        {
            return new CellDataObsolute
            {
                surfels = Array.Empty<Surfel>(),
                bricks = Array.Empty<SurfelIndices>(),
                factors = Array.Empty<BrickFactor>(),
                probes = Array.Empty<FactorIndices>(),
                validityMasks = Array.Empty<float>()
            };
        }
    }

    /// <summary>
    /// Manages the grid of Surfel bricks and handles Surfel organization
    /// </summary>
    public class SurfelGrid
    {
        private readonly Dictionary<ulong, SurfelBrick> _bricks = new();
        private int _nextBrickIndex;
        private readonly List<Surfel> _allSurfels = new();

        private static SurfelDirection GetSurfelDirection(Vector3 normal)
        {
            float absX = Mathf.Abs(normal.x);
            float absY = Mathf.Abs(normal.y);
            float absZ = Mathf.Abs(normal.z);

            if (absX >= absY && absX >= absZ)
                return normal.x >= 0 ? SurfelDirection.PosX : SurfelDirection.NegX;
            if (absY >= absX && absY >= absZ)
                return normal.y >= 0 ? SurfelDirection.PosY : SurfelDirection.NegY;
            return normal.z >= 0 ? SurfelDirection.PosZ : SurfelDirection.NegZ;
        }

        private static Vector3Int WorldToGrid(Vector3 worldPos)
        {
            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / SurfelBrick.brickSize),
                Mathf.FloorToInt(worldPos.y / SurfelBrick.brickSize),
                Mathf.FloorToInt(worldPos.z / SurfelBrick.brickSize)
            );
        }

        private static ulong GenerateHashKey(Vector3Int gridPos, SurfelDirection dir)
        {
            ulong key = ((ulong)(gridPos.x + 32768) << 36) |
                       ((ulong)(gridPos.y + 32768) << 20) |
                       ((ulong)(gridPos.z + 32768) << 4) |
                       ((ulong)dir);
            return key;
        }

        private static float CalculateBrickWeight(Vector3 probePosition, Vector3 brickCenter, Vector3 brickAverageNormal)
        {
            Vector3 brickToProbe = (probePosition - brickCenter).normalized;
            float normalDot = Vector3.Dot(brickAverageNormal, brickToProbe);
            return Mathf.Max(0.0f, normalDot);
        }

        private static ulong GeneratePositionHash(Vector3 position, float precision)
        {
            int x = Mathf.FloorToInt(position.x / precision + 0.5f);
            int y = Mathf.FloorToInt(position.y / precision + 0.5f);
            int z = Mathf.FloorToInt(position.z / precision + 0.5f);

            ulong hash = 1469598103934665603UL;
            hash ^= (uint)x; hash *= 1099511628211UL;
            hash ^= (uint)y; hash *= 1099511628211UL;
            hash ^= (uint)z; hash *= 1099511628211UL;
            return hash;
        }

        private static Surfel MergeSurfels(List<Surfel> surfelsToMerge)
        {
            if (surfelsToMerge.Count == 1)
                return surfelsToMerge[0];

            Vector3 avgPosition = Vector3.zero;
            Vector3 avgNormal = Vector3.zero;
            Vector3 avgAlbedo = Vector3.zero;
            float avgSkyMask = 0f;

            foreach (var surfel in surfelsToMerge)
            {
                avgPosition += surfel.position;
                avgAlbedo += surfel.albedo;
                avgSkyMask += surfel.skyMask;
                avgNormal += surfel.normal;
            }

            int count = surfelsToMerge.Count;
            avgPosition /= count;
            avgAlbedo /= count;
            avgSkyMask /= count;
            avgNormal = (avgNormal / count).normalized;

            return new Surfel
            {
                position = avgPosition,
                normal = avgNormal,
                albedo = avgAlbedo,
                skyMask = avgSkyMask
            };
        }

        private static List<Surfel> MergeSurfelsInBrick(List<Surfel> surfels)
        {
            const float mergeDistance = 0.1f;
            var positionGroups = new Dictionary<ulong, List<Surfel>>();

            foreach (var surfel in surfels)
            {
                ulong posHash = GeneratePositionHash(surfel.position, mergeDistance);
                if (!positionGroups.TryGetValue(posHash, out var group))
                {
                    group = new List<Surfel>();
                    positionGroups[posHash] = group;
                }
                group.Add(surfel);
            }

            var mergedSurfels = new List<Surfel>();
            foreach (var group in positionGroups.Values)
            {
                mergedSurfels.Add(MergeSurfels(group));
            }

            return mergedSurfels;
        }

        public void AddSurfel(Surfel surfel, PRTProbe probe)
        {
            Vector3Int gridPos = WorldToGrid(surfel.position);
            SurfelDirection dir = GetSurfelDirection(surfel.normal);
            ulong key = GenerateHashKey(gridPos, dir);

            if (!_bricks.TryGetValue(key, out SurfelBrick brick))
            {
                brick = new SurfelBrick(_nextBrickIndex++);
                _bricks[key] = brick;
            }

            int surfelIndex = _allSurfels.Count;
            _allSurfels.Add(surfel);
            brick.surfelIndices.Add(surfelIndex);
            brick.referencedProbes.Add(probe);
        }

        public CellDataObsolute GenerateCell(PRTProbe[] probeGrid, float[] validityMask)
        {
            var uniqueBricks = _bricks.Values.OrderBy(b => b.index).ToArray();
            var allMergedSurfels = new List<Surfel>();
            var brickIndices = new SurfelIndices[uniqueBricks.Length];

            for (int brickIndex = 0; brickIndex < uniqueBricks.Length; brickIndex++)
            {
                var brick = uniqueBricks[brickIndex];
                var brickSurfels = new List<Surfel>();
                foreach (int surfelIndex in brick.surfelIndices)
                {
                    brickSurfels.Add(_allSurfels[surfelIndex]);
                }

                var mergedSurfels = MergeSurfelsInBrick(brickSurfels);
                var indices = new SurfelIndices
                {
                    start = allMergedSurfels.Count,
                    count = mergedSurfels.Count
                };

                allMergedSurfels.AddRange(mergedSurfels);
                brickIndices[brickIndex] = indices;
            }

            var reorderedSurfels = allMergedSurfels.ToArray();
            var allFactors = new List<BrickFactor>();
            var probeIndices = new FactorIndices[probeGrid.Length];

            for (int probeIndex = 0; probeIndex < probeGrid.Length; probeIndex++)
            {
                var probe = probeGrid[probeIndex];
                var probePosition = probe.position;
                int factorStartIndex = allFactors.Count;
                var probeBrickFactors = new List<(int brickIndex, float weight)>();

                for (int brickIndex = 0; brickIndex < uniqueBricks.Length; brickIndex++)
                {
                    var brick = uniqueBricks[brickIndex];
                    if (!brick.referencedProbes.Contains(probe))
                        continue;

                    Vector3 brickCenter = Vector3.zero;
                    Vector3 brickAverageNormal = Vector3.zero;
                    var brickSurfelIndices = brickIndices[brickIndex];
                    int surfelCount = brickSurfelIndices.count;

                    for (int i = 0; i < surfelCount; i++)
                    {
                        var surfel = reorderedSurfels[brickSurfelIndices.start + i];
                        brickCenter += surfel.position;
                        brickAverageNormal += surfel.normal;
                    }

                    if (surfelCount > 0)
                    {
                        brickCenter /= surfelCount;
                        brickAverageNormal = (brickAverageNormal / surfelCount).normalized;
                    }

                    float weight = CalculateBrickWeight(probePosition, brickCenter, brickAverageNormal);
                    probeBrickFactors.Add((brickIndex, weight));
                }

                float totalWeight = probeBrickFactors.Sum(f => f.weight);
                for (int i = 0; i < probeBrickFactors.Count; i++)
                {
                    var (brickIndex, weight) = probeBrickFactors[i];
                    float normalizedWeight = totalWeight > 0 ? weight / totalWeight : 0;
                    probeBrickFactors[i] = (brickIndex, normalizedWeight);
                }

                probeBrickFactors.Sort((a, b) => a.brickIndex.CompareTo(b.brickIndex));

                foreach (var (brickIndex, weight) in probeBrickFactors)
                {
                    allFactors.Add(new BrickFactor
                    {
                        brickIndex = brickIndex,
                        weight = weight
                    });
                }

                probeIndices[probeIndex] = new FactorIndices
                {
                    start = factorStartIndex,
                    end = allFactors.Count - 1
                };
            }

            return new CellDataObsolute(reorderedSurfels, brickIndices, allFactors.ToArray(), probeIndices, validityMask);
        }
    }
}
