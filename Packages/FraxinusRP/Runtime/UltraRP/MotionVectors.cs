using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    // Motion vector data that persists over frames. (per camera)
    internal sealed class MotionVectorsPersistentData
    {
        #region Fields

        private const int k_eyeCount = 2;

        readonly Matrix4x4[] _projection = new Matrix4x4[k_eyeCount];
        readonly Matrix4x4[] _view = new Matrix4x4[k_eyeCount];
        readonly Matrix4x4[] _viewProjection = new Matrix4x4[k_eyeCount];

        readonly Matrix4x4[] _previousProjection = new Matrix4x4[k_eyeCount];
        readonly Matrix4x4[] _previousView = new Matrix4x4[k_eyeCount];
        readonly Matrix4x4[] _previousViewProjection = new Matrix4x4[k_eyeCount];

        readonly Matrix4x4[] _previousPreviousProjection = new Matrix4x4[k_eyeCount];
        readonly Matrix4x4[] _previousPreviousView = new Matrix4x4[k_eyeCount];

        readonly int[] _lastFrameIndex = new int[k_eyeCount];
        readonly float[] _prevAspectRatio = new float[k_eyeCount];

        float _deltaTime;
        float _lastDeltaTime;

        Vector3 _worldSpaceCameraPos;
        Vector3 _previousWorldSpaceCameraPos;
        Vector3 _previousPreviousWorldSpaceCameraPos;

        #endregion

        #region Constructors

        internal MotionVectorsPersistentData()
        {
            Reset();
        }

        #endregion

        #region Properties

        internal int lastFrameIndex
        {
            get => _lastFrameIndex[0];
        }

        internal Matrix4x4 viewProjection
        {
            get => _viewProjection[0];
        }

        internal Matrix4x4 previousViewProjection
        {
            get => _previousViewProjection[0];
        }

        internal Matrix4x4[] viewProjectionStereo
        {
            get => _viewProjection;
        }

        internal Matrix4x4[] previousViewProjectionStereo
        {
            get => _previousViewProjection;
        }

        internal Matrix4x4[] projectionStereo
        {
            get => _projection;
        }

        internal Matrix4x4[] previousProjectionStereo
        {
            get => _previousProjection;
        }

        internal Matrix4x4[] previousPreviousProjectionStereo
        {
            get => _previousPreviousProjection;
        }

        internal Matrix4x4[] viewStereo
        {
            get => _view;
        }

        internal Matrix4x4[] previousViewStereo
        {
            get => _previousView;
        }

        internal Matrix4x4[] previousPreviousViewStereo
        {
            get => _previousPreviousView;
        }

        internal float deltaTime
        {
            get => _deltaTime;
        }

        internal float lastDeltaTime
        {
            get => _lastDeltaTime;
        }

        internal Vector3 worldSpaceCameraPos
        {
            get => _worldSpaceCameraPos;
        }

        internal Vector3 previousWorldSpaceCameraPos
        {
            get => _previousWorldSpaceCameraPos;
        }

        internal Vector3 previousPreviousWorldSpaceCameraPos
        {
            get => _previousPreviousWorldSpaceCameraPos;
        }
        #endregion

        public void Reset()
        {
            for (int i = 0; i < k_eyeCount; i++)
            {
                _projection[i] = Matrix4x4.identity;
                _view[i] = Matrix4x4.identity;
                _viewProjection[i] = Matrix4x4.identity;

                _previousPreviousProjection[i] = Matrix4x4.identity;
                _previousPreviousView[i] = Matrix4x4.identity;

                _previousProjection[i] = Matrix4x4.identity;
                _previousView[i] = Matrix4x4.identity;
                _previousViewProjection[i] = Matrix4x4.identity;

                _lastFrameIndex[i] = -1;
                _prevAspectRatio[i] = -1;
            }

            _deltaTime = 0.0f;
            _lastDeltaTime = 0.0f;

            _worldSpaceCameraPos = Vector3.zero;
            _previousWorldSpaceCameraPos = Vector3.zero;
            _previousPreviousWorldSpaceCameraPos = Vector3.zero;
        }

        static private int GetXRMultiPassId(XRPass xr)
        {
#if FRAXINUSRP_ENABLE_XR && ENABLE_VR && ENABLE_XR_MODULE
            return xr.enabled ? xr.multipassId : 0;
#else
            return 0;
#endif
        }

        public void Update(CameraData cameraData)
        {
            // TODO: ingonre

            //int eyeIndex = GetXRMultiPassId(cameraData.xr);
            // XR multipass renders the frame twice, avoid updating camera history twice.
            //bool xrMultipassEnabled = cameraData.xr.enabled && !cameraData.xr.singlePassEnabled;

            int eyeIndex = 0;
            bool xrMultipassEnabled = false;

            bool isNewFrame = !xrMultipassEnabled || (eyeIndex == 0);

            int frameIndex = Time.frameCount;

            // Update per-frame data once regardless of how many eyes are being rendered
            if (isNewFrame && _lastFrameIndex[0] != frameIndex)
            {
                // For per-frame data, we only care if this is the first frame for eye zero because eye zero
                // is always updated once per frame regardless of how XR is configured.
                bool isPreviousFrameDataInvalid = (_lastFrameIndex[0] == -1);

                float deltaTime = Time.deltaTime;
                Vector3 worldSpaceCameraPos = cameraData.camera.transform.position;

                // Use the current delta time if the previous time is invalid
                if (isPreviousFrameDataInvalid)
                {
                    _lastDeltaTime = deltaTime;
                    _deltaTime = deltaTime;

                    _previousPreviousWorldSpaceCameraPos = worldSpaceCameraPos;
                    _previousWorldSpaceCameraPos = worldSpaceCameraPos;
                    _worldSpaceCameraPos = worldSpaceCameraPos;
                }

                _lastDeltaTime = _deltaTime;
                _deltaTime = deltaTime;

                _previousPreviousWorldSpaceCameraPos = _previousWorldSpaceCameraPos;
                _previousWorldSpaceCameraPos = _worldSpaceCameraPos;
                _worldSpaceCameraPos = worldSpaceCameraPos;
            }

            // A camera could be rendered multiple times per frame, only update the view projections if needed
            bool aspectChanged = _prevAspectRatio[eyeIndex] != cameraData.aspectRatio;
            if (_lastFrameIndex[eyeIndex] != frameIndex || aspectChanged)
            {
                bool isPreviousFrameDataInvalid = (_lastFrameIndex[eyeIndex] == -1) || aspectChanged;

                // TODO: ingonre
                // int numActiveViews = cameraData.xr.enabled ? cameraData.xr.viewCount : 1;
                int numActiveViews = 1;

                // Make sure we don't try to handle more views than we expect to support
                Debug.Assert(numActiveViews <= k_eyeCount);

                for (int viewIndex = 0; viewIndex < numActiveViews; ++viewIndex)
                {
                    int targetIndex = viewIndex + eyeIndex;

                    var gpuP = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrixNoJitter(viewIndex), true);

                    var gpuV = cameraData.GetViewMatrix(viewIndex);

                    var gpuVP = gpuP * gpuV;

                    // If the data for the previous frame is invalid, we need to set all previous frame data
                    // to the current frame data to avoid generating invalid motion vectors
                    if (isPreviousFrameDataInvalid)
                    {
                        _previousPreviousProjection[targetIndex] = gpuP;
                        _previousProjection[targetIndex] = gpuP;
                        _projection[targetIndex] = gpuP;

                        _previousPreviousView[targetIndex] = gpuV;
                        _previousView[targetIndex] = gpuV;
                        _view[targetIndex] = gpuV;

                        _viewProjection[targetIndex] = gpuVP;
                        _previousViewProjection[targetIndex] = gpuVP;
                    }

                    // Shift all matrices to the next position
                    _previousPreviousProjection[targetIndex] = _previousProjection[targetIndex];
                    _previousProjection[targetIndex] = _projection[targetIndex];
                    _projection[targetIndex] = gpuP;

                    _previousPreviousView[targetIndex] = _previousView[targetIndex];
                    _previousView[targetIndex] = _view[targetIndex];
                    _view[targetIndex] = gpuV;

                    _previousViewProjection[targetIndex] = _viewProjection[targetIndex];
                    _viewProjection[targetIndex] = gpuVP;
                }

                _lastFrameIndex[eyeIndex] = frameIndex;
                _prevAspectRatio[eyeIndex] = cameraData.aspectRatio;
            }
        }

        // Set global motion vector matrix GPU constants.
        public void SetGlobalMotionMatrices(RasterCommandBuffer cmd, XRPass xr)
        {
            var passID = GetXRMultiPassId(xr);
#if FRAXINUSRP_ENABLE_XR && ENABLE_VR && ENABLE_XR_MODULE
            if (xr.enabled && xr.singlePassEnabled)
            {
                cmd.SetGlobalMatrixArray(ShaderPropertyId.previousViewProjectionNoJitterStereo, previousViewProjectionStereo);
                cmd.SetGlobalMatrixArray(ShaderPropertyId.viewProjectionNoJitterStereo, viewProjectionStereo);
            }
            else
#endif
            {
                cmd.SetGlobalMatrix(ShaderPropertyId.previousViewProjectionNoJitter, previousViewProjectionStereo[passID]);
                cmd.SetGlobalMatrix(ShaderPropertyId.viewProjectionNoJitter, viewProjectionStereo[passID]);
            }
        }
    }
}
