using System;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_6000_0_OR_NEWER
using RenderingCategories = UnityEngine.Categorization;
#else
using RenderingCategories = UnityEngine.Rendering.Categorization;
#endif

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    [RenderingCategories.CategoryInfo(Name = "R: Debug Shaders", Order = 1000), HideInInspector]
    public class UltraPipelineDebugShaders : IRenderPipelineResources
    {
        public int version => 0;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => true;


        [SerializeField]
        [ResourcePath("Shaders/Debug/DebugReplacement.shader")]
        Shader _debugReplacementPS;

        /// <summary>
        /// Debug shader used to output interpolated vertex attributes.
        /// </summary>
        public Shader debugReplacementPS
        {
            get => _debugReplacementPS;
            set => this.SetValueAndNotify(ref _debugReplacementPS, value, nameof(_debugReplacementPS));
        }

        [SerializeField]
        [ResourcePath("Shaders/Debug/HDRDebugView.shader")]
        Shader _hdrDebugViewPS;

        /// <summary>
        /// Debug shader used to output HDR Chromacity mapping.
        /// </summary>
        public Shader hdrDebugViewPS
        {
            get => _hdrDebugViewPS;
            set => this.SetValueAndNotify(ref _hdrDebugViewPS, value, nameof(_hdrDebugViewPS));
        }

        [SerializeField]
        [ResourcePath("Shaders/Debug/ProbeVolumeSamplingDebugPositionNormal.compute")]
        ComputeShader _probeVolumeSamplingDebugComputeShader;

        public ComputeShader probeVolumeSamplingDebugComputeShader
        {
            get => _probeVolumeSamplingDebugComputeShader;
            set => this.SetValueAndNotify(ref _probeVolumeSamplingDebugComputeShader, value, nameof(_probeVolumeSamplingDebugComputeShader));
        }

        [SerializeField]
        [ResourcePath("Shaders/Debug/DebugViewTiles.shader")]
        Shader _tileClusterDebugPS;

        /// <summary>
        /// Debug shader used to show tile and cluster.
        /// </summary>
        public Shader tileClusterDebugPS
        {
            get => _tileClusterDebugPS;
            set => this.SetValueAndNotify(ref _tileClusterDebugPS, value, nameof(_tileClusterDebugPS));
        }

    }
}
