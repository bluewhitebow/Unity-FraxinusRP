using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    public class CameraData : ContextItem
    {
        public Camera camera;
        
        internal bool isSubContext;
        public int scaledWidth => isSubContext ? cameraTargetDescriptor.width : Mathf.Max(1, (int)(camera.pixelWidth * renderScale));
        public int scaledHeight => isSubContext ? cameraTargetDescriptor.height : Mathf.Max(1, (int)(camera.pixelHeight * renderScale));

        UltraCameraHistory _historyManager;
        public UltraCameraHistory historyManager { get => _historyManager; set => _historyManager = value; }


        public CameraRenderType renderType;

        /// <summary>
        /// Controls the final target texture for a camera. If null camera will resolve rendering to screen.
        /// </summary>
        public RenderTexture targetTexture;

        /// <summary>
        /// Render texture settings used to create intermediate camera textures for rendering.
        /// </summary>
        public RenderTextureDescriptor cameraTargetDescriptor;
        internal Rect pixelRect;
        internal bool useScreenCoordOverride;
        internal Vector4 screenSizeOverride;
        internal Vector4 screenCoordScaleBias;
        internal int pixelWidth;
        internal int pixelHeight;
        internal float aspectRatio;

        /// <summary>
        /// Render scale to apply when creating camera textures. Scaled extents are rounded down to integers.
        /// </summary>
        public float renderScale;
        internal ImageScalingMode imageScalingMode;
        internal ImageUpscalingFilter upscalingFilter;
        internal bool fsrOverrideSharpness;
        internal float fsrSharpness;
        internal HDRColorBufferPrecision hdrColorBufferPrecision;

        public bool clearDepth;
        public CameraType cameraType;
        public bool isDefaultViewport;
        public bool isHdrEnabled;
        public bool allowHDROutput;
        public bool isAlphaOutputEnabled;

        public SortingCriteria defaultOpaqueSortFlags;

        public bool requiresDepthTexture;
        public bool requiresOpaqueTexture;

        internal bool useGPUOcclusionCulling;

        public float maxShadowDistance;
        public bool postProcessEnabled;
        internal bool stackAnyPostProcessingEnabled;
        public IEnumerator<Action<RenderTargetIdentifier, CommandBuffer>> captureActions;
        public LayerMask volumeLayerMask;
        public Transform volumeTrigger;
        public bool isStopNaNEnabled;
        public bool isDitheringEnabled;
        public AntialiasingMode antialiasing;
        public AntialiasingQuality antialiasingQuality;

        public bool resolveFinalTarget;

        public Vector3 worldSpaceCameraPos;
        public Color backgroundColor;

        internal TaaHistory taaHistory;
        internal StpHistory stpHistory;
        internal Sgsr2History sgsr2History;
        internal bool sgsr2Enabled;

        internal TAASettings taaSettings;
        
        public ScriptableRenderer renderer;

        Matrix4x4 _viewMatrix;
        Matrix4x4 _projectionMatrix;
        Matrix4x4 _jitterMatrix;

        // PixelCoord to ViewDirWS. For Sky or compute shaders.
        Matrix4x4 _pixelCoordToViewDirWS;

        internal bool requireSrgbConversion
        {
            get
            {
                return targetTexture == null && Display.main.requiresSrgbBlitToBackbuffer;
            }
        }

        internal bool resetHistory
        {
            get => taaSettings.resetHistoryFrames != 0;
        }

        public bool isGameCamera => cameraType == CameraType.Game;

        public bool isSceneViewCamera => cameraType == CameraType.SceneView;

        public bool isPreviewCamera => cameraType == CameraType.Preview;

        internal bool isRenderPassSupportedCamera => (cameraType == CameraType.Game || cameraType == CameraType.Reflection);

        internal bool resolveToScreen => targetTexture == null && resolveFinalTarget && (cameraType == CameraType.Game || camera.cameraType == CameraType.VR);

        public bool isHDROutputActive
        {
            get
            {
                bool hdrDisplayOutputActive = UltraRenderPipeline.HDROutputForMainDisplayIsActive();
                return hdrDisplayOutputActive && allowHDROutput && resolveToScreen;
            }
        }

        internal bool stackLastCameraOutputToHDR;

        public HDROutputUtils.HDRDisplayInformation hdrDisplayInformation
        {
            get
            {
                HDROutputUtils.HDRDisplayInformation displayInformation;
                HDROutputSettings displaySettings = HDROutputSettings.main;
                displayInformation = new HDROutputUtils.HDRDisplayInformation(displaySettings.maxFullFrameToneMapLuminance,
                    displaySettings.maxToneMapLuminance,
                    displaySettings.minToneMapLuminance,
                    displaySettings.paperWhiteNits);
    

                return displayInformation;
            }
        }

        public ColorGamut hdrDisplayColorGamut
        {
            get
            {
                HDROutputSettings displaySettings = HDROutputSettings.main;
                return displaySettings.displayColorGamut;
            }
        }

        /// <summary>
        /// True if the Camera should render overlay UI.
        /// </summary>
        public bool rendersOverlayUI => SupportedRenderingFeatures.active.rendersUIOverlay && resolveToScreen;


        internal void SetViewAndProjectionMatrix(Matrix4x4 viewMatrix, Matrix4x4 projectionMatrix)
        {
            _viewMatrix = viewMatrix;
            _projectionMatrix = projectionMatrix;
            _jitterMatrix = Matrix4x4.identity;
        }

        internal void SetViewProjectionAndJitterMatrix(Matrix4x4 viewMatrix, Matrix4x4 projectionMatrix, Matrix4x4 jitterMatrix)
        {
            _viewMatrix = viewMatrix;
            _projectionMatrix = projectionMatrix;
            _jitterMatrix = jitterMatrix;
        }

        internal void SetPixelCoordToViewDirWSMatrix(Matrix4x4 pCoordToViewDirWSMatrix)
        {
            _pixelCoordToViewDirWS = pCoordToViewDirWSMatrix;
        }

        public Matrix4x4 GetViewMatrix(int viewIndex = 0)
        {
            return _viewMatrix;
        }

        public Matrix4x4 GetProjectionMatrix(int viewIndex = 0)
        {
            return _jitterMatrix * _projectionMatrix;
        }


        internal Matrix4x4 GetProjectionMatrixNoJitter(int viewIndex = 0)
        {
            return _projectionMatrix;
        }

        public Matrix4x4 GetGPUProjectionMatrix(int viewIndex = 0)
        {
            // Disable obsolete warning for internal usage
            #pragma warning disable CS0618
            // GetGPUProjectionMatrix takes a projection matrix and returns a GfxAPI adjusted version, does not set or get any state.
            return _jitterMatrix * GL.GetGPUProjectionMatrix(GetProjectionMatrixNoJitter(viewIndex), IsCameraProjectionMatrixFlipped());
            #pragma warning restore CS0618
        }

        public Matrix4x4 GetGPUProjectionMatrixNoJitter(int viewIndex = 0)
        {
            // Disable obsolete warning for internal usage
            #pragma warning disable CS0618
            // GetGPUProjectionMatrix takes a projection matrix and returns a GfxAPI adjusted version, does not set or get any state.
            return GL.GetGPUProjectionMatrix(GetProjectionMatrixNoJitter(viewIndex), IsCameraProjectionMatrixFlipped());
            #pragma warning restore CS0618
        }

        internal Matrix4x4 GetGPUProjectionMatrix(bool renderIntoTexture, int viewIndex = 0)
        {
            return _jitterMatrix * GL.GetGPUProjectionMatrix(GetProjectionMatrixNoJitter(viewIndex), renderIntoTexture);
        }

        /// <summary>
        /// Utility matrix (used by sky) to map screen position to WS view direction.
        /// </summary>
        /// <returns></returns>
        internal Matrix4x4 GetPixelCoordToViewDirWSMatrix()
        {
            return _pixelCoordToViewDirWS;
        }

        public bool IsRenderTargetProjectionMatrixFlipped(RTHandle color, RTHandle depth = null)
        {
            if (!SystemInfo.graphicsUVStartsAtTop)
                return true;

            return targetTexture != null || IsHandleYFlipped(color ?? depth);
        }


        public bool IsHandleYFlipped(RTHandle handle)
        {
            return IsHandleYFlipped(handle, cameraType);
        }

        internal static bool IsHandleYFlipped(RTHandle handle, CameraType cameraType)
        {
            if (!SystemInfo.graphicsUVStartsAtTop)
                return true;

            if (cameraType == CameraType.SceneView || cameraType == CameraType.Preview)
                return true;

            var handleID = new RenderTargetIdentifier(handle.nameID, 0, CubemapFace.Unknown, 0);
            bool isBackbuffer = handleID == BuiltinRenderTextureType.CameraTarget || handleID == BuiltinRenderTextureType.Depth;
            return !isBackbuffer;
        }

        /// <summary>
        /// True if the camera device projection matrix is flipped. This happens when the pipeline is rendering
        /// to a render texture in non OpenGL platforms. If you are doing a custom Blit pass to copy camera textures
        /// (_CameraColorTexture, _CameraDepthAttachment) you need to check this flag to know if you should flip the
        /// matrix when rendering with for cmd.Draw* and reading from camera textures.
        /// </summary>
        /// <returns> True if the camera device projection matrix is flipped. </returns>
        public bool IsCameraProjectionMatrixFlipped()
        {
            if (!SystemInfo.graphicsUVStartsAtTop)
                return false;

            // Users only have access to CameraData on UltraRP rendering scope. The current renderer should never be null.
            var renderer = ScriptableRenderer.current;
            Debug.Assert(renderer != null, "IsCameraProjectionMatrixFlipped is being called outside camera rendering scope.");

            // Disable obsolete warning for internal usage
            #pragma warning disable CS0618
            // todo: 过后补上
            //if (renderer != null)
            //    return IsHandleYFlipped(renderer.cameraColorTargetHandle) || targetTexture != null;
            #pragma warning restore CS0618

            return true;
        }

        internal bool IsTemporalAARequested()
        {
            return antialiasing == AntialiasingMode.TemporalAntiAliasing;
        }


        internal bool IsTemporalAAEnabled()
        {
            var debugSettings = UltraRenderPipelineDebugDisplaySettings.Instance;
            if (debugSettings.AreAnySettingsActive && !isPreviewCamera && !debugSettings.IsPostProcessingAllowed)
                return false;

            UltraAdditionalCameraData additionalCameraData;
            camera.TryGetComponent(out additionalCameraData);

            return IsTemporalAARequested() && !IsSGSR2Requested()                                                                     // SGSR2 owns its temporal resolve
                   && postProcessEnabled                                                                                              // Postprocessing Enabled
                   && (taaHistory != null)                                                                                            // Initialized
                   && (cameraTargetDescriptor.msaaSamples == 1)                                                                       // No MSAA
                   && !(additionalCameraData?.renderType == CameraRenderType.Overlay || additionalCameraData?.cameraStack.Count > 0)  // No Camera stack
                   && !camera.allowDynamicResolution                                                                                  // No Dynamic Resolution
                   && !isSubContext                                                                                                   // History belongs to the main camera
                   && !UltraRenderer.IsOffscreenDepthTexture(this)                                                                     // Depth-only targets do not accumulate color
                   && renderer is UltraRenderer ultraRenderer && ultraRenderer.SupportsTemporalAA();                                 // Renderer opt-in, before jitter/history setup
        }

        internal bool IsSTPRequested()
        {
            return (imageScalingMode == ImageScalingMode.Upscaling) && (upscalingFilter == ImageUpscalingFilter.STP);
        }

        internal bool IsSTPEnabled()
        {
            return IsSTPRequested() && IsTemporalAAEnabled();
        }

        internal bool IsSGSR2Requested()
        {
            return imageScalingMode == ImageScalingMode.Upscaling && upscalingFilter == ImageUpscalingFilter.SGSR2;
        }

        internal bool IsSGSR2Enabled()
        {
            return sgsr2Enabled && IsSGSR2Requested() && sgsr2History != null && postProcessEnabled && !isSubContext;
        }

        // Resolve once during camera initialization, before projection jitter is applied.
        internal bool CanEnableSGSR2()
        {
            if (!IsSGSR2Requested() || sgsr2History == null || !postProcessEnabled || isSubContext ||
                pixelWidth <= 0 || pixelHeight <= 0 || cameraTargetDescriptor.width <= 0 || cameraTargetDescriptor.height <= 0 ||
                renderType != CameraRenderType.Base || !resolveFinalTarget ||
                cameraTargetDescriptor.msaaSamples != 1 || camera.allowDynamicResolution || camera.stereoEnabled ||
                cameraTargetDescriptor.dimension != TextureDimension.Tex2D ||
                UltraRenderer.IsOffscreenDepthTexture(this))
                return false;

            if (!(renderer is UltraRenderer ultraRenderer) || !ultraRenderer.SupportsTemporalUpscaling())
                return false;

            if (camera.TryGetComponent<UltraAdditionalCameraData>(out var additionalCameraData) &&
                (additionalCameraData.renderType == CameraRenderType.Overlay || additionalCameraData.cameraStack.Count > 0))
                return false;

            var debugSettings = UltraRenderPipelineDebugDisplaySettings.Instance;
            return !debugSettings.AreAnySettingsActive || isPreviewCamera || debugSettings.IsPostProcessingAllowed;
        }

        internal bool IsTemporalJitterEnabled()
        {
            return IsSGSR2Enabled() || IsTemporalAAEnabled();
        }

        // Camera at the top of the overlay camera stack
        public Camera baseCamera;

        internal bool isLastBaseCamera;

        public override void Reset()
        {
            isSubContext = false;
            _viewMatrix = default;
            _projectionMatrix = default;
            _jitterMatrix = default;

            camera = null;
            renderType = CameraRenderType.Base;
            targetTexture = null;
            cameraTargetDescriptor = default;
            pixelRect = default;
            useScreenCoordOverride = false;
            screenSizeOverride = default;
            screenCoordScaleBias = default;
            pixelWidth = 0;
            pixelHeight = 0;
            aspectRatio = 0.0f;
            renderScale = 1.0f;
            imageScalingMode = ImageScalingMode.None;
            upscalingFilter = ImageUpscalingFilter.Point;
            fsrOverrideSharpness = false;
            fsrSharpness = 0.0f;
            cameraType = CameraType.Game;
            isDefaultViewport = false;
            isHdrEnabled = false;
            allowHDROutput = false;
            isAlphaOutputEnabled = false;
            requiresDepthTexture = false;
            requiresOpaqueTexture = false;
            useGPUOcclusionCulling = false;
            maxShadowDistance = 0.0f;
            postProcessEnabled = false;
            captureActions = default;
            volumeLayerMask = 0;
            volumeTrigger = default;
            isStopNaNEnabled = false;
            isDitheringEnabled = false;
            antialiasing = AntialiasingMode.None;
            antialiasingQuality = AntialiasingQuality.Low;
            renderer = null;
            resolveFinalTarget = false;
            worldSpaceCameraPos = default;
            backgroundColor = Color.black;
            taaHistory = null;
            stpHistory = null;
            sgsr2History = null;
            sgsr2Enabled = false;
            taaSettings = default;
            baseCamera = null;
            isLastBaseCamera = false;

            stackLastCameraOutputToHDR = false;
        }
    }
}
