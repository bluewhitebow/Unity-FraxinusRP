#if UNITY_2022_3 && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !ENABLE_IL2CPP))
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher.Rendering
{
    // The stock and custom editors report the same version. Bind only the extension
    // published by the custom CoreModule and a matching Win64 graphics receiver.
    internal static class Custom2022IndirectSubmission
    {
        private delegate void WriteOutput(IntPtr outputSlot, IntPtr commands, int commandCount, IntPtr ranges, int rangeCount);

        [StructLayout(LayoutKind.Sequential, Size = 120)]
        private struct ContractStats { }

        [DllImport("FraxinusBrgContractNative", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FxBrgGetContractStats(out ContractStats stats, uint bytes);
        [DllImport("FraxinusBrgVulkanContract", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FxVkIsContractEnabled();

        private static readonly WriteOutput s_write;
        private static readonly Action<ScriptableRenderContext> s_refresh;
        private static readonly Func<bool> s_hasPendingCarriers;
        private static readonly Action<GraphicsBuffer, int, int, int> s_setLayout;

        static Custom2022IndirectSubmission()
        {
            var api = SystemInfo.graphicsDeviceType;
            if (IntPtr.Size != 8 || (api != GraphicsDeviceType.Vulkan && api != GraphicsDeviceType.Direct3D11) ||
                !SystemInfo.supportsComputeShaders || BatchRendererGroup.BufferTarget != BatchBufferTarget.RawBuffer)
                return;
            var writer = typeof(BatchRendererGroup).Assembly.GetType("UnityEngine.Rendering.IndexedBrgOutputWriter");
            if (writer == null) return;
            var helper = Type.GetType("Fraxinus.BrgContract.Managed.PlayerOutputTranslator, Fraxinus.BrgContract.Managed", false);
            var write = writer.GetMethod("Write", new[] {typeof(IntPtr), typeof(IntPtr), typeof(int), typeof(IntPtr), typeof(int)});
            var refresh = helper?.GetMethod("RefreshIndexedWork", new[] { typeof(ScriptableRenderContext) });
            if (write == null || refresh == null || UnsafeUtility.SizeOf<BatchDrawCommandIndirect>() != 52 ||
                UnsafeUtility.SizeOf<BatchDrawRange>() != 40 || UnsafeUtility.SizeOf<BatchCullingOutputDrawCommands>() != 96)
                return;
            try
            {
                if (FxBrgGetContractStats(out _, (uint)Marshal.SizeOf<ContractStats>()) != 0)
                    return;
                if (api == GraphicsDeviceType.Direct3D11)
                {
                    var getVersion = helper.GetMethod("GetD3D11GraphVersion", Type.EmptyTypes);
                    var setLayout = helper.GetMethod("SetD3D11GraphBatchLayout",
                        new[] { typeof(GraphicsBuffer), typeof(int), typeof(int), typeof(int) });
                    if (getVersion == null || setLayout == null ||
                        ((Func<int>)getVersion.CreateDelegate(typeof(Func<int>)))() != 1)
                        return;
                    s_setLayout = (Action<GraphicsBuffer, int, int, int>)setLayout.CreateDelegate(typeof(Action<GraphicsBuffer, int, int, int>));
                }
                else
                {
                    if (FxVkIsContractEnabled() != 1) return;
                    var setLayout = helper.GetMethod("SetVulkanGraphBatchLayout",
                        new[] { typeof(GraphicsBuffer), typeof(int), typeof(int), typeof(int) });
                    if (setLayout?.ReturnType == typeof(void))
                        s_setLayout = (Action<GraphicsBuffer, int, int, int>)setLayout.CreateDelegate(typeof(Action<GraphicsBuffer, int, int, int>));
                    var pending = helper.GetMethod("HasPendingVulkanCarrierDraws", Type.EmptyTypes);
                    if (pending?.ReturnType == typeof(bool))
                        s_hasPendingCarriers = (Func<bool>)pending.CreateDelegate(typeof(Func<bool>));
                }
            }
            catch (DllNotFoundException) { return; }
            catch (EntryPointNotFoundException) { return; }
            if (api == GraphicsDeviceType.Vulkan)
                s_refresh = (Action<ScriptableRenderContext>)refresh.CreateDelegate(typeof(Action<ScriptableRenderContext>));
            s_write = (WriteOutput)write.CreateDelegate(typeof(WriteOutput));
        }

        internal static bool isAvailable => s_write != null;

        internal static void SetMatrixLayout(GraphicsBuffer buffer, uint objectToWorld, uint worldToObject, uint previous)
            => s_setLayout?.Invoke(buffer, checked((int)objectToWorld), checked((int)worldToObject), checked((int)previous));

        internal static unsafe bool TrySubmit(in BatchCullingOutput output, ReadOnlySpan<BatchDrawCommandIndirect> commands,
            ReadOnlySpan<BatchDrawRange> ranges)
        {
            if (!isAvailable) return false;
            if (!output.drawCommands.IsCreated || output.drawCommands.Length == 0)
                throw new ArgumentException("A BRG callback output slot is required.", nameof(output));
            foreach (ref readonly var command in commands)
                if ((command.flags & IndirectDrawCommandFlags.HasSortingPosition) != 0)
                    throw new NotSupportedException("The indexed BRG bridge accepts opaque commands without sorting positions.");
            // The host's writer owns copying, marking ranges Indirect and publishing TempJob memory.
            fixed (BatchDrawCommandIndirect* source = commands)
            fixed (BatchDrawRange* range = ranges)
                s_write((IntPtr)output.drawCommands.GetUnsafePtr(), (IntPtr)source, commands.Length, (IntPtr)range, ranges.Length);
            return true;
        }

        internal static void AfterGraphCull(ScriptableRenderContext context, CommandBuffer cmd)
        {
            if (s_refresh == null || (s_hasPendingCarriers != null && !s_hasPendingCarriers())) return;
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            // Rebind the visibility/argument buffers after the GPU producer, before renderer lists consume them.
            s_refresh(context);
        }
    }
}
#endif
