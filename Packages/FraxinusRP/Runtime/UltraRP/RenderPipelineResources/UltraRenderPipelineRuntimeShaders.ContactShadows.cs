using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    public partial class UltraRenderPipelineRuntimeShaders
    {
        [SerializeField, ResourcePath("Shaders/Lighting/ContactShadows.compute")]
        ComputeShader _contactShadowsCS;

        public ComputeShader contactShadowsCS
        {
            get => _contactShadowsCS;
            set => this.SetValueAndNotify(ref _contactShadowsCS, value);
        }
    }
}
