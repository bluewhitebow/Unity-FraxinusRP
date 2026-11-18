using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>Scene-referred histogram exposure. Scheduled before all optional post effects.</summary>
    public sealed class AutoExposurePass : BasePostPass
    {
        const int k_HistogramBins = 128;
        const int k_MaxMeteringDimension = 512;
        const GraphicsFormat k_ExposureFormat = GraphicsFormat.R32G32_SFloat;

        static readonly bool s_HardwareSupported = SystemInfo.supportsComputeShaders &&
            SystemInfo.IsFormatSupported(k_ExposureFormat, FormatUsage.LoadStore) &&
            SystemInfo.IsFormatSupported(k_ExposureFormat, FormatUsage.Sample);

        static readonly int s_SourceTexture = Shader.PropertyToID("_SourceTexture");
        static readonly int s_SourceSize = Shader.PropertyToID("_SourceSize");
        static readonly int s_SourceIsGamma = Shader.PropertyToID("_SourceIsGamma");
        static readonly int s_Histogram = Shader.PropertyToID("_Histogram");
        static readonly int s_PreviousExposure = Shader.PropertyToID("_PreviousExposure");
        static readonly int s_ExposureOutput = Shader.PropertyToID("_ExposureOutput");
        static readonly int s_MeteringParams = Shader.PropertyToID("_MeteringParams");
        static readonly int s_AdaptationParams = Shader.PropertyToID("_AdaptationParams");
        static readonly int s_Compensation = Shader.PropertyToID("_Compensation");
        static readonly int s_AutoExposureTexture = Shader.PropertyToID("_AutoExposureTexture");

        sealed class CameraExposure
        {
            internal Camera camera;
            internal readonly RTHandle[] textures = new RTHandle[2];
            internal int currentIndex;
            internal int lastFrame = -1;
            internal double lastUsedTime;
            internal double lastUpdateTime;
            internal Vector2Int sourceSize;
            internal bool initialized;

            internal CameraExposure(Camera owner)
            {
                camera = owner;
                for (int i = 0; i < textures.Length; ++i)
                    textures[i] = RTHandles.Alloc(1, 1, colorFormat: k_ExposureFormat,
                        filterMode: FilterMode.Point, enableRandomWrite: true,
                        name: "Auto Exposure " + owner.name + " " + i);
            }

            internal void Release()
            {
                foreach (var texture in textures)
                    texture?.Release();
            }
        }

        sealed class HistogramPassData
        {
            internal ComputeShader shader;
            internal int clearKernel, buildKernel;
            internal TextureHandle source;
            internal BufferHandle histogram;
            internal Vector4 sourceSize;
            internal int sourceIsGamma;
        }

        sealed class AdaptationPassData
        {
            internal ComputeShader shader;
            internal int kernel;
            internal BufferHandle histogram;
            internal TextureHandle previous, output;
            internal Vector4 metering, adaptation;
            internal float compensation;
        }

        sealed class ApplyPassData
        {
            internal TextureHandle source, exposure;
            internal Material material;
        }

        readonly Dictionary<int, CameraExposure> _histories = new Dictionary<int, CameraExposure>();
        readonly List<int> _retired = new List<int>();
        AutoExposure _settings;
        ComputeShader _shader;
        Material _applyMaterial;
        int _clearKernel, _buildKernel, _reduceKernel;

        public override bool Setup(bool enable, VolumeStack stack)
        {
            RetireUnusedCameras();
            _settings = stack.GetComponent<AutoExposure>();
            if (!enable || _settings == null || !_settings.IsActive() || !s_HardwareSupported)
                return false;

            var shaders = _postProcessPass.graph.postProcessData?.shaders;
            if (shaders == null || shaders.autoExposureCS == null || shaders.autoExposurePS == null)
                return false;

            if (_shader != shaders.autoExposureCS)
            {
                _shader = shaders.autoExposureCS;
                _clearKernel = _shader.FindKernel("ClearHistogram");
                _buildKernel = _shader.FindKernel("BuildHistogram");
                _reduceKernel = _shader.FindKernel("ReduceHistogram");
            }
            _applyMaterial = _postProcessPass.graph.materialLib.GetMaterial("autoExposurePS");
            return _applyMaterial != null;
        }

        public override TextureHandle Render(RenderGraph renderGraph, ContextContainer frameData,
            in TextureHandle activeCameraColor)
        {
            var cameraData = frameData.Get<CameraData>();
            var descriptor = _postProcessPass.descriptor;
            // A nested post node must not overwrite its parent's metering history.
            // An overlay can already contain its base camera's exposed/tonemapped
            // image, so it must not meter that image as scene-referred color again.
            if (cameraData.camera == null || cameraData.isSubContext || cameraData.camera.stereoEnabled ||
                cameraData.renderType != CameraRenderType.Base ||
                cameraData.isPreviewCamera || descriptor.dimension != TextureDimension.Tex2D ||
                descriptor.volumeDepth != 1 || descriptor.useDynamicScale ||
                descriptor.width <= 0 || descriptor.height <= 0)
                return activeCameraColor;

            int cameraId = cameraData.camera.GetInstanceID();
            if (!_histories.TryGetValue(cameraId, out var history) || history.camera != cameraData.camera)
            {
                history?.Release();
                history = new CameraExposure(cameraData.camera);
                _histories[cameraId] = history;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            int frame = Time.frameCount;
            var size = new Vector2Int(descriptor.width, descriptor.height);
            bool reset = !history.initialized || cameraData.resetHistory || history.sourceSize != size ||
                         now - history.lastUpdateTime > 1.0 ||
                         (Application.isPlaying && history.lastFrame < frame - 1);

            // A camera may be submitted more than once in a frame. Apply its exposure
            // to each submission, but do not advance adaptation multiple times.
            if (!Application.isPlaying || history.lastFrame != frame || reset)
            {
                float deltaTime = history.initialized ? (float)(now - history.lastUpdateTime) : 0f;
                deltaTime = Mathf.Clamp(deltaTime, 0f, 1f);
                int outputIndex = 1 - history.currentIndex;
                var previous = renderGraph.ImportTexture(history.textures[history.currentIndex]);
                var output = renderGraph.ImportTexture(history.textures[outputIndex]);
                RecordMetering(renderGraph, activeCameraColor, previous, output, size, deltaTime, reset);
                history.currentIndex = outputIndex;
                history.lastFrame = frame;
                history.lastUpdateTime = now;
                history.initialized = true;
                history.sourceSize = size;
            }
            history.lastUsedTime = now;

            var exposure = renderGraph.ImportTexture(history.textures[history.currentIndex]);
            var destinationDesc = PostProcessPass.GetCompatibleDescriptor(descriptor,
                descriptor.width, descriptor.height, descriptor.graphicsFormat, GraphicsFormat.None);
            var destination = UltraRenderer.CreateRenderGraphTexture(renderGraph, destinationDesc,
                "Auto Exposure Color", false, FilterMode.Bilinear);

            using (var builder = renderGraph.AddRasterRenderPass<ApplyPassData>(
                "Apply Auto Exposure", out var passData, _profilingSampler))
            {
                passData.source = activeCameraColor;
                passData.exposure = exposure;
                passData.material = _applyMaterial;
                builder.UseTexture(passData.source, AccessFlags.Read);
                builder.UseTexture(exposure, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (ApplyPassData data, RasterGraphContext context) =>
                {
                    RTHandle source = data.source;
                    // Bind in command order; the material is shared across cameras.
                    context.cmd.SetGlobalTexture(s_AutoExposureTexture, data.exposure);
                    Vector2 scale = source.useScaling
                        ? new Vector2(source.rtHandleProperties.rtHandleScale.x, source.rtHandleProperties.rtHandleScale.y)
                        : Vector2.one;
                    Blitter.BlitTexture(context.cmd, source, scale, data.material, 0);
                });
            }
            return destination;
        }

        internal void InvalidateHistory(Camera camera)
        {
            if (camera != null && _histories.TryGetValue(camera.GetInstanceID(), out var history))
            {
                history.initialized = false;
                history.lastFrame = -1;
            }
        }

        void RecordMetering(RenderGraph renderGraph, TextureHandle source, TextureHandle previous,
            TextureHandle output, Vector2Int size, float deltaTime, bool reset)
        {
            float scale = Mathf.Min(0.5f, (float)k_MaxMeteringDimension / Mathf.Max(size.x, size.y));
            int width = Mathf.Max(1, Mathf.CeilToInt(size.x * scale));
            int height = Mathf.Max(1, Mathf.CeilToInt(size.y * scale));
            var histogram = renderGraph.CreateBuffer(new BufferDesc(k_HistogramBins, sizeof(uint))
                { name = "Auto Exposure Histogram" });

            using (var builder = renderGraph.AddComputePass<HistogramPassData>(
                "Auto Exposure Histogram", out var passData, _profilingSampler))
            {
                passData.shader = _shader;
                passData.clearKernel = _clearKernel;
                passData.buildKernel = _buildKernel;
                passData.source = source;
                passData.histogram = histogram;
                passData.sourceSize = new Vector4(size.x, size.y, width, height);
                passData.sourceIsGamma = QualitySettings.activeColorSpace == ColorSpace.Gamma ? 1 : 0;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseBuffer(histogram, AccessFlags.ReadWrite);
                builder.SetRenderFunc(static (HistogramPassData data, ComputeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    cmd.SetComputeBufferParam(data.shader, data.clearKernel, s_Histogram, data.histogram);
                    cmd.DispatchCompute(data.shader, data.clearKernel, 1, 1, 1);
                    cmd.SetComputeVectorParam(data.shader, s_SourceSize, data.sourceSize);
                    cmd.SetComputeIntParam(data.shader, s_SourceIsGamma, data.sourceIsGamma);
                    cmd.SetComputeTextureParam(data.shader, data.buildKernel, s_SourceTexture, data.source);
                    cmd.SetComputeBufferParam(data.shader, data.buildKernel, s_Histogram, data.histogram);
                    cmd.DispatchCompute(data.shader, data.buildKernel,
                        ((int)data.sourceSize.z + 15) / 16, ((int)data.sourceSize.w + 15) / 16, 1);
                });
            }

            using (var builder = renderGraph.AddComputePass<AdaptationPassData>(
                "Auto Exposure Adaptation", out var passData, _profilingSampler))
            {
                float low = Mathf.Min(_settings.lowPercent.value, _settings.highPercent.value) * 0.01f;
                float high = Mathf.Max(_settings.lowPercent.value, _settings.highPercent.value) * 0.01f;
                low = Mathf.Min(low, 0.99f);
                high = Mathf.Max(high, low + 0.01f);
                passData.shader = _shader;
                passData.kernel = _reduceKernel;
                passData.histogram = histogram;
                passData.previous = previous;
                passData.output = output;
                passData.metering = new Vector4(low, high,
                    Mathf.Min(_settings.minEV.value, _settings.maxEV.value),
                    Mathf.Max(_settings.minEV.value, _settings.maxEV.value));
                passData.adaptation = new Vector4(_settings.speedDarkToLight.value,
                    _settings.speedLightToDark.value, deltaTime, reset ? 1f : 0f);
                passData.compensation = _settings.compensation.value;
                builder.UseBuffer(histogram, AccessFlags.Read);
                builder.UseTexture(previous, AccessFlags.Read);
                builder.UseTexture(output, AccessFlags.Write);
                // The persistent exposure is also consumed on subsequent frames.
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (AdaptationPassData data, ComputeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    cmd.SetComputeVectorParam(data.shader, s_MeteringParams, data.metering);
                    cmd.SetComputeVectorParam(data.shader, s_AdaptationParams, data.adaptation);
                    cmd.SetComputeFloatParam(data.shader, s_Compensation, data.compensation);
                    cmd.SetComputeBufferParam(data.shader, data.kernel, s_Histogram, data.histogram);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, s_PreviousExposure, data.previous);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, s_ExposureOutput, data.output);
                    cmd.DispatchCompute(data.shader, data.kernel, 1, 1, 1);
                });
            }
        }

        void RetireUnusedCameras()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            _retired.Clear();
            foreach (var pair in _histories)
                if (pair.Value.camera == null || now - pair.Value.lastUsedTime > 60.0)
                    _retired.Add(pair.Key);
            foreach (int id in _retired)
            {
                _histories[id].Release();
                _histories.Remove(id);
            }
        }

        protected override void OnDispose()
        {
            foreach (var history in _histories.Values)
                history.Release();
            _histories.Clear();
            _retired.Clear();
        }
    }
}
