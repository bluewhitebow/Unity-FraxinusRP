using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using FraxinusEngine.Base;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// 上下文桥接节点
    /// 用于将子作用域的渲染结果桥接到主作用域，建立执行依赖
    /// 不执行任何渲染操作，仅作为作用域间的连接桥梁
    /// </summary>
    [Serializable, NodeMenuItem("Context/ContextBridge")]
    public class ContextBridgeNode : ScriptableRenderPassNode
    {
        [Input("Camera Target")]
        public CameraTargetBufferNodeParameter inCameraTarget;
        
        [Input("Color Texture")]
        public TextureHandleNodeParameter inColorTexture;
        

        [Output("Camera Target", allowMultiple = true)]
        public CameraTargetBufferNodeParameter outCameraTarget;
        
        public ContextBridgeNode() : base()
        {
            profilingSampler = new ProfilingSampler(nameof(ContextBridgeNode));
        }
        
        public override void SetupPass()
        {
            ConfigurePortValue(outCameraTarget, inCameraTarget.value);
        }
        
        public override void Execute(RenderGraph renderGraph, ScriptableRenderContext context, ContextContainer frameData)
        {

        }
    }
}
