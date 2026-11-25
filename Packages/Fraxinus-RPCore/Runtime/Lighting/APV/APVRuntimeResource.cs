


namespace UnityEngine.Rendering
{
    public struct APVRuntimeResource : IRuntimeResources
    {
        public ComputeBuffer _index;
        public ComputeBuffer _cellIndices;

        /// <summary>
        /// Texture containing Spherical Harmonics L0 band data and first coefficient of L1_R.
        /// </summary>
        public RenderTexture L0_L1rx;
        /// <summary>
        /// Texture containing the second channel of Spherical Harmonics L1 band data and second coefficient of L1_R.
        /// </summary>
        public RenderTexture L1_G_ry;
        /// <summary>
        /// Texture containing the second channel of Spherical Harmonics L1 band data and third coefficient of L1_R.
        /// </summary>
        public RenderTexture L1_B_rz;
        /// <summary>
        /// Texture containing the first coefficient of Spherical Harmonics L2 band data and first channel of the fifth.
        /// </summary>
        public RenderTexture L2_0;
        /// <summary>
        /// Texture containing the second coefficient of Spherical Harmonics L2 band data and second channel of the fifth.
        /// </summary>
        public RenderTexture L2_1;
        /// <summary>
        /// Texture containing the third coefficient of Spherical Harmonics L2 band data and third channel of the fifth.
        /// </summary>
        public RenderTexture L2_2;
        /// <summary>
        /// Texture containing the fourth coefficient of Spherical Harmonics L2 band data.
        /// </summary>
        public RenderTexture L2_3;

        /// <summary>
        /// Texture containing 4 light occlusion coefficients for each probe.
        /// </summary>
        public RenderTexture ProbeOcclusion;

        /// <summary>
        /// Texture containing packed validity binary data for the neighbourhood of each probe. Only used when L1. Otherwise this info is stored
        /// in the alpha channel of L2_3.
        /// </summary>
        public RenderTexture Validity;

        /// <summary>
        /// Texture containing Sky Occlusion SH data (only L0 and L1 band)
        /// </summary>
        public RenderTexture SkyOcclusionL0L1;

        /// <summary>
        /// Texture containing Sky Shading direction indices
        /// </summary>
        public RenderTexture SkyShadingDirectionIndices;

        /// <summary>
        /// Precomputed table of shading directions for sky occlusion shading.
        /// </summary>
        public ComputeBuffer SkyPrecomputedDirections;
        /// <summary>
        /// Precomputed table of sampling mask for quality leak reduction.
        /// </summary>
        public ComputeBuffer QualityLeakReductionData;

        public ComputeBuffer index { get => _index; set => _index = value; }
        public ComputeBuffer cellIndices { get => _cellIndices; set => _cellIndices = value; }
    }
}
