using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;

namespace UnityEngine.Rendering
{
    public class ProbeBrickPoolBase
    {
        internal static int DivRoundUp(int x, int y) => (x + y - 1) / y;

        internal const int maxPoolWidth = 1 << 11; // 2048 texels is a d3d11 limit for tex3d in all dimensions

        internal const int chunkSizeInBricks = 128;

        [DebuggerDisplay("Chunk ({x}, {y}, {z})")]
        public struct BrickChunkAlloc
        {
            public int x, y, z;

            internal int flattenIndex(int sx, int sy) { return z * (sx * sy) + y * sx + x; }
        }

        public interface IDataLocation
        {
            int width { get; set; }
            int height { get; set; }
            int depth { get; set; }

            bool Valid();

            void Cleanup();
        }

        internal const int brickCellCount = ProbeVolumeLayout.brickCellCount;
        internal const int brickProbeCountPerDim = ProbeVolumeLayout.brickProbeCountPerDim;
        internal const int brickProbeCountTotal = ProbeVolumeLayout.brickProbeCountTotal;
        internal const int chunkProbeCountPerDim = chunkSizeInBricks * brickProbeCountPerDim;

        internal static int GetChunkSizeInBrickCount() { return chunkSizeInBricks; }
        internal static int GetChunkSizeInProbeCount() { return chunkSizeInBricks * brickProbeCountTotal; }

        internal static Vector3Int ProbeCountToDataLocSize(int numProbes)
        {
            Debug.Assert(numProbes != 0);
            Debug.Assert(numProbes % brickProbeCountTotal == 0);

            int numBricks = numProbes / brickProbeCountTotal;
            int poolWidth = maxPoolWidth / brickProbeCountPerDim;

            int width, height, depth;
            depth = (numBricks + poolWidth * poolWidth - 1) / (poolWidth * poolWidth);
            if (depth > 1)
                width = height = poolWidth;
            else
            {
                height = (numBricks + poolWidth - 1) / poolWidth;
                if (height > 1)
                    width = poolWidth;
                else
                    width = numBricks;
            }

            width *= brickProbeCountPerDim;
            height *= brickProbeCountPerDim;
            depth *= brickProbeCountPerDim;

            return new Vector3Int(width, height, depth);
        }

        static int EstimateMemoryCost(int width, int height, int depth, GraphicsFormat format)
        {
            int elementSize = format == GraphicsFormat.R16G16B16A16_SFloat ? 8 :
                format == GraphicsFormat.R8G8B8A8_UNorm ? 4 : 1;
            return (width * height * depth) * elementSize;
        }

        public static Texture CreateDataTexture(int width, int height, int depth, GraphicsFormat format, string name, bool allocateRendertexture, ref int allocatedBytes)
        {
            Texture texture = null;
            try
            {
                if (allocateRendertexture)
                {
                    texture = new RenderTexture(new RenderTextureDescriptor()
                    {
                        width = width,
                        height = height,
                        volumeDepth = depth,
                        graphicsFormat = format,
                        mipCount = 1,
                        enableRandomWrite = SystemInfo.supportsComputeShaders,
                        dimension = TextureDimension.Tex3D,
                        msaaSamples = 1,
                    });
                }
                else
                    texture = new Texture3D(width, height, depth, format, TextureCreationFlags.None, 1);

                texture.hideFlags = HideFlags.HideAndDontSave;
                texture.name = name;

                if (texture is RenderTexture renderTexture && (!renderTexture.Create() || !renderTexture.IsCreated()))
                    throw new InvalidOperationException($"The graphics device could not create probe volume texture '{name}' ({width}x{height}x{depth}, {format}).");

                allocatedBytes += EstimateMemoryCost(width, height, depth, format);
                return texture;
            }
            catch
            {
                CoreUtils.Destroy(texture);
                throw;
            }
        }

        internal static void DerivePoolSizeFromBudget(ProbeVolumeTextureMemoryBudget memoryBudget, out int width, out int height, out int depth)
        {
            // TODO: This is fairly simplistic for now and relies on the enum to have the value set to the desired numbers,
            // might change the heuristic later on.
            width = (int)memoryBudget;
            height = (int)memoryBudget;
            depth = brickProbeCountPerDim;
        }

        internal static int GetChunkCount(int brickCount)
        {
            int chunkSize = chunkSizeInBricks;
            return (brickCount + chunkSize - 1) / chunkSize;
        }

        internal static int EstimateMemoryCostForBlending(ProbeVolumeTextureMemoryBudget memoryBudget, bool compressed)
        {
            if (memoryBudget == 0)
                return 0;

            DerivePoolSizeFromBudget(memoryBudget, out int width, out int height, out int depth);
            Vector3Int locSize = ProbeCountToDataLocSize(width * height * depth);
            width = locSize.x;
            height = locSize.y;
            depth = locSize.z;

            int allocatedBytes = 0;
            var L0Format = GraphicsFormat.R16G16B16A16_SFloat;
            var L1L2Format = compressed ? GraphicsFormat.RGBA_BC7_UNorm : GraphicsFormat.R8G8B8A8_UNorm;

            allocatedBytes += EstimateMemoryCost(width, height, depth, L0Format);
            allocatedBytes += EstimateMemoryCost(width, height, depth, L1L2Format) * 2;

            return allocatedBytes;
        }

    }

    public abstract class TProbeBrickPool<TDataLocation> : ProbeBrickPoolBase where TDataLocation : struct, ProbeBrickPoolBase.IDataLocation
    {
        internal int estimatedVMemCost { get; set; }

        internal TDataLocation m_Pool; // internal to access it from blending pool only
        protected BrickChunkAlloc m_NextFreeChunk;
        protected Stack<BrickChunkAlloc> m_FreeList;
        int m_AvailableChunkCount;
        readonly ProbeVolumeTextureMemoryBudget m_MemoryBudget;

        internal TProbeBrickPool(ProbeVolumeTextureMemoryBudget memoryBudget)
        {
            Profiler.BeginSample("Create ProbeBrickPool");
            m_MemoryBudget = memoryBudget;
            m_NextFreeChunk.x = m_NextFreeChunk.y = m_NextFreeChunk.z = 0;

            m_FreeList = new Stack<BrickChunkAlloc>(256);
            Profiler.EndSample();
        }

        internal virtual void Initialize()
        {
            // Allocate only after the derived pool has initialized its format
            // and optional-channel flags. Calling a virtual AllocatePool from
            // this base constructor runs before those fields are assigned and
            // silently omits validity/L2 textures.
            if (!m_Pool.Valid())
            {
                DerivePoolSizeFromBudget(m_MemoryBudget, out int width, out int height, out int depth);
                AllocatePool(width, height, depth);
                m_AvailableChunkCount = (m_Pool.width / (chunkSizeInBricks * brickProbeCountPerDim)) *
                    (m_Pool.height / brickProbeCountPerDim) * (m_Pool.depth / brickProbeCountPerDim);
            }
        }

        internal void EnsureTextureValidity()
        {
            // We assume that if a texture is null, all of them are. In any case we reboot them altogether.
            if (!m_Pool.Valid())
            {
                m_Pool.Cleanup();
                AllocatePool(m_Pool.width, m_Pool.height, m_Pool.depth);
            }
        }

        public int GetRemainingChunkCount()
        {
            return m_AvailableChunkCount;
        }

        internal int GetPoolWidth() { return m_Pool.width; }
        internal int GetPoolHeight() { return m_Pool.height; }
        internal Vector3Int GetPoolDimensions() { return new Vector3Int(m_Pool.width, m_Pool.height, m_Pool.depth); }

        internal bool Allocate(int numberOfBrickChunks, List<BrickChunkAlloc> outAllocations, bool ignoreErrorLog)
        {
            while (m_FreeList.Count > 0 && numberOfBrickChunks > 0)
            {
                outAllocations.Add(m_FreeList.Pop());
                numberOfBrickChunks--;
                m_AvailableChunkCount--;
            }

            for (uint i = 0; i < numberOfBrickChunks; i++)
            {
                if (m_NextFreeChunk.z >= m_Pool.depth)
                {
                    // During baking we know we can hit this when trying to do dilation of all cells at the same time.
                    // We don't want controlled error message spam during baking so we ignore it.
                    // In theory this should never happen with proper streaming/defrag but we keep the message just in case otherwise.
                    if (!ignoreErrorLog)
                        Debug.LogError("Cannot allocate more brick chunks, probe volume brick pool is full.");

                    Deallocate(outAllocations);
                    outAllocations.Clear();
                    return false; // failure case, pool is full
                }

                outAllocations.Add(m_NextFreeChunk);
                m_AvailableChunkCount--;

                m_NextFreeChunk.x += chunkSizeInBricks * brickProbeCountPerDim;
                if (m_NextFreeChunk.x >= m_Pool.width)
                {
                    m_NextFreeChunk.x = 0;
                    m_NextFreeChunk.y += brickProbeCountPerDim;
                    if (m_NextFreeChunk.y >= m_Pool.height)
                    {
                        m_NextFreeChunk.y = 0;
                        m_NextFreeChunk.z += brickProbeCountPerDim;
                    }
                }
            }

            return true;
        }

        internal void Deallocate(List<BrickChunkAlloc> allocations)
        {
            m_AvailableChunkCount += allocations.Count;

            foreach (var brick in allocations)
                m_FreeList.Push(brick);
        }

        internal void GetRuntimeResources<TResources>(ref TResources rr) where TResources : struct, IRuntimeResources
        {
            ProbeRuntimeResourcesTypeAdapter.GetRuntimeResources(ref rr, m_Pool);
        }

        internal void Clear()
        {
            m_FreeList.Clear();
            m_NextFreeChunk.x = m_NextFreeChunk.y = m_NextFreeChunk.z = 0;
        }

        internal void Cleanup()
        {
            m_Pool.Cleanup();
        }

        #region abstract method

        internal abstract void AllocatePool(int width, int height, int depth);

        internal abstract void Update(TDataLocation source, List<BrickChunkAlloc> srcLocations, List<BrickChunkAlloc> dstLocations, int destStartIndex);

        internal abstract void UpdateValidity(TDataLocation source, List<BrickChunkAlloc> srcLocations, List<BrickChunkAlloc> dstLocations, int destStartIndex);

        #endregion
    }

    public abstract class TProbeBrickBlendingPool<TProbeBrickPool, TDataLocation> : ProbeBrickPoolBase
        where TProbeBrickPool : global::UnityEngine.Rendering.TProbeBrickPool<TDataLocation>
        where TDataLocation : struct, ProbeBrickPoolBase.IDataLocation
    {
        protected TProbeBrickPool m_State0, m_State1;
        protected ProbeVolumeTextureMemoryBudget m_MemoryBudget;
        bool m_SupportBlending;

        internal bool isAllocated => m_State0 != null;
        internal int estimatedVMemCost
        {
            get
            {
                if (!m_SupportBlending)
                    return 0;
                if (isAllocated)
                    return m_State0.estimatedVMemCost + m_State1.estimatedVMemCost;

                return ProbeBrickPoolBase.EstimateMemoryCostForBlending(m_MemoryBudget, false) * 2;
            }
        }

        internal int GetPoolWidth() { return m_State0.m_Pool.width; }
        internal int GetPoolHeight() { return m_State0.m_Pool.height; }
        internal int GetPoolDepth() { return m_State0.m_Pool.depth; }

        internal TProbeBrickBlendingPool(ProbeVolumeBlendingTextureMemoryBudget memoryBudget, bool supportBlending)
        {
            // Casting to other memory budget struct works cause it's casted to int in the end anyway
            m_MemoryBudget = (ProbeVolumeTextureMemoryBudget)memoryBudget;
            m_SupportBlending = supportBlending;
        }

        internal virtual void Initialize()
        {
            if (!SystemInfo.supportsComputeShaders)
                return;
        }

        internal virtual void AllocateResourcesIfNeeded()
        {
            if (isAllocated)
                return;
        }

        internal void Clear() => m_State0?.Clear();

        internal bool Allocate(int numberOfBrickChunks, List<ProbeBrickPoolBase.BrickChunkAlloc> outAllocations)
        {
            AllocateResourcesIfNeeded();
            if (numberOfBrickChunks > m_State0.GetRemainingChunkCount())
                return false;

            return m_State0.Allocate(numberOfBrickChunks, outAllocations, false);
        }

        internal void Deallocate(List<ProbeBrickPoolBase.BrickChunkAlloc> allocations)
        {
            if (allocations.Count == 0)
                return;

            m_State0.Deallocate(allocations);
        }

        internal void EnsureTextureValidity()
        {
            if (isAllocated)
            {
                m_State0.EnsureTextureValidity();
                m_State1.EnsureTextureValidity();
            }
        }

        internal void Cleanup()
        {
            if (isAllocated)
            {
                m_State0.Cleanup();
                m_State1.Cleanup();
            }
        }

        #region abstract method
        internal abstract void Update(TDataLocation source, List<BrickChunkAlloc> srcLocations, List<BrickChunkAlloc> dstLocations, int destStartIndex, int state);

        internal abstract void BlendChunks(BaseCell cell, TProbeBrickPool dstPool);

        internal abstract void PerformBlending(CommandBuffer cmd, float factor, TProbeBrickPool dstPool);
        #endregion
    }
}

