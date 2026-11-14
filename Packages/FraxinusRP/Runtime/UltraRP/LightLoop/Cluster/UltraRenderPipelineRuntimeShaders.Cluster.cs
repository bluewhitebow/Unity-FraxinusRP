using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    partial class UltraRenderPipelineRuntimeShaders
    {
        #region Cluster

        /// <summary>
        /// GPU lights list compute shader.
        /// </summary>
        [SerializeField, ResourcePath("Shaders/Cluster/GPULightsClearLists.compute")]
        private ComputeShader _gpuLightsClearLists;

        public ComputeShader gpuLightsClearLists
        {
            get => _gpuLightsClearLists;
            set => this.SetValueAndNotify(ref _gpuLightsClearLists, value);
        }

        [SerializeField, ResourcePath("Shaders/Cluster/GPULightsCoarseCulling.compute")]
        private ComputeShader _gpuLightsCoarseCullingCS;

        public ComputeShader gpuLightsCoarseCullingCS
        {
            get => _gpuLightsCoarseCullingCS;
            set => this.SetValueAndNotify(ref _gpuLightsCoarseCullingCS, value);
        }

        [SerializeField, ResourcePath("Shaders/Cluster/GPULightsFPTL.compute")]
        private ComputeShader _gpuLightsFPTL;
        public ComputeShader gpuLightsFPTL
        {
            get => _gpuLightsFPTL;
            set => this.SetValueAndNotify(ref _gpuLightsFPTL, value);
        }

        [SerializeField, ResourcePath("Shaders/Cluster/GPULightsCluster.compute")]
        private ComputeShader _gpuLightsCluster;

        public ComputeShader gpuLightsCluster
        {
            get => _gpuLightsCluster;
            set => this.SetValueAndNotify(ref _gpuLightsCluster, value);
        }

        #endregion

        /// <summary>
        /// Deferred lighting compute shader.
        /// </summary>
        [SerializeField, ResourcePath("Shaders/Lighting/DeferredLit.compute")]
        private ComputeShader _deferredLightingCS;

        public ComputeShader deferredLightingCS
        {
            get => _deferredLightingCS;
            set => this.SetValueAndNotify(ref _deferredLightingCS, value);
        }

        [SerializeField, ResourcePath("Shaders/Lighting/ClusterDeferred.shader")]
        private Shader _clusterDeferredPS;

        public Shader clusterDeferredPS
        {
            get => _clusterDeferredPS;
            set => this.SetValueAndNotify(ref _clusterDeferredPS, value);
        }

        [SerializeField, ResourcePath("Shaders/Lighting/DeferredPlus.shader")]
        private Shader _deferredPlusPS;

        public Shader deferredPlusPS
        {
            get => _deferredPlusPS;
            set => this.SetValueAndNotify(ref _deferredPlusPS, value);
        }
    }
}
