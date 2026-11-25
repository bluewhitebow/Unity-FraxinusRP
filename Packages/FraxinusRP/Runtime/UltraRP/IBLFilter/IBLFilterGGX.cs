using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    class IBLFilterGGX : IBLFilterBSDF
    {
        static readonly int k_planarReflectionFilterTex0ID = Shader.PropertyToID("_PlanarReflectionFilterTex0");
        static readonly int k_planarReflectionFilterTex1ID = Shader.PropertyToID("_PlanarReflectionFilterTex1");
        static readonly int k_planarReflectionFilterDepthTex0ID = Shader.PropertyToID("_PlanarReflectionFilterDepthTex0");
        static readonly int k_planarReflectionFilterDepthTex1ID = Shader.PropertyToID("_PlanarReflectionFilterDepthTex1");

        RenderTexture _ggxIblSampleData;
        int _ggxIblMaxSampleCount = 89;   // Width, mobile is 34 
        const int k_ggxIblMipCountMinusOne = 6;    // Height (UNITY_SPECCUBE_LOD_STEPS)

        ComputeShader _computeGgxIblSampleDataCS;
        int _computeGgxIblSampleDataKernel = -1;

        ComputeShader _buildProbabilityTablesCS;
        int _conditionalDensitiesKernel = -1;
        int _marginalRowDensitiesKernel = -1;

        //// Planar reflection filtering
        //ComputeShader _planarReflectionFilteringCS;
        //int _planarReflectionDepthConversionKernel = -1;
        //int _planarReflectionDownScaleKernel = -1;
        //int _planarReflectionFilteringKernel = -1;
        //const int k_defaultPlanarResolution = 512;
        
        // Intermediate variables
        Vector4 _currentScreenSize = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        MaterialPropertyBlock _materialPropertyBlock = new MaterialPropertyBlock();

        static IBLFilterGGX s_instance = null;

        internal static IBLFilterGGX instance
        {
            get
            {
                if (s_instance == null)
                    s_instance = new IBLFilterGGX();

                return s_instance;
            }
        }

        public IBLFilterGGX() : base()
        {
        }

        public bool IsIblSampleDataInitialized()
        {
            return _ggxIblSampleData != null;
        }

        public override void Initialize(UltraRenderPipelineRuntimeShaders runtimeShaders)
        {
            if (!_computeGgxIblSampleDataCS)
            {
                _computeGgxIblSampleDataCS = runtimeShaders.computeGgxIblSampleDataCS;
                _computeGgxIblSampleDataKernel = _computeGgxIblSampleDataCS.FindKernel("ComputeGgxIblSampleData");
            }

            if (!_buildProbabilityTablesCS)
            {
                _buildProbabilityTablesCS = runtimeShaders.buildProbabilityTablesCS;
                _conditionalDensitiesKernel = _buildProbabilityTablesCS.FindKernel("ComputeConditionalDensities");
                _marginalRowDensitiesKernel = _buildProbabilityTablesCS.FindKernel("ComputeMarginalRowDensities");
            }

            if (!_convolveMaterial)
            {
                _convolveMaterial = CoreUtils.CreateEngineMaterial(runtimeShaders.ggxConvolvePS);
            }

            if (!_ggxIblSampleData)
            {
                _ggxIblSampleData = new RenderTexture(_ggxIblMaxSampleCount, k_ggxIblMipCountMinusOne, 0, GraphicsFormat.R16G16B16A16_SFloat);
                _ggxIblSampleData.useMipMap = false;
                _ggxIblSampleData.autoGenerateMips = false;
                _ggxIblSampleData.enableRandomWrite = true;
                _ggxIblSampleData.filterMode = FilterMode.Point;
                _ggxIblSampleData.name = CoreUtils.GetRenderTargetAutoName(_ggxIblMaxSampleCount, k_ggxIblMipCountMinusOne, 1, GraphicsFormat.R16G16B16A16_SFloat, "GGXIblSampleData");
                _ggxIblSampleData.hideFlags = HideFlags.HideAndDontSave;
                //_ggxIblSampleData.Create();

                //InitializeGgxIblSampleData(cmd);
            }

            //if (!_planarReflectionFilteringCS)
            //{
            //    _planarReflectionFilteringCS = runtimeShaders.planarReflectionFilteringCS;
            //    _planarReflectionDepthConversionKernel = _planarReflectionFilteringCS.FindKernel("DepthConversion");
            //    _planarReflectionDownScaleKernel = _planarReflectionFilteringCS.FindKernel("DownScale");
            //    _planarReflectionFilteringKernel = _planarReflectionFilteringCS.FindKernel("FilterPlanarReflection");
            //}

            for (int i = 0; i < 6; ++i)
            {
                var lookAt = Matrix4x4.LookAt(Vector3.zero, CoreUtils.lookAtList[i], CoreUtils.upVectorList[i]);
                _faceWorldToViewMatrixMatrices[i] = lookAt * Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)); // Need to scale -1.0 on Z to match what is being done in the camera.wolrdToCameraMatrix API. ...
            }
        }

        void InitializeGgxIblSampleData(CommandBuffer cmd)
        {
            _computeGgxIblSampleDataCS.SetTexture(_computeGgxIblSampleDataKernel, "outputResult", _ggxIblSampleData);
            cmd.DispatchCompute(_computeGgxIblSampleDataCS, _computeGgxIblSampleDataKernel, 1, 1, 1);
        }

        private static RenderTextureDescriptor MakeRenderTextureDescriptor(int texWidth, int texHeight, GraphicsFormat format, bool useMipMap)
        {
            return new RenderTextureDescriptor
            {
                dimension = TextureDimension.Tex2D,
                width = texWidth,
                height = texHeight,
                volumeDepth = TextureXR.slices,
                graphicsFormat = format,
                enableRandomWrite = true,
                useDynamicScale = false,
                useMipMap = useMipMap,
                msaaSamples = 1
            };
        }

        private static void CreateIntermediateTextures(CommandBuffer cmd, int texWidth, int texHeight)
        {
            var probeFormat = GraphicsFormat.B10G11R11_UFloatPack32;// or R16G16B16A16_SFloat

            cmd.GetTemporaryRT(k_planarReflectionFilterTex0ID, MakeRenderTextureDescriptor(texWidth, texHeight, probeFormat, true));
            cmd.GetTemporaryRT(k_planarReflectionFilterTex1ID, MakeRenderTextureDescriptor(texWidth, texHeight, probeFormat, false));
            cmd.GetTemporaryRT(k_planarReflectionFilterDepthTex0ID, MakeRenderTextureDescriptor(texWidth, texHeight, GraphicsFormat.R32_SFloat, true));
            cmd.GetTemporaryRT(k_planarReflectionFilterDepthTex1ID, MakeRenderTextureDescriptor(texWidth, texHeight, GraphicsFormat.R32_SFloat, false));
        }

        private static void ReleaseItrermediateTextures(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(k_planarReflectionFilterTex0ID);
            cmd.ReleaseTemporaryRT(k_planarReflectionFilterTex1ID);
            cmd.ReleaseTemporaryRT(k_planarReflectionFilterDepthTex0ID);
            cmd.ReleaseTemporaryRT(k_planarReflectionFilterDepthTex1ID);
        }

        public override void Cleanup()
        {
            CoreUtils.Destroy(_convolveMaterial);
            CoreUtils.Destroy(_ggxIblSampleData);
        }

        void FilterCubemapCommon(CommandBuffer cmd,
            Texture source, RenderTexture target,
            Matrix4x4[] worldToViewMatrices)
        {
            using (new ProfilingScope(cmd, ProfilingSampler.Get(UltraRenderPipeline.UltraRPProfileId.FilterCubemapGGX)))
            {
                int mipCount = 1 + (int)Mathf.Log(source.width, 2.0f);
                if (mipCount < (int)EnvConstants.ConvolutionMipCount)
                {
                    Debug.LogWarning("RenderCubemapGGXConvolution: Cubemap size is too small for GGX convolution, needs at least " + (int)EnvConstants.ConvolutionMipCount + " mip levels");
                    return;
                }

                // Copy the first mip
                for (int f = 0; f < 6; f++)
                {
                    cmd.CopyTexture(source, f, 0, target, f, 0);
                }

                // Solid angle associated with a texel of the cubemap.
                float invOmegaP = (6.0f * source.width * source.width) / (4.0f * Mathf.PI);

                if (!_ggxIblSampleData.IsCreated())
                {
                    _ggxIblSampleData.Create();
                    InitializeGgxIblSampleData(cmd);
                }

                _convolveMaterial.SetTexture("_GgxIblSamples", _ggxIblSampleData);

                _materialPropertyBlock.SetTexture("_MainTex", source);
                _materialPropertyBlock.SetFloat("_InvOmegaP", invOmegaP);

                for (int mip = 1; mip < (int)EnvConstants.ConvolutionMipCount; ++mip)
                {
                    _materialPropertyBlock.SetFloat("_Level", mip);

                    for (int face = 0; face < 6; ++face)
                    {
                        var faceSize = new Vector4(source.width >> mip, source.height >> mip, 1.0f / (source.width >> mip), 1.0f / (source.height >> mip));
                        var transform = RenderingUtils.ComputePixelCoordToWorldSpaceViewDirectionMatrix(0.5f * Mathf.PI, Vector2.zero, faceSize, worldToViewMatrices[face], true);

                        _materialPropertyBlock.SetMatrix("_PixelCoordToViewDirWS", transform);

                        CoreUtils.SetRenderTarget(cmd, target, ClearFlag.None, mip, (CubemapFace)face);
                        CoreUtils.DrawFullScreen(cmd, _convolveMaterial, _materialPropertyBlock);
                    }
                }
            }
        }

        // Filters MIP map levels (other than 0) with GGX using multiple importance sampling.
        //override public void FilterCubemapMIS(CommandBuffer cmd,
        //    Texture source, RenderTexture target,
        //    RenderTexture conditionalCdf, RenderTexture marginalRowCdf)
        //{
        //    // Bind the input cubemap.
        //    _buildProbabilityTablesCS.SetTexture(_conditionalDensitiesKernel, "envMap", source);

        //    // Bind the outputs.
        //    _buildProbabilityTablesCS.SetTexture(_conditionalDensitiesKernel, "conditionalDensities", conditionalCdf);
        //    _buildProbabilityTablesCS.SetTexture(_conditionalDensitiesKernel, "marginalRowDensities", marginalRowCdf);
        //    _buildProbabilityTablesCS.SetTexture(_marginalRowDensitiesKernel, "marginalRowDensities", marginalRowCdf);

        //    int numRows = conditionalCdf.height;

        //    cmd.DispatchCompute(_buildProbabilityTablesCS, _conditionalDensitiesKernel, numRows, 1, 1);
        //    cmd.DispatchCompute(_buildProbabilityTablesCS, _marginalRowDensitiesKernel, 1, 1, 1);

        //    _convolveMaterial.EnableKeyword("USE_MIS");
        //    _convolveMaterial.SetTexture("_ConditionalDensities", conditionalCdf);
        //    _convolveMaterial.SetTexture("_MarginalRowDensities", marginalRowCdf);

        //    FilterCubemapCommon(cmd, source, target, _faceWorldToViewMatrixMatrices);
        //}

        override public void FilterCubemap(CommandBuffer cmd, Texture source, RenderTexture target)
        {
            FilterCubemapCommon(cmd, source, target, _faceWorldToViewMatrixMatrices);
        }

        //void BuildColorAndDepthMipChain(CommandBuffer cmd, RenderTexture sourceColor, RenderTexture sourceDepth, ref PlanarTextureFilteringParameters planarTextureFilteringParameters)
        //{
        //    int currentTexWidth = sourceColor.width;
        //    int currentTexHeight = sourceColor.height;

        //    // The first color level can be copied straight away in the mip chain.
        //    cmd.CopyTexture(sourceColor, 0, 0, 0, 0, sourceColor.width, sourceColor.height, k_planarReflectionFilterTex0ID, 0, 0, 0, 0);

        //    // For depth it is a bit trickier, we want to convert the depth from oblique space to non-oblique space due to the poor interpolation properties of the oblique matrix
        //    cmd.SetComputeVectorParam(_planarReflectionFilteringCS, HDShaderIDs._CaptureCameraPositon, planarTextureFilteringParameters.captureCameraPosition);
        //    cmd.SetComputeMatrixParam(_planarReflectionFilteringCS, HDShaderIDs._CaptureCameraVP_NO, planarTextureFilteringParameters.captureCameraVP_NonOblique);
        //    cmd.SetComputeMatrixParam(_planarReflectionFilteringCS, HDShaderIDs._CaptureCameraIVP, planarTextureFilteringParameters.captureCameraIVP);
        //    _currentScreenSize.Set(currentTexWidth, currentTexHeight, 1.0f / currentTexWidth, 1.0f / currentTexHeight);
        //    cmd.SetComputeVectorParam(_planarReflectionFilteringCS, HDShaderIDs._CaptureCurrentScreenSize, _currentScreenSize);
        //    cmd.SetComputeFloatParam(_planarReflectionFilteringCS, HDShaderIDs._CaptureCameraFarPlane, planarTextureFilteringParameters.captureFarPlane);

        //    // Input textures
        //    cmd.SetComputeTextureParam(_planarReflectionFilteringCS, _planarReflectionDepthConversionKernel, HDShaderIDs._DepthTextureOblique, sourceDepth);

        //    // Output textures
        //    cmd.SetComputeTextureParam(_planarReflectionFilteringCS, _planarReflectionDepthConversionKernel, HDShaderIDs._DepthTextureNonOblique, k_planarReflectionFilterDepthTex0ID);

        //    // Compute the dispatch parameters and evaluate the new mip
        //    int tileSize = 8;
        //    int numTilesXHR = (currentTexWidth + (tileSize - 1)) / tileSize;
        //    int numTilesYHR = (currentTexHeight + (tileSize - 1)) / tileSize;
        //    cmd.DispatchCompute(_planarReflectionFilteringCS, _planarReflectionDepthConversionKernel, numTilesXHR, numTilesYHR, 1);

        //    // Move to the next mip and build the chain
        //    int currentMipSource = 0;
        //    int texWidthHalf = sourceColor.width >> 1;
        //    int texHeightHalf = sourceColor.height >> 1;

        //    // Until we have a 2x2 texture, continue
        //    while (texWidthHalf >= 2 && texHeightHalf >= 2)
        //    {
        //        // Constant inputs
        //        cmd.SetComputeIntParam(_planarReflectionFilteringCS, HDShaderIDs._SourceMipIndex, currentMipSource);

        //        // Input textures
        //        cmd.SetComputeTextureParam(_planarReflectionFilteringCS, _planarReflectionDownScaleKernel, HDShaderIDs._ReflectionColorMipChain, k_planarReflectionFilterTex0ID);
        //        cmd.SetComputeTextureParam(_planarReflectionFilteringCS, _planarReflectionDownScaleKernel, HDShaderIDs._HalfResReflectionBuffer, k_planarReflectionFilterTex1ID);

        //        // Output textures
        //        cmd.SetComputeTextureParam(_planarReflectionFilteringCS, _planarReflectionDownScaleKernel, HDShaderIDs._DepthTextureMipChain, k_planarReflectionFilterDepthTex0ID);
        //        cmd.SetComputeTextureParam(_planarReflectionFilteringCS, _planarReflectionDownScaleKernel, HDShaderIDs._HalfResDepthBuffer, k_planarReflectionFilterDepthTex1ID);
        //        _currentScreenSize.Set(currentTexWidth, currentTexHeight, 1.0f / currentTexWidth, 1.0f / currentTexHeight);
        //        cmd.SetComputeVectorParam(_planarReflectionFilteringCS, HDShaderIDs._CaptureCurrentScreenSize, _currentScreenSize);

        //        // Compute the dispatch parameters and evaluate the new mip
        //        int numTilesXHRHalf = (texWidthHalf + (tileSize - 1)) / tileSize;
        //        int numTilesYHRHalf = (texHeightHalf + (tileSize - 1)) / tileSize;
        //        cmd.DispatchCompute(_planarReflectionFilteringCS, _planarReflectionDownScaleKernel, numTilesXHRHalf, numTilesYHRHalf, 1);

        //        // Given that mip to mip in compute doesn't work, we have to do this :(
        //        cmd.CopyTexture(k_planarReflectionFilterTex1ID, 0, 0, 0, 0, texWidthHalf, texHeightHalf, k_planarReflectionFilterTex0ID, 0, currentMipSource + 1, 0, 0);
        //        cmd.CopyTexture(k_planarReflectionFilterDepthTex1ID, 0, 0, 0, 0, texWidthHalf, texHeightHalf, k_planarReflectionFilterDepthTex0ID, 0, currentMipSource + 1, 0, 0);

        //        // Update the parameters for the next mip
        //        currentTexWidth = currentTexWidth >> 1;
        //        currentTexHeight = currentTexHeight >> 1;
        //        texWidthHalf = texWidthHalf >> 1;
        //        texHeightHalf = texHeightHalf >> 1;
        //        currentMipSource++;
        //    }
        //}

        //override public void FilterPlanarTexture(CommandBuffer cmd, RenderTexture source, ref PlanarTextureFilteringParameters planarTextureFilteringParameters, RenderTexture target)
        //{
        //    // Init the mip descent
        //    int texWidth = source.width;
        //    int texHeight = source.height;

        //    // First we need to copy the Mip0 (that matches perfectly smooth surface), no processing to be done on it
        //    cmd.CopyTexture(source, 0, 0, 0, 0, texWidth, texHeight, target, 0, 0, 0, 0);

        //    // If we are smooth reflection then the work is finish
        //    if (planarTextureFilteringParameters.smoothPlanarReflection)
        //        return;

        //    CreateIntermediateTextures(cmd, texWidth, texHeight);

        //    // Then we need to build a mip chain (one for color, one for depth) that we will sample later on in the process
        //    BuildColorAndDepthMipChain(cmd, source, planarTextureFilteringParameters.captureCameraDepthBuffer, ref planarTextureFilteringParameters);

        //    // Initialize the parameters for the descent
        //    int mipIndex = 1;
        //    int tileSize = 8;
        //    // Based on the initial texture resolution, the number of available mips for us to read from is variable and is based on the maximal texture width
        //    int numMipsChain = (int)(Mathf.Log((float)texWidth, 2.0f) - 1.0f);
        //    texWidth = texWidth >> 1;
        //    texHeight = texHeight >> 1;

        //    // Loop until we have the right amount of mips
        //    while (mipIndex < (int)EnvConstants.ConvolutionMipCount)
        //    {
        //        // Evaluate the dispatch parameters
        //        int numTilesXHR = (texWidth + (tileSize - 1)) / tileSize;
        //        int numTilesYHR = (texHeight + (tileSize - 1)) / tileSize;

        //        // Set input textures
        //        cmd.SetComputeTextureParam(_planarReflectionFilteringCS, _planarReflectionFilteringKernel, HDShaderIDs._DepthTextureMipChain, k_planarReflectionFilterDepthTex0ID);
        //        cmd.SetComputeTextureParam(_planarReflectionFilteringCS, _planarReflectionFilteringKernel, HDShaderIDs._ReflectionColorMipChain, k_planarReflectionFilterTex0ID);

        //        // Input constant parameters required
        //        cmd.SetComputeVectorParam(_planarReflectionFilteringCS, HDShaderIDs._CaptureBaseScreenSize, planarTextureFilteringParameters.captureCameraScreenSize);
        //        _currentScreenSize.Set(texWidth, texHeight, 1.0f / texWidth, 1.0f / texHeight);
        //        cmd.SetComputeVectorParam(_planarReflectionFilteringCS, HDShaderIDs._CaptureCurrentScreenSize, _currentScreenSize);
        //        cmd.SetComputeIntParam(_planarReflectionFilteringCS, HDShaderIDs._SourceMipIndex, mipIndex);
        //        cmd.SetComputeIntParam(_planarReflectionFilteringCS, HDShaderIDs._MaxMipLevels, numMipsChain);
        //        cmd.SetComputeFloatParam(_planarReflectionFilteringCS, HDShaderIDs._RTScaleFactor, 1.0f);
        //        cmd.SetComputeVectorParam(_planarReflectionFilteringCS, HDShaderIDs._ReflectionPlaneNormal, planarTextureFilteringParameters.probeNormal);
        //        cmd.SetComputeVectorParam(_planarReflectionFilteringCS, HDShaderIDs._ReflectionPlanePosition, planarTextureFilteringParameters.probePosition);
        //        cmd.SetComputeVectorParam(_planarReflectionFilteringCS, HDShaderIDs._CaptureCameraPositon, planarTextureFilteringParameters.captureCameraPosition);
        //        cmd.SetComputeMatrixParam(_planarReflectionFilteringCS, HDShaderIDs._CaptureCameraIVP_NO, planarTextureFilteringParameters.captureCameraIVP_NonOblique);
        //        cmd.SetComputeFloatParam(_planarReflectionFilteringCS, HDShaderIDs._CaptureCameraFOV, planarTextureFilteringParameters.captureFOV * Mathf.PI / 180.0f);

        //        // Set output textures
        //        cmd.SetComputeTextureParam(_planarReflectionFilteringCS, _planarReflectionFilteringKernel, HDShaderIDs._FilteredPlanarReflectionBuffer, k_planarReflectionFilterTex1ID);

        //        // Evaluate the next convolution
        //        cmd.DispatchCompute(_planarReflectionFilteringCS, _planarReflectionFilteringKernel, numTilesXHR, numTilesYHR, 1);

        //        // Copy the convoluted texture into the next mip and move on
        //        cmd.CopyTexture(k_planarReflectionFilterTex1ID, 0, 0, 0, 0, texWidth, texHeight, target, 0, mipIndex, 0, 0);

        //        // Move to the next mip
        //        texWidth = texWidth >> 1;
        //        texHeight = texHeight >> 1;
        //        mipIndex++;
        //    }

        //    ReleaseItrermediateTextures(cmd);
        //}
    }
}

