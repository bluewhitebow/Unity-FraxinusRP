using System;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public enum InjectionPoint
    {
        Before,
        After,
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class InjectPassAttribute : Attribute
    {
        public Type targetPassType;
        public InjectionPoint point;
        public bool finalInjectPostPass;
        
        /// <summary>
        /// 作用域过滤器：控制此InjectPass在哪些作用域中注入
        /// 默认为All（所有作用域）
        /// </summary>
        public ContextScopeFilter scopeFilter = ContextScopeFilter.All;

        public InjectPassAttribute(Type targetPassType, InjectionPoint point, bool finalInjectPostPass = false)
        {
            this.targetPassType = targetPassType;
            this.point = point;
            this.finalInjectPostPass = finalInjectPostPass;
        }
    }

    public abstract class InjectPass : IRendererModule
    {
        protected string _passName;
        protected ProfilingSampler _profilingSampler;

        internal ContextScopeFilter fallbackScopeFilter { get; set; }

        public InjectPass()
        {
            _passName = this.GetType().Name;
            _profilingSampler = new ProfilingSampler(this.GetType().Name);
        }

        void IRendererModule.OnCameraCleanup(CommandBuffer cmd)
        {
            OnCameraCleanup(cmd);
        }

        void IDisposable.Dispose()
        {
            OnDispose();
        }

        public abstract void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData);

        /// <summary>
        /// Clears per-camera resources and shader state, including when this pass is disabled.
        /// Do not request input textures from this hook.
        /// </summary>
        public virtual void ResetFrame(RenderGraph renderGraph, ContextContainer frameData) { }

        /// <summary>
        /// Called once per camera for enabled, bound passes before texture allocation.
        /// Merge texture requirements into RenderingData.renderPassInputs here.
        /// Return true when this effect requires an intermediate camera target and final blit.
        /// </summary>
        public virtual bool PrepareFrame(RenderGraph renderGraph, ContextContainer frameData) => false;

        public virtual bool IsEligibleToInject(ScriptableRenderer renderer, ScriptableRenderPassNode node)
        {
            return true;
        }

        protected abstract void OnDispose();

        protected abstract void OnCameraCleanup(CommandBuffer cmd);
    }
}

