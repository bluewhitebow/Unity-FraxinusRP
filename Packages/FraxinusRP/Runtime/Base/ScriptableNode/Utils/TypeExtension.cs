using System;
using System.Linq.Expressions;

namespace FraxinusEngine.Base
{
    public static class TypeExtension
    {
        public static bool IsReallyAssignableFrom(this Type type, Type otherType)
        {
            // Port values are copied into the input parameter. A downcast or a
            // language conversion operator is not an executable port adapter.
            return type != null && otherType != null && type.IsAssignableFrom(otherType);
        }
    }
}
