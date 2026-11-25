using System;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public abstract class BasePostPass : IDisposable
    {
        protected string _passName;
        protected ProfilingSampler _profilingSampler;

        protected PostProcessPass _postProcessPass;


        public BasePostPass()
        {
            _passName = this.GetType().Name;
            _profilingSampler = new ProfilingSampler(this.GetType().Name);
        }

        public virtual bool Setup(bool enable, VolumeStack stack) { return enable; }

        public void Init(PostProcessPass postProcessPass)
        {
            _postProcessPass = postProcessPass;
        }


        public virtual ScriptableRenderPassInput renderInput => ScriptableRenderPassInput.None;

        public abstract TextureHandle Render(RenderGraph renderGraph, ContextContainer frameData, in TextureHandle activeCameraColor);

        void IDisposable.Dispose()
        {
            OnDispose();
        }

        protected abstract void OnDispose();
    }

}
