using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public partial class UltraRenderPipelineRuntimeShaders
    {
        [SerializeField, ResourcePath("Shaders/Utils/HiZDepth.compute")]
        ComputeShader _hiZDepthCS;

        public ComputeShader hiZDepthCS
        {
            get => _hiZDepthCS;
            set => this.SetValueAndNotify(ref _hiZDepthCS, value);
        }
    }
}
