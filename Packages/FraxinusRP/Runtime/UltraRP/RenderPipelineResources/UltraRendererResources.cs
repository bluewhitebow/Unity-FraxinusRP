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
    /// <summary>
    /// Class containing shader resources used in UltraRP.
    /// </summary>
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    [RenderingCategories.CategoryInfo(Name = "R: Ultra Renderer Shaders", Order = 1000), HideInInspector]
    public class UltraRendererResources : IRenderPipelineResources
    {
        /// <summary>Version of the resource. </summary>
        public int version => 0;
        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => true;

        [SerializeField]
        [ResourcePath("Shaders/Utils/CopyDepth.shader")]
        private Shader _copyDepthPS;

        /// <summary>
        /// Copy Depth shader.
        /// </summary>
        public Shader copyDepthPS
        {
            get => _copyDepthPS;
            set => this.SetValueAndNotify(ref _copyDepthPS, value, nameof(_copyDepthPS));
        }

        [SerializeField]
        [ResourcePath("Shaders/CameraMotionVectors.shader")]
        private Shader _cameraMotionVector;

        /// <summary>
        /// Camera Motion Vectors shader.
        /// </summary>
        public Shader cameraMotionVector
        {
            get => _cameraMotionVector;
            set => this.SetValueAndNotify(ref _cameraMotionVector, value, nameof(_cameraMotionVector));
        }

        [SerializeField]
        [ResourcePath("Shaders/Utils/StencilDeferred.shader")]
        private Shader _stencilDeferredPS;

        /// <summary>
        /// Stencil Deferred shader.
        /// </summary>
        public Shader stencilDeferredPS
        {
            get => _stencilDeferredPS;
            set => this.SetValueAndNotify(ref _stencilDeferredPS, value, nameof(_stencilDeferredPS));
        }

        [SerializeField]
        [ResourcePath("Shaders/Decal/DBufferClear.shader")]
        private Shader _decalDBufferClear;

        /// <summary>
        /// DBuffer Clear shader for decals.
        /// </summary>
        public Shader decalDBufferClear
        {
            get => _decalDBufferClear;
            set => this.SetValueAndNotify(ref _decalDBufferClear, value, nameof(_decalDBufferClear));
        }
    }
}
