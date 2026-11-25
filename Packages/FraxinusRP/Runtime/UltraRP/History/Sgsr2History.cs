using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Persistent output-resolution history used by SGSR2.
    ///
    /// SGSR2 reconstructs directly into the display resolution, while the
    /// camera color buffer can be rendered at a lower render scale.  The
    /// regular <see cref="TaaHistory"/> is therefore not suitable: it is
    /// deliberately allocated at the render resolution and is consumed by
    /// the pre-upscale TAA pass.  This item owns a separate double-buffered
    /// pair at <c>pixelWidth x pixelHeight</c>.
    /// </summary>
    internal sealed class Sgsr2History : CameraHistoryItem
    {
        private readonly int[] m_Ids = new int[2];
        private readonly int[] m_Versions = { -1, -1 };
        private readonly int[] m_LastRepeatedFrames = { -1, -1 };
        private static readonly string[] s_Names =
        {
            "Sgsr2OutputHistory0",
            "Sgsr2OutputHistory1"
        };

        private Hash128 m_DescKey;
        private bool m_XrMultipassEnabled;
        private Vector2Int m_RenderSize;

        // The non-jittered matrix is retained for the no-motion-vector path.
        // Keeping this state with the camera history makes camera resets and
        // resolution changes invalidate reprojection atomically with color.
        private readonly Matrix4x4[] m_PreviousViewProjection =
        {
            Matrix4x4.identity,
            Matrix4x4.identity
        };
        private readonly bool[] m_HasPreviousViewProjection = new bool[2];

        public override void OnCreate(BufferedRTHandleSystem owner, uint typeId)
        {
            base.OnCreate(owner, typeId);
            m_Ids[0] = MakeId(0);
            m_Ids[1] = MakeId(1);
        }

        public override void Reset()
        {
            for (int i = 0; i < m_Ids.Length; ++i)
            {
                ReleaseHistoryFrameRT(m_Ids[i]);
                m_Versions[i] = -1;
                m_LastRepeatedFrames[i] = -1;
                m_PreviousViewProjection[i] = Matrix4x4.identity;
                m_HasPreviousViewProjection[i] = false;
            }

            m_DescKey = Hash128.Compute(0);
            m_XrMultipassEnabled = false;
            m_RenderSize = default;
        }

        internal RTHandle GetCurrentTexture(int eyeIndex = 0)
        {
            if ((uint)eyeIndex >= m_Ids.Length)
                return null;

            return GetCurrentFrameRT(m_Ids[eyeIndex]);
        }

        internal RTHandle GetPreviousTexture(int eyeIndex = 0)
        {
            if ((uint)eyeIndex >= m_Ids.Length)
                return null;

            return GetPreviousFrameRT(m_Ids[eyeIndex]);
        }

        internal int GetAccumulationVersion(int eyeIndex = 0)
        {
            return (uint)eyeIndex < m_Versions.Length ? m_Versions[eyeIndex] : -1;
        }

        internal void SetAccumulationVersion(int eyeIndex, int version)
        {
            if ((uint)eyeIndex < m_Versions.Length)
            {
                if (m_Versions[eyeIndex] == version)
                    m_LastRepeatedFrames[eyeIndex] = version;
                m_Versions[eyeIndex] = version;
            }
        }

        internal int GetLastRepeatedFrame(int eyeIndex = 0)
        {
            return (uint)eyeIndex < m_LastRepeatedFrames.Length ? m_LastRepeatedFrames[eyeIndex] : -1;
        }

        internal Matrix4x4 GetPreviousViewProjection(int eyeIndex = 0)
        {
            return (uint)eyeIndex < m_PreviousViewProjection.Length
                ? m_PreviousViewProjection[eyeIndex]
                : Matrix4x4.identity;
        }

        internal bool HasPreviousViewProjection(int eyeIndex = 0)
        {
            return (uint)eyeIndex < m_HasPreviousViewProjection.Length && m_HasPreviousViewProjection[eyeIndex];
        }

        internal void SetPreviousViewProjection(int eyeIndex, Matrix4x4 viewProjection)
        {
            if ((uint)eyeIndex >= m_PreviousViewProjection.Length)
                return;

            m_PreviousViewProjection[eyeIndex] = viewProjection;
            m_HasPreviousViewProjection[eyeIndex] = true;
        }

        private bool IsAllocated(int eyeIndex)
        {
            return GetCurrentTexture(eyeIndex) != null;
        }

        private bool IsDirty(ref RenderTextureDescriptor descriptor)
        {
            return m_DescKey != Hash128.Compute(ref descriptor);
        }

        private void Alloc(ref RenderTextureDescriptor descriptor, bool xrMultipassEnabled)
        {
            // Two frames are required: the current output is written while
            // the previous output is sampled by the temporal resolve.
            AllocHistoryFrameRT(m_Ids[0], 2, ref descriptor, s_Names[0]);

            if (xrMultipassEnabled)
                AllocHistoryFrameRT(m_Ids[1], 2, ref descriptor, s_Names[1]);

            m_DescKey = Hash128.Compute(ref descriptor);
            m_XrMultipassEnabled = xrMultipassEnabled;
        }

        private static RenderTextureDescriptor MakeDescriptor(
            ref RenderTextureDescriptor cameraDescriptor,
            int outputWidth,
            int outputHeight)
        {
            var descriptor = cameraDescriptor;
            descriptor.width = Mathf.Max(1, outputWidth);
            descriptor.height = Mathf.Max(1, outputHeight);
            descriptor.msaaSamples = 1;
            descriptor.depthStencilFormat = GraphicsFormat.None;
            descriptor.mipCount = 0;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.memoryless = RenderTextureMemoryless.None;
            descriptor.enableRandomWrite = false;
            descriptor.bindMS = false;
            descriptor.useDynamicScale = false;

            // Sample and render support are both required for temporal feedback.
            const GraphicsFormatUsage usage = GraphicsFormatUsage.Render | GraphicsFormatUsage.Sample | GraphicsFormatUsage.Linear;
            if (descriptor.graphicsFormat == GraphicsFormat.None ||
                !RenderingUtils.SupportsGraphicsFormat(descriptor.graphicsFormat, usage))
            {
                descriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            }

            return descriptor;
        }

        /// <summary>
        /// Ensures output-resolution history is allocated for this camera.
        /// Returns <c>true</c> when a new allocation (and therefore a history
        /// reset) occurred.
        /// </summary>
        internal bool Update(ref RenderTextureDescriptor cameraDescriptor,
            int outputWidth,
            int outputHeight,
            bool xrMultipassEnabled = false)
        {
            if (cameraDescriptor.width <= 0 || cameraDescriptor.height <= 0 || outputWidth <= 0 || outputHeight <= 0)
                return false;

            var descriptor = MakeDescriptor(ref cameraDescriptor, outputWidth, outputHeight);
            var renderSize = new Vector2Int(cameraDescriptor.width, cameraDescriptor.height);

            if (IsDirty(ref descriptor) || m_XrMultipassEnabled != xrMultipassEnabled || m_RenderSize != renderSize)
                Reset();

            if (!IsAllocated(0) || (xrMultipassEnabled && !IsAllocated(1)))
            {
                Alloc(ref descriptor, xrMultipassEnabled);
                m_RenderSize = renderSize;
                return true;
            }

            return false;
        }
    }
}
