using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// PRT Baker Core - Runtime烘焙核心逻辑
    /// </summary>
    public sealed class PRTBakerCore : IPRTBaker, IDisposable
    {
        private static class ShaderProperties
        {
            public static readonly int _ProbePos = Shader.PropertyToID("_ProbePos");
            public static readonly int _RandSeed = Shader.PropertyToID("_RandSeed");
            public static readonly int _WorldPosCubemap = Shader.PropertyToID("_WorldPosCubemap");
            public static readonly int _NormalCubemap = Shader.PropertyToID("_NormalCubemap");
            public static readonly int _AlbedoCubemap = Shader.PropertyToID("_AlbedoCubemap");
            public static readonly int _InputCubemap = Shader.PropertyToID("_InputCubemap");
            public static readonly int _CoefficientSH9 = Shader.PropertyToID("_CoefficientSH9");
            public static readonly int _Surfels = Shader.PropertyToID("_Surfels");
        }

        /// <summary>
        /// G-buffer捕获模式
        /// </summary>
        private enum GBufferCaptureMode
        {
            WorldPosition,
            Normal,
            Albedo
        }

        private const int k_threadX = 32;
        private const int k_threadY = 16;
        private const int k_rayNum = k_threadX * k_threadY; // 512 rays per probe
        private const int k_surfelByteSize = 3 * 12 + 4; // sizeof(Surfel)

        // Shared render textures for G-buffer capture
        private RenderTexture _worldPosRT;
        private RenderTexture _normalRT;
        private RenderTexture _albedoRT;

        // Baking settings
        private readonly int _cubemapSize;
        private readonly LayerMask _bakeLayers;

        // Progress callback (runtime可用)
        public event Action<string, float> onProgress;

        private bool _isInitialized;
        private bool _disposed;

        // Dictionary to store original shaders for restoration
        private readonly Dictionary<Material, Shader> _originalShaders = new();
        private Camera _cubemapCamera;

        private readonly ComputeShader _surfelSampleCS;
        private readonly int _surfelSampleKernel;

        private readonly ComputeShader _reflectionProbeSampleCS;
        private readonly int _reflectionProbeSampleKernel;

        /// <summary>
        /// 初始化Runtime烘焙器
        /// </summary>
        /// <param name="bakeResolution">烘焙分辨率</param>
        /// <param name="bakeLayers">烘焙Layer掩码</param>
        public PRTBakerCore(PRTBakeResolution bakeResolution, LayerMask bakeLayers)
        {
            _cubemapSize = (int)bakeResolution;
            _bakeLayers = bakeLayers;
            InitializeRenderTextures();

            // 加载Compute Shader
            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRenderPipelineRuntimeShaders>(out var runtimeShaders))
            {
                _surfelSampleCS = runtimeShaders.prtSurfelSampleCS;
                if (_surfelSampleCS)
                    _surfelSampleKernel = _surfelSampleCS.FindKernel("CSMain");

                _reflectionProbeSampleCS = runtimeShaders.reflectionProbeSampleCS;
                if (_reflectionProbeSampleCS)
                    _reflectionProbeSampleKernel = _reflectionProbeSampleCS.FindKernel("CSMain");
            }
            else
                Debug.LogError("[PRTBakerCore] UltraRenderPipelineRuntimeShaders not found in GraphicsSettings.");
        }

        /// <summary>
        /// 初始化渲染纹理用于G-buffer捕获
        /// </summary>
        private void InitializeRenderTextures()
        {
            if (_isInitialized) return;

            // Create cubemap render textures
            _worldPosRT = new RenderTexture(_cubemapSize, _cubemapSize, 24, RenderTextureFormat.ARGBFloat)
            {
                dimension = TextureDimension.Cube,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _worldPosRT.Create();

            _normalRT = new RenderTexture(_cubemapSize, _cubemapSize, 24, RenderTextureFormat.ARGBFloat)
            {
                dimension = TextureDimension.Cube,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _normalRT.Create();

            _albedoRT = new RenderTexture(_cubemapSize, _cubemapSize, 24, RenderTextureFormat.ARGB32)
            {
                dimension = TextureDimension.Cube,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _albedoRT.Create();

            _isInitialized = true;
        }

        /// <summary>
        /// 更新进度（触发回调）
        /// </summary>
        private void UpdateProgress(string status, float progress)
        {
            onProgress?.Invoke(status, progress);
        }

        /// <summary>
        /// 批量设置Shader并记录原始Shader（用于后续恢复）
        /// </summary>
        private void RecordAndSetShaders(Renderer[] renderers, Shader shader)
        {
            // Record original shaders
            foreach (var renderer in renderers)
            {
                if (!renderer) continue;
                
                var materials = renderer.sharedMaterials;
                if (materials.Length > 0)
                {
                    foreach (var material in materials)
                    {
                        if (material && !_originalShaders.ContainsKey(material))
                            _originalShaders[material] = material.shader;
                    }
                }
            }

            // Set new shader
            foreach (var renderer in renderers)
            {
                if (!renderer) continue;
                
                var materials = renderer.sharedMaterials;
                if (materials.Length > 0)
                {
                    foreach (var material in materials)
                    {
                        if (material)
                            material.shader = shader;
                    }
                }
            }
        }

        /// <summary>
        /// 恢复原始Shader
        /// </summary>
        private void RestoreOriginalShaders()
        {
            foreach (var kvp in _originalShaders)
            {
                var material = kvp.Key;
                var originalShader = kvp.Value;

                if (material && originalShader)
                    material.shader = originalShader;
            }

            _originalShaders.Clear();
        }

        /// <summary>
        /// 设置全局Shader关键字（G-buffer捕获模式）
        /// </summary>
        private static void SetGlobalGBufferCaptureMode(GBufferCaptureMode captureMode)
        {
            switch (captureMode)
            {
                case GBufferCaptureMode.WorldPosition:
                    Shader.EnableKeyword("_GBUFFER_WORLDPOS");
                    Shader.DisableKeyword("_GBUFFER_NORMAL");
                    break;
                case GBufferCaptureMode.Normal:
                    Shader.DisableKeyword("_GBUFFER_WORLDPOS");
                    Shader.EnableKeyword("_GBUFFER_NORMAL");
                    break;
                case GBufferCaptureMode.Albedo:
                    Shader.DisableKeyword("_GBUFFER_WORLDPOS");
                    Shader.DisableKeyword("_GBUFFER_NORMAL");
                    break;
            }
        }

        /// <summary>
        /// 清除所有G-buffer关键字
        /// </summary>
        private static void ClearGlobalGBufferKeywords()
        {
            Shader.DisableKeyword("_GBUFFER_WORLDPOS");
            Shader.DisableKeyword("_GBUFFER_NORMAL");
        }

        /// <summary>
        /// 创建用于Cubemap渲染的临时相机
        /// </summary>
        private static Camera CreateCubemapCamera()
        {
            GameObject cubemapCamera = new GameObject("PRTBakerCore_CubemapCamera")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            Camera camera = cubemapCamera.AddComponent<Camera>();
            camera.cameraType = CameraType.Reflection;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.0f, 0.0f, 0.0f, 0.0f);
            camera.enabled = false;
            
            // 设置Layer Mask (Runtime使用)
            // 注意：这里不使用StaticEditorFlags，而是依赖外部传入的bakeLayers
            return camera;
        }

        /// <summary>
        /// 捕获G-buffer Cubemaps
        /// </summary>
        /// <param name="position">探针位置</param>
        /// <param name="useCullingMask">是否使用相机的Culling Mask</param>
        private void CaptureGbufferCubemaps(Vector3 position, bool useCullingMask = true)
        {
            _cubemapCamera.transform.SetPositionAndRotation(position, Quaternion.identity);
            
            if (useCullingMask)
                _cubemapCamera.cullingMask = _bakeLayers;

            // Capture GBuffers
            SetGlobalGBufferCaptureMode(GBufferCaptureMode.WorldPosition);
            _cubemapCamera.RenderToCubemap(_worldPosRT);
            
            SetGlobalGBufferCaptureMode(GBufferCaptureMode.Normal);
            _cubemapCamera.RenderToCubemap(_normalRT);
            
            SetGlobalGBufferCaptureMode(GBufferCaptureMode.Albedo);
            _cubemapCamera.RenderToCubemap(_albedoRT);

            // Clean up global keywords
            ClearGlobalGBufferKeywords();

            // Force GPU flush
            GL.Flush();
        }

        /// <summary>
        /// 捕获光照Cubemap（用于反射探针）
        /// </summary>
        private void CaptureLightingCubemap(Vector3 position)
        {
            _cubemapCamera.transform.SetPositionAndRotation(position, Quaternion.identity);
            _cubemapCamera.cullingMask = _bakeLayers;
            _cubemapCamera.RenderToCubemap(_albedoRT);
        }

        /// <summary>
        /// 烘焙Surfel数据
        /// </summary>
        /// <param name="probePosition">探针位置</param>
        /// <returns>Surfel数组</returns>
        public Surfel[] BakeSurfelData(Vector3 probePosition)
        {
            CaptureGbufferCubemaps(probePosition);
            return SampleSurfels(_worldPosRT, _normalRT, _albedoRT, probePosition);
        }

        /// <summary>
        /// 烘焙反射探针附加数据
        /// </summary>
        /// <param name="probePosition">探针位置</param>
        /// <returns>SH系数数组</returns>
        public float[] BakeAdditionalProbeData(Vector3 probePosition)
        {
            CaptureLightingCubemap(probePosition);
            return SampleReflectionProbe(_albedoRT);
        }

        /// <summary>
        /// 使用Compute Shader采样Surfel数据
        /// </summary>
        private Surfel[] SampleSurfels(RenderTexture worldPosCubemap, RenderTexture normalCubemap,
            RenderTexture albedoCubemap, Vector3 position)
        {
            if (!_surfelSampleCS)
            {
                Debug.LogError("[PRTBakerCore] Surfel sample compute shader is missing.");
                return new Surfel[0];
            }

            var surfels = new ComputeBuffer(k_rayNum, k_surfelByteSize);
            try
            {
                var readBackBuffer = new Surfel[k_rayNum];

                var cs = _surfelSampleCS;
                var kernel = _surfelSampleKernel;

                // Set shader parameters
                cs.SetVector(ShaderProperties._ProbePos, new Vector4(position.x, position.y, position.z, 1.0f));
                cs.SetFloat(ShaderProperties._RandSeed, UnityEngine.Random.Range(0.0f, 1.0f));
                cs.SetTexture(kernel, ShaderProperties._WorldPosCubemap, worldPosCubemap);
                cs.SetTexture(kernel, ShaderProperties._NormalCubemap, normalCubemap);
                cs.SetTexture(kernel, ShaderProperties._AlbedoCubemap, albedoCubemap);
                cs.SetBuffer(kernel, ShaderProperties._Surfels, surfels);

                // Dispatch compute shader
                cs.Dispatch(kernel, 1, 1, 1);

                // Read back results
                surfels.GetData(readBackBuffer);
                return readBackBuffer;
            }
            finally
            {
                surfels.Release();
            }
        }

        /// <summary>
        /// 采样反射探针数据
        /// </summary>
        private float[] SampleReflectionProbe(RenderTexture lightingCubemap)
        {
            if (!_reflectionProbeSampleCS)
            {
                Debug.LogError("[PRTBakerCore] Reflection probe sample compute shader is missing.");
                return new float[27];
            }

            var coefficientSH9 = new ComputeBuffer(27, sizeof(float));
            try
            {
                var cs = _reflectionProbeSampleCS;
                var kernel = _reflectionProbeSampleKernel;

                // Set shader parameters
                cs.SetFloat(ShaderProperties._RandSeed, UnityEngine.Random.Range(0.0f, 1.0f));
                cs.SetTexture(kernel, ShaderProperties._InputCubemap, lightingCubemap);
                cs.SetBuffer(kernel, ShaderProperties._CoefficientSH9, coefficientSH9);

                // Dispatch compute shader
                cs.Dispatch(kernel, 1, 1, 1);

                // Read back results
                var readBackBuffer = new float[27];
                coefficientSH9.GetData(readBackBuffer);
                return readBackBuffer;
            }
            finally
            {
                coefficientSH9.Release();
            }
        }

        /// <summary>
        /// 检查物体是否应该参与烘焙（基于LayerMask）
        /// </summary>
        /// <param name="go">GameObject</param>
        /// <returns>是否烘焙</returns>
        private bool ShouldBakeObject(GameObject go)
        {
            return (_bakeLayers & (1 << go.layer)) != 0;
        }

        /// <summary>
        /// 烘焙整个ProbeVolume
        /// </summary>
        /// <param name="volume">探针体积</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task BakeVolume(PRTProbeVolume volume, System.Threading.CancellationToken cancellationToken = default)
        {
            if (!_isInitialized || _disposed)
            {
                Debug.LogError("[PRTBakerCore] Baker is not initialized or already disposed.");
                return;
            }

            UpdateProgress("Preparing scene for baking...", 0.0f);

            // 查找场景中所有Renderer，过滤出应该参与烘焙的
            Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                .Where(r => r.gameObject.activeInHierarchy && ShouldBakeObject(r.gameObject))
                .ToArray();

            if (renderers.Length == 0)
            {
                Debug.LogWarning($"[PRTBakerCore] No renderers found in bakeLayers. Check PRTProbeVolume.bakeLayers setting.");
            }

            var captureShader = Shader.Find("Hidden/PRT/GBuffer");
            if (!captureShader)
            {
                Debug.LogError("[PRTBakerCore] PRT/GBuffer shader not found. Please ensure it's included in the build.");
                return;
            }

            RecordAndSetShaders(renderers, captureShader);
            _cubemapCamera = CreateCubemapCamera();

            try
            {
                UpdateProgress("Baking probe volume...", 0.1f);
                await volume.BakeDataAsync(this, cancellationToken);
                UpdateProgress("Baking complete!", 1.0f);
            }
            finally
            {
                RestoreOriginalShaders();
                
                // Clean up temporary camera
                if (_cubemapCamera)
                {
                    UnityEngine.Object.Destroy(_cubemapCamera.gameObject);
                    _cubemapCamera = null;
                }
            }
        }

        /// <summary>
        /// 检查Baker是否已初始化
        /// </summary>
        private bool IsInitialized() => _isInitialized && !_disposed;

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _worldPosRT?.Release();
            _worldPosRT = null;
            
            _normalRT?.Release();
            _normalRT = null;
            
            _albedoRT?.Release();
            _albedoRT = null;

            if (_cubemapCamera)
            {
                UnityEngine.Object.Destroy(_cubemapCamera.gameObject);
                _cubemapCamera = null;
            }

            _originalShaders.Clear();
            _isInitialized = false;
            onProgress = null;
            _disposed = true;
        }
    }
}
