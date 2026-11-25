using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Raw render depth history. The depth snapshot is taken before post processing.
    /// A matching color is RawColorHistory.
    /// Format is the camera depth format or R32Float on platforms with limitations.
    /// If TemporalAA is enabled the depth is jittered.
    /// No mips. No depth pyramid.
    /// MSAA is not supported and is resolved for the history.
    /// XR is supported.
    /// </summary>
    public sealed class RawDepthHistory : CameraHistoryItem
    {
        private int[] _ids = new int[2];
        private static readonly string[] _names = new[]
        {
            "RawDepthHistory0",
            "RawDepthHistory1"
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
            // Generic type, we need double buffering.
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
            var depthDesc = cameraDesc;
            depthDesc.mipCount = 0;
            depthDesc.msaaSamples = 1;  // History copy should not have MSAA.

            return depthDesc;
        }

        // Return true if the RTHandles were reallocated.
        internal bool Update(ref RenderTextureDescriptor cameraDesc, bool xrMultipassEnabled)
        {
            if (cameraDesc.width > 0 && cameraDesc.height > 0 && (cameraDesc.depthStencilFormat != GraphicsFormat.None || cameraDesc.graphicsFormat != GraphicsFormat.None) )
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
