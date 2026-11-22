// Adapted for Fraxinus GPU Batcher from BRG Instanced Renderer.
// Original implementation copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Unity 2022 exposes baked occlusion through CullingGroup rather than the
    /// newer BRG AABB query. Enclosing spheres conservatively retain more chunks.
    /// Publish visibility only after the corresponding native camera Cull.
    /// </summary>
    public static class GPUBatcherOcclusionBridge
    {
        public static bool isSupported => true;
        public static bool isNativeAABBSupported => false;

        public static bool Supports(Camera camera, in ScriptableCullingParameters parameters) =>
            camera != null && camera.useOcclusionCulling && !camera.stereoEnabled &&
            (parameters.cullingOptions & CullingOptions.OcclusionCull) != 0;

        public static bool TryEncloseBounds(Bounds bounds, out BoundingSphere sphere)
        {
            Vector3 center = bounds.center, extents = bounds.extents;
            float radius = extents.magnitude;
            radius += Mathf.Max(0.0001f, radius * 0.00001f);
            bool valid = IsFinite(center.x) && IsFinite(center.y) && IsFinite(center.z) &&
                extents.x >= 0 && extents.y >= 0 && extents.z >= 0 && IsFinite(radius);
            sphere = valid ? new BoundingSphere(center, radius) : default;
            return valid;
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>
        /// One native group per camera. A generation identifies one native Cull,
        /// not a frame. Keep the backing sphere array alive until native disposal.
        /// </summary>
        public sealed class CameraState : IDisposable
        {
            static readonly float[] s_noDistanceLimit = { float.PositiveInfinity };
            readonly CullingGroup _group;
            BoundingSphere[] _spheres = Array.Empty<BoundingSphere>();
            Bounds[] _bounds = Array.Empty<Bounds>();
            bool[] _visible = Array.Empty<bool>();
            int[] _hiddenIndices = Array.Empty<int>();
            int[] _chunkToSphere = Array.Empty<int>();
            uint[] _chunkGenerations = Array.Empty<uint>();
            int _count;
            bool _pending, _published, _disposed;

            public Camera camera { get; }
            public uint generation { get; private set; }
            public bool isPublished => _published && !_disposed;
            public int sphereCount => _count;

            public CameraState(Camera camera)
            {
                if (camera == null) throw new ArgumentNullException(nameof(camera));
                this.camera = camera;
                _group = new CullingGroup { targetCamera = camera, enabled = false };
                _group.SetBoundingDistances(s_noDistanceLimit);
            }

            public void Begin(int chunkCapacity, int sphereCapacity)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(CameraState));
                Cancel();
                if (chunkCapacity < 0 || sphereCapacity < 0) throw new ArgumentOutOfRangeException();
                if (_chunkToSphere.Length < chunkCapacity)
                {
                    Array.Resize(ref _chunkToSphere, chunkCapacity);
                    Array.Resize(ref _chunkGenerations, chunkCapacity);
                }
                if (_spheres.Length < sphereCapacity)
                {
                    int capacity = Mathf.NextPowerOfTwo(Mathf.Max(1, sphereCapacity));
                    _spheres = new BoundingSphere[capacity];
                    _bounds = new Bounds[capacity];
                    _visible = new bool[capacity];
                    _hiddenIndices = new int[capacity];
                    _group.SetBoundingSpheres(_spheres);
                }
                _count = 0;
                _pending = true;
            }

            public void Add(int chunkId, Bounds bounds)
            {
                if (!_pending || _disposed) throw new InvalidOperationException("Begin a camera cull before adding bounds.");
                if ((uint)chunkId >= _chunkToSphere.Length) throw new ArgumentOutOfRangeException(nameof(chunkId));
                if (!TryEncloseBounds(bounds, out var sphere)) return;
                if (_count >= _spheres.Length) throw new InvalidOperationException("Sphere capacity exceeded.");
                _spheres[_count] = sphere;
                _bounds[_count] = bounds;
                _chunkToSphere[chunkId] = _count;
                _chunkGenerations[chunkId] = generation;
                ++_count;
            }

            public void Commit()
            {
                if (!_pending || _disposed) return;
                _group.SetBoundingSphereCount(_count);
                _group.enabled = _count > 0;
            }

            /// <summary>Call immediately after the same ScriptableRenderContext.Cull returns.</summary>
            public void Publish()
            {
                if (!_pending || _disposed) return;
                for (int i = 0; i < _count; ++i)
                    _visible[i] = true;
                // One native query for the entire group, rather than one managed
                // to native transition per chunk. Capacity covers every sphere.
                int hiddenCount = _count > 0 ? _group.QueryIndices(false, _hiddenIndices, 0) : 0;
                for (int i = 0; i < hiddenCount; ++i)
                    _visible[_hiddenIndices[i]] = false;
                _pending = false;
                _published = true;
            }

            public bool IsVisible(uint generation, int chunkId, Bounds currentBounds)
            {
                // Missing/changed queries cannot prove occlusion. Never apply a
                // previous cull after the camera, chunk bounds, or index changed.
                if (!isPublished || generation != this.generation || (uint)chunkId >= _chunkToSphere.Length ||
                    _chunkGenerations[chunkId] != generation) return true;
                int index = _chunkToSphere[chunkId];
                return !_bounds[index].Equals(currentBounds) || _visible[index];
            }

            public void Cancel()
            {
                if (_disposed) return;
                unchecked { ++generation; }
                if (generation == 0)
                {
                    Array.Clear(_chunkGenerations, 0, _chunkGenerations.Length);
                    generation = 1;
                }
                _pending = _published = false;
                _group.enabled = false;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _pending = _published = false;
                _group.Dispose();
            }
        }
    }
}
