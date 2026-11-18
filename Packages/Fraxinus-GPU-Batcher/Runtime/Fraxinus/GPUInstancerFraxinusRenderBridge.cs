#if GPUI_FRAXINUS
using FraxinusEngine.Rendering.Ultra;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher
{
    /// <summary>Submits indirect draws before Fraxinus culls each game camera.</summary>
    internal static class GPUInstancerFraxinusRenderBridge
    {
        private static bool s_installed;

        internal static bool isActive => s_installed &&
            GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset;

        internal static void Install()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            s_installed = true;
        }

        internal static void Uninstall()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            s_installed = false;
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!Application.isPlaying || !isActive || camera == null ||
                !camera.isActiveAndEnabled || camera.cameraType != CameraType.Game)
                return;
#if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isPaused)
                return;
#endif
            if (camera.TryGetComponent<UltraAdditionalCameraData>(out var additionalData) &&
                additionalData.renderType == CameraRenderType.Overlay)
                return;

            // CameraRenderingScope runs before CreateCameraData and context.Cull,
            // so opaque and shadow renderer lists both see these indirect draws.
            GPUInstancerManagerHub.RenderForCamera(camera);
        }
    }
}
#endif
