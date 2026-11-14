using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.SceneManagement;
using SceneLightTracker = UnityEngine.Rendering.Universal.SceneLightTracker;

namespace FraxinusEngine.Rendering.Ultra
{
    // Camera lighting inputs and RenderGraph dependencies belong to UltraRP; probe
    // storage, compute dispatch and sampling remain in the Core APRT backend.
    [InjectPass(typeof(RenderDrivenPass), InjectionPoint.After, scopeFilter = ContextScopeFilter.Main)]
    public sealed class APRTRelightPass : InjectPass
    {
        static readonly ProfilingSampler s_Sampler = new ProfilingSampler("APRT Relight");
        static Light s_SkySun;
        static int s_SkySunSceneHandle = int.MinValue;
        static SceneLightTracker s_SceneLights;

        public APRTRelightPass() { }

        protected override void OnCameraCleanup(CommandBuffer cmd) { }

        // The pipeline owns the persistent sky cache and clears it through Cleanup.
        protected override void OnDispose() { }

        internal static void Cleanup()
        {
            s_SceneLights?.Dispose();
            s_SceneLights = null;
            s_SkySun = null;
            s_SkySunSceneHandle = int.MinValue;
        }

        // Share the Core-owned engine bridge with URP. It preserves native scene
        // ordering and refreshes the array only when light membership changes.
        static Light[] GetSceneLights() => (s_SceneLights ??= new SceneLightTracker()).GetLights();

        sealed class LightingResources : ContextItem
        {
            internal readonly List<BufferHandle> buffers = new List<BufferHandle>(32);
            public override void Reset() => buffers.Clear();
        }

        sealed class PassData
        {
            internal APRTProbeReferenceVolume volume;
            // RenderGraph pools PassData only after the graph is finished. Keep
            // recording storage here so pending cameras never share mutable lists.
            internal readonly List<APRTProbeReferenceVolume> contexts = new List<APRTProbeReferenceVolume>(4);
            internal readonly List<APRTProbeReferenceVolume> sources = new List<APRTProbeReferenceVolume>(4);
            internal readonly List<GraphicsBuffer> runtimeSH = new List<GraphicsBuffer>(4);
            internal readonly List<GraphicsBuffer> skyIrradianceSH = new List<GraphicsBuffer>(4);
            internal readonly List<APRTScenarioBlendSnapshot> lighting = new List<APRTScenarioBlendSnapshot>(4);
            internal readonly List<APRTRelightSettings> settings = new List<APRTRelightSettings>(4);
            internal readonly List<GraphicsBuffer> gpuBuffers = new List<GraphicsBuffer>(32);
            internal readonly HashSet<GraphicsBuffer> importedBuffers = new HashSet<GraphicsBuffer>();
            internal readonly HashSet<GraphicsBuffer> outputs = new HashSet<GraphicsBuffer>();
            internal readonly Vector4[] referenceColors = new Vector4[4], referenceDirections = new Vector4[4];
            internal readonly Vector4[] visibleColors = new Vector4[4], visibleDirections = new Vector4[4];
            internal readonly Vector4[] skyRadiance = new Vector4[9];
            internal int cameraId;
            internal bool relight;
            internal bool enableSampling;
            internal bool suppressDebug;

            internal void Reset()
            {
                contexts.Clear(); sources.Clear(); runtimeSH.Clear(); skyIrradianceSH.Clear();
                lighting.Clear(); settings.Clear(); gpuBuffers.Clear(); importedBuffers.Clear(); outputs.Clear();
            }

            internal void AddBuffer(GraphicsBuffer buffer)
            {
                if (importedBuffers.Add(buffer)) gpuBuffers.Add(buffer);
            }
        }

        internal static bool CanUpdate(CameraData cameraData)
        {
            if (cameraData == null || cameraData.isSubContext || cameraData.camera == null || cameraData.renderType != CameraRenderType.Base ||
                cameraData.cameraType == CameraType.Reflection || cameraData.cameraType == CameraType.Preview)
                return false;

            if (cameraData.renderer == null || !cameraData.renderer.graph.IsInjectPassEnabled(typeof(APRTRelightPass)))
                return false;

            var target = cameraData.camera.targetTexture;
            if (cameraData.cameraTargetDescriptor.dimension == TextureDimension.Cube ||
                cameraData.cameraTargetDescriptor.dimension == TextureDimension.CubeArray)
                return false;
            return target == null || (target.format != RenderTextureFormat.Depth &&
                target.dimension != TextureDimension.Cube && target.dimension != TextureDimension.CubeArray);
        }

        internal static bool CanPrepareSkyEnvironment(Camera camera)
        {
            if (camera == null || UltraRenderPipeline.asset == null || !UltraRenderPipeline.asset.supportAPRTV ||
                camera.cameraType == CameraType.Reflection || camera.cameraType == CameraType.Preview)
                return false;
            if (camera.TryGetComponent<UltraAdditionalCameraData>(out var additional) && additional.renderType != CameraRenderType.Base)
                return false;
            var target = camera.targetTexture;
            return target == null || (target.format != RenderTextureFormat.Depth &&
                target.dimension != TextureDimension.Cube && target.dimension != TextureDimension.CubeArray);
        }

        internal static APRTRelightSettings ResolveSettings(VolumeStack stack)
        {
            var set = APRTProbeReferenceVolume.instance.currentBakingSet;
            return ResolveSettings(stack, set);
        }

        internal static APRTRelightSettings ResolveSettings(VolumeStack stack, ProbeVolumeBakingSet set)
        {
            var data = set?.GetBakedData(APRTProbeReferenceVolumeBackend.backendId) as APRTBakedData;
            return APRTRelightSettings.Resolve(set, data?.lightingModel ?? APRTLightingModel.APRTV,
                stack?.GetComponent<APRTOptions>());
        }

        internal static void PrepareSkyEnvironment(Camera camera, ScriptableRenderer renderer)
        {
            if (!CanPrepareSkyEnvironment(camera)) return;
            var volume = APRTProbeReferenceVolume.instance;
            if (renderer == null || !renderer.graph.IsInjectPassEnabled(typeof(APRTRelightPass)))
            {
                // An enabled Overlay on another renderer must not revive this
                // disabled Base camera's result from a previous frame.
                volume.InvalidateCameraSampling();
                return;
            }
            var contexts = volume.GetRenderingBakingSetRuntimes(camera);
            Light sun = null;
            bool sunResolved = false;
            for (int i = 0; i < contexts.Count; ++i)
            {
                var context = contexts[i];
                var settings = ResolveSettings(VolumeManager.instance.stack, context.currentBakingSet);
                if (context.PrepareRelight(camera, settings) && !context.usesLightingScenarios)
                {
                    if (!sunResolved && context.hasSkyIrradianceTransfer && settings.skyIntensity > 0 &&
                        !float.IsInfinity(settings.skyIntensity) && RenderSettings.skybox != null)
                    { sun = FindSkySun(); sunResolved = true; }
                    context.UpdateSkyEnvironment(sun, settings, camera);
                }
            }
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext renderContext, ContextContainer frameData)
        {
            if (renderGraph == null || frameData == null ||
                !frameData.Contains<CameraData>() || frameData.Get<CameraData>().isSubContext)
                return;

            // The pipeline resets APRT globals before recording each camera. Only
            // publish dependencies when this main scope has a valid relight or
            // inherited Base result; child scopes share these same buffer handles.
            var resources = frameData.GetOrCreate<LightingResources>();
            resources.Reset();
            if (!frameData.Contains<LightData>() ||
                UltraRenderPipeline.asset == null || !UltraRenderPipeline.asset.supportAPRTV)
                return;

            var cameraData = frameData.Get<CameraData>();
            var volume = APRTProbeReferenceVolume.instance;
            bool eligible = CanUpdate(cameraData);
            var allContexts = volume.GetRenderingBakingSetRuntimes(cameraData.camera);
            var target = cameraData.camera != null ? cameraData.camera.targetTexture : null;
            bool overlay = cameraData.renderType == CameraRenderType.Overlay && cameraData.camera != null &&
                cameraData.cameraType != CameraType.Reflection && cameraData.cameraType != CameraType.Preview &&
                cameraData.cameraTargetDescriptor.dimension != TextureDimension.Cube &&
                cameraData.cameraTargetDescriptor.dimension != TextureDimension.CubeArray &&
                (target == null || (target.format != RenderTextureFormat.Depth &&
                    target.dimension != TextureDimension.Cube && target.dimension != TextureDimension.CubeArray));
            // An Overlay inherits only this stack's successful Base result. A
            // Base that has disabled GI must not expose an earlier camera's SH.
            bool inheritBase = overlay && cameraData.baseCamera != null &&
                volume.CanSampleForCamera(cameraData.baseCamera.GetInstanceID());
            using (var builder = renderGraph.AddUnsafePass<PassData>("APRT Relight", out var data, s_Sampler))
            {
                data.Reset();
                data.volume = volume;
                // Snapshot the current camera's stack and lighting before another
                // camera changes them. Only the graph-owned lists survive recording.
                bool referenceLightsReady = false, visibleLightsReady = false;
                if (eligible || inheritBase)
                {
                    for (int i = 0; i < allContexts.Count; ++i)
                    {
                        var runtime = allContexts[i];
                        var resolved = ResolveSettings(VolumeManager.instance.stack, runtime.currentBakingSet);
                        if (eligible && !runtime.PrepareRelight(cameraData.camera, resolved)) continue;
                        if (!eligible && !runtime.CanSampleForCamera(cameraData.baseCamera.GetInstanceID())) continue;
                        APRTScenarioBlendSnapshot lighting = null;
                        if (eligible)
                        {
                            Vector4[] colors = null, directions = null;
                            if (!runtime.usesLightingScenarios)
                            {
                                bool reference = runtime.lightingModel == APRTLightingModel.APRTV;
                                colors = reference ? data.referenceColors : data.visibleColors;
                                directions = reference ? data.referenceDirections : data.visibleDirections;
                                if (reference && !referenceLightsReady)
                                {
                                    CollectReferenceDirectionalLights(colors, directions);
                                    referenceLightsReady = true;
                                }
                                else if (!reference && !visibleLightsReady)
                                {
                                    CollectDirectionalLights(frameData.Get<LightData>(), colors, directions);
                                    visibleLightsReady = true;
                                }
                            }
                            runtime.CopySkyRadianceCoefficients(cameraData.camera, data.skyRadiance);
                            lighting = runtime.CaptureScenarioBlend(colors, directions, data.skyRadiance);
                            if (lighting == null) { runtime.InvalidateCameraSampling(); continue; }
                        }
                        data.contexts.Add(runtime); data.settings.Add(resolved); data.lighting.Add(lighting);
                    }
                }
                data.relight = eligible && data.contexts.Count > 0;
                // Freeze all resident sources for the frame, including sets disabled
                // by this camera's Volume. Later cameras can use a different subset.
                bool atlasReady = data.contexts.Count > 0 && volume.PrepareSamplingAtlas(allContexts);
                if (atlasReady)
                {
                    var sources = volume.preparedSamplingContexts;
                    for (int i = 0; i < sources.Count; ++i)
                    {
                        var runtime = sources[i];
                        data.sources.Add(runtime);
                        data.runtimeSH.Add(runtime.runtimeSHBuffer);
                        data.skyIrradianceSH.Add(runtime.runtimeSkyIrradianceBuffer);
                        foreach (var buffer in runtime.gpuBuffers) data.AddBuffer(buffer);
                        data.outputs.Add(runtime.runtimeSHBuffer); data.outputs.Add(runtime.runtimeSkyIrradianceBuffer);
                    }
                    foreach (var buffer in volume.samplingAtlasBuffers)
                    { data.AddBuffer(buffer); data.outputs.Add(buffer); }
                    volume.PinBakingSetRuntimesForFrame(data.sources);
                }
                int bufferCount = data.gpuBuffers.Count;
                data.cameraId = cameraData.camera != null ? cameraData.camera.GetInstanceID() : 0;
                data.enableSampling = atlasReady && bufferCount > 0;
                data.suppressDebug = !eligible && !inheritBase;
                for (int i = 0; i < bufferCount; ++i)
                {
                    var buffer = data.gpuBuffers[i];
                    var handle = renderGraph.ImportBuffer(buffer);
                    resources.buffers.Add(handle);
                    builder.UseBuffer(handle, data.outputs.Contains(buffer)
                        ? AccessFlags.ReadWrite : AccessFlags.Read);
                }
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData pass, UnsafeGraphContext context) =>
                {
                    // The callback must use the same allocation declared during recording.
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    bool sameAllocation = pass.enableSampling;
                    for (int i = 0; i < pass.sources.Count; ++i)
                        sameAllocation &= ReferenceEquals(pass.runtimeSH[i], pass.sources[i].runtimeSHBuffer) &&
                            ReferenceEquals(pass.skyIrradianceSH[i], pass.sources[i].runtimeSkyIrradianceBuffer);
                    if (sameAllocation)
                    {
                        if (pass.relight)
                            for (int i = 0; i < pass.contexts.Count; ++i)
                                pass.contexts[i].DispatchRelight(cmd, pass.lighting[i], pass.settings[i], pass.cameraId);
                        pass.volume.CopyAndBindSamplingAtlas(cmd, pass.contexts);
                    }
                    else pass.volume.BindPRVRuntimeResources(cmd, false);
                    if (pass.suppressDebug)
                        cmd.SetGlobalInt("_APRTDebugMode", 0);
                });
            }
        }

        internal static bool HasLightingResources(ContextContainer frameData) =>
            frameData != null && frameData.Contains<LightingResources>() &&
            frameData.Get<LightingResources>().buffers.Count > 0;

        internal static void UseLightingResources(IBaseRenderGraphBuilder builder, ContextContainer frameData)
        {
            if (builder == null || frameData == null || !frameData.Contains<LightingResources>())
                return;
            var buffers = frameData.Get<LightingResources>().buffers;
            foreach (var buffer in buffers)
                builder.UseBuffer(buffer, AccessFlags.Read);
        }

        static Light FindSkySun()
        {
            // The atmosphere remains a light source when direct sun illumination
            // is disabled. Keep its orientation; the private sky capture excludes
            // the sun disk and does not use ordinary ambient intensity.
            if (RenderSettings.sun != null) return RenderSettings.sun;
            int sceneHandle = SceneManager.GetActiveScene().handle;
            if (s_SkySunSceneHandle != sceneHandle)
            {
                s_SkySun = null;
                s_SkySunSceneHandle = sceneHandle;
            }
            if (s_SkySun != null) return s_SkySun;
            Light brightest = null, activeSceneSun = null;
            foreach (var light in GetSceneLights())
            {
                if (light == null || light.type != LightType.Directional) continue;
                if (brightest == null || light.intensity > brightest.intensity) brightest = light;
                if (light.gameObject.scene.handle == sceneHandle && (activeSceneSun == null || light.intensity > activeSceneSun.intensity))
                    activeSceneSun = light;
            }
            s_SkySun = activeSceneSun != null ? activeSceneSun : brightest;
            return s_SkySun;
        }

        static void CollectReferenceDirectionalLights(Vector4[] colors, Vector4[] directions)
        {
            System.Array.Clear(colors, 0, colors.Length);
            System.Array.Clear(directions, 0, directions.Length);
            // Preserve APRTV's scene-light ordering and radiometry for the
            // matching response, including its extra directional contribution.
            int count = 0;
            foreach (var light in GetSceneLights())
            {
                if (light == null || !light.isActiveAndEnabled || light.type != LightType.Directional)
                    continue;
                colors[count] = light.color.linear * light.intensity;
                colors[count].w = 1;
                Vector3 direction = -light.transform.forward;
                directions[count] = new Vector4(direction.x, direction.y, direction.z, 0);
                if (++count == colors.Length)
                    break;
            }
        }

        static void CollectDirectionalLights(LightData lights, Vector4[] colors, Vector4[] directions)
        {
            System.Array.Clear(colors, 0, colors.Length);
            System.Array.Clear(directions, 0, directions.Length);
            if (!lights.visibleLights.IsCreated)
                return;
            int count = 0;
            // Include the pipeline's chosen sun first, rather than relying on culling order.
            AddLight(lights.mainLightIndex);
            for (int i = 0; i < lights.visibleLights.Length && count < colors.Length; ++i)
                if (i != lights.mainLightIndex)
                    AddLight(i);

            void AddLight(int index)
            {
                if (index < 0 || index >= lights.visibleLights.Length || count >= colors.Length)
                    return;
                var light = lights.visibleLights[index];
                if (light.lightType != LightType.Directional ||
                    (light.light != null && IsBakedLight(light.light)))
                    return;
                Color color = light.finalColor;
                colors[count] = new Vector4(color.r, color.g, color.b, 1);
                Vector3 direction = -(Vector3)light.localToWorldMatrix.GetColumn(2);
                direction.Normalize();
                directions[count] = new Vector4(direction.x, direction.y, direction.z, 0);
                ++count;
            }
        }

        static bool IsBakedLight(Light light)
        {
#if UNITY_EDITOR
            return light.lightmapBakeType == LightmapBakeType.Baked;
#else
            return light.bakingOutput.lightmapBakeType == LightmapBakeType.Baked;
#endif
        }
    }
}
