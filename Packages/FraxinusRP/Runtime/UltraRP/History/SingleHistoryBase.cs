using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    // TODO: Draft. A convenience base class to wrap single texture into a type.
    internal abstract class SingleHistoryBase : CameraHistoryItem
    {
        private int _id;
        private RenderTextureDescriptor _descriptor;
        private Hash128 _descKey;

        /// <summary>
        /// Called internally on instance creation.
        /// Sets up RTHandle ids.
        /// </summary>
        public override void OnCreate(BufferedRTHandleSystem owner, uint typeId)
        {
            base.OnCreate(owner, typeId);
            _id = MakeId(0);
        }

        /// <summary>
        /// Get the history texture(s).
        /// </summary>
        public RTHandle GetTexture(int frameIndex = 0)
        {
            if ((uint)frameIndex >= GetHistoryFrameCount())
                return null;
            return storage.GetFrameRT(_id, frameIndex);
        }

        public RTHandle GetCurrentTexture()
        {
            return GetCurrentFrameRT(_id);
        }

        public RTHandle GetPreviousTexture()
        {
            return GetTexture(1);
        }

        internal bool IsAllocated()
        {
            return GetTexture() != null;
        }

        // True if the desc changed, graphicsFormat etc.
        internal bool IsDirty(ref RenderTextureDescriptor desc)
        {
            return _descKey != Hash128.Compute(ref desc);
        }

        private void Alloc(ref RenderTextureDescriptor desc)
        {
            AllocHistoryFrameRT(_id, GetHistoryFrameCount(), ref desc, GetHistoryName());

            _descriptor = desc;
            _descKey = Hash128.Compute(ref desc);
        }

        /// <summary>
        /// Release the history texture(s).
        /// </summary>
        public override void Reset()
        {
            ReleaseHistoryFrameRT(_id);
        }

        // Return true if the RTHandles were reallocated.
        internal bool Update(ref RenderTextureDescriptor cameraDesc)
        {
            if (cameraDesc.width > 0 && cameraDesc.height > 0 && cameraDesc.graphicsFormat != GraphicsFormat.None)
            {
                var historyDesc = GetHistoryDescriptor(ref cameraDesc);

                if (IsDirty(ref historyDesc))
                    Reset();

                if (!IsAllocated())
                {
                    Alloc(ref historyDesc);
                    return true;
                }
            }

            return false;
        }

        protected abstract int GetHistoryFrameCount();
        protected abstract string GetHistoryName();
        protected abstract RenderTextureDescriptor GetHistoryDescriptor(ref RenderTextureDescriptor cameraDesc);
    }
}
