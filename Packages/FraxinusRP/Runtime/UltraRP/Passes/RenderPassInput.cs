namespace FraxinusEngine.Rendering.Ultra
{
    public struct RenderPassInputSummary
    {
        public bool requiresIntermediateTexture;
        public bool requiresDepthTexture;
        public bool requiresColorTexture;
        public bool requiresColorTextureCreated;
        public bool requiresMotionVectors;
        public bool requiresNormalsTexture;
        public bool requiresDepthPrepass;
        // Early screen-space consumers need all opaque depth, including deferred materials.
        public bool requiresFullDepthPrepass;
        public bool disableFramebufferFetch;
    }
}
