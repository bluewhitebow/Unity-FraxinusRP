using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Raw render color history. The color snapshot is taken before post processing. Raw rendered geometry. No UI overlay.
    /// A matching depth is RawDepthHistory.
    /// Color space is linear RGB.
    /// No mips.
    /// MSAA is not supported and is resolved for the history.
    /// XR is supported.
    /// </summary>
    public sealed class RawColorHistory : CameraHistoryItem
    {
        private int[] _ids = new int[2];
        private static readonly string[] _names = new[]
        {
            "RawColorHistory0",
            "RawColorHistory1"
        };
        private RenderTextureDescriptor _descriptor;
        private Hash128 _descKey;

        /// <inheritdoc />
        public override void OnCreate(BufferedRTHandleSystem owner, uint typeId)
        {
            base.OnCreate(owner, typeId);
            _ids[0] = MakeId(0);
            _ids[1] = MakeId(1);
        }

        /// <summary>
        /// Get the current history texture.
        /// Current history might not be valid yet. It is valid only after executing the producing render pass.
        /// </summary>
        /// <param name="eyeIndex">Eye index, typically XRPass.multipassId.</param>
        /// <returns>The texture.</returns>
        public RTHandle GetCurrentTexture(int eyeIndex = 0)
        {
            if ((uint)eyeIndex >= _ids.Length)
                return null;

            return GetCurrentFrameRT(_ids[eyeIndex]);
        }

        /// <summary>
        /// Get the previous history texture.
        /// Previous history might not be valid yet. It is valid only after executing the producing render pass.
        /// </summary>
        /// <param name="eyeIndex">Eye index, typically XRPass.multipassId.</param>
        /// <returns>The texture.</returns>
        public RTHandle GetPreviousTexture(int eyeIndex = 0)
        {
            if ((uint)eyeIndex >= _ids.Length)
                return null;

            return GetPreviousFrameRT(_ids[eyeIndex]);
        }

        private bool IsAllocated()
        {
            return GetCurrentTexture() != null;
        }

        // True if the desc changed, graphicsFormat etc.
        private bool IsDirty(ref RenderTextureDescriptor desc)
        {
            return _descKey != Hash128.Compute(ref desc);
        }

        private void Alloc(ref RenderTextureDescriptor desc, bool xrMultipassEnabled)
        {
            // In generic case, the current texture might not have been written yet. We need double buffering.
            AllocHistoryFrameRT(_ids[0], 2, ref desc, _names[0]);

            if(xrMultipassEnabled)
                AllocHistoryFrameRT(_ids[1], 2, ref desc, _names[1]);

            _descriptor = desc;
            _descKey = Hash128.Compute(ref desc);
        }

        /// <summary>
        /// Release the history texture(s).
        /// </summary>
        public override void Reset()
        {
            for(int i = 0; i < _ids.Length; i++)
                ReleaseHistoryFrameRT(_ids[i]);
        }

        internal RenderTextureDescriptor GetHistoryDescriptor(ref RenderTextureDescriptor cameraDesc)
        {
            var colorDesc = cameraDesc;
            colorDesc.depthStencilFormat = GraphicsFormat.None;
            colorDesc.mipCount = 0;
            colorDesc.msaaSamples = 1;

            return colorDesc;
        }

        // Return true if the RTHandles were reallocated.
        internal bool Update(ref RenderTextureDescriptor cameraDesc, bool xrMultipassEnabled = false)
        {
            if (cameraDesc.width > 0 && cameraDesc.height > 0 && cameraDesc.graphicsFormat != GraphicsFormat.None)
            {
                var historyDesc = GetHistoryDescriptor(ref cameraDesc);

                if (IsDirty(ref historyDesc))
                    Reset();

                if (!IsAllocated())
                {
                    Alloc(ref historyDesc, xrMultipassEnabled);
                    return true;
                }
            }

            return false;
        }
    }
}
