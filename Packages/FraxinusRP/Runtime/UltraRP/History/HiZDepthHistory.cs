using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Camera-owned hierarchical depth for consumers that run before camera rendering.
    /// Depth is reversed (0 = far, 1 = near) and every mip stores the minimum depth
    /// of its covered pixels. Request each frame before the camera is rendered.
    /// </summary>
    public sealed class HiZDepthHistory : CameraHistoryItem
    {
        // Device capabilities stay fixed for the lifetime of the graphics device.
        static readonly bool s_hardwareSupported = SystemInfo.supportsComputeShaders &&
            SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.LoadStore) &&
            SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.Sample);

        int _id;
        int _lastRequestedFrame = -1;
        int _lastGeneratedFrame = -1;
        int _width, _height;
        Rect _sourcePixelRect;
        RenderTexture _sourceTarget;
        UltraRenderPipelineAsset _sourcePipeline;
        RenderPipeline _sourcePipelineInstance;
        ScriptableRenderer _sourceRenderer;
        float _sourceRenderScale;

        /// <summary>
        /// Request a pyramid for this camera's next render in the current frame.
        /// This does not allocate textures. Call before begin-camera culling.
        /// </summary>
        public static void Request(Camera camera)
        {
            if (!IsCameraSupported(camera))
                return;
            var additional = camera.GetUltraAdditionalCameraData();
            if (additional.renderType != CameraRenderType.Base || additional.history == null)
                return;

            additional.history.RequestAccess<HiZDepthHistory>();
            // Establish the per-camera request marker without allocating GPU memory.
            // GetHistoryForWrite marks framework access, not valid pyramid contents;
            // validity is tracked separately and set only by the producing pass.
            var history = additional.history.GetHistoryForWrite<HiZDepthHistory>();
            history._lastRequestedFrame = Time.frameCount;
        }

        /// <summary>
        /// Get the last produced pyramid, valid for the current or previous frame.
        /// Returns false for the first frame, stale history, unsupported cameras,
        /// changed camera targets/layouts, or a disabled/unbound producing pass.
        /// The returned texture is owned by FraxinusRP; consumers must not release it.
        /// </summary>
        public static bool TryGetTexture(Camera camera, out RenderTexture texture, out Vector2 size)
        {
            texture = null;
            size = Vector2.zero;
            if (!IsCameraSupported(camera) || !camera.TryGetComponent(out UltraAdditionalCameraData additional) ||
                additional.renderType != CameraRenderType.Base || additional.history == null)
                return false;

            var history = additional.history.GetHistoryForRead<HiZDepthHistory>();
            if (history == null || !history.IsValid(camera))
                return false;

            var renderer = additional.scriptableRenderer;
            if (!ReferenceEquals(renderer, history._sourceRenderer) || !HiZDepthPass.HasActiveMainInjection(renderer))
            {
                history.Invalidate();
                return false;
            }

            texture = history.GetTexture().rt;
            size = new Vector2(texture.width, texture.height);
            return true;
        }

        internal static bool IsCameraSupported(Camera camera)
        {
            if (!s_hardwareSupported || camera == null ||
                (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView) ||
                camera.stereoEnabled || camera.allowDynamicResolution || camera.pixelWidth <= 0 || camera.pixelHeight <= 0)
                return false;

            var target = camera.targetTexture;
            return target == null || (target.dimension == TextureDimension.Tex2D && !target.useDynamicScale &&
                target.graphicsFormat != GraphicsFormat.None);
        }

        bool IsValid(Camera camera)
        {
            var texture = GetTexture()?.rt;
            int age = Time.frameCount - _lastGeneratedFrame;
            return _lastGeneratedFrame >= 0 && age >= 0 && age <= 1 && texture != null && texture.IsCreated() &&
                texture.width == _width && texture.height == _height && camera.pixelRect == _sourcePixelRect &&
                camera.targetTexture == _sourceTarget && _sourcePipeline != null &&
                GraphicsSettings.currentRenderPipeline == _sourcePipeline &&
                ReferenceEquals(RenderPipelineManager.currentPipeline, _sourcePipelineInstance) &&
                Mathf.Approximately(_sourcePipeline.renderScale, _sourceRenderScale);
        }

        public override void OnCreate(BufferedRTHandleSystem owner, uint typeId)
        {
            base.OnCreate(owner, typeId);
            _id = MakeId(0);
        }

        internal bool requestedThisFrame => _lastRequestedFrame == Time.frameCount;
        internal RTHandle GetTexture() => storage?.GetFrameRT(_id, 0);
        internal void Invalidate() => _lastGeneratedFrame = -1;

        internal RTHandle Update(int width, int height)
        {
            var handle = GetTexture();
            if (handle?.rt == null || !handle.rt.IsCreated() || _width != width || _height != height)
            {
                // Preserve the current request when only the texture layout changes.
                storage.ReleaseBuffer(_id);
                Invalidate();
                var descriptor = new RenderTextureDescriptor(width, height)
                {
                    graphicsFormat = GraphicsFormat.R32_SFloat,
                    depthStencilFormat = GraphicsFormat.None,
                    msaaSamples = 1,
                    volumeDepth = 1,
                    dimension = TextureDimension.Tex2D,
                    enableRandomWrite = true,
                    useMipMap = true,
                    autoGenerateMips = false,
                    useDynamicScale = false
                };
                // Consumers submit their previous-depth work before this frame's
                // producer on the graphics queue, so one persistent buffer suffices.
                storage.AllocBuffer(_id, 1, ref descriptor, FilterMode.Point, TextureWrapMode.Clamp,
                    name: "Fraxinus Hi-Z Depth History");
                _width = width;
                _height = height;
                handle = GetTexture();
            }
            return handle;
        }

        internal void MarkGenerated(Camera camera, ScriptableRenderer renderer)
        {
            _sourcePixelRect = camera.pixelRect;
            _sourceTarget = camera.targetTexture;
            _sourcePipeline = GraphicsSettings.currentRenderPipeline as UltraRenderPipelineAsset;
            _sourcePipelineInstance = RenderPipelineManager.currentPipeline;
            _sourceRenderer = renderer;
            _sourceRenderScale = _sourcePipeline != null ? _sourcePipeline.renderScale : 1f;
            _lastGeneratedFrame = Time.frameCount;
        }

        public override void Reset()
        {
            storage?.ReleaseBuffer(_id);
            _lastRequestedFrame = -1;
            Invalidate();
            _width = _height = 0;
            _sourcePixelRect = default;
            _sourceTarget = null;
            _sourcePipeline = null;
            _sourcePipelineInstance = null;
            _sourceRenderer = null;
            _sourceRenderScale = 0f;
        }
    }
}
