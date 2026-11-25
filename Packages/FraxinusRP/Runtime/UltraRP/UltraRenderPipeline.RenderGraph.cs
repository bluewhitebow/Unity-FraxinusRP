using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public sealed partial class UltraRenderPipeline
    {
        static void RecordRenderGraph(RenderGraph renderGraph, ScriptableRenderContext context, ScriptableRenderer renderer)
        {
            renderer.RecordRenderGraph(renderGraph, context);
        }

        static void RecordAndExecuteRenderGraph(RenderGraph renderGraph, ScriptableRenderContext context, ScriptableRenderer renderer, CommandBuffer cmd, Camera camera, string cameraName)
        {
  
            RenderGraphParameters rgParams = new RenderGraphParameters
            {
                executionName = cameraName,
                commandBuffer = cmd,
                scriptableRenderContext = context,
                currentFrameIndex = Time.frameCount,
            };

            renderGraph.BeginRecording(rgParams);
            RecordRenderGraph(renderGraph, context, renderer);
            renderGraph.EndRecordingAndExecute();
        }
    }
}
