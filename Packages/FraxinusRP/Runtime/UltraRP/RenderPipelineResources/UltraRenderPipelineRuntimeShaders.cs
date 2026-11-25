using System;
using UnityEngine;
using UnityEngine.Rendering;
using System.Reflection;

#if UNITY_6000_0_OR_NEWER
using RenderingCategories = UnityEngine.Categorization;
#else
using RenderingCategories = UnityEngine.Rendering.Categorization;
#endif

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable]
    [SupportedOnRenderPipeline(typeof(UltraRenderPipelineAsset))]
    [RenderingCategories.CategoryInfo(Name = "R: Runtime Shaders", Order = 1000), HideInInspector]
    public partial class UltraRenderPipelineRuntimeShaders : IRenderPipelineResources
    {
        [SerializeField][HideInInspector] private int _version = 0;

        public int version => _version;

        bool IRenderPipelineGraphicsSettings.isAvailableInPlayerBuild => true;


        [SerializeField, ResourcePath("Shaders/Utils/FallbackError.shader")]
        Shader _fallbackErrorShader;

        public Shader fallbackErrorShader
        {
            get => _fallbackErrorShader;
            set => this.SetValueAndNotify(ref _fallbackErrorShader, value, nameof(_fallbackErrorShader));
        }


        [SerializeField]
        [ResourcePath("Shaders/Utils/CoreBlit.shader")]
        Shader _coreBlitPS;

        /// <summary>
        /// Core Blit shader.
        /// </summary>
        public Shader coreBlitPS
        {
            get => _coreBlitPS;
            set => this.SetValueAndNotify(ref _coreBlitPS, value, nameof(_coreBlitPS));
        }

        [SerializeField]
        [ResourcePath("Shaders/Utils/BlitHDROverlay.shader")]
        Shader _blitHDROverlay;

        public Shader blitHDROverlay
        {
            get => _blitHDROverlay;
            set => this.SetValueAndNotify(ref _blitHDROverlay, value, nameof(_blitHDROverlay));
        }

        [SerializeField]
        [ResourcePath("Shaders/Utils/CoreBlitColorAndDepth.shader")]
        Shader _coreBlitColorAndDepthPS;

        /// <summary>
        /// Core Blit Color And Depth shader.
        /// </summary>
        public Shader coreBlitColorAndDepthPS
        {
            get => _coreBlitColorAndDepthPS;
            set => this.SetValueAndNotify(ref _coreBlitColorAndDepthPS, value, nameof(_coreBlitColorAndDepthPS));
        }

        [SerializeField]
        [ResourcePath("Shaders/IBLFilter/GGXConvolve.shader")]
        Shader _ggxConvolvePS;

        /// <summary>
        /// GGX Convolve shader for IBL filtering.
        /// </summary>
        public Shader ggxConvolvePS
        {
            get => _ggxConvolvePS;
            set => this.SetValueAndNotify(ref _ggxConvolvePS, value, nameof(_ggxConvolvePS));
        }

        [SerializeField]
        [ResourcePath("Shaders/IBLFilter/ComputeGgxIblSampleData.compute")]
        ComputeShader _computeGgxIblSampleDataCS;

        /// <summary>
        /// Compute GGX IBL Sample Data compute shader.
        /// </summary>
        public ComputeShader computeGgxIblSampleDataCS
        {
            get => _computeGgxIblSampleDataCS;
            set => this.SetValueAndNotify(ref _computeGgxIblSampleDataCS, value, nameof(_computeGgxIblSampleDataCS));
        }

        [SerializeField]
        [ResourcePath("Shaders/IBLFilter/BuildProbabilityTables.compute")]
        ComputeShader _buildProbabilityTablesCS;

        /// <summary>
        /// Build Probability Tables compute shader for IBL filtering.
        /// </summary>
        public ComputeShader buildProbabilityTablesCS
        {
            get => _buildProbabilityTablesCS;
            set => this.SetValueAndNotify(ref _buildProbabilityTablesCS, value, nameof(_buildProbabilityTablesCS));
        }

        [SerializeField]
        [ResourcePath("Shaders/PreIntegratedFGD/PreIntegratedFGD_GGXDisneyDiffuse.shader")]
        Shader _preIntegratedFGD_GGXDisneyDiffusePS;

        /// <summary>
        /// Pre-Integrated FGD shader for GGX and Disney Diffuse.
        /// </summary>
        public Shader preIntegratedFGD_GGXDisneyDiffusePS
        {
            get => _preIntegratedFGD_GGXDisneyDiffusePS;
            set => this.SetValueAndNotify(ref _preIntegratedFGD_GGXDisneyDiffusePS, value, nameof(_preIntegratedFGD_GGXDisneyDiffusePS));
        }

        [SerializeField]
        [ResourcePath("Shaders/PreIntegratedFGD/PreIntegratedFGD_CharlieFabricLambert.shader")]
        Shader _preIntegratedFGD_CharlieFabricLambertPS;

        /// <summary>
        /// Pre-Integrated FGD shader for Charlie and Fabric Lambert.
        /// </summary>
        public Shader preIntegratedFGD_CharlieFabricLambertPS
        {
            get => _preIntegratedFGD_CharlieFabricLambertPS;
            set => this.SetValueAndNotify(ref _preIntegratedFGD_CharlieFabricLambertPS, value, nameof(_preIntegratedFGD_CharlieFabricLambertPS));
        }

#if UNITY_EDITOR
        [SerializeField]
        [ResourcePath("Shaders/GlobalIllumination/PRT/ProbeSHDebug.shader")]
        Shader _probeSHDebugPS;

        /// <summary>
        /// PRT Probe SH Debug visualization shader (Editor only)
        /// </summary>
        public Shader probeSHDebugPS
        {
            get => _probeSHDebugPS;
            set => this.SetValueAndNotify(ref _probeSHDebugPS, value, nameof(_probeSHDebugPS));
        }
#endif

        [SerializeField]
        [ResourcePath("Shaders/GlobalIllumination/PRT/PRTGBuffer.shader")]
        Shader _prtGBufferPS;

        /// <summary>
        /// PRT GBuffer shader for baking.
        /// </summary>
        public Shader prtGBufferPS
        {
            get => _prtGBufferPS;
            set => this.SetValueAndNotify(ref _prtGBufferPS, value, nameof(_prtGBufferPS));
        }

        [SerializeField]
        [ResourcePath("Shaders/GlobalIllumination/PRT/PRTSurfelSample.compute")]
        ComputeShader _prtSurfelSampleCS;

        /// <summary>
        /// PRT Surfel Sample compute shader.
        /// </summary>
        public ComputeShader prtSurfelSampleCS
        {
            get => _prtSurfelSampleCS;
            set => this.SetValueAndNotify(ref _prtSurfelSampleCS, value, nameof(_prtSurfelSampleCS));
        }

        [SerializeField]
        [ResourcePath("Shaders/GlobalIllumination/PRT/ReflectionProbeSample.compute")]
        ComputeShader _reflectionProbeSampleCS;

        /// <summary>
        /// Reflection Probe Sample compute shader.
        /// </summary>
        public ComputeShader reflectionProbeSampleCS
        {
            get => _reflectionProbeSampleCS;
            set => this.SetValueAndNotify(ref _reflectionProbeSampleCS, value, nameof(_reflectionProbeSampleCS));
        }

        [SerializeField]
        [ResourcePath("Shaders/GlobalIllumination/PRT/PRTBrickRelight.compute")]
        ComputeShader _prtBrickRelightCS;

        /// <summary>
        /// PRT Brick Relight compute shader
        /// </summary>
        public ComputeShader prtBrickRelightCS
        {
            get => _prtBrickRelightCS;
            set => this.SetValueAndNotify(ref _prtBrickRelightCS, value, nameof(_prtBrickRelightCS));
        }

        [SerializeField]
        [ResourcePath("Shaders/GlobalIllumination/PRT/PRTProbeRelight.compute")]
        ComputeShader _prtProbeRelightCS;

        /// <summary>
        /// PRT Probe Relight compute shader
        /// </summary>
        public ComputeShader prtProbeRelightCS
        {
            get => _prtProbeRelightCS;
            set => this.SetValueAndNotify(ref _prtProbeRelightCS, value, nameof(_prtProbeRelightCS));
        }

        [SerializeField, ResourcePath("Shaders/VirtualTexturing/DownsampleVTFeedback.compute")]
        private ComputeShader _vtFeedbackDownsample;
        public ComputeShader vtFeedbackDownsample
        {
            get => _vtFeedbackDownsample;
            set => this.SetValueAndNotify(ref _vtFeedbackDownsample, value);
        }


        /// <summary>
        /// Sky.
        /// </summary>
        [SerializeField, ResourcePath("Shaders/Sky/HDRISky.shader")]
        private Shader _hdriSkyPS;

        public Shader hdriSkyPS
        {
            get => _hdriSkyPS;
            set => this.SetValueAndNotify(ref _hdriSkyPS, value);
        }

        [SerializeField, ResourcePath("Shaders/Sky/GradientSky.shader")]
        private Shader _gradientSkyPS;

        public Shader gradientSkyPS
        {
            get => _gradientSkyPS;
            set => this.SetValueAndNotify(ref _gradientSkyPS, value);
        }

        [SerializeField, ResourcePath("Shaders/Sky/AmbientProbeConvolution.compute")]
        private ComputeShader _ambientProbeConvolutionCS;

        public ComputeShader ambientProbeConvolutionCS
        {
            get => _ambientProbeConvolutionCS;
            set => this.SetValueAndNotify(ref _ambientProbeConvolutionCS, value);
        }

#if UNITY_EDITOR
        public void EnsureShadersCompiled()
        {
            void CheckComputeShaderMessages(ComputeShader computeShader)
            {
                foreach (var message in UnityEditor.ShaderUtil.GetComputeShaderMessages(computeShader))
                {
                    if (message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
                    {
                        // Will be catched by the try in HDRenderPipelineAsset.CreatePipeline()
                        throw new System.Exception(System.String.Format(
                            "Compute Shader compilation error on platform {0} in file {1}:{2}: {3}{4}\n" +
                            "RenderPipeline will not run until the error is fixed.\n",
                            message.platform, message.file, message.line, message.message, message.messageDetails
                        ));
                    }
                }
            }

            // We iterate over all compute shader to verify if they are all compiled, if it's not the case then
            // we throw an exception to avoid allocating resources and crashing later on by using a null compute kernel.
            foreach (var field in GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(ComputeShader) && field.GetValue(this) is ComputeShader shader)
                    CheckComputeShaderMessages(shader);
            }
        }
#endif
    }
}
