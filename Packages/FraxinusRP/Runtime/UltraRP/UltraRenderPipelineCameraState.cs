#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>Editor camera choices shared by Fraxinus rendering diagnostics.</summary>
    public static class UltraRenderPipelineCameraState
    {
        private static readonly List<Camera> s_renderedGameCameras = new List<Camera>();
        private static Camera s_selectedReferenceCamera;
        private static int s_lastGameRenderFrame = -1;

        /// <summary>Game cameras from the most recent frame with Game rendering; idle and SceneView repaints retain the list.</summary>
        public static IReadOnlyList<Camera> renderedGameCameras
        {
            get { Prune(); return s_renderedGameCameras; }
        }

        public static Camera selectedReferenceCamera
        {
            get => IsEligible(s_selectedReferenceCamera) ? s_selectedReferenceCamera : null;
            set => s_selectedReferenceCamera = IsEligible(value) ? value : null;
        }

        // Resolve live, including the first beginCameraRendering callback, without a cached Camera.main.
        public static Camera referenceCamera
        {
            get
            {
                if (!(GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset)) return null;
                if (IsEligible(s_selectedReferenceCamera)) return s_selectedReferenceCamera;
                var main = Camera.main;
                return IsEligible(main) ? main : null;
            }
        }

        public static bool IsSceneViewCamera(Camera camera) => camera && camera.cameraType == CameraType.SceneView;

        public static bool IsReferenceCamera(Camera camera) => IsEligible(camera) && camera == referenceCamera;

        private static bool IsEligible(Camera camera) => camera && camera.isActiveAndEnabled && camera.cameraType == CameraType.Game;

        internal static void Record(Camera camera)
        {
            if (!IsEligible(camera)) return;
            // Same-frame standalone requests and nested contexts share the batch. Only an
            // actual Game render rolls it forward; editor update ticks do not expire cameras.
            if (s_lastGameRenderFrame != Time.frameCount)
            {
                s_renderedGameCameras.Clear();
                s_lastGameRenderFrame = Time.frameCount;
            }
            if (!s_renderedGameCameras.Contains(camera)) s_renderedGameCameras.Add(camera);
        }

        private static void Prune()
        {
            bool active = GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset;
            for (int i = s_renderedGameCameras.Count - 1; i >= 0; --i)
            {
                var camera = s_renderedGameCameras[i];
                if (active && IsEligible(camera)) continue;
                s_renderedGameCameras.RemoveAt(i);
            }
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
