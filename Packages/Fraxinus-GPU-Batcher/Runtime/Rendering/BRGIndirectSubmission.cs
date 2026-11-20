using System;
using UnityEngine.Rendering;

namespace Fraxinus.GPUBatcher.Rendering
{
    /// <summary>
    /// Submission boundary for native BRG indirect commands.
    /// The backend is currently a placeholder, including on newer Unity versions.
    /// </summary>
    public static class BRGIndirectSubmission
    {
        public static bool IsAvailable => false;

        /// <summary>
        /// Currently returns false without allocating, submitting draws or modifying output.
        /// The caller retains ownership of all inputs and referenced GPU buffers.
        /// </summary>
        /// <param name="output">The BRG culling callback output.</param>
        /// <param name="commands">Indirect commands prepared by the caller.</param>
        /// <param name="ranges">
        /// Ranges indexing commands. A future native adapter must mark these as indirect ranges.
        /// </param>
        /// <returns>False until a native submission backend is implemented.</returns>
        public static bool TrySubmit(
            in BatchCullingOutput output,
            ReadOnlySpan<BatchDrawCommandIndirect> commands,
            ReadOnlySpan<BatchDrawRange> ranges)
        {
            // TODO: Convert to native indirect commands and submit when the API is available.
            return false;
        }
    }
}
