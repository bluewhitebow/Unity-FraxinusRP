using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FraxinusEngine.Base
{
    public abstract class NodeParameter : ICloneable
    {
        public const string debuggerDisplay = "{_value}";

        public virtual T GetValue<T>()
        {
            return ((NodeParameter<T>)this).value;
        }

        public virtual void SetValue<T>(T val)
        {
            ((NodeParameter<T>)this).value = val;
        }

        public abstract void SetParamterValue(NodeParameter parameter);

        public virtual void Default() { }

        public abstract object Clone();
    }

    [DebuggerDisplay(debuggerDisplay)]
    public class NodeParameter<T> : NodeParameter, IEquatable<NodeParameter<T>>
    {
        protected T _value;

        public virtual T value
        {
            get => _value;
            set => _value = value;
        }

        public NodeParameter() : this(default) { }

        protected NodeParameter(T value)
        {
            _value = value;
        }

        public override void SetParamterValue(NodeParameter parameter)
        {
            _value = ((NodeParameter<T>)parameter)._value;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                if (!EqualityComparer<T>.Default.Equals(value, default))
                    hash = hash * 23 + value.GetHashCode();

                return hash;
            }
        }

        public override string ToString() => $"{value} )";

        public static bool operator ==(NodeParameter<T> lhs, T rhs) => lhs != null && !ReferenceEquals(lhs.value, null) && lhs.value.Equals(rhs);

        public static bool operator !=(NodeParameter<T> lhs, T rhs) => !(lhs == rhs);

        public bool Equals(NodeParameter<T> other)
        {
            if (ReferenceEquals(null, other))
                return false;

            if (ReferenceEquals(this, other))
                return true;

            return EqualityComparer<T>.Default.Equals(_value, other._value);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
                return false;

            if (ReferenceEquals(this, obj))
                return true;

            if (obj.GetType() != GetType())
                return false;

            return Equals((NodeParameter<T>)obj);
        }

        public override object Clone()
        {
            return new NodeParameter<T>(GetValue<T>());
        }

        public override void Default()
        {
            _value = default;
        }

        public static explicit operator T(NodeParameter<T> prop) => prop._value;
    }

    [DebuggerDisplay(debuggerDisplay)]
    public class BoolNodeParameter : NodeParameter<bool>
    {
        public BoolNodeParameter() : this(default) { }

        public BoolNodeParameter(bool value) : base(value) { }
    }

    [DebuggerDisplay(debuggerDisplay)]
    public class IntNodeParameter : NodeParameter<int>
    {
        public IntNodeParameter() : this(default) { }

        public IntNodeParameter(int value) : base(value) { }
    }

    [DebuggerDisplay(debuggerDisplay)]
    public class FloatNodeParameter : NodeParameter<float>
    {
        public FloatNodeParameter() : this(default) { }

        public FloatNodeParameter(int value) : base(value) { }
    }

    [DebuggerDisplay(debuggerDisplay)]
    public class DoubleNodeParameter : NodeParameter<double>
    {
        public DoubleNodeParameter() : this(default) { }

        public DoubleNodeParameter(double value) : base(value) { }
    }

    public class Vector2NodeParameter : NodeParameter<Vector2>
    {
        public Vector2NodeParameter() : this(default) { }

        public Vector2NodeParameter(Vector2 value) : base(value) { }
    }

    public class Vector3NodeParameter : NodeParameter<Vector3>
    {
        public Vector3NodeParameter() : this(default) { }

        public Vector3NodeParameter(Vector3 value) : base(value) { }
    }

    public class Vector4NodeParameter : NodeParameter<Vector4>
    {
        public Vector4NodeParameter() : this(default) { }

        public Vector4NodeParameter(Vector4 value) : base(value) { }
    }

    [DebuggerDisplay(debuggerDisplay)]
    public class ColorNodeParameter : NodeParameter<Color>
    {
        public ColorNodeParameter() : this(default) { }

        public ColorNodeParameter(Color value) : base(value) { }
    }

    [DebuggerDisplay(debuggerDisplay)]
    public class RenderTextureNodeParameter : NodeParameter<RenderTexture>
    {
        public RenderTextureNodeParameter() : this(default) { }

        public RenderTextureNodeParameter(RenderTexture value) : base(value) { }
    }

    [DebuggerDisplay(debuggerDisplay)]
    public class Texture2DNodeParameter : NodeParameter<Texture2D>
    {
        public Texture2DNodeParameter() : this(default) { }

        public Texture2DNodeParameter(Texture2D value) : base(value) { }
    }

    [DebuggerDisplay(debuggerDisplay)]
    public class MaterialNodeParameter : NodeParameter<Material>
    {
        public MaterialNodeParameter() : this(default) { }

        public MaterialNodeParameter(Material value) : base(value) { }
    }

    [DebuggerDisplay(debuggerDisplay)]
    public class GameObjectNodeParameter : NodeParameter<GameObject>
    {
        public GameObjectNodeParameter() : this(default) { }

        public GameObjectNodeParameter(GameObject value) : base(value) { }
    }

    [DebuggerDisplay(debuggerDisplay)]
    public class ExposedNodeParameter : NodeParameter<ExposedParameter>
    {
        public ExposedNodeParameter() : this(default) { }

        public ExposedNodeParameter(ExposedParameter value) : base(value) { }

        public TParam GetExposeValue<TParam>()
        {
            return _value.GetValue<TParam>();
        }

        public void SetExposeValue<TParam>(TParam val)
        {
            _value?.SetValue<TParam>(val);
        }
    }

    public class ObjectParameter<T> : NodeParameter<T>
    {
        internal ReadOnlyCollection<NodeParameter> parameters { get; private set; }

        public sealed override T value
        {
            get => _value;
            set
            {
                _value = value;

                if (_value == null)
                {
                    parameters = null;
                    return;
                }

                parameters = _value.GetType()
                    .GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(t => t.FieldType.IsSubclassOf(typeof(NodeParameter)))
                    .OrderBy(t => t.MetadataToken)
                    .Select(t => (NodeParameter)t.GetValue(_value))
                    .ToList()
                    .AsReadOnly();
            }
        }

        public ObjectParameter() : this(default) { }

        public ObjectParameter(T value)
        {
            this.value = value;
        }
    }
}
