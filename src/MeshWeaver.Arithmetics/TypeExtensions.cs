using System.Collections;
using MeshWeaver.Reflection;

namespace MeshWeaver.Arithmetics
{
    public static class FormulaFrameworkTypeExtensions
    {
        public static bool IsDictionary(this Type type)
        {
            return typeof(IDictionary).IsAssignableFrom(type) || (type.IsGenericType && typeof(IDictionary<,>).IsAssignableFrom(type.GetGenericTypeDefinition()));
        }

        public static bool IsList(this Type type)
        {
            return type.GetGenericArgumentTypes(typeof(IList<>)) != null;
        }
        public static bool IsEnumerable(this Type type)
        {
            return type.GetGenericArgumentTypes(typeof(IEnumerable<>)) != null;
        }
    }
}
