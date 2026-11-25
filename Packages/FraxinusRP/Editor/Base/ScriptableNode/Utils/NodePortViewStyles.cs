using System;
using System.Collections.Generic;
using UnityEngine;
using FraxinusEngine.Base;

namespace FraxinusEditor.Base
{
    public abstract class IPortValueTypeColorMap
    {
        public virtual IEnumerable<(Type, Color)> GetCompatiblePortColorMap() { yield break; }
        public virtual IEnumerable<(Type, Color startColor, Color endColor)> GetCompatiblePortGradientMap() { yield break; }
    }

    public class CommonPortNodeParamterColorMap : IPortValueTypeColorMap
    {
        public override IEnumerable<(Type, Color)> GetCompatiblePortColorMap()
        {
            yield return (typeof(BoolNodeParameter), Color.white);
            yield return (typeof(IntNodeParameter), Color.white);
            yield return (typeof(FloatNodeParameter), Color.white);
            yield return (typeof(Vector2NodeParameter), Color.blue);
            yield return (typeof(Vector3NodeParameter), Color.blue);
            yield return (typeof(Vector4NodeParameter), Color.blue);
            yield return (typeof(ColorNodeParameter), Color.cyan);
            yield return (typeof(RenderTextureNodeParameter), Color.red);
            yield return (typeof(Texture2DNodeParameter), Color.red);
        }
    }

    public static class NodePortViewStyles
    {
        static readonly Color s_defaultColor = new Color(0.9411765f, 0.9411765f, 0.9411765f);

        static Dictionary<Type, Color> s_compatiblePortColors = new Dictionary<Type, Color>();
        static Dictionary<Type, (Color start, Color end)> s_compatiblePortGradients = new Dictionary<Type, (Color, Color)>();

        static bool s_loaded = false;

        static void LoadPortTypeColors()
        {
            // todo: 缩小至 FraxinusEngine 的程序集范围
            foreach (Type type in AppDomain.CurrentDomain.GetAllTypes())
            {
                if (typeof(IPortValueTypeColorMap).IsAssignableFrom(type))
                {
                    if (type.IsAbstract)
                        continue;

                    var colorMap = Activator.CreateInstance(type) as IPortValueTypeColorMap;
                    if (colorMap != null)
                    {
                        foreach (var map in colorMap.GetCompatiblePortColorMap())
                        { 
                            if (!map.Item1.IsValueType && map.Item1.IsSubclassOf(typeof(NodeParameter)))
                                s_compatiblePortColors.Add(map.Item1, map.Item2);
                        }
                        
                        foreach (var gradientMap in colorMap.GetCompatiblePortGradientMap())
                        {
                            if (!gradientMap.Item1.IsValueType && gradientMap.Item1.IsSubclassOf(typeof(NodeParameter)))
                                s_compatiblePortGradients[gradientMap.Item1] = (gradientMap.startColor, gradientMap.endColor);
                        }
                    }
                }
            }

            s_loaded = true;
        }

        public static Color GetPortColor(Type portValType)
        {
            if (!s_loaded)
                LoadPortTypeColors();

            if (s_compatiblePortColors.TryGetValue(portValType, out Color col))
                return col;
            else
                return s_defaultColor;
        }

        public static bool TryGetPortGradient(Type portValType, out Color startColor, out Color endColor)
        {
            if (!s_loaded)
                LoadPortTypeColors();

            if (s_compatiblePortGradients.TryGetValue(portValType, out var gradient))
            {
                startColor = gradient.start;
                endColor = gradient.end;
                return true;
            }

            startColor = Color.white;
            endColor = Color.white;
            return false;
        }
    }
}
