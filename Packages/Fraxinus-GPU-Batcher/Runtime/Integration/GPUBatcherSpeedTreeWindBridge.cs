// Adapted for Fraxinus GPU Batcher from BRG Instanced Renderer.
// Original implementation copyright (c) 2026 Milk_Drinker01. All rights reserved.

using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Fraxinus.GPUBatcher
{
    /// <summary>
    /// Captures current and previous SpeedTree wind parameters by drawing Tree proxies into a GPU buffer.
    /// </summary>
    public static class GPUBatcherSpeedTreeWindBridge
    {
        private const int k_maxWindParamsCount = 16;
        public static int maxWindParamsCount => k_maxWindParamsCount;
        const int k_vectorsPerRenderer = 32, k_bytesPerRenderer = 512, k_proxyLayer = 31;
        const string k_captureResource = "FraxinusGPUBatcherSpeedTreeWindCapture";
        static readonly int s_outputOffset = Shader.PropertyToID("_FraxinusWindOutputByteOffset");
        static readonly ShaderTagId s_captureTag = new ShaderTagId("FraxinusGPUBatcherWindCaptureCurrent");
        static readonly ShaderTagId s_historyTag = new ShaderTagId("FraxinusGPUBatcherWindCaptureHistory");
        static readonly Dictionary<int, Proxy> s_proxies = new Dictionary<int, Proxy>();
        static readonly Dictionary<int, int> s_retainCounts = new Dictionary<int, int>();
        static readonly Stack<int> s_freeCaptureSlots = new Stack<int>();
        static readonly List<GraphicsBuffer> s_retiredBuffers = new List<GraphicsBuffer>();
        static int s_captureSlotHighWater;
        static readonly Vector4[] s_readback = new Vector4[k_vectorsPerRenderer];
        static Material s_material;
        static GameObject s_inactiveRoot;
        static Camera s_camera;
        static RenderTexture s_target;
        static CommandBuffer s_commands;
        static ComputeShader s_copyShader;
        static int s_copyKernel;
        static GraphicsBuffer s_slotMap;
        static uint[] s_slotSources = Array.Empty<uint>();

        sealed class Proxy
        {
            internal Renderer source;
            internal Transform sourceRoot;
            internal Tree sourceTree, nativeTree;
            internal GameObject root;
            internal Renderer renderer;
            internal Material material;
            internal bool initialized;
            internal int captureSlot;
        }

        /// <summary>Whether the device can write wind parameters from a draw pass.</summary>
        public static bool isSupported => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null &&
            SystemInfo.graphicsShaderLevel >= 45 && SystemInfo.supportedRandomWriteTargetCount > 1;
        internal static bool IsCaptureCamera(int instanceID) => s_camera && s_camera.GetInstanceID() == instanceID;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => Dispose();
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void RegisterEditorCleanup()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            UnityEditor.EditorApplication.quitting -= Dispose;
            UnityEditor.EditorApplication.quitting += Dispose;
        }
#endif

        internal static bool RetainRenderer(int id)
        {
            if (id == 0) return false;
            s_retainCounts.TryGetValue(id, out int owners);
            s_retainCounts[id] = owners + 1;
            return TryGetProxy(id, out _);
        }

        internal static void ReleaseRenderer(int id)
        {
            if (s_retainCounts.TryGetValue(id, out int owners) && owners > 1)
            {
                s_retainCounts[id] = owners - 1;
                return;
            }
            s_retainCounts.Remove(id);
            if (s_proxies.TryGetValue(id, out var proxy))
            {
                DestroyProxy(proxy); s_freeCaptureSlots.Push(proxy.captureSlot);
            }
            s_proxies.Remove(id);
        }

        /// <summary>Run once per rendering context after slot uploads, before camera/shadow rendering.</summary>
        internal static bool Capture(ScriptableRenderContext context, int[] ids, int count,
            GraphicsBuffer destination, int byteOffset)
        {
            if (ids == null || count < 0 || count > ids.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if ((destination.target & GraphicsBuffer.Target.Raw) == 0 || byteOffset < 0 || (byteOffset & 15) != 0 ||
                (long)byteOffset + (long)count * k_bytesPerRenderer > (long)destination.count * destination.stride)
                throw new ArgumentException("Wind capture needs a raw buffer with an aligned, in-bounds byte range.");
            if (count == 0) return true;
            if (!EnsureResources()) return false;
            foreach (var proxy in s_proxies.Values)
            {
                proxy.initialized = false;
                if (proxy.renderer) proxy.renderer.forceRenderingOff = true;
            }
            bool success = true;
            int active = 0;
            Bounds captureBounds = default;
            for (int i = 0; i < count; ++i)
            {
                if (ids[i] == 0) continue;
                if (!TryGetProxy(ids[i], out var proxy)) { success = false; continue; }
                proxy.root.transform.SetPositionAndRotation(proxy.sourceRoot.position, proxy.sourceRoot.rotation);
                proxy.root.transform.localScale = proxy.sourceRoot.lossyScale;
                proxy.nativeTree.transform.SetPositionAndRotation(proxy.sourceTree.transform.position, proxy.sourceTree.transform.rotation);
                proxy.renderer.forceRenderingOff = false;
                if (active == 0) captureBounds = proxy.renderer.bounds;
                else captureBounds.Encapsulate(proxy.renderer.bounds);
                ++active;
            }
            // Several destination slots can share the same captured source.
            EnsureCaptureBuffer(Math.Max(count, s_captureSlotHighWater));
            for (int i = 0; i < count; ++i)
            {
                s_slotSources[i] = ids[i] != 0 && s_proxies.TryGetValue(ids[i], out var slotProxy) ? (uint)slotProxy.captureSlot : uint.MaxValue;
            }
            if (active == 0)
            {
                s_commands.Clear(); s_commands.SetBufferData(s_slotMap, s_slotSources, 0, 0, count);
                RecordSlotCopy(destination, count, byteOffset);
                context.ExecuteCommandBuffer(s_commands); s_commands.Clear();
                return success;
            }
            // Native Cull rejects an empty plane set. Fit a valid orthographic frustum around
            // the retained meshes without moving their wind-sampling positions.
            s_camera.aspect = k_vectorsPerRenderer;
            s_camera.orthographicSize = Math.Max(1, Math.Max(captureBounds.extents.y, captureBounds.extents.x / k_vectorsPerRenderer) + 1);
            s_camera.nearClipPlane = 0.1f;
            s_camera.farClipPlane = Math.Max(2, captureBounds.size.z + 2);
            s_camera.transform.position = captureBounds.center - Vector3.forward * (captureBounds.extents.z + 1);
            if (!s_camera.TryGetCullingParameters(out var parameters))
            {
                foreach (var proxy in s_proxies.Values) if (proxy.renderer) proxy.renderer.forceRenderingOff = true;
                return false;
            }
            parameters.cullingMask = 1u << k_proxyLayer;
            // Keep the engine's normal renderer preparation, including native Tree wind setup.
            parameters.cullingOptions &= ~(CullingOptions.OcclusionCull | CullingOptions.ShadowCasters);
            parameters.shadowDistance = 0;
            parameters.maximumVisibleLights = 0;
            var culling = context.Cull(ref parameters);
            context.SetupCameraProperties(s_camera);
            var drawing = new DrawingSettings(s_captureTag, new SortingSettings(s_camera))
            {
                enableInstancing = false, enableDynamicBatching = false,
                perObjectData = PerObjectData.MotionVectors
            };
            var filtering = new FilteringSettings(RenderQueueRange.all, 1 << k_proxyLayer);
            s_commands.Clear();
            s_commands.SetBufferData(s_slotMap, s_slotSources, 0, 0, count);
            s_commands.SetRenderTarget(s_target);
            s_commands.SetViewport(new Rect(0, 0, k_maxWindParamsCount, 1));
            s_commands.SetRandomWriteTarget(1, s_captureBuffer);
            context.ExecuteCommandBuffer(s_commands);
            context.DrawRenderers(culling, ref drawing, ref filtering);
            s_commands.Clear();
            s_commands.SetViewport(new Rect(k_maxWindParamsCount, 0, k_maxWindParamsCount, 1));
            context.ExecuteCommandBuffer(s_commands);
            drawing.SetShaderPassName(0, s_historyTag);
            context.DrawRenderers(culling, ref drawing, ref filtering);
            s_commands.Clear();
            s_commands.ClearRandomWriteTargets();
            RecordSlotCopy(destination, count, byteOffset);
            foreach (var proxy in s_proxies.Values)
            {
                if (!proxy.renderer || proxy.renderer.forceRenderingOff) continue;
                proxy.initialized = true;
            }
            context.ExecuteCommandBuffer(s_commands);
            s_commands.Clear();
            return success;
        }

        static void RecordSlotCopy(GraphicsBuffer destination, int count, int byteOffset)
        {
            s_commands.SetComputeBufferParam(s_copyShader, s_copyKernel, "_WindCapture", s_captureBuffer);
            s_commands.SetComputeBufferParam(s_copyShader, s_copyKernel, "_WindSlotSources", s_slotMap);
            s_commands.SetComputeBufferParam(s_copyShader, s_copyKernel, "_WindDestination", destination);
            s_commands.SetComputeIntParam(s_copyShader, "_WindSlotCount", count);
            s_commands.SetComputeIntParam(s_copyShader, "_WindDestinationByteOffset", byteOffset);
            s_commands.DispatchCompute(s_copyShader, s_copyKernel, (count * k_vectorsPerRenderer + 63) / 64, 1, 1);
        }

        static GraphicsBuffer s_captureBuffer;
        static void EnsureCaptureBuffer(int slots)
        {
            int capacity = Mathf.NextPowerOfTwo(Math.Max(1, slots));
            int count = capacity * k_bytesPerRenderer / 4;
            if (s_captureBuffer != null && s_captureBuffer.count >= count) return;
            // Earlier draws may still be queued in the same ScriptableRenderContext. Power-of-two
            // growth bounds the total retired storage below the current capacity until teardown.
            if (s_captureBuffer != null) s_retiredBuffers.Add(s_captureBuffer);
            s_captureBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, count, 4);
            if (s_slotMap != null) s_retiredBuffers.Add(s_slotMap);
            s_slotMap = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 4);
            s_slotSources = new uint[capacity];
        }

        public static void ComputeWindParams(int rendererInstanceID, NativeArray<Vector4> output, bool history)
        {
            if (!output.IsCreated || output.Length < k_maxWindParamsCount)
                throw new ArgumentException("Output must hold 16 wind vectors.", nameof(output));
            TryReadWind(rendererInstanceID, output, 0, history);
        }
        public static void ComputeWindParams(NativeArray<int> ids, NativeArray<Vector4> output, bool history)
            => TryComputeWindParams(ids, output, history);

        /// <summary>CPU snapshot of a source in the most recent capture, after its context is submitted.</summary>
        public static bool TryComputeWindParams(NativeArray<int> ids, NativeArray<Vector4> output, bool history)
        {
            if (!ids.IsCreated) throw new ArgumentException("Renderer IDs must be allocated.", nameof(ids));
            if (!output.IsCreated || output.Length < ids.Length * k_maxWindParamsCount)
                throw new ArgumentException("Output must hold 16 wind vectors per renderer.", nameof(output));
            bool success = true;
            for (int i = 0; i < ids.Length; ++i) success &= TryReadWind(ids[i], output, i * k_maxWindParamsCount, history);
            return success;
        }
        static bool TryReadWind(int id, NativeArray<Vector4> output, int start, bool history)
        {
            if (!s_proxies.TryGetValue(id, out var proxy) || !proxy.source || !proxy.initialized || s_captureBuffer == null)
            {
                for (int i = 0; i < k_maxWindParamsCount; ++i) output[start + i] = Vector4.zero;
                return false;
            }
            s_captureBuffer.GetData(s_readback, 0, proxy.captureSlot * k_vectorsPerRenderer, k_vectorsPerRenderer);
            for (int i = 0; i < k_maxWindParamsCount; ++i) output[start + i] = s_readback[i + (history ? k_maxWindParamsCount : 0)];
            return true;
        }

        static bool EnsureResources()
        {
            if (!isSupported) return false;
            if (s_material) return true;
            var shader = Resources.Load<Shader>(k_captureResource);
            if (!shader || !shader.isSupported) return false;
            s_copyShader = Resources.Load<ComputeShader>("FraxinusGPUBatcherSpeedTreeWindCopy");
            if (!s_copyShader || !SystemInfo.supportsComputeShaders) return false;
            s_copyKernel = s_copyShader.FindKernel("CopyWindSlots");
            s_material = new Material(shader) { name = "Fraxinus native wind capture", hideFlags = HideFlags.HideAndDontSave };
            s_material.EnableKeyword("_WINDQUALITY_BEST");
            s_target = new RenderTexture(k_vectorsPerRenderer, 1, 0, RenderTextureFormat.ARGB32)
                { name = "Fraxinus native wind target", hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1 };
            if (!s_target.Create())
            {
                DestroyOwned(s_target); s_target = null;
                DestroyOwned(s_material); s_material = null; return false;
            }
            s_commands = new CommandBuffer { name = "FraxinusGPUBatcher.NativeSpeedTreeWind" };
            s_inactiveRoot = new GameObject("Fraxinus wind proxy construction") { hideFlags = HideFlags.HideAndDontSave };
            s_inactiveRoot.SetActive(false);
            var cameraObject = new GameObject("Fraxinus native wind camera") { hideFlags = HideFlags.HideAndDontSave };
            s_camera = cameraObject.AddComponent<Camera>(); s_camera.enabled = false;
            s_camera.cullingMask = 1 << k_proxyLayer; s_camera.useOcclusionCulling = false;
            s_camera.orthographic = true; s_camera.orthographicSize = 100000; s_camera.farClipPlane = 100000;
            s_camera.targetTexture = s_target;
            if (Application.isPlaying) { Object.DontDestroyOnLoad(s_inactiveRoot); Object.DontDestroyOnLoad(cameraObject); }
            return true;
        }

        static bool TryGetProxy(int id, out Proxy proxy)
        {
            proxy = null;
            if (id == 0 || !EnsureResources()) return false;
            if (s_proxies.TryGetValue(id, out proxy))
            {
                if (proxy.source && proxy.sourceTree && proxy.sourceRoot && proxy.root && proxy.renderer && proxy.nativeTree) return true;
                DestroyProxy(proxy); s_freeCaptureSlots.Push(proxy.captureSlot);
                s_proxies.Remove(id); proxy = null;
            }
            var source = Resources.InstanceIDToObject(id) as MeshRenderer;
            if (!source) return false;
            var tree = source.GetComponentInParent<Tree>(true);
            if (!tree) return false;
            var group = source.GetComponentInParent<LODGroup>(true);
            var sourceRoot = group ? group.transform : tree.transform;
            int sourceLod = 0;
            if (group)
            {
                var lods = group.GetLODs();
                for (int lod = 0; lod < lods.Length; ++lod)
                    foreach (var member in lods[lod].renderers) if (member == source) sourceLod = lod;
            }
            var path = new List<int>();
            for (var node = source.transform; node != sourceRoot; node = node.parent) path.Add(node.GetSiblingIndex());
            // Inactive construction prevents source scripts receiving Awake/OnEnable before removal.
            var root = Object.Instantiate(sourceRoot.gameObject, s_inactiveRoot.transform, false);
            root.name = "Fraxinus native wind: " + source.name;
            var target = root.transform;
            for (int i = path.Count - 1; i >= 0; --i) target = target.GetChild(path[i]);
            var renderer = target.GetComponent<MeshRenderer>(); var filter = target.GetComponent<MeshFilter>();
            var nativeTree = renderer ? renderer.GetComponentInParent<Tree>(true) : null;
            var nativeGroup = root.GetComponent<LODGroup>();
            if (!renderer || !filter || !nativeTree) { DestroyOwned(root); return false; }
            if (!TryGetTriangle(filter.sharedMesh, out int vertex0, out int vertex1, out int vertex2))
            {
                DestroyOwned(root); return false;
            }
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
                node.gameObject.hideFlags = HideFlags.HideAndDontSave;
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour) Object.DestroyImmediate(behaviour);
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!component) continue;
                if (component is Transform || component is Renderer || component is MeshFilter || component is Tree || component is LODGroup)
                    component.hideFlags = HideFlags.HideAndDontSave;
                else Object.DestroyImmediate(component);
            }
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                node.gameObject.hideFlags = HideFlags.HideAndDontSave; node.gameObject.layer = k_proxyLayer;
                node.gameObject.SetActive(true);
            }
            foreach (var member in root.GetComponentsInChildren<Renderer>(true))
            {
                member.enabled = member == renderer; member.forceRenderingOff = true;
                member.shadowCastingMode = ShadowCastingMode.Off; member.receiveShadows = false;
                member.lightProbeUsage = LightProbeUsage.Off; member.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            // Keep all original LODs. SpeedTree's last LOD uses global-only wind, so collapsing
            // the hierarchy to one LOD silently drops branch/leaf parameters on the captured LOD.
            if (nativeGroup) nativeGroup.enabled = true;
            // Native Tree initialization and ongoing wind evaluation retain the imported mesh's
            // SpeedTree metadata. Only the material is replaced after native activation.
            root.transform.SetParent(null, false);
            root.transform.SetPositionAndRotation(sourceRoot.position, sourceRoot.rotation); root.transform.localScale = sourceRoot.lossyScale;
            if (nativeGroup) nativeGroup.ForceLOD(sourceLod);
            if (Application.isPlaying) Object.DontDestroyOnLoad(root);
            if (!nativeTree.hasSpeedTreeWind) { DestroyOwned(root); return false; }
            int captureSlot = s_freeCaptureSlots.Count > 0 ? s_freeCaptureSlots.Pop() : s_captureSlotHighWater++;
            // Native Tree rendering supplies its own property block. Stable capture integers
            // must live in the owned material, otherwise that block replaces per-renderer values.
            var material = new Material(s_material) { name = root.name, hideFlags = HideFlags.HideAndDontSave };
            material.SetInteger(s_outputOffset, captureSlot * k_bytesPerRenderer);
            material.SetInteger("_FraxinusWindVertex0", vertex0);
            material.SetInteger("_FraxinusWindVertex1", vertex1);
            material.SetInteger("_FraxinusWindVertex2", vertex2);
            // Retain only submesh zero's private material; no original material may render a
            // proxy in the main camera while the queued capture is waiting for submission.
            renderer.sharedMaterials = new[] { material }; renderer.SetPropertyBlock(null); renderer.SetPropertyBlock(null, 0);
#if UNITY_EDITOR
            UnityEditor.SceneVisibilityManager.instance.DisablePicking(root, true);
#endif
            proxy = new Proxy { source = source, sourceRoot = sourceRoot, sourceTree = tree, nativeTree = nativeTree,
                root = root, renderer = renderer, material = material, captureSlot = captureSlot };
            s_proxies.Add(id, proxy); return true;
        }

        // Read the index buffer once during prototype construction, never per frame. Keeping the
        // imported mesh retains native SpeedTree flags; three selected vertices cover the tiny RT.
        static bool TryGetTriangle(Mesh mesh, out int a, out int b, out int c)
        {
            a = b = c = 0;
            if (!mesh || mesh.subMeshCount == 0) return false;
            var submesh = mesh.GetSubMesh(0);
            if (submesh.topology != MeshTopology.Triangles || submesh.indexCount < 3) return false;
            if (mesh.isReadable)
            {
                var indices = mesh.GetIndices(0, false);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    if (indices[i] == indices[i + 1] || indices[i] == indices[i + 2] || indices[i + 1] == indices[i + 2]) continue;
                    a = indices[i] + submesh.baseVertex; b = indices[i + 1] + submesh.baseVertex; c = indices[i + 2] + submesh.baseVertex;
                    return true;
                }
                return false;
            }
            using (var buffer = mesh.GetIndexBuffer())
            {
                if (buffer == null) return false;
                const int k_blockSize = 3 * 256;
                var indices16 = mesh.indexFormat == IndexFormat.UInt16 ? new ushort[k_blockSize] : null;
                var indices32 = indices16 == null ? new uint[k_blockSize] : null;
                for (int start = 0; start < submesh.indexCount; start += k_blockSize)
                {
                    int length = Math.Min(k_blockSize, submesh.indexCount - start);
                    if (indices16 != null) buffer.GetData(indices16, 0, submesh.indexStart + start, length);
                    else buffer.GetData(indices32, 0, submesh.indexStart + start, length);
                    for (int i = 0; i + 2 < length; i += 3)
                    {
                        int v0 = indices16 != null ? indices16[i] : (int)indices32[i];
                        int v1 = indices16 != null ? indices16[i + 1] : (int)indices32[i + 1];
                        int v2 = indices16 != null ? indices16[i + 2] : (int)indices32[i + 2];
                        if (v0 == v1 || v0 == v2 || v1 == v2) continue;
                        a = v0 + submesh.baseVertex; b = v1 + submesh.baseVertex; c = v2 + submesh.baseVertex;
                        return true;
                    }
                }
            }
            return false;
        }

        static void DestroyOwned(Object value)
        {
            if (!value) return;
            if (value is GameObject gameObject) gameObject.SetActive(false);
            if (Application.isPlaying) Object.Destroy(value); else Object.DestroyImmediate(value);
        }
        static void DestroyProxy(Proxy proxy)
        {
            DestroyOwned(proxy.root);
            DestroyOwned(proxy.material);
        }
        public static void Dispose()
        {
            foreach (var proxy in s_proxies.Values) DestroyProxy(proxy); s_proxies.Clear(); s_retainCounts.Clear();
            s_freeCaptureSlots.Clear(); s_captureSlotHighWater = 0;
            s_captureBuffer?.Dispose(); s_captureBuffer = null;
            foreach (var buffer in s_retiredBuffers) buffer.Dispose(); s_retiredBuffers.Clear();
            s_slotMap?.Dispose(); s_slotMap = null; s_slotSources = Array.Empty<uint>(); s_copyShader = null;
            s_commands?.Release(); s_commands = null;
            if (s_camera) { s_camera.targetTexture = null; DestroyOwned(s_camera.gameObject); } s_camera = null;
            DestroyOwned(s_inactiveRoot); s_inactiveRoot = null;
            DestroyOwned(s_material); s_material = null;
            if (s_target) s_target.Release(); DestroyOwned(s_target); s_target = null;
        }
    }
}
