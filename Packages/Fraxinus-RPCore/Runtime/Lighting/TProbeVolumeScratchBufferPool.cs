using System;
using System.Collections.Generic;
using System.Diagnostics;

using CellStreamingScratchBuffer = UnityEngine.Rendering.TProbeReferenceVolumeBase.CellStreamingScratchBuffer;
using ICellStreamingScratchBufferLayout =  UnityEngine.Rendering.TProbeReferenceVolumeBase.ICellStreamingScratchBufferLayout;

namespace UnityEngine.Rendering
{
    internal abstract class TProbeVolumeScratchBufferPoolBase
    {
        [DebuggerDisplay("ChunkCount = {chunkCount} ElementCount = {pool.Count}")]
        internal class ScratchBufferPool : IComparable<ScratchBufferPool>
        {
            public int chunkCount = -1;
            public Stack<CellStreamingScratchBuffer> pool = new Stack<CellStreamingScratchBuffer>();

            public ScratchBufferPool(int chunkCount)
            {
                this.chunkCount = chunkCount;
            }

            private ScratchBufferPool()
            {

            }

            public int CompareTo(ScratchBufferPool other)
            {
                if (chunkCount < other.chunkCount)
                    return -1;
                else if (chunkCount > other.chunkCount)
                    return 1;
                else
                    return 0;
            }
        }

        public abstract int chunkSize { get; }
        public abstract int maxChunkCount { get; }

        internal abstract bool AllocateScratchBuffer(int chunkCount, out CellStreamingScratchBuffer scratchBuffer, out ICellStreamingScratchBufferLayout layout, bool allocateGraphicsBuffers);

        public abstract void ReleaseScratchBuffer(CellStreamingScratchBuffer scratchBuffer);

        public abstract void Cleanup();
    }

    internal abstract class TProbeVolumeScratchBufferPool<TLayout> : TProbeVolumeScratchBufferPoolBase where TLayout : struct, ICellStreamingScratchBufferLayout
    {
        private int _chunkSize = 0;
        private int _maxChunkCount = 0;

        // Maximum number of chunks that should allocated in total. This is a hint, size may differ a bit to ensure at least one scratch buffer for each required size.
        public override int maxChunkCount => _maxChunkCount;
        public int allocatedMemory => chunkSize * m_CurrentlyAllocatedChunkCount;

        // Used to avoid gcalloc in m_Pools.FindIndex
        protected static int s_ChunkCount;


        protected int m_CurrentlyAllocatedChunkCount = 0;
        // List and not a Dictionary because we need the list sorted.
        protected List<ScratchBufferPool> m_Pools = new List<ScratchBufferPool>();
        // We store layouts separately because we might use a bigger buffer than required but we still want the layout to match the exact chunk count.
        protected Dictionary<int, TLayout> m_Layouts = new Dictionary<int, TLayout>();

        public TProbeVolumeScratchBufferPool(ProbeVolumeBakingSet bakingSet)
        {
            _maxChunkCount = bakingSet.maxSHChunkCount;
        }

        // Size in bytes of a full SH chunk (L0+L1+L2+ValidityMask+Occlusion)
        public override int chunkSize { get { return _chunkSize; } }


        public void SetChunkSize(int val)
        {
            _chunkSize = val;
        }

        protected CellStreamingScratchBuffer CreateScratchBuffer(int chunkCount, bool allocateGraphicsBuffers)
        {
            var scratchBuffer = new CellStreamingScratchBuffer(chunkCount, chunkSize, allocateGraphicsBuffers);
            m_CurrentlyAllocatedChunkCount += chunkCount;

            return scratchBuffer;
        }

        public override void ReleaseScratchBuffer(CellStreamingScratchBuffer scratchBuffer)
        {
            if (scratchBuffer.chunkSize != chunkSize)
            {
                scratchBuffer.Dispose();
                return;
            }

            s_ChunkCount = scratchBuffer.chunkCount;
            var pool = m_Pools.Find((o) => o.chunkCount == s_ChunkCount);
            Debug.Assert(pool != null);
            pool.pool.Push(scratchBuffer);
        }

        public override void Cleanup()
        {
            foreach (var pool in m_Pools)
            {
                while (pool.pool.Count > 0)
                {
                    var scratchBuffer = pool.pool.Pop();
                    scratchBuffer.Dispose();
                }
            }

            m_Pools.Clear();
            m_CurrentlyAllocatedChunkCount = 0;
            _chunkSize = 0;
            _maxChunkCount = 0;
        }

        internal override bool AllocateScratchBuffer(int chunkCount, out CellStreamingScratchBuffer scratchBuffer, out ICellStreamingScratchBufferLayout layout, bool allocateGraphicsBuffers)
        {
            bool success = AllocateScratchBuffer(chunkCount, out scratchBuffer, out TLayout typedLayout, allocateGraphicsBuffers);
            layout = typedLayout;
            return success;
        }

        internal abstract bool AllocateScratchBuffer(int chunkCount, out CellStreamingScratchBuffer scratchBuffer, out TLayout layout, bool allocateGraphicsBuffers);
    }
}

