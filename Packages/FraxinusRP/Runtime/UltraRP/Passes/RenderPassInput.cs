namespace FraxinusEngine.Rendering.Ultra
{
    public struct RenderPassInputSummary
    {
        // Explicit camera-color requirement; depth-only consumers need not request this.
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

        /// <summary>Combines a pass's requirements without clearing requests from other passes.</summary>
        public void MergeFrom(in RenderPassInputSummary requirements)
        {
            requiresIntermediateTexture |= requirements.requiresIntermediateTexture;
            requiresDepthTexture |= requirements.requiresDepthTexture;
            requiresColorTexture |= requirements.requiresColorTexture;
            requiresColorTextureCreated |= requirements.requiresColorTextureCreated;
            requiresMotionVectors |= requirements.requiresMotionVectors;
            requiresNormalsTexture |= requirements.requiresNormalsTexture;
            requiresDepthPrepass |= requirements.requiresDepthPrepass;
            requiresFullDepthPrepass |= requirements.requiresFullDepthPrepass;
            disableFramebufferFetch |= requirements.disableFramebufferFetch;
        }
    }
}
