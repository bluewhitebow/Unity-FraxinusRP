#if HAS_VFX_GRAPH

namespace UnityEditor.VFX.UltraRP
{
    class VFXUltraRPSubOutput : VFXSRPSubOutput
    {
        //UltraRP only support motion vector on opaque geometry (with or without ShaderGraph)
        public override bool supportsMotionVector => owner.isBlendModeOpaque;
    }
}
#endif
