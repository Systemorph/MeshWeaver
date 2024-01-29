using OpenSmc.Arithmetics.Aggregation;
using OpenSmc.DataCubes.Operations;
using OpenSmc.Reflection;

namespace OpenSmc.DataCubes
{
    public static class DataCubeExtensions
    {
        public static IDataCube<T> ToDataCube<T>(this IEnumerable<T> enumerable)
        {
            var data = enumerable as ICollection<T> ?? enumerable.ToArray();
            return new DataCube<T>(data);
        }

        public static IDataCube<T> Aggregate<T>(this IEnumerable<IDataCube<T>> cubes)
        {
            if (cubes == null)
                return null;
            return new AggregateDataCube<T>(cubes);
        }

        public static IDataCube<T> AggregateBy<T>(this IDataCube<T> cube, params string[] propertyNames)
            where T : class
        {
            return ((IEnumerable<T>)cube).AggregateBy(propertyNames)
                                         .ToDataCube();
        }

        public static IDataCube<T> AggregateOver<T>(this IDataCube<T> cube, params string[] propertyNames)
            where T : class
        {
            return ((IEnumerable<T>)cube).AggregateOver(propertyNames)
                                         .ToDataCube();
        }

        public static bool IsDataCube(this Type type) => typeof(IDataCube).IsAssignableFrom(type);

        public static Type GetDataCubeElementType(this Type type) => type.GetGenericArgumentTypes(typeof(IDataCube<>))?.FirstOrDefault();
    }
}