using System;
using UnityEngine.Rendering;
#if FRAXINUS_BRG_INDIRECT_API
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using NativeCommand = UnityEngine.Rendering.BatchDrawCommandIndirect;
#endif
#if !FRAXINUS_BRG_INDIRECT_API && !UNITY_6000_0_OR_NEWER && (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || (UNITY_ANDROID && !UNITY_EDITOR && FRAXINUS_BRG_ANDROID_PREVIEW))
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
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
#elif !UNITY_6000_0_OR_NEWER && (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || (UNITY_ANDROID && !UNITY_EDITOR && FRAXINUS_BRG_ANDROID_PREVIEW))
        private delegate void Indexed2022Writer(IntPtr slot, IntPtr commands, int commandCount, IntPtr ranges, int rangeCount);
        private static readonly Indexed2022Writer s_indexed2022Writer;
        private static readonly Action<ScriptableRenderContext> s_refreshIndexedResources;

        static BRGIndirectSubmission()
        {
            bool enabled = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11)
                enabled = D3D11ReceiverReady();
#endif
            if (!enabled)
                return;

            var writer = typeof(BatchRendererGroup).Assembly.GetType("UnityEngine.Rendering.IndexedBrgOutputWriter")
                ?.GetMethod("Write", new[] {typeof(IntPtr), typeof(IntPtr), typeof(int), typeof(IntPtr), typeof(int)});
            if (writer == null) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (IntPtr.Size != 8 || BatchRendererGroup.BufferTarget != BatchBufferTarget.RawBuffer ||
                !SystemInfo.supportsComputeShaders || !AndroidReceiverReady()) return;
#endif
            if (UnsafeUtility.SizeOf<BatchDrawCommandIndirect>() != 52 || UnsafeUtility.SizeOf<BatchDrawRange>() != 40 ||
                UnsafeUtility.SizeOf<BatchCullingOutputDrawCommands>() != 96)
                throw new InvalidOperationException("Indexed BRG runtime and package outputs have incompatible layouts.");
            s_indexed2022Writer = (Indexed2022Writer)Delegate.CreateDelegate(typeof(Indexed2022Writer),writer);
            var translator = Type.GetType("Fraxinus.BrgContract.Managed.PlayerOutputTranslator, Fraxinus.BrgContract.Managed", false);
            var refresh = translator?.GetMethod("RefreshIndexedWork", new[] { typeof(ScriptableRenderContext) });
            if (refresh != null)
                s_refreshIndexedResources = (Action<ScriptableRenderContext>)Delegate.CreateDelegate(typeof(Action<ScriptableRenderContext>), refresh);
            submitHandler = Submit2022;
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        [System.Runtime.InteropServices.DllImport("GfxPluginBrgEvents")]
        private static extern int BrgD3D11Capabilities();
        private static Func<Material, bool> s_supportsD3D11Material;

        private static bool D3D11ReceiverReady()
        {
            if (IntPtr.Size != 8 || BatchRendererGroup.BufferTarget != BatchBufferTarget.RawBuffer ||
                !SystemInfo.supportsComputeShaders) return false;
            var translator = Type.GetType("Fraxinus.BrgContract.Managed.PlayerOutputTranslator, Fraxinus.BrgContract.Managed", false);
            var supports = translator?.GetMethod("SupportsD3D11IndexedMaterial", new[] { typeof(Material) });
            if (supports == null) return false;
            try
            {
                if (BrgD3D11Capabilities() != 1) return false;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            s_supportsD3D11Material = (Func<Material, bool>)Delegate.CreateDelegate(typeof(Func<Material, bool>), supports);
            return true;
        }

        internal static bool SupportsD3D11Material(Material material) =>
            s_supportsD3D11Material == null || s_supportsD3D11Material(material);
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("FraxinusBrgVulkanContract")]
        static extern int FxVkIsContractEnabled();

        static bool AndroidReceiverReady()
        {
            try { return FxVkIsContractEnabled() == 1; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
#endif

        // RendererList draws are recorded in a command buffer. The 2022 host's
        // immediate DrawRenderers refresh therefore cannot see this graph dispatch.
        internal static void AfterGraphCull(ScriptableRenderContext context, CommandBuffer commands)
        {
            if (s_refreshIndexedResources == null || submitHandler != Submit2022) return;
            context.ExecuteCommandBuffer(commands);
            commands.Clear();
            s_refreshIndexedResources(context);
        }

        private static unsafe bool Submit2022(in BatchCullingOutput output, ReadOnlySpan<BatchDrawCommandIndirect> commands,
            ReadOnlySpan<BatchDrawRange> ranges)
        {
            if (!output.drawCommands.IsCreated || output.drawCommands.Length == 0)
                throw new ArgumentException("Expected a BRG callback output slot.", nameof(output));
            foreach (ref readonly var command in commands)
                if ((command.flags & IndirectDrawCommandFlags.HasSortingPosition) != 0)
                    throw new NotSupportedException("This adapter accepts opaque commands without per-instance sorting positions.");
            fixed (BatchDrawCommandIndirect* cmd = commands)
            fixed (BatchDrawRange* range = ranges)
                s_indexed2022Writer((IntPtr)output.drawCommands.GetUnsafePtr(), (IntPtr)cmd, commands.Length, (IntPtr)range, ranges.Length);
            return true;
        }
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
#if UNITY_6000_0_OR_NEWER
                target->proceduralDrawCommands != null || target->proceduralIndirectDrawCommands != null ||
#endif
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
