// Adapted for Fraxinus GPU Batcher from BRG Instanced Renderer.
// Original implementation copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher
{
    public struct ChangeTrackerTransformData : IDisposable
    {
        NativeTransformChanges _data;
        internal ChangeTrackerTransformData(NativeTransformChanges data) => _data = data;
        public NativeArray<int> transformedIDs => _data.transformedIDs;
        public NativeArray<Matrix4x4> localToWorldMatrices => _data.localToWorldMatrices;
        public NativeArray<Vector3> positions => _data.positions;
        public NativeArray<Quaternion> rotations => _data.rotations;
        public NativeArray<Vector3> scales => _data.scales;
        public int length => transformedIDs.IsCreated ? transformedIDs.Length : 0;
        public void Dispose() => _data.Dispose();
    }

    public struct ChangeTrackerTypeData : IDisposable
    {
        NativeTypeChanges _data;
        internal ChangeTrackerTypeData(NativeTypeChanges data) => _data = data;
        public UnityEngine.Object[] changed => _data.changed;
        public NativeArray<int> changedIDs => _data.changedIDs;
        public NativeArray<int> destroyedIDs => _data.destroyedIDs;
        public int length => changedIDs.IsCreated ? changedIDs.Length : 0;
        public void Dispose() => _data.Dispose();
    }

    /// <summary>Native ObjectDispatcher tracking through Core's engine-friend wrapper.</summary>
    public static class GPUBatcherChangeTracker
    {
        static NativeObjectChangeTracker s_dispatcher;
        public static bool isInitialized => s_dispatcher != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticState() => Dispose();

        public static void Initialize()
        {
            Dispose();
            s_dispatcher = new NativeObjectChangeTracker();
        }

        public static void TrackTransforms<T>() where T : UnityEngine.Object
        {
            if (s_dispatcher == null) return;
            s_dispatcher.TrackTransforms(typeof(T));
        }

        public static void TrackType<T>() where T : UnityEngine.Object
        {
            if (s_dispatcher == null) return;
            s_dispatcher.TrackType(typeof(T));
        }

        public static ChangeTrackerTransformData GetTransformChanges<T>(Allocator allocator) where T : UnityEngine.Object
        {
            if (s_dispatcher == null) return default;
            return new ChangeTrackerTransformData(s_dispatcher.GetTransformChanges(typeof(T), allocator));
        }

        public static ChangeTrackerTypeData GetTypeChanges<T>(Allocator allocator) where T : UnityEngine.Object
        {
            if (s_dispatcher == null) return default;
            return new ChangeTrackerTypeData(s_dispatcher.GetTypeChanges(typeof(T), allocator));
        }

        public static void Dispose()
        {
            s_dispatcher?.Dispose();
            s_dispatcher = null;
        }
    }
}
