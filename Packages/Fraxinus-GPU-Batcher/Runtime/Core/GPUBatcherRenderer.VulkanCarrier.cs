#if !UNITY_6000_0_OR_NEWER && (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Fraxinus.GPUBatcher.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher
{
    public partial class GPUBatcherRenderer
    {
        // Unity selects the material, DOTS pass, winding, and shadow split using a direct
        // BRG item. Its private index allocation identifies precisely that item's GPU args.
        private sealed class VulkanCarrierMesh
        {
            public Mesh source, mesh;
            public BatchMeshID id;
            public IntPtr index;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VulkanDraw
        {
            public IntPtr index, args;
            public uint argsOffset, indexCount, firstIndex;
            public int baseVertex;
            public uint firstInstance, indexBytes;
        }

        private sealed class VulkanViewSubmission
        {
            public readonly List<VulkanCarrierMesh> meshes = new();
            public VulkanDraw[] draws = Array.Empty<VulkanDraw>();
            public GraphicsBuffer argsBuffer;
            public IntPtr argsNative;
            public IntPtr ticket;
            public bool begun;
            public int owner;
        }

        private const string k_vkPlugin = "FraxinusBrgVulkan";
        [DllImport(k_vkPlugin)] private static extern IntPtr FxVkGetEventFunc();
        [DllImport(k_vkPlugin)] private static extern int FxVkGetEventBase();
        [DllImport(k_vkPlugin)] private static extern unsafe IntPtr FxVkPrepare(VulkanDraw* list, int count);
        [DllImport(k_vkPlugin)] private static extern int FxVkCancel(IntPtr ticket);
        private static IntPtr s_vkEvent;
        private static int s_vkEventBase;
        private static bool s_vkProbed;
        private static bool VulkanCarrierAvailable
        {
            get
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan ||
                    BatchRendererGroup.BufferTarget != BatchBufferTarget.RawBuffer || IntPtr.Size != 8)
                    return false;
                if (!s_vkProbed)
                {
                    s_vkProbed = true;
                    if (Array.IndexOf(Environment.GetCommandLineArgs(), "-disableFraxinusVulkanBRG") >= 0) return false;
                    try
                    {
                        s_vkEventBase = FxVkGetEventBase();
                        if (s_vkEventBase >= 0) s_vkEvent = FxVkGetEventFunc();
                    }
                    catch (DllNotFoundException) { }
                    catch (EntryPointNotFoundException) { }
                }
                return s_vkEvent != IntPtr.Zero;
            }
        }

        private readonly Dictionary<CullingBufferSet, VulkanViewSubmission> _vkViews = new();
        private readonly List<VulkanCarrierMesh> _vkRetiredMeshes = new();
        private bool _vkReportedSubmissionError;
        private int _vkCullingCamera;
        private bool _vkCameraDeferred;

        private void VkPrimePool(bool shadow)
        {
            var commands = shadow ? _shadowDrawCommandTemplate : _cameraDrawCommandTemplate;
            var ranges = shadow ? _shadowDrawRangeTemplate : _cameraDrawRangeTemplate;
            int rangeCount = shadow ? _shadowDrawRangeCount : _cameraDrawRangeCount;
            if (!commands.IsCreated || !ranges.IsCreated || rangeCount == 0) return;
            // BRG builds its material/metadata cache when native culling starts. Register
            // the first view before Cull, including the very first frame after a rebuild.
            var pool = shadow ? _shadowBufferPool : _cameraBufferPool;
            int next = 0;
            var set = shadow
                ? AcquireBufferSet(pool, ref next, _shadowBucketCount, _shadowMaxVisibleInstances,
                    _shadowDrawCommandCount, "Shadow", _shadowBuckets, _shadowIndirectArgsTemplate)
                : AcquireBufferSet(pool, ref next, _bucketCount, _maxVisibleInstances,
                    _drawCommandCount, "Camera", _buckets, _cameraIndirectArgsTemplate);
            if (!_vkViews.TryGetValue(set, out var view)) _vkViews.Add(set, view = new VulkanViewSubmission());
            for (int r = 0; r < rangeCount; ++r)
            {
                var range = ranges[r];
                for (uint n = 0; n < range.drawCommandsCount; ++n)
                {
                    var item = commands[(int)(range.drawCommandsBegin + n)];
                    if (_meshIdReverse.TryGetValue(item.meshID, out var mesh) && VkGetCarrierBatch(set, item.batchID, out _))
                        VkMesh(view, checked((int)(item.indirectArgsBufferOffset / k_indirectArgsStride)), mesh);
                }
            }
        }

        private VulkanCarrierMesh VkMesh(VulkanViewSubmission view, int index, Mesh source)
        {
            while (view.meshes.Count <= index) view.meshes.Add(null);
            var existing = view.meshes[index];
            if (existing != null && existing.source == source) return existing;
            // Clone preserves the original vertex layout and shader inputs, including
            // wind/alpha-clipped geometry. Never register the source IB as a carrier.
            var clone = UnityEngine.Object.Instantiate(source);
            clone.hideFlags = HideFlags.HideAndDontSave;
            clone.name = source.name + " (Fraxinus Vulkan BRG)";
            var carrier = new VulkanCarrierMesh { source = source, mesh = clone };
            try
            {
                carrier.index = clone.GetNativeIndexBufferPtr();
                if (carrier.index == IntPtr.Zero || carrier.index == source.GetNativeIndexBufferPtr())
                    throw new InvalidOperationException("The BRG carrier mesh requires a private index buffer.");
                carrier.id = _brg.RegisterMesh(clone);
                if (carrier.id.value == 0) throw new InvalidOperationException("BRG carrier mesh registration failed.");
            }
            catch { CoreUtils.Destroy(clone); throw; }
            if (existing != null) _vkRetiredMeshes.Add(existing);
            view.meshes[index] = carrier;
            return carrier;
        }

        private unsafe void VkEmit(CullingBufferSet set, in BatchCullingOutput output,
            ReadOnlySpan<Rendering.BatchDrawCommandIndirect> source, ReadOnlySpan<BatchDrawRange> ranges,
            NativeArray<uint> geometry, int owner, bool deferred)
        {
            if (source.IsEmpty) return;
            if (!_vkViews.TryGetValue(set, out var view))
                _vkViews.Add(set, view = new VulkanViewSubmission());
            if (view.ticket != IntPtr.Zero)
                throw new InvalidOperationException("A Vulkan BRG view was reused before its camera ended.");
            if (view.draws.Length < source.Length) Array.Resize(ref view.draws, source.Length);
            var result = new BatchCullingOutputDrawCommands();
            try
            {
                // 2022 owns/frees these TempJob arrays after publication.
                result.drawCommands = VkAlloc<BatchDrawCommand>(source.Length);
                result.visibleInstances = VkAlloc<int>(source.Length);
                result.drawRanges = VkAlloc<BatchDrawRange>(ranges.Length);
                // GetNativeBufferPtr can synchronize Unity's render thread. The pool
                // allocates a new GraphicsBuffer object when the arguments storage changes.
                if (view.argsBuffer != set.indirectArgsBuffer || view.argsNative == IntPtr.Zero)
                {
                    view.argsBuffer = set.indirectArgsBuffer;
                    view.argsNative = view.argsBuffer.GetNativeBufferPtr();
                }
                int emitted = 0;
                for (int r = 0; r < ranges.Length; ++r)
                {
                    var range = ranges[r];
                    int firstCommand = checked((int)range.drawCommandsBegin);
                    int length = checked((int)range.drawCommandsCount);
                    if (length > source.Length - firstCommand || length > source.Length - emitted)
                        throw new InvalidOperationException("Invalid Vulkan BRG draw range.");
                    range.drawCommandsBegin = (uint)emitted;
                    result.drawRanges[r] = range;
                    for (int j = 0; j < length; ++j)
                    {
                        ref readonly var item = ref source[firstCommand + j];
                        int i = emitted++;
                        if (item.visibleOffset >= 0x00800000u || !_meshIdReverse.TryGetValue(item.meshID, out var mesh) ||
                            !VkGetCarrierBatch(set, item.batchID, out var batch))
                            throw new InvalidOperationException($"Unsupported Vulkan BRG draw at {firstCommand+j} (visibility {item.visibleOffset}, mesh {item.meshID.value}, batch {item.batchID.value}).");
                        // Shadow ranges can omit different cascades per light. Key by the
                        // producer's stable args slot instead of the compacted output index.
                        var carrier = VkMesh(view, checked((int)(item.indirectArgsBufferOffset / k_indirectArgsStride)), mesh);
                        int word = checked((int)(item.indirectArgsBufferOffset / sizeof(uint)));
                        uint elements = geometry[word], first = geometry[word + 2], vertex = geometry[word + 3];
                        int sub = 0;
                        for (; sub < mesh.subMeshCount; ++sub)
                            if (mesh.GetIndexStart(sub) == first && mesh.GetIndexCount(sub) == elements &&
                                mesh.GetBaseVertex(sub) == vertex && mesh.GetTopology(sub) == item.topology) break;
                        if (sub == mesh.subMeshCount || sub > ushort.MaxValue || elements == 0)
                            throw new InvalidOperationException("Indexed BRG command geometry does not match its source mesh.");
                        view.draws[i] = new VulkanDraw
                        {
                            index = carrier.index, args = view.argsNative, argsOffset = item.indirectArgsBufferOffset,
                            indexCount = elements, firstIndex = first, baseVertex = (int)vertex,
                            indexBytes = mesh.indexFormat == IndexFormat.UInt32 ? 4u : 2u
                        };
                        result.visibleInstances[i] = (int)item.visibleOffset |
                            ((item.flags & IndirectDrawCommandFlags.LODCrossFadeValuePacked) != 0 ? 0x00800000 : 0);
                        result.drawCommands[i] = new BatchDrawCommand
                        {
                            meshID = carrier.id, batchID = batch, materialID = item.materialID, submeshIndex = (ushort)sub,
                            splitVisibilityMask = item.splitVisibilityMask, visibleCount = 1, visibleOffset = (uint)i,
                            sortingPosition = item.sortingPosition, flags = (BatchDrawCommandFlags)((uint)item.flags & 31u)
                        };
                    }
                }
                if (emitted == 0) return;
                result.drawCommandCount = result.visibleInstanceCount = emitted;
                result.drawRangeCount = ranges.Length;
                fixed (VulkanDraw* draws = view.draws) view.ticket = FxVkPrepare(draws, emitted);
                if (view.ticket == IntPtr.Zero) throw new InvalidOperationException("Vulkan BRG plugin rejected the submission.");
                view.owner = owner;
                if (!deferred)
                {
                    var cmd = CommandBufferPool.Get("Vulkan BRG visibility");
                    try { VkStartView(cmd, set); Graphics.ExecuteCommandBuffer(cmd); }
                    finally { CommandBufferPool.Release(cmd); }
                }
                *(BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr() = result;
                result = default;
            }
            catch (Exception e)
            {
                if (view.ticket != IntPtr.Zero && !view.begun) { FxVkCancel(view.ticket); view.ticket = IntPtr.Zero; }
                if (!_vkReportedSubmissionError) { Debug.LogException(e); _vkReportedSubmissionError = true; }
            }
            finally
            {
                UnsafeUtility.Free(result.drawCommands, Allocator.TempJob);
                UnsafeUtility.Free(result.visibleInstances, Allocator.TempJob);
                UnsafeUtility.Free(result.drawRanges, Allocator.TempJob);
            }
        }

        private static unsafe T* VkAlloc<T>(int count) where T : unmanaged =>
            (T*)UnsafeUtility.Malloc((long)count * sizeof(T), UnsafeUtility.AlignOf<T>(), Allocator.TempJob);

        private void VkStartView(CommandBuffer cmd, CullingBufferSet set)
        {
            if (!_vkViews.TryGetValue(set, out var view) || view.ticket == IntPtr.Zero) return;
            if (!VkCopyVisibility(cmd, set)) throw new InvalidOperationException("Vulkan BRG instance-data copy failed.");
            cmd.IssuePluginEventAndData(s_vkEvent, s_vkEventBase + (view.begun ? 2 : 0), view.ticket);
            view.begun = true;
        }

        private void VkEndViews(int camera)
        {
            CommandBuffer cmd = null;
            try
            {
                foreach (var view in _vkViews.Values)
                {
                    if (view.owner != camera || view.ticket == IntPtr.Zero) continue;
                    if (view.begun)
                    {
                        cmd ??= CommandBufferPool.Get("End Vulkan BRG view");
                        cmd.IssuePluginEventAndData(s_vkEvent, s_vkEventBase + 1, view.ticket);
                    }
                    else FxVkCancel(view.ticket);
                    view.ticket = IntPtr.Zero;
                    view.begun = false;
                }
                if (cmd != null) Graphics.ExecuteCommandBuffer(cmd);
            }
            finally { if (cmd != null) CommandBufferPool.Release(cmd); }
        }

        private void VkDisposeSubmission()
        {
            var cmd = CommandBufferPool.Get("Release Vulkan BRG views");
            try
            {
                foreach (var view in _vkViews.Values)
                {
                    if (view.ticket != IntPtr.Zero)
                    {
                        if (view.begun) cmd.IssuePluginEventAndData(s_vkEvent, s_vkEventBase + 1, view.ticket);
                        else FxVkCancel(view.ticket);
                    }
                    foreach (var carrier in view.meshes) if (carrier != null) _vkRetiredMeshes.Add(carrier);
                }
                if (cmd.sizeInBytes > 0) Graphics.ExecuteCommandBuffer(cmd);
            }
            finally { CommandBufferPool.Release(cmd); }
            foreach (var carrier in _vkRetiredMeshes)
            {
                _brg?.UnregisterMesh(carrier.id);
                CoreUtils.Destroy(carrier.mesh);
            }
            _vkRetiredMeshes.Clear();
            _vkViews.Clear();
            VkDisposeVisibility();
        }
    }
}
#endif
