using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FraxinusEngine.Base
{
    public abstract class ITypeAdapter
    {
        public virtual IEnumerable<(Type, Type)> GetIncompatibleTypes() { yield break; }
        public virtual IEnumerable<(Type, Type)> GetCompatibleTypes() { yield break; }
    }

    public static class TypeAdapter
    {
        static readonly Dictionary<(Type from, Type to), Action<object, object>> s_adapters = new Dictionary<(Type, Type), Action<object, object>>();
        static readonly Dictionary<(Type from, Type to), MethodInfo> s_adapterMethods = new Dictionary<(Type, Type), MethodInfo>();
        static readonly HashSet<(Type from, Type to)> s_incompatibleTypes = new HashSet<(Type, Type)>();
        static readonly HashSet<(Type from, Type to)> s_compatibleTypes = new HashSet<(Type, Type)>();
        static bool s_adaptersLoaded;

        static void LoadAllAdapters()
        {
            if (s_adaptersLoaded)
                return;
            // Mark first so an extension querying compatibility from its constructor
            // cannot recursively discover/instantiate the same extensions.
            s_adaptersLoaded = true;
            foreach (Type type in AppDomain.CurrentDomain.GetAllTypes()
                .Where(t => typeof(ITypeAdapter).IsAssignableFrom(t) && !t.IsAbstract && !t.ContainsGenericParameters)
                .OrderBy(t => t.AssemblyQualifiedName, StringComparer.Ordinal))
            {
                if (!TryRegisterAdapter(type, out string error))
                    Debug.LogWarning("Ignoring type adapter " + type.FullName + ": " + error);
            }
        }

        // Registration is transactional: a broken constructor, lazy enumerator or
        // method signature must not leave half an adapter or prevent other extensions loading.
        internal static bool TryRegisterAdapter(Type type, out string error)
        {
            error = null;
            if (type == null || !typeof(ITypeAdapter).IsAssignableFrom(type) || type.IsAbstract
                || type.ContainsGenericParameters || type.GetConstructor(Type.EmptyTypes) == null)
            {
                error = "A concrete closed type with a public parameterless constructor is required.";
                return false;
            }

            (Type, Type)[] incompatible;
            (Type, Type)[] compatible;
            try
            {
                var adapter = (ITypeAdapter)Activator.CreateInstance(type);
                incompatible = adapter.GetIncompatibleTypes()?.ToArray() ?? Array.Empty<(Type, Type)>();
                compatible = adapter.GetCompatibleTypes()?.ToArray() ?? Array.Empty<(Type, Type)>();
            }
            catch (Exception exception)
            {
                error = (exception is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException : exception).Message;
                return false;
            }

            if (incompatible.Concat(compatible).Any(pair => pair.Item1 == null || pair.Item2 == null))
            {
                error = "Compatibility declarations cannot contain null types.";
                return false;
            }

            var methods = new Dictionary<(Type, Type), MethodInfo>();
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                    continue;
                var parameters = method.GetParameters();
                if (method.ContainsGenericParameters || method.ReturnType != typeof(void) || parameters.Length != 2
                    || parameters.Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer))
                {
                    error = "Conversion methods must be non-generic void methods with two value parameters: " + method.Name;
                    return false;
                }
                var key = (parameters[0].ParameterType, parameters[1].ParameterType);
                if (s_adapterMethods.TryGetValue(key, out var existing) && existing != method)
                {
                    error = "A conversion is already registered for " + key + " by " + existing.DeclaringType.FullName;
                    return false;
                }
                if (methods.ContainsKey(key))
                {
                    error = "Duplicate conversion pair: " + key;
                    return false;
                }
                methods.Add(key, method);
            }

            foreach (var pair in incompatible)
            {
                s_incompatibleTypes.Add(pair);
                s_incompatibleTypes.Add((pair.Item2, pair.Item1));
            }
            foreach (var pair in compatible)
            {
                s_compatibleTypes.Add(pair);
                s_compatibleTypes.Add((pair.Item2, pair.Item1));
            }
            foreach (var pair in methods)
            {
                MethodInfo method = pair.Value;
                s_adapterMethods[pair.Key] = method;
                s_adapters[pair.Key] = (from, to) => method.Invoke(null, new[] { from, to });
            }
            // Conversions are directional. A reverse conversion is optional.
            return true;
        }

        public static bool AreCompatible(Type from, Type to)
        {
            LoadAllAdapters();
            return s_compatibleTypes.Contains((from, to));
        }

        public static bool AreIncompatible(Type from, Type to)
        {
            LoadAllAdapters();
            return s_incompatibleTypes.Contains((from, to));
        }

        public static bool AreAssignable(Type from, Type to)
        {
            LoadAllAdapters();
            return !s_incompatibleTypes.Contains((from, to)) && s_adapters.ContainsKey((from, to));
        }

        public static MethodInfo GetConvertionMethod(Type from, Type to)
        {
            LoadAllAdapters();
            return s_adapterMethods[(from, to)];
        }

        public static void Convert(object from, object to, Type targetType)
        {
            LoadAllAdapters();
            if (from != null && to != null && s_adapters.TryGetValue((from.GetType(), to.GetType()), out var conversion))
                conversion(from, to);
        }
    }
}
