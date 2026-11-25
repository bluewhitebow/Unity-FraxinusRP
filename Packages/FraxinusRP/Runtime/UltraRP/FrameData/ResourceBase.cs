using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace FraxinusEngine.Rendering.Ultra
{
    public abstract class ResourceBase : ContextItem
    {
        internal enum ActiveID
        {
            /// <summary>The camera buffer.</summary>
            Camera,

            /// <summary>The backbuffer.</summary>
            BackBuffer
        }

        internal bool isAccessible { get; set; }

        internal void InitFrame()
        {
            isAccessible = true;
        }

        internal void EndFrame()
        {
            isAccessible = false;
        }

        protected void CheckAndSetTextureHandle(ref TextureHandle handle, TextureHandle newHandle)
        {
            if (!CheckAndWarnAboutAccessibility())
                return;

            handle = newHandle;
        }

        protected TextureHandle CheckAndGetTextureHandle(ref TextureHandle handle)
        {
            if (!CheckAndWarnAboutAccessibility())
                return TextureHandle.nullHandle;

            return handle;
        }

        protected void CheckAndSetTextureHandle(ref TextureHandle[] handle, TextureHandle[] newHandle)
        {
            if (!CheckAndWarnAboutAccessibility())
                return;

            if (handle == null || handle.Length != newHandle.Length)
                handle = new TextureHandle[newHandle.Length];

            for (int i = 0; i < newHandle.Length; i++)
                handle[i] = newHandle[i];
        }

        protected TextureHandle[] CheckAndGetTextureHandle(ref TextureHandle[] handle)
        {
            if (!CheckAndWarnAboutAccessibility())
                return new[] { TextureHandle.nullHandle };

            return handle;
        }

        protected void CheckAndSetBufferHandle(ref BufferHandle handle, BufferHandle newHandle)
        {
            if (!CheckAndWarnAboutAccessibility())
                return;

            handle = newHandle;
        }

        protected BufferHandle CheckAndGetBufferHandle(ref BufferHandle handle)
        {
            if (!CheckAndWarnAboutAccessibility())
                return BufferHandle.nullHandle;

            return handle;
        }

        protected bool CheckAndWarnAboutAccessibility()
        {
            if (!isAccessible)
                Debug.LogError("Trying to access Ultra Resources outside of the current frame setup.");

            return isAccessible;
        }
    }
}
