#if UNITY_EDITOR
using System.Collections.Generic;
#if FRAXINUS_RP
using FraxinusEngine.Rendering.Ultra;
#endif
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher
{
    internal static class GPUBatcherDebugCameras
    {
        private static readonly List<Camera> s_renderedGameCameras = new();
        private static Camera s_selectedReferenceCamera;
        private static int s_lastGameRenderFrame = -1;

        internal static IReadOnlyList<Camera> renderedGameCameras
        {
            get
            {
                s_renderedGameCameras.RemoveAll(camera => !IsEligible(camera));
                return s_renderedGameCameras;
            }
        }

        internal static Camera selectedReferenceCamera
        {
            get => IsEligible(s_selectedReferenceCamera) ? s_selectedReferenceCamera : null;
            set => s_selectedReferenceCamera = IsEligible(value) ? value : null;
        }

        // Resolve before the first camera callback without caching Camera.main.
        internal static Camera referenceCamera
        {
            get
            {
#if FRAXINUS_RP
                if (!(GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset)) return null;
                if (IsEligible(s_selectedReferenceCamera)) return s_selectedReferenceCamera;
                var main = Camera.main;
                return IsEligible(main) ? main : null;
#else
                return null;
#endif
            }
        }

        private static bool IsEligible(Camera camera) =>
            camera && camera.isActiveAndEnabled && camera.cameraType == CameraType.Game;

        internal static void OnBeforeCameraCulling(Camera camera, in ScriptableCullingParameters parameters)
        {
            if (!IsEligible(camera)) return;
            // Only an actual Game render advances the batch. Same-frame requests and
            // overlays share it; idle Editor updates and SceneView repaints retain it.
            if (s_lastGameRenderFrame != Time.frameCount)
            {
                s_renderedGameCameras.Clear();
                s_lastGameRenderFrame = Time.frameCount;
            }
            if (!s_renderedGameCameras.Contains(camera)) s_renderedGameCameras.Add(camera);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void Clear()
        {
            s_renderedGameCameras.Clear();
            s_selectedReferenceCamera = null;
            s_lastGameRenderFrame = -1;
        }

    }
}
#endif
