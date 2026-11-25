using System;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public interface IRendererModule : IDisposable
    {
        public void OnCameraCleanup(CommandBuffer cmd);
    }
}
