using FraxinusEngine.Base;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    public class MotionBlurPass : BasePostPass
    {
        private class MotionBlurPassData
        {
            internal TextureHandle destinationTexture;
            internal TextureHandle sourceTexture;
            internal TextureHandle motionVectors;
            internal Material material;
            internal int passIndex;
            internal Camera camera;
            internal float intensity;
            internal float clamp;
            internal bool enableAlphaOutput;
        }

        internal static readonly int k_ShaderPropertyId_ViewProjM = Shader.PropertyToID("_ViewProjM");
        internal static readonly int k_ShaderPropertyId_PrevViewProjM = Shader.PropertyToID("_PrevViewProjM");
        internal static readonly int k_ShaderPropertyId_ViewProjMStereo = Shader.PropertyToID("_ViewProjMStereo");
        internal static readonly int k_ShaderPropertyId_PrevViewProjMStereo = Shader.PropertyToID("_PrevViewProjMStereo");

        MotionBlur _motionBlur;

        public MotionBlurPass() : base()
        {

        }

        public override ScriptableRenderPassInput renderInput
        { 
            get
            {
                if (_motionBlur.mode.value == MotionBlurMode.CameraAndObjects)
                    return ScriptableRenderPassInput.Motion;
                else
                    return ScriptableRenderPassInput.None;
            }
        }

        public override bool Setup(bool enable, VolumeStack stack)
        {
            _motionBlur = stack.GetComponent<MotionBlur>();

            return enable && _motionBlur.IsActive();
        }

        public override TextureHandle Render(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle activeCameraColor)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            CameraData cameraData = frameData.Get<CameraData>();

            TextureHandle currentSource = activeCameraColor;

            bool useMotionBlur = _motionBlur.IsActive() && !cameraData.isSceneViewCamera;
            if (useMotionBlur)
            {
                RenderMotionBlur(renderGraph, resourceData, cameraData, in currentSource, out var MotionBlurTarget);
                currentSource = MotionBlurTarget;
            }

            return currentSource;
        }

        protected override void OnDispose()
        {

        }

        private void RenderMotionBlur(RenderGraph renderGraph, ResourceData resourceData, CameraData cameraData, in TextureHandle source, out TextureHandle destination)
        {
            var descriptor = _postProcessPass.descriptor;
            var graph = _postProcessPass.graph;
            var data = _postProcessPass.data;

            var material = graph.materialLib.GetMaterial("cameraMotionBlurPS");
            var desc = PostProcessPass.GetCompatibleDescriptor(descriptor,
                descriptor.width,
                descriptor.height,
                descriptor.graphicsFormat,
                GraphicsFormat.None);

            destination = UltraRenderer.CreateRenderGraphTexture(renderGraph, desc, "_MotionBlurTarget", true, FilterMode.Bilinear);

            TextureHandle motionVectorColor = resourceData.motionVectorColor;
            TextureHandle cameraDepthTexture = resourceData.cameraDepthTexture;

            var mode = _motionBlur.mode.value;
            int passIndex = (int)_motionBlur.quality.value;
            passIndex += (mode == MotionBlurMode.CameraAndObjects) ? 3 : 0;

            using (var builder = renderGraph.AddRasterRenderPass<MotionBlurPassData>("Motion Blur", out var passData, ProfilingSampler.Get(UltraRPProfileId.RG_MotionBlur)))
            {
                builder.AllowGlobalStateModification(true);
                passData.destinationTexture = destination;
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                passData.sourceTexture = source;
                builder.UseTexture(source, AccessFlags.Read);

                if (mode == MotionBlurMode.CameraAndObjects)
                {
                    Debug.Assert(ScriptableRenderer.current.SupportsMotionVectors(), "Current renderer does not support motion vectors.");
                    Debug.Assert(motionVectorColor.IsValid(), "Motion vectors are invalid. Per-object motion blur requires a motion vector texture.");

                    passData.motionVectors = motionVectorColor;
                    builder.UseTexture(motionVectorColor, AccessFlags.Read);
                }
                else
                {
                    passData.motionVectors = TextureHandle.nullHandle;
                }

                Debug.Assert(cameraDepthTexture.IsValid(), "Camera depth texture is invalid. Per-camera motion blur requires a depth texture.");
                builder.UseTexture(cameraDepthTexture, AccessFlags.Read);
                passData.material = material;
                passData.passIndex = passIndex;
                passData.camera = cameraData.camera;
                passData.enableAlphaOutput = cameraData.isAlphaOutputEnabled;
                passData.intensity = _motionBlur.intensity.value;
                passData.clamp = _motionBlur.clamp.value;
                builder.SetRenderFunc(static (MotionBlurPassData data, RasterGraphContext context) =>
                {
                    var cmd = context.cmd;
                    RTHandle sourceTextureHdl = data.sourceTexture;

                    UpdateMotionBlurMatrices(ref data.material, data.camera);

                    data.material.SetFloat("_Intensity", data.intensity);
                    data.material.SetFloat("_Clamp", data.clamp);
                    CoreUtils.SetKeyword(data.material, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, data.enableAlphaOutput);

                    PostProcessUtils.SetSourceSize(cmd, data.sourceTexture);
                    Vector2 viewportScale = sourceTextureHdl.useScaling ? new Vector2(sourceTextureHdl.rtHandleProperties.rtHandleScale.x, sourceTextureHdl.rtHandleProperties.rtHandleScale.y) : Vector2.one;
                    Blitter.BlitTexture(cmd, sourceTextureHdl, viewportScale, data.material, data.passIndex);
                });

                return;
            }
        }

        internal static void UpdateMotionBlurMatrices(ref Material material, Camera camera)
        {
            MotionVectorsPersistentData motionData = null;

            if (camera.TryGetComponent<UltraAdditionalCameraData>(out var additionalCameraData))
                motionData = additionalCameraData.motionVectorsPersistentData;

            if (motionData == null)
                return;

            {
                int viewProjMIdx = 0;
                // TODO: These should be part of UltraRP main matrix set. For now, we set them here for motion vector rendering.
                material.SetMatrix(k_ShaderPropertyId_PrevViewProjM, motionData.previousViewProjectionStereo[viewProjMIdx]);
                material.SetMatrix(k_ShaderPropertyId_ViewProjM, motionData.viewProjectionStereo[viewProjMIdx]);
            }
        }
    }

}
