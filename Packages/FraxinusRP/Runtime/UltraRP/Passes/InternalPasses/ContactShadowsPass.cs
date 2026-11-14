using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    [InjectPass(typeof(RenderDrivenPass), InjectionPoint.After, scopeFilter = ContextScopeFilter.Main)]
    public sealed class ContactShadowsPass : InjectPass
    {
        static readonly ProfilingSampler s_Sampler = new ProfilingSampler("Contact Shadows");
        internal static readonly int enabledId = Shader.PropertyToID("_ContactShadowsEnabled");
        static readonly int s_Texture = Shader.PropertyToID("_ContactShadowTexture");
        static readonly int s_Depth = Shader.PropertyToID("_ContactDepthTexture");
        static readonly int s_Output = Shader.PropertyToID("_ContactShadowOutput");
        static readonly int s_Size = Shader.PropertyToID("_ContactShadowSize");
        static readonly int s_Projection = Shader.PropertyToID("_ContactProjection");
        static readonly int s_InverseProjection = Shader.PropertyToID("_ContactInverseProjection");
        static readonly int s_LightDirection = Shader.PropertyToID("_ContactLightDirectionVS");
        static readonly int s_Ray = Shader.PropertyToID("_ContactRayParameters");
        static readonly int s_Fade = Shader.PropertyToID("_ContactFadeParameters");
        static readonly int s_Sampling = Shader.PropertyToID("_ContactSamplingParameters");

        sealed class PassData
        {
            internal ComputeShader shader;
            internal int kernel, width, height;
            internal TextureHandle depth, output;
            internal Matrix4x4 projection;
            internal Vector4 lightDirection, ray, fade, sampling;
        }

        sealed class ResetPassData { }

        public ContactShadowsPass() { }

        protected override void OnCameraCleanup(CommandBuffer cmd) { }

        protected override void OnDispose() { }

        public override void ResetFrame(RenderGraph graph, ContextContainer frame)
        {
            var data = frame.GetOrCreate<ContactShadowsData>();
            data.Reset();
            frame.Get<ResourceData>().contactShadowsTexture = TextureHandle.nullHandle;
            ResetGlobals(graph);
        }

        public override bool PrepareFrame(RenderGraph graph, ContextContainer frame)
        {
            var data = frame.GetOrCreate<ContactShadowsData>();
            var camera = frame.Get<CameraData>();
            if (!IsCameraSupported(camera) || !HasActiveMainInjection(camera.renderer, frame))
                return false;

            var settings = VolumeManager.instance.stack.GetComponent<ContactShadows>();
            if (settings == null || !settings.IsActive() ||
                !RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>(out var shaders) ||
                shaders.contactShadowsCS == null || !shaders.contactShadowsCS.HasKernel("ContactShadows"))
                return false;

            var lights = frame.Get<LightData>();
            if (lights.mainLightIndex < 0 || lights.mainLightIndex >= lights.visibleLights.Length)
                return false;
            var light = lights.visibleLights[lights.mainLightIndex];
            if (light.lightType != LightType.Directional)
                return false;

            data.shader = shaders.contactShadowsCS;
            data.kernel = data.shader.FindKernel("ContactShadows");
            data.width = camera.cameraTargetDescriptor.width;
            data.height = camera.cameraTargetDescriptor.height;
            // The depth texture is an intermediate target. Capture its flipped GPU
            // projection and temporal jitter before any other context can change them.
            data.projection = camera.GetGPUProjectionMatrix(true);
            Vector3 directionWS = -light.localToWorldMatrix.GetColumn(2);
            data.lightDirection = camera.GetViewMatrix().MultiplyVector(directionWS).normalized;
            data.ray = new Vector4(settings.length.value, settings.bias.value, settings.thickness.value, settings.distanceScale.value);
            data.fade = new Vector4(settings.maxDistance.value, settings.fadeDistance.value, settings.opacity.value, camera.camera.nearClipPlane);
            data.sampling = new Vector4(settings.sampleCount.value,
                camera.IsTemporalJitterEnabled() ? Time.frameCount & 7 : 0, camera.camera.orthographic ? 1f : 0f, 0f);
            data.active = true;

            var rendering = frame.Get<RenderingData>();
            rendering.renderPassInputs.requiresDepthTexture = true;
            rendering.renderPassInputs.requiresDepthPrepass = true;
            rendering.renderPassInputs.requiresIntermediateTexture = true;
            rendering.renderPassInputs.requiresFullDepthPrepass = true;
            rendering.renderPassInputs.disableFramebufferFetch = true;
            return true;
        }

        bool HasActiveMainInjection(ScriptableRenderer renderer, ContextContainer frame)
        {
            if (renderer == null)
                return false;

            // Match the graph's structural scopes. Subcontexts can disable injection,
            // and their depth targets are not supported by this main-camera effect.
            int contextDepth = 0;
            foreach (var node in renderer.sortedNodes)
            {
                if (node is ContextBeginNode)
                {
                    ++contextDepth;
                    continue;
                }
                if (node is ContextEndNode)
                {
                    contextDepth = Mathf.Max(0, contextDepth - 1);
                    continue;
                }

                if (contextDepth != 0 || !(node is RenderDrivenPass driven) ||
                    !driven.active || driven.computeOrder < 0 || !driven.canProcess ||
                    !driven.SupportedInCurrentFrame(frame))
                    continue;

                var injectedPasses = driven.afterRenderingPasses;
                for (int i = 0; i < injectedPasses.Count; ++i)
                    if (ReferenceEquals(injectedPasses[i], this))
                        return true;
            }
            return false;
        }

        static bool IsCameraSupported(CameraData camera)
        {
            // Child targets and XR have different projection/depth conventions.
            // Keep them neutral until they have a dedicated implementation.
            if (camera == null || camera.camera == null || camera.isSubContext ||
                camera.renderType != CameraRenderType.Base ||
                UltraRenderer.IsOffscreenDepthTexture(camera) ||
                (camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView) ||
                camera.cameraTargetDescriptor.dimension != TextureDimension.Tex2D ||
                camera.cameraTargetDescriptor.width <= 0 || camera.cameraTargetDescriptor.height <= 0 ||
                camera.cameraTargetDescriptor.useDynamicScale ||
                camera.camera.stereoEnabled || !SystemInfo.supportsComputeShaders ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, FormatUsage.LoadStore) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, FormatUsage.Sample))
                return false;

            return true;
        }

        static void ResetGlobals(RenderGraph graph)
        {
            using (var builder = graph.AddRasterRenderPass<ResetPassData>("Reset Contact Shadows", out _))
            {
                builder.AllowGlobalStateModification(true);
                builder.SetGlobalTextureAfterPass(graph.defaultResources.whiteTexture, s_Texture);
                builder.SetRenderFunc((ResetPassData data, RasterGraphContext context) =>
                    context.cmd.SetGlobalFloat(enabledId, 0f));
            }
        }

        public override void Execute(RenderGraph graph, ScriptableRenderContext renderContext, ContextContainer frame)
        {
            // Child scopes can fall back to main frame data for unregistered types.
            // Never consume the main camera's snapshot or output in a child target.
            if (frame.Get<CameraData>().isSubContext || !frame.Contains<ContactShadowsData>())
                return;
            var prepared = frame.Get<ContactShadowsData>();
            var rendering = frame.Get<RenderingData>();
            var resources = frame.Get<ResourceData>();
            // After RenderDriven, a requested full prepass has also published its
            // depth texture, either directly or through the scheduled depth copy.
            if (!prepared.active || !rendering.requiresDepthPrepass || !rendering.renderPassInputs.requiresFullDepthPrepass ||
                resources.contactShadowsTexture.IsValid() || !resources.cameraDepthTexture.IsValid())
                return;

            var output = graph.CreateTexture(new TextureDesc(prepared.width, prepared.height)
            {
                name = "Main Light Contact Shadows",
                colorFormat = GraphicsFormat.R16_SFloat,
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                clearBuffer = false
            });
            resources.contactShadowsTexture = output;

            using (var builder = graph.AddComputePass<PassData>("Contact Shadows", out var data, s_Sampler))
            {
                data.shader = prepared.shader;
                data.kernel = prepared.kernel;
                data.width = prepared.width;
                data.height = prepared.height;
                data.depth = resources.cameraDepthTexture;
                data.output = output;
                data.projection = prepared.projection;
                data.lightDirection = prepared.lightDirection;
                data.ray = prepared.ray;
                data.fade = prepared.fade;
                data.sampling = prepared.sampling;
                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.UseTexture(output, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(output, s_Texture);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData pass, ComputeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    cmd.SetComputeTextureParam(pass.shader, pass.kernel, s_Depth, pass.depth);
                    cmd.SetComputeTextureParam(pass.shader, pass.kernel, s_Output, pass.output);
                    cmd.SetComputeVectorParam(pass.shader, s_Size, new Vector4(pass.width, pass.height, 1f / pass.width, 1f / pass.height));
                    cmd.SetComputeMatrixParam(pass.shader, s_Projection, pass.projection);
                    cmd.SetComputeMatrixParam(pass.shader, s_InverseProjection, pass.projection.inverse);
                    cmd.SetComputeVectorParam(pass.shader, s_LightDirection, pass.lightDirection);
                    cmd.SetComputeVectorParam(pass.shader, s_Ray, pass.ray);
                    cmd.SetComputeVectorParam(pass.shader, s_Fade, pass.fade);
                    cmd.SetComputeVectorParam(pass.shader, s_Sampling, pass.sampling);
                    cmd.DispatchCompute(pass.shader, pass.kernel, (pass.width + 7) / 8, (pass.height + 7) / 8, 1);
                });
            }
        }
    }
}
