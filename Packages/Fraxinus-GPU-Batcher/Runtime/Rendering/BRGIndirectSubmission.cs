using System;
using UnityEngine.Rendering;
#if FRAXINUS_BRG_INDIRECT_API
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using NativeCommand = UnityEngine.Rendering.BatchDrawCommandIndirect;
#endif

namespace Fraxinus.GPUBatcher.Rendering
{
    public static class BRGIndirectSubmission
    {
        /// <summary>Submit synchronously. The caller owns the spans and GPU buffers. A false return must leave output untouched.</summary>
        public delegate bool SubmitHandler(
            in BatchCullingOutput output,
            ReadOnlySpan<BatchDrawCommandIndirect> commands,
            ReadOnlySpan<BatchDrawRange> ranges);

        /// <summary>Engine binding used by both camera and shadow culling. Set during engine integration startup.</summary>
        public static SubmitHandler submitHandler { get; set; }

        public static bool isAvailable => submitHandler != null;

#if FRAXINUS_BRG_INDIRECT_API
        static BRGIndirectSubmission() => submitHandler = SubmitNative;
#endif

        public static bool TrySubmit(
            in BatchCullingOutput output,
            ReadOnlySpan<BatchDrawCommandIndirect> commands,
            ReadOnlySpan<BatchDrawRange> ranges)
        {
            var submit = submitHandler;
            return submit != null && submit(in output, commands, ranges);
        }

#if FRAXINUS_BRG_INDIRECT_API
        private static unsafe bool SubmitNative(
            in BatchCullingOutput output,
            ReadOnlySpan<BatchDrawCommandIndirect> commands,
            ReadOnlySpan<BatchDrawRange> ranges)
        {
            if (!output.drawCommands.IsCreated || output.drawCommands.Length == 0)
                throw new ArgumentException("A native BRG output slot is required.", nameof(output));
            for (int i = 0; i < commands.Length; ++i)
            {
                if ((commands[i].flags & IndirectDrawCommandFlags.HasSortingPosition) != 0)
                    throw new NotSupportedException("This adapter accepts opaque commands without per-instance sorting positions.");
            }
            for (int i = 0; i < ranges.Length; ++i)
            {
                if ((ulong)ranges[i].drawCommandsBegin + ranges[i].drawCommandsCount > (ulong)commands.Length)
                    throw new ArgumentException("An indirect draw range exceeds its command span.", nameof(ranges));
            }
            var target = (BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr();
            if (target->drawCommands != null || target->indirectDrawCommands != null || target->drawRanges != null ||
                target->proceduralDrawCommands != null || target->proceduralIndirectDrawCommands != null ||
                target->visibleInstances != null || target->instanceSortingPositions != null ||
#if UNITY_6000_3_OR_NEWER
                target->drawCommandPickingEntityIds != null)
#else
                target->drawCommandPickingInstanceIDs != null)
#endif
                throw new InvalidOperationException("Native BRG output already owns draw data.");

            NativeCommand* nativeCommands = null;
            BatchDrawRange* nativeRanges = null;
            try
            {
                if (commands.Length > 0)
                    nativeCommands = (NativeCommand*)UnsafeUtility.Malloc(
                        (long)commands.Length * UnsafeUtility.SizeOf<NativeCommand>(),
                        UnsafeUtility.AlignOf<NativeCommand>(), Allocator.TempJob);
                if (ranges.Length > 0)
                    nativeRanges = (BatchDrawRange*)UnsafeUtility.Malloc(
                        (long)ranges.Length * UnsafeUtility.SizeOf<BatchDrawRange>(),
                        UnsafeUtility.AlignOf<BatchDrawRange>(), Allocator.TempJob);
                for (int i = 0; i < commands.Length; ++i)
                {
                    ref readonly var source = ref commands[i];
                    nativeCommands[i] = new NativeCommand
                    {
                        flags = (UnityEngine.Rendering.BatchDrawCommandFlags)(int)source.flags,
                        batchID = source.batchID,
                        materialID = source.materialID,
                        splitVisibilityMask = source.splitVisibilityMask,
                        lightmapIndex = source.lightmapIndex,
                        sortingPosition = source.sortingPosition,
                        visibleOffset = source.visibleOffset,
                        meshID = source.meshID,
                        topology = source.topology,
                        visibleInstancesBufferHandle = source.visibleInstancesBufferHandle,
                        visibleInstancesBufferWindowOffset = source.visibleInstancesBufferWindowOffset,
                        visibleInstancesBufferWindowSizeBytes = source.visibleInstancesBufferWindowSizeBytes,
                        indirectArgsBufferHandle = source.indirectArgsBufferHandle,
                        indirectArgsBufferOffset = source.indirectArgsBufferOffset
                    };
                }
                for (int i = 0; i < ranges.Length; ++i)
                {
                    nativeRanges[i] = ranges[i];
                    nativeRanges[i].drawCommandsType = BatchDrawCommandType.Indirect;
                }
                // Publish only after both allocations are complete. Never alias the
                // renderer's Persistent NativeArrays or cast the package descriptor ABI.
                *target = new BatchCullingOutputDrawCommands
                {
                    indirectDrawCommands = nativeCommands,
                    indirectDrawCommandCount = commands.Length,
                    drawRanges = nativeRanges,
                    drawRangeCount = ranges.Length
                };
                nativeCommands = null;
                nativeRanges = null;
                return true;
            }
            finally
            {
                if (nativeCommands != null) UnsafeUtility.Free(nativeCommands, Allocator.TempJob);
                if (nativeRanges != null) UnsafeUtility.Free(nativeRanges, Allocator.TempJob);
            }
        }
#endif
    }
}
