using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    [Flags]
    public enum ScriptableRenderPassInput
    {
        None    = 0,
        Depth   = 1 << 0,
        Normal  = 1 << 1,
        Color   = 1 << 2,
        Motion  = 1 << 3,
    }

    public abstract partial class ScriptableRenderPassNode
    {
        private static partial class Profiling
        {
            private const string k_name = nameof(ScriptableRenderPassNode);
            public static readonly ProfilingSampler pullDatasSampler = new ProfilingSampler($"{k_name}.PullDatas");
            public static readonly ProfilingSampler pushDatasSampler = new ProfilingSampler($"{k_name}.PushDats");
            public static readonly ProfilingSampler preCullSampler = new ProfilingSampler($"{k_name}.PreCull");
            public static readonly ProfilingSampler excuteSampler = new ProfilingSampler($"{k_name}.Excute");
        }

        private ProfilingSampler _profingSampler;
        private string _passName;

        public static RTHandle cameraTarget = RTHandles.Alloc(BuiltinRenderTextureType.CameraTarget);

        public virtual ScriptableRenderPassInput input => ScriptableRenderPassInput.None;

        [SerializeField, HideInInspector]
        public RenderingMode renderingModes = RenderingMode.None;

        public bool requiresIntermediateTexture { get; set; }

        [SerializeField, HideInInspector]
        public RenderOrder renderOrder = RenderOrder.Default;

        private List<InjectPass> _beforeRenderingPass = new List<InjectPass>();
        private List<InjectPass> _afterRenderingPass = new List<InjectPass>();
        private readonly List<ContextScopeFilter> _beforeScopeFilters = new();
        private readonly List<ContextScopeFilter> _afterScopeFilters = new();
        private List<InjectPass> _fallbackRenderingPass;
        private ScriptableRenderer _injectionFallbackRenderer;

        public IReadOnlyList<InjectPass> beforeRenderingPasses => _beforeRenderingPass;
        public IReadOnlyList<InjectPass> afterRenderingPasses => _afterRenderingPass;
        public IReadOnlyList<InjectPass> fallbackRenderingPasses => _fallbackRenderingPass ?? (IReadOnlyList<InjectPass>)Array.Empty<InjectPass>();


        protected internal ProfilingSampler profilingSampler
        {
            get
            {
#if (DEVELOPMENT_BUILD || UNITY_EDITOR)
                return _profingSampler;
#else 
                //We only remove the sampler in release build to reduce overhead
                return null;
#endif
            }
            set
            {
                _profingSampler = value;
                _passName = (value != null) ? value.name : this.GetType().Name;
            }
        }

        protected internal string passName { get { return _passName; } }

        public virtual LayerMask GetOpaqueLayerMask()
        {
            return 0;
        }

        static internal DebugHandler GetActiveDebugHandler(CameraData cameraData)
        {
            var debugHandler = cameraData.renderer.debugHandler;
            if ((debugHandler != null) && debugHandler.IsActiveForCamera(cameraData.isPreviewCamera))
                return debugHandler;

            return null;
        }

        public DrawingSettings CreateDrawingSettings(ShaderTagId shaderTagId, ref ContextContainer frameData, SortingCriteria sortingCriteria)
        {
            RenderingData renderingData = frameData.Get<RenderingData>();
            CameraData cameraData = frameData.Get<CameraData>();
            LightData lightData = frameData.Get<LightData>();

            return RenderingUtils.CreateDrawingSettings(shaderTagId, renderingData, cameraData, lightData, sortingCriteria);
        }

        public virtual bool SupportedInCurrentFrame(ContextContainer frameData)
        {
            return true;
        }

        public virtual RenderingMode RequestRenderingMode()
        {
            return RenderingMode.None;
        }

        internal void ProcessNodeInjectPasses(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData, bool isBefore = true)
        {
            var passes = isBefore ? _beforeRenderingPass : _afterRenderingPass;
            if (passes.Count == 0 && (!isBefore || _injectionFallbackRenderer == null))
                return;
            if (!active || !SupportedInCurrentFrame(frameData))
                return;
            var state = frameData.GetOrCreate<ContextStateManager>();
            var filters = isBefore ? _beforeScopeFilters : _afterScopeFilters;
            for (int i = 0; i < passes.Count; i++)
                if (graph.IsInjectPassEnabled(passes[i].GetType()) && state.TryBeginInjection(passes[i], filters[i]))
                    passes[i].Execute(renderGraph, context, frameData);
            if (isBefore)
                _injectionFallbackRenderer?.ProcessFallbackInjections(renderGraph, context, frameData);
        }

        internal void AddInjectPass(InjectPass pass, InjectionPoint point, ContextScopeFilter filter = ContextScopeFilter.All)
        {
            var passes = point == InjectionPoint.Before ? _beforeRenderingPass : _afterRenderingPass;
            var filters = point == InjectionPoint.Before ? _beforeScopeFilters : _afterScopeFilters;
            int index = passes.IndexOf(pass);
            if (index >= 0)
            {
                // Multiple attributes can resolve to the same node and injection point.
                // Keep one entry without losing any declared scope eligibility.
                filters[index] |= filter;
                return;
            }

            passes.Add(pass);
            filters.Add(filter);
        }


        internal void ClearAllInjectPasses()
        {
            _beforeRenderingPass.Clear();
            _afterRenderingPass.Clear();
            _beforeScopeFilters.Clear();
            _afterScopeFilters.Clear();
            _fallbackRenderingPass?.Clear();
            _injectionFallbackRenderer = null;
        }

        internal void SetInjectionFallbackRenderer(ScriptableRenderer renderer)
        {
            _injectionFallbackRenderer = renderer;
        }

        internal void AddFallbackInjectPass(InjectPass pass)
        {
            (_fallbackRenderingPass ??= new List<InjectPass>()).Add(pass);
        }
    }
}
