namespace UnityEngine.Rendering
{
    public interface IRuntimeResources
    {
        /// <summary>
        /// Index data to fetch the correct location in the Texture3D.
        /// </summary>
        ComputeBuffer index { get; set; }

        /// <summary>
        /// Indices of the various index buffers for each cell.
        /// </summary>
        ComputeBuffer cellIndices { get; set; }
    }
}
