using System;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    public class RTHandleNodeParameter : NodeParameter<RTHandle>
    {
        public RTHandleNodeParameter() : this(default) { }

        public RTHandleNodeParameter(RTHandle value) : base(value) { }
    }

    public class TextureHandleNodeParameter : NodeParameter<TextureHandle>
    {
        public TextureHandleNodeParameter() : this(default) { }

        public TextureHandleNodeParameter(TextureHandle value) : base(value) { }
    }

    public class TextureHandlesNodeParameter : NodeParameter<TextureHandle[]>
    {
        public TextureHandlesNodeParameter() : this(default) { }

        public TextureHandlesNodeParameter(TextureHandle[] value) : base(value) { }
    }

    /// <summary>
    /// 相机目标缓冲区，封装 Color 和 Depth 纹理，确保它们总是成对连接
    /// </summary>
    [Serializable]
    public struct CameraTargetBuffer
    {
        public TextureHandle color;
        public TextureHandle depth;

        public CameraTargetBuffer(TextureHandle color, TextureHandle depth)
        {
            this.color = color;
            this.depth = depth;
        }

        public bool IsValid()
        {
            return color.IsValid() && depth.IsValid();
        }
    }

    public class CameraTargetBufferNodeParameter : NodeParameter<CameraTargetBuffer>
    {
        public CameraTargetBufferNodeParameter() : this(default) { }

        public CameraTargetBufferNodeParameter(CameraTargetBuffer value) : base(value) { }
    }
}