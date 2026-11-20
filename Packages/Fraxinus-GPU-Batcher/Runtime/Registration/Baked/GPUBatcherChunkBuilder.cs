// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Fraxinus.GPUBatcher
{
    // pure spatial partitioning of instances into chunks, shared by registerers.
    // no GameObject/link state - operates only on InstanceData + per-prototype extents.
    internal static class GPUBatcherChunkBuilder
    {
        public struct Chunk
        {
            public InstanceData[] instances;
            public Bounds bounds;
        }

        public static List<Chunk> Build(List<InstanceData> instances, float[] protoExtents,
            int maxPerChunk, ChunkMode mode, float chunkSize)
        {
            var chunks = new List<Chunk>();
            if (instances == null || instances.Count == 0) return chunks;
            if (maxPerChunk < 1) maxPerChunk = 1;

            switch (mode)
            {
                case ChunkMode.StaticSingleChunk:
                case ChunkMode.DynamicGlobalChunk:
                {
                    var indices = new List<int>(instances.Count);
                    for (int i = 0; i < instances.Count; i++) indices.Add(i);
                    BuildFromIndices(instances, indices, protoExtents, maxPerChunk, chunks,
                        globalBounds: mode == ChunkMode.DynamicGlobalChunk);
                    break;
                }

                case ChunkMode.Static:
                {
                    var grid = new Dictionary<int2, List<int>>();
                    for (int i = 0; i < instances.Count; i++)
                    {
                        var pos = instances[i].position;
                        var cell = new int2(
                            (int)math.floor(pos.x / chunkSize),
                            (int)math.floor(pos.z / chunkSize));
                        if (!grid.TryGetValue(cell, out var list))
                        {
                            list = new List<int>();
                            grid[cell] = list;
                        }
                        list.Add(i);
                    }
                    foreach (var kvp in grid)
                        BuildFromIndices(instances, kvp.Value, protoExtents, maxPerChunk, chunks, globalBounds: false);
                    break;
                }
            }

            return chunks;
        }

        static void BuildFromIndices(List<InstanceData> instances, List<int> indices,
            float[] protoExtents, int maxPerChunk, List<Chunk> chunks, bool globalBounds)
        {
            int offset = 0;
            while (offset < indices.Count)
            {
                int count = math.min(indices.Count - offset, maxPerChunk);
                var chunkInstances = new InstanceData[count];

                var bMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                var bMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

                for (int i = 0; i < count; i++)
                {
                    var inst = instances[indices[offset + i]];
                    chunkInstances[i] = inst;

                    Vector3 pos = (Vector3)inst.position;
                    float protoExtent = (inst.signatureIndex >= 0 && protoExtents != null && inst.signatureIndex < protoExtents.Length)
                        ? protoExtents[inst.signatureIndex] : 1f;
                    float expand = protoExtent * math.cmax(math.abs(inst.scale));
                    bMin = Vector3.Min(bMin, pos - Vector3.one * expand);
                    bMax = Vector3.Max(bMax, pos + Vector3.one * expand);
                }

                Bounds bounds = globalBounds
                    ? new Bounds((bMin + bMax) * 0.5f, Vector3.one * 100000f)
                    : new Bounds((bMin + bMax) * 0.5f, bMax - bMin);

                chunks.Add(new Chunk { instances = chunkInstances, bounds = bounds });
                offset += count;
            }
        }
    }
}
