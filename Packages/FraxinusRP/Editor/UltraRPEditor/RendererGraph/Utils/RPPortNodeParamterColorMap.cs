using System;
using System.Collections.Generic;
using UnityEngine;
using FraxinusEngine.Rendering.Ultra;

using FraxinusEditor.Base;

namespace FraxinusEditor.Rendering.Ultra
{
    public class RPPortNodeParamterColorMap : IPortValueTypeColorMap
    {
        public override IEnumerable<(Type, Color)> GetCompatiblePortColorMap()
        {
            // 如果端口配置了渐变，颜色会自动从渐变中获取，不需要在这里重复配置
            yield break;
        }

        public override IEnumerable<(Type, Color startColor, Color endColor)> GetCompatiblePortGradientMap()
        {
            // Output端口(起点)使用 startColor，Input端口(终点)使用 endColor
            
            yield return (
                typeof(TextureHandleNodeParameter),
                new Color(0.0f, 0.8f, 1f),      // Output端口：深蓝
                new Color(0.0f, 1f, 0.6f)       // Input端口：青绿
            );

            yield return (
                typeof(TextureHandlesNodeParameter),
                new Color(0.4f, 0.0f, 1f),      // Output端口：紫色
                new Color(0.0f, 1f, 0.8f)       // Input端口：青色
            );

            yield return (
                typeof(CameraTargetBufferNodeParameter),
                new Color(1f, 0.4f, 0.15f),     // Output端口：日落橙
                new Color(1f, 0.75f, 0.3f)      // Input端口：金色
            );
        }
    }
}