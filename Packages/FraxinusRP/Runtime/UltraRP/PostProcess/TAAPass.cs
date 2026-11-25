using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    public class TAAPass : BasePostPass
    {
        private const string _TemporalAATargetName = "_TemporalAATarget";

        public TAAPass() : base() { }


        public override bool Setup(bool enable, VolumeStack stack)
        {
            return enable;
        }

        public override TextureHandle Render(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle activeCameraColor)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();

            bool useTemporalAA = cameraData.IsTemporalAAEnabled();
            bool isSTPRequested = cameraData.IsSTPRequested();
            bool useSTP = useTemporalAA && isSTPRequested;

            // Warn users if TAA and STP are disabled despite being requested
            if (!useTemporalAA && cameraData.IsTemporalAARequested())
                TemporalAA.ValidateAndWarn(cameraData, isSTPRequested);

            TextureHandle currentSource = activeCameraColor;
            if (useTemporalAA)
            {
                if (useSTP)
                {
                    RenderSTP(renderGraph, resourceData, cameraData, ref currentSource, out var StpTarget);
                    currentSource = StpTarget;
                }
                else
                {
                    RenderTemporalAA(renderGraph, resourceData, cameraData, ref currentSource, out var TemporalAATarget);
                    currentSource = TemporalAATarget;
                }
            }

            return currentSource;
        }

        protected override void OnDispose()
        {

        }

        #region STP
        private const string _UpscaledColorTargetName = "_CameraColorUpscaledSTP";

        #region TemporalAA
        private void RenderSTP(RenderGraph renderGraph, ResourceData resourceData, CameraData cameraData, ref TextureHandle source, out TextureHandle destination)
        {
            var descriptor = _postProcessPass.descriptor;
            var graph = _postProcessPass.graph;
            var data = _postProcessPass.data;

            TextureHandle cameraDepth = resourceData.cameraDepthTexture;
            TextureHandle motionVectors = resourceData.motionVectorColor;

            Debug.Assert(motionVectors.IsValid(), "MotionVectors are invalid. STP requires a motion vector texture.");

            var desc =PostProcessPass.GetCompatibleDescriptor(cameraData.cameraTargetDescriptor,
                cameraData.pixelWidth,
                cameraData.pixelHeight,
                cameraData.cameraTargetDescriptor.graphicsFormat);

            // STP uses compute shaders so all render textures must enable random writes
            desc.enableRandomWrite = true;

            // Avoid enabling sRGB because STP works with compute shaders which can't output sRGB automatically.
            desc.sRGB = false;

            destination = UltraRenderer.CreateRenderGraphTexture(renderGraph, desc, _UpscaledColorTargetName, false, FilterMode.Bilinear);

            int frameIndex = Time.frameCount;
            var noiseTexture = data.textures.blueNoise16LTex[frameIndex & (data.textures.blueNoise16LTex.Length - 1)];

            StpUtils.Execute(renderGraph, resourceData, cameraData, source, cameraDepth, motionVectors, destination, noiseTexture);

            // Update the camera resolution to reflect the upscaled size
            _postProcessPass.UpdateCameraResolution(renderGraph, cameraData, new Vector2Int(desc.width, desc.height));
        }
        #endregion

        private void RenderTemporalAA(RenderGraph renderGraph, ResourceData resourceData, CameraData cameraData, ref TextureHandle source, out TextureHandle destination)
        {
            var descriptor = _postProcessPass.descriptor;
            var graph = _postProcessPass.graph;
            var data = _postProcessPass.data;

            var desc = PostProcessPass.GetCompatibleDescriptor(descriptor,
                descriptor.width,
                descriptor.height,
                descriptor.graphicsFormat,
                GraphicsFormat.None);
            destination = UltraRenderer.CreateRenderGraphTexture(renderGraph, desc, _TemporalAATargetName, false, FilterMode.Bilinear);

            TextureHandle cameraDepth = resourceData.cameraDepth;
            TextureHandle motionVectors = resourceData.motionVectorColor;

            Debug.Assert(motionVectors.IsValid(), "MotionVectors are invalid. TAA requires a motion vector texture.");

            var material = graph.materialLib.GetMaterial("temporalAntialiasingPS");

            TemporalAA.Render(renderGraph, material, cameraData, ref source, ref cameraDepth, ref motionVectors, ref destination);
        }
        #endregion
    }
}
