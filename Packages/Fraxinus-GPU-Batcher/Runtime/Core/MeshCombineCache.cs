// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher
{
    /// <summary>caches runtime-combined meshes keyed by source mesh IDs + local transforms.</summary>
    internal class MeshCombineCache
    {
        class Entry
        {
            public Mesh mesh;
            public int refCount;
            public long key;
        }

        private readonly Dictionary<long, Entry> _cache = new();
        // reverse lookup: combined mesh -> cache key
        private readonly Dictionary<Mesh, Entry> _meshToEntry = new();

        public int Count => _cache.Count;

        public Mesh GetOrCombine(Mesh[] meshes, Matrix4x4[] localMatrices, string debugName = null)
        {
            long key = ComputeKey(meshes, localMatrices);

            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.mesh != null)
                {
                    entry.refCount++;
                    return entry.mesh;
                }
                // mesh was destroyed externally - remove stale entry and recreate
                _meshToEntry.Remove(entry.mesh);
                _cache.Remove(key);
            }

            Mesh combined = CombineMeshesUnity(meshes, localMatrices);

            combined.name = debugName ?? $"GPUBatcher_Combined_{key:X16}";
            combined.hideFlags = HideFlags.HideAndDontSave;
            combined.RecalculateBounds();

#if DEBUG
            if (combined.vertexCount == 0)
                Debug.LogWarning($"MeshCombineCache: tried to cache a mesh, but it has 0 verts! {combined.name}");
#endif

            var newEntry = new Entry { mesh = combined, refCount = 1, key = key };
            _cache[key] = newEntry;
            _meshToEntry[combined] = newEntry;
            return combined;
        }

        public void AddRef(Mesh mesh)
        {
            if (mesh != null && _meshToEntry.TryGetValue(mesh, out var entry))
                entry.refCount++;
        }

        public bool Release(Mesh mesh)
        {
            if (mesh == null) return false;
            if (!_meshToEntry.TryGetValue(mesh, out var entry)) return false;

            entry.refCount--;
            if (entry.refCount <= 0)
            {
                _cache.Remove(entry.key);
                _meshToEntry.Remove(mesh);
                Object.DestroyImmediate(entry.mesh);
                return true;
            }
            return false;
        }

        public void Clear()
        {
            foreach (var kvp in _cache)
            {
                if (kvp.Value.mesh != null)
                    Object.DestroyImmediate(kvp.Value.mesh);
            }
            _cache.Clear();
            _meshToEntry.Clear();
        }

        public bool IsCombinedMesh(Mesh mesh)
        {
            return mesh != null && _meshToEntry.ContainsKey(mesh);
        }

        public IEnumerable<(Mesh mesh, int refCount)> GetEntries()
        {
            foreach (var kvp in _cache)
                yield return (kvp.Value.mesh, kvp.Value.refCount);
        }

        internal static long ComputeKey(Mesh[] meshes, Matrix4x4[] matrices)
        {
            var h = new Hash64();
            for (int i = 0; i < meshes.Length; i++)
                HashMeshEntry(ref h, meshes[i], matrices[i]);
            return h.ToHashCode();
        }

        internal static long ComputeKey(List<Mesh> meshes, List<Matrix4x4> matrices)
        {
            var h = new Hash64();
            for (int i = 0; i < meshes.Count; i++)
                HashMeshEntry(ref h, meshes[i], matrices[i]);
            return h.ToHashCode();
        }

        static void HashMeshEntry(ref Hash64 h, Mesh mesh, Matrix4x4 m)
        {
            h.Add(mesh != null ? GPUBatcherHelpers.IdentityHash(mesh) : 0);
            h.Add(m.m00); h.Add(m.m01); h.Add(m.m02); h.Add(m.m03);
            h.Add(m.m10); h.Add(m.m11); h.Add(m.m12); h.Add(m.m13);
            h.Add(m.m20); h.Add(m.m21); h.Add(m.m22); h.Add(m.m23);
        }

        static Mesh CombineMeshesUnity(Mesh[] meshes, Matrix4x4[] localMatrices)
        {
            // Mesh.CombineMeshes fails on non-readable sources; swap those out for readable copies
            Mesh[] tempReadableCopies = null;
            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i].isReadable) continue;
                if (tempReadableCopies == null)
                {
                    tempReadableCopies = new Mesh[meshes.Length];
                    var clone = new Mesh[meshes.Length];
                    System.Array.Copy(meshes, clone, meshes.Length);
                    meshes = clone;
                }
                tempReadableCopies[i] = CopyToReadableMesh(meshes[i]);
                meshes[i] = tempReadableCopies[i];
            }

            try
            {
                int totalSubmeshes = 0;
                for (int i = 0; i < meshes.Length; i++)
                    totalSubmeshes += meshes[i].subMeshCount;

                bool singleSubmeshEach = true;
                for (int i = 0; i < meshes.Length; i++)
                {
                    if (meshes[i].subMeshCount > 1)
                    {
                        singleSubmeshEach = false;
                        break;
                    }
                }

                Mesh combined;
                if (singleSubmeshEach && meshes.Length > 1)
                {
                    var combines = new CombineInstance[meshes.Length];
                    for (int i = 0; i < meshes.Length; i++)
                    {
                        combines[i].mesh = meshes[i];
                        combines[i].transform = localMatrices[i];
                        combines[i].subMeshIndex = 0;
                    }
                    combined = new Mesh();
                    combined.indexFormat = NeedsUInt32(meshes) ? IndexFormat.UInt32 : IndexFormat.UInt16;
                    combined.CombineMeshes(combines, mergeSubMeshes: false, useMatrices: true);
                }
                else
                {
                    var flatCombines = new CombineInstance[totalSubmeshes];
                    int idx = 0;
                    for (int i = 0; i < meshes.Length; i++)
                    {
                        for (int s = 0; s < meshes[i].subMeshCount; s++)
                        {
                            flatCombines[idx].mesh = meshes[i];
                            flatCombines[idx].transform = localMatrices[i];
                            flatCombines[idx].subMeshIndex = s;
                            idx++;
                        }
                    }
                    combined = new Mesh();
                    combined.indexFormat = NeedsUInt32(meshes) ? IndexFormat.UInt32 : IndexFormat.UInt16;
                    combined.CombineMeshes(flatCombines, mergeSubMeshes: false, useMatrices: true);
                }

                return combined;
            }
            finally
            {
                if (tempReadableCopies != null)
                    for (int i = 0; i < tempReadableCopies.Length; i++)
                        if (tempReadableCopies[i] != null) Object.DestroyImmediate(tempReadableCopies[i]);
            }
        }

        // read non-readable mesh data back from GPU via GraphicsBuffer.GetData, build a readable copy
        static Mesh CopyToReadableMesh(Mesh source)
        {
            var copy = new Mesh
            {
                name = source.name + "_readable",
                indexFormat = source.indexFormat,
                hideFlags = HideFlags.HideAndDontSave,
            };

            copy.SetVertexBufferParams(source.vertexCount, source.GetVertexAttributes());
            int streamCount = source.vertexBufferCount;
            for (int s = 0; s < streamCount; s++)
            {
                GraphicsBuffer vb = source.GetVertexBuffer(s);
                int bytes = vb.stride * vb.count;
                var data = new byte[bytes];
                vb.GetData(data);
                copy.SetVertexBufferData(data, 0, 0, bytes, s);
                vb.Release();
            }

            GraphicsBuffer ib = source.GetIndexBuffer();
            int indexTotalBytes = ib.stride * ib.count;
            var indexData = new byte[indexTotalBytes];
            ib.GetData(indexData);
            copy.SetIndexBufferParams(ib.count, source.indexFormat);
            copy.SetIndexBufferData(indexData, 0, 0, indexTotalBytes);
            ib.Release();
            copy.subMeshCount = source.subMeshCount;
            const MeshUpdateFlags kSubMeshFlags = MeshUpdateFlags.DontRecalculateBounds
                | MeshUpdateFlags.DontValidateIndices
                | MeshUpdateFlags.DontNotifyMeshUsers
                | MeshUpdateFlags.DontResetBoneBounds;
            for (int i = 0; i < source.subMeshCount; i++)
                copy.SetSubMesh(i, source.GetSubMesh(i), kSubMeshFlags);
            copy.RecalculateBounds();
            return copy;
        }

        static bool NeedsUInt32(Mesh[] meshes)
        {
            int totalVerts = 0;
            for (int i = 0; i < meshes.Length; i++)
            {
                totalVerts += meshes[i].vertexCount * meshes[i].subMeshCount;
                if (meshes[i].indexFormat == IndexFormat.UInt32)
                    return true;
            }
            return totalVerts > 65535;
        }
    }
}
