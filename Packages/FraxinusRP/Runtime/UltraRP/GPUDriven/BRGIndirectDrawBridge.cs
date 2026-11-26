using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    internal static class BRGIndirectDrawBridge
    {
#if UNITY_2022_3 && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !ENABLE_IL2CPP))
        delegate int FlushDraws(CommandBuffer cmd, int cameraID, string passName, int layerMask, int stencilRef, int stencilMask);

        static readonly FlushDraws s_flush;
        static readonly Action<int, bool> s_setVulkanCamera;

        static BRGIndirectDrawBridge()
        {
            var api = SystemInfo.graphicsDeviceType;
            if (api != GraphicsDeviceType.Direct3D11 && api != GraphicsDeviceType.Vulkan) return;
            var type = Type.GetType("Fraxinus.BrgContract.Managed.PlayerOutputTranslator, Fraxinus.BrgContract.Managed", false);
            string name = api == GraphicsDeviceType.Vulkan ? "FlushVulkanGraphDraws" : "FlushD3D11GraphDraws";
            var method = type?.GetMethod(name, BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(CommandBuffer), typeof(int), typeof(string), typeof(int), typeof(int), typeof(int) }, null);
            if (method?.ReturnType != typeof(int)) return;
            if (api == GraphicsDeviceType.Vulkan)
            {
                var version = type.GetMethod("GetVulkanGraphVersion", Type.EmptyTypes);
                var setCamera = type.GetMethod("SetVulkanGraphCamera", new[] { typeof(int), typeof(bool) });
                if (version?.ReturnType != typeof(int) || setCamera?.ReturnType != typeof(void) ||
                    ((Func<int>)version.CreateDelegate(typeof(Func<int>)))() != 1) return;
                s_setVulkanCamera = (Action<int, bool>)setCamera.CreateDelegate(typeof(Action<int, bool>));
            }
            s_flush = (FlushDraws)method.CreateDelegate(typeof(FlushDraws));
        }

        internal static bool IsAvailable => s_flush != null;
#else
        internal static bool IsAvailable => false;
#endif

        // The Vulkan receiver remains the owner unless this camera will consume the opaque draws.
        internal readonly struct CameraScope : IDisposable
        {
            readonly int _id;

            internal CameraScope(CameraData data, InternalRendererData rendering, UltraRenderPipelineAsset asset)
            {
                _id = 0;
#if UNITY_2022_3 && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !ENABLE_IL2CPP))
                if (s_setVulkanCamera == null || !rendering.hasDeferred || !data.isGameCamera || data.isSubContext ||
                    data.renderType != CameraRenderType.Base || data.camera.stereoEnabled || data.camera != Camera.main ||
                    rendering.renderPassInputs.requiresMotionVectors || data.IsTemporalJitterEnabled() ||
                    (asset.shadowDistance > 0 && (asset.supportsMainLightShadows || asset.supportsAdditionalLightShadows)))
                    return;

                int depth = 0;
                foreach (var node in data.renderer.sortedNodes)
                {
                    if (node is ContextBeginNode) { depth++; continue; }
                    if (node is ContextEndNode) { depth--; continue; }
                    if (depth != 0 || !(node is GBufferPassNode pass) || !pass.active || !pass.canProcess || pass.computeOrder < 0)
                        continue;
                    _id = data.camera.GetInstanceID();
                    s_setVulkanCamera(_id, true);
                    break;
                }
#endif
            }

            public void Dispose()
            {
#if UNITY_2022_3 && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !ENABLE_IL2CPP))
                if (_id != 0) s_setVulkanCamera(_id, false);
#endif
            }
        }

        // Snapshot the pass filters at record time. A default request has no indirect work.
        internal readonly struct DrawRequest
        {
#if UNITY_2022_3 && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !ENABLE_IL2CPP))
            readonly int _cameraID, _layerMask, _stencilRef, _stencilMask;
            readonly string _passName;
#endif

            internal DrawRequest(CameraData camera, string passName, int layerMask, int stencilRef = 0, int stencilMask = 0)
            {
                this = default;
#if UNITY_2022_3 && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !ENABLE_IL2CPP))
                if (!IsAvailable || camera.isSubContext || !camera.camera || layerMask == 0)
                    return;

                _cameraID = camera.camera.GetInstanceID();
                _passName = passName;
                _layerMask = layerMask;
                _stencilRef = stencilRef;
                _stencilMask = stencilMask;
#endif
            }

            internal void Draw(RasterCommandBuffer cmd)
            {
#if UNITY_2022_3 && (UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !ENABLE_IL2CPP))
                if (_passName != null)
                    s_flush(cmd.m_WrappedCommandBuffer, _cameraID, _passName, _layerMask, _stencilRef, _stencilMask);
#endif
            }
        }
    }
}
