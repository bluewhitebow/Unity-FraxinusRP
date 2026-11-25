namespace UnityEngine.Rendering
{
    public partial struct GPUSort
    {
        /// <summary>
        /// Data structure containing runtime dispatch parameters for the sort.
        /// </summary>
        public struct Args
        {
            /// <summary>Count</summary>
            public uint             count;
            /// <summary>Defines the maximum height of the bitonic sort. By default, should be the same as count for a full sort.</summary>
            public uint             maxDepth;
            /// <summary>Input Keys</summary>
            public GraphicsBuffer   inputKeys;
            /// <summary>Input Values</summary>
            public GraphicsBuffer   inputValues;
            /// <summary>Required runtime resources.</summary>
            public SupportResources resources;

            internal int workGroupCount;
        }

        /// <summary>
        /// Data structure containing the runtime resources that are bound by the command buffer.
        /// </summary>
        public struct SupportResources
        {
            /// <summary>Sorted key buffer.</summary>
            public GraphicsBuffer sortBufferKeys;
            /// <summary>Sorted values buffer.</summary>
            public GraphicsBuffer sortBufferValues;

            /// <summary>
            /// Dispose the supporting resources.
            /// </summary>
            public void Dispose()
            {
                if (sortBufferKeys != null)
                {
                    sortBufferKeys.Dispose();
                    sortBufferKeys = null;
                }

                if (sortBufferValues != null)
                {
                    sortBufferValues.Dispose();
                    sortBufferValues = null;
                }
            }
        }

        /// <summary>
        /// Structure defining any required assets used by the GPU sort.
        /// </summary>
        public struct SystemResources
        {
            /// <summary>
            /// The compute asset that defines all of the kernels for the GPU sorting.
            /// </summary>
            public ComputeShader computeAsset;
        }
    }
}
