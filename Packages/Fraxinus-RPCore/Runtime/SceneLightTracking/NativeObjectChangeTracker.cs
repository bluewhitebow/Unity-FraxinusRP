using System;
using Unity.Collections;

namespace UnityEngine.Rendering
{
    // ObjectDispatcher is internal in Unity 2022. This engine-friend assembly
    // exposes its owned snapshots without reflection or scene-wide polling.
    internal struct NativeTransformChanges : IDisposable
    {
        TransformDispatchData m_Data;
        internal NativeTransformChanges(TransformDispatchData data) => m_Data = data;
        internal NativeArray<int> transformedIDs => m_Data.transformedID;
        internal NativeArray<Matrix4x4> localToWorldMatrices => m_Data.localToWorldMatrices;
        internal NativeArray<Vector3> positions => m_Data.positions;
        internal NativeArray<Quaternion> rotations => m_Data.rotations;
        internal NativeArray<Vector3> scales => m_Data.scales;
        public void Dispose()
        {
            if (m_Data.transformedID.IsCreated)
                m_Data.Dispose();

            m_Data = default;
        }
    }

    internal struct NativeTypeChanges : IDisposable
    {
        TypeDispatchData m_Data;
        internal NativeTypeChanges(TypeDispatchData data) => m_Data = data;
        internal Object[] changed => m_Data.changed;
        internal NativeArray<int> changedIDs => m_Data.changedID;
        internal NativeArray<int> destroyedIDs => m_Data.destroyedID;
        public void Dispose()
        {
            if (m_Data.changedID.IsCreated || m_Data.destroyedID.IsCreated)
                m_Data.Dispose();

            m_Data = default;
        }
    }

    internal sealed class NativeObjectChangeTracker : IDisposable
    {
        readonly ObjectDispatcher m_Dispatcher = new ObjectDispatcher();

        internal void TrackTransforms(Type type) => m_Dispatcher.EnableTransformTracking(ObjectDispatcher.TransformTrackingType.GlobalTRS, type);

        internal void TrackType(Type type) => m_Dispatcher.EnableTypeTracking(type);

        internal NativeTransformChanges GetTransformChanges(Type type, Allocator allocator) =>
            new NativeTransformChanges(m_Dispatcher.GetTransformChangesAndClear(
                type, ObjectDispatcher.TransformTrackingType.GlobalTRS, allocator));

        internal NativeTypeChanges GetTypeChanges(Type type, Allocator allocator) =>
            new NativeTypeChanges(m_Dispatcher.GetTypeChangesAndClear(type, allocator));

        public void Dispose() => m_Dispatcher.Dispose();
    }
}
