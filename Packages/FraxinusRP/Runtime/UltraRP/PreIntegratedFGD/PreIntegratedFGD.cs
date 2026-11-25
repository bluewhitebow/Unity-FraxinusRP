using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Pre-Integrated Fresnel Diffuse GGX (FGD) lookup tables for physically-based rendering.
    /// Provides pre-computed integration results for specular and diffuse BRDFs.
    /// </summary>
    sealed class PreIntegratedFGD
    {
        [GenerateHLSL]
        public enum FGDTexture
        {
            Resolution = 64
        }

        static PreIntegratedFGD s_instance;

        public static PreIntegratedFGD instance
        {
            get
            {
                if (s_instance == null)
                    s_instance = new PreIntegratedFGD();

                return s_instance;
            }
        }

        public enum FGDIndex
        {
            FGD_GGXAndDisneyDiffuse = 0,
            FGD_CharlieAndFabricLambert = 1,
            Count = 2
        }

        bool[] _isInit = new bool[(int)FGDIndex.Count];
        int[] _refCounting = new int[(int)FGDIndex.Count];

        Material[] _preIntegratedFGDMaterial = new Material[(int)FGDIndex.Count];
        RenderTexture[] _preIntegratedFGD = new RenderTexture[(int)FGDIndex.Count];
        RTHandle[] _preIntegratedFGDHandles = new RTHandle[(int)FGDIndex.Count];

        PreIntegratedFGD()
        {
            for (int i = 0; i < (int)FGDIndex.Count; ++i)
            {
                _isInit[i] = false;
                _refCounting[i] = 0;
            }
        }

        private Shader GetShaderForIndex(FGDIndex index)
        {
            var shaders = RenderPipelineGraphicsSettingsCompatibility.GetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>();
            if (shaders == null)
            {
                Debug.LogError($"Invalid resources to get shader for {index}.");
                return null;
            }

            switch (index)
            {
                case FGDIndex.FGD_GGXAndDisneyDiffuse: return shaders.preIntegratedFGD_GGXDisneyDiffusePS;
                case FGDIndex.FGD_CharlieAndFabricLambert: return shaders.preIntegratedFGD_CharlieFabricLambertPS;
                default: Debug.LogError($"Unable to get shader for index: {index}."); break;
            }

            return null;
        }

        /// <summary>
        /// Build resources in pipeline constructor.
        /// </summary>
        /// <param name="index">FGD texture index</param>
        public void Build(FGDIndex index)
        {
            Debug.Assert(index != FGDIndex.Count);
            Debug.Assert(_refCounting[(int)index] >= 0);

            if (_refCounting[(int)index] == 0)
            {
                Shader pixelShader = GetShaderForIndex(index);
                int res = (int)FGDTexture.Resolution;
                _preIntegratedFGDMaterial[(int)index] = CoreUtils.CreateEngineMaterial(pixelShader);
                _preIntegratedFGD[(int)index] = new RenderTexture(res, res, 0, GraphicsFormat.A2B10G10R10_UNormPack32)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    name = CoreUtils.GetRenderTargetAutoName(res, res, 1, GraphicsFormat.A2B10G10R10_UNormPack32, $"PreIntegrated{index}")
                };
                _preIntegratedFGD[(int)index].Create();
                _preIntegratedFGDHandles[(int)index] = RTHandles.Alloc(_preIntegratedFGD[(int)index]);
                _isInit[(int)index] = false;
            }

            _refCounting[(int)index]++;
        }

        /// <summary>
        /// Initialize global resources. Re-creates texture if it was internally destroyed.
        /// </summary>
        public void RenderInit(FGDIndex index, CommandBuffer cmd)
        {
            // Here we have to test IsCreated because in some circumstances (like loading RenderDoc),
            // the texture is internally destroyed but we don't know from C# side.
            // In this case IsCreated will return false, allowing us to re-render the texture
            if (_isInit[(int)index] && _preIntegratedFGD[(int)index].IsCreated())
                return;

            // If we are in wireframe mode, the drawfullscreen will not work as expected,
            // but we don't need the LUT anyway. So create the texture to avoid errors,
            // it will be initialized by the first render without wireframe
            if (GL.wireframe)
            {
                _preIntegratedFGD[(int)index].Create();
                return;
            }

            CoreUtils.DrawFullScreen(cmd, _preIntegratedFGDMaterial[(int)index], new RenderTargetIdentifier(_preIntegratedFGD[(int)index]));
            _isInit[(int)index] = true;
        }

        /// <summary>
        /// Cleanup resources.
        /// </summary>
        /// <param name="index">FGD texture index</param>
        public void Cleanup(FGDIndex index)
        {
            _refCounting[(int)index]--;

            if (_refCounting[(int)index] == 0)
            {
                _preIntegratedFGDHandles[(int)index]?.Release();
                _preIntegratedFGDHandles[(int)index] = null;
                CoreUtils.Destroy(_preIntegratedFGDMaterial[(int)index]);
                CoreUtils.Destroy(_preIntegratedFGD[(int)index]);
                _preIntegratedFGDMaterial[(int)index] = null;
                _preIntegratedFGD[(int)index] = null;

                _isInit[(int)index] = false;
            }

            Debug.Assert(_refCounting[(int)index] >= 0);
        }

        /// <summary>
        /// Bind material textures to global shader properties (legacy CommandBuffer path).
        /// </summary>
        /// <param name="cmd">Command buffer</param>
        /// <param name="index">FGD texture index</param>
        public void Bind(CommandBuffer cmd, FGDIndex index)
        {
            switch (index)
            {
                case FGDIndex.FGD_GGXAndDisneyDiffuse:
                    cmd.SetGlobalTexture(ShaderConstants._PreIntegratedFGD_GGXDisneyDiffuse, _preIntegratedFGD[(int)index]);
                    break;

                case FGDIndex.FGD_CharlieAndFabricLambert:
                    cmd.SetGlobalTexture(ShaderConstants._PreIntegratedFGD_CharlieAndFabric, _preIntegratedFGD[(int)index]);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Bind material textures to global shader properties (RenderGraph path).
        /// </summary>
        /// <param name="cmd">Compute command buffer</param>
        /// <param name="index">FGD texture index</param>
        /// <param name="texture">Texture handle from RenderGraph</param>
        public void Bind(ComputeCommandBuffer cmd, FGDIndex index, TextureHandle texture)
        {
            switch (index)
            {
                case FGDIndex.FGD_GGXAndDisneyDiffuse:
                    cmd.SetGlobalTexture(ShaderConstants._PreIntegratedFGD_GGXDisneyDiffuse, texture);
                    break;

                case FGDIndex.FGD_CharlieAndFabricLambert:
                    cmd.SetGlobalTexture(ShaderConstants._PreIntegratedFGD_CharlieAndFabric, texture);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Import texture to RenderGraph for use in passes.
        /// </summary>
        /// <param name="renderGraph">RenderGraph instance</param>
        /// <param name="index">FGD texture index</param>
        /// <returns>TextureHandle for the requested FGD texture</returns>
        public TextureHandle ImportToRenderGraph(RenderGraph renderGraph, FGDIndex index)
        {
            switch (index)
            {
                case FGDIndex.FGD_GGXAndDisneyDiffuse:
                    return renderGraph.ImportTexture(_preIntegratedFGDHandles[(int)index]);
                case FGDIndex.FGD_CharlieAndFabricLambert:
                    return renderGraph.ImportTexture(_preIntegratedFGDHandles[(int)index]);
                default:
                    return TextureHandle.nullHandle;
            }
        }

        internal static class ShaderConstants
        {
            public static readonly int _PreIntegratedFGD_GGXDisneyDiffuse = Shader.PropertyToID("_PreIntegratedFGD_GGXDisneyDiffuse");
            public static readonly int _PreIntegratedFGD_CharlieAndFabric = Shader.PropertyToID("_PreIntegratedFGD_CharlieAndFabric");
        }
    }
}

