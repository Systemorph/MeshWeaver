using System.Collections;
using MeshWeaver.DataCubes;

namespace MeshWeaver.Scopes.DataCubes
{
    public static class DataCubeScopesExtensions
    {
        public const string PropertyDimension = "__P";
        public static bool IsDataCubeInterface(this Type type) => type == typeof(IDataCube) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDataCube<>));

        public static bool IsEnumerableInterface(this Type type) => type == typeof(IEnumerable) || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>);
    }
}