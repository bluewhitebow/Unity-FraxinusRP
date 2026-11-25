using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class MotionVectorRenderPass : BuiltinPass
    {
        public class MotionMatrixPassData
        {
            internal MotionVectorsPersistentData motionData;
        };

        private class PassData
        {
            internal Camera camera;

            internal TextureHandle motionVectorColor;
            internal TextureHandle motionVectorDepth;
            internal TextureHandle cameraDepth;
            internal Material cameraMaterial;
            internal RendererListHandle rendererListHdl;

            // Required for code sharing purpose between RG and non-RG.
            internal RendererList rendererList;
        }

        static readonly int s_cameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");
        static readonly ProfilingSampler s_setMotionMatrixProfilingSampler = new ProfilingSampler("Set Motion Vector Global Matrices");

        internal const string motionVectorTextureName = "_MotionVectorTexture";
        internal const string motionVectorDepthTextureName = "_MotionVectorDepthTexture";
        internal const GraphicsFormat targetFormat = GraphicsFormat.R16G16_SFloat;

        public const string motionVectorsLightModeTag = "MotionVectors";
        static readonly string[] s_shaderTags = new string[] { motionVectorsLightModeTag };

        private FilteringSettings _filteringSettings;

        Material _cameraMaterial = null;


        private LayerMask _layerMask = -1;
        public LayerMask layerMask
        {
            get => _layerMask;
            set
            {
                _layerMask = value;
                SetDirty();
            }
        }

        public MotionVectorRenderPass() : base()
        {
            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRendererResources>(
                    out var ultraRendererShaders))
            {
                _cameraMaterial = CoreUtils.CreateEngineMaterial(ultraRendererShaders.cameraMotionVector);
            }

            FlushRenderSettings();
        }


        protected override void OnCameraCleanup(CommandBuffer cmd) { }

        protected override void OnDispose()
        {
            CoreUtils.Destroy(_cameraMaterial);
            _cameraMaterial = null;
        }


        internal static void SetRenderGraphMotionVectorGlobalMatrices(RenderGraph renderGraph, CameraData cameraData)
        {
            if (cameraData.camera.TryGetComponent<UltraAdditionalCameraData>(out var additionalCameraData))
            {
                using (var builder = renderGraph.AddRasterRenderPass<MotionMatrixPassData>(s_setMotionMatrixProfilingSampler.name, out var passData, s_setMotionMatrixProfilingSampler))
                {
                    passData.motionData = additionalCameraData.motionVectorsPersistentData;

                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (MotionMatrixPassData data, RasterGraphContext context) =>
                    {
                        data.motionData.SetGlobalMotionMatrices(context.cmd, null);
                    });
                }
            }
        }

        private void InitPassData(ref PassData passData, CameraData cameraData)
        {
            passData.camera = cameraData.camera;
            passData.cameraMaterial = _cameraMaterial;
        }

        private void InitRendererLists(ref PassData passData, ref CullingResults cullResults, bool supportsDynamicBatching, ScriptableRenderContext context, RenderGraph renderGraph, bool useRenderGraph)
        {
            var drawingSettings = GetDrawingSettings(passData.camera, supportsDynamicBatching);
            var renderStateBlock = new RenderStateBlock(RenderStateMask.Nothing);
            if (useRenderGraph)
                RenderingUtils.CreateRendererListWithRenderStateBlock(renderGraph, ref cullResults, drawingSettings, _filteringSettings, renderStateBlock, ref passData.rendererListHdl);
            else
                RenderingUtils.CreateRendererListWithRenderStateBlock(context, ref cullResults, drawingSettings, _filteringSettings, renderStateBlock, ref passData.rendererList);
        }

        internal void Render(RenderGraph renderGraph, ContextContainer frameData, TextureHandle cameraDepthTexture, TextureHandle motionVectorColor, TextureHandle motionVectorDepth)
        {
            RenderingData renderingData = frameData.Get<RenderingData>();
            CameraData cameraData = frameData.Get<CameraData>();
            _filteringSettings.layerMask = _layerMask & renderingData.opaqueLayerMask;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(_passName, out var passData, _profilingSampler))
            {
                builder.UseAllGlobalTextures(true);

                //  TODO RENDERGRAPH: culling? force culling off for testing
                builder.AllowGlobalStateModification(true);

                passData.motionVectorColor = motionVectorColor;
                builder.SetRenderAttachment(motionVectorColor, 0, AccessFlags.Write);
                passData.motionVectorDepth = motionVectorDepth;
                builder.SetRenderAttachmentDepth(motionVectorDepth, AccessFlags.Write);
                InitPassData(ref passData, cameraData);
                passData.cameraDepth = cameraDepthTexture;
                builder.UseTexture(cameraDepthTexture, AccessFlags.Read);

                InitRendererLists(ref passData, ref renderingData.cullResults, renderingData.supportsDynamicBatching,
                    default(ScriptableRenderContext), renderGraph, true);
                builder.UseRendererList(passData.rendererListHdl);

                if (motionVectorColor.IsValid())
                    builder.SetGlobalTextureAfterPass(motionVectorColor, Shader.PropertyToID(motionVectorTextureName));
                if (motionVectorDepth.IsValid())
                    builder.SetGlobalTextureAfterPass(motionVectorDepth, Shader.PropertyToID(motionVectorDepthTextureName));

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    if (data.cameraMaterial != null)
                        data.cameraMaterial.SetTexture(s_cameraDepthTextureID, data.cameraDepth);

                    ExecutePass(context.cmd, data, data.rendererListHdl);
                });
            }
        }

        private static void ExecutePass(RasterCommandBuffer cmd, PassData passData, RendererList rendererList)
        {
            var cameraMaterial = passData.cameraMaterial;

            if (cameraMaterial == null)
                return;

            // Get data
            Camera camera = passData.camera;

            // Never draw in Preview
            if (camera.cameraType == CameraType.Preview)
                return;

            // These flags are still required in SRP or the engine won't compute previous model matrices...
            // If the flag hasn't been set yet on this camera, motion vectors will skip a frame.
            camera.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;

            // TODO: add option to only draw either one?
            DrawCameraMotionVectors(cmd, cameraMaterial);
            DrawObjectMotionVectors(cmd, ref rendererList);
        }

        private static DrawingSettings GetDrawingSettings(Camera camera, bool supportsDynamicBatching)
        {
            var sortingSettings = new SortingSettings(camera) { criteria = SortingCriteria.CommonOpaque };
            var drawingSettings = new DrawingSettings(ShaderTagId.none, sortingSettings)
            {
                perObjectData = PerObjectData.MotionVectors,
                enableDynamicBatching = supportsDynamicBatching,
                enableInstancing = true,
            };

            for (int i = 0; i < s_shaderTags.Length; ++i)
                drawingSettings.SetShaderPassName(i, new ShaderTagId(s_shaderTags[i]));

            return drawingSettings;
        }

        private static void DrawCameraMotionVectors(RasterCommandBuffer cmd, Material cameraMaterial)
        {
            // Draw fullscreen quad
            cmd.DrawProcedural(Matrix4x4.identity, cameraMaterial, 0, MeshTopology.Triangles, 3, 1);
        }

        private static void DrawObjectMotionVectors(RasterCommandBuffer cmd, ref RendererList rendererList)
        {
            cmd.DrawRendererList(rendererList);
        }

        private void SetDirty() => FlushRenderSettings();

        private void FlushRenderSettings()
        {
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, _layerMask);
        }
    }

}
