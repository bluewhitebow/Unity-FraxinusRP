using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// Temporal Anti-aliasing (TAA) persistent data for UltraRP.
    /// Holds the TAA color history accumulation texture.
    /// </summary>
    public sealed class TaaHistory : CameraHistoryItem
    {
        private int[] _taaAccumulationTextureIds = new int[2];
        private int[] _taaAccumulationVersions = new int[2];

        private static readonly string[] _taaAccumulationNames = new[]
        {
            "TaaAccumulationTex0",
            "TaaAccumulationTex1"
        };

        private RenderTextureDescriptor _descriptor;
        private Hash128 _descKey;

        /// <summary>
        /// Called internally on instance creation.
        /// Sets up RTHandle ids.
        /// </summary>
        /// <param name="owner">BufferedRTHandleSystem of the owning camera.</param>
        /// <param name="typeId">Unique id given to TaaHistory by the owning camera.</param>
        public override void OnCreate(BufferedRTHandleSystem owner, uint typeId)
        {
            base.OnCreate(owner, typeId);
            _taaAccumulationTextureIds[0] = MakeId(0);
            _taaAccumulationTextureIds[1] = MakeId(1);
        }

        /// <summary>
        /// Release TAA accumulation textures.
        /// </summary>
        public override void Reset()
        {
            for (int i = 0; i < _taaAccumulationTextureIds.Length; i++)
            {
                ReleaseHistoryFrameRT(_taaAccumulationTextureIds[i]);
                _taaAccumulationVersions[i] = -1;
            }

            _descriptor.width = 0;
            _descriptor.height = 0;
            _descriptor.graphicsFormat = GraphicsFormat.None;
            _descKey = Hash128.Compute(0);
        }

        /// <summary>
        /// Get TAA accumulation texture.
        /// </summary>
        /// <param name="eyeIndex">Eye index for XR multi-pass.</param>
        /// <returns>Current frame RTHandle for TAA accumulation texture.</returns>
        public RTHandle GetAccumulationTexture(int eyeIndex = 0)
        {
            return GetCurrentFrameRT(_taaAccumulationTextureIds[eyeIndex]);
        }

        /// <summary>
        /// Get TAA accumulation texture version.
        /// Tracks which frame the accumulation was last updated.
        /// </summary>
        /// <param name="eyeIndex">Eye index for XR multi-pass.</param>
        /// <returns>Accumulation texture version.</returns>
        public int GetAccumulationVersion(int eyeIndex = 0)
        {
            return _taaAccumulationVersions[eyeIndex];
        }

        internal void SetAccumulationVersion(int eyeIndex, int version)
        {
            _taaAccumulationVersions[eyeIndex] = version;
        }

        // Check if the TAA accumulation texture is valid.
        private bool IsValid()
        {
            return GetAccumulationTexture(0) != null;
        }

        // True if the desc changed, graphicsFormat etc.
        private bool IsDirty(ref RenderTextureDescriptor desc)
        {
            return _descKey != Hash128.Compute(ref desc);
        }

        private void Alloc(ref RenderTextureDescriptor desc, bool xrMultipassEnabled)
        {
            AllocHistoryFrameRT(_taaAccumulationTextureIds[0], 1, ref desc, _taaAccumulationNames[0]);

            if (xrMultipassEnabled)
                AllocHistoryFrameRT(_taaAccumulationTextureIds[1], 1, ref desc, _taaAccumulationNames[1]);

            _descriptor = desc;
            _descKey = Hash128.Compute(ref desc);
        }

        // Return true if the RTHandles were reallocated.
        internal bool Update(ref RenderTextureDescriptor cameraDesc, bool xrMultipassEnabled = false)
        {
            if (cameraDesc.width > 0 && cameraDesc.height > 0 && cameraDesc.graphicsFormat != GraphicsFormat.None)
            {
                var taaDesc = TemporalAA.TemporalAADescFromCameraDesc(ref cameraDesc);
     
                if (IsDirty(ref taaDesc))
                    Reset();

                if (!IsValid())
                {
                    Alloc(ref taaDesc, xrMultipassEnabled);
                    return true;
                }
            }

            return false;
        }
    }
}
