using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;
using static FraxinusEngine.Rendering.Ultra.UltraRenderPipeline.Profiling.Pipeline;

namespace FraxinusEngine.Rendering.Ultra
{
    [Serializable]
    internal class DecalSettings
    {
        public DecalTechniqueOption technique = DecalTechniqueOption.Automatic;
        public float maxDrawDistance = 1000f;
        // Unity 2022's keyword prefilter cannot traverse the graph's parallel node/edge
        // collections. Keep decal-layer variants without placing filters inside the graph.
        public bool decalLayers = false;
        public DBufferSettings dBufferSettings = new DBufferSettings();
        public DecalScreenSpaceSettings screenSpaceSettings = new DecalScreenSpaceSettings();
    }

    internal class SharedDecalEntityManager : IDisposable
    {
        private DecalEntityManager _decalEntityManager;
        private int _referenceCounter;

        public DecalEntityManager Get()
        {
            if (_decalEntityManager == null)
            {
                Debug.Assert(_referenceCounter == 0);

                _decalEntityManager = new DecalEntityManager();

                var decalProjectors = GameObject.FindObjectsByType<DecalProjector>(FindObjectsSortMode.InstanceID);
                foreach (var decalProjector in decalProjectors)
                {
                    if (!decalProjector.isActiveAndEnabled || _decalEntityManager.IsValid(decalProjector.decalEntity))
                        continue;
                    decalProjector.decalEntity = _decalEntityManager.CreateDecalEntity(decalProjector);
                }

                DecalProjector.onDecalAdd += OnDecalAdd;
                DecalProjector.onDecalRemove += OnDecalRemove;
                DecalProjector.onDecalPropertyChange += OnDecalPropertyChange;
                DecalProjector.onDecalMaterialChange += OnDecalMaterialChange;
                DecalProjector.onAllDecalPropertyChange += OnAllDecalPropertyChange;
            }

            _referenceCounter++;

            return _decalEntityManager;
        }

        public void Release(DecalEntityManager decalEntityManager)
        {
            if (_referenceCounter == 0)
                return;

            _referenceCounter--;

            if (_referenceCounter == 0)
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            _decalEntityManager?.Dispose();
            _decalEntityManager = null;
            _referenceCounter = 0;

            DecalProjector.onDecalAdd -= OnDecalAdd;
            DecalProjector.onDecalRemove -= OnDecalRemove;
            DecalProjector.onDecalPropertyChange -= OnDecalPropertyChange;
            DecalProjector.onDecalMaterialChange -= OnDecalMaterialChange;
            DecalProjector.onAllDecalPropertyChange -= OnAllDecalPropertyChange;
        }

        private void OnDecalAdd(DecalProjector decalProjector)
        {
            if (!_decalEntityManager.IsValid(decalProjector.decalEntity))
                decalProjector.decalEntity = _decalEntityManager.CreateDecalEntity(decalProjector);
        }

        private void OnDecalRemove(DecalProjector decalProjector)
        {
            _decalEntityManager.DestroyDecalEntity(decalProjector.decalEntity);
        }

        private void OnDecalPropertyChange(DecalProjector decalProjector)
        {
            if (_decalEntityManager.IsValid(decalProjector.decalEntity))
                _decalEntityManager.UpdateDecalEntityData(decalProjector.decalEntity, decalProjector);
        }

        private void OnAllDecalPropertyChange()
        {
            _decalEntityManager.UpdateAllDecalEntitiesData();
        }

        private void OnDecalMaterialChange(DecalProjector decalProjector)
        {
            OnDecalRemove(decalProjector);
            OnDecalAdd(decalProjector);
        }
    }

    [Serializable, NodeMenuItem("UltraRP/DecalRenderPassNode")]
    [DisallowMultipleNode("DecalRenderPassNode")]
    public class DecalRenderPassNode : ScriptableRenderPassNode
    {
        private static SharedDecalEntityManager s_sharedDecalEntityManager { get; } = new SharedDecalEntityManager();

        [SerializeField]
        private DecalSettings _settings = new DecalSettings();

        [Input("Camera Target")]
        public CameraTargetBufferNodeParameter inCameraTarget;

        [Output("Camera Target")]
        internal CameraTargetBufferNodeParameter outCameraTarget;

        private DecalTechnique _technique = DecalTechnique.Invalid;
        private DBufferSettings _dBufferSettings;
        private DecalScreenSpaceSettings _screenSpaceSettings;
        private bool _recreateSystems;

        // Entities
        private DecalEntityManager _decalEntityManager;
        private DecalUpdateCachedSystem _updateCachedSystem;
        private DecalUpdateCullingGroupSystem _updateCullingGroupSystem;
        private DecalUpdateCulledSystem _decalUpdateCulledSystem;
        private DecalCreateDrawCallSystem _decalCreateDrawCallSystem;
        private DecalDrawErrorSystem _drawErrorSystem;

        // DBuffer Rendering
        private DBufferCopyDepthPass _copyDepthPass;
        private DBufferRenderPass _dBufferRenderPass;
        private DecalDrawFowardEmissiveSystem _decalDrawFowardEmissiveSystem;
        private DecalDrawDBufferSystem _drawDBufferSystem;
        private DecalForwardEmissivePass _forwardEmissivePass;
        private Material _dBufferClearMaterial;

        // ScreenSpace Rendering
        private DecalScreenSpaceRenderPass _screenSpaceRenderPass;
        private DecalDrawScreenSpaceSystem _decalDrawScreenSpaceSystem;
        private DecalSkipCulledSystem _decalSkipCulledSystem;

        // GBuffer Rendering
        private DecalGBufferRenderPass _gBufferRenderPass;
        private DecalDrawGBufferSystem _drawGBufferSystem;
        private DeferredLights _deferredLights;

        // Preview
        private DecalPreviewPass _previewPass;

        // Internal
        internal ref DecalSettings settings => ref _settings;
        internal bool intermediateRendering => _technique == DecalTechnique.DBuffer;
        internal bool requiresDecalLayers => _settings.decalLayers;
        internal static bool isGLDevice => SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 
                                            || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore;

        public DecalRenderPassNode() : base()
        {
            profilingSampler = new ProfilingSampler(nameof(DecalRenderPassNode));
            _recreateSystems = true;
        }

        protected override void Enable()
        {
            base.Enable();

            // Managed-reference node constructors also run during graph
            // deserialization. DecalPreviewPass creates ShaderTagIds, which call
            // Unity's TagToID and must wait for the graph's OnEnable lifecycle.
            _previewPass ??= new DecalPreviewPass();

#if UNITY_EDITOR
            ResourceReloader.TryReloadAllNullIn(this, UltraRenderPipelineAsset.packagePath);
#endif
        }

        public override bool SupportedInCurrentFrame(ContextContainer frameData)
        {
            return _technique != DecalTechnique.Invalid;
        }

        #region Technique Selection

        internal DBufferSettings GetDBufferSettings()
        {
            if (_settings.technique == DecalTechniqueOption.Automatic)
                return new DBufferSettings() { surfaceData = DecalSurfaceData.AlbedoNormalMAOS };
            else
                return _settings.dBufferSettings;
        }

        internal DecalScreenSpaceSettings GetScreenSpaceSettings()
        {
            if (_settings.technique == DecalTechniqueOption.Automatic)
            {
                return new DecalScreenSpaceSettings()
                {
                    normalBlend = DecalNormalBlend.Low,
                };
            }
            else
                return _settings.screenSpaceSettings;
        }

        internal DecalTechnique GetTechnique(bool isDeferred, bool needsGBufferAccurateNormals, bool checkForInvalidTechniques = true)
        {
            DecalTechnique technique = DecalTechnique.Invalid;
            switch (_settings.technique)
            {
                case DecalTechniqueOption.Automatic:
                    if (IsAutomaticDBuffer() || isDeferred && needsGBufferAccurateNormals)
                        technique = DecalTechnique.DBuffer;
                    else if (isDeferred)
                        technique = DecalTechnique.GBuffer;
                    else
                        technique = DecalTechnique.ScreenSpace;
                    break;
                case DecalTechniqueOption.ScreenSpace:
                    technique = isDeferred ? DecalTechnique.GBuffer : DecalTechnique.ScreenSpace;
                    break;
                case DecalTechniqueOption.DBuffer:
                    technique = DecalTechnique.DBuffer;
                    break;
            }

            if (!checkForInvalidTechniques)
                return technique;

            // Check if the technique is valid
            if (technique == DecalTechnique.DBuffer && isGLDevice)
            {
                Debug.LogError("Decal DBuffer technique is not supported with OpenGL.");
                return DecalTechnique.Invalid;
            }

            bool mrt4 = SystemInfo.supportedRenderTargetCount >= 4;
            if (technique == DecalTechnique.DBuffer && !mrt4)
            {
                Debug.LogError("Decal DBuffer technique requires MRT4 support.");
                return DecalTechnique.Invalid;
            }

            if (technique == DecalTechnique.GBuffer && !mrt4)
            {
                Debug.LogError("Decal GBuffer technique requires MRT4 support.");
                return DecalTechnique.Invalid;
            }

            return technique;
        }

        private bool IsAutomaticDBuffer()
        {
#if UNITY_EDITOR
            if (UnityEditor.EditorUserBuildSettings.selectedBuildTargetGroup == UnityEditor.BuildTargetGroup.WebGL)
                return false;
#else
            if (Application.platform == RuntimePlatform.WebGLPlayer)
                return false;
#endif
            return !PlatformAutoDetect.isShaderAPIMobileDefined;
        }

        #endregion

        #region System Initialization

        private bool RecreateSystemsIfNeeded(ScriptableRenderer renderer, in CameraData cameraData)
        {
            if (!_recreateSystems)
                return true;

            bool isDeferred = renderer.frameData.Get<InternalRendererData>().hasDeferred;
            bool needsGBufferAccurateNormals = renderer.accurateGbufferNormals;

            _technique = GetTechnique(isDeferred, needsGBufferAccurateNormals);
            if (_technique == DecalTechnique.Invalid)
                return false;

            _dBufferSettings = GetDBufferSettings();
            _screenSpaceSettings = GetScreenSpaceSettings();

            if (_decalEntityManager == null)
                _decalEntityManager = s_sharedDecalEntityManager.Get();

            _updateCachedSystem = new DecalUpdateCachedSystem(_decalEntityManager);
            _decalUpdateCulledSystem = new DecalUpdateCulledSystem(_decalEntityManager);
            _decalCreateDrawCallSystem = new DecalCreateDrawCallSystem(_decalEntityManager, _settings.maxDrawDistance);

            if (intermediateRendering)
                _updateCullingGroupSystem = new DecalUpdateCullingGroupSystem(_decalEntityManager, _settings.maxDrawDistance);
            else
                _decalSkipCulledSystem = new DecalSkipCulledSystem(_decalEntityManager);

            _drawErrorSystem = new DecalDrawErrorSystem(_decalEntityManager, _technique);

            // Create Passes and Systems based on technique
            CreateRenderingPaths(renderer);

            _recreateSystems = false;
            return true;
        }

        private void CreateRenderingPaths(ScriptableRenderer renderer)
        {
            switch (_technique)
            {
                case DecalTechnique.DBuffer:
                    CreateDBufferPath();
                    break;
                case DecalTechnique.ScreenSpace:
                    CreateScreenSpacePath();
                    break;
                case DecalTechnique.GBuffer:
                    CreateGBufferPath(renderer);
                    break;
            }
        }

        private void CreateDBufferPath()
        {
            // Get DBuffer clear material from resources
            if (RenderPipelineGraphicsSettingsCompatibility.TryGetRenderPipelineSettings<UltraRendererResources>(
                out var ultraRendererShaders))
            {
                _dBufferClearMaterial = CoreUtils.CreateEngineMaterial(ultraRendererShaders.decalDBufferClear);
            }

            _copyDepthPass = new DBufferCopyDepthPass();
            _drawDBufferSystem = new DecalDrawDBufferSystem(_decalEntityManager);

            _dBufferRenderPass = new DBufferRenderPass(_dBufferClearMaterial, _dBufferSettings, _drawDBufferSystem, _settings.decalLayers);
            _decalDrawFowardEmissiveSystem = new DecalDrawFowardEmissiveSystem(_decalEntityManager);
            _forwardEmissivePass = new DecalForwardEmissivePass(_decalDrawFowardEmissiveSystem);
        }

        private void CreateScreenSpacePath()
        {
            _decalDrawScreenSpaceSystem = new DecalDrawScreenSpaceSystem(_decalEntityManager);
            _screenSpaceRenderPass = new DecalScreenSpaceRenderPass(_screenSpaceSettings, _decalDrawScreenSpaceSystem, _settings.decalLayers);
        }

        private void CreateGBufferPath(ScriptableRenderer renderer)
        {
            // Get DeferredLights from renderer
            var deferredRenderer = renderer.GetModule<DeferredLights>();
            if (deferredRenderer != null)
                _deferredLights = deferredRenderer;

            _drawGBufferSystem = new DecalDrawGBufferSystem(_decalEntityManager);
            _gBufferRenderPass = new DecalGBufferRenderPass(_screenSpaceSettings, _drawGBufferSystem, _settings.decalLayers);
            if (_deferredLights != null)
            {
                _gBufferRenderPass.Setup(_deferredLights);
            }
        }

        #endregion

        #region Rendering

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            if (cameraData.cameraType == CameraType.Preview)
                return;

            bool isValid = RecreateSystemsIfNeeded(renderer, cameraData);
            if (!isValid)
                return;

            ChangeAdaptivePerformanceDrawDistances();

            // Update decal entities
            _decalEntityManager.Update();
            _updateCachedSystem.Execute();

            if (intermediateRendering)
            {
                _updateCullingGroupSystem.Execute(cameraData.camera);
            }
            else
            {
                _decalSkipCulledSystem.Execute(cameraData.camera);
                _decalCreateDrawCallSystem.Execute();

                if (_technique == DecalTechnique.ScreenSpace)
                    _decalDrawScreenSpaceSystem.Execute(cameraData);
                else if (_technique == DecalTechnique.GBuffer)
                    _drawGBufferSystem.Execute(cameraData);
            }

            // Execute draw error system for debugging
            _drawErrorSystem.Execute(cameraData);
        }

        public override void SetupPass()
        {
            CameraTargetBuffer buffer = inCameraTarget.value;

            ConfigurePortValue(outCameraTarget, buffer);
        }

        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {
            CameraData cameraData = frameData.Get<CameraData>();
            ResourceData resourceData = frameData.Get<ResourceData>();
            RenderingData renderingData = frameData.Get<RenderingData>();

            CameraTargetBuffer buffer = inCameraTarget.value;
            
            if (!buffer.IsValid())
                return;

            TextureHandle colorTarget = buffer.color;
            TextureHandle depthTarget = buffer.depth;

            // Handle preview cameras
            if (cameraData.cameraType == CameraType.Preview)
            {
                _previewPass.Render(renderGraph, frameData, colorTarget, depthTarget);
                return;
            }

            var renderer = cameraData.renderer;
            bool isValid = RecreateSystemsIfNeeded(renderer, cameraData);
            if (!isValid)
                return;

            ChangeAdaptivePerformanceDrawDistances();

            if (intermediateRendering)
            {
                _decalUpdateCulledSystem.Execute();
                _decalCreateDrawCallSystem.Execute();
            }

            RenderDecals(renderGraph, frameData);
        }

        private void RenderDecals(RenderGraph renderGraph, ContextContainer frameData)
        {
            ResourceData resourceData = frameData.Get<ResourceData>();
            
            switch (_technique)
            {
                case DecalTechnique.DBuffer:
                    if (_dBufferRenderPass != null)
                    {
                        // Step 1: Copy depth for DBuffer rendering
                        TextureHandle dBufferDepth = _copyDepthPass.Render(renderGraph, frameData);

                        // Step 2: Render to DBuffer (use dBufferDepth if created, otherwise use cameraDepth)
                        TextureHandle depthTarget = dBufferDepth.IsValid() 
                            ? dBufferDepth
                            : resourceData.cameraDepth;

                        if (depthTarget.IsValid())
                        {
                            _dBufferRenderPass.Render(renderGraph, frameData, depthTarget);
                        }
                        
                        // Step 3: Render emissive decals
                        if (_forwardEmissivePass != null && resourceData.cameraColor.IsValid() && resourceData.cameraDepth.IsValid())
                        {
                            _forwardEmissivePass.Render(renderGraph, frameData, resourceData.cameraColor, resourceData.cameraDepth);
                        }
                    }
                    break;

                case DecalTechnique.ScreenSpace:
                    if (_screenSpaceRenderPass != null && resourceData.cameraColor.IsValid() && resourceData.cameraDepth.IsValid())
                    {
                        _screenSpaceRenderPass.Render(renderGraph, frameData, resourceData.cameraColor, resourceData.cameraDepth);
                    }
                    break;

                case DecalTechnique.GBuffer:
                    if (_gBufferRenderPass != null && resourceData.gBuffer != null && resourceData.cameraDepth.IsValid())
                    {
                        _gBufferRenderPass.Render(renderGraph, frameData, resourceData.gBuffer, resourceData.cameraDepth);
                    }
                    break;
            }
        }

        #endregion

        #region Resource Management

        protected override void Disable()
        {
            base.Disable();

            // Dispose passes
            _copyDepthPass?.Dispose();
            _dBufferRenderPass?.Dispose();
            
            // Dispose material
            if (_dBufferClearMaterial != null)
            {
                CoreUtils.Destroy(_dBufferClearMaterial);
                _dBufferClearMaterial = null;
            }

            // Release entity manager
            if (_decalEntityManager != null)
            {
                s_sharedDecalEntityManager.Release(_decalEntityManager);
                _decalEntityManager = null;
            }

            // Clear systems
            _updateCachedSystem = null;
            _updateCullingGroupSystem = null;
            _decalUpdateCulledSystem = null;
            _decalCreateDrawCallSystem = null;
            _decalSkipCulledSystem = null;
            _drawErrorSystem = null;

            _drawDBufferSystem = null;
            _decalDrawFowardEmissiveSystem = null;
            _copyDepthPass = null;
            _dBufferRenderPass = null;
            _forwardEmissivePass = null;

            _decalDrawScreenSpaceSystem = null;
            _screenSpaceRenderPass = null;

            _drawGBufferSystem = null;
            _gBufferRenderPass = null;
            _deferredLights = null;

            _recreateSystems = true;
        }

        #endregion

        #region RenderingLayers

        /// <summary>
        /// todo:renderinglayer要好好规划的
        /// Checks if this pass requires RenderingLayers and at what event.
        /// </summary>
        internal bool RequireRenderingLayers(bool isDeferred, bool needsGBufferAccurateNormals, out RenderingLayerUtils.Event atEvent, out RenderingLayerUtils.MaskSize maskSize)
        {
            var technique = GetTechnique(isDeferred, needsGBufferAccurateNormals, checkForInvalidTechniques: false);
            atEvent = technique == DecalTechnique.DBuffer ? RenderingLayerUtils.Event.DepthNormalPrePass : RenderingLayerUtils.Event.Opaque;
            maskSize = RenderingLayerUtils.MaskSize.Bits8;
            return requiresDecalLayers;
        }

        #endregion

        #region Adaptive Performance
        private void ChangeAdaptivePerformanceDrawDistances()
        {
#if ADAPTIVE_PERFORMANCE_4_0_0_OR_NEWER
            if (UltraRenderPipeline.asset.useAdaptivePerformance)
            {
                if (_createDrawCallSystem != null)
                {
                    _createDrawCallSystem.maxDrawDistance = AdaptivePerformance.AdaptivePerformanceRenderSettings.DecalsDrawDistance;
                }
                if (_updateCullingGroupSystem != null)
                {
                    _updateCullingGroupSystem.boundingDistance = AdaptivePerformance.AdaptivePerformanceRenderSettings.DecalsDrawDistance;
                }
            }
#endif
        }

        #endregion
    }
}
