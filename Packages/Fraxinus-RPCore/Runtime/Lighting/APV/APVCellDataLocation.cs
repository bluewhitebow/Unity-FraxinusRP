namespace UnityEngine.Rendering
{
    public struct APVDataLocation : ProbeBrickPoolBase.IDataLocation
    {
        internal Texture TexL0_L1rx;

        internal Texture TexL1_G_ry;
        internal Texture TexL1_B_rz;

        internal Texture TexL2_0;
        internal Texture TexL2_1;
        internal Texture TexL2_2;
        internal Texture TexL2_3;

        internal Texture TexProbeOcclusion;

        internal Texture TexValidity;
        internal Texture TexSkyOcclusion;
        internal Texture TexSkyShadingDirectionIndices;

        private int _width;
        private int _height;
        private int _depth;

        public int width { get => _width; set => _width = value; }
        public int height { get => _height; set => _height = value; }
        public int depth { get => _depth; set => _depth = value; }

        public bool Valid()
        {
            return TexL0_L1rx != null;
        }

        public void Cleanup()
        {
            CoreUtils.Destroy(TexL0_L1rx);

            CoreUtils.Destroy(TexL1_G_ry);
            CoreUtils.Destroy(TexL1_B_rz);

            CoreUtils.Destroy(TexL2_0);
            CoreUtils.Destroy(TexL2_1);
            CoreUtils.Destroy(TexL2_2);
            CoreUtils.Destroy(TexL2_3);

            CoreUtils.Destroy(TexProbeOcclusion);

            CoreUtils.Destroy(TexValidity);
            CoreUtils.Destroy(TexSkyOcclusion);
            CoreUtils.Destroy(TexSkyShadingDirectionIndices);

            TexL0_L1rx = null;

            TexL1_G_ry = null;
            TexL1_B_rz = null;

            TexL2_0 = null;
            TexL2_1 = null;
            TexL2_2 = null;
            TexL2_3 = null;
            TexProbeOcclusion = null;
            TexValidity = null;
            TexSkyOcclusion = null;
            TexSkyShadingDirectionIndices = null;
        }
    }
}