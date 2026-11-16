#if UNITY_6000_0_OR_NEWER
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public partial class UltraRenderPipelineAsset : IGPUResidentRenderPipeline
    {
        GPUResidentDrawerMode IGPUResidentRenderPipeline.gpuResidentDrawerMode
        {
            get => (GPUResidentDrawerMode)gpuResidentDrawerMode;
            set => gpuResidentDrawerMode = (UltraGPUResidentDrawerMode)value;
        }

        GPUResidentDrawerSettings IGPUResidentRenderPipeline.gpuResidentDrawerSettings => new()
        {
            mode = (GPUResidentDrawerMode)gpuResidentDrawerMode,
            enableOcclusionCulling = gpuResidentDrawerEnableOcclusionCullingInCameras,
            supportDitheringCrossFade = _enableLODCrossFade,
            allowInEditMode = true,
            smallMeshScreenPercentage = _smallMeshScreenPercentage,
#if UNITY_EDITOR
            pickingShader = Shader.Find("Hidden/Ultra Render Pipeline/BRGPicking"),
#endif
            errorShader = Shader.Find("Hidden/Ultra Render Pipeline/FallbackError"),
            loadingShader = Shader.Find("Hidden/Ultra Render Pipeline/FallbackLoading"),
        };

        public bool IsGPUResidentDrawerSupportedBySRP(out string message, out LogType severity)
        {
            message = string.Empty;
            severity = LogType.Warning;

            foreach (var rendererGraph in rendererGraphList)
            {
                if (rendererGraph is not UltraRendererGraph)
                {
                    message = $"{nameof(GPUResidentDrawer)} Disabled due to some configured Ultra RenderGraph not being {nameof(UltraRendererGraph)}.";
                    return false;
                }
            }

            return true;
        }
    }
}
#endif
