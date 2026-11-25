using System;
using System.Diagnostics;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace UnityEngine.Rendering
{
    internal struct ParallelBitArray
    {
        private Allocator m_Allocator;
        private NativeArray<long> m_Bits;
        private int m_Length;

        public int Length => m_Length;

        public bool IsCreated
        {
            get { return m_Bits.IsCreated; }
        }

        public ParallelBitArray(int length, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            length = Math.Max(length, 0);
            m_Allocator = allocator;
            m_Bits = new NativeArray<long>((length + 63) / 64, allocator, options);
            m_Length = length;
        }

        public void Dispose()
        {
            m_Bits.Dispose();
            m_Length = 0;
        }

        public void Dispose(JobHandle inputDeps)
        {
            m_Bits.Dispose(inputDeps);
            m_Length = 0;
        }

        public void Resize(int newLength)
        {
            if (newLength < 0)
                throw new ArgumentOutOfRangeException(nameof(newLength));

            int oldLength = m_Length;
            if (newLength == oldLength)
                return;

            int oldBitsLength = m_Bits.Length;
            int newBitsLength = (newLength + 63) / 64;
            if (newBitsLength != oldBitsLength)
            {
                var newBits = new NativeArray<long>(newBitsLength, m_Allocator, NativeArrayOptions.UninitializedMemory);
                if (m_Bits.IsCreated)
                {
                    int copiedChunkCount = Math.Min(m_Bits.Length, newBits.Length);
                    if (copiedChunkCount > 0)
                        NativeArray<long>.Copy(m_Bits, newBits, copiedChunkCount);
                    // UninitializedMemory is intentional for the copied
                    // portion, but every newly allocated chunk must start at
                    // zero or growing the array exposes arbitrary set bits.
                    for (int i = copiedChunkCount; i < newBits.Length; ++i)
                        newBits[i] = 0;
                    m_Bits.Dispose();
                }
                else
                {
                    for (int i = 0; i < newBits.Length; ++i)
                        newBits[i] = 0;
                }
                m_Bits = newBits;
            }

            // Clear the portion that did not belong to the old logical range.
            // This covers both growth within the same 64-bit chunk and the
            // high bits retained when shrinking.
            if (m_Bits.IsCreated && m_Bits.Length > 0)
            {
                int preservedLength = Math.Min(oldLength, newLength);
                int chunkIndex = preservedLength >> 6;
                int bitIndex = preservedLength & 63;
                if (chunkIndex < m_Bits.Length)
                {
                    if (bitIndex == 0)
                        m_Bits[chunkIndex] = 0;
                    else
                        m_Bits[chunkIndex] &= (long)((1UL << bitIndex) - 1UL);

                    for (int i = chunkIndex + 1; i < m_Bits.Length; ++i)
                        m_Bits[i] = 0;
                }
            }
            m_Length = newLength;
        }

        public void Set(int index, bool value)
        {
            unsafe
            {
                Debug.Assert(0 <= index && index < m_Length);

                int entry_index = index >> 6;
                long* entries = (long*)m_Bits.GetUnsafePtr();

                ulong bit = 1ul << (index & 0x3f);
                long and_mask = (long)(~bit);
                long or_mask = value ? (long)bit : 0;

                long old_entry, new_entry;
                do
                {
                    old_entry = Interlocked.Read(ref entries[entry_index]);
                    new_entry = (old_entry & and_mask) | or_mask;
                } while (Interlocked.CompareExchange(ref entries[entry_index], new_entry, old_entry) != old_entry);
            }
        }

        public bool Get(int index)
        {
            unsafe
            {
                Debug.Assert(0 <= index && index < m_Length);

                int entry_index = index >> 6;
                long* entries = (long*)m_Bits.GetUnsafeReadOnlyPtr();

                ulong bit = 1ul << (index & 0x3f);
                long check_mask = (long)bit;
                return (entries[entry_index] & check_mask) != 0;
            }
        }

        public ulong GetChunk(int chunk_index)
        {
            return (ulong)m_Bits[chunk_index];
        }

        public void SetChunk(int chunk_index, ulong chunk_bits)
        {
            m_Bits[chunk_index] = (long)chunk_bits;
        }

        public unsafe ulong InterlockedReadChunk(int chunk_index)
        {
            long* entries = (long*)m_Bits.GetUnsafeReadOnlyPtr();
            return (ulong)Interlocked.Read(ref entries[chunk_index]);
        }

        public unsafe void InterlockedOrChunk(int chunk_index, ulong chunk_bits)
        {
            long* entries = (long*)m_Bits.GetUnsafePtr();

            long old_entry, new_entry;
            do
            {
                old_entry = Interlocked.Read(ref entries[chunk_index]);
                new_entry = old_entry | (long)chunk_bits;
            } while (Interlocked.CompareExchange(ref entries[chunk_index], new_entry, old_entry) != old_entry);
        }

        public int ChunkCount()
        {
            return m_Bits.Length;
        }

        public ParallelBitArray GetSubArray(int length)
        {
            if (length < 0 || length > m_Length)
                throw new ArgumentOutOfRangeException(nameof(length));

            ParallelBitArray array = new ParallelBitArray();
            array.m_Allocator = m_Allocator;
            array.m_Bits = m_Bits.GetSubArray(0, (length + 63) / 64);
            array.m_Length = length;
            return array;
        }

        public NativeArray<long> GetBitsArray()
        {
            return m_Bits;
        }

        public void FillZeroes(int length)
        {
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length));
            length = Math.Min(length, m_Length);
            int chunkIndex = length / 64;
            int remainder = length & 63;

            for (int i = 0; i < chunkIndex; ++i)
                m_Bits[i] = 0;

            if(remainder > 0)
            {
                long lastChunkMask = (1L << remainder) - 1;
                m_Bits[chunkIndex] &= ~lastChunkMask;
            }
        }
    }
}
