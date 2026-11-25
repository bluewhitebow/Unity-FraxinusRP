namespace FraxinusEditor.Rendering.Ultra
{
    public static class UltraRPProperty
    {
        // Surface Options
        public static readonly string SpecularWorkflowMode = "_WorkflowMode";
        public static readonly string SurfaceType = "_Surface";
        public static readonly string BlendMode = "_Blend";
        public static readonly string BlendModePreserveSpecular = "_BlendModePreserveSpecular";
        public static readonly string AlphaClip = "_AlphaClip";
        public static readonly string AlphaClipThreshold = "_Cutoff";
        public static readonly string AlphaToMask = "_AlphaToMask";

        // Blend
        public static readonly string SrcBlend = "_SrcBlend";
        public static readonly string DstBlend = "_DstBlend";
        public static readonly string SrcBlendAlpha = "_SrcBlendAlpha";
        public static readonly string DstBlendAlpha = "_DstBlendAlpha";

        // Depth
        public static readonly string ZWrite = "_ZWrite";
        public static readonly string ZWriteControl = "_ZWriteControl";
        public static readonly string ZTest = "_ZTest";

        // Cull
        public static readonly string CullMode = "_Cull";

        // Shadows
        public static readonly string CastShadows = "_CastShadows";
        public static readonly string ReceiveShadows = "_ReceiveShadows";

        // Emission
        public static readonly string EmissionMap = "_EmissionMap";
        public static readonly string EmissionColor = "_EmissionColor";

        // Queue
        public static readonly string QueueOffset = "_QueueOffset";
        public static readonly string QueueControl = "_QueueControl";

        // Motion Vectors
        public static readonly string AddPrecomputedVelocity = "_AddPrecomputedVelocity";
    }
}
