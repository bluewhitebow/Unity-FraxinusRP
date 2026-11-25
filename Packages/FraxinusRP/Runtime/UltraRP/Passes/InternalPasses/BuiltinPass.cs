using System;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public abstract class BuiltinPass : IRendererModule
    {
        protected string _passName;
        protected ProfilingSampler _profilingSampler;

        public BuiltinPass()
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


        protected abstract void OnDispose();

        protected abstract void OnCameraCleanup(CommandBuffer cmd);
    }

}
