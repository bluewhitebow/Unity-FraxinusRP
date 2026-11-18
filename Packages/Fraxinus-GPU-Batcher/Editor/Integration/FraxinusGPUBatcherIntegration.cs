#if GPUI_FRAXINUS
using FraxinusEngine.Rendering.Ultra;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher.Editor.Integration
{
    internal static class FraxinusGPUBatcherIntegration
    {
        [MenuItem("Tools/Fraxinus GPU Batcher/Check Fraxinus Integration")]
        private static void CheckIntegration()
        {
            if (GraphicsSettings.currentRenderPipeline is UltraRenderPipelineAsset pipeline)
                Debug.Log("Fraxinus GPU Batcher: camera rendering is registered automatically by the runtime scheduler. FraxinusRP owns HiZDepthPass and supplies camera Hi-Z history on request.", pipeline);
            else
                Debug.LogWarning("Fraxinus GPU Batcher requires a Fraxinus Ultra Render Pipeline asset under Project Settings > Graphics or Quality.");
        }
    }
}
#endif
