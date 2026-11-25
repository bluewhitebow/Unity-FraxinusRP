using System;
using System.Collections.Generic;
using UnityEngine;

namespace FraxinusEngine.Base
{
    public class CommonConversions : ITypeAdapter
    {
        // Boolean to Exposed
        public static void ConvertExposeToNodeBool(ExposedNodeParameter from, BoolNodeParameter to)
        {
            if (from.value is not null)
            {
                if (from.value.GetValueType() == typeof(int))
                {
                    int exposeValue = from.GetExposeValue<int>();
                    if (exposeValue == 1)
                        to.value = true;
                    else if (exposeValue == 0)
                        to.value = false;
                }
                else
                    to.value = from.GetExposeValue<bool>();
            }
        }

        public static void ConvertNodeBoolToExpose(BoolNodeParameter from, ExposedNodeParameter to)
        {
            if (to.value is not null)
            {
                if (to.value.GetValueType() == typeof(int))
                    to.SetExposeValue<int>(from.value ? 1 : 0);
                else
                    to.SetExposeValue<bool>(from.value);
            }
        }

        // Int to Exposed
        public static void ConvertExposeToNodeInt(ExposedNodeParameter from, IntNodeParameter to)
        {
            if (from.value is not null)
            {
                if (from.value.GetValueType() == typeof(int))
                    to.value = from.GetExposeValue<int>();
                else if (from.value.GetValueType() == typeof(bool))
                    to.value = from.GetExposeValue<bool>() ? 1 : 0;
                else if (from.value.GetValueType() == typeof(Vector2))
                    to.value = (int)from.GetExposeValue<Vector2>().x;
                else if (from.value.GetValueType() == typeof(Vector3))
                    to.value = (int)from.GetExposeValue<Vector3>().x;
                else if (from.value.GetValueType() == typeof(Vector4))
                    to.value = (int)from.GetExposeValue<Vector4>().x;
            }
        }

        public static void ConvertNodeIntToExpose(IntNodeParameter from, ExposedNodeParameter to)
        {
            if (to.value is not null)
            {
                if (to.value.GetValueType() == typeof(int))
                    to.SetExposeValue<int>(from.value);
                else if (to.value.GetValueType() == typeof(bool))
                {
                    if (from.value == 1)
                        to.SetExposeValue<bool>(true);
                    else if (from.value == 0)
                        to.SetExposeValue<bool>(false);
                }
                else if (to.value.GetValueType() == typeof(Vector2))
                    to.SetExposeValue<Vector2>(new Vector2(from.value, from.value));
                else if (to.value.GetValueType() == typeof(Vector3))
                    to.SetExposeValue<Vector3>(new Vector3(from.value, from.value, from.value));
                else if (to.value.GetValueType() == typeof(Vector4))
                    to.SetExposeValue<Vector4>(new Vector4(from.value, from.value, from.value, from.value));
            }
        }

        // Int to Boolean
        public static void ConvertNodeIntToNodeBool(IntNodeParameter from, BoolNodeParameter to)
        {
            if (from.value == 1)
                to.value = true;
            else if (from.value == 0)
                to.value = false;
        }

        public static void ConvertNodeBoolToNodeInt(BoolNodeParameter from, IntNodeParameter to)
        {
            to.value = from.value ? 1 : 0;
        }

        // Float to Exposed
        public static void ConvertExposeToNodeFloat(ExposedNodeParameter from, FloatNodeParameter to)
        {
            if (from.value is not null)
            {
                if (from.value.GetValueType() == typeof(float))
                    to.value = from.GetExposeValue<float>();
                if (from.value.GetValueType() == typeof(int))
                    to.value = from.GetExposeValue<int>();
                else if (from.value.GetValueType() == typeof(Vector2))
                    to.value = from.GetExposeValue<Vector2>().x;
                else if (from.value.GetValueType() == typeof(Vector3))
                    to.value = from.GetExposeValue<Vector3>().x;
                else if (from.value.GetValueType() == typeof(Vector4))
                    to.value = from.GetExposeValue<Vector4>().x;
            }
        }

        public static void ConvertNodeFloatToExpose(FloatNodeParameter from, ExposedNodeParameter to)
        {
            if (to.value is not null)
            {
                if (to.value.GetValueType() == typeof(float))
                    to.SetExposeValue<float>(from.value);
                if (to.value.GetValueType() == typeof(int))
                    to.SetExposeValue<int>(Mathf.RoundToInt(from.value));
                else if (to.value.GetValueType() == typeof(Vector2))
                    to.SetExposeValue<Vector2>(new Vector2(from.value, from.value));
                else if (to.value.GetValueType() == typeof(Vector3))
                    to.SetExposeValue<Vector3>(new Vector3(from.value, from.value, from.value));
                else if (to.value.GetValueType() == typeof(Vector4))
                    to.SetExposeValue<Vector4>(new Vector4(from.value, from.value, from.value, from.value));
            }
        }

        // Vector2 to Exposed
        public static void ConvertExposeToNodeVector2(ExposedNodeParameter from, Vector2NodeParameter to)
        {
            if (from.value is not null)
            {
                if (from.value.GetValueType() == typeof(Vector2))
                    to.value = from.GetExposeValue<Vector2>();
                else if (from.value.GetValueType() == typeof(int))
                {
                    int val = from.GetExposeValue<int>();
                    to.value = new Vector2(val, val);
                }
                else if (from.value.GetValueType() == typeof(float))
                {
                    float val = from.GetExposeValue<float>();
                    to.value = new Vector2(val, val);
                }
                else if (from.value.GetValueType() == typeof(Vector3))
                {
                    Vector3 val = from.GetExposeValue<Vector3>();
                    to.value = new Vector2(val.x, val.y);
                }
                else if (from.value.GetValueType() == typeof(Vector4))
                {
                    Vector4 val = from.GetExposeValue<Vector4>();
                    to.value = new Vector2(val.x, val.y);
                }
            }
        }

        public static void ConvertNodeVector2ToExpose(Vector2NodeParameter from, ExposedNodeParameter to)
        {
            if (to.value is not null)
            {
                if (to.value.GetValueType() == typeof(Vector2))
                    to.SetExposeValue<Vector2>(from.value);
                if (to.value.GetValueType() == typeof(int))
                {
                    int val = Mathf.RoundToInt(from.value.x);
                    to.SetExposeValue<int>(Mathf.RoundToInt(val));
                }
                else if (to.value.GetValueType() == typeof(float))
                    to.SetExposeValue<float>(from.value.x);
                else if (to.value.GetValueType() == typeof(Vector3))
                    to.SetExposeValue<Vector3>(new Vector3(from.value.x, from.value.y, 0));
                else if (to.value.GetValueType() == typeof(Vector4))
                    to.SetExposeValue<Vector4>(new Vector4(from.value.x, from.value.y, 0, 0));
            }
        }

        // Vector3 to Exposed
        public static void ConvertExposeToNodeVector3(ExposedNodeParameter from, Vector3NodeParameter to)
        {
            if (from.value is not null)
            {
                if (from.value.GetValueType() == typeof(Vector3))
                    to.value = from.GetExposeValue<Vector3>();
                else if (from.value.GetValueType() == typeof(int))
                {
                    int val = from.GetExposeValue<int>();
                    to.value = new Vector3(val, val, val);
                }
                else if (from.value.GetValueType() == typeof(float))
                {
                    float val = from.GetExposeValue<float>();
                    to.value = new Vector3(val, val, val);
                }
                else if (from.value.GetValueType() == typeof(Vector2))
                {
                    Vector2 val = from.GetExposeValue<Vector2>();
                    to.value = new Vector3(val.x, val.y, 0);
                }
                else if (from.value.GetValueType() == typeof(Vector4))
                {
                    Vector4 val = from.GetExposeValue<Vector4>();
                    to.value = new Vector3(val.x, val.y, val.z);
                }
            }
        }

        public static void ConvertNodeVector3ToExpose(Vector3NodeParameter from, ExposedNodeParameter to)
        {
            if (to.value is not null)
            {
                if (to.value.GetValueType() == typeof(Vector3))
                    to.SetExposeValue<Vector3>(from.value);
                if (to.value.GetValueType() == typeof(int))
                {
                    int val = Mathf.RoundToInt(from.value.x);
                    to.SetExposeValue<int>(Mathf.RoundToInt(val));
                }
                else if (to.value.GetValueType() == typeof(float))
                    to.SetExposeValue<float>(from.value.x);
                else if (to.value.GetValueType() == typeof(Vector2))
                    to.SetExposeValue<Vector2>(new Vector2(from.value.x, from.value.y));
                else if (to.value.GetValueType() == typeof(Vector4))
                    to.SetExposeValue<Vector4>(new Vector4(from.value.x, from.value.y, from.value.z, 0));
            }
        }

        // Vector4 to Exposed
        public static void ConvertExposeToNodeVector4(ExposedNodeParameter from, Vector4NodeParameter to)
        {
            if (from.value is not null)
            {
                if (from.value.GetValueType() == typeof(Vector4))
                    to.value = from.GetExposeValue<Vector4>();
                else if (from.value.GetValueType() == typeof(int))
                {
                    int val = from.GetExposeValue<int>();
                    to.value = new Vector4(val, val, val, val);
                }
                else if (from.value.GetValueType() == typeof(float))
                {
                    float val = from.GetExposeValue<float>();
                    to.value = new Vector4(val, val, val, val);
                }
                else if (from.value.GetValueType() == typeof(Vector2))
                {
                    Vector2 val = from.GetExposeValue<Vector2>();
                    to.value = new Vector4(val.x, val.y, 0, 0);
                }
                else if (from.value.GetValueType() == typeof(Vector3))
                {
                    Vector3 val = from.GetExposeValue<Vector3>();
                    to.value = new Vector4(val.x, val.y, val.z, 0);
                }
            }
        }

        public static void ConvertNodeVector4ToExpose(Vector4NodeParameter from, ExposedNodeParameter to)
        {
            if (to.value is not null)
            {
                if (to.value.GetValueType() == typeof(Vector4))
                    to.SetExposeValue<Vector4>(from.value);
                if (to.value.GetValueType() == typeof(int))
                {
                    int val = Mathf.RoundToInt(from.value.x);
                    to.SetExposeValue<int>(Mathf.RoundToInt(val));
                }
                else if (to.value.GetValueType() == typeof(float))
                    to.SetExposeValue<float>(from.value.x);
                else if (to.value.GetValueType() == typeof(Vector2))
                    to.SetExposeValue<Vector2>(new Vector2(from.value.x, from.value.y));
                else if (to.value.GetValueType() == typeof(Vector4))
                    to.SetExposeValue<Vector3>(new Vector3(from.value.x, from.value.y, from.value.z));
            }
        }

        // Color
        public static void ConvertExposeToNodeColor(ExposedNodeParameter from, ColorNodeParameter to)
        {
            if (from.value is not null)
            {
                if (from.value.GetValueType() == typeof(Color))
                    to.value = from.GetExposeValue<Color>();
                else if (from.value.GetValueType() == typeof(Vector3))
                {
                    Vector3 val = from.GetExposeValue<Vector3>();
                    to.value = new Color(val.x, val.y, val.z, 1.0f);
                }
                else if (from.value.GetValueType() == typeof(Vector4))
                {
                    Vector4 val = from.GetExposeValue<Vector4>();
                    to.value = new Color(val.x, val.y, val.z, val.w);
                }
            }
        }

        public static void ConvertNodeColorToExpose(ColorNodeParameter from, ExposedNodeParameter to)
        {
            if (to.value is not null)
            {
                if (to.value.GetValueType() == typeof(Color))
                    to.SetExposeValue<Color>(from.value);
                if (to.value.GetValueType() == typeof(Vector3))
                    to.SetExposeValue<Vector3>(new Vector3(from.value.r, from.value.g, from.value.b));
                else if (to.value.GetValueType() == typeof(Vector4))
                    to.SetExposeValue<Vector4>(new Vector4(from.value.r, from.value.g, from.value.b, from.value.a));
            }
        }

        // RenderTexture
        public static void ConvertExposeToNodeRenderTexture(ExposedNodeParameter from, RenderTextureNodeParameter to)
        {
            if (from.value is not null)
                to.value = from.GetExposeValue<RenderTexture>();
        }

        public static void ConvertNodeRenderTextureToExpose(RenderTextureNodeParameter from, ExposedNodeParameter to)
        {
            to.SetExposeValue<RenderTexture>(from.value);
        }

        // Texture2D
        public static void ConvertExposeToNodeTexture2D(ExposedNodeParameter from, Texture2DNodeParameter to)
        {
            if (from.value is not null)
                to.value = from.GetExposeValue<Texture2D>();
        }

        public static void ConvertNodeTexture2DToExpose(Texture2DNodeParameter from, ExposedNodeParameter to)
        {
            to.SetExposeValue<Texture2D>(from.value);
        }

        // Int to Float
        public static void ConvertNodeIntToNodeFloat(IntNodeParameter from, FloatNodeParameter to) => to.value = (float)from.value;

        public static void ConvertNodeFloatToNodeInt(FloatNodeParameter from, IntNodeParameter to) => to.value = Mathf.RoundToInt(from.value);

        // Int to Vector
        public static void ConvertNodeVector2ToNodeInt(Vector2NodeParameter from, IntNodeParameter to) => to.value = (int)from.value.x;

        public static void ConvertNodeIntToNodeVector2(IntNodeParameter from, Vector2NodeParameter to) => to.value = new Vector2(from.value, from.value);

        public static void ConvertNodeVector3ToNodeInt(Vector3NodeParameter from, IntNodeParameter to) => to.value = (int)from.value.x;

        public static void ConvertNodeIntToNodeVector3(IntNodeParameter from, Vector3NodeParameter to) => to.value = new Vector3(from.value, from.value, from.value);

        public static void ConvertNodeVector4ToNodeInt(Vector4NodeParameter from, IntNodeParameter to) => to.value = (int)from.value.x;

        public static void ConvertNodeIntToNodeVector4(IntNodeParameter from, Vector4NodeParameter to) => to.value = new Vector4(from.value, from.value, from.value, from.value);

        // Float to Vector
        public static void ConvertNodeVector2ToNodeFloat(Vector2NodeParameter from, FloatNodeParameter to) => to.value = from.value.x;

        public static void ConvertNodeFloatToNodeVector2(FloatNodeParameter from, Vector2NodeParameter to) => to.value = new Vector2(from.value, from.value);

        public static void ConvertNodeVector3ToNodeFloat(Vector3NodeParameter from, FloatNodeParameter to) => to.value = from.value.x;

        public static void ConvertNodeFloatToNodeVector3(FloatNodeParameter from, Vector3NodeParameter to) => to.value = new Vector3(from.value, from.value, from.value);

        public static void ConvertNodeVector4ToNodeFloat(Vector4NodeParameter from, FloatNodeParameter to) => to.value = from.value.x;

        public static void ConvertNodeFloatToNodeVector4(FloatNodeParameter from, Vector4NodeParameter to) => to.value = new Vector4(from.value, from.value, from.value, from.value);

        // Vector to Vector
        public static void ConvertNodeVector2ToNodeVector3(Vector2NodeParameter from, Vector3NodeParameter to) => to.value = new Vector3(from.value.x, from.value.y, 0);

        public static void ConvertNodeVector3ToNodeVector2(Vector3NodeParameter from, Vector2NodeParameter to) => to.value = new Vector2(from.value.x, from.value.y);

        public static void ConvertNodeVector2ToNodeVector4(Vector2NodeParameter from, Vector4NodeParameter to) => to.value = new Vector4(from.value.x, from.value.y, 0, 0);

        public static void ConvertNodeVector4ToNodeVector2(Vector4NodeParameter from, Vector2NodeParameter to) => to.value = new Vector2(from.value.x, from.value.y);

        public static void ConvertNodeVector3ToNodeVector4(Vector3NodeParameter from, Vector4NodeParameter to) => to.value = new Vector4(from.value.x, from.value.y, from.value.z, 0);

        public static void ConvertNodeVector4ToNodeVector3(Vector4NodeParameter from, Vector3NodeParameter to) => to.value = new Vector3(from.value.x, from.value.y, from.value.z);

        // Vector to Color
        public static void ConvertNodeVector3ToNodeColor(Vector3NodeParameter from, ColorNodeParameter to) => to.value = new Color(from.value.x, from.value.y, from.value.z, 1.0f);

        public static void ConvertNodeColorToNodeVector3(ColorNodeParameter from, Vector3NodeParameter to) => to.value = new Vector3(from.value.r, from.value.g, from.value.b);

        public static void ConvertNodeVector4ToNodeColor(Vector4NodeParameter from, ColorNodeParameter to) => to.value = new Color(from.value.x, from.value.y, from.value.z, from.value.w);

        public static void ConvertNodeColorToNodeVector4(ColorNodeParameter from, Vector4NodeParameter to) => to.value = new Vector4(from.value.r, from.value.g, from.value.b, from.value.a);


        public override IEnumerable<(Type, Type)> GetCompatibleTypes()
        {
            yield return (typeof(bool),                 typeof(BoolNodeParameter));
            yield return (typeof(int),                  typeof(BoolNodeParameter));

            yield return (typeof(bool),                 typeof(IntNodeParameter));
            yield return (typeof(int),                  typeof(IntNodeParameter));
            yield return (typeof(Vector2),              typeof(IntNodeParameter));
            yield return (typeof(Vector3),              typeof(IntNodeParameter));
            yield return (typeof(Vector4),              typeof(IntNodeParameter));

            yield return (typeof(int),                  typeof(FloatNodeParameter));
            yield return (typeof(float),                typeof(FloatNodeParameter));
            yield return (typeof(Vector2),              typeof(FloatNodeParameter));
            yield return (typeof(Vector3),              typeof(FloatNodeParameter));
            yield return (typeof(Vector4),              typeof(FloatNodeParameter));

            yield return (typeof(int),                  typeof(Vector2NodeParameter));
            yield return (typeof(float),                typeof(Vector2NodeParameter));
            yield return (typeof(Vector2),              typeof(Vector2NodeParameter));
            yield return (typeof(Vector3),              typeof(Vector2NodeParameter));
            yield return (typeof(Vector4),              typeof(Vector2NodeParameter));

            yield return (typeof(int),                  typeof(Vector3NodeParameter));
            yield return (typeof(float),                typeof(Vector3NodeParameter));
            yield return (typeof(Vector2),              typeof(Vector3NodeParameter));
            yield return (typeof(Vector3),              typeof(Vector3NodeParameter));
            yield return (typeof(Vector4),              typeof(Vector3NodeParameter));

            yield return (typeof(int),                  typeof(Vector4NodeParameter));
            yield return (typeof(float),                typeof(Vector4NodeParameter));
            yield return (typeof(Vector2),              typeof(Vector4NodeParameter));
            yield return (typeof(Vector3),              typeof(Vector4NodeParameter));
            yield return (typeof(Vector4),              typeof(Vector4NodeParameter));

            yield return (typeof(Vector3),              typeof(ColorNodeParameter));
            yield return (typeof(Vector4),              typeof(ColorNodeParameter));
            yield return (typeof(Color),                typeof(ColorNodeParameter));

            yield return (typeof(RenderTexture),        typeof(RenderTextureNodeParameter));
            yield return (typeof(Texture2D),            typeof(Texture2DNodeParameter));
        }
    }
}
