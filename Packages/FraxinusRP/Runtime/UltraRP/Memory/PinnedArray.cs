using System;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;

namespace FraxinusEngine.Rendering.Ultra
{
    unsafe struct PinnedArray<T> : IDisposable where T : struct
    {
        public T[] managedArray;
        public GCHandle handle;
        public NativeArray<T> nativeArray;

        public int length => managedArray != null ? managedArray.Length : 0;

        public PinnedArray(int length)
        {
            managedArray = new T[length];
            handle = GCHandle.Alloc(managedArray, GCHandleType.Pinned);
            nativeArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>((void*)handle.AddrOfPinnedObject(), length, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref nativeArray, AtomicSafetyHandle.Create());
#endif
        }

        public void Dispose()
        {
            if (managedArray == null) return;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.Release(NativeArrayUnsafeUtility.GetAtomicSafetyHandle(nativeArray));
#endif
            handle.Free();
            this = default;
        }
    }

}
