using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public class LensFlare : BasePostPass
    {
        sealed class DataPass
        {
            internal TextureHandle destination, depth;
            internal Material material;
            internal CameraData camera;
            internal int width, height;
            internal bool panini;
            internal float distance, crop;
        }

        sealed class BloomSourcePass
        {
            internal TextureHandle source;
            internal Material material;
            internal Vector4 parameters;
            internal bool highQuality, alpha;
            internal int pass;
        }

        sealed class ScreenPass
        {
            internal TextureHandle destination, bloomMip, streak1, streak2, result;
            internal Camera camera;
            internal Material material;
            internal int width, height;
            internal Color tint;
            internal Vector4 parameters1, parameters2, parameters3, parameters4, parameters5;
        }

        static readonly int s_Depth = Shader.PropertyToID("_CameraDepthTexture");
        static readonly int s_DepthSize = Shader.PropertyToID("_CameraDepthTexture_TexelSize");
        static readonly Func<Light, Camera, Vector3, float> s_Attenuation = LightAttenuation;
        ScreenSpaceLensFlare _screen;
        Bloom _bloom;
        Material _brightFilter;

        public override ScriptableRenderPassInput renderInput => ScriptableRenderPassInput.Depth;

        public override bool Setup(bool enable, VolumeStack stack)
        {
            _screen = stack.GetComponent<ScreenSpaceLensFlare>();
            _bloom = stack.GetComponent<Bloom>();
            return enable && ((_screen != null && _screen.IsActive()) || !LensFlareCommonSRP.Instance.IsEmpty());
        }

        public override TextureHandle Render(RenderGraph graph, ContextContainer frameData, in TextureHandle color)
        {
            var resources = frameData.Get<ResourceData>();
            var camera = frameData.Get<CameraData>();
            var post = frameData.Get<PostProcessingData>();
            // Screen flares are independent of the Bloom entry/intensity. Their extraction uses
            // the Bloom threshold and mip controls, then composites additively before UberPost.
            if (post.supportScreenSpaceLensFlare && _screen != null && _screen.IsActive())
                RenderScreenSpace(graph, camera, color);
            if (post.supportDataDrivenLensFlare && !LensFlareCommonSRP.Instance.IsEmpty())
            {
                TextureHandle depth = resources.cameraDepthTexture;
                if (depth.IsValid())
                    RenderDataDriven(graph, camera, color, depth);
            }
            return color;
        }

        protected override void OnDispose()
        {
            CoreUtils.Destroy(_brightFilter);
            _brightFilter = null;
        }

        void FillData(DataPass data, CameraData camera, TextureHandle destination, TextureHandle depth, RenderGraph graph)
        {
            var descriptor = destination.GetDescriptor(graph);
            data.destination = destination;
            data.depth = depth;
            data.material = _postProcessPass.graph.materialLib.GetMaterial(nameof(PostProcessData.ShaderResources.LensFlareDataDrivenPS));
            data.camera = camera;
            data.width = descriptor.width;
            data.height = descriptor.height;
            data.panini = false;
            data.distance = 0;
            data.crop = 1;
        }

        void RenderDataDriven(RenderGraph graph, CameraData camera, TextureHandle color, TextureHandle depth)
        {
            bool useOcclusion = LensFlareCommonSRP.IsOcclusionRTCompatible() && LensFlareCommonSRP.occlusionRT != null;
            TextureHandle occlusion = useOcclusion ? graph.ImportTexture(LensFlareCommonSRP.occlusionRT) : TextureHandle.nullHandle;
            if (useOcclusion)
            {
                using (var builder = graph.AddUnsafePass<DataPass>("Lens Flare Occlusion", out var data, _profilingSampler))
                {
                    FillData(data, camera, color, depth, graph);
                    builder.UseTexture(depth, AccessFlags.Read);
                    builder.UseTexture(occlusion, AccessFlags.ReadWrite);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (DataPass pass, UnsafeGraphContext context) =>
                    {
                        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        BindDepth(cmd, pass.depth);
                        var xr = GetXRPass(pass.camera);
                        int count = xr.enabled && xr.singlePassEnabled ? xr.viewCount : 1;
                        for (int eye = 0; eye < count; ++eye)
                        {
                            int xrIndex = xr.enabled && !xr.singlePassEnabled ? xr.multipassId : eye;
                            var vp = GL.GetGPUProjectionMatrix(pass.camera.GetProjectionMatrixNoJitter(eye), true) * pass.camera.GetViewMatrix(eye);
                            LensFlareCommonSRP.ComputeOcclusion(pass.material, pass.camera.camera, xr, xrIndex,
                                pass.width, pass.height, pass.panini, pass.distance, pass.crop, false,
                                pass.camera.worldSpaceCameraPos, vp, cmd, false, false, null, null);
                        }
                    });
                }
            }
            using (var builder = graph.AddUnsafePass<DataPass>("Lens Flare Data Driven", out var data, _profilingSampler))
            {
                FillData(data, camera, color, depth, graph);
                builder.UseTexture(color, AccessFlags.ReadWrite); // Additive blending must preserve the source.
                builder.UseTexture(depth, AccessFlags.Read);
                if (useOcclusion) builder.UseTexture(occlusion, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (DataPass pass, UnsafeGraphContext context) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    BindDepth(cmd, pass.depth);
                    var xr = GetXRPass(pass.camera);
                    int count = xr.enabled && xr.singlePassEnabled ? xr.viewCount : 1;
                    RTHandle destination = pass.destination;
                    for (int eye = 0; eye < count; ++eye)
                    {
                        int xrIndex = xr.enabled && !xr.singlePassEnabled ? xr.multipassId : eye;
                        var vp = GL.GetGPUProjectionMatrix(pass.camera.GetProjectionMatrixNoJitter(eye), true) * pass.camera.GetViewMatrix(eye);
                        LensFlareCommonSRP.DoLensFlareDataDrivenCommon(pass.material, pass.camera.camera,
                            new Rect(0, 0, pass.width, pass.height), xr, xrIndex, pass.width, pass.height,
                            pass.panini, pass.distance, pass.crop, false, pass.camera.worldSpaceCameraPos, vp,
                            cmd, false, false, null, null, destination.nameID, s_Attenuation, false);
                    }
                });
            }
        }

        // UltraRP's camera integration is currently mono; Core still requires an XRPass.
        static XRPass GetXRPass(CameraData camera) => XRSystem.emptyPass;

        static void BindDepth(CommandBuffer cmd, TextureHandle depth)
        {
            RTHandle handle = depth;
            cmd.SetGlobalTexture(s_Depth, handle);
            cmd.SetGlobalVector(s_DepthSize, new Vector4(1f / handle.rt.width, 1f / handle.rt.height, handle.rt.width, handle.rt.height));
        }

        TextureHandle BloomSource(RenderGraph graph, CameraData camera, TextureHandle color)
        {
            var descriptor = color.GetDescriptor(graph);
            descriptor.msaaSamples = MSAASamples.None;
            descriptor.depthBufferBits = DepthBits.None;
            descriptor.clearBuffer = false;
            descriptor.filterMode = FilterMode.Bilinear;
            descriptor.width = Mathf.Max(1, descriptor.width / 2);
            descriptor.height = Mathf.Max(1, descriptor.height / 2);
            descriptor.name = "Lens Flare Bright Source";
            TextureHandle source = graph.CreateTexture(descriptor);
            RecordBloomBlit(graph, color, source, 0, camera.isAlphaOutputEnabled);
            int mip = Mathf.Min(_screen.bloomMip.value, Mathf.Max(0, Mathf.FloorToInt(Mathf.Log(Mathf.Max(descriptor.width, descriptor.height), 2))));
            for (int i = 0; i < mip; ++i)
            {
                descriptor.width = Mathf.Max(1, descriptor.width / 2);
                descriptor.height = Mathf.Max(1, descriptor.height / 2);
                descriptor.name = "Lens Flare Blur Horizontal";
                var horizontal = graph.CreateTexture(descriptor);
                descriptor.name = "Lens Flare Blur Vertical";
                var vertical = graph.CreateTexture(descriptor);
                RecordBloomBlit(graph, source, horizontal, 1, camera.isAlphaOutputEnabled);
                RecordBloomBlit(graph, horizontal, vertical, 2, camera.isAlphaOutputEnabled);
                source = vertical;
            }
            return source;
        }

        void RecordBloomBlit(RenderGraph graph, TextureHandle source, TextureHandle destination, int shaderPass, bool alpha)
        {
            using (var builder = graph.AddRasterRenderPass<BloomSourcePass>("Lens Flare Bright Filter", out var data, _profilingSampler))
            {
                float threshold = Mathf.GammaToLinearSpace(_bloom.threshold.value);
                data.parameters = new Vector4(Mathf.Lerp(0.05f, 0.95f, _bloom.scatter.value), _bloom.clamp.value, threshold, threshold * 0.5f);
                data.source = source;
                data.material = _brightFilter;
                data.highQuality = _bloom.highQualityFiltering.value;
                data.alpha = alpha;
                data.pass = shaderPass;
                builder.UseTexture(source, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (BloomSourcePass pass, RasterGraphContext context) =>
                {
                    pass.material.SetVector(ShaderConstants._Params, pass.parameters);
                    CoreUtils.SetKeyword(pass.material, ShaderKeywordStrings.BloomHQ, pass.highQuality);
                    CoreUtils.SetKeyword(pass.material, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, pass.alpha);
                    Blitter.BlitTexture(context.cmd, pass.source, new Vector4(1, 1, 0, 0), pass.material, pass.pass);
                });
            }
        }

        void RenderScreenSpace(RenderGraph graph, CameraData camera, TextureHandle color)
        {
            if (_brightFilter == null)
                _brightFilter = CoreUtils.CreateEngineMaterial(_postProcessPass.graph.postProcessData.shaders.bloomPS);
            var sourceDescriptor = color.GetDescriptor(graph);
            int downsample = (int)_screen.resolution.value;
            var descriptor = sourceDescriptor;
            descriptor.width = Mathf.Max(1, descriptor.width / downsample);
            descriptor.height = Mathf.Max(1, descriptor.height / downsample);
            descriptor.msaaSamples = MSAASamples.None;
            descriptor.depthBufferBits = DepthBits.None;
            descriptor.clearBuffer = true;
            descriptor.clearColor = Color.clear;
            descriptor.filterMode = FilterMode.Bilinear;
            descriptor.name = "Lens Flare Streak A";
            var streak1 = graph.CreateTexture(descriptor);
            descriptor.name = "Lens Flare Streak B";
            var streak2 = graph.CreateTexture(descriptor);
            descriptor.name = "Lens Flare Screen Result";
            var result = graph.CreateTexture(descriptor);
            var bloomMip = BloomSource(graph, camera, color);
            using (var builder = graph.AddUnsafePass<ScreenPass>("Lens Flare Screen Space", out var data, _profilingSampler))
            {
                data.destination = color; data.bloomMip = bloomMip;
                data.streak1 = streak1; data.streak2 = streak2; data.result = result;
                data.camera = camera.camera;
                data.width = sourceDescriptor.width; data.height = sourceDescriptor.height;
                data.material = _postProcessPass.graph.materialLib.GetMaterial(nameof(PostProcessData.ShaderResources.LensFlareScreenSpacePS));
                data.tint = _screen.tintColor.value;
                data.parameters1 = new Vector4(_screen.intensity.value, _screen.firstFlareIntensity.value, _screen.secondaryFlareIntensity.value, _screen.warpedFlareIntensity.value);
                data.parameters2 = new Vector4(_screen.vignetteEffect.value, _screen.startingPosition.value, _screen.scale.value, 0);
                data.parameters3 = new Vector4(_screen.samples.value, _screen.sampleDimmer.value, _screen.chromaticAbberationIntensity.value, 0);
                data.parameters4 = new Vector4(_screen.streaksIntensity.value, _screen.streaksLength.value, _screen.streaksOrientation.value, _screen.streaksThreshold.value);
                data.parameters5 = new Vector4(downsample, Mathf.Max(0.001f, _screen.warpedFlareScale.value.x), Mathf.Max(0.001f, _screen.warpedFlareScale.value.y), 0);
                builder.UseTexture(color, AccessFlags.ReadWrite);
                builder.UseTexture(bloomMip, AccessFlags.Read);
                builder.UseTexture(streak1, AccessFlags.ReadWrite);
                builder.UseTexture(streak2, AccessFlags.ReadWrite);
                builder.UseTexture(result, AccessFlags.ReadWrite);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (ScreenPass pass, UnsafeGraphContext context) =>
                {
                    LensFlareCommonSRP.DoLensFlareScreenSpaceCommon(pass.material, pass.camera, pass.width, pass.height,
                        pass.tint, pass.destination, pass.bloomMip, null, pass.streak1, pass.streak2,
                        pass.parameters1, pass.parameters2, pass.parameters3, pass.parameters4, pass.parameters5,
                        CommandBufferHelpers.GetNativeCommandBuffer(context.cmd), pass.result, false);
                });
            }
        }

        static float LightAttenuation(Light light, Camera camera, Vector3 direction)
        {
            if (light == null) return 1;
            switch (light.type)
            {
                case LightType.Directional: return LensFlareCommonSRP.ShapeAttenuationDirLight(light.transform.forward, camera.transform.forward);
                case LightType.Point: return LensFlareCommonSRP.ShapeAttenuationPointLight();
                case LightType.Spot: return LensFlareCommonSRP.ShapeAttenuationSpotConeLight(light.transform.forward, direction, light.spotAngle, Mathf.Clamp01(light.innerSpotAngle / Mathf.Max(light.spotAngle, .001f)));
                default: return 1;
            }
        }
    }
}
