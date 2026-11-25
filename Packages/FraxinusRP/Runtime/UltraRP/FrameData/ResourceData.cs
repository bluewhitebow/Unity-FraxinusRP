using System;
using UnityEngine;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class ResourceData : ResourceBase
    {
        internal ActiveID activeColorID { get; set; }
        internal bool decalLayers;

        public TextureHandle activeColorTexture
        {
            get
            {
                if (!CheckAndWarnAboutAccessibility())
                    return TextureHandle.nullHandle;

                switch (activeColorID)
                {
                    case ActiveID.Camera:
                        return cameraColor;
                    case ActiveID.BackBuffer:
                        return backBufferColor;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        internal ActiveID activeDepthID { get; set; }

        public TextureHandle activeDepthTexture
        {
            get
            {
                if (!CheckAndWarnAboutAccessibility())
                    return TextureHandle.nullHandle;

                switch (activeDepthID)
                {
                    case ActiveID.Camera:
                        return cameraDepth;
                    case ActiveID.BackBuffer:
                        return backBufferDepth;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        public bool isActiveTargetBackBuffer
        {
            get
            {
                if (!isAccessible)
                {
                    Debug.LogError("Trying to access frameData outside of the current frame setup.");
                    return false;
                }

                return activeColorID == ResourceData.ActiveID.BackBuffer;
            }
        }

        private TextureHandle _backBufferColor;
        public TextureHandle backBufferColor
        {
            get => CheckAndGetTextureHandle(ref _backBufferColor);
            set => CheckAndSetTextureHandle(ref _backBufferColor, value);
        }

        private TextureHandle _backBufferDepth;
        public TextureHandle backBufferDepth
        {
            get => CheckAndGetTextureHandle(ref _backBufferDepth);
            set => CheckAndSetTextureHandle(ref _backBufferDepth, value);
        }

        private TextureHandle _cameraColor;
        public TextureHandle cameraColor
        {
            get => CheckAndGetTextureHandle(ref _cameraColor);
            set => CheckAndSetTextureHandle(ref _cameraColor, value);
        }

        private TextureHandle _cameraDepth;
        public TextureHandle cameraDepth
        {
            get => CheckAndGetTextureHandle(ref _cameraDepth);
            set => CheckAndSetTextureHandle(ref _cameraDepth, value);
        }

        private TextureHandle[] _gBuffer = new TextureHandle[RenderGraphUtils.gBufferSize];
        public TextureHandle[] gBuffer
        {
            get => CheckAndGetTextureHandle(ref _gBuffer);
            set => CheckAndSetTextureHandle(ref _gBuffer, value);
        }

        private TextureHandle _cameraDepthTexture;
        public TextureHandle cameraDepthTexture
        {
            get => CheckAndGetTextureHandle(ref _cameraDepthTexture);
            internal set => CheckAndSetTextureHandle(ref _cameraDepthTexture, value);
        }

        public TextureHandle cameraNormalsTexture
        {
            get => CheckAndGetTextureHandle(ref _cameraNormalsTexture);
            internal set => CheckAndSetTextureHandle(ref _cameraNormalsTexture, value);
        }

        private TextureHandle _cameraNormalsTexture;

        public TextureHandle motionVectorColor
        {
            get => CheckAndGetTextureHandle(ref _motionVectorColor);
            set => CheckAndSetTextureHandle(ref _motionVectorColor, value);
        }
        private TextureHandle _motionVectorColor;

        public TextureHandle motionVectorDepth
        {
            get => CheckAndGetTextureHandle(ref _motionVectorDepth);
            set => CheckAndSetTextureHandle(ref _motionVectorDepth, value);
        }
        private TextureHandle _motionVectorDepth;

        public TextureHandle renderingLayersTexture
        {
            get => CheckAndGetTextureHandle(ref _renderingLayersTexture);
            internal set => CheckAndSetTextureHandle(ref _renderingLayersTexture, value);
        }
        private TextureHandle _renderingLayersTexture;

        // Full-resolution requests are transient; the resolver's readback target is camera-owned.
        private TextureHandle _virtualTextureFeedback;
        internal TextureHandle virtualTextureFeedback
        {
            get => CheckAndGetTextureHandle(ref _virtualTextureFeedback);
            set => CheckAndSetTextureHandle(ref _virtualTextureFeedback, value);
        }

        private TextureHandle[] _dBuffer = new TextureHandle[RenderGraphUtils.dBufferSize];
        public TextureHandle[] dBuffer
        {
            get => CheckAndGetTextureHandle(ref _dBuffer);
            set => CheckAndSetTextureHandle(ref _dBuffer, value);
        }


        /// <summary>
        /// Color output of post-process passes (uberPost and finalPost) when HDR debug views are enabled. It replaces
        /// the backbuffer color as standard output because the later cannot be sampled back (or may not be in HDR format).
        /// If used, DebugHandler will perform the blit from DebugScreenTexture to BackBufferColor.
        /// </summary>
        private TextureHandle _debugScreenColor;
        public TextureHandle debugScreenColor
        {
            get => CheckAndGetTextureHandle(ref _debugScreenColor);
            set => CheckAndSetTextureHandle(ref _debugScreenColor, value);
        }

        /// <summary>
        /// Depth output of post-process passes (uberPost and finalPost) when HDR debug views are enabled. It replaces
        /// the backbuffer depth as standard output because the later cannot be sampled back.
        /// </summary>
        private TextureHandle _debugScreenDepth;
        public TextureHandle debugScreenDepth
        {
            get => CheckAndGetTextureHandle(ref _debugScreenDepth);
            set => CheckAndSetTextureHandle(ref _debugScreenDepth, value);
        }

        /// <summary>
        /// After Post Process Color. Stores the contents of the main color target after the post processing passes.
        /// </summary>
        private TextureHandle _afterPostProcessColor;
        public TextureHandle afterPostProcessColor
        {
            get => CheckAndGetTextureHandle(ref _afterPostProcessColor);
            internal set => CheckAndSetTextureHandle(ref _afterPostProcessColor, value);
        }

        /// <summary>
        /// Overlay UI Texture. The DrawScreenSpaceUI pass writes to this texture when rendering off-screen.
        /// </summary>
        private TextureHandle _overlayUITexture;
        public TextureHandle overlayUITexture
        {
            get => CheckAndGetTextureHandle(ref _overlayUITexture);
            internal set => CheckAndSetTextureHandle(ref _overlayUITexture, value);
        }

        /// <summary>
        /// STP debug visualization written to by the STP upscaler.
        /// </summary>
        private TextureHandle _stpDebugView;
        internal TextureHandle stpDebugView
        {
            get => CheckAndGetTextureHandle(ref _stpDebugView);
            set => CheckAndSetTextureHandle(ref _stpDebugView, value);
        }

        private BufferHandle _skyAmbientProbe;
        internal BufferHandle skyAmbientProbe
        {
            get => CheckAndGetBufferHandle(ref _skyAmbientProbe);
            set => CheckAndSetBufferHandle(ref _skyAmbientProbe, value);
        }

        private TextureHandle _skyReflectionProbe;
        internal TextureHandle skyReflectionProbe
        {
            get => CheckAndGetTextureHandle(ref _skyReflectionProbe);
            set => CheckAndSetTextureHandle(ref _skyReflectionProbe, value);
        }

        private TextureHandle _ambientOcclusionTexture;
        public TextureHandle ambientOcclusionTexture
        {
            get => CheckAndGetTextureHandle(ref _ambientOcclusionTexture);
            internal set => CheckAndSetTextureHandle(ref _ambientOcclusionTexture, value);
        }
        internal float ambientOcclusionDirectLightingStrength;

        private TextureHandle _contactShadowsTexture;
        internal TextureHandle contactShadowsTexture
        {
            get => CheckAndGetTextureHandle(ref _contactShadowsTexture);
            set => CheckAndSetTextureHandle(ref _contactShadowsTexture, value);
        }

        public override void Reset()
        {
            decalLayers = false;
            _backBufferColor = TextureHandle.nullHandle;
            _backBufferDepth = TextureHandle.nullHandle;
            _cameraColor = TextureHandle.nullHandle;
            _cameraDepth = TextureHandle.nullHandle;
            _cameraDepthTexture = TextureHandle.nullHandle;
            _cameraNormalsTexture = TextureHandle.nullHandle;
            _motionVectorColor = TextureHandle.nullHandle;
            _motionVectorDepth = TextureHandle.nullHandle;
            _renderingLayersTexture = TextureHandle.nullHandle;
            _virtualTextureFeedback = TextureHandle.nullHandle;
            _debugScreenColor = TextureHandle.nullHandle;
            _debugScreenDepth = TextureHandle.nullHandle;
            _overlayUITexture = TextureHandle.nullHandle;
            _afterPostProcessColor = TextureHandle.nullHandle;
            _stpDebugView = TextureHandle.nullHandle;
            _skyAmbientProbe = BufferHandle.nullHandle;
            _skyReflectionProbe = TextureHandle.nullHandle;
            _contactShadowsTexture = TextureHandle.nullHandle;
            _ambientOcclusionTexture = TextureHandle.nullHandle;
            ambientOcclusionDirectLightingStrength = 0f;

            for (int i = 0; i < _gBuffer.Length; i++)
                _gBuffer[i] = TextureHandle.nullHandle;

            for (int i = 0; i < _dBuffer.Length; i++)
                _dBuffer[i] = TextureHandle.nullHandle;
        }
    }
}
