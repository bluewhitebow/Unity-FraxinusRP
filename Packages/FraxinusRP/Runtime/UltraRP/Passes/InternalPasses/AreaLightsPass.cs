using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    // Recorded during the renderer's lighting setup, before any object draw.
    // This phase has no camera-depth dependency and needs no user graph node.
    public sealed class AreaLightsPass : BuiltinPass
    {
        readonly AreaLightManager _manager = new AreaLightManager();
        static readonly int s_Count = Shader.PropertyToID("_FraxinusAreaLightCount");
        static readonly int s_Ready = Shader.PropertyToID("_FraxinusLightingLutsReady");
        static readonly int s_Position = Shader.PropertyToID("_FraxinusAreaLightPositionRange");
        static readonly int s_Right = Shader.PropertyToID("_FraxinusAreaLightRightSize");
        static readonly int s_Up = Shader.PropertyToID("_FraxinusAreaLightUpSize");
        static readonly int s_Color = Shader.PropertyToID("_FraxinusAreaLightColor");
        static readonly int s_Layers = Shader.PropertyToID("_FraxinusAreaLightLayers");
        static readonly int s_Ltc = Shader.PropertyToID("_LtcData");

        sealed class PassData
        {
            // RenderGraph pools a distinct snapshot for each recorded pass. Never
            // capture a renderer-owned mutable array in a delayed render function.
            internal readonly AreaLightManager.Snapshot lights = new AreaLightManager.Snapshot();
            internal bool ready;
            internal bool areaLightsEnabled;
        }

        public AreaLightsPass() { }
        protected override void OnCameraCleanup(CommandBuffer cmd) { }
        protected override void OnDispose() { }

        internal void PrepareFrame(RenderGraph graph, ContextContainer frame)
        {
            var resources = frame.GetOrCreate<AreaLightFrameData>();
            resources.Reset();
            using (var builder = graph.AddRasterRenderPass<PassData>("Prepare Area Lights and FGD", out var pass))
            {
                pass.lights.count = 0;
                // Match the pipeline's build capability, including cameras with
                // zero area lights. Count-based keyword toggling would require
                // keeping an unnecessary OFF variant in enabled-only builds.
                pass.areaLightsEnabled = UltraRenderPipeline.asset?.supportsAreaLights ?? false;
                pass.ready = AreaLightSystem.instance.isAvailable && SystemInfo.graphicsShaderLevel >= 35;
                if (pass.ready)
                {
                    resources.ltc = AreaLightSystem.instance.ImportToRenderGraph(graph);
                    resources.fgd = PreIntegratedFGD.instance.ImportToRenderGraph(graph,
                        PreIntegratedFGD.FGDIndex.FGD_GGXAndDisneyDiffuse);
                    pass.ready = resources.ltc.IsValid() && resources.fgd.IsValid();
                    if (pass.ready)
                    {
                        builder.UseTexture(resources.ltc, AccessFlags.Read);
                        builder.UseTexture(resources.fgd, AccessFlags.Read);
                        builder.SetGlobalTextureAfterPass(resources.ltc, s_Ltc);
                        builder.SetGlobalTextureAfterPass(resources.fgd,
                            PreIntegratedFGD.ShaderConstants._PreIntegratedFGD_GGXDisneyDiffuse);
                        if (pass.areaLightsEnabled)
                            _manager.Prepare(frame.Get<CameraData>(), frame.Get<LightData>(), pass.lights);
                    }
                }
                resources.lightCount = pass.lights.count;
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    context.cmd.SetKeyword(ShaderGlobalKeywords.AreaLights, data.areaLightsEnabled);
                    context.cmd.SetGlobalInt(s_Ready, data.ready ? 1 : 0);
                    context.cmd.SetGlobalInt(s_Count, data.lights.count);
                    if (data.lights.count == 0)
                        return;
                    context.cmd.SetGlobalVectorArray(s_Position, data.lights.positionRange);
                    context.cmd.SetGlobalVectorArray(s_Right, data.lights.rightSize);
                    context.cmd.SetGlobalVectorArray(s_Up, data.lights.upSize);
                    context.cmd.SetGlobalVectorArray(s_Color, data.lights.color);
                    context.cmd.SetGlobalVectorArray(s_Layers, data.lights.layers);
                });
            }
        }

        internal static void UseLightingResources(IBaseRenderGraphBuilder builder, ContextContainer frame)
        {
            if (!frame.Contains<AreaLightFrameData>())
                return;
            var data = frame.Get<AreaLightFrameData>();
            if (data.ltc.IsValid()) builder.UseTexture(data.ltc, AccessFlags.Read);
            if (data.fgd.IsValid()) builder.UseTexture(data.fgd, AccessFlags.Read);
        }
    }
}
