using System;
using UnityEngine;

namespace FraxinusEngine.Base
{
    [Serializable]
    public class ExposedParameter : ISerializationCallbackReceiver
    {
        [Serializable]
        public class Settings
        {
            public bool isHidden = false;
            public bool expanded = false;

            [SerializeField]
            internal string guid = null;

            public override bool Equals(object obj)
            {
                if (obj is Settings s && s != null)
                    return Equals(s);
                else
                    return false;
            }

            public virtual bool Equals(Settings param) => isHidden == param.isHidden && expanded == param.expanded;

            public override int GetHashCode() => base.GetHashCode();
        }

        public string guid;
        public string name;
        public bool input = true;

        [SerializeReference]
        public Settings settings;
        public string shortType => GetValueType()?.Name;

        protected virtual Settings CreateSettings() => new Settings();

        public virtual object value { get; set; }

        public virtual Type GetValueType() => value == null ? typeof(object) : value.GetType();

        public void Initialize(string name, object value)
        {
            guid = Guid.NewGuid().ToString();
            settings = CreateSettings();
            settings.guid = guid;
            this.name = name;
            this.value = value;
        }

        public void OnAfterDeserialize() { }

        public void OnBeforeSerialize() { }

        public void SetValue<T>(T val)
        {
            ((ExposedParameter<T>)this).val = val;
        }

        public T GetValue<T>()
        {
            return ((ExposedParameter<T>)this).val;
        }

        public static bool operator ==(ExposedParameter param1, ExposedParameter param2)
        {
            if (ReferenceEquals(param1, null) && ReferenceEquals(param2, null))
                return true;

            if (ReferenceEquals(param1, param2))
                return true;

            if (ReferenceEquals(param1, null))
                return false;

            if (ReferenceEquals(param2, null))
                return false;

            return param1.Equals(param2);
        }

        public static bool operator !=(ExposedParameter param1, ExposedParameter param2) => !(param1 == param2);

        public bool Equals(ExposedParameter parameter) => guid == parameter.guid;

        public override bool Equals(object obj)
        {
            if ((obj == null) || !this.GetType().Equals(obj.GetType()))
                return false;
            else
                return Equals((ExposedParameter)obj);
        }

        public override int GetHashCode() => guid.GetHashCode();

        public ExposedParameter Clone()
        {
            var clonedParam = Activator.CreateInstance(GetType()) as ExposedParameter;

            clonedParam.guid = guid;
            clonedParam.name = name;
            clonedParam.input = input;
            clonedParam.settings = settings;
            clonedParam.value = value;

            return clonedParam;
        }
    }

    [Serializable]
    public class ExposedParameter<T> : ExposedParameter
    {
        [SerializeField]
        public T val;

        public override Type GetValueType() => typeof(T);
    }

    [Serializable]
    public class ColorParameter : ExposedParameter<Color>
    {
        public enum ColorMode
        {
            Default,
            HDR
        }

        [Serializable]
        public class ColorSettings : Settings
        {
            public ColorMode mode;

            public override bool Equals(Settings param)
                => base.Equals(param) && mode == ((ColorSettings)param).mode;
        }

        public override object value { get => val; set => val = (Color)value; }
        protected override Settings CreateSettings() => new ColorSettings();
    }

    [Serializable]
    public class FloatParameter : ExposedParameter<float>
    {
        public enum FloatMode
        {
            Default,
            Slider,
        }

        [Serializable]
        public class FloatSettings : Settings
        {
            public FloatMode mode;
            public float min = 0;
            public float max = 1;

            public override bool Equals(Settings param)
                => base.Equals(param) && mode == ((FloatSettings)param).mode && min == ((FloatSettings)param).min && max == ((FloatSettings)param).max;
        }

        public override object value { get => val; set => val = (float)value; }
        protected override Settings CreateSettings() => new FloatSettings();
    }

    [Serializable]
    public class Vector2Parameter : ExposedParameter<Vector2>
    {
        public enum Vector2Mode
        {
            Default,
            MinMaxSlider,
        }

        [Serializable]
        public class Vector2Settings : Settings
        {
            public Vector2Mode mode;
            public float min = 0;
            public float max = 1;

            public override bool Equals(Settings param)
                => base.Equals(param) && mode == ((Vector2Settings)param).mode && min == ((Vector2Settings)param).min && max == ((Vector2Settings)param).max;
        }

        public override object value { get => val; set => val = (Vector2)value; }
        protected override Settings CreateSettings() => new Vector2Settings();
    }

    [Serializable]
    public class Vector3Parameter : ExposedParameter<Vector3>
    {

        public override object value { get => val; set => val = (Vector3)value; }
    }

    [Serializable]
    public class Vector4Parameter : ExposedParameter<Vector4>
    {

        public override object value { get => val; set => val = (Vector4)value; }
    }

    [Serializable]
    public class IntParameter : ExposedParameter<int>
    {
        public enum IntMode
        {
            Default,
            Slider,
        }

        [Serializable]
        public class IntSettings : Settings
        {
            public IntMode mode;
            public int min = 0;
            public int max = 10;

            public override bool Equals(Settings param)
                => base.Equals(param) && mode == ((IntSettings)param).mode && min == ((IntSettings)param).min && max == ((IntSettings)param).max;
        }

        public override object value { get => val; set => val = (int)value; }
        protected override Settings CreateSettings() => new IntSettings();
    }

    [Serializable]
    public class Vector2IntParameter : ExposedParameter<Vector2Int>
    {
        public override object value { get => val; set => val = (Vector2Int)value; }
    }

    [Serializable]
    public class Vector3IntParameter : ExposedParameter<Vector3Int>
    {
        public override object value { get => val; set => val = (Vector3Int)value; }
    }

    [Serializable]
    public class DoubleParameter : ExposedParameter<Double>
    {
        public override object value { get => val; set => val = (Double)value; }
    }

    [Serializable]
    public class LongParameter : ExposedParameter<long>
    {
        public override object value { get => val; set => val = (long)value; }
    }

    [Serializable]
    public class StringParameter : ExposedParameter<string>
    {
        public override object value { get => val; set => val = (string)value; }
        public override Type GetValueType() => typeof(String);
    }

    [Serializable]
    public class RectParameter : ExposedParameter<Rect>
    {
        public override object value { get => val; set => val = (Rect)value; }
    }

    [Serializable]
    public class RectIntParameter : ExposedParameter<RectInt>
    {
        public override object value { get => val; set => val = (RectInt)value; }
    }

    [Serializable]
    public class BoundsParameter : ExposedParameter<Bounds>
    {
        public override object value { get => val; set => val = (Bounds)value; }
    }

    [Serializable]
    public class BoundsIntParameter : ExposedParameter<BoundsInt>
    {
        public override object value { get => val; set => val = (BoundsInt)value; }
    }

    [Serializable]
    public class AnimationCurveParameter : ExposedParameter<AnimationCurve>
    {
        public override object value { get => val; set => val = (AnimationCurve)value; }
        public override Type GetValueType() => typeof(AnimationCurve);
    }

    [Serializable]
    public class GradientParameter : ExposedParameter<Gradient>
    {
        public enum GradientColorMode
        {
            Default,
            HDR,
        }

        [Serializable]
        public class GradientSettings : Settings
        {
            public GradientColorMode mode;

            public override bool Equals(Settings param)
                => base.Equals(param) && mode == ((GradientSettings)param).mode;
        }

        [SerializeField, GradientUsage(true)] Gradient hdrVal;

        public override object value { get => val; set => val = (Gradient)value; }
        public override Type GetValueType() => typeof(Gradient);
        protected override Settings CreateSettings() => new GradientSettings();
    }

    [Serializable]
    public class GameObjectParameter : ExposedParameter<GameObject>
    {
        public override object value { get => val; set => val = (GameObject)value; }
        public override Type GetValueType() => typeof(GameObject);
    }

    [Serializable]
    public class BoolParameter : ExposedParameter<bool>
    {
        public override object value { get => val; set => val = (bool)value; }
    }

    [Serializable]
    public class Texture2DParameter : ExposedParameter<Texture2D>
    {
        public override object value { get => val; set => val = (Texture2D)value; }
        public override Type GetValueType() => typeof(Texture2D);
    }

    [Serializable]
    public class RenderTextureParameter : ExposedParameter<RenderTexture>
    {
        public override object value { get => val; set => val = (RenderTexture)value; }
        public override Type GetValueType() => typeof(RenderTexture);
    }

    [Serializable]
    public class MeshParameter : ExposedParameter<Mesh>
    {
        public override object value { get => val; set => val = (Mesh)value; }
        public override Type GetValueType() => typeof(Mesh);
    }

    [Serializable]
    public class MaterialParameter : ExposedParameter<Material>
    {
        public override object value { get => val; set => val = (Material)value; }
        public override Type GetValueType() => typeof(Material);
    }

    [Serializable]
    class TextureParameter : ExposedParameter<Texture>
    {
        public override object value { get => val; set => val = (Texture)value; }
        public override Type GetValueType() => typeof(Texture);
    }

    [Serializable]
    class Texture3DParameter : ExposedParameter<Texture3D>
    {
        public override object value { get => val; set => val = (Texture3D)value; }
        public override Type GetValueType() => typeof(Texture3D);
    }

    [Serializable]
    class CubemapParameter : ExposedParameter<Cubemap>
    {
        public override object value { get => val; set => val = (Cubemap)value; }
        public override Type GetValueType() => typeof(Cubemap);
    }

    [Serializable]
    class ComputeBufferParameter : ExposedParameter<ComputeBuffer>
    {
        public override object value { get => val; set => val = (ComputeBuffer)value; }
        public override Type GetValueType() => typeof(ComputeBuffer);
    }
}
