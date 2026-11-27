using System;
using UnityEngine.Rendering;
#if UNITY_6000_0_OR_NEWER
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using NativeCommand = UnityEngine.Rendering.BatchDrawCommandIndirect;
#endif

namespace Fraxinus.GPUBatcher.Rendering
{
    public static class BRGIndirectSubmission
    {
#if UNITY_6000_0_OR_NEWER
        internal static bool hasNativeApi => true;
        private static GraphicsDeviceType? s_checkedDevice;
        private static bool s_deviceSupported;

        public static bool isAvailable
        {
            get
            {
                var device = SystemInfo.graphicsDeviceType;
                if (s_checkedDevice != device)
                {
                    // AddBatch binds a raw matrix buffer and our draw commands bind a raw
                    // visibility buffer. Constant-buffer BRG requires a different layout.
                    s_deviceSupported = device != GraphicsDeviceType.Null &&
                        SystemInfo.supportsComputeShaders && SystemInfo.supportsInstancing &&
                        SystemInfo.supportsIndirectArgumentsBuffer && SystemInfo.maxComputeBufferInputsVertex >= 2 &&
                        BatchRendererGroup.BufferTarget == BatchBufferTarget.RawBuffer;
                    s_checkedDevice = device;
                }
                return s_deviceSupported;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetDeviceSupport() => s_checkedDevice = null;

        /// <summary>Copies camera or shadow commands to BRG-owned memory when native indirect draws are available.</summary>
        public static unsafe bool TrySubmit(
            in BatchCullingOutput output,
            ReadOnlySpan<BatchDrawCommandIndirect> commands,
            ReadOnlySpan<BatchDrawRange> ranges)
        {
            if (!isAvailable) return false;
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
#else
        internal static bool hasNativeApi => false;
        public static bool isAvailable => false;
#endif
    }
}
