using System;
using System.Collections.Generic;
using System.Reflection;

namespace FraxinusEngine.Base
{
    public static class AppDomainExtension
    {
        public static IEnumerable<Type> GetAllTypes(this AppDomain domain)
        {
            foreach (var assembly in domain.GetAssemblies())
            {
                Type[] types;

                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    // A third-party assembly can have unavailable dependencies.
                    // Keep its loadable types so the remaining extensions still register.
                    types = exception.Types;
                }

                foreach (var type in types)
                    if (type != null)
                        yield return type;
            }
        }
    }
}