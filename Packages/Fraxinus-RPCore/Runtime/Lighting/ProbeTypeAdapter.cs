using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityEngine.Rendering
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public class ProbeRuntimeResourceHandlerAttribute : Attribute
    {
    }

    public static class ProbeRuntimeResourcesTypeAdapter
    {
        struct TypePair : IEquatable<TypePair>
        {
            public Type resourceType;
            public Type dataLocationType;

            public TypePair(Type resourceType, Type dataLocationType)
            {
                this.resourceType = resourceType;
                this.dataLocationType = dataLocationType;
            }

            public bool Equals(TypePair other)
            {
                return resourceType == other.resourceType && dataLocationType == other.dataLocationType;
            }

            public override bool Equals(object obj)
            {
                return obj is TypePair other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (resourceType.GetHashCode() * 397) ^ dataLocationType.GetHashCode();
                }
            }
        }

        public delegate void UntypedHandler<TData>(TData pool, ref object resource)
            where TData : struct;

        public delegate void TypedHandler<TResources, TData>(TData pool, ref TResources resource)
            where TResources : struct, IRuntimeResources
            where TData : struct;

        public abstract class HandlerWrapper<TData> where TData : struct
        {
            public abstract void Invoke(TData pool, ref IRuntimeResources resource);
        }

        public class TypedHandlerWrapper<TResources, TData> : HandlerWrapper<TData>
            where TResources : struct, IRuntimeResources
            where TData : struct
        {
            private readonly TypedHandler<TResources, TData> m_Handler;

            public TypedHandlerWrapper(TypedHandler<TResources, TData> handler)
            {
                m_Handler = handler;
            }

            public override void Invoke(TData pool, ref IRuntimeResources resource)
            {
                var typedResource = (TResources)resource;
                m_Handler(pool, ref typedResource);
                resource = typedResource;
            }

            public void InvokeTyped(TData pool, ref TResources resource)
            {
                m_Handler(pool, ref resource);
            }
        }

        static readonly Dictionary<TypePair, object> s_Handlers = new Dictionary<TypePair, object>();

        static readonly MethodInfo s_RegisterHandlerOpenGeneric = FindRegisterHandlerOpenGeneric();

        const BindingFlags k_StaticMethods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static MethodInfo FindRegisterHandlerOpenGeneric()
        {
            foreach (var m in typeof(ProbeRuntimeResourcesTypeAdapter).GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                if (m.Name == nameof(RegisterHandler) && m.IsGenericMethodDefinition)
                    return m;
            }
            return null;
        }

        public static void RegisterHandler<TResources, TDataLocation>(TypedHandler<TResources, TDataLocation> handler)
            where TResources : struct, IRuntimeResources
            where TDataLocation : struct
        {
            var key = new TypePair(typeof(TResources), typeof(TDataLocation));
            s_Handlers[key] = new TypedHandlerWrapper<TResources, TDataLocation>(handler);
        }

        public static void GetRuntimeResources<TResources, TDataLocation>(ref TResources rr, TDataLocation pool)
            where TResources : struct, IRuntimeResources
            where TDataLocation : struct
        {
            var key = new TypePair(typeof(TResources), typeof(TDataLocation));
            if (s_Handlers.TryGetValue(key, out var handlerObj))
            {
                var wrapper = (TypedHandlerWrapper<TResources, TDataLocation>)handlerObj;
                wrapper.InvokeTyped(pool, ref rr);
            }
        }

        public static void AutoRegisterHandlers()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }

                foreach (var type in types)
                {
                    if (type == null || type.ContainsGenericParameters)
                        continue;
                    foreach (var method in type.GetMethods(k_StaticMethods))
                    {
                        if (method.IsDefined(typeof(ProbeRuntimeResourceHandlerAttribute), false))
                            TryRegisterHandlerMethod(method);
                    }
                }
            }
        }

        static void TryRegisterHandlerMethod(MethodInfo method)
        {
            if (method.ContainsGenericParameters || method.ReturnType != typeof(void))
                return;

            var parameters = method.GetParameters();
            if (parameters.Length != 2)
                return;

            var dataLocationType = parameters[0].ParameterType;
            var resourceType = parameters[1].ParameterType;
            if (!resourceType.IsByRef)
                return;
            resourceType = resourceType.GetElementType();

            if (!dataLocationType.IsValueType || !resourceType.IsValueType)
                return;
            if (dataLocationType.IsByRefLike || resourceType.IsByRefLike ||
                Nullable.GetUnderlyingType(dataLocationType) != null || Nullable.GetUnderlyingType(resourceType) != null)
                return;
            if (!typeof(IRuntimeResources).IsAssignableFrom(resourceType))
                return;
            if (s_RegisterHandlerOpenGeneric == null)
                return;

            var delegateType = typeof(TypedHandler<,>).MakeGenericType(resourceType, dataLocationType);
            var handler = Delegate.CreateDelegate(delegateType, method, throwOnBindFailure: false);
            if (handler == null)
                return;
            s_RegisterHandlerOpenGeneric.MakeGenericMethod(resourceType, dataLocationType).Invoke(null, new object[] { handler });
            Debug.Log($"[ProbeRuntimeResourceHandler] Registered: {method.DeclaringType}.{method.Name}<{resourceType.Name}, {dataLocationType.Name}>");
        }

        static ProbeRuntimeResourcesTypeAdapter()
        {
            AutoRegisterHandlers();
        }
    }
}
