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
        NativeTransformChanges m_Data;
        internal ChangeTrackerTransformData(NativeTransformChanges data) => m_Data = data;
        public NativeArray<int> transformedIDs => m_Data.transformedIDs;
        public NativeArray<Matrix4x4> localToWorldMatrices => m_Data.localToWorldMatrices;
        public NativeArray<Vector3> positions => m_Data.positions;
        public NativeArray<Quaternion> rotations => m_Data.rotations;
        public NativeArray<Vector3> scales => m_Data.scales;
        public int Length => transformedIDs.IsCreated ? transformedIDs.Length : 0;
        public void Dispose() => m_Data.Dispose();
    }

    public struct ChangeTrackerTypeData : IDisposable
    {
        NativeTypeChanges m_Data;
        internal ChangeTrackerTypeData(NativeTypeChanges data) => m_Data = data;
        public UnityEngine.Object[] changed => m_Data.changed;
        public NativeArray<int> changedIDs => m_Data.changedIDs;
        public NativeArray<int> destroyedIDs => m_Data.destroyedIDs;
        public int Length => changedIDs.IsCreated ? changedIDs.Length : 0;
        public void Dispose() => m_Data.Dispose();
    }

    /// <summary>Native ObjectDispatcher tracking through Core's engine-friend wrapper.</summary>
    public static class GPUBatcherChangeTracker
    {
        static NativeObjectChangeTracker s_Dispatcher;
        public static bool IsInitialized => s_Dispatcher != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticState() => Dispose();

        public static void Initialize()
        {
            Dispose();
            s_Dispatcher = new NativeObjectChangeTracker();
        }

        public static void TrackTransforms<T>() where T : UnityEngine.Object
        {
            if (s_Dispatcher == null) return;
            s_Dispatcher.TrackTransforms(typeof(T));
        }

        public static void TrackType<T>() where T : UnityEngine.Object
        {
            if (s_Dispatcher == null) return;
            s_Dispatcher.TrackType(typeof(T));
        }

        public static ChangeTrackerTransformData GetTransformChanges<T>(Allocator allocator) where T : UnityEngine.Object
        {
            if (s_Dispatcher == null) return default;
            return new ChangeTrackerTransformData(s_Dispatcher.GetTransformChanges(typeof(T), allocator));
        }

        public static ChangeTrackerTypeData GetTypeChanges<T>(Allocator allocator) where T : UnityEngine.Object
        {
            if (s_Dispatcher == null) return default;
            return new ChangeTrackerTypeData(s_Dispatcher.GetTypeChanges(typeof(T), allocator));
        }

        public static void Dispose()
        {
            s_Dispatcher?.Dispose();
            s_Dispatcher = null;
        }
    }
}
