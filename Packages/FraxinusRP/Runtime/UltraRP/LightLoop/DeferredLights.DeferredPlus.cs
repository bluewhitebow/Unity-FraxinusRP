using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public partial class DeferredLights
    {
        Material _deferredPlusMaterial;

        internal bool useDeferredPlus { get; private set; }
        internal Material deferredPlusMaterial => _deferredPlusMaterial;

        void EnsureDeferredPlusMaterial()
        {
            var shader = _runtimeShaders?.deferredPlusPS;
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("Deferred+ requires a supported shader in UltraRenderPipelineRuntimeShaders.deferredPlusPS.");

            if (_deferredPlusMaterial == null || _deferredPlusMaterial.shader != shader)
            {
                CoreUtils.Destroy(_deferredPlusMaterial);
                _deferredPlusMaterial = CoreUtils.CreateEngineMaterial(shader);
            }

            if (_deferredPlusMaterial.passCount < (int)ShadingModels.CurModelsNum)
                throw new InvalidOperationException("Deferred+ requires a lighting pass for every shading model.");
        }
    }
}
