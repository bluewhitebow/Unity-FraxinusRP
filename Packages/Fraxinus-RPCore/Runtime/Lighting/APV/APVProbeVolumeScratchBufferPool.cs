using UnityEngine;


namespace UnityEngine.Rendering
{
    using CellStreamingScratchBuffer = TProbeReferenceVolumeBase.CellStreamingScratchBuffer;
    using CellStreamingScratchBufferLayout = APVProbeReferenceVolume.CellStreamingScratchBufferLayout;

    internal class APVProbeVolumeScratchBufferPool : TProbeVolumeScratchBufferPool<CellStreamingScratchBufferLayout>
    {
        int m_L0Size;
        int m_L1Size;
        int m_ValiditySize;
        int m_ValidityLayerCount;
        int m_L2Size;
        int m_ProbeOcclusionSize;
        int m_SkyOcclusionSize;
        int m_SkyShadingDirectionSize;

        public APVProbeVolumeScratchBufferPool(ProbeVolumeBakingSet bakingSet, ProbeVolumeSHBands shBands) : base(bakingSet)
        {
            SetChunkSize(APVProbeVolumeBakingDataFormat.GetChunkGPUMemory(bakingSet, shBands));
            m_L0Size = bakingSet.L0ChunkSize;
            m_L1Size = bakingSet.L1ChunkSize;
            m_ValiditySize = bakingSet.sharedValidityMaskChunkSize;
            m_ValidityLayerCount = bakingSet.bakedMaskCount;
            m_SkyOcclusionSize = bakingSet.sharedSkyOcclusionL0L1ChunkSize;
            m_SkyShadingDirectionSize = bakingSet.sharedSkyShadingDirectionIndicesChunkSize;
            m_L2Size = shBands == ProbeVolumeSHBands.SphericalHarmonicsL2 ? bakingSet.L2TextureChunkSize : 0;
            m_ProbeOcclusionSize = bakingSet.bakedProbeOcclusion ? bakingSet.ProbeOcclusionChunkSize : 0;
        }

        internal override bool AllocateScratchBuffer(int chunkCount, out CellStreamingScratchBuffer scratchBuffer, out CellStreamingScratchBufferLayout layout, bool allocateGraphicsBuffers)
        {
            s_ChunkCount = chunkCount;
            int index = m_Pools.FindIndex(0, (o) => o.chunkCount == s_ChunkCount);
            // The size of buffer we return may not be the exact requested size (can be bigger).
            // So we need to make sure the layout is the right one for the number of requested chunks.
            layout = GetOrCreateScratchBufferLayout(chunkCount);

            if (index != -1)
            {
                var pool = m_Pools[index].pool;
                // If one is available, return it.
                if (pool.Count > 0)
                {
                    scratchBuffer = pool.Pop();
                    scratchBuffer.Swap();
                    return true;
                }
                else
                {
                    // If none are available of the exact size, try to find a bigger buffer instead.
                    // We won't go above twice the size to avoid "wasting" bigger buffers for requests that are too small.
                    for (int i = index; i < m_Pools.Count; ++i)
                    {
                        var biggerPool = m_Pools[i];
                        // Next existing pools scratch size are too big. Break out of the loop.
                        // We don't want to hog a big buffer for a small request.
                        if (biggerPool.chunkCount >= (chunkCount * 2))
                            break;
                        else if (biggerPool.pool.Count > 0)
                        {
                            scratchBuffer = biggerPool.pool.Pop();
                            scratchBuffer.Swap();
                            return true;
                        }
                    }

                    // We did not find any suitable buffer. Create a new one with the exact requested size.
                    if ((m_CurrentlyAllocatedChunkCount + chunkCount) < maxChunkCount)
                    {
                        scratchBuffer = CreateScratchBuffer(chunkCount, allocateGraphicsBuffers);
                        return true;
                    }
                    // If we are above the maximum, we don't allocate more buffers.
                    // It's ok since we know at least one buffer will exist for any given chunkCount so we won't get impossible to fulfill requests.
                    else
                    {
                        scratchBuffer = null;
                        return false;
                    }
                }
            }
            else
            {
                // No pool of this size exists. Create a new pool of that size and return a new buffer;
                ScratchBufferPool newPool = new ScratchBufferPool(chunkCount);
                m_Pools.Add(newPool);
                m_Pools.Sort();

                scratchBuffer = CreateScratchBuffer(chunkCount, allocateGraphicsBuffers);
                return true;
            }
        }

        private CellStreamingScratchBufferLayout GetOrCreateScratchBufferLayout(int chunkCount)
        {
            if (m_Layouts.TryGetValue(chunkCount, out var layout))
            {
                return layout;
            }
            else
            {
                var bufferLayout = new CellStreamingScratchBufferLayout();
                bufferLayout._L0Size = m_L0Size;
                bufferLayout._L1Size = m_L1Size;
                bufferLayout._ValiditySize = m_ValiditySize;
                bufferLayout._ValidityProbeSize = m_ValidityLayerCount; // 1layer => 1xbyte => 4 probes at a time.
                if (m_SkyOcclusionSize != 0)
                {
                    bufferLayout._SkyOcclusionSize = m_SkyOcclusionSize;
                    bufferLayout._SkyOcclusionProbeSize = 8; // 4xFP16

                    if (m_SkyShadingDirectionSize != 0)
                    {
                        bufferLayout._SkyShadingDirectionSize = m_SkyShadingDirectionSize;
                        bufferLayout._SkyShadingDirectionProbeSize = 1; // 1xbyte => 4 probes at a time.
                    }
                    else
                    {
                        bufferLayout._SkyShadingDirectionSize = 0;
                        bufferLayout._SkyShadingDirectionProbeSize = 0;
                    }
                }
                else
                {
                    bufferLayout._SkyOcclusionSize = 0;
                    bufferLayout._SkyOcclusionProbeSize = 0;

                    bufferLayout._SkyShadingDirectionSize = 0;
                    bufferLayout._SkyShadingDirectionProbeSize = 0;
                }
                bufferLayout._L2Size = m_L2Size;

                if (m_ProbeOcclusionSize != 0)
                {
                    bufferLayout._ProbeOcclusionSize = m_ProbeOcclusionSize;
                    bufferLayout._ProbeOcclusionProbeSize = 4; // 4xbyte
                }
                else
                {
                    bufferLayout._ProbeOcclusionSize = 0;
                    bufferLayout._ProbeOcclusionProbeSize = 0;
                }

                // TODO: Find a generic way to pass this down? (depends on the gfx format we use).
                bufferLayout._L0ProbeSize = 8; // 4xFP16
                bufferLayout._L1ProbeSize = 4; // 4xbyte
                bufferLayout._L2ProbeSize = 4; // 4xbyte

                int destChunksSize = chunkCount * sizeof(uint) * 4; // 1 Chunk == Vector4Int
                                                                    // First destination chunks at offset 0 (no explicit member for this).
                                                                    // Then, shared data destination chunks. Can be different from SH data destination in case of blending
                                                                    // (one pool for blending and one other pool for shared data and blending destination).
                bufferLayout._SharedDestChunksOffset = destChunksSize;
                bufferLayout._L0L1rxOffset = bufferLayout._SharedDestChunksOffset + destChunksSize;
                bufferLayout._L1GryOffset = bufferLayout._L0L1rxOffset + m_L0Size * chunkCount;
                bufferLayout._L1BrzOffset = bufferLayout._L1GryOffset + m_L1Size * chunkCount;
                bufferLayout._ValidityOffset = bufferLayout._L1BrzOffset + m_L1Size * chunkCount;
                // Match the disk reader: L0/L1, shared data, optional L2, then probe occlusion.
                bufferLayout._SkyOcclusionOffset = bufferLayout._ValidityOffset + m_ValiditySize * chunkCount;
                bufferLayout._SkyShadingDirectionOffset = bufferLayout._SkyOcclusionOffset + m_SkyOcclusionSize * chunkCount;
                bufferLayout._L2_0Offset = bufferLayout._SkyShadingDirectionOffset + m_SkyShadingDirectionSize * chunkCount;
                bufferLayout._L2_1Offset = bufferLayout._L2_0Offset + m_L2Size * chunkCount;
                bufferLayout._L2_2Offset = bufferLayout._L2_1Offset + m_L2Size * chunkCount;
                bufferLayout._L2_3Offset = bufferLayout._L2_2Offset + m_L2Size * chunkCount;
                bufferLayout._ProbeOcclusionOffset = bufferLayout._L2_3Offset + m_L2Size * chunkCount;

                bufferLayout._ProbeCountInChunkLine = ProbeBrickPoolBase.chunkProbeCountPerDim;
                bufferLayout._ProbeCountInChunkSlice = ProbeBrickPoolBase.chunkProbeCountPerDim * ProbeBrickPoolBase.brickProbeCountPerDim;

                m_Layouts.Add(chunkCount, bufferLayout);
                return bufferLayout;
            }
        }
    }

}
