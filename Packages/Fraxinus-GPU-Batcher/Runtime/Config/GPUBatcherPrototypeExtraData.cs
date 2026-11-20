// Copyright (c) 2026 Milk_Drinker01. All rights reserved.

using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace Fraxinus.GPUBatcher
{
    public enum MinScreenSizeFadeMode
    {
        None,
        Crossfade,
        AnimatedCrossfade
    }

    public enum DistanceDensityMode
    {
        ScreenSize,
        RawDistance
    }

    /// <summary>optional component on prototype prefabs to override BRG render settings.</summary>
    [AddComponentMenu("Fraxinus GPU Batcher/Prototype Extra Data")]
    public class GPUBatcherPrototypeExtraData : MonoBehaviour
    {
#if UNITY_EDITOR




#endif

        [Tooltip("Optional group this prototype belongs to. " +
                 "Assign a GPUBatcherPrototypeGroup asset to control LOD bias and density for all members at runtime " +
                 "via GPUBatcherRenderer.SetPrototypeGroupLodBias() / SetPrototypeGroupDensity(). " +
                 "When unassigned, this prototype uses default LOD bias (1) and full density (1).")]
        public GPUBatcherPrototypeGroup prototypeGroup;

        [Range(1, 4)]
        [Tooltip("Maximum shadow cascades this prototype renders into. " +
                 "Trees typically use 4 (full quality). Grass/details use 1 (nearest cascade only). " +
                 "Lower values = fewer shadow draw calls.")]
        public int maxShadowCascade = 4;

        [Range(0, 7)]
        [Tooltip("Minimum LOD level used for shadow mesh. " +
                 "If 1, LOD 0 instances cast shadows using LOD 1's mesh. " +
                 "If 2, LOD 0 and 1 both use LOD 2's mesh for shadows. " +
                 "Reduces shadow polycount without affecting visual LOD.")]
        public int minShadowLOD = 0;

        public bool overrideMaxShadowDistance = false;

        [Tooltip("Maximum shadow distance for this prototype. " +
                 "Limits how far shadows render for this prototype, saving GPU work for small details.")]
        [Min(0.01f)]
        public float maxShadowDistance = 80f;

        public float EffectiveMaxShadowDistance => overrideMaxShadowDistance ? maxShadowDistance : 0f;

        public bool overrideMaxShadowCrossfadeCascade = false;

        [Tooltip("Number of shadow cascades that use crossfade for this prototype. " +
                 "0 = disabled (no crossfade), 1 = cascade 0 only, 2 = cascades 0-1, 4 = all cascades. " +
                 "Higher cascades are further from camera and benefit less from crossfade. " +
                 "Lower values = fewer shadow buckets and less GPU memory.")]
        [Range(0, 4)]
        public int maxShadowCrossfadeCascade = 4;

        public int EffectiveMaxShadowCrossfadeCascade => overrideMaxShadowCrossfadeCascade ? maxShadowCrossfadeCascade : -99; // -99 = use global

        [Tooltip("Enable per-instance distance density culling. Randomly thins out instances based on distance or screen size. " +
                 "Uses the prototype's LOD fade mode for transitions.")]
        [FormerlySerializedAs("enableDynamicDensity")]
        public bool enableDistanceDensity = false;

        [Tooltip("Minimum density fraction at the smallest visible screen size. " +
                 "0 = cull all instances at max distance. 0.3 = keep 30% of instances at max distance.")]
        [Range(0f, 1f)]
        public float densityMinFraction = 0f;

        [Tooltip("Fade transition control. " +
                 "For stateless crossfade: density-space fade band width (0-1). Wider = smoother but more GPU cost. " +
                 "For animated crossfade: fade duration in seconds.")]
        [Min(0f)]
        public float densityFadeWidth = 0.05f;

        [Tooltip("Density metric: ScreenSize uses screen-space percentage (consistent with LOD). " +
                 "\n\nRaw Distance uses world-space distance (more predictable for ground cover).")]
        [FormerlySerializedAs("dynamicDensityMode")]
        public DistanceDensityMode distanceDensityMode = DistanceDensityMode.ScreenSize;

        [Tooltip("Screen size (%) where density starts reducing. " +
                 "Above this = full density. Below this = gradually fewer instances.")]
        [Range(0.1f, 10f)]
        public float densityStartScreenSize = 2f;

        [Tooltip("Distance (meters) where density is at full (100%). Closer = full density.")]
        [Min(0.01f)]
        public float densityStartDistance = 50f;

        [Tooltip("Distance (meters) where density reaches minimum fraction. Farther = min density.")]
        [Min(0.01f)]
        public float densityEndDistance = 150f;

        public bool overrideLodFadeDuration = false;

        [Tooltip("Animated crossfade duration in seconds for this prototype. " +
                 "Controls how long LOD transitions take. Smaller values = faster transitions.")]
        [Min(0.01f)]
        public float lodFadeDuration = 0.3f;

        [Tooltip("Minimum screen size (%) below which instances are culled. " +
                 "0 = disabled (use LODGroup defaults). " +
                 "For LOD prototypes: overrides the last LOD's cull threshold if larger. " +
                 "For non-LOD prototypes: adds screen-size culling to what would otherwise always render.")]
        [Range(0f, 5f)]
        public float minScreenSize = 0f;

        [Tooltip("Crossfade mode used at the min screen size cull boundary. " +
                 "None = hard cull. Crossfade = stateless fade zone. " +
                 "Animated = time-based fade using LOD Fade Duration from config.")]
        public MinScreenSizeFadeMode minScreenSizeFadeMode = MinScreenSizeFadeMode.None;

        [Tooltip("Fade transition width for the min screen size cull zone (0-1). " +
                 "0 = hard cull (no fade). Higher values = wider fade-to-cull zone. " +
                 "Only used with stateless Crossfade mode.")]
        [Range(0f, 1f)]
        public float minScreenSizeFadeWidth = 0f;


#if UNITY_EDITOR
        private int _prevMaxShadowCascade;
        private int _prevMinShadowLOD;
        private bool _prevOverrideMaxShadowDistance;
        private float _prevMaxShadowDistance;
        private bool _prevOverrideMaxShadowCrossfadeCascade;
        private int _prevMaxShadowCrossfadeCascade;
        private GPUBatcherPrototypeGroup _prevPrototypeGroup;
        private bool _prevEnableDistanceDensity;
        private DistanceDensityMode _prevDistanceDensityMode;
        private float _prevDensityStartScreenSize;
        private float _prevDensityStartDistance;
        private float _prevDensityEndDistance;
        private float _prevDensityMinFraction;
        private float _prevDensityFadeWidth;
        private bool _prevOverrideLodFadeDuration;
        private float _prevLodFadeDuration;
        private float _prevMinScreenSize;
        private MinScreenSizeFadeMode _prevMinScreenSizeFadeMode;
        private float _prevMinScreenSizeFadeWidth;
        private void OnValidate()
        {
            // ensure end > start
            if (densityEndDistance <= densityStartDistance)
                densityEndDistance = densityStartDistance + 0.01f;

            lodFadeDuration = math.clamp(lodFadeDuration, 0f, 10f);

            bool changed = _prevMaxShadowCascade != maxShadowCascade ||
                           _prevMinShadowLOD != minShadowLOD ||
                           _prevOverrideMaxShadowDistance != overrideMaxShadowDistance ||
                           _prevMaxShadowDistance != maxShadowDistance ||
                           _prevOverrideMaxShadowCrossfadeCascade != overrideMaxShadowCrossfadeCascade ||
                           _prevMaxShadowCrossfadeCascade != maxShadowCrossfadeCascade ||
                           _prevPrototypeGroup != prototypeGroup ||
                           _prevEnableDistanceDensity != enableDistanceDensity ||
                           _prevDistanceDensityMode != distanceDensityMode ||
                           _prevDensityStartScreenSize != densityStartScreenSize ||
                           _prevDensityStartDistance != densityStartDistance ||
                           _prevDensityEndDistance != densityEndDistance ||
                           _prevDensityMinFraction != densityMinFraction ||
                           _prevDensityFadeWidth != densityFadeWidth ||
                           _prevOverrideLodFadeDuration != overrideLodFadeDuration ||
                           _prevLodFadeDuration != lodFadeDuration ||
                           _prevMinScreenSize != minScreenSize ||
                           _prevMinScreenSizeFadeMode != minScreenSizeFadeMode ||
                           _prevMinScreenSizeFadeWidth != minScreenSizeFadeWidth;

            CachePrevValues();

            if (changed)
            {
                var root = gameObject;
                while (root.transform.parent != null)
                    root = root.transform.parent.gameObject;

                GPUBatcherRenderer.MarkPrototypeDirty(root);
            }
        }

        private void CachePrevValues()
        {
            _prevMaxShadowCascade = maxShadowCascade;
            _prevMinShadowLOD = minShadowLOD;
            _prevOverrideMaxShadowDistance = overrideMaxShadowDistance;
            _prevMaxShadowDistance = maxShadowDistance;
            _prevOverrideMaxShadowCrossfadeCascade = overrideMaxShadowCrossfadeCascade;
            _prevMaxShadowCrossfadeCascade = maxShadowCrossfadeCascade;
            _prevPrototypeGroup = prototypeGroup;
            _prevEnableDistanceDensity = enableDistanceDensity;
            _prevDistanceDensityMode = distanceDensityMode;
            _prevDensityStartScreenSize = densityStartScreenSize;
            _prevDensityStartDistance = densityStartDistance;
            _prevDensityEndDistance = densityEndDistance;
            _prevDensityMinFraction = densityMinFraction;
            _prevDensityFadeWidth = densityFadeWidth;
            _prevOverrideLodFadeDuration = overrideLodFadeDuration;
            _prevLodFadeDuration = lodFadeDuration;
            _prevMinScreenSize = minScreenSize;
            _prevMinScreenSizeFadeMode = minScreenSizeFadeMode;
            _prevMinScreenSizeFadeWidth = minScreenSizeFadeWidth;
        }

        private void Reset() => CachePrevValues();
        private void Awake() => CachePrevValues();
        private void OnEnable() => CachePrevValues();
#endif
    }
}
