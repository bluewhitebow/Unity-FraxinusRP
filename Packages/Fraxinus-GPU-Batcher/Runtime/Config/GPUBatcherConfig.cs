// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace Fraxinus.GPUBatcher
{
    /// <summary>singleton config for BRG instanced renderer.</summary>
    public class GPUBatcherConfig : ScriptableObject
    {
        private static GPUBatcherConfig s_instance;
        private static bool s_configMissing;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal static GPUBatcherConfig loadedConfig => s_instance;
#if FRAXINUS_RP
        private static DebugUI.MessageBox s_missingConfigWarning;
#endif
#endif
        private const string k_missingConfigMessage = "GPU Batcher config is missing. Open Project Settings > Graphics > Fraxinus GPU Batcher to create or preload it.";

        // Automatic configuration assets belong to the project rather than the embedded package.
        private const string k_fallbackFolder = "Assets/FraxinusGPUBatcherData";
        private const string k_configFileName = "FraxinusGPUBatcherConfig.asset";
        private const string k_defaultAssetPath = k_fallbackFolder + "/" + k_configFileName;
        private const string k_packagePath = "Packages/com.bluewhitebow.fraxinus-gpu-batcher";
        public static string packagePath => k_packagePath;
        private const string k_projectAssetPath = k_defaultAssetPath;
        public static string projectAssetPath => k_projectAssetPath;

        public static GPUBatcherConfig instance
        {
            get
            {
                if (!Rendering.BRGIndirectSubmission.hasNativeApi) return s_instance;
                if (s_instance == null)
                {
#if UNITY_EDITOR
                    GetOrCreateProjectConfig();
#else
                    var found = Resources.FindObjectsOfTypeAll<GPUBatcherConfig>();
                    if (found.Length > 0)
                        s_instance = found[0];
#endif
                    SetConfigMissing(s_instance == null);
                }
                return s_instance;
            }
        }

        private static void SetConfigMissing(bool missing)
        {
            if (s_configMissing == missing)
                return;

            s_configMissing = missing;
#if FRAXINUS_RP && (UNITY_EDITOR || DEVELOPMENT_BUILD)
            if (missing)
            {
                s_missingConfigWarning = new DebugUI.MessageBox
                {
                    displayName = k_missingConfigMessage,
                    style = DebugUI.MessageBox.Style.Error
                };
                GPUBatcherDebugUI.GetPanel().children.Insert(0, s_missingConfigWarning);
            }
            else
            {
                var panel = s_missingConfigWarning.panel;
                panel?.children.Remove(s_missingConfigWarning);
                GPUBatcherDebugUI.RemovePanelIfEmpty(panel);
                s_missingConfigWarning = null;
            }
#endif
            if (missing)
                Debug.LogError(k_missingConfigMessage);
        }

        private void OnEnable()
        {
            if (!Rendering.BRGIndirectSubmission.hasNativeApi) return;
            SetConfigMissing(false);
#if UNITY_EDITOR
            if (s_instance == null || FindInPreloadedAssets() == this)
                s_instance = this;
            EnsureShaderReferences();
#if FRAXINUS_RP
            EnsureTextureGrassShader();
#endif
#elif DEVELOPMENT_BUILD
            if (s_instance == null)
                s_instance = this;
#endif
        }

#if UNITY_EDITOR
        internal static GPUBatcherConfig FindInPreloadedAssets()
        {
            var preloaded = UnityEditor.PlayerSettings.GetPreloadedAssets();
            foreach (var asset in preloaded)
            {
                if (asset is GPUBatcherConfig config)
                    return config;
            }
            return null;
        }

        internal static int CountInPreloadedAssets()
        {
            int count = 0;
            var preloaded = UnityEditor.PlayerSettings.GetPreloadedAssets();
            foreach (var asset in preloaded)
            {
                if (asset is GPUBatcherConfig)
                    count++;
            }
            return count;
        }
#endif

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticInstance()
        {
            s_instance = null;
            SetConfigMissing(false);
        }

        [UnityEditor.InitializeOnLoadMethod]
        private static void AutoCreateOnDomainReload()
        {
            if (!Rendering.BRGIndirectSubmission.hasNativeApi) return;
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (s_instance == null)
                    _ = instance;
            };
        }
#endif

#if UNITY_EDITOR
        // set while the plugin itself is being deleted
        private static bool s_suppressAssetCreation;

        internal static void SuppressAssetCreation() => s_suppressAssetCreation = true;

        internal static GPUBatcherConfig GetOrCreateProjectConfig() => s_instance = FindOrCreateAsset();

        private static GPUBatcherConfig FindOrCreateAsset()
        {
            var found0 = FindInPreloadedAssets();
            if (found0 != null) return found0;

            var found = UnityEditor.AssetDatabase.LoadAssetAtPath<GPUBatcherConfig>(k_defaultAssetPath);

            if (found == null)
            {
                string[] guids = UnityEditor.AssetDatabase.FindAssets("t:GPUBatcherConfig");
                if (guids.Length > 0)
                {
                    string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
                    found = UnityEditor.AssetDatabase.LoadAssetAtPath<GPUBatcherConfig>(path);
                }
            }

            if (found == null)
            {
                if (s_suppressAssetCreation)
                {
                    // transient instance keeps callers alive until the domain reload
                    found = CreateInstance<GPUBatcherConfig>();
                    found.hideFlags = HideFlags.HideAndDontSave;
                    return found;
                }

                string assetPath = ResolveConfigAssetPath();

                found = CreateInstance<GPUBatcherConfig>();
                UnityEditor.AssetDatabase.CreateAsset(found, assetPath);
                UnityEditor.AssetDatabase.SaveAssets();
                Debug.Log($"GPUBatcherConfig: Created config at {assetPath}");
            }

            EnsurePreloadedAsset(found);

            found.EnsureShaderReferences();

            return found;
        }

        private static string ResolveConfigAssetPath()
        {
            if (!UnityEditor.AssetDatabase.IsValidFolder(k_fallbackFolder))
                UnityEditor.AssetDatabase.CreateFolder("Assets", "FraxinusGPUBatcherData");
            return k_defaultAssetPath;
        }

        private static void EnsurePreloadedAsset(GPUBatcherConfig config)
        {
            if (!Rendering.BRGIndirectSubmission.hasNativeApi) return;
            var preloaded = UnityEditor.PlayerSettings.GetPreloadedAssets();

            int emptySlot = -1;
            for (int i = 0; i < preloaded.Length; i++)
            {
                if (preloaded[i] == config) return;
                if (preloaded[i] == null && emptySlot < 0) emptySlot = i;
            }

            if (emptySlot >= 0)
            {
                preloaded[emptySlot] = config;
                UnityEditor.PlayerSettings.SetPreloadedAssets(preloaded);
            }
            else
            {
                var list = new System.Collections.Generic.List<Object>(preloaded) { config };
                UnityEditor.PlayerSettings.SetPreloadedAssets(list.ToArray());
            }
        }

        private void EnsureShaderReferences()
        {
            if (pickingShader == null)
            {
                pickingShader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(
                    "Packages/com.bluewhitebow.fraxinus-rp/Shaders/BRGPicking.shader");
                if (pickingShader != null)
                    UnityEditor.EditorUtility.SetDirty(this);
            }

            bool dirty = false;

            if (cullingShader == null)
            {
                cullingShader = FindComputeShaderByName("GPUBatcher_Culling");
                if (cullingShader != null)
                    dirty = true;
            }

            if (uploadShader == null)
            {
                uploadShader = FindComputeShaderByName("GPUBatcher_Upload");
                if (uploadShader != null)
                    dirty = true;
            }

            if (occlusionShader == null)
            {
                occlusionShader = FindComputeShaderByName("GPUBatcher_HZBGeneration");
                if (occlusionShader != null)
                    dirty = true;
            }

            if (dirty)
            {
                UnityEditor.EditorUtility.SetDirty(this);
                // rebind a renderer that cached a null shader at init
                if (GPUBatcherRenderer.hasInstance && cullingShader != null)
                    GPUBatcherRenderer.instance.Reinitialize();
            }
        }

        // defer to idle so the asset db is ready
        [UnityEditor.InitializeOnLoadMethod]
        private static void ResolveShadersAfterReload()
        {
            if (!Rendering.BRGIndirectSubmission.hasNativeApi) return;
            UnityEditor.EditorApplication.delayCall += () => instance?.EnsureShaderReferences();
        }

#if FRAXINUS_RP
        // serialize the grass shader when enabled so it ships in builds, clear it otherwise
        private void EnsureTextureGrassShader()
        {
            var desired = allowTextureGrass
                ? UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(k_packagePath + "/Shaders/FraxinusGPUBatcherTerrainTextureGrass.shader")
                : null;
            if (_textureGrassShader == desired) return;

            _textureGrassShader = desired;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

        private static ComputeShader FindComputeShaderByName(string shaderName)
        {
            string[] guids = UnityEditor.AssetDatabase.FindAssets($"{shaderName} t:ComputeShader");
            foreach (var guid in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                // findAssets does substring matching, verify exact name
                if (shader != null && shader.name == shaderName)
                    return shader;
            }
            return null;
        }

        [UnityEditor.MenuItem("Tools/Fraxinus GPU Batcher/Select Config")]
        public static void SelectConfig()
        {
            if (!Rendering.BRGIndirectSubmission.hasNativeApi) return;
            var config = instance;
            UnityEditor.Selection.activeObject = config;
            UnityEditor.EditorGUIUtility.PingObject(config);
        }

        [UnityEditor.MenuItem("Tools/Fraxinus GPU Batcher/Select Config", true)]
        private static bool CanSelectConfig() => Rendering.BRGIndirectSubmission.hasNativeApi;

        private int _prevMaxShadowCrossfadeCascade = -1;
        private float _prevDefaultAnimatedCrossfadeDuration;
        private float _prevDefaultSmallMeshSize;
        private MinScreenSizeFadeMode _prevDefaultSmallMeshFadeMode;
        private float _prevDefaultSmallMeshFadeWidth;
        private int _prevMaxPoolRefsPerChunk;
        private bool _prevSupportLegacyLightProbes;
        private bool _prevEnablePerInstanceColor;
        private bool _prevEnableSpeedTreeWind;
        private bool _prevCombineMultiMeshLODs;
        private NegativeScaleMode _prevNegativeScaleMode;
        private bool _prevUseGPUOcclusionCulling;
        private bool _prevCullDetailsAtDetailDistance;
#if FRAXINUS_RP
        private bool _prevAllowTextureGrass;
#endif
        private bool _prevValuesCached;
        private bool _reinitScheduled;
        private bool _rebuildBatchesScheduled;
        private bool _occlusionToggleScheduled;
#endif

        private void OnValidate()
        {
            if (!Rendering.BRGIndirectSubmission.hasNativeApi) return;
            maxPoolRefsPerChunk = math.max(1, maxPoolRefsPerChunk);
            initialChunkCount = math.max(1, initialChunkCount);
            initialPoolCapacity = math.max(1, initialPoolCapacity);
            defaultAnimatedCrossfadeDuration = math.clamp(defaultAnimatedCrossfadeDuration, 0f, 10f);
            terrainMaxUploadsPerFrame = math.max(1, terrainMaxUploadsPerFrame);
            terrainMaxUnloadsPerFrame = math.max(1, terrainMaxUnloadsPerFrame);
            terrainMaxConcurrentJobs = math.max(1, terrainMaxConcurrentJobs);

#if UNITY_EDITOR
            EnsureShaderReferences();
#if FRAXINUS_RP
            EnsureTextureGrassShader();
#endif

            // rebuild batches when GPU prototype data changes
            bool defaultCrossfadeChanged = _prevValuesCached &&
                _prevDefaultAnimatedCrossfadeDuration != defaultAnimatedCrossfadeDuration;
            if (_prevValuesCached && !_rebuildBatchesScheduled &&
                (_prevMaxShadowCrossfadeCascade != maxShadowCrossfadeCascade ||
                 defaultCrossfadeChanged ||
                 _prevDefaultSmallMeshSize != defaultSmallMeshSize ||
                 _prevDefaultSmallMeshFadeMode != defaultSmallMeshFadeMode ||
                 _prevDefaultSmallMeshFadeWidth != defaultSmallMeshFadeWidth))
            {
                if (GPUBatcherRenderer.hasInstance)
                {
                    _rebuildBatchesScheduled = true;
                    UnityEditor.EditorApplication.delayCall += () =>
                    {
                        _rebuildBatchesScheduled = false;
                        if (GPUBatcherRenderer.hasInstance)
                        {
                            if (defaultCrossfadeChanged)
                                GPUBatcherRenderer.instance.RefreshDefaultCrossfadeDuration();
                            GPUBatcherRenderer.instance.RebuildBatches();
                        }
                    };
                }
            }
            _prevMaxShadowCrossfadeCascade = maxShadowCrossfadeCascade;
            _prevDefaultAnimatedCrossfadeDuration = defaultAnimatedCrossfadeDuration;
            _prevDefaultSmallMeshSize = defaultSmallMeshSize;
            _prevDefaultSmallMeshFadeMode = defaultSmallMeshFadeMode;
            _prevDefaultSmallMeshFadeWidth = defaultSmallMeshFadeWidth;

            // reinitialize when buffer layout changes
            if (_prevValuesCached && !_reinitScheduled &&
                (_prevSupportLegacyLightProbes != supportLegacyLightProbes ||
                 _prevEnablePerInstanceColor != enablePerInstanceColor ||
                 _prevEnableSpeedTreeWind != enableSpeedTreeWind ||
                 _prevCombineMultiMeshLODs != combineMultiMeshLODs ||
                 _prevMaxPoolRefsPerChunk != maxPoolRefsPerChunk ||
                 _prevNegativeScaleMode != negativeScaleMode))
            {
                if (GPUBatcherRenderer.hasInstance)
                {
                    _reinitScheduled = true;
                    UnityEditor.EditorApplication.delayCall += () =>
                    {
                        _reinitScheduled = false;
                        if (GPUBatcherRenderer.hasInstance)
                            GPUBatcherRenderer.instance.Reinitialize();
                    };
                }
            }
            // GPU occlusion toggle: bump buffer pool generation + clean up HZB state
            if (_prevValuesCached && !_occlusionToggleScheduled && _prevUseGPUOcclusionCulling != _useGPUOcclusionCulling)
            {
                if (GPUBatcherRenderer.hasInstance)
                {
                    _occlusionToggleScheduled = true;
                    UnityEditor.EditorApplication.delayCall += () =>
                    {
                        _occlusionToggleScheduled = false;
                        if (GPUBatcherRenderer.hasInstance)
                            GPUBatcherRenderer.instance.OnGPUOcclusionToggled();
                    };
                }
            }

            // runtime refresh when terrain detail cull toggle changes
            if (_prevValuesCached && _prevCullDetailsAtDetailDistance != cullDetailsAtDetailDistance)
            {
                if (GPUBatcherRenderer.hasInstance)
                {
                    UnityEditor.EditorApplication.delayCall += () =>
                    {
                        if (GPUBatcherRenderer.hasInstance)
                            GPUBatcherRenderer.instance.RuntimeRefresh();
                    };
                }
            }

#if FRAXINUS_RP
            // runtime refresh so texture grass protos build/tear down when the toggle changes
            if (_prevValuesCached && _prevAllowTextureGrass != allowTextureGrass)
            {
                if (GPUBatcherRenderer.hasInstance)
                {
                    UnityEditor.EditorApplication.delayCall += () =>
                    {
                        if (GPUBatcherRenderer.hasInstance)
                            GPUBatcherRenderer.instance.RuntimeRefresh();
                    };
                }
            }
#endif

            _prevSupportLegacyLightProbes = supportLegacyLightProbes;
            _prevEnablePerInstanceColor = enablePerInstanceColor;
            _prevEnableSpeedTreeWind = enableSpeedTreeWind;
            _prevCombineMultiMeshLODs = combineMultiMeshLODs;
            _prevMaxPoolRefsPerChunk = maxPoolRefsPerChunk;
            _prevNegativeScaleMode = negativeScaleMode;
            _prevUseGPUOcclusionCulling = _useGPUOcclusionCulling;
            _prevCullDetailsAtDetailDistance = cullDetailsAtDetailDistance;
#if FRAXINUS_RP
            _prevAllowTextureGrass = allowTextureGrass;
#endif
            _prevValuesCached = true;
#endif
        }

        [Header("Compute Shaders")]
        [Tooltip("Compute shader for hierarchical GPU culling, LOD selection, and shadow cascade filtering.")]
        public ComputeShader cullingShader;

        [Tooltip("Compute shader for GPU buffer uploads, matrix computation, and animated crossfade state updates.")]
        public ComputeShader uploadShader;

        [Tooltip("Compute shader for HZB generation (GPUBatcher_HZBGeneration).")]
        public ComputeShader occlusionShader;

#if UNITY_EDITOR
        [Header("Editor Shaders")]
        [Tooltip("Shader used to pick GPU Batcher instances in the Scene view.")]
        public Shader pickingShader;
#endif

        [Header("Culling")]
        [Tooltip("Test spheres enclosing each chunk against Unity's baked occlusion after the current camera cull.")]
        public bool useBakedOcclusionCulling = true;

        [FormerlySerializedAs("useGPUOcclusionCulling")]
        [Tooltip("GPU occlusion using a hierarchical depth buffer. History mode uses the previous frame's opaque depth; enable the retest pass to use this frame's complete depth prepass.")]
        [SerializeField] private bool _useGPUOcclusionCulling = false;

        public bool useGPUOcclusionCulling => _useGPUOcclusionCulling;

        public void SetGPUOcclusionCulling(bool value)
        {
            if (_useGPUOcclusionCulling == value) return;
            _useGPUOcclusionCulling = value;
            if (GPUBatcherRenderer.hasInstance)
                GPUBatcherRenderer.instance.OnGPUOcclusionToggled();
        }

        [Tooltip("Request a complete depth prepass in Fraxinus RP, then repeat GPU culling against its current-frame HZB before opaque rendering.")]
        public bool useHZBRetestPass = false;

        [Range(0f, 8f)]
        [Tooltip("Fixed pixel expansion for HZB occlusion test (in HZB texels). Compensates for temporal mismatch between previous frame's depth and current camera. ~2-3 covers typical camera speeds at 60fps.")]
        public float hzbPixelExpansion = 2f;

        [Tooltip("Cull shadow casters whose entire swept shadow volume is hidden behind the previous frame's depth. Directional lights only, requires GPU occlusion culling. Worst case artifact: a shadow reappears one frame late after a fast disocclusion.")]
        public bool shadowCasterOcclusion = true;

        [Tooltip("Default minimum screen size (%) for game object prototypes that don't define their own. 0 = disabled. " +
                 "\n\nApplies only to non-LODGroup, non-MeshLOD prototypes without a per-prototype override.")]
        [Range(0f, 5f)]
        public float defaultSmallMeshSize = 0.3f;

        [Tooltip("Fade mode for the default small mesh screen size cull boundary. Crossfade costs some performance by doubling the draw calls for non-LOD prototypes.")]
        public MinScreenSizeFadeMode defaultSmallMeshFadeMode = MinScreenSizeFadeMode.None;

        [Tooltip("Fade transition width for the default small mesh cull zone (0-1). " +
                 "Only used with stateless Crossfade mode. 0 = hard cull.")]
        [Range(0f, 1f)]
        public float defaultSmallMeshFadeWidth = 0.5f;

        [Header("Pooling & Memory")]
        [Tooltip("Maximum pools a single chunk can use. Each pool holds 16 instances.")]
        public int maxPoolRefsPerChunk = 128;

        [Tooltip("Initial number of chunk slots. Grows automatically when full.")]
        public int initialChunkCount = 256;

        [Tooltip("Initial number of instance pools. Each pool holds 16 instances. Grows automatically when full.")]
        public int initialPoolCapacity = 4096;

        [Tooltip("Automatically compact instance buffers when waste exceeds 50% (instances <= half of allocated slots). Reduces memory but causes brief hitches during compaction.")]
        public bool enableAutoCompaction = false;

        [Tooltip("Seconds to keep an unused prototype (refcount 0) alive before auto-purging. Allows fast re-registration during scene transitions. 0 = purge on next frame.")]
        [Range(0f, 120f)]
        public float prototypeIdleGracePeriod = 30f;

        [Header("Rendering")]
        [InspectorName("Combine multi-mesh LODs")]
        [Tooltip("Combine multiple sub-meshes of a prefab/LOD into a single mesh, baking their offset transforms into the vertices. " +
                 "\n\nWhen disabled, only the first mesh of each LOD is used and child offset transforms are ignored (no combining, no per-prefab mesh allocations).")]
        public bool combineMultiMeshLODs = true;

        [Tooltip("Enable per-instance color tinting. Adds 4 bytes per instance to the matrix buffer.")]
        public bool enablePerInstanceColor = false;

        [Tooltip("Enable SpeedTree wind. When disabled, SpeedTree instances use the shared batch with no wind parameters.")]
        public bool enableSpeedTreeWind = true;

        [Tooltip("FlipWinding: preserve negative scale, emit separate draw commands with flipped winding (default). " +
                 "\n\nForcePositiveScale: abs() all scale on CPU - zero GPU cost but mirrors are lost.")]
        public NegativeScaleMode negativeScaleMode = NegativeScaleMode.FlipWinding;

        [Tooltip("Workaround for HDRP forward rendering bug: BRG instances with non-Object motion mode are incorrectly excluded from the depth prepass when their material has 'Motion Vectors For Vertex Animation' enabled. Forces HasMotion on affected draw commands so they render via the motion vector pass instead.")]
        public bool hdrpForwardDepthFix = false;

        [Tooltip("Sample Light Probe Groups on the CPU and upload SH and occlusion per instance (128 bytes per slot). Fraxinus shaders sample APV and APRTV through the render pipeline independently of this setting.")]
        public bool supportLegacyLightProbes = false;

        [Header("Terrain")]
        [Tooltip("Maximum detail chunk uploads per frame across ALL terrains. Lower = smoother but slower loading.")]
        public int terrainMaxUploadsPerFrame = 16;

        [Tooltip("Maximum detail chunk unloads per frame across ALL terrains.")]
        public int terrainMaxUnloadsPerFrame = 16;

        [Tooltip("Maximum detail extraction jobs in flight simultaneously across ALL terrains (async path only).")]
        public int terrainMaxConcurrentJobs = 32;

        [Tooltip("Force extraction jobs to complete on the same frame they are scheduled. Eliminates pop-in from multi-frame async completion at the cost of a per-patch main thread stall.")]
        public bool terrainCompleteJobsSameFrame = false;

        [Tooltip("Bypass upload throttle on the first frame so all in-range patches load immediately (causes one-time hitch but no pop-in).")]
        public bool terrainSkipThrottleOnStart = false;

        [Tooltip("Extra distance (m) beyond detail render distance to keep patches loaded (hysteresis).")]
        public float terrainDetailUnloadHysteresis = 10f;

        [Tooltip("When enabled, allows per-object motion for terrain details. (prototypes use the prefab's native motion vector mode) \n\nWhen disabled (default), forces Camera motion mode, matching Unity's default terrain behavior. (UUM-43491)")]
        public bool terrainDetailObjectMotionVectors = false;

        [Tooltip("Hard-cull detail instances beyond the terrain's detail object distance. Eliminates gridding from detail chunk size.")]
        public bool cullDetailsAtDetailDistance = true;

#if FRAXINUS_RP
        [Tooltip("Render texture-mode terrain grass (DetailRenderMode.Grass / GrassBillboard) through BRG. When enabled, the grass shader is serialized into this config so it ships in builds.")]
        public bool allowTextureGrass = false;

        [SerializeField, HideInInspector] private Shader _textureGrassShader;

        public Shader textureGrassShader => _textureGrassShader;
#endif

#if UNITY_EDITOR
        [Tooltip("Enable click-to-select terrain trees in the scene view. Spawns a temporary proxy GameObject for transform editing.")]
        public bool enableTerrainTreeSelection = true;
#endif

        [Header("LOD & Crossfade")]
        [Tooltip("Number of shadow cascades that use crossfade. 0 = disabled (no crossfade), 1 = cascade 0 only, 2 = cascades 0-1, 4 = all cascades. Can be overridden per-prototype.")]
        [Range(0, 4)]
        public int maxShadowCrossfadeCascade = 4;

        [HideInInspector] public bool debugDisableCrossfade = false;

        [Tooltip("PerPrototype: All instances of a prototype fade in lockstep using a global cycle timer. Matches Default Unity Behavior." +
                 "\n\nPerInstance: Each instance manages and tracks its own fade state independently.")]
        public AnimatedCrossfadeMode animatedCrossfadeMode = AnimatedCrossfadeMode.PerPrototype;

        [Tooltip("Default animated crossfade duration in seconds. Can be overridden per-prototype via GPUBatcherPrototypeExtraData.")]
        public float defaultAnimatedCrossfadeDuration = 0.3f;
        public bool skipIntermediateLODs = true;

#if DEBUG
        [HideInInspector]
        public bool enableDebugLogging = false;
#endif
        [Tooltip("Mark all chunks visible, skip CPU frustum/distance culling. GPU CullChunks still runs.")]
        [HideInInspector] public bool debugDisableCPUChunkCulling = false;
        [Tooltip("Skip all GPU culling dispatches and reuse the previous frame's visibility/indirect args. Useful for isolating CPU vs GPU cost.")]
        [HideInInspector] public bool debugDisableCulling = false;
        [Tooltip("Skip BRG rendering entirely (OnPerformCulling returns empty). GPU culling still runs. Useful for profiling culling cost without draw overhead.")]
        [HideInInspector] public bool debugDisableRendering = false;
        [Tooltip("Draw chunk bounds as gizmos when a registerer is selected in the scene view.")]
        [HideInInspector] public bool showChunkBounds = false;
#if UNITY_EDITOR
        // session-only: survives domain reloads, resets on editor restart
        public bool debugDrawBoundingSpheres
        {
            get => UnityEditor.SessionState.GetBool("FraxinusGPUBatcher_DrawBoundingSpheres", false);
            set => UnityEditor.SessionState.SetBool("FraxinusGPUBatcher_DrawBoundingSpheres", value);
        }

        public bool debugVisualizeCullingInSceneView
        {
            get => UnityEditor.SessionState.GetBool("FraxinusGPUBatcher_VisualizeCullingInSceneView", false);
            set => UnityEditor.SessionState.SetBool("FraxinusGPUBatcher_VisualizeCullingInSceneView", value);
        }

        // set by HDRP patcher or URP injector on domain reload - indicates pass 1 retest is available
        public static bool hzbRetestPassCompatible
        {
            get => UnityEditor.SessionState.GetBool("FraxinusGPUBatcher_HZBRetestPassCompatible", false);
            set => UnityEditor.SessionState.SetBool("FraxinusGPUBatcher_HZBRetestPassCompatible", value);
        }

        [Tooltip("Defer pool frees until after FlushStagedUploads. Prevents freed pools from being reused before staged overrides flush.")]
        [HideInInspector] public bool debugDeferPoolFrees = false;

        public bool debugValidateUploads
        {
            get => UnityEditor.SessionState.GetBool("FraxinusGPUBatcher_ValidateUploads", false);
            set => UnityEditor.SessionState.SetBool("FraxinusGPUBatcher_ValidateUploads", value);
        }
#endif

        // gathered allocations, records peak usage for sizing
#if UNITY_EDITOR
        [Header("Gathered Allocations")]
        [Tooltip("Record peak allocation sizes during play mode.")]
        public bool recordAllocations = false;

        [Tooltip("Peak chunk slot count observed during runtime.")]
        public int gatheredMaxChunks;

        [Tooltip("Peak pool count observed during runtime.")]
        public int gatheredPoolCapacity;

        [Tooltip("Peak total instance count observed during runtime.")]
        public int gatheredMaxInstances;

        public void CopyGatheredToInitial(float padding = 1f)
        {
            if (gatheredMaxChunks > 0)
                initialChunkCount = (int)(gatheredMaxChunks * padding);
            if (gatheredPoolCapacity > 0)
                initialPoolCapacity = (int)(gatheredPoolCapacity * padding);
        }

        public void ClearGatheredData()
        {
            gatheredMaxChunks = 0;
            gatheredPoolCapacity = 0;
            gatheredMaxInstances = 0;
        }
#endif

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024f * 1024f):F2} MB";
            return $"{bytes / (1024f * 1024f * 1024f):F2} GB";
        }

#if UNITY_EDITOR
        public void RecordHighWaterMarks(int chunkCount, int poolCount, int instanceCount)
        {
            bool dirty = false;
            if (chunkCount > gatheredMaxChunks) { gatheredMaxChunks = chunkCount; dirty = true; }
            if (poolCount > gatheredPoolCapacity) { gatheredPoolCapacity = poolCount; dirty = true; }
            if (instanceCount > gatheredMaxInstances) { gatheredMaxInstances = instanceCount; dirty = true; }
            if (dirty) UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
