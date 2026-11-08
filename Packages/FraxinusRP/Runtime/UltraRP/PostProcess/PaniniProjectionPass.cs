using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public sealed class PaniniProjectionPass : BasePostPass
    {
        PaniniProjection settings;
        static readonly int Params = Shader.PropertyToID("_Params");

        sealed class PassData
        {
            public TextureHandle source;
            public Material material;
            public Vector4 parameters;
            public bool generic;
        }

        public override bool Setup(bool enable, VolumeStack stack)
        {
            settings = stack.GetComponent<PaniniProjection>();
            return enable && settings != null && settings.active && settings.IsActive();
        }

        public override TextureHandle Render(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle activeCameraColor)
        {
            var cameraData = frameData.Get<CameraData>();
            if (cameraData.isSceneViewCamera || cameraData.camera.orthographic)
                return activeCameraColor;
            var material = _postProcessPass.graph.materialLib.GetMaterial("paniniProjectionPS");
            if (material == null)
                return activeCameraColor;
            var descriptor = _postProcessPass.descriptor;
            var desc = PostProcessPass.GetCompatibleDescriptor(descriptor, descriptor.width,
                descriptor.height, descriptor.graphicsFormat, GraphicsFormat.None);
            var destination = UltraRenderer.CreateRenderGraphTexture(renderGraph, desc,
                "_PaniniProjectionTarget", true, FilterMode.Bilinear);

            float distance = settings.distance.value;
            float extentY = Mathf.Tan(0.5f * cameraData.camera.fieldOfView * Mathf.Deg2Rad);
            var extents = new Vector2(descriptor.width / (float)descriptor.height * extentY, extentY);
            float cylinderDistance = 1f / Mathf.Sqrt(extents.x * extents.x + 1f);
            var cropExtents = extents * cylinderDistance * ((1f + distance) / (cylinderDistance + distance));
            float cropScale = Mathf.Min(cropExtents.x / extents.x, cropExtents.y / extents.y);
            float scale = Mathf.Lerp(1f, Mathf.Clamp01(cropScale), settings.cropToFit.value);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Panini Projection", out var pass,
                _profilingSampler))
            {
                pass.source = activeCameraColor;
                pass.material = material;
                pass.parameters = new Vector4(extents.x, extents.y, distance, scale);
                pass.generic = 1f - Mathf.Abs(distance) > float.Epsilon;
                builder.UseTexture(activeCameraColor, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    RTHandle source = data.source;
                    data.material.SetVector(Params, data.parameters);
                    CoreUtils.SetKeyword(data.material, ShaderKeywordStrings.PaniniGeneric, data.generic);
                    CoreUtils.SetKeyword(data.material, ShaderKeywordStrings.PaniniUnitDistance, !data.generic);
                    Vector2 scale = source.useScaling
                        ? new Vector2(source.rtHandleProperties.rtHandleScale.x, source.rtHandleProperties.rtHandleScale.y)
                        : Vector2.one;
                    Blitter.BlitTexture(context.cmd, source, scale, data.material, 0);
                });
            }
            return destination;
        }

        protected override void OnDispose() { }
    }
}
