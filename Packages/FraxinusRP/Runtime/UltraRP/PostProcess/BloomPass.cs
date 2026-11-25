using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    static class ShaderConstants
    {
        public static readonly int _Params = Shader.PropertyToID("_Params");
        public static readonly int _SourceTexLowMip = Shader.PropertyToID("_SourceTexLowMip");
        public static readonly int _Bloom_Params = Shader.PropertyToID("_Bloom_Params");
        public static readonly int _Bloom_Texture = Shader.PropertyToID("_Bloom_Texture");
        public static readonly int _LensDirt_Texture = Shader.PropertyToID("_LensDirt_Texture");
        public static readonly int _LensDirt_Params = Shader.PropertyToID("_LensDirt_Params");
        public static readonly int _LensDirt_Intensity = Shader.PropertyToID("_LensDirt_Intensity");

        public static int[] _BloomMipUp = new int[16];
        public static int[] _BloomMipDown = new int[16];
    }

    public class BloomPass : BasePostPass
    {
        private class BloomPassData
        {
            internal int mipCount;

            internal Material material;
            internal Material[] upsampleMaterials;

            internal TextureHandle sourceTexture;

            internal TextureHandle[] bloomMipUp;
            internal TextureHandle[] bloomMipDown;
        }

        // Copied into each recorded Uber pass without allocating a per-camera object.
        internal struct UberSetupBloomPassData
        {
            internal Vector4 bloomParams;
            internal Vector4 dirtScaleOffset;
            internal float dirtIntensity;
            internal Texture dirtTexture;
            internal bool highQualityFilteringValue;
            internal TextureHandle bloomTexture;
        }

        internal struct BloomMaterialParams
        {
            internal Vector4 parameters;
            internal bool highQualityFiltering;
            internal bool enableAlphaOutput;

            internal bool Equals(ref BloomMaterialParams other)
            {
                return parameters == other.parameters &&
                       highQualityFiltering == other.highQualityFiltering &&
                       enableAlphaOutput == other.enableAlphaOutput;
            }
        }


        const int k_maxPyramidSize = 16;

        RTHandle[] _bloomMipDown;
        RTHandle[] _bloomMipUp;
        TextureHandle[] _bloomMipUpHdls;
        TextureHandle[] _bloomMipDownHlds;

        private Bloom _bloom;

        private Material _bloomMat;
        private Material[] _bloomUpsampleMats;


        // Cached bloom params from previous frame to avoid unnecessary material updates
        BloomMaterialParams _bloomParamsPrev;

        public BloomPass() : base()
        {
            ShaderConstants._BloomMipUp = new int[k_maxPyramidSize];
            ShaderConstants._BloomMipDown = new int[k_maxPyramidSize];
            _bloomMipUp = new RTHandle[k_maxPyramidSize];
            _bloomMipDown = new RTHandle[k_maxPyramidSize];
           
            for (int i = 0; i < k_maxPyramidSize; i++)
            {
                ShaderConstants._BloomMipUp[i] = Shader.PropertyToID("_BloomMipUp" + i);
                ShaderConstants._BloomMipDown[i] = Shader.PropertyToID("_BloomMipDown" + i);
                // Get name, will get Allocated with descriptor later
                _bloomMipUp[i] = RTHandles.Alloc(ShaderConstants._BloomMipUp[i], name: "_BloomMipUp" + i);
                _bloomMipDown[i] = RTHandles.Alloc(ShaderConstants._BloomMipDown[i], name: "_BloomMipDown" + i);
            }

            // Bloom pyramid TextureHandles
            _bloomMipUpHdls = new TextureHandle[k_maxPyramidSize];
            _bloomMipDownHlds = new TextureHandle[k_maxPyramidSize];
        }


        public override bool Setup(bool enable, VolumeStack stack)
        {
            _bloom = stack.GetComponent<Bloom>();

            bool bloomActive = _bloom.IsActive();
            if (bloomActive)
            {
                var graph = _postProcessPass.graph;
                _bloomMat = graph.materialLib.GetMaterial("bloomPS");

                if (_bloomUpsampleMats == null)
                {
                    _bloomUpsampleMats = new Material[k_maxPyramidSize];
                    for (uint i = 0; i < k_maxPyramidSize; ++i)
                        _bloomUpsampleMats[i] = CoreUtils.CreateEngineMaterial(graph.postProcessData.shaders.bloomPS);
                }
            }

            return enable && bloomActive;
        }


        public override TextureHandle Render(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle activeCameraColor)
        {
            CameraData cameraData = frameData.Get<CameraData>();

            TextureHandle currentSource = activeCameraColor;
            RenderBloomTexture(renderGraph, currentSource, out var bloomTexture, cameraData.isAlphaOutputEnabled);
            PrepareUberPostBloom(in bloomTexture);

            return currentSource;
        }

        protected override void OnDispose()
        {
            foreach (var handle in _bloomMipDown)
                handle?.Release();
            foreach (var handle in _bloomMipUp)
                handle?.Release();

            if (_bloomUpsampleMats is not null && _bloomUpsampleMats.Length > 0)
            {
                for (uint i = 0; i < k_maxPyramidSize; ++i)
                    CoreUtils.Destroy(_bloomUpsampleMats[i]);
            }

        }

        private void RenderBloomTexture(RenderGraph renderGraph, in TextureHandle source, out TextureHandle destination, bool enableAlphaOutput)
        {
            var descriptor = _postProcessPass.descriptor;
            var graph = _postProcessPass.graph;
            var data = _postProcessPass.data;

            // Start at half-res
            int downres = 1;
            switch (_bloom.downscale.value)
            {
                case BloomDownscaleMode.Half:
                    downres = 1;
                    break;
                case BloomDownscaleMode.Quarter:
                    downres = 2;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            //We should set the limit the downres result to ensure we dont turn 1x1 textures, which should technically be valid
            //into 0x0 textures which will be invalid
            int tw = Mathf.Max(1, descriptor.width >> downres);
            int th = Mathf.Max(1, descriptor.height >> downres);

            // Determine the iteration count
            int maxSize = Mathf.Max(tw, th);
            int iterations = Mathf.FloorToInt(Mathf.Log(maxSize, 2f) - 1);
            int mipCount = Mathf.Clamp(iterations, 1, _bloom.maxIterations.value);

            // Setup
            using (new ProfilingScope(ProfilingSampler.Get(UltraRPProfileId.RG_BloomSetup)))
            {
                // Pre-filtering parameters
                float clamp = _bloom.clamp.value;
                float threshold = Mathf.GammaToLinearSpace(_bloom.threshold.value);
                float thresholdKnee = threshold * 0.5f; // Hardcoded soft knee

                // Material setup
                float scatter = Mathf.Lerp(0.05f, 0.95f, _bloom.scatter.value);

                BloomMaterialParams bloomParams = new BloomMaterialParams();
                bloomParams.parameters = new Vector4(scatter, clamp, threshold, thresholdKnee);
                bloomParams.highQualityFiltering = _bloom.highQualityFiltering.value;
                bloomParams.enableAlphaOutput = enableAlphaOutput;

                // Setting keywords can be somewhat expensive on low-end platforms.
                // Previous params are cached to avoid setting the same keywords every frame.
                var material = _bloomMat;
                bool bloomParamsDirty = !_bloomParamsPrev.Equals(ref bloomParams);
                bool isParamsPropertySet = material.HasProperty(ShaderConstants._Params);
                if (bloomParamsDirty || !isParamsPropertySet)
                {
                    material.SetVector(ShaderConstants._Params, bloomParams.parameters);
                    CoreUtils.SetKeyword(material, ShaderKeywordStrings.BloomHQ, bloomParams.highQualityFiltering);
                    CoreUtils.SetKeyword(material, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, bloomParams.enableAlphaOutput);

                    // These materials are duplicate just to allow different bloom blits to use different textures.
                    for (uint i = 0; i < k_maxPyramidSize; ++i)
                    {
                        var materialPyramid = _bloomUpsampleMats[i];
                        materialPyramid.SetVector(ShaderConstants._Params, bloomParams.parameters);
                        CoreUtils.SetKeyword(materialPyramid, ShaderKeywordStrings.BloomHQ, bloomParams.highQualityFiltering);
                        CoreUtils.SetKeyword(materialPyramid, ShaderKeywordStrings._ENABLE_ALPHA_OUTPUT, bloomParams.enableAlphaOutput);
                    }

                    _bloomParamsPrev = bloomParams;
                }

                // Create bloom mip pyramid textures
                {
                    var desc = PostProcessPass.GetCompatibleDescriptor(descriptor, tw, th, _postProcessPass.defaultColorFormat);
                    _bloomMipDownHlds[0] = UltraRenderer.CreateRenderGraphTexture(renderGraph, desc, _bloomMipDown[0].name, false, FilterMode.Bilinear);
                    _bloomMipUpHdls[0] = UltraRenderer.CreateRenderGraphTexture(renderGraph, desc, _bloomMipUp[0].name, false, FilterMode.Bilinear);

                    for (int i = 1; i < mipCount; i++)
                    {
                        tw = Mathf.Max(1, tw >> 1);
                        th = Mathf.Max(1, th >> 1);
                        ref TextureHandle mipDown = ref _bloomMipDownHlds[i];
                        ref TextureHandle mipUp = ref _bloomMipUpHdls[i];

                        desc.width = tw;
                        desc.height = th;

                        // NOTE: Reuse RTHandle names for TextureHandles
                        mipDown = UltraRenderer.CreateRenderGraphTexture(renderGraph, desc, _bloomMipDown[i].name, false, FilterMode.Bilinear);
                        mipUp = UltraRenderer.CreateRenderGraphTexture(renderGraph, desc, _bloomMipUp[i].name, false, FilterMode.Bilinear);
                    }
                }
            }

            using (var builder = renderGraph.AddUnsafePass<BloomPassData>("Blit Bloom Mipmaps", out var passData, ProfilingSampler.Get(UltraRPProfileId.Bloom)))
            {
                passData.mipCount = mipCount;
                passData.material = _bloomMat;
                passData.upsampleMaterials = _bloomUpsampleMats;
                passData.sourceTexture = source;
                passData.bloomMipDown = _bloomMipDownHlds;
                passData.bloomMipUp = _bloomMipUpHdls;

                // TODO RENDERGRAPH: properly setup dependencies between passes
                builder.AllowPassCulling(false);

                builder.UseTexture(source, AccessFlags.Read);
                for (int i = 0; i < mipCount; i++)
                {
                    builder.UseTexture(_bloomMipDownHlds[i], AccessFlags.ReadWrite);
                    builder.UseTexture(_bloomMipUpHdls[i], AccessFlags.ReadWrite);
                }

                builder.SetRenderFunc(static (BloomPassData data, UnsafeGraphContext context) =>
                {
                    // TODO: can't call BlitTexture with unsafe command buffer
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    var material = data.material;
                    int mipCount = data.mipCount;

                    var loadAction = RenderBufferLoadAction.DontCare;   // Blit - always write all pixels
                    var storeAction = RenderBufferStoreAction.Store;    // Blit - always read by then next Blit

                    // Prefilter
                    using (new ProfilingScope(cmd, ProfilingSampler.Get(UltraRPProfileId.RG_BloomPrefilter)))
                    {
                        Blitter.BlitCameraTexture(cmd, data.sourceTexture, data.bloomMipDown[0], loadAction, storeAction, material, 0);
                    }

                    // Downsample - gaussian pyramid
                    // Classic two pass gaussian blur - use mipUp as a temporary target
                    //   First pass does 2x downsampling + 9-tap gaussian
                    //   Second pass does 9-tap gaussian using a 5-tap filter + bilinear filtering
                    using (new ProfilingScope(cmd, ProfilingSampler.Get(UltraRPProfileId.RG_BloomDownsample)))
                    {
                        TextureHandle lastDown = data.bloomMipDown[0];
                        for (int i = 1; i < mipCount; i++)
                        {
                            TextureHandle mipDown = data.bloomMipDown[i];
                            TextureHandle mipUp = data.bloomMipUp[i];

                            Blitter.BlitCameraTexture(cmd, lastDown, mipUp, loadAction, storeAction, material, 1);
                            Blitter.BlitCameraTexture(cmd, mipUp, mipDown, loadAction, storeAction, material, 2);

                            lastDown = mipDown;
                        }
                    }

                    using (new ProfilingScope(cmd, ProfilingSampler.Get(UltraRPProfileId.RG_BloomUpsample)))
                    {
                        // Upsample (bilinear by default, HQ filtering does bicubic instead
                        for (int i = mipCount - 2; i >= 0; i--)
                        {
                            TextureHandle lowMip = (i == mipCount - 2) ? data.bloomMipDown[i + 1] : data.bloomMipUp[i + 1];
                            TextureHandle highMip = data.bloomMipDown[i];
                            TextureHandle dst = data.bloomMipUp[i];

                            // We need a separate material for each upsample pass because setting the low texture mip source
                            // gets overriden by the time the render func is executed.
                            // Material is a reference, so all the blits would share the same material state in the cmdbuf.
                            // NOTE: another option would be to use cmd.SetGlobalTexture().
                            var upMaterial = data.upsampleMaterials[i];
                            upMaterial.SetTexture(ShaderConstants._SourceTexLowMip, lowMip);

                            Blitter.BlitCameraTexture(cmd, highMip, dst, loadAction, storeAction, upMaterial, 3);
                        }
                    }
                });

                destination = mipCount > 1 ? passData.bloomMipUp[0] : passData.bloomMipDown[0];
            }
        }

        private void PrepareUberPostBloom(in TextureHandle bloomTexture)
        {
            var descriptor = _postProcessPass.descriptor;
            var tint = _bloom.tint.value.linear;
            var luma = ColorUtils.Luminance(tint);
            tint = luma > 0f ? tint * (1f / luma) : Color.white;
            var dirtTexture = _bloom.dirtTexture.value == null ? Texture2D.blackTexture : _bloom.dirtTexture.value;
            float dirtRatio = dirtTexture.width / (float)dirtTexture.height;
            float screenRatio = descriptor.width / (float)descriptor.height;
            var dirtScaleOffset = new Vector4(1f, 1f, 0f, 0f);
            if (dirtRatio > screenRatio)
            {
                dirtScaleOffset.x = screenRatio / dirtRatio;
                dirtScaleOffset.z = (1f - dirtScaleOffset.x) * 0.5f;
            }
            else if (screenRatio > dirtRatio)
            {
                dirtScaleOffset.y = dirtRatio / screenRatio;
                dirtScaleOffset.w = (1f - dirtScaleOffset.y) * 0.5f;
            }

            // Keep the texture and all material state with the pass that actually
            // samples Bloom. A setup-only pass does not extend its RG lifetime.
            _postProcessPass.bloomData = new UberSetupBloomPassData
            {
                bloomParams = new Vector4(_bloom.intensity.value, tint.r, tint.g, tint.b),
                dirtScaleOffset = dirtScaleOffset,
                dirtIntensity = _bloom.dirtIntensity.value,
                dirtTexture = dirtTexture,
                highQualityFilteringValue = _bloom.highQualityFiltering.value,
                bloomTexture = bloomTexture
            };
        }

        internal static void SetupUberMaterial(in UberSetupBloomPassData data, Material material)
        {
            // Material instances are shared; clear every Bloom mode for disabled
            // cameras and for changes between quality/dirt variants.
            bool enabled = data.bloomTexture.IsValid();
            CoreUtils.SetKeyword(material, ShaderKeywordStrings.BloomHQDirt, enabled && data.highQualityFilteringValue && data.dirtIntensity > 0f);
            CoreUtils.SetKeyword(material, ShaderKeywordStrings.BloomHQ, enabled && data.highQualityFilteringValue && data.dirtIntensity <= 0f);
            CoreUtils.SetKeyword(material, ShaderKeywordStrings.BloomLQDirt, enabled && !data.highQualityFilteringValue && data.dirtIntensity > 0f);
            CoreUtils.SetKeyword(material, ShaderKeywordStrings.BloomLQ, enabled && !data.highQualityFilteringValue && data.dirtIntensity <= 0f);
            if (!enabled)
                return;

            material.SetVector(ShaderConstants._Bloom_Params, data.bloomParams);
            material.SetVector(ShaderConstants._LensDirt_Params, data.dirtScaleOffset);
            material.SetFloat(ShaderConstants._LensDirt_Intensity, data.dirtIntensity);
            material.SetTexture(ShaderConstants._LensDirt_Texture, data.dirtTexture);
            material.SetTexture(ShaderConstants._Bloom_Texture, data.bloomTexture);
        }
    }
}
