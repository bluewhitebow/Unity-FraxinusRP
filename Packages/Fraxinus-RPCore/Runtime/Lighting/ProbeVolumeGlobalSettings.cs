using UnityEngine.Experimental.Rendering;
using System;

namespace UnityEngine.Rendering
{
    [Serializable]
    [SupportedOnRenderPipeline()]
    [Categorization.CategoryInfo(Name = "R: Adaptive Probe Volumes", Order = 1000), HideInInspector]
    class ProbeVolumeRuntimeResources : IRenderPipelineResources
    {
        public bool isAvailableInPlayerBuild => true;
        [SerializeField, HideInInspector]
        int m_Version = 1;

        public int version { get => m_Version; }

        [Header("Runtime")]
        [ResourcePath("Runtime/Lighting/ProbeVolume/ProbeVolumeBlendStates.compute")]
        public ComputeShader probeVolumeBlendStatesCS;
        [ResourcePath("Runtime/Lighting/ProbeVolume/ProbeVolumeUploadData.compute")]
        public ComputeShader probeVolumeUploadDataCS;
        [ResourcePath("Runtime/Lighting/ProbeVolume/ProbeVolumeUploadDataL2.compute")]
        public ComputeShader probeVolumeUploadDataL2CS;
    }

    [Serializable]
    [SupportedOnRenderPipeline()]
    [Categorization.CategoryInfo(Name = "R: Adaptive Probe Volumes", Order = 1000), HideInInspector]
    class ProbeVolumeDebugResources : IRenderPipelineResources
    {
        public bool isAvailableInPlayerBuild => true;
        [SerializeField, HideInInspector]
        int m_Version = 1;

        public int version { get => m_Version; }

        [Header("Debug")]
        [ResourcePath("Runtime/Debug/ProbeVolumeDebug.shader")]
        public Shader probeVolumeDebugShader;
        [ResourcePath("Runtime/Debug/ProbeVolumeFragmentationDebug.shader")]
        public Shader probeVolumeFragmentationDebugShader;
        [ResourcePath("Runtime/Debug/ProbeVolumeSamplingDebug.shader")]
        public Shader probeVolumeSamplingDebugShader;
        [ResourcePath("Runtime/Debug/ProbeVolumeOffsetDebug.shader")]
        public Shader probeVolumeOffsetDebugShader;
        [ResourcePath("Runtime/Debug/ProbeSamplingDebugMesh.fbx")]
        public Mesh probeSamplingDebugMesh;
        [ResourcePath("Runtime/Debug/ProbeVolumeNumbersDisplayTex.png")]
        public Texture2D numbersDisplayTex;
    }

    [Serializable]
    [SupportedOnRenderPipeline()]
    [Categorization.CategoryInfo(Name = "R: Adaptive Probe Volumes", Order = 1000), HideInInspector]
    class ProbeVolumeBakingResources : IRenderPipelineResources
    {
        public bool isAvailableInPlayerBuild => false;
        [SerializeField, HideInInspector]
        int m_Version = 1;

        public int version { get => m_Version; }

        [Header("Baking")]
        [ResourcePath("Editor/Lighting/APV/ProbeVolumeCellDilation.compute")]
        public ComputeShader dilationShader;
        [ResourcePath("Editor/Lighting/ProbeVolume/ProbeVolumeSubdivide.compute")]
        public ComputeShader subdivideSceneCS;
        [ResourcePath("Editor/Lighting/ProbeVolume/VoxelizeScene.shader")]
        public Shader voxelizeSceneShader;

        [ResourcePath("Editor/Lighting/APV/VirtualOffset/TraceVirtualOffset.urtshader")]
        public ComputeShader traceVirtualOffsetCS;
        [ResourcePath("Editor/Lighting/APV/VirtualOffset/TraceVirtualOffset.urtshader")]
        public RayTracingShader traceVirtualOffsetRT;

        [ResourcePath("Editor/Lighting/APV/DynamicGI/DynamicGISkyOcclusion.urtshader")]
        public ComputeShader skyOcclusionCS;
        [ResourcePath("Editor/Lighting/APV/DynamicGI/DynamicGISkyOcclusion.urtshader")]
        public RayTracingShader skyOcclusionRT;

        [ResourcePath("Editor/Lighting/APV/RenderingLayerMask/TraceRenderingLayerMask.urtshader")]
        public ComputeShader renderingLayerCS;
        [ResourcePath("Editor/Lighting/APV/RenderingLayerMask/TraceRenderingLayerMask.urtshader")]
        public RayTracingShader renderingLayerRT;
    }

    [Serializable]
    [SupportedOnRenderPipeline()]
    [Categorization.CategoryInfo(Name = "Adaptive Probe Volumes", Order = 20)]
    class ProbeVolumeGlobalSettings : IRenderPipelineGraphicsSettings
    {
        public bool isAvailableInPlayerBuild => true;
        [SerializeField, HideInInspector]
        int m_Version = 1;
        [SerializeField, Tooltip("Enabling this will make APV baked data assets compatible with Addressables and Asset Bundles. This will also make Disk Streaming unavailable. After changing this setting, a clean rebuild may be required for data assets to be included in Adressables and Asset Bundles.")]
        bool m_ProbeVolumeDisableStreamingAssets;

        public int version { get => m_Version; }

        public bool probeVolumeDisableStreamingAssets
        {
            get => m_ProbeVolumeDisableStreamingAssets;
            set => this.SetValueAndNotify(ref m_ProbeVolumeDisableStreamingAssets, value, nameof(m_ProbeVolumeDisableStreamingAssets));
        }
    }
}
