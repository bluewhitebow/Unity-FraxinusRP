using System;

namespace UnityEngine.Rendering
{
    public partial struct GPUPrefixSum
    {
        [GenerateHLSL]
        internal static class ShaderDefs
        {
            public const int GroupSize = 128;

            // Stride of the indirect arguement buffer in uints, the buffer is split into two sections dispatch options ( a lower or upper arguement set )
            public const int ArgsBufferStride = 16;
            public const int ArgsBufferUpper  = 0;
            public const int ArgsBufferLower  = 8;

            public static int DivUpGroup(int value)
            {
                return (value + GroupSize - 1) / GroupSize;
            }

            public static int AlignUpGroup(int value)
            {
                return DivUpGroup(value) * GroupSize;
            }

            public static void CalculateTotalBufferSize(int maxElementCount, out int totalSize, out int levelCounts)
            {
                int alignedSupportMaxCount = AlignUpGroup(maxElementCount);
                totalSize = alignedSupportMaxCount;
                levelCounts = 1;
                while (alignedSupportMaxCount > GroupSize)
                {
                    alignedSupportMaxCount = AlignUpGroup(DivUpGroup(alignedSupportMaxCount));
                    totalSize += alignedSupportMaxCount;
                    ++levelCounts;
                }
            }
        }

        /// <summary>
        /// Structure defining level offsets.
        /// </summary>
        [GenerateHLSL]
        public struct LevelOffsets
        {
            /// <summary> Number of levels. </summary>
            public uint count;

            /// <summary> Level offset. </summary>
            public uint offset;

            /// <summary> Parent level offset. </summary>
            public uint parentOffset;
        }

        /// <summary>
        /// Data structure containing the runtime resources that are bound by the command buffer.
        /// </summary>
        public struct SupportResources
        {
            internal bool ownsResources;
            internal int  alignedElementCount;
            internal int  maxBufferCount;
            internal int  maxLevelCount;

            internal GraphicsBuffer prefixBuffer0;
            internal GraphicsBuffer prefixBuffer1;
            internal GraphicsBuffer totalLevelCountBuffer;
            internal GraphicsBuffer levelOffsetBuffer;
            internal GraphicsBuffer indirectDispatchArgsBuffer;

            /// <summary>The prefix sum result.</summary>
            public GraphicsBuffer output => prefixBuffer0;

            /// <summary>
            /// Allocate support resources to accomodate a max count.
            /// </summary>
            /// <param name="maxElementCount">The max element count.</param>
            /// <returns>The created support resources.</returns>
            public static SupportResources Create(int maxElementCount)
            {
                var resources = new SupportResources() { alignedElementCount = 0, ownsResources = true };
                resources.Resize(maxElementCount);
                return resources;
            }


            internal void Resize(int newMaxElementCount)
            {
                if (!ownsResources)
                    throw new Exception("Cannot resize resources unless they are owned. Use GpuPrefixSumSupportResources.Create() for this.");

                newMaxElementCount = Math.Max(newMaxElementCount, 1); //at bare minimum support a single group.
                if (alignedElementCount >= newMaxElementCount)
                    return;

                Dispose();
                ShaderDefs.CalculateTotalBufferSize(newMaxElementCount, out int totalSize, out int levelCounts);

                alignedElementCount = ShaderDefs.AlignUpGroup(newMaxElementCount);
                maxBufferCount      = totalSize;
                maxLevelCount       = levelCounts;

                prefixBuffer0              = new GraphicsBuffer(GraphicsBuffer.Target.Raw, totalSize, 4);
                prefixBuffer1              = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newMaxElementCount, 4);
                totalLevelCountBuffer      = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 1, 4);
                levelOffsetBuffer          = new GraphicsBuffer(GraphicsBuffer.Target.Structured, levelCounts, System.Runtime.InteropServices.Marshal.SizeOf<LevelOffsets>());
                indirectDispatchArgsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, ShaderDefs.ArgsBufferStride * levelCounts, sizeof(uint));//3 arguments for upp dispatch, 3 arguments for lower dispatch
            }


            /// <summary>
            /// Dispose the supporting resources.
            /// </summary>
            public void Dispose()
            {
                if (alignedElementCount == 0 || !ownsResources)
                    return;

                alignedElementCount = 0;

                void TryFreeBuffer(GraphicsBuffer resource)
                {
                    if (resource != null)
                    {
                        resource.Dispose();
                        resource = null;
                    }
                }

                TryFreeBuffer(prefixBuffer0);
                TryFreeBuffer(prefixBuffer1);
                TryFreeBuffer(levelOffsetBuffer);
                TryFreeBuffer(indirectDispatchArgsBuffer);
                TryFreeBuffer(totalLevelCountBuffer);
            }
        }

        /// <summary>
        /// Arguments for a direct prefix sum.
        /// </summary>
        public struct DirectArgs
        {
            /// <summary>An inclusive or exclusive prefix sum.</summary>
            public bool             exclusive;
            /// <summary>The size of the input list.</summary>
            public int              inputCount;
            /// <summary>The input list.</summary>
            public GraphicsBuffer   input;
            /// <summary>Required runtime resources.</summary>
            public SupportResources supportResources;
        }

        /// <summary>
        /// Arguments for an indirect prefix sum.
        /// </summary>
        public struct IndirectDirectArgs
        {
            /// <summary>An inclusive or exclusive prefix sum.</summary>
            public bool             exclusive;
            /// <summary>Byte offset of the count inside the input count buffer.</summary>
            public int              inputCountBufferByteOffset;
            /// <summary>GPU buffer defining the size of the input list.</summary>
            public ComputeBuffer    inputCountBuffer;
            /// <summary>The input list.</summary>
            public GraphicsBuffer   input;
            /// <summary>Required runtime resources.</summary>
            public SupportResources supportResources;
        }

        /// <summary>
        /// Structure defining any required assets used by the GPU sort.
        /// </summary>
        public struct SystemResources
        {
            /// <summary>
            /// The compute asset that defines all of the kernels for the GPU prefix sum.
            /// </summary>
            public ComputeShader computeAsset;

            internal int kernelCalculateLevelDispatchArgsFromConst;
            internal int kernelCalculateLevelDispatchArgsFromBuffer;
            internal int kernelPrefixSumOnGroup;
            internal int kernelPrefixSumOnGroupExclusive;
            internal int kernelPrefixSumNextInput;
            internal int kernelPrefixSumResolveParent;
            internal int kernelPrefixSumResolveParentExclusive;

            internal void LoadKernels()
            {
                if (computeAsset == null)
                    return;

                kernelCalculateLevelDispatchArgsFromConst  = computeAsset.FindKernel("MainCalculateLevelDispatchArgsFromConst");
                kernelCalculateLevelDispatchArgsFromBuffer = computeAsset.FindKernel("MainCalculateLevelDispatchArgsFromBuffer");
                kernelPrefixSumOnGroup                     = computeAsset.FindKernel("MainPrefixSumOnGroup");
                kernelPrefixSumOnGroupExclusive            = computeAsset.FindKernel("MainPrefixSumOnGroupExclusive");
                kernelPrefixSumNextInput                   = computeAsset.FindKernel("MainPrefixSumNextInput");
                kernelPrefixSumResolveParent               = computeAsset.FindKernel("MainPrefixSumResolveParent");
                kernelPrefixSumResolveParentExclusive      = computeAsset.FindKernel("MainPrefixSumResolveParentExclusive");
            }
        }
    }
}
