using System;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public class UltraCameraHistory : ICameraHistoryReadAccess, ICameraHistoryWriteAccess, IPerFrameHistoryAccessTracker, IDisposable
    {
        const int k_validVersionCount = 2;  // current frame + previous frame

        private static uint s_typeCount = 0;
        private static class TypeId<T>
        {
            public static uint value = s_typeCount++;
        }

        private struct Item
        {
            public ContextItem storage;

            // The last tick the type was requested.
            public int requestVersion;

            // The last tick the type was written to.
            public int writeVersion;

            public void Reset()
            {
                storage?.Reset();
                requestVersion = -k_validVersionCount;  // NOTE: must be invalid on frame 0
                writeVersion   = -k_validVersionCount;
            }
        }

        private Item[] _items = new Item[32];
        private int _version = 0;

        // A central storage for camera history textures.
        private BufferedRTHandleSystem _historyTextures = new BufferedRTHandleSystem();

    #region SrpApi

        /// <summary>
        /// Request access to a history item.
        /// </summary>
        /// <typeparam name="Type">Type of the history item.</typeparam>
        public void RequestAccess<Type>() where Type : ContextItem
        {
            uint index = TypeId<Type>.value;

            // Resize
            if(index >= _items.Length)
            {
                var items = new Item[math.max(math.ceilpow2(s_typeCount), _items.Length * 2)];
                for (var i = 0; i < _items.Length; i++)
                {
                    items[i] = _items[i];
                }
                _items = items;
            }

            _items[index].requestVersion = _version;
        }

        /// <summary>
        /// Obtain read access to a history item.
        /// Valid only if the item was requested and written this or the previous frame.
        /// </summary>
        /// <typeparam name="Type">Type of the history item.</typeparam>
        /// <returns>Instance of the history item if valid. Null otherwise.</returns>
        public Type GetHistoryForRead<Type>() where Type : ContextItem
        {
            uint index = TypeId<Type>.value;

            if (index >= _items.Length)
                return null;

            // If the Type wasn't written in previous or this frame, return null.
            // The Type design is expected to handle, current/previous/Nth frame history access via BufferedRTHandleSystem.
            if (!IsValid((int)index))
                return null;

            return (Type)_items[index].storage;
        }

        /// <summary>
        /// Check if a type was requested this frame.
        /// </summary>
        /// <typeparam name="Type">Type of the history item.</typeparam>
        /// <returns>True if an active request exists. False otherwise.</returns>
        public bool IsAccessRequested<Type>() where Type : ContextItem
        {
            uint index = TypeId<Type>.value;

            if (index >= _items.Length)
                return false;

            return IsValidRequest((int)index);
        }

        /// <summary>
        /// Obtain write access to a history item.
        /// Valid only if the item was requested this or the previous frame.
        /// Write access implies that the contents of the history item must be written.
        /// </summary>
        /// <typeparam name="Type">Type of the history item.</typeparam>
        /// <returns>Instance of the history item if valid. Null otherwise.</returns>
        public Type GetHistoryForWrite<Type>() where Type : ContextItem, new()
        {
            uint index = TypeId<Type>.value;

            if (index >= _items.Length)
                return null;

            if (!IsValidRequest((int)index)) // If the request is too old, return null.
                return null;

            // Create Type instance on the first use
            if (_items[index].storage == null)
            {
                ref var i = ref _items[index];
                i.storage = new Type();

                // If the convenience base class for BufferedRTHandleSystem is used, set the owner.
                if (i.storage is CameraHistoryItem hi)
                    hi.OnCreate(_historyTextures, index);
            }

            // Assume the write for GetForWrite is done correctly by the caller.
            _items[index].writeVersion = _version;

            ContextItem item = _items[index].storage;
            return (Type)item;
        }

        /// <summary>
        /// Check if a type was written this frame.
        /// </summary>
        /// <typeparam name="Type">Type of the history item.</typeparam>
        /// <returns>True if write access was obtained this frame. False otherwise.</returns>
        public bool IsWritten<Type>() where Type : ContextItem
        {
            uint index = TypeId<Type>.value;

            if (index >= _items.Length)
                return false;

            return _items[index].writeVersion == _version;
        }

        /// <summary>
        /// Register external type request callbacks to this event.
        /// </summary>
        public event ICameraHistoryReadAccess.HistoryRequestDelegate OnGatherHistoryRequests;

    #endregion
    #region UltraRPApi

        internal UltraCameraHistory()
        {
            // Init items with invalid versions.
            for(int i = 0; i < _items.Length; i++)
                _items[i].Reset();
        }

        /// <summary>
        /// Release all camera history textures on the GPU.
        /// </summary>
        public void Dispose()
        {
            for (int i = 0; i < _items.Length; i++)
                _items[i].Reset();

            _historyTextures.ReleaseAll();
        }

        // Query which types are needed for the registered system.
        internal void GatherHistoryRequests()
        {
            OnGatherHistoryRequests?.Invoke(this);
        }

        // A type is valid if it was requested this or the last frame.
        // Requesting access in the leaf classes, means we might be 1 frame late.
        private bool IsValidRequest(int i)
        {
            return ((_version - _items[i].requestVersion) < k_validVersionCount);
        }

        // A type is valid if it was requested and written to this or the last frame.
        // Requesting access in the leaf classes, means we might be 1 frame late.
        // NOTE: BufferedRTHandleSystem technically supports history of N length.
        //       We might need to keep the history for N frames.
        //       For now we expect that active history has the previous frame written.
        private bool IsValid(int i)
        {
            return ((_version - _items[i].writeVersion) < k_validVersionCount);
        }

        // Garbage collect old unused type instances and Reset them. The user is expected to free any GPU resources.
        internal void ReleaseUnusedHistory()
        {
            for (int i = 0; i < _items.Length; i++)
            {
                if (!IsValidRequest(i) && !IsValid(i))
                    _items[i].Reset();
            }

            // After collecting stale Types, start a new generation.
            _version++;
        }

        // Set the camera reference size for all history textures.
        internal void SwapAndSetReferenceSize(int cameraWidth, int cameraHeight)
        {
            _historyTextures.SwapAndSetReferenceSize(cameraWidth, cameraHeight);
        }

    #endregion
    }
}
