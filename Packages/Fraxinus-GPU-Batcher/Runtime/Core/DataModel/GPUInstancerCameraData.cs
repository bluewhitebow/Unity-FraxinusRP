using System;
using UnityEngine;
#if GPUI_FRAXINUS
using FraxinusEngine.Rendering.Ultra;
#endif

namespace Fraxinus.GPUBatcher
{
    [Serializable]
    public class GPUInstancerCameraData
    {
        public Camera mainCamera;
        public bool renderOnlySelectedCamera = false;
        [NonSerialized]
        public RenderTexture hiZDepthTexture;
        [NonSerialized]
        public Vector2 hiZTextureSize;
        [NonSerialized]
        public Matrix4x4 mvpMatrix;
        [NonSerialized]
        public Matrix4x4 mvpMatrix2;
        [NonSerialized]
        public Vector3 cameraPosition = Vector3.zero;
        [NonSerialized]
        public bool hasOcclusionDepth = false;
        [NonSerialized]
        public float halfAngle;

        public GPUInstancerCameraData() : this(null) { }

        public GPUInstancerCameraData(Camera mainCamera)
        {
            this.mainCamera = mainCamera;
            CalculateHalfAngle();
        }

        public void SetCamera(Camera mainCamera)
        {
            this.mainCamera = mainCamera;
            hiZDepthTexture = null;
            hiZTextureSize = Vector2.zero;
            hasOcclusionDepth = false;
            CalculateHalfAngle();
        }

        public void CalculateCameraData(bool useOcclusionCulling = false)
        {
            // The render pipeline owns this texture and its lifetime. Only borrow valid history.
            hiZDepthTexture = null;
            hiZTextureSize = Vector2.zero;
            hasOcclusionDepth = false;
#if GPUI_FRAXINUS
            if (useOcclusionCulling && Application.isPlaying
#if UNITY_EDITOR
                && mainCamera.name != GPUInstancerLoopScheduler.sceneViewCameraName
#endif
                )
            {
                HiZDepthHistory.Request(mainCamera);
                hasOcclusionDepth = HiZDepthHistory.TryGetTexture(mainCamera, out hiZDepthTexture, out hiZTextureSize);
            }
#endif

            mvpMatrix = (hasOcclusionDepth && GPUInstancerProjectSettings.IsVREnabled() ? mainCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) : mainCamera.projectionMatrix) * mainCamera.worldToCameraMatrix;

            if (hasOcclusionDepth && GPUInstancerConstants.projectSettings.IsUseBothEyesVRCulling())
            {
                mvpMatrix2 = mainCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) * mainCamera.worldToCameraMatrix;
            }

            cameraPosition = mainCamera.transform.position;
        }

        public void CalculateHalfAngle()
        {
            if (mainCamera != null)
                halfAngle = Mathf.Tan(Mathf.Deg2Rad * mainCamera.fieldOfView * 0.25f);
        }

        public Camera GetRenderingCamera()
        {
            if (renderOnlySelectedCamera
#if UNITY_EDITOR
                || UnityEditor.EditorApplication.isPaused
#endif
                )
                return mainCamera;
            return null;
        }
    }
}
