#if VISUAL_EFFECT_GRAPH_0_0_1_OR_NEWER
using UnityEngine.Rendering;
using FraxinusEngine.Rendering.Ultra;

namespace UnityEngine.VFX.Utility
{
    /// <summary>
    /// UltraRP Camera parameter binding helper class for VFX Graph.
    /// </summary>
    [VFXBinder("UltraRP/UltraRP Camera")]
    public class UltraRPCameraBinder : VFXBinderBase
    {
        /// <summary>
        /// Camera UltraRP additional data.
        /// </summary>
        public UltraAdditionalCameraData AdditionalData;
        Camera _camera;

        [VFXPropertyBinding("UnityEditor.VFX.CameraType"), SerializeField]
        ExposedProperty CameraProperty = "Camera";

        ExposedProperty _position;
        ExposedProperty _angles;
        ExposedProperty _scale;
        ExposedProperty _fieldOfView;
        ExposedProperty _nearPlane;
        ExposedProperty _farPlane;
        ExposedProperty _aspectRatio;
        ExposedProperty _dimensions;
        ExposedProperty _scaledDimensions;
        ExposedProperty _depthBuffer;
        ExposedProperty _colorBuffer;
        ExposedProperty _orthographic;
        ExposedProperty _orthographicSize;
        ExposedProperty _lensShift;

        /// <summary>
        /// Set a camera property.
        /// </summary>
        /// <param name="name">Property name.</param>
        public void SetCameraProperty(string name)
        {
            CameraProperty = name;
            UpdateSubProperties();
        }

        void UpdateSubProperties()
        {
            // Get Camera component from UltraRP additional data
            if (AdditionalData != null)
            {
                _camera = AdditionalData.GetComponent<Camera>();
            }

            // Update VFX Sub Properties
            _position = CameraProperty + "_transform_position";
            _angles = CameraProperty + "_transform_angles";
            _scale = CameraProperty + "_transform_scale";
            _orthographic = CameraProperty + "_orthographic";
            _fieldOfView = CameraProperty + "_fieldOfView";
            _nearPlane = CameraProperty + "_nearPlane";
            _farPlane = CameraProperty + "_farPlane";
            _orthographicSize = CameraProperty + "_orthographicSize";
            _aspectRatio = CameraProperty + "_aspectRatio";
            _dimensions = CameraProperty + "_pixelDimensions";
            _lensShift = CameraProperty + "_lensShift";
            _depthBuffer = CameraProperty + "_depthBuffer";
            _colorBuffer = CameraProperty + "_colorBuffer";
            _scaledDimensions = CameraProperty + "_scaledPixelDimensions";
        }

        static void RequestHistoryAccess(IPerFrameHistoryAccessTracker access)
        {
            access?.RequestAccess<RawColorHistory>();
            access?.RequestAccess<RawDepthHistory>();
        }

        /// <summary>
        /// OnEnable implementation.
        /// </summary>
        protected override void OnEnable()
        {
            base.OnEnable();

            if (AdditionalData != null)
                AdditionalData.history.OnGatherHistoryRequests += RequestHistoryAccess;

            UpdateSubProperties();
        }

        /// <summary>
        /// OnDisable implementation.
        /// </summary>
        protected override void OnDisable()
        {
            base.OnDisable();

            if (AdditionalData != null)
                AdditionalData.history.OnGatherHistoryRequests -= RequestHistoryAccess;
        }

        private void OnValidate()
        {
            UpdateSubProperties();
            if (AdditionalData != null)
                AdditionalData.history.OnGatherHistoryRequests += RequestHistoryAccess;
        }

        /// <summary>
        /// Returns true if the Visual Effect and the configuration of the binder are valid to perform the binding.
        /// </summary>
        /// <param name="component">Component to be tested.</param>
        /// <returns>True if the Visual Effect and the configuration of the binder are valid to perform the binding.</returns>
        public override bool IsValid(VisualEffect component)
        {
            return AdditionalData != null
                && _camera != null
                && component.HasVector3(_position)
                && component.HasVector3(_angles)
                && component.HasVector3(_scale)
                && component.HasBool(_orthographic)
                && component.HasFloat(_fieldOfView)
                && component.HasFloat(_nearPlane)
                && component.HasFloat(_farPlane)
                && component.HasFloat(_orthographicSize)
                && component.HasFloat(_aspectRatio)
                && component.HasVector2(_dimensions)
                && component.HasVector2(_lensShift)
                && component.HasTexture(_depthBuffer)
                && component.HasTexture(_colorBuffer)
                && component.HasVector2(_scaledDimensions);
        }

        /// <summary>
        /// Update bindings for a visual effect.
        /// </summary>
        /// <param name="component">Component to update.</param>
        public override void UpdateBinding(VisualEffect component)
        {
            var asset = UltraRenderPipeline.asset;
            if (AdditionalData == null || asset == null)
                return;

            var targetSpace = component.visualEffectAsset.GetExposedSpace(_position);
            Matrix4x4 readTransform;
            if (targetSpace == VFXSpace.Local)
            {
                readTransform = component.transform.worldToLocalMatrix * AdditionalData.transform.localToWorldMatrix;
            }
            else
            {
                readTransform = AdditionalData.transform.localToWorldMatrix;
            }

            component.SetVector3(_position, readTransform.GetPosition());
            component.SetVector3(_angles, readTransform.rotation.eulerAngles);
            component.SetVector3(_scale, readTransform.lossyScale);

            component.SetBool(_orthographic, _camera.orthographic);
            component.SetFloat(_orthographicSize, _camera.orthographicSize);
            // While field of View is set in degrees for the camera, it is expected in radians in VFX
            component.SetFloat(_fieldOfView, Mathf.Deg2Rad * _camera.fieldOfView);
            component.SetFloat(_nearPlane, _camera.nearClipPlane);
            component.SetFloat(_farPlane, _camera.farClipPlane);
            component.SetVector2(_lensShift, _camera.lensShift);
            component.SetFloat(_aspectRatio, _camera.aspect);

            var cameraSize = new Vector2(_camera.pixelWidth, _camera.pixelHeight);
            component.SetVector2(_dimensions, cameraSize);

            var scaledSize = new Vector2(_camera.scaledPixelWidth, _camera.scaledPixelHeight) * asset.renderScale;
            component.SetVector2(_scaledDimensions, scaledSize);

            var depth = AdditionalData.history.GetHistoryForRead<RawDepthHistory>()?.GetCurrentTexture();
            var color = AdditionalData.history.GetHistoryForRead<RawColorHistory>()?.GetCurrentTexture();

            if (depth != null)
                component.SetTexture(_depthBuffer, depth);

            if (color != null)
                component.SetTexture(_colorBuffer, color);
        }

        /// <summary>
        /// To string implementation.
        /// </summary>
        /// <returns>String containing the binder information.</returns>
        public override string ToString()
        {
            return string.Format($"UltraRP Camera : '{(AdditionalData == null ? "null" : AdditionalData.gameObject.name)}' -> {CameraProperty}");
        }
    }
}

#endif
