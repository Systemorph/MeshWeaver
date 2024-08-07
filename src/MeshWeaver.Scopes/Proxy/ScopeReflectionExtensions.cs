using System.Reflection;
using MeshWeaver.Collections;
using MeshWeaver.DataCubes;
using MeshWeaver.Reflection;

namespace MeshWeaver.Scopes.Proxy
{
    public static class ScopeReflectionExtensions
    {
        public static bool IsFilterableInterface(this Type type)
        {
            return type == typeof(IFilterable) ||
                   type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IFilterable<>);
        }

        public static Type[] GetParameterTypes(this MethodInfo method)
        {
            return method.GetParameters().Select(p => p.ParameterType).ToArray();
        }

        public static bool IsScope(this Type type)
        {
            return typeof(IScope).IsAssignableFrom(type);
        }

        public static IEnumerable<(Type ScopeType, PropertyInfo[] Properties)> GetScopeProperties(this Type scopeType)
        {
            return scopeType.RepeatOnce()
                            .Concat(scopeType.GetAllInterfaces())
                            .Where(i => i.IsScope())
                            .Select(i => (i, i.GetProperties(BindingFlags.Instance | BindingFlags.Public)));
        }

        public static bool IsScopeInterface(this Type type)
        {
            return typeof(IScope) == type || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IScope<>);
        }

        internal static bool IsFilterable(this Type type)
        {
            return typeof(IFilterable).IsAssignableFrom(type);
        }

        public static Type GetIdentityType(this Type type)
        {
            var genericArgs = type.GetGenericArgumentTypes(typeof(IScope<>));
            return genericArgs?.FirstOrDefault();
        }
    }
}