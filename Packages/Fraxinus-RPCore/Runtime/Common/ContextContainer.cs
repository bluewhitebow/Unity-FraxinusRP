using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Mathematics;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// ContextContainer is a Dictionary like storage where the key is a generic parameter and the value is of the same type.
    /// </summary>
    public class ContextContainer : IDisposable
    {
        Item[] m_Items = new Item[64];
        List<uint> m_ActiveItemIndices = new();
        ScopeData m_CurrentScope;

        /// <summary>
        /// Stores borrowed item references for a scope. Clearing this storage does not reset its items.
        /// </summary>
        public class ScopeData
        {
            ContextItem[] m_Items = new ContextItem[4];

            /// <summary>Returns the borrowed reference, or null when this scope does not contain it.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public T Get<T>() where T : ContextItem
            {
                return (T)Get(TypeId<T>.value);
            }

            /// <summary>Sets a borrowed reference. A null value removes the scope override.</summary>
            public void Set<T>(T value) where T : ContextItem
            {
                var typeId = TypeId<T>.value;
                if (typeId >= m_Items.Length)
                    Array.Resize(ref m_Items, Math.Max((int)typeId + 1, m_Items.Length * 2));

                m_Items[typeId] = value;
            }

            /// <summary>Clears references while retaining capacity, without resetting borrowed items.</summary>
            public void Clear()
            {
                Array.Clear(m_Items, 0, m_Items.Length);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ContextItem Get(uint typeId)
            {
                return typeId < m_Items.Length ? m_Items[typeId] : null;
            }
        }

        /// <summary>
        /// Sets borrowed overrides for reads. Missing scope entries fall back to items owned by this
        /// container. Pass null to restore reads from owned items only.
        /// </summary>
        public void SetCurrentScope(ScopeData scope)
        {
            m_CurrentScope = scope;
        }

        /// <summary>
        /// Retrieves the current scope's borrowed item, falling back to an item created in this container.
        /// </summary>
        /// <typeparam name="T">Is the class which you are trying to fetch. T has to inherit from <c>ContextContainerItem</c></typeparam>
        /// <returns>The current borrowed reference or the previously created owned item.</returns>
        /// <exception cref="InvalidOperationException">Thrown when neither the scope nor this container has the item.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>()
            where T : ContextItem, new()
        {
            var typeId = TypeId<T>.value;
            var item = GetItem(typeId);
            if (item == null)
            {
                throw new InvalidOperationException($"Type {typeof(T).FullName} has not been created yet.");
            }

            return (T)item;
        }

        /// <summary>
        /// Creates the value of type T.
        /// </summary>
        /// <typeparam name="T">Is the class which you are trying to fetch. T has to inherit from <c>ContextContainerItem</c></typeparam>
         /// <returns>The value of type T created inside the <c>ContextContainer</c>.</returns>
        /// <exception cref="InvalidOperationException">Thown if you try to create the value of type T agian after it is already created.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if CONTEXT_CONTAINER_ALLOCATOR_DEBUG
        public T Create<T>([CallerLineNumber] int lineNumber = 0, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
#else
        public T Create<T>()
#endif
            where T : ContextItem, new()
        {
            var typeId = TypeId<T>.value;
            if (Contains(typeId))
            {
#if CONTEXT_CONTAINER_ALLOCATOR_DEBUG
                if (!ContainsOwned(typeId))
                    throw new InvalidOperationException($"Type {typeof(T).FullName} is already available in the current scope.");
                throw new InvalidOperationException($"Type {typeof(T).FullName} has already been created. It was previously created in member {m_Items[typeId].memberName} at line {m_Items[typeId].lineNumber} in {m_Items[typeId].filePath}.");
#else
                throw new InvalidOperationException($"Type {typeof(T).FullName} has already been created.");
#endif
            }

#if CONTEXT_CONTAINER_ALLOCATOR_DEBUG
            return CreateAndGetData<T>(typeId, lineNumber, memberName, filePath);
#else
            return CreateAndGetData<T>(typeId);
#endif
        }

        /// <summary>
        /// Gets the current scope's borrowed item or an owned item, creating an owned item if neither exists.
        /// </summary>
        /// <typeparam name="T">Is the class which you are trying to fetch. T has to inherit from <c>ContextContainerItem</c></typeparam>
        /// <returns>Returns the value of type T which is created or retrived.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if CONTEXT_CONTAINER_ALLOCATOR_DEBUG
        public T GetOrCreate<T>([CallerLineNumber] int lineNumber = 0, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "")
#else
        public T GetOrCreate<T>()
#endif
            where T : ContextItem, new()
        {
            var typeId = TypeId<T>.value;
            var item = GetItem(typeId);
            if (item != null)
            {
                return (T)item;
            }


#if CONTEXT_CONTAINER_ALLOCATOR_DEBUG
            return CreateAndGetData<T>(typeId, lineNumber, memberName, filePath);
#else
            return CreateAndGetData<T>(typeId);
#endif
        }

        /// <summary>
        /// Checks whether the current scope or this container contains an item of type T.
        /// </summary>
        /// <typeparam name="T">Is the class which you are trying to fetch. T has to inherit from <c>ContextContainerItem</c></typeparam>
        /// <returns>Returns true if the value exists and false otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains<T>()
            where T : ContextItem, new()
        {
            var typeId = TypeId<T>.value;
            return Contains(typeId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool Contains(uint typeId) => GetItem(typeId) != null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool ContainsOwned(uint typeId) => typeId < m_Items.Length && m_Items[typeId].isSet;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ContextItem GetItem(uint typeId)
        {
            var scopedItem = m_CurrentScope?.Get(typeId);
            if (scopedItem != null)
                return scopedItem;

            return ContainsOwned(typeId) ? m_Items[typeId].storage : null;
        }

#if CONTEXT_CONTAINER_ALLOCATOR_DEBUG
        T CreateAndGetData<T>(uint typeId, int lineNumber, string memberName, string filePath)
#else
        T CreateAndGetData<T>(uint typeId)
#endif
            where T : ContextItem, new()
        {
            if (m_Items.Length <= typeId)
            {
                var items = new Item[math.max(math.ceilpow2(s_TypeCount), m_Items.Length * 2)];
                for (var i = 0; i < m_Items.Length; i++)
                {
                    items[i] = m_Items[i];
                }
                m_Items = items;
            }

            m_ActiveItemIndices.Add(typeId);
            ref var item = ref m_Items[typeId];
            item.storage ??= new T();
            item.isSet = true;
#if CONTEXT_CONTAINER_ALLOCATOR_DEBUG
            item.lineNumber = lineNumber;
            item.memberName = memberName;
            item.filePath = filePath;
#endif

            return (T)item.storage;
        }

        /// <summary>
        /// Clears the current scope and resets owned items. Borrowed items are never reset here.
        /// </summary>
        public void Dispose()
        {
            m_CurrentScope = null;
            foreach (var index in m_ActiveItemIndices)
            {
                ref var item = ref m_Items[index];
                item.storage.Reset();
                item.isSet = false;
            }

            m_ActiveItemIndices.Clear();
        }

        static int s_TypeCount;

        static class TypeId<T>
        {
            public static readonly uint value = (uint)(Interlocked.Increment(ref s_TypeCount) - 1);
        }

        struct Item
        {
            public ContextItem storage;
            public bool isSet;
#if CONTEXT_CONTAINER_ALLOCATOR_DEBUG
            public int lineNumber;
            public string memberName;
            public string filePath;
#endif
        }

    }

    /// <summary>
    /// This is needed to add the data to <c>ContextContainer</c> and will control how the data are removed when calling Dispose on the <c>ContextContainer</c>.
    /// </summary>
    public abstract class ContextItem
    {
        /// <summary>
        /// Resets the object so it can be used as a new instance next time it is created.
        /// To avoid memory allocations and generating garbage, the system reuses objects.
        /// This function should clear the object so it can be reused without leaking any
        /// information (e.g. pointers to objects that will no longer be valid to access).
        /// So it is important the implementation carefully clears all relevant members.
        /// Note that this is different from a Dispose or Destructor as the object in not
        /// freed but reset. This can be useful when havin large sub-allocated objects like
        /// arrays or lists which can be cleared and reused without re-allocating.
        /// </summary>
        public abstract void Reset();
    }
}
